using System;
using System.Collections.Generic;
using System.Linq;

namespace S2Siglip2;

/// <summary>同点の画像の順位の決め方。Optimistic = 同点の先頭の順位(厳密に良い画像の数 + 1)、Mean = 塊が占める順位の平均、Pessimistic = 同点の最後の順位(同じか良い画像の数 + 1)。</summary>
public enum TieMode { Optimistic, Mean, Pessimistic }

/// <summary>
/// 1つのクエリに対する、1つの経路の結果。画像(0 〜 Count-1)ごとのスコア(大きいほど良い)と、ヒットしたか(Present)。
/// 順位は3通り持つ: Rank = 「厳密に良い画像の数 + 1」(楽観。既存の known-item の規則と同じ)、PessimisticRank、MeanRank(同点の塊の平均。段階3の主)。ヒットしていない画像には順位が無い。
/// 全文検索は、語を含む文書だけが Present(含まないものは経路に出てこない)。SigLIP2 は全画像が Present。
/// </summary>
public sealed class RankedPath
{
    public int Count { get; }
    public double[] Score { get; }
    public bool[] Present { get; }
    /// <summary>Present な画像の順位(1 始まり)。Present でなければ 0。</summary>
    public int[] Rank { get; }
    /// <summary>Present な画像を、良い順(同点は画像の番号順)に並べたもの。</summary>
    public int[] Order { get; }
    /// <summary>
    /// 悲観側の順位(「スコアが同じか良いほかの画像の数 + 1」= 同点の最後の位置)。Present でなければ 0。
    /// Rank(楽観側。同点の先頭の位置)との差が、同点の塊の中でどの位置になるか分からない幅。
    /// </summary>
    public int[] PessimisticRank { get; }
    /// <summary>その画像と同点の画像の数(自分を含む。同点なしなら 1)。Present でなければ 0。</summary>
    public int[] TieSize { get; }

    private RankedPath(double[] score, bool[] present)
    {
        if (score.Length != present.Length) throw new ArgumentException("スコアとヒットの長さが違う");
        Count = score.Length;
        Score = score;
        Present = present;
        Order = Enumerable.Range(0, Count).Where(i => present[i]).OrderByDescending(i => score[i]).ThenBy(i => i).ToArray();
        Rank = new int[Count];
        PessimisticRank = new int[Count];
        TieSize = new int[Count];
        MeanRank = new double[Count];
        for (int p = 0; p < Order.Length; p++)
        {
            int i = Order[p];
            // 同点は同じ順位(直前と同じスコアなら、直前の順位)。厳密に良い画像の数 + 1 は、並べた位置 + 1(同点の先頭の位置)と同じ
            Rank[i] = p > 0 && score[Order[p - 1]] == score[i] ? Rank[Order[p - 1]] : p + 1;
        }
        // 同点の塊 [first, last] ごとに、悲観側の順位 = last + 1、塊の大きさ = last - first + 1
        for (int first = 0; first < Order.Length;)
        {
            int last = first;
            while (last + 1 < Order.Length && score[Order[last + 1]] == score[Order[first]]) last++;
            for (int p = first; p <= last; p++)
            {
                PessimisticRank[Order[p]] = last + 1;
                TieSize[Order[p]] = last - first + 1;
                MeanRank[Order[p]] = (first + 1 + last + 1) / 2.0; // 塊が占める順位 first+1 〜 last+1 の平均
            }
            first = last + 1;
        }
    }

    /// <summary>全画像がヒットする経路(SigLIP2 のように、全画像にスコアがある)。</summary>
    public static RankedPath Dense(double[] score) => new(score, Enumerable.Repeat(true, score.Length).ToArray());

    /// <summary>一部の画像だけがヒットする経路(全文検索)。hits は (画像の番号, スコア。大きいほど良い)。同じ画像が2回あれば例外。</summary>
    public static RankedPath Sparse(int count, IEnumerable<(int Index, double Score)> hits)
    {
        var score = new double[count];
        var present = new bool[count];
        foreach (var (i, s) in hits)
        {
            if (i < 0 || i >= count) throw new ArgumentOutOfRangeException(nameof(hits), $"画像の番号 {i} が範囲外(0〜{count - 1})");
            if (present[i]) throw new InvalidOperationException($"画像 {i} が2回ヒットしている");
            present[i] = true;
            score[i] = s;
        }
        return new RankedPath(score, present);
    }

    public int HitCount => Order.Length;

    /// <summary>画像の順位。ヒットしていなければ null。</summary>
    public int? RankOf(int image) => Present[image] ? Rank[image] : null;

    /// <summary>上位 n 件(ヒットが n 件に満たなければ、その分だけ)。</summary>
    public IReadOnlyList<int> Top(int n) => Order.Take(n).ToArray();

