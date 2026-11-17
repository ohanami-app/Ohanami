using System;
using System.Collections.Generic;

namespace Ohanami.Core;

/// <summary>診断ログの出し先(App が実装して書き出す。Core は書き出し先を持たない)。</summary>
public interface ICoreLog
{
    void Info(string message);

    void Warning(string message);

    void Error(string message, Exception? exception = null);
}

/// <summary>
/// ジョブ(埋め込みなど)の実行の状態。走査中かどうかは <see cref="IndexState.Scan"/> が null かどうかで表す(走査とジョブは並行してよいため)。
/// <c>Waiting</c> は、利用者が始めたジョブの待ちが無くなって、指定を覚えたまま待っている状態(資源は空けてある。走査で新しい待ちが入ると、同じ指定で自動で動き出す)。
/// 起動時は <c>Idle</c>、一時停止は <c>Paused</c>(自動では動かない)(ADR 0027)。
/// </summary>
public enum IndexRunState { Idle, Running, Pausing, Paused, Failed, Waiting }

/// <summary>画像エンコーダ(GPU)の状態。起動時には NotCreated。最初の埋め込みの直前に作って自己検査し、結果を出す。</summary>
public enum EnginePhase { NotCreated, Checking, Ready, Failed }

public sealed record EngineStatus(EnginePhase Phase, string? Message, SelfCheckReport? SelfCheck)
{
    public static EngineStatus NotCreated { get; } = new(EnginePhase.NotCreated, null, null);
}

/// <summary>
/// 段階ごとの件数。Pending = 現存するパスがあるのに stage_status に行が無い画像(行を先に作らない)。Done = 現行の model_id で完了。
/// Failed = 失敗の記録がある。Outdated = 完了しているが現行の model_id と違う(要再作成。ADR 0006)。
/// </summary>
public sealed record StageSummary(Stage Stage, int Pending, int Done, int Failed, int Outdated);

/// <summary>
/// 直近の実行で、読めなくて飛ばした画像 1 枚(<see cref="Indexer.ListSkippedAsync"/>。記録していない)。<paramref name="Job"/> はどのジョブが飛ばしたか、
/// <paramref name="Path"/> はいま現存するパスの先頭(無ければ null)、<paramref name="Reason"/> は飛ばした理由(例: 「…: 内容が走査のときと違う」「現存するパスが無い」)。
/// </summary>
public sealed record SkippedItem(JobKind Job, string Hash, string? Path, string Reason);

/// <summary>
/// Indexer の状態の不変のスナップショット。<c>SkippedImages</c> は、ファイルを読めなかったので直近の実行で飛ばした画像の数(ランナーごとの飛ばした集合の和。同じ画像は 1 枚)
/// (記録していない=<c>LastError</c> とは別。UI が「n 枚は読めなかったので次回」と言えるように。利用者が次に Start すると 0 に戻る)。
/// </summary>
public sealed record IndexState(
    IndexRunState Run,
    JobKind? CurrentJob,
    IReadOnlyList<StageSummary> Stages,
    EngineStatus Engine,
    ScanProgress? Scan,
    string? CurrentPath,
    double? ImagesPerSecond,
    TimeSpan? Remaining,
    string? LastError,
    int SkippedImages)
{
    /// <summary>モデルのファイルが無くて動かせないジョブ(失敗ではなく、未取得の状態)。無ければ空。ファイルがそろえば、次に状態を作るとき空になる。</summary>
    public IReadOnlyList<UnavailableJob> UnavailableJobs { get; init; } = [];
}

/// <summary>
/// モデルのファイルが無くて動かせないジョブ(<see cref="IndexState.UnavailableJobs"/>)。<paramref name="MissingFiles"/> は無いファイルの相対パス(モデルのディレクトリから。区切りは "/")。
/// </summary>
public sealed record UnavailableJob(JobKind Job, IReadOnlyList<string> MissingFiles);
