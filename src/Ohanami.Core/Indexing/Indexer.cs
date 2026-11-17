using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.Indexing;
using Ohanami.Core.Power;
using Ohanami.Core.Scanning;

namespace Ohanami.Core;

/// <summary>
/// 走査・ジョブの実行・一時停止・再開・失敗の再試行と、その状態の通知。App が呼ぶ窓口。
/// <list type="bullet">
/// <item>走査(<see cref="ScanAsync"/>)とジョブの実行(<see cref="StartAsync"/>)は並行してよい。走査中かどうかは <see cref="IndexState.Scan"/> が null かどうかで表す。<see cref="PauseAsync"/> の対象はジョブだけ。</item>
/// <item>ジョブは <c>JobRunner</c> の配列(優先順)で、待ちがある最初のランナーを動かす。待ちが無くなったランナーは、その Start の間は再選択しない
///   (ただし、そのあとに新しい待ちが入ったら選び直す。<see cref="RunLoopAsync"/>)。
///   別のランナーに替えるときは、前のランナーの資源を空ける(<c>ReleaseAsync</c>)。1 つのランナーを動かしている間は資源を握ったまま(<c>Yielded</c> でも)。</item>
/// <item>待ちが無くなったら、ジョブの指定を覚えたまま<b>待機</b>(<see cref="IndexRunState.Waiting"/>)に入り、資源は空ける。走査が終わったとき、<b>新しく入った待ち</b>
///   (待ち − 飛ばした集合のうち今も待ちのもの)が 1 枚以上あれば、同じ指定で自動で動き出す。一時停止は <see cref="IndexRunState.Paused"/> で、自動では動かない。起動時は Idle(ADR 0027)。</item>
/// <item>切り替え(動いているジョブより前の順のジョブへ)は、そのジョブの<b>新しく入った待ち</b>が <see cref="SwitchPendingThreshold"/> 枚以上か、最初に入ってから
///   <see cref="SwitchAfter"/> 経ったとき。数え直すのは走査の完了のときと、<c>shouldYield</c> の評価のとき(<see cref="RecountInterval"/> 以上あけて)だけ(ADR 0027)。</item>
/// <item>一時停止: ランナーに渡す ct を切る → ランナーが安全な境目で止まるのを待つ → 資源を空ける(<c>ReleaseAsync</c>)の順。待ち行列は DB にあるので、失うものはない。</item>
/// <item>呼び出し側の ct のキャンセルも同じ止め方で、状態は Idle(<c>Paused</c> でない)になり、Task はキャンセルで終わる。</item>
/// <item>ジョブの致命的な失敗(エンコーダが作れない、自己検査の失敗、想定外の例外)は、Task を失敗させず、<c>Run = Failed</c> と <c>LastError</c> で伝える。</item>
/// <item>状態の通知(<see cref="StateChanged"/>)は不変のスナップショット。進捗は 1 秒に 10 回までに間引く(末尾の 1 回は必ず送る)。実行の状態の切り替えは間引かない。イベントはスレッドプールのスレッドで呼ばれる(Dispatcher には触らない)。</item>
/// </list>
/// </summary>
public sealed class Indexer
{
    private static readonly TimeSpan MinPublishInterval = TimeSpan.FromMilliseconds(100);
    private const int RateWindow = 32;

    /// <summary>切り替える条件その 1: 前の順のジョブの「新しく入った待ち」がこの枚数以上(推測で決めた初期値。実際に溜まった状態での挙動は未確認。ADR 0027)。</summary>
    internal const int SwitchPendingThreshold = 20;

    /// <summary>切り替える条件その 2: 「新しく入った待ち」が最初に見えてからこの時間たった(初期値)。</summary>
    internal static readonly TimeSpan SwitchAfter = TimeSpan.FromMinutes(10);

    /// <summary><c>shouldYield</c> の評価で待ちを数え直す間隔の下限。<c>shouldYield</c> は埋め込みでは 1 枚約 80ms ごとに呼ばれる。</summary>
    internal static readonly TimeSpan RecountInterval = TimeSpan.FromSeconds(5);

    private readonly OhanamiDatabase _db;
    private readonly Scanner _scanner;
    private readonly IReadOnlyList<JobRunner> _runners;
    private readonly TimeProvider _time;
    private readonly string _appVersion;
    private readonly ICoreLog _log;
    private readonly StateThrottle _throttle;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly CancellationTokenSource _stopCts = new();   // StopAsync(破棄)で切る。走査をこれで止める

    // 状態(_stateLock で守る)
    private readonly object _stateLock = new();
    private IndexRunState _run = IndexRunState.Idle;
    private JobKind? _currentJob;
    private EngineStatus _engine = EngineStatus.NotCreated;
    private ScanProgress? _scan;
    private string? _currentPath;
    private string? _lastError;
    private List<JobKind> _jobs = [];
    private readonly Dictionary<JobKind, Dictionary<string, string>> _skippedSets = [];   // ランナーごとの「直近の実行で飛ばした hash → 理由」(_stateLock で守る。ADR 0027)
    private readonly Dictionary<JobKind, DateTimeOffset> _newPendingSince = [];       // ランナーごとの「新しく入った待ち」が最初に見えた時刻(_stateLock)
    private RunRecorder? _currentRun;                                                // いま動いているランナーの記録(飛ばした画像の数の表示用。_stateLock)
    private CancellationTokenSource? _pauseCts;
    private Task _runTask = Task.CompletedTask;
    private readonly Queue<long> _completions = new();
    private bool _disposed;

    private int _yieldRequested;
    private long _lastRecountTicks;   // 直近の shouldYield での数え直しの時刻(UTC の Ticks。Interlocked)
    private long _scanGeneration;     // 走査の完了ごとに 1 増える番号。RunLoop が、待機に入る直前に、選び直したあとで走査の完了が無かったかを確かめる(取り残しの窓を閉じる)

    private readonly Func<IDisposable> _startSleepGuard;
    private readonly MissingImagePurger? _purger;
    private bool _purging;   // 見つからない画像の記録を消している最中(_stateLock)。この間は、ジョブを始めない・待機から動き出さない

