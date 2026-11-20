using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// 「Ohanami が PC に残す物は、データの場所(%LOCALAPPDATA%\Ohanami)の下の、決まった名前の物だけ」の見張り。
/// データ削除の道具(src/Ohanami.RemoveData/Ohanami-RemoveData.ps1)は、その名前と型の表だけを頼りに消す。アプリが別の物を書き始めると、道具が消し残す(または、知らないまま残す)。
/// 製品のコード(src/ のすべて。コメントは除く)を読んで、ファイルやフォルダを書く式を、許可の表(式・許すファイル・理由)で決める。
/// 表に無いファイルが書けば落ちる。落ちたら、(1) 書く物の名前と置き場を決め、(2) 道具の表(.ps1 の「名前と型の表」)に足し、
/// (3) tests/Ohanami.RemoveData.Tests の根(DataRootLayout と RealWritersTests)に足し、(4) 道具の一覧を載せた ADR 0052 を直し、(5) この表に足す。
/// 理由の欄は、その書き手が書く物と、それを消す道具の型を示す。
/// 見張れない範囲: 型の名前を書かない呼び出し(リフレクション・別のアセンブリ経由)、変数で受けた FileInfo・DirectoryInfo の Create・Replace(string.Replace や別の Create と、書き方が同じ。
/// MoveTo・CreateText・AppendText・OpenWrite と、new FileInfo(...) に続けた Create・Replace・CopyTo は拾う。CopyTo は、メモリの写しと区別できないので、使うファイルを表で決める)、NuGet のパッケージが自分で書く物(Core・App は、書くパッケージを使っていない。SQLite の -wal / -shm は
/// DB を開いている間だけ SQLite が作る)。
/// </summary>
public sealed class WritePlacesGuardTests
{
    /// <summary>(検出する式, その式を使ってよいファイル名, 理由)。表に無いファイルが使えば違反。</summary>
    private static readonly (string Pattern, string[] AllowedFiles, string Why)[] Rules =
    [
        (@"\bFile\.(WriteAll\w*|AppendAll\w*|Create|CreateText|AppendText|OpenWrite|Open|Move|Copy|Replace)\b",
            ["FileCoreLog.cs", "ThumbnailService.cs", "ChildProcessGuard.cs", "ModelDownloader.cs"],
            "ファイルを書く・置く・動かす(FileCoreLog = logs\\ohanami-yyyyMMdd.log。ThumbnailService = thumbs\\v<N>\\<2 桁>\\<ハッシュ>_<幅>x<高さ>.jpg とその .<32 桁>.tmp。"
            + "ChildProcessGuard = llama-server.pid とその .<32 桁>.tmp。ModelDownloader = モデルのファイルとその .part)"),
        (@"\bFileMode\.(Create|CreateNew|Append|OpenOrCreate|Truncate)\b|\bFileAccess\.(Write|ReadWrite)\b",
            ["ModelDownloader.cs", "LlamaServer.cs"],
            "書き込みで開く(ModelDownloader = モデルの .part。LlamaServer = logs\\llama-server-yyyyMMdd-HHmmss-fff.log)"),
        (@"\bStreamWriter\b|\bFile\.CreateText\b", ["LlamaServer.cs"], "テキストのファイルへの書き込み(LlamaServer = logs\\llama-server-….log)"),
        (@"\bDirectory\.(CreateDirectory|CreateSubdirectory)\b|\.CreateSubdirectory\(",
            ["FileCoreLog.cs", "SetupWindow.cs", "OhanamiDatabase.cs", "ModelDownloader.cs", "ThumbnailService.cs", "ChildProcessGuard.cs", "LlamaServer.cs"],
            "フォルダを作る(データの場所・logs・thumbs・models の下だけ。SetupWindow は、モデルのフォルダを作ってから開く。道具は、空になった既知のフォルダを消す)"),
        (@"VACUUM\s+INTO", ["OhanamiDatabase.cs"], "移行の前のバックアップ ohanami.db.bak-v<N>[-<n>]"),
        (@"\bnew\s+SqliteConnection\(|\bSqliteConnectionStringBuilder\b", ["OhanamiDatabase.cs"], "DB を開く(ohanami.db と -wal / -shm)。DB を開くのは、この 1 ファイルだけ"),
        (@"\bEnvironment\.SpecialFolder\b|\bEnvironment\.GetFolderPath\b|\bPath\.GetTempPath\b|\bPath\.GetTempFileName\b|\bPath\.GetRandomFileName\b", ["AppPaths.cs"],
            "データの場所を決める(既定は %LOCALAPPDATA%\\Ohanami。道具は同じ決め方を写している)。一時フォルダなど、ほかの置き場は使わない"),
        (@"\bMicrosoft\.Win32\.Registry\b|\bRegistryKey\b|\bIsolatedStorage\w*\b", [], "レジストリ・分離ストレージ(道具は、ファイルしか消さない。使うなら道具と ADR に足す)"),
        // FileInfo・DirectoryInfo の書く側のメソッド、Directory.Move、ほかの File の書く API(変数の型を問わず、メソッドの名前で拾う)
        (@"\bDirectory\.Move\b|\.(MoveTo|CreateText|AppendText|OpenWrite|CreateAsSymbolicLink)\(|\bFile\.(OpenHandle|Encrypt|Decrypt|CreateSymbolicLink)\b|\bDirectory\.CreateSymbolicLink\b"
            + @"|\bnew\s+(FileInfo|DirectoryInfo)\s*\([^;]*?\)\s*\.(Create|Replace|CopyTo)\(",
            [], "FileInfo・DirectoryInfo・Directory の移す・作る・書く(使うなら、書く物の名前と置き場を決めて、道具と ADR に足し、この表に許すファイルを足す)"),
        // FileInfo.CopyTo は、Span・Stream の CopyTo(メモリや別のストリームへの写し)と書き方が同じで、拾い分けられない。使うファイルを表で決める。
        // 表のファイルが、ファイルのコピーを足していないことは、レビューで見る
        (@"\.CopyTo(Async)?\(", ["Siglip2Tokenizer.cs", "VectorTable.cs"], "CopyTo(Siglip2Tokenizer・VectorTable = メモリの Span の写し。ファイルのコピーではない)"),
        // Win32 のファイル・フォルダの操作を、P/Invoke で直に呼ぶ(今は、どのファイルも呼んでいない)
        (@"(?<!Directory\.)\b(CreateFile|MoveFile|CopyFile|ReplaceFile|WriteFile|CreateHardLink|CreateSymbolicLink|CreateDirectory|SHFileOperation|SHCreateDirectory)(Ex|2|Transacted)?[AW]?\s*\(", [],
            "Win32 のファイル・フォルダの操作(P/Invoke)"),
    ];

