using System;
using System.Collections.Generic;
using System.Text;

namespace S1Vlm;

/// <summary>
/// 採点用のテキスト処理。S3(bigram FTS)と同じ正規化規則を使うため、正規化は <see cref="Normalize"/> の1か所に集約する。
/// 文字はすべてコードポイント(Rune)単位で数える。
/// </summary>
public static class TextNorm
{
    /// <summary>NFKC → 小文字化(ToLowerInvariant)→ 空白と改行をすべて除去(char.IsWhiteSpace)。</summary>
    public static string Normalize(string s)
    {
        // 不正なサロゲートを含む文字列は Normalize が ArgumentException を投げる。握りつぶさない。
        string n = s.Normalize(NormalizationForm.FormKC);
        n = n.ToLowerInvariant();
        var sb = new StringBuilder(n.Length);
        foreach (char c in n)
        {
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>文字列をコードポイント(Rune)の配列にする。</summary>
    public static int[] ToCodePoints(string s)
    {
        var list = new List<int>(s.Length);
        foreach (Rune r in s.EnumerateRunes()) list.Add(r.Value);
        return list.ToArray();
    }

    /// <summary>レーベンシュタイン距離(挿入・削除・置換とも1)。</summary>
    public static int Levenshtein(int[] a, int[] b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = prev[j - 1] + cost;
                int del = prev[j] + 1;
                int ins = cur[j - 1] + 1;
                if (del < v) v = del;
                if (ins < v) v = ins;
                cur[j] = v;
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>隣り合う2文字(コードポイント)の多重集合。</summary>
    public static Dictionary<(int, int), int> Bigrams(int[] cps)
    {
        var d = new Dictionary<(int, int), int>();
        for (int i = 0; i + 1 < cps.Length; i++)
        {
            var key = (cps[i], cps[i + 1]);
            d.TryGetValue(key, out int c);
            d[key] = c + 1;
        }
        return d;
    }

    public static int Total(Dictionary<(int, int), int> m)
    {
        int t = 0;
        foreach (int v in m.Values) t += v;
        return t;
    }

    /// <summary>多重集合の共通部分の個数(キーごとの min の合計)。</summary>
    public static int CommonCount(Dictionary<(int, int), int> a, Dictionary<(int, int), int> b)
    {
        int common = 0;
        foreach (var kv in a)
        {
            if (b.TryGetValue(kv.Key, out int other)) common += Math.Min(kv.Value, other);
        }
        return common;
    }
}

/// <summary>1画像ぶんの比較結果。分母が0になる指標は null(出さない)。</summary>
public sealed record CompareResult(
    int RefLen,
    int OutLen,
    int Distance,
    double? Cer,
    int RefBigrams,
    int OutBigrams,
    int Common,
    double? Recall,
    double? Precision);

public static class Scoring
{
    /// <summary>output と reference を同じ規則で正規化して比較する。</summary>
    public static CompareResult Compare(string output, string reference)
    {
        int[] o = TextNorm.ToCodePoints(TextNorm.Normalize(output));
        int[] r = TextNorm.ToCodePoints(TextNorm.Normalize(reference));
        int dist = TextNorm.Levenshtein(o, r);
        var ob = TextNorm.Bigrams(o);
        var rb = TextNorm.Bigrams(r);
        int obn = TextNorm.Total(ob);
        int rbn = TextNorm.Total(rb);
        int common = TextNorm.CommonCount(ob, rb);
        double? cer = r.Length > 0 ? (double)dist / r.Length : null;
        double? recall = rbn > 0 ? (double)common / rbn : null;
        double? precision = obn > 0 ? (double)common / obn : null;
        return new CompareResult(r.Length, o.Length, dist, cer, rbn, obn, common, recall, precision);
    }
}
