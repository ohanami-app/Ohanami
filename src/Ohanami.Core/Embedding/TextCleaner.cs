using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using Ohanami.Core.Embedding.Generated;

namespace Ohanami.Core.Embedding;

/// <summary>
/// SigLIP2 のテキスト前処理。open_clip の canonicalize(<c>tokenizer_kwargs.clean = "canonicalize"</c>)を簡略化した関数で、次の2段を行う。
/// <code>
/// text = " ".join(text.split())                              空白の連なりを1つにまとめ、両端を除く
/// text = text.translate(ASCII 句読点 32 字を削除).lower()      削除であって置き換えではない("don't" → "dont")
/// </code>
/// open_clip の canonicalize(<c>canonicalize_text(basic_clean(x))</c>)そのものではなく、次の点で違う(open_clip 2.31 のソースで確かめた):
/// (1) ftfy による文字の修復と HTML の unescape(<c>basic_clean</c>)を行わない。
/// (2) open_clip は "_" を空白に置き換えてから句読点を削除する("a_b" → "a b")。ここは "_" も句読点として削除する("a_b" → "ab")。
/// (3) open_clip は空白の整理を最後に行う。ここは先に行うので、句読点を削除したあとに空白が2つ並ぶことがある("a - b" → "a  b"。open_clip は "a b")。
/// 参照実装(Python の clean_text)に合わせたのは、ここに書いた2段(空白の整理 → ASCII 句読点の削除 + 小文字化)で、
/// 一致はコーパス 821 件で確かめてある(tests の TestData/README と tools/siglip2-reference/README に出典と範囲を書いた)。
/// モデルの学習時の前処理(open_clip の canonicalize)との違いが検索の質に効くかは、確かめていない。
/// NFKC はかけない(語彙側の正規化が identity のため。テキスト検索の正規化[ADR 0029]を流用してはいけない)。全角の句読点は残る。
/// 空白の集合と小文字化の対応は、参照の Python(str.split / str.lower)が実際に返す結果を表にしたもの
/// (PythonTextTables。tools/siglip2-reference/gen_text_tables.py が生成)。.NET の char.IsWhiteSpace / ToLowerInvariant には依存しない。
/// 既知の差(参照の Python と違う入力。比較のコーパスからは外してある): U+0130(İ。Python は2文字になるが、ここは1対1の写像で
/// 表にない = そのまま)、U+03A3(Σ。Python は語末で ς、それ以外で σ にするが、ここは常に σ)。
/// </summary>
internal static class TextCleaner
{
    /// <summary>前処理の仕様の版。models テーブルの model_id に含める。仕様 = 空白の整理 + ASCII 句読点 32 字の削除 + 小文字化(Python 3.10 / Unicode 13.0 の表)。変えたら版を上げる。</summary>
    public const string SpecVersion = "siglip2-textclean-v1";

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

    /// <summary>テキストの前処理(上の2段)。null は ArgumentNullException、不正なサロゲートは ArgumentException。</summary>
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
