using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ohanami.App;
using Ohanami.Core;
using Xunit;

namespace Ohanami.RemoveData.Tests;

/// <summary>
/// 道具(.ps1)を、Windows PowerShell 5.1 で実際に動かして確かめる。判定は、終了コードとファイルの状態で行う(画面の日本語では見ない)。
/// 動かすのは、テストごとの一時フォルダの中のコピーと、そこに作った根だけ(<see cref="ScriptEntrance"/> の決まり)。
/// 終了コード: 0 = 終わった・消す物が無かった / 2 = 始める前に止めた / 3 = 利用者がやめた / 4 = 消せない物があった。
/// </summary>
public sealed class RemoveDataRunTests
{
    private static IReadOnlyList<string> Args(string root, params string[] more) => ["-DataRoot", root, .. more];

    private static ScriptResult Run(TempArea area, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env = null) =>
        ScriptEntrance.RunScript(area.AppFolder, args, env);

    [Fact]
    public void Models_and_thumbnails_mode_removes_the_model_files_part_files_and_thumbnails_and_leaves_everything_else_untouched()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout layout = DataRootLayout.CreateFull(root);
        Dictionary<string, string> before = DataRootLayout.SnapshotFiles(root);

        ScriptResult r = Run(area, Args(root, "-Mode", "ModelsAndThumbnails", "-Yes"));

