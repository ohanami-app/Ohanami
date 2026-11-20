using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ohanami.App;
using Xunit;

namespace Ohanami.RemoveData.Tests;

/// <summary>
/// 道具(.ps1)の、止まり方・残し方・対話・根の綴りの確かめ。判定は、終了コードとファイルの状態で行う。
/// 終了コード: 0 = 終わった・消す物が無かった / 2 = 始める前に止めた / 3 = 利用者がやめた / 4 = 消せない物・調べられない所があった。
/// </summary>
public sealed class RemoveDataSafetyTests
{
    private static IReadOnlyList<string> Args(string root, params string[] more) => ["-DataRoot", root, .. more];

    private static ScriptResult Run(TempArea area, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env = null, IReadOnlyList<string>? stdin = null) =>
        ScriptEntrance.RunScript(area.AppFolder, args, env, stdin);

    private static void AssertUntouched(string root, Dictionary<string, string> before) =>
        Assert.Equal(before.OrderBy(kv => kv.Key), DataRootLayout.SnapshotFiles(root).OrderBy(kv => kv.Key));

    // ---------------------------------------------------------------- 標準入力に流した行での対話(-Mode を付けない実行)

    // 全角の数字と文字は、標準入力を UTF-8 で読む .ps1 の側(Console.InputEncoding)で、半角に直して読む。
    // 子プロセスの標準入力は、入口が UTF-8 で書く(ScriptEntrance)。
    [Theory]
    [InlineData("1", "Models")]
    [InlineData("2|y", "All")]
    [InlineData("2|n", "Nothing")]
    [InlineData("2", "Nothing")]                // 「y」を答える前に、入力が閉じる
    [InlineData("", "Nothing")]                 // 空の行
    [InlineData("3", "Nothing")]                // 知らない番号
    [InlineData("１", "Models")]                // 全角の 1
    [InlineData("２|ｙ", "All")]                // 全角の 2 と y
    [InlineData("２|Ｙ", "All")]                // 全角の大文字の Y
    [InlineData("  2  | Y ", "All")]            // 前後の空白と大文字
    public void A_run_without_a_mode_follows_the_lines_given_on_standard_input(string lines, string expected)
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout layout = DataRootLayout.CreateFull(root);
        Dictionary<string, string> before = DataRootLayout.SnapshotFiles(root);

        ScriptResult r = Run(area, Args(root), stdin: lines.Split('|'));

