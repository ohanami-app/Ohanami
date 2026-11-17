using System;
using System.Collections.Generic;
using Microsoft.ML.OnnxRuntime;
using Ohanami.Core.Models;
using Ohanami.Core.Native;

namespace Ohanami.Core.Embedding;

/// <summary>画像のセッションの Run が失敗したとき(ORT の例外)。<see cref="Siglip2ImageEncoder"/> が、セッションを作り直して 1 回だけ再試行する対象。</summary>
internal sealed class ImageRunException : Exception
{
    public ImageRunException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>画像側の 1 つのセッション(ORT)。作り直せるように、<see cref="Siglip2ImageEncoder"/> から切り離してある(テストで偽物に替えられる)。</summary>
internal interface IImageSession : IDisposable
{
    /// <summary>形 [3,384,384] の float32(CHW)を入れて、モデルの出力(正規化前)を返す。Run の失敗は <see cref="ImageRunException"/>。</summary>
    float[] Run(float[] chwTensor);
}

/// <summary>セッションを作った結果。GPU の画像エンコーダを作ったときは自己検査の結果が付く。</summary>
internal sealed record ImageSessionCreation(IImageSession Session, SelfCheckReport? SelfCheck);

/// <summary>画像側のセッションを(再)作成するもの。作り直しのたびに同じ手順(GPU なら自己検査まで)を行う。</summary>
internal interface IImageSessionFactory
{
    ImageSessionCreation Create();
}

/// <summary>
/// SigLIP2 の画像側(GPU、CUDA EP)。最初に必要になったときに作り、Pause やジョブの切り替えで Dispose する(再開時に作り直す。作成 1.3〜1.6 秒。ADR 0011)。
/// 入力は形 [3,384,384] の float32(CHW。<see cref="Siglip2ImagePreprocessor.ToTensor"/> の結果)、出力は L2 正規化した 1152 次元。
/// <list type="bullet">
/// <item>作成の手順(GPU): <c>NativeRuntime.RequireConfigured</c> → <c>VerifyRequiredFiles</c> → セッション作成 → ダミー入力(0)で 1 回 Run
///   → <c>NativeSelfCheck.Evaluate</c>。自己検査に失敗したら <see cref="NativeRuntimeException"/>(1 枚も書かせない)。結果は <see cref="SelfCheck"/>。</item>
/// <item>Run の例外(<see cref="ImageRunException"/>)では、セッションを Dispose → 作り直し(自己検査も含む)→ その 1 枚を 1 回だけ再試行する。
///   再試行しても失敗なら、その例外を呼び出し側に返す(その画像を failed にして先へ進む)。作り直し自体の失敗は、そのまま例外(致命的)。</item>
/// <item>呼び出しは 1 本のワーカーから(排他制御はしているが、GPU は 1 本で使う設計)。Dispose は Embed が終わってから。</item>
/// </list>
/// </summary>
internal sealed class Siglip2ImageEncoder : IDisposable
{
    private readonly IImageSessionFactory _factory;
    private readonly object _gate = new();
    private IImageSession _session;
    private bool _disposed;

    /// <summary>直近に作ったセッションの自己検査の結果(GPU のとき。偽物や CPU の工場では null)。</summary>
    public SelfCheckReport? SelfCheck { get; private set; }

    /// <summary>Run の失敗でセッションを作り直した回数(診断用)。</summary>
    public int RecreateCount { get; private set; }

    /// <summary>直近に作り直したときの、元の Run の例外のメッセージ(診断用)。作り直しが無ければ null。</summary>
    public string? LastRecreateReason { get; private set; }

    /// <summary>セッションを作ってエンコーダにする(作成に失敗したら例外。エンコーダは作られない)。</summary>
    internal Siglip2ImageEncoder(IImageSessionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        ImageSessionCreation first = factory.Create();
        _session = first.Session;
        SelfCheck = first.SelfCheck;
    }

    /// <summary>
    /// GPU の画像エンコーダを作る。<paramref name="nativeDirectory"/> は <c>NativeRuntime.Configure</c> に渡したディレクトリ
    /// (ORT・CUDA・cuDNN の DLL が平らにある、exe のディレクトリ)。Configure が済んでいなければ <see cref="NativeRuntimeException"/>。
    /// モデルのハッシュの検証(<see cref="ModelVerifier.VerifyImageSideAsync"/>)は呼び出し側が済ませておく(ここでは存在とサイズだけ確かめる。
    /// ハッシュを読むと、作り直しのたびに 1.7GB を読むことになる)。
    /// </summary>
    public static Siglip2ImageEncoder Create(string modelDirectory, EmbeddingModelSpec spec, string nativeDirectory)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ModelFile visual = spec.File(ModelRole.VisualOnnx);
        ModelVerifier.CheckPresence(modelDirectory, [visual]);
        return new Siglip2ImageEncoder(new CudaImageSessionFactory(ModelVerifier.PathOf(modelDirectory, visual), nativeDirectory));
    }

