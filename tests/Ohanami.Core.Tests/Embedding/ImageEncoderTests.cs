using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ohanami.Core.Embedding;
using Ohanami.Core.Models;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// 画像エンコーダのセッションの扱い(作り直し・再試行・Dispose)。偽のセッションで確かめる(GPU もモデルのファイルも要らない)。
/// </summary>
public sealed class ImageEncoderTests
{
    private static readonly int TensorLength = 3 * Siglip2ImagePreprocessor.Size * Siglip2ImagePreprocessor.Size;

    /// <summary>決まった手順で動く偽のセッション。scripts の i 番目の呼び出しが null なら成功、例外なら投げる。</summary>
    private sealed class FakeSession(int id, Func<int, float[]> run) : IImageSession
    {
        public int Id { get; } = id;
        public int Runs { get; private set; }
        public bool Disposed { get; private set; }

        public float[] Run(float[] chwTensor)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            return run(Runs++);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeFactory : IImageSessionFactory
    {
        private readonly Func<int, FakeSession> _make;
        public List<FakeSession> Created { get; } = [];
        public bool FailOnCreate { get; set; }

        public FakeFactory(Func<int, FakeSession> make) => _make = make;

        public ImageSessionCreation Create()
        {
            if (FailOnCreate && Created.Count > 0) throw new InvalidOperationException("作り直しに失敗");
            FakeSession s = _make(Created.Count);
            Created.Add(s);
            return new ImageSessionCreation(s, new SelfCheckReport(true, "fake-" + s.Id, [], []));
        }
    }

    private static float[] Vec(float scale = 2f) => Enumerable.Range(0, ModelCatalog.EmbeddingDim).Select(i => (i % 7 + 1) * scale).ToArray();

    private static float[] Tensor() => new float[TensorLength];

    private static ImageRunException RunFailure(string why = "CUDA error") => new(why, new InvalidOperationException(why));

    [Fact]
    public void Embed_returns_the_normalized_output_of_the_session()
    {
        var factory = new FakeFactory(id => new FakeSession(id, _ => Vec()));
        using var encoder = new Siglip2ImageEncoder(factory);
        float[] e = encoder.Embed(Tensor());
        Assert.Equal(ModelCatalog.EmbeddingDim, e.Length);
        Assert.Equal(1.0, Math.Sqrt(e.Sum(x => (double)x * x)), 5);
        Assert.Equal(Embeddings.L2Normalize(Vec()), e);       // C# 側で必ず正規化する(スケールに依らない)
        Assert.Equal(Embeddings.L2Normalize(Vec(5f)), e, new ApproxComparer());
        Assert.Single(factory.Created);
        Assert.Equal(0, encoder.RecreateCount);
        Assert.Equal("fake-0", encoder.SelfCheck!.OrtVersion);
    }

    private sealed class ApproxComparer : IEqualityComparer<float[]>
    {
        public bool Equals(float[]? x, float[]? y) => x is not null && y is not null && x.Length == y.Length && x.Zip(y).All(p => Math.Abs(p.First - p.Second) < 1e-6f);
        public int GetHashCode(float[] obj) => obj.Length;
    }

    [Fact]
    public void A_failed_run_disposes_the_session_recreates_it_and_retries_the_same_image_once()
    {
        // 1 つ目のセッションは 2 回目の Run で失敗し、作り直したセッションは成功する
        var factory = new FakeFactory(id => new FakeSession(id, n => id == 0 && n == 1 ? throw RunFailure() : Vec()));
        using var encoder = new Siglip2ImageEncoder(factory);

        encoder.Embed(Tensor());                         // セッション 0 の 1 回目: 成功
        float[] e = encoder.Embed(Tensor());             // セッション 0 の 2 回目: 失敗 → 作り直して再試行 → 成功
        Assert.Equal(Embeddings.L2Normalize(Vec()), e);

        Assert.Equal(2, factory.Created.Count);
        Assert.True(factory.Created[0].Disposed);        // 失敗したセッションは捨てた
        Assert.False(factory.Created[1].Disposed);
        Assert.Equal(1, factory.Created[1].Runs);        // 再試行は新しいセッションで 1 回
        Assert.Equal(1, encoder.RecreateCount);
        Assert.Equal("fake-1", encoder.SelfCheck!.OrtVersion); // 作り直しのたびに自己検査の結果も更新される

        encoder.Embed(Tensor());                         // 以後は新しいセッションで普通に動く
        Assert.Equal(2, factory.Created.Count);
    }

