using System;
using System.Linq;
using System.Threading.Tasks;
using Ohanami.Core;
using Xunit;

namespace Ohanami.App.Tests;

/// <summary>
/// VLM のモデルのファイルが無いとき(Core が <see cref="IndexState.UnavailableJobs"/> で伝える)、画面は失敗ではなく「未取得」の状態として出す:
/// 左パネルの件数の内訳と補足の行、状態の行(失敗にしない)、「インデックスの状態」の窓の上の 1 行。
/// </summary>
public sealed class ModelNotFetchedTests
{
    private static readonly UnavailableJob VlmMissing = new(JobKind.Vlm, ["Qwen3-VL-8B-Instruct-Q4_K_M.gguf", "mmproj-F16.gguf"]);

    private static IndexState State(IndexRunState run, params UnavailableJob[] unavailable) =>
        new(run, null,
            [new StageSummary(Stage.Embed, 0, 5, 0, 0), new StageSummary(Stage.Ocr, 5, 0, 0, 0), new StageSummary(Stage.Caption, 5, 0, 0, 0)],
            EngineStatus.NotCreated, null, null, null, null, null, 0)
        { UnavailableJobs = unavailable };

    [Fact]
    public void The_left_panel_says_the_model_is_not_fetched_in_the_count_row_and_the_note_row_and_keeps_the_state_row()
    {
        IndexRows rows = IndexStatusText.BuildRows(State(IndexRunState.Waiting, VlmMissing));

        Assert.Equal("テキスト検索: 0 / 5 枚", rows.Stage2Count);
        Assert.Equal("モデル未取得、未作成 5", rows.Stage2Extra);
        Assert.Equal("特徴ベクトル検索: 5 / 5 枚", rows.Stage1Count);
        Assert.Equal("", rows.Stage1Extra);
        Assert.Equal(ProcessingWords.StateWaiting, rows.State);   // 状態の行は変えない(失敗ではない)
        Assert.Equal("テキスト検索のモデルが未取得です", rows.Note);   // 左パネルの補足の行は、1 行に収まるよう、モデルの名前を添えない
        Assert.Contains("足りないファイル: Qwen3-VL-8B-Instruct-Q4_K_M.gguf、mmproj-F16.gguf", rows.NoteFull);
        Assert.Contains("「インデックス作成を開始」", rows.NoteFull);
    }

    [Fact]
    public void Nothing_is_added_when_the_files_are_there()
    {
        IndexRows rows = IndexStatusText.BuildRows(State(IndexRunState.Waiting));

        Assert.Equal("未作成 5", rows.Stage2Extra);
        Assert.Equal("", rows.Note);
        Assert.DoesNotContain("未取得", IndexStatusText.Build(State(IndexRunState.Waiting)));
    }

    [Fact]
    public void A_real_failure_comes_before_the_not_fetched_note()
    {
        IndexState s = State(IndexRunState.Failed, VlmMissing) with { LastError = "別の理由" };

        IndexRows rows = IndexStatusText.BuildRows(s);

        Assert.Equal("別の理由", rows.Note);                                                      // 短い行は、頭の「止まった原因: 」を外して、理由から始める
        Assert.StartsWith(ProcessingWords.StopReasonPrefix + "別の理由", rows.NoteFull, StringComparison.Ordinal);   // ヒントの全文は、頭つき
        Assert.Equal(2, rows.NoteFull.Split('\n').Length);
    }

    [Fact]
    public void The_full_status_text_names_the_missing_files()
    {
        string text = IndexStatusText.Build(State(IndexRunState.Idle, VlmMissing));

        Assert.Contains("足りないファイル: Qwen3-VL-8B-Instruct-Q4_K_M.gguf、mmproj-F16.gguf", text);
    }

    [Fact]
    public void The_snapshot_carries_the_missing_files_and_does_not_count_them_as_failures()
    {
        ProcessingSnapshot snap = ProcessingSnapshot.Build(State(IndexRunState.Waiting, VlmMissing), 0, open: true);

        Assert.Equal(["Qwen3-VL-8B-Instruct-Q4_K_M.gguf", "mmproj-F16.gguf"], snap.VlmMissingFiles);
        Assert.Equal(0, snap.VlmFailed);
        Assert.Equal("", ProcessingWords.LeftSummary(snap));
        Assert.Empty(ProcessingSnapshot.Build(State(IndexRunState.Waiting), 0, open: true).VlmMissingFiles);
        Assert.Empty(ProcessingSnapshot.Closed.VlmMissingFiles);
    }

    [Fact]
    public async Task The_status_window_says_it_above_the_list_and_hides_it_once_the_files_are_there()
    {
        await StaDispatcher.RunAsync(() =>
        {
            FailureListWindow w = new(ProblemKind.EmbedFailures, (_, _) => Task.FromResult(new ProblemList("", [], [], false)), new FakeShell(),
                ProcessingSnapshot.Build(State(IndexRunState.Waiting, VlmMissing), 0, open: true), _ => Task.CompletedTask);

            Assert.Equal("テキスト検索のモデル (Qwen3-VL-8B-Instruct) が未取得です。そろえたあとの「インデックス作成を開始」で、作り始めます", w.ModelNoteText);

            w.Apply(ProcessingSnapshot.Build(State(IndexRunState.Waiting), 0, open: true));
            Assert.Equal("", w.ModelNoteText);
            w.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void The_words_are_built_from_one_place_and_use_the_model_label_from_core()
    {
        Assert.Contains(OhanamiCore.ModelLabels.Vlm, ProcessingWords.ModelNotFetchedLine(JobKind.Vlm));
        Assert.Contains(OhanamiCore.ModelLabels.FeatureVector, ProcessingWords.ModelNotFetchedLine(JobKind.Embed));
        Assert.Equal("モデル未取得", ProcessingWords.ModelNotFetchedExtra);
    }
}
