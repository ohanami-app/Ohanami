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
/// 処理状態の窓(ADR 0039): 5 種類のラジオの数、種類ごとの操作の行(文字・押せるか・押せない理由を隣に 1 行)、操作の呼び出しと二度押し、
/// 数が変わったときの自動の読み直し(選択・状態の行を残す)、元のファイルが無い行の理由、フォルダに無い画像(本物の Core)、確認の持ち主、開いたときの最初の種類。
/// 窓は表示しない(作って、部品の状態を読むだけ)。実際のクリップボード・ダイアログには届かない(ShellBoundaryGuardTests が見張る)。
/// </summary>
public sealed class ProcessingWindowTests
{
    private static string H(char c) => new(c, 64);

    private static IndexState State(
        IndexRunState run = IndexRunState.Idle, int embedFailed = 0, int vlmFailed = 0, int skipped = 0, int embedOutdated = 0, int vlmOutdated = 0) =>
        new(run, null,
            [new StageSummary(Stage.Embed, 0, 5, embedFailed, embedOutdated), new StageSummary(Stage.Ocr, 0, 5, vlmFailed, vlmOutdated), new StageSummary(Stage.Caption, 0, 5, vlmFailed, vlmOutdated)],
            EngineStatus.NotCreated, null, null, null, null, null, skipped);

    private static ProcessingSnapshot Snap(IndexState state, int? missing = 0, bool busy = false) => ProcessingSnapshot.Build(state, missing, open: true, busy);

    private static ProblemList Sample(ProblemKind kind) => new(
        kind + " の見出し",
        [new ProblemColumn("ファイル", 220), new ProblemColumn("理由", null)],
        [
            new ProblemRow(H('a'), @"C:\shots\a.png", @"C:\shots\a.png", ["a.png", "理由 A"], "理由 A"),
            new ProblemRow(H('b'), null, "(元のファイルが無い画像)", ["(元のファイルが無い画像)", "理由 B"], "理由 B"),
        ], false);

    private static FailureListWindow NewWindow(ProblemKind kind, ProcessingSnapshot snap, Func<ProcessingAction, Task>? perform = null, Func<ProblemKind, Task<ProblemList>>? load = null) =>
        new(kind, (k, _) => load is null ? Task.FromResult(Sample(k)) : load(k), new FakeShell(), snap, perform ?? (_ => Task.CompletedTask));

    // ---------------------------------------------------------------- ラジオ(数を添える)