    /// <param name="startSleepGuard">ジョブの実行中のスリープ抑止を始める(返したものを Dispose すると解除)。null なら <see cref="SleepGuard.Start"/>(テストで差し替える)。</param>
    internal Indexer(
        OhanamiDatabase db, Scanner scanner, IReadOnlyList<JobRunner> runners, TimeProvider time, string appVersion, ICoreLog log,
        Func<IDisposable>? startSleepGuard = null, MissingImagePurger? purger = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(runners);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        ArgumentNullException.ThrowIfNull(log);
        if (runners.Select(r => r.Kind).Distinct().Count() != runners.Count) throw new ArgumentException("同じ種類のジョブのランナーが 2 つある", nameof(runners));
        _db = db;
        _scanner = scanner;
        _runners = runners;
        _time = time;
        _appVersion = appVersion;
        _log = log;
        _startSleepGuard = startSleepGuard ?? StartRealSleepGuard;
        _purger = purger;
        _throttle = new StateThrottle(time, MinPublishInterval, Publish);
    }

    /// <summary>いまの状態(その場で作る。段階ごとの件数は DB から数える)。</summary>
    public IndexState State => BuildState();

    /// <summary>状態が変わったときに呼ばれる(不変のスナップショット。進捗は 1 秒に 10 回まで)。</summary>
    public event Action<IndexState>? StateChanged;

    // ------------------------------------------------------------------ 走査

    /// <summary>走査が成功して終わったとき(きっかけ・走査したフォルダ・結果)。呼ぶのは走査を呼んだスレッドで、走査の排他(同時に 1 本)を手放したあと。購読者は軽い処理だけをする(App は UI のスレッドに渡す)。</summary>
    public event Action<ScanCompletedInfo>? ScanCompleted;

    /// <summary>登録済みで有効なフォルダ(または指定のもの)を手動のきっかけで走査する(<see cref="ScanAsync(IReadOnlyCollection{long}?, ScanTrigger, CancellationToken)"/> を参照)。</summary>
    public Task<ScanSummary> ScanAsync(IReadOnlyCollection<long>? folderIds = null, CancellationToken ct = default) =>
        ScanAsync(folderIds, ScanTrigger.Manual, ct);

    /// <summary>
    /// 登録済みで有効なフォルダ(または指定のもの)を走査して、差分を DB に反映する。同時に 2 つは走らせず、順に待つ。
    /// <paramref name="trigger"/> は走査を始めたきっかけ(ログと <see cref="ScanCompleted"/> に載る)。始めと終わりをログに出し、成功したら <see cref="ScanCompleted"/> を起こす。
    /// 走査の完了は、ジョブの切り替え・待機からの再開の判定(<see cref="OnScanCompleted"/>)も起こす。
    /// </summary>
    public async Task<ScanSummary> ScanAsync(IReadOnlyCollection<long>? folderIds, ScanTrigger trigger, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 破棄(StopAsync)でも止まるようにする: 呼び出し側の ct と、破棄の印をつなぐ(DB を閉じたあとに走査が続かないように)
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopCts.Token);
        await _scanLock.WaitAsync(linked.Token).ConfigureAwait(false);
        IReadOnlyList<long> ids;
        ScanSummary summary;
        try
        {
            ids = _scanner.ResolveFolderIds(folderIds);   // 無い id は FolderRegistrationException(走査と同じ)
            _log.Info($"走査: 始める(きっかけ: {TriggerName(trigger)}、フォルダ {ids.Count.ToString(CultureInfo.InvariantCulture)} 個)");
            SetScan(new ScanProgress(0, 0, 0, 0, 0, 0, 0, 0, 0));
            try
            {
                summary = await _scanner.ScanAsync(ids, SetScan, linked.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warning($"走査: 失敗した(きっかけ: {TriggerName(trigger)}): {e.Message}");
                throw;
            }
            _log.Info(ScanEndLine(summary));
            OnScanCompleted();
        }
        finally
        {
            SetScan(null);
            _scanLock.Release();
        }
        RaiseScanCompleted(new ScanCompletedInfo(trigger, ids, summary));
        return summary;
    }

    private void RaiseScanCompleted(ScanCompletedInfo info)
    {
        Action<ScanCompletedInfo>? handler = ScanCompleted;
        if (handler is null) return;
        try { handler(info); }
        catch (Exception e) { _log.Error("ScanCompleted の購読者が例外を投げた: " + e.Message, e); }
    }

    private static string TriggerName(ScanTrigger trigger) => trigger switch
    {
        ScanTrigger.Startup => "起動",
        ScanTrigger.Manual => "手動",
        ScanTrigger.Watcher => "監視",
        ScanTrigger.Periodic => "定期",
        ScanTrigger.Resume => "復帰",
        ScanTrigger.Missing => "見つからない",
        ScanTrigger.Retry => "再試行",
        _ => trigger.ToString(),
    };

