using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>検索結果の見出し(件数・経路の状態・上位 N 件)と、一覧のツールチップ(出た理由・順位・経路の状態)。</summary>
public sealed class SearchResultTextTests
{
    private static SearchResponse Response(IReadOnlyList<PathOutcome> paths, int results = 3, int indexed = 10, int total = 10) =>
        new("猫 ガチャ", Enumerable.Range(0, results).Select(i => new SearchResult(new string((char)('a' + i), 64), [$"C:\\{i}.png"], i + 1, 0.5, [])).ToList(), paths, indexed, total, TimeSpan.FromMilliseconds(12));

    private static PathOutcome Outcome(string id, PathState state, string? message = null, int population = 10) => new(id, null, population, state, message);

    [Fact]
    public void The_header_shows_the_fulltext_population_and_the_note_about_dropped_terms()
    {
        string text = ResultsPanel.BuildHeader(Response([Outcome("visual", PathState.Ok), Outcome("fulltext", PathState.Ok, "検索できない語を除いた: 、", population: 7)]));

        Assert.StartsWith("「猫 ガチャ」 3 枚 (全部の画像を近い順に) (12 ms)。特徴ベクトル検索の対象 10 / 10 枚", text, StringComparison.Ordinal);
        Assert.Contains($"{TestWords.TextSearch}で一致した画像: 7 枚", text, StringComparison.Ordinal);
        Assert.Contains($"{TestWords.TextSearch}: 検索できない語を除いた: 、", text, StringComparison.Ordinal);
        Assert.DoesNotContain($"{TestWords.VisualWithModel}:", text, StringComparison.Ordinal);   // 注意の無い経路は何も足さない
    }

