using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ohanami.Core.FullText;

/// <summary>元のテキスト上の範囲(UTF-16 の位置と長さ)。</summary>
internal readonly record struct TextRange(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// NFKC → 小文字化(ToLowerInvariant)→ カタカナをひらがなへ(<see cref="KanaFold"/>)した文字列と、正規化後の各 UTF-16 コードユニットが
/// 元のテキストのどの範囲から来たかの対応表。空白はここでは除去しない(除去は <see cref="Tokenizer"/> の仕事)。
/// NFKC と小文字化の部分は、OCR の採点の正規化と同じ規則(ADR 0029)。
/// </summary>
internal sealed class NormalizedText
{
    private readonly int[] _origStart;
    private readonly int[] _origEnd;

    public string Original { get; }

    /// <summary>NFKC + 小文字化 + カタカナ → ひらがなの結果。</summary>
    public string Text { get; }

    private NormalizedText(string original, string text, int[] origStart, int[] origEnd)
    {
        Original = original;
        Text = text;
        _origStart = origStart;
        _origEnd = origEnd;
    }

    /// <summary>
    /// 書記素クラスタ(例: 半角「ｶ」+「ﾞ」、「e」+結合アクセント、サロゲートペア)ごとに NFKC をかける。
    /// クラスタごとの結果を連結したものが、全体に NFKC をかけた結果と一致しなければ例外にする
    /// (一致しない入力では、位置の対応表が信用できないため)。
    /// </summary>
    public static NormalizedText Create(string original)
    {
        CharClassifier.Validate(original, nameof(original));

        var sb = new StringBuilder(original.Length);
        var starts = new List<int>(original.Length);
        var ends = new List<int>(original.Length);
        int pos = 0;
        while (pos < original.Length)
        {
            int len = StringInfo.GetNextTextElementLength(original.AsSpan(pos));
            if (len <= 0) throw new InvalidOperationException($"位置 {pos} で書記素クラスタの長さが 0 になった。");
            string cluster = original.Substring(pos, len);
            string norm = KanaFold.Fold(cluster.Normalize(NormalizationForm.FormKC).ToLowerInvariant());
            sb.Append(norm);
            for (int k = 0; k < norm.Length; k++)
            {
                starts.Add(pos);
                ends.Add(pos + len);
            }
            pos += len;
        }

        string text = sb.ToString();
        string whole = KanaFold.Fold(original.Normalize(NormalizationForm.FormKC).ToLowerInvariant());
        if (!string.Equals(text, whole, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("クラスタごとの NFKC と全体の NFKC が一致しない。位置の対応表を作れない入力。");
        }
        return new NormalizedText(original, text, starts.ToArray(), ends.ToArray());
    }

    /// <summary>正規化後の [normStart, normEnd)(UTF-16 位置)に対応する、元のテキスト上の範囲。</summary>
    public TextRange ToOriginal(int normStart, int normEnd)
    {
        if (normStart < 0 || normEnd > Text.Length || normStart >= normEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(normStart), $"不正な範囲 [{normStart}, {normEnd}) (長さ {Text.Length})。");
        }
        int s = _origStart[normStart];
        int e = _origEnd[normEnd - 1];
        return new TextRange(s, e - s);
    }
}
