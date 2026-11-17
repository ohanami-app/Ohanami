using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Embedding;
using Ohanami.Core.Models;

namespace Ohanami.Core.Indexing;

// 埋め込みのジョブ(EmbedRunner)と、画像のエンコーダの実物(Embedding/Siglip2ImageEncoder)の間の「小さな継ぎ目」。
// テストでは偽物に替える(GPU もモデルも要らない)。

/// <summary>前処理の結果: 元画像の幅・高さ(images に入れる)と、形 [3,384,384] の float32(CHW)のテンソル。</summary>
internal sealed record PreprocessedImage(int Width, int Height, float[] Tensor);

/// <summary>
/// 画像ファイルのバイト列(PNG / JPEG / WebP)を、エンコーダの入力にする(デコード → リサイズ → 正規化)。CPU のワーカーから呼ばれる(並列に呼ばれうる)。
/// デコードできない・壊れた画像は例外にする(EmbedRunner はその画像を failed にして先へ進む)。
/// </summary>
internal interface IImagePreprocessor
{
    PreprocessedImage Preprocess(byte[] encoded);
}

/// <summary>
/// 画像のエンコーダ 1 つ(GPU のセッションを持つ)。**Run の失敗でセッションを作り直して、その 1 枚を 1 回だけ再試行するのは、エンコーダの責任**
/// (<see cref="Siglip2ImageEncoder"/> が行う。作り直しのたびに自己検査も行う)。呼び出し側(EmbedRunner)の扱い:
/// <list type="bullet">
/// <item><see cref="ImageRunException"/>(再試行しても失敗)・<see cref="System.IO.InvalidDataException"/>(出力が不正)→ その画像を failed にして先へ進む。</item>
/// <item>それ以外の例外(作り直しの失敗、<see cref="NativeRuntimeException"/>、ObjectDisposed など)→ 致命的。ジョブを止めて Run = Failed。</item>
/// </list>
/// 呼び出しは 1 本のワーカーから。Dispose は呼び出し側(ランナーの ReleaseAsync)が行う。
/// </summary>
internal interface IImageEmbedder : IDisposable
{
    /// <summary>直近に作ったセッションの自己検査の結果(GPU のとき。偽物や CPU では null)。</summary>
    SelfCheckReport? SelfCheck { get; }

    /// <summary>実際に使っている実行の設定(CUDA のプロバイダーのオプション、セッションの設定)。runs.runtime_json に記録する。偽物では null。</summary>
    IReadOnlyDictionary<string, string>? ExecutionSettings { get; }

    /// <summary>Run の失敗でセッションを作り直した回数(このエンコーダを作ってからの累計。診断用。偽物は 0)。<see cref="EmbedRunner"/> が、増えたらログに残す。</summary>
    int RecreateCount { get; }

    /// <summary>直近の作り直しの理由(元の Run の例外のメッセージ)。作り直しが無ければ null。</summary>
    string? LastRecreateReason { get; }

    /// <summary>形 [3,384,384] の float32(CHW)から、L2 正規化した埋め込み(モデルの次元)を返す。</summary>
    float[] Embed(float[] chwTensor);
}

/// <summary>
/// エンコーダの工場。<see cref="Model"/> は GPU を作らずに分かる(現行の model_id の判定や Outdated の件数に使う)。
/// <see cref="CreateAsync"/> は、画像側のモデルのハッシュ検証の完了待ち → 検索パスの設定の確認 → 必要ファイルの事前検査 → セッション作成
/// → ダミー Run → 自己検査、までを済ませて返す。自己検査に失敗したら <see cref="NativeRuntimeException"/>(エンコーダは返さない=1 枚も書かせない)。
/// </summary>
internal interface IImageEmbedderFactory
{
    EmbeddingModelSpec Model { get; }

    /// <summary>models.name に入れる名前。</summary>
    string ModelName { get; }

    Task<IImageEmbedder> CreateAsync(CancellationToken ct);
}

