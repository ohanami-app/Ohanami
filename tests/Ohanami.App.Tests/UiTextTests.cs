using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 画面の文言の規則(ADR 0038)のうち、画面の文字列で確かめられるもの: 内部の語を画面に出さない(<see cref="UiText.Plain(string?)"/>)、
/// 窓や確認が出るボタンに「…」、ショートカットの表示、呼び名の統一(「文字認識」を使わない)。文言そのものは、各画面のテストが確かめる。
/// </summary>
public sealed class UiTextTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Sha2 = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    // ---------------------------------------------------------------- 区切りの「 — 」・フォルダのツールチップ・時間の見込み

    private const string UnreachableRecord = "到達できなかった場所 2 件(配下は今回の走査の対象外):\nC:\\shots\\a — 存在しない、または開けない(取り外したドライブ、切れたネットワークなど)\nD:\\b — アクセスが拒否された";

    [Fact]
    public void Plain_turns_the_dash_separator_of_a_reason_into_the_colon_form_of_rule_4()
    {
        Assert.Equal("C:\\shots\\a: 存在しません", UiText.Plain("C:\\shots\\a — 存在しない"));
        Assert.Equal("到達できなかった場所 2 件 (配下は今回のスキャンの対象外):\nC:\\shots\\a: 存在しません、または開けません (取り外したドライブ、切れたネットワークなど)\nD:\\b: アクセスが拒否されました",
            UiText.Plain(UnreachableRecord));
        Assert.DoesNotContain("—", UiText.Plain(UnreachableRecord), StringComparison.Ordinal);
        Assert.Equal("ハイフンのみ-は-そのまま", UiText.Plain("ハイフンのみ-は-そのまま"));
        Assert.Equal("区切りでない—長いダッシュ", UiText.Plain("区切りでない—長いダッシュ"));           // 前後に空白が無いものは区切りではない
    }

    [Fact]
    public void The_folder_tooltip_does_not_prefix_the_unreachable_record_because_the_core_sentence_already_says_it()
    {
        string tip = IndexPanel.FolderTooltip(@"C:\shots", UnreachableRecord, null);

        Assert.StartsWith("C:\\shots\n到達できなかった場所 2 件 (配下は今回のスキャンの対象外):", tip, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(tip, "到達できなかった場所"));   // 2 回続かない
        Assert.DoesNotContain("到達できなかった場所:", tip, StringComparison.Ordinal);                          // 接頭の「到達できなかった場所: 」は無い
        Assert.DoesNotContain("—", tip, StringComparison.Ordinal);
        Assert.Contains("C:\\shots\\a: 存在しません", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_folder_tooltip_is_just_the_path_without_records_and_keeps_the_unwatched_explanation()
    {
        Assert.Equal(@"C:\shots", IndexPanel.FolderTooltip(@"C:\shots", null, null));

        string unwatched = IndexPanel.FolderTooltip(@"C:\shots", null, "フォルダが無い、または開けない(取り外したドライブ、切れたネットワークなど)");
        Assert.StartsWith("C:\\shots\n監視できません: フォルダがありません、または開けません", unwatched, StringComparison.Ordinal);
        Assert.Contains("(監視は、フォルダの変化を見張って", unwatched, StringComparison.Ordinal);
    }

    [Fact]
    public void The_vlm_start_tooltip_and_the_requeue_confirmation_are_made_from_the_same_seconds_per_image()
    {
        string tip = ProcessingWords.StartTooltip();
        TimeSpan tenThousand = ProcessingText.Estimate(JobKind.Vlm, ProcessingText.VlmTooltipSampleCount);

        Assert.Equal(TimeSpan.FromSeconds(10_000 * ProcessingText.VlmSecondsPerImage), tenThousand);
        Assert.Contains($"1 万枚で約 {Math.Round(tenThousand.TotalHours):0} 時間", tip, StringComparison.Ordinal);       // 固定の「36 時間」ではなく、見込みの数から
        Assert.DoesNotContain("36 時間", tip, StringComparison.Ordinal);
        Assert.Contains(ProcessingText.PerImageBasis(JobKind.Vlm), tip, StringComparison.Ordinal);                       // 計算の根拠(「1 枚約 12 秒で計算」)も同じ文字列
        string confirm = ProcessingText.RequeueConfirmText(JobKind.Vlm, ProcessingText.VlmTooltipSampleCount);
        Assert.Contains(ProcessingText.PerImageBasis(JobKind.Vlm), confirm, StringComparison.Ordinal);
        Assert.Contains("約 " + IndexPanel.FormatDuration(tenThousand), confirm, StringComparison.Ordinal);              // 確認は同じ数から正確な時間(33 時間 20 分)
        Assert.Equal("1 枚約 12 秒で計算しています", ProcessingText.PerImageBasis(JobKind.Vlm));
        Assert.Equal("1 枚約 0.1 秒で計算しています", ProcessingText.PerImageBasis(JobKind.Embed));
    }

    // ---------------------------------------------------------------- UiText.Plain(内部の語を画面から消す)

    [Fact]
    public void Plain_drops_the_exception_type_name_at_the_start_but_nothing_else()
    {
        Assert.Equal("database is locked", UiText.Plain("InvalidOperationException: database is locked"));
        Assert.Equal("x", UiText.Plain("System.IO.IOException: x"));
        Assert.Equal("Microsoft.Data.Sqlite の例外ではありません: 先頭ではない型名は残す", UiText.Plain("Microsoft.Data.Sqlite の例外ではない: 先頭ではない型名は残す"));
        Assert.Equal("", UiText.Plain((string?)null));
        Assert.Equal("ふつうの文はそのまま", UiText.Plain("ふつうの文はそのまま"));
    }

    [Fact]
    public void Plain_hides_sha256_values_and_keeps_what_is_missing_and_where_it_was_looked_for()
    {
        // 規則 11: 何が無いか、どこを探したかは画面に出す。sha256 の値は画面に出さない(ログには元の文が残る)
        string missing = $"モデルのファイルが無い: visual/model.onnx(探した場所: C:\\m\\visual\\model.onnx)。期待サイズ 123 バイト、sha256 {Sha}。";
        string shown = UiText.Plain(missing);
        Assert.Equal("モデルのファイルがありません: visual/model.onnx(探した場所: C:\\m\\visual\\model.onnx)。期待サイズ 123 バイト。", shown);
        Assert.DoesNotContain(Sha, shown, StringComparison.Ordinal);

        string different = $"モデルのファイルの sha256 が違う: visual/model.onnx 期待 {Sha}、実際 {Sha2}(場所: C:\\m\\visual\\model.onnx)。";
        shown = UiText.Plain(different);
        Assert.Equal("モデルのファイルの中身が想定と違います (SHA-256 が一致しません): visual/model.onnx(場所: C:\\m\\visual\\model.onnx)。", shown);
        Assert.DoesNotContain(Sha, shown, StringComparison.Ordinal);
        Assert.DoesNotContain(Sha2, shown, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_rewords_the_vlm_program_but_keeps_the_path_so_the_user_knows_where_to_put_it()
    {
        string noExe = $"llama-server.exe が無い: C:\\rt\\llama-server.exe。期待 sha256 {Sha}。";
        Assert.Equal("VLM の実行ファイル (llama-server) がありません: C:\\rt\\llama-server.exe。", UiText.Plain(noExe));

        string differentExe = $"llama-server.exe の sha256 が違う: 期待 {Sha}、実際 {Sha2}(場所: C:\\rt\\llama-server.exe)。";
        string shown = UiText.Plain(differentExe);
        Assert.Equal("VLM の実行ファイル (llama-server) の中身が想定と違います (SHA-256 が一致しません。場所: C:\\rt\\llama-server.exe)。", shown);

        Assert.Equal("VLM の処理プログラム (llama-server) が 3 枚続けて異常になりました。", UiText.Plain("llama-server が 3 枚続けて異常になった。"));
    }

    [Fact]
    public void Plain_removes_enum_names_hash_prefixes_hresults_and_internal_check_names()
    {
        Assert.Equal("テキスト検索のインデックスを作る部品がありません (この版では動かせません)", UiText.Plain("ジョブ Vlm のランナーが無い(この版では動かせない)"));   // 列挙の名前(Vlm)・ジョブ・ランナーは画面に出ない
        Assert.Equal("インデックス作成は実行中です。止めてから始めてください。", UiText.Plain("ジョブは実行中(状態 Running)。止めてから始める。"));
        Assert.Equal("C:\\a.png: 内容がスキャンのときと違います (スキャンのあとで書き換えられました)",
            UiText.Plain("C:\\a.png: 内容が走査のときと違う(ハッシュ 1a2b3c4d5e6f… ≠ 7a8b9c0d1e2f…。走査のあとで書き換えられた)"));
        Assert.Equal("Windows のシェルがエラーを返しました (HRESULT 0x80070002): C:\\a.png", UiText.Plain("SHParseDisplayName が失敗(HRESULT 0x80070002): C:\\a.png"));
        Assert.Equal("GPU の動作確認 (自己検査)に失敗しました: x", UiText.Plain("GPU の自己検査に失敗した: x"));
        Assert.Equal("不正な文字 (文字コードとして壊れています。対になっていないサロゲート)を含む", UiText.Plain("不正な文字(対になっていないサロゲート)を含む"));
        Assert.Equal("特徴ベクトルを読み込み中", UiText.Plain("ベクトルを読み込み中"));
        Assert.Equal("特徴ベクトルの読み込みがまだ始まっていません", UiText.Plain("ベクトルの読み込みがまだ始まっていない"));
    }

    // ---------------------------------------------------------------- 括弧の前の空白(ADR 0038)

    [Theory]
    [InlineData("検索準備完了(特徴ベクトル検索の対象 10 枚)", "検索準備完了 (特徴ベクトル検索の対象 10 枚)")]   // 前が日本語
    [InlineData("12 MB(12,582,912 バイト)", "12 MB (12,582,912 バイト)")]                                 // 前が英数字
    [InlineData("ocr(文字)と caption(説明文)", "ocr (文字)と caption (説明文)")]
    [InlineData("(先頭の括弧)は空けない", "(先頭の括弧)は空けない")]                                       // 文の頭
    [InlineData("理由 (すでに空白がある)", "理由 (すでに空白がある)")]                                       // 空白の直後
    [InlineData("「(」と [(x)] と ((二重))", "「(」と [(x)] と ((二重))")]                                   // 開きの括弧・かぎ括弧の直後
    [InlineData("(a)(b)", "(a) (b)")]                                                                      // 閉じの括弧の直後は空ける
    [InlineData("1\n2(行ごとに数える)\n(行の頭)", "1\n2 (行ごとに数える)\n(行の頭)")]
    [InlineData("C:\\shots\\名前(1).png: 読み込めません", "C:\\shots\\名前(1).png: 読み込めません")]         // パスの中は変えない
    [InlineData("shots/a(1).png", "shots/a(1).png")]
    public void SpaceBeforeParen_puts_a_space_before_every_half_width_paren_except_in_paths_and_after_a_space_or_an_opening_bracket(string text, string expected)
    {
        Assert.Equal(expected, UiText.SpaceBeforeParen(text));
    }

    // ---------------------------------------------------------------- 起動の状態(ステータスバー)

    [Fact]
    public async Task The_status_bar_says_what_is_missing_and_where_without_the_core_prefix_the_type_name_or_the_hash()
    {
        await Sta.RunAsync(() =>
        {
            var bar = new StatusBarView();
            bar.ShowCoreStatus(new CoreStatus(CorePhase.Starting, null, false, 0));
            Assert.Equal("起動中 (モデルの確認など)…", bar.CoreText);

            bar.ShowCoreStatus(new CoreStatus(CorePhase.Ready, null, true, 1234));
            Assert.Equal("検索準備完了 (特徴ベクトル検索の対象 1234 枚)", bar.CoreText);
            bar.ShowCoreStatus(new CoreStatus(CorePhase.Ready, null, false, 0));
            Assert.Equal("起動中 (特徴ベクトルを読み込み中です。特徴ベクトル検索はまだ使えません)", bar.CoreText);
            Assert.DoesNotContain("準備完了", bar.CoreText, StringComparison.Ordinal);                // 「完了」とは言わない(インデックス作成は続く)
            Assert.DoesNotContain("検索は少し待つ", bar.CoreText, StringComparison.Ordinal);          // テキスト検索は読み込み中も答える

            bar.ShowCoreStatus(new CoreStatus(CorePhase.Failed,
                $"モデルのファイルが無い: visual/model.onnx(探した場所: C:\\m\\visual\\model.onnx)。期待サイズ 123 バイト、sha256 {Sha}。", false, 0));
            Assert.Equal("起動できませんでした: モデルのファイルがありません: visual/model.onnx(探した場所: C:\\m\\visual\\model.onnx)。期待サイズ 123 バイト。", bar.CoreText);
            Assert.DoesNotContain("Core", bar.CoreText, StringComparison.Ordinal);
            Assert.DoesNotContain(Sha, bar.CoreText, StringComparison.Ordinal);
        });
    }

    // ---------------------------------------------------------------- ショートカットの表示・ボタンの「…」

    [Fact]
    public async Task The_context_menu_shows_the_shortcuts_and_says_manual_tag()
    {
        await Sta.RunAsync(() =>
        {
            var panel = new ResultsPanel(new TestLog(), _ => { }, new FakeShell());
            MenuItem[] items = panel.ResultGrid.ContextMenu!.Items.OfType<MenuItem>().ToArray();

            Assert.Equal(["エクスプローラーで表示", "画像をコピー", "ファイルとしてコピー", "手動タグを付ける…"], items.Select(i => (string)i.Header).ToArray());
            Assert.Equal(["E", "Ctrl+C", "Ctrl+Shift+C", ""], items.Select(i => i.InputGestureText).ToArray());
        });
    }

    [Fact]
    public async Task The_detail_buttons_show_their_shortcuts_in_the_tooltip()
    {
        await Sta.RunAsync(() =>
        {
            var view = new DetailView();
            view.ShowModel(new DetailModel("a.png", [], [], null, new ManualTagsModel([], false), [], [], [], null));

            Dictionary<string, object?> tips = view.Buttons().Where(b => b.Content is string).ToDictionary(b => (string)b.Content, b => (object?)b.ToolTip);
            Assert.Equal("ショートカット: E", tips["エクスプローラーで表示"]);
            Assert.Equal("ショートカット: Ctrl+C", tips["画像をコピー"]);
            Assert.Equal("ショートカット: Ctrl+Shift+C", tips["ファイルとしてコピー"]);
        });
    }

    [Fact]
    public async Task The_left_panel_has_four_buttons_and_the_ones_that_open_a_window_or_a_confirmation_end_with_dots()
    {
        await Sta.RunAsync(() =>
        {
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false);
            string[] buttons = TextsOf(panel).Buttons;

            // 左パネルのボタンは 5 つ(ADR 0039)。まれな操作は処理状態の窓へ、有効/無効は行の右クリックへ
            Assert.Equal(["追加…", "外す…", "インデックス作成を開始", "インデックスの状態…"], buttons);
            // 窓・確認が出る(追加はフォルダの選択、外すは確認、処理状態は窓)には「…」。確認なしで始まる(再走査・開始)には付けない
            Assert.All(new[] { "追加…", "外す…", "インデックスの状態…" }, name => Assert.EndsWith("…", name, StringComparison.Ordinal));
            Assert.All(new[] { "インデックス作成を開始" }, name => Assert.DoesNotContain("…", name, StringComparison.Ordinal));
        });
    }

    // ---------------------------------------------------------------- 呼び名(ADR 0038)

    [Fact]
    public async Task The_left_panel_names_the_stages_by_the_search_they_make_and_puts_the_exact_technical_names_in_the_hint()
    {
        await Sta.RunAsync(() =>
        {
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false);
            (string[] buttons, string[] tips) = TextsOf(panel);
            string all = string.Join("\n", buttons.Concat(tips));

            // 撤回した名前・内部の名前は出さない(「埋め込み」はふつうの日本語で「何かを埋め込む」と読めるので、本文から外した)
            foreach (string retired in new[] { "文字認識", "ハッシュ", "llama-server", "Core:", "自己検査", "見つからない画像", "待ちに戻", "埋め込み", "有効/無効" })
                Assert.DoesNotContain(retired, all, StringComparison.Ordinal);
            // 本文は目的の言葉(段階の名前 = その段階で使えるようになる検索)、ヒントに正確な技術の名前(特徴ベクトル化・OCR・VLM・モデル名)
            string start = (string)panel.StartToolTip;
            Assert.Contains(ProcessingWords.StageName(JobKind.Embed), start, StringComparison.Ordinal);
            Assert.Contains(ProcessingWords.StageName(JobKind.Vlm), start, StringComparison.Ordinal);
            Assert.Contains("特徴ベクトル化", start, StringComparison.Ordinal);
            Assert.Contains("OCR", start, StringComparison.Ordinal);
            Assert.Contains(OhanamiCore.ModelLabels.FeatureVector, start, StringComparison.Ordinal);   // モデルの名前は、Core の公開の表示名から(書き写さない)
            Assert.Contains(OhanamiCore.ModelLabels.Vlm, start, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task The_result_panel_says_manual_tag_for_the_tag_button_and_its_tooltip()
    {
        await Sta.RunAsync(() =>
        {
            var panel = new ResultsPanel(new TestLog(), _ => { }, new FakeShell());
            Assert.Equal("手動タグを付ける…", panel.TagButton.Content);
            Assert.Contains("手動タグ", (string)panel.TagButton.ToolTip, StringComparison.Ordinal);
        });
    }

    /// <summary>パネルの中のボタンの文字とツールチップ(文字列のもの)を集める。</summary>
    private static (string[] Buttons, string[] Tips) TextsOf(DependencyObject root)
    {
        var buttons = new List<string>();
        var tips = new List<string>();
        void Walk(DependencyObject d)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(d))
            {
                if (child is Button b)
                {
                    if (b.Content is string s) buttons.Add(s);
                    if (b.ToolTip is string t) tips.Add(t);
                }
                if (child is DependencyObject next) Walk(next);
            }
        }
        Walk(root);
        return ([.. buttons], [.. tips]);
    }
}
