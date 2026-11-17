using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>インデックスの状態の文章(IndexStatusText.Build / Progress): 埋め込みと VLM の進み具合、待ち・一時停止・失敗の言い方、進捗のバー。</summary>
public sealed class IndexStatusTextTests
{
    private static IndexState State(
        IndexRunState run = IndexRunState.Idle, JobKind? job = null, IReadOnlyList<StageSummary>? stages = null, EngineStatus? engine = null,
        TimeSpan? remaining = null, double? rate = null, string? path = null, string? error = null, int skipped = 0) =>
        new(run, job, stages ?? [], engine ?? EngineStatus.NotCreated, null, path, rate, remaining, error, skipped);

    private static StageSummary Stg(Stage stage, int pending = 0, int done = 0, int failed = 0, int outdated = 0) => new(stage, pending, done, failed, outdated);

    [Fact]
    public void Embed_and_vlm_lines_show_done_over_total_with_the_non_zero_extras()
    {
        string text = IndexStatusText.Build(State(stages: [Stg(Stage.Embed, done: 30), Stg(Stage.Ocr, pending: 17, done: 12, failed: 1), Stg(Stage.Caption, pending: 17, done: 12, failed: 1, outdated: 2)]));

        Assert.Contains("特徴ベクトル検索: 30 / 30 枚", text, StringComparison.Ordinal);
        Assert.Contains($"{TestWords.TextSearch}: 12 / 32 枚 (未作成 17、失敗 1、要再作成 2)", text, StringComparison.Ordinal);   // ocr と caption は同じ呼び出しで埋まるので、caption だけ
        Assert.DoesNotContain("特徴ベクトル検索: 30 / 30 枚(", text, StringComparison.Ordinal);                 // 0 の項目は出さない
    }

