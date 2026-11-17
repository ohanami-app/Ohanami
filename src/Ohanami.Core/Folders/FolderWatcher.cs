using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Scanning;

namespace Ohanami.Core;

/// <summary>監視が数えるイベントの種類(<see cref="FolderWatcher.NoteEvent"/>)。</summary>
internal enum WatchedChange { Created, Changed, Deleted, Renamed, Error }

/// <summary>
/// フォルダの監視と、定期的な走査(ADR 0009)。<b>監視のイベントは「このフォルダを走査し直す」のきっかけにだけ使う</b>。どのファイルかは見ず、
/// 差分の判定は <see cref="Scanner"/>(mtime とサイズ → ハッシュ)だけが正。理由: あふれで落ちたファイルが分からない、フォルダごとの移動で中身のイベントが来ない、
/// 名前の変更の組み方がアプリごとに違う(どれも「列挙し直す」なら同じ扱い)。
/// <list type="bullet">
/// <item>イベントのハンドラは、印を付けて返すだけ(フォルダごとに、最初と最後のイベントの時刻を覚える。I/O をしない。遅いとバッファがあふれる)。
///   選び方: <c>NotifyFilter = FileName | DirectoryName | LastWrite | Size</c>、サブフォルダも見る、<c>Filter</c> は付けない。
///   <b>拡張子で絞るのは Changed だけ</b>(動画の録画などで多発する)。Created / Deleted / Renamed は拡張子を問わず数える(フォルダの移動を拾う)。Error は常に数える。</item>
/// <item>まとめ方: フォルダごとに、数えるイベントが <see cref="QuietPeriod"/>(3 秒)止まったら走査する。続いても、最初のイベントから <see cref="MaxWait"/>(30 秒)で一度走査する。
///   次の走査は、前の走査の終わりから、その所要時間の <see cref="GapFactor"/>(2)倍以上あける。</item>
/// <item>一時的に読めなかったファイルがあった(<see cref="ScanSummary.TransientlyUnreadable"/>)監視の走査は、<see cref="RetryDelay"/>(5 秒)後にそのフォルダをもう一度走査する。
///   3 回続けて 1 件以上なら、定期の走査に任せる(0 件になったら回数を戻す)。</item>
/// <item><c>Error</c>(あふれ以外も)が来たら、そのフォルダの監視を作り直して走査する。作り直せなければ「監視できない」(<see cref="UnavailableFolders"/>)にして、定期の走査だけにする。</item>
/// <item>定期: <see cref="PeriodicInterval"/>(15 分)ごとに、全部の監視を作り直してから、全部の有効なフォルダを走査する(<c>EnableRaisingEvents</c> が当てにならないので、生死を判定せず毎回作り直す)。
///   スリープからの復帰(<see cref="OnResumed"/>)も同じ(全部作り直して全部走査)。</item>
/// <item>フォルダの登録・解除・有効/無効のあとは <see cref="Refresh"/> を明示的に呼ぶ(<see cref="FolderCatalog"/> にイベントは無い)。
///   解除した直後にまとめの待ちが発火して走査が <see cref="FolderRegistrationException"/> になっても、ログに出すだけで落ちない。</item>
/// </list>
/// 時刻と待ちは <see cref="TimeProvider"/>(テストで動かす)。走査の要求は <c>scan</c> に流す(製品は <c>Indexer.ScanAsync(ids, trigger)</c>。同時に 1 本の決まりは Indexer が守る)。
/// 数字(3 秒・30 秒・5 秒・15 分・2 倍・3 回・64KB)は推測の初期値。
/// </summary>
internal sealed class FolderWatcher : IDisposable
{
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan PeriodicInterval = TimeSpan.FromMinutes(15);
    internal const int GapFactor = 2;
    internal const int MaxTransientStreak = 3;
    private const int BufferSize = 64 * 1024;

    private readonly Func<IReadOnlyList<FolderInfo>> _listFolders;
    private readonly Func<IReadOnlyList<long>, ScanTrigger, CancellationToken, Task<ScanSummary>> _scan;
    private readonly TimeProvider _time;
    private readonly ICoreLog _log;
    private readonly object _gate = new();
    private readonly Dictionary<long, FolderState> _folders = [];
    private readonly CancellationTokenSource _cts = new();
    private ITimer? _timer;
    private ITimer? _periodic;
    private bool _running;
    private bool _stopped;
    private ScanTrigger? _wholeRequest;
    private Task _runTask = Task.CompletedTask;
    private int _notedEvents;