    [Fact]
    public async Task The_radios_carry_the_counts_and_the_outdated_one_splits_embedding_and_vlm()
    {
        await StaDispatcher.RunAsync(() =>
        {
            FailureListWindow w = NewWindow(ProblemKind.EmbedFailures, Snap(State(embedFailed: 3, skipped: 2, embedOutdated: 4, vlmOutdated: 5), missing: 7));

            Assert.Equal(["特徴ベクトル検索のインデックス作成に失敗 (3)", "テキスト検索のインデックス作成に失敗 (0)", "飛ばした画像 (2)", "要再作成 (特徴ベクトル検索 4・テキスト検索 5)", "フォルダに無い画像 (7)"], w.RadioTexts);
            Assert.Equal(ProcessingWords.WindowTitle, w.Title);
            w.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task New_counts_update_the_radio_texts()
    {
        await StaDispatcher.RunAsync(() =>
        {
            FailureListWindow w = NewWindow(ProblemKind.Skipped, Snap(State(skipped: 2)));

            w.Apply(Snap(State(skipped: 5, embedFailed: 1), missing: 9));

            Assert.Equal(["特徴ベクトル検索のインデックス作成に失敗 (1)", "テキスト検索のインデックス作成に失敗 (0)", "飛ばした画像 (5)", "要再作成 (特徴ベクトル検索 0・テキスト検索 0)", "フォルダに無い画像 (9)"], w.RadioTexts);
            w.Close();
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- 操作の行(数の隣。押せない理由は隣に 1 行)

    [Fact]
    public async Task Each_kind_shows_its_own_actions_with_the_label_the_state_and_the_reason_beside_it()
    {
        await StaDispatcher.RunAsync(() =>
        {
            ProcessingSnapshot idle = Snap(State(embedFailed: 3, skipped: 2, embedOutdated: 4, vlmOutdated: 300), missing: 7);

            FailureListWindow embed = NewWindow(ProblemKind.EmbedFailures, idle);
            Assert.Equal([(ProcessingAction.RetryEmbed, (string?)null, "この 3 枚をやり直す", true, "")], embed.ActionRows);

            FailureListWindow vlm = NewWindow(ProblemKind.VlmFailures, idle);                      // VLM の失敗は 0 枚: 押せない。理由は隣に
            Assert.Equal([(ProcessingAction.RetryVlm, (string?)null, "この 0 枚をやり直す", false, ProcessingWords.NothingToDo(ProcessingAction.RetryVlm))], vlm.ActionRows);

            FailureListWindow skipped = NewWindow(ProblemKind.Skipped, idle);
            Assert.Equal([(ProcessingAction.RetrySkipped, (string?)null, "もう一度試す", true, "")], skipped.ActionRows);

            FailureListWindow outdated = NewWindow(ProblemKind.Outdated, idle);                    // 段階ごとに 1 行(前置きの文 + ボタン)
            Assert.Equal(
            [
                (ProcessingAction.RequeueEmbed, (string?)"特徴ベクトル検索: 要再作成 4 枚 (見込み 1 秒未満)", "再作成を始める…", true, ""),
                (ProcessingAction.RequeueVlm, (string?)"テキスト検索: 要再作成 300 枚 (見込み 約 1 時間 0 分)", "再作成を始める…", true, ""),
            ], outdated.ActionRows);

            FailureListWindow missing = NewWindow(ProblemKind.MissingImages, idle);
            Assert.Equal([(ProcessingAction.PurgeMissing, (string?)null, "記録を消す…", true, "")], missing.ActionRows);
            foreach (FailureListWindow w in new[] { embed, vlm, skipped, outdated, missing }) w.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_running_job_or_a_busy_operation_disables_the_actions_and_says_why_in_the_window()
    {
        await StaDispatcher.RunAsync(() =>
        {
            FailureListWindow running = NewWindow(ProblemKind.EmbedFailures, Snap(State(IndexRunState.Running, embedFailed: 3)));
            Assert.Equal((false, ProcessingWords.RunBlockedReason(IndexRunState.Running, false)), (running.ActionRows[0].Enabled, running.ActionRows[0].Reason));

            FailureListWindow paused = NewWindow(ProblemKind.MissingImages, Snap(State(IndexRunState.Paused), missing: 2));     // 一時停止中でも、記録は消せる
            Assert.True(paused.ActionRows[0].Enabled);

            FailureListWindow busy = NewWindow(ProblemKind.Skipped, Snap(State(skipped: 2), busy: true));
            Assert.Equal((false, ProcessingWords.BusyReason), (busy.ActionRows[0].Enabled, busy.ActionRows[0].Reason));

            // 状態が替わると(再開した、など)、同じ窓の行が更新される
            running.Apply(Snap(State(IndexRunState.Waiting, embedFailed: 3)));
            Assert.True(running.ActionRows[0].Enabled);
            Assert.Equal("", running.ActionRows[0].Reason);
            foreach (FailureListWindow w in new[] { running, paused, busy }) w.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Switching_the_kind_shows_the_actions_of_that_kind()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            FailureListWindow w = NewWindow(ProblemKind.EmbedFailures, Snap(State(embedFailed: 1, embedOutdated: 2, vlmOutdated: 3), missing: 4),
                load: k => Task.FromResult(k == ProblemKind.Outdated ? ProcessingText.OutdatedList(new RequeuePlan(2, 3)) : Sample(k)));
            await w.ReloadAsync();

            w.SwitchTo(ProblemKind.Outdated);
            await w.ReloadAsync();
            Assert.Equal([ProcessingAction.RequeueEmbed, ProcessingAction.RequeueVlm], w.ActionRows.Select(r => r.Action));
            Assert.Empty(w.CurrentList!.Rows);

            w.SwitchTo(ProblemKind.MissingImages);
            Assert.Equal([ProcessingAction.PurgeMissing], w.ActionRows.Select(r => r.Action));
            w.Close();
        });
    }

    [Fact]
    public async Task One_radio_is_always_chosen_when_the_window_opens_for_every_kind_and_after_switching()
    {
        // 何も選ばれない状態・2 つ選ばれた状態は、作りの上で無い(開くとき・表示の確かめ・切り替えのあとのどれでも、ちょうど 1 つ)
        await StaDispatcher.RunAsync(async () =>
        {
            foreach (ProblemKind first in Enum.GetValues<ProblemKind>())
            {
                FailureListWindow w = NewWindow(first, Snap(State(), missing: 0));         // 数は全部 0(「空」に見えやすい形)
                Assert.Equal(1, w.CheckedRadioCount);
                Assert.Equal(first, w.Kind);
                w.EnsureKindCheckedForTests();
                Assert.Equal(1, w.CheckedRadioCount);
                foreach (ProblemKind other in Enum.GetValues<ProblemKind>())
                {
                    w.SwitchTo(other);
                    await w.ReloadAsync();
                    Assert.Equal(1, w.CheckedRadioCount);
                    Assert.Equal(other, w.Kind);
                }
                w.Close();
            }
        });
    }

    // ---------------------------------------------------------------- 操作の呼び出しと二度押し

    [Fact]
    public async Task An_enabled_action_calls_perform_a_disabled_one_does_nothing()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var calls = new List<ProcessingAction>();
            FailureListWindow w = NewWindow(ProblemKind.EmbedFailures, Snap(State(embedFailed: 3)), a => { calls.Add(a); return Task.CompletedTask; });

            await w.PerformAsync(ProcessingAction.RetryEmbed);
            await w.PerformAsync(ProcessingAction.RetryVlm);                           // VLM の失敗は 0 枚: 押せない
            await w.PerformAsync(ProcessingAction.PurgeMissing);                       // フォルダに無い画像は 0 枚

            Assert.Equal([ProcessingAction.RetryEmbed], calls);
            w.Close();
        });
    }

    [Fact]
    public async Task A_second_press_while_the_first_operation_is_in_progress_is_ignored()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            var calls = new List<ProcessingAction>();
            FailureListWindow? w = null;
            ProcessingSnapshot idle = Snap(State(embedFailed: 3));
            w = NewWindow(ProblemKind.EmbedFailures, idle, a =>
            {
                calls.Add(a);
                w!.Apply(idle with { Busy = true });          // IndexPanel は、最初の await の前に「操作の最中」の状態を届ける
                return Task.CompletedTask;
            });

            await w.PerformAsync(ProcessingAction.RetryEmbed);
            await w.PerformAsync(ProcessingAction.RetryEmbed);                         // 二度押し

            Assert.Single(calls);
            Assert.Equal(ProcessingWords.BusyReason, w.ActionRows[0].Reason);
            w.Apply(idle);                                                              // 開始の呼び出しが済んだあと、また押せる
            Assert.True(w.ActionRows[0].Enabled);
            w.Close();
        });
    }

    [Fact]
    public async Task A_failing_operation_is_shown_in_the_status_line_and_does_not_crash()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            FailureListWindow w = NewWindow(ProblemKind.Skipped, Snap(State(skipped: 1)), _ => throw new InvalidOperationException("できない(テスト)"));

            await w.PerformAsync(ProcessingAction.RetrySkipped);

            Assert.Equal("操作できませんでした: できません (テスト)", w.StatusText);
            w.Close();
        });
    }

