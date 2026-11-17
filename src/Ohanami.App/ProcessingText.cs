using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>「再作成を始める」の計画(<see cref="ProcessingText.PlanRequeue"/>)。件数は、現行のモデルと違うモデルで済んでいる画像の数(<see cref="StageSummary.Outdated"/>)。</summary>
internal sealed record RequeuePlan(int EmbedOutdated, int VlmOutdated)
{
    /// <summary>そのジョブの、要再作成の件数。</summary>
    public int For(JobKind job) => job == JobKind.Embed ? EmbedOutdated : VlmOutdated;
}

/// <summary>一覧の列 1 つ(見出しと幅。WPF の型を含まない)。<paramref name="Width"/> は固定の幅(px)。null は残りの幅(星)。</summary>
internal sealed record ProblemColumn(string Header, double? Width);

/// <summary>
/// 一覧の 1 行(画面の表示用。WPF の型を含まない)。<paramref name="Hash"/> は詳細を開く鍵、<paramref name="Path"/> は現存する最初のパス(無ければ null=元のファイルが無い画像)、
/// <paramref name="PathText"/> はファイルの欄のツールチップ(パス。無ければ代わりの文)、<paramref name="Cells"/> は列ごとの文字(先頭がファイルの欄)、<paramref name="Tooltip"/> は行のツールチップ。
/// </summary>
internal sealed record ProblemRow(string Hash, string? Path, string PathText, IReadOnlyList<string> Cells, string? Tooltip);

/// <summary>一覧 1 つの内容(見出し・列・行)。<paramref name="Truncated"/> は、上限で切って、全部は出していないこと。</summary>
internal sealed record ProblemList(string Heading, IReadOnlyList<ProblemColumn> Columns, IReadOnlyList<ProblemRow> Rows, bool Truncated);

/// <summary>
/// 処理状態の欄の文章と計画(画面の部品を使わない純粋な関数。テストできる)。文言は ADR 0038 に沿う。
/// <list type="bullet">
/// <item>再作成を始める: 件数と見込みの時間を出して確認する。見込みは、VLM が 1 枚約 12 秒(<see cref="VlmSecondsPerImage"/>)、埋め込みが 1 枚約 0.1 秒(<see cref="EmbedSecondsPerImage"/>)で計算(推測の初期値)。</item>
/// <item>フォルダに無い画像の記録を消す: 確認の文言に「消すと、同じ画像が戻ったときにインデックスを作り直す」を入れる。</item>
/// <item>失敗の一覧: パス(元のファイルが無い画像は「(元のファイルが無い画像)」)・理由・日時・回数。新しい順。</item>
/// </list>
/// </summary>
internal static class ProcessingText
{
    /// <summary>VLM の 1 枚あたりの秒(見込みの計算用。通しで測った約 12 秒。ADR 0027)。</summary>
    public const double VlmSecondsPerImage = 12;

    /// <summary>埋め込みの 1 枚あたりの秒(見込みの計算用。推測の初期値)。</summary>
    public const double EmbedSecondsPerImage = 0.1;

    public const string MissingPathText = "(元のファイルが無い画像)";

    /// <summary>段階の短い名前。<see cref="ProcessingWords.StageName"/>(段階の名前を作る唯一の場所)。</summary>
    public static string JobLabel(JobKind job) => ProcessingWords.StageName(job);

    /// <summary>助詞(の・が)の前に置く名前(「VLM の失敗」。「埋め込みの失敗」)。<see cref="ProcessingWords.StageSubject"/>。</summary>
    public static string Subject(JobKind job) => ProcessingWords.StageSubject(job);

