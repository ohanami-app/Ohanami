using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// 「失敗をやり直す」の計画(<see cref="IndexStatusText.PlanRetry"/>)。<paramref name="EmbedFailed"/> と <paramref name="VlmFailed"/> は、失敗した画像の数。
/// </summary>
internal sealed record RetryPlan(int EmbedFailed, int VlmFailed)
{
    /// <summary>やり直す画像があるか。</summary>
    public bool Any => EmbedFailed > 0 || VlmFailed > 0;

    /// <summary>失敗を待ちに戻すジョブ(<c>Indexer.RetryFailedAsync</c> に渡す)。失敗のあるものだけ。</summary>
    public IReadOnlyList<JobKind> RetryKinds => [.. new[] { (JobKind.Embed, EmbedFailed), (JobKind.Vlm, VlmFailed) }.Where(x => x.Item2 > 0).Select(x => x.Item1)];
}

/// <summary>
/// 左パネルの「インデックス」欄の**固定の 7 行**(<see cref="IndexStatusText.BuildRows"/>。欄の高さを、どの状態でも同じにする。ADR 0039)。
/// 同じ種類の情報は、いつも同じ行に出す。空の行は空のまま、高さを保つ。入りきらない行は、折り返さず末尾を「…」で切り、全文はその行のヒントで見る。
/// <list type="number">
/// <item>特徴ベクトル検索の件数(「特徴ベクトル検索: 1030 / 1030 枚」)と、その内訳(「未作成 218、失敗 1、要再作成 2」。0 のものは書かない)。</item>
/// <item>テキスト検索の件数と、その内訳。</item>
/// <item><see cref="State"/>: 状態(「停止中 (開始を押すまで作りません)」「実行中: …のインデックス作成」など。**どの状態でも 1 行に収まる**。長い説明は <see cref="StateFull"/>(ヒント))。</item>
/// <item><see cref="Progress"/>: 実行中の速さと残り(「0.1 枚/秒、残り約 34 時間 30 分」)。実行中だけ。</item>
/// <item><see cref="Note"/>: 補足。同時に複数あるときは、優先順の先頭だけを出し、全部は <see cref="NoteFull"/>(ヒント)に。</item>
/// </list>
/// </summary>
internal sealed record IndexRows(string Stage1Count, string Stage1Extra, string Stage2Count, string Stage2Extra, string State, string StateFull, string Progress, string Note, string NoteFull)
{
    public static IndexRows Empty { get; } = new("", "", "", "", "", "", "", "", "");
}

/// <summary>
/// インデックスの進捗の表示の文字列(画面の部品を使わない純粋な関数。テストできる)。左のパネル(<see cref="IndexPanel"/>)が使う。
/// 文言は ADR 0038 に沿う。
/// </summary>
internal static class IndexStatusText
{
    /// <summary>ジョブの表示名(中身の説明つき)。<see cref="ProcessingWords.StageNameLong"/>(段階の名前を作る唯一の場所)。</summary>
    public static string JobLabel(JobKind? job) => ProcessingWords.StageNameLong(job);

    /// <summary>段階の件数の行(「特徴ベクトル検索: 12 / 30 枚 (未作成 18、失敗 1、要再作成 2)」)。段階が無ければ null。</summary>
    public static string? StageLine(string label, StageSummary? stage)
    {
        if (stage is null) return null;
        (string count, string extra) = StageParts(label, stage);
        return extra.Length == 0 ? count : count + " (" + extra + ")";
    }

    /// <summary>段階の件数の行と、その内訳(「未作成 18、失敗 1、要再作成 2」。0 のものは書かない。無ければ空)。段階が無ければ両方空。</summary>
    public static (string Count, string Extra) StageParts(string label, StageSummary? stage)
    {
        if (stage is null) return ("", "");
        string count = string.Create(CultureInfo.InvariantCulture, $"{label}: {stage.Done} / {Total(stage)} 枚");
        var extra = new List<string>();
        if (stage.Pending > 0) extra.Add($"{ProcessingWords.WordPending} {stage.Pending}");
        if (stage.Failed > 0) extra.Add($"{ProcessingWords.WordFailed} {stage.Failed}");
        if (stage.Outdated > 0) extra.Add($"{ProcessingWords.WordOutdated} {stage.Outdated}");
        return (count, string.Join("、", extra));
    }