    private sealed class FolderState(long id, string path)
    {
        public long Id { get; } = id;
        public string Path { get; } = path;
        public FileSystemWatcher? Watcher { get; set; }
        public string? Unavailable { get; set; }            // 監視できない理由(作れた = null)
        public DateTimeOffset? First { get; set; }          // 数えたイベントの最初の時刻(まとめ待ち)
        public DateTimeOffset? Last { get; set; }           // 最後の時刻
        public bool Immediate { get; set; }                 // Error が来た(まとめずに、すぐ走査する)
        public bool NeedsRecreate { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTimeOffset? RetryAt { get; set; }        // 一時的に読めなかったファイルの再走査(5 秒後)
        public int TransientStreak { get; set; }
        public DateTimeOffset NextAllowed { get; set; }     // 前の走査の終わり + 所要の 2 倍
    }

    /// <param name="listFolders">登録済みのフォルダ(製品は <c>FolderCatalog.List</c>)。有効なものだけを監視する。</param>
    /// <param name="scan">走査の要求(製品は <c>Indexer.ScanAsync</c>)。</param>
    public FolderWatcher(
        Func<IReadOnlyList<FolderInfo>> listFolders,
        Func<IReadOnlyList<long>, ScanTrigger, CancellationToken, Task<ScanSummary>> scan,
        TimeProvider time, ICoreLog log)
    {
        ArgumentNullException.ThrowIfNull(listFolders);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _listFolders = listFolders;
        _scan = scan;
        _time = time;
        _log = log;
    }

    /// <summary>「監視できない」になっているフォルダ(id → 理由)。定期の走査のたびに作り直しを試す。</summary>
    public IReadOnlyDictionary<long, string> UnavailableFolders
    {
        get
        {
            lock (_gate) return _folders.Values.Where(f => f.Unavailable is not null).ToDictionary(f => f.Id, f => f.Unavailable!);
        }
    }

    /// <summary>「監視できない」の一覧が変わった(監視を作れた・作れなかった・外した)。呼ぶスレッドは決まっていない。</summary>
    public event Action? StatusChanged;

    /// <summary>テスト用: そのフォルダに、まとめの待ちに入っているイベントがあるか。</summary>
    internal bool HasPendingEvents(long folderId)
    {
        lock (_gate) return _folders.TryGetValue(folderId, out FolderState? st) && (st.Last is not null || st.Immediate);
    }

    /// <summary>テスト用: これまでに数えたイベントの数(拡張子で除いたものを除く)。OS のイベントの到着が落ち着いたかを見るため。</summary>
    internal int NotedEventCount => Volatile.Read(ref _notedEvents);

    /// <summary>テスト用: 走査の要求を処理している最中か(まとめの待ちのあとの実行・全部の走査)。</summary>
    internal bool IsBusy
    {
        get
        {
            lock (_gate) return _running;
        }
    }

    /// <summary>テスト用: そのフォルダの監視が(OS に)登録されているか。</summary>
    internal bool IsWatching(long folderId)
    {
        lock (_gate) return _folders.TryGetValue(folderId, out FolderState? st) && st.Watcher is not null;
    }

    // ------------------------------------------------------------------ 開始・フォルダの操作のあと

    /// <summary>
    /// 登録済みのフォルダの一覧と、監視の集合を合わせる(有効なフォルダだけを監視する。無くなった・無効にしたものは外し、増えたものは作る)。
    /// 起動時(走査より先)と、フォルダの登録・解除・有効/無効のあとに呼ぶ。作れなかったフォルダは「監視できない」になる。
    /// </summary>
    public void Refresh()
    {
        IReadOnlyList<FolderInfo> folders = _listFolders();
        List<FileSystemWatcher> toDispose = [];
        List<FolderState> toCreate = [];
        bool changed = false;
        lock (_gate)
        {
            if (_stopped) return;
            EnsureTimersLocked();
            var enabled = folders.Where(f => f.Enabled).ToDictionary(f => f.Id);
            foreach (long id in _folders.Keys.Where(id => !enabled.ContainsKey(id)).ToList())
            {
                FolderState gone = _folders[id];
                if (gone.Watcher is not null) toDispose.Add(gone.Watcher);
                if (gone.Unavailable is not null) changed = true;
                _folders.Remove(id);
            }
            foreach (FolderInfo f in enabled.Values)
            {
                if (_folders.ContainsKey(f.Id)) continue;
                var st = new FolderState(f.Id, f.Path);
                _folders[f.Id] = st;
                toCreate.Add(st);
            }
            ScheduleLocked();
        }
        DisposeAll(toDispose);
        foreach (FolderState st in toCreate) changed |= Create(st);
        if (changed) RaiseStatusChanged();
    }

