using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 起動の関門(<see cref="StartupGate"/>): モデルのファイルがそろっていれば画面を出さず、欠けていれば画面を出して結果を返す。
/// 画面は出さない(出す口を偽物に替える)。Program が、メインの窓を作る前に関門を通ることは、ソースを読んで確かめる。
/// </summary>
public sealed class StartupGateTests
{
    [Fact]
    public void When_everything_is_there_no_window_is_made_or_shown()
    {
        var log = new TestLog();
        bool shown = false;

        SetupChoice choice = StartupGate.Run(SetupWindowTests.AllReady, new FakeShell(), new FakeDownloads(), log, _ => { shown = true; return SetupChoice.Exit; });

        Assert.Equal(SetupChoice.Ready, choice);
        Assert.False(shown);
        Assert.DoesNotContain(log.Messages, m => m.Contains("セットアップ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("WithoutVlm")]
    [InlineData("Exit")]
    public async Task When_a_model_is_missing_the_setup_window_is_shown_and_its_choice_is_returned(string answerName)
    {
        SetupChoice answer = Enum.Parse<SetupChoice>(answerName);
        await StaDispatcher.RunAsync(() =>
        {
            var log = new TestLog();
            SetupState? shownState = null;

            SetupChoice choice = StartupGate.Run(SetupWindowTests.BothMissing, new FakeShell(), new FakeDownloads(), log, w => { shownState = w.State; return answer; });

            Assert.Equal(answer, choice);
            Assert.Equal(SetupKind.EmbeddingIncomplete, shownState!.Kind);
            Assert.Contains(log.Messages, m => m.Contains("セットアップの画面を出す", StringComparison.Ordinal));
            Assert.Contains(log.Messages, m => m.Contains("セットアップ: " + answer, StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task When_only_the_vlm_model_is_missing_the_window_is_shown_so_that_the_user_can_continue_without_it()
    {
        await StaDispatcher.RunAsync(() =>
        {
            SetupKind? kind = null;

            SetupChoice choice = StartupGate.Run(SetupWindowTests.VlmOnlyMissing, new FakeShell(), new FakeDownloads(), new TestLog(), w => { kind = w.State.Kind; return SetupChoice.WithoutVlm; });

            Assert.Equal(SetupKind.VlmIncomplete, kind);
            Assert.Equal(SetupChoice.WithoutVlm, choice);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Program_passes_the_gate_before_it_makes_the_main_window_and_exits_on_exit()
    {
        string program = File.ReadAllText(Path.Combine(ShellBoundaryGuardTests.RepoRoot(), "src", "Ohanami.App", "Program.cs"));

        int gate = program.IndexOf("StartupGate.Run(", StringComparison.Ordinal);
        int main = program.IndexOf("new Main" + "Window(", StringComparison.Ordinal);   // テストのソースに、窓を作る式をそのまま書かない(ShellBoundaryGuardTests が見張るため)
        int exit = program.IndexOf("SetupChoice.Exit", StringComparison.Ordinal);

        Assert.True(gate > 0, "Program が起動の関門を通っていない");
        Assert.True(main > gate, "メインの窓(Core を開く)より先に、関門を通る");
        Assert.InRange(exit, gate, main);   // 終了を選んだら、メインの窓を作らずに終わる
        Assert.Equal(1, CountOf(program, "StartupGate.Run("));
        // 関門の画面を閉じても終了しない(最初の窓が閉じても、メインの窓を開く)。メインの窓を開く前に、メインの窓が閉じたら終了する形に戻す
        Assert.True(program.IndexOf("ShutdownMode.OnExplicitShutdown", StringComparison.Ordinal) is > 0 and var explicitAt && explicitAt < gate);
        Assert.InRange(program.IndexOf("ShutdownMode.OnMainWindowClose", StringComparison.Ordinal), gate, main);
    }

    private static int CountOf(string text, string needle)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
