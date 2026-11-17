using System;
using System.Collections.Generic;
using System.Linq;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// <see cref="ProcessingSnapshot"/>(処理状態の窓と左パネルが見る、数と押せる・押せないの純粋な関数。ADR 0039)と、窓の一覧の形(列の見出しつき。
/// フォルダに無い画像の行・要再作成の行)。**状態の規則は、もとの <c>IndexPanel.UpdateButtons</c> と同じ**(下の「旧規則」と全部の組み合わせで突き合わせる)。
/// </summary>
public sealed class ProcessingSnapshotTests
{
    private static string H(char c) => new(c, 64);

    private static IndexState State(IndexRunState run, int embedFailed = 0, int vlmFailed = 0, int skipped = 0, int embedOutdated = 0, int vlmOutdated = 0) =>
        new(run, null,
            [new StageSummary(Stage.Embed, 0, 5, embedFailed, embedOutdated), new StageSummary(Stage.Ocr, 0, 5, vlmFailed, vlmOutdated), new StageSummary(Stage.Caption, 0, 5, vlmFailed, vlmOutdated)],
            EngineStatus.NotCreated, null, null, null, null, null, skipped);

    // ---------------------------------------------------------------- 数

    [Fact]
    public void The_counts_come_from_the_state_and_the_vlm_takes_the_larger_of_ocr_and_caption()
    {
        var state = new IndexState(IndexRunState.Idle, null,
            [new StageSummary(Stage.Embed, 0, 5, 2, 3), new StageSummary(Stage.Ocr, 0, 5, 1, 5), new StageSummary(Stage.Caption, 0, 5, 4, 7)],
            EngineStatus.NotCreated, null, null, null, null, null, 6);

        ProcessingSnapshot s = ProcessingSnapshot.Build(state, missing: 9, open: true);

        Assert.Equal((2, 4, 6, 3, 7, 9), (s.EmbedFailed, s.VlmFailed, s.Skipped, s.EmbedOutdated, s.VlmOutdated, s.Missing));
        Assert.Equal(10, s.Count(ProblemKind.Outdated));                           // 埋め込み + VLM
        Assert.Equal(9, s.Count(ProblemKind.MissingImages));
    }

    [Fact]
    public void Without_a_state_everything_is_zero_and_an_uncounted_missing_is_zero_in_the_count_but_not_counted_in_the_reason()
    {
        ProcessingSnapshot s = ProcessingSnapshot.Build(null, missing: null, open: true);

        Assert.All(Enum.GetValues<ProblemKind>(), k => Assert.Equal(0, s.Count(k)));
        Assert.Equal(ProcessingWords.MissingNotCounted, s.Action(ProcessingAction.PurgeMissing).Reason);
        Assert.Equal(ProblemKind.EmbedFailures, s.FirstKind());                      // 全部 0 なら最初の種類
    }

    [Fact]
    public void The_first_kind_is_the_first_with_a_count_in_the_order_of_the_window()
    {
        Assert.Equal(ProblemKind.Skipped, ProcessingSnapshot.Build(State(IndexRunState.Idle, skipped: 1, embedOutdated: 2), 3, true).FirstKind());
        Assert.Equal(ProblemKind.VlmFailures, ProcessingSnapshot.Build(State(IndexRunState.Idle, vlmFailed: 1, skipped: 1), null, true).FirstKind());
        Assert.Equal(ProblemKind.Outdated, ProcessingSnapshot.Build(State(IndexRunState.Idle, vlmOutdated: 1), 4, true).FirstKind());
        Assert.Equal(ProblemKind.MissingImages, ProcessingSnapshot.Build(State(IndexRunState.Idle), 4, true).FirstKind());
        Assert.Equal(new[] { ProblemKind.EmbedFailures, ProblemKind.VlmFailures, ProblemKind.Skipped, ProblemKind.Outdated, ProblemKind.MissingImages }, Enum.GetValues<ProblemKind>());
    }

    // ---------------------------------------------------------------- 状態の規則(旧規則と全部の組み合わせで突き合わせる)

    /// <summary>もとの <c>IndexPanel.UpdateButtons</c> の規則(書き写したもの=変えていない確かめ)。</summary>
    private static bool OldCanStart(bool open, IndexRunState run) => open && run is IndexRunState.Idle or IndexRunState.Failed or IndexRunState.Waiting;

    private static bool OldPurge(bool open, IndexRunState run, int? missing) => open && run is not (IndexRunState.Running or IndexRunState.Pausing) && missing is > 0;

    public static IEnumerable<object[]> RunStatesAndOpen() =>
        from run in Enum.GetValues<IndexRunState>() from open in new[] { true, false } select new object[] { run, open };

