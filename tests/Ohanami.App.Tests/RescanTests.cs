using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>STA のスレッドで Dispatcher を回して、async のテストを動かす(await のあとも UI のスレッドに戻る。WPF の要素を、await をまたいで使うテスト用)。</summary>
internal static class StaDispatcher
{
    public static Task RunAsync(Func<Task> test)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await test();
                    tcs.SetResult();
                }
                catch (Exception e)
                {
                    tcs.SetException(e);
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}

public sealed class FolderMatcherTests
{
    private static FolderInfo Folder(long id, string path) => new(id, path, true, 0, null, null);

    [Fact]
    public void A_path_is_matched_to_the_registered_folder_that_contains_it_ignoring_case()
    {
        FolderInfo[] folders = [Folder(1, @"C:\Shots"), Folder(2, @"D:\Games\Screens")];

        Assert.Equal([1L], FolderMatcher.FolderIdsContaining(folders, [@"c:\SHOTS\a\b.png"]));
        Assert.Equal([2L], FolderMatcher.FolderIdsContaining(folders, [@"D:\Games\Screens\x.png"]));
        Assert.Equal([1L, 2L], FolderMatcher.FolderIdsContaining(folders, [@"C:\Shots\a.png", @"D:\Games\Screens\sub\b.png", @"C:\Shots\c.png"]));
    }

