using System;
using System.Collections.Generic;
using System.Globalization;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// 処理状態の窓(<see cref="FailureListWindow"/>)と <see cref="ProcessingSnapshot"/> が使う語を、**1 か所**に集めたもの(ADR 0038・0039)。
/// 語を替えるときは、ここだけを直す。呼び名(「埋め込み」「VLM」)は <see cref="ProcessingText.JobLabel"/>・<see cref="ProcessingText.Subject"/>。
/// </summary>
internal static class ProcessingWords
{
    // ---- 段階の名前(**段階の名前を作る唯一の場所**。画面のどこでも、ここから作る)
    /// <summary>
    /// 段階の名前(件数の行・失敗の一覧・通知)。**その段階で使えるようになる検索の名前で呼ぶ**(ADR 0038): 埋め込みの段階 → 特徴ベクトル検索、VLM の段階 → テキスト検索
    /// (「全文検索」は、もともと文章でできたものに使う語なので、OCR・説明文・タグ・手動タグを探すこの経路は「テキスト検索」。ADR 0038)。
    /// 正確な技術の名前は <see cref="StageHint"/>(ツールチップ)に添える。
    /// </summary>
    public static string StageName(JobKind job) => job == JobKind.Embed ? "特徴ベクトル検索" : "テキスト検索";

    /// <summary>段階の名前(「実行中: …」)。知らないジョブ(null)は「ジョブ」。</summary>
    public static string StageNameLong(JobKind? job) => job is { } j ? StageName(j) : "インデックス作成";

    /// <summary>助詞(の・が)の前に置く名前。英字で終わる名前は、助詞の前に空白を入れる(今の 2 つの名前は日本語なので、空白は要らない)。</summary>
    public static string StageSubject(JobKind job) => StageName(job);

    /// <summary>
    /// 段階のヒント(ツールチップ。正確な技術の名前を添える。ADR 0038)。モデルの名前は <see cref="ModelName"/>(Core の公開の表示名 <see cref="OhanamiCore.ModelLabels"/>)から作る
    /// (モデルを替えても嘘にならないように、ここに名前を書かない。ModelNamesGuardTests が、App のソースに名前の書き写しが無いことを見張る)。
    /// </summary>
    public static string StageHint(JobKind job) => job == JobKind.Embed
        ? $"{StageName(job)}: {ModelName(job)} で画像の特徴をベクトルにし (特徴ベクトル化)、言葉と見た目の近さで探します"
        : $"{StageName(job)}: VLM ({ModelName(job)}) で画面の文字の読み取り (OCR)・説明文・タグを作り、それと手動タグの文字で探します";

    /// <summary>段階で使うモデルの画面に出す名前(特徴ベクトル化のモデル・VLM)。Core が公開している表示名(定義のそば)から。</summary>
    public static string ModelName(JobKind job) => job == JobKind.Embed ? OhanamiCore.ModelLabels.FeatureVector : OhanamiCore.ModelLabels.Vlm;

    /// <summary>段階の名前にモデル名を添えた形(例: 特徴ベクトル検索 (SigLIP2))。経路の名前などで、モデル名を出すとき。</summary>
    public static string StageNameWithModel(JobKind job) => $"{StageName(job)} ({ModelName(job)})";

    // ---- 状態の語(未作成 / 失敗 / 要再作成。ADR 0038)。**状態の語を作る唯一の場所**
    /// <summary>まだインデックスが無い(その段階で、まだ作っていない)。</summary>
    public const string WordPending = "未作成";

    /// <summary>その画像の、その段階のインデックス作成が失敗した(記録あり)。</summary>
    public const string WordFailed = "失敗";

    /// <summary>いまのモデルと違うモデルで作ったインデックス(作り直しが要る)。作り直しは窓の「再作成を始める…」で始める。</summary>
    public const string WordOutdated = "要再作成";

