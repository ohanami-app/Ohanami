using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace S2Siglip2.Tests;

/// <summary>precision@10 の判定(TSV の入出力と判定の線)と、判定用 HTML の中身。</summary>
public class PrecisionTests
{
    [Fact]
    public void Template_HasEmptyCounts_AndUniqueQueriesInFirstSeenOrder()
    {
        var rows = new List<KnownItemRow>
        {
            new("a.png", "黒髪", null), new("b.png", "森", null), new("c.png", "黒髪", null), new("d.png", null, null), new("e.png", "メイド", null),
        };
        var q = PrecisionJudgement.UniqueQueries(rows);
        Assert.Equal(new[] { "黒髪", "森", "メイド" }, q);
        Assert.Equal("query_ja\trelevant_count\n黒髪\t\n森\t\nメイド\t\n", PrecisionJudgement.Template(q));
        Assert.Throws<ArgumentException>(() => PrecisionJudgement.Template(["a\tb"]));
    }

    [Fact]
    public void Evaluate_Thresholds_AreSeventyAndForty()
    {
        PrecisionJudgement.Result Eval(params int[] counts) =>
            PrecisionJudgement.Evaluate(counts.Select((c, i) => new PrecisionJudgement.Entry("q" + i, c)).ToList());
        Assert.StartsWith("見た目の経路として使える", Eval(7, 7, 7).Label);   // 平均 0.7 ちょうどは「使える」
        Assert.StartsWith("微妙", Eval(6, 7, 7).Label);                       // 0.667
        Assert.StartsWith("微妙", Eval(4, 4, 4).Label);                       // 0.4 ちょうどは「微妙」
        Assert.StartsWith("使えない", Eval(3, 4, 4).Label);                   // 0.367
        var r = Eval(10, 0, 5);
        Assert.Equal(0.5, r.Mean, 10);
        Assert.Equal((3, 0), (r.Judged, r.Unjudged));
    }

    [Fact]
    public void Evaluate_RefusesUnjudged_UnlessPartialIsAllowed()
    {
        var entries = new List<PrecisionJudgement.Entry> { new("a", 5), new("b", null) };
        Assert.Throws<InvalidDataException>(() => PrecisionJudgement.Evaluate(entries));
        var r = PrecisionJudgement.Evaluate(entries, allowPartial: true);
        Assert.Equal((1, 1, 0.5), (r.Judged, r.Unjudged, r.Mean));
        Assert.Throws<InvalidDataException>(() => PrecisionJudgement.Evaluate([new("a", null)], allowPartial: true)); // 判定済みが1つも無い
    }

    [Fact]
    public void Evaluate_RefusesQueriesThatDoNotMatchKnownItems()
    {
        var entries = new List<PrecisionJudgement.Entry> { new("a", 5), new("x", 5) };
        var e = Assert.Throws<InvalidDataException>(() => PrecisionJudgement.Evaluate(entries, ["a", "b"]));
        Assert.Contains("足りない", e.Message);
        Assert.Contains("余計", e.Message);
    }

    [Theory]
    [InlineData("query_ja\trelevant_count\na\t11\n")]
    [InlineData("query_ja\trelevant_count\na\t-1\n")]
    [InlineData("query_ja\trelevant_count\na\t3.5\n")]
    [InlineData("query_ja\trelevant_count\na\tabc\n")]
    [InlineData("query_ja\trelevant_count\na\t3\ta\n")]
    [InlineData("query_ja\trelevant_count\na\n")]
    [InlineData("query_ja\trelevant_count\na\t1\na\t2\n")]
    [InlineData("query\tcount\na\t1\n")]
    [InlineData("")]
    public void Parse_RejectsMalformedInput(string text)
    {
        Assert.Throws<InvalidDataException>(() => PrecisionJudgement.Parse(text.Split('\n')));
    }

    [Fact]
    public void Parse_AcceptsFilledEmptyAndCrLf()
    {
        var list = PrecisionJudgement.Parse("\uFEFFquery_ja\trelevant_count\r\n黒髪\t7\r\n森\t\r\n\r\n".Split('\n'));
        Assert.Equal(new[] { new PrecisionJudgement.Entry("黒髪", 7), new PrecisionJudgement.Entry("森", null) }, list);
    }

    [Fact]
    public void RepoTemplate_MatchesKnownItems_AndIsStillBlankOrFilledValidly()
    {
        string root = TestFiles.Root;
        var expected = PrecisionJudgement.UniqueQueries(KnownItems.Read(Path.Combine(root, "spikes", "S2-siglip2", "known-items.tsv")));
        var entries = PrecisionJudgement.Read(Path.Combine(root, "spikes", "S2-siglip2", "precision-judgement.tsv"));
        Assert.Equal(expected, entries.Select(e => e.Query).ToList()); // 雛形(または貼った結果)のクエリと順序が known-items.tsv と同じ
    }

    [Fact]
    public void Page_IsSelfContained_EscapesText_AndMarksTargets()
    {
        var top = Enumerable.Range(1, 10).Select(r => new PrecisionPage.Hit(r, $"画像{r}<&>.png", 0.5 - r * 0.01, $"file:///C:/imgs/%E7%94%BB%E5%83%8F{r}.png", r == 3)).ToList();
        var blocks = new List<PrecisionPage.QueryBlock>
        {
            new("森<script>", [("画像3<&>.png", 3)], top),
            new("黒髪", [("遠い.png", 57)], top),
        };
        string html = PrecisionPage.Build(blocks, "題<名>", "メモ&", "id1");
        Assert.DoesNotContain("<script>森", html);                       // クエリの HTML をエスケープしている
        Assert.Contains("森&lt;script&gt;", html);
        Assert.Contains("題&lt;名&gt;", html);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "class=\"card isTarget\"").Count); // 2つのクエリが同じ上位10件を使っているので、目的の画像の枠は2枚
        Assert.Contains("目的の画像が上位10件に入っています", html);
        Assert.Contains("目的の画像が上位10件に無い: 遠い.png(57位)", html);   // 見出しに順位
        Assert.Contains("<textarea id=\"tsv\"", html);
        Assert.Contains("query_ja", html);
        Assert.Contains("data-page=\"id1\"", html);
        // 外部の資源を読まない: http(s):// の参照・<link>・<script src>・@import が無い
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("<script src", html);
        Assert.DoesNotContain("@import", html);
        Assert.Equal(20, System.Text.RegularExpressions.Regex.Matches(html, "<input type=\"checkbox\"").Count);
    }
}
