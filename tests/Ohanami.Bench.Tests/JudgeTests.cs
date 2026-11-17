using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ohanami.Bench;
using Xunit;

namespace Ohanami.Bench.Tests;

/// <summary>qrels の source 列の保持と書き出し、判定ページに載せる画像の選び方、ページの HTML(外部を読まない、エスケープ、書き出す TSV の形)。</summary>
public sealed class JudgeTests
{
    private static string Row(string q, string f, int rel, string src = "precision-page:1") => $"{q}\t{f}\t{rel}\t{src}";

    private static Qrels MakeQrels(params string[] rows) => Ohanami.Bench.Qrels.Parse([Ohanami.Bench.Qrels.Header, .. rows]);

    private static PrecisionRow Prec(string query, string path, params string[] top) => new(query, path, 0, 0, 0, top.Length, top);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoTargets = new Dictionary<string, IReadOnlyList<string>>();

    private static string Url(string f) => "file:///x/" + f;

    // ---------------------------------------------------------------- qrels の source

    [Fact]
    public void Qrels_keeps_the_source_column_and_renders_the_same_file_back()
    {
        string tsv = Ohanami.Bench.Qrels.Header + "\n" + Row("銀髪", "a.png", 1, "precision-page:20261105113812") + "\n" + Row("銀髪", "b.png", 0, "judge-page:20261106101111") + "\n";
        Qrels q = Ohanami.Bench.Qrels.Parse(tsv.Split('\n'));
        Assert.Equal(tsv, q.Render());
        Assert.Equal(["precision-page:20261105113812", "judge-page:20261106101111"], q.Rows.Select(r => r.Source));
    }

    [Fact]
    public void A_duplicate_pair_keeps_the_first_source_and_a_conflicting_judgement_still_throws()
    {
        Qrels q = MakeQrels(Row("q", "a.png", 1, "first"), Row("q", "a.png", 1, "second"));
        Assert.Equal("first", Assert.Single(q.Rows).Source);
        Assert.Throws<InvalidDataException>(() => MakeQrels(Row("q", "a.png", 1), Row("q", "a.png", 0)));
    }

    [Fact]
    public void Render_writes_the_header_and_a_row_with_a_tab_in_it_cannot_even_be_read()
    {
        Qrels q = MakeQrels(Row("q", "a.png", 1, "s"));
        Assert.Equal(Ohanami.Bench.Qrels.Header + "\n" + Row("q", "a.png", 1, "s") + "\n", q.Render());
        // 読み込み側が列数を数えるので、タブ入りの行は読めない(=書き出しまで届かない)ことも確かめる
        Assert.Throws<InvalidDataException>(() => MakeQrels("q\ta\tb.png\t1\ts"));
    }

    // ---------------------------------------------------------------- 画像の選び方

    [Fact]
    public void Only_images_missing_from_qrels_are_listed_per_query_with_their_ranks_in_each_route()
    {
        Qrels qrels = MakeQrels(Row("猫", "judged1.png", 1), Row("猫", "judged2.png", 0));
        var precision = new[]
        {
            Prec("猫", Metrics.Fused, "judged1.png", "new-a.png", "new-b.png"),
            Prec("猫", "visual", "new-b.png", "judged2.png", "new-a.png"),
            Prec("猫", "fulltext", "new-a.png"),
        };
        List<JudgeBlock> blocks = JudgePlan.Build(precision, qrels, NoTargets, Url);

        JudgeBlock b = Assert.Single(blocks);
        Assert.Equal("猫", b.Query);
        Assert.Equal(2, b.AlreadyJudged);
        Assert.Equal(["new-a.png", "new-b.png"], b.Candidates.Select(c => c.File));      // 最も良い順位が先、同じならファイル名順
        Assert.Equal("融合 2位 / 画像 3位 / 全文 1位", b.Candidates[0].Routes);
        Assert.Equal("融合 3位 / 画像 1位", b.Candidates[1].Routes);
        Assert.Equal("file:///x/new-a.png", b.Candidates[0].ImageUrl);
    }

    [Fact]
    public void The_third_path_and_the_fused_with_it_have_their_own_labels_and_a_single_path_page_collects_only_that_path()
    {
        Assert.Equal("3 本目", JudgePlan.Label(ThirdPath.PathId));
        Assert.Equal("融合 + 3 本目", JudgePlan.Label(ThirdPath.Fused3));
        Qrels qrels = MakeQrels();
        // judge --path の絞り込みと同じ: 指定した経路の行だけを渡せば、ほかの経路にしか出ない画像は入らない
        var precision = new[] { Prec("猫", Metrics.Fused, "only-fused.png"), Prec("猫", ThirdPath.Fused3, "f3-a.png", "f3-b.png") };
        List<JudgeBlock> blocks = JudgePlan.Build([.. precision.Where(r => r.Path == ThirdPath.Fused3)], qrels, NoTargets, Url);

        Assert.Equal(["f3-a.png", "f3-b.png"], Assert.Single(blocks).Candidates.Select(c => c.File));
        Assert.Equal("融合 + 3 本目 1位", blocks[0].Candidates[0].Routes);
    }