    /// <summary>その段階のモデルのファイルが、まだ置き場に無い(どれか 1 つでも足りない)。画像の状態ではなく、モデルの状態の語。</summary>
    public const string WordNotFetched = "未取得";

    /// <summary>左パネルの件数の内訳に足す、モデルが無いことの 1 語(「モデル未取得、未作成 1030」)。</summary>
    public const string ModelNotFetchedExtra = "モデル" + WordNotFetched;

    /// <summary>左パネルの補足の行(幅 282 に収まるよう、モデルの名前は添えない。名前は <see cref="ModelNotFetchedLine"/> とヒントにある)。</summary>
    public static string ModelNotFetchedNote(JobKind job) => $"{StageName(job)}のモデルが{WordNotFetched}です";

    /// <summary>モデルのファイルが無くて、その段階のインデックスを作れない、の 1 行(モデルの名前つき。窓の上の行の元)。</summary>
    public static string ModelNotFetchedLine(JobKind job) => $"{StageName(job)}のモデル ({ModelName(job)}) が{WordNotFetched}です";

    /// <summary>補足の行のヒント(全文)。足りないファイルを添え、次の行動(そろえて、開始を押す)を言う。</summary>
    public static string ModelNotFetchedHint(JobKind job, IReadOnlyList<string> files) =>
        $"{StageName(job)}のインデックスを作るモデルのファイルが、まだありません (足りないファイル: {string.Join("、", files)})。そろえたあとに「{StartLabel}」を押すと、作り始めます";

    /// <summary>「インデックスの状態…」の窓の上に出す 1 行。</summary>
    public static string ModelNotFetchedWindowLine(JobKind job) => $"{ModelNotFetchedLine(job)}。そろえたあとの「{StartLabel}」で、作り始めます";

    // ---- 左パネル(ADR 0039)。語は ADR 0038 の付録(語と意味と使う場所の表)が正
    public const string FolderHeader = "フォルダ";
    public const string IndexHeader = "インデックス";
    public const string AddFolderLabel = "追加…";
    public const string RemoveFolderLabel = "外す…";

    /// <summary>
    /// フォルダの行の右クリック「今すぐ変化を確かめる」(そのフォルダを走査する。左パネルにボタンは無い。ADR 0039)。自動でも確かめている(撮ってから 3〜4 秒で拾う)ので、
    /// 手で押す意味は、変化を見張れないフォルダを、15 分待たずに確かめたいときくらい。
    /// </summary>
    public const string FolderCheckNow = "今すぐフォルダを再スキャン";

    /// <summary>「今すぐ変化を確かめる」のヒント(自動の確かめの一覧: 監視 3 秒後・15 分ごと・スリープからの復帰・起動時。Core: FolderWatcher.QuietPeriod・PeriodicInterval)。</summary>
    public const string FolderCheckNowHint = "このフォルダをスキャンし直して、画像の追加・変更・削除を確かめます。自動でもスキャンしています (フォルダの監視、15 分ごとの確認、スリープからの復帰、起動時)。変化を見張れないフォルダで、15 分待たずに確かめたいときに使います";

    /// <summary>取り込みを止めているフォルダでは、「今すぐ変化を確かめる」を押せない(Core は、指定されたフォルダを、止めていても走査してしまうため、App で押せなくする)。</summary>
    public const string FolderCheckNowStoppedReason = "取り込みを止めているフォルダはスキャンしません。先に「取り込みを再開する」を選んでください";
    public const string FolderStopImport = "取り込みを止める";
    public const string FolderResumeImport = "取り込みを再開する";
    public const string FolderStoppedMark = "(取り込み停止) ";

    /// <summary>取り込みを止める / 再開するのヒント(止めても、取り込み済みの画像は検索に出続け、インデックス作成も続く。有効かどうかを見るのは走査と監視だけ。ADR 0038)。</summary>
    public const string FolderImportHint = "止めても、取り込み済みの画像は検索に出続け、インデックス作成も続きます。新しい画像・変更・削除には気づかなくなります";