    [Fact]
    public void The_comparison_is_on_a_separator_boundary_not_a_string_prefix_and_a_trailing_separator_is_ignored()
    {
        FolderInfo[] folders = [Folder(1, @"C:\a"), Folder(2, @"C:\data\")];

        Assert.Empty(FolderMatcher.FolderIdsContaining(folders, [@"C:\ab\x.png"]));            // C:\a は C:\ab を含まない
        Assert.Equal([1L], FolderMatcher.FolderIdsContaining(folders, [@"C:\a\x.png"]));
        Assert.Equal([2L], FolderMatcher.FolderIdsContaining(folders, [@"C:\data\x.png"]));     // 登録の末尾の区切りは無視
        Assert.Empty(FolderMatcher.FolderIdsContaining(folders, [@"C:\a"]));                    // フォルダそのものは、ファイルではない
    }

    [Fact]
    public void Paths_outside_every_folder_and_redundant_segments_are_handled()
    {
        FolderInfo[] folders = [Folder(1, @"C:\Shots")];

        Assert.Empty(FolderMatcher.FolderIdsContaining(folders, [@"E:\elsewhere\a.png"]));
        Assert.Empty(FolderMatcher.FolderIdsContaining([], [@"C:\Shots\a.png"]));
        Assert.Empty(FolderMatcher.FolderIdsContaining(folders, []));
        Assert.Equal([1L], FolderMatcher.FolderIdsContaining(folders, [@"C:\Shots\sub\..\a.png"]));   // 正規化してから比べる
    }
}

/// <summary>
/// 元のファイルが無くなった画像が見つかったときの、走査し直しと検索のやり直し(動かした画像が、次の起動まで検索に出続けない。ADR 0007)。
/// </summary>
public sealed class RescanTests
{
    private static SearchResult Result(string hash, params string[] paths) => new(hash, paths, 1, 0.5, []);

    private static SearchResponse Response(string query, params SearchResult[] results) => new(query, results, [], results.Length, results.Length, TimeSpan.FromMilliseconds(5));

    private static string Hash(char c) => new(c, 64);

    // ---------------------------------------------------------------- 結果パネル: 見つからない画像の知らせ

    [Fact]
    public async Task Missing_files_found_by_copy_reveal_and_copy_image_are_reported_with_their_candidate_paths_and_existing_files_are_not()
    {
        using var temp = new TempDirectory();
        string present = temp.File("present.png");
        AgedFile.Write(present, [1, 2, 3]);
        string goneA = temp.File("gone-a.png"), goneB = temp.File("gone-b.png");
        await Sta.RunAsync(() =>
        {
            var reported = new List<string[]>();
            var shell = new FakeShell();
            var panel = new ResultsPanel(new TestLog(), _ => { }, shell);
            panel.MissingFilesDetected += paths => reported.Add(paths.ToArray());
            panel.SetResults([Result(Hash('a'), present), Result(Hash('b'), goneA), Result(Hash('c'), goneB, temp.File("also-gone.png"))]);

            // 在るファイルだけ選んでコピー: 知らせない
            panel.ResultGrid.SetItems([new GridItem(Hash('a'))]);
            panel.ResultGrid.SelectAll();
            panel.CopySelectedFiles();
            Assert.Empty(reported);
            Assert.Equal([$"copy-files {present}"], shell.Calls);     // 本物のクリップボードではなく、偽のシェルに渡した

            // 在る 1 つと無い 2 つ: 無い画像のパス(候補を全部)だけを知らせる
            panel.ResultGrid.SetItems([new GridItem(Hash('a')), new GridItem(Hash('b')), new GridItem(Hash('c'))]);
            panel.ResultGrid.SelectAll();
            panel.CopySelectedFiles();
            Assert.Single(reported);
            Assert.Equal(new[] { goneA, goneB, temp.File("also-gone.png") }.OrderBy(p => p, StringComparer.Ordinal), reported[0].OrderBy(p => p, StringComparer.Ordinal));
            Assert.True(panel.BannerVisible);   // 赤い帯の知らせも出る

            // エクスプローラーで表示・画像をコピー: 先頭の選択が無ければ、その画像のパスを知らせる
            reported.Clear();
            panel.ResultGrid.SetItems([new GridItem(Hash('b'))]);
            panel.ResultGrid.SelectAll();
            panel.RevealSelected();
            panel.CopySelectedImage();
            Assert.Equal(2, reported.Count);
            Assert.All(reported, r => Assert.Equal([goneA], r));
            Assert.Equal([$"copy-files {present}", $"copy-files {present}"], shell.Calls.Where(c => c.StartsWith("copy-files", StringComparison.Ordinal)).ToArray());   // 無い画像では、シェルに渡さない(在る 1 つだけが、2 回目のコピーでも渡る)
            Assert.DoesNotContain(shell.Calls, c => c.StartsWith("reveal", StringComparison.Ordinal) || c.StartsWith("copy-image", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task The_grids_drag_report_of_missing_items_becomes_their_paths()
    {
        await Sta.RunAsync(() =>
        {
            var reported = new List<string[]>();
            var panel = new ResultsPanel(new TestLog(), _ => { }, new FakeShell());
            panel.MissingFilesDetected += paths => reported.Add(paths.ToArray());
            panel.SetResults([Result(Hash('a'), @"C:\gone\a.png"), Result(Hash('b'), @"C:\gone\b1.png", @"C:\gone\b2.png")]);

            panel.ResultGrid.MissingFilesDetected!([new GridItem(Hash('a')), new GridItem(Hash('b'))]);

            Assert.Single(reported);
            Assert.Equal([@"C:\gone\a.png", @"C:\gone\b1.png", @"C:\gone\b2.png"], reported[0]);
        });
    }

    // ---------------------------------------------------------------- 結果パネル: 今の検索のやり直し

    [Fact]
    public async Task Refresh_runs_the_last_query_again_replaces_the_results_and_keeps_the_banner()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var panel = new ResultsPanel(new TestLog(), _ => { }, new FakeShell());
            var queries = new List<string>();
            var next = new Queue<SearchResponse>([
                Response("猫", Result(Hash('a'), @"C:\x\a.png"), Result(Hash('b'), @"C:\x\b.png"), Result(Hash('c'), @"C:\x\c.png")),
                Response("猫", Result(Hash('a'), @"C:\x\a.png"), Result(Hash('c'), @"C:\x\c.png")),   // b が消えた
            ]);
            panel.SearchFunc = (q, _) => { queries.Add(q.Text); return Task.FromResult(next.Dequeue()); };
            panel.SearchBox.Text = "  猫 ";
            panel.SearchButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Eventually(() => panel.ResultGrid.ItemCount == 3);
            panel.ShowProblem("元のファイルが無い");

            await panel.RefreshSearch();

            Assert.Equal(["猫", "猫"], queries);                    // 同じ語(trim 済み)で
            Assert.Equal(2, panel.ResultGrid.ItemCount);            // 結果が差し替わった
            Assert.Equal([Hash('a'), Hash('c')], panel.ResultGrid.Items.Select(i => i.Hash).ToArray());
            Assert.True(panel.BannerVisible);                       // 赤い帯の知らせは、そのまま
            Assert.Contains("2 枚", panel.HeaderText.Text, StringComparison.Ordinal);
        });
    }

    // ---------------------------------------------------------------- 走査のあとのやり直しで、見ている位置と選択を保つ(検索の結果。自動の作り直しで、利用者が見ている最中に、一覧が先頭へ戻ったり、選択が消えたりしないため)

    private static string Hx(int i) => Hash((char)(0x100 + i));

    private static SearchResponse Many(string query, IEnumerable<int> ids) =>
        Response(query, ids.Select((id, k) => new SearchResult(Hx(id), [$@"C:\x\{id}.png"], k + 1, 0.5, [])).ToArray());

    /// <summary>「猫」を検索して 60 枚(番号 0〜59)を出し、800 × 600 に置いて(1 行 3 枚、行の高さ 152)、6 行目の途中までスクロールし、16 番と 41 番を選んだ状態にする。</summary>
    private static async Task<ResultsPanel> SearchedAndScrolled(Func<int, SearchResponse> responseOfCall)
    {
        var panel = new ResultsPanel(new TestLog(), _ => { }, new FakeShell());
        int call = 0;
        panel.SearchFunc = (q, _) => Task.FromResult(responseOfCall(call++));
        panel.SearchBox.Text = "猫";
        panel.SearchButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Eventually(() => panel.ResultGrid.ItemCount > 0);
        panel.ResultGrid.Measure(new System.Windows.Size(800, 600));
        panel.ResultGrid.Arrange(new System.Windows.Rect(0, 0, 800, 600));
        panel.ResultGrid.SetScale(1.0);                          // 列数は、置いた大きさが決まってから数え直す
        Assert.Equal(3, panel.ResultGrid.Columns);
        panel.ResultGrid.Offset = 5 * 152 + 40;
        panel.ResultGrid.SelectByHashes([Hx(16), Hx(41)]);
        return panel;
    }

    private static string[] SelectedHashes(ResultsPanel p) => p.ResultGrid.SelectedItems().Select(i => i.Hash).OrderBy(h => h, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task A_refresh_that_adds_an_image_above_the_view_shifts_the_offset_and_one_below_the_view_leaves_it_and_the_selection_stays()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            // 2 回目: 新しい画像 100 〜 102 が 1 位から(見ている所の上。1 行分)  3 回目: 新しい画像 200 が 50 位あたり(見ている所の下)
            ResultsPanel panel = await SearchedAndScrolled(call => call switch
            {
                0 => Many("猫", Enumerable.Range(0, 60)),
                1 => Many("猫", [100, 101, 102, .. Enumerable.Range(0, 57)]),
                _ => Many("猫", [100, 101, 102, .. Enumerable.Range(0, 47), 200, .. Enumerable.Range(47, 10)]),
            });
            double before = panel.ResultGrid.Offset;

            await panel.RefreshSearch();
            Assert.Equal(60, panel.ResultGrid.ItemCount);
            Assert.Equal(Hx(100), panel.ResultGrid.Items[0].Hash);
            Assert.Equal(before + 152, panel.ResultGrid.Offset);                      // 上に 1 行分入った: 見ていた画像は同じ位置
            Assert.Equal([Hx(16), Hx(41)], SelectedHashes(panel));

            double mid = panel.ResultGrid.Offset;
            await panel.RefreshSearch();
            Assert.Equal(mid, panel.ResultGrid.Offset);                               // 下に入った: 動かない
            Assert.Equal([Hx(16), Hx(41)], SelectedHashes(panel));
            Assert.Contains(panel.ResultGrid.Items, i => i.Hash == Hx(200));
        });
    }

    [Fact]
    public async Task A_refresh_with_the_same_results_does_not_move_the_view_or_the_selection()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            ResultsPanel panel = await SearchedAndScrolled(_ => Many("猫", Enumerable.Range(0, 60)));
            double before = panel.ResultGrid.Offset;

            await panel.RefreshSearch();

            Assert.Equal(before, panel.ResultGrid.Offset);
            Assert.Equal([Hx(16), Hx(41)], SelectedHashes(panel));
            Assert.Equal(Enumerable.Range(0, 60).Select(Hx).ToArray(), panel.ResultGrid.Items.Select(i => i.Hash).ToArray());
        });
    }

    [Fact]
    public async Task A_selected_image_that_leaves_the_results_in_a_refresh_is_dropped_from_the_selection_and_the_view_is_kept()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            // 41 のファイルが無くなって、結果から消えた(MissingFilesDetected → 走査し直し → RefreshSearch の場面)
            ResultsPanel panel = await SearchedAndScrolled(call => call == 0 ? Many("猫", Enumerable.Range(0, 60)) : Many("猫", Enumerable.Range(0, 60).Where(i => i != 41)));
            double before = panel.ResultGrid.Offset;

            await panel.RefreshSearch();

            Assert.Equal(59, panel.ResultGrid.ItemCount);
            Assert.Equal([Hx(16)], SelectedHashes(panel));
            Assert.Equal(before, panel.ResultGrid.Offset);                            // 見ていた所より下の画像が消えただけ
        });
    }

    [Fact]
    public async Task Searching_again_by_the_user_even_with_the_same_words_starts_from_the_top_with_nothing_selected()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            ResultsPanel panel = await SearchedAndScrolled(_ => Many("猫", Enumerable.Range(0, 60)));
            Assert.True(panel.ResultGrid.Offset > 0);
            int calls = 0;
            panel.SearchFunc = (q, _) => { calls++; return Task.FromResult(Many("猫", Enumerable.Range(0, 60))); };

            panel.SearchButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));   // 利用者の検索(keepBanner が偽)
            await Eventually(() => calls == 1);
            await Eventually(() => panel.ResultGrid.Offset == 0 && panel.ResultGrid.SelectedCount == 0);   // 先頭から。選択なし

            Assert.Equal(0, panel.ResultGrid.Offset);
            Assert.Equal(0, panel.ResultGrid.SelectedCount);
        });
    }

    [Fact]
    public async Task A_refresh_keeps_an_open_detail_open_with_a_corrected_position_and_the_view_underneath_is_kept()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            ResultsPanel panel = await SearchedAndScrolled(call => call == 0 ? Many("猫", Enumerable.Range(0, 60)) : Many("猫", [100, 101, 102, .. Enumerable.Range(0, 57)]));
            var t0 = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
            panel.DisplayZone = TimeZoneInfo.Utc;
            panel.DetailFunc = (hash, _) => Task.FromResult<ImageDetail?>(new ImageDetail(hash, 1000, "png", 4, 2, t0,
                [new ImagePathDetail($@"C:\x\{hash[0]}.png", false, null, t0, 1000)],
                [new StageDetail(Stage.Embed, StageState.Pending, null, null, 0, null), new StageDetail(Stage.Ocr, StageState.Pending, null, null, 0, null), new StageDetail(Stage.Caption, StageState.Pending, null, null, 0, null)],
                null, []));
            panel.ImageFunc = (_, _) => Task.FromResult(ThumbnailResult.Ok(new ThumbnailPixels(4, 2, new byte[32]), fromCache: false));
            await panel.OpenDetailAsync(Hx(20));
            Assert.Equal("21 / 60", panel.DetailViewForTests.PositionText);
            double before = panel.ResultGrid.Offset;

            await panel.RefreshSearch();                                               // 上に 3 枚(1 行)入った

            Assert.True(panel.IsDetailOpen);                                           // 開いたまま
            Assert.Equal(Hx(20), panel.DetailHash);
            Assert.Equal("24 / 60", panel.DetailViewForTests.PositionText);            // 前後の移動と位置は、新しい並びで正しい
            Assert.True(panel.DetailViewForTests.PreviousEnabled);
            Assert.True(panel.DetailViewForTests.NextEnabled);
            Assert.Equal(before + 152, panel.ResultGrid.Offset);                       // 下の一覧は、見ていた所のまま(詳細を閉じると戻れる)
        });
    }

    [Fact]
    public async Task Refresh_does_nothing_when_nothing_has_been_searched_or_the_query_was_cleared()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var panel = new ResultsPanel(new TestLog(), _ => { }, new FakeShell());
            int calls = 0;
            panel.SearchFunc = (q, _) => { calls++; return Task.FromResult(Response(q.Text, Result(Hash('a'), @"C:\x\a.png"))); };

            await panel.RefreshSearch();
            Assert.Equal(0, calls);                                  // まだ検索していない

            panel.SearchBox.Text = "x";
            panel.SearchButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Eventually(() => calls == 1);
            panel.SearchBox.Text = "";
            panel.SearchButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Eventually(() => panel.ResultGrid.ItemCount == 0);

            await panel.RefreshSearch();
            Assert.Equal(1, calls);                                  // 検索語を消したあとも、やり直さない
        });
    }

    [Fact]
    public async Task A_refresh_that_is_overtaken_by_a_newer_search_is_discarded()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var panel = new ResultsPanel(new TestLog(), _ => { }, new FakeShell());
            var slowRefresh = new TaskCompletionSource<SearchResponse>();
            int call = 0;
            panel.SearchFunc = (q, ct) =>
            {
                call++;
                if (call == 2) return slowRefresh.Task;   // 2 回目(やり直し)は、結果が遅れる
                return Task.FromResult(Response(q.Text, Result(call == 1 ? Hash('a') : Hash('z'), @"C:\x\one.png")));
            };
            panel.SearchBox.Text = "first";
            panel.SearchButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Eventually(() => panel.ResultGrid.ItemCount == 1);

            Task refresh = panel.RefreshSearch();              // 遅れるやり直し
            panel.SearchBox.Text = "second";
            panel.SearchButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));   // 新しい検索が追い越す
            await Eventually(() => panel.ResultGrid.Items.Count > 0 && panel.ResultGrid.Items[0].Hash == Hash('z'));
            slowRefresh.SetResult(Response("first", Result(Hash('q'), @"C:\x\late.png")));
            await refresh;

            Assert.Equal([Hash('z')], panel.ResultGrid.Items.Select(i => i.Hash).ToArray());   // 遅れて来た古いやり直しの結果で、新しい結果を上書きしない
        });
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

    // ---------------------------------------------------------------- 走査パネル: フォルダごとの走査し直し

    [Fact]
    public async Task Rescanning_the_folder_of_a_missing_file_marks_it_missing_scans_only_that_folder_and_signals_the_end()
    {
        using var temp = new TempDirectory();
        string dirA = temp.File("A"), dirB = temp.File("B");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        string fileA = Path.Combine(dirA, "a.png");
        AgedFile.Write(fileA, MakePng("a"));
        AgedFile.Write(Path.Combine(dirB, "b.png"), MakePng("b"));
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            await core.Folders.AddAsync(dirA);
            await core.Folders.AddAsync(dirB);
            await core.Indexer.ScanAsync();
            // 走査のあとで、A の画像を消し、B に新しい画像を足す
            File.Delete(fileA);
            AgedFile.Write(Path.Combine(dirB, "new.png"), MakePng("new"));
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false);
            panel.Attach(core);
            var finished = new List<ScanSummary>();
            panel.ScanFinished += finished.Add;

            await panel.RescanFoldersOfAsync([fileA]);   // A の中のファイルが見つかりませんでした

            Assert.Single(finished);
            Assert.Equal(1, finished[0].Missing);        // A の a.png に missing_since が付いた
            Assert.Equal(0, finished[0].Added);          // B は走査していない(新しい画像はまだ入っていない)
            Assert.Equal(1, core.Folders.List().Single(f => f.Path.EndsWith("B", StringComparison.OrdinalIgnoreCase)).ImageCount);
            Assert.Equal(0, core.Folders.List().Single(f => f.Path.EndsWith("A", StringComparison.OrdinalIgnoreCase)).ImageCount);
        });
    }

    [Fact]
    public async Task A_paths_outside_every_folder_scans_nothing_and_a_rescan_requested_during_a_scan_runs_once_afterwards()
    {
        using var temp = new TempDirectory();
        string dirA = temp.File("A"), dirB = temp.File("B");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        AgedFile.Write(Path.Combine(dirA, "a.png"), MakePng("a"));
        AgedFile.Write(Path.Combine(dirB, "b.png"), MakePng("b"));
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            await core.Folders.AddAsync(dirA);
            await core.Folders.AddAsync(dirB);
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false);
            panel.Attach(core);
            var finished = new List<ScanSummary>();
            panel.ScanFinished += finished.Add;

            await panel.RescanFoldersOfAsync([temp.File(@"elsewhere\x.png")]);
            Assert.Empty(finished);                                    // どのフォルダにも含まれない: 走査しない

            // A の走査の最中に、B の画像が見つからないと分かった: 終わったあとに、B を 1 回だけ走査する
            Task first = panel.RescanFoldersOfAsync([Path.Combine(dirA, "a.png")]);
            await panel.RescanFoldersOfAsync([Path.Combine(dirB, "b.png")]);   // 走査の最中: 待たせて、すぐ戻る
            await panel.RescanFoldersOfAsync([Path.Combine(dirB, "b.png")]);   // 同じフォルダの重複は、1 回にまとまる
            await first;

            Assert.Equal(2, finished.Count);
            Assert.Equal(1, finished[0].Added);   // 最初: A だけ
            Assert.Equal(1, finished[1].Added);   // 次: B だけ(1 回)
            Assert.Equal(1, core.Folders.List().Single(f => f.Path.EndsWith("B", StringComparison.OrdinalIgnoreCase)).ImageCount);
        });
    }

    private static byte[] MakePng(string seed)
    {
        // 走査は中身の形式(PNG のシグネチャ)とハッシュだけを見る。シグネチャ + 種を、画像ごとに違うバイト列にする
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        return sig.Concat(System.Text.Encoding.UTF8.GetBytes("x" + seed)).Concat(Enumerable.Repeat((byte)7, 64)).ToArray();
    }

    private static Task<OhanamiCore> OpenCoreAsync(TempDirectory temp)
    {
        string models = temp.File("no-models");
        Directory.CreateDirectory(models);
        var options = new CoreOptions(temp.File("data/ohanami.db"), temp.File("thumbs"), models, AppContext.BaseDirectory, new TestLog(), WatchFolders: false);
        return OhanamiCore.OpenAsync(options, configureNative: false, time: null, CancellationToken.None);
    }
}