    /// <summary>同点の塊が占める順位の平均(例: 4〜43位を40枚で分けるなら全員 23.5)。Present でなければ 0。段階3の主の順位(同点の扱いは中立)。</summary>
    public double[] MeanRank { get; }

    /// <summary>指定の同点規則での順位(Present でなければ 0)。</summary>
    public double RankBy(int image, TieMode mode) => mode switch
    {
        TieMode.Optimistic => Rank[image],
        TieMode.Mean => MeanRank[image],
        TieMode.Pessimistic => PessimisticRank[image],
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>
    /// カスケード(RRF とは別の並べ方): primary(全文検索)のヒットを primary の順位(同点は平均順位)で先に並べ、その後ろに fallback(SigLIP2)の順で
    /// primary にまだ入っていない画像を並べる。結果は全画像が Present な経路で、primary のヒットは primary の平均順位と同じ順位(同点は同点のまま)、
    /// 残りは「primary のヒット数 + fallback の順(primary に入った画像を除く)」の順位になる。fallback の同点は考えない(SigLIP2 は同点なし)ので、同点は画像の番号順。
    /// </summary>
    public static RankedPath Cascade(RankedPath primary, RankedPath fallback)
    {
        int n = primary.Count;
        if (fallback.Count != n) throw new ArgumentException("経路ごとの画像数が違う");
        if (fallback.Present.Any(p => !p)) throw new ArgumentException("fallback は全画像がヒットする経路でなければならない(順位の穴が出る)", nameof(fallback));
        var score = new double[n];
        for (int i = 0; i < n; i++) if (primary.Present[i]) score[i] = -primary.MeanRank[i];
        int k = primary.HitCount;
        foreach (int i in fallback.Order)
        {
            if (primary.Present[i]) continue;
            k++;
            score[i] = -k;
        }
        return new RankedPath(score, Enumerable.Repeat(true, n).ToArray());
    }

    /// <summary>平均順位(ヒットしていなければ null)。</summary>
    public double? MeanRankOf(int image) => Present[image] ? MeanRank[image] : null;

    /// <summary>上位 n 件の切り出しが同点の塊をまたぐか(n 位と n+1 位が同点)。またぐときの切り方は画像の番号順(表示の順のため)で、意味のある順ではない。</summary>
    public bool TieStraddles(int n) => n > 0 && Order.Length > n && Score[Order[n - 1]] == Score[Order[n]];

    /// <summary>
    /// RRF(Reciprocal Rank Fusion)。各経路で Present な画像について 1/(k + 順位) を足す。k は引数(段階3では 60 固定)。
    /// 入力の経路の順位は mode で選ぶ(既定は平均順位。ヒットなしの経路は寄与 0)。
    /// </summary>
    public static RankedPath Rrf(IReadOnlyList<RankedPath> paths, int k, TieMode mode = TieMode.Mean)
    {
        if (paths.Count == 0) throw new ArgumentException("経路が無い", nameof(paths));
        if (k < 0) throw new ArgumentOutOfRangeException(nameof(k));
        int n = paths[0].Count;
        if (paths.Any(p => p.Count != n)) throw new ArgumentException("経路ごとの画像数が違う", nameof(paths));
        var score = new double[n];
        var present = new bool[n];
        foreach (RankedPath p in paths)
        {
            for (int i = 0; i < n; i++)
            {
                if (!p.Present[i]) continue;
                present[i] = true;
                score[i] += 1.0 / (k + p.RankBy(i, mode));
            }
        }
        return new RankedPath(score, present);
    }
}

/// <summary>precision@10(qrels による)。上位 10 件のうち、当たり・外れ(判定済み)・未判定を数える。経路がヒットを 10 件返せなければ、足りない分は外れ(分母は常に 10)。</summary>
public static class Stage3Precision
{
    public sealed record PerQuery(string Query, int Relevant, int JudgedMiss, int Unjudged, int Returned);

    public sealed record Summary(int Queries, int Relevant, int JudgedMiss, int Unjudged, int Returned, IReadOnlyList<PerQuery> PerQuery)
    {
        public int Slots => Queries * PrecisionJudgement.TopCount;
        /// <summary>平均の precision@10(未判定は外れとして数える。= 下限)。</summary>
        public double MeanLower => Slots == 0 ? 0 : (double)Relevant / Slots;
        /// <summary>未判定がすべて当たりだったときの平均(= 上限)。</summary>
        public double MeanUpper => Slots == 0 ? 0 : (double)(Relevant + Unjudged) / Slots;
    }

