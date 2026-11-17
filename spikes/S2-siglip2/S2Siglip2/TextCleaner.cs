using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using S2Siglip2.Generated;

namespace S2Siglip2;

/// <summary>
/// SigLIP2 のテキスト前処理。参照の clean_text と同じ結果を返す。
/// <code>
/// text = " ".join(text.split())                              空白の連なりを1つにまとめ、両端を除く
/// text = text.translate(ASCII 句読点 32 字を削除).lower()      削除であって置き換えではない("don't" → "dont")
/// </code>
/// NFKC はかけない(語彙側の正規化が identity のため。S3 の正規化を流用してはいけない)。全角の句読点は残る。
/// 空白の集合と小文字化の対応は、参照の Python(str.split / str.lower)が実際に返す結果を表にしたもの
/// (PythonTextTables。reference/gen_text_tables.py が生成)。.NET の char.IsWhiteSpace / ToLowerInvariant には依存しない。
/// 既知の差(参照の Python と違う入力。比較のコーパスからは外してある): U+0130(İ。Python は2文字になるが、ここは1対1の写像で
/// 表にない = そのまま)、U+03A3(Σ。Python は語末で ς、それ以外で σ にするが、ここは常に σ)。
/// </summary>
public static class TextCleaner
{
    /// <summary>string.punctuation(ASCII の句読点 32 字)。</summary>
    private const string AsciiPunctuation = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    private static readonly HashSet<int> Whitespace = new(PythonTextTables.WhitespaceCodePoints);
    private static readonly Dictionary<int, int> Lower = BuildLower();

    private static Dictionary<int, int> BuildLower()
    {
        int[] p = PythonTextTables.LowercasePairs;
        var d = new Dictionary<int, int>(p.Length / 2);
        for (int i = 0; i < p.Length; i += 2) d.Add(p[i], p[i + 1]);
        return d;
    }

    /// <summary>clean_text(canonicalize = true)。null は ArgumentNullException、不正なサロゲートは ArgumentException。</summary>
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // 1. " ".join(text.split())
        var collapsed = new StringBuilder(text.Length);
        bool pendingSpace = false;
        int i = 0;
        while (i < text.Length)
        {
            Rune r = ReadRune(text, i, out int consumed);
            i += consumed;
            if (Whitespace.Contains(r.Value))
            {
                if (collapsed.Length > 0) pendingSpace = true;
                continue;
            }
            if (pendingSpace)
            {
                collapsed.Append(' ');
                pendingSpace = false;
            }
            collapsed.Append(r.ToString());
        }

        // 2. ASCII 句読点を削除し、3. 小文字化(1対1の写像)
        var result = new StringBuilder(collapsed.Length);
        foreach (Rune r in collapsed.ToString().EnumerateRunes())
        {
            int v = r.Value;
            if (v < 0x80 && AsciiPunctuation.Contains((char)v)) continue;
            if (Lower.TryGetValue(v, out int lower)) result.Append(new Rune(lower).ToString());
            else result.Append(r.ToString());
        }
        return result.ToString();
    }

    private static Rune ReadRune(string s, int index, out int consumed)
    {
        if (Rune.DecodeFromUtf16(s.AsSpan(index), out Rune r, out consumed) != OperationStatus.Done)
        {
            throw new ArgumentException($"位置 {index} に不正なサロゲート(U+{(int)s[index]:X4})がある。", nameof(s));
        }
        return r;
    }
}
