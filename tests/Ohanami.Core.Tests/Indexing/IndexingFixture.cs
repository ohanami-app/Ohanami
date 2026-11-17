using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Embedding;
using Ohanami.Core.Indexing;
using Ohanami.Core.Models;
using Ohanami.Core.Tests.Scanning;

namespace Ohanami.Core.Tests.Indexing;

/// <summary>テストの診断ログ(メッセージを集める)。</summary>
internal sealed class TestLog : ICoreLog
{
    private readonly object _gate = new();
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages { get { lock (_gate) return _messages.ToArray(); } }

    public void Info(string message) { lock (_gate) _messages.Add("INFO " + message); }

    public void Warning(string message) { lock (_gate) _messages.Add("WARN " + message); }

    public void Error(string message, Exception? exception = null) { lock (_gate) _messages.Add("ERROR " + message); }
}

/// <summary>前処理の偽物: バイト列から決まる値のテンソルを返す。"BAD" を含むバイト列はデコードできない画像として例外にする。</summary>
internal sealed class FakePreprocessor : IImagePreprocessor
{
    public const int TensorLength = 3 * 384 * 384;
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public PreprocessedImage Preprocess(byte[] encoded)
    {
        Interlocked.Increment(ref _calls);
        if (encoded.AsSpan().IndexOf("BAD"u8) >= 0)
            throw new InvalidDataException("デコードできない画像(テスト)");
        var tensor = new float[TensorLength];
        tensor[0] = Checksum(encoded);
        return new PreprocessedImage(100 + encoded.Length % 50, 60 + encoded.Length % 30, tensor);
    }

    /// <summary>バイト列から決まる 0〜1151 の値。偽のエンコーダはこれを「1 になる要素の位置」にする(one-hot)。</summary>
    public static int Checksum(byte[] bytes)
    {
        int sum = 0;
        foreach (byte b in bytes) sum = (sum * 31 + b) % 1152;
        return sum;
    }
}

/// <summary>偽のエンコーダ: テンソルの先頭の値の位置が 1 の one-hot ベクトル(1152 次元)を返す。</summary>
internal sealed class FakeEmbedder : IImageEmbedder
{
    private readonly FakeEmbedderFactory _owner;

    public FakeEmbedder(FakeEmbedderFactory owner, SelfCheckReport? report)
    {
        _owner = owner;
        SelfCheck = report;
    }

    public SelfCheckReport? SelfCheck { get; }

    public IReadOnlyDictionary<string, string>? ExecutionSettings => _owner.Settings;

    /// <summary>テストが、作り直したことにする(本物は <c>Siglip2ImageEncoder</c> が数える)。</summary>
    public int RecreateCount { get; set; }

    public string? LastRecreateReason { get; set; }

    public bool Disposed { get; private set; }

    public float[] Embed(float[] chwTensor)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        int index = Interlocked.Increment(ref _owner.EmbedCountField);
        _owner.OnEmbed?.Invoke(index);
        if (_owner.EmbedBehavior is not null) return _owner.EmbedBehavior(index, chwTensor);
        var v = new float[1152];
        v[(int)chwTensor[0]] = 1f;
        return v;
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        Interlocked.Increment(ref _owner.DisposeCountField);
    }
}

/// <summary>偽の工場。作成・破棄・Embed の回数を数え、失敗や待ちを差し込める。</summary>
internal sealed class FakeEmbedderFactory : IImageEmbedderFactory
{
    internal int CreateCountField;
    internal int DisposeCountField;
    internal int EmbedCountField;

    public EmbeddingModelSpec Model { get; } = ModelCatalog.Current;

    public string ModelName => ModelCatalog.Name;

    public int CreateCount => Volatile.Read(ref CreateCountField);

    public int DisposeCount => Volatile.Read(ref DisposeCountField);

    public int EmbedCount => Volatile.Read(ref EmbedCountField);

    /// <summary>非 null なら、作成時に投げる(自己検査の失敗など)。</summary>
    public Exception? CreateFailure { get; set; }

    /// <summary>作成の前に待つ・遅らせる。</summary>
    public Func<CancellationToken, Task>? BeforeCreate { get; set; }

    /// <summary>Embed が呼ばれるたびに(1 始まりの通し番号で)呼ばれる。待ち・例外の差し込み用。</summary>
    public Action<int>? OnEmbed { get; set; }

    /// <summary>非 null なら、Embed の戻り値を決める。</summary>
    public Func<int, float[], float[]>? EmbedBehavior { get; set; }

    /// <summary>エンコーダが報告する実行の設定(runs.runtime_json の settings に入る)。</summary>
    public IReadOnlyDictionary<string, string>? Settings { get; set; }

    public SelfCheckReport? Report { get; set; } = new(
        true, "1.31.1-fake",
        [new LoadedModule("onnxruntime.dll", @"C:\app\onnxruntime.dll", "1.31.1"), new LoadedModule("cudnn64_9.dll", @"C:\app\cudnn64_9.dll", "9.28")],
        []);

    public FakeEmbedder? Last { get; private set; }

    public async Task<IImageEmbedder> CreateAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref CreateCountField);
        if (BeforeCreate is not null) await BeforeCreate(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (CreateFailure is not null) throw CreateFailure;
        var embedder = new FakeEmbedder(this, Report);
        Last = embedder;
        return embedder;
    }
}

/// <summary>Indexer のテスト一式: 走査用のフィクスチャ(DB・フォルダ・スキャナ)に、偽の工場と EmbedRunner を載せる。</summary>
internal sealed class IndexingFixture : IDisposable
{
    public ScanFixture Scan { get; } = new();
    public FakeEmbedderFactory Factory { get; } = new();
    public FakePreprocessor Preprocessor { get; } = new();
    public TestLog Log { get; } = new();
    public EmbedRunner Runner { get; }
    public Indexer Indexer { get; }
    public List<(string Hash, float[] Vector)> Embedded { get; } = [];

    public IndexingFixture()
    {
        Runner = new EmbedRunner(Scan.Db, Factory, Preprocessor, Scan.Time, Log, (hash, vector) => { lock (Embedded) Embedded.Add((hash, vector)); });
        Indexer = new Indexer(Scan.Db, Scan.Scanner, [Runner], Scan.Time, "test-1.0", Log);
    }

    public void Dispose()
    {
        Indexer.StopAsync().GetAwaiter().GetResult();
        Scan.Dispose();
    }

    /// <summary>登録したフォルダに n 枚(中身が全部違うもの)を置いて、走査して DB に入れる。作ったファイルのパスを返す。</summary>
    public async Task<List<string>> AddImagesAsync(int count, string prefix = "img")
    {
        var paths = new List<string>();
        for (int i = 0; i < count; i++) paths.Add(Scan.Write($"{prefix}{i}.png", ScanFixture.FakePng($"{prefix}-{i}")));
        if (Scan.Folders.List().Count == 0) await Scan.RegisterRootAsync();
        await Scan.ScanAsync();
        return paths;
    }

    public StageSummary Summary() => Indexer.State.Stages.Single();

    public long Count(string sql) => Scan.Long(sql);

    public static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(10, TestContextToken);
        }
        return condition();
    }

    private static CancellationToken TestContextToken => Xunit.TestContext.Current.CancellationToken;
}
