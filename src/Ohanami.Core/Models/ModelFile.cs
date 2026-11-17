using System;

namespace Ohanami.Core.Models;

/// <summary>モデルを構成するファイルの役割。</summary>
internal enum ModelRole
{
    /// <summary>画像側の ONNX(visual/model.onnx)。</summary>
    VisualOnnx,

    /// <summary>テキスト側の ONNX(textual/model.onnx。グラフだけ)。</summary>
    TextualOnnx,

    /// <summary>テキスト側の外部データ(textual/model.onnx_data。1 ファイル)。</summary>
    TextualExternalData,

    /// <summary>トークナイザ(textual/tokenizer.json)。</summary>
    Tokenizer,
}

/// <summary>検証の単位。テキスト側は起動時(Ready の条件)、画像側は裏で検証し、エンコーダを作る時点で参照する。</summary>
internal enum ModelSide
{
    Image,
    Text,
}

/// <summary>モデルの構成要素 1 つ: {役割、モデルのディレクトリからの相対パス(区切りは "/")、sha256(小文字 16 進 64 桁)、サイズ(バイト)}。</summary>
internal sealed record ModelFile(ModelRole Role, string RelativePath, string Sha256, long Size)
{
    public ModelSide Side => ModelRoles.SideOf(Role);
}

/// <summary>役割 ↔ 文字列の明示的な変換と、役割の性質。列挙の名前に頼らない(ADR 0005)。</summary>
internal static class ModelRoles
{
    /// <summary>すべての役割。この並びが、正準文字列と spec_json の中のファイルの並び(固定の順)。</summary>
    public static readonly ModelRole[] All =
    [
        ModelRole.VisualOnnx,
        ModelRole.TextualOnnx,
        ModelRole.TextualExternalData,
        ModelRole.Tokenizer,
    ];

    public static string ToText(ModelRole role) => role switch
    {
        ModelRole.VisualOnnx => "visual-onnx",
        ModelRole.TextualOnnx => "textual-onnx",
        ModelRole.TextualExternalData => "textual-onnx-data",
        ModelRole.Tokenizer => "tokenizer",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "未定義のモデルの役割"),
    };

    public static ModelRole FromText(string text) => text switch
    {
        "visual-onnx" => ModelRole.VisualOnnx,
        "textual-onnx" => ModelRole.TextualOnnx,
        "textual-onnx-data" => ModelRole.TextualExternalData,
        "tokenizer" => ModelRole.Tokenizer,
        _ => throw new ModelMismatchException($"モデルの役割「{text}」を知らない(この版のコードが扱えない spec_json)。", subject: text),
    };

    public static ModelSide SideOf(ModelRole role) => role switch
    {
        ModelRole.VisualOnnx => ModelSide.Image,
        ModelRole.TextualOnnx => ModelSide.Text,
        ModelRole.TextualExternalData => ModelSide.Text,
        ModelRole.Tokenizer => ModelSide.Text,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "未定義のモデルの役割"),
    };
}