        switch (expected)
        {
            case "Models":
                Assert.Equal(0, r.ExitCode);
                foreach (string model in layout.ModelFiles) Assert.False(File.Exists(model), model);
                foreach (string other in layout.OtherFiles) Assert.True(File.Exists(other), other);
                break;
            case "All":
                Assert.Equal(0, r.ExitCode);
                Assert.False(Directory.Exists(root));
                break;
            default:
                Assert.Equal(3, r.ExitCode);
                AssertUntouched(root, before);
                break;
        }
    }

    // ---------------------------------------------------------------- 根の綴り

    public static TheoryData<string> UnusableRoots =>
    [
        @"C:\", @"C:\.", @"C:\x\..", @"c:\", @"C:\\",
        @"\\?\C:\x", @"\\.\C:\x", @"\\?\UNC\server\share\x", @"\\?\C:\", @"\\.\PhysicalDrive0",
        // スラッシュの綴りでも、ドライブ直下・デバイスパスは拒む。ドライブ文字だけの C:data・C: と、区切り 1 つの \data・/data は、相対なので拒む
        "C:/", "C:/.", "C://", "//?/C:/x", @"\/?\C:\x", "//./C:/x", "//server/share", @"\\server\share",
        "C:data", "C:", @"\data", "/data",
    ];

    // 削除のモードは入口が拒むので、-ListOnly で、根の決め方の段階の拒否だけを見る
    [Theory]
    [MemberData(nameof(UnusableRoots))]
    public void A_drive_root_or_a_device_path_is_refused_as_the_data_root(string root)
    {
        using TempArea area = TempArea.Create();

        Assert.Equal(2, Run(area, [.. Args(root), "-ListOnly"]).ExitCode);

        var env = new Dictionary<string, string> { [AppPaths.DataDirectoryVariable] = root };
        Assert.Equal(2, Run(area, ["-ListOnly"], env).ExitCode);
    }

    // アプリは、データの場所を Path.GetFullPath に通す。区切りが / の絶対パスも、同じ根を指すので、道具も絶対パスとして受ける
    [Fact]
    public void A_data_root_written_with_forward_slashes_is_an_absolute_path_and_means_the_same_folder()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout layout = DataRootLayout.CreateFull(root);
        string slashed = root.Replace('\\', '/');
        Dictionary<string, string> before = DataRootLayout.SnapshotFiles(root);

        var env = new Dictionary<string, string> { [AppPaths.DataDirectoryVariable] = slashed };
        ScriptResult listed = Run(area, ["-ListOnly"], env);
        Assert.Equal(0, listed.ExitCode);
        Assert.Contains(Path.GetFullPath(slashed), listed.Output, StringComparison.Ordinal);

        using (SingleInstance instance = SingleInstance.Acquire(root))                       // 動いているアプリは、/ の綴りでも見つかる
        {
            Assert.True(instance.IsOwner);
            Assert.Equal(2, Run(area, Args(slashed, "-Mode", "All", "-Yes")).ExitCode);
            Assert.Equal(2, Run(area, Args(slashed + "/", "-Mode", "Models", "-Yes")).ExitCode);
        }
        AssertUntouched(root, before);

        Assert.Equal(0, Run(area, Args(slashed, "-Mode", "Models", "-Yes")).ExitCode);
        foreach (string model in layout.ModelFiles) Assert.False(File.Exists(model), model);
        Assert.True(File.Exists(layout.DatabasePath));
        Assert.Equal(0, Run(area, Args(slashed, "-Mode", "All", "-Yes")).ExitCode);
        Assert.False(Directory.Exists(root));
    }

    // ---------------------------------------------------------------- アプリが動いているかの確かめ

    [Fact]
    public void The_single_instance_name_is_found_whatever_the_case_or_trailing_separator_of_the_root_is()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout.CreateFull(root);
        Dictionary<string, string> before = DataRootLayout.SnapshotFiles(root);

        using SingleInstance instance = SingleInstance.Acquire(root);
        Assert.True(instance.IsOwner);
        foreach (string spelling in new[] { root.ToUpperInvariant(), root.ToLowerInvariant(), root + @"\", root + @"\\", root + @"\.", Path.Combine(root, "..", "data") })
        {
            Assert.Equal(2, Run(area, Args(spelling, "-Mode", "All", "-Yes")).ExitCode);
            Assert.Equal(2, Run(area, Args(spelling, "-Mode", "Models", "-Yes")).ExitCode);
        }

        AssertUntouched(root, before);
    }

    // Mutex の名前に頼らない受け皿: データベースのファイルが開かれていれば、どのモードでも、何も消さずに止まる
    [Fact]
    public void In_the_models_mode_a_database_held_open_stops_the_run_before_any_model_is_removed()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout layout = DataRootLayout.CreateFull(root);
        Dictionary<string, string> before = DataRootLayout.SnapshotFiles(root);

        using (new FileStream(layout.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))   // SQLite の開き方と同じ(共有あり)
        {
            Assert.Equal(2, Run(area, Args(root, "-Mode", "Models", "-Yes")).ExitCode);
            Assert.Equal(2, Run(area, Args(root, "-Mode", "All", "-Yes")).ExitCode);
        }

        AssertUntouched(root, before);
    }

    [Theory]
    [InlineData("ohanami.db-wal")]
    [InlineData("ohanami.db-shm")]
    public void In_the_all_mode_a_wal_or_shm_file_held_open_stops_the_run_before_the_database_is_removed(string heldName)
    {
        // -wal だけが開かれているとき、本体を先に消すと、-wal と -shm が孤児で残り、次に作られる DB と並ぶ
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout.CreateFull(root);
        Dictionary<string, string> before = DataRootLayout.SnapshotFiles(root);

        using (new FileStream(Path.Combine(root, heldName), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.Equal(2, Run(area, Args(root, "-Mode", "All", "-Yes")).ExitCode);
        }

        AssertUntouched(root, before);
    }

    [Fact]
    public void A_wal_file_that_cannot_be_removed_stops_the_run_with_the_database_set_handled_first()
    {
        // 開かれてはいないが消せない -wal(読み取り専用): データベースの組を最初に消す途中で失敗するので、残りの物には手を付けずに 4 で止まる
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout layout = DataRootLayout.CreateFull(root);
        string wal = Path.Combine(root, "ohanami.db-wal");
        File.SetAttributes(wal, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal(4, Run(area, Args(root, "-Mode", "All", "-Yes")).ExitCode);

            Assert.True(File.Exists(wal));
            foreach (string model in layout.ModelFiles) Assert.True(File.Exists(model), model);                               // モデルにも手を付けない
            Assert.True(File.Exists(Path.Combine(root, "llama-server.pid")));
            Assert.True(File.Exists(Path.Combine(root, "logs", "ohanami-20261120.log")));
        }
        finally
        {
            File.SetAttributes(wal, FileAttributes.Normal);
        }
    }

    // ---------------------------------------------------------------- 調べられなかった所

    // 「消す物が見つからなかった」と「調べられなかった」は別。調べ残しがあれば、報告を出して 4(0 にしない)。
    // 読めないフォルダは、自分の一覧の読み取りを拒否する ACE を付けて作る(icacls。一時フォルダの中だけ。終わったら外す)
    private static void AssertListingIsDenied(string directory) =>
        Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFiles(directory));

    [Fact]
    public void An_unreadable_models_folder_with_nothing_else_to_remove_is_reported_with_exit_code_4()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        string models = Path.Combine(root, "models");
        Directory.CreateDirectory(models);

        using (ScriptEntrance.DenyListing(models))
        {
            AssertListingIsDenied(models);
            Assert.Equal(0, Run(area, [.. Args(root), "-ListOnly"]).ExitCode);              // 一覧は、調べられなかったことを書いて 0
            Assert.Equal(4, Run(area, Args(root, "-Mode", "Models", "-Yes")).ExitCode);
            Assert.Equal(4, Run(area, Args(root, "-Mode", "All", "-Yes")).ExitCode);
        }

        Assert.True(Directory.Exists(models));
    }

    [Fact]
    public void An_unreadable_data_folder_is_reported_with_exit_code_4_in_both_modes()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout layout = DataRootLayout.CreateFull(root);

        using (ScriptEntrance.DenyListing(root))
        {
            AssertListingIsDenied(root);
            Assert.Equal(4, Run(area, Args(root, "-Mode", "Models", "-Yes")).ExitCode);
            Assert.Equal(4, Run(area, Args(root, "-Mode", "All", "-Yes")).ExitCode);
        }

        foreach (string f in layout.AllFiles) Assert.True(File.Exists(f), f);               // 何も消えていない
    }

    [Fact]
    public void An_unreadable_folder_does_not_stop_the_other_files_from_being_removed_but_the_run_ends_with_4()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout layout = DataRootLayout.CreateFull(root);
        string thumbs = Path.Combine(root, "thumbs");

        using (ScriptEntrance.DenyListing(thumbs))
        {
            AssertListingIsDenied(thumbs);
            Assert.Equal(4, Run(area, Args(root, "-Mode", "All", "-Yes")).ExitCode);

            foreach (string f in layout.ModelFiles) Assert.False(File.Exists(f), f);
            Assert.False(File.Exists(layout.DatabasePath));
            Assert.True(Directory.Exists(root));                                               // 根は、中に残るので消えない
        }
    }

    // ---------------------------------------------------------------- 空のフォルダだけが残っているとき

    [Fact]
    public void Empty_known_folders_alone_are_removed_by_the_all_mode_together_with_the_data_folder()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout layout = DataRootLayout.CreateFull(root);
        Assert.Equal(0, Run(area, Args(root, "-Mode", "All", "-Yes")).ExitCode);            // 全部消した結果
        Directory.CreateDirectory(Path.Combine(layout.EmbeddingDirectory, "visual"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Path.Combine(root, "thumbs", "v1", "ab"));

        Assert.Equal(0, Run(area, Args(root, "-Mode", "All", "-Yes")).ExitCode);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Empty_model_folders_alone_are_removed_by_the_models_mode_and_the_data_folder_stays()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        DataRootLayout layout = DataRootLayout.CreateFull(root);
        Assert.Equal(0, Run(area, Args(root, "-Mode", "Models", "-Yes")).ExitCode);
        Directory.CreateDirectory(Path.Combine(layout.EmbeddingDirectory, "visual"));

        Assert.Equal(0, Run(area, Args(root, "-Mode", "Models", "-Yes")).ExitCode);

        Assert.False(Directory.Exists(Path.Combine(root, "models")));
        Assert.True(File.Exists(layout.DatabasePath));
    }

    [Fact]
    public void An_empty_data_folder_is_removed_by_the_all_mode_and_left_by_the_models_mode()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        Directory.CreateDirectory(root);

        Assert.Equal(0, Run(area, Args(root, "-Mode", "Models", "-Yes")).ExitCode);
        Assert.True(Directory.Exists(root));
        Assert.Equal(0, Run(area, Args(root, "-Mode", "All", "-Yes")).ExitCode);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_data_folder_holding_only_an_unknown_file_is_left_and_the_run_is_not_an_error()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        string unknown = Path.Combine(root, "notes.txt");
        DataRootLayout.WriteFile(unknown);

        Assert.Equal(0, Run(area, Args(root, "-Mode", "All", "-Yes")).ExitCode);

        Assert.True(File.Exists(unknown));
        Assert.True(Directory.Exists(root));
    }

    // ---------------------------------------------------------------- 根がリンク

    [Fact]
    public void A_data_folder_that_is_a_junction_keeps_the_link_and_is_emptied_inside()
    {
        using TempArea area = TempArea.Create();
        string real = area.Combine("real-data");
        DataRootLayout layout = DataRootLayout.CreateFull(real);
        string unknown = Path.Combine(real, "memo.txt");
        DataRootLayout.WriteFile(unknown);
        string link = area.Combine("link-data");
        ScriptEntrance.CreateJunction(link, real);
        try
        {
            ScriptResult r = Run(area, Args(link, "-Mode", "All", "-Yes"));

            Assert.Equal(0, r.ExitCode);
            Assert.True(Directory.Exists(link));
            Assert.True(new DirectoryInfo(link).LinkTarget is not null);                    // リンクのまま残っている
            Assert.True(Directory.Exists(real));                                            // 向き先も残る
            Assert.True(File.Exists(unknown));                                              // 知らない物は残る
            foreach (string f in layout.AllFiles) Assert.False(File.Exists(f), f);          // 知っている物は、リンク越しに消える
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);                             // リンクだけ外す(向き先には触れない)
        }
    }

    [Fact]
    public void A_data_folder_that_is_a_junction_to_an_empty_folder_is_not_unlinked()
    {
        using TempArea area = TempArea.Create();
        string real = area.Combine("real-empty");
        Directory.CreateDirectory(real);
        string link = area.Combine("link-empty");
        ScriptEntrance.CreateJunction(link, real);
        try
        {
            Assert.Equal(0, Run(area, Args(link, "-Mode", "All", "-Yes")).ExitCode);

            Assert.True(Directory.Exists(link));
            Assert.True(new DirectoryInfo(link).LinkTarget is not null);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }
}
