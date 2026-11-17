using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ohanami.Bench;
using Ohanami.Core;
using Xunit;

namespace Ohanami.Bench.Tests;

/// <summary>通し(vlm-full)の道具(通しの合格の見方と、通しのあとの確認)の純粋な部分。GPU も llama-server も DB も使わない。</summary>
public sealed class FullRunTests
{
    private static VlmEvalImage EvalImage(string name, bool ok = true, string ocr = "o", string desc = "d", string[]? tags = null) =>
        new(name, 200, ok ? "stop" : "length", ok, ok ? ocr : null, ok ? desc : null, ok ? tags ?? ["t"] : null, 1000, 100, null, null, 0, 4096, ok ? null : "finish_reason=length");

    private static StoredCaption Cap(string ocr = "o", string desc = "d", string[]? tags = null, int attempt = 1, string settings = "{\"attempts\":[{\"attempt\":1,\"wall_ms\":12000}]}") =>
        new(ocr, desc, tags ?? ["t"], attempt, settings);

    private static CaptionsCheckRow Row(string kind, int expected = 1, int? actual = 1, bool mismatch = false, string detail = "") =>
        new("f.png", new string('a', 64), kind, expected, actual, mismatch, detail);

    // ---------------------------------------------------------------- 期待と分類

    [Fact]
    public void The_expectation_comes_from_the_full_run_then_the_dry_retry_then_the_repeat_retry()
    {
        VlmEvalImage ok = EvalImage("a.png"), bad = EvalImage("b.png", ok: false);
        Assert.Equal(1, CaptionsCheckLogic.Expect(ok, EvalImage("a.png", ok: false), null)!.Attempt);                          // 全件の run が成功なら、再試行の run は見ない
        Assert.Equal(2, CaptionsCheckLogic.Expect(bad, EvalImage("b.png"), EvalImage("b.png"))!.Attempt);                              // dry で救えた
        Assert.Equal(3, CaptionsCheckLogic.Expect(bad, EvalImage("b.png", ok: false), EvalImage("b.png"))!.Attempt);                   // dry は失敗、rep で救えた
        Assert.Null(CaptionsCheckLogic.Expect(bad, EvalImage("b.png", ok: false), EvalImage("b.png", ok: false)));                     // どちらでも救えなかった
        Assert.Null(CaptionsCheckLogic.Expect(bad, null, null));                                                         // 再試行の run が無い
        Assert.Same(ok, CaptionsCheckLogic.Expect(ok, null, null)!.Source);
    }

    [Fact]
    public void A_stored_caption_equal_to_the_expectation_and_obtained_at_the_expected_attempt_matches()
    {
        CaptionsCheckRow r = CaptionsCheckLogic.Classify("a.png", "h", CaptionsCheckLogic.Expect(EvalImage("a.png"), null, null), Cap(), null);
        Assert.Equal(VlmCompare.Match, r.Kind);
        Assert.False(r.AttemptMismatch);
    }

