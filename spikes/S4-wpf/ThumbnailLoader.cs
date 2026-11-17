using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace S4Wpf;

/// <summary>デコード結果1件(UI スレッドの LRU に入る)。</summary>
public sealed class LoadedThumb
{
    public int Index;
    public BitmapSource? Bitmap;       // 失敗時は null
    public string Source = "";         // original / disk / failed
    public double DecodeMs;
    public string? Error;
}

public sealed class LoaderStats
{
    public long CompletedOriginal, CompletedDisk, Failed, DiskWritten;
    public double[] DecodeMsOriginal = Array.Empty<double>();
    public double[] DecodeMsDisk = Array.Empty<double>();
    public double[] DiskWriteMs = Array.Empty<double>();
    public List<string> FirstErrors = new();
}

/// <summary>
/// サムネイルを UI スレッドの外でデコードし、メモリ上の LRU に保持する。
/// - UI スレッドが「欲しい項目の順序つきリスト」(SetWanted)を渡す。ワーカーは先頭から、未着手のものを取る。
/// - 結果はキューに積まれ、UI スレッドが Drain() で LRU に取り込む(UI スレッド以外は LRU に触らない)。
/// - 上限(capacity)を超えたら、画面に見えている範囲(SetProtected)以外の最も古いものから捨てる。
/// </summary>
public sealed class ThumbnailLoader : IDisposable
{
    private readonly IReadOnlyList<ThumbItem> _items;
    private readonly int _boxW, _boxH;           // デバイスピクセル
    private readonly string? _diskDir;
    private readonly int _capacity;

    private readonly object _gate = new();
    private int[] _wanted = Array.Empty<int>();
    private readonly HashSet<int> _known = new();   // 着手済み(実行中 + 結果待ち + LRU 内)
    private readonly ConcurrentQueue<LoadedThumb> _done = new();

    // UI スレッドだけが触る
    private readonly Dictionary<int, LinkedListNode<LoadedThumb>> _lru = new();
    private readonly LinkedList<LoadedThumb> _order = new();
    private int _protFirst = -1, _protLast = -1;

    private readonly Thread[] _workers;
    private readonly ManualResetEventSlim _wake = new(false);
    private volatile bool _stop;

    // 統計
    private readonly object _statLock = new();
    private readonly List<double> _msOriginal = new(), _msDisk = new(), _msWrite = new();
    private readonly List<string> _errors = new();
    private long _completedOriginal, _completedDisk, _failed, _diskWritten;
    private long _completedTotal;

    /// <summary>true なら Drain した結果を LRU に入れず捨てる(ディスクキャッシュを埋めるだけの prime 用)。</summary>
    public bool DiscardResults;