    private static string ScanEndLine(ScanSummary s)
    {
        var parts = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"新規 {s.Added}"),
            string.Create(CultureInfo.InvariantCulture, $"変更 {s.Changed}"),
            string.Create(CultureInfo.InvariantCulture, $"消えた {s.Missing}"),
        };
        if (s.TransientlyUnreadable > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"一時的に読めない {s.TransientlyUnreadable}"));
        if (s.Unreadable > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"読めない {s.Unreadable}"));
        if (s.Unsupported > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"未対応の形式 {s.Unsupported}"));
        if (s.Unreachable > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"到達できない所 {s.Unreachable}"));
        parts.Add(string.Create(CultureInfo.InvariantCulture, $"所要 {s.Elapsed.TotalSeconds:0.0} 秒"));
        return "走査: 終わった(" + string.Join("、", parts) + ")";
    }

    /// <summary>
    /// 走査が終わったときの判定(ADR 0027)。
    /// <list type="bullet">
    /// <item>ジョブが動いている(<see cref="IndexRunState.Running"/>)とき: いま動いているジョブより<b>前の順</b>のジョブの「新しく入った待ち」が、切り替える条件
    ///   (<see cref="SwitchPendingThreshold"/> 枚以上、または最初に入ってから <see cref="SwitchAfter"/>)を満たしたら、切り替えの合図(<see cref="RequestYield"/>)を立てる。</item>
    /// <item>待機中(<see cref="IndexRunState.Waiting"/>)のとき: 覚えているジョブの指定のどれかに「新しく入った待ち」が 1 枚以上あれば、同じ指定で自動で動き出す。
    ///   待機からの判定は、ここ(走査の完了)だけで行う。</item>
    /// <item>それ以外(Idle・Paused・Failed など)は何もしない。</item>
    /// </list>
    /// </summary>
    private void OnScanCompleted()
    {
        // 先に世代を進める: RunLoop は「選べるランナーが無い」と判断したあと、待機に入る(_stateLock の中で状態を替える)前に、この番号が変わっていないかを見る
        Interlocked.Increment(ref _scanGeneration);
        IndexRunState run;
        lock (_stateLock) run = _run;
        if (run == IndexRunState.Running)
        {
            Interlocked.Exchange(ref _lastRecountTicks, _time.GetUtcNow().UtcTicks);   // 直後の shouldYield で、同じ数え直しをもう一度しない
            if (ShouldSwitchNow()) RequestYield();
        }
        else if (run == IndexRunState.Waiting)
        {
            TryContinueFromWaiting();
        }
    }

    /// <summary>
    /// いま動いているジョブより前の順のジョブのうち、切り替える条件を満たしたものがあるか。動いていない、いまのジョブが最優先なら false(今の短絡)。
    /// 「新しく入った待ち」が 0 になったジョブは、最初に見えた時刻の記録を消す(ヒステリシスのやり直し)。
    /// </summary>
    private bool ShouldSwitchNow()
    {
        JobKind current;
        List<JobKind> order;
        lock (_stateLock)
        {
            if (_run != IndexRunState.Running || _currentJob is not { } c) return false;
            current = c;
            order = _jobs;
        }
        int currentIndex = order.IndexOf(current);
        DateTimeOffset now = _time.GetUtcNow();
        for (int i = 0; i < currentIndex; i++)
        {
            JobRunner? runner = _runners.FirstOrDefault(r => r.Kind == order[i]);
            if (runner is null) continue;
            int fresh = CountNewPending(runner);
            DateTimeOffset since;
            lock (_stateLock)
            {
                if (fresh == 0)
                {
                    _newPendingSince.Remove(runner.Kind);
                    continue;
                }
                if (!_newPendingSince.TryGetValue(runner.Kind, out since)) _newPendingSince[runner.Kind] = since = now;
            }
            if (fresh >= SwitchPendingThreshold || now - since >= SwitchAfter) return true;
        }
        return false;
    }

    /// <summary>
    /// ランナーの「新しく入った待ち」= 待ち − 飛ばした集合のうち今も待ちのもの(hash の IN で 1 回数える)。
    /// 飛ばした集合は、そのランナーの直近の実行で読めずに飛ばした画像。数ではなく hash で引くので、飛ばした画像が消えるのと新着が同じ走査で起きても、新着は新着として数える。
    /// </summary>
    private int CountNewPending(JobRunner runner)
    {
        if (runner.UnavailableFiles().Count > 0) return 0;   // モデルのファイルが無くて動かせないランナーの待ちは、新しい待ちに数えない(待機から動き出さない・切り替えの対象にしない)
        int pending = runner.CountPendingImages();
        if (pending == 0) return 0;
        string[] skipped;
        lock (_stateLock) skipped = _skippedSets.TryGetValue(runner.Kind, out Dictionary<string, string>? set) ? set.Keys.ToArray() : [];
        int old = skipped.Length == 0 ? 0 : runner.CountPendingAmong(skipped);
        return Math.Max(0, pending - old);
    }

    /// <summary>
    /// ランナーが、モデルのファイルが無くて動かせないか(<see cref="JobRunner.UnavailableFiles"/>)。動かせないときは、<paramref name="logged"/> に初めて入れたとき(1 回の実行に 1 回)だけ、理由をログに出す。
    /// 失敗ではない: 待ちの画像は待ちのまま残り、ファイルがそろったあとの次の開始(または待機中の次の走査の完了)で動き出す。
    /// </summary>
    private bool IsUnavailable(JobRunner runner, HashSet<JobKind>? logged)
    {
        IReadOnlyList<string> missing = runner.UnavailableFiles();
        if (missing.Count == 0) return false;
        if (logged is not null && logged.Add(runner.Kind))
            _log.Info($"ジョブ: {JobName(runner.Kind)} は、モデルのファイルが無いので動かさない(未取得: {string.Join("、", missing)})");
        return true;
    }

    /// <summary>
    /// 待機中に、新しく入った待ちが 1 枚以上あれば、同じジョブの指定で自動で動き出す(<see cref="StartCore"/>。飛ばした集合は消さない)。
    /// 切り替えの条件(20 枚・10 分)は使わない: 待機中は GPU の資源が空いていて、動き出しに往復の費用が無い。
    /// </summary>
    private void TryContinueFromWaiting()
    {
        List<JobKind> jobs;
        lock (_stateLock)
        {
            if (_run != IndexRunState.Waiting) return;
            jobs = _jobs;
        }
        // 新しい待ちは、段階(ランナー)ごとに数える。足した合計にはしない: 1 枚の新着は [埋め込み, VLM] のそれぞれの待ちに入るので、合計は画像の枚数の倍になって、
        // ログの「新しい画像 2 枚」が、1 枚の新着と読めなくなるため。動き出すかどうかは「どれかに 1 枚以上」
        var fresh = new List<(JobKind Kind, int Count)>();
        foreach (JobKind kind in jobs)
        {
            JobRunner? runner = _runners.FirstOrDefault(r => r.Kind == kind);
            if (runner is not null) fresh.Add((kind, CountNewPending(runner)));
        }
        if (fresh.All(f => f.Count == 0)) return;
        Task? task;
        try
        {
            task = StartCore(jobs, CancellationToken.None, auto: true);
        }
        catch (Exception e)
        {
            _log.Error("待機からジョブを自動で始められなかった: " + e.Message, e);
            return;
        }
        if (task is null) return;   // 数えている間に、一時停止・開始・破棄で状態が変わった
        _log.Info($"ジョブ: 待機から続ける(新しい待ち {string.Join("、", fresh.Select(f => string.Create(CultureInfo.InvariantCulture, $"{JobName(f.Kind)} {f.Count} 枚")))}。指定: {string.Join("、", jobs.Select(JobName))})");
        _ = ObserveAutoRunAsync(task);
    }

    /// <summary>自動で始めた実行の Task は、誰も await しない。例外は観測してログに残す(RunLoop は致命的な失敗を Failed の状態にして終わるので、通常はここに来ない)。</summary>
    private async Task ObserveAutoRunAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 呼び出し側の ct は無い(None)ので通常は来ない
        }
        catch (Exception e)
        {
            _log.Error("自動で始めたジョブの Task が例外で終わった: " + e.Message, e);
        }
    }

    private void SetScan(ScanProgress? progress)
    {
        lock (_stateLock) _scan = progress;
        _throttle.Request();
    }

    // ------------------------------------------------------------------ ジョブの実行

    /// <summary>
    /// ジョブの実行を始める。待ち行列は毎回 DB から組み立てる。待ちが無くなったら、指定を覚えたまま待機(<see cref="IndexRunState.Waiting"/>)に入って Task が終わる。
    /// Pause / キャンセルされたときも Task が終わる。実行中(Running / Pausing)に呼ぶと <see cref="InvalidOperationException"/>。
    /// Paused と Waiting のときは、新しい指定で始める(例外にしない)。飛ばした集合は、ここで空にする(利用者が始めたので、読めなかった画像をもう一度試す)。
    /// </summary>
    public Task StartAsync(IReadOnlyList<JobKind> jobs, CancellationToken ct = default) => StartCore(jobs, ct, auto: false)!;

    /// <summary>
    /// <see cref="StartAsync"/> の本体。<paramref name="auto"/> は待機からの自動の再開(走査の完了で新しい待ちが入った)で、Waiting のときだけ始め(違えば null を返す)、
    /// 飛ばした集合を消さない(消すと、読めない画像が毎回「新しい待ち」になって、待機と実行を繰り返す)。
    /// </summary>
    private Task? StartCore(IReadOnlyList<JobKind> jobs, CancellationToken ct, bool auto)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Count == 0) throw new ArgumentException("ジョブが 1 つも無い", nameof(jobs));
        var selected = new List<JobRunner>();
        foreach (JobKind kind in jobs.Distinct())
        {
            JobRunner runner = _runners.FirstOrDefault(r => r.Kind == kind)
                ?? throw new ArgumentException($"ジョブ {kind} のランナーが無い(この版では動かせない)", nameof(jobs));
            selected.Add(runner);
        }

        Task task;
        lock (_stateLock)
        {
            if (auto)
            {
                if (_disposed || _purging || _run != IndexRunState.Waiting) return null;   // 消している間は、待機から動き出さない(取った待ちを消される)
            }
            else
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_run is IndexRunState.Running or IndexRunState.Pausing)
                    throw new InvalidOperationException($"ジョブは実行中(状態 {_run})。止めてから始める。");
                if (_purging) throw new InvalidOperationException("見つからない画像の記録を消している最中は、ジョブを始められない。");
            }
            _jobs = jobs.Distinct().ToList();
            _run = IndexRunState.Running;
            _currentJob = null;
            _currentPath = null;
            _lastError = null;
            if (!auto)
            {
                _skippedSets.Clear();
                _newPendingSince.Clear();
            }
            _currentRun = null;
            _engine = EngineStatus.NotCreated;
            _completions.Clear();
            Interlocked.Exchange(ref _yieldRequested, 0);
            var pause = new CancellationTokenSource();
            _pauseCts = pause;
            task = Task.Run(() => RunLoopAsync(selected, pause, ct), CancellationToken.None);
            _runTask = task;
        }
        _throttle.PublishNow();
        return task;
    }

    /// <summary>
    /// 実行中のランナーに止まるよう伝え(ランナーに渡す ct を切る)、止まるのを待ってから、資源を空ける。止まったら戻り、状態は Paused。
    /// 待機中(<see cref="IndexRunState.Waiting"/>)なら、待つ Task は無いので、すぐ Paused にする(以後、走査が終わっても自動では動かない)。
    /// それ以外でジョブが動いていなければ何もしない。
    /// </summary>
    public async Task PauseAsync()
    {
        Task task;
        bool fromWaiting = false;
        lock (_stateLock)
        {
            if (_run == IndexRunState.Waiting)
            {
                _run = IndexRunState.Paused;
                fromWaiting = true;
                task = Task.CompletedTask;
            }
            else if (_run == IndexRunState.Running && _pauseCts is not null)
            {
                _run = IndexRunState.Pausing;
                task = _runTask;
                _pauseCts.Cancel();
            }
            else
            {
                return;
            }
        }
        if (fromWaiting) _log.Info("ジョブ: 一時停止した(待機中から)");
        _throttle.PublishNow();
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* 呼び出し側の ct も切れていた。状態は Idle */ }
    }

    /// <summary>Paused のとき、前回と同じジョブの指定で続きを始める(待ち行列は DB から組み立て直す)。Paused でなければ何もしない。</summary>
    public Task ResumeAsync(CancellationToken ct = default)
    {
        List<JobKind> jobs;
        lock (_stateLock)
        {
            if (_run != IndexRunState.Paused) return Task.CompletedTask;
            jobs = _jobs;
        }
        return StartAsync(jobs, ct);
    }

    /// <summary>消せる画像(見つからない画像。現存するパスが 1 つも無い画像。パスの行が 0 行のものを含む)の数。</summary>
    public Task<int> CountMissingImagesAsync(CancellationToken ct = default) => Purger.CountAsync(ct);

    /// <summary>
    /// 消せる画像(見つからない画像)の一覧(読むだけ)。条件は <see cref="PurgeMissingAsync"/> と同じ。無くなった日時の新しい順、上限つき
    /// (<paramref name="limit"/> の既定は <see cref="MissingImagePurger.DefaultListLimit"/>)。件数(<see cref="MissingImageList.Total"/>)は上限で切る前の数。
    /// </summary>
    public Task<MissingImageList> ListMissingImagesAsync(int limit = MissingImagePurger.DefaultListLimit, CancellationToken ct = default) => Purger.ListAsync(limit, ct);

    /// <summary>
    /// 見つからない画像の記録を消す(明示的な操作。消した画像の数を返す)。<see cref="MissingImagePurger"/> が、子の表から順に 1 つのトランザクションで消す。
    /// <b>ジョブが動いている間(Running・Pausing)は拒む</b>(<see cref="InvalidOperationException"/>): ランナーは取った待ちの hash をメモリに持ち、消されたあとに stage_status を書くと
    /// 外部キーで失敗して Run が Failed になる。待機中・一時停止中・Idle は可で、<b>消している間は待機から動き出さない・Start できない</b>(動き出したランナーが、消す前の待ちを取らないように)。
    /// 走査中は可(消すトランザクションと走査のバッチは同じ書き込みの接続で直列になる。消した画像が走査で戻っても、新しく登録し直されるだけ)。
    /// 終わったあと、待機中なら、新しく入った待ちが無いかを 1 度数える(消している間に走査が終わって、動き出しを止められていた分)。
    /// </summary>
    public async Task<int> PurgeMissingAsync(CancellationToken ct = default)
    {
        MissingImagePurger purger = Purger;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_run is IndexRunState.Running or IndexRunState.Pausing)
                throw new InvalidOperationException($"ジョブの実行中(状態 {_run})は、見つからない画像の記録を消せない。止めてから消す。");
            if (_purging) throw new InvalidOperationException("見つからない画像の記録を消している最中");
            _purging = true;
        }
        int removed;
        try
        {
            removed = await purger.PurgeAsync(ct).ConfigureAwait(false);
            _log.Info($"見つからない画像の記録を消した({removed.ToString(CultureInfo.InvariantCulture)} 枚)");
        }
        finally
        {
            lock (_stateLock) _purging = false;
        }
        _throttle.Request();
        TryContinueFromWaiting();
        return removed;
    }

    private MissingImagePurger Purger => _purger ?? throw new InvalidOperationException("この構成では、見つからない画像の記録を消せない(消す部品が結ばれていない)");

    /// <summary>
    /// 失敗した画像を、もう一度待ち行列に戻す(手動)。そのジョブが埋める段階の status = failed の行を消す(待ち行列は「行が無い画像」なので、次の Start で再試行される)。
    /// 以前の失敗の回数(attempts)は消える。
    /// </summary>
    public async Task RetryFailedAsync(JobKind job, CancellationToken ct = default)
    {
        JobRunner runner = RunnerOf(job);
        string failed = DbEnums.ToDb(StageStatus.Failed);
        await _db.WriteAsync((conn, tx) =>
        {
            foreach (Stage stage in runner.Stages)
                Exec(conn, tx, "DELETE FROM stage_status WHERE stage = $stage AND status = $status",
                    ("$stage", DbEnums.ToDb(stage)), ("$status", failed));
        }, ct).ConfigureAwait(false);
        _throttle.Request();
    }

    /// <summary>
    /// 現行の model_id と違うモデルで完了している結果(要再作成=Outdated)を、もう一度待ち行列に戻す(手動)。
    /// stage_status の行だけを消す。古いモデルのベクトルなどの結果(image_vectors)は残す(結果は (hash, model_id) で持つ)。
    /// </summary>
    public async Task RequeueOutdatedAsync(JobKind job, CancellationToken ct = default)
    {
        JobRunner runner = RunnerOf(job);
        string done = DbEnums.ToDb(StageStatus.Done);
        await _db.WriteAsync((conn, tx) =>
        {
            foreach (Stage stage in runner.Stages)
                Exec(conn, tx, "DELETE FROM stage_status WHERE stage = $stage AND status = $status AND model_id <> $model",
                    ("$stage", DbEnums.ToDb(stage)), ("$status", done), ("$model", runner.CurrentModelIds[stage]));
        }, ct).ConfigureAwait(false);
        _throttle.Request();
    }

    /// <summary>今の 1 枚を終えたら戻って、ランナーを選び直す合図(「優先のジョブに待ちが出た」などの切り替え用)。</summary>
    internal void RequestYield() => Interlocked.Exchange(ref _yieldRequested, 1);

    /// <summary>実行中の処理を止めて、資源を空ける(OhanamiCore の DisposeAsync から)。</summary>
    internal async Task StopAsync()
    {
        Task task;
        lock (_stateLock)
        {
            _disposed = true;
            task = _runTask;
            _pauseCts?.Cancel();
        }
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        // 走査も止めて、終わるのを待つ(ロックを取れるのは、走査が終わったあと)
        await _stopCts.CancelAsync().ConfigureAwait(false);
        await _scanLock.WaitAsync().ConfigureAwait(false);
        _scanLock.Release();
        _throttle.Dispose();
    }

    // ------------------------------------------------------------------ 実行ループ

    private async Task RunLoopAsync(List<JobRunner> selected, CancellationTokenSource pause, CancellationToken callerCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerCt, pause.Token);
        var exhausted = new HashSet<JobRunner>();
        var loggedUnavailable = new HashSet<JobKind>();   // 動かせない(モデルのファイルが無い)と、この実行でログに出したジョブ
        var drainedBaseline = new Dictionary<JobRunner, int>();   // Drained になった時点の「新しく入った待ち」。これより増えたら選び直す
        bool disposing = false;
        bool paused, waiting;
        var session = new RunSession(_time.GetUtcNow());
        Exception? fatal;
        IDisposable? sleepGuard = StartSleepGuard();   // ジョブが Running の間(一時停止・完了・失敗で解除)
        try
        {
            // 外側のループ: 待機に入る直前(資源を空けたあと)に、走査の完了が入っていたら、もう一度内側のループを回す(待機に入る直前に走査の完了が届くと、動き出す判定が取り残されるので拾う。ADR 0027)
            while (true)
            {
                JobRunner? current = null;
                RunRecorder? run = null;
                DateTimeOffset runStarted = default;
                RunEnd? lastEnd = null;                       // いまのランナーの直近の RunAsync の終わり方(例外で終わったら null のまま)
                bool drainedAll = false;                      // 選べるランナーが無くなって、ループを抜けた(待機に入る)
                long seenGeneration = 0;                      // 最後に「選べるランナーが無い」と判断したときの、選び直しの前の走査の世代
                fatal = null;
                try
                {
                    while (!linked.IsCancellationRequested)
                    {
                        long generation = Volatile.Read(ref _scanGeneration);   // 選び直し(待ちの数え直し)の前に読む
                        JobRunner? next = null;
                        foreach (JobRunner r in selected)
                        {
                            if (exhausted.Contains(r) || IsUnavailable(r, loggedUnavailable)) continue;
                            if (r.Summarize().Sum(s => s.Pending) > 0) { next = r; break; }
                        }
                        if (next is null)
                        {
                            // 待ちを使い切って exhausted に入ったランナーに、そのあとで新しい待ち(Drained になった時点より増えた分)が入っていたら、選び直す。
                            // 前の順のジョブが Drained のあとに、後ろのジョブの実行中に新着が来て、切り替える条件(20 枚・10 分)に届かないまま後ろのジョブが終わると、
                            // 新着が待機に入ったまま取り残される(次の走査の完了まで処理されない)ため。待ちが減らない偽のランナーや、飛ばした画像では、増えないので回らない
                            foreach (JobRunner r in selected)
                            {
                                if (exhausted.Contains(r) && drainedBaseline.TryGetValue(r, out int baseline) && CountNewPending(r) > baseline)
                                {
                                    exhausted.Remove(r);
                                    next = r;
                                    break;
                                }
                            }
                        }
                        if (next is null)
                        {
                            // 数えている間に走査が終わっていたら、その新着を見ていないかもしれない。資源を空ける前に、もう一度選び直す
                            if (Volatile.Read(ref _scanGeneration) != generation) continue;
                            seenGeneration = generation;
                            drainedAll = true;   // すべてのランナーで待ちが無い(または今回はもう動かせない)
                            break;
                        }

                        if (!ReferenceEquals(next, current))
                        {
                            await FinishRunAsync(current, run, runStarted, lastEnd, "切り替え", session).ConfigureAwait(false);
                            current = next;
                            runStarted = _time.GetUtcNow();
                            lastEnd = null;
                            run = NewRecorder(next.Kind);
                            lock (_stateLock)
                            {
                                _completions.Clear();   // 速さ(枚/秒)は、いまのジョブの分だけで測る(埋め込みと VLM では桁が違う)
                                _currentRun = run;
                                _newPendingSince.Remove(next.Kind);   // このランナーが動くので、「新しく入った待ち」の最初に見えた時刻はやり直し
                            }
                        }
                        lock (_stateLock) _currentJob = next.Kind;
                        _throttle.Request();

                        RunEnd end = await current!.RunAsync(run!, ShouldYield, linked.Token).ConfigureAwait(false);
                        lastEnd = end;
                        if (end == RunEnd.Drained)
                        {
                            // 待ちを使い切った実行の飛ばした集合で置き換える(ADR 0027)。そのうえで、この時点の「新しく入った待ち」を基準にする
                            CommitSkipped(current.Kind, run!, replace: true);
                            drainedBaseline[current] = CountNewPending(current);
                        }
                        // 合図は「選び直してほしい」の意味。Drained になったランナー(優先のもの)にも、新着で仕事が出たかもしれないので、もう一度選び直す対象に戻す。
                        // Drained でも、ランナーが最後に合図を見たあと(Drained を返すまでの数 ms)に走査の完了が合図を立てることがある。その合図を見落とすと、
                        // 後ろのランナーは exhausted に入ったままで選ばれず、新着が次の Start まで処理されない。だから、Drained のときも合図を見る
                        if (end == RunEnd.Yielded || (end == RunEnd.Drained && ShouldYield()))
                        {
                            Interlocked.Exchange(ref _yieldRequested, 0);
                            exhausted.Clear();
                        }
                        else if (end == RunEnd.Drained) exhausted.Add(current);
                        else if (end == RunEnd.Cancelled) break;
                    }
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    // ランナーが投げたキャンセル。止め方は Cancelled と同じ
                }
                catch (Exception e)
                {
                    fatal = e;
                    _log.Error("ジョブの実行が失敗した: " + e.Message, e);
                }
                finally
                {
                    bool pausedNow = pause.IsCancellationRequested && !callerCt.IsCancellationRequested;
                    lock (_stateLock) disposing = _disposed;
                    // 資源を空ける理由(ログ)。VLM の llama-server の停止の行に付く
                    string reason = fatal is not null ? "失敗"
                        : disposing || callerCt.IsCancellationRequested ? "終了"
                        : pausedNow ? "一時停止"
                        : drainedAll ? "待機"
                        : "終了";
                    await FinishRunAsync(current, run, runStarted, lastEnd, reason, session).ConfigureAwait(false);
                }

                paused = pause.IsCancellationRequested && !callerCt.IsCancellationRequested;
                // 待機: 待ちが無くなって終わった(一時停止・呼び出し側の ct・破棄・失敗のときは入らない)。ジョブの指定(_jobs)は覚えたまま
                waiting = fatal is null && !paused && drainedAll && !callerCt.IsCancellationRequested;
                bool again = false;
                lock (_stateLock)
                {
                    if (waiting && Volatile.Read(ref _scanGeneration) != seenGeneration)
                    {
                        // 「選べるランナーが無い」と判断してから、ここ(待機に入る)までの間に走査が終わった(資源を空けている間など)。
                        // 待機に入ると、その新着は次の走査の完了まで見られないので、もう一度選び直す(状態は Running のまま)
                        again = true;
                    }
                    else
                    {
                        _currentJob = null;
                        _currentPath = null;
                        _currentRun = null;
                        if (fatal is not null)
                        {
                            _run = IndexRunState.Failed;
                            _lastError = fatal.Message;
                            if (fatal is NativeRuntimeException && _engine.Phase != EnginePhase.Failed)
                                _engine = new EngineStatus(EnginePhase.Failed, fatal.Message, null);
                        }
                        else
                        {
                            _run = paused ? IndexRunState.Paused : waiting ? IndexRunState.Waiting : IndexRunState.Idle;
                        }
                        if (_engine.Phase != EnginePhase.Failed) _engine = EngineStatus.NotCreated;
                    }
                }
                if (!again) break;
            }
        }
        finally
        {
            sleepGuard?.Dispose();
        }

        _log.Info(SessionEndLine(session, fatal is not null, disposing, paused, waiting));
        _throttle.PublishNow();
        if (fatal is null && !paused) callerCt.ThrowIfCancellationRequested();
    }

    /// <summary>ある段階(ランナー)が、1 回の実行の通しで処理・失敗にした数。</summary>
    private sealed record StageCount(JobKind Kind, int Completed, int Failed);

    /// <summary>
    /// 1 回の実行(Start から待機・一時停止・失敗・終了まで)の通しの件数。ジョブの完了のログ用。
    /// 処理・失敗は<b>段階(ランナー)ごと</b>に持つ(足した合計にしない。1 枚が埋め込みと VLM を通ると、合計は 2 になって、画像の枚数に見えてしまう)。
    /// 飛ばした画像だけは、hash の和集合(画像の数)。
    /// </summary>
    private sealed class RunSession(DateTimeOffset started)
    {
        public DateTimeOffset Started { get; } = started;

        /// <summary>段階ごとの件数。最初に動いた順(同じ段階が切り替えで何度か動いたら、足す)。</summary>
        public List<StageCount> Stages { get; } = [];

        public HashSet<string> Skipped { get; } = new(StringComparer.Ordinal);

        public void Add(RunRecorder run)
        {
            int i = Stages.FindIndex(s => s.Kind == run.Kind);
            if (i < 0) Stages.Add(new StageCount(run.Kind, run.CompletedCount, run.FailedCount));
            else Stages[i] = Stages[i] with { Completed = Stages[i].Completed + run.CompletedCount, Failed = Stages[i].Failed + run.FailedCount };
            Skipped.UnionWith(run.SkippedHashes);
        }
    }

    private string SessionEndLine(RunSession session, bool failed, bool disposing, bool paused, bool waiting)
    {
        string counts = CountsText(session.Stages, session.Skipped.Count, _time.GetUtcNow() - session.Started);
        if (failed) return $"ジョブ: 失敗で止まった({counts})";
        if (disposing) return $"ジョブ: 終了した({counts})";
        if (paused) return $"ジョブ: 一時停止した({counts})";
        if (waiting) return $"ジョブ: 待機に入る(新しい画像が来たら続ける。{counts})";
        return $"ジョブ: 終了した({counts})";
    }

    /// <summary>1 つのランナーの行(「ジョブ: 埋め込みが終わった」など)の件数。その段階の数(行の主語が段階)。飛ばしたのは画像の数。</summary>
    private static string CountsText(int completed, int failed, int skippedImages, TimeSpan elapsed) =>
        string.Create(CultureInfo.InvariantCulture, $"処理 {completed} 枚、失敗 {failed}、飛ばした(画像) {skippedImages} 枚、所要 {ElapsedText(elapsed)}");

    /// <summary>
    /// 実行の通しの行(待機に入る・一時停止・失敗・終了)の件数。処理・失敗は段階ごとの内訳(「処理 埋め込み 1 枚・VLM 1 枚」)。
    /// 1 枚が複数の段階を通ると、段階ごとに 1 回ずつ数えるので、内訳の足し算は画像の枚数ではない。飛ばしたのは画像の数(和集合)。
    /// </summary>
    private static string CountsText(IReadOnlyList<StageCount> stages, int skippedImages, TimeSpan elapsed)
    {
        string done = stages.Count == 0 ? "0 枚" : string.Join("・", stages.Select(s => string.Create(CultureInfo.InvariantCulture, $"{JobName(s.Kind)} {s.Completed} 枚")));
        string failed = stages.Count == 0 ? "0" : string.Join("・", stages.Select(s => string.Create(CultureInfo.InvariantCulture, $"{JobName(s.Kind)} {s.Failed}")));
        return string.Create(CultureInfo.InvariantCulture, $"処理 {done}、失敗 {failed}、飛ばした(画像) {skippedImages} 枚、所要 {ElapsedText(elapsed)}");
    }

    private static string ElapsedText(TimeSpan elapsed) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}");

    private static string JobName(JobKind kind) => kind switch
    {
        JobKind.Embed => "埋め込み",
        JobKind.Vlm => "VLM",
        _ => kind.ToString(),
    };

    /// <summary>ログの文の主語(「ジョブ: 埋め込みが終わった」「ジョブ: VLM が終わった」)。英数字で終わる名前には、助詞の前に空白を入れる。</summary>
    private static string JobSubject(JobKind kind)
    {
        string name = JobName(kind);
        return char.IsAscii(name[^1]) ? name + " " : name;
    }

    /// <summary>
    /// ランナーの実行を締める: 飛ばした集合を確定し、(VLM なら理由つきで停止の行を出して)資源を空け、runs の ended_at を書き、件数のログを出す。
    /// <paramref name="reason"/> は 切り替え / 一時停止 / 待機 / 終了 / 失敗。<paramref name="end"/> は直近の RunAsync の終わり方(例外なら null)。
    /// </summary>
    private async Task FinishRunAsync(JobRunner? runner, RunRecorder? run, DateTimeOffset started, RunEnd? end, string reason, RunSession session)
    {
        if (runner is null) return;
        if (run is not null)
        {
            CommitSkipped(runner.Kind, run, replace: end == RunEnd.Drained);
            session.Add(run);
            lock (_stateLock) { if (ReferenceEquals(_currentRun, run)) _currentRun = null; }
        }
        // 止める理由は Indexer だけが知っている(JobRunner.ReleaseAsync に理由の引数を足さない)。llama-server が動いた実行(runs の行がある)だけ
        if (runner.Kind == JobKind.Vlm && run is { Started: true }) _log.Info($"ジョブ: VLM を止める(理由: {reason})");
        try { await runner.ReleaseAsync().ConfigureAwait(false); }
        catch (Exception e) { _log.Error("資源を空けるのに失敗した: " + e.Message, e); }
        if (run is null) return;
        try { await run.EndAsync().ConfigureAwait(false); }
        catch (Exception e) { _log.Error("runs の ended_at を書けなかった: " + e.Message, e); }
        string counts = CountsText(run.CompletedCount, run.FailedCount, run.SkippedCount, _time.GetUtcNow() - started);
        _log.Info(end == RunEnd.Drained
            ? $"ジョブ: {JobSubject(runner.Kind)}が終わった({counts})"
            : $"ジョブ: {JobSubject(runner.Kind)}を止めた(理由: {reason}。{counts})");
    }

    /// <summary>
    /// 実行の飛ばした画像を、ランナーごとの「直近の実行で飛ばした集合」に入れる。待ちを使い切って終わった実行(<paramref name="replace"/>)は置き換え、
    /// それ以外(切り替えで中断・一時停止・失敗)は足す(中断した実行の飛ばした分を、待ちの判定から落とさない)。
    /// </summary>
    private void CommitSkipped(JobKind kind, RunRecorder run, bool replace)
    {
        IReadOnlyDictionary<string, string> reasons = run.SkippedReasons;
        lock (_stateLock)
        {
            if (replace || !_skippedSets.TryGetValue(kind, out Dictionary<string, string>? set))
                _skippedSets[kind] = set = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string hash, string reason) in reasons) set[hash] = reason;
        }
    }

    /// <summary>
    /// 直近の実行で、読めなくて飛ばした画像(理由つき。記録していない=stage_status に行が無く、待ちに数えている)。
    /// 寿命は ADR 0027 の飛ばした集合と同じ(ランナーごとに、待ちを使い切った実行で置き換え、利用者の <see cref="StartAsync"/> で空になる。メモリだけ)。いま動いている実行の分も含む。
    /// <see cref="SkippedItem.Path"/> は、いま現存するパスの先頭(無ければ null)。並びは、ジョブの種類 → パス → hash。
    /// </summary>
    public Task<IReadOnlyList<SkippedItem>> ListSkippedAsync(CancellationToken ct = default)
    {
        var reasons = new List<(JobKind Job, string Hash, string Reason)>();
        lock (_stateLock)
        {
            foreach ((JobKind kind, Dictionary<string, string> set) in _skippedSets)
                foreach ((string hash, string reason) in set) reasons.Add((kind, hash, reason));
            if (_currentRun is { } run && _currentJob is { } job)
                foreach ((string hash, string reason) in run.SkippedReasons)
                    if (!reasons.Any(r => r.Job == job && r.Hash == hash)) reasons.Add((job, hash, reason));
        }
        return Task.Run<IReadOnlyList<SkippedItem>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            Dictionary<string, string?> paths = reasons.Count == 0 ? [] : _db.ReadSnapshot(conn => FirstPaths(conn, reasons.Select(r => r.Hash).Distinct().ToArray()));
            return reasons
                .Select(r => new SkippedItem(r.Job, r.Hash, paths.GetValueOrDefault(r.Hash), r.Reason))
                .OrderBy(i => i.Job)
                .ThenBy(i => i.Path ?? "￿", StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Hash, StringComparer.Ordinal)
                .ToList();
        }, ct);
    }

    private static Dictionary<string, string?> FirstPaths(SqliteConnection conn, string[] hashes)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path FROM image_paths WHERE hash = $h AND missing_since IS NULL ORDER BY path COLLATE NOCASE, path LIMIT 1";
        SqliteParameter hashParam = cmd.Parameters.Add("$h", SqliteType.Text);
        foreach (string hash in hashes)
        {
            hashParam.Value = hash;
            map[hash] = cmd.ExecuteScalar() as string;
        }
        return map;
    }

    private static IDisposable StartRealSleepGuard() => SleepGuard.Start();

    private IDisposable? StartSleepGuard()
    {
        try
        {
            return _startSleepGuard();
        }
        catch (Exception e)
        {
            // スリープを抑止できなくても、ジョブは動かす(長時間の実行では、スリープで止まるかもしれない)
            _log.Warning("スリープの抑止を始められなかった(ジョブは続ける): " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// ランナーが画像の境目で呼ぶ「切り替えてほしいか」。合図(<see cref="RequestYield"/>)が立っていれば true。立っていなくても、いまのジョブより前の順のジョブがあるとき
    /// (=最優先のジョブには要らない)で、前回の数え直しから <see cref="RecountInterval"/> 以上たっていれば、前の順のジョブの「新しく入った待ち」を数え直し、
    /// 切り替える条件を満たしたら合図を立てて true(ADR 0027)。タイマーは使わない。
    /// </summary>
    private bool ShouldYield()
    {
        if (Volatile.Read(ref _yieldRequested) == 1) return true;
        lock (_stateLock)
        {
            if (_run != IndexRunState.Running || _currentJob is not { } current || _jobs.IndexOf(current) <= 0) return false;
        }
        long now = _time.GetUtcNow().UtcTicks;
        long last = Interlocked.Read(ref _lastRecountTicks);
        if (now - last < RecountInterval.Ticks) return false;
        if (Interlocked.CompareExchange(ref _lastRecountTicks, now, last) != last) return false;   // 同時に呼ばれた別のスレッドが数える
        if (!ShouldSwitchNow()) return false;
        RequestYield();
        return true;
    }

    private RunRecorder NewRecorder(JobKind kind) => new(
        _db, kind, _appVersion, _time,
        onEngine: status => { lock (_stateLock) _engine = status; _throttle.Request(); },
        onCurrent: path => { lock (_stateLock) _currentPath = path; _throttle.Request(); },
        onSkipped: _ => _throttle.Request(),
        onCompleted: () =>
        {
            lock (_stateLock)
            {
                _completions.Enqueue(_time.GetTimestamp());
                while (_completions.Count > RateWindow) _completions.Dequeue();
            }
            _throttle.Request();
        });

    // ------------------------------------------------------------------ 状態

    private JobRunner RunnerOf(JobKind job) =>
        _runners.FirstOrDefault(r => r.Kind == job) ?? throw new ArgumentException($"ジョブ {job} のランナーが無い", nameof(job));

    private void Publish()
    {
        Action<IndexState>? handler = StateChanged;
        if (handler is null) return;
        try { handler(BuildState()); }
        catch (Exception e) { _log.Error("StateChanged の購読者が例外を投げた: " + e.Message, e); }
    }

    private IndexState BuildState()
    {
        IndexRunState run;
        JobKind? job;
        EngineStatus engine;
        ScanProgress? scan;
        string? path, error;
        int skipped;
        long[] completions;
        lock (_stateLock)
        {
            run = _run; job = _currentJob; engine = _engine; scan = _scan; path = _currentPath; error = _lastError;
            completions = _completions.ToArray();
            // 飛ばした枚数 = ランナーごとの飛ばした集合と、いまの実行の飛ばした画像の和集合の大きさ(同じ画像を数え直さない)
            var all = new HashSet<string>(StringComparer.Ordinal);
            foreach (Dictionary<string, string> set in _skippedSets.Values) all.UnionWith(set.Keys);
            if (_currentRun is { } currentRun) all.UnionWith(currentRun.SkippedHashes);
            skipped = all.Count;
        }

        List<StageSummary> stages = _runners.SelectMany(r => r.Summarize()).ToList();

        double? rate = null;
        TimeSpan? remaining = null;
        if (completions.Length >= 2)
        {
            double seconds = _time.GetElapsedTime(completions[0], completions[^1]).TotalSeconds;
            if (seconds > 0) rate = (completions.Length - 1) / seconds;
        }
        if (run == IndexRunState.Running && rate is > 0 && job is { } kind)
        {
            // 速さは、いまのジョブの完了から測っている。残りも、いまのジョブの待ちだけで見積もる(段階が複数のジョブ=VLM は、段階ごとの同じ待ちを 2 回数えない)
            JobRunner? runner = _runners.FirstOrDefault(r => r.Kind == kind);
            if (runner is not null)
            {
                int pending = stages.Where(s => runner.Stages.Contains(s.Stage)).Select(s => s.Pending).DefaultIfEmpty(0).Max();
                remaining = TimeSpan.FromSeconds(pending / rate.Value);
            }
        }

        var unavailable = new List<UnavailableJob>();
        foreach (JobRunner r in _runners)
        {
            IReadOnlyList<string> missing = r.UnavailableFiles();
            if (missing.Count > 0) unavailable.Add(new UnavailableJob(r.Kind, missing));
        }

        return new IndexState(run, job, stages, engine, scan, path, rate, remaining, error, skipped) { UnavailableJobs = unavailable };
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