        Assert.Equal(0, r.ExitCode);
        Dictionary<string, string> after = DataRootLayout.SnapshotFiles(root);
        foreach (string gone in layout.ModelsAndThumbnailFiles) Assert.False(File.Exists(gone), gone);
        foreach (string other in layout.OtherFiles) Assert.True(File.Exists(other), other);
        // サムネイルの枝のフォルダは、空になるので消える。根と、ログのフォルダは残る
        Assert.False(Directory.Exists(Path.Combine(root, "thumbs")));
        Assert.False(Directory.Exists(Path.Combine(root, "models")));
        Assert.True(Directory.Exists(Path.Combine(root, "logs")));
        // DB(-wal・-shm を含む)・バックアップ・pid・ログは、バイト単位で同じ
        Dictionary<string, string> expected = before.Where(kv => !layout.ModelsAndThumbnailFiles.Contains(Path.Combine(root, kv.Key))).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected.OrderBy(kv => kv.Key), after.OrderBy(kv => kv.Key));
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void Models_and_thumbnails_mode_reports_that_thumbnails_are_rebuilt_and_the_all_mode_does_not_say_so()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout.CreateFull(root);

        ScriptResult light = Run(area, Args(root, "-Mode", "ModelsAndThumbnails", "-Yes"));
        Assert.Equal(0, light.ExitCode);
        Assert.Contains("サムネイルは、次に一覧や詳細を表示したときに作り直されます。", light.Output, StringComparison.Ordinal);

        DataRootLayout.CreateFull(root);
        ScriptResult all = Run(area, Args(root, "-Mode", "All", "-Yes"));
        Assert.Equal(0, all.ExitCode);
        Assert.DoesNotContain("作り直されます", all.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_old_mode_name_Models_is_not_accepted_and_nothing_is_removed()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout.CreateFull(root);
        Dictionary<string, string> before = DataRootLayout.SnapshotFiles(root);

        ScriptResult r = Run(area, Args(root, "-Mode", "Models", "-Yes"));

        Assert.Equal(1, r.ExitCode);
        Assert.Equal(before.OrderBy(kv => kv.Key), DataRootLayout.SnapshotFiles(root).OrderBy(kv => kv.Key));
    }

    [Fact]
    public void All_mode_removes_the_data_folder_itself_when_nothing_unknown_is_left()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout.CreateFull(root);

        Assert.Equal(0, Run(area, Args(root, "-Mode", "ModelsAndThumbnails", "-Yes")).ExitCode);
        Assert.True(Directory.Exists(root));
        Assert.Equal(0, Run(area, Args(root, "-Mode", "All", "-Yes")).ExitCode);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Unknown_files_and_folders_stay_along_with_the_folders_that_hold_them_and_the_data_folder()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout layout = DataRootLayout.CreateFull(root);
        string[] unknown =
        [
            Path.Combine(root, "memo", "notes.txt"),                                       // 根の直下の知らないフォルダ
            Path.Combine(root, "readme.txt"),                                              // 根の直下の知らないファイル
            Path.Combine(root, "logs", "my-notes.txt"),                                    // logs の、型に合わない名前
            Path.Combine(root, "thumbs", "v1", "ab", "photo.png"),                         // thumbs の、型に合わない画像
            Path.Combine(layout.EmbeddingDirectory, "visual", "extra.bin"),                // モデルのフォルダの、余分なファイル
        ];
        foreach (string u in unknown) DataRootLayout.WriteFile(u);
        Dictionary<string, string> before = DataRootLayout.SnapshotFiles(root);

        ScriptResult r = Run(area, Args(root, "-Mode", "All", "-Yes"));

        Assert.Equal(0, r.ExitCode);
        foreach (string known in layout.AllFiles) Assert.False(File.Exists(known), known);
        foreach (string u in unknown)
        {
            Assert.True(File.Exists(u), u);
            for (string? dir = Path.GetDirectoryName(u); dir is not null && dir.Length >= root.Length; dir = Path.GetDirectoryName(dir))
                Assert.True(Directory.Exists(dir), dir);                                   // 抱えるフォルダと、根まで残る
        }
        Dictionary<string, string> after = DataRootLayout.SnapshotFiles(root);
        Assert.Equal(unknown.Select(u => Path.GetRelativePath(root, u)).Order(StringComparer.OrdinalIgnoreCase), after.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (string u in unknown) Assert.Equal(before[Path.GetRelativePath(root, u)], after[Path.GetRelativePath(root, u)]);
    }

    [Fact]
    public void A_junction_inside_thumbs_is_not_entered_and_its_target_outside_the_data_folder_stays()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout.CreateFull(root);
        string outside = area.Combine("outside");
        string targetFile = Path.Combine(outside, DataRootLayout.Hex64 + "_100x100.jpg");   // サムネイルの名前の型に合う
        DataRootLayout.WriteFile(targetFile);
        string junction = Path.Combine(root, "thumbs", "v1", "cd");                          // 2 桁の 16 進(サムネイルの枝の名前の型に合う)
        ScriptEntrance.CreateJunction(junction, outside);
        try
        {
            ScriptResult r = Run(area, Args(root, "-Mode", "All", "-Yes"));

            Assert.Equal(0, r.ExitCode);
            Assert.True(File.Exists(targetFile));
            Assert.True(Directory.Exists(junction));
            Assert.True(new DirectoryInfo(junction).LinkTarget is not null);                 // リンクのまま残っている
            Assert.True(Directory.Exists(root));                                             // リンクを抱える根も残る
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);                      // リンクだけ外す(向き先には触れない)
        }
    }

    [Fact]
    public void Backup_files_of_every_numbering_and_part_and_tmp_files_are_all_removed()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout layout = DataRootLayout.CreateFull(root);
        string[] extra =
        [
            Path.Combine(root, "ohanami.db.bak-v2"),
            Path.Combine(root, "ohanami.db.bak-v2-2"),
            Path.Combine(root, "ohanami.db.bak-v10"),
        ];
        foreach (string e in extra) DataRootLayout.WriteFile(e);
        string part = layout.ModelFiles.First(f => f.EndsWith(".part", StringComparison.Ordinal));
        string pidTmp = layout.OtherFiles.First(f => f.EndsWith(".tmp", StringComparison.Ordinal) && Path.GetFileName(f).StartsWith("llama-server.pid.", StringComparison.Ordinal));
        string thumbTmp = layout.ThumbnailFiles.First(f => f.EndsWith(".tmp", StringComparison.Ordinal) && Path.GetFileName(f).Contains(".jpg.", StringComparison.Ordinal));

        ScriptResult r = Run(area, Args(root, "-Mode", "All", "-Yes"));

        Assert.Equal(0, r.ExitCode);
        foreach (string f in extra.Append(part).Append(pidTmp).Append(thumbTmp)) Assert.False(File.Exists(f), f);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void While_the_app_holds_its_single_instance_name_nothing_is_removed_and_after_it_is_released_the_run_removes()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout.CreateFull(root);
        Dictionary<string, string> before = DataRootLayout.SnapshotFiles(root);

        SingleInstance instance = SingleInstance.Acquire(root);
        try
        {
            Assert.True(instance.IsOwner);
            Assert.Equal(2, Run(area, Args(root, "-Mode", "All", "-Yes")).ExitCode);
            Assert.Equal(before.OrderBy(kv => kv.Key), DataRootLayout.SnapshotFiles(root).OrderBy(kv => kv.Key));
        }
        finally
        {
            instance.Dispose();
        }

        Assert.Equal(0, Run(area, Args(root, "-Mode", "All", "-Yes")).ExitCode);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Without_a_mode_the_run_asks_and_when_input_is_closed_it_stops_without_removing()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout.CreateFull(root);
        Dictionary<string, string> before = DataRootLayout.SnapshotFiles(root);

        ScriptResult r = Run(area, Args(root));

        Assert.Equal(3, r.ExitCode);
        Assert.Equal(before.OrderBy(kv => kv.Key), DataRootLayout.SnapshotFiles(root).OrderBy(kv => kv.Key));
    }

    [Fact]
    public void A_relative_data_root_is_refused_whether_it_comes_from_the_argument_or_the_environment_variable()
    {
        using TempArea area = TempArea.Create();

        Assert.Equal(2, Run(area, ["-DataRoot", @"relative\data", "-ListOnly"]).ExitCode);

        var env = new Dictionary<string, string> { [AppPaths.DataDirectoryVariable] = @"relative\data" };
        Assert.Equal(2, Run(area, ["-ListOnly"], env).ExitCode);
    }

    [Fact]
    public void The_data_folder_chosen_by_the_environment_variable_is_the_same_full_path_the_app_resolves()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout.CreateFull(root);
        Dictionary<string, string> before = DataRootLayout.SnapshotFiles(root);

        var env = new Dictionary<string, string> { [AppPaths.DataDirectoryVariable] = root };
        ScriptResult r = Run(area, ["-ListOnly"], env);

        string appResolved = AppPaths.Resolve(name => env.GetValueOrDefault(name), area.Dir, area.Dir, OhanamiCore.ModelDirectoryName).DataDirectory;
        Assert.Equal(0, r.ExitCode);
        Assert.Contains(appResolved, r.Output, StringComparison.Ordinal);
        Assert.Equal(before.OrderBy(kv => kv.Key), DataRootLayout.SnapshotFiles(root).OrderBy(kv => kv.Key));
    }

    [Fact]
    public void Yes_without_a_mode_stops_before_starting_and_removes_nothing()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout.CreateFull(root);
        Dictionary<string, string> before = DataRootLayout.SnapshotFiles(root);

        ScriptResult r = Run(area, Args(root, "-Yes"));

        Assert.Equal(2, r.ExitCode);
        Assert.Equal(before.OrderBy(kv => kv.Key), DataRootLayout.SnapshotFiles(root).OrderBy(kv => kv.Key));
    }

    [Fact]
    public void A_data_folder_that_does_not_exist_is_nothing_to_remove()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("no-such-data");

        ScriptResult r = Run(area, Args(root, "-Mode", "All", "-Yes"));

        Assert.Equal(0, r.ExitCode);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_data_folder_with_japanese_and_spaces_in_its_name_is_removed_and_its_single_instance_name_is_detected()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("データ の 置き場 ohanami");
        DataRootLayout.CreateFull(root);

        using (SingleInstance instance = SingleInstance.Acquire(root))
        {
            Assert.True(instance.IsOwner);
            Assert.Equal(2, Run(area, Args(root, "-Mode", "All", "-Yes")).ExitCode);
            Assert.True(Directory.Exists(root));
        }

        Assert.Equal(0, Run(area, Args(root, "-Mode", "All", "-Yes")).ExitCode);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void The_cmd_file_passes_its_arguments_through_and_its_exit_code_reaches_the_caller()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("データ の 置き場 ohanami");
        DataRootLayout.CreateFull(root);

        // 引数なしの停止(-Yes だけ → 終了コード 2)が、cmd.exe の外まで届く
        ScriptResult stopped = ScriptEntrance.RunCmd(area.AppFolder, Args(root, "-Yes"));
        Assert.Equal(2, stopped.ExitCode);
        Assert.True(Directory.Exists(root));

        ScriptResult done = ScriptEntrance.RunCmd(area.AppFolder, Args(root, "-Mode", "All", "-Yes"));
        Assert.Equal(0, done.ExitCode);
        Assert.False(Directory.Exists(root));
    }
}