    public ThumbnailLoader(IReadOnlyList<ThumbItem> items, int boxWidthPx, int boxHeightPx, string? diskCacheDir, int capacity, int workers)
    {
        _items = items;
        _boxW = boxWidthPx;
        _boxH = boxHeightPx;
        _diskDir = diskCacheDir;
        _capacity = capacity;
        if (_diskDir != null) Directory.CreateDirectory(_diskDir);
        _workers = new Thread[workers];
        for (int i = 0; i < workers; i++)
        {
            _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"thumb-worker-{i}", Priority = ThreadPriority.BelowNormal };
            _workers[i].Start();
        }
    }

    public int Count => _lru.Count;
    public long CompletedTotal => Interlocked.Read(ref _completedTotal);
    public int InFlightOrQueued { get { lock (_gate) return _known.Count - _lru.Count; } }

    /// <summary>欲しい項目を優先順に並べて渡す。差し替えで、古い要求は取り下げられる(実行中のデコードは止められない)。</summary>
    public void SetWanted(int[] wanted)
    {
        lock (_gate) _wanted = wanted;
        _wake.Set();
    }

    public void SetProtected(int first, int last)
    {
        _protFirst = first;
        _protLast = last;
    }

    /// <summary>UI スレッド: 結果を LRU に取り込む。取り込んだ項目の番号を返す。</summary>
    public List<int> Drain()
    {
        var added = new List<int>();
        while (_done.TryDequeue(out LoadedThumb? r))
        {
            if (DiscardResults) continue;
            if (_lru.TryGetValue(r.Index, out var old)) { _order.Remove(old); _lru.Remove(r.Index); }
            _lru[r.Index] = _order.AddFirst(r);
            added.Add(r.Index);
        }
        Evict();
        return added;
    }

    private void Evict()
    {
        int guard = _lru.Count;
        while (_lru.Count > _capacity && guard-- > 0)
        {
            LinkedListNode<LoadedThumb>? tail = _order.Last;
            if (tail == null) break;
            int idx = tail.Value.Index;
            if (idx >= _protFirst && idx <= _protLast)
            {
                // 画面内のものは捨てない(先頭へ回す)
                _order.RemoveLast();
                _lru[idx] = _order.AddFirst(tail.Value);
                continue;
            }
            _order.RemoveLast();
            _lru.Remove(idx);
            lock (_gate) _known.Remove(idx);
        }
    }

    /// <summary>UI スレッド: LRU から取る(取ったら最近使ったものとして先頭へ)。</summary>
    public bool TryGet(int index, out LoadedThumb? thumb)
    {
        if (_lru.TryGetValue(index, out var node))
        {
            if (node != _order.First)
            {
                _order.Remove(node);
                _lru[index] = _order.AddFirst(node.Value);
            }
            thumb = node.Value;
            return true;
        }
        thumb = null;
        return false;
    }

    public bool Contains(int index) => _lru.ContainsKey(index);

    public void ClearMemory()
    {
        _lru.Clear();
        _order.Clear();
        lock (_gate) { _known.Clear(); _wanted = Array.Empty<int>(); }
        while (_done.TryDequeue(out _)) { }
    }

    private void WorkerLoop()
    {
        while (!_stop)
        {
            int idx = NextWanted();
            if (idx < 0)
            {
                _wake.Wait(20);
                _wake.Reset();
                continue;
            }
            LoadedThumb r = Decode(_items[idx]);
            _done.Enqueue(r);
            Interlocked.Increment(ref _completedTotal);
        }
    }

    private int NextWanted()
    {
        lock (_gate)
        {
            int[] w = _wanted;
            for (int i = 0; i < w.Length; i++)
            {
                if (_known.Add(w[i])) return w[i];
            }
        }
        return -1;
    }

    private string CacheFile(ThumbItem it)
    {
        string key = it.CacheKey + "|" + _boxW.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x" + _boxH.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_diskDir!, hash + ".jpg");
    }

    private LoadedThumb Decode(ThumbItem it)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (_diskDir != null)
            {
                string cf = CacheFile(it);
                if (File.Exists(cf))
                {
                    BitmapSource b = DecodeFile(cf, null);
                    double ms = sw.Elapsed.TotalMilliseconds;
                    lock (_statLock) { _msDisk.Add(ms); _completedDisk++; }
                    return new LoadedThumb { Index = it.Index, Bitmap = b, Source = "disk", DecodeMs = ms };
                }
            }

            // 画像の大きさだけ先に読み(ヘッダのみ)、箱に収まる縮小幅を決めてからデコードする
            int pw, ph;
            using (var fs = new FileStream(it.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                BitmapDecoder dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                BitmapFrame f0 = dec.Frames[0];
                pw = f0.PixelWidth;
                ph = f0.PixelHeight;
            }
            double scale = Math.Min(Math.Min((double)_boxW / pw, (double)_boxH / ph), 1.0);
            int dw = Math.Max(1, (int)Math.Round(pw * scale));
            BitmapSource bmp = DecodeFile(it.Path, scale < 1.0 ? dw : null);
            double msOrig = sw.Elapsed.TotalMilliseconds;
            lock (_statLock) { _msOriginal.Add(msOrig); _completedOriginal++; }

            if (_diskDir != null) WriteDisk(it, bmp);
            return new LoadedThumb { Index = it.Index, Bitmap = bmp, Source = "original", DecodeMs = msOrig };
        }
        catch (Exception e)
        {
            lock (_statLock)
            {
                _failed++;
                if (_errors.Count < 20) _errors.Add($"{it.Path}: {e.GetType().Name}: {e.Message}");
            }
            return new LoadedThumb { Index = it.Index, Bitmap = null, Source = "failed", DecodeMs = sw.Elapsed.TotalMilliseconds, Error = e.Message };
        }
    }

    private static BitmapSource DecodeFile(string path, int? decodePixelWidth)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodePixelWidth is int w) bi.DecodePixelWidth = w;
        bi.StreamSource = fs;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private void WriteDisk(ThumbItem it, BitmapSource bmp)
    {
        var sw = Stopwatch.StartNew();
        string dest = CacheFile(it);
        string tmp = dest + "." + Environment.CurrentManagedThreadId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0);
        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(conv));
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            enc.Save(fs);
        }
        File.Move(tmp, dest, true);
        lock (_statLock) { _msWrite.Add(sw.Elapsed.TotalMilliseconds); _diskWritten++; }
    }

    public LoaderStats Snapshot()
    {
        lock (_statLock)
        {
            return new LoaderStats
            {
                CompletedOriginal = _completedOriginal,
                CompletedDisk = _completedDisk,
                Failed = _failed,
                DiskWritten = _diskWritten,
                DecodeMsOriginal = _msOriginal.ToArray(),
                DecodeMsDisk = _msDisk.ToArray(),
                DiskWriteMs = _msWrite.ToArray(),
                FirstErrors = new List<string>(_errors),
            };
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        foreach (Thread t in _workers) t.Join(2000);
    }
}