/// <summary><see cref="IImagePreprocessor"/> の実物: SkiaSharp でデコードし、Pillow と一致するリサイズ・正規化(<see cref="Siglip2ImagePreprocessor"/>)をする。</summary>
internal sealed class SkiaImagePreprocessor : IImagePreprocessor
{
    private readonly IImageDecoder _decoder;

    public SkiaImagePreprocessor() : this(new SkiaImageDecoder()) { }

    public SkiaImagePreprocessor(IImageDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        _decoder = decoder;
    }

    public PreprocessedImage Preprocess(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        RgbImage rgb = _decoder.DecodeRgb(encoded);
        return new PreprocessedImage(rgb.Width, rgb.Height, Siglip2ImagePreprocessor.ToTensor(Siglip2ImagePreprocessor.Resize(rgb)));
    }
}

/// <summary>
/// 実物の工場: 画像側のモデルのハッシュ検証(起動時から裏で走っているタスク)の完了を待ち、<see cref="Siglip2ImageEncoder.Create"/> でエンコーダを作る
/// (Configure 済みの確認・必要ファイルの事前検査・セッション作成・ダミー Run・自己検査は Create の中)。
/// </summary>
internal sealed class Siglip2ImageEmbedderFactory : IImageEmbedderFactory
{
    private readonly string _modelDirectory;
    private readonly string _nativeDirectory;
    private readonly Task _imageSideVerified;

    /// <param name="imageSideVerified">画像側のハッシュ検証のタスク(失敗なら <see cref="ModelMismatchException"/> で完了している)。</param>
    public Siglip2ImageEmbedderFactory(string modelDirectory, EmbeddingModelSpec model, string modelName, string nativeDirectory, Task imageSideVerified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeDirectory);
        ArgumentNullException.ThrowIfNull(imageSideVerified);
        _modelDirectory = modelDirectory;
        Model = model;
        ModelName = modelName;
        _nativeDirectory = nativeDirectory;
        _imageSideVerified = imageSideVerified;
    }

    public EmbeddingModelSpec Model { get; }

    public string ModelName { get; }

    public async Task<IImageEmbedder> CreateAsync(CancellationToken ct)
    {
        await _imageSideVerified.WaitAsync(ct).ConfigureAwait(false);
        // セッションの作成は 1.3〜1.6 秒かかる同期処理なので、呼び出しのスレッドを塞がない
        return await Task.Run(() => (IImageEmbedder)new Siglip2ImageEmbedder(Siglip2ImageEncoder.Create(_modelDirectory, Model, _nativeDirectory)), ct).ConfigureAwait(false);
    }
}

/// <summary><see cref="Siglip2ImageEncoder"/>(Embedding/ にある実物。このファイルでは触らない)を継ぎ目の形に合わせる薄い包み。</summary>
internal sealed class Siglip2ImageEmbedder : IImageEmbedder
{
    private readonly Siglip2ImageEncoder _encoder;

    public Siglip2ImageEmbedder(Siglip2ImageEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        _encoder = encoder;
    }

    public SelfCheckReport? SelfCheck => _encoder.SelfCheck;

    public IReadOnlyDictionary<string, string>? ExecutionSettings { get; } = MergedSettings();

    private static Dictionary<string, string> MergedSettings()
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in OrtSessions.ImageCudaProviderOptions()) all["cuda." + kv.Key] = kv.Value;
        foreach (KeyValuePair<string, string> kv in OrtSessions.ImageSessionSettings()) all["session." + kv.Key.Replace("session.", "", StringComparison.Ordinal)] = kv.Value;
        return all;
    }

    public int RecreateCount => _encoder.RecreateCount;

    public string? LastRecreateReason => _encoder.LastRecreateReason;

    public float[] Embed(float[] chwTensor) => _encoder.Embed(chwTensor);

    public void Dispose() => _encoder.Dispose();
}
