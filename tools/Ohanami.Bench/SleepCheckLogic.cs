using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ohanami.Bench;

/// <summary>スリープと強制終了の手動確認(ADR 0026)の場面。<c>sleep-report --scenario</c> の値。</summary>
internal enum SleepScenario
{
    /// <summary>VLM の処理中(リクエストの途中、または画像の合間)にスリープ → 復帰。</summary>
    SleepVlm,
    /// <summary>llama-server の読み込み中(/health を待っている間)にスリープ → 復帰。</summary>
    SleepLoad,
    /// <summary>埋め込み(ORT)の処理中にスリープ → 復帰。</summary>
    SleepEmbed,
    /// <summary>App を強制終了した直後(llama-server が消えたか)。</summary>
    KillAppAfterKill,
    /// <summary>App を強制終了し、起動し直して続きを流し終えたあと。</summary>
    KillAppFinal,
    /// <summary>llama-server だけを kill したとき。</summary>
    KillServer,
    /// <summary>サーバの異常が 3 枚続いて、ジョブが止まったあと。</summary>
    CutoffStopped,
    /// <summary>止まったあと、「失敗をやり直す」で全部流し終えたあと。</summary>
    CutoffRecovered,
}

internal enum SleepVerdict { Pass, Fail, Warn, Info, NotApplicable }

internal sealed record SleepCheck(string Name, SleepVerdict Verdict, string Detail);

/// <summary>アプリの診断ログの 1 行(<c>logs\ohanami-yyyyMMdd.log</c>。時刻は UTC)。</summary>
internal sealed record SleepLogLine(DateTimeOffset Time, string Level, string Message);

/// <summary>Windows が記録したスリープ 1 回(イベントログ Microsoft-Windows-Power-Troubleshooter の ID 1)。</summary>
/// <param name="EffectiveState">イベントの EffectiveState の番号。**番号の意味は未確認**: SYSTEM_POWER_STATE
/// (https://learn.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-system_power_state。3 = PowerSystemSleeping2、4 = PowerSystemSleeping3 [S3]、5 = PowerSystemHibernate [S4])と同じ列挙かどうかを書いた Microsoft の文書は、見つからなかった。
/// そのため番号のまま出し、意味のラベルは付けない。分からなければ null。</param>
/// <param name="HiberPagesWritten">イベントの HiberPagesWritten(休止状態のファイルに書いたページ数。0 なら書いていない)。事実の欄として並べる(休止状態と S3 の見分けの手がかり。実機のスリープで裏を取る)。無ければ null。</param>
internal sealed record SleepPeriod(DateTimeOffset Start, DateTimeOffset End, int? EffectiveState, string? WakeSource, long? HiberPagesWritten = null)
{
    public TimeSpan Length => End - Start;
}

internal sealed record SleepRunRow(long Id, string JobKind, DateTimeOffset Started, DateTimeOffset? Ended);

/// <summary>フィクスチャのフォルダの画像の、ある段階(embed / caption / ocr)の件数。</summary>
/// <param name="Expected">フォルダの画像の数(DB の、現存するパスのハッシュ)。</param>
/// <param name="DoneTimes">done の確定の時刻(stage_status.updated_at)。</param>
/// <param name="FailedErrors">failed の error。</param>
internal sealed record SleepStageTally(int Expected, IReadOnlyList<DateTimeOffset> DoneTimes, IReadOnlyList<string> FailedErrors)
{
    public int Done => DoneTimes.Count;

    public int Failed => FailedErrors.Count;
}

/// <summary>フィクスチャの画像のうち、OCR の結果に、ファイル名の 4 桁の番号が含まれたもの(取り違え・失われた画像の検出)。</summary>
internal sealed record SleepOcrMatch(int Matched, int Total);

/// <summary>確認の材料(DB・ログ・プロセス・イベントログ)。ここから先は純粋な判定(<see cref="SleepCheckLogic.Evaluate"/>)。</summary>
/// <param name="FixtureFiles">フィクスチャのフォルダの、実際の画像のファイル数(走査が終わったかを見る)。</param>
/// <param name="LlamaSpawns">llama-server のログ(<c>llama-server-yyyyMMdd-HHmmss-fff.log</c>)の作成時刻 = 起動した時刻(読み込みの開始)。</param>
internal sealed record SleepSnapshot(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<SleepLogLine> Log,
    IReadOnlyList<SleepPeriod> Sleeps,
    IReadOnlyList<SleepRunRow> Runs,
    IReadOnlyDictionary<string, SleepStageTally> Stages,
    int FixtureFiles,
    SleepOcrMatch? Ocr,
    IReadOnlyList<DateTimeOffset> LlamaSpawns,
    IReadOnlyList<int> LlamaPids,
    bool AppRunning,
    bool PidFileExists,
    int? VramMiB,
    int VramLimitMiB);