    // ---------------------------------------------------------------- 自動の読み直し

    [Fact]
    public async Task A_changed_count_of_the_shown_kind_reloads_and_keeps_the_selection_and_the_status_line_but_other_changes_do_not()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            int loads = 0;
            FailureListWindow w = NewWindow(ProblemKind.EmbedFailures, Snap(State(embedFailed: 3, skipped: 1)), load: k => { Interlocked.Increment(ref loads); return Task.FromResult(Sample(k)); });
            Assert.True(await Wait.UntilAsync(() => w.CurrentList is not null));
            int afterOpen = Volatile.Read(ref loads);
            w.SelectRow(0);
            w.FileExists = _ => false;
            w.Reveal();                                                                 // 状態の行に何か出す(自動の読み直しで消えないこと)
            string status = w.StatusText;
            Assert.NotEqual("", status);

            w.Apply(Snap(State(embedFailed: 3, skipped: 1)));                          // 同じ数: 読み直さない
            w.Apply(Snap(State(embedFailed: 3, skipped: 4), missing: 2));              // ほかの種類の数が変わった: 読み直さない
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal(afterOpen, Volatile.Read(ref loads));

            w.Apply(Snap(State(embedFailed: 2, skipped: 4), missing: 2));              // 表示中の種類の数が変わった: 読み直す
            Assert.True(await Wait.UntilAsync(() => Volatile.Read(ref loads) == afterOpen + 1));
            await Task.Delay(50, TestContext.Current.CancellationToken);

