namespace Ohanami.Core;

/// <summary>
/// 製品が使っているモデルの、画面に出す名前(<see cref="OhanamiCore.ModelLabels"/>)。<paramref name="FeatureVector"/> は特徴ベクトル化のモデル(例: SigLIP2)、
/// <paramref name="Vlm"/> は VLM(例: Qwen3-VL-8B-Instruct)。値は <c>ModelCatalog.DisplayName</c>・<c>VlmCatalog.DisplayName</c>(定義のそば)で、書き写さない。
/// </summary>
public sealed record ModelLabels(string FeatureVector, string Vlm);
