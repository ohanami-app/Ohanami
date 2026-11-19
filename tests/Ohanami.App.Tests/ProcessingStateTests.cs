using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// 処理状態の欄(ADR 0039): 要再作成を始める(件数と見込みの時間の確認)、フォルダに無い画像の記録を消す(確認の文言)、失敗の一覧と飛ばした画像の一覧、
/// ボタンの押せる条件、失敗の一覧の「詳細」(検索の結果の外から詳細画面を開く)。
/// </summary>
public sealed class ProcessingStateTests
{
    private static string H(char c) => new(c, 64);

    // ---------------------------------------------------------------- 文章と計画(純粋な関数)

    private static IndexState State(
        IndexRunState run = IndexRunState.Idle, IReadOnlyList<StageSummary>? stages = null, int skipped = 0) =>
        new(run, null, stages ?? [], EngineStatus.NotCreated, null, null, null, null, null, skipped);

    private static StageSummary Stg(Stage stage, int pending = 0, int done = 0, int failed = 0, int outdated = 0) => new(stage, pending, done, failed, outdated);

    [Fact]
    public void The_requeue_plan_counts_outdated_per_job_and_takes_the_larger_of_ocr_and_caption_for_the_vlm()
    {
        IndexState s = State(stages: [Stg(Stage.Embed, outdated: 3), Stg(Stage.Ocr, outdated: 5), Stg(Stage.Caption, outdated: 7)]);

        RequeuePlan plan = ProcessingText.PlanRequeue(s);

        Assert.Equal(3, plan.For(JobKind.Embed));
        Assert.Equal(7, plan.For(JobKind.Vlm));
        Assert.Equal(new RequeuePlan(0, 0), ProcessingText.PlanRequeue(State()));
    }

    [Fact]
    public void Every_start_uses_the_one_specification_embedding_first_then_vlm()
    {
        // 開始の指定は 1 通り(ADR 0039)。要再作成・失敗のやり直し・飛ばした画像のもう一度も、同じ(StartAsyncSingleCallSiteTests が呼び出しの 1 か所を見張る)
        Assert.Equal([JobKind.Embed, JobKind.Vlm], IndexPanel.AllJobs);
    }