            Assert.Equal(H('a'), w.SelectedRow?.Hash);                                  // 選択は残る
            Assert.Equal(status, w.StatusText);                                         // 状態の行は消えない
            w.Close();
        });
    }

    [Fact]
    public async Task The_status_line_says_why_reveal_is_unavailable_for_a_row_without_a_file()
    {
        await StaDispatcher.RunAsync(async () =>
        {
            FailureListWindow w = NewWindow(ProblemKind.MissingImages, Snap(State(), missing: 2));
            await w.ReloadAsync();

            w.SelectRow(1);                                                             // 現存するパスが無い行
            Assert.False(w.CanReveal);
            Assert.True(w.CanOpenDetail);                                               // 詳細は開ける(消える結果を、消す前に見られる)
            Assert.Equal(ProcessingWords.RevealUnavailable, w.StatusText);

            w.SelectRow(0);
            Assert.True(w.CanReveal);
            Assert.Equal("", w.StatusText);
            w.Close();
        });
    }

    // ---------------------------------------------------------------- IndexPanel とのつなぎ(本物の Core)

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

    [Fact]
    public async Task Opening_without_a_kind_starts_with_the_first_kind_that_has_a_count()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false, new FakeShell());
            panel.ShowFailureWindow = _ => { };
            panel.Attach(core);
            panel.ShowForTests(State(vlmFailed: 2, skipped: 1));

            FailureListWindow w = panel.OpenFailureList()!;

            Assert.Equal(ProblemKind.VlmFailures, w.Kind);                              // 埋め込みの失敗は 0、VLM の失敗が最初
            w.Close();
        });
    }

    [Fact]
    public async Task Opening_with_nothing_to_show_starts_with_the_first_kind()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false, new FakeShell());
            panel.ShowFailureWindow = _ => { };
            panel.Attach(core);

            FailureListWindow w = panel.OpenFailureList()!;

            Assert.Equal(ProblemKind.EmbedFailures, w.Kind);
            w.Close();
        });
    }

    [Fact]
    public async Task The_confirmation_belongs_to_the_processing_window_while_it_is_open()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            var panel = new IndexPanel(new TestLog(), _ => { }, () => false, new FakeShell());
            panel.ShowFailureWindow = _ => { };
            panel.Attach(core);
            Assert.Null(panel.ConfirmOwner);                                            // 窓が無く、メインの画面も無い(テスト)

            FailureListWindow w = panel.OpenFailureList(ProblemKind.EmbedFailures)!;

            Assert.Same(w, panel.ConfirmOwner);                                         // 窓が開いている間は、窓が持ち主
            w.Close();
            Assert.Null(panel.ConfirmOwner);                                            // 閉じたら、メインの画面(テストでは無い)に戻る
        });
    }

    [Fact]
    public async Task Missing_images_are_listed_with_their_manual_tag_note_and_purging_from_the_window_empties_the_list_by_itself()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            FolderInfo folder = await core.Folders.AddAsync(MakeFolder(temp, "a", 2));
            await core.Indexer.ScanAsync([folder.Id]);
            await core.Folders.RemoveAsync(folder.Id);                                  // 解除: パスの行が 0 行 = フォルダに無い画像が 2 枚
            var notices = new List<string>();
            var panel = new IndexPanel(new TestLog(), notices.Add, () => false, new FakeShell());
            panel.ShowFailureWindow = _ => { };
            panel.Attach(core);
            Assert.True(await Wait.UntilAsync(() => panel.MissingCount == 2));

            FailureListWindow w = panel.OpenFailureList(ProblemKind.MissingImages)!;
            Assert.True(await Wait.UntilAsync(() => w.CurrentList is { Columns.Count: 4 }));
            Assert.Equal("フォルダに無い画像 2 枚 (うち手動タグあり 0 枚)", w.CurrentList!.Heading);
            Assert.Equal(2, w.CurrentList.Rows.Count);
            Assert.Equal(["ファイル", "最後に在ったフォルダ", "無くなった日時", "手動タグ"], w.CurrentList.Columns.Select(c => c.Header));
            Assert.Contains("フォルダに無い画像 (2)", w.RadioTexts, StringComparer.Ordinal);
            Assert.True(w.ActionRows[0].Enabled);

            panel.ConfirmFunc = (_, _) => true;
            await w.PerformAsync(ProcessingAction.PurgeMissing);                       // 窓の「記録を消す…」から

            Assert.Contains(notices, n => n == "フォルダに無い画像の記録を消しました (2 枚)");
            Assert.True(await Wait.UntilAsync(() => w.CurrentList is { Rows.Count: 0 }), "数が 0 になったので、窓が自動で読み直す");
            Assert.Equal("フォルダに無い画像 0 枚 (うち手動タグあり 0 枚)", w.CurrentList!.Heading);
            Assert.False(w.ActionRows[0].Enabled);
            Assert.Equal(ProcessingWords.NothingToDo(ProcessingAction.PurgeMissing), w.ActionRows[0].Reason);
            w.Close();
        });
    }

    [Fact]
    public async Task Pressing_an_operation_twice_in_a_row_runs_it_once()
    {
        using var temp = new TempDirectory();
        await StaDispatcher.RunAsync(async () =>
        {
            await using OhanamiCore core = await OpenCoreAsync(temp);
            FolderInfo folder = await core.Folders.AddAsync(MakeFolder(temp, "a", 2));
            await core.Indexer.ScanAsync([folder.Id]);
            await core.Folders.RemoveAsync(folder.Id);
            var notices = new List<string>();
            var panel = new IndexPanel(new TestLog(), notices.Add, () => false, new FakeShell());
            panel.Attach(core);
            Assert.True(await Wait.UntilAsync(() => panel.MissingCount == 2));
            int asked = 0;
            panel.ConfirmFunc = (_, _) => { asked++; return true; };

            Task first = panel.PurgeMissingAsync();
            Task second = panel.PurgeMissingAsync();                                    // 最初の操作の最中(確認・消すの間)の二度押し
            await Task.WhenAll(first, second);

            Assert.Equal(1, asked);
            Assert.Single(notices, n => n.StartsWith("フォルダに無い画像の記録を消しました", StringComparison.Ordinal));
            Assert.True(panel.CurrentSnapshot.Action(ProcessingAction.PurgeMissing).Reason is not null || !panel.CurrentSnapshot.Busy);
            Assert.False(panel.CurrentSnapshot.Busy);                                   // 終わったあと、操作の最中の印は消える
        });
    }
}
