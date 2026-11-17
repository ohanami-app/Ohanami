using System;
using System.Collections.Generic;

namespace S3Fts;

public static class Highlighter
{
    /// <summary>
    /// クエリが original のどこに一致したかを、original 上の範囲で返す(昇順、重なる範囲は1つにまとめる)。
    /// 一致の判定は索引と同じトークン列の連続一致(1文字クエリは正規化後の文字の出現)。
    /// 範囲は、一致した最初のトークンの先頭から最後のトークンの末尾まで(間の改行や句読点も含む)。
    /// NFKC で長さが変わる文字は、元の文字の全体(例: 「ｶﾞ」は2文字、「㈱」は1文字)を範囲にする。
    /// </summary>
    public static IReadOnlyList<TextRange> Find(string original, string query)
    {
        QueryPlan plan = QueryPlanner.Plan(query);
        NormalizedText n = NormalizedText.Create(original);
        var raw = new List<TextRange>();

        if (plan.Kind == QueryKind.SingleChar)
        {
            string text = n.Text;
            string q = plan.SingleChar!;
            int from = 0;
            while (from <= text.Length - q.Length)
            {
                int at = text.IndexOf(q, from, StringComparison.Ordinal);
                if (at < 0) break;
                raw.Add(n.ToOriginal(at, at + q.Length));
                from = at + q.Length;
            }
        }
        else
        {
            IReadOnlyList<Token> doc = Tokenizer.TokenizeDocument(n);
            IReadOnlyList<Token> q = plan.Tokens;
            for (int i = 0; i + q.Count <= doc.Count; i++)
            {
                bool all = true;
                for (int k = 0; k < q.Count; k++)
                {
                    if (!string.Equals(doc[i + k].Text, q[k].Text, StringComparison.Ordinal)) { all = false; break; }
                }
                if (!all) continue;
                // 境界記号のトークンは、隣の実文字の範囲を持っている。範囲は先頭トークンの先頭から末尾トークンの末尾まで。
                raw.Add(n.ToOriginal(doc[i].NormStart, doc[i + q.Count - 1].NormEnd));
            }
        }
        return Merge(raw);
    }

    private static IReadOnlyList<TextRange> Merge(List<TextRange> ranges)
    {
        if (ranges.Count == 0) return ranges;
        ranges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Length.CompareTo(b.Length));
        var merged = new List<TextRange>();
        TextRange cur = ranges[0];
        for (int i = 1; i < ranges.Count; i++)
        {
            TextRange r = ranges[i];
            if (r.Start < cur.End)
            {
                cur = new TextRange(cur.Start, Math.Max(cur.End, r.End) - cur.Start);
            }
            else
            {
                merged.Add(cur);
                cur = r;
            }
        }
        merged.Add(cur);
        return merged;
    }
}
