using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 「なぜ出たのか」の表示(ADR 0038): 理由を言葉にする(ReasonText)、一覧のツールチップと詳細画面の「出た理由」、
/// 理由の行に類似度の数字を出さず、順位の行に置くこと、「全文検索のインデックスがまだありません(VLM が済んでいない、または失敗した)」を結果が出たあとに引くこと、新しい順の一覧・処理状態の一覧から開いた詳細には出さないこと。
/// </summary>
public sealed class ReasonTests
{
    private static string H(char c) => new(c, 64);

    private static PathHit Visual(double rank = 3, double cosine = 0.21) => new("visual", "m", rank, cosine, new VisualDetail(cosine, cosine));

    private static PathHit Text(TextDetail detail, double rank = 1) => new("fulltext", null, rank, 5.5, detail);

    private static TextDetail Matches(TextSpan[]? ocr = null, TextSpan[]? description = null, TextSpan[]? tags = null, TextSpan[]? manual = null, params string[] fuzzy) =>
        new([
            new FieldMatch(TextField.Ocr, "ocr", ocr ?? []),
            new FieldMatch(TextField.Description, "desc", description ?? []),
            new FieldMatch(TextField.Tags, "tags", tags ?? []),
            new FieldMatch(TextField.ManualTags, "manual", manual ?? []),
        ]) { FuzzyTerms = fuzzy };

    private static SearchResult Result(char c, params PathHit[] hits) => new(H(c), [$@"C:\shots\{c}.png"], 2, 0.03, hits);

    private static readonly TextSpan One = new(0, 1);

    private static readonly PathOutcome[] BothOk = [new("visual", "m", 100, PathState.Ok, null), new("fulltext", null, 30, PathState.Ok, null)];

    // ---------------------------------------------------------------- 理由の言葉

    [Fact]
    public void An_image_that_only_the_visual_path_found_is_visually_close()
    {
        Assert.Equal(["特徴ベクトル検索で出ました (SigLIP2 の類似度。文字の一致なし)"], ReasonText.Lines(Result('a', Visual()), noTextIndex: false));
    }

    [Fact]
    public void A_text_match_names_the_fields_that_matched_and_the_visual_rank_is_not_a_reason_then()
    {
        Assert.Equal(["文字が一致 (OCR)"], ReasonText.Lines(Result('a', Visual(), Text(Matches(ocr: [One]))), false));
        Assert.Equal(["文字が一致 (OCR・説明文)"], ReasonText.Lines(Result('a', Visual(), Text(Matches(ocr: [One], description: [One]))), false));
        Assert.Equal(["文字が一致 (説明文)"], ReasonText.Lines(Result('a', Text(Matches(description: [One]))), false));   // 見た目の順位が無くても同じ
    }

    [Fact]
    public void A_tag_match_names_vlm_tags_and_manual_tags_separately_from_a_text_match()
    {
        Assert.Equal(["タグが一致 (VLM のタグ)"], ReasonText.Lines(Result('a', Text(Matches(tags: [One]))), false));
        Assert.Equal(["タグが一致 (手動タグ)"], ReasonText.Lines(Result('a', Text(Matches(manual: [One]))), false));
        Assert.Equal(["タグが一致 (VLM のタグ・手動タグ)"], ReasonText.Lines(Result('a', Text(Matches(tags: [One], manual: [One]))), false));
        Assert.Equal(["文字が一致 (OCR)", "タグが一致 (手動タグ)"], ReasonText.Lines(Result('a', Text(Matches(ocr: [One], manual: [One]))), false));
    }

    [Fact]
    public void A_fuzzy_match_says_the_text_is_similar_and_names_the_words()
    {
        Assert.Equal(["文字が似ています (読み違いかもしれません): 「とろんぷ」"], ReasonText.Lines(Result('a', Visual(), Text(Matches(ocr: [One], fuzzy: "とろんぷ"))), false));
        Assert.Equal(["文字が似ています (読み違いかもしれません): 「とろんぷ」「ねむ」"], ReasonText.Lines(Result('a', Text(Matches(ocr: [One], tags: [One], fuzzy: ["とろんぷ", "ねむ"]))), false));   // 似ている語の行だけ
    }

    [Fact]
    public void A_text_hit_without_ranges_is_still_a_text_match_and_a_result_without_hits_has_no_reason()
    {
        Assert.Equal(["文字が一致"], ReasonText.Lines(Result('a', Text(Matches())), false));    // 一致の範囲を出せませんでした(強調だけ諦めた)
        Assert.Empty(ReasonText.Lines(Result('a'), false));
    }