    [Fact]
    public void The_header_shows_not_ready_failed_and_skipped_fulltext_states()
    {
        Assert.Contains($"{TestWords.TextSearch}は準備中です: {TestWords.TextSearch}のインデックスを作り直しています。しばらくしてから、もう一度検索してください",
            ResultsPanel.BuildHeader(Response([Outcome("fulltext", PathState.NotReady, $"{TestWords.TextSearch}の索引を作り直している")])), StringComparison.Ordinal);
        Assert.Contains($"{TestWords.TextSearch}で検索できませんでした: インデックスが壊れています",
            ResultsPanel.BuildHeader(Response([Outcome("fulltext", PathState.Failed, "索引が壊れている")])), StringComparison.Ordinal);
        string skipped = ResultsPanel.BuildHeader(Response([Outcome("visual", PathState.Ok), Outcome("fulltext", PathState.Skipped, "検索できる語が無い")]));
        Assert.Contains($"{TestWords.TextSearch}は使いませんでした: 検索できる語がありません", skipped, StringComparison.Ordinal);
        Assert.DoesNotContain($"{TestWords.TextSearch}で一致した画像", skipped, StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_says_top_n_only_when_the_limit_cut_the_list_and_otherwise_says_what_the_list_is()
    {
        // 上限(300)で切れたとき: 「上位 300 枚」。特徴ベクトル検索は全部の画像に順位を付けるので、切れたら必ずそうなる
        string cut = ResultsPanel.BuildHeader(Response([Outcome("visual", PathState.Ok), Outcome("fulltext", PathState.Ok)], results: ResultsPanel.SearchLimit, indexed: 1030, total: 1030));
        Assert.StartsWith("「猫 ガチャ」 上位 300 枚 (12 ms)。", cut, StringComparison.Ordinal);
        Assert.DoesNotContain("全部の画像", cut, StringComparison.Ordinal);

        // 切れていなくて、特徴ベクトル検索が使える: 全部の画像を近い順に(済んでいない画像があれば、対象を近い順に)
        Assert.StartsWith("「猫 ガチャ」 3 枚 (全部の画像を近い順に) (12 ms)。", ResultsPanel.BuildHeader(Response([Outcome("visual", PathState.Ok)])), StringComparison.Ordinal);
        Assert.StartsWith("「猫 ガチャ」 3 枚 (特徴ベクトル検索の対象を近い順に) (12 ms)。", ResultsPanel.BuildHeader(Response([Outcome("visual", PathState.Ok)], indexed: 4, total: 10)), StringComparison.Ordinal);

        // 特徴ベクトル検索が準備中: 件数は全文検索で一致した画像だけなので、「上位」とも「全部の画像」とも言わない(新しい嘘になる)
        string notReady = ResultsPanel.BuildHeader(Response([Outcome("visual", PathState.NotReady, "ベクトルを読み込み中"), Outcome("fulltext", PathState.Ok, population: 3)], results: 12, indexed: 0, total: 12));
        Assert.StartsWith("「猫 ガチャ」 12 枚 (12 ms)。", notReady, StringComparison.Ordinal);
        Assert.DoesNotContain("上位", notReady, StringComparison.Ordinal);
        Assert.DoesNotContain("全部の画像", notReady, StringComparison.Ordinal);
        Assert.Contains($"{TestWords.VisualWithModel}は準備中です: 特徴ベクトルを読み込み中。しばらくしてから、もう一度検索してください", notReady, StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_says_how_many_matched_in_fulltext_and_how_many_of_them_are_in_the_list_only_when_they_differ()
    {
        static SearchResult WithText(char c) => new(new string(c, 64), [$"C:\\{c}.png"], 1, 0.5, [new PathHit("fulltext", null, 1, 1.0, new TextDetail([]))]);
        static SearchResponse Of(int population, params SearchResult[] results) =>
            new("猫", results, [Outcome("visual", PathState.Ok), Outcome("fulltext", PathState.Ok, population: population)], 10, 10, TimeSpan.FromMilliseconds(12));

        // 一致 2 枚のうち、一覧に 2 枚とも入った: 「うち m 枚」は足さない
        string same = ResultsPanel.BuildHeader(Of(2, WithText('a'), WithText('b')));
        Assert.Contains($"\n{TestWords.TextSearch}で一致した画像: 2 枚", same, StringComparison.Ordinal);
        Assert.DoesNotContain("一覧には", same, StringComparison.Ordinal);

        // 一致 214 枚のうち、一覧には 1 枚(上限で一致した画像が落ちた): 「(一覧にはうち 1 枚)」
        Assert.Contains($"\n{TestWords.TextSearch}で一致した画像: 214 枚 (一覧にはうち 1 枚)", ResultsPanel.BuildHeader(Of(214, WithText('a'))), StringComparison.Ordinal);

        // 一致の枚数を「対象」とは呼ばない(ADR 0038: 一致した枚数であって、全文検索できる画像の枚数ではない)
        Assert.DoesNotContain("対象: ", same, StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_never_shows_a_path_id_when_a_not_ready_or_failed_path_has_no_reason()
    {
        // Core の準備中の経路は必ず理由の文を付けるが、理由の文が無い(null・空)PathOutcome が来ても、経路の ID(fulltext・visual)を画面に出さない
        string notReady = ResultsPanel.BuildHeader(Response([new PathOutcome("fulltext", null, 0, PathState.NotReady, null), new PathOutcome("visual", "m", 0, PathState.Failed, "")]));
        Assert.Contains($"{TestWords.TextSearch}は準備中です: (理由の記録なし)。しばらくしてから、もう一度検索してください", notReady, StringComparison.Ordinal);
        Assert.Contains($"{TestWords.VisualWithModel}で検索できませんでした: (理由の記録なし)", notReady, StringComparison.Ordinal);
        Assert.DoesNotContain("fulltext", notReady, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("visual", notReady, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_header_marks_images_not_yet_embedded()
    {
        Assert.Contains("(インデックスがまだ無い画像は、特徴ベクトル検索に出ません)", ResultsPanel.BuildHeader(Response([], indexed: 4, total: 10)), StringComparison.Ordinal);
        Assert.DoesNotContain("未処理", ResultsPanel.BuildHeader(Response([])), StringComparison.Ordinal);
    }

    [Fact]
    public void The_tooltip_says_the_reason_in_words_and_keeps_the_ranks_and_the_similarity_below_it()
    {
        var detail = new TextDetail([
            new FieldMatch(TextField.Ocr, "ガチャ10連", [new TextSpan(0, 3)]),
            new FieldMatch(TextField.Description, "説明", []),
            new FieldMatch(TextField.Tags, "猫、犬", [new TextSpan(0, 1)]),
        ]);
        var x = new SearchResult("h", ["C:\\a.png"], 2, 0.0161, [new PathHit("visual", "m", 5, 0.2, new VisualDetail(0.2)), new PathHit("fulltext", null, 1.5, 3.25, detail)]);

        string text = ResultsPanel.BuildTooltip(x);

        Assert.StartsWith("文字が一致 (OCR)\nタグが一致 (VLM のタグ)\n統合: 2 位\n", text, StringComparison.Ordinal);   // 理由(言葉)が先、順位はその下
        Assert.Contains($"{TestWords.TextSearch}: 1.5 位", text, StringComparison.Ordinal);
        Assert.DoesNotContain("一致した欄", text, StringComparison.Ordinal);                                   // 欄は理由の行に出した
        Assert.DoesNotContain("説明文", text, StringComparison.Ordinal);                                        // 一致の無い欄は出ない
        Assert.Contains($"{TestWords.VisualWithModel}: 5 位 (類似度 0.2000)", text, StringComparison.Ordinal);                   // ツールチップの奥には類似度を残す
    }

    [Fact]
    public void The_tooltip_adds_path_states_and_notes_before_the_file_path_and_stays_unchanged_without_them()
    {
        var x = new SearchResult("h", ["C:\\a.png"], 2, 0.5, [new PathHit("visual", "m", 5, 0.2, new VisualDetail(0.2))]);
        string plain = ResultsPanel.BuildTooltip(x);
        Assert.Equal(plain, ResultsPanel.BuildTooltip(x, []));
        Assert.Equal(plain, ResultsPanel.BuildTooltip(x, [Outcome("visual", PathState.Ok), Outcome("fulltext", PathState.Ok)]));   // 状態が Ok で注意が無いなら何も足さない

        string text = ResultsPanel.BuildTooltip(x, [
            Outcome("fulltext", PathState.NotReady, "索引を作り直している"),
            Outcome("visual", PathState.Ok, "注意の例"),
            Outcome("x", PathState.Skipped, "語が無い"),
            Outcome("y", PathState.Failed),
        ]);
        Assert.Contains($"{TestWords.TextSearch}: 準備中 (インデックスを作り直しています)", text, StringComparison.Ordinal);
        Assert.Contains($"{TestWords.VisualWithModel}: 注意 (注意の例)", text, StringComparison.Ordinal);
        Assert.Contains("x: この検索では使いませんでした (語がありません)", text, StringComparison.Ordinal);
        Assert.Contains("y: 失敗", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("準備中", StringComparison.Ordinal) < text.IndexOf("C:\\a.png", StringComparison.Ordinal));   // パスの前
    }
}