    [Fact]
    public void If_the_retry_also_fails_the_failure_is_reported_and_there_is_no_second_retry()
    {
        var factory = new FakeFactory(id => new FakeSession(id, _ => throw RunFailure("always " + id)));
        using var encoder = new Siglip2ImageEncoder(factory);

        var e = Assert.Throws<ImageRunException>(() => encoder.Embed(Tensor()));
        Assert.Contains("always 0", e.Message, StringComparison.Ordinal);   // 1 回目の理由
        Assert.Contains("always 1", e.Message, StringComparison.Ordinal);   // 2 回目の理由
        Assert.Equal(2, factory.Created.Count);                              // 作り直しは 1 回だけ
        Assert.Equal(1, factory.Created[0].Runs);
        Assert.Equal(1, factory.Created[1].Runs);
        Assert.True(factory.Created[0].Disposed);
        Assert.Equal(1, encoder.RecreateCount);
    }

    [Fact]
    public void The_next_image_gets_its_own_retry_after_a_failed_one()
    {
        // 画像 A は 2 回とも失敗、画像 B は成功(次の画像には新しい 1 回の再試行が付く)
        int call = 0;
        var factory = new FakeFactory(id => new FakeSession(id, _ => call++ < 2 ? throw RunFailure() : Vec()));
        using var encoder = new Siglip2ImageEncoder(factory);
        Assert.Throws<ImageRunException>(() => encoder.Embed(Tensor()));
        float[] b = encoder.Embed(Tensor());
        Assert.Equal(Embeddings.L2Normalize(Vec()), b);
        Assert.Equal(2, factory.Created.Count);
    }

    [Fact]
    public void If_recreating_the_session_fails_the_error_propagates_and_the_encoder_is_left_unusable()
    {
        var factory = new FakeFactory(id => new FakeSession(id, _ => throw RunFailure()));
        using var encoder = new Siglip2ImageEncoder(factory);
        factory.FailOnCreate = true;

        var e = Assert.Throws<InvalidOperationException>(() => encoder.Embed(Tensor()));
        Assert.Contains("作り直しに失敗", e.Message, StringComparison.Ordinal);
        Assert.True(factory.Created[0].Disposed);
        Assert.Throws<ObjectDisposedException>(() => encoder.Embed(Tensor()));   // セッションが無いまま残さない
    }

    [Fact]
    public void Errors_that_are_not_run_failures_are_not_retried()
    {
        // ノルム 0(= 不正な出力)は InvalidDataException。セッションを作り直しても直らないので再試行しない
        var factory = new FakeFactory(id => new FakeSession(id, _ => new float[ModelCatalog.EmbeddingDim]));
        using var encoder = new Siglip2ImageEncoder(factory);
        Assert.Throws<InvalidDataException>(() => encoder.Embed(Tensor()));
        Assert.Single(factory.Created);
        Assert.Equal(0, encoder.RecreateCount);
        Assert.Equal(1, factory.Created[0].Runs);
    }

    [Fact]
    public void A_tensor_of_the_wrong_length_is_rejected_before_touching_the_session()
    {
        var factory = new FakeFactory(id => new FakeSession(id, _ => Vec()));
        using var encoder = new Siglip2ImageEncoder(factory);
        Assert.Throws<ArgumentException>(() => encoder.Embed(new float[TensorLength - 1]));
        Assert.Throws<ArgumentNullException>(() => encoder.Embed(null!));
        Assert.Equal(0, factory.Created[0].Runs);
    }

    [Fact]
    public void Dispose_disposes_the_session_once_and_the_encoder_can_no_longer_be_used()
    {
        var factory = new FakeFactory(id => new FakeSession(id, _ => Vec()));
        var encoder = new Siglip2ImageEncoder(factory);
        encoder.Dispose();
        encoder.Dispose();                               // 2 回目は何もしない
        Assert.True(factory.Created[0].Disposed);
        Assert.Throws<ObjectDisposedException>(() => encoder.Embed(Tensor()));

        // Pause → Resume = Dispose して、新しいエンコーダを作る(セッションも作り直し)
        var again = new Siglip2ImageEncoder(factory);
        Assert.Equal(2, factory.Created.Count);
        Assert.False(factory.Created[1].Disposed);
        again.Dispose();
    }