/// <summary>
/// スリープ・強制終了の確認の合否の見方(結果を見る前に、ここに書く)。判定は、ログの文言・DB・プロセスから読めることだけで行う。
/// 読めないこと(リクエストの途中でスリープしたか、画像の合間だったか、復帰の通知が届いたか)は、情報として出して、判定には使わない。
/// ログの文言は <c>VlmRunner</c>・<c>LlamaServer</c>・<c>Indexer</c>・<c>EmbedRunner</c>・<c>ChildProcessGuard</c> の文言と同じ(変えたらここも変える)。
/// </summary>
internal static class SleepCheckLogic
{
    public const string Restarted = "kill して起動し直す";
    public const string HealthAnswered = "/health に答えた";
    public const string Ready = "llama-server を起動した";
    public const string CutoffMessage = "3 枚続けて異常";
    public const string JobFailed = "ジョブの実行が失敗した";
    public const string Orphan = "孤児として残っていた";
    public const string EmbedFailed = "埋め込みに失敗した";
    public const string TwiceInARow = "2 回続けた";
    public const string ResumeNotified = "スリープからの復帰の通知を受けた";
    public const string SessionRecreated = "セッションを作り直した";

    /// <summary>OCR の番号の一致の下限(これ未満は注意。OCR の誤読で 1〜2 枚外れることがある)。</summary>
    public const double OcrMatchMinimum = 0.9;

    /// <summary>1 リクエストのタイムアウト(300 秒)。これより長いスリープは、復帰の直後にタイムアウトで ServerFailure になる(正常な動き。未確認)。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(300);

