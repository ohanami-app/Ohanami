using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// モデルのセットアップの画面(<see cref="SetupWindow"/>)の語を、1 か所に集めたもの。語の決まりは <see cref="ProcessingWords"/> と同じ
/// (段階の名前は <see cref="ProcessingWords.StageName"/>、モデルの名前は <see cref="ProcessingWords.ModelName"/>)。
/// </summary>
internal static class SetupWords
{
    public const string WindowTitle = "モデルのセットアップ";

    // ---- 見出しと導入の文(状態ごと)
    public const string HeadingMissing = "モデルのファイルをそろえてください";

    public static string LeadMissing =>
        "Ohanami は、画像を探せるようにするために、2 つのモデルのファイルを使います。\n「" + DownloadLabel + "」を押すか、下の置き場に表と同じ名前・同じ大きさのファイルを置いてから「" + RecheckLabel + "」を押してください";

    public static string HeadingVlmMissing => $"{ProcessingWords.StageName(JobKind.Vlm)}のモデルが、まだありません";

    public static string LeadVlmMissing =>
        $"{ProcessingWords.StageName(JobKind.Embed)}のモデルはそろっています。{ProcessingWords.StageName(JobKind.Vlm)}のモデルをそろえるか、このまま始めてください。"
        + $"このまま始めると、モデルがそろうまで、{ProcessingWords.StageName(JobKind.Vlm)}のインデックスは作れません (それまで、起動のたびにこの画面が出ます)";

    // ---- モデルの区画
    /// <summary>区画の見出し(「特徴ベクトル検索のモデル (…)」)。</summary>
    public static string SectionTitle(JobKind job) => $"{ProcessingWords.StageName(job)}のモデル ({ProcessingWords.ModelName(job)})";

    /// <summary>区画の説明。何で探せるようになるかと、いま要るか。</summary>
    public static string SectionPurpose(JobKind job) => job == JobKind.Embed
        ? "画像の見た目と検索語の近さで探します。使い始めるのに必要です"
        : "画面の文字・説明文・タグで探します。あとでそろえてもかまいません";

    public const string ColumnFile = "ファイル";
    public const string ColumnSize = "大きさ";
    public const string ColumnState = "状態";

    public const string FolderLabel = "置き場";
    public const string SourceLabel = "入手先";

    public const string StatePresent = "あり";
    public const string StateMissing = "未取得";

    /// <summary>ダウンロード中に、いま取っているファイルの行の状態(進み具合の行の「取得中」と同じ語)。</summary>
    public const string StateDownloading = "取得中";

    /// <summary>ダウンロード中に、取り終わって中身を確かめているファイルの行の状態(進み具合の行の「確かめ中」と同じ語)。</summary>
    public const string StateVerifying = "確かめ中";

    /// <summary>モデル 1 組のファイルが、そろって使える(区画の状態。緑で出す。状態のバーの「検索準備完了」は、検索の両方が答えられるときの別の語)。</summary>
    public const string SetComplete = "準備完了";

    /// <summary>ファイル 1 つの状態。大きさが違うときは、実際の大きさを添える(途中までのファイルか、別のファイルか、の手がかり)。</summary>
    public static string FileState(ModelFileStatus file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.State switch
        {
            ModelFileState.Present => StatePresent,
            ModelFileState.Missing => StateMissing,
            ModelFileState.SizeMismatch => $"大きさが違う (実際 {FormatSize(file.ActualSize ?? 0)})",
            _ => throw new ArgumentOutOfRangeException(nameof(file), file.State, "知らない状態"),
        };
    }