    [Fact]
    public void A_rescued_image_is_compared_with_the_retry_run_and_its_attempt_is_checked()
    {
        VlmEvalImage bad = EvalImage("b.png", ok: false), dry = EvalImage("b.png", ocr: "救済"), rep = EvalImage("b.png", ocr: "別");
        ExpectedCaption? expected = CaptionsCheckLogic.Expect(bad, dry, rep);

        CaptionsCheckRow same = CaptionsCheckLogic.Classify("b.png", "h", expected, Cap(ocr: "救済", attempt: 2), null);
        Assert.Equal(VlmCompare.Match, same.Kind);
        Assert.False(same.AttemptMismatch);

        CaptionsCheckRow wrongAttempt = CaptionsCheckLogic.Classify("b.png", "h", expected, Cap(ocr: "救済", attempt: 3), null);
        Assert.Equal(VlmCompare.Match, wrongAttempt.Kind);                                                              // 中身は一致
        Assert.True(wrongAttempt.AttemptMismatch);                                                                      // が、試行の回が違う
        Assert.Contains("試行の回が違う(期待 2、実際 3)", wrongAttempt.Detail, StringComparison.Ordinal);

        CaptionsCheckRow differs = CaptionsCheckLogic.Classify("b.png", "h", expected, Cap(ocr: "違う", attempt: 2), null);
        Assert.Equal(VlmCompare.Differs, differs.Kind);
        Assert.Contains("ocr:", differs.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_and_failed_results_are_classified()
    {
        ExpectedCaption? ok = CaptionsCheckLogic.Expect(EvalImage("a.png"), null, null);
        var failure = new StoredFailure("finish_reason=length", "{}");

        Assert.Equal(CaptionsCheckRow.NotProcessed, CaptionsCheckLogic.Classify("a.png", "h", ok, null, null).Kind);
        CaptionsCheckRow productOnly = CaptionsCheckLogic.Classify("a.png", "h", ok, null, failure);
        Assert.Equal(VlmCompare.ProductFailed, productOnly.Kind);
        Assert.Contains("finish_reason=length", productOnly.Detail, StringComparison.Ordinal);

        Assert.Equal(VlmCompare.BothFailed, CaptionsCheckLogic.Classify("b.png", "h", null, null, failure).Kind);          // 評価の実験でも救えず、製品も失敗
        CaptionsCheckRow evalOnly = CaptionsCheckLogic.Classify("b.png", "h", null, Cap(attempt: 3), null);
        Assert.Equal(VlmCompare.VlmEvalFailed, evalOnly.Kind);                                                                  // 期待が無いのに、製品が成功した
        Assert.Equal(CaptionsCheckRow.NotProcessed, CaptionsCheckLogic.Classify("b.png", "h", null, null, null).Kind);
    }

    // ---------------------------------------------------------------- 時間・分布

    [Fact]
    public void Wall_seconds_sums_all_attempts_and_a_wrong_shape_is_an_error()
    {
        Assert.Equal(12.5, CaptionsCheckLogic.WallSeconds("{\"attempts\":[{\"attempt\":1,\"wall_ms\":12000},{\"attempt\":2,\"wall_ms\":500.0}]}"), 6);
        Assert.Throws<InvalidDataException>(() => CaptionsCheckLogic.WallSeconds("{\"attempts\":[]}"));
        Assert.Throws<InvalidDataException>(() => CaptionsCheckLogic.WallSeconds("{\"x\":1}"));
        Assert.Throws<InvalidDataException>(() => CaptionsCheckLogic.WallSeconds("{\"attempts\":[{\"attempt\":1}]}"));
    }

    [Fact]
    public void The_distribution_uses_the_same_nearest_rank_method_as_the_evaluation_summary()
    {
        Distribution d = Distribution.From(Enumerable.Range(1, 100).Select(i => (double)i));
        Assert.Equal(100, d.Count);
        Assert.Equal(51, d.P50);      // 並べて、先頭から 0.5 × 100 = 50 番目(0 始まり)= 51
        Assert.Equal(91, d.P90);
        Assert.Equal(100, d.P99);
        Assert.Equal(100, d.Max);
        Assert.Equal(5050, d.Total);
        Assert.Equal(0, Distribution.From([]).Count);
    }

    // ---------------------------------------------------------------- 通しのあとの確認の合格の見方

    private static CaptionsCheckInput Input(IReadOnlyList<CaptionsCheckRow> rows, double p50 = 12, double p90 = 16, int runs = 2, int reprocessed = 0, int ocr = 10, int caption = 10, bool times = true) =>
        new(rows, Enumerable.Repeat(p50, 8).Concat(Enumerable.Repeat(p90, 3)).ToList(), Enumerable.Repeat(12.0, 8).Concat(Enumerable.Repeat(16.3, 3)).ToList(),   // 11 枚: p50 は 6 番目、p90 は 10 番目
            Enumerable.Range(1, runs).Select(i => new RunRow(i, "vlm", "2026-11-06T00:00:00.000Z", "2026-11-06T01:00:00.000Z", "{\"server_version\":\"build 12110\"}")).ToList(),
            reprocessed, ocr, caption, ExpectedVlmRuns: 2, times);

    private static List<CaptionsCheckRow> GoodRows() =>
        [.. Enumerable.Range(0, 1014).Select(_ => Row(VlmCompare.Match)), .. Enumerable.Range(0, 14).Select(_ => Row(VlmCompare.Match, 2, 2)), .. Enumerable.Range(0, 2).Select(_ => Row(VlmCompare.Match, 3, 3))];

    [Fact]
    public void The_expected_result_of_the_full_run_passes_every_criterion()
    {
        IReadOnlyList<CheckResult> results = CaptionsCheckLogic.Evaluate(Input(GoodRows()));
        Assert.Equal(9, results.Count);
        Assert.All(results, r => Assert.True(r.Passed, r.Name + ": " + r.Detail));
    }

    [Fact]
    public void Each_criterion_fails_on_its_own_kind_of_problem()
    {
        static CheckResult Find(IReadOnlyList<CheckResult> rs, string prefix) => rs.Single(r => r.Name.StartsWith(prefix, StringComparison.Ordinal));
        List<CaptionsCheckRow> good = GoodRows();

        // 未処理
        Assert.False(Find(CaptionsCheckLogic.Evaluate(Input([.. good, Row(CaptionsCheckRow.NotProcessed)])), "1.").Passed);
        // 差ありは 3 枚まで
        Assert.True(Find(CaptionsCheckLogic.Evaluate(Input([.. good, .. Enumerable.Range(0, 3).Select(_ => Row(VlmCompare.Differs))])), "2.").Passed);
        Assert.False(Find(CaptionsCheckLogic.Evaluate(Input([.. good, .. Enumerable.Range(0, 4).Select(_ => Row(VlmCompare.Differs))])), "2.").Passed);
        // 製品だけ失敗は 0 枚
        Assert.False(Find(CaptionsCheckLogic.Evaluate(Input([.. good, Row(VlmCompare.ProductFailed, 1, null, detail: "製品は失敗: finish_reason=length")])), "2.").Passed);
        // 試行の回の不一致
        Assert.False(Find(CaptionsCheckLogic.Evaluate(Input([.. good, Row(VlmCompare.Match, 2, 3, mismatch: true)])), "3.").Passed);
        // length の失敗: 2 枚までで、理由は length だけ
        CaptionsCheckRow lengthFail = Row(VlmCompare.BothFailed, 0, null, detail: "評価の実験でも救えず、製品も失敗: finish_reason=length");
        Assert.True(Find(CaptionsCheckLogic.Evaluate(Input([.. good, lengthFail, lengthFail])), "4.").Passed);
        Assert.False(Find(CaptionsCheckLogic.Evaluate(Input([.. good, lengthFail, lengthFail, lengthFail])), "4.").Passed);
        Assert.False(Find(CaptionsCheckLogic.Evaluate(Input([.. good, Row(VlmCompare.BothFailed, 0, null, detail: "HTTP 400")])), "4.").Passed);
        // 救えた数: 期待(2 回目 14、3 回目 2)から 2 以上ずれたら不合格
        List<CaptionsCheckRow> fewer = [.. Enumerable.Range(0, 1014).Select(_ => Row(VlmCompare.Match)), .. Enumerable.Range(0, 11).Select(_ => Row(VlmCompare.Match, 2, 2)), .. Enumerable.Range(0, 2).Select(_ => Row(VlmCompare.Match, 3, 3)), .. Enumerable.Range(0, 3).Select(_ => Row(VlmCompare.ProductFailed, 2, null, detail: "x"))];
        Assert.False(Find(CaptionsCheckLogic.Evaluate(Input(fewer)), "5.").Passed);
        // 時間: p50 が評価の 0.75〜1.25 倍の外、p90 が 1.25 倍を超える
        Assert.False(Find(CaptionsCheckLogic.Evaluate(Input(good, p50: 16)), "6.").Passed);
        Assert.False(Find(CaptionsCheckLogic.Evaluate(Input(good, p50: 8)), "6.").Passed);
        Assert.False(Find(CaptionsCheckLogic.Evaluate(Input(good, p90: 30)), "6.").Passed);
        // runs: 行の数・ended_at・ビルド
        Assert.False(Find(CaptionsCheckLogic.Evaluate(Input(good, runs: 1)), "7.").Passed);
        // 再処理・段階の不一致
        Assert.False(Find(CaptionsCheckLogic.Evaluate(Input(good, reprocessed: 1)), "8.").Passed);
        Assert.False(Find(CaptionsCheckLogic.Evaluate(Input(good, ocr: 10, caption: 9)), "9.").Passed);
    }

    [Fact]
    public void Runs_without_an_end_mark_or_without_the_build_fail_and_times_can_be_skipped_for_imported_rows()
    {
        CaptionsCheckInput good = Input(GoodRows());
        var open = good with { Runs = [new RunRow(1, "vlm", "t", null, "{\"x\":\"12110\"}"), new RunRow(2, "vlm", "t", "t", "{\"x\":\"12110\"}")] };
        var noBuild = good with { Runs = [new RunRow(1, "vlm", "t", "t", "{}"), new RunRow(2, "vlm", "t", "t", "{}")] };
        var embedOnly = good with { Runs = [new RunRow(1, "embed", "t", null, "{}")] };
        Assert.False(CaptionsCheckLogic.Evaluate(open).Single(r => r.Name.StartsWith("7.", StringComparison.Ordinal)).Passed);
        Assert.False(CaptionsCheckLogic.Evaluate(noBuild).Single(r => r.Name.StartsWith("7.", StringComparison.Ordinal)).Passed);
        Assert.False(CaptionsCheckLogic.Evaluate(embedOnly).Single(r => r.Name.StartsWith("7.", StringComparison.Ordinal)).Passed);   // VLM 以外の行は数えない

        CheckResult skipped = CaptionsCheckLogic.Evaluate(Input(GoodRows(), times: false)).Single(r => r.Name.StartsWith("6.", StringComparison.Ordinal));
        Assert.True(skipped.Passed);
        Assert.True(skipped.Weak);                                    // 通ったが「見ていない」と分かる
    }

    // ---------------------------------------------------------------- 通しの運転の合格の見方

    private static VlmFullObservation Obs(
        int pending = 0, int done = 1030, int failed = 0, int embedFailed = 0, IndexRunState run = IndexRunState.Idle, double seconds = 12_000, int? vram = 6_100,
        double priv = 9_800, double ws = 3_200, bool pauseExpected = true, bool paused = true, bool gone = true, bool released = true, int runs = 2,
        string? vramProblem = null, int gpuWideDelta = 6_100) =>
        new(pending, done, failed, embedFailed, run, seconds, vram, priv, ws, pauseExpected, paused, gone, released, runs, vramProblem, gpuWideDelta);

    [Fact]
    public void The_vram_criterion_is_decided_by_the_process_value_not_by_the_gpu_wide_value()
    {
        // 実測: GPU 全体 − 起動前は 6,783MiB(ブラウザの分が混ざった)、llama-server のプロセスごとの値は 6,058MiB
        CheckResult d = VlmFullLogic.Evaluate(Obs(vram: 6_058, gpuWideDelta: 6_783)).Single(r => r.Name.StartsWith("D.", StringComparison.Ordinal));
        Assert.True(d.Passed, d.Detail);
        Assert.False(d.Unmeasured);
        Assert.Contains("6058", d.Detail, StringComparison.Ordinal);
        Assert.Contains("6783", d.Detail, StringComparison.Ordinal);                  // 参考として並べる
        Assert.Contains("合否に使わない", d.Detail, StringComparison.Ordinal);

        // 逆: プロセスごとの値が足切りの外なら、GPU 全体の値が小さくても不合格
        CheckResult over = VlmFullLogic.Evaluate(Obs(vram: 6_700, gpuWideDelta: 6_000)).Single(r => r.Name.StartsWith("D.", StringComparison.Ordinal));
        Assert.False(over.Passed);
        Assert.False(over.Unmeasured);
    }

    [Fact]
    public void An_unmeasured_vram_is_reported_as_unmeasured_and_never_falls_back_to_the_gpu_wide_value()
    {
        // GPU 全体の値(6,100)は足切りの内側だが、プロセスごとの値が無いので、通さない
        CheckResult d = VlmFullLogic.Evaluate(Obs(vram: null, vramProblem: "性能カウンタの読み取りが 3 回失敗した", gpuWideDelta: 6_100)).Single(r => r.Name.StartsWith("D.", StringComparison.Ordinal));
        Assert.False(d.Passed);
        Assert.True(d.Unmeasured);
        Assert.Contains("測れなかった", d.Detail, StringComparison.Ordinal);
        Assert.Contains("性能カウンタの読み取りが 3 回失敗した", d.Detail, StringComparison.Ordinal);
        Assert.Contains("6100", d.Detail, StringComparison.Ordinal);                  // 参考の値は残る
        Assert.Contains("戻さない", d.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_exit_code_separates_unmeasured_from_failed()
    {
        IReadOnlyList<CheckResult> ok = VlmFullLogic.Evaluate(Obs());
        IReadOnlyList<CheckResult> unmeasuredOnly = VlmFullLogic.Evaluate(Obs(vram: null, vramProblem: "x"));
        IReadOnlyList<CheckResult> failed = VlmFullLogic.Evaluate(Obs(pending: 5));
        IReadOnlyList<CheckResult> both = VlmFullLogic.Evaluate(Obs(pending: 5, vram: null, vramProblem: "x"));
        Assert.Equal(0, VlmFullLogic.ExitCode(ok, IndexRunState.Idle));
        Assert.Equal(7, VlmFullLogic.ExitCode(unmeasuredOnly, IndexRunState.Idle));      // 測れなかっただけ
        Assert.Equal(6, VlmFullLogic.ExitCode(failed, IndexRunState.Idle));
        Assert.Equal(6, VlmFullLogic.ExitCode(both, IndexRunState.Idle));                // 不合格があれば 6
        Assert.Equal(3, VlmFullLogic.ExitCode(failed, IndexRunState.Failed));            // 実行が Failed なら 3
    }

    [Fact]
    public void A_run_that_ended_waiting_for_new_images_counts_as_complete_but_failed_or_paused_does_not()
    {
        // 待ちが無くなったジョブは、待機(Waiting)に入って終わる(ADR 0028)。通しの「完走」は Idle でも Waiting でも合格
        Assert.True(VlmFullLogic.Evaluate(Obs(run: IndexRunState.Waiting)).Single(r => r.Name.StartsWith("A.", StringComparison.Ordinal)).Passed);
        Assert.True(VlmFullLogic.Evaluate(Obs(run: IndexRunState.Idle)).Single(r => r.Name.StartsWith("A.", StringComparison.Ordinal)).Passed);
        Assert.False(VlmFullLogic.Evaluate(Obs(run: IndexRunState.Failed)).Single(r => r.Name.StartsWith("A.", StringComparison.Ordinal)).Passed);
        Assert.False(VlmFullLogic.Evaluate(Obs(run: IndexRunState.Paused)).Single(r => r.Name.StartsWith("A.", StringComparison.Ordinal)).Passed);
    }

    [Fact]
    public void An_evaluation_like_full_run_passes_the_operation_criteria()
    {
        Assert.All(VlmFullLogic.Evaluate(Obs()), r => Assert.True(r.Passed, r.Name + ": " + r.Detail));
    }

    [Theory]
    [InlineData("A", 5, 1030, 0, 0, IndexRunState.Idle, 12000.0, 6100, 9800.0, 3200.0, true, true, true, true, 2)]       // VLM の待ちが残った
    [InlineData("A", 0, 1030, 0, 0, IndexRunState.Failed, 12000.0, 6100, 9800.0, 3200.0, true, true, true, true, 2)]    // 最後の状態が Failed
    [InlineData("A", 0, 1030, 0, 3, IndexRunState.Idle, 12000.0, 6100, 9800.0, 3200.0, true, true, true, true, 2)]      // 埋め込みの失敗
    [InlineData("B", 0, 1030, 0, 0, IndexRunState.Idle, 12000.0, 6100, 9800.0, 3200.0, true, false, true, true, 2)]     // 一時停止しなかった
    [InlineData("B", 0, 1030, 0, 0, IndexRunState.Idle, 12000.0, 6100, 9800.0, 3200.0, true, true, false, true, 2)]     // llama-server が終わらない
    [InlineData("B", 0, 1030, 0, 0, IndexRunState.Idle, 12000.0, 6100, 9800.0, 3200.0, true, true, true, false, 2)]     // VRAM が戻らない
    [InlineData("B", 0, 1030, 0, 0, IndexRunState.Idle, 12000.0, 6100, 9800.0, 3200.0, true, true, true, true, 1)]      // runs の行が 1 つ
    [InlineData("C", 0, 1030, 0, 0, IndexRunState.Idle, 17000.0, 6100, 9800.0, 3200.0, true, true, true, true, 2)]      // 4.7 時間(合格線 4.5 時間)
    [InlineData("D", 0, 1030, 0, 0, IndexRunState.Idle, 12000.0, 6700, 9800.0, 3200.0, true, true, true, true, 2)]      // VRAM が足切りの外
    [InlineData("E", 0, 1030, 0, 0, IndexRunState.Idle, 12000.0, 6100, 12500.0, 3200.0, true, true, true, true, 2)]     // プライベートが多い
    [InlineData("E", 0, 1030, 0, 0, IndexRunState.Idle, 12000.0, 6100, 9800.0, 4100.0, true, true, true, true, 2)]      // ワーキングセットが多い
    public void Each_operation_criterion_fails_on_its_own_problem(string name, int pending, int done, int failed, int embedFailed, IndexRunState run, double seconds, int vram, double priv, double ws, bool pauseExpected, bool paused, bool gone, bool released, int runs)
    {
        IReadOnlyList<CheckResult> results = VlmFullLogic.Evaluate(Obs(pending, done, failed, embedFailed, run, seconds, vram, priv, ws, pauseExpected, paused, gone, released, runs));
        Assert.False(results.Single(r => r.Name.StartsWith(name + ".", StringComparison.Ordinal)).Passed);
    }

    [Fact]
    public void Without_a_pause_the_pause_criterion_is_not_evaluated_and_the_limits_follow_the_evaluation()
    {
        IReadOnlyList<CheckResult> results = VlmFullLogic.Evaluate(Obs(pauseExpected: false, paused: false, runs: 1));
        Assert.DoesNotContain(results, r => r.Name.StartsWith("B.", StringComparison.Ordinal));
        Assert.All(results, r => Assert.True(r.Passed, r.Name));

        Assert.Equal(4.5 * 3600, VlmFullLogic.ElapsedLimitSeconds, 3);         // 4.5 時間(再試行を重く見た見積もりの上限 4.4 時間を含む)
        Assert.True(VlmFullLogic.ElapsedLimitSeconds > 4.4 * 3600 && VlmFullLogic.ElapsedLimitSeconds > VlmFullLogic.VlmEvalTotalSeconds);
        Assert.Equal(6656, VlmFullLogic.VramLimitMiB);                         // 評価の足切り
        Assert.True(VlmFullLogic.PrivateLimitMiB > 9812 && VlmFullLogic.WorkingSetLimitMiB > 3178);   // 評価の実測より上
    }

    // ---------------------------------------------------------------- 取り込み済みの評価の JSON を読む

    [Fact]
    public void The_retry_runs_request_extra_json_is_not_an_image_result()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ohanami-fullrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "request-extra.json"), "{\"dry_multiplier\":0.8}");
            File.WriteAllText(Path.Combine(dir, "a.json"), "{\"filename\":\"a.png\",\"http_status\":500,\"finish_reason\":\"\",\"parsed\":{\"ok\":false}}");
            IReadOnlyList<VlmEvalImage> images = VlmEvalRun.ReadImages(dir);
            Assert.Equal("a.png", Assert.Single(images).FileName);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_wall_times_of_the_evaluation_summary_are_read_in_seconds_by_nfc_name()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ohanami-fullrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string nfd = "が.png".Normalize(System.Text.NormalizationForm.FormD);
            File.WriteAllText(Path.Combine(dir, "summary.tsv"), $"filename\tstratum\twall_ms\n{nfd}\tall\t12500.5\nb.png\tall\t1000\n");
            Dictionary<string, double> walls = CaptionsCheckCommand.ReadVlmEvalWalls(dir);
            Assert.Equal(12.5005, walls["が.png"], 6);
            Assert.Equal(1.0, walls["b.png"], 6);
            File.WriteAllText(Path.Combine(dir, "summary.tsv"), "filename\tx\na\t1\n");
            Assert.Throws<InvalidDataException>(() => CaptionsCheckCommand.ReadVlmEvalWalls(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
