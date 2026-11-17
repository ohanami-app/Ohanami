using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 詳細画面(ResultsPanel の上に重ねる全面の表示): 開く・閉じる・前後・強調の範囲・画像の記録が無いとき・読めなかったとき・画像が出せないとき、
/// 古い読み込みが新しい表示を上書きしない、結果が替わったときの扱い、操作(エクスプローラー・コピー・ドラッグ)が表示中の画像を対象にし、元のファイルが無ければ知らせること。
/// Core は偽の関数(DetailFunc・ImageFunc)。WPF の要素は STA のスレッドで動かす。
/// </summary>
public sealed class DetailViewTests
{
    private static string H(char c) => new(c, 64);

    private static SearchResult Result(char c, double rank, TextDetail? text = null, params string[] paths)
    {
        var hits = new List<PathHit> { new("visual", "m", rank, 0.1, new VisualDetail(0.2)) };
        if (text is not null) hits.Add(new PathHit("fulltext", null, rank, 3, text));
        return new SearchResult(H(c), paths.Length == 0 ? [$@"C:\no-such-dir\{c}.png"] : paths, rank, 0.01, hits);
    }

    private static TextDetail Text(string ocr, params TextSpan[] spans) =>
        new([new FieldMatch(TextField.Ocr, ocr, spans), new FieldMatch(TextField.Description, "", []), new FieldMatch(TextField.Tags, "", []), new FieldMatch(TextField.ManualTags, "", [])]);