    [Theory]
    [MemberData(nameof(RunStatesAndOpen))]
    public void The_enabling_rules_are_the_old_update_buttons_rules_for_every_state(IndexRunState run, bool open)
    {
        foreach (int count in new[] { 0, 3 })
        {
            ProcessingSnapshot s = ProcessingSnapshot.Build(State(run, embedFailed: count, vlmFailed: count, skipped: count, embedOutdated: count, vlmOutdated: count), count, open);

            Assert.Equal(OldCanStart(open, run), s.CanStart);
            // やり直す・再作成を始める・もう一度試す: 始められる状態で、数が 1 以上
            foreach (ProcessingAction a in new[] { ProcessingAction.RetryEmbed, ProcessingAction.RetryVlm, ProcessingAction.RetrySkipped, ProcessingAction.RequeueEmbed, ProcessingAction.RequeueVlm })
                Assert.Equal(OldCanStart(open, run) && count > 0, s.Action(a).Enabled);
            // 消す: 実行中・一時停止の途中でなく、数が 1 以上
            Assert.Equal(OldPurge(open, run, count), s.Action(ProcessingAction.PurgeMissing).Enabled);
            // 一覧を見る: Core を開いていて、数が 1 以上(ジョブの状態によらない)
            foreach (ProblemKind k in Enum.GetValues<ProblemKind>())
                Assert.Equal(open && s.Count(k) > 0, s.CanView(k));
        }
    }

    // ---------------------------------------------------------------- 理由

    [Fact]
    public void A_disabled_action_says_why_in_one_line_and_an_enabled_one_says_nothing()
    {
        foreach (IndexRunState run in Enum.GetValues<IndexRunState>())
        foreach (bool open in new[] { true, false })
        foreach (bool busy in new[] { true, false })
        foreach (int count in new[] { 0, 2 })
        {
            ProcessingSnapshot s = ProcessingSnapshot.Build(State(run, embedFailed: count, vlmFailed: count, skipped: count, embedOutdated: count, vlmOutdated: count), count, open, busy);
            foreach (ProcessingAction a in Enum.GetValues<ProcessingAction>())
            {
                ActionState st = s.Action(a);
                if (st.Enabled) Assert.Null(st.Reason);
                else
                {
                    Assert.False(string.IsNullOrWhiteSpace(st.Reason), $"{run}/{open}/{busy}/{count}/{a}: 理由が無い");
                    Assert.DoesNotContain('\n', st.Reason!);
                }
            }
        }
    }

    [Fact]
    public void The_reason_follows_the_priority_not_open_then_busy_then_zero_then_the_run_state()
    {
        // 起動が済んでいない(最優先)
        Assert.Equal(ProcessingWords.NotOpenReason, ProcessingSnapshot.Build(State(IndexRunState.Running, embedFailed: 1), 1, open: false, busy: true).Action(ProcessingAction.RetryEmbed).Reason);
        // 操作の最中
        Assert.Equal(ProcessingWords.BusyReason, ProcessingSnapshot.Build(State(IndexRunState.Idle, embedFailed: 1), 1, open: true, busy: true).Action(ProcessingAction.RetryEmbed).Reason);
        // 数が 0(ジョブの状態より先)
        Assert.Equal(ProcessingWords.NothingToDo(ProcessingAction.RetryVlm), ProcessingSnapshot.Build(State(IndexRunState.Running), 0, true).Action(ProcessingAction.RetryVlm).Reason);
        // ジョブの状態
        Assert.Equal(ProcessingWords.RunBlockedReason(IndexRunState.Running, purge: false), ProcessingSnapshot.Build(State(IndexRunState.Running, embedOutdated: 2), 0, true).Action(ProcessingAction.RequeueEmbed).Reason);
        Assert.Equal(ProcessingWords.RunBlockedReason(IndexRunState.Paused, purge: false), ProcessingSnapshot.Build(State(IndexRunState.Paused, skipped: 2), 0, true).Action(ProcessingAction.RetrySkipped).Reason);
        Assert.Equal(ProcessingWords.RunBlockedReason(IndexRunState.Pausing, purge: true), ProcessingSnapshot.Build(State(IndexRunState.Pausing), 4, true).Action(ProcessingAction.PurgeMissing).Reason);
        Assert.Contains("再開", ProcessingWords.RunBlockedReason(IndexRunState.Paused, false), StringComparison.Ordinal);     // 一時停止中は、再開を案内する
    }

    // ---------------------------------------------------------------- 一覧の形(列の見出しつき)

