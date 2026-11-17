using System;
using System.Collections.Generic;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>処理状態の窓で見る一覧の種類(5 つ)。窓の上のラジオの並びと同じ順。</summary>
internal enum ProblemKind { EmbedFailures, VlmFailures, Skipped, Outdated, MissingImages }

/// <summary>処理状態の窓の操作(やり直す・再作成を始める・もう一度試す・記録を消す)。</summary>
internal enum ProcessingAction { RetryEmbed, RetryVlm, RetrySkipped, RequeueEmbed, RequeueVlm, PurgeMissing }

/// <summary>操作の可否。押せないとき、<paramref name="Reason"/> は窓の中に 1 行で書く理由(ツールチップに頼らない)。押せるときは null。</summary>
internal sealed record ActionState(bool Enabled, string? Reason);

/// <summary>
/// 処理状態の窓と左パネルが見る、「いま何が数えられていて、何が押せるか」。WPF の型を含まない record。
/// <see cref="Build"/> が、Core の状態(<see cref="IndexState"/>)と数えた枚数から作る**純粋な関数**(もとは <c>IndexPanel.UpdateButtons</c> の中身。**状態の規則は変えていない**):
/// <list type="bullet">
/// <item>一覧を見る・詳細を開く: Core を開いていて、数が 1 以上なら、ジョブの状態によらずいつでも。</item>
/// <item>やり直す・再作成を始める・もう一度試す: 始められる状態(止まっている・失敗・待機中)で、数が 1 以上のとき。実行中・一時停止の途中・一時停止中は押せない(一時停止中は「再開」を使う)。</item>
/// <item>記録を消す: ジョブが実行中・一時停止の途中でなく、数が 1 以上のとき(Core も拒む)。待機中・一時停止中は可。</item>
/// <item>操作の最中(<see cref="Busy"/>)は、どの操作も押せない(二度押しで、開始のあとにすぐ別の操作が走らないように)。</item>
/// </list>
/// 押せない理由は 1 行の文(<see cref="ProcessingWords"/> から作る)。理由の優先順: 起動が済んでいない → 操作の最中 → 数が 0 → ジョブの状態。
/// </summary>
internal sealed record ProcessingSnapshot(
    IndexRunState Run, bool Open, bool Busy,
    int EmbedFailed, int VlmFailed, int Skipped, int EmbedOutdated, int VlmOutdated, int? Missing)
{
    /// <summary>VLM のモデルのファイルで、置き場に無いもの(空ならそろっている)。そろうまで、テキスト検索のインデックスは作れない(失敗ではない)。</summary>
    public IReadOnlyList<string> VlmMissingFiles { get; init; } = [];

    /// <summary>すべて 0・Core を開いていない状態。</summary>
    public static ProcessingSnapshot Closed { get; } = new(IndexRunState.Idle, false, false, 0, 0, 0, 0, 0, null);

    /// <param name="state">最後に表示した Core の状態(まだ無ければ null)。</param>
    /// <param name="missing">フォルダに無い画像の数(数えたあと。数えていなければ null)。</param>
    /// <param name="open">Core を開いている(<c>OhanamiCore</c> を持っている)。</param>
    /// <param name="busy">押した操作が、開始の呼び出しに届くまでの間。</param>
    public static ProcessingSnapshot Build(IndexState? state, int? missing, bool open, bool busy = false)
    {
        if (state is null) return new ProcessingSnapshot(IndexRunState.Idle, open, busy, 0, 0, 0, 0, 0, missing);
        RetryPlan failures = IndexStatusText.PlanRetry(state);
        RequeuePlan outdated = ProcessingText.PlanRequeue(state);
        return new ProcessingSnapshot(state.Run, open, busy, failures.EmbedFailed, failures.VlmFailed, state.SkippedImages, outdated.EmbedOutdated, outdated.VlmOutdated, missing)
        {
            VlmMissingFiles = IndexStatusText.UnavailableFiles(state, JobKind.Vlm),
        };
    }

    /// <summary>ジョブを始められる状態(止まっている・失敗・待機中)か。Core を開いていることも含む。待機中も、新しく始められる(Core の <c>StartAsync</c> は一時停止中・待機中を新しい指定で始める)。</summary>
    public bool CanStart => Open && Run is IndexRunState.Idle or IndexRunState.Failed or IndexRunState.Waiting;

    /// <summary>その種類の数。要再作成は、埋め込みと VLM の合計。</summary>
    public int Count(ProblemKind kind) => kind switch
    {
        ProblemKind.EmbedFailures => EmbedFailed,
        ProblemKind.VlmFailures => VlmFailed,
        ProblemKind.Skipped => Skipped,
        ProblemKind.Outdated => EmbedOutdated + VlmOutdated,
        ProblemKind.MissingImages => Missing ?? 0,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "知らない種類"),
    };

    /// <summary>その種類の一覧を見られるか(Core を開いていて、数が 1 以上。ジョブの状態によらない)。</summary>
    public bool CanView(ProblemKind kind) => Open && Count(kind) > 0;

    /// <summary>窓を開くときの、最初の種類: 数が 1 以上の最初の種類(並びの順)。全部 0 なら最初の種類(埋め込みの失敗)。</summary>
    public ProblemKind FirstKind()
    {
        foreach (ProblemKind kind in Enum.GetValues<ProblemKind>())
            if (Count(kind) > 0) return kind;
        return ProblemKind.EmbedFailures;
    }

    /// <summary>操作の可否と、押せない理由。</summary>
    public ActionState Action(ProcessingAction action)
    {
        int count = action switch
        {
            ProcessingAction.RetryEmbed => EmbedFailed,
            ProcessingAction.RetryVlm => VlmFailed,
            ProcessingAction.RetrySkipped => Skipped,
            ProcessingAction.RequeueEmbed => EmbedOutdated,
            ProcessingAction.RequeueVlm => VlmOutdated,
            ProcessingAction.PurgeMissing => Missing ?? 0,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "知らない操作"),
        };
        bool purge = action == ProcessingAction.PurgeMissing;

        if (!Open) return new ActionState(false, ProcessingWords.NotOpenReason);
        if (Busy) return new ActionState(false, ProcessingWords.BusyReason);
        if (count == 0) return new ActionState(false, purge && Missing is null ? ProcessingWords.MissingNotCounted : ProcessingWords.NothingToDo(action));

        bool runAllows = purge
            ? Run is not (IndexRunState.Running or IndexRunState.Pausing)
            : Run is IndexRunState.Idle or IndexRunState.Failed or IndexRunState.Waiting;
        return runAllows ? new ActionState(true, null) : new ActionState(false, ProcessingWords.RunBlockedReason(Run, purge));
    }
}