    public static IReadOnlyList<SleepCheck> Evaluate(SleepScenario scenario, SleepSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var checks = new List<SleepCheck>();
        checks.Add(FixtureCheck(s, scenario));
        switch (scenario)
        {
            case SleepScenario.SleepVlm:
                IReadOnlyList<SleepPeriod> vlmSleeps = RelatedSleeps(s, "vlm", "caption");
                checks.AddRange(SleepDuringJob(s, vlmSleeps, "vlm", "caption"));
                checks.AddRange(ResumeNotice(s, vlmSleeps, required: true));
                checks.AddRange(VlmReaction(s, vlmSleeps));
                checks.Add(AllDone(s, "caption"));
                checks.Add(AllDone(s, "ocr"));
                checks.Add(NoLeftovers(s));
                checks.Add(OcrCheck(s));
                break;
            case SleepScenario.SleepLoad:
                IReadOnlyList<SleepPeriod> loadSleeps = RelatedSleeps(s, "vlm", "caption");
                checks.AddRange(SleepDuringLoad(s, loadSleeps));
                checks.AddRange(ResumeNotice(s, loadSleeps, required: false));
                checks.Add(AllDone(s, "caption"));
                checks.Add(NoLeftovers(s));
                checks.Add(OcrCheck(s));
                break;
            case SleepScenario.SleepEmbed:
                IReadOnlyList<SleepPeriod> embedSleeps = RelatedSleeps(s, "embed", "embed");
                checks.AddRange(SleepDuringJob(s, embedSleeps, "embed", "embed"));
                checks.AddRange(ResumeNotice(s, embedSleeps, required: false));
                checks.Add(SessionRecreation(s, embedSleeps));
                checks.Add(NoEmbedFailureLog(s));
                checks.Add(AllDone(s, "embed"));
                checks.Add(NoLeftovers(s));
                break;
            case SleepScenario.KillAppAfterKill:
                checks.AddRange(AfterAppKill(s));
                break;
            case SleepScenario.KillAppFinal:
                checks.AddRange(AfterAppKillFinal(s));
                break;
            case SleepScenario.KillServer:
                checks.Add(ServerKilled(s));
                checks.Add(AllDone(s, "caption"));
                checks.Add(AllDone(s, "ocr"));
                checks.Add(NoLeftovers(s));
                checks.Add(OcrCheck(s));
                break;
            case SleepScenario.CutoffStopped:
                checks.AddRange(CutoffStopped(s));
                break;
            case SleepScenario.CutoffRecovered:
                checks.Add(AllDone(s, "caption"));
                checks.Add(AllDone(s, "ocr"));
                checks.Add(NoLeftovers(s));
                checks.Add(OcrCheck(s));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "未知の場面");
        }
        return checks;
    }

    /// <summary>全体の判定: 不合格が 1 つでもあれば Fail、なければ注意が 1 つでもあれば Warn、なければ Pass。</summary>
    public static SleepVerdict Overall(IReadOnlyList<SleepCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (checks.Any(c => c.Verdict == SleepVerdict.Fail)) return SleepVerdict.Fail;
        return checks.Any(c => c.Verdict == SleepVerdict.Warn) ? SleepVerdict.Warn : SleepVerdict.Pass;
    }

    // ------------------------------------------------------------------ 共通

    private static SleepStageTally? Tally(SleepSnapshot s, string stage) => s.Stages.TryGetValue(stage, out SleepStageTally? t) ? t : null;

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string T(DateTimeOffset t) => t.UtcDateTime.ToString("HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static SleepCheck FixtureCheck(SleepSnapshot s, SleepScenario scenario)
    {
        SleepStageTally? any = Tally(s, "embed") ?? Tally(s, "caption");
        if (any is null || any.Expected == 0)
            return new SleepCheck("フィクスチャの画像が DB にある", SleepVerdict.Fail, "フォルダの画像が DB に無い(App でフォルダを追加して走査したか、--folder の指定を確かめる)");
        int minimum = scenario == SleepScenario.CutoffStopped ? 4 : 1;
        if (any.Expected < minimum)
            return new SleepCheck("フィクスチャの画像が DB にある", SleepVerdict.Fail, $"画像は {N(any.Expected)} 枚(この場面は {N(minimum)} 枚以上が要る)");
        if (s.FixtureFiles != any.Expected)
            return new SleepCheck("フィクスチャの画像が DB にある", SleepVerdict.Warn, $"DB は {N(any.Expected)} 枚、フォルダのファイルは {N(s.FixtureFiles)} 枚(走査が終わっていないか、別のフォルダ)");
        return new SleepCheck("フィクスチャの画像が DB にある", SleepVerdict.Pass, $"{N(any.Expected)} 枚");
    }

    /// <summary>その段階の画像が、全部 done で、failed が無い(ジョブが最後まで流れた)。</summary>
    private static SleepCheck AllDone(SleepSnapshot s, string stage)
    {
        string name = $"{stage}: 全部 done、failed なし";
        SleepStageTally? t = Tally(s, stage);
        if (t is null) return new SleepCheck(name, SleepVerdict.Fail, "段階の集計が無い");
        string detail = $"期待 {N(t.Expected)}、done {N(t.Done)}、failed {N(t.Failed)}";
        if (t.Failed > 0) detail += "(error: " + string.Join(" / ", t.FailedErrors.Distinct().Take(3)) + ")";
        return new SleepCheck(name, t.Expected > 0 && t.Done == t.Expected && t.Failed == 0 ? SleepVerdict.Pass : SleepVerdict.Fail, detail);
    }

    /// <summary>ジョブが終わった(または止まった)あと: llama-server が居ない。pid ファイルが残っていない。</summary>
    private static SleepCheck NoLeftovers(SleepSnapshot s)
    {
        if (s.LlamaPids.Count > 0)
            return new SleepCheck("ジョブのあと llama-server が残っていない", SleepVerdict.Fail, $"llama-server が {N(s.LlamaPids.Count)} つ居る(pid {string.Join(", ", s.LlamaPids)})。ジョブが終わってから見たか確かめる");
        if (s.PidFileExists)
            return new SleepCheck("ジョブのあと llama-server が残っていない", SleepVerdict.Warn, "プロセスは居ないが、pid ファイルが残っている(正常に止めたら消える。次の起動の掃除が片づける)");
        return new SleepCheck("ジョブのあと llama-server が残っていない", SleepVerdict.Pass, "プロセスなし、pid ファイルなし" + (s.VramMiB is { } v ? $"(VRAM {N(v)} MiB)" : ""));
    }

    private static SleepCheck OcrCheck(SleepSnapshot s)
    {
        const string name = "OCR にファイル名の番号が含まれる(取り違えがない)";
        if (s.Ocr is null || s.Ocr.Total == 0) return new SleepCheck(name, SleepVerdict.NotApplicable, "captions が無い");
        double ratio = (double)s.Ocr.Matched / s.Ocr.Total;
        string detail = $"{N(s.Ocr.Matched)} / {N(s.Ocr.Total)}(下限 {OcrMatchMinimum.ToString("P0", CultureInfo.InvariantCulture)})";
        return new SleepCheck(name, ratio >= OcrMatchMinimum ? SleepVerdict.Pass : SleepVerdict.Warn, detail);
    }

    private static IEnumerable<SleepLogLine> After(SleepSnapshot s, DateTimeOffset t) => s.Log.Where(l => l.Time >= t);

    // ------------------------------------------------------------------ スリープ(処理中)

    /// <summary>runs の ended_at と stage_status.updated_at の時刻のずれの許容(確定の書き込みは実行の終わりの直前に起きる)。</summary>
    private static readonly TimeSpan RunSlack = TimeSpan.FromSeconds(2);

    /// <summary>
    /// この場面の処理が動いていた時間帯: そのフォルダの画像の <paramref name="stage"/> の確定(done)の時刻を含む、<paramref name="jobKind"/> の実行(runs)。
    /// runs は DB 全体の行なので、フォルダの画像の確定を含まない実行(別のフォルダを流した実行)は入れない。含む実行が無ければ、確定の最初〜最後。確定が 1 枚も無ければ空。
    /// </summary>
    internal static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> SceneIntervals(SleepSnapshot s, string jobKind, string stage)
    {
        SleepStageTally? t = Tally(s, stage);
        if (t is null || t.DoneTimes.Count == 0) return [];
        List<(DateTimeOffset, DateTimeOffset)> runs = s.Runs
            .Where(r => r.JobKind == jobKind && t.DoneTimes.Any(d => d >= r.Started - RunSlack && (r.Ended is null || d <= r.Ended.Value + RunSlack)))
            .Select(r => (r.Started, r.Ended ?? s.To))
            .ToList();
        return runs.Count > 0 ? runs : [(t.DoneTimes.Min(), t.DoneTimes.Max())];
    }

    /// <summary>この場面の処理の時間帯と重なるスリープ(古い順)。対象の時間の別のスリープ(アプリを起動する前の、場面と無関係なもの)は入れない。</summary>
    internal static IReadOnlyList<SleepPeriod> RelatedSleeps(SleepSnapshot s, string jobKind, string stage)
    {
        var intervals = SceneIntervals(s, jobKind, stage);
        return s.Sleeps.Where(p => intervals.Any(iv => p.Start < iv.End && p.End > iv.Start)).OrderBy(p => p.Start).ToList();
    }

    /// <summary>場面と重なるスリープが 2 回以上あるとき、判定の名前に付ける印(どのスリープの判定か)。1 回なら付けない。</summary>
    private static string Suffix(IReadOnlyList<SleepPeriod> related, SleepPeriod p) => related.Count < 2 ? "" : $" [{T(p.Start)} のスリープ]";

    /// <summary>対象の時間のスリープの全部を、どれを判定に使ったかを付けて並べる(黙って 1 つを選ばない)。</summary>
    private static string DescribeSleeps(SleepSnapshot s, IReadOnlyList<SleepPeriod> related)
    {
        var parts = new List<string>();
        for (int i = 0; i < s.Sleeps.Count; i++)
        {
            SleepPeriod p = s.Sleeps[i];
            string use = related.Contains(p) ? "この場面の処理と重なる = 判定に使った" : "この場面の処理と重ならない = 判定に使わない";
            parts.Add($"{N(i + 1)} 回目 {T(p.Start)} → {T(p.End)}(約 {N((int)p.Length.TotalSeconds)} 秒、{StateText(p)}。{use})");
        }
        string head = $"対象の時間に {N(s.Sleeps.Count)} 回: ";
        if (related.Count >= 2) head += $"場面と重なるものが {N(related.Count)} 回あるので、全部を判定する(名前に時刻を付けた)。";
        return head + string.Join("、", parts);
    }

    /// <summary>スリープの状態の表示。EffectiveState は番号のまま(意味は未確認。ラベルは付けない)。HiberPagesWritten は事実の欄として並べる。</summary>
    internal static string StateText(SleepPeriod p) =>
        "EffectiveState=" + (p.EffectiveState is { } st ? N(st) + "(意味は未確認)" : "不明")
        + (p.HiberPagesWritten is { } h ? $"、HiberPagesWritten={h.ToString(CultureInfo.InvariantCulture)}" : "");

    /// <summary>スリープの時点でそのジョブが動いていて、復帰のあとに確定が続いた。判定するのは、この場面の処理と重なるスリープ(<paramref name="related"/>)。</summary>
    private static IEnumerable<SleepCheck> SleepDuringJob(SleepSnapshot s, IReadOnlyList<SleepPeriod> related, string jobKind, string stage)
    {
        if (s.Sleeps.Count == 0)
        {
            yield return new SleepCheck("スリープの記録がある", SleepVerdict.Fail, "対象の時間に、Windows のスリープの記録(イベントログ)が無い。スリープさせたか、--since を確かめる");
            yield break;
        }
        yield return new SleepCheck("スリープの記録がある", SleepVerdict.Pass, DescribeSleeps(s, related));

        if (related.Count == 0)
        {
            yield return new SleepCheck($"スリープの時点で {jobKind} のジョブが動いていた", SleepVerdict.Fail,
                "この場面のフォルダの画像を処理した実行(runs)と重なるスリープが無い(上の一覧のどれも重ならない)。ジョブが終わる前にスリープさせて、やり直す");
            yield break;
        }
        foreach (SleepPeriod p in related)
        {
            string suffix = Suffix(related, p);
            SleepRunRow? running = s.Runs.Where(r => r.JobKind == jobKind && r.Started < p.Start && (r.Ended is null || r.Ended > p.End)).OrderByDescending(r => r.Started).FirstOrDefault();
            yield return running is null
                ? new SleepCheck($"スリープの時点で {jobKind} のジョブが動いていた{suffix}", SleepVerdict.Fail, "その時刻に動いていた実行(runs の行)が無い。ジョブが終わる前にスリープさせて、やり直す")
                : new SleepCheck($"スリープの時点で {jobKind} のジョブが動いていた{suffix}", SleepVerdict.Pass, $"runs {running.Id}(開始 {T(running.Started)})");

            SleepStageTally? t = Tally(s, stage);
            int after = t is null ? 0 : t.DoneTimes.Count(d => d > p.End);
            int before = t is null ? 0 : t.DoneTimes.Count(d => d < p.Start);
            yield return new SleepCheck($"復帰のあとに {stage} の確定が続いた{suffix}", after > 0 ? SleepVerdict.Pass : SleepVerdict.Fail,
                $"スリープ前に {N(before)} 枚、復帰後に {N(after)} 枚が確定");
        }
    }

    /// <summary>VLM の、復帰のあとの llama-server の扱い(情報)と、「答えた」の直後に「kill して起動し直す」が並ぶかの確認。場面と重なるスリープごと。</summary>
    private static IEnumerable<SleepCheck> VlmReaction(SleepSnapshot s, IReadOnlyList<SleepPeriod> related)
    {
        for (int i = 0; i < related.Count; i++)
        {
            SleepPeriod? next = i + 1 < related.Count ? related[i + 1] : null;
            foreach (SleepCheck c in VlmReactionFor(s, related, related[i], next)) yield return c;
        }
    }

    private static IEnumerable<SleepCheck> VlmReactionFor(SleepSnapshot s, IReadOnlyList<SleepPeriod> related, SleepPeriod p, SleepPeriod? next)
    {
        string suffix = Suffix(related, p);
        DateTimeOffset from = p.End - TimeSpan.FromSeconds(5);   // 復帰の通知のログは、イベントの時刻の少し前後する
        DateTimeOffset until = next is null ? DateTimeOffset.MaxValue : next.Start;   // 次の(場面と重なる)スリープまで
        List<SleepLogLine> lines = After(s, from).Where(l => l.Time < until)
            .Where(l => l.Message.Contains(HealthAnswered, StringComparison.Ordinal) || l.Message.Contains(Restarted, StringComparison.Ordinal)).ToList();

        if (lines.Count == 0)
        {
            bool notified = Notices(s, p).Count > 0;
            yield return new SleepCheck("復帰のあとの llama-server の扱い(情報)" + suffix, SleepVerdict.Info, notified
                ? "復帰の通知は届いたが、起動し直しも「/health に答えた」のログも無い。復帰のあとに、次の画像の前の確認まで進まなかった(ジョブが終わった、または一時停止した)可能性がある"
                : "起動し直しも「/health に答えた」のログも無い。復帰の通知のログも無いので、通知が届いたかは、この結果からは分からない(リクエストの途中でスリープして、そのまま成功した可能性もある)");
        }
        else
        {
            yield return new SleepCheck("復帰のあとの llama-server の扱い(情報)" + suffix, SleepVerdict.Info,
                string.Join(" → ", lines.Take(6).Select(l => T(l.Time) + " " + Summarize(l.Message))));
        }

        // 「/health に答えた。そのまま続ける」の直後に「kill して起動し直す」が並ぶなら、復帰後は無条件に起動し直す形に替える
        bool pairFound = false;
        for (int i = 0; i + 1 < lines.Count; i++)
        {
            if (lines[i].Message.Contains(HealthAnswered, StringComparison.Ordinal) && lines[i + 1].Message.Contains(Restarted, StringComparison.Ordinal))
            {
                yield return new SleepCheck("「答えた」の直後に起動し直しが並ばない" + suffix, SleepVerdict.Warn,
                    $"{T(lines[i].Time)} /health に答えた → {T(lines[i + 1].Time)} {Summarize(lines[i + 1].Message)}。復帰後は無条件に起動し直す形に替えるか、実機のスリープの結果で決める");
                pairFound = true;
                break;
            }
        }
        if (!pairFound) yield return new SleepCheck("「答えた」の直後に起動し直しが並ばない" + suffix, SleepVerdict.Pass, "並んでいない");

        if (p.Length > RequestTimeout)
        {
            bool timeout = lines.Any(l => l.Message.Contains(Restarted, StringComparison.Ordinal) && l.Message.Contains("タイムアウト", StringComparison.Ordinal));
            yield return new SleepCheck("長いスリープ(300 秒超)のあとの扱い(情報)" + suffix, SleepVerdict.Info,
                timeout ? "復帰の直後にタイムアウトで起動し直した(想定どおり。タイマーはスリープ中の時間を含む)" : "タイムアウトでの起動し直しのログは無い(リクエストの途中ではなかった、またはスリープ中の時間を数えない)");
        }
    }

    private static string Summarize(string message) => message.Length <= 140 ? message : message[..140] + "…";

    /// <summary>復帰の通知のログ(復帰の時刻の少し前から 60 秒後まで)。</summary>
    private static List<SleepLogLine> Notices(SleepSnapshot s, SleepPeriod p) =>
        s.Log.Where(l => l.Time >= p.End - TimeSpan.FromSeconds(5) && l.Time <= p.End + TimeSpan.FromSeconds(60) && l.Message.Contains(ResumeNotified, StringComparison.Ordinal)).ToList();

    /// <summary>
    /// 復帰の通知(<c>PowerEvents</c>)が届いたこと。VLM の確認では必須(復帰後の確認はこの通知が起点)。通知が無くても、HTTP の失敗での復旧は働くので、不合格でなく注意。
    /// 読み込み中・埋め込みの確認では情報(通知は VLM ランナーだけが受ける)。場面と重なるスリープごとに見る(重なるものが無ければ対象外)。
    /// </summary>
    private static IEnumerable<SleepCheck> ResumeNotice(SleepSnapshot s, IReadOnlyList<SleepPeriod> related, bool required)
    {
        const string name = "復帰の通知(PowerEvents)が届いた";
        if (related.Count == 0)
        {
            yield return new SleepCheck(name, SleepVerdict.NotApplicable, s.Sleeps.Count == 0 ? "スリープの記録が無い" : "この場面と重なるスリープが無い");
            yield break;
        }
        foreach (SleepPeriod p in related)
        {
            string suffix = Suffix(related, p);
            List<SleepLogLine> notices = Notices(s, p);
            if (notices.Count > 0)
                yield return new SleepCheck(name + suffix, required ? SleepVerdict.Pass : SleepVerdict.Info, $"{N(notices.Count)} 回({string.Join("、", notices.Select(n => T(n.Time)))}。復帰は {T(p.End)})");
            else
                yield return new SleepCheck(name + suffix, required ? SleepVerdict.Warn : SleepVerdict.Info,
                    "復帰の通知のログが無い。通知を登録できなかった(起動時のログに「登録」の警告が無いか)か、取りこぼした。HTTP の失敗での復旧(リクエストの失敗 → 起動し直し)は、通知が無くても働く");
        }
    }

    /// <summary>ORT のセッションの作り直し(復帰後の最初の Run の失敗からの回復)。情報(作り直しが無いこと = 復帰後の最初の Run が成功、も正常)。</summary>
    private static SleepCheck SessionRecreation(SleepSnapshot s, IReadOnlyList<SleepPeriod> related)
    {
        const string name = "ORT のセッションの作り直し(情報)";
        DateTimeOffset since = related.Count > 0 ? related[0].Start : s.From;
        List<SleepLogLine> lines = s.Log.Where(l => l.Time >= since && l.Message.Contains(SessionRecreated, StringComparison.Ordinal)).ToList();
        return lines.Count == 0
            ? new SleepCheck(name, SleepVerdict.Info, "作り直しは無かった(復帰後の最初の Run は成功した)")
            : new SleepCheck(name, SleepVerdict.Info, $"{N(lines.Count)} 回: " + string.Join(" / ", lines.Take(3).Select(l => T(l.Time) + " " + Summarize(l.Message))));
    }

    private static SleepCheck NoEmbedFailureLog(SleepSnapshot s)
    {
        int n = s.Log.Count(l => l.Message.Contains(EmbedFailed, StringComparison.Ordinal));
        return new SleepCheck("ORT: 埋め込みの失敗のログがない", n == 0 ? SleepVerdict.Pass : SleepVerdict.Fail,
            n == 0 ? "なし(復帰後の最初の Run が失敗しても、作り直して成功すれば、ここには出ない。作り直しは次の項目で見る)" : $"{N(n)} 行");
    }

    // ------------------------------------------------------------------ スリープ(読み込み中)

    private static IEnumerable<SleepCheck> SleepDuringLoad(SleepSnapshot s, IReadOnlyList<SleepPeriod> related)
    {
        if (s.Sleeps.Count == 0)
        {
            yield return new SleepCheck("スリープの記録がある", SleepVerdict.Fail, "対象の時間に、Windows のスリープの記録が無い");
            yield break;
        }
        yield return new SleepCheck("スリープの記録がある", SleepVerdict.Pass, DescribeSleeps(s, related));
        if (related.Count == 0)
        {
            yield return new SleepCheck("スリープの時点で llama-server が読み込み中だった", SleepVerdict.Fail,
                "この場面のフォルダの画像を処理した実行(runs)と重なるスリープが無い(上の一覧のどれも重ならない。読み込みに失敗して画像が 1 枚も確定していない場合も、ここに来る)。読み込み中にスリープさせて、やり直す");
            yield break;
        }
        foreach (SleepPeriod p in related)
            foreach (SleepCheck c in LoadCheckFor(s, related, p)) yield return c;

        yield return new SleepCheck("llama-server が同時に 2 つ以上居ない", s.LlamaPids.Count <= 1 ? SleepVerdict.Pass : SleepVerdict.Fail,
            $"今 {N(s.LlamaPids.Count)} つ(スリープ中に起動し直しが重なって、古いのが残っていないか)");
    }

    private static IEnumerable<SleepCheck> LoadCheckFor(SleepSnapshot s, IReadOnlyList<SleepPeriod> related, SleepPeriod p)
    {
        string suffix = Suffix(related, p);
        DateTimeOffset? spawn = s.LlamaSpawns.Where(t => t <= p.Start).Select(t => (DateTimeOffset?)t).OrderByDescending(t => t).FirstOrDefault();
        if (spawn is null)
        {
            yield return new SleepCheck("スリープの時点で llama-server が読み込み中だった" + suffix, SleepVerdict.Fail, "スリープの前に始まった llama-server の起動(llama-server-*.log)が無い");
            yield break;
        }
        SleepLogLine? ready = s.Log.Where(l => l.Time >= spawn && l.Message.Contains(Ready, StringComparison.Ordinal)).OrderBy(l => l.Time).FirstOrDefault();
        if (ready is not null && ready.Time < p.Start)
        {
            yield return new SleepCheck("スリープの時点で llama-server が読み込み中だった" + suffix, SleepVerdict.Fail,
                $"起動({T(spawn.Value)})はスリープの前に終わっていた({T(ready.Time)})。読み込み中にスリープさせて、やり直す");
            yield break;
        }
        yield return new SleepCheck("スリープの時点で llama-server が読み込み中だった" + suffix, SleepVerdict.Pass,
            $"起動 {T(spawn.Value)} → スリープ {T(p.Start)}" + (ready is null ? "(起動の完了のログなし)" : $" → 起動の完了 {T(ready.Time)}"));

        SleepLogLine? failed = s.Log.Where(l => l.Time >= spawn && l.Message.Contains(JobFailed, StringComparison.Ordinal)).OrderBy(l => l.Time).FirstOrDefault();
        if (ready is not null && failed is null)
            yield return new SleepCheck("読み込み中のスリープのあとの結果(情報)" + suffix, SleepVerdict.Info, "スリープをまたいで、そのまま起動できた");
        else if (failed is not null)
            yield return new SleepCheck("読み込み中のスリープのあとの結果(情報)" + suffix, SleepVerdict.Info,
                $"起動に失敗した({T(failed.Time)} {Summarize(failed.Message)})。画面に理由が出て、「VLM を始める」をもう一度押して回復できたかは、次の項目で見る");
        else
            yield return new SleepCheck("読み込み中のスリープのあとの結果(情報)" + suffix, SleepVerdict.Warn, "起動の完了も失敗もログに無い(まだ読み込み中か、ログが足りない)");
    }

    // ------------------------------------------------------------------ 強制終了

    private static IEnumerable<SleepCheck> AfterAppKill(SleepSnapshot s)
    {
        yield return new SleepCheck("App が終わっている", s.AppRunning ? SleepVerdict.Fail : SleepVerdict.Pass, s.AppRunning ? "Ohanami.App.exe がまだ居る(強制終了のあとに見る)" : "居ない");
        yield return new SleepCheck("llama-server が一緒に消えた(ジョブオブジェクト)", s.LlamaPids.Count == 0 ? SleepVerdict.Pass : SleepVerdict.Fail,
            s.LlamaPids.Count == 0 ? "居ない" : $"{N(s.LlamaPids.Count)} つ残っている(pid {string.Join(", ", s.LlamaPids)})。数秒待ってから見直す");
        yield return new SleepCheck("VRAM が戻った", s.VramMiB is null ? SleepVerdict.NotApplicable : s.VramMiB <= s.VramLimitMiB ? SleepVerdict.Pass : SleepVerdict.Warn,
            s.VramMiB is { } v ? $"{N(v)} MiB(上限 {N(s.VramLimitMiB)} MiB。他のアプリの分が混ざる)" : "nvidia-smi が使えない");
        SleepRunRow? open = s.Runs.Where(r => r.JobKind == "vlm" && r.Ended is null).OrderByDescending(r => r.Started).FirstOrDefault();
        yield return new SleepCheck("強制終了した実行の runs の行は ended_at が空(情報)", SleepVerdict.Info,
            open is null ? "ended_at が空の vlm の行は無い(VLM のジョブを始める前に終了させた、または止める処理が走った)" : $"runs {open.Id}(開始 {T(open.Started)})。正常に止まらなかった印で、想定どおり");
        yield return new SleepCheck("pid ファイル(情報)", SleepVerdict.Info, s.PidFileExists ? "残っている(次の起動の掃除が確かめて消す)" : "無い");
    }

    private static IEnumerable<SleepCheck> AfterAppKillFinal(SleepSnapshot s)
    {
        int vlmRuns = s.Runs.Count(r => r.JobKind == "vlm");
        yield return new SleepCheck("VLM の runs が 2 つ以上(強制終了の前後)", vlmRuns >= 2 ? SleepVerdict.Pass : SleepVerdict.Fail, $"{N(vlmRuns)} つ");
        bool orphan = s.Log.Any(l => l.Message.Contains(Orphan, StringComparison.Ordinal));
        yield return new SleepCheck("前回の llama-server が孤児として残っていなかった", orphan ? SleepVerdict.Warn : SleepVerdict.Pass,
            orphan ? "「孤児として残っていた」のログがある。ジョブオブジェクトが効かず、pid ファイルの掃除で片づいた(動作はするが、ジョブオブジェクトの確認は不合格)" : "ログに無い(ジョブオブジェクトで消えた)");
        yield return AllDone(s, "caption");
        yield return AllDone(s, "ocr");
        yield return NoLeftovers(s);
        yield return OcrCheck(s);
    }

    private static SleepCheck ServerKilled(SleepSnapshot s)
    {
        List<SleepLogLine> restarts = s.Log.Where(l => l.Message.Contains(Restarted, StringComparison.Ordinal) && !l.Message.Contains("復帰", StringComparison.Ordinal)).ToList();
        return restarts.Count > 0
            ? new SleepCheck("llama-server の kill で起動し直した", SleepVerdict.Pass, $"{N(restarts.Count)} 回(" + Summarize(restarts[0].Message) + ")")
            : new SleepCheck("llama-server の kill で起動し直した", SleepVerdict.Fail, "「kill して起動し直す」のログが無い(kill したのが処理中の llama-server か、時間帯を確かめる)");
    }

    // ------------------------------------------------------------------ 連続のサーバ異常の打ち切り

    private static IEnumerable<SleepCheck> CutoffStopped(SleepSnapshot s)
    {
        bool cutoff = s.Log.Any(l => l.Level == "error" && l.Message.Contains(CutoffMessage, StringComparison.Ordinal));
        yield return new SleepCheck("3 枚続けて異常で止まった(error のログ)", cutoff ? SleepVerdict.Pass : SleepVerdict.Fail, cutoff ? "ある" : "「" + CutoffMessage + "」の error が無い");
        bool jobFailed = s.Log.Any(l => l.Message.Contains(JobFailed, StringComparison.Ordinal) && l.Message.Contains("llama-server", StringComparison.Ordinal));
        yield return new SleepCheck("ジョブが失敗として終わった", jobFailed ? SleepVerdict.Pass : SleepVerdict.Fail, jobFailed ? "「ジョブの実行が失敗した」がある" : "無い");

        SleepStageTally? t = Tally(s, "caption");
        if (t is null) yield break;
        yield return new SleepCheck("failed がちょうど 3 枚", t.Failed == 3 ? SleepVerdict.Pass : SleepVerdict.Fail, $"failed {N(t.Failed)}、done {N(t.Done)}");
        bool allTwice = t.FailedErrors.All(e => e.Contains(TwiceInARow, StringComparison.Ordinal));
        yield return new SleepCheck("failed の理由が「同じ画像で 2 回続けた」", t.Failed > 0 && allTwice ? SleepVerdict.Pass : SleepVerdict.Fail, string.Join(" / ", t.FailedErrors.Distinct().Take(2)));
        int untouched = t.Expected - t.Done - t.Failed;
        yield return new SleepCheck("4 枚目以降は記録がない(やり直せる)", untouched >= 1 ? SleepVerdict.Pass : SleepVerdict.Fail, $"未処理 {N(untouched)} 枚");
        yield return NoLeftovers(s);
    }
}
