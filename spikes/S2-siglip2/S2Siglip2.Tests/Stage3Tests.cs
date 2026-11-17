using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace S2Siglip2.Tests;

/// <summary>段階3の部品: qrels、経路の順位、RRF、precision@10、判定の規則、qrels の書き出し(ページへの追加)、判定用ページ。</summary>
public class Stage3Tests
{
    // ---------- qrels ----------

    private static string Q(params string[] rows) => Qrels.Header + "\n" + string.Join("\n", rows) + "\n";

    [Fact]
    public void Qrels_Parse_ReadsRows_AndSkipsRepeatedHeaderAndBlankLines()
    {
        var q = Qrels.Parse((Q("黒髪\ta.png\t1\tp1", "黒髪\tb.png\t0\tp1") + "\n" + Q("森\tc.png\t1\tp2")).Split('\n'));
        Assert.Equal(3, q.Count);
        Assert.True(q.TryGet("黒髪", "a.png"));
        Assert.False(q.TryGet("黒髪", "b.png"));
        Assert.Null(q.TryGet("黒髪", "c.png"));          // 未判定は null(外れとは別)
        Assert.Equal(new[] { "黒髪", "森" }, q.Queries.ToArray());
    }

    [Fact]
    public void Qrels_Parse_SameRowTwice_IsMerged_ButConflictThrows()
    {
        Assert.Equal(1, Qrels.Parse(Q("黒髪\ta.png\t1\tp1", "黒髪\ta.png\t1\tp2").Split('\n')).Count);
        Assert.Throws<InvalidDataException>(() => Qrels.Parse(Q("黒髪\ta.png\t1\tp1", "黒髪\ta.png\t0\tp2").Split('\n')));
    }

    [Theory]
    [InlineData("黒髪\ta.png\t2\tp")]       // relevant が 1 か 0 でない
    [InlineData("黒髪\ta.png\t\tp")]        // relevant が空
    [InlineData("黒髪\ta.png\t1")]          // 列が足りない
    [InlineData("黒髪\ta.png\t1\tp\tx")]    // 列が多い
    [InlineData("\ta.png\t1\tp")]           // query が空
    [InlineData("黒髪\t\t1\tp")]            // filename が空
    [InlineData("黒髪\ta.png\t1\t")]        // source が空
    public void Qrels_Parse_BadRows_Throw(string row) => Assert.Throws<InvalidDataException>(() => Qrels.Parse(Q(row).Split('\n')));

    [Fact]
    public void Qrels_Parse_WrongHeader_Throws() =>
        Assert.Throws<InvalidDataException>(() => Qrels.Parse(["query_ja\trelevant_count", "黒髪\t1"]));

    [Fact]
    public void Qrels_FileNames_AreComparedInNfc()
    {
        // 濁点を結合文字で書いた「ガ」(U+30AB U+3099)と合成済みの「ガ」(U+30AC)は、同じ画像として扱う
        string decomposed = "\u30AB\u3099.png", composed = "\u30AC.png";
        var q = Qrels.Parse(Q($"黒髪\t{decomposed}\t1\tp").Split('\n'));
        Assert.True(q.TryGet("黒髪", composed));
        q.Validate(["黒髪"], [composed]);
    }

    [Fact]
    public void Qrels_Validate_UnknownQueryOrImage_Throws()
    {
        var q = Qrels.Parse(Q("黒髪\ta.png\t1\tp", "森\tzzz.png\t0\tp").Split('\n'));
        var e = Assert.Throws<InvalidDataException>(() => q.Validate(["黒髪"], ["a.png"]));
        Assert.Contains("森", e.Message);
        Assert.Contains("zzz.png", e.Message);
    }

    [Fact]
    public void Qrels_CompareWithCounts_DetectsMismatchesAndUnexpectedRows()
    {
        var q = Qrels.Parse(Q(
            string.Join("\n", Enumerable.Range(0, 10).Select(i => $"黒髪\t{i}.png\t{(i < 3 ? 1 : 0)}\tp")),
            string.Join("\n", Enumerable.Range(0, 9).Select(i => $"森\t{i}.png\t0\tp")),   // 9 行しかない
            "花\t0.png\t0\tp").Split('\n'));
        var entries = new List<PrecisionJudgement.Entry> { new("黒髪", 3), new("森", 0), new("花", null), new("町中", 5) };
        var problems = q.CompareWithCounts(entries, 10);
        Assert.Contains(problems, p => p.Contains("森") && p.Contains("9"));          // 行数
        Assert.Contains(problems, p => p.Contains("花") && p.Contains("未判定"));     // 当たりの数の表: 未判定、qrels: 行あり
        Assert.Contains(problems, p => p.Contains("町中"));                           // 当たりの数の表: 5 件当たり、qrels: 行なし
        Assert.DoesNotContain(problems, p => p.Contains("黒髪"));
    }

