using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Ohanami.App.Tests;

public sealed class FileCoreLogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 34, 56, 789, TimeSpan.Zero);

    [Fact]
    public void Each_event_is_one_line_with_utc_time_and_level()
    {
        using var temp = new TempDirectory();
        var time = new FakeTime(T0);
        var log = new FileCoreLog(temp.File("logs"), time);

        log.Info("起動した");
        time.Advance(TimeSpan.FromMilliseconds(250));
        log.Warning("遅い");
        log.Error("失敗した");

        string[] lines = File.ReadAllLines(log.CurrentFile);
        Assert.Equal(
            [
                "2026-11-05T12:34:56.789Z [info] 起動した",
                "2026-11-05T12:34:57.039Z [warn] 遅い",
                "2026-11-05T12:34:57.039Z [error] 失敗した",
            ],
            lines);
        Assert.Equal(temp.File(Path.Combine("logs", "ohanami-20261105.log")), log.CurrentFile);
    }

    [Fact]
    public void Newlines_in_messages_and_exceptions_never_break_the_one_line_per_event_rule()
    {
        using var temp = new TempDirectory();
        var log = new FileCoreLog(temp.File("logs"), new FakeTime(T0));

        log.Info("a\r\nb\nc\rd");
        log.Error("失敗", new InvalidOperationException("line1\nline2"));

        string[] lines = File.ReadAllLines(log.CurrentFile);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("a ⏎ b ⏎ c ⏎ d", lines[0], StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: line1 ⏎ line2", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_is_logged_with_its_stack_trace_on_the_same_single_line()
    {
        using var temp = new TempDirectory();
        var log = new FileCoreLog(temp.File("logs"), new FakeTime(T0));
        Exception thrown;
        try { ThrowDeep(); throw new InvalidOperationException("unreachable"); }
        catch (InvalidOperationException ex) { thrown = ex; }

        log.Error("失敗した", thrown);

        string[] lines = File.ReadAllLines(log.CurrentFile);
        Assert.Single(lines);
        Assert.Contains("InvalidOperationException: deep failure", lines[0], StringComparison.Ordinal);
        Assert.Contains("ThrowDeep", lines[0], StringComparison.Ordinal); // スタックの行が残る
        Assert.Contains(" ⏎ ", lines[0], StringComparison.Ordinal);
    }

    private static void ThrowDeep() => throw new InvalidOperationException("deep failure");

    [Fact]
    public void A_new_day_goes_to_a_new_file_and_the_old_one_is_kept()
    {
        using var temp = new TempDirectory();
        var time = new FakeTime(new DateTimeOffset(2026, 11, 5, 23, 59, 59, TimeSpan.Zero));
        var log = new FileCoreLog(temp.File("logs"), time);

        log.Info("before midnight");
        time.Advance(TimeSpan.FromSeconds(2));
        log.Info("after midnight");

        string[] files = Directory.GetFiles(temp.File("logs")).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray()!;
        Assert.Equal(["ohanami-20261105.log", "ohanami-20261106.log"], files);
    }

    [Fact]
    public void Appends_to_an_existing_file_without_a_byte_order_mark()
    {
        using var temp = new TempDirectory();
        var first = new FileCoreLog(temp.File("logs"), new FakeTime(T0));
        first.Info("one");
        var second = new FileCoreLog(temp.File("logs"), new FakeTime(T0));
        second.Info("two");

        byte[] bytes = File.ReadAllBytes(first.CurrentFile);
        Assert.NotEqual(0xEF, bytes[0]); // BOM なし
        Assert.Equal(2, File.ReadAllLines(first.CurrentFile).Length);
    }

    [Fact]
    public void A_log_that_cannot_be_written_does_not_stop_the_application()
    {
        using var temp = new TempDirectory();
        string blocker = temp.File("logs");
        File.WriteAllText(blocker, "ディレクトリのはずの場所にファイルがある");
        var log = new FileCoreLog(blocker, new FakeTime(T0));

        log.Info("書けない"); // 例外にならない
        log.Error("これも", new InvalidOperationException("x"));
    }

    [Fact]
    public async Task Concurrent_writers_do_not_interleave_lines()
    {
        using var temp = new TempDirectory();
        var log = new FileCoreLog(temp.File("logs"), new FakeTime(T0));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++) log.Info($"thread {t} message {i} " + new string('x', 200));
        }, TestContext.Current.CancellationToken)));

        string[] lines = File.ReadAllLines(log.CurrentFile);
        Assert.Equal(800, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"^2026-11-05T12:34:56\.789Z \[info\] thread \d message \d+ x{200}$", l));
    }

    [Fact]
    public void Blank_directory_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new FileCoreLog(" "));
    }
}
