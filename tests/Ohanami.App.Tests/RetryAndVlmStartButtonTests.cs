using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>「失敗をやり直す」と「VLM を始める」のボタンのつなぎ(IndexPanel と OhanamiCore の間。実物の DB)。</summary>
public sealed class RetryAndVlmStartButtonTests
{
    private const string ModelName = "model-x";

    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string n, string v) in vars) map[n] = v;
        return name => map.TryGetValue(name, out string? v) ? v : null;
    }

    private static IndexState State(
        IndexRunState run = IndexRunState.Idle, JobKind? job = null, IReadOnlyList<StageSummary>? stages = null, EngineStatus? engine = null,
        TimeSpan? remaining = null, double? rate = null, string? path = null, string? error = null, int skipped = 0) =>
        new(run, job, stages ?? [], engine ?? EngineStatus.NotCreated, null, path, rate, remaining, error, skipped);

    private static StageSummary Stg(Stage stage, int pending = 0, int done = 0, int failed = 0, int outdated = 0) => new(stage, pending, done, failed, outdated);

    // ---------------------------------------------------------------- 「失敗をやり直す」

    [Fact]
    public void The_retry_plan_counts_failures_per_job_and_puts_back_only_the_failed_jobs()
    {
        RetryPlan none = IndexStatusText.PlanRetry(State(stages: [Stg(Stage.Embed, done: 5), Stg(Stage.Ocr, done: 5), Stg(Stage.Caption, done: 5)]));
        Assert.False(none.Any);

        RetryPlan embedOnly = IndexStatusText.PlanRetry(State(stages: [Stg(Stage.Embed, done: 5, failed: 2), Stg(Stage.Caption, done: 5)]));
        Assert.True(embedOnly.Any);
        Assert.Equal([JobKind.Embed], embedOnly.RetryKinds);                      // 待ちに戻すのは、失敗のある段階だけ(始めるジョブは、いつも [Embed, Vlm] の 1 通り)

        RetryPlan vlm = IndexStatusText.PlanRetry(State(stages: [Stg(Stage.Embed, done: 5), Stg(Stage.Ocr, done: 3, failed: 1), Stg(Stage.Caption, done: 3, failed: 4)]));
        Assert.Equal(4, vlm.VlmFailed);                                           // ocr と caption の多いほう
        Assert.Equal([JobKind.Vlm], vlm.RetryKinds);

        RetryPlan both = IndexStatusText.PlanRetry(State(stages: [Stg(Stage.Embed, failed: 1), Stg(Stage.Caption, failed: 1)]));
        Assert.Equal([JobKind.Embed, JobKind.Vlm], both.RetryKinds);
        Assert.Equal(0, IndexStatusText.PlanRetry(State()).EmbedFailed);          // 段階が無い(VLM の無い Core)
    }

    [Fact]
    public async Task The_retry_button_without_failures_is_disabled_and_says_so_when_called()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp, vlm: null);
            var notices = new List<string>();
            var panel = new IndexPanel(new TestLog(), notices.Add, () => false);
            panel.Attach(core);

            Assert.False(panel.CurrentSnapshot.Action(ProcessingAction.RetryEmbed).Enabled);
            Assert.False(panel.CurrentSnapshot.Action(ProcessingAction.RetryVlm).Enabled);
            await panel.RetryFailedAsync();

            Assert.Equal(["失敗した画像はありません"], notices);
            Assert.Equal(IndexRunState.Idle, panel.Run);
        });
    }

    [Fact]
    public async Task The_retry_button_puts_failed_vlm_rows_back_and_starts_the_jobs()
    {
        using var temp = new TempDirectory();
        string images = temp.File("images");
        Directory.CreateDirectory(images);
        AgedFile.Write(Path.Combine(images, "a.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8]);
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp, vlm: new VlmOptions(temp.File("no-llama.exe"), temp.File("no-vlm-models")));
            await core.Folders.AddAsync(images);
            await core.Indexer.ScanAsync();
            string hash = Scalar<string>(core, "SELECT hash FROM images");
            // 夜間の通しで caption が失敗した(length)、という行を作る
            await core.DatabaseForTests.WriteAsync((conn, tx) =>
            {
                Exec(conn, tx, "INSERT INTO models(id, kind, name, spec_json, created_at) VALUES ('vlm-test', 'vlm', 'x', '{}', 't')");
                Exec(conn, tx, "INSERT INTO runs(id, job_kind, started_at, app_version, runtime_json) VALUES (1, 'vlm', 't', 'test', '{}')");
                foreach (string stage in new[] { "ocr", "caption" })
                    Exec(conn, tx, $"INSERT INTO stage_status(hash, stage, status, model_id, run_id, settings_json, error, attempts, updated_at) VALUES ('{hash}', '{stage}', 'failed', 'vlm-test', 1, '{{\"attempts\":[{{\"attempt\":1}}]}}', 'finish_reason=length', 1, 't')");
            });
            var notices = new List<string>();
            var panel = new IndexPanel(new TestLog(), notices.Add, () => false);
            panel.Attach(core);
            Assert.True(panel.CurrentSnapshot.Action(ProcessingAction.RetryVlm).Enabled);        // VLM の失敗がある
            Assert.Contains($"{TestWords.TextSearch}: 0 / 1 枚 (失敗 1)", panel.StatusText, StringComparison.Ordinal);

            await panel.RetryFailedAsync();

            Assert.Equal(0, Scalar<long>(core, "SELECT count(*) FROM stage_status WHERE status = 'failed'"));     // 失敗の行は消えて、待ちに戻った
            Assert.Contains(notices, n => n.Contains($"未作成に戻しました (特徴ベクトル検索 0 枚、{TestWords.TextSearch} 1 枚)", StringComparison.Ordinal));
            // そのまま [Embed, Vlm] を始めた(この Core には埋め込みのモデルが無いので、埋め込みの準備で失敗して止まる。ジョブが動いたことの印)
            Assert.NotEqual(IndexRunState.Running, panel.Run);
        });
    }

    private static T Scalar<T>(OhanamiCore core, string sql)
    {
        using Microsoft.Data.Sqlite.SqliteConnection conn = core.DatabaseForTests.CreateReadConnection();
        using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction tx, string sql)
    {
        using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- 「VLM を始める」のつなぎ

    private static Task<OhanamiCore> OpenCoreAsync(TempDirectory temp, VlmOptions? vlm)
    {
        string models = temp.File("no-models");
        Directory.CreateDirectory(models);
        var options = new CoreOptions(temp.File("data/ohanami.db"), temp.File("thumbs"), models, AppContext.BaseDirectory, new TestLog(), vlm, WatchFolders: false);
        return OhanamiCore.OpenAsync(options, configureNative: false, time: null, CancellationToken.None);
    }

    [Fact]
    public async Task The_vlm_button_without_vlm_options_reports_the_failure_in_the_notice_instead_of_crashing()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp, vlm: null);
            var notices = new List<string>();
            var panel = new IndexPanel(new TestLog(), notices.Add, () => false);
            panel.Attach(core);

            await panel.StartProcessingAsync();

            Assert.Contains(notices, n => n.StartsWith("インデックス作成を開始できませんでした: ", StringComparison.Ordinal));      // VLM のランナーが無い(場所が組み込まれていない)
            Assert.Equal(IndexRunState.Idle, panel.Run);
        });
    }

    [Fact]
    public async Task The_vlm_button_with_vlm_options_starts_the_embed_then_vlm_jobs_and_an_empty_library_finishes_at_once()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            AppPaths paths = AppPaths.Resolve(Env((AppPaths.DataDirectoryVariable, temp.File("data")), (AppPaths.RuntimeDirectoryVariable, temp.File("rt")), (AppPaths.ModelsDirectoryVariable, temp.File("md"))),
                temp.File("local"), temp.File("start"), ModelName);
            await using OhanamiCore core = await OpenCoreAsync(temp, paths.ToVlmOptions());
            var notices = new List<string>();
            var panel = new IndexPanel(new TestLog(), notices.Add, () => false);
            panel.Attach(core);

            await panel.StartProcessingAsync();

            Assert.Empty(notices);                                             // 失敗の知らせは出ない(待ちが無いので、llama-server の検証も起動もしない)
            Assert.Equal(IndexRunState.Waiting, panel.Run);
            Assert.Contains($"{TestWords.TextSearch}: 0 / 0 枚", panel.StatusText, StringComparison.Ordinal);      // VLM の段階が表示に出る
            Assert.Contains("特徴ベクトル検索: 0 / 0 枚", panel.StatusText, StringComparison.Ordinal);
        });
    }
}