    /// <summary>
    /// 固定の行を作る(<see cref="IndexRows"/>)。補足の行の優先順(同時に複数あるとき、先頭だけを出す。全部はヒント):
    /// 1 止まった原因・準備できなかった理由(止まって、次の行動が要る)→ 2 準備中(動き出していない理由)→ 3 スキャン中(いま起きている別の操作)→ 4 いま処理している画像のファイル名(進みの細部)→ 5 飛ばした枚数
    /// (「インデックスの状態…」の隣のまとめの行にも出ているので、ここは最後)。
    /// </summary>
    public static IndexRows BuildRows(IndexState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        (string c1, string e1) = StageParts(ProcessingWords.StageName(JobKind.Embed), s.Stages.FirstOrDefault(x => x.Stage == Stage.Embed));
        // VLM は ocr と caption が同じ呼び出しで同時に埋まる(件数は同じ)ので、説明文の段階だけを出す
        (string c2, string e2) = StageParts(ProcessingWords.StageName(JobKind.Vlm), s.Stages.FirstOrDefault(x => x.Stage == Stage.Caption));
        // モデルのファイルが無いと、その段階は動かせない(失敗ではない)。件数の内訳の頭に「モデル未取得」と書き、下の補足に足りないファイルを書く
        IReadOnlyList<string> vlmMissing = UnavailableFiles(s, JobKind.Vlm);
        if (vlmMissing.Count > 0 && c2.Length > 0) e2 = e2.Length == 0 ? ProcessingWords.ModelNotFetchedExtra : ProcessingWords.ModelNotFetchedExtra + "、" + e2;

        string state = s.Run switch
        {
            IndexRunState.Running => ProcessingWords.RunningLine(s.CurrentJob),
            IndexRunState.Idle => ProcessingWords.StateIdle,
            IndexRunState.Waiting => ProcessingWords.StateWaiting,
            IndexRunState.Pausing => ProcessingWords.StatePausing,
            IndexRunState.Paused => ProcessingWords.StatePaused,
            IndexRunState.Failed => ProcessingWords.StateFailed,
            _ => "",
        };

        var speed = new List<string>();
        if (s.Run == IndexRunState.Running)
        {
            if (s.ImagesPerSecond is { } ips) speed.Add(string.Create(CultureInfo.InvariantCulture, $"{ips:0.0} 枚/秒"));
            if (s.Remaining is { } rem) speed.Add("残り約 " + IndexPanel.FormatDuration(rem));
        }

        // 補足の行(短い 1 行と、全文のヒント)。短い行は、左パネルの幅(約 264 ピクセル)に収まる分だけが見えるので、先頭に理由を置く。
        // 失敗の理由は、頭の「…を準備できませんでした: 」「止まった原因: 」を短い行から外す(全文のヒントには、段階の名前つきで残す)。すぐ上の状態の行に「停止中 (失敗で止まりました)」が出るので、失敗したことは分かる。
        // 頭だけで 1 行の大半(約 190〜260 ピクセル)を使うと、理由の頭(「NVIDIA の GPU を使えません。…」)が見えない
        var notes = new List<(string Short, string Full)>();
        bool sameAsEngine = s.Engine.Phase == EnginePhase.Failed && string.Equals(s.LastError, s.Engine.Message, StringComparison.Ordinal);
        if (s.Engine.Phase == EnginePhase.Failed)
        {
            string reason = UiText.Plain(s.Engine.Message);
            notes.Add((reason, ProcessingWords.PrepareFailedPrefix(s.CurrentJob) + reason));
        }
        if (s.LastError is { } err && !sameAsEngine)
        {
            string reason = UiText.Plain(err);
            notes.Add((reason, ProcessingWords.StopReasonPrefix + reason));
        }
        if (vlmMissing.Count > 0) notes.Add((ProcessingWords.ModelNotFetchedNote(JobKind.Vlm), ProcessingWords.ModelNotFetchedHint(JobKind.Vlm, vlmMissing)));
        if (s.Engine.Phase == EnginePhase.Checking) notes.Add(Same(ProcessingWords.PreparingLine(s.CurrentJob)));
        if (s.Scan is { } scan)
            notes.Add(Same(string.Create(CultureInfo.InvariantCulture, $"スキャン中: {scan.FilesSeen} 枚を確認しました (内容の確認 {scan.Hashed}、新規 {scan.Added}、変更 {scan.Changed})")));
        if (s.Run == IndexRunState.Running && s.CurrentPath is { } cp) notes.Add(Same(Path.GetFileName(cp)));
        if (s.SkippedImages > 0) notes.Add(Same(ProcessingWords.SkippedImagesLine(s.SkippedImages)));

        return new IndexRows(c1, e1, c2, e2, state, ProcessingWords.StateHint(s.Run) ?? state, string.Join("、", speed), notes.Count > 0 ? notes[0].Short : "",
            string.Join('\n', notes.Select(n => n.Full)));
    }

    /// <summary>短い行と全文が同じ補足。</summary>
    private static (string Short, string Full) Same(string text) => (text, text);