    /// <param name="perQuery">クエリ(重複なし)ごとの、上位の画像のファイル名(最大 10 件。順位順)。</param>
    public static Summary Compute(IEnumerable<(string Query, IReadOnlyList<string> Top)> perQuery, Qrels qrels)
    {
        var list = new List<PerQuery>();
        foreach (var (q, top) in perQuery)
        {
            if (top.Count > PrecisionJudgement.TopCount) throw new ArgumentException($"「{q}」の上位が {PrecisionJudgement.TopCount} 件を超えている");
            int rel = 0, miss = 0, unj = 0;
            foreach (string f in top)
            {
                bool? j = qrels.TryGet(q, f);
                if (j is null) unj++; else if (j.Value) rel++; else miss++;
            }
            list.Add(new PerQuery(q, rel, miss, unj, top.Count));
        }
        return new Summary(list.Count, list.Sum(p => p.Relevant), list.Sum(p => p.JudgedMiss), list.Sum(p => p.Unjudged), list.Sum(p => p.Returned), list);
    }
}

/// <summary>
/// 段階3の判定(ADR 0033・0037 の規則をそのまま)。比較は整数で行う(浮動小数点の境界誤差を避ける)。
/// <list type="bullet">
/// <item>統合で precision@10 ≥ 0.7 かつ known-item の上位20位以内 ≥ 2/3 → 製品の検索は成り立つ(ADR 0011 の方針は維持。しきい値と k は本実装で決める)</item>
/// <item>統合で precision@10 &lt; 0.4 → 日本語 CLIP を Python だけで同じ tsv で測り、説明文のテキスト埋め込みも試す</item>
/// <item>その間 → 経路ごとの寄与で決める。SigLIP2 を外しても統合の precision@10 の下がりが 0.05 未満で、SigLIP2 だけが拾う known-item が3件未満 →
///   経路2は「関連度低」として下に分け、主役は全文検索にする。それ以上 → 両方残して調整する</item>
/// </list>
/// 読みの注意: 「precision@10 ≥ 0.7 だが known-item が 2/3 に届かない」は、第1の規則(両方が必要)にも第2の規則(&lt; 0.4)にも当たらないので、「その間」として扱う。
/// </summary>
public static class Stage3Verdict
{
    public sealed record Input(
        int FusedRelevant, int PrecisionQueries,
        int FtsRelevant,
        int FusedTop20, int KnownItemQueries,
        int Top20OnlySigLip2);

    public sealed record Result(string Branch, string Text, string Detail);

    public static Result Decide(Input x)
    {
        if (x.PrecisionQueries <= 0 || x.KnownItemQueries <= 0) throw new ArgumentException("クエリが無い");
        int total = x.FusedRelevant, n = x.PrecisionQueries;
        // 平均 = total / (10 n)。≥ 0.7 ⇔ total ≥ 7n、< 0.4 ⇔ total < 4n
        bool pHigh = total >= 7 * n;
        bool pLow = total < 4 * n;
        bool kHigh = x.FusedTop20 * 3 >= x.KnownItemQueries * 2;
        string detail = $"統合の precision@10 = {total / (10.0 * n):F3}(≥ 0.7: {Yn(pHigh)}、< 0.4: {Yn(pLow)})、統合の known-item の上位20位以内 = {x.FusedTop20}/{x.KnownItemQueries}(≥ 2/3: {Yn(kHigh)})";
        if (pHigh && kHigh)
            return new Result("A", "製品の検索は成り立つ(ADR 0011 の方針は維持。しきい値と k は本実装で決める)", detail);
        if (pLow)
            return new Result("B", "日本語 CLIP を Python だけで同じ tsv で測り、説明文のテキスト埋め込みも試す", detail);

        // その間: SigLIP2 を外したときの下がり = 統合 - 全文検索のみ。< 0.05 ⇔ (統合の当たり - 全文の当たり) / (10 n) < 0.05 ⇔ 2 × 差 < n
        int drop = x.FusedRelevant - x.FtsRelevant;
        bool dropSmall = 2 * drop < n;
        bool onlySmall = x.Top20OnlySigLip2 < 3;
        string detail2 = detail + $"。SigLIP2 を外したときの統合の precision@10 の下がり = {drop / (10.0 * n):F3}(< 0.05: {Yn(dropSmall)})、SigLIP2 だけが拾う known-item = {x.Top20OnlySigLip2} 件(< 3: {Yn(onlySmall)})";
        if (pHigh && !kHigh) detail2 += "。注意: precision@10 は 0.7 以上だが known-item が 2/3 に届かない組み合わせは、規則に明記がないので「その間」として扱った";
        return dropSmall && onlySmall
            ? new Result("C1", "経路2(SigLIP2)は「関連度低」として下に分け、主役は全文検索にする", detail2)
            : new Result("C2", "全文検索と SigLIP2 の両方を残して調整する", detail2);

        static string Yn(bool b) => b ? "はい" : "いいえ";
    }
}