    /// <summary>
    /// 要再作成の件数。VLM は ocr と caption が別の段階の行だが、同じ呼び出しで埋まるので、多いほうを数える(<see cref="IndexStatusText.PlanRetry"/> と同じ)。
    /// </summary>
    public static RequeuePlan PlanRequeue(IndexState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        int Outdated(Stage stage) => s.Stages.FirstOrDefault(x => x.Stage == stage)?.Outdated ?? 0;
        return new RequeuePlan(Outdated(Stage.Embed), Math.Max(Outdated(Stage.Ocr), Outdated(Stage.Caption)));
    }

    public static TimeSpan Estimate(JobKind job, int count) =>
        TimeSpan.FromSeconds(count * (job == JobKind.Vlm ? VlmSecondsPerImage : EmbedSecondsPerImage));

    /// <summary>見込みの計算の根拠(「1 枚約 12 秒で計算」)。<see cref="VlmSecondsPerImage"/>・<see cref="EmbedSecondsPerImage"/> から作る(要再作成の確認と「VLM を始める」のツールチップが、同じ数を使う)。</summary>
    public static string PerImageBasis(JobKind job) =>
        string.Create(CultureInfo.InvariantCulture, $"1 枚約 {(job == JobKind.Vlm ? VlmSecondsPerImage : EmbedSecondsPerImage):0.#} 秒で計算しています");

    /// <summary>「VLM を始める」のツールチップに出す枚数の例(1 万枚)。</summary>
    public const int VlmTooltipSampleCount = 10_000;

    public static string RequeueConfirmTitle(JobKind job) => Subject(job) + "の再作成を始める";

    public static string RequeueConfirmText(JobKind job, int count)
    {
        TimeSpan estimate = Estimate(job, count);
        string basis = PerImageBasis(job);
        string time = estimate < TimeSpan.FromSeconds(1) ? "1 秒未満" : "約 " + IndexPanel.FormatDuration(estimate);
        // 段階ごとに言い分ける(ADR 0006): 特徴ベクトル検索は、作り直すまで、その画像が検索に出ない。全文検索は、作り直すまで、古い結果のまま検索に使われる
        string effect = job == JobKind.Embed
            ? $"作り直すまで、この画像は{Subject(job)}に出ません。"
            : $"作り直すまで、古い結果のまま{Subject(job)}に使われます。";
        return string.Create(CultureInfo.InvariantCulture,
            $"{Subject(job)}のインデックスを、いまのモデルと違うモデルで作った画像 {count} 枚について、もう一度作ります。{effect}\n見込みの時間: {time} ({basis})。\n始めますか。");
    }

    public static string PurgeConfirmTitle => ProcessingWords.KindLabel(ProblemKind.MissingImages) + "の記録を消す";

    /// <summary>フォルダに無い画像の記録を消す確認。手動タグのある枚数を添える(消して戻らないのは、手動タグだけのため)。</summary>
    public static string PurgeConfirmText(int count, int withManualTags) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{ProcessingWords.KindLabel(ProblemKind.MissingImages)}の記録 {count} 枚 (うち手動タグあり {withManualTags} 枚) を消します (画像ファイルは消えません)。\n消すと、その画像のインデックス ({ProcessingWords.StageName(JobKind.Embed)}・{ProcessingWords.StageName(JobKind.Vlm)}) と手動タグも消えます。同じ画像が戻ったときは、インデックスを作り直します。\n消した記録は元に戻せません。消しますか。");

    private static readonly ProblemColumn[] FailureColumns =
        [new(ProcessingWords.ColumnFile, 220), new(ProcessingWords.ColumnReason, null), new(ProcessingWords.ColumnWhen, 140), new(ProcessingWords.ColumnAttempts, 60)];

    private static readonly ProblemColumn[] SkippedColumns = [new(ProcessingWords.ColumnFile, 220), new(ProcessingWords.ColumnReason, null)];

    private static readonly ProblemColumn[] MissingColumns =
        [new(ProcessingWords.ColumnFile, 220), new(ProcessingWords.ColumnLastFolder, null), new(ProcessingWords.ColumnMissingSince, 140), new(ProcessingWords.ColumnManualTags, 100)];