    [Fact]
    public void An_image_without_a_text_index_yet_says_so_after_the_visual_reason()
    {
        Assert.Equal(["特徴ベクトル検索で出ました (SigLIP2 の類似度。文字の一致なし)", $"{TestWords.TextSearch}のインデックスがまだありません (VLM が済んでいないか、失敗しています)"], ReasonText.Lines(Result('a', Visual()), noTextIndex: true));
        Assert.Equal([$"{TestWords.TextSearch}のインデックスがまだありません (VLM が済んでいないか、失敗しています)"], ReasonText.Lines(Result('a'), noTextIndex: true));
    }

    [Fact]
    public void Reasons_never_use_internal_words()
    {
        string all = string.Join("\n", ReasonText.Lines(Result('a', Visual(), Text(Matches(ocr: [One], tags: [One], manual: [One]))), true)
            .Concat(ReasonText.Lines(Result('b', Visual()), true)));
        foreach (string bad in new[] { "visual", "fulltext", "cosine", "fuzzy", "FTS", "bigram", "hash", "VLM のモデル" })
            Assert.DoesNotContain(bad, all, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- ツールチップ

    [Fact]
    public void The_tooltip_starts_with_the_reasons_and_adds_the_missing_index_line_only_when_asked()
    {
        SearchResult x = Result('a', Visual());

        string without = ResultsPanel.BuildTooltip(x, BothOk);
        string with = ResultsPanel.BuildTooltip(x, BothOk, noTextIndex: true);

        Assert.StartsWith("特徴ベクトル検索で出ました (SigLIP2 の類似度。文字の一致なし)\n統合: 2 位\n", without, StringComparison.Ordinal);
        Assert.StartsWith($"特徴ベクトル検索で出ました (SigLIP2 の類似度。文字の一致なし)\n{TestWords.TextSearch}のインデックスがまだありません (VLM が済んでいないか、失敗しています)\n統合: 2 位\n", with, StringComparison.Ordinal);
        Assert.Contains($"{TestWords.VisualWithModel}: 3 位 (類似度 0.2100)", with, StringComparison.Ordinal);   // 数字はツールチップの奥に残す
    }

    // ---------------------------------------------------------------- 詳細(Presenter)

    private static ImageDetail Detail(char c, CaptionDetail? caption = null, params string[] manual) =>
        new(H(c), 1000, "png", 4, 2, DateTimeOffset.UnixEpoch, [new ImagePathDetail($@"C:\shots\{c}.png", false, null, DateTimeOffset.UnixEpoch, 1000)],
            [new StageDetail(Stage.Embed, StageState.Done, "m", null, 1, null), new StageDetail(Stage.Ocr, StageState.Pending, null, null, 0, null), new StageDetail(Stage.Caption, StageState.Pending, null, null, 0, null)],
            caption, manual);

    private static readonly CaptionDetail SomeCaption = new("m", "o", "d", ["t"], DateTimeOffset.UnixEpoch);

    [Fact]
    public void The_detail_model_has_the_reasons_for_a_search_result_and_none_for_the_other_kinds_of_detail()
    {
        SearchResult x = Result('a', Visual());

        DetailModel search = DetailPresenter.Build(x, BothOk, DetailLoad.Loaded(Detail('a', SomeCaption)), TimeZoneInfo.Utc);
        DetailModel recent = DetailPresenter.Build(x, [], DetailLoad.Loaded(Detail('a', SomeCaption)), TimeZoneInfo.Utc, fromSearch: false, fromRecentList: true);
        DetailModel standalone = DetailPresenter.Build(x, [], DetailLoad.Loaded(Detail('a', SomeCaption)), TimeZoneInfo.Utc, fromSearch: false);

        Assert.Equal(["特徴ベクトル検索で出ました (SigLIP2 の類似度。文字の一致なし)"], search.ReasonLines);
        Assert.Empty(recent.ReasonLines);          // 新しい順の一覧は検索の結果ではない: 理由は出さない
        Assert.Empty(standalone.ReasonLines);      // 処理状態の一覧から開いた詳細も同じ
    }

    [Fact]
    public void The_missing_index_line_comes_from_the_detail_when_it_is_loaded_and_only_while_the_text_path_works()
    {
        SearchResult x = Result('a', Visual());
        DetailModel Of(DetailLoad load, PathOutcome[] paths, bool hint = false) => DetailPresenter.Build(x, paths, load, TimeZoneInfo.Utc, noTextIndexHint: hint);

        Assert.Equal(["特徴ベクトル検索で出ました (SigLIP2 の類似度。文字の一致なし)", $"{TestWords.TextSearch}のインデックスがまだありません (VLM が済んでいないか、失敗しています)"], Of(DetailLoad.Loaded(Detail('a')), BothOk).ReasonLines);          // caption も手動タグも無い
        Assert.Equal(["特徴ベクトル検索で出ました (SigLIP2 の類似度。文字の一致なし)"], Of(DetailLoad.Loaded(Detail('a', SomeCaption)), BothOk).ReasonLines);                                  // caption がある
        Assert.Equal(["特徴ベクトル検索で出ました (SigLIP2 の類似度。文字の一致なし)"], Of(DetailLoad.Loaded(Detail('a', null, "猫")), BothOk).ReasonLines);                                   // 手動タグだけでも、索引に文書はある
        Assert.Equal(["特徴ベクトル検索で出ました (SigLIP2 の類似度。文字の一致なし)"], Of(DetailLoad.Loaded(Detail('a')), [new("visual", "m", 100, PathState.Ok, null), new("fulltext", null, 0, PathState.NotReady, "索引を作り直し中")]).ReasonLines);   // 索引の作り直しの最中は言わない
        // まだ読めていない間は、一覧が引いておいた値(ヒント)を使う
        Assert.Equal(["特徴ベクトル検索で出ました (SigLIP2 の類似度。文字の一致なし)", $"{TestWords.TextSearch}のインデックスがまだありません (VLM が済んでいないか、失敗しています)"], Of(DetailLoad.Loading, BothOk, hint: true).ReasonLines);
        Assert.Equal(["特徴ベクトル検索で出ました (SigLIP2 の類似度。文字の一致なし)"], Of(DetailLoad.Loading, BothOk, hint: false).ReasonLines);
    }

    // ---------------------------------------------------------------- 一覧のパネル

    private sealed class Harness
    {
        public ResultsPanel Panel { get; set; } = null!;
        public List<string> Notices { get; } = [];
        public List<string[]> NoIndexCalls { get; } = [];
        public HashSet<string> NoIndex { get; } = [];
        public Func<Task>? NoIndexGate { get; set; }
        public TestLog Log { get; } = new();
        public List<string> Searches { get; } = [];
        public PathOutcome[] Paths { get; set; } = BothOk;
    }

    private static Harness NewPanel()
    {
        var h = new Harness();
        h.Panel = new ResultsPanel(h.Log, h.Notices.Add, new FakeShell())
        {
            DisplayZone = TimeZoneInfo.Utc,
            DetailFunc = (hash, _) => Task.FromResult<ImageDetail?>(Detail(hash[0], SomeCaption)),
            ImageFunc = (_, _) => Task.FromResult(ThumbnailResult.Ok(new ThumbnailPixels(4, 2, new byte[32]), fromCache: false)),
            SearchFunc = (q, _) =>
            {
                h.Searches.Add(q.Text);
                // 'a' は見た目だけ(索引なし)、'b' は文字で当たった
                SearchResult[] results = [Result('a', Visual()), Result('b', Visual(), Text(Matches(ocr: [One])))];
                return Task.FromResult(new SearchResponse(q.Text, results, h.Paths, 2, 2, TimeSpan.Zero));
            },
            NoTextIndexFunc = async (hashes, _) =>
            {
                h.NoIndexCalls.Add([.. hashes]);
                if (h.NoIndexGate is { } gate) await gate();
                return [.. hashes.Where(h.NoIndex.Contains)];
            },
        };
        h.NoIndex.Add(H('a'));
        return h;
    }

    private static async Task Search(Harness h, string text)
    {
        h.Panel.SearchBox.Text = text;
        h.Panel.SearchButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Eventually(() => h.Panel.ResultGrid.ItemCount > 0 || h.Panel.HeaderText.Text.Length > 0 && !h.Panel.HeaderText.Text.StartsWith("検索中", StringComparison.Ordinal));
    }

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("条件が成り立たない");
            await Task.Delay(10);
        }
    }