    [Fact]
    public void A_running_vlm_job_shows_its_name_speed_remaining_time_and_current_file()
    {
        string text = IndexStatusText.Build(State(IndexRunState.Running, JobKind.Vlm, [Stg(Stage.Caption, pending: 100, done: 5)], rate: 0.08, remaining: TimeSpan.FromHours(34.5), path: @"C:\shots\a b.png"));

        Assert.Contains($"実行中: {TestWords.TextSearch}のインデックス作成 (0.1 枚/秒、残り約 34 時間 30 分)", text, StringComparison.Ordinal);
        Assert.Contains("a b.png", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\shots", text, StringComparison.Ordinal);                              // ファイル名だけ
    }

    [Fact]
    public void A_waiting_job_says_it_continues_when_new_images_come_and_shows_no_running_line()
    {
        string text = IndexStatusText.Build(State(IndexRunState.Waiting, null, [Stg(Stage.Embed, done: 30), Stg(Stage.Caption, done: 30)]));

        Assert.Contains("待機中 (新しい画像が来たら続けます)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("実行中", text, StringComparison.Ordinal);
        Assert.DoesNotContain("一時停止", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(IndexRunState.Pausing, "一時停止の途中…")]
    [InlineData(IndexRunState.Paused, "一時停止中 (再開を押すまで作りません)")]
    [InlineData(IndexRunState.Failed, "停止中 (失敗で止まりました)")]
    public void The_pause_and_failure_states_are_shown_for_vlm_as_well(IndexRunState run, string expected)
    {
        Assert.Contains(expected, IndexStatusText.Build(State(run, JobKind.Vlm)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_preparation_failure_is_shown_once_not_as_both_the_preparation_line_and_the_last_error_line()
    {
        // 準備(検証・起動・GPU の自己検査)の失敗では、Engine の理由とジョブの失敗の理由(LastError)が同じ文になる
        const string reason = "モデルのファイルが無い: x.gguf";
        const string shown = "モデルのファイルがありません: x.gguf";   // 画面に出る形(UiText.Plain が語尾を「です・ます」調にする)
        var failed = new EngineStatus(EnginePhase.Failed, reason, null);

        foreach (JobKind job in new[] { JobKind.Vlm, JobKind.Embed })
        {
            string text = IndexStatusText.Build(State(IndexRunState.Failed, job, engine: failed, error: reason));

            Assert.Equal(1, CountOf(text, shown));                                         // 同じ文は 1 行だけ
            Assert.Contains("を準備できませんでした: " + shown, text, StringComparison.Ordinal);
            Assert.DoesNotContain("止まった原因", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_abort_that_is_not_a_preparation_failure_shows_only_the_last_error_line()
    {
        // 3 枚続けて異常になった打ち切りは、準備の失敗ではない(Engine は Failed にならない)。「準備できなかった」とは書かず、理由は「止まった原因」の 1 行だけ。
        // 画面では llama-server を「VLM の処理プログラム」と言い換える(UiText.Plain。手がかりの名前は括弧で残す。元の文はログにも残る。ADR 0038)
        const string reason = "llama-server が 3 枚続けて異常になった。GPU が他で使われているか、ドライバの問題かもしれない";

        string text = IndexStatusText.Build(State(IndexRunState.Failed, JobKind.Vlm, engine: EngineStatus.NotCreated, error: reason));

        const string shown = "VLM の処理プログラム (llama-server) が 3 枚続けて異常になりました。GPU が他で使われているか、ドライバの問題かもしれません";
        Assert.DoesNotContain("準備できませんでした", text, StringComparison.Ordinal);
        Assert.DoesNotContain("llama-server が", text, StringComparison.Ordinal);           // 言い換え前の言い方のまま出ない
        Assert.Equal(1, CountOf(text, shown));
        Assert.Contains("止まった原因: " + shown, text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_last_error_is_still_shown_next_to_a_preparation_failure()
    {
        var failed = new EngineStatus(EnginePhase.Failed, "準備の理由", null);

        string text = IndexStatusText.Build(State(IndexRunState.Failed, JobKind.Vlm, engine: failed, error: "別の理由"));

        Assert.Contains($"{TestWords.TextSearch}のインデックスを準備できませんでした: 準備の理由", text, StringComparison.Ordinal);
        Assert.Contains("止まった原因: 別の理由", text, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string part)
    {
        int n = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void The_engine_status_is_worded_by_the_current_job()
    {
        var checking = new EngineStatus(EnginePhase.Checking, null, null);
        var failed = new EngineStatus(EnginePhase.Failed, "モデルのファイルが無い: x.gguf", null);

        Assert.Contains("特徴ベクトル検索のインデックス作成の準備中… (GPU の動作確認です)", IndexStatusText.Build(State(IndexRunState.Running, JobKind.Embed, engine: checking)), StringComparison.Ordinal);
        Assert.Contains($"{TestWords.TextSearch}のインデックス作成の準備中… (VLM のモデルの確認と起動です", IndexStatusText.Build(State(IndexRunState.Running, JobKind.Vlm, engine: checking)), StringComparison.Ordinal);
        Assert.Contains("特徴ベクトル検索のインデックスを準備できませんでした: モデルのファイルがありません: x.gguf", IndexStatusText.Build(State(IndexRunState.Failed, JobKind.Embed, engine: failed)), StringComparison.Ordinal);
        Assert.Contains($"{TestWords.TextSearch}のインデックスを準備できませんでした: モデルのファイルがありません: x.gguf", IndexStatusText.Build(State(IndexRunState.Failed, JobKind.Vlm, engine: failed)), StringComparison.Ordinal);
        Assert.Contains("インデックス作成を準備できませんでした: ", IndexStatusText.Build(State(IndexRunState.Failed, null, engine: failed)), StringComparison.Ordinal);   // どのジョブか分からないとき
        Assert.DoesNotContain("準備", IndexStatusText.Build(State(IndexRunState.Running, JobKind.Vlm, engine: new EngineStatus(EnginePhase.Ready, null, null))), StringComparison.Ordinal);
    }

    [Fact]
    public void Skipped_images_and_the_last_error_are_shown_and_an_empty_state_is_empty()
    {
        string text = IndexStatusText.Build(State(skipped: 3, error: "HTTP 500"));
        Assert.Contains("3 枚は読めなかったので飛ばしました", text, StringComparison.Ordinal);
        Assert.Contains("止まった原因: HTTP 500", text, StringComparison.Ordinal);
        Assert.Equal(ProcessingWords.StateIdle, IndexStatusText.Build(State()));      // 止まっているときの文だけが出る(件数の行は、段階が無いので無い)
    }

    [Fact]
    public void The_progress_bar_follows_the_current_job()
    {
        StageSummary[] stages = [Stg(Stage.Embed, done: 10), Stg(Stage.Ocr, pending: 15, done: 5), Stg(Stage.Caption, pending: 15, done: 5)];
        Assert.Equal(1.0, IndexStatusText.Progress(State(stages: stages)), 6);                                              // 動いていない: 埋め込み
        Assert.Equal(1.0, IndexStatusText.Progress(State(IndexRunState.Running, JobKind.Embed, stages)), 6);
        Assert.Equal(0.25, IndexStatusText.Progress(State(IndexRunState.Running, JobKind.Vlm, stages)), 6);               // VLM: 5 / 20
        Assert.Equal(0.0, IndexStatusText.Progress(State(IndexRunState.Running, JobKind.Vlm)), 6);                        // 段階が無い
        Assert.Equal(0.0, IndexStatusText.Progress(State(IndexRunState.Running, JobKind.Vlm, [Stg(Stage.Caption)])), 6);  // 件数 0
    }
}
