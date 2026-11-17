using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ohanami.Core;

namespace Ohanami.App.Tests;

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ohanami-app-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* 後始末の失敗でテストを落とさない */ }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>テストの診断ログ(メッセージを集める)。</summary>
internal sealed class TestLog : ICoreLog
{
    private readonly object _gate = new();
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages { get { lock (_gate) return _messages.ToArray(); } }

    public void Info(string message) { lock (_gate) _messages.Add("INFO " + message); }

    public void Warning(string message) { lock (_gate) _messages.Add("WARN " + message); }

    public void Error(string message, Exception? exception = null) { lock (_gate) _messages.Add("ERROR " + message + (exception is null ? "" : " | " + exception.GetType().Name)); }
}

/// <summary>固定した時刻を返す TimeProvider(進められる)。</summary>
internal sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTime(DateTimeOffset now) { _now = now; }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// 画像などのファイルを書いて、更新時刻を 1 時間前にする。走査は「更新から 2 秒以内のファイル」を書き込みの途中かもしれないとして今回は登録しない
/// (ADR 0010)ので、本物の時計の Core で「書いてすぐ走査する」テストは、古い更新時刻で書く。
/// </summary>
internal static class AgedFile
{
    public static void Write(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
    }
}

internal static class Wait
{
    /// <summary>条件が成り立つまで待つ(上限 10 秒)。成り立てば true。</summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        CancellationToken ct = Xunit.TestContext.Current.CancellationToken;
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (started.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(10, ct);
        }
        return true;
    }
}

/// <summary>
/// 偽のシェル(クリップボード・エクスプローラー・他のアプリへのドラッグ)。呼ばれたことを記録するだけで、**本物のクリップボードにも、エクスプローラーにも、OLE のドラッグにも触らない**。
/// テストが <see cref="ResultsPanel"/> を作るときは、必ずこれを渡す(2 つ引数の組み立ては本物を使う。ShellBoundaryGuardTests が見張る)。<see cref="FailWith"/> を入れると、操作が失敗する。
/// </summary>
internal sealed class FakeShell : IShellActions
{
    public List<string> Calls { get; } = [];

    public Exception? FailWith { get; set; }

    public DragDropEffects DragResult { get; set; } = DragDropEffects.Copy;

    public void RevealInExplorer(string path)
    {
        Calls.Add("reveal " + path);
        if (FailWith is { } e) throw e;
    }

    public void OpenFolder(string path)
    {
        Calls.Add("open-folder " + path);
        if (FailWith is { } e) throw e;
    }

    public System.Threading.Tasks.Task CopyImageAsync(string path)
    {
        Calls.Add("copy-image " + path);
        return FailWith is { } e ? System.Threading.Tasks.Task.FromException(e) : System.Threading.Tasks.Task.CompletedTask;
    }

    public void CopyFiles(string[] paths)
    {
        Calls.Add("copy-files " + string.Join("|", paths));
        if (FailWith is { } e) throw e;
    }

    public DragDropEffects DragFiles(DependencyObject source, string[] paths)
    {
        Calls.Add("drag " + string.Join("|", paths));
        return DragResult;
    }
}
