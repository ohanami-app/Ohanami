using System;
using System.Collections.Generic;
using System.Linq;
using Ohanami.Core.Search;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>平均順位の RankedPath と RRF(平均順位だけの形。ADR 0033)。外部データなし。</summary>
public sealed class RankedPathTests
{
    [Fact]
    public void Dense_gives_tied_scores_the_average_of_the_ranks_they_occupy()
    {
        // 0.5 が 3 枚(画像 0, 2, 4)で 2〜4 位を占める → 平均 3。0.9 は 1 位、0.1 は 5 位
        var p = RankedPath.Dense([0.5, 0.9, 0.5, 0.1, 0.5]);
        Assert.Equal([3.0, 1.0, 3.0, 5.0, 3.0], p.MeanRank);
        Assert.Equal([1, 0, 2, 4, 3], p.Order);              // 同点は画像の添字の順
        Assert.All(p.Present, Assert.True);
    }

    [Fact]
    public void A_tie_group_of_even_size_gives_a_half_rank()
    {
        // 4〜43 位を 40 枚で分けるなら全員 23.5
        var scores = new double[50];
        for (int i = 0; i < 50; i++) scores[i] = i < 3 ? 10 - i : (i < 43 ? 1.0 : 0.0);
        var p = RankedPath.Dense(scores);
        Assert.Equal(1.0, p.MeanRank[0]);
        Assert.Equal(3.0, p.MeanRank[2]);
        Assert.Equal(23.5, p.MeanRank[3]);
        Assert.Equal(23.5, p.MeanRank[42]);
        Assert.Equal(47.0, p.MeanRank[43]);                    // 44〜50 位の 7 枚の平均
    }

    [Fact]
    public void Sparse_ranks_only_the_images_in_the_population()
    {
        var p = RankedPath.Sparse(5, [(3, 2.0), (1, 5.0), (4, 2.0)]);
        Assert.Equal(3, p.HitCount);
        Assert.Equal([1, 3, 4], p.Order);
        Assert.Equal([0.0, 1.0, 0.0, 2.5, 2.5], p.MeanRank);   // 3 と 4 は同点で 2〜3 位 → 2.5。母集団外は 0
        Assert.Equal([false, true, false, true, true], p.Present);
        Assert.Equal([1, 3], p.Top(2));
        Assert.Equal([1, 3, 4], p.Top(10));                     // 母集団が足りなければ、その分だけ
        Assert.Throws<InvalidOperationException>(() => RankedPath.Sparse(5, [(1, 1.0), (1, 2.0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RankedPath.Sparse(5, [(5, 1.0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RankedPath.Sparse(5, [(-1, 1.0)]));
    }

    [Fact]
    public void Empty_population_is_allowed()
    {
        var p = RankedPath.Sparse(3, []);
        Assert.Equal(0, p.HitCount);
        Assert.Empty(p.Top(5));
    }

    [Fact]
    public void Rrf_sums_the_reciprocals_of_one_over_k_plus_rank()
    {
        // 経路 A: 画像 0 が 1 位、画像 1 が 2 位(画像 2 は母集団外)。経路 B(全画像): 画像 2 が 1 位、画像 1 が 2 位、画像 0 が 3 位
        var a = RankedPath.Sparse(3, [(0, 9.0), (1, 8.0)]);
        var b = RankedPath.Dense([0.1, 0.5, 0.9]);
        var f = RankedPath.Rrf([a, b], 60);
        Assert.Equal(1.0 / 61 + 1.0 / 63, f.Score[0], 12);
        Assert.Equal(1.0 / 62 + 1.0 / 62, f.Score[1], 12);
        Assert.Equal(1.0 / 61, f.Score[2], 12);                 // 経路 A の母集団外 = 寄与 0
        Assert.Equal([0, 1, 2], f.Order);                       // 0.032266 > 0.032258 > 0.016393
        Assert.Equal([1.0, 2.0, 3.0], f.MeanRank);
        Assert.All(f.Present, Assert.True);
        Assert.Throws<ArgumentException>(() => RankedPath.Rrf([RankedPath.Dense([1.0]), RankedPath.Dense([1.0, 2.0])], 60));
        Assert.Throws<ArgumentException>(() => RankedPath.Rrf([], 60));
    }

    [Fact]
    public void Rrf_uses_the_mean_rank_of_tied_inputs()
    {
        // 経路 A: 画像 0 と 1 が同点で 1〜2 位(平均 1.5)、画像 2 は 3 位、画像 3 は母集団外。経路 B: 2 が 1 位、1 が 2 位、0 が 3 位、3 が 4 位
        var a = RankedPath.Sparse(4, [(0, 5.0), (1, 5.0), (2, 3.0)]);
        var b = RankedPath.Dense([0.1, 0.5, 0.9, 0.05]);
        var f = RankedPath.Rrf([a, b], 60);
        Assert.Equal(1.0 / 61.5 + 1.0 / 63, f.Score[0], 12);
        Assert.Equal(1.0 / 61.5 + 1.0 / 62, f.Score[1], 12);
        Assert.Equal(1.0 / 63 + 1.0 / 61, f.Score[2], 12);
        Assert.Equal(1.0 / 64, f.Score[3], 12);                 // 経路 A に無い = B の分だけ
    }

    [Fact]
    public void Rrf_with_one_path_keeps_that_paths_order_and_population()
    {
        var a = RankedPath.Sparse(4, [(2, 3.0), (0, 2.0), (3, 1.0)]);
        var f = RankedPath.Rrf([a], 60);
        Assert.Equal(a.Order, f.Order);
        Assert.False(f.Present[1]);
        Assert.Equal([2.0, 0.0, 1.0, 3.0], new[] { f.MeanRank[0], f.MeanRank[1], f.MeanRank[2], f.MeanRank[3] });
    }

    [Fact]
    public void Tied_fused_scores_share_the_mean_rank()
    {
        // 画像 0: A で 1 位・B で 2 位、画像 1: A で 2 位・B で 1 位 → 統合のスコアが完全に同じ(足す順に依らない)
        var a = RankedPath.Sparse(3, [(0, 9.0), (1, 8.0)]);
        var b = RankedPath.Sparse(3, [(1, 9.0), (0, 8.0)]);
        var f = RankedPath.Rrf([a, b], 60);
        Assert.Equal(f.Score[0], f.Score[1]);
        Assert.Equal([1.5, 1.5, 0.0], f.MeanRank);
        Assert.Equal([0, 1], f.Order);
    }

    [Fact]
    public void Rrf_k_is_validated_and_the_default_search_k_is_60()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RankedPath.Rrf([RankedPath.Dense([1.0])], -1));
        Assert.Equal(60, SearchService.RrfK);
    }

    [Fact]
    public void Order_is_by_score_descending_then_by_index_for_many_random_scores()
    {
        var rng = new Random(7);
        double[] scores = Enumerable.Range(0, 500).Select(_ => Math.Round(rng.NextDouble(), 2)).ToArray(); // 同点を多く含む
        var p = RankedPath.Dense(scores);
        for (int k = 1; k < p.Order.Length; k++)
        {
            int prev = p.Order[k - 1], cur = p.Order[k];
            Assert.True(scores[prev] > scores[cur] || (scores[prev] == scores[cur] && prev < cur));
            Assert.True(p.MeanRank[prev] <= p.MeanRank[cur]);
        }
        // 平均順位の合計は 1+2+…+n に等しい(同点を平均にしても、順位の総和は変わらない)
        Assert.Equal(500.0 * 501 / 2, p.MeanRank.Sum(), 6);
    }
}