    [Fact]
    public void The_estimate_uses_twelve_seconds_per_image_for_the_vlm()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), ProcessingText.Estimate(JobKind.Vlm, 10));
        Assert.Equal(TimeSpan.FromSeconds(12), ProcessingText.Estimate(JobKind.Embed, 120));
    }

    [Fact]
    public void The_requeue_confirmation_shows_the_count_and_the_estimated_time_and_says_old_results_stay()
    {
        string vlm = ProcessingText.RequeueConfirmText(JobKind.Vlm, 300);          // 300 枚 × 12 秒 = 1 時間

        Assert.Contains("300 枚", vlm, StringComparison.Ordinal);
        Assert.Contains("約 1 時間 0 分", vlm, StringComparison.Ordinal);
        Assert.Contains("1 枚約 12 秒で計算しています", vlm, StringComparison.Ordinal);
        Assert.Contains("古い結果のままテキスト検索に使われます", vlm, StringComparison.Ordinal);                 // 段階ごとの言い分け(ADR 0038 の語の表)
        Assert.Equal("テキスト検索の再作成を始める", ProcessingText.RequeueConfirmTitle(JobKind.Vlm));
        Assert.Contains("この画像は特徴ベクトル検索に出ません", ProcessingText.RequeueConfirmText(JobKind.Embed, 5), StringComparison.Ordinal);
        Assert.Contains("1 秒未満", ProcessingText.RequeueConfirmText(JobKind.Embed, 5), StringComparison.Ordinal);   // 5 × 0.1 秒
    }

    [Fact]
    public void The_purge_confirmation_says_files_stay_and_that_a_returning_image_is_indexed_again()
    {
        string text = ProcessingText.PurgeConfirmText(12, 3);

        Assert.Contains("12 枚 (うち手動タグあり 3 枚)", text, StringComparison.Ordinal);
        Assert.Contains("同じ画像が戻ったときは、インデックスを作り直します", text, StringComparison.Ordinal);
        Assert.Contains("画像ファイルは消えません", text, StringComparison.Ordinal);
        Assert.Contains("元に戻せません", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_rows_show_the_path_reason_time_and_attempts_and_a_missing_image_says_so()
    {
        var at = new DateTimeOffset(2026, 11, 8, 3, 4, 5, TimeSpan.Zero);
        var list = new FailureList(Stage.Embed,
        [
            new FailureItem(H('a'), @"C:\shots\a.png", "画像をデコード・前処理できない: x", 2, at, "m"),
            new FailureItem(H('b'), null, null, 1, at, "m"),
        ], 2);

        ProblemList shown = ProcessingText.FailureRows(JobKind.Embed, list, TimeZoneInfo.Utc);

        Assert.Equal("特徴ベクトル検索のインデックス作成に失敗 2 枚 (新しい順)", shown.Heading);
        Assert.False(shown.Truncated);
        Assert.Equal(@"C:\shots\a.png", shown.Rows[0].PathText);
        Assert.Equal("画像をデコード・前処理できません: x", shown.Rows[0].Cells[1]);
        Assert.Equal("2026-11-08 03:04", shown.Rows[0].Cells[2]);   // 日時は分まで
        Assert.Equal("2 回", shown.Rows[0].Cells[3]);
        Assert.Equal("(元のファイルが無い画像)", shown.Rows[1].PathText);
        Assert.Null(shown.Rows[1].Path);
        Assert.Equal("(理由の記録なし)", shown.Rows[1].Cells[1]);
    }

    [Fact]
    public void A_failure_list_cut_by_the_limit_says_how_many_are_shown_of_how_many()
    {
        var at = DateTimeOffset.UnixEpoch;
        var list = new FailureList(Stage.Caption, [new FailureItem(H('a'), "p", "e", 1, at, "m")], 250);

        ProblemList shown = ProcessingText.FailureRows(JobKind.Vlm, list, TimeZoneInfo.Utc);

        Assert.True(shown.Truncated);
        Assert.Equal("テキスト検索のインデックス作成に失敗 250 枚 (新しい順に 1 枚を表示)", shown.Heading);
    }

    [Fact]
    public void Skipped_rows_name_the_job_and_the_reason_and_say_they_are_not_recorded()
    {
        ProblemList shown = ProcessingText.SkippedRows([new SkippedItem(JobKind.Vlm, H('a'), @"C:\x.png", "別のプロセスが使っている"), new SkippedItem(JobKind.Embed, H('b'), null, "現存するパスが無い")]);

        Assert.Equal("テキスト検索: 別のプロセスが使っています", shown.Rows[0].Cells[1]);
        Assert.Equal("特徴ベクトル検索: 現存するパスがありません", shown.Rows[1].Cells[1]);
        Assert.Equal("(元のファイルが無い画像)", shown.Rows[1].PathText);
        Assert.Contains("記録もしていません", shown.Heading, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 失敗の一覧の画面

    private static ProblemList Sample(ProblemKind kind) => new(
        kind.ToString() + " の見出し",
        [new ProblemColumn("ファイル", 220), new ProblemColumn("理由", null), new ProblemColumn("日時", 140), new ProblemColumn("回数", 60)],
        [
            new ProblemRow(H('a'), @"C:\shots\a.png", @"C:\shots\a.png", ["a.png", "理由 A", "2026-11-08 01:00", "1 回"], "理由 A"),
            new ProblemRow(H('b'), null, "(元のファイルが無い画像)", ["(元のファイルが無い画像)", "理由 B", "2026-11-08 02:00", "2 回"], "理由 B"),
        ], false);

    [Fact]
    public async Task The_failure_window_lists_the_rows_and_enables_the_buttons_by_the_selection()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var asked = new List<ProblemKind>();
            var window = new FailureListWindow(ProblemKind.VlmFailures, (k, _) => { asked.Add(k); return Task.FromResult(Sample(k)); }, new FakeShell());
            await window.ReloadAsync();

            Assert.Equal(ProblemKind.VlmFailures, asked[^1]);
            Assert.Equal("VlmFailures の見出し", window.HeadingText);
            Assert.Equal(2, window.CurrentList!.Rows.Count);
            Assert.False(window.CanReveal);                       // 何も選んでいない
            Assert.False(window.CanOpenDetail);

            window.SelectRow(0);
            Assert.True(window.CanReveal);                        // 現存するパスがある
            Assert.True(window.CanOpenDetail);

            window.SelectRow(1);
            Assert.False(window.CanReveal);                       // 見つからない画像は、エクスプローラーでは表示できない
            Assert.True(window.CanOpenDetail);                    // 詳細は開ける(記録はある)
            window.Close();
        });
    }

    [Fact]
    public async Task Reveal_opens_the_explorer_for_an_existing_file_and_explains_a_missing_one_in_the_status_line()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var shell = new FakeShell();
            var window = new FailureListWindow(ProblemKind.EmbedFailures, (k, _) => Task.FromResult(Sample(k)), shell)
            {
                FileExists = p => p.EndsWith("a.png", StringComparison.Ordinal),
            };
            await window.ReloadAsync();

            window.SelectRow(0);
            window.Reveal();
            Assert.Equal(["reveal " + @"C:\shots\a.png"], shell.Calls);
            Assert.Contains("エクスプローラーで表示しました: a.png", window.StatusText, StringComparison.Ordinal);

            window.SelectRow(1);
            window.Reveal();                                      // 現存するパスが無い
            Assert.Single(shell.Calls);
            Assert.Contains("元のファイルが無い画像", window.StatusText, StringComparison.Ordinal);

            var goneShell = new FakeShell();
            var gone = new FailureListWindow(ProblemKind.EmbedFailures, (k, _) => Task.FromResult(Sample(k)), goneShell) { FileExists = _ => false };
            await gone.ReloadAsync();
            gone.SelectRow(0);
            gone.Reveal();                                        // DB の上では現存でも、ファイルが無い
            Assert.Empty(goneShell.Calls);
            Assert.Contains("元のファイルがありません", gone.StatusText, StringComparison.Ordinal);
            window.Close();
            gone.Close();
        });
    }

    [Fact]
    public async Task A_failing_reveal_is_shown_in_the_status_line_and_does_not_crash()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var window = new FailureListWindow(ProblemKind.EmbedFailures, (k, _) => Task.FromResult(Sample(k)), new FakeShell { FailWith = new IOException("シェルの失敗(テスト)") })
            {
                FileExists = _ => true,
            };
            await window.ReloadAsync();
            window.SelectRow(0);

            window.Reveal();

            Assert.Contains("エクスプローラーで表示できませんでした: シェルの失敗 (テスト)", window.StatusText, StringComparison.Ordinal);
            window.Close();
        });
    }

    [Fact]
    public async Task The_detail_button_raises_the_request_with_the_hash_and_closes_the_window()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var window = new FailureListWindow(ProblemKind.EmbedFailures, (k, _) => Task.FromResult(Sample(k)), new FakeShell());
            await window.ReloadAsync();
            var requested = new List<string>();
            bool closed = false;
            window.OpenDetailRequested += requested.Add;
            window.CloseWindow = () => closed = true;             // 画面を実際に表示しない(Close の呼び出しだけ確かめる)
            window.SelectRow(1);

            window.OpenDetail();

            Assert.Equal([H('b')], requested);
            Assert.True(closed);
        });
    }

    [Fact]
    public async Task Switching_the_kind_reloads_and_a_load_failure_is_shown_in_the_heading()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            bool fail = false;
            var window = new FailureListWindow(ProblemKind.EmbedFailures, (k, _) =>
                fail ? throw new InvalidOperationException("読めない(テスト)") : Task.FromResult(Sample(k)), new FakeShell());
            await window.ReloadAsync();
            Assert.Equal("EmbedFailures の見出し", window.HeadingText);

            fail = true;
            await window.ReloadAsync();

            Assert.Contains("一覧を読み込めませんでした: 読めません (テスト)", window.HeadingText, StringComparison.Ordinal);
            Assert.Null(window.CurrentList);
            Assert.False(window.CanOpenDetail);
            window.Close();
        });
    }

    // ---------------------------------------------------------------- パネルのボタンと操作(Core は本物。モデルが無いので、始めたジョブは準備で失敗する。GPU は使わない)

    private static Task<OhanamiCore> OpenCoreAsync(TempDirectory temp)
    {
        string models = temp.File("no-models");
        Directory.CreateDirectory(models);
        var options = new CoreOptions(temp.File("data/ohanami.db"), temp.File("thumbs"), models, AppContext.BaseDirectory, new TestLog(), null, WatchFolders: false);
        return OhanamiCore.OpenAsync(options, configureNative: false, time: null, CancellationToken.None);
    }

    private static string MakeFolder(TempDirectory temp, string name, int count)
    {
        string dir = temp.File(name);
        Directory.CreateDirectory(dir);
        for (int i = 0; i < count; i++)
            AgedFile.Write(Path.Combine(dir, $"{name}-{i}.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, (byte)i, (byte)name[0], 3, 4, 5, 6, 7, 8]);
        return dir;
    }

    private static T Scalar<T>(OhanamiCore core, string sql)
    {
        using Microsoft.Data.Sqlite.SqliteConnection conn = core.DatabaseForTests.CreateReadConnection();
        using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Task ExecAsync(OhanamiCore core, params string[] sql) =>
        core.DatabaseForTests.WriteAsync((conn, tx) =>
        {
            foreach (string s in sql)
            {
                using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = s;
                cmd.ExecuteNonQuery();
            }
        });

    [Theory]
    [InlineData(IndexRunState.Idle, 2, 1, 4, 3, true, true, true, true, true)]
    [InlineData(IndexRunState.Failed, 2, 1, 4, 3, true, true, true, true, true)]
    [InlineData(IndexRunState.Waiting, 2, 1, 4, 3, true, true, true, true, true)]
    [InlineData(IndexRunState.Paused, 2, 1, 4, 3, true, true, false, false, true)]      // 一時停止中は、再作成を始められない(「再開」を使う)。消すのは可
    [InlineData(IndexRunState.Running, 2, 1, 4, 3, true, true, false, false, false)]    // 動いている間は、消せない(Core も拒む)。一覧は見られる
    [InlineData(IndexRunState.Pausing, 2, 1, 4, 3, true, true, false, false, false)]
    [InlineData(IndexRunState.Idle, 0, 0, 0, 0, false, false, false, false, false)]     // 何も無ければ、全部押せない
    public async Task The_processing_buttons_follow_the_counts_and_the_run_state(
        IndexRunState run, int embedFailed, int vlmFailed, int embedOutdated, int vlmOutdated, bool failures, bool vlmFailures, bool embedRequeue, bool vlmRequeue, bool purge)
    {
        _ = vlmFailures; _ = vlmRequeue;
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            FolderInfo folder = await core.Folders.AddAsync(MakeFolder(temp, "a", 2));
            await core.Indexer.ScanAsync([folder.Id]);
            if (purge) await core.Folders.RemoveAsync(folder.Id);   // 消せる画像(パスの行が無い)を作る
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false, new FakeShell());
            panel.Attach(core);
            if (purge) Assert.True(await Wait.UntilAsync(() => panel.MissingCount == 2));

            panel.ShowForTests(State(run,
                [Stg(Stage.Embed, failed: embedFailed, outdated: embedOutdated), Stg(Stage.Ocr, failed: vlmFailed, outdated: vlmOutdated), Stg(Stage.Caption, failed: vlmFailed, outdated: vlmOutdated)],
                skipped: embedFailed > 0 ? 1 : 0));

            ProcessingSnapshot snap = panel.CurrentSnapshot;                              // 左パネルのボタンは無い。窓の操作の可否は、スナップショットが決める
            bool ef = snap.CanView(ProblemKind.EmbedFailures), vf = snap.CanView(ProblemKind.VlmFailures), sk = snap.CanView(ProblemKind.Skipped);
            bool er = snap.Action(ProcessingAction.RequeueEmbed).Enabled, vr = snap.Action(ProcessingAction.RequeueVlm).Enabled, pg = snap.Action(ProcessingAction.PurgeMissing).Enabled;
            Assert.Equal(embedFailed > 0, ef);
            Assert.Equal(vlmFailed > 0, vf);
            Assert.Equal(embedFailed > 0, sk);
            Assert.Equal(embedRequeue && embedOutdated > 0, er);
            Assert.Equal(vlmRequeue && vlmOutdated > 0, vr);
            Assert.Equal(purge, pg);
            Assert.Equal(failures, ef);
        });
    }

    [Fact]
    public async Task Purging_asks_with_the_count_and_the_wording_and_removes_only_after_ok()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            FolderInfo folder = await core.Folders.AddAsync(MakeFolder(temp, "a", 2));
            await core.Indexer.ScanAsync([folder.Id]);
            await core.Folders.RemoveAsync(folder.Id);                                  // 解除: images は残り、パスの行は 0 行 = 消せる画像が 2 枚
            var notices = new List<string>();
            var panel = new IndexPanel(new TestLog(), notices.Add, () => false, new FakeShell());
            panel.Attach(core);
            Assert.True(await Wait.UntilAsync(() => panel.MissingCount == 2));
            var asked = new List<(string Title, string Text)>();
            bool answer = false;
            panel.ConfirmFunc = (t, x) => { asked.Add((t, x)); return answer; };

            await panel.PurgeMissingAsync();                                            // キャンセル

            Assert.Single(asked);
            Assert.Equal(ProcessingText.PurgeConfirmTitle, asked[0].Title);
            Assert.Contains("2 枚", asked[0].Text, StringComparison.Ordinal);
            Assert.Contains("同じ画像が戻ったときは", asked[0].Text, StringComparison.Ordinal);
            Assert.Equal(2, Scalar<long>(core, "SELECT count(*) FROM images"));        // 消えていない
            Assert.DoesNotContain(notices, n => n.Contains("消した", StringComparison.Ordinal));

            answer = true;
            await panel.PurgeMissingAsync();                                            // OK

            Assert.Equal(0, Scalar<long>(core, "SELECT count(*) FROM images"));
            Assert.Contains(notices, n => n == "フォルダに無い画像の記録を消しました (2 枚)");
            Assert.True(await Wait.UntilAsync(() => panel.MissingCount == 0));
            Assert.False(panel.CurrentSnapshot.Action(ProcessingAction.PurgeMissing).Enabled);

            await panel.PurgeMissingAsync();                                            // 0 枚になった
            Assert.Equal(2, asked.Count);                                               // 確認は出さない
            Assert.Contains(notices, n => n == "フォルダに無い画像の記録はありません");
        });
    }

    [Fact]
    public async Task Requeueing_outdated_images_asks_with_the_count_and_the_estimate_then_puts_them_back_and_starts()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            FolderInfo folder = await core.Folders.AddAsync(MakeFolder(temp, "a", 1));
            await core.Indexer.ScanAsync([folder.Id]);
            string hash = Scalar<string>(core, "SELECT hash FROM images");
            // 古いモデルで埋め込みが済んでいる画像(要再作成)を作る
            await ExecAsync(core,
                "INSERT INTO models(id, kind, name, spec_json, created_at) VALUES ('old-embed', 'vlm', 'x', '{}', 't')",
                "INSERT INTO runs(id, job_kind, started_at, app_version, runtime_json) VALUES (1, 'embed', 't', 'test', '{}')",
                $"INSERT INTO stage_status(hash, stage, status, model_id, run_id, settings_json, error, attempts, updated_at) VALUES ('{hash}', 'embed', 'done', 'old-embed', 1, NULL, NULL, 1, '2026-11-06T00:00:00.000Z')");
            var notices = new List<string>();
            var panel = new IndexPanel(new TestLog(), notices.Add, () => false, new FakeShell());
            panel.Attach(core);
            Assert.Contains("要再作成 1", panel.StatusText, StringComparison.Ordinal);
            Assert.True(panel.CurrentSnapshot.Action(ProcessingAction.RequeueEmbed).Enabled);
            Assert.False(panel.CurrentSnapshot.Action(ProcessingAction.RequeueVlm).Enabled);
            var asked = new List<(string Title, string Text)>();
            bool answer = false;
            panel.ConfirmFunc = (t, x) => { asked.Add((t, x)); return answer; };

            await panel.RequeueAsync(JobKind.Embed);                                    // キャンセル

            Assert.Single(asked);
            Assert.Equal("特徴ベクトル検索の再作成を始める", asked[0].Title);
            Assert.Contains("1 枚", asked[0].Text, StringComparison.Ordinal);
            Assert.Contains("1 枚約 0.1 秒で計算しています", asked[0].Text, StringComparison.Ordinal);
            Assert.Equal(1, Scalar<long>(core, "SELECT count(*) FROM stage_status"));  // 戻していない

            answer = true;
            await panel.RequeueAsync(JobKind.Embed);                                    // OK

            Assert.Equal(0, Scalar<long>(core, "SELECT count(*) FROM stage_status"));  // 行を消して待ちに戻した(古いベクトルなどの結果は残る設計)
            Assert.Contains(notices, n => n.Contains("要再作成の画像 1 枚を未作成に戻しました (特徴ベクトル検索)", StringComparison.Ordinal));
            Assert.NotEqual(IndexRunState.Running, panel.Run);                          // そのまま始めた(この Core には埋め込みのモデルが無いので、準備で失敗して止まる)
        });
    }

    [Fact]
    public async Task Requeueing_with_nothing_outdated_only_says_so()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            var notices = new List<string>();
            var panel = new IndexPanel(new TestLog(), notices.Add, () => false, new FakeShell());
            panel.Attach(core);
            panel.ConfirmFunc = (_, _) => throw new InvalidOperationException("確認は出さないはず");

            await panel.RequeueAsync(JobKind.Vlm);

            Assert.Equal(["要再作成の画像はありません"], notices);
        });
    }

    [Fact]
    public async Task The_failure_list_from_the_panel_reads_core_and_the_detail_button_reaches_the_panel_event()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            FolderInfo folder = await core.Folders.AddAsync(MakeFolder(temp, "a", 1));
            await core.Indexer.ScanAsync([folder.Id]);
            string hash = Scalar<string>(core, "SELECT hash FROM images");
            await ExecAsync(core,
                "INSERT INTO models(id, kind, name, spec_json, created_at) VALUES ('m', 'vlm', 'x', '{}', 't')",
                "INSERT INTO runs(id, job_kind, started_at, app_version, runtime_json) VALUES (1, 'embed', 't', 'test', '{}')",
                $"INSERT INTO stage_status(hash, stage, status, model_id, run_id, settings_json, error, attempts, updated_at) VALUES ('{hash}', 'embed', 'failed', 'm', 1, NULL, 'デコードできません (テスト)', 2, '2026-11-06T00:00:00.000Z')");
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false, new FakeShell());
            FailureListWindow? shown = null;
            panel.ShowFailureWindow = w => shown = w;                                   // 実際には表示しない
            panel.Attach(core);
            Assert.True(panel.CurrentSnapshot.CanView(ProblemKind.EmbedFailures));
            var requested = new List<string>();
            panel.OpenDetailRequested += requested.Add;

            FailureListWindow? window = panel.OpenFailureList(ProblemKind.EmbedFailures);
            Assert.Same(shown, window);
            await window!.ReloadAsync();

            ProblemList list = window.CurrentList!;
            Assert.Equal("特徴ベクトル検索のインデックス作成に失敗 1 枚 (新しい順)", list.Heading);
            ProblemRow row = Assert.Single(list.Rows);
            Assert.Equal(hash, row.Hash);
            Assert.Equal("デコードできません (テスト)", row.Cells[1]);
            Assert.Equal("2 回", row.Cells[3]);
            Assert.EndsWith("a-0.png", row.PathText, StringComparison.OrdinalIgnoreCase);

            window.SelectRow(0);
            window.OpenDetail();
            Assert.Equal([hash], requested);
        });
    }

    // ---------------------------------------------------------------- 失敗の一覧の画面は 1 つだけ

    [Fact]
    public async Task Pressing_the_failure_buttons_again_reuses_the_one_window_fronts_it_and_switches_the_kind_and_a_closed_window_is_replaced()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false, new FakeShell());
            var shown = new List<FailureListWindow>();
            var fronted = new List<FailureListWindow>();
            panel.ShowFailureWindow = shown.Add;                    // 実際には表示しない
            panel.ActivateFailureWindow = fronted.Add;              // 実際には前に出さない
            panel.Attach(core);

            FailureListWindow first = panel.OpenFailureList(ProblemKind.EmbedFailures)!;
            Assert.Equal(ProblemKind.EmbedFailures, first.Kind);
            Assert.Equal([first], shown);
            Assert.Empty(fronted);                                  // 新しく作ったときは、表示だけ(前に出す処理は、開いている窓があるときだけ)

            // 別の種類のボタン: 窓は増えない。同じ窓を前に出して、その種類に切り替える
            FailureListWindow second = panel.OpenFailureList(ProblemKind.VlmFailures)!;
            Assert.Same(first, second);
            Assert.Equal([first], shown);                           // 表示は 1 回だけ
            Assert.Equal([first], fronted);
            Assert.Equal(ProblemKind.VlmFailures, first.Kind);
            await first.ReloadAsync();
            Assert.StartsWith("テキスト検索のインデックス作成に失敗", first.CurrentList!.Heading, StringComparison.Ordinal);

            // 同じ種類をもう一度: 窓は増えない。前に出して、いまの種類を読み直す
            FailureListWindow third = panel.OpenFailureList(ProblemKind.VlmFailures)!;
            Assert.Same(first, third);
            Assert.Equal([first, first], fronted);
            Assert.Equal(ProblemKind.VlmFailures, first.Kind);
            FailureListWindow skipped = panel.OpenFailureList(ProblemKind.Skipped)!;
            Assert.Same(first, skipped);
            Assert.Equal(ProblemKind.Skipped, first.Kind);
            Assert.Single(shown);

            // 閉じたあとは、また新しく作る
            first.Close();
            FailureListWindow fresh = panel.OpenFailureList(ProblemKind.EmbedFailures)!;
            Assert.NotSame(first, fresh);
            Assert.Equal([first, fresh], shown);
            Assert.Equal(ProblemKind.EmbedFailures, fresh.Kind);
        });
    }

    [Fact]
    public async Task The_failure_window_without_a_core_is_not_opened_and_nothing_is_fronted()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false, new FakeShell());
            var shown = new List<FailureListWindow>();
            panel.ShowFailureWindow = shown.Add;
            panel.ActivateFailureWindow = _ => throw new InvalidOperationException("Core が無いので、窓は出さない");

            Assert.Null(panel.OpenFailureList(ProblemKind.EmbedFailures));
            Assert.Empty(shown);
            await Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- 開始のボタンのツールチップ(「インデックス作成を開始」)

    [Fact]
    public async Task The_start_button_tooltip_comes_from_the_estimate_and_not_from_a_fixed_sentence()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false, new FakeShell());

            Assert.Equal(ProcessingWords.StartTooltip(), panel.StartToolTip);
            Assert.DoesNotContain("36 時間", panel.StartToolTip, StringComparison.Ordinal);
            Assert.Contains("1 万枚で約 33 時間", panel.StartToolTip, StringComparison.Ordinal);    // 12 秒 × 1 万枚 = 33.3 時間
            await Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- 空の欄の専用の空表示は製品では到達しない

    [Fact]
    public async Task In_the_product_wiring_the_search_and_the_recent_list_are_bound_and_released_together_so_an_empty_box_always_has_the_recent_list()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            var panel = new ResultsPanel(new TestLog(), _ => { }, new FakeShell());
            Assert.Null(panel.SearchFunc);                          // 結ぶ前は、どちらも無い(検索ボックスは使えない)
            Assert.Null(panel.RecentFunc);

            panel.Attach(core);
            Assert.NotNull(panel.SearchFunc);                       // Attach で、一緒に結ばれる
            Assert.NotNull(panel.RecentFunc);                       // → 空の欄の検索は、いつも新しい順の一覧に戻る(RecentFunc が無いまま SearchFunc だけがある状態は、製品には無い)

            panel.Detach();
            Assert.Null(panel.SearchFunc);                          // Detach で、一緒に外れる
            Assert.Null(panel.RecentFunc);
        });
    }

    [Fact]
    public async Task An_empty_search_without_the_recent_list_wired_only_clears_and_keeps_the_first_empty_message_for_tests()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var panel = new ResultsPanel(new TestLog(), _ => { }, new FakeShell())
            {
                SearchFunc = (q, _) => Task.FromResult(new SearchResponse(q.Text, [new SearchResult(new string('a', 64), [@"C:\x\a.png"], 1, 0, [])], [], 1, 1, TimeSpan.Zero)),
            };
            string initial = panel.ResultGrid.EmptyMessage;

            panel.SearchBox.Text = "";
            panel.SearchButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(50);

            Assert.Equal(initial, panel.ResultGrid.EmptyMessage);   // 専用の「検索ボックスに言葉を入れる」は無い
            Assert.NotEqual("検索ボックスに言葉を入れる", panel.ResultGrid.EmptyMessage);
            Assert.Equal("フォルダを追加して、検索ボックスに言葉を入れてください", initial);
            Assert.Empty(panel.ResultGrid.Items);
            Assert.Equal("", panel.HeaderText.Text);
        });
    }

    // ---------------------------------------------------------------- 検索の結果の外から詳細画面を開く

    private static ImageDetail Detail(string hash, string path) =>
        new(hash, 1000, "png", 4, 2, new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero),
            [new ImagePathDetail(path, false, null, new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), 1000)],
            [new StageDetail(Stage.Embed, StageState.Failed, "m", "デコードできません (テスト)", 2, null), new StageDetail(Stage.Ocr, StageState.Pending, null, null, 0, null), new StageDetail(Stage.Caption, StageState.Pending, null, null, 0, null)],
            null, []);

    private static ThumbnailResult Pixels() => ThumbnailResult.Ok(new ThumbnailPixels(4, 2, new byte[4 * 2 * 4]), fromCache: false);

    private static (ResultsPanel Panel, List<string> Notices) NewResultsPanel(Func<string, CancellationToken, Task<ImageDetail?>> detail)
    {
        var notices = new List<string>();
        var panel = new ResultsPanel(new TestLog(), notices.Add, new FakeShell())
        {
            DetailFunc = detail,
            ImageFunc = (_, _) => Task.FromResult(Pixels()),
            DisplayZone = TimeZoneInfo.Utc,
        };
        SearchResult a = new(H('a'), [@"C:\no-such-dir\a.png"], 1, 0.01, [new PathHit("visual", "m", 1, 0.1, new VisualDetail(0.2, 0.2))]);
        panel.SetResults([a]);
        return (panel, notices);
    }

    [Fact]
    public async Task A_hash_outside_the_search_results_opens_a_standalone_detail_without_rank_or_neighbours()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            (ResultsPanel panel, List<string> notices) = NewResultsPanel((hash, _) => Task.FromResult<ImageDetail?>(Detail(hash, @"C:\no-such-dir\z.png")));

            await panel.OpenDetailByHashAsync(H('z'));

            Assert.True(panel.IsDetailOpen);
            Assert.Equal(H('z'), panel.DetailHash);
            DetailView view = panel.DetailViewForTests;
            Assert.Equal("1 / 1", view.PositionText);
            Assert.False(view.PreviousEnabled);
            Assert.False(view.NextEnabled);
            Assert.Equal("z.png", view.TitleText);
            Assert.Contains(DetailPresenter.StandaloneRankLine, view.SideText, StringComparison.Ordinal);
            Assert.DoesNotContain("統合:", view.SideText, StringComparison.Ordinal);                // 順位・類似度は、検索のときの情報
            Assert.Contains("特徴ベクトル検索: 失敗", view.SideText, StringComparison.Ordinal);               // 段階の状態は ImageDetail の値
            Assert.Contains("デコードできません (テスト)", view.SideText, StringComparison.Ordinal);
            Assert.Empty(notices);

            panel.NavigateDetail(+1);                                                                 // 前後は無い
            Assert.Equal(H('z'), panel.DetailHash);
            panel.CloseDetail();
            Assert.False(panel.IsDetailOpen);
        });
    }

    [Fact]
    public async Task A_hash_that_is_in_the_search_results_opens_the_normal_detail_with_its_rank()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            (ResultsPanel panel, _) = NewResultsPanel((hash, _) => Task.FromResult<ImageDetail?>(Detail(hash, @"C:\no-such-dir\a.png")));

            await panel.OpenDetailByHashAsync(H('a'));

            Assert.Equal("1 / 1", panel.DetailViewForTests.PositionText);
            Assert.Contains("統合: 1 位", panel.DetailViewForTests.SideText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_hash_without_a_record_is_not_opened_and_is_noted()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            (ResultsPanel panel, List<string> notices) = NewResultsPanel((_, _) => Task.FromResult<ImageDetail?>(null));

            await panel.OpenDetailByHashAsync(H('z'));

            Assert.False(panel.IsDetailOpen);
            Assert.Contains(notices, n => n.Contains("この画像の記録がありません", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task A_standalone_detail_stays_open_when_the_results_are_refreshed_after_a_scan_but_closes_on_a_new_search()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            (ResultsPanel panel, _) = NewResultsPanel((hash, _) => Task.FromResult<ImageDetail?>(Detail(hash, @"C:\no-such-dir\z.png")));
            await panel.OpenDetailByHashAsync(H('z'));
            SearchResult a = new(H('a'), [@"C:\no-such-dir\a.png"], 1, 0.01, []);

            panel.SetResults([a], keepBanner: true);                                                 // 走査のあとの差し替え

            Assert.True(panel.IsDetailOpen);                                                          // 検索の結果の並びとは関係が無いので、開いたまま

            panel.SetResults([a]);                                                                    // 新しい検索

            Assert.False(panel.IsDetailOpen);
        });
    }
}