    /// <summary>開始・一時停止・再開の 1 つのボタンの文字(止まっている・失敗 → 開始、実行中・待機中 → 一時停止、一時停止中 → 再開)。</summary>
    public const string StartLabel = "インデックス作成を開始";
    public const string PauseLabel = "一時停止";
    public const string ResumeLabel = "再開";

    /// <summary>窓を開くボタン(詳細画面の 1 枚ごとの「処理状態」とは別の名前)。</summary>
    public const string ProcessingStateLabel = "インデックスの状態…";
    public static string ProcessingStateTooltip => $"失敗・飛ばした画像・{WordOutdated}・フォルダに無い画像を確認して、操作します (窓が開きます)";

    // ---- 状態の文(状態の名前 + 新しい画像が来たらどうなるか。名前は「停止中・待機中・一時停止の途中・一時停止中」にそろえ、説明は括弧で添えて「です・ます」調にする。ADR 0039)
    // 状態の行は、どの状態でも 1 行に収める(幅 282 で切れない)。説明は短くし、長い説明(新しい画像が来たときにどうなるか)は、行のヒント(<see cref="StateHint"/>)に
    public const string StateIdle = "停止中 (開始を押すまで作りません)";
    public const string StateWaiting = "待機中 (新しい画像が来たら続けます)";
    public const string StatePausing = "一時停止の途中…";
    public const string StatePaused = "一時停止中 (再開を押すまで作りません)";
    public const string StateFailed = "停止中 (失敗で止まりました)";

    /// <summary>
    /// 状態の行のヒント(全文。新しい画像が来たときにどうなるかまで言う。「です・ます」調)。実行中は行の文字がそのまま全文なので null。
    /// </summary>
    public static string? StateHint(IndexRunState run) => run switch
    {
        IndexRunState.Idle => "停止中です。新しい画像が来ても、開始を押すまでインデックスは作りません",
        IndexRunState.Waiting => "待機中です。新しい画像が来たら、続けてインデックスを作ります",
        IndexRunState.Pausing => "一時停止の途中です。止まるまで待ってください",
        IndexRunState.Paused => "一時停止中です。新しい画像が来ても、再開を押すまでインデックスは作りません",
        IndexRunState.Failed => "インデックス作成が失敗して止まりました。原因は、下の補足の行(ヒントに全文)で確認できます。開始を押すと、もう一度始めます",
        _ => null,
    };

    /// <summary>実行中の行(速さと残りの前まで)。**作る側の文**(ADR 0038): 「…のインデックスを作成」と言う(「特徴ベクトル検索を実行中」だと、検索が動いている話に読める)。知らないジョブは「インデックス作成」。</summary>
    public static string RunningLine(JobKind? job) => job is { } j ? $"実行中: {StageName(j)}のインデックス作成" : "実行中: インデックス作成";

    /// <summary>準備中の行(作る側の文。ADR 0038)。全文検索は VLM の処理プログラムの起動、特徴ベクトル検索は GPU の動作確認。</summary>
    public static string PreparingLine(JobKind? job) => job == JobKind.Vlm
        ? $"{StageName(JobKind.Vlm)}のインデックス作成の準備中… (VLM のモデルの確認と起動です。読み込みに数十秒かかります)"
        : $"{StageName(JobKind.Embed)}のインデックス作成の準備中… (GPU の動作確認です)";

    /// <summary>準備できなかった行の頭(理由が続く。作る側の文。ADR 0038)。知らないジョブは「インデックス作成」。</summary>
    public static string PrepareFailedPrefix(JobKind? job) => job is { } j
        ? $"{StageName(j)}のインデックスを準備できませんでした: "
        : "インデックス作成を準備できませんでした: ";

    public const string StopReasonPrefix = "止まった原因: ";

