using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Indexing;
using Ohanami.Core.Models;
using Ohanami.Core.Power;
using Ohanami.Core.Vlm;
using Xunit;

namespace Ohanami.Core.Tests.Vlm;

/// <summary>
/// VlmRunner を直接(Indexer なしで)、別プロセスの偽の llama-server で確かめる(ADR 0027・0028): 新しい順、1 枚ごとの確定、境目での中断・切り替え、
/// サーバの異常(落ちる・止まる)からの復旧、ファイルを開けない画像、検証の失敗、資源(サーバ)の持ち方。
/// </summary>
public sealed class VlmRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string OcrOf(VlmFixture fx, string hash) => fx.Text($"SELECT ocr FROM captions WHERE hash = '{hash}'");

    private static int Started(VlmFixture fx) => fx.Log.Messages.Count(m => m.Contains("llama-server を起動した", StringComparison.Ordinal));

    // ---------------------------------------------------------------- 正常

    [Fact]
    public async Task Images_are_processed_newest_first_and_each_result_is_committed_with_both_stages_and_the_model_and_run_recorded()
    {
        using var fx = new VlmFixture("ok");
        List<string> hashes = await fx.AddImagesAsync(3);   // hashes[2] が一番新しい
        RunRecorder run = fx.NewRecorder();

        RunEnd end = await fx.Runner.RunAsync(run, () => false, Ct);

        Assert.Equal(RunEnd.Drained, end);
        Assert.Equal(3, run.CompletedCount);
        // 偽サーバは、受けた順に fake-ocr-1, 2, 3 を返す。新しい順なので、一番新しい画像が 1 番目
        Assert.Equal("fake-ocr-1", OcrOf(fx, hashes[2]));
        Assert.Equal("fake-ocr-2", OcrOf(fx, hashes[1]));
        Assert.Equal("fake-ocr-3", OcrOf(fx, hashes[0]));
        Assert.Equal(3, fx.StageCount("ocr", "done"));
        Assert.Equal(3, fx.StageCount("caption", "done"));
        Assert.Equal(3, fx.Count($"SELECT count(*) FROM stage_status WHERE model_id = '{fx.Spec.ModelId}' AND run_id = {run.RunId}") / 2);
        Assert.Equal(3, fx.Count("SELECT count(*) FROM captions"));
        Assert.Equal(3, fx.Count("SELECT count(*) FROM search_text")); // 索引にも入っている(同じトランザクション)
        // models に VLM が登録され、runs の行(job_kind = vlm、runtime_json にビルドと引数)が作られている
        Assert.Equal("vlm", fx.Text($"SELECT kind FROM models WHERE id = '{fx.Spec.ModelId}'"));
        Assert.Equal("vlm", fx.Text($"SELECT job_kind FROM runs WHERE id = {run.RunId}"));
        JsonNode runtime = JsonNode.Parse(fx.Text($"SELECT runtime_json FROM runs WHERE id = {run.RunId}"))!;
        Assert.Equal(12110, (int)runtime["llama_server"]!["build"]!);
        Assert.Equal("cd959bae9", (string)runtime["llama_server"]!["commit"]!);
        Assert.Contains("--image-max-tokens", runtime["llama_server"]!["arguments"]!.AsArray().Select(a => (string)a!));
        Assert.Equal(3584, (int)runtime["model"]!["image_max_tokens"]!);
        // 起動は 1 回。戻っても、サーバは握ったまま(ReleaseAsync まで)
        Assert.Equal(1, Started(fx));
    }

    [Fact]
    public async Task Stage_summaries_count_pending_done_failed_and_outdated_for_both_stages()
    {
        using var fx = new VlmFixture("ok");
        List<string> hashes = await fx.AddImagesAsync(4);
        Assert.All(fx.Runner.Summarize(), s => Assert.Equal(4, s.Pending));
        Assert.Equal([Stage.Ocr, Stage.Caption], fx.Runner.Summarize().Select(s => s.Stage).ToArray());
        // 古いモデルで完了した 1 枚(要再作成)と、失敗の 1 枚
        await ModelRegistry.EnsureVlmModelRegisteredAsync(fx.Scan.Db, "Test VLM", fx.Spec, DateTimeOffset.UtcNow, Ct);   // 現行の model_id を(外部キーのため)登録しておく
        await fx.Scan.Db.WriteAsync((conn, tx) =>
        {
            using var m = conn.CreateCommand();
            m.Transaction = tx;
            m.CommandText = "INSERT INTO models(id, kind, name, spec_json, created_at) VALUES ('old-vlm:abc', 'vlm', 'old', '{}', '2026-01-01T00:00:00.000Z')";
            m.ExecuteNonQuery();
            using var r = conn.CreateCommand();
            r.Transaction = tx;
            r.CommandText = "INSERT INTO runs(job_kind, started_at, app_version, runtime_json) VALUES ('vlm', '2026-01-01T00:00:00.000Z', 't', '{}')";
            r.ExecuteNonQuery();
            foreach (string stage in new[] { "ocr", "caption" })
            {
                using var a = conn.CreateCommand();
                a.Transaction = tx;
                a.CommandText = $"INSERT INTO stage_status(hash, stage, status, model_id, run_id, settings_json, error, attempts, updated_at) VALUES ('{hashes[0]}', '{stage}', 'done', 'old-vlm:abc', 1, NULL, NULL, 1, '2026-01-01T00:00:00.000Z')";
                a.ExecuteNonQuery();
                using var b = conn.CreateCommand();
                b.Transaction = tx;
                b.CommandText = $"INSERT INTO stage_status(hash, stage, status, model_id, run_id, settings_json, error, attempts, updated_at) VALUES ('{hashes[1]}', '{stage}', 'failed', '{fx.Spec.ModelId}', 1, NULL, 'x', 1, '2026-01-01T00:00:00.000Z')";
                b.ExecuteNonQuery();
            }
        }, Ct);

        foreach (StageSummary s in fx.Runner.Summarize())
            Assert.Equal((2, 0, 1, 1), (s.Pending, s.Done, s.Failed, s.Outdated));
    }

    // ---------------------------------------------------------------- 境目での切り替え・中断

    [Fact]
    public async Task A_yield_signal_is_honoured_at_the_image_boundary_after_the_current_image_is_committed_and_the_server_is_kept()
    {
        using var fx = new VlmFixture("ok");
        await fx.AddImagesAsync(3);
        RunRecorder run = fx.NewRecorder();

        RunEnd first = await fx.Runner.RunAsync(run, () => run.CompletedCount >= 1, Ct);

        Assert.Equal(RunEnd.Yielded, first);
        Assert.Equal(1, run.CompletedCount);
        Assert.Equal(1, fx.StageCount("caption", "done")); // 今の 1 枚は確定してから戻る

        RunEnd second = await fx.Runner.RunAsync(run, () => false, Ct);

        Assert.Equal(RunEnd.Drained, second);
        Assert.Equal(3, fx.StageCount("caption", "done"));
        Assert.Equal(3, fx.Count("SELECT count(*) FROM captions"));   // 重複なし
        Assert.Equal(1, Started(fx));                                  // 資源は握ったまま。起動し直していない
    }

    [Fact]
    public async Task Cancelling_during_a_request_aborts_it_leaves_that_image_unprocessed_and_release_kills_the_server()
    {
        using var fx = new VlmFixture("hang");
        List<string> hashes = await fx.AddImagesAsync(2);
        RunRecorder run = fx.NewRecorder();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(2500); // サーバの起動のあと、リクエストの途中で切る

        RunEnd end = await fx.Runner.RunAsync(run, () => false, cts.Token);

        Assert.Equal(RunEnd.Cancelled, end);
        Assert.Equal(0, fx.Count("SELECT count(*) FROM stage_status")); // 処理中の 1 枚は、stage_status を書かない
        Assert.Equal(0, fx.Count("SELECT count(*) FROM captions"));
        Assert.Equal(0, run.CompletedCount);
        PidRecord record = ChildProcessGuard.Parse(await File.ReadAllTextAsync(fx.Guard.PidFilePath, Ct));
        Assert.True(FakeLlama.IsRunning(record.Pid)); // 切っただけでは、サーバは生きている(止めるのは ReleaseAsync)

        await fx.Runner.ReleaseAsync();

        Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(record.Pid)), "ReleaseAsync で llama-server を kill する");
        Assert.False(File.Exists(fx.Guard.PidFilePath));
        _ = hashes;
    }

    [Fact]
    public async Task A_cancelled_token_before_the_run_returns_cancelled_without_starting_the_server()
    {
        using var fx = new VlmFixture("ok");
        await fx.AddImagesAsync(1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        RunEnd end = await fx.Runner.RunAsync(fx.NewRecorder(), () => false, cts.Token);

        Assert.Equal(RunEnd.Cancelled, end);
        Assert.Equal(0, Started(fx));
    }

    [Fact]
    public async Task Release_is_idempotent_and_the_next_run_starts_a_new_server()
    {
        using var fx = new VlmFixture("ok");
        await fx.AddImagesAsync(2);
        RunRecorder run1 = fx.NewRecorder();
        await fx.Runner.RunAsync(run1, () => run1.CompletedCount >= 1, Ct);
        PidRecord first = ChildProcessGuard.Parse(await File.ReadAllTextAsync(fx.Guard.PidFilePath, Ct));

        await fx.Runner.ReleaseAsync();
        await fx.Runner.ReleaseAsync();
        Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(first.Pid)));

        RunRecorder run2 = fx.NewRecorder(); // 再開は新しい runs の行
        RunEnd end = await fx.Runner.RunAsync(run2, () => false, Ct);

        Assert.Equal(RunEnd.Drained, end);
        Assert.Equal(2, Started(fx));
        Assert.NotEqual(run1.RunId, run2.RunId);
        Assert.Equal(2, fx.StageCount("caption", "done"));
    }

    // ---------------------------------------------------------------- 画像ごとの結果

    [Theory]
    [InlineData("400", "HTTP 400")]
    [InlineData("extra", "想定外のプロパティ")]
    [InlineData("think", "思考の痕跡")]
    [InlineData("surrogate", "不正なサロゲート")]
    public async Task A_failed_image_is_recorded_as_failed_in_both_stages_without_a_caption_and_the_run_goes_on(string mode, string expectedError)
    {
        using var fx = new VlmFixture(mode);
        await fx.AddImagesAsync(2);
        RunRecorder run = fx.NewRecorder();

        RunEnd end = await fx.Runner.RunAsync(run, () => false, Ct);

        Assert.Equal(RunEnd.Drained, end);
        Assert.Equal(2, run.CompletedCount);
        Assert.Equal(0, fx.Count("SELECT count(*) FROM captions"));
        Assert.Equal(2, fx.StageCount("ocr", "failed"));
        Assert.Equal(2, fx.StageCount("caption", "failed"));
        Assert.Contains(expectedError, fx.Text("SELECT error FROM stage_status WHERE stage = 'caption' LIMIT 1"), StringComparison.Ordinal);
        // 失敗の settings_json は、試した設定の一覧(attempts)を持つ形
        JsonNode settings = JsonNode.Parse(fx.Text("SELECT settings_json FROM stage_status WHERE stage = 'caption' LIMIT 1"))!;
        Assert.True(settings["attempts"]!.AsArray().Count >= 1);
        Assert.Equal(1, (int)settings["attempts"]![0]!["attempt"]!);
    }

    [Fact]
    public async Task A_length_retry_saves_the_result_with_its_attempt_and_the_penalty_settings()
    {
        using var fx = new VlmFixture("length,ok");
        List<string> hashes = await fx.AddImagesAsync(1);

        RunEnd end = await fx.Runner.RunAsync(fx.NewRecorder(), () => false, Ct);

        Assert.Equal(RunEnd.Drained, end);
        Assert.Equal(2, fx.Count($"SELECT attempt FROM captions WHERE hash = '{hashes[0]}'"));
        JsonNode settings = JsonNode.Parse(fx.Text($"SELECT settings_json FROM captions WHERE hash = '{hashes[0]}'"))!;
        Assert.Equal(0.8, (double)settings["dry_multiplier"]!);
        Assert.Equal(2, settings["attempts"]!.AsArray().Count);
        Assert.Equal("extras=dry_multiplier", fx.Text($"SELECT description FROM captions WHERE hash = '{hashes[0]}'"));
    }

    // ---------------------------------------------------------------- サーバの異常

    [Fact]
    public async Task A_server_that_dies_during_a_request_is_restarted_and_the_same_image_is_redone_with_one_runs_row()
    {
        using var fx = new VlmFixture("crashonce");
        List<string> hashes = await fx.AddImagesAsync(2);
        RunRecorder run = fx.NewRecorder();

        RunEnd end = await fx.Runner.RunAsync(run, () => false, Ct);

        Assert.Equal(RunEnd.Drained, end);
        Assert.Equal(2, fx.StageCount("caption", "done")); // 落ちた画像も、やり直して成功した(failed にならない)
        Assert.Equal(0, fx.StageCount("caption", "failed"));
        Assert.Equal(2, Started(fx));                       // 起動し直した
        Assert.Equal(1, fx.Count("SELECT count(*) FROM runs"));      // runs の行は 1 つのまま
        Assert.Equal(2, fx.Count("SELECT count(*) FROM captions"));
        Assert.Contains(fx.Log.Messages, m => m.Contains("kill して起動し直す", StringComparison.Ordinal));
        _ = hashes;
    }

    [Fact]
    public async Task A_request_that_times_out_is_treated_like_a_dead_server_restart_and_redo()
    {
        using var fx = new VlmFixture("hangonce", requestTimeout: TimeSpan.FromSeconds(1.5));
        await fx.AddImagesAsync(1);

        RunEnd end = await fx.Runner.RunAsync(fx.NewRecorder(), () => false, Ct);

        Assert.Equal(RunEnd.Drained, end);
        Assert.Equal(1, fx.StageCount("caption", "done"));
        Assert.Equal(2, Started(fx)); // タイムアウトで kill して起動し直し、同じ画像をやり直した
    }

    [Fact]
    public async Task The_same_image_taking_the_server_down_twice_in_a_row_is_failed_and_the_run_moves_on_without_looping_forever()
    {
        using var fx = new VlmFixture("crash");   // どの起動でも、最初のリクエストで落ちる
        await fx.AddImagesAsync(2);

        RunEnd end = await fx.Runner.RunAsync(fx.NewRecorder(), () => false, Ct);

        Assert.Equal(RunEnd.Drained, end);
        Assert.Equal(2, fx.StageCount("caption", "failed"));
        Assert.Equal(2, fx.StageCount("ocr", "failed"));
        Assert.Contains("llama-server が異常終了した", fx.Text("SELECT error FROM stage_status WHERE stage = 'caption' LIMIT 1"), StringComparison.Ordinal);
        JsonNode settings = JsonNode.Parse(fx.Text("SELECT settings_json FROM stage_status WHERE stage = 'caption' LIMIT 1"))!;
        Assert.Equal(2, settings["server_failures"]!.AsArray().Count);   // 各回のサーバの異常の理由
        Assert.True(settings["attempts"]!.AsArray().Count >= 1);
    }

    [Fact]
    public async Task Three_images_in_a_row_failed_by_server_failures_stop_the_job_as_an_environment_problem()
    {
        // 原因が画像でなく環境(GPU が他で使われている・ドライバ・VRAM 不足)のとき、全画像を failed にし続けない。3 枚目で止める
        using var fx = new VlmFixture("crash");
        await fx.AddImagesAsync(4);
        EngineStatus? engine = null;
        RunRecorder run = fx.NewRecorder(onEngine: st => engine = st);

        LlamaServerException e = await Assert.ThrowsAsync<LlamaServerException>(() => fx.Runner.RunAsync(run, () => false, Ct));

        Assert.Contains("3 枚続けて異常", e.Message, StringComparison.Ordinal);
        Assert.Contains("GPU", e.Message, StringComparison.Ordinal);
        Assert.Equal(3, fx.StageCount("caption", "failed"));   // 3 枚は failed(その枚数分は記録される)
        Assert.Equal(3, fx.StageCount("ocr", "failed"));
        Assert.Equal(3, fx.Count("SELECT count(*) FROM stage_status WHERE stage = 'caption'"));   // 4 枚目は stage_status が無い(次の Start でやり直せる)
        Assert.NotEqual(EnginePhase.Failed, engine!.Phase);   // 準備(検証・起動)の失敗ではない。理由は例外の文(Indexer の LastError)だけで伝える

        // Indexer は、致命的な例外のあとも ReleaseAsync を呼ぶ。サーバが残らない
        PidRecord record = ChildProcessGuard.Parse(await File.ReadAllTextAsync(fx.Guard.PidFilePath, Ct));
        await fx.Runner.ReleaseAsync();
        Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(record.Pid)));
    }

    [Fact]
    public async Task A_server_that_is_gone_before_an_image_is_restarted_without_counting_as_a_failure()
    {
        using var fx = new VlmFixture("ok");
        await fx.AddImagesAsync(3);
        RunRecorder run = fx.NewRecorder();
        await fx.Runner.RunAsync(run, () => run.CompletedCount >= 1, Ct);
        PidRecord record = ChildProcessGuard.Parse(await File.ReadAllTextAsync(fx.Guard.PidFilePath, Ct));
        System.Diagnostics.Process.GetProcessById(record.Pid).Kill(entireProcessTree: true); // 画像の合間に、サーバが落ちた(外から kill)
        Assert.True(await FakeLlama.UntilAsync(() => !FakeLlama.IsRunning(record.Pid)));

        RunEnd end = await fx.Runner.RunAsync(run, () => false, Ct);

        Assert.Equal(RunEnd.Drained, end);
        Assert.Equal(3, fx.StageCount("caption", "done"));
        Assert.Equal(0, fx.StageCount("caption", "failed"));
        Assert.Equal(2, Started(fx));
        Assert.Equal(1, fx.Count("SELECT count(*) FROM runs"));
    }

    [Fact]
    public async Task After_a_resume_from_sleep_the_server_is_checked_before_the_next_image_and_kept_when_it_answers()
    {
        using var fx = new VlmFixture("ok");
        await fx.AddImagesAsync(3);
        RunRecorder run = fx.NewRecorder();
        await fx.Runner.RunAsync(run, () => run.CompletedCount >= 1, Ct);
        PidRecord before = ChildProcessGuard.Parse(await File.ReadAllTextAsync(fx.Guard.PidFilePath, Ct));

        fx.Power.Handle(PowerEvents.ResumeAutomatic);   // スリープからの復帰の通知(OS のコールバックと同じ入口)
        RunEnd end = await fx.Runner.RunAsync(run, () => false, Ct);

        Assert.Equal(RunEnd.Drained, end);
        Assert.Equal(3, fx.StageCount("caption", "done"));
        Assert.Equal(1, Started(fx)); // /health に答えたので、起動し直さない
        Assert.Equal(before.Pid, ChildProcessGuard.Parse(await File.ReadAllTextAsync(fx.Guard.PidFilePath, Ct)).Pid);
        Assert.Contains(fx.Log.Messages, m => m.Contains("スリープからの復帰のあと", StringComparison.Ordinal));
        // 通知が届いたことが、ログに残る(tools の SleepCheckLogic が読む文言)。通知は 1 回だけ、info で
        string[] notices = fx.Log.Messages.Where(m => m.Contains("スリープからの復帰の通知を受けた", StringComparison.Ordinal)).ToArray();
        string notice = Assert.Single(notices);
        Assert.StartsWith("INFO ", notice, StringComparison.Ordinal);
        Assert.Contains("疑わしい", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void A_resume_notification_without_a_running_server_is_logged_too()
    {
        using var fx = new VlmFixture("ok");

        fx.Power.Handle(PowerEvents.ResumeSuspend);   // llama-server が無いとき(読み込み中・ジョブの前)も、通知が届いたことは残す

        string notice = Assert.Single(fx.Log.Messages, m => m.Contains("スリープからの復帰の通知を受けた", StringComparison.Ordinal));
        Assert.Contains("動いていない", notice, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- ファイルを開けない・内容が違う

    [Fact]
    public async Task Unreadable_and_rewritten_images_are_skipped_without_a_record_and_the_run_still_drains_beyond_one_chunk()
    {
        using var fx = new VlmFixture("ok");
        List<string> hashes = await fx.AddImagesAsync(70);   // 64 件の塊より多い
        // 走査のあとで、新しい方の 3 枚を消し、1 枚を書き換える(DB の上では現存のまま)
        File.Delete(fx.Scan.PathOf("img69.png"));
        File.Delete(fx.Scan.PathOf("img68.png"));
        File.Delete(fx.Scan.PathOf("img67.png"));
        File.WriteAllBytes(fx.Scan.PathOf("img66.png"), Scanning.ScanFixture.FakePng("rewritten"));
        int skipped = 0;
        RunRecorder run = fx.NewRecorder(onSkipped: _ => skipped++);

        RunEnd end = await fx.Runner.RunAsync(run, () => false, Ct);

        Assert.Equal(RunEnd.Drained, end);     // 飛ばした画像だけが残っても、止まる(同じ 64 件を取り続けない)
        Assert.Equal(4, skipped);
        Assert.Equal(66, fx.StageCount("caption", "done"));
        Assert.Equal(0, fx.StageCount("caption", "failed"));
        foreach (string skippedHash in new[] { hashes[69], hashes[68], hashes[67], hashes[66] })
            Assert.Equal(0, fx.Count($"SELECT count(*) FROM stage_status WHERE hash = '{skippedHash}'")); // 記録しない(次回の Start で再試行)
        Assert.Equal(4, fx.Runner.Summarize()[1].Pending);
        // 飛ばした理由(画面の「飛ばした画像」に出す): 消えた 3 枚は開けなかったパスとメッセージ、書き換えた 1 枚は「内容が走査のときと違う」
        Assert.Equal(4, run.SkippedReasons.Count);
        Assert.Contains("内容が走査のときと違う", run.SkippedReasons[hashes[66]], StringComparison.Ordinal);
        Assert.All(new[] { hashes[69], hashes[68], hashes[67] }, h => Assert.Contains("img", run.SkippedReasons[h], StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_queue_is_fetched_eight_images_at_a_time_so_a_new_arrival_is_processed_ninth()
    {
        // 待ち行列は 8 枚ずつ取る(ADR 0027)。新着(最も新しい)が、実行中の塊を使い切った次の取り直しの先頭に入る = 9 番目。
        // 64 枚ずつだと 21 番目になり、新着が VLM に入るまで最大約 13 分かかる
        using var fx = new VlmFixture("ok");
        await fx.AddImagesAsync(20);   // i が大きいほど新しい
        byte[] arrival = Scanning.ScanFixture.FakePng("arrival");
        string arrivalHash = VlmFixture.Sha(arrival);
        bool added = false;
        RunRecorder run = fx.NewRecorder();
        bool ShouldYield()
        {
            if (!added && run.CompletedCount == 1)
            {
                added = true;   // 1 枚目を終えた直後に、最も新しい更新時刻の新着が入る
                fx.Scan.Write("arrival.png", arrival, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                fx.Scan.ScanAsync().GetAwaiter().GetResult();
            }
            return false;
        }

        RunEnd end = await fx.Runner.RunAsync(run, ShouldYield, Ct);

        Assert.Equal(RunEnd.Drained, end);
        Assert.Equal(21, fx.Count("SELECT count(*) FROM captions"));
        Assert.Equal(arrivalHash, fx.Scan.Text("SELECT hash FROM captions ORDER BY rowid LIMIT 1 OFFSET 8"));
    }

    [Fact]
    public async Task A_second_path_with_the_right_content_is_used_when_the_first_is_gone()
    {
        using var fx = new VlmFixture("ok");
        byte[] bytes = Scanning.ScanFixture.FakePng("dup");
        fx.Scan.Write("a/first.png", bytes);
        fx.Scan.Write("b/second.png", bytes);
        await fx.Scan.RegisterRootAsync();
        await fx.Scan.ScanAsync();
        File.Delete(fx.Scan.PathOf("a/first.png"));

        RunEnd end = await fx.Runner.RunAsync(fx.NewRecorder(), () => false, Ct);

        Assert.Equal(RunEnd.Drained, end);
        Assert.Equal(1, fx.StageCount("caption", "done"));
    }

    // ---------------------------------------------------------------- 致命的な失敗

    [Fact]
    public async Task A_model_file_that_does_not_verify_is_fatal_reported_as_an_engine_failure_and_nothing_is_started_or_recorded()
    {
        using var fx = new VlmFixture("ok", breakModel: true);
        await fx.AddImagesAsync(1);
        var engines = new List<EngineStatus>();
        RunRecorder run = fx.NewRecorder(onEngine: engines.Add);

        ModelMismatchException ex = await Assert.ThrowsAsync<ModelMismatchException>(() => fx.Runner.RunAsync(run, () => false, Ct));

        Assert.Contains("sha256", ex.Message, StringComparison.Ordinal);
        Assert.False(run.Started);                                       // runs の行を残さない
        Assert.Equal(0, Started(fx));
        Assert.Equal(EnginePhase.Failed, engines[^1].Phase);
        Assert.Equal(0, fx.Count("SELECT count(*) FROM stage_status"));
        Assert.Equal(0, fx.Count("SELECT count(*) FROM models WHERE kind = 'vlm'"));
    }

    [Fact]
    public async Task A_server_executable_with_the_wrong_hash_is_fatal()
    {
        using var fx = new VlmFixture("ok");
        VlmRunner runner = new(fx.Scan.Db, fx.Spec, "x", fx.Settings with { ExpectedExeSha256 = new string('0', 64) }, fx.Guard, new VlmVerifier(), fx.Captions, fx.Scan.Time, fx.Log);
        await fx.AddImagesAsync(1);

        ModelMismatchException ex = await Assert.ThrowsAsync<ModelMismatchException>(() => runner.RunAsync(fx.NewRecorder(), () => false, Ct));

        Assert.Contains("llama-server.exe", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_cannot_be_started_is_fatal_and_a_protocol_error_is_fatal_too()
    {
        using var broken = new VlmFixture("ok", extraEnv: FakeLlama.Env(("FAKE_LLAMA_FAIL_ALWAYS", "1")));
        await broken.AddImagesAsync(1);
        await Assert.ThrowsAsync<LlamaServerException>(() => broken.Runner.RunAsync(broken.NewRecorder(), () => false, Ct));
        Assert.Equal(0, broken.Count("SELECT count(*) FROM stage_status"));

        using var misconfigured = new VlmFixture("404");   // 401・404 などは、画像ごとの失敗にせず、ジョブを止める
        await misconfigured.AddImagesAsync(2);
        VlmProtocolException ex = await Assert.ThrowsAsync<VlmProtocolException>(() => misconfigured.Runner.RunAsync(misconfigured.NewRecorder(), () => false, Ct));
        Assert.Contains("HTTP 404", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, misconfigured.Count("SELECT count(*) FROM stage_status"));
    }

    [Fact]
    public async Task The_second_run_does_not_verify_the_big_files_again_in_the_same_process()
    {
        using var fx = new VlmFixture("ok");
        await fx.AddImagesAsync(2);
        RunRecorder run1 = fx.NewRecorder();
        await fx.Runner.RunAsync(run1, () => run1.CompletedCount >= 1, Ct);
        await fx.Runner.ReleaseAsync();
        // 検証済みのファイルを、同じサイズ・更新日時のまま壊す: プロセスの中では再検証しないので、再開が通る(決めきれない点 3 の (a))
        string gguf = Path.Combine(fx.ModelDirectory, "m.gguf");
        DateTime when = File.GetLastWriteTimeUtc(gguf);
        byte[] bad = File.ReadAllBytes(gguf);
        bad[0] ^= 0xFF;
        File.WriteAllBytes(gguf, bad);
        File.SetLastWriteTimeUtc(gguf, when);

        RunEnd end = await fx.Runner.RunAsync(fx.NewRecorder(), () => false, Ct);

        Assert.Equal(RunEnd.Drained, end);
    }
}