    private static IEnumerable<string> SourceFiles()
    {
        string src = Path.Combine(RepoPaths.Root, "src");
        return Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase));
    }

    private static List<(string File, string Code)> ReadSources() =>
        [.. SourceFiles().Select(f => (Path.GetFileName(f), CommentStripper.StripCSharp(File.ReadAllText(f))))];

    [Fact]
    public void Only_the_allowed_files_write_files_or_folders_and_nothing_else_in_the_product_does()
    {
        List<(string File, string Code)> sources = ReadSources();
        Assert.True(sources.Count >= 100, $"ソースを読めていない(読んだのは {sources.Count} 個)");

        var violations = new List<string>();
        foreach ((string pattern, string[] allowed, string why) in Rules)
        {
            foreach (string file in NetworkBoundaryGuardTests.FilesMatching(pattern, sources))
            {
                if (!allowed.Contains(file, StringComparer.Ordinal)) violations.Add($"{file}: {why}");
            }
        }

        Assert.True(violations.Count == 0,
            "データ削除の道具が知らない書き方がある(道具の表と ADR 0052 と tests/Ohanami.RemoveData.Tests も直す):\n" + string.Join("\n", violations));
    }

    [Fact]
    public void The_allowed_files_really_exist_and_use_what_the_table_lets_them_use()
    {
        // 表が古くなって(ファイル名が変わって)、何も見張らないまま通ることを防ぐ
        List<(string File, string Code)> sources = ReadSources();
        foreach ((string pattern, string[] allowed, string why) in Rules)
        {
            foreach (string file in allowed)
            {
                Assert.True(sources.Any(s => s.File == file), $"表のファイルが無い: {file}({why})");
                Assert.Contains(file, NetworkBoundaryGuardTests.FilesMatching(pattern, sources));   // 許したのに、使っていないファイルは、表から外す
            }
        }
    }

    [Fact]
    public void The_thumbnail_cache_names_are_the_ones_the_data_removal_script_expects()
    {
        // 道具は、thumbs\v<N>\<ハッシュの先頭 2 桁>\<ハッシュ>_<幅>x<高さ>.jpg と、その後ろに .<32 桁の 16 進>.tmp が付いた名前を消す。
        // 一時ファイルは改名で消えて観測できないので、書き手のソースを読む(.jpg の側は、tests/Ohanami.RemoveData.Tests が本物の書き手で確かめる)。
        string source = CommentStripper.StripCSharp(File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "Ohanami.Core", "Thumbnails", "ThumbnailService.cs")));
        Assert.Contains("path + \".\" + Guid.NewGuid().ToString(\"N\") + \".tmp\"", source, StringComparison.Ordinal);
        Assert.Contains("\"v\" + CacheFormatVersion.ToString(CultureInfo.InvariantCulture), hash[..2]", source, StringComparison.Ordinal);
        Assert.Contains("$\"{hash}_{boxWidthPx}x{boxHeightPx}.jpg\"", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("File.WriteAllBytes(path, bytes);", 0)]
    [InlineData("File.Move(a, b);", 0)]
    [InlineData("using var s = new FileStream(p, FileMode.Create, FileAccess.Write);", 1)]
    [InlineData("var o = new FileStreamOptions { Access = FileAccess.ReadWrite };", 1)]
    [InlineData("using var w = new StreamWriter(stream);", 2)]
    [InlineData("Directory.CreateDirectory(dir);", 3)]
    [InlineData("var d = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);", 6)]
    [InlineData("using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(\"x\");", 7)]
    [InlineData("Directory.Move(a, b);", 8)]
    [InlineData("info.MoveTo(target);", 8)]
    [InlineData("using var w = info.CreateText();", 8)]
    [InlineData("using var w = file.AppendText();", 8)]
    [InlineData("using var o = file.OpenWrite();", 8)]
    [InlineData("using var h = File.OpenHandle(p, FileMode.Create);", 8)]
    [InlineData("new FileInfo(p).Create();", 8)]
    [InlineData("new FileInfo(p).Replace(dest, backup);", 8)]
    [InlineData("new FileInfo(Path.Combine(a, b)).CopyTo(dest);", 8)]
    [InlineData("File.CreateSymbolicLink(a, b);", 8)]
    [InlineData("info.CopyTo(dest, true);", 9)]
    [InlineData("await source.CopyToAsync(destination);", 9)]
    [InlineData("[DllImport(\"kernel32.dll\")] static extern IntPtr CreateFileW(string name);", 10)]
    [InlineData("SHFileOperation(ref op);", 10)]
    [InlineData("CopyFileExW(a, b, null, 0, 0, 0);", 10)]
    public void The_rules_detect_what_they_are_meant_to_detect(string code, int rule)
    {
        Assert.Matches(Rules[rule].Pattern, code);
    }

    [Theory]
    [InlineData("var x = File.ReadAllText(path);")]
    [InlineData("string s = text.Replace(a, b);")]
    [InlineData("var c = factory.Create();")]
    [InlineData("var info = new FileInfo(path); long n = info.Length;")]
    [InlineData("foreach (FileInfo f in new DirectoryInfo(d).GetFiles(\"*.log\")) { }")]
    [InlineData("using var s = new FileStream(p, FileMode.Open, FileAccess.Read);")]
    [InlineData("await ModelRegistry.EnsureEmbeddingModelRegisteredAsync(db);")]
    public void The_rules_do_not_fire_on_reading_or_on_unrelated_names(string code)
    {
        Assert.All(Rules, r => Assert.DoesNotMatch(r.Pattern, code));
    }
}
