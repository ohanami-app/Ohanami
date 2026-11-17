using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ohanami.Bench;
using SkiaSharp;
using Xunit;

namespace Ohanami.Bench.Tests;

/// <summary>sleep-report の純粋な部分: 場面ごとの合否の見方、イベントログの読み取り、フィクスチャの画像。GPU・実機のスリープは使わない。</summary>
public sealed class SleepCheckTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 1, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private static SleepLogLine Log(double seconds, string message, string level = "info") => new(At(seconds), level, message);

    private static SleepSnapshot Snap(
        IReadOnlyList<SleepLogLine>? log = null,
        IReadOnlyList<SleepPeriod>? sleeps = null,
        IReadOnlyList<SleepRunRow>? runs = null,
        int expected = 10,
        IReadOnlyList<double>? captionDone = null,
        IReadOnlyList<string>? captionFailed = null,
        IReadOnlyList<double>? embedDone = null,
        int? files = null,
        SleepOcrMatch? ocr = null,
        IReadOnlyList<DateTimeOffset>? spawns = null,
        IReadOnlyList<int>? pids = null,
        bool app = false,
        bool pidFile = false,
        int? vram = 800)
    {
        // 既定は「10 枚全部が done」
        List<DateTimeOffset> done = (captionDone ?? Enumerable.Range(0, expected).Select(i => 100.0 + i * 10)).Select(At).ToList();
        List<DateTimeOffset> embed = (embedDone ?? Enumerable.Range(0, expected).Select(i => 10.0 + i)).Select(At).ToList();
        var stages = new Dictionary<string, SleepStageTally>
        {
            ["embed"] = new(expected, embed, []),
            ["caption"] = new(expected, done, captionFailed ?? []),
            ["ocr"] = new(expected, done, captionFailed ?? []),
        };
        return new SleepSnapshot(At(-60), At(3600), log ?? [], sleeps ?? [], runs ?? [], stages, files ?? expected, ocr ?? new SleepOcrMatch(expected, expected),
            spawns ?? [], pids ?? [], app, pidFile, vram, 2000);
    }

    private static SleepPeriod Sleep(double start, double seconds, int? state = 3) => new(At(start), At(start + seconds), state, "キーボード");

    private static SleepCheck Find(IReadOnlyList<SleepCheck> checks, string nameStart) =>
        checks.Single(c => c.Name.StartsWith(nameStart, StringComparison.Ordinal));

    /// <summary>VLM の最中にスリープして、サーバは生きていて(答えた)、最後まで流れた。</summary>
    private static SleepLogLine Notice(double seconds) => Log(seconds, "スリープからの復帰の通知を受けた(PowerEvents)。llama-server に「疑わしい」の印を付けた(次の画像の前に /health を確かめる)");

    private static SleepSnapshot GoodSleepVlm(IReadOnlyList<SleepLogLine>? extraLog = null, IReadOnlyList<SleepPeriod>? sleeps = null) => Snap(
        log: [Log(10, "llama-server を起動した(pid 1、ポート 2、ログ x)"), Notice(161), .. extraLog ?? [Log(165, "スリープからの復帰のあと、llama-server は /health に答えた。そのまま続ける")]],
        sleeps: sleeps ?? [Sleep(100, 60)],
        runs: [new SleepRunRow(1, "embed", At(0), At(9)), new SleepRunRow(2, "vlm", At(10), At(300))],
        captionDone: [20, 30, 40, 170, 180, 190, 200, 210, 220, 230]);

    // ---------------------------------------------------------------- VLM の最中のスリープ

    [Fact]
    public void Sleep_during_vlm_passes_when_the_server_answered_and_everything_finished()
    {
        IReadOnlyList<SleepCheck> checks = SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, GoodSleepVlm());

        Assert.Equal(SleepVerdict.Pass, SleepCheckLogic.Overall(checks));
        Assert.Equal(SleepVerdict.Pass, Find(checks, "復帰のあとに caption").Verdict);
        Assert.Contains("3 枚", Find(checks, "復帰のあとに caption").Detail, StringComparison.Ordinal);   // スリープの前に 3 枚、復帰のあとに 7 枚
        Assert.Equal(SleepVerdict.Info, Find(checks, "復帰のあとの llama-server の扱い").Verdict);
        Assert.Contains("/health に答えた", Find(checks, "復帰のあとの llama-server の扱い").Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("ログも無い", Find(checks, "復帰のあとの llama-server の扱い").Detail, StringComparison.Ordinal);   // 「無い」の文にも同じ語が入るので、取り違えを防ぐ
    }

    [Fact]
    public void Without_any_reaction_log_the_detail_depends_on_whether_the_resume_notice_was_logged()
    {
        SleepSnapshot notified = GoodSleepVlm([Log(130, "スリープ中の行(復帰より前なので、反応としては数えない)")]);
        SleepSnapshot silent = notified with { Log = notified.Log.Where(l => !l.Message.Contains("復帰の通知", StringComparison.Ordinal)).ToList() };

        SleepCheck a = Find(SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, notified), "復帰のあとの llama-server の扱い");
        SleepCheck b = Find(SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, silent), "復帰のあとの llama-server の扱い");

        Assert.Contains("通知は届いた", a.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("分からない", a.Detail, StringComparison.Ordinal);
        Assert.Contains("分からない", b.Detail, StringComparison.Ordinal);   // 通知のログも反応のログも無い: 届いたか分からない
    }

    [Fact]
    public void A_missing_resume_notice_is_a_warning_for_vlm_and_information_elsewhere()
    {
        SleepSnapshot good = GoodSleepVlm();
        SleepSnapshot silent = good with { Log = good.Log.Where(l => !l.Message.Contains("復帰の通知", StringComparison.Ordinal)).ToList() };

        Assert.Equal(SleepVerdict.Pass, Find(SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, good), "復帰の通知").Verdict);
        Assert.Contains("1 回", Find(SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, good), "復帰の通知").Detail, StringComparison.Ordinal);
        SleepCheck missing = Find(SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, silent), "復帰の通知");
        Assert.Equal(SleepVerdict.Warn, missing.Verdict);
        Assert.Equal(SleepVerdict.Warn, SleepCheckLogic.Overall(SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, silent)));   // 不合格ではなく注意
        // 通知は復帰の少し前から 60 秒後までのものだけ数える(別のスリープの通知を取り違えない)
        SleepSnapshot stale = silent with { Log = [.. silent.Log, Notice(40), Notice(300)] };
        Assert.Equal(SleepVerdict.Warn, Find(SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, stale), "復帰の通知").Verdict);
        // 読み込み中・埋め込みの確認では、通知が無くても情報
        SleepSnapshot load = Snap(log: [Log(200, "llama-server を起動した(pid 1、ポート 2、ログ x)")], sleeps: [Sleep(100, 60)], spawns: [At(90)]);
        Assert.Equal(SleepVerdict.Info, Find(SleepCheckLogic.Evaluate(SleepScenario.SleepLoad, load), "復帰の通知").Verdict);
        Assert.Equal(SleepVerdict.NotApplicable, Find(SleepCheckLogic.Evaluate(SleepScenario.SleepLoad, load with { Sleeps = [] }), "復帰の通知").Verdict);
    }

    [Fact]
    public void The_ort_session_recreation_is_reported_as_information_with_its_reason()
    {
        SleepSnapshot none = Snap(sleeps: [Sleep(100, 60)], runs: [new SleepRunRow(1, "embed", At(0), At(300))], embedDone: [10, 20, 30, 170, 180, 190, 200, 210, 220, 230]);
        SleepSnapshot recreated = none with { Log = [Log(165, "画像のセッションを作り直した(Run の失敗から。累計 1 回。理由: CUDA error: unknown error)")] };

        SleepCheck a = Find(SleepCheckLogic.Evaluate(SleepScenario.SleepEmbed, none), "ORT のセッションの作り直し");
        SleepCheck b = Find(SleepCheckLogic.Evaluate(SleepScenario.SleepEmbed, recreated), "ORT のセッションの作り直し");

        Assert.Equal(SleepVerdict.Info, a.Verdict);
        Assert.Contains("作り直しは無かった", a.Detail, StringComparison.Ordinal);
        Assert.Equal(SleepVerdict.Info, b.Verdict);
        Assert.Contains("CUDA error: unknown error", b.Detail, StringComparison.Ordinal);
        Assert.Equal(SleepVerdict.Pass, SleepCheckLogic.Overall(SleepCheckLogic.Evaluate(SleepScenario.SleepEmbed, recreated)));   // 作り直して成功したなら、不合格でも注意でもない
    }

    [Fact]
    public void Sleep_during_vlm_fails_without_a_windows_sleep_record()
    {
        IReadOnlyList<SleepCheck> checks = SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, Snap(sleeps: []));

        Assert.Equal(SleepVerdict.Fail, Find(checks, "スリープの記録がある").Verdict);
        Assert.Equal(SleepVerdict.Fail, SleepCheckLogic.Overall(checks));
    }

    [Fact]
    public void Sleep_during_vlm_fails_when_the_job_was_not_running_at_the_time()
    {
        // VLM の実行は、スリープの前に終わっていた
        SleepSnapshot s = Snap(sleeps: [Sleep(500, 60)], runs: [new SleepRunRow(2, "vlm", At(10), At(300))]);

        Assert.Equal(SleepVerdict.Fail, Find(SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, s), "スリープの時点で vlm").Verdict);
    }

    [Fact]
    public void An_image_failed_by_the_sleep_makes_the_run_fail()
    {
        SleepSnapshot s = GoodSleepVlm() with
        {
            Stages = new Dictionary<string, SleepStageTally>
            {
                ["embed"] = new(10, [], []),
                ["caption"] = new(10, [At(20), At(30), At(170), At(180), At(190), At(200), At(210), At(220), At(230)], ["llama-server が異常終了した(同じ画像で 2 回続けた)"]),
                ["ocr"] = new(10, [At(20)], []),
            },
        };

        IReadOnlyList<SleepCheck> checks = SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, s);

        Assert.Equal(SleepVerdict.Fail, Find(checks, "caption: 全部 done").Verdict);
        Assert.Contains("2 回続けた", Find(checks, "caption: 全部 done").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_health_answer_followed_by_a_restart_is_flagged_for_the_unconditional_restart_decision()
    {
        // 「/health に答えた。そのまま続ける」の直後に「kill して起動し直す」が並ぶ
        SleepSnapshot s = GoodSleepVlm([
            Log(165, "スリープからの復帰のあと、llama-server は /health に答えた。そのまま続ける"),
            Log(166, "llama-server を kill して起動し直す: C:\\x.png: HTTP の接続が切れた", "warn"),
        ]);

        IReadOnlyList<SleepCheck> checks = SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, s);

        SleepCheck flag = Find(checks, "「答えた」の直後");
        Assert.Equal(SleepVerdict.Warn, flag.Verdict);
        Assert.Contains("無条件に起動し直す", flag.Detail, StringComparison.Ordinal);
        Assert.Equal(SleepVerdict.Warn, SleepCheckLogic.Overall(checks));   // 不合格ではなく、注意(実機のスリープの結果で決める)
    }

    [Fact]
    public void A_restart_right_after_resume_is_information_not_a_failure()
    {
        SleepSnapshot s = GoodSleepVlm([Log(160, "llama-server を kill して起動し直す: C:\\x.png: タイムアウト", "warn")]);

        IReadOnlyList<SleepCheck> checks = SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, s);

        Assert.Equal(SleepVerdict.Pass, SleepCheckLogic.Overall(checks));
        Assert.Contains("起動し直す", Find(checks, "復帰のあとの llama-server の扱い").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sleep_longer_than_the_request_timeout_reports_whether_a_timeout_restart_followed()
    {
        SleepSnapshot withTimeout = GoodSleepVlm([Log(415, "llama-server を kill して起動し直す: C:\\x.png: HTTP リクエストがタイムアウトした(300 秒)", "warn")], [Sleep(100, 310)]);
        SleepSnapshot without = GoodSleepVlm([Log(415, "スリープからの復帰のあと、llama-server は /health に答えた。そのまま続ける")], [Sleep(100, 310)]);

        SleepCheck a = Find(SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, withTimeout), "長いスリープ");
        SleepCheck b = Find(SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, without), "長いスリープ");

        Assert.Equal(SleepVerdict.Info, a.Verdict);
        Assert.Contains("タイムアウトで起動し直した", a.Detail, StringComparison.Ordinal);
        Assert.Contains("無い", b.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, GoodSleepVlm()), c => c.Name.StartsWith("長いスリープ", StringComparison.Ordinal));   // 60 秒のスリープでは出ない
    }

    [Fact]
    public void Leftovers_after_the_job_fail_for_a_process_and_warn_for_a_stale_pid_file()
    {
        SleepSnapshot running = GoodSleepVlm() with { LlamaPids = [1234] };
        SleepSnapshot stale = GoodSleepVlm() with { PidFileExists = true };

        Assert.Equal(SleepVerdict.Fail, Find(SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, running), "ジョブのあと").Verdict);
        Assert.Equal(SleepVerdict.Warn, Find(SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, stale), "ジョブのあと").Verdict);
    }

    [Fact]
    public void A_low_ocr_match_is_a_warning_and_a_missing_fixture_is_a_failure()
    {
        SleepSnapshot lowOcr = GoodSleepVlm() with { Ocr = new SleepOcrMatch(8, 10) };   // 0.8 < 0.9
        SleepSnapshot goodOcr = GoodSleepVlm() with { Ocr = new SleepOcrMatch(9, 10) };   // ちょうど 0.9 は合格

        Assert.Equal(SleepVerdict.Warn, Find(SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, lowOcr), "OCR").Verdict);
        Assert.Equal(SleepVerdict.Pass, Find(SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, goodOcr), "OCR").Verdict);
        Assert.Equal(SleepVerdict.Fail, SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, Snap(expected: 0)).First().Verdict);
        Assert.Equal(SleepVerdict.Warn, SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, GoodSleepVlm() with { FixtureFiles = 12 }).First().Verdict);   // DB 10 枚、ファイル 12 枚
    }

    // ---------------------------------------------------------------- 読み込み中のスリープ

    [Fact]
    public void Sleep_during_load_needs_the_sleep_between_spawn_and_ready()
    {
        SleepSnapshot ok = Snap(
            log: [Log(200, "llama-server を起動した(pid 1、ポート 2、ログ x)")],
            sleeps: [Sleep(100, 60)], spawns: [At(90)]);
        SleepSnapshot tooLate = Snap(
            log: [Log(95, "llama-server を起動した(pid 1、ポート 2、ログ x)")],
            sleeps: [Sleep(100, 60)], spawns: [At(90)]);   // スリープの前に起動が終わっていた
        SleepSnapshot noSpawn = Snap(sleeps: [Sleep(100, 60)]);

        IReadOnlyList<SleepCheck> good = SleepCheckLogic.Evaluate(SleepScenario.SleepLoad, ok);

        Assert.Equal(SleepVerdict.Pass, Find(good, "スリープの時点で llama-server が読み込み中").Verdict);
        Assert.Contains("そのまま起動できた", Find(good, "読み込み中のスリープのあとの結果").Detail, StringComparison.Ordinal);
        Assert.Equal(SleepVerdict.Pass, SleepCheckLogic.Overall(good));
        Assert.Equal(SleepVerdict.Fail, Find(SleepCheckLogic.Evaluate(SleepScenario.SleepLoad, tooLate), "スリープの時点で llama-server が読み込み中").Verdict);
        Assert.Equal(SleepVerdict.Fail, Find(SleepCheckLogic.Evaluate(SleepScenario.SleepLoad, noSpawn), "スリープの時点で llama-server が読み込み中").Verdict);
    }

    [Fact]
    public void A_startup_failure_across_the_sleep_is_information_and_recovery_is_judged_by_the_final_counts()
    {
        SleepSnapshot s = Snap(
            log: [Log(170, "ジョブの実行が失敗した: llama-server の /health がタイムアウトした", "error"), Log(400, "llama-server を起動した(pid 3、ポート 4、ログ y)")],
            sleeps: [Sleep(100, 60)], spawns: [At(90), At(380)]);

        IReadOnlyList<SleepCheck> checks = SleepCheckLogic.Evaluate(SleepScenario.SleepLoad, s);

        Assert.Equal(SleepVerdict.Info, Find(checks, "読み込み中のスリープのあとの結果").Verdict);
        Assert.Contains("起動に失敗した", Find(checks, "読み込み中のスリープのあとの結果").Detail, StringComparison.Ordinal);
        Assert.Equal(SleepVerdict.Pass, Find(checks, "caption: 全部 done").Verdict);   // 回復して全部流れた
    }

    [Fact]
    public void Two_llama_servers_at_once_after_a_load_sleep_fail()
    {
        SleepSnapshot s = Snap(log: [Log(200, "llama-server を起動した(pid 1、ポート 2、ログ x)")], sleeps: [Sleep(100, 60)], spawns: [At(90)], pids: [11, 12]);

        Assert.Equal(SleepVerdict.Fail, Find(SleepCheckLogic.Evaluate(SleepScenario.SleepLoad, s), "llama-server が同時に").Verdict);
    }

    // ---------------------------------------------------------------- 埋め込み(ORT)の最中のスリープ

    [Fact]
    public void Sleep_during_embed_requires_continued_progress_and_no_embed_failure_log()
    {
        SleepSnapshot good = Snap(
            sleeps: [Sleep(100, 60)],
            runs: [new SleepRunRow(1, "embed", At(0), At(300))],
            embedDone: [10, 20, 30, 170, 180, 190, 200, 210, 220, 230]);
        SleepSnapshot failed = good with { Log = [Log(165, "C:\\x.png: 埋め込みに失敗した: 画像の Run が失敗し、セッションを作り直して再試行しても失敗した", "warn")] };

        Assert.Equal(SleepVerdict.Pass, SleepCheckLogic.Overall(SleepCheckLogic.Evaluate(SleepScenario.SleepEmbed, good)));
        Assert.Equal(SleepVerdict.Fail, Find(SleepCheckLogic.Evaluate(SleepScenario.SleepEmbed, failed), "ORT:").Verdict);
    }

    // ---------------------------------------------------------------- 強制終了

    [Fact]
    public void After_an_app_kill_the_llama_server_must_be_gone_and_the_open_run_row_is_expected()
    {
        SleepSnapshot gone = Snap(runs: [new SleepRunRow(2, "vlm", At(10), null)], pidFile: true);
        SleepSnapshot left = gone with { LlamaPids = [99] };
        SleepSnapshot appStillUp = gone with { AppRunning = true };

        IReadOnlyList<SleepCheck> checks = SleepCheckLogic.Evaluate(SleepScenario.KillAppAfterKill, gone);

        Assert.Equal(SleepVerdict.Pass, SleepCheckLogic.Overall(checks));
        Assert.Equal(SleepVerdict.Info, Find(checks, "強制終了した実行").Verdict);
        Assert.Contains("runs 2", Find(checks, "強制終了した実行").Detail, StringComparison.Ordinal);
        Assert.Equal(SleepVerdict.Fail, Find(SleepCheckLogic.Evaluate(SleepScenario.KillAppAfterKill, left), "llama-server が一緒に消えた").Verdict);
        Assert.Equal(SleepVerdict.Fail, Find(SleepCheckLogic.Evaluate(SleepScenario.KillAppAfterKill, appStillUp), "App が終わっている").Verdict);
        Assert.Equal(SleepVerdict.Warn, Find(SleepCheckLogic.Evaluate(SleepScenario.KillAppAfterKill, gone with { VramMiB = 6500 }), "VRAM").Verdict);
        Assert.Equal(SleepVerdict.NotApplicable, Find(SleepCheckLogic.Evaluate(SleepScenario.KillAppAfterKill, gone with { VramMiB = null }), "VRAM").Verdict);
    }

    [Fact]
    public void After_an_app_kill_and_a_restart_the_run_finishes_with_two_vlm_runs_and_no_orphan_log()
    {
        SleepSnapshot ok = Snap(runs: [new SleepRunRow(2, "vlm", At(10), null), new SleepRunRow(3, "vlm", At(400), At(900))]);
        SleepSnapshot oneRun = Snap(runs: [new SleepRunRow(2, "vlm", At(10), At(900))]);
        SleepSnapshot orphan = ok with { Log = [Log(401, "前回の llama-server が孤児として残っていた(pid 5、ポート 6)。終わらせる。", "warn")] };

        Assert.Equal(SleepVerdict.Pass, SleepCheckLogic.Overall(SleepCheckLogic.Evaluate(SleepScenario.KillAppFinal, ok)));
        Assert.Equal(SleepVerdict.Fail, Find(SleepCheckLogic.Evaluate(SleepScenario.KillAppFinal, oneRun), "VLM の runs").Verdict);
        Assert.Equal(SleepVerdict.Warn, Find(SleepCheckLogic.Evaluate(SleepScenario.KillAppFinal, orphan), "前回の llama-server").Verdict);
    }

    [Fact]
    public void Killing_only_the_server_needs_a_restart_log_that_is_not_about_a_resume_and_no_failed_image()
    {
        SleepSnapshot ok = Snap(log: [Log(50, "llama-server を kill して起動し直す: C:\\x.png: 接続できない", "warn")]);
        SleepSnapshot resumeOnly = Snap(log: [Log(50, "llama-server を kill して起動し直す: スリープからの復帰のあと、/health に答えない", "warn")]);
        SleepSnapshot failedImage = ok with { Stages = new Dictionary<string, SleepStageTally> { ["embed"] = new(10, [], []), ["caption"] = new(10, [At(1)], ["x"]), ["ocr"] = new(10, [At(1)], []) } };

        Assert.Equal(SleepVerdict.Pass, SleepCheckLogic.Overall(SleepCheckLogic.Evaluate(SleepScenario.KillServer, ok)));
        Assert.Equal(SleepVerdict.Fail, Find(SleepCheckLogic.Evaluate(SleepScenario.KillServer, resumeOnly), "llama-server の kill").Verdict);
        Assert.Equal(SleepVerdict.Fail, Find(SleepCheckLogic.Evaluate(SleepScenario.KillServer, failedImage), "caption: 全部 done").Verdict);
    }

    // ---------------------------------------------------------------- 連続のサーバ異常の打ち切り

    [Fact]
    public void The_cutoff_after_three_server_failed_images_is_checked_in_every_part()
    {
        string twice = "llama-server が異常終了した(同じ画像で 2 回続けた): 接続できない / 接続できない";
        SleepSnapshot ok = Snap(
            expected: 8,
            log: [Log(300, "llama-server が 3 枚続けて異常になった。GPU が他で使われているか、ドライバの問題かもしれない", "error"), Log(301, "ジョブの実行が失敗した: llama-server が 3 枚続けて異常になった。GPU が他で使われているか", "error")],
            captionDone: [], captionFailed: [twice, twice, twice]);

        IReadOnlyList<SleepCheck> checks = SleepCheckLogic.Evaluate(SleepScenario.CutoffStopped, ok);

        Assert.Equal(SleepVerdict.Pass, SleepCheckLogic.Overall(checks));
        Assert.Contains("未処理 5 枚", Find(checks, "4 枚目以降").Detail, StringComparison.Ordinal);

        SleepSnapshot twoOnly = ok with { Stages = new Dictionary<string, SleepStageTally> { ["embed"] = new(8, [], []), ["caption"] = new(8, [], [twice, twice]), ["ocr"] = new(8, [], [twice, twice]) } };
        SleepSnapshot noLog = ok with { Log = [] };
        SleepSnapshot tooFew = Snap(expected: 3, log: ok.Log, captionDone: [], captionFailed: [twice, twice, twice]);

        Assert.Equal(SleepVerdict.Fail, Find(SleepCheckLogic.Evaluate(SleepScenario.CutoffStopped, twoOnly), "failed がちょうど 3 枚").Verdict);
        Assert.Equal(SleepVerdict.Fail, Find(SleepCheckLogic.Evaluate(SleepScenario.CutoffStopped, noLog), "3 枚続けて異常で止まった").Verdict);
        Assert.Equal(SleepVerdict.Fail, SleepCheckLogic.Evaluate(SleepScenario.CutoffStopped, tooFew).First().Verdict);   // 4 枚以上が要る
    }

    [Fact]
    public void After_retrying_failed_images_the_cutoff_run_recovers_to_all_done()
    {
        Assert.Equal(SleepVerdict.Pass, SleepCheckLogic.Overall(SleepCheckLogic.Evaluate(SleepScenario.CutoffRecovered, Snap(expected: 8))));
        Assert.Equal(SleepVerdict.Fail, SleepCheckLogic.Overall(SleepCheckLogic.Evaluate(SleepScenario.CutoffRecovered, Snap(expected: 8, captionFailed: ["x"]))));
    }

    // ---------------------------------------------------------------- 場面と重なるスリープの選び方

    /// <summary>App を起動する前の、場面と無関係なスリープ(実機の結果で、誤って判定された例)。</summary>
    private static SleepPeriod Unrelated() => Sleep(-2000, 1900, 4);

    [Fact]
    public void An_earlier_unrelated_sleep_is_not_used_for_the_vlm_judgement_and_is_listed_as_not_used()
    {
        // 1 回目: 場面より前(runs も確定も無い時間)。2 回目: VLM の実行の最中(いつもの good)
        SleepSnapshot s = GoodSleepVlm(sleeps: [Unrelated(), Sleep(100, 60)]);

        IReadOnlyList<SleepCheck> checks = SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, s);

        Assert.Equal(SleepVerdict.Pass, SleepCheckLogic.Overall(checks));                                  // 1 回目を見て不合格・注意を出さない
        SleepCheck list = Find(checks, "スリープの記録がある");
        Assert.Contains("対象の時間に 2 回", list.Detail, StringComparison.Ordinal);
        Assert.Contains("判定に使わない", list.Detail, StringComparison.Ordinal);                           // 使わなかったものを明記する
        Assert.Contains("判定に使った", list.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(checks, c => c.Name.Contains("のスリープ]", StringComparison.Ordinal));      // 重なるのは 1 回だけなので、名前に時刻は付かない
        Assert.Equal(SleepVerdict.Pass, Find(checks, "スリープの時点で vlm のジョブが動いていた").Verdict);
        Assert.Equal(SleepVerdict.Pass, Find(checks, "復帰の通知").Verdict);
    }

    [Fact]
    public void The_order_of_the_sleeps_does_not_matter_for_the_choice()
    {
        // 場面と重なるのが 1 回目で、場面より後の無関係なスリープが 2 回目
        SleepSnapshot s = GoodSleepVlm(sleeps: [Sleep(100, 60), Sleep(2000, 50, 4)]);

        IReadOnlyList<SleepCheck> checks = SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, s);

        Assert.Equal(SleepVerdict.Pass, SleepCheckLogic.Overall(checks));
        Assert.Contains("判定に使わない", Find(checks, "スリープの記録がある").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void When_no_sleep_overlaps_the_scene_the_job_check_still_fails_and_lists_the_sleeps()
    {
        SleepSnapshot s = GoodSleepVlm(sleeps: [Unrelated(), Sleep(2000, 50, 4)]);   // どちらも、場面の実行(10〜300 秒)の外

        IReadOnlyList<SleepCheck> checks = SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, s);

        SleepCheck job = Find(checks, "スリープの時点で vlm のジョブが動いていた");
        Assert.Equal(SleepVerdict.Fail, job.Verdict);
        Assert.Contains("やり直す", job.Detail, StringComparison.Ordinal);
        Assert.Equal(SleepVerdict.Fail, SleepCheckLogic.Overall(checks));
        Assert.Contains("対象の時間に 2 回", Find(checks, "スリープの記録がある").Detail, StringComparison.Ordinal);   // 一覧は出す
        Assert.Equal(SleepVerdict.NotApplicable, Find(checks, "復帰の通知").Verdict);                              // 重なるものが無いので、通知は見ない
        Assert.DoesNotContain(checks, c => c.Name.StartsWith("復帰のあとの llama-server", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_sleeps_during_the_scene_are_both_judged_with_the_time_in_the_check_names()
    {
        // 場面の実行の中に 2 回。2 回目には復帰の通知のログが無い(1 回目の復帰は 161 秒に通知あり)
        SleepSnapshot s = GoodSleepVlm(sleeps: [Sleep(100, 60), Sleep(240, 20)]);

        IReadOnlyList<SleepCheck> checks = SleepCheckLogic.Evaluate(SleepScenario.SleepVlm, s);

        Assert.Contains("全部を判定する", Find(checks, "スリープの記録がある").Detail, StringComparison.Ordinal);
        List<SleepCheck> notices = checks.Where(c => c.Name.StartsWith("復帰の通知", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, notices.Count);
        Assert.All(notices, n => Assert.Contains("のスリープ]", n.Name, StringComparison.Ordinal));
        Assert.Equal(SleepVerdict.Pass, notices.Single(n => n.Name.Contains("01:01:40Z", StringComparison.Ordinal)).Verdict);
        Assert.Equal(SleepVerdict.Warn, notices.Single(n => n.Name.Contains("01:04:00Z", StringComparison.Ordinal)).Verdict);     // 2 回目は通知が無い
        Assert.Equal(2, checks.Count(c => c.Name.StartsWith("スリープの時点で vlm のジョブが動いていた", StringComparison.Ordinal)));
        Assert.Equal(2, checks.Count(c => c.Name.StartsWith("復帰のあとに caption の確定が続いた", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_earlier_unrelated_sleep_is_ignored_for_the_load_and_the_embed_scenes_too()
    {
        SleepSnapshot load = Snap(
            log: [Log(200, "llama-server を起動した(pid 1、ポート 2、ログ x)")],
            sleeps: [Unrelated(), Sleep(100, 60)], spawns: [At(90)]);
        IReadOnlyList<SleepCheck> loadChecks = SleepCheckLogic.Evaluate(SleepScenario.SleepLoad, load);
        Assert.Equal(SleepVerdict.Pass, Find(loadChecks, "スリープの時点で llama-server が読み込み中").Verdict);   // 1 回目を見ると「スリープの前に始まった起動が無い」で不合格になる
        Assert.Equal(SleepVerdict.Pass, SleepCheckLogic.Overall(loadChecks));
        Assert.Contains("判定に使わない", Find(loadChecks, "スリープの記録がある").Detail, StringComparison.Ordinal);

        SleepSnapshot embed = Snap(
            sleeps: [Unrelated(), Sleep(100, 60)],
            runs: [new SleepRunRow(1, "embed", At(0), At(300))],
            embedDone: [10, 20, 30, 170, 180, 190, 200, 210, 220, 230]);
        IReadOnlyList<SleepCheck> embedChecks = SleepCheckLogic.Evaluate(SleepScenario.SleepEmbed, embed);
        Assert.Equal(SleepVerdict.Pass, SleepCheckLogic.Overall(embedChecks));
        Assert.Contains("判定に使わない", Find(embedChecks, "スリープの記録がある").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Runs_of_another_folder_do_not_make_a_sleep_belong_to_this_scene()
    {
        // 別のフォルダを流した vlm の実行(この場面の画像の確定を含まない)の最中のスリープは、この場面のものではない
        SleepSnapshot s = GoodSleepVlm(sleeps: [Sleep(5000, 60), Sleep(100, 60)]) with
        {
            Runs = [new SleepRunRow(1, "embed", At(0), At(9)), new SleepRunRow(2, "vlm", At(10), At(300)), new SleepRunRow(3, "vlm", At(4000), At(6000))],
        };

        IReadOnlyList<SleepPeriod> related = SleepCheckLogic.RelatedSleeps(s, "vlm", "caption");

        Assert.Equal(At(100), Assert.Single(related).Start);
    }

    [Fact]
    public void The_state_number_is_shown_as_is_with_the_meaning_marked_unverified_and_the_hibernation_field_as_a_fact()
    {
        string four = SleepCheckLogic.StateText(new SleepPeriod(At(0), At(70), 4, null, 0));
        string three = SleepCheckLogic.StateText(new SleepPeriod(At(0), At(70), 3, null));
        string none = SleepCheckLogic.StateText(new SleepPeriod(At(0), At(70), null, null));

        Assert.Equal("EffectiveState=4(意味は未確認)、HiberPagesWritten=0", four);
        Assert.Equal("EffectiveState=3(意味は未確認)", three);
        Assert.Equal("EffectiveState=不明", none);
        Assert.DoesNotContain("休止", four, StringComparison.Ordinal);       // 番号から意味のラベルを作らない
        Assert.DoesNotContain("S4", four, StringComparison.Ordinal);
        Assert.DoesNotContain("S3", three, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- イベントログ

    private const string PowerEventXml = """
        <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Power-Troubleshooter'/><EventID>1</EventID><TimeCreated SystemTime='2026-11-06T00:04:25.0340779Z'/></System><EventData><Data Name='SleepTime'>2026-11-05T14:38:10.0512484Z</Data><Data Name='WakeTime'>2026-11-06T00:04:23.9351509Z</Data><Data Name='HiberPagesWritten'>0</Data><Data Name='EffectiveState'>4</Data><Data Name='WakeSourceText'>AMD USB</Data></EventData></Event>
        <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><EventID>1</EventID></System><EventData><Data Name='SleepTime'>2026-11-05T09:09:13.9985215Z</Data><Data Name='WakeTime'>2026-11-05T12:02:15.9564084Z</Data><Data Name='EffectiveState'>3</Data></EventData></Event>
        """;

    [Fact]
    public void Sleep_events_are_read_from_the_wevtutil_xml_and_filtered_by_the_time_range()
    {
        var from = new DateTimeOffset(2026, 11, 5, 13, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 11, 6, 2, 0, 0, TimeSpan.Zero);

        List<SleepPeriod> periods = SleepCheckCommand.ParseSleeps(PowerEventXml, from, to);

        SleepPeriod p = Assert.Single(periods);   // 12:02 に復帰した 1 つ目は、範囲より前
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 14, 38, 10, TimeSpan.Zero), p.Start, TimeSpan.FromSeconds(1));
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 0, 4, 24, TimeSpan.Zero), p.End, TimeSpan.FromSeconds(1));
        Assert.Equal(4, p.EffectiveState);
        Assert.Equal(0, p.HiberPagesWritten);                                                             // 欄があれば読む(休止状態の書き込みの有無の手がかり)
        Assert.Equal("AMD USB", p.WakeSource);
        Assert.Equal(2, SleepCheckCommand.ParseSleeps(PowerEventXml, new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), to).Count);   // 範囲を広げれば 2 つ
        Assert.Null(SleepCheckCommand.ParseSleeps(PowerEventXml, new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), to)[0].WakeSource);   // 2 つ目のイベントには、解除元が無い
        Assert.Null(SleepCheckCommand.ParseSleeps(PowerEventXml, new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), to)[0].HiberPagesWritten);   // HiberPagesWritten も無い
        Assert.Empty(SleepCheckCommand.ParseSleeps("", from, to));
    }

    [Fact]
    public void An_event_without_sleep_or_wake_time_is_a_loud_failure()
    {
        const string broken = "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><EventData><Data Name='WakeTime'>2026-11-06T00:04:23.9351509Z</Data></EventData></Event>";

        Assert.Throws<System.IO.InvalidDataException>(() => SleepCheckCommand.ParseSleeps(broken, T0.AddYears(-1), T0.AddYears(1)));
    }

    // ---------------------------------------------------------------- stage-count(スクリプトが「done が N 枚」を待つのに使う)

    /// <summary>テスト用の DB: フォルダ A(2 枚。うち a-2 は missing)、フォルダ B(1 枚)。stage_status は caption の done を A に 2 つ(うち 1 つは missing の画像)、B に 1 つ、embed の done を A に 2 つ。</summary>
    private static string MakeCountDb(string dir, string folderA, string folderB)
    {
        string db = System.IO.Path.Combine(dir, "ohanami.db");
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False");
        conn.Open();
        void Exec(string sql)
        {
            using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        Exec("CREATE TABLE image_paths (path TEXT PRIMARY KEY, hash TEXT NOT NULL, missing_since TEXT)");
        Exec("CREATE TABLE stage_status (hash TEXT NOT NULL, stage TEXT NOT NULL, status TEXT NOT NULL, error TEXT, updated_at TEXT NOT NULL, PRIMARY KEY (hash, stage))");
        string pa1 = System.IO.Path.Combine(folderA, "a-1.png").Replace("'", "''");
        string pa2 = System.IO.Path.Combine(folderA, "a-2.png").Replace("'", "''");
        string pb1 = System.IO.Path.Combine(folderB, "b-1.png").Replace("'", "''");
        Exec($"INSERT INTO image_paths VALUES ('{pa1}', 'h1', NULL), ('{pa2}', 'h2', '2026-11-06T00:00:00.000Z'), ('{pb1}', 'h3', NULL)");
        Exec("INSERT INTO stage_status VALUES ('h1', 'caption', 'done', NULL, 't'), ('h2', 'caption', 'done', NULL, 't'), ('h3', 'caption', 'done', NULL, 't'), ('h1', 'embed', 'done', NULL, 't'), ('h2', 'embed', 'failed', 'x', 't')");
        return db;
    }

    [Fact]
    public void The_done_count_covers_only_the_folders_current_images_and_the_given_stage()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ohanami-bench-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string a = System.IO.Path.Combine(dir, "kill-app"), b = System.IO.Path.Combine(dir, "kill-server");
            string db = MakeCountDb(dir, a, b);

            Assert.Equal(1, SleepCheckCommand.CountDone(db, a, "caption"));      // a-2 は missing_since が付いている(現存しない)ので数えない
            Assert.Equal(1, SleepCheckCommand.CountDone(db, b, "caption"));
            Assert.Equal(1, SleepCheckCommand.CountDone(db, a, "embed"));        // 段階ごと(a-2 の embed は failed)
            Assert.Equal(0, SleepCheckCommand.CountDone(db, a, "ocr"));          // 行が無い段階は 0
            Assert.Equal(1, SleepCheckCommand.CountDone(db, a + System.IO.Path.DirectorySeparatorChar, "caption"));   // 末尾の区切りは気にしない
            Assert.Equal(0, SleepCheckCommand.CountDone(db, System.IO.Path.Combine(dir, "kill"), "caption"));         // 名前の前半だけ一致するフォルダは別
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_done_count_fails_loudly_for_an_unknown_stage_or_a_missing_db()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ohanami-bench-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string a = System.IO.Path.Combine(dir, "a");
            string db = MakeCountDb(dir, a, System.IO.Path.Combine(dir, "b"));

            Assert.Throws<ArgumentException>(() => SleepCheckCommand.CountDone(db, a, "caption'; DROP TABLE x; --"));
            Assert.Throws<ArgumentException>(() => SleepCheckCommand.CountDone(db, a, "vlm"));
            Assert.Throws<System.IO.FileNotFoundException>(() => SleepCheckCommand.CountDone(System.IO.Path.Combine(dir, "none.db"), a, "caption"));
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_pause_job_log_line_appears_in_the_timeline()
    {
        SleepSnapshot s = Snap(log: [Log(10, "ジョブ: VLM を始める"), Log(20, "ジョブ: 一時停止"), Log(30, "ジョブ: 再開")]);

        List<string> lines = SleepCheckCommand.Timeline(s).ToList();

        Assert.Equal(3, lines.Count);
        Assert.Contains("ジョブ: 一時停止", lines[1], StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 時系列とフィクスチャ

    [Fact]
    public void The_timeline_merges_windows_events_and_relevant_log_lines_in_time_order_and_drops_unrelated_info()
    {
        SleepSnapshot s = Snap(
            log: [Log(5, "サムネイルを作った"), Log(20, "llama-server を起動した(pid 1、ポート 2、ログ x)"), Log(140, "警告の例", "warn")],
            sleeps: [Sleep(100, 30, 3)], spawns: [At(10)]);

        List<string> lines = SleepCheckCommand.Timeline(s).ToList();

        Assert.Equal(5, lines.Count);   // 起動の開始、起動した、スリープ、復帰、警告(サムネイルの行は出ない)
        Assert.Contains("llama-server のログができた", lines[0], StringComparison.Ordinal);
        Assert.Contains("スリープに入った(EffectiveState=3(意味は未確認))", lines[2], StringComparison.Ordinal);   // S3 などのラベルは付けない
        Assert.DoesNotContain("S3", lines[2], StringComparison.Ordinal);
        Assert.Contains("復帰した", lines[3], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.Contains("サムネイル", StringComparison.Ordinal));
        Assert.True(lines.SequenceEqual(lines.OrderBy(l => l, StringComparer.Ordinal)));   // 時刻(先頭)の昇順
    }

    [Fact]
    public void Fixture_plan_has_enough_images_for_each_scenario_and_file_names_carry_a_four_digit_number()
    {
        IReadOnlyList<(string Folder, string Label, int Count)> plan = SleepFixture.Plan(1500, 30);

        Assert.Equal(plan.Count, plan.Select(p => p.Folder).Distinct().Count());
        Assert.True(plan.Single(p => p.Folder == "cutoff").Count >= 4);          // 3 枚 failed + 4 枚目が必要
        Assert.True(plan.Single(p => p.Folder == "sleep-embed").Count >= 1000);
        Assert.Equal("sleep-vlm-0007.png", SleepFixture.FileName("SLEEP-VLM", 7));
        Assert.Matches(@"\d{4}", SleepFixture.FileName("KILL-APP", 12));
    }

    [Fact]
    public void Fixture_images_are_deterministic_and_differ_between_numbers()
    {
        using SKTypeface typeface = SKFontManager.Default.MatchCharacter('あ') ?? throw new InvalidOperationException("日本語のフォントが無い");

        byte[] a1 = SleepFixture.Render(typeface, "SLEEP-VLM", 1);
        byte[] a2 = SleepFixture.Render(typeface, "SLEEP-VLM", 1);
        byte[] b = SleepFixture.Render(typeface, "SLEEP-VLM", 2);
        byte[] other = SleepFixture.Render(typeface, "KILL-APP", 1);

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.NotEqual(a1, other);
        using SKBitmap bmp = SKBitmap.Decode(a1);
        Assert.Equal((1280, 720), (bmp.Width, bmp.Height));
    }
}