    [Fact]
    public void The_failure_and_skipped_lists_carry_their_columns()
    {
        var at = new DateTimeOffset(2026, 11, 8, 3, 4, 5, TimeSpan.Zero);
        ProblemList failures = ProcessingText.FailureRows(JobKind.Embed, new FailureList(Stage.Embed, [new FailureItem(H('a'), @"C:\shots\a.png", "e", 2, at, "m")], 1), TimeZoneInfo.Utc);
        ProblemList skipped = ProcessingText.SkippedRows([new SkippedItem(JobKind.Vlm, H('b'), @"C:\x.png", "読めない")]);

        Assert.Equal(["ファイル", "理由", "日時", "回数"], failures.Columns.Select(c => c.Header));
        Assert.Equal(["a.png", "e", "2026-11-08 03:04", "2 回"], failures.Rows[0].Cells);
        Assert.Equal(["ファイル", "理由"], skipped.Columns.Select(c => c.Header));      // 飛ばした画像の日時・回数の列は無い
        Assert.Equal(["x.png", "テキスト検索: 読めません"], skipped.Rows[0].Cells);
        Assert.All(new[] { failures, skipped }, l => Assert.All(l.Rows, r => Assert.Equal(l.Columns.Count, r.Cells.Count)));
    }

    [Fact]
    public void The_missing_images_list_has_its_columns_the_last_folder_the_date_the_manual_tags_and_says_how_many_have_manual_tags()
    {
        var since = new DateTimeOffset(2026, 11, 6, 3, 0, 0, TimeSpan.Zero);
        var list = new MissingImageList(
        [
            new MissingImageItem(H('a'), @"C:\shots\sub\a.png", since, 2, true),
            new MissingImageItem(H('b'), null, null, 0, false),                       // パスの行が無い
            new MissingImageItem(H('c'), @"C:\shots\c.png", since.AddHours(-1), 1, false),
        ], 3, 2);

        ProblemList shown = ProcessingText.MissingRows(list, TimeZoneInfo.Utc);

        Assert.Equal(["ファイル", "最後に在ったフォルダ", "無くなった日時", "手動タグ"], shown.Columns.Select(c => c.Header));
        Assert.Equal("フォルダに無い画像 3 枚 (うち手動タグあり 2 枚)", shown.Heading);
        Assert.False(shown.Truncated);
        Assert.Equal(["a.png", @"C:\shots\sub", "2026-11-06 03:00", "2 個"], shown.Rows[0].Cells);
        Assert.Equal(["(パスの記録なし)", "", "", ""], shown.Rows[1].Cells);        // 日時・フォルダ・手動タグは空(0 のものは書かない)
        Assert.Equal(["c.png", @"C:\shots", "2026-11-06 02:00", "1 個"], shown.Rows[2].Cells);
        Assert.All(shown.Rows, r => Assert.Null(r.Path));                          // 現存するパスは無い(エクスプローラーで表示は押せない。詳細は開ける)
        Assert.Equal(H('b'), shown.Rows[1].Hash);
        Assert.Equal(@"C:\shots\sub\a.png", shown.Rows[0].PathText);
    }

    [Fact]
    public void A_missing_images_list_cut_by_the_limit_says_how_many_are_shown()
    {
        var list = new MissingImageList([new MissingImageItem(H('a'), @"C:\a.png", DateTimeOffset.UnixEpoch, 0, true)], 250, 7);

        ProblemList shown = ProcessingText.MissingRows(list, TimeZoneInfo.Utc);

        Assert.True(shown.Truncated);
        Assert.Equal("フォルダに無い画像 250 枚 (うち手動タグあり 7 枚)。無くなった日時の新しい順に 1 枚を表示", shown.Heading);
    }

    [Fact]
    public void The_outdated_content_has_no_list_only_a_heading_and_one_line_per_job_with_the_estimate()
    {
        ProblemList shown = ProcessingText.OutdatedList(new RequeuePlan(3, 300));

        Assert.Empty(shown.Rows);
        Assert.Empty(shown.Columns);
        Assert.Equal("要再作成 (モデルが替わって、作り直しが要る画像): 特徴ベクトル検索 3 枚、テキスト検索 300 枚", shown.Heading);
        Assert.Equal("特徴ベクトル検索: 要再作成 3 枚 (見込み 1 秒未満)", ProcessingWords.OutdatedLine(JobKind.Embed, 3));
        Assert.Equal("テキスト検索: 要再作成 300 枚 (見込み 約 1 時間 0 分)", ProcessingWords.OutdatedLine(JobKind.Vlm, 300));      // 300 枚 × 12 秒
    }

    [Fact]
    public void Every_kind_and_action_has_a_word()
    {
        Assert.All(Enum.GetValues<ProblemKind>(), k => Assert.False(string.IsNullOrWhiteSpace(ProcessingWords.KindLabel(k))));
        Assert.All(Enum.GetValues<ProcessingAction>(), a => Assert.False(string.IsNullOrWhiteSpace(ProcessingWords.NothingToDo(a))));
        Assert.Equal("この 3 枚をやり直す", ProcessingWords.RetryActionLabel(3));
        Assert.Equal("", ProcessingWords.ManualTagCountText(0));
    }
}
