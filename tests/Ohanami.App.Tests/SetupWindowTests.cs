using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// モデルのセットアップの画面(<see cref="SetupWindow"/>)。状態の種類(特徴ベクトル検索のモデルが無い / テキスト検索のモデルだけ無い / そろっている)・ボタン・確かめ直しの動き・
/// フォルダを開く(偽のシェル)・大きさの書き方。窓は表示しない(作って、部品の状態を読むだけ)。
/// </summary>
public sealed class SetupWindowTests
{
    private const string ModelsRoot = @"C:\Users\example\AppData\Local\Ohanami\models";
    private static readonly string EmbeddingDirectory = Path.Combine(ModelsRoot, OhanamiCore.ModelDirectoryName);
    private static readonly string VlmDirectory = Path.Combine(ModelsRoot, VlmOptions.ModelDirectoryName);

    /// <summary>実際の定義(名前・大きさ・入手先)で、置き場に何も無い状態から作る。</summary>
    private static (ModelSetStatus Embedding, ModelSetStatus Vlm) Empty() =>
    (
        ModelFiles.InspectEmbedding(EmbeddingDirectory),
        ModelFiles.InspectVlm(VlmDirectory)
    );

    private static ModelSetStatus AllPresent(ModelSetStatus set) =>
        set with { Files = [.. set.Files.Select(f => f with { State = ModelFileState.Present, ActualSize = f.ExpectedSize })] };

    private static ModelSetStatus Set(ModelSetStatus set, int index, ModelFileState state, long? actual) =>
        set with { Files = [.. set.Files.Select((f, i) => i == index ? f with { State = state, ActualSize = actual } : f)] };

    internal static SetupState BothMissing() { (ModelSetStatus e, ModelSetStatus v) = Empty(); return new SetupState(e, v); }

    internal static SetupState VlmOnlyMissing() { (ModelSetStatus e, ModelSetStatus v) = Empty(); return new SetupState(AllPresent(e), v); }

    internal static SetupState WithMismatch()
    {
        (ModelSetStatus e, ModelSetStatus v) = Empty();
        e = AllPresent(e);
        e = Set(e, 2, ModelFileState.SizeMismatch, 1_200_000_000);   // 途中までの大きさのファイル
        e = Set(e, 3, ModelFileState.Missing, null);
        v = Set(AllPresent(v), 0, ModelFileState.SizeMismatch, 4_000_000_000);
        return new SetupState(e, v);
    }

    internal static SetupState AllReady() { (ModelSetStatus e, ModelSetStatus v) = Empty(); return new SetupState(AllPresent(e), AllPresent(v)); }

    internal static SetupWindow NewWindow(SetupState initial, Func<SetupState>? inspect = null, FakeShell? shell = null, FakeDownloads? downloads = null) =>
        new(inspect ?? (() => initial), shell ?? new FakeShell(), downloads ?? new FakeDownloads(), initial);

    internal static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    internal static Button ButtonOf(SetupWindow w, string text) =>
        FindButtons(w).Single(b => (string)b.Content == text);

