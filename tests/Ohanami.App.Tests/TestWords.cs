using Ohanami.Core;

namespace Ohanami.App.Tests;

/// <summary>
/// 試験が期待する、画面の語のうち、<see cref="ProcessingWords"/> から流れる名前(段階・経路の名前)。試験の文字列に名前を書き写さず、ここから取る
/// (名前を替えたとき、画面と試験が一緒に替わる。「全文検索」→「テキスト検索」、「特徴ベクトル検索(SigLIP2)」の括弧の前の空白が入ったとき、試験が壊れないように)。
/// </summary>
internal static class TestWords
{
    /// <summary>テキスト検索(VLM の段階・経路)の名前。</summary>
    public static string TextSearch => ProcessingWords.StageName(JobKind.Vlm);

    /// <summary>特徴ベクトル検索の名前(モデル名なし)。</summary>
    public static string VisualSearch => ProcessingWords.StageName(JobKind.Embed);

    /// <summary>特徴ベクトル検索の名前にモデル名を添えた形(経路の名前)。</summary>
    public static string VisualWithModel => ProcessingWords.StageNameWithModel(JobKind.Embed);
}