    [Fact]
    public void A_query_whose_top_images_are_all_judged_gets_no_block_and_a_judgement_under_another_query_does_not_count()
    {
        Qrels qrels = MakeQrels(Row("猫", "a.png", 1), Row("犬", "b.png", 0));
        var precision = new[] { Prec("猫", Metrics.Fused, "a.png"), Prec("犬", Metrics.Fused, "a.png") };
        List<JudgeBlock> blocks = JudgePlan.Build(precision, qrels, NoTargets, Url);
        Assert.Equal("犬", Assert.Single(blocks).Query);                                  // 猫は全部判定済み。犬では a.png は未判定
    }

    [Fact]
    public void Target_images_are_marked_and_file_names_are_compared_in_nfc()
    {
        string nfd = "が.png".Normalize(System.Text.NormalizationForm.FormD);
        string nfc = "が.png";
        var targets = new Dictionary<string, IReadOnlyList<string>> { ["q"] = [nfc] };
        List<JudgeBlock> blocks = JudgePlan.Build([Prec("q", Metrics.Fused, nfd)], MakeQrels(), targets, Url);
        JudgeCandidate c = Assert.Single(Assert.Single(blocks).Candidates);
        Assert.Equal(nfc, c.File);
        Assert.True(c.IsTarget);
    }

    [Fact]
    public void The_page_id_depends_only_on_the_candidates_so_a_rebuilt_identical_page_keeps_its_checks()
    {
        var p = new[] { Prec("q", Metrics.Fused, "a.png", "b.png") };
        string id1 = JudgePlan.PageId(JudgePlan.Build(p, MakeQrels(), NoTargets, Url));
        string id2 = JudgePlan.PageId(JudgePlan.Build(p, MakeQrels(), NoTargets, f => "file:///elsewhere/" + f));
        string id3 = JudgePlan.PageId(JudgePlan.Build(p, MakeQrels(Row("q", "b.png", 1)), NoTargets, Url));
        Assert.Equal(id1, id2);
        Assert.NotEqual(id1, id3);
        Assert.Equal(12, id1.Length);
    }

    // ---------------------------------------------------------------- ページ

    private static string Page(params JudgeBlock[] blocks) => JudgePage.Build(blocks, "判定", "メモ", "pid", "judge-page:20261106101111");

    [Fact]
    public void The_page_is_a_single_file_that_loads_nothing_but_the_local_images()
    {
        string html = Page(new JudgeBlock("霧の中に浮かぶ灯り", ["t.png"], 3, [new JudgeCandidate("a b.png", "file:///C:/img/a%20b.png", "融合 1位", true)]));
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.Ordinal);
        Assert.DoesNotContain("url(", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script src", html, StringComparison.OrdinalIgnoreCase);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<script>"));      // インラインのスクリプトが 1 つだけ
        Assert.Contains("src=\"file:///C:/img/a%20b.png\"", html, StringComparison.Ordinal);
        Assert.Contains("data-source=\"judge-page:20261106101111\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"card isTarget\"", html, StringComparison.Ordinal);
        Assert.Contains("すでに判定済み", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_is_html_encoded_in_text_and_attributes()
    {
        string html = Page(new JudgeBlock("<b>\"x\"&", [], 0, [new JudgeCandidate("a\"><script>.png", "file:///x/a.png", "融合 1位", false)]));
        Assert.DoesNotContain("<b>\"x\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("a\"><script>", html, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<script>"));
        Assert.Contains("&lt;b&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_writes_the_qrels_header_that_Qrels_expects()
    {
        string html = Page(new JudgeBlock("q", [], 0, [new JudgeCandidate("a.png", "file:///x/a.png", "融合 1位", false)]));
        Assert.Contains("var HEADER = 'query_ja\\tfilename\\trelevant\\tsource';", html, StringComparison.Ordinal);   // Qrels.Header と同じ(JS の文字列の中のタブ)
        Assert.DoesNotContain("/*HEADER*/", html, StringComparison.Ordinal);
        // ページが書き出す形の TSV(判定済みの行だけ。チェックなし = 0)は、そのまま qrels に読める
        string exported = Ohanami.Bench.Qrels.Header + "\n" + Row("q", "a.png", 1, "judge-page:20261106101111") + "\n" + Row("q", "b.png", 0, "judge-page:20261106101111") + "\n";
        Qrels q = Ohanami.Bench.Qrels.Parse(exported.Split('\n'));
        Assert.True(q.TryGet("q", "a.png"));
        Assert.False(q.TryGet("q", "b.png"));
    }

    [Fact]
    public void A_query_or_file_name_with_a_tab_or_newline_is_refused_before_the_page_is_built()
    {
        Assert.Throws<ArgumentException>(() => Page(new JudgeBlock("a\tb", [], 0, [new JudgeCandidate("a.png", "u", "r", false)])));
        Assert.Throws<ArgumentException>(() => Page(new JudgeBlock("q", [], 0, [new JudgeCandidate("a\n.png", "u", "r", false)])));
    }
}
