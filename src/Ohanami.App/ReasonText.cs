using System;
using System.Collections.Generic;
using System.Linq;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// 検索の結果の 1 枚が「なぜ出たのか」を、言葉にする(純粋な関数で、WPF の型を使わない)。
/// 材料は検索の結果(<see cref="SearchResult.Hits"/>。文字の一致は <see cref="TextDetail"/> の一致箇所と <see cref="TextDetail.FuzzyTerms"/>)。
/// 文言は ADR 0038 に沿う(内部の語を出さない。欄の名前は「OCR」「説明文」「VLM のタグ」「手動タグ」)。
/// 一覧のツールチップと詳細画面の「出た理由」が、同じ行を使う。**検索の結果でない一覧(新しい順・処理状態の一覧から開いた詳細)には使わない**(呼び出し側が出さない)。
/// </summary>
internal static class ReasonText
{
    /// <summary>文字(OCR・説明文)が、検索の語に一致した。<c>{0}</c> は一致した欄(「・」でつなぐ)。</summary>
    public const string TextMatchedPrefix = "文字が一致";

    /// <summary>タグ(VLM のタグ・手動タグ)が、検索の語に一致した。</summary>
    public const string TagMatchedPrefix = "タグが一致";

    /// <summary>あいまい一致(OCR の読み違いへの fallback)で当たった。</summary>
    public const string TextSimilar = "文字が似ています (読み違いかもしれません)";

    /// <summary>
    /// 文字では一致せず、特徴ベクトル検索で出た(一覧に入った)。「出た」= 一覧に入った、「一致」= 全文検索で語が合った(特徴ベクトル検索には「一致」を使わない)。
    /// 近さを言い切らない(「類似度」と言うだけ。全画像に順位が付くので、「近い」とは限らない)。モデル名は Core の公開の表示名から(ここに書かない)。
    /// </summary>
    public static string VisuallyClose => $"{ProcessingWords.StageName(JobKind.Embed)}で出ました ({ProcessingWords.ModelName(JobKind.Embed)} の類似度。文字の一致なし)";

    /// <summary>
    /// この画像は、まだ全文検索のインデックスに文書が無いので、文字では探せない。「VLM の前」とは言わない(時間の順に読める。VLM が失敗したままの画像も同じ状態)。
    /// 手動タグがあれば文書はあるので、この行は出ない。
    /// </summary>
    public static string NoTextIndex => $"{ProcessingWords.StageName(JobKind.Vlm)}のインデックスがまだありません (VLM が済んでいないか、失敗しています)";

    /// <summary>
    /// 理由の行(言葉)。<paramref name="noTextIndex"/> は、その画像に全文検索の索引の文書がまだ無いこと(呼び出し側が、全文の経路が使えているときだけ立てる)。
    /// <list type="bullet">
    /// <item>文字の一致(全文の経路の結果に入っている): あいまい一致の語があれば「文字が似ている(読み違いかもしれない): 「語」」。
    ///   無ければ、一致した欄から「文字が一致(OCR・説明文)」「タグが一致(VLM のタグ・手動タグ)」。(あいまい一致の語と正確な一致の語が混ざるクエリは、語ごとの欄が分からないので、
    ///   似ている語の行だけを出す。一致の強調は、詳細画面の欄にある)</item>
    /// <item>「特徴ベクトル検索で出ました」: **文字の理由が無いときだけ**。特徴ベクトル検索は全画像に類似度が付くので、文字で当たった画像にも特徴ベクトル検索の順位はあるが、それを理由とは言わない
    ///   (関連度の低い見た目を「近い」と言わないため。関連度低は入れない。ADR 0034)。</item>
    /// <item>「テキスト検索のインデックスがまだありません」: 索引が無い画像に、足す(文字で当たらなかった理由)。</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> Lines(SearchResult result, bool noTextIndex)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lines = new List<string>();

        TextDetail? text = result.Hits.Select(h => h.Detail).OfType<TextDetail>().FirstOrDefault();
        if (text is not null)
        {
            if (text.FuzzyTerms.Count > 0)
            {
                lines.Add(TextSimilar + ": " + string.Join("", text.FuzzyTerms.Select(t => "「" + t + "」")));
            }
            else
            {
                string[] words = FieldNames(text, TextField.Ocr, TextField.Description);
                string[] tags = FieldNames(text, TextField.Tags, TextField.ManualTags);
                if (words.Length > 0) lines.Add(TextMatchedPrefix + " (" + string.Join("・", words) + ")");
                if (tags.Length > 0) lines.Add(TagMatchedPrefix + " (" + string.Join("・", tags) + ")");
                if (lines.Count == 0) lines.Add(TextMatchedPrefix);   // 一致の範囲を出せなかった(まれ。強調だけを諦めた)が、全文の経路には当たっている
            }
        }

        if (lines.Count == 0 && result.Hits.Any(h => h.Detail is VisualDetail)) lines.Add(VisuallyClose);
        if (noTextIndex) lines.Add(NoTextIndex);
        return lines;
    }

    /// <summary>一致の範囲がある欄の名前(<paramref name="fields"/> の順)。</summary>
    private static string[] FieldNames(TextDetail text, params TextField[] fields) =>
        [.. fields.Where(f => text.Matches.Any(m => m.Field == f && m.Spans.Count > 0)).Select(Name)];

    private static string Name(TextField field) => field switch
    {
        TextField.Ocr => "OCR",
        TextField.Description => "説明文",
        TextField.Tags => "VLM のタグ",
        TextField.ManualTags => "手動タグ",
        _ => field.ToString(),
    };
}
