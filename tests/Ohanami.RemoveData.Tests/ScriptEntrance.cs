using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Ohanami.App;

namespace Ohanami.RemoveData.Tests;

internal sealed record ScriptResult(int ExitCode, string Output, string Error);

/// <summary>
/// データ削除の道具(src/Ohanami.RemoveData/ の .ps1 と .cmd)を動かす、このプロジェクトで**ただ 1 つの入口**。
/// プロセスを起こす記述(<c>ProcessStartInfo</c>)は、このファイルにしか置かない(<c>SourceGuardTests</c> が、ソースを読んで見張る)。
/// 本物のデータ(%LOCALAPPDATA%\Ohanami)を消さないための決まりを、ここで強制する:
/// <list type="number">
/// <item>Windows PowerShell 5.1(<c>%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe</c>)を、フルパスで起動する。無ければ失敗。pwsh 7 は試さない
///   (利用者の PC にある物と同じ物で確かめる)。</item>
/// <item>リポジトリの 2 つのファイルを、テストごとの一時フォルダ(アプリのフォルダ)にコピーして、そのコピーを動かす。</item>
/// <item>消す動き(<c>-Mode</c> 付き)は、<c>-DataRoot</c> が一時フォルダの下を指しているときだけ動かす。違えば、動かさずに例外を投げる。</item>
/// <item><c>-DataRoot</c> が無い実行、または一時フォルダの外を指す実行は、<c>-ListOnly</c> だけ。</item>
/// <item>子プロセスの環境から <c>OHANAMI_DATA_DIR</c> と <c>OHANAMI_MODELS_DIR</c> を外す(テストが明示したときだけ付ける)。</item>
/// <item>標準入力はすぐ閉じる。時間切れで止める。標準出力は UTF-8 で読む。</item>
/// <item>結果は、終了コードとファイルの状態で見る(画面の日本語では見ない)。junction の作成(mklink /J)と cmd.exe の実行も、ここに置く。</item>
/// </list>
/// </summary>
internal static class ScriptEntrance
{
    public const string ScriptName = "Ohanami-RemoveData.ps1";
    public const string CmdName = "Ohanami-RemoveData.cmd";

    /// <summary>テストの一時フォルダの親。消す動きの <c>-DataRoot</c> は、この下だけ。</summary>
    public static readonly string TempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ohanami-removedata-tests"));

    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(2);

    public static string WindowsPowerShellPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    public static bool IsUnderTempRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        string full = Path.GetFullPath(path).TrimEnd('\\', '/');
        return full.StartsWith(TempRoot + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>リポジトリの 2 つのファイル(バイト列のまま)を、<paramref name="appFolder"/> にコピーする。コピーした .ps1 のパスを返す。</summary>
    public static string PrepareAppFolder(string appFolder)
    {
        RequireUnderTempRoot(appFolder);
        Directory.CreateDirectory(appFolder);
        string source = Path.Combine(RepoRoot.Path, "src", "Ohanami.RemoveData");
        foreach (string name in new[] { ScriptName, CmdName }) File.Copy(Path.Combine(source, name), Path.Combine(appFolder, name), overwrite: true);
        return Path.Combine(appFolder, ScriptName);
    }

    /// <summary>Windows PowerShell 5.1 で、コピーした .ps1 を <c>-File</c> で動かす。</summary>
    public static ScriptResult RunScript(string appFolder, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        CheckArguments(arguments);
        if (!File.Exists(WindowsPowerShellPath)) throw new FileNotFoundException("Windows PowerShell 5.1 が無い: " + WindowsPowerShellPath);
        string script = PrepareAppFolder(appFolder);

        var info = new ProcessStartInfo(WindowsPowerShellPath);
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(script);
        foreach (string a in arguments) info.ArgumentList.Add(a);
        return Start(info, environment);
    }

    /// <summary><c>cmd.exe /d /c ""&lt;コピーした .cmd&gt;" 引数…"</c> で動かす(.cmd が .ps1 に引数を渡し、終了コードを返すことを確かめるため)。</summary>
    public static ScriptResult RunCmd(string appFolder, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        CheckArguments(arguments);
        PrepareAppFolder(appFolder);
        string cmdFile = Path.Combine(appFolder, CmdName);

        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            Arguments = "/d /c \"\"" + cmdFile + "\" " + string.Join(' ', arguments.Select(Quote)) + "\"",
        };
        return Start(info, environment);
    }

    /// <summary>NTFS の junction(<c>mklink /J</c>)を作る。リンクも向き先も、一時フォルダの下だけ。</summary>
    public static void CreateJunction(string link, string target)
    {
        RequireUnderTempRoot(link);
        RequireUnderTempRoot(target);
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            Arguments = "/d /c mklink /J \"" + link + "\" \"" + target + "\"",
        };
        ScriptResult r = Start(info, null);
        if (r.ExitCode != 0) throw new InvalidOperationException("mklink /J が失敗した: " + r.Output + r.Error);
    }

    // 消す動きの安全の確認(入口の決まり 3・4)。引数の並びだけを見る
    private static void CheckArguments(IReadOnlyList<string> arguments)
    {
        int mode = IndexOf(arguments, "-Mode");
        int dataRoot = IndexOf(arguments, "-DataRoot");
        bool listOnly = arguments.Contains("-ListOnly", StringComparer.OrdinalIgnoreCase);
        bool rootIsTemp = dataRoot >= 0 && dataRoot + 1 < arguments.Count && IsUnderTempRoot(arguments[dataRoot + 1]);

        if (mode >= 0 && !rootIsTemp)
            throw new InvalidOperationException("消す動き(-Mode)は、一時フォルダの下を指す -DataRoot が付いているときだけ動かせる: " + string.Join(' ', arguments));
        if (!rootIsTemp && !listOnly)
            throw new InvalidOperationException("-DataRoot が無い(または一時フォルダの外を指す)実行は、-ListOnly だけ動かせる: " + string.Join(' ', arguments));
    }

    private static int IndexOf(IReadOnlyList<string> arguments, string name)
    {
        for (int i = 0; i < arguments.Count; i++)
            if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static void RequireUnderTempRoot(string path)
    {
        if (!IsUnderTempRoot(path)) throw new InvalidOperationException("テストの一時フォルダの外: " + path);
    }

    private static string Quote(string argument) => argument.Length == 0 || argument.Contains(' ') ? "\"" + argument + "\"" : argument;

    private static ScriptResult Start(ProcessStartInfo info, IReadOnlyDictionary<string, string>? environment)
    {
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        info.Environment.Remove(AppPaths.DataDirectoryVariable);
        info.Environment.Remove(AppPaths.ModelsDirectoryVariable);
        if (environment is not null)
            foreach ((string name, string value) in environment) info.Environment[name] = value;

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("プロセスを起こせなかった: " + info.FileName);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(RunTimeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"{RunTimeout.TotalSeconds:0} 秒で終わらなかった: {info.FileName}");
        }
        process.WaitForExit();
        return new ScriptResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}
