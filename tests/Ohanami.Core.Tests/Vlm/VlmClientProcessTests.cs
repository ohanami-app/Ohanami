using System;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Data;
using Ohanami.Core.Tests.Data;
using Ohanami.Core.Tests.Indexing;
using Ohanami.Core.Vlm;
using Xunit;

namespace Ohanami.Core.Tests.Vlm;

/// <summary>
/// VlmClient を、<b>別プロセスの偽の llama-server</b>(tests/Ohanami.FakeLlamaServer。<see cref="LlamaServer"/> で起動する)に対して確かめる。
/// <see cref="VlmClientTests"/> がテストの中の偽サーバで細部を確かめるのに対し、こちらは、実際のプロセスの起動・HTTP・プロセスの異常終了(リクエストの途中で落ちる)を通す。
/// VlmRunner のテストの土台でもある(<c>FAKE_LLAMA_COMPLETIONS</c> でリクエストごとの答え方を指定する)。
/// </summary>
public sealed class VlmClientProcessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = ThumbnailTestImages.Gradient(64, 48);

    private static async Task<(LlamaServer Server, ChildProcessGuard Guard, TempDirectory Dir)> StartAsync(string completions)
    {
        var dir = new TempDirectory();
        var log = new TestLog();
        ChildProcessGuard guard = FakeLlama.Guard(dir, log);
        LlamaServer server = await LlamaServer.StartAsync(FakeLlama.Launch(dir, FakeLlama.Env(("FAKE_LLAMA_COMPLETIONS", completions))), guard, log, Ct);
        return (server, guard, dir);
    }

    [Fact]
    public async Task A_normal_response_from_a_real_child_process_is_a_success()
    {
        (LlamaServer server, ChildProcessGuard guard, TempDirectory dir) = await StartAsync("ok");
        using (dir) using (guard) await using (server)
        {
            using var client = new VlmClient(server.BaseUri);

            VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

            Assert.Equal(VlmOutcome.Ok, r.Outcome);
            Assert.Equal("fake-ocr-1", r.Caption!.Ocr);
            Assert.Equal("extras=", r.Caption.Description); // 追加のキーなし
            Assert.Equal(1000, r.PromptTokens);
        }
    }

    [Fact]
    public async Task Length_then_ok_retries_with_dry_multiplier_and_length_twice_with_repeat_penalty_alone()
    {
        (LlamaServer server, ChildProcessGuard guard, TempDirectory dir) = await StartAsync("length,ok,length,length,ok");
        using (dir) using (guard) await using (server)
        {
            using var client = new VlmClient(server.BaseUri);

            VlmResult second = await client.CaptionAsync(Png, ImageFormat.Png, Ct);   // リクエスト 1(length)、2(ok)
            VlmResult third = await client.CaptionAsync(Png, ImageFormat.Png, Ct);    // リクエスト 3(length)、4(length)、5(ok)

            Assert.Equal((VlmOutcome.Ok, 2), (second.Outcome, second.Attempt));
            Assert.Equal("extras=dry_multiplier", second.Caption!.Description);
            Assert.Equal((VlmOutcome.Ok, 3), (third.Outcome, third.Attempt));
            Assert.Equal("extras=repeat_penalty", third.Caption!.Description);
        }
    }

    [Theory]
    [InlineData("400", false, "HTTP 400")]
    [InlineData("extra", false, "想定外のプロパティ")]
    [InlineData("think", false, "思考の痕跡")]
    [InlineData("surrogate", false, "不正なサロゲート")]
    [InlineData("500", true, "HTTP 500")]
    [InlineData("badjson", true, "JSON でない")]
    public async Task Each_kind_of_bad_response_gives_the_documented_outcome(string mode, bool serverFailure, string expectedError)
    {
        (LlamaServer server, ChildProcessGuard guard, TempDirectory dir) = await StartAsync(mode);
        using (dir) using (guard) await using (server)
        {
            using var client = new VlmClient(server.BaseUri);

            VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

            Assert.Equal(serverFailure ? VlmOutcome.ServerFailure : VlmOutcome.Failed, r.Outcome);
            Assert.Contains(expectedError, r.Error, StringComparison.Ordinal);
            Assert.False(server.HasExited, "サーバのプロセスは、HTTP の失敗では落ちない(落ちたかどうかは呼び出し側が見る)");
        }
    }

    [Fact]
    public async Task A_child_that_dies_in_the_middle_of_a_request_is_a_server_failure_and_the_exit_is_visible()
    {
        (LlamaServer server, ChildProcessGuard guard, TempDirectory dir) = await StartAsync("crash");
        using (dir) using (guard) await using (server)
        {
            using var client = new VlmClient(server.BaseUri);

            VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

            Assert.Equal(VlmOutcome.ServerFailure, r.Outcome);
            Assert.True(await FakeLlama.UntilAsync(() => server.HasExited));
            Assert.Equal(9, server.ExitCode);
            Assert.False(await server.CheckHealthAsync(Ct));
            // HasExited は出力の読み取りの終わりを待たない(負荷が高いと、終了の直前の行は少し遅れてログに入る)。読み切るまで待つ製品の読み方で読む
            Assert.Contains("fake crash during request", await server.LogTailAfterExitAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_child_that_hangs_makes_the_request_time_out_as_a_server_failure_and_stop_kills_it()
    {
        (LlamaServer server, ChildProcessGuard guard, TempDirectory dir) = await StartAsync("hang");
        using (dir) using (guard)
        {
            int pid = server.ProcessId;
            using (var client = new VlmClient(server.BaseUri, TimeSpan.FromMilliseconds(600)))
            {
                VlmResult r = await client.CaptionAsync(Png, ImageFormat.Png, Ct);

                Assert.Equal(VlmOutcome.ServerFailure, r.Outcome);
                Assert.Contains("タイムアウト", r.Error, StringComparison.Ordinal);
            }
            Assert.False(server.HasExited); // 止まっているだけで、まだ生きている。呼び出し側(VlmRunner)が kill して起動し直す
            await server.StopAsync();
            Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(pid)));
        }
    }
}
