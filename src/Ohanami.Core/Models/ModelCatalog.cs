using System.Linq;
using Ohanami.Core.Embedding;

namespace Ohanami.Core.Models;

/// <summary>
/// 画像の埋め込みに使うモデルの構成(静的な定義)。自前変換のモデル(tools/convert-siglip2/ が元の重みから作ったもの)を指す。
/// 値(パス・サイズ・sha256)は models/MANIFEST.tsv の <c>ohanami-local/ViT-SO400M-16-SigLIP2-384__ohanami</c> の行と同じで、
/// テストで MANIFEST と突き合わせる。モデルを替えるときは、ここを意図して書き換える(新しい model_id になり、全件が要再作成になる)。
/// </summary>
internal static class ModelCatalog
{
    /// <summary>models/ の下のディレクトリ名。開発中のモデルの置き場は <c>models/&lt;この名前&gt;</c>。</summary>
    public const string DirectoryName = "ViT-SO400M-16-SigLIP2-384__ohanami";

    /// <summary>models.name に入れる名前。</summary>
    public const string Name = DirectoryName;

    /// <summary>
    /// 画面に出す名前(「どのモデルで作ったか」を見せる。<see cref="OhanamiCore.ModelLabels"/> で App に渡す。ADR 0038)。モデルのファミリーの名前(SigLIP2)だけ。
    /// モデルを替えるときは、定義(<see cref="DirectoryName"/>・<see cref="Family"/>)と一緒にここも書き換える(ModelLabelsTests が、定義と食い違っていないかを見張る)。
    /// </summary>
    public const string DisplayName = "SigLIP2";

    /// <summary>model_id の接頭辞。</summary>
    public const string Family = "siglip2-so400m-p16-384";

    /// <summary>埋め込みの次元。</summary>
    public const int EmbeddingDim = 1152;

    // ---- 取得元(ダウンロードの URL を作る定数。EmbeddingModelSpec・spec_json には入れない: 入れると、今ある DB の models の行が読めなくなる)

    /// <summary>
    /// モデルを取る Hugging Face の URL の根。<c>huggingface.co</c> の文字があってよいのは、この定数を持つカタログ(この型と <see cref="VlmCatalog"/>)だけで、
    /// 見張りのテスト(NetworkBoundaryGuardTests)が見張る。
    /// </summary>
    public const string HfBaseUrl = "https://huggingface.co";

    /// <summary>
    /// 自前変換した SigLIP2 を置く Hugging Face のリポジトリ(公開)。置く中身は、この型の <see cref="Current"/> のファイルと同じ相対パス
    /// (visual/model.onnx・textual/model.onnx・textual/model.onnx_data・textual/tokenizer.json)・同じ SHA-256。
    /// </summary>
    public const string HfRepository = "ohanami-app/siglip2-so400m-patch16-384-onnx";

    /// <summary>
    /// 上のリポジトリのコミット(40 桁)。ファイル(<see cref="Current"/> の 4 つ。サイズと SHA-256 は定義と同じ)を上げたコミットで、
    /// ダウンロードは、毎回このコミットを指す(あとから上書きされても、同じ中身が取れる)。
    /// </summary>
    public const string HfRevision = "2fffd6544cbb7b1bfcde13e3f25d3c7c11911043";

    /// <summary>
    /// 取得元のリポジトリの画面の URL。<paramref name="revision"/> を渡すと、そのコミットのファイル一覧(<c>/tree/&lt;コミット&gt;</c>)、無ければリポジトリの最初の画面。
    /// 画面が「入手先」として見せる(利用者が、手でファイルを取るとき)。<c>huggingface.co</c> の文字を、この型の外に書かないために、ここで作る。
    /// </summary>
    public static string RepositoryUrl(string repository, string? revision = null)
    {
        System.ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        return HfBaseUrl + "/" + repository + (revision is null ? "" : "/tree/" + revision);
    }

    /// <summary>画像の埋め込みに使うモデル。</summary>
    public static EmbeddingModelSpec Current { get; } = new(
        Family,
        [
            new ModelFile(ModelRole.VisualOnnx, "visual/model.onnx",
                "c801892ee1737a913b729ac19f4b1ed69bbcc80755b568e7cd8266e9c8f32b6e", 1713350137),
            new ModelFile(ModelRole.TextualOnnx, "textual/model.onnx",
                "785486a7ed6c5a0209e31740ed35e9fc806ef4a5aa1ac37bc0f3a5d7137f07df", 560482),
            new ModelFile(ModelRole.TextualExternalData, "textual/model.onnx_data",
                "b04effd92d0ea4dd30063018b29b9811e46367e119f9fc5b51879e27f32290bc", 2831131584),
            new ModelFile(ModelRole.Tokenizer, "textual/tokenizer.json",
                "220c63d496e0c14e63eb656c91e0215e926202e4c74b1f089e09f1920d779b04", 34362885),
        ],
        Siglip2ImagePreprocessor.SpecVersion,
        TextCleaner.SpecVersion,
        EmbeddingDim,
        EmbeddingModelSpec.NormalizeL2);
}