    /// <summary>モデルのファイルが無くて動かせない段階の、足りないファイル(動かせるなら空)。</summary>
    public static IReadOnlyList<string> UnavailableFiles(IndexState s, JobKind job)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.UnavailableJobs.FirstOrDefault(u => u.Job == job)?.MissingFiles ?? [];
    }

    private static int Total(StageSummary s) => s.Pending + s.Done + s.Failed + s.Outdated;

    /// <summary>
    /// 進捗バーの割合(0〜1)。いまのジョブが VLM のときは VLM の段階(説明文 = <see cref="Stage.Caption"/>。<see cref="Stage.Ocr"/> は同じ件数で進む)、
    /// それ以外(動いていないときを含む)は埋め込み。
    /// </summary>
    public static double Progress(IndexState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        StageSummary? stage = s.Stages.FirstOrDefault(x => x.Stage == (s.CurrentJob == JobKind.Vlm ? Stage.Caption : Stage.Embed));
        if (stage is null) return 0;
        int total = Total(stage);
        return total == 0 ? 0 : (double)stage.Done / total;
    }

    /// <summary>
    /// 失敗した画像の数から、「失敗をやり直す」の計画を作る。VLM は ocr と caption が同じ呼び出しで埋まるが、別の段階の行なので、多いほうを数える。
    /// </summary>
    public static RetryPlan PlanRetry(IndexState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        int Failed(Stage stage) => s.Stages.FirstOrDefault(x => x.Stage == stage)?.Failed ?? 0;
        return new RetryPlan(Failed(Stage.Embed), Math.Max(Failed(Stage.Ocr), Failed(Stage.Caption)));
    }

    /// <summary>状態の全体の文章(走査、段階の件数、実行の状態、準備の状態、飛ばした枚数、止まった原因)。</summary>
    public static string Build(IndexState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var sb = new StringBuilder();

        if (s.Scan is { } scan)
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"スキャン中: {scan.FilesSeen} 枚を確認しました (内容の確認 {scan.Hashed}、新規 {scan.Added}、変更 {scan.Changed})\n"));

        if (StageLine(ProcessingWords.StageName(JobKind.Embed), s.Stages.FirstOrDefault(x => x.Stage == Stage.Embed)) is { } embedLine) sb.Append(embedLine).Append('\n');
        // VLM は ocr と caption が同じ呼び出しで同時に埋まる(件数は同じ)ので、説明文の段階だけを出す
        StageSummary? caption = s.Stages.FirstOrDefault(x => x.Stage == Stage.Caption);
        if (StageLine(ProcessingWords.StageName(JobKind.Vlm), caption) is { } vlmLine) sb.Append(vlmLine).Append('\n');

        switch (s.Run)
        {
            case IndexRunState.Running:
                sb.Append(ProcessingWords.RunningLine(s.CurrentJob));
                var speed = new List<string>();
                if (s.ImagesPerSecond is { } ips) speed.Add(string.Create(CultureInfo.InvariantCulture, $"{ips:0.0} 枚/秒"));
                if (s.Remaining is { } rem) speed.Add("残り約 " + IndexPanel.FormatDuration(rem));
                if (speed.Count > 0) sb.Append(" (").Append(string.Join("、", speed)).Append(')');
                sb.Append('\n');
                if (s.CurrentPath is { } cp) sb.Append(Path.GetFileName(cp)).Append('\n');
                break;
            case IndexRunState.Idle: sb.Append(ProcessingWords.StateIdle).Append('\n'); break;
            case IndexRunState.Waiting: sb.Append(ProcessingWords.StateWaiting).Append('\n'); break;
            case IndexRunState.Pausing: sb.Append(ProcessingWords.StatePausing).Append('\n'); break;
            case IndexRunState.Paused: sb.Append(ProcessingWords.StatePaused).Append('\n'); break;
            case IndexRunState.Failed: sb.Append(ProcessingWords.StateFailed).Append('\n'); break;
        }

        // 準備の状態(Engine)。特徴ベクトル検索の段階なら GPU とモデルの自己検査、全文検索の段階なら VLM の処理プログラムの検証・起動と読み込み。
        // **作る側の文**(ADR 0038): 「…のインデックス作成の準備中」「…のインデックスを準備できなかった」と言う(「特徴ベクトル検索の準備中」だと、検索が使えない話に読める)
        if (s.Engine.Phase == EnginePhase.Checking) sb.Append(ProcessingWords.PreparingLine(s.CurrentJob)).Append('\n');
        if (s.Engine.Phase == EnginePhase.Failed) sb.Append(ProcessingWords.PrepareFailedPrefix(s.CurrentJob)).Append(UiText.Plain(s.Engine.Message)).Append('\n');

        if (UnavailableFiles(s, JobKind.Vlm) is { Count: > 0 } vlmMissing) sb.Append(ProcessingWords.ModelNotFetchedHint(JobKind.Vlm, vlmMissing)).Append('\n');
        if (s.SkippedImages > 0) sb.Append(ProcessingWords.SkippedImagesLine(s.SkippedImages)).Append('\n');
        // 準備の失敗(Engine が Failed)では、ジョブの失敗の理由(LastError)も同じ文になる。同じ文を 2 行出さない(「準備に失敗」の行だけ)。
        // 準備の失敗ではない打ち切り(3 枚続けて異常など。Engine は Failed にならない)は、「止まった原因」の行だけが出る
        bool sameAsEngine = s.Engine.Phase == EnginePhase.Failed && string.Equals(s.LastError, s.Engine.Message, StringComparison.Ordinal);
        if (s.LastError is { } err && !sameAsEngine) sb.Append(ProcessingWords.StopReasonPrefix).Append(UiText.Plain(err)).Append('\n');
        return sb.ToString().TrimEnd();
    }
}