    private static System.Collections.Generic.IEnumerable<Button> FindButtons(DependencyObject root)
    {
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Button b) yield return b;
            foreach (Button inner in FindButtons(child)) yield return inner;
        }
    }

    // ---------------------------------------------------------------- 状態の種類

    [Fact]
    public void The_kind_follows_which_model_is_incomplete()
    {
        Assert.Equal(SetupKind.EmbeddingIncomplete, BothMissing().Kind);
        Assert.Equal(SetupKind.EmbeddingIncomplete, WithMismatch().Kind);
        Assert.Equal(SetupKind.VlmIncomplete, VlmOnlyMissing().Kind);
        Assert.Equal(SetupKind.Ready, AllReady().Kind);
        Assert.Equal(6, BothMissing().ProblemFileCount);
        Assert.Equal(3, WithMismatch().ProblemFileCount);   // 大きさ違い 2 + 無い 1
        Assert.Equal(2, VlmOnlyMissing().ProblemFileCount);
    }

    // ---------------------------------------------------------------- 文言

    [Fact]
    public async Task The_window_lists_every_file_with_its_size_and_state_and_the_folder_and_source()
    {
        await StaDispatcher.RunAsync(() =>
        {
            SetupWindow w = NewWindow(WithMismatch());
            string[] texts = w.TextsForTests();

            Assert.Equal(SetupWords.WindowTitle, w.Title);
            Assert.Contains(SetupWords.HeadingMissing, texts);
            Assert.Contains(SetupWords.SectionTitle(JobKind.Embed), texts);
            Assert.Contains(SetupWords.SectionTitle(JobKind.Vlm), texts);
            Assert.Contains("未取得 1 ファイル、大きさが違う 1 ファイル", texts);
            Assert.Contains("大きさが違う (実際 1.20 GB)", texts);
            Assert.Contains("あり", texts);
            Assert.Contains("未取得", texts);
            Assert.Contains("2.83 GB", texts);                     // textual/model.onnx_data
            Assert.Contains(EmbeddingDirectory, texts);            // 置き場
            Assert.Contains(ModelFiles.InspectEmbedding(EmbeddingDirectory).SourceUrl!, texts);
            Assert.Contains(ModelFiles.InspectVlm(VlmDirectory).SourceUrl!, texts);
            w.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Sizes_are_written_in_decimal_units_like_the_download_site_with_the_exact_bytes_in_the_hint()
    {
        Assert.Equal("5.03 GB", SetupWords.FormatSize(5_027_784_800));
        Assert.Equal("2.83 GB", SetupWords.FormatSize(2_831_131_584));
        Assert.Equal("34.4 MB", SetupWords.FormatSize(34_362_885));
        Assert.Equal("560 KB", SetupWords.FormatSize(560_482));
        Assert.Equal("999 バイト", SetupWords.FormatSize(999));
        Assert.Equal("2,831,131,584 バイト", SetupWords.ExactSize(2_831_131_584));
    }

    [Fact]
    public void The_summary_and_the_set_state_count_files_by_kind()
    {
        SetupState s = WithMismatch();

        Assert.Equal("4 ファイル、合計 4.58 GB", SetupWords.SetSummary(s.Embedding));
        Assert.Equal("2 ファイル、合計 6.19 GB", SetupWords.SetSummary(s.Vlm));
        Assert.Equal("未取得 1 ファイル、大きさが違う 1 ファイル", SetupWords.SetState(s.Embedding));
        Assert.Equal("大きさが違う 1 ファイル", SetupWords.SetState(s.Vlm));
        Assert.Equal(SetupWords.SetComplete, SetupWords.SetState(AllReady().Embedding));
    }

    // ---------------------------------------------------------------- ボタン

    /// <summary>「2 つともダウンロード」のボタンか(「ダウンロード」で始まる区画ごとのボタンとは、文字の頭が違う)。</summary>
    internal static bool IsBothButton(string text) => text.StartsWith("2 つと", StringComparison.Ordinal);

    [Fact]
    public async Task The_buttons_depend_on_the_state()
    {
        await StaDispatcher.RunAsync(() =>
        {
            string[] both = NewWindow(BothMissing()).ButtonTextsForTests();
            string[] vlmOnly = NewWindow(VlmOnlyMissing()).ButtonTextsForTests();

            Assert.Equal(2, both.Count(b => b == SetupWords.OpenFolderLabel));
            Assert.Equal(2, both.Count(b => b.StartsWith(SetupWords.DownloadLabel, StringComparison.Ordinal)));   // 区画ごとに「ダウンロード」
            Assert.Equal(1, both.Count(IsBothButton));                                                              // 「2 つともダウンロード」(区画ごとのボタンとは、別に数える)
            Assert.Contains(SetupWords.RecheckLabel, both);
            Assert.Contains(SetupWords.ExitLabel, both);
            Assert.DoesNotContain(SetupWords.ContinueLabel, both);   // 特徴ベクトル検索のモデルが無いと、「このまま始める」は無い
            Assert.Equal(1, vlmOnly.Count(b => b.StartsWith(SetupWords.DownloadLabel, StringComparison.Ordinal)));   // そろっている区画には、ダウンロードは出ない
            Assert.Equal(0, vlmOnly.Count(IsBothButton));                                                            // 片方がそろっていれば、「2 つとも」は出ない
            Assert.Contains(SetupWords.ContinueLabel, vlmOnly);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void The_promise_is_the_same_sentence_as_the_one_at_the_top_of_agents_md()
    {
        string agents = File.ReadAllText(Path.Combine(ShellBoundaryGuardTests.RepoRoot(), "AGENTS.md"));

        Assert.Contains(SetupWords.Promise, agents, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 確かめ直し・結果

    [Fact]
    public async Task Closing_the_window_means_exit_by_default()
    {
        await StaDispatcher.RunAsync(() =>
        {
            SetupWindow w = NewWindow(BothMissing());
            Assert.Equal(SetupChoice.Exit, w.Choice);
            w.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Rechecking_with_everything_there_finishes_with_ready()
    {
        await StaDispatcher.RunAsync(() =>
        {
            SetupState current = BothMissing();
            SetupWindow w = NewWindow(current, () => current);
            bool closed = false;
            w.CloseWindow = () => closed = true;

            current = AllReady();
            Click(ButtonOf(w, SetupWords.RecheckLabel));

            Assert.True(closed);
            Assert.Equal(SetupChoice.Ready, w.Choice);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Rechecking_with_the_vlm_still_missing_stays_open_and_says_how_many_files_are_left()
    {
        await StaDispatcher.RunAsync(() =>
        {
            SetupState current = BothMissing();
            SetupWindow w = NewWindow(current, () => current);
            bool closed = false;
            w.CloseWindow = () => closed = true;

            current = VlmOnlyMissing();   // 特徴ベクトル検索のモデルがそろった
            Click(ButtonOf(w, SetupWords.RecheckLabel));

            Assert.False(closed);
            Assert.Equal(SetupKind.VlmIncomplete, w.State.Kind);
            Assert.Equal(SetupWords.StillIncomplete(2), w.StatusText);
            Assert.Contains(SetupWords.HeadingVlmMissing, w.TextsForTests());
            Assert.Contains(SetupWords.ContinueLabel, w.ButtonTextsForTests());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task An_automatic_check_when_the_window_comes_back_does_not_write_the_status_line()
    {
        await StaDispatcher.RunAsync(() =>
        {
            SetupWindow w = NewWindow(BothMissing());
            w.CloseWindow = () => { };

            w.Recheck(manual: false);

            Assert.Equal("", w.StatusText);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Continuing_without_the_vlm_model_returns_the_choice_and_closes()
    {
        await StaDispatcher.RunAsync(() =>
        {
            SetupWindow w = NewWindow(VlmOnlyMissing());
            bool closed = false;
            w.CloseWindow = () => closed = true;

            Click(ButtonOf(w, SetupWords.ContinueLabel));

            Assert.True(closed);
            Assert.Equal(SetupChoice.WithoutVlm, w.Choice);
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- フォルダを開く

    [Fact]
    public async Task Open_folder_creates_the_folder_when_it_is_missing_and_opens_it_through_the_shell()
    {
        string root = Path.Combine(Path.GetTempPath(), "ohanami-setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            await StaDispatcher.RunAsync(() =>
            {
                string folder = Path.Combine(root, "models", OhanamiCore.ModelDirectoryName);
                (ModelSetStatus e, ModelSetStatus v) = Empty();
                var shell = new FakeShell();
                SetupWindow w = NewWindow(new SetupState(e with { Directory = folder }, v), shell: shell);

                Click(FindButtons(w).First(b => (string)b.Content == SetupWords.OpenFolderLabel));

                Assert.True(Directory.Exists(folder));
                Assert.Equal(["open-folder " + folder], shell.Calls);
                return Task.CompletedTask;
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Open_folder_failure_is_written_in_the_status_line_not_thrown()
    {
        string root = Path.Combine(Path.GetTempPath(), "ohanami-setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            await StaDispatcher.RunAsync(() =>
            {
                (ModelSetStatus e, ModelSetStatus v) = Empty();
                var shell = new FakeShell { FailWith = new IOException("開けない") };
                SetupWindow w = NewWindow(new SetupState(e with { Directory = Path.Combine(root, "a") }, v), shell: shell);

                Click(FindButtons(w).First(b => (string)b.Content == SetupWords.OpenFolderLabel));

                Assert.StartsWith("フォルダを開けませんでした: ", w.StatusText, StringComparison.Ordinal);
                return Task.CompletedTask;
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------- 測定用(見た目の案の画像)

    [Fact(Skip = "測定用(セットアップの画面の案)。手元で Skip を外して回すと、%TEMP% の ohanami-setup-shots に、状態ごとの画面の PNG を書く。通常の回帰テストには入れない")]
    public async Task Render_the_setup_window_for_each_state_offscreen()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ohanami-setup-shots");
        Directory.CreateDirectory(dir);
        await StaDispatcher.RunAsync(() =>
        {
            foreach ((string name, SetupState state) in new (string, SetupState)[]
            {
                ("1-both-missing", BothMissing()),
                ("2-vlm-only-missing", VlmOnlyMissing()),
                ("3-size-mismatch", WithMismatch()),
            })
            {
                SetupWindow w = NewWindow(state);
                Save(w, Path.Combine(dir, name + ".png"));
                w.Close();
            }
            return Task.CompletedTask;
        });
        Assert.Equal(3, Directory.GetFiles(dir, "*.png").Length);
    }

    /// <summary>窓の中身を、画面を出さずに描いて PNG に書く(測定用)。</summary>
    internal static void Save(SetupWindow w, string path)
    {
        var root = (UIElement)w.Content;
        w.Content = null;
        var host = new Border { Background = AppColors.WindowBackground, Child = root, Width = 820 };
        TestLook.Setup(host);
        host.Measure(new Size(820, double.PositiveInfinity));
        double height = Math.Ceiling(host.DesiredSize.Height);
        host.Arrange(new Rect(0, 0, 820, height));
        host.UpdateLayout();
        var bmp = new RenderTargetBitmap(820, (int)height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using FileStream fs = File.Create(path);
        enc.Save(fs);
        host.Child = null;
        w.Content = root;   // 描いたあとも、窓の部品として使えるように戻す(host から外してから)
    }
}