    private static ImageDetail Detail(string hash, string? path = null) =>
        new(hash, 1000, "png", 4, 2, new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero),
            [new ImagePathDetail(path ?? $@"C:\no-such-dir\{hash[0]}.png", false, null, new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), 1000)],
            [new StageDetail(Stage.Embed, StageState.Done, "m", null, 1, null), new StageDetail(Stage.Ocr, StageState.Pending, null, null, 0, null), new StageDetail(Stage.Caption, StageState.Pending, null, null, 0, null)],
            null, []);

    private static ThumbnailResult Pixels() => ThumbnailResult.Ok(new ThumbnailPixels(4, 2, new byte[4 * 2 * 4]), fromCache: false);

    private sealed class Harness
    {
        public required ResultsPanel Panel { get; init; }
        public required List<string> Notices { get; init; }
        public required TestLog Log { get; init; }
        public required List<IReadOnlyList<string>> Missing { get; init; }
        public required FakeShell Shell { get; init; }
        public DetailView View => Panel.DetailViewForTests;
    }

    private static Harness NewPanel(IReadOnlyList<SearchResult> results, Func<string, CancellationToken, Task<ImageDetail?>>? detail = null,
        Func<string, CancellationToken, Task<ThumbnailResult>>? image = null)
    {
        var notices = new List<string>();
        var log = new TestLog();
        var shell = new FakeShell();
        var panel = new ResultsPanel(log, notices.Add, shell)
        {
            DetailFunc = detail ?? ((hash, _) => Task.FromResult<ImageDetail?>(Detail(hash))),
            ImageFunc = image ?? ((_, _) => Task.FromResult(Pixels())),
            DisplayZone = TimeZoneInfo.Utc,
        };
        var missing = new List<IReadOnlyList<string>>();
        panel.MissingFilesDetected += missing.Add;
        panel.SetResults(results);
        return new Harness { Panel = panel, Notices = notices, Log = log, Missing = missing, Shell = shell };
    }

    private static SearchResult[] Three() => [Result('a', 1), Result('b', 2), Result('c', 3)];

    // ---------------------------------------------------------------- 開く・閉じる

    [Fact]
    public async Task Opening_shows_the_overlay_with_position_image_and_the_search_result_parts()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel([Result('a', 1, Text("ガチャ10連", new TextSpan(0, 3))), Result('b', 2), Result('c', 3)]);
            Assert.False(h.Panel.IsDetailOpen);
            Assert.False(h.View.IsVisible || h.View.Visibility == System.Windows.Visibility.Visible);

            await h.Panel.OpenDetailAsync(H('a'));

            Assert.True(h.Panel.IsDetailOpen);
            Assert.Equal(H('a'), h.Panel.DetailHash);
            Assert.Equal(System.Windows.Visibility.Visible, h.View.Visibility);
            Assert.Equal("1 / 3", h.View.PositionText);
            Assert.False(h.View.PreviousEnabled);
            Assert.True(h.View.NextEnabled);
            Assert.True(h.View.HasImage);
            Assert.Equal("a.png", h.View.TitleText);
            Assert.Contains("統合: 1 位", h.View.SideText, StringComparison.Ordinal);
            Assert.Contains($"{TestWords.VisualWithModel}: 1 位 (類似度 0.2000)", h.View.SideText, StringComparison.Ordinal);
            Assert.Contains("類似度 0.2000", h.View.SideText, StringComparison.Ordinal);                        // 類似度は本文に出す(ADR 0038)
            Assert.Contains("割合ではありません", h.View.TooltipOfLine($"{TestWords.VisualWithModel}: 1 位 (類似度 0.2000)"), StringComparison.Ordinal);   // 見方はツールチップ
            Assert.Contains("出た理由", h.View.SideText, StringComparison.Ordinal);
            Assert.Contains("文字が一致 (OCR)", h.View.SideText, StringComparison.Ordinal);
            Assert.Equal(["ガチャ"], h.View.HighlightedTexts("文字 (OCR)"));
            Assert.Equal("ガチャ10連", h.View.FieldText("文字 (OCR)"));
            Assert.Contains("寸法: 4 × 2", h.View.SideText, StringComparison.Ordinal);       // ImageDetail の値
            Assert.Contains("特徴ベクトル検索: 済", h.View.SideText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Activating_an_item_from_the_grid_opens_the_detail_instead_of_the_old_notice()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three());

            h.Panel.ResultGrid.ItemActivated!(new GridItem(H('b')));       // ダブルクリック・Enter が呼ぶ
            Assert.True(await Wait.UntilAsync(() => h.Panel.IsDetailOpen && h.View.HasImage));

            Assert.Equal(H('b'), h.Panel.DetailHash);
            Assert.Equal("2 / 3", h.View.PositionText);
            Assert.Empty(h.Notices);
        });
    }

    [Fact]
    public async Task Closing_hides_the_overlay_releases_the_image_and_returns_to_the_list()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three());
            await h.Panel.OpenDetailAsync(H('a'));

            h.Panel.CloseDetail();

            Assert.False(h.Panel.IsDetailOpen);
            Assert.Null(h.Panel.DetailHash);
            Assert.Equal(System.Windows.Visibility.Collapsed, h.View.Visibility);
            Assert.False(h.View.HasImage);
            h.Panel.CloseDetail();                                        // 閉じていても安全
        });
    }

    [Fact]
    public async Task The_back_button_closes_and_a_hash_that_is_not_in_the_results_opens_nothing()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three());
            await h.Panel.OpenDetailAsync(H('z'));
            Assert.False(h.Panel.IsDetailOpen);

            await h.Panel.OpenDetailAsync(H('a'));
            h.View.Buttons().Single(b => ((string)b.Content).Contains("戻る (Esc)", StringComparison.Ordinal)).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.False(h.Panel.IsDetailOpen);
        });
    }

    // ---------------------------------------------------------------- 前後

    [Fact]
    public async Task Navigation_moves_between_results_and_stops_at_both_ends()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three());
            await h.Panel.OpenDetailAsync(H('a'));

            h.Panel.NavigateDetail(-1);                                   // 先頭では前へ行けない
            Assert.Equal(H('a'), h.Panel.DetailHash);

            h.Panel.NavigateDetail(+1);
            Assert.True(await Wait.UntilAsync(() => h.View.PositionText == "2 / 3" && h.View.HasImage));
            Assert.Equal(H('b'), h.Panel.DetailHash);
            Assert.True(h.View.PreviousEnabled);
            Assert.True(h.View.NextEnabled);
            Assert.Equal("b.png", h.View.TitleText);

            h.Panel.NavigateDetail(+1);
            Assert.True(await Wait.UntilAsync(() => h.View.PositionText == "3 / 3"));
            Assert.False(h.View.NextEnabled);
            h.Panel.NavigateDetail(+1);                                   // 末尾では次へ行けない
            Assert.Equal(H('c'), h.Panel.DetailHash);

            h.Panel.NavigateDetail(-1);
            Assert.True(await Wait.UntilAsync(() => h.View.PositionText == "2 / 3"));
        });
    }

    [Fact]
    public async Task The_arrow_and_escape_keys_drive_the_detail_through_the_resolved_actions()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three());
            await h.Panel.OpenDetailAsync(H('a'));

            Assert.True(h.Panel.HandleDetailAction(KeyShortcuts.ResolveDetail(Key.Right, Key.None, ModifierKeys.None, searchBoxFocused: false)));
            Assert.True(await Wait.UntilAsync(() => h.Panel.DetailHash == H('b')));
            Assert.True(h.Panel.HandleDetailAction(KeyShortcuts.ResolveDetail(Key.Left, Key.None, ModifierKeys.None, searchBoxFocused: false)));
            Assert.True(await Wait.UntilAsync(() => h.Panel.DetailHash == H('a')));
            Assert.False(h.Panel.HandleDetailAction(KeyShortcuts.ResolveDetail(Key.Right, Key.None, ModifierKeys.None, searchBoxFocused: true)));   // 検索ボックスの中は、キャレットの移動
            Assert.Equal(H('a'), h.Panel.DetailHash);

            Assert.True(h.Panel.HandleDetailAction(KeyShortcuts.ResolveDetail(Key.Escape, Key.None, ModifierKeys.None, searchBoxFocused: true)));
            Assert.False(h.Panel.IsDetailOpen);
        });
    }

    [Fact]
    public async Task A_slow_load_of_the_previous_image_never_overwrites_the_one_now_shown()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var slow = new TaskCompletionSource<ImageDetail?>();
            Harness h = NewPanel(Three(), detail: (hash, _) => hash == H('a') ? slow.Task : Task.FromResult<ImageDetail?>(Detail(hash, @"C:\x\b-detail.png")));
            Task first = h.Panel.OpenDetailAsync(H('a'));
            h.Panel.NavigateDetail(+1);
            Assert.True(await Wait.UntilAsync(() => h.View.SideText.Contains("b-detail.png", StringComparison.Ordinal)));

            slow.SetResult(Detail(H('a'), @"C:\x\a-detail.png"));        // a の詳細が、b を出したあとに届く
            await first;

            Assert.Equal(H('b'), h.Panel.DetailHash);
            Assert.DoesNotContain("a-detail.png", h.View.SideText, StringComparison.Ordinal);
            Assert.Contains("b-detail.png", h.View.SideText, StringComparison.Ordinal);
        });
    }

    // ---------------------------------------------------------------- 記録が無い・読めない・画像が出せない

    [Fact]
    public async Task A_missing_image_record_shows_the_notice_and_the_search_parts_without_failing()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel([Result('a', 1, Text("セリフ", new TextSpan(0, 3)), @"C:\no-such-dir\a.png")], detail: (_, _) => Task.FromResult<ImageDetail?>(null));

            await h.Panel.OpenDetailAsync(H('a'));

            Assert.True(h.Panel.IsDetailOpen);                             // 落ちない
            Assert.Contains("この画像の記録がありません", h.View.SideText, StringComparison.Ordinal);
            Assert.Equal(["セリフ"], h.View.HighlightedTexts("文字 (OCR)"));  // 検索の結果の情報は出る
            Assert.Contains(@"C:\no-such-dir\a.png", h.View.SideText, StringComparison.Ordinal);
            Assert.DoesNotContain("処理の状態", h.View.SideText, StringComparison.Ordinal);
            Assert.DoesNotContain(h.Log.Messages, m => m.StartsWith("ERROR", StringComparison.Ordinal));
            Assert.Contains(h.Log.Messages, m => m.Contains("記録が見つからない", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task A_failing_detail_read_shows_the_reason_logs_the_error_and_keeps_the_panel_open()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), detail: (_, _) => throw new InvalidOperationException("database is locked"));

            await h.Panel.OpenDetailAsync(H('a'));

            Assert.True(h.Panel.IsDetailOpen);
            Assert.Contains("詳細を読み込めませんでした: database is locked", h.View.SideText, StringComparison.Ordinal);
            Assert.Contains(h.Log.Messages, m => m.StartsWith("ERROR 画像の詳細を読めなかった", StringComparison.Ordinal));
            Assert.True(h.View.HasImage);                                  // 画像は別に読める
            h.Panel.NavigateDetail(+1);                                    // 前後には動ける
            Assert.Equal(H('b'), h.Panel.DetailHash);
        });
    }

    [Theory]
    [InlineData(ThumbnailStatus.SourceUnavailable, "元のファイルがありません")]
    [InlineData(ThumbnailStatus.SourceUndecodable, "画像として読み込めませんでした")]
    public async Task An_image_that_cannot_be_shown_says_why_in_the_image_area_and_the_rest_still_shows(ThumbnailStatus status, string expected)
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), image: (_, _) => Task.FromResult(ThumbnailResult.Failure(status, "壊れている")));

            await h.Panel.OpenDetailAsync(H('a'));

            Assert.False(h.View.HasImage);
            Assert.Contains(expected, h.View.ImageMessage, StringComparison.Ordinal);
            Assert.Contains("寸法: 4 × 2", h.View.SideText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_failing_image_read_shows_a_message_and_logs_it()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three(), image: (_, _) => throw new InvalidOperationException("デコーダーの失敗"));
            await h.Panel.OpenDetailAsync(H('a'));
            Assert.Contains("画像を読み込めませんでした: デコーダーの失敗", h.View.ImageMessage, StringComparison.Ordinal);
            Assert.Contains(h.Log.Messages, m => m.StartsWith("ERROR 詳細画面の画像を読めなかった", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Without_a_core_the_detail_opens_with_a_reason_instead_of_crashing()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three());
            h.Panel.DetailFunc = null;
            h.Panel.ImageFunc = null;

            await h.Panel.OpenDetailAsync(H('a'));

            Assert.True(h.Panel.IsDetailOpen);
            Assert.Contains("詳細を読み込めませんでした", h.View.SideText, StringComparison.Ordinal);
            Assert.False(h.View.HasImage);
        });
    }

    // ---------------------------------------------------------------- 結果が替わったとき

    [Fact]
    public async Task A_new_search_result_closes_the_detail()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three());
            await h.Panel.OpenDetailAsync(H('b'));

            h.Panel.SetResults([Result('x', 1)]);                          // 新しい検索(keepBanner なし)

            Assert.False(h.Panel.IsDetailOpen);
        });
    }

    [Fact]
    public async Task A_refresh_that_still_has_the_image_keeps_it_open_and_fixes_the_position_and_one_that_dropped_it_closes_with_a_notice()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three());
            await h.Panel.OpenDetailAsync(H('c'));
            Assert.Equal("3 / 3", h.View.PositionText);

            h.Panel.SetResults([Result('a', 1), Result('c', 2)], keepBanner: true);    // 走査のあとの差し替え。b が消えた(c は残った)
            Assert.True(h.Panel.IsDetailOpen);
            Assert.Equal("2 / 2", h.View.PositionText);
            Assert.False(h.View.NextEnabled);
            Assert.True(h.View.PreviousEnabled);

            h.Panel.SetResults([Result('a', 1)], keepBanner: true);                    // c も消えた
            Assert.False(h.Panel.IsDetailOpen);
            Assert.Contains(h.Notices, n => n.Contains("結果から外れました", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Detaching_the_core_closes_the_detail_and_forgets_the_readers()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three());
            await h.Panel.OpenDetailAsync(H('a'));

            h.Panel.Detach();

            Assert.False(h.Panel.IsDetailOpen);
            Assert.Null(h.Panel.DetailFunc);
            Assert.Null(h.Panel.ImageFunc);
        });
    }

    // ---------------------------------------------------------------- 操作(一覧と同じ部品)

    [Fact]
    public async Task Reveal_and_copy_act_on_the_shown_image_and_a_missing_original_is_reported_like_in_the_list()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel([Result('a', 1, null, @"C:\no-such-dir\a.png", @"D:\no-such-dir\a-copy.png")]);
            await h.Panel.OpenDetailAsync(H('a'));
            Assert.Equal(0, h.Panel.ResultGrid.SelectedCount);              // 一覧には何も選んでいない。対象は表示中の画像

            h.Panel.RevealSelected();                                       // 実在を確かめ、無いので赤い帯と走査し直しの依頼
            Assert.True(h.Panel.BannerVisible);
            Assert.Contains("元のファイルがありません", h.Panel.BannerText, StringComparison.Ordinal);
            IReadOnlyList<string> reported = Assert.Single(h.Missing);
            Assert.Equal([@"C:\no-such-dir\a.png", @"D:\no-such-dir\a-copy.png"], reported.ToArray());   // その画像の現存パス(DB の上で)

            h.Missing.Clear();
            h.Panel.CopySelectedImage();
            Assert.Single(h.Missing);
            h.Missing.Clear();
            h.Panel.CopySelectedFiles();
            Assert.Single(h.Missing);
        });
    }

    [Fact]
    public async Task The_buttons_of_the_detail_call_the_same_operations()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel([Result('a', 1)]);
            await h.Panel.OpenDetailAsync(H('a'));

            foreach (string label in new[] { "エクスプローラーで表示", "画像をコピー", "ファイルとしてコピー" })
            {
                h.Missing.Clear();
                System.Windows.Controls.Button button = h.View.Buttons().Single(b => (string)b.Content == label);
                button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Single(h.Missing);                                    // 元のファイルが無いので、どれも「見つからない」を知らせる
            }
        });
    }

    [Fact]
    public async Task Dragging_the_image_with_a_missing_original_does_not_start_and_reports_it()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel([Result('a', 1)]);
            await h.Panel.OpenDetailAsync(H('a'));

            h.View.DragRequested!();                                        // 画像を押したまま動かした

            Assert.True(h.Panel.BannerVisible);
            Assert.Contains("ドラッグできません", h.Panel.BannerText, StringComparison.Ordinal);
            Assert.Single(h.Missing);
        });
    }

    [Fact]
    public async Task The_list_selection_is_still_the_target_when_no_detail_is_open()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three());
            h.Panel.ResultGrid.SelectAll();

            h.Panel.RevealSelected();                                       // 一覧の先頭の選択(a)

            IReadOnlyList<string> reported = Assert.Single(h.Missing);
            Assert.Equal([$@"C:\no-such-dir\a.png"], reported.ToArray());
            await Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- 実在する原本での操作(偽のシェルに渡る。本物のクリップボード・エクスプローラー・ドラッグには届かない)

    [Fact]
    public async Task Operations_on_an_existing_original_go_to_the_shell_seam_with_the_path()
    {
        using var temp = new TempDirectory();
        string file = temp.File("real.png");
        System.IO.File.WriteAllBytes(file, [1, 2, 3]);
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel([Result('a', 1, null, file)]);
            await h.Panel.OpenDetailAsync(H('a'));

            h.Panel.RevealSelected();
            h.Panel.CopySelectedImage();
            h.Panel.CopySelectedFiles();

            Assert.Equal([$"reveal {file}", $"copy-image {file}", $"copy-files {file}"], h.Shell.Calls);
            Assert.Contains(h.Notices, n => n == "エクスプローラーで表示しました: real.png");
            Assert.Contains(h.Notices, n => n == "画像をコピーしました: real.png");
            Assert.Contains(h.Notices, n => n == "1 枚をファイルとしてコピーしました");
            Assert.False(h.Panel.BannerVisible);
            Assert.Empty(h.Missing);                                         // 在るので、見つからない知らせは出ない
        });
    }

    [Fact]
    public async Task A_failing_shell_operation_shows_the_reason_in_the_banner_and_logs_it()
    {
        using var temp = new TempDirectory();
        string file = temp.File("real.png");
        System.IO.File.WriteAllBytes(file, [1, 2, 3]);
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel([Result('a', 1, null, file)]);
            await h.Panel.OpenDetailAsync(H('a'));

            h.Shell.FailWith = new System.IO.IOException("explorer が開けない");
            h.Panel.RevealSelected();
            Assert.Equal("エクスプローラーで表示できませんでした: explorer が開けません", h.Panel.BannerText);
            h.Panel.CopySelectedImage();
            Assert.Equal("画像をコピーできませんでした: explorer が開けません", h.Panel.BannerText);

            h.Shell.FailWith = new System.Runtime.InteropServices.COMException("クリップボードが開けない");
            h.Panel.CopySelectedFiles();
            Assert.Equal("ファイルをコピーできませんでした: クリップボードが開けません", h.Panel.BannerText);
            Assert.Equal(3, h.Log.Messages.Count(m => m.StartsWith("ERROR", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public async Task Dragging_the_image_with_an_existing_original_hands_the_path_to_the_shell_seam_and_reports_the_result()
    {
        using var temp = new TempDirectory();
        string file = temp.File("real.png");
        System.IO.File.WriteAllBytes(file, [1, 2, 3]);
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel([Result('a', 1, null, file)]);
            await h.Panel.OpenDetailAsync(H('a'));

            h.View.DragRequested!();                                         // 画像を押したまま動かした

            Assert.Equal([$"drag {file}"], h.Shell.Calls);                   // 本物の DoDragDrop(OLE のマウスの捕捉)は呼ばない
            Assert.Contains(h.Notices, n => n == "ドラッグで他のアプリへ渡しました (1 枚)");
            Assert.False(h.Panel.BannerVisible);

            h.Shell.DragResult = System.Windows.DragDropEffects.None;        // ドロップされず取り消された
            h.Notices.Clear();
            h.View.DragRequested!();
            Assert.Equal([$"drag {file}", $"drag {file}"], h.Shell.Calls);
            Assert.Empty(h.Notices);                                         // 取り消しは、画面(状態の行)に出さない
            Assert.False(h.Panel.BannerVisible);
            Assert.Contains("INFO ドラッグは取り消されました (1 枚)", h.Log.Messages);   // ログには残す
        });
    }

    [Fact]
    public async Task A_cancelled_drag_that_left_out_missing_files_is_still_reported_on_screen()
    {
        // 取り消し(None)でも、元のファイルが無いものを除いたときは知らせる(今のまま)。ThumbnailGridControl の DragFinished と同じ形で呼ぶ(本物のドラッグは起こさない)
        await StaDispatcher.RunAsync(() =>
        {
            Harness h = NewPanel([Result('a', 1)]);

            h.Panel.ResultGrid.DragFinished!(System.Windows.DragDropEffects.None, 1, 2, "x.png");

            Assert.Contains(h.Notices, n => n == "ドラッグは取り消されました (1 枚、元のファイルが無い 2 枚は除きました)");
            Assert.True(h.Panel.BannerVisible);
            Assert.DoesNotContain("INFO ドラッグは取り消されました (1 枚)", h.Log.Messages);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_finished_drag_from_the_list_says_so_on_screen_but_a_cancelled_one_does_not()
    {
        await StaDispatcher.RunAsync(() =>
        {
            Harness h = NewPanel([Result('a', 1)]);

            h.Panel.ResultGrid.DragFinished!(System.Windows.DragDropEffects.Copy, 3, 0, "x.png");
            Assert.Equal(["ドラッグで他のアプリへ渡しました (3 枚)"], h.Notices);

            h.Notices.Clear();
            h.Panel.ResultGrid.DragFinished!(System.Windows.DragDropEffects.None, 1, 0, "x.png");
            Assert.Empty(h.Notices);
            Assert.False(h.Panel.BannerVisible);
            Assert.Contains("INFO ドラッグは取り消されました (1 枚)", h.Log.Messages);
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- マウスの戻るボタン

    [Theory]
    [InlineData(MouseButton.XButton1, true, true)]    // 詳細画面で戻るボタン = 戻る (Esc)
    [InlineData(MouseButton.XButton2, true, false)]   // 進むボタンは何もしない
    [InlineData(MouseButton.Left, true, false)]
    [InlineData(MouseButton.Right, true, false)]
    [InlineData(MouseButton.Middle, true, false)]
    [InlineData(MouseButton.XButton1, false, false)]  // 一覧が出ているときは、今のまま(何もしない)
    [InlineData(MouseButton.XButton2, false, false)]
    public void The_mouse_back_button_closes_the_detail_only(MouseButton button, bool detailOpen, bool closes)
        => Assert.Equal(closes, MouseShortcuts.ClosesDetail(button, detailOpen));

    [Fact]
    public async Task The_mouse_back_button_in_the_detail_does_what_the_back_button_does_and_the_forward_button_does_nothing()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            Harness h = NewPanel(Three());

            Assert.False(h.Panel.HandleMouseButton(MouseButton.XButton1));   // 一覧が出ている: 何もしない(処理しない=false)

            await h.Panel.OpenDetailAsync(H('b'));
            Assert.True(h.Panel.IsDetailOpen);

            Assert.False(h.Panel.HandleMouseButton(MouseButton.XButton2));   // 進むボタン: 何もしない
            Assert.False(h.Panel.HandleMouseButton(MouseButton.Left));
            Assert.True(h.Panel.IsDetailOpen);

            Assert.True(h.Panel.HandleMouseButton(MouseButton.XButton1));    // 戻るボタン: 一覧に戻る
            Assert.False(h.Panel.IsDetailOpen);
            Assert.Equal(System.Windows.Visibility.Collapsed, h.View.Visibility);

            Assert.False(h.Panel.HandleMouseButton(MouseButton.XButton1));   // 閉じたあとの戻るボタンは何もしない
        });
    }

    [Fact]
    public async Task The_back_button_tooltip_mentions_the_mouse_back_button_and_the_label_keeps_the_esc_shortcut()
    {
        await StaDispatcher.RunAsync(() =>
        {
            Harness h = NewPanel(Three());
            System.Windows.Controls.Button back = h.View.Buttons().Single(b => ((string)b.Content).Contains("戻る (Esc)", StringComparison.Ordinal));

            Assert.Equal("◀ 戻る (Esc)", back.Content);
            Assert.Equal(DetailView.BackTooltip, back.ToolTip);
            Assert.Contains("マウスの戻るボタンでも戻れます", (string)back.ToolTip, StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- ダブルクリックの 2 回目からは、ドラッグを始めない

    [Theory]
    [InlineData(1, true)]    // 1 回目の押下: 動かせばドラッグが始まる
    [InlineData(2, false)]   // ダブルクリックの 2 回目: 始めない
    [InlineData(3, false)]
    public void Only_the_first_press_of_a_click_arms_the_drag(int clickCount, bool arms)
        => Assert.Equal(arms, MouseShortcuts.ArmsDrag(clickCount));
}
