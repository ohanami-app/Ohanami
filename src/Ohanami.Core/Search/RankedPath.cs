using System;
using System.Collections.Generic;
using System.Linq;

namespace Ohanami.Core.Search;

/// <summary>
/// 1 つのクエリに対する、1 つの経路の結果: 画像(添字 0 〜 Count-1)ごとのスコア(大きいほど良い)と、その経路の母集団に入っているか(Present)。
/// 順位は<b>同点の塊が占める順位の平均</b>(<see cref="MeanRank"/>。1 始まり。例: 4〜43 位を 40 枚で分けるなら全員 23.5)。
/// 同点を楽観・悲観のどちらにも寄せない中立の扱い(ADR 0033)。母集団に入っていない画像には順位が無い。
/// 製品に要るのは平均順位だけ(楽観・悲観の順位、Cascade は回帰ベンチ側にあり、Core には入れない)。
/// 画像の添字は呼び出し側(<see cref="SearchService"/>)がクエリごとに作る「ハッシュ → 添字の表」で、ハッシュそのものは持たない。
/// </summary>
internal sealed class RankedPath
{
    public int Count { get; }
    public double[] Score { get; }
    public bool[] Present { get; }

    /// <summary>母集団に入っている画像を、良い順(同点は画像の添字の順)に並べたもの。</summary>
    public int[] Order { get; }

    /// <summary>母集団に入っている画像の平均順位(1 始まり)。入っていなければ 0。</summary>
    public double[] MeanRank { get; }

    private RankedPath(double[] score, bool[] present)
    {
        if (score.Length != present.Length) throw new ArgumentException("スコアと母集団の長さが違う");
        Count = score.Length;
        Score = score;
        Present = present;
        Order = Enumerable.Range(0, Count).Where(i => present[i]).OrderByDescending(i => score[i]).ThenBy(i => i).ToArray();
        MeanRank = new double[Count];
        // 同点(スコアが完全に等しい)の塊 [first, last] ごとに、占める順位 first+1 〜 last+1 の平均
        for (int first = 0; first < Order.Length;)
        {
            int last = first;
            while (last + 1 < Order.Length && score[Order[last + 1]] == score[Order[first]]) last++;
            double mean = (first + 1 + last + 1) / 2.0;
            for (int p = first; p <= last; p++) MeanRank[Order[p]] = mean;
            first = last + 1;
        }
    }

    /// <summary>全画像が母集団に入る経路(見た目の経路のように、全画像にスコアがある)。</summary>
    public static RankedPath Dense(double[] score) => new(score, Enumerable.Repeat(true, score.Length).ToArray());

    /// <summary>一部の画像だけが母集団に入る経路(全文検索のようにヒットした画像だけ)。hits は (画像の添字, スコア)。同じ画像が 2 回あれば例外。</summary>
    public static RankedPath Sparse(int count, IEnumerable<(int Index, double Score)> hits)
    {
        var score = new double[count];
        var present = new bool[count];
        foreach (var (i, s) in hits)
        {
            if (i < 0 || i >= count) throw new ArgumentOutOfRangeException(nameof(hits), $"画像の添字 {i} が範囲外(0〜{count - 1})");
            if (present[i]) throw new InvalidOperationException($"画像 {i} が 2 回入っている");
            present[i] = true;
            score[i] = s;
        }
        return new RankedPath(score, present);
    }

    public int HitCount => Order.Length;

    /// <summary>上位 n 件(母集団が n 件に満たなければ、その分だけ)。</summary>
    public IReadOnlyList<int> Top(int n) => Order.Take(n).ToArray();

    /// <summary>
    /// RRF(Reciprocal Rank Fusion)。各経路で母集団に入っている画像について 1 / (k + 平均順位) を足す(入っていない経路の寄与は 0)。
    /// 結果の順位も同点は平均順位。k は引数(検索では 60 固定。39 クエリで調整すると、その評価に過適合するため)。
    /// </summary>
    public static RankedPath Rrf(IReadOnlyList<RankedPath> paths, int k)
    {
        ArgumentNullException.ThrowIfNull(paths);
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
                score[i] += 1.0 / (k + p.MeanRank[i]);
            }
        }
        return new RankedPath(score, present);
    }
}
