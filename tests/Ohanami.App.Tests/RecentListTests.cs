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
/// 検索していないときの新しい順の一覧(ADR 0041)の App 側: 起動の直後と検索の欄を空にしたときに出る、検索の結果と同じ一覧・詳細画面で開く、
/// 走査のあとの作り直し(新しい画像が入れば作り直す・並びが同じなら動かさない・検索の結果を出している間は何もしない)、状態の行の文言。Core は偽の関数(RecentFunc・SearchFunc)。
/// </summary>
public sealed class RecentListTests
{
    private static string H(char c) => new(c, 64);

    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static RecentImage Img(char c, int minutes = 0, params string[] paths) =>
        new(H(c), paths.Length == 0 ? [$@"C:\shots\{c}.png"] : paths, T0.AddMinutes(minutes));

    private static RecentResponse Recent(int? total = null, params RecentImage[] images) => new(images, total ?? images.Length, TimeSpan.FromMilliseconds(3));

    private static SearchResponse Found(string query, params char[] hashes) =>
        new(query, hashes.Select((c, i) => new SearchResult(H(c), [$@"C:\shots\{c}.png"], i + 1, 0.5, [])).ToList(), [], hashes.Length, hashes.Length, TimeSpan.FromMilliseconds(5));

    private sealed class Harness
    {
        public ResultsPanel Panel { get; set; } = null!;
        public List<string> Notices { get; } = [];
        public List<int> RecentCalls { get; } = [];
        public List<string> Searches { get; } = [];
        public Queue<RecentResponse> NextRecent { get; } = new();
        public Func<int, CancellationToken, Task<RecentResponse>>? RecentOverride { get; set; }
        public TestLog Log { get; } = new();
    }

    private static Harness NewPanel(params RecentResponse[] recents)
    {
        var h = new Harness();
        foreach (RecentResponse r in recents) h.NextRecent.Enqueue(r);
        h.Panel = new ResultsPanel(h.Log, h.Notices.Add, new FakeShell())
        {
            DisplayZone = TimeZoneInfo.Utc,
            DetailFunc = (hash, _) => Task.FromResult<ImageDetail?>(Detail(hash)),
            ImageFunc = (_, _) => Task.FromResult(ThumbnailResult.Ok(new ThumbnailPixels(4, 2, new byte[32]), fromCache: false)),
            RecentFunc = (limit, ct) =>
            {
                h.RecentCalls.Add(limit);
                if (h.RecentOverride is { } over) return over(limit, ct);
                // 最後の 1 つは、何度呼ばれても返し続ける
                return Task.FromResult(h.NextRecent.Count > 1 ? h.NextRecent.Dequeue() : h.NextRecent.Peek());
            },
            SearchFunc = (q, _) => { h.Searches.Add(q.Text); return Task.FromResult(Found(q.Text, 'x', 'y')); },
        };
        return h;
    }

    private static ImageDetail Detail(string hash) =>
        new(hash, 1000, "png", 4, 2, T0,
            [new ImagePathDetail($@"C:\shots\{hash[0]}.png", false, null, T0, 1000)],
            [new StageDetail(Stage.Embed, StageState.Pending, null, null, 0, null), new StageDetail(Stage.Ocr, StageState.Pending, null, null, 0, null), new StageDetail(Stage.Caption, StageState.Pending, null, null, 0, null)],
            null, []);