    [Fact]
    public void If_creating_the_first_session_fails_no_encoder_is_made()
    {
        var factory = new FakeFactory(_ => throw new InvalidOperationException("作成に失敗"));
        Assert.Throws<InvalidOperationException>(() => new Siglip2ImageEncoder(factory));
    }

    [Fact]
    public void Concurrent_callers_are_serialized()
    {
        int active = 0, maxActive = 0;
        var factory = new FakeFactory(id => new FakeSession(id, _ =>
        {
            int now = System.Threading.Interlocked.Increment(ref active);
            int seen;
            while ((seen = System.Threading.Volatile.Read(ref maxActive)) < now && System.Threading.Interlocked.CompareExchange(ref maxActive, now, seen) != seen) { }
            System.Threading.Thread.Sleep(2);
            System.Threading.Interlocked.Decrement(ref active);
            return Vec();
        }));
        using var encoder = new Siglip2ImageEncoder(factory);
        System.Threading.Tasks.Parallel.For(0, 16, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => encoder.Embed(Tensor()));
        Assert.Equal(1, maxActive);
    }

    // ---------------------------------------------------------------- 作成の前提の検査(ファイル・Configure)

    private static EmbeddingModelSpec TinySpec(string dir)
    {
        // 中身が小さい偽のモデルのファイル一式と、それに合う spec(サイズだけが合えばよい。ここではハッシュは読まない)
        var files = new List<ModelFile>();
        foreach ((ModelRole role, string path) in new[]
        {
            (ModelRole.VisualOnnx, "visual/model.onnx"), (ModelRole.TextualOnnx, "textual/model.onnx"),
            (ModelRole.TextualExternalData, "textual/model.onnx_data"), (ModelRole.Tokenizer, "textual/tokenizer.json"),
        })
        {
            string full = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[16]);
            files.Add(new ModelFile(role, path, new string('a', 64), 16));
        }
        return new EmbeddingModelSpec("fam", files, "pre-v1", "clean-v1", 1152, "l2");
    }

    [Fact]
    public void Creating_the_gpu_encoder_without_the_model_files_throws_ModelMismatchException()
    {
        using var tmp = new Ohanami.Core.Tests.Data.TempDirectory();
        Assert.Throws<ModelMismatchException>(() => Siglip2ImageEncoder.Create(tmp.Path, ModelCatalog.Current, tmp.Path));
        Assert.Throws<ModelMismatchException>(() => Siglip2TextEncoder.Create(tmp.Path, ModelCatalog.Current));
    }

    [Fact]
    public void Creating_the_gpu_encoder_with_a_file_of_the_wrong_size_throws_before_loading_anything()
    {
        using var tmp = new Ohanami.Core.Tests.Data.TempDirectory();
        _ = TinySpec(tmp.Path);
        // 中身は 16 バイトだが、カタログは 1.7GB を期待する
        var e = Assert.Throws<ModelMismatchException>(() => Siglip2ImageEncoder.Create(tmp.Path, ModelCatalog.Current, tmp.Path));
        Assert.Contains("visual/model.onnx", e.Message, StringComparison.Ordinal);
        Assert.Contains("サイズ", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Creating_the_gpu_encoder_requires_NativeRuntime_Configure_for_the_same_directory()
    {
        using var tmp = new Ohanami.Core.Tests.Data.TempDirectory();
        EmbeddingModelSpec tiny = TinySpec(tmp.Path);
        using var native = new Ohanami.Core.Tests.Data.TempDirectory();
        // Configure が済んでいない(または別のディレクトリで済んでいる)ので、ORT に触る前に止まる
        var e = Assert.Throws<Ohanami.Core.NativeRuntimeException>(() => Siglip2ImageEncoder.Create(tmp.Path, tiny, native.Path));
        Assert.Contains("Configure", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Thread_counts_and_dimensions_are_the_designed_values()
    {
        // ADR 0011: ORT のスレッドは画像側(GPU)2、テキスト側(CPU)4。入力は 64 id と 3×384×384、出力は 1152 次元
        Assert.Equal(2, OrtSessions.ImageIntraOpThreads);
        Assert.Equal(4, OrtSessions.TextIntraOpThreads);
        Assert.Equal(64, Siglip2Tokenizer.ContextLength);
        Assert.Equal(384, Siglip2ImagePreprocessor.Size);
        Assert.Equal(1152, ModelCatalog.EmbeddingDim);
    }
}
