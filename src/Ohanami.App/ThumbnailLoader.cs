using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>サムネイルの作り手(本物は <c>OhanamiCore.Thumbnails.GetAsync</c>。テストでは差し替える)。</summary>
internal delegate Task<ThumbnailResult> ThumbnailFetch(string hash, int boxWidthPx, int boxHeightPx, CancellationToken ct);

/// <summary>デコード結果 1 件(UI スレッドの LRU に入る)。<see cref="Bitmap"/> は <see cref="Status"/> が Ok のときだけ。</summary>
internal sealed class LoadedThumb
{
    public required string Hash { get; init; }
    public required ThumbnailStatus Status { get; init; }
    public BitmapSource? Bitmap { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// サムネイルを UI スレッドの外で作り(Core の <c>ThumbnailService</c>)、メモリ上の LRU に保持する。ADR 0041。
/// 鍵は項目の番号でなく<b>内容ハッシュ</b>(検索し直して並びが変わっても、同じ画像のサムネイルを使い回す)。箱(デバイスピクセル)はローダーごとに固定で、
/// DPI が変わったら作り直す(LRU の鍵 = (ハッシュ, 箱) をローダーの単位で持つのと同じ)。デコードは Core(SkiaSharp とディスクキャッシュ)で、WPF の画素は
/// Core の BGRA の配列から <c>BitmapSource.Create</c> で作る(BitmapImage のデコーダを通さない: PNG のデコードが gen2 GC を頻発させるため。ADR 0041)。
/// <list type="bullet">
/// <item>UI スレッドが「欲しいハッシュの優先順のリスト」(<see cref="SetWanted"/>)を渡す。ワーカーは先頭から、未着手のものを取る。</item>
/// <item>結果はキューに積まれ、UI スレッドが <see cref="Drain"/> で LRU に取り込む(LRU に触るのは UI スレッドだけ)。</item>
/// <item>上限(capacity)を超えたら、画面に見えているもの(<see cref="SetProtected"/>)以外の最も古いものから捨てる。</item>
/// </list>
/// </summary>
internal sealed class ThumbnailLoader : IDisposable
{
    private readonly ThumbnailFetch _fetch;
    private readonly ICoreLog _log;
    private readonly int _boxW, _boxH;           // デバイスピクセル
    private readonly int _capacity;

    private readonly object _gate = new();
    private string[] _wanted = [];
    private readonly HashSet<string> _known = [];   // 着手済み(実行中 + 結果待ち + LRU 内)
    private readonly ConcurrentQueue<LoadedThumb> _done = new();

    // UI スレッドだけが触る
    private readonly Dictionary<string, LinkedListNode<LoadedThumb>> _lru = [];
    private readonly LinkedList<LoadedThumb> _order = new();
    private HashSet<string> _protected = [];

    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _workers;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);   // 欲しいものが渡されたことをワーカーに知らせる(待ちが無いあいだは眠る)
    private int _resultSignalled;
    private long _completedTotal;
    private int _disposed;

    public ThumbnailLoader(ThumbnailFetch fetch, ICoreLog log, int boxWidthPx, int boxHeightPx, int capacity, int workers)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentOutOfRangeException.ThrowIfLessThan(boxWidthPx, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(boxHeightPx, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        _fetch = fetch;
        _log = log;
        _boxW = boxWidthPx;
        _boxH = boxHeightPx;
        _capacity = capacity;
        _workers = new Task[workers];
        for (int i = 0; i < workers; i++)
        {
            CancellationToken ct = _stop.Token;
            _workers[i] = Task.Run(() => WorkerLoopAsync(ct), CancellationToken.None);
        }
    }

    /// <summary>
    /// 結果が 1 つ以上キューに積まれたとき(ワーカーのスレッドから、<see cref="Drain"/> までに 1 回だけ)。MainWindow は UI スレッドに渡して、毎フレームの購読をつなぎ直す
    /// (放置のあいだは購読を外しているため、結果の到着で起こす)。
    /// </summary>
    public Action? ResultReady { get; set; }

    public int BoxWidthPx => _boxW;
    public int BoxHeightPx => _boxH;

    /// <summary>メモリ上(LRU)の数。UI スレッドから。</summary>
    public int Count => _lru.Count;

    public long CompletedTotal => Interlocked.Read(ref _completedTotal);

    /// <summary>着手済みで、まだ LRU に入っていないもの(実行中 + 結果待ち)の数。</summary>
    public int InFlightOrQueued
    {
        get { lock (_gate) return _known.Count - _lru.Count; }
    }

    /// <summary>欲しいハッシュを優先順に並べて渡す。差し替えで、古い要求は取り下げられる(実行中のものは止められない)。</summary>
    public void SetWanted(string[] wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        lock (_gate) _wanted = wanted;
        int missing = _workers.Length - _wake.CurrentCount;   // 眠っているワーカーを起こす(積みすぎない)
        if (missing > 0) _wake.Release(missing);
    }

    /// <summary>UI スレッド: 画面に見えているハッシュ。LRU の上限を超えても、これらは捨てない。</summary>
    public void SetProtected(HashSet<string> visible) => _protected = visible;

    /// <summary>UI スレッド: 結果を LRU に取り込む。画面に見えているもの(<see cref="SetProtected"/>)が 1 つでも入ったら true(再描画が要る)。</summary>
    public bool Drain()
    {
        bool visibleChanged = false;
        Volatile.Write(ref _resultSignalled, 0);   // これ以降に積まれた結果は、また知らせる
        while (_done.TryDequeue(out LoadedThumb? r))
        {
            if (_lru.TryGetValue(r.Hash, out var old)) { _order.Remove(old); _lru.Remove(r.Hash); }
            _lru[r.Hash] = _order.AddFirst(r);
            if (_protected.Contains(r.Hash)) visibleChanged = true;
        }
        Evict();
        return visibleChanged;
    }

    private void Evict()
    {
        int guard = _lru.Count;
        while (_lru.Count > _capacity && guard-- > 0)
        {
            LinkedListNode<LoadedThumb>? tail = _order.Last;
            if (tail is null) break;
            string hash = tail.Value.Hash;
            _order.RemoveLast();
            if (_protected.Contains(hash))
            {
                _lru[hash] = _order.AddFirst(tail.Value);   // 画面内のものは捨てない(先頭へ回す)
                continue;
            }
            _lru.Remove(hash);
            lock (_gate) _known.Remove(hash);
        }
    }

    /// <summary>UI スレッド: LRU から取る(取ったら最近使ったものとして先頭へ)。</summary>
    public bool TryGet(string hash, out LoadedThumb? thumb)
    {
        if (_lru.TryGetValue(hash, out var node))
        {
            if (node != _order.First)
            {
                _order.Remove(node);
                _lru[hash] = _order.AddFirst(node.Value);
            }
            thumb = node.Value;
            return true;
        }
        thumb = null;
        return false;
    }

    public bool Contains(string hash) => _lru.ContainsKey(hash);

    /// <summary>
    /// UI スレッド: 「原本なし」で終わったセルを捨てて、もう一度作れるようにする(新しい結果を出すとき。そのあいだに原本が戻る・走査で見つかるかもしれない)。
    /// デコード不能(壊れたファイル)は捨てない(同じ結果になる)。
    /// </summary>
    public void ForgetUnavailable()
    {
        List<string>? gone = null;
        foreach (LoadedThumb t in _order)
        {
            if (t.Status == ThumbnailStatus.SourceUnavailable) (gone ??= []).Add(t.Hash);
        }
        if (gone is null) return;
        lock (_gate)
        {
            foreach (string hash in gone)
            {
                _order.Remove(_lru[hash]);
                _lru.Remove(hash);
                _known.Remove(hash);
            }
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? hash = NextWanted();
            if (hash is null)
            {
                try { await _wake.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            LoadedThumb thumb;
            try
            {
                ThumbnailResult result = await _fetch(hash, _boxW, _boxH, ct).ConfigureAwait(false);
                thumb = ToThumb(hash, result);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // Core は想定内の失敗を結果で返す。ここへ来るのは想定外(DB の例外など): ログに残し、そのセルは失敗の表示にする(一覧は止めない)
                _log.Error("サムネイルの作成で想定外の例外: " + hash, e);
                thumb = new LoadedThumb { Hash = hash, Status = ThumbnailStatus.SourceUndecodable, Message = e.GetType().Name + ": " + e.Message };
            }
            _done.Enqueue(thumb);
            Interlocked.Increment(ref _completedTotal);
            if (Interlocked.Exchange(ref _resultSignalled, 1) == 0) ResultReady?.Invoke();
        }
    }

    private string? NextWanted()
    {
        lock (_gate)
        {
            string[] w = _wanted;
            for (int i = 0; i < w.Length; i++)
            {
                if (_known.Add(w[i])) return w[i];
            }
        }
        return null;
    }

    /// <summary>Core の画素(BGRA、アルファは常に 255)から、固定した BitmapSource を作る。任意のスレッドで呼んでよい。</summary>
    internal static LoadedThumb ToThumb(string hash, ThumbnailResult result)
    {
        if (result.Status != ThumbnailStatus.Ok)
            return new LoadedThumb { Hash = hash, Status = result.Status, Message = result.Message };
        ThumbnailPixels p = result.Pixels ?? throw new InvalidOperationException("Ok なのに画素が無い: " + hash);
        BitmapSource bmp = BitmapSource.Create(p.Width, p.Height, 96, 96, PixelFormats.Bgr32, null, p.Bgra, p.Stride);
        bmp.Freeze();
        return new LoadedThumb { Hash = hash, Status = ThumbnailStatus.Ok, Bitmap = bmp };
    }

    /// <summary>ワーカーに止まるよう伝える(待たない。実行中のデコードは終わってから捨てられる)。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        ResultReady = null;
    }
}