    public static string SkippedImagesLine(int count) => string.Create(CultureInfo.InvariantCulture, $"{count} 枚は読めなかったので飛ばしました (次回に再試行します)");

    /// <summary>
    /// 左パネルの「インデックスの状態…」の隣の 1 行。**0 のものは書かない**(全部 0 なら空=行ごと出さない)。失敗は特徴ベクトル検索と全文検索の合計、フォルダに無い画像は数えたあと。
    /// </summary>
    public static string LeftSummary(ProcessingSnapshot s)
    {
        var parts = new List<string>();
        int failed = s.EmbedFailed + s.VlmFailed;
        if (failed > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"失敗 {failed}"));
        if (s.Skipped > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"飛ばした {s.Skipped}"));
        int outdated = s.EmbedOutdated + s.VlmOutdated;
        if (outdated > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{WordOutdated} {outdated}"));
        if (s.Missing is > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"フォルダに無い {s.Missing}"));
        return string.Join("・", parts);
    }

    /// <summary>
    /// 開始のボタンのヒント(時間の見込みは、再作成の確認と同じ数 <see cref="ProcessingText.Estimate"/> から作る。「約」なので、時間は時間の単位に丸める)。
    /// 2 つの段階の名前とヒントを添える。
    /// </summary>
    public static string StartTooltip()
    {
        TimeSpan t = ProcessingText.Estimate(JobKind.Vlm, ProcessingText.VlmTooltipSampleCount);
        string time = t.TotalHours >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(t.TotalHours):0} 時間") : IndexPanel.FormatDuration(t);
        return string.Create(CultureInfo.InvariantCulture,
            $"画像を検索できるようにします。{StageName(JobKind.Embed)}を先に済ませてから、{StageName(JobKind.Vlm)}を作ります (GPU を長時間使います。{StageName(JobKind.Vlm)}は 1 万枚で約 {time}です。{ProcessingText.PerImageBasis(JobKind.Vlm)})。新しい画像が増えたら、{StageName(JobKind.Embed)}を先にやります。一時停止・再開は同じボタンです\n{StageHint(JobKind.Embed)}\n{StageHint(JobKind.Vlm)}");
    }

    // ---- 窓
    public const string WindowTitle = "インデックスの状態";

    /// <summary>種類のラジオの文字(数を添える。要再作成は特徴ベクトル検索と全文検索の内訳)。</summary>
    public static string KindRadioText(ProblemKind kind, ProcessingSnapshot s) => kind == ProblemKind.Outdated
        ? string.Create(CultureInfo.InvariantCulture, $"{KindLabel(kind)} ({StageName(JobKind.Embed)} {s.EmbedOutdated}・{StageName(JobKind.Vlm)} {s.VlmOutdated})")
        : string.Create(CultureInfo.InvariantCulture, $"{KindLabel(kind)} ({s.Count(kind)})");

    /// <summary>この種類の操作(窓の見出しの下に出す。要再作成は段階ごとに 2 つ)。</summary>
    public static IReadOnlyList<ProcessingAction> ActionsOf(ProblemKind kind) => kind switch
    {
        ProblemKind.EmbedFailures => [ProcessingAction.RetryEmbed],
        ProblemKind.VlmFailures => [ProcessingAction.RetryVlm],
        ProblemKind.Skipped => [ProcessingAction.RetrySkipped],
        ProblemKind.Outdated => [ProcessingAction.RequeueEmbed, ProcessingAction.RequeueVlm],
        ProblemKind.MissingImages => [ProcessingAction.PurgeMissing],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "知らない種類"),
    };

    /// <summary>操作の前に添える 1 行(要再作成は「特徴ベクトル検索: 要再作成 n 枚(見込み …)」。ほかは無し)。</summary>
    public static string? ActionLead(ProcessingAction action, ProcessingSnapshot s) => action switch
    {
        ProcessingAction.RequeueEmbed => OutdatedLine(JobKind.Embed, s.EmbedOutdated),
        ProcessingAction.RequeueVlm => OutdatedLine(JobKind.Vlm, s.VlmOutdated),
        _ => null,
    };

    /// <summary>操作のボタンの文字。</summary>
    public static string ActionLabel(ProcessingAction action, ProcessingSnapshot s) => action switch
    {
        ProcessingAction.RetryEmbed => RetryActionLabel(s.EmbedFailed),
        ProcessingAction.RetryVlm => RetryActionLabel(s.VlmFailed),
        ProcessingAction.RetrySkipped => RetrySkippedActionLabel,
        ProcessingAction.RequeueEmbed or ProcessingAction.RequeueVlm => RequeueActionLabel,
        ProcessingAction.PurgeMissing => PurgeActionLabel,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "知らない操作"),
    };

    /// <summary>元のファイルが無い画像(現存するパスが無い行)は、エクスプローラーで表示できない、の 1 行。</summary>
    public const string RevealUnavailable = "元のファイルが無い画像なので、エクスプローラーでは表示できません";

    // ---- 種類(ラジオの文字。数は呼び出し側が添える)
    /// <summary>失敗の種類の名前の後ろ。失敗したのは検索ではなく、インデックス作成(ADR 0038)。「{段階}のインデックス作成に失敗」。</summary>
    public const string FailureSuffix = "のインデックス作成に失敗";

    public static string KindLabel(ProblemKind kind) => kind switch
    {
        ProblemKind.EmbedFailures => StageSubject(JobKind.Embed) + FailureSuffix,
        ProblemKind.VlmFailures => StageSubject(JobKind.Vlm) + FailureSuffix,
        ProblemKind.Skipped => "飛ばした画像",
        ProblemKind.Outdated => WordOutdated,
        ProblemKind.MissingImages => "フォルダに無い画像",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "知らない種類"),
    };

    // ---- 列の見出し
    public const string ColumnFile = "ファイル";
    public const string ColumnReason = "理由";
    public const string ColumnWhen = "日時";
    public const string ColumnAttempts = "回数";
    public const string ColumnLastFolder = "最後に在ったフォルダ";
    public const string ColumnMissingSince = "無くなった日時";
    public const string ColumnManualTags = "手動タグ";

    // ---- 行の中の語
    public const string NoReasonRecorded = "(理由の記録なし)";
    public const string NoPathRecorded = "(パスの記録なし)";

    /// <summary>手動タグの数の書き方(0 のときは書かない=空)。</summary>
    public static string ManualTagCountText(int count) => count > 0 ? string.Create(CultureInfo.InvariantCulture, $"{count} 個") : "";

    // ---- 見出し
    public static string FailureHeading(JobKind job, int total, int shown) =>
        shown < total
            ? string.Create(CultureInfo.InvariantCulture, $"{ProcessingText.Subject(job)}{FailureSuffix} {total} 枚 (新しい順に {shown} 枚を表示)")
            : string.Create(CultureInfo.InvariantCulture, $"{ProcessingText.Subject(job)}{FailureSuffix} {total} 枚 (新しい順)");

    public static string SkippedHeading(int count) =>
        string.Create(CultureInfo.InvariantCulture, $"飛ばした画像 {count} 枚 (読めなかったので、今回はインデックスを作っていません。記録もしていません。次に始めたとき、もう一度試します)");

    /// <summary>フォルダに無い画像の見出し。「うち手動タグあり m 枚」を添える(消して戻らないのは手動タグだけのため)。上限で切ったときは表示した枚数も。</summary>
    public static string MissingHeading(int total, int withManualTags, int shown)
    {
        string head = string.Create(CultureInfo.InvariantCulture, $"フォルダに無い画像 {total} 枚 (うち手動タグあり {withManualTags} 枚)");
        return shown < total ? head + string.Create(CultureInfo.InvariantCulture, $"。無くなった日時の新しい順に {shown} 枚を表示") : head;
    }

    public static string OutdatedHeading(int embed, int vlm) =>
        string.Create(CultureInfo.InvariantCulture, $"{WordOutdated} (モデルが替わって、作り直しが要る画像): {StageName(JobKind.Embed)} {embed} 枚、{StageName(JobKind.Vlm)} {vlm} 枚");

    /// <summary>
    /// 要再作成のヒント(ツールチップ)。**段階ごとに書き分ける**(ADR 0006): 特徴ベクトル検索は、今のモデルの行だけを読むので、作り直すまで、その画像は検索に出ない。
    /// 全文検索は、画像の最新の結果から作るので、作り直すまで、古い結果のまま検索に使われる(作り直すと最新の結果に替わる)。
    /// </summary>
    public static string OutdatedHint(JobKind job) => job == JobKind.Embed
        ? $"いまのモデル ({ModelName(job)}) と違うモデルで作ったベクトルです。作り直すまで、その画像は{StageName(job)}に出ません"
        : $"いまのモデル ({ModelName(job)}) と違うモデルで作った文字・説明文・タグです。作り直すまで、古い結果のまま{StageName(job)}に使われます (作り直すと最新の結果に替わります)";

    /// <summary>要再作成の段階ごとの 1 行(一覧は無い)。見込みの時間つき。</summary>
    public static string OutdatedLine(JobKind job, int count)
    {
        TimeSpan estimate = ProcessingText.Estimate(job, count);
        string time = estimate < TimeSpan.FromSeconds(1) ? "1 秒未満" : "約 " + IndexPanel.FormatDuration(estimate);
        return string.Create(CultureInfo.InvariantCulture, $"{ProcessingText.JobLabel(job)}: {WordOutdated} {count} 枚 (見込み {time})");
    }

    // ---- 操作の文字
    public static string RetryActionLabel(int count) => string.Create(CultureInfo.InvariantCulture, $"この {count} 枚をやり直す");

    public const string RetrySkippedActionLabel = "もう一度試す";
    public const string RequeueActionLabel = "再作成を始める…";
    public const string PurgeActionLabel = "記録を消す…";

    // ---- 押せない理由(窓の中に 1 行で書く)
    public const string NotOpenReason = "起動が済むまで押せません";
    public const string BusyReason = "前の操作の最中です。次の状態が届くまで待ってください";
    public const string MissingNotCounted = "フォルダに無い画像を数えている最中です";

    public static string NothingToDo(ProcessingAction action) => action switch
    {
        ProcessingAction.RetryEmbed => $"{StageSubject(JobKind.Embed)}{FailureSuffix}した画像はありません",
        ProcessingAction.RetryVlm => $"{StageSubject(JobKind.Vlm)}{FailureSuffix}した画像はありません",
        ProcessingAction.RetrySkipped => "飛ばした画像はありません",
        ProcessingAction.RequeueEmbed => $"{StageSubject(JobKind.Embed)}の{WordOutdated}はありません",
        ProcessingAction.RequeueVlm => $"{StageSubject(JobKind.Vlm)}の{WordOutdated}はありません",
        ProcessingAction.PurgeMissing => "フォルダに無い画像はありません",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "知らない操作"),
    };

    /// <summary>ジョブの状態のせいで押せない理由。<paramref name="purge"/> は記録を消す操作(待機中・一時停止中でも消せる)。</summary>
    public static string RunBlockedReason(IndexRunState run, bool purge) => run switch
    {
        IndexRunState.Running => purge ? "インデックス作成が動いている間は消せません。一時停止してからにしてください" : "インデックス作成が動いている間は押せません。一時停止してからにしてください",
        IndexRunState.Pausing => "一時停止の途中です。止まるまで待ってください",
        IndexRunState.Paused => "一時停止中です。先に「再開」を押してください",
        _ => throw new ArgumentOutOfRangeException(nameof(run), run, "この状態では押せない理由は無い"),
    };
}
