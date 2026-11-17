using System;
using System.Collections.Generic;
using System.Globalization;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// 左パネルが状態のバーの右に出す通知・ダイアログの文を、**1 か所の関数**から作る(ADR 0038。文を作る関数なので、
/// <c>WordRulesGuardTests</c> が出力を調べられる)。<see cref="IndexPanel"/> は、ここの関数を呼ぶだけで、文を持たない。
/// 規則: 失敗は「〜できませんでした: 理由」(ADR 0038)。説明の文は「です・ます」、括弧の前に半角の空白。「再開」は、インデックス作成と取り込みの 2 つに付くので、**目的語つき**。段階の名前は <see cref="ProcessingWords"/>。
/// 通知はすべて、ログにも「画面: …」として残る。
/// </summary>
internal static class NoticeWords
{
    // ---- 走査
    /// <summary>走査の結果の文章(「新規 n、変更 n、消えた n」+ 0 でないもの)。「消えた」は、そのパスのファイルが見つからなくなったこと。</summary>
    public static string DescribeScan(ScanSummary s)
    {
        var parts = new List<string> { $"新規 {s.Added}", $"変更 {s.Changed}", $"消えた {s.Missing}" };
        if (s.Unreadable > 0) parts.Add($"読めない {s.Unreadable}");
        if (s.Unsupported > 0) parts.Add($"未対応の形式 {s.Unsupported}");
        if (s.Unreachable > 0) parts.Add($"到達できない場所 {s.Unreachable}");
        if (s.SkippedLinks > 0) parts.Add($"辿らなかったリンク {s.SkippedLinks}");
        return string.Join("、", parts);
    }

    public static string ScanFinished(ScanSummary s) =>
        string.Create(CultureInfo.InvariantCulture, $"スキャンが終わりました ({s.Elapsed.TotalSeconds:0.0} 秒): ") + DescribeScan(s);

    /// <summary>自動の走査が何をきっかけに起きたか(通知の括弧の中)。</summary>
    public static string TriggerLabel(ScanTrigger trigger) => trigger switch
    {
        ScanTrigger.Watcher => "フォルダの変化",
        ScanTrigger.Periodic => "定期の確認",
        ScanTrigger.Resume => "スリープからの復帰",
        ScanTrigger.Retry => "読めなかったファイルの再試行",
        _ => "自動",
    };

    public static string AutoScanFinished(ScanTrigger trigger, ScanSummary s) =>
        string.Create(CultureInfo.InvariantCulture, $"自動のスキャンが終わりました ({TriggerLabel(trigger)}、{s.Elapsed.TotalSeconds:0.0} 秒): ") + DescribeScan(s);

    public static string ScanFailed(string reason) => "スキャンできませんでした: " + reason;

    // ---- フォルダ
    public static string RemoveFolderFailed(string reason) => "フォルダを外せませんでした: " + reason;

    /// <summary>取り込みを止められなかった / 再開できなかった(目的語つき。「再開」はインデックス作成にも付くため)。</summary>
    public static string ToggleImportFailed(bool stopping, string reason) =>
        (stopping ? "取り込みを止められませんでした: " : "取り込みを再開できませんでした: ") + reason;

    // ---- インデックス作成(開始・一時停止・再開)
    public const string StartWhat = "インデックス作成を開始";
    public static string StartFailed(string reason) => "インデックス作成を開始できませんでした: " + reason;
    public static string ResumeFailed(string reason) => "インデックス作成を再開できませんでした: " + reason;
    public static string PauseFailed(string reason) => "一時停止できませんでした: " + reason;

    // ---- 失敗のやり直し・もう一度試す
    public const string NoFailures = "失敗した画像はありません";
    public static string RetryResetFailed(string reason) => "失敗した画像を未作成に戻せませんでした: " + reason;
    public static string RetryRestarted(int embed, int vlm) =>
        $"失敗した画像を{ProcessingWords.WordPending}に戻しました ({ProcessingWords.StageName(JobKind.Embed)} {embed} 枚、{ProcessingWords.StageName(JobKind.Vlm)} {vlm} 枚)。続けて始めます";
    public static string RetryFailedStart(string reason) => "失敗した画像をやり直せませんでした: " + reason;
    public static string RetrySkippedFailed(string reason) => "飛ばした画像をもう一度試せませんでした: " + reason;

    // ---- 要再作成
    public const string NoOutdated = "要再作成の画像はありません";
    public static string RequeueResetFailed(string reason) => $"要再作成の画像を{ProcessingWords.WordPending}に戻せませんでした: " + reason;
    public static string RequeueReset(JobKind job, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"要再作成の画像 {count} 枚を{ProcessingWords.WordPending}に戻しました ({ProcessingText.JobLabel(job)})。続けて始めます");
    public static string RequeueStartFailed(JobKind job, string reason) => $"{ProcessingText.Subject(job)}の再作成を始められませんでした: " + reason;
    public static string RequeueWhat(JobKind job) => $"{ProcessingText.Subject(job)}の再作成を始める";

    // ---- フォルダに無い画像
    public static string MissingCountFailed(string reason) => "フォルダに無い画像を数えられませんでした: " + reason;
    public const string NoMissing = "フォルダに無い画像の記録はありません";
    public static string Purged(int removed) => string.Create(CultureInfo.InvariantCulture, $"フォルダに無い画像の記録を消しました ({removed} 枚)");
    public static string PurgeFailed(string reason) => "フォルダに無い画像の記録を消せませんでした: " + reason;

    // ---- ダイアログ
    public const string AddFolderDialogTitle = "画像のフォルダを選ぶ";
    public const string AddFolderFailedTitle = "フォルダを追加できませんでした";
    public const string RemoveFolderTitle = "フォルダを外す";

    /// <summary>
    /// フォルダを外す確認。**何が消えて何が残るか**を言う: 画像ファイルは消えない。この中の画像は検索に出なくなる。インデックスと手動タグの記録は残り(インデックスの状態の「フォルダに無い画像」に数えられる)、
    /// このフォルダを追加し直すと、作り直さずに元に戻る(外すと、そのフォルダのパスの記録だけが消える。FolderCatalog.RemoveAsync)。
    /// </summary>
    public static string RemoveFolderConfirm(string path) =>
        $"「{path}」を一覧から外します。\n画像ファイルは消えません。この中の画像は検索に出なくなります。\nインデックスと手動タグの記録は残り ({ProcessingWords.WindowTitle}の「{ProcessingWords.KindLabel(ProblemKind.MissingImages)}」に数えられます)、このフォルダを追加し直すと、作り直さずに元に戻ります。\n外しますか。";
}