    [Fact]
    public void Qrels_CompareWithCounts_RowFilterLimitsToFirstSource_ExtraSourceRowsAreIgnored()
    {
        // 黒髪: precision-page 由来 10 行(当たり 3)+ 追加の判定 5 行(当たり 4)。全行だと食い違うが、precision-page の行だけなら一致する
        var q = Qrels.Parse(Q(
            string.Join("\n", Enumerable.Range(0, 10).Select(i => $"黒髪\t{i}.png\t{(i < 3 ? 1 : 0)}\tprecision-page:1")),
            string.Join("\n", Enumerable.Range(10, 5).Select(i => $"黒髪\t{i}.png\t{(i < 14 ? 1 : 0)}\tstage3-judge:2")),
            "森\t0.png\t0\tstage3-judge:2").Split('\n'));
        var entries = new List<PrecisionJudgement.Entry> { new("黒髪", 3) };
        Assert.NotEmpty(q.CompareWithCounts(entries, 10));
        Assert.Empty(q.CompareWithCounts(entries, 10, r => r.Source.StartsWith("precision-page:", StringComparison.Ordinal)));
        // precision-page の行が1件も無いクエリは、行数 0 ≠ 10 として報告される
        var onlyExtra = new List<PrecisionJudgement.Entry> { new("森", 0) };
        Assert.Contains(q.CompareWithCounts(onlyExtra, 10, r => r.Source.StartsWith("precision-page:", StringComparison.Ordinal)), p => p.Contains("森"));
    }

    // ---------- 経路の順位と RRF ----------

    [Fact]
    public void RankedPath_Dense_RanksByStrictlyBetterPlusOne_TiesShareRank()
    {
        var p = RankedPath.Dense([0.5, 0.9, 0.5, 0.1]);
        Assert.Equal([2, 1, 2, 4], p.Rank);                // 同点(0.5)は同じ順位 2、次は 4
        Assert.Equal([1, 0, 2, 3], p.Order);               // 同点は画像の番号順
        Assert.Equal(1, p.RankOf(1));
    }