    private static string? Tip(Harness h, char c) => h.Panel.ResultGrid.TooltipOf!(new GridItem(H(c)));

    [Fact]
    public async Task After_a_search_the_images_without_a_text_index_are_looked_up_once_and_their_tooltips_say_so()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel();

            await Search(h, "猫");
            await Eventually(() => Tip(h, 'a')!.Contains($"{TestWords.TextSearch}のインデックスがまだありません", StringComparison.Ordinal));

            Assert.Single(h.NoIndexCalls);
            Assert.Equal([H('a'), H('b')], h.NoIndexCalls[0]);                                                    // 結果の全部を、まとめて 1 回で
            Assert.StartsWith($"特徴ベクトル検索で出ました (SigLIP2 の類似度。文字の一致なし)\n{TestWords.TextSearch}のインデックスがまだありません (VLM が済んでいないか、失敗しています)\n統合: 2 位", Tip(h, 'a'), StringComparison.Ordinal);
            Assert.StartsWith("文字が一致 (OCR)\n統合: 2 位", Tip(h, 'b'), StringComparison.Ordinal);               // 文字で当たった画像に、見た目の理由は付かない
        });
    }

    [Fact]
    public async Task The_lookup_is_skipped_when_the_text_path_is_not_working_and_a_failure_only_hides_the_line()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel();
            h.Paths = [new("visual", "m", 100, PathState.Ok, null), new("fulltext", null, 0, PathState.NotReady, $"{TestWords.TextSearch}の索引を作り直し中")];
            await Search(h, "猫");
            Assert.Empty(h.NoIndexCalls);                                                                          // 索引の作り直しの最中は、引かない(「索引が無い」と言うと嘘になる)
            Assert.DoesNotContain("文字の索引", Tip(h, 'a'), StringComparison.Ordinal);

            h.Paths = BothOk;
            h.Panel.NoTextIndexFunc = (_, _) => Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("DB が開けない"));
            await Search(h, "犬");
            await Eventually(() => h.Log.Messages.Any(m => m.StartsWith("WARN 文字の索引がまだ無い画像を調べられなかった", StringComparison.Ordinal)));
            Assert.DoesNotContain("文字の索引", Tip(h, 'a'), StringComparison.Ordinal);                             // 落ちない。理由の行が出ないだけ
            Assert.StartsWith("特徴ベクトル検索で出ました (SigLIP2 の類似度。文字の一致なし)\n統合:", Tip(h, 'a'), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_lookup_that_finishes_after_the_results_changed_is_discarded()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel();
            var gate = new TaskCompletionSource();
            h.NoIndexGate = () => gate.Task;                  // 1 回目の検索の引き込みを止めておく
            await Search(h, "猫");
            await Eventually(() => h.NoIndexCalls.Count == 1);

            h.NoIndexGate = null;
            h.NoIndex.Clear();                                 // 2 回目の検索では、索引のある画像だけ
            await Search(h, "犬");
            await Eventually(() => h.NoIndexCalls.Count == 2);
            gate.SetResult();                                  // 遅れた 1 回目が着く(中身は、止める前の「a は索引なし」)
            await Task.Delay(100);

            Assert.DoesNotContain("文字の索引", Tip(h, 'a'), StringComparison.Ordinal);   // 古い引き込みで、新しい結果の理由を汚さない
        });
    }

    [Fact]
    public async Task The_recent_list_never_shows_a_reason_and_never_looks_up_the_index()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel();
            h.Panel.RecentFunc = (_, _) => Task.FromResult(new RecentResponse([new RecentImage(H('a'), [@"C:\shots\a.png"], DateTimeOffset.UnixEpoch)], 1, TimeSpan.Zero));

            await h.Panel.ShowRecentAsync();

            Assert.Empty(h.NoIndexCalls);
            string tip = Tip(h, 'a')!;
            Assert.StartsWith("更新: ", tip, StringComparison.Ordinal);
            Assert.DoesNotContain("特徴ベクトル検索で出ました (SigLIP2 の類似度。文字の一致なし)", tip, StringComparison.Ordinal);
            await h.Panel.OpenDetailAsync(H('a'));
            Assert.DoesNotContain("出た理由", h.Panel.DetailViewForTests.SideText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task The_detail_shows_the_reasons_and_puts_the_similarity_in_a_tooltip()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel();
            await Search(h, "猫");
            await Eventually(() => Tip(h, 'a')!.Contains($"{TestWords.TextSearch}のインデックスがまだありません", StringComparison.Ordinal));

            await h.Panel.OpenDetailAsync(H('b'));
            DetailView view = h.Panel.DetailViewForTests;

            Assert.Contains("出た理由", view.SideText, StringComparison.Ordinal);
            Assert.Contains("文字が一致 (OCR)", view.SideText, StringComparison.Ordinal);
            Assert.Contains("類似度 0.2100", view.SideText, StringComparison.Ordinal);   // 類似度は本文に出す
            Assert.Contains("割合ではありません", view.TooltipOfLine($"{TestWords.VisualWithModel}: 3 位 (類似度 0.2100)"), StringComparison.Ordinal);

            // 詳細を読めたあとは、画像の記録(caption がある)から決める: 'a' の詳細は caption ありなので「索引が無い」の行は出ない
            await h.Panel.OpenDetailAsync(H('a'));
            Assert.Contains("特徴ベクトル検索で出ました (SigLIP2 の類似度。文字の一致なし)", view.SideText, StringComparison.Ordinal);
            Assert.DoesNotContain($"{TestWords.TextSearch}のインデックスがまだありません", view.SideText, StringComparison.Ordinal);
        });
    }
}