    /// <summary>失敗の一覧の内容(新しい順は Core が決めている)。<paramref name="zone"/> は日時を出すタイムゾーン(画面は <see cref="TimeZoneInfo.Local"/>)。</summary>
    public static ProblemList FailureRows(JobKind job, FailureList list, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(zone);
        var rows = list.Items.Select(i =>
        {
            string reason = i.Error is { Length: > 0 } e ? UiText.Plain(e) : ProcessingWords.NoReasonRecorded;
            return new ProblemRow(i.Hash, i.Path, i.Path ?? MissingPathText,
                [FileCell(i.Path), reason, When(i.UpdatedAt, zone), string.Create(CultureInfo.InvariantCulture, $"{i.Attempts} 回")], reason);
        }).ToArray();
        return new ProblemList(ProcessingWords.FailureHeading(job, list.Total, rows.Length), FailureColumns, rows, list.Total > rows.Length);
    }

    /// <summary>飛ばした画像(読めなくて、今回は処理しなかった。記録していない。次に始めると、もう一度試す)の一覧の内容。</summary>
    public static ProblemList SkippedRows(IReadOnlyList<SkippedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var rows = items.Select(i =>
        {
            string reason = JobLabel(i.Job) + ": " + UiText.Plain(i.Reason);
            return new ProblemRow(i.Hash, i.Path, i.Path ?? MissingPathText, [FileCell(i.Path), reason], reason);
        }).ToArray();
        return new ProblemList(ProcessingWords.SkippedHeading(rows.Length), SkippedColumns, rows, false);
    }

    /// <summary>
    /// フォルダに無い画像の一覧の内容(Core の <c>Indexer.ListMissingImagesAsync</c>。無くなった日時の新しい順)。
    /// 列: ファイル(最後に分かっているパスのファイル名)・最後に在ったフォルダ・無くなった日時・手動タグ(個数)。現存するパスは無いので、行の <see cref="ProblemRow.Path"/> は null
    /// (「エクスプローラーで表示」は押せない。「詳細」は開ける)。
    /// </summary>
    public static ProblemList MissingRows(MissingImageList list, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(zone);
        var rows = list.Items.Select(i => new ProblemRow(
            i.Hash, null, i.LastPath ?? ProcessingWords.NoPathRecorded,
            [i.LastPath is null ? ProcessingWords.NoPathRecorded : Path.GetFileName(i.LastPath),
             i.LastPath is null ? "" : Path.GetDirectoryName(i.LastPath) ?? "",
             i.MissingSince is { } at ? When(at, zone) : "",
             ProcessingWords.ManualTagCountText(i.ManualTagCount)],
            i.LastPath)).ToArray();
        return new ProblemList(ProcessingWords.MissingHeading(list.Total, list.WithManualTags, rows.Length), MissingColumns, rows, list.Total > rows.Length);
    }

    /// <summary>
    /// 要再作成の内容。**一覧は無い**(数はあるが、モデルを替えると全部になるので、一覧は写しになるだけ)。見出しに埋め込みと VLM の枚数を出し、段階ごとの 1 行
    /// (<see cref="ProcessingWords.OutdatedLine"/>)は窓が <paramref name="plan"/> から作る。行・列は空。
    /// </summary>
    public static ProblemList OutdatedList(RequeuePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new ProblemList(ProcessingWords.OutdatedHeading(plan.EmbedOutdated, plan.VlmOutdated), [], [], false);
    }

    /// <summary>ファイルの欄の文字(現存するパスのファイル名。無ければ「元のファイルが無い画像」)。</summary>
    private static string FileCell(string? path) => path is null ? MissingPathText : Path.GetFileName(path);

    private static string When(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>行の「エクスプローラーで表示」の対象のパス(現存するパスが無ければ null=見つからない画像)。</summary>
    public static string? RevealPathOf(ProblemRow row) => row.Path;

}