    [Fact]
    public void RankedPath_Sparse_OnlyHitsHaveRank()
    {
        var p = RankedPath.Sparse(5, [(3, 2.0), (1, 5.0)]);
        Assert.Equal(2, p.HitCount);
        Assert.Equal(1, p.RankOf(1));
        Assert.Equal(2, p.RankOf(3));
        Assert.Null(p.RankOf(0));
        Assert.Equal([1, 3], p.Top(10));                    // ヒットが 10 件に満たなければ、その分だけ
        Assert.Throws<InvalidOperationException>(() => RankedPath.Sparse(5, [(1, 1.0), (1, 2.0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RankedPath.Sparse(5, [(5, 1.0)]));
    }

    [Fact]
    public void Rrf_SumsReciprocalRanks_WithK()
    {
        // 経路A: 画像 0 が 1 位、画像 1 が 2 位(画像 2 はヒットなし)。経路B(全画像): 画像 2 が 1 位、画像 1 が 2 位、画像 0 が 3 位
        var a = RankedPath.Sparse(3, [(0, 9.0), (1, 8.0)]);
        var b = RankedPath.Dense([0.1, 0.5, 0.9]);
        var f = RankedPath.Rrf([a, b], 60);
        Assert.Equal(1.0 / 61 + 1.0 / 63, f.Score[0], 12);
        Assert.Equal(1.0 / 62 + 1.0 / 62, f.Score[1], 12);
        Assert.Equal(1.0 / 61, f.Score[2], 12);              // 経路A にないので、経路B の分だけ
        Assert.Equal([0, 1, 2], f.Order);                    // 画像0 (0.032266) > 画像1 (0.032258) > 画像2 (0.016393)
        Assert.All(f.Present, Assert.True);
        Assert.Throws<ArgumentException>(() => RankedPath.Rrf([RankedPath.Dense([1.0]), RankedPath.Dense([1.0, 2.0])], 60));
    }

    [Fact]
    public void Rrf_WithOnePath_KeepsThatPathsOrder()
    {
        var a = RankedPath.Sparse(4, [(2, 3.0), (0, 2.0), (3, 1.0)]);
        var f = RankedPath.Rrf([a], 60);
        Assert.Equal(a.Order, f.Order);
        Assert.False(f.Present[1]);
    }

    [Fact]
    public void RankedPath_PessimisticRank_CountsSameOrBetterPlusOne_AndTieSize()
    {
        // スコア: 0.5, 0.9, 0.5, 0.1, 0.5 → 楽観 [2,1,2,5,2]、悲観(同じか良い画像の数)[4,1,4,5,4]、同点の塊 {0,2,4} は 3 枚
        var p = RankedPath.Dense([0.5, 0.9, 0.5, 0.1, 0.5]);
        Assert.Equal([2, 1, 2, 5, 2], p.Rank);
        Assert.Equal([4, 1, 4, 5, 4], p.PessimisticRank);
        Assert.Equal([3, 1, 3, 1, 3], p.TieSize);
        // ヒットしていない画像は 0(順位なし)。ヒット同士の同点だけを数える(ヒットなしはスコア 0 でも同点に数えない)
        var s = RankedPath.Sparse(5, [(0, 0.0), (1, 0.0), (2, 1.0)]);
        Assert.Equal([2, 2, 1, 0, 0], s.Rank);
        Assert.Equal([3, 3, 1, 0, 0], s.PessimisticRank);
        Assert.Equal([2, 2, 1, 0, 0], s.TieSize);
    }

    [Fact]
    public void RankedPath_PessimisticRank_EqualsRank_WhenNoTies()
    {
        var p = RankedPath.Dense([0.3, 0.9, 0.1, 0.5]);
        Assert.Equal(p.Rank, p.PessimisticRank);
        Assert.All(p.TieSize, t => Assert.Equal(1, t));
    }

    [Fact]
    public void Rrf_PessimisticInputs_UsesPessimisticRanks()
    {
        // 経路A: 画像 0 と 1 が同点(楽観 1 位、悲観 2 位)、画像 2 が 3 位。経路B: 1 位 画像 0、2 位 画像 1、3 位 画像 2
        var a = RankedPath.Sparse(3, [(0, 5.0), (1, 5.0), (2, 3.0)]);
        var b = RankedPath.Dense([0.9, 0.5, 0.1]);
        var opt = RankedPath.Rrf([a, b], 60, TieMode.Optimistic);
        Assert.Equal(1.0 / 61 + 1.0 / 61, opt.Score[0], 12);
        Assert.Equal(1.0 / 61 + 1.0 / 62, opt.Score[1], 12);
        var pess = RankedPath.Rrf([a, b], 60, TieMode.Pessimistic);
        Assert.Equal(1.0 / 62 + 1.0 / 61, pess.Score[0], 12);
        Assert.Equal(1.0 / 62 + 1.0 / 62, pess.Score[1], 12);
        Assert.Equal(1.0 / 63 + 1.0 / 63, pess.Score[2], 12);
    }

    [Fact]
    public void RankedPath_MeanRank_IsAverageOfRanksTheTieGroupOccupies()
    {
        // スコア 0.5 が3枚(画像 0, 2, 4)で 2〜4 位を占める → 平均 3。0.9 は 1 位、0.1 は 5 位
        var p = RankedPath.Dense([0.5, 0.9, 0.5, 0.1, 0.5]);
        Assert.Equal([3.0, 1.0, 3.0, 5.0, 3.0], p.MeanRank);
        Assert.Equal(3.0, p.RankBy(0, TieMode.Mean));
        Assert.Equal(2, p.RankBy(0, TieMode.Optimistic));
        Assert.Equal(4, p.RankBy(0, TieMode.Pessimistic));
        // 偶数枚の塊は .5: 4〜43位を40枚で分けるなら 23.5
        var scores = new double[50];
        for (int i = 0; i < 50; i++) scores[i] = i < 3 ? 10 - i : (i < 43 ? 1.0 : 0.0);
        var q = RankedPath.Dense(scores);
        Assert.Equal(4, q.Rank[3]);
        Assert.Equal(43, q.PessimisticRank[3]);
        Assert.Equal(23.5, q.MeanRank[3]);
        Assert.Equal(40, q.TieSize[3]);
        // ヒットなしは 0 / null
        var s = RankedPath.Sparse(4, [(1, 2.0), (2, 2.0)]);
        Assert.Equal([0.0, 1.5, 1.5, 0.0], s.MeanRank);
        Assert.Null(s.MeanRankOf(0));
        Assert.Equal(1.5, s.MeanRankOf(1));
    }

    [Fact]
    public void Rrf_DefaultUsesMeanRankOfInputs_AbsentPathContributesZero()
    {
        // 経路A: 画像 0 と 1 が同点で 1〜2 位(平均 1.5)、画像 2 は 3 位。経路B(SigLIP2 相当): 画像 2 が 1 位、1 が 2 位、0 が 3 位
        var a = RankedPath.Sparse(4, [(0, 5.0), (1, 5.0), (2, 3.0)]);   // 画像 3 は経路A にヒットなし
        var b = RankedPath.Dense([0.1, 0.5, 0.9, 0.05]);
        var f = RankedPath.Rrf([a, b], 60);
        Assert.Equal(1.0 / 61.5 + 1.0 / 63, f.Score[0], 12);
        Assert.Equal(1.0 / 61.5 + 1.0 / 62, f.Score[1], 12);
        Assert.Equal(1.0 / 63 + 1.0 / 61, f.Score[2], 12);
        Assert.Equal(1.0 / 64, f.Score[3], 12);                         // 経路A にないので B の分だけ(B で 4 位)
        // 平均順位の統合で、画像 1(0.03226...)と画像 2(0.03226...)の順は数値で決まる
        Assert.Equal(f.Score.Select((s, i) => (s, i)).OrderByDescending(t => t.s).ThenBy(t => t.i).Select(t => t.i).ToArray(), f.Order);
    }

    [Fact]
    public void Cascade_PrimaryHitsFirst_ThenFallbackOrderWithoutDuplicates_TiesStayTied()
    {
        // 全文検索: 画像 3 と 1 が同点で 1〜2 位(平均 1.5)、画像 5 が 3 位。ヒットなし: 0, 2, 4
        var fts = RankedPath.Sparse(6, [(3, 9.0), (1, 9.0), (5, 4.0)]);
        // SigLIP2: 順位は 2 > 0 > 5 > 4 > 1 > 3 (スコアの大きい順)
        var sig = RankedPath.Dense([0.8, 0.2, 0.9, 0.1, 0.5, 0.6]);
        var c = RankedPath.Cascade(fts, sig);
        Assert.Equal([2, 0, 4], c.Order.Skip(3).ToArray());              // 4 位以降は SigLIP2 の順(2, 0, 4)。すでに入った 5, 1, 3 は除く
        Assert.Equal(1.5, c.MeanRank[3]);
        Assert.Equal(1.5, c.MeanRank[1]);                                // 全文検索の同点は同点のまま
        Assert.Equal(3.0, c.MeanRank[5]);
        Assert.Equal([4.0, 5.0, 6.0], new[] { c.MeanRank[2], c.MeanRank[0], c.MeanRank[4] });   // ヒット数 3 + SigLIP2 の順
        Assert.All(c.Present, Assert.True);
        // ヒットが 0 件なら SigLIP2 の順そのまま
        var none = RankedPath.Cascade(RankedPath.Sparse(6, []), sig);
        Assert.Equal(sig.Order, none.Order);
        Assert.Equal(sig.Rank, none.Rank);
        // fallback が Sparse(穴がある)なら例外
        Assert.Throws<ArgumentException>(() => RankedPath.Cascade(fts, RankedPath.Sparse(6, [(0, 1.0)])));
    }

    [Fact]
    public void TieStraddles_WhenNthAndNextAreTied()
    {
        var p = RankedPath.Dense([3, 3, 2, 2, 2, 1]);                   // 3位・4位・5位が同点
        Assert.True(p.TieStraddles(3));                                 // 3位と4位が同点
        Assert.True(p.TieStraddles(4));
        Assert.False(p.TieStraddles(2));                                // 2位と3位は別のスコア
        Assert.False(p.TieStraddles(5));                                // 5位と6位は別
        Assert.False(p.TieStraddles(6));                                // 6位の次が無い
        Assert.False(RankedPath.Sparse(3, [(0, 1.0)]).TieStraddles(1));
    }

    [Fact]
    public void KnownItems_EvaluateMean_UsesSameRuleWithFractionalRanks()
    {
        // 20.5 は上位20位以内に入らない / 20.0 は入る(≤ 20)
        Assert.Equal(1, KnownItems.EvaluateMean([20.0, 20.5, 200.0]).Top20);
        Assert.Equal(2, KnownItems.EvaluateMean([20.0, 19.5, 200.0]).Top20);
        Assert.Equal(KnownItems.Evaluate([4, 1, 100]), KnownItems.EvaluateMean([4.0, 1.0, 100.0]));
        var v = KnownItems.EvaluateMean([1.5, 2.5, 3.5, 100.0]);
        Assert.Equal(0, v.Top1);                                        // 1.5 は 1位ではない
        Assert.Equal(3, v.Top5);
        Assert.Equal(3.0, v.MedianRank);
    }

    [Fact]
    public void Rrf_TiedFusedScores_GetSameRank_AndPessimisticRankCountsThem()
    {
        // 画像 0: A で 1 位・B で 2 位、画像 1: A で 2 位・B で 1 位 → 統合のスコアが完全に同じ
        var a = RankedPath.Sparse(3, [(0, 9.0), (1, 8.0)]);
        var b = RankedPath.Sparse(3, [(1, 9.0), (0, 8.0)]);
        var f = RankedPath.Rrf([a, b], 60);
        Assert.Equal(f.Score[0], f.Score[1]);
        Assert.Equal([1, 1, 0], f.Rank);
        Assert.Equal([2, 2, 0], f.PessimisticRank);
    }

    // ---------- precision@10(qrels) ----------

    [Fact]
    public void Precision_CountsRelevantJudgedMissAndUnjudged_FewerThanTenReturnedCountsAsMiss()
    {
        var qrels = Qrels.Parse(Q("黒髪\ta.png\t1\tp", "黒髪\tb.png\t0\tp", "森\tc.png\t1\tp").Split('\n'));
        var s = Stage3Precision.Compute(
        [
            ("黒髪", ["a.png", "b.png", "x.png"]),   // 当たり 1、外れ 1、未判定 1(返したのは 3 件)
            ("森", ["c.png"]),                       // 当たり 1(返したのは 1 件。残り 9 枠は外れ)
        ], qrels);
        Assert.Equal(2, s.Queries);
        Assert.Equal(2, s.Relevant);
        Assert.Equal(1, s.JudgedMiss);
        Assert.Equal(1, s.Unjudged);
        Assert.Equal(4, s.Returned);
        Assert.Equal(20, s.Slots);
        Assert.Equal(2 / 20.0, s.MeanLower, 12);          // 未判定は外れ
        Assert.Equal(3 / 20.0, s.MeanUpper, 12);          // 未判定が全部当たりなら
        Assert.Equal(3, s.PerQuery.Count == 2 ? s.PerQuery[0].Returned : -1);
    }

    [Fact]
    public void Precision_MoreThanTenInTop_Throws() =>
        Assert.Throws<ArgumentException>(() => Stage3Precision.Compute([("q", Enumerable.Range(0, 11).Select(i => $"{i}.png").ToList())], Qrels.Parse([Qrels.Header])));

    // ---------- 判定の規則 ----------

    // 39 クエリ・45 件を基準にする(平均 ≥ 0.7 ⇔ 当たり合計 ≥ 7 × 39 = 273、< 0.4 ⇔ < 156)

    [Theory]
    [InlineData(273, 30, "A")]   // ちょうど 0.7 かつ 30/45 = 2/3 ちょうど → 成り立つ(境界は「以上」)
    [InlineData(272, 45, "C")]   // 0.7 未満(0.6974)。0.4 以上なので「その間」
    [InlineData(155, 45, "B")]   // 0.4 未満(0.3974)
    [InlineData(156, 45, "C")]   // ちょうど 0.4 は「その間」
    [InlineData(300, 29, "C")]   // precision は高いが known-item が 2/3 未満 → 規則に明記がないので「その間」
    public void Verdict_Boundaries(int fusedRelevant, int fusedTop20, string branchPrefix)
    {
        var r = Stage3Verdict.Decide(new Stage3Verdict.Input(fusedRelevant, 39, 0, fusedTop20, 45, 0));
        Assert.StartsWith(branchPrefix, r.Branch);
    }

    [Fact]
    public void Verdict_Between_SplitsBySigLip2Contribution()
    {
        // 統合の当たり 200(0.513)。全文検索のみ 190 → 下がり 10 枠 = 10 / 390 = 0.0256 < 0.05(2 × 10 < 39)
        var small = Stage3Verdict.Decide(new Stage3Verdict.Input(200, 39, 190, 20, 45, 2));
        Assert.Equal("C1", small.Branch);
        // SigLIP2 だけが拾う known-item が 3 件 → 3 未満でない → 両方残す
        Assert.Equal("C2", Stage3Verdict.Decide(new Stage3Verdict.Input(200, 39, 190, 20, 45, 3)).Branch);
        // 下がりがちょうど 0.05 以上: 差 20 枠 = 20 / 390 = 0.0513(2 × 20 ≥ 39)
        Assert.Equal("C2", Stage3Verdict.Decide(new Stage3Verdict.Input(200, 39, 180, 20, 45, 0)).Branch);
        // 差 19 枠 = 0.0487 < 0.05
        Assert.Equal("C1", Stage3Verdict.Decide(new Stage3Verdict.Input(200, 39, 181, 20, 45, 0)).Branch);
    }

    // ---------- qrels の書き出しを既存の precision-page.html に足す ----------

    private static string SamplePage()
    {
        var blocks = new List<PrecisionPage.QueryBlock>
        {
            new("黒髪", [("t.png", 3)], [new PrecisionPage.Hit(1, "a.png", 0.2, "file:///a.png", false), new PrecisionPage.Hit(2, "t.png", 0.19, "file:///t.png", true)]),
            new("森", [("u.png", 40)], [new PrecisionPage.Hit(1, "b.png", 0.3, "file:///b.png", false)]),
        };
        return PrecisionPage.Build(blocks, "テスト", "メモ", "20260101000000");
    }

    [Fact]
    public void QrelsExport_Patch_KeepsPageIdAndOriginalScript_AndAddsSectionOnce()
    {
        string html = SamplePage();
        string patched = QrelsExport.Patch(html);
        Assert.Contains("data-page=\"20261105113812\"".Replace("20261105113812", "20260101000000"), patched);
        Assert.Contains("precision10:", patched);                       // 元の保存の鍵の式はそのまま
        Assert.Contains(QrelsExport.Marker, patched);
        Assert.Contains("precision-page:", patched);                    // source 列
        Assert.Contains("query_ja\\tfilename\\trelevant\\tsource", patched);
        // 元のページの内容(最後の </script> より前)は、足した部分を除いてそのまま残っている
        int cut = html.LastIndexOf("</script>", StringComparison.Ordinal);
        Assert.StartsWith(html[..(cut + "</script>".Length)], patched);
        Assert.EndsWith("</body>\n</html>", patched.TrimEnd());
        Assert.Throws<InvalidOperationException>(() => QrelsExport.Patch(patched));      // 二重に足さない
    }

    [Fact]
    public void QrelsExport_Patch_RejectsPagesOfOtherShapes()
    {
        Assert.Throws<InvalidDataException>(() => QrelsExport.Patch("<html><body>x</body></html>"));
        // 別のページ(段階3の判定用)にも足さない: 保存の鍵が precision10: でない
        var other = QrelsPage.Build([new QrelsPage.QueryBlock("q", [], 0, [new QrelsPage.Candidate("a.png", "file:///a.png", "r", false)])], "t", "n", "id", "src");
        Assert.Throws<InvalidDataException>(() => QrelsExport.Patch(other));
    }

    // ---------- 段階3の判定用ページ ----------

    [Fact]
    public void QrelsPage_Build_HasCheckboxesPerCandidate_ExportScript_AndNoExternalResources()
    {
        var html = QrelsPage.Build(
        [
            new QrelsPage.QueryBlock("黒髪 <ロング>", ["t.png"], 4, [
                new QrelsPage.Candidate("a&b.png", "file:///C:/x/a%26b.png", "統合 1位 / 全文 2位 / 画像 5位", true),
                new QrelsPage.Candidate("c.png", "file:///C:/x/c.png", "統合 2位 / 全文 ヒットなし / 画像 1位", false)]),
        ], "題", "メモ", "20260101000000", "stage3-judge:20260101000000");
        Assert.Equal(2, CountOf(html, "<input type=\"checkbox\" class=\"hit\""));
        Assert.Contains("data-query=\"黒髪 &lt;ロング&gt;\"", html);          // HTML エスケープ
        Assert.Contains("data-file=\"a&amp;b.png\"", html);
        Assert.Contains("data-source=\"stage3-judge:20260101000000\"", html);
        Assert.Contains("すでに判定済み", html);
        Assert.Contains("query_ja\\tfilename\\trelevant\\tsource", html);
        Assert.Contains("qrels-judge:", html);                                // 保存の鍵(precision10: とは別)
        Assert.DoesNotContain("http://", html.Replace("http://www.w3.org", ""));
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("<script src", html);
    }

    private static int CountOf(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }
}