    /// <summary>CPU の画像エンコーダ(GPU が要らないテスト用。製品は <see cref="Create"/>)。</summary>
    internal static Siglip2ImageEncoder CreateCpuForTests(string modelDirectory, EmbeddingModelSpec spec, int threads)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ModelFile visual = spec.File(ModelRole.VisualOnnx);
        ModelVerifier.CheckPresence(modelDirectory, [visual]);
        return new Siglip2ImageEncoder(new CpuImageSessionFactory(ModelVerifier.PathOf(modelDirectory, visual), threads));
    }

    /// <summary>形 [3,384,384] の float32(CHW)から、L2 正規化した 1152 次元の埋め込みを返す。</summary>
    public float[] Embed(float[] chwTensor)
    {
        ArgumentNullException.ThrowIfNull(chwTensor);
        int expected = 3 * Siglip2ImagePreprocessor.Size * Siglip2ImagePreprocessor.Size;
        if (chwTensor.Length != expected) throw new ArgumentException($"テンソルの長さが 3×{Siglip2ImagePreprocessor.Size}×{Siglip2ImagePreprocessor.Size} = {expected} でない(実際 {chwTensor.Length})", nameof(chwTensor));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                return Embeddings.L2Normalize(_session.Run(chwTensor));
            }
            catch (ImageRunException first)
            {
                // セッションを捨てて作り直し、この 1 枚だけを 1 回だけ再試行する(CUDA の一時的な失敗などから回復するため)
                _session.Dispose();
                RecreateCount++;
                LastRecreateReason = first.Message;
                ImageSessionCreation again;
                try
                {
                    again = _factory.Create();
                }
                catch
                {
                    _disposed = true; // セッションが無い状態で残さない(以後の Embed は ObjectDisposedException)
                    throw;
                }
                _session = again.Session;
                SelfCheck = again.SelfCheck ?? SelfCheck;
                try
                {
                    return Embeddings.L2Normalize(_session.Run(chwTensor));
                }
                catch (ImageRunException second)
                {
                    throw new ImageRunException($"画像の Run が失敗し、セッションを作り直して再試行しても失敗した。1 回目: {first.Message} / 2 回目: {second.Message}", second);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _session.Dispose();
        }
    }
}

/// <summary>GPU(CUDA EP)の画像セッションを、Configure 済みの確認 → 必要ファイルの確認 → 作成 → ダミー入力で 1 回 Run → 自己検査、の順で作る。</summary>
internal sealed class CudaImageSessionFactory : IImageSessionFactory
{
    private readonly string _visualOnnxPath;
    private readonly string _nativeDirectory;

    public CudaImageSessionFactory(string visualOnnxPath, string nativeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(visualOnnxPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeDirectory);
        _visualOnnxPath = visualOnnxPath;
        _nativeDirectory = nativeDirectory;
    }

    public ImageSessionCreation Create()
    {
        // 1. 検索パスの設定が済んでいること(PATH の DLL に黙って頼らない)
        NativeRuntime.RequireConfigured(_nativeDirectory);
        // 2. 必要な DLL が exe のディレクトリに平らに揃っていること(足りないと ORT がプロセスごと落ちることがあるので、ORT を呼ぶ前に)
        NativeRuntime.VerifyRequiredFiles(_nativeDirectory);
        // 3. セッション作成(CPU に落ちるノードがあれば失敗)
        InferenceSession session = OrtSessions.OpenImageCuda(_visualOnnxPath);
        try
        {
            // 4. ダミー入力(0)で 1 回 Run。cuDNN などの DLL は最初の Run でロードされる。欠落は、ここで例外になる
            try
            {
                OrtSessions.RunImage(session, new float[3 * Siglip2ImagePreprocessor.Size * Siglip2ImagePreprocessor.Size]);
            }
            catch (OnnxRuntimeException e)
            {
                throw new NativeRuntimeException($"GPU の最初の Run に失敗した(cuDNN などの DLL の欠落、またはドライバの問題の可能性): {e.Message}", e);
            }

            // 5. 自己検査: ロードされた cu* / nv* / onnxruntime* の DLL がアプリのディレクトリから読まれていること
            SelfCheckReport report = NativeSelfCheck.Evaluate(
                OrtEnv.Instance().GetVersionString(), NativeRuntime.CaptureLoadedModules(), _nativeDirectory, Environment.SystemDirectory);
            if (!report.Passed)
            {
                throw new NativeRuntimeException("GPU の自己検査に失敗した: " + string.Join(" / ", report.Violations));
            }
            return new ImageSessionCreation(new OrtImageSession(session), report);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }
}

/// <summary>CPU の画像セッションの工場(GPU が要らないテスト用)。</summary>
internal sealed class CpuImageSessionFactory : IImageSessionFactory
{
    private readonly string _visualOnnxPath;
    private readonly int _threads;

    public CpuImageSessionFactory(string visualOnnxPath, int threads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(visualOnnxPath);
        _visualOnnxPath = visualOnnxPath;
        _threads = threads;
    }

    public ImageSessionCreation Create() => new(new OrtImageSession(OrtSessions.OpenImageCpu(_visualOnnxPath, _threads)), null);
}

/// <summary>ORT の <see cref="InferenceSession"/> を <see cref="IImageSession"/> にする。Run の ORT の例外を <see cref="ImageRunException"/> にする。</summary>
internal sealed class OrtImageSession : IImageSession
{
    private readonly InferenceSession _session;

    public OrtImageSession(InferenceSession session) => _session = session;

    public float[] Run(float[] chwTensor)
    {
        try
        {
            return OrtSessions.RunImage(_session, chwTensor);
        }
        catch (OnnxRuntimeException e)
        {
            throw new ImageRunException("ORT の Run が失敗した: " + e.Message, e);
        }
    }

    public void Dispose() => _session.Dispose();
}