    /// <summary>
    /// ダウンロード中の、いま取っているファイル 1 つの行の状態。取っている間(接続中・取得中)は「取得中」、中身を確かめている間は「確かめ中」。
    /// 取り終わって置き場に置いたファイルは、この語ではなく、ふつうの「あり」になる(<see cref="FileState"/>)。
    /// </summary>
    public static string FileStateWhileDownloading(ModelDownloadPhase phase) => phase switch
    {
        ModelDownloadPhase.Connecting or ModelDownloadPhase.Downloading => StateDownloading,
        ModelDownloadPhase.Verifying => StateVerifying,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "取っている最中の段階ではない"),
    };

    /// <summary>モデル 1 組の状態(区画の見出しの右)。</summary>
    public static string SetState(ModelSetStatus set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.IsComplete) return SetComplete;
        int missing = set.Files.Count(f => f.State == ModelFileState.Missing);
        int mismatch = set.Files.Count(f => f.State == ModelFileState.SizeMismatch);
        var parts = new List<string>();
        if (missing > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{StateMissing} {missing} ファイル"));
        if (mismatch > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"大きさが違う {mismatch} ファイル"));
        return string.Join("、", parts);
    }

    /// <summary>区画の見出しの下の、ファイルの数と合計の大きさ。</summary>
    public static string SetSummary(ModelSetStatus set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return string.Create(CultureInfo.InvariantCulture, $"{set.Files.Count} ファイル、合計 {FormatSize(set.Files.Sum(f => f.ExpectedSize))}");
    }

    /// <summary>大きさ(10 進。Hugging Face の表示に合わせる)。1 GB 以上は小数 2 桁、1 MB 以上は小数 1 桁、1 KB 以上は整数。</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.00} GB"),
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e6:0.0} MB"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e3:0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} バイト"),
    };

    /// <summary>大きさの列のヒント(正確なバイト数)。</summary>
    public static string ExactSize(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes:N0} バイト");

    // ---- ボタン
    public const string OpenFolderLabel = "フォルダを開く";
    public const string OpenFolderHint = "エクスプローラーで置き場を開きます。フォルダが無ければ、作ってから開きます";
    public const string RecheckLabel = "確かめ直す";
    public const string RecheckHint = "ファイルの有無と大きさを確かめます。中身の検証は、使い始めるときに行います";
    public const string ContinueLabel = "このまま始める";
    public const string ExitLabel = "終了";

    public static string ContinueHint =>
        $"{ProcessingWords.StageName(JobKind.Embed)}だけで始めます。{ProcessingWords.StageName(JobKind.Vlm)}のモデルをそろえたあとの「{ProcessingWords.StartLabel}」で、{ProcessingWords.StageName(JobKind.Vlm)}のインデックスを作り始めます";

    // ---- 確かめ直したあとの 1 行
    public static string StillIncomplete(int count) => string.Create(CultureInfo.InvariantCulture, $"確かめ直しました。まだ {count} 個のファイルがそろっていません");

    public static string OpenFolderFailed(string reason) => "フォルダを開けませんでした: " + reason;

    // ---- ダウンロード
    public const string DownloadLabel = "ダウンロード";

    /// <summary>「ダウンロード」のボタンの文字。前回の続き(.part)があれば「続きから」と言い、取る量(残り)を添える。</summary>
    public static string DownloadButton(ModelDownloadPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string label = plan.Files.Any(f => f.PartialBytes > 0) ? "続きからダウンロード" : DownloadLabel;
        return $"{label} ({FormatSize(plan.RemainingBytes)})";
    }

    /// <summary>
    /// 「2 つともダウンロード」のボタンの文字(特徴ベクトル検索のモデルと、テキスト検索のモデルを、続けて取る)。続き・残りの言い方は <see cref="DownloadButton"/> と同じ。
    /// </summary>
    public static string DownloadBothButton(ModelDownloadPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string label = plan.Files.Any(f => f.PartialBytes > 0) ? "2 つとも続きからダウンロード" : "2 つともダウンロード";
        return $"{label} ({FormatSize(plan.RemainingBytes)})";
    }

    /// <summary>
    /// 一括の領域のボタンの文字。片方がそろっていて、残りの組だけを取るとき(「2 つとも」とは言えない)。続き・残りの言い方は <see cref="DownloadButton"/> と同じ。
    /// </summary>
    public static string DownloadRemainingButton(ModelDownloadPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string label = plan.Files.Any(f => f.PartialBytes > 0) ? "残りを続きからダウンロード" : "残りをダウンロード";
        return $"{label} ({FormatSize(plan.RemainingBytes)})";
    }

    /// <summary>一括の領域の文の頭。片方がそろっていることを言う(<paramref name="readyJob"/> は、そろっているほうのモデル)。</summary>
    public static string BulkReadyNote(JobKind readyJob) => $"{ProcessingWords.StageName(readyJob)}のモデルは、そろっています。";

    /// <summary>「2 つともダウンロード」の領域に添える 1 行(取る順)。</summary>
    public static string BothOrder => $"{ProcessingWords.StageName(JobKind.Embed)}のモデルから先に取ります";

    public const string DownloadButtonHint = "取得元から、足りないファイルを取って、置き場に置きます。取ったあとに、中身を確かめます";

    /// <summary>押す前に分かること(何を・どこから・どこへ・空き)。</summary>
    public static string PlanLine(ModelDownloadPlan plan, long? freeBytes, string root) => PlanLine(plan, [(root, freeBytes)]);

    /// <summary>
    /// 一括の領域で、片方がそろっていて残りだけを取るときの、押す前に分かること(<see cref="BulkReadyNote"/> + 見積もりの文。ファイル数を「残りの N ファイル」と言う)。
    /// </summary>
    public static string RemainingPlanLine(JobKind readyJob, ModelDownloadPlan plan, IReadOnlyList<(string Root, long? Free)> drives) =>
        BulkReadyNote(readyJob) + PlanLine(plan, drives, remainder: true);

    /// <summary>
    /// 押す前に分かること。<paramref name="drives"/> は、置き場のドライブと、その空き(分からなければ null)。置き場が 1 つのドライブなら「置き場のドライブ (C:\) の空きは …」、
    /// 2 つのドライブにまたがるとき(2 つのモデルの置き場が別のドライブ)は、ドライブごとに空きを言う。
    /// </summary>
    public static string PlanLine(ModelDownloadPlan plan, IReadOnlyList<(string Root, long? Free)> drives, bool remainder = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(drives);
        string head = string.Create(CultureInfo.InvariantCulture, $"押すと、Hugging Face から{(remainder ? "残りの " : " ")}{plan.Files.Count} ファイル (合計 {FormatSize(plan.TotalBytes)}) を取って、置き場に置きます");
        long partial = plan.TotalBytes - plan.RemainingBytes;
        string resume = partial > 0 ? $"。前回の続きがあるので、取るのは残りの {FormatSize(plan.RemainingBytes)} です" : "";
        (string Root, long? Free)[] known = [.. drives.Where(d => d.Free is not null)];
        string free = known.Length switch
        {
            0 => "",
            1 => $"。置き場のドライブ ({known[0].Root}) の空きは {FormatSize(known[0].Free!.Value)} です",
            _ => "。置き場のドライブの空きは、" + string.Join("、", known.Select(d => $"{d.Root} が {FormatSize(d.Free!.Value)}")) + " です",
        };
        return head + resume + free;
    }

    /// <summary>空きが足りないとき、押せない理由(押す前に、1 行で言う)。</summary>
    public static string NoSpaceReason(ModelDownloadPlan plan, long freeBytes, string root)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return NoSpaceReason(plan.RequiredFreeBytes, freeBytes, root);
    }

    /// <summary>空きが足りないドライブ 1 つについて、押せない理由(<paramref name="requiredBytes"/> は、そのドライブに置くぶんの残りと余裕)。</summary>
    public static string NoSpaceReason(long requiredBytes, long freeBytes, string root) =>
        $"空きが足りません: 要るのは {FormatSize(requiredBytes)}、置き場のドライブ ({root}) の空きは {FormatSize(freeBytes)} です。空きを作ってから、「{RecheckLabel}」を押してください";

    /// <summary>取っている間、ほかのダウンロードのボタンが押せない理由(1 つの組を取っている間も、2 つともを取っている間も)。</summary>
    public const string BusyReason = "ダウンロードしている間は、押せません";

    public const string Starting = "ダウンロードを始めています…";
    public const string CancelLabel = "中止";
    public const string CancelHint = "取るのをやめます。途中までのファイルは残り、次に「ダウンロード」を押すと、そこから再開します";
    public const string Cancelling = "中止しています…";
    public const string Cancelled = "中止しました。途中までのファイルは残してあります。次に「ダウンロード」を押すと、そこから再開します";

    /// <summary>進み具合の 1 行目(段階・ファイル名・何ファイル目か)。</summary>
    public static string ProgressLine(ModelDownloadProgress p)
    {
        ArgumentNullException.ThrowIfNull(p);
        string number = string.Create(CultureInfo.InvariantCulture, $"{p.FileIndex + 1} / {p.FileCount} ファイル");
        return p.Phase switch
        {
            ModelDownloadPhase.Connecting => $"接続中… {p.FileName} ({number})",
            ModelDownloadPhase.Downloading => $"取得中: {p.FileName} ({number})",
            ModelDownloadPhase.Verifying => $"確かめ中: {p.FileName} ({number}。取ったファイルの中身を確かめています)",
            ModelDownloadPhase.Completed => $"置き場に置きました: {p.FileName} ({number})",
            _ => throw new ArgumentOutOfRangeException(nameof(p), p.Phase, "知らない段階"),
        };
    }

    /// <summary>進み具合の 2 行目(済み / 総量・割合・速さ・残りの時間)。速さが測れない間は書かない。</summary>
    public static string AmountLine(ModelDownloadProgress p, double? bytesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(p);
        double percent = p.BytesTotal > 0 ? 100.0 * p.BytesDone / p.BytesTotal : 0;
        string line = string.Create(CultureInfo.InvariantCulture, $"{FormatSize(p.BytesDone)} / {FormatSize(p.BytesTotal)} ({percent:0}%)");
        if (p.Phase == ModelDownloadPhase.Downloading && bytesPerSecond is { } rate)
        {
            line += $"、{FormatSize((long)rate)}/秒";
            long left = p.BytesTotal - p.BytesDone;
            if (left > 0 && rate > 0) line += "、残り約 " + IndexPanel.FormatDuration(TimeSpan.FromSeconds(left / rate));
        }
        return line;
    }

    // ---- 失敗(平らな文。状態コード・接続先・ファイル名・空きを添える。手で置く方法へ案内する)
    public const string FailureHead = "ダウンロードできませんでした";

    /// <summary>一括の領域の、失敗のあとの案内(置き場は、各区画に書いてある)。</summary>
    public const string BulkManualGuidance = "手で置くときは、各区画の置き場に置いて「" + RecheckLabel + "」を押してください";

    public const string ManualGuidance = "手で置くこともできます。置き場に、表と同じ名前・同じ大きさのファイルを置いて、「" + RecheckLabel + "」を押してください";

    public static string FailureSummary(ModelDownloadException e)
    {
        ArgumentNullException.ThrowIfNull(e);
        string file = string.IsNullOrEmpty(e.FileName) ? "ファイル" : e.FileName;
        string host = string.IsNullOrEmpty(e.Host) ? "" : $" (接続先: {e.Host})";
        return e.Kind switch
        {
            ModelDownloadFailure.HttpStatus when e.StatusCode is 408 or 429 or >= 500 =>
                string.Create(CultureInfo.InvariantCulture, $"{file}: サーバーが状態コード {e.StatusCode} を返しました{host}。しばらくしてから、もう一度試してください"),
            ModelDownloadFailure.HttpStatus =>
                string.Create(CultureInfo.InvariantCulture, $"{file}: サーバーが状態コード {e.StatusCode} を返しました{host}。取得元で、このファイルを取れない状態です"),
            ModelDownloadFailure.UnexpectedResponse => $"{file}: サーバーの応答が想定と違いました{host}",
            ModelDownloadFailure.SizeMismatch => $"{file}: 取ったファイルの大きさが、定義と違いました",
            ModelDownloadFailure.HashMismatch => $"{file}: 取ったファイルの中身が、定義と違いました (SHA-256 が一致しません)。取ったファイルは消しました",
            ModelDownloadFailure.Stalled => $"{file}: データが届かなくなりました{host}。回線を確かめて、もう一度試してください",
            ModelDownloadFailure.Network => $"{file}: サーバーに接続できませんでした{host}。回線やファイアウォールを確かめて、もう一度試してください",
            ModelDownloadFailure.InsufficientSpace => e.NeededBytes is { } needed && e.AvailableBytes is { } available
                ? $"置き場の空きが足りません (要るのは {FormatSize(needed)}、空きは {FormatSize(available)})。空きを作ってから、もう一度試してください"
                : "置き場の空きが足りません。空きを作ってから、もう一度試してください",
            ModelDownloadFailure.Io => $"{file}: 置き場に書けませんでした。置き場の権限と空きを確かめてください",
            _ => throw new ArgumentOutOfRangeException(nameof(e), e.Kind, "知らない失敗の種類"),
        };
    }

    /// <summary>失敗の詳細(Core のメッセージを、画面の語に直したもの)。</summary>
    public static string FailureDetail(ModelDownloadException e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return "詳細: " + UiText.Plain(e.Message);
    }

    // ---- 窓を閉じるとき
    public const string CloseConfirmTitle = "Ohanami";
    public const string CloseConfirmText = "ダウンロード中です。中止して終了しますか? (途中までのファイルは残り、次回そこから再開できます)";

    /// <summary>約束の文(AGENTS.md の冒頭。README・画面・ADR で、この文のまま)。ダウンロードのボタンの近くに出す。</summary>
    public const string Promise =
        "画像・検索語・インデックスは、PC の外に出ません。ネットワークに出るのは、モデルのファイルを取るときだけです。「ダウンロード」を押すと、Hugging Face にファイルを要求します (相手には、IP アドレスと、取ったファイルが伝わります)。モデルを手で置けば、一度も接続しません。";
}