    private static void Click(ResultsPanel panel) =>
        panel.SearchButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("条件が成り立たない");
            await Task.Delay(10);
        }
    }

    private static string[] Hashes(ResultsPanel p) => p.ResultGrid.Items.Select(i => i.Hash).ToArray();

    // ---------------------------------------------------------------- 一覧が出る

    [Fact]
    public async Task Showing_the_recent_list_puts_the_images_in_the_given_order_with_a_header_that_says_it_is_not_a_search_result()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(null, Img('b', 20), Img('a', 10)));

            await h.Panel.ShowRecentAsync();

            Assert.Equal([H('b'), H('a')], Hashes(h.Panel));
            Assert.Equal([ResultsPanel.RecentLimit], h.RecentCalls);                  // 一覧に上限は設けない(ADR 0041)。検索の 300 とは別
            Assert.Equal("新しい順 (ファイルの更新日時) に 2 枚", h.Panel.HeaderText.Text);
            Assert.Empty(h.Searches);                                                 // 検索は呼ばない(空のクエリは Core が例外にする)
        });
    }

    [Fact]
    public async Task The_recent_list_has_no_limit_so_thousands_of_images_all_show_and_the_header_has_no_of_total_part_while_search_keeps_its_300()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            RecentImage[] many = Enumerable.Range(0, 5000).Select(i => Img((char)(0x100 + i), 100000 - i)).ToArray();   // 300 を大きく超える
            Harness h = NewPanel(Recent(null, many));
            int searchLimit = 0;
            h.Panel.SearchFunc = (q, _) => { searchLimit = q.Limit; return Task.FromResult(Found(q.Text, 'x', 'y')); };

            await h.Panel.ShowRecentAsync();

            Assert.Equal(5000, h.Panel.ResultGrid.ItemCount);                                          // 全部が一覧に入る
            Assert.Equal(H((char)0x100), Hashes(h.Panel)[0]);
            Assert.Equal(H((char)(0x100 + 4999)), Hashes(h.Panel)[4999]);
            Assert.Equal("新しい順 (ファイルの更新日時) に 5000 枚", h.Panel.HeaderText.Text);           // 「(全 M 枚のうち…)」は付けない
            Assert.Equal([ResultsPanel.RecentLimit], h.RecentCalls);

            h.Panel.SearchBox.Text = "猫";
            Click(h.Panel);
            await Eventually(() => searchLimit != 0);
            Assert.Equal(300, searchLimit);                                                            // 検索の結果の上限は、そのまま
        });
    }

    [Fact]
    public async Task The_header_never_mentions_a_total_even_when_the_response_reports_more_images_than_it_lists()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(500, Img('a')));      // TotalImages が一覧より多い応答(上限ありで呼んだ場合の形)

            await h.Panel.ShowRecentAsync();

            Assert.Equal("新しい順 (ファイルの更新日時) に 1 枚", h.Panel.HeaderText.Text);
        });
    }

    [Fact]
    public async Task An_empty_library_shows_the_how_to_start_message_and_no_header()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(0));

            await h.Panel.ShowRecentAsync();

            Assert.Empty(h.Panel.ResultGrid.Items);
            Assert.Equal("", h.Panel.HeaderText.Text);
            Assert.Equal("フォルダを追加して、検索ボックスに言葉を入れてください", h.Panel.ResultGrid.EmptyMessage);
        });
    }

    [Fact]
    public async Task Pressing_search_with_an_empty_box_goes_back_to_the_recent_list_and_a_word_replaces_it()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(null, Img('a', 5), Img('b', 1)));
            await h.Panel.ShowRecentAsync();

            h.Panel.SearchBox.Text = "猫";
            Click(h.Panel);
            await Eventually(() => Hashes(h.Panel).SequenceEqual([H('x'), H('y')]));
            Assert.Equal(["猫"], h.Searches);
            Assert.Contains("「猫」", h.Panel.HeaderText.Text, StringComparison.Ordinal);   // 検索の見出し

            h.Panel.SearchBox.Text = "  ";
            Click(h.Panel);
            await Eventually(() => Hashes(h.Panel).SequenceEqual([H('a'), H('b')]));
            Assert.Equal(["猫"], h.Searches);                                                // 空の欄では検索を呼ばない
            Assert.Equal("新しい順 (ファイルの更新日時) に 2 枚", h.Panel.HeaderText.Text);
            Assert.Equal(2, h.RecentCalls.Count);
        });
    }

    [Fact]
    public async Task Without_a_recent_source_an_empty_box_just_clears_the_list_as_before()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(null, Img('a')));
            h.Panel.RecentFunc = null;
            h.Panel.SearchBox.Text = "x";
            Click(h.Panel);
            await Eventually(() => h.Panel.ResultGrid.ItemCount == 2);

            h.Panel.SearchBox.Text = "";
            Click(h.Panel);
            await Eventually(() => h.Panel.ResultGrid.ItemCount == 0);

            Assert.Empty(h.RecentCalls);
            Assert.Equal("", h.Panel.HeaderText.Text);
        });
    }

    [Fact]
    public async Task A_failure_to_build_the_list_is_shown_with_a_reason_and_does_not_throw()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(0));
            h.RecentOverride = (_, _) => Task.FromException<RecentResponse>(new InvalidOperationException("DB が開けない"));

            await h.Panel.ShowRecentAsync();

            Assert.Empty(h.Panel.ResultGrid.Items);
            Assert.StartsWith("新しい順の一覧を作れませんでした: ", h.Panel.HeaderText.Text, StringComparison.Ordinal);
            Assert.Equal("一覧を作れませんでした", h.Panel.ResultGrid.EmptyMessage);
            Assert.Contains(h.Log.Messages, m => m.StartsWith("ERROR 新しい順の一覧を作れなかった", StringComparison.Ordinal));
        });
    }

    // ---------------------------------------------------------------- 走査のあとの作り直し

    [Fact]
    public async Task A_new_image_found_by_a_scan_rebuilds_the_recent_list_with_it_on_top()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(null, Img('a', 10)), Recent(null, Img('n', 99), Img('a', 10)));
            await h.Panel.ShowRecentAsync();
            Assert.Equal([H('a')], Hashes(h.Panel));

            await h.Panel.RefreshSearch();                         // ScanFinished → MainWindow が呼ぶ

            Assert.Equal([H('n'), H('a')], Hashes(h.Panel));
            Assert.Equal("新しい順 (ファイルの更新日時) に 2 枚", h.Panel.HeaderText.Text);
            Assert.Equal(2, h.RecentCalls.Count);
            Assert.Empty(h.Searches);
        });
    }

    // ---------------------------------------------------------------- 走査のあと、見ている位置と選択を保つ(自動の作り直しで、利用者が見ている最中に、一覧が先頭へ戻ったり、選択が消えたりしないため)

    /// <summary>60 枚(画像 0 が一番新しい)。画像の目印は c = 0x100 + 番号の文字(ハッシュ・パスが互いに違う)。</summary>
    private static RecentImage[] Sixty(params RecentImage[] onTop)
    {
        var list = new List<RecentImage>(onTop);
        for (int i = 0; i < 60; i++) list.Add(Img((char)(0x100 + i), 1000 - i));
        return list.ToArray();
    }

    private static string Hx(int i) => H((char)(0x100 + i));

    private static void Arrange(ResultsPanel panel)
    {
        panel.ResultGrid.Measure(new Size(800, 600));
        panel.ResultGrid.Arrange(new Rect(0, 0, 800, 600));      // 1 行 3 枚、行の高さ 152
        panel.ResultGrid.SetScale(1.0);                          // 列数は、置いた大きさが決まってから数え直す
        Assert.Equal(3, panel.ResultGrid.Columns);
    }

    [Fact]
    public async Task A_new_image_found_by_a_scan_while_scrolled_down_goes_on_top_and_the_view_and_the_selection_stay()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(null, Sixty()), Recent(null, Sixty(Img('N', 5000), Img('M', 4999), Img('L', 4998))));   // 先頭に 3 枚(1 行分)
            await h.Panel.ShowRecentAsync();
            Arrange(h.Panel);
            h.Panel.ResultGrid.Offset = 5 * 152 + 40;
            h.Panel.ResultGrid.SelectByHashes([Hx(16), Hx(41)]);
            double before = h.Panel.ResultGrid.Offset;

            await h.Panel.RefreshSearch();

            Assert.Equal(63, h.Panel.ResultGrid.ItemCount);
            Assert.Equal(H('N'), Hashes(h.Panel)[0]);                              // 新しい画像は一番上
            Assert.Equal(before + 152, h.Panel.ResultGrid.Offset);                  // 見ていた画像は、画面の同じ位置
            Assert.Equal([Hx(16), Hx(41)], h.Panel.ResultGrid.SelectedItems().Select(i => i.Hash).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.Equal("新しい順 (ファイルの更新日時) に 63 枚", h.Panel.HeaderText.Text);
        });
    }

    [Fact]
    public async Task A_new_image_found_by_a_scan_while_looking_at_the_top_keeps_the_top_so_it_can_be_seen()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(null, Sixty()), Recent(null, Sixty(Img('N', 5000))));
            await h.Panel.ShowRecentAsync();
            Arrange(h.Panel);
            Assert.Equal(0, h.Panel.ResultGrid.Offset);

            await h.Panel.RefreshSearch();

            Assert.Equal(0, h.Panel.ResultGrid.Offset);
            Assert.Equal(H('N'), Hashes(h.Panel)[0]);
        });
    }

    [Fact]
    public async Task A_selected_image_that_has_left_the_list_is_dropped_from_the_selection_and_the_others_stay()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            RecentImage[] second = Sixty(Img('N', 5000)).Where(x => x.Hash != Hx(41)).ToArray();    // 41 が消えた(ファイルが無くなった)
            Harness h = NewPanel(Recent(null, Sixty()), Recent(null, second));
            await h.Panel.ShowRecentAsync();
            Arrange(h.Panel);
            h.Panel.ResultGrid.Offset = 5 * 152;
            h.Panel.ResultGrid.SelectByHashes([Hx(16), Hx(41)]);

            await h.Panel.RefreshSearch();

            Assert.Equal([Hx(16)], h.Panel.ResultGrid.SelectedItems().Select(i => i.Hash).ToArray());
        });
    }

    [Fact]
    public async Task Coming_back_to_the_recent_list_by_emptying_the_box_starts_from_the_top_with_nothing_selected()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(null, Sixty()));
            await h.Panel.ShowRecentAsync();
            Arrange(h.Panel);
            h.Panel.ResultGrid.Offset = 5 * 152 + 40;
            h.Panel.ResultGrid.SelectByHashes([Hx(16)]);

            await h.Panel.ShowRecentAsync();                       // 検索の欄を空にして Enter / 「検索」: 先頭から

            Assert.Equal(0, h.Panel.ResultGrid.Offset);
            Assert.Equal(0, h.Panel.ResultGrid.SelectedCount);
        });
    }

    [Fact]
    public async Task An_open_detail_stays_open_and_the_view_underneath_is_kept_when_a_scan_adds_an_image()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(null, Sixty()), Recent(null, Sixty(Img('N', 5000))));
            await h.Panel.ShowRecentAsync();
            Arrange(h.Panel);
            h.Panel.ResultGrid.Offset = 5 * 152 + 40;
            double before = h.Panel.ResultGrid.Offset;
            await h.Panel.OpenDetailAsync(Hx(20));
            Assert.Equal("21 / 60", h.Panel.DetailViewForTests.PositionText);

            await h.Panel.RefreshSearch();

            Assert.True(h.Panel.IsDetailOpen);
            Assert.Equal(Hx(20), h.Panel.DetailHash);
            Assert.Equal("22 / 61", h.Panel.DetailViewForTests.PositionText);      // 前後の移動と位置は、新しい並びで正しい
            Assert.True(h.Panel.DetailViewForTests.PreviousEnabled);
            Assert.True(h.Panel.DetailViewForTests.NextEnabled);
            Assert.Equal(before, h.Panel.ResultGrid.Offset);                         // 1 枚足しただけ(同じ行)。詳細を閉じたときに、見ていた所に戻れる
        });
    }

    [Fact]
    public async Task A_rebuild_with_the_same_list_does_not_touch_the_grid_and_keeps_the_header()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(1, Img('a', 10)), Recent(7, Img('a', 10)));
            await h.Panel.ShowRecentAsync();
            IReadOnlyList<GridItem> before = h.Panel.ResultGrid.Items;

            await h.Panel.RefreshSearch();

            Assert.Same(before, h.Panel.ResultGrid.Items);          // 同じ並び: SetItems を呼ばない(選択・スクロールが動かない)
            Assert.Equal("新しい順 (ファイルの更新日時) に 1 枚", h.Panel.HeaderText.Text);   // 全数(TotalImages)は見出しに出さない
        });
    }

    [Fact]
    public async Task While_search_results_are_shown_a_refresh_searches_again_and_never_builds_the_recent_list()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(null, Img('a')));
            await h.Panel.ShowRecentAsync();
            h.Panel.SearchBox.Text = "猫";
            Click(h.Panel);
            await Eventually(() => Hashes(h.Panel).SequenceEqual([H('x'), H('y')]));
            int recentCalls = h.RecentCalls.Count;

            await h.Panel.RefreshSearch();

            Assert.Equal(recentCalls, h.RecentCalls.Count);          // 検索の結果を出しているときは、同じ語で検索し直す
            Assert.Equal(["猫", "猫"], h.Searches);
        });
    }

    [Fact]
    public async Task A_refresh_does_not_overtake_a_search_the_user_is_waiting_for()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(null, Img('a')));
            await h.Panel.ShowRecentAsync();
            var slow = new TaskCompletionSource<SearchResponse>();
            h.Panel.SearchFunc = (q, _) => { h.Searches.Add(q.Text); return slow.Task; };
            h.Panel.SearchBox.Text = "猫";
            Click(h.Panel);
            await Eventually(() => h.Searches.Count == 1);
            int recentCalls = h.RecentCalls.Count;

            await h.Panel.RefreshSearch();                           // 検索の結果を待っている間の走査の完了

            Assert.Equal(recentCalls, h.RecentCalls.Count);          // 一覧を作り直さない(待っている検索を潰さない)
            slow.SetResult(Found("猫", 'z'));
            await Eventually(() => Hashes(h.Panel).SequenceEqual([H('z')]));
        });
    }

    [Fact]
    public async Task A_slow_rebuild_overtaken_by_a_search_is_discarded()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(null, Img('a')));
            await h.Panel.ShowRecentAsync();
            var slow = new TaskCompletionSource<RecentResponse>();
            h.RecentOverride = (_, _) => slow.Task;

            Task refresh = h.Panel.RefreshSearch();                  // 遅れる作り直し
            h.Panel.SearchBox.Text = "猫";
            Click(h.Panel);
            await Eventually(() => Hashes(h.Panel).SequenceEqual([H('x'), H('y')]));
            slow.SetResult(Recent(null, Img('q', 500)));
            await refresh;

            Assert.Equal([H('x'), H('y')], Hashes(h.Panel));         // 遅れて来た古い一覧で、検索の結果を上書きしない
        });
    }

    // ---------------------------------------------------------------- 詳細画面・ツールチップ

    [Fact]
    public async Task The_detail_opened_from_the_recent_list_says_it_is_not_a_search_result_and_moves_between_neighbours()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(null, Img('c', 30), Img('b', 20), Img('a', 10)));
            await h.Panel.ShowRecentAsync();

            await h.Panel.OpenDetailAsync(H('b'));

            DetailView view = h.Panel.DetailViewForTests;
            Assert.Contains(DetailPresenter.RecentRankLine, view.SideText, StringComparison.Ordinal);
            Assert.DoesNotContain("統合:", view.SideText, StringComparison.Ordinal);       // 順位・類似度は出さない
            Assert.Equal("2 / 3", view.PositionText);
            Assert.True(view.PreviousEnabled);
            Assert.True(view.NextEnabled);
            h.Panel.NavigateDetail(+1);
            await Eventually(() => h.Panel.DetailHash == H('a'));
        });
    }

    [Fact]
    public async Task A_rebuild_keeps_an_open_detail_when_the_image_is_still_listed_and_closes_it_with_a_notice_when_it_is_gone()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(
                Recent(null, Img('b', 20), Img('a', 10)),
                Recent(null, Img('n', 99), Img('b', 20), Img('a', 10)),   // 新しい画像が入った: b は 2 番目から 2 番目へ(n が先頭、b は 2 番目)
                Recent(null, Img('n', 99), Img('a', 10)));                // b が消えた
            await h.Panel.ShowRecentAsync();
            await h.Panel.OpenDetailAsync(H('b'));
            Assert.True(h.Panel.IsDetailOpen);

            await h.Panel.RefreshSearch();
            Assert.True(h.Panel.IsDetailOpen);                         // 開いたまま
            Assert.Equal(H('b'), h.Panel.DetailHash);
            Assert.Equal("2 / 3", h.Panel.DetailViewForTests.PositionText);   // n が先頭に入って、b は 2 番目

            await h.Panel.RefreshSearch();
            Assert.False(h.Panel.IsDetailOpen);                        // 一覧から外れた: 閉じて知らせる
            Assert.Contains(h.Notices, n => n.Contains("一覧から外れました", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task The_tooltip_of_a_recent_item_shows_the_modification_time_and_path_not_a_rank()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Recent(null, Img('a', 5, @"C:\shots\a.png", @"D:\copy\a.png")));
            await h.Panel.ShowRecentAsync();

            string? tip = h.Panel.ResultGrid.TooltipOf!(new GridItem(H('a')));

            Assert.Equal("更新: 2026-11-10 12:05\nC:\\shots\\a.png\n(ほか 1 件のパス)\nEnter かダブルクリックで詳細を開けます", tip);
            Assert.DoesNotContain("統合", tip, StringComparison.Ordinal);
        });
    }

    // ---------------------------------------------------------------- 純粋な部分

    [Fact]
    public void The_header_the_tooltip_and_the_sameness_check_are_plain_functions()
    {
        Assert.Equal("", ResultsPanel.BuildRecentHeader(Recent(0)));
        Assert.Equal("新しい順 (ファイルの更新日時) に 300 枚", ResultsPanel.BuildRecentHeader(new RecentResponse(Enumerable.Range(0, 300).Select(i => Img('a', i)).ToArray(), 300, TimeSpan.Zero)));

        var zone = TimeZoneInfo.CreateCustomTimeZone("jst", TimeSpan.FromHours(9), "jst", "jst");
        var r = new SearchResult(H('a'), [@"C:\a.png"], 1, 0, []);
        Assert.StartsWith("更新: 2026-11-10 21:00\n", ResultsPanel.BuildRecentTooltip(r, T0, zone), StringComparison.Ordinal);   // 画面は地域の時刻、分まで

        RecentImage[] a = [Img('a', 1), Img('b', 2)];
        Assert.True(ResultsPanel.SameRecent(a, [Img('a', 1), Img('b', 2)]));
        Assert.False(ResultsPanel.SameRecent(a, [Img('b', 2), Img('a', 1)]));                       // 並びが違う
        Assert.False(ResultsPanel.SameRecent(a, [Img('a', 1)]));                                    // 件数が違う
        Assert.False(ResultsPanel.SameRecent(a, [Img('a', 1), Img('b', 3)]));                       // 更新日時が違う(上書き保存)
        Assert.False(ResultsPanel.SameRecent(a, [Img('a', 1), Img('b', 2, @"C:\moved\b.png")]));    // パスが違う(移動)
    }

    [Fact]
    public void The_presenter_shows_the_recent_note_only_for_the_recent_mode()
    {
        var result = new SearchResult(H('a'), [@"C:\a.png"], 3, 0, []);
        DetailModel recent = DetailPresenter.Build(result, [], DetailLoad.Loading, TimeZoneInfo.Utc, fromSearch: false, fromRecentList: true);
        DetailModel standalone = DetailPresenter.Build(result, [], DetailLoad.Loading, TimeZoneInfo.Utc, fromSearch: false);
        DetailModel search = DetailPresenter.Build(result, [], DetailLoad.Loading, TimeZoneInfo.Utc);

        Assert.Equal([DetailPresenter.RecentRankLine], recent.RankLines);
        Assert.Equal([DetailPresenter.StandaloneRankLine], standalone.RankLines);
        Assert.Equal(["統合: 3 位"], search.RankLines);
    }
}
