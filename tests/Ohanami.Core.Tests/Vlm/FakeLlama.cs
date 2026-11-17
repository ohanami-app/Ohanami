using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Indexing;
using Ohanami.Core.Tests.Data;
using Ohanami.Core.Tests.Indexing;
using Ohanami.Core.Vlm;

namespace Ohanami.Core.Tests.Vlm;

/// <summary>偽の llama-server(tests/Ohanami.FakeLlamaServer。テストの出力ディレクトリに exe が入る)を使うための補助。</summary>
internal static class FakeLlama
{
    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "Ohanami.FakeLlamaServer.exe");

    public static LlamaServerLaunch Launch(
        TempDirectory dir, IReadOnlyDictionary<string, string>? env = null, TimeSpan? healthTimeout = null, int attempts = 3, int? imageMaxTokens = 3584, TimeSpan? outputDrainTimeout = null) =>
        new(ExePath, dir.File("model.gguf"), dir.File("mmproj.gguf"), ImageMinTokens: null, imageMaxTokens, ChatTemplateKwargs: null,
            LogDirectory: dir.File("logs"), HealthTimeout: healthTimeout ?? TimeSpan.FromSeconds(20), MaxPortAttempts: attempts, Environment: env, OutputDrainTimeout: outputDrainTimeout);

    public static ChildProcessGuard Guard(TempDirectory dir, ICoreLog log, IProcessInspector? inspector = null, bool useJob = true) =>
        new(dir.File("llama-server.pid"), log, inspector, useJob);

    public static Dictionary<string, string> Env(params (string Name, string Value)[] vars)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string n, string v) in vars) d[n] = v;
        return d;
    }

    /// <summary>pid のプロセスが、まだ動いているか。</summary>
    public static bool IsRunning(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>条件が成り立つまで待つ(上限 timeoutMs)。成り立てば true。</summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        CancellationToken ct = Xunit.TestContext.Current.CancellationToken;
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(25, ct);
        }
        return true;
    }

    /// <summary>メッセージ・ログの中の「pid N」の行(偽サーバが起動時に出す)から pid を取る。</summary>
    public static int PidFromText(string text)
    {
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(text, @"^(?:\[out\] )?pid (\d+)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
        if (!m.Success) throw new InvalidOperationException("「pid N」の行が無い: " + text);
        return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 起動したばかりのプロセスの exe のパス。**期待するパス(<paramref name="expectedExePath"/>)が読めるまで待つ**。プロセスが初期化を終えるまで <c>MainModule</c> は読めない(null や例外)うえ、
    /// 初期化の途中では、まれに先頭のモジュールが ntdll.dll など exe でない名前になる(テストを繰り返して見つけた。最初に読めた名前を使うと、記録の exe のパスが違って、掃除が「別のプロセス」と判断する)。
    /// </summary>
    public static string ModuleFileName(Process process, string expectedExePath)
    {
        string expected = Path.GetFullPath(expectedExePath);
        var sw = Stopwatch.StartNew();
        string? last = null;
        while (true)
        {
            try
            {
                if (process.MainModule?.FileName is { } name)
                {
                    last = name;
                    if (string.Equals(Path.GetFullPath(name), expected, StringComparison.OrdinalIgnoreCase)) return name;
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { /* 初期化中 */ }
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException($"プロセスの MainModule が期待のパスにならない(期待 {expected}、最後に読めた名前 {last ?? "(読めない)"})");
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// 偽サーバのログ(llama-server-*.log)に出る「pid N」から pid を取る。子の出力は非同期に読まれるので、出るまで待つ
    /// (失敗が早いと、例外のメッセージのログの末尾が空のことがある)。
    /// </summary>
    public static async Task<int> PidFromLogAsync(TempDirectory dir)
    {
        string? text = null;
        bool found = await UntilAsync(() =>
        {
            if (!Directory.Exists(dir.File("logs"))) return false;   // サーバの起動より前に呼ばれると、ログのディレクトリがまだ無い
            foreach (string file in Directory.GetFiles(dir.File("logs"), "llama-server-*.log"))
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                string t = reader.ReadToEnd();
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\[out\] pid \d+\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)) { text = t; return true; }
            }
            return false;
        });
        Xunit.Assert.True(found, "ログに「pid N」が出ない");
        return PidFromText(text!);
    }
}
