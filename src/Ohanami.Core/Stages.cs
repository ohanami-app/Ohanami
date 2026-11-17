namespace Ohanami.Core;

/// <summary>
/// stage_status の行の stage 列(どの結果を埋めたか)。Ocr と Caption は VLM の 1 回の呼び出しで同時に埋まる(行は分けたまま)。
/// 実行の単位である <see cref="JobKind"/> とは別(1 つのジョブが複数の段階を埋めうる)。
/// </summary>
public enum Stage
{
    Embed,
    Ocr,
    Caption,
}

/// <summary>
/// 実行の単位(どのランナーを動かすか)。Vlm は Ocr と Caption の段階を埋める。
/// </summary>
public enum JobKind
{
    Embed,
    Vlm,
}