    /// <summary>
    /// 全部の監視を作り直す(定期・復帰)。一覧との差も合わせる。作り直しの瞬間の取りこぼしは、直後の走査が拾う。
    /// </summary>
    private void RecreateAll()
    {
        Refresh();
        List<FolderState> all;
        List<FileSystemWatcher> old = [];
        lock (_gate)
        {
            all = _folders.Values.ToList();
            foreach (FolderState st in all)
            {
                if (st.Watcher is not null) old.Add(st.Watcher);
                st.Watcher = null;
            }
        }
        DisposeAll(old);
        bool changed = false;
        foreach (FolderState st in all) changed |= Create(st);
        if (changed) RaiseStatusChanged();
    }

    // ------------------------------------------------------------------ 監視の作成

    /// <summary>監視を 1 つ作る。作れなければ、理由を持って「監視できない」にする。「監視できない」の一覧が変わったら true。</summary>
    private bool Create(FolderState st)
    {
        FileSystemWatcher? w = null;
        string? error = null;
        try
        {
            if (!Directory.Exists(st.Path)) throw new DirectoryNotFoundException("フォルダが無い、または開けない(取り外したドライブ、切れたネットワークなど)");
            long id = st.Id;
            w = new FileSystemWatcher(st.Path)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = BufferSize,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            w.Created += (_, e) => NoteEvent(id, WatchedChange.Created, e.FullPath);
            w.Changed += (_, e) => NoteEvent(id, WatchedChange.Changed, e.FullPath);
            w.Deleted += (_, e) => NoteEvent(id, WatchedChange.Deleted, e.FullPath);
            w.Renamed += (_, e) => NoteEvent(id, WatchedChange.Renamed, e.FullPath);
            w.Error += (_, e) => OnError(id, e.GetException());
            w.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or PlatformNotSupportedException or InvalidOperationException)
        {
            w?.Dispose();
            w = null;
            error = e.Message;
        }

        bool changed;
        bool orphan = false;
        lock (_gate)
        {
            if (_stopped || !_folders.TryGetValue(st.Id, out FolderState? current) || !ReferenceEquals(current, st))
            {
                orphan = true;   // 作っている間に外された・止められた
                changed = false;
            }
            else
            {
                st.Watcher = w;
                changed = !string.Equals(st.Unavailable, error, StringComparison.Ordinal);
                st.Unavailable = error;
            }
        }
        if (orphan)
        {
            w?.Dispose();
            return false;
        }
        if (changed)
        {
            if (error is not null) _log.Warning($"監視: 監視できない(定期の走査だけになる): {st.Path}: {error}");
            else _log.Info($"監視: 監視できるようになった: {st.Path}");
        }
        return changed;
    }

    private static void DisposeAll(List<FileSystemWatcher> watchers)
    {
        foreach (FileSystemWatcher w in watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); }
            catch (Exception e) when (e is ObjectDisposedException or IOException) { /* もう無い */ }
        }
    }

    // ------------------------------------------------------------------ イベント(印を付けて返すだけ)

    /// <summary>
    /// 監視のイベントを数える(ハンドラから。I/O をしない)。<b>Changed だけ</b>、画像の拡張子(.png / .jpg / .jpeg / .webp)のものに絞る。
    /// Created / Deleted / Renamed は拡張子を問わず数える。<paramref name="change"/> が Error なら、まとめずにすぐ走査し、監視を作り直す。
    /// </summary>
    internal void NoteEvent(long folderId, WatchedChange change, string? path)
    {
        if (change == WatchedChange.Changed && (path is null || !ImageFormatDetector.HasSupportedExtension(path))) return;
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_stopped || !_folders.TryGetValue(folderId, out FolderState? st)) return;
            _notedEvents++;
            if (change == WatchedChange.Error)
            {
                st.Immediate = true;
                st.NeedsRecreate = true;
            }
            st.First ??= now;
            st.Last = now;
            ScheduleLocked();
        }
    }

    private void OnError(long folderId, Exception? error)
    {
        lock (_gate)
        {
            if (_folders.TryGetValue(folderId, out FolderState? st)) st.ErrorMessage = error?.Message;
        }
        NoteEvent(folderId, WatchedChange.Error, null);
    }

    // ------------------------------------------------------------------ 待ちと実行

    private void EnsureTimersLocked()
    {
        _timer ??= _time.CreateTimer(_ => StartRunIfIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _periodic ??= _time.CreateTimer(_ => { RequestWhole(ScanTrigger.Periodic); }, null, PeriodicInterval, PeriodicInterval);
    }

    /// <summary>そのフォルダを、いつ走査してよいか(まとめの期限・再試行の時刻と、前の走査からの間隔の遅いほう)。待つものが無ければ null。</summary>
    private DateTimeOffset? DueOf(FolderState st)
    {
        DateTimeOffset? basis = null;
        if (st.Immediate) basis = DateTimeOffset.MinValue;
        else if (st.Last is { } last && st.First is { } first)
        {
            DateTimeOffset quiet = last + QuietPeriod;
            DateTimeOffset max = first + MaxWait;
            basis = quiet < max ? quiet : max;
        }
        if (st.RetryAt is { } retry) basis = basis is null || retry < basis ? retry : basis;
        if (basis is null) return null;
        return basis.Value > st.NextAllowed ? basis : st.NextAllowed;
    }

    /// <summary>次に走査するフォルダの期限に合わせて、待ちを 1 つだけ掛け直す(_gate の中で)。</summary>
    private void ScheduleLocked()
    {
        if (_stopped || _timer is null) return;
        DateTimeOffset? due = null;
        foreach (FolderState st in _folders.Values)
        {
            DateTimeOffset? d = DueOf(st);
            if (d is { } v && (due is null || v < due)) due = v;
        }
        if (due is null)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }
        TimeSpan delay = due.Value - _time.GetUtcNow();
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>テスト用: 15 分ごとの定期の走査(全部作り直して全部走査)を、いま起こす。</summary>
    internal void OnPeriodic() => RequestWhole(ScanTrigger.Periodic);

    /// <summary>スリープから復帰した(<c>PowerEvents.Resumed</c>。OS のスレッドから)。全部の監視を作り直して、全部の有効なフォルダを走査する(復帰の前後の監視の挙動が未確認なので、確かめずに済む形)。</summary>
    public void OnResumed() => RequestWhole(ScanTrigger.Resume);

    private void RequestWhole(ScanTrigger trigger)
    {
        lock (_gate)
        {
            if (_stopped) return;
            // 復帰は定期より優先して表に出す(両方が重なったら復帰)
            if (_wholeRequest is null || trigger == ScanTrigger.Resume) _wholeRequest = trigger;
        }
        StartRunIfIdle();
    }

    private void StartRunIfIdle()
    {
        lock (_gate)
        {
            if (_stopped || _running) return;
            _running = true;
            _runTask = Task.Run(RunAsync, CancellationToken.None);
        }
    }

    /// <summary>期限の来たフォルダ(と、全部の走査の要求)を、1 つずつ順に走査する。やることが無くなったら、待ちを掛け直して終わる。</summary>
    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                ScanTrigger? whole;
                List<FolderState> due = [];
                lock (_gate)
                {
                    if (_stopped)
                    {
                        _running = false;
                        return;
                    }
                    whole = _wholeRequest;
                    _wholeRequest = null;
                    if (whole is null)
                    {
                        DateTimeOffset now = _time.GetUtcNow();
                        foreach (FolderState st in _folders.Values)
                            if (DueOf(st) is { } d && d <= now) due.Add(st);
                    }
                    if (whole is null && due.Count == 0)
                    {
                        _running = false;
                        ScheduleLocked();
                        return;
                    }
                }
                try
                {
                    if (whole is { } w) await ScanAllAsync(w).ConfigureAwait(false);
                    else foreach (FolderState st in due) await ScanOneAsync(st).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    lock (_gate) _running = false;
                    return;
                }
                catch (Exception e)
                {
                    _log.Error("監視: 走査の要求で想定外の例外: " + e.Message, e);   // 落ちない。次の期限で続ける
                }
            }
        }
        catch (Exception e)
        {
            lock (_gate) _running = false;
            _log.Error("監視: 想定外の例外で止まった: " + e.Message, e);
        }
    }

    /// <summary>期限の来たフォルダ 1 つ: まとめのイベントの印を消し、(Error なら監視を作り直して)走査し、一時的に読めなかった分があれば 5 秒後の再走査を予約する。</summary>
    private async Task ScanOneAsync(FolderState st)
    {
        ScanTrigger trigger;
        bool recreate;
        string? errorMessage;
        lock (_gate)
        {
            if (!_folders.TryGetValue(st.Id, out FolderState? current) || !ReferenceEquals(current, st)) return;   // 外された
            DateTimeOffset now = _time.GetUtcNow();
            bool eventDue = st.Immediate || (st.Last is { } last && st.First is { } first && Math.Min((last + QuietPeriod).UtcTicks, (first + MaxWait).UtcTicks) <= now.UtcTicks);
            trigger = eventDue ? ScanTrigger.Watcher : ScanTrigger.Retry;
            st.First = null;
            st.Last = null;
            st.Immediate = false;
            st.RetryAt = null;
            recreate = st.NeedsRecreate;
            st.NeedsRecreate = false;
            errorMessage = st.ErrorMessage;
            st.ErrorMessage = null;
        }
        if (recreate)
        {
            _log.Warning($"監視: Error が来たので、監視を作り直して走査する(あふれなど): {st.Path}" + (errorMessage is null ? "" : ": " + errorMessage));
            FileSystemWatcher? old;
            lock (_gate)
            {
                old = st.Watcher;
                st.Watcher = null;
            }
            if (old is not null) DisposeAll([old]);
            if (Create(st)) RaiseStatusChanged();
        }

        long started = _time.GetTimestamp();
        ScanSummary? summary = null;
        try
        {
            summary = await _scan([st.Id], trigger, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            throw;
        }
        catch (FolderRegistrationException e)
        {
            _log.Info($"監視: 走査しようとしたフォルダはもう登録されていない(外した直後など): {st.Path}: {e.Message}");
        }
        catch (Exception e)
        {
            _log.Warning($"監視: 走査に失敗した(次の期限でもう一度): {st.Path}: {e.Message}");
        }
        TimeSpan took = _time.GetElapsedTime(started);

        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            st.NextAllowed = now + took * GapFactor;
            if (summary is not null)
            {
                if (summary.TransientlyUnreadable > 0)
                {
                    st.TransientStreak++;
                    if (st.TransientStreak < MaxTransientStreak) st.RetryAt = now + RetryDelay;
                    else _log.Info($"監視: 一時的に読めないファイルが {MaxTransientStreak} 回続けて残った。定期の走査に任せる: {st.Path}");
                }
                else
                {
                    st.TransientStreak = 0;
                }
            }
        }
    }

    /// <summary>定期・復帰: 全部の監視を作り直してから、全部の有効なフォルダを(1 回の要求で)走査する。</summary>
    private async Task ScanAllAsync(ScanTrigger trigger)
    {
        RecreateAll();
        List<long> ids;
        lock (_gate)
        {
            ids = _folders.Keys.ToList();
            foreach (FolderState st in _folders.Values)
            {
                // この走査が全部を見るので、まとめの待ち・再試行は消す(走査の最中に来たイベントは、また印が付く)
                st.First = null;
                st.Last = null;
                st.Immediate = false;
                st.NeedsRecreate = false;
                st.RetryAt = null;
                st.TransientStreak = 0;
            }
        }
        if (ids.Count == 0) return;
        long started = _time.GetTimestamp();
        try
        {
            await _scan(ids, trigger, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            throw;
        }
        catch (FolderRegistrationException e)
        {
            _log.Info($"監視: 走査しようとしたフォルダがもう登録されていない(外した直後など): {e.Message}");
        }
        catch (Exception e)
        {
            _log.Warning($"監視: 走査に失敗した(きっかけ: {trigger}): {e.Message}");
        }
        TimeSpan took = _time.GetElapsedTime(started);
        lock (_gate)
        {
            DateTimeOffset next = _time.GetUtcNow() + took * GapFactor;
            foreach (FolderState st in _folders.Values) st.NextAllowed = next;
        }
    }

    private void RaiseStatusChanged()
    {
        Action? handler = StatusChanged;
        if (handler is null) return;
        try { handler(); }
        catch (Exception e) { _log.Error("FolderWatcher.StatusChanged の購読者が例外を投げた: " + e.Message, e); }
    }

    // ------------------------------------------------------------------ 止める

    /// <summary>監視と待ちを止めて、実行中の走査の要求が終わるのを待つ(DB を閉じる前に)。2 回呼んでもよい。</summary>
    public async Task StopAsync()
    {
        List<FileSystemWatcher> watchers = [];
        Task run;
        lock (_gate)
        {
            if (_stopped)
            {
                run = _runTask;
            }
            else
            {
                _stopped = true;
                foreach (FolderState st in _folders.Values)
                {
                    if (st.Watcher is not null) watchers.Add(st.Watcher);
                    st.Watcher = null;
                }
                _timer?.Dispose();
                _periodic?.Dispose();
                run = _runTask;
            }
        }
        _cts.Cancel();
        DisposeAll(watchers);
        try { await run.ConfigureAwait(false); }
        catch (Exception) { /* RunAsync が自分で捕まえる。ここでは握りつぶすだけ */ }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
