using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core;
using Ohanami.Core.Models;
using Ohanami.Core.Vlm;

namespace Ohanami.Bench;

/// <summary>vlm-full の設定。パスはすべてフルパス。</summary>
/// <param name="ImagesDirectory">画像のフォルダ(読み取りだけ)。</param>
/// <param name="ModelsDirectory">models/ に当たるディレクトリ(埋め込みのモデルと VLM のモデルの両方)。</param>
/// <param name="LlamaServerExe">llama-server.exe。</param>
/// <param name="Root">通しの DB・サムネイル・結果の置き場(<c>&lt;root&gt;/db/bench.db</c>。利用者の実際のデータの場所とは別。リポジトリの外)。</param>
/// <param name="PauseAfter">VLM の done がこの枚数に増えたら、一時停止 → 確認 → 再開を 1 回行う。0 なら行わない。</param>
/// <param name="Preflight">true なら、検証と走査だけで終わる(埋め込みも VLM も流さない。GPU を使わない)。</param>
/// <param name="StopAfter">VLM の done がこの枚数(開始前からの増え)に増えたら、Ctrl-C と同じに、一時停止して終わる(運転の確認用。0 なら止めない)。</param>
internal sealed record VlmFullOptions(string ImagesDirectory, string ModelsDirectory, string LlamaServerExe, string Root, int PauseAfter, bool Preflight, int StopAfter = 0);

/// <summary>通しの観測(<see cref="VlmFullLogic.Evaluate"/> の入力)。</summary>
internal sealed record VlmFullObservation(
    int CaptionPending, int CaptionDone, int CaptionFailed, int EmbedFailed, IndexRunState FinalRun,
    double ElapsedSeconds, int? ProcessVramPeakMiB, double PrivatePeakMiB, double WorkingSetPeakMiB,
    bool PauseExpected, bool PauseHappened, bool PauseProcessGone, bool PauseVramReleased, int VlmRuns,
    string? ProcessVramProblem = null, int GpuWideDeltaMiB = 0, string ProcessVramNote = "");

/// <summary>
/// 通しの合格の見方(結果を見る前に決めたもの。VLM の評価の run の値から決まる)。
/// 画像ごとの評価との一致・再試行・時間の分布・runs は、別に <c>captions-check</c>(<see cref="CaptionsCheckLogic"/>)で見る。ここは、通しの運転(完走・一時停止と再開・資源)。
/// </summary>
internal static class VlmFullLogic
{
    /// <summary>VLM の評価の全件の run: 合計の時間(秒)。1030 枚で 11,944 秒(3.32 時間)。</summary>
    public const double VlmEvalTotalSeconds = 11944;

    /// <summary>
    /// 合計の時間の合格線: 4.5 時間(16,200 秒)。見込みは 2 通り(結果を見る前に書く): (1) 3.2〜3.6 時間 = 評価の 3.32 時間から、length の 16 枚の分を差し替えたもの
    /// (評価は 4096 トークンまで出して 1 枚約 70 秒の失敗。製品は max_tokens 2048 なので、1 回目の length は約 37 秒、2 回目の dry_multiplier で成功する 14 枚は 5〜19 秒、
    /// 残りの 2 枚が 3 回目まで要る)。(2) 再試行を重く見た見積もり: 1030 × 約 12 秒 ≈ 3.4 時間に、length の 16 枚の再試行(1 枚最長 3 回 × 60〜100 秒)で +0.5〜1 時間 = 3.9〜4.4 時間
    /// (再試行の length が 4096 トークンのときの時間を使っているので、max_tokens 2048 の製品では長めの見積もり)。評価の 1.3 倍(4.3 時間)は、(2) の上限 4.4 時間を割るので、合格線にしない。
    /// 4.5 時間にしてある(「悪いほうの見積もりでも、夜間の通しとして成り立つ」ことを見る線)。
    /// </summary>
    public const double ElapsedLimitSeconds = 16_200;

    /// <summary>
    /// VRAM のピーク(llama-server のプロセスの専用メモリ = 性能カウンタ GPU Process Memory の Dedicated Usage)の足切り(評価の足切り。評価の実測は 6,096MiB[GPU 全体 − 起動前])。
    /// 通しの実測で、GPU 全体 − 起動前の値には、途中でブラウザで動画を見た分(310〜530MiB)が混ざって 6,783MiB になったが、プロセスごとの値は 6,058MiB だった。
    /// 以降、合否はプロセスごとの値で決める(GPU 全体 − 起動前の値は参考に並べるだけ)。
    /// </summary>
    public const int VramLimitMiB = 6656;

    /// <summary>llama-server のプライベートバイトのピークの合格線(評価の実測は 9,812MiB。評価の約 1.22 倍)。</summary>
    public const double PrivateLimitMiB = 12_000;

    /// <summary>llama-server のワーキングセットのピークの合格線(評価の実測は 3,178MiB。評価の約 1.26 倍)。</summary>
    public const double WorkingSetLimitMiB = 4_000;

    /// <summary>一時停止のあと、llama-server が終わり VRAM が戻るのを待つ上限(秒)と、戻ったとみなす VRAM(起動前 + この MiB 以下)。</summary>
    public const int PauseReleaseWaitSeconds = 15;

    public const int PauseVramSlackMiB = 700;

    public static IReadOnlyList<CheckResult> Evaluate(VlmFullObservation o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var r = new List<CheckResult>
        {
            new("A. 完走", o.CaptionPending == 0 && (o.FinalRun is IndexRunState.Idle or IndexRunState.Waiting) && o.EmbedFailed == 0,
                $"VLM の待ち {o.CaptionPending}(許容 0)、最後の状態 {o.FinalRun}(Idle か Waiting(待ちが無くなって待機に入った)のはず)、埋め込みの失敗 {o.EmbedFailed}(許容 0)。VLM の done {o.CaptionDone}、failed {o.CaptionFailed}"),
        };
        if (o.PauseExpected)
        {
            r.Add(new CheckResult("B. 一時停止と再開", o.PauseHappened && o.PauseProcessGone && o.PauseVramReleased && o.VlmRuns >= 2,
                $"一時停止 {(o.PauseHappened ? "した" : "しなかった")}、{PauseReleaseWaitSeconds} 秒以内に llama-server が終わった {Yn(o.PauseProcessGone)}、VRAM が起動前 + {PauseVramSlackMiB}MiB 以下に戻った {Yn(o.PauseVramReleased)}、VLM の runs の行 {o.VlmRuns}(再開で新しい行になるので 2 以上)"));
        }
        r.Add(new CheckResult("C. 合計の時間", o.ElapsedSeconds <= ElapsedLimitSeconds,
            $"{o.ElapsedSeconds / 3600:F2} 時間(合格線 {ElapsedLimitSeconds / 3600:F2} 時間。見込み 3.2〜3.6 時間、再試行を重く見た見積もり 3.9〜4.4 時間。評価は 3.32 時間。埋め込みと走査を含む)"));
        string reference = $"参考(合否に使わない): GPU 全体 − 起動前 = {o.GpuWideDeltaMiB}MiB。ほかのプロセスの分が混ざる";
        if (o.ProcessVramPeakMiB is not { } vram)
            r.Add(new CheckResult("D. VRAM のピーク", false,
                $"**測れなかった**(プロセスごとの値が取れていない): {o.ProcessVramProblem ?? "(理由なし)"}。GPU 全体の値には戻さない。{reference}", Unmeasured: true));
        else
            r.Add(new CheckResult("D. VRAM のピーク", vram <= VramLimitMiB,
                $"llama-server のプロセス分 {vram}MiB(足切り {VramLimitMiB}MiB。評価は 6,096MiB。性能カウンタ Dedicated Usage){(o.ProcessVramNote.Length > 0 ? "。" + o.ProcessVramNote : "")}。{reference}"));
        r.Add(new CheckResult("E. RAM のピーク", o.PrivatePeakMiB <= PrivateLimitMiB && o.WorkingSetPeakMiB <= WorkingSetLimitMiB,
            $"llama-server のプライベート {o.PrivatePeakMiB:F0}MiB(合格線 {PrivateLimitMiB:F0}。評価は 9,812)、ワーキングセット {o.WorkingSetPeakMiB:F0}MiB(合格線 {WorkingSetLimitMiB:F0}。評価は 3,178)"));
        return r;

        static string Yn(bool b) => b ? "はい" : "いいえ";
    }

    /// <summary>
    /// 終了コード: 0 = 全部合格、3 = 実行が Failed、6 = 不合格の項目がある、7 = 不合格はなく、測れなかった項目(D)だけがある(測り直し)。
    /// 不合格と測れなかったが両方あれば 6(測れなかった項目も報告には出る)。
    /// </summary>
    public static int ExitCode(IReadOnlyList<CheckResult> results, IndexRunState finalRun)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.All(c => c.Passed)) return 0;
        if (finalRun == IndexRunState.Failed) return 3;
        return results.Any(c => !c.Passed && !c.Unmeasured) ? 6 : 7;
    }
}

/// <summary>llama-server のプロセス(この実行より前から動いていたものを除く)のメモリを 1 秒おきに取り、GPU 全体の VRAM と合わせて最大値を持つ。</summary>
internal sealed class ServerSampler : IDisposable
{
    private readonly GpuMonitor _gpu;
    private readonly HashSet<int> _ignore;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _task;
    private readonly object _lock = new();
    private double _privatePeak, _workingSetPeak;
    private int _vramWhileServer;
    private int _count;

    public ServerSampler(GpuMonitor gpu)
    {
        _gpu = gpu;
        _ignore = Process.GetProcessesByName("llama-server").Select(p => p.Id).ToHashSet();
        _task = Task.Run(LoopAsync);
    }

    public double PrivatePeakMiB { get { lock (_lock) return _privatePeak; } }

    public double WorkingSetPeakMiB { get { lock (_lock) return _workingSetPeak; } }

    /// <summary>llama-server が動いている間の、GPU 全体の VRAM の最大(MiB。1 秒おきの標本)。</summary>
    public int VramWhileServerMiB { get { lock (_lock) return _vramWhileServer; } }

    /// <summary>いま動いている(この実行で起動した)llama-server の pid(プロセスごとの VRAM の測定が、標本ごとに引く。起動し直して pid が変わっても追える)。</summary>
    public IReadOnlyCollection<int> ServerPids() => LlamaServerProcesses.Except(_ignore);

    /// <summary>いま動いている(この実行で起動した)llama-server の数。</summary>
    public int Count { get { lock (_lock) return _count; } }

    public static int RunningCount(IReadOnlySet<int>? ignore = null) =>
        Process.GetProcessesByName("llama-server").Count(p => ignore is null || !ignore.Contains(p.Id));

    private async Task LoopAsync()
    {
        const double MiB = 1024.0 * 1024.0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                double priv = 0, ws = 0;
                int n = 0;
                foreach (Process p in Process.GetProcessesByName("llama-server"))
                {
                    try
                    {
                        if (_ignore.Contains(p.Id)) continue;
                        n++;
                        priv = Math.Max(priv, p.PrivateMemorySize64 / MiB);
                        ws = Math.Max(ws, p.WorkingSet64 / MiB);
                    }
                    catch (InvalidOperationException) { /* 終わった */ }
                    finally { p.Dispose(); }
                }
                lock (_lock)
                {
                    _count = n;
                    _privatePeak = Math.Max(_privatePeak, priv);
                    _workingSetPeak = Math.Max(_workingSetPeak, ws);
                    if (n > 0) _vramWhileServer = Math.Max(_vramWhileServer, _gpu.Current);
                }
            }
            while (await timer.WaitForNextTickAsync(_cts.Token));
        }
        catch (OperationCanceledException) { /* 停止 */ }
    }

    /// <summary>この実行で起動した llama-server が無くなるのを、上限まで待つ。無くなれば true。</summary>
    public async Task<bool> WaitUntilGoneAsync(TimeSpan limit, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < limit)
        {
            if (RunningCount(_ignore) == 0) return true;
            await Task.Delay(500, ct);
        }
        return RunningCount(_ignore) == 0;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _task.Wait(2000); } catch (AggregateException) { /* 停止時の例外は無視 */ }
        _cts.Dispose();
    }
}

/// <summary>
/// <c>vlm-full</c>: 1030 枚の通し。製品の経路そのまま(<c>OhanamiCore</c> の <c>Indexer</c> に [Embed, Vlm] を渡す。App と同じ)で、
/// 利用者の実際のデータとは別の root の DB に流す。VLM の done が <see cref="VlmFullOptions.PauseAfter"/> 枚に増えたら、一時停止 → llama-server が終わり VRAM が戻ることを確認 → 再開、を 1 回行う。
/// 途中で Ctrl-C したら、一時停止して終わる(同じコマンドをもう一度実行すると、DB の続きから進む。待ち行列は毎回 DB から組み立てる)。
/// スリープは入れない(スリープの確認は sleep-report の道具で別に行う)。画像ごとの比較・時間の分布・runs は、終わってから <c>captions-check</c> で見る。
/// </summary>
internal static class VlmFull
{
    public static async Task<int> RunAsync(VlmFullOptions o, CancellationToken ct)
    {
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string outDir = Path.Combine(o.Root, "vlm-full", stamp);
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        void Out(string line)
        {
            Console.WriteLine(line);
            report.AppendLine(line);
        }

        // ---- 検証(製品と同じ。GPU は使わない)
        VlmSpec spec = VlmCatalog.Current;
        string vlmModelDir = Path.Combine(o.ModelsDirectory, VlmCatalog.DirectoryName);
        var verifier = new VlmVerifier();
        await verifier.VerifyModelAsync(vlmModelDir, spec, ct);
        LlamaServerVersion version = await verifier.VerifyLlamaServerAsync(o.LlamaServerExe, VlmCatalog.LlamaServerExeSha256, VlmCatalog.LlamaServerBuild, VlmCatalog.LlamaServerCommit, ct);
        Out($"検証済み: VLM のモデル 2 ファイルの sha256、llama-server build {version.Build} commit {version.Commit}。model_id = {spec.ModelId}");

        // ---- DB と走査
        var run = new RunOptions(o.ImagesDirectory, o.ModelsDirectory, o.Root, "", "vlm-full");
        Out($"DB: {Path.Combine(o.Root, "db", "bench.db")}(利用者の実際のデータとは別)");
        await using OhanamiCore core = await BenchRunner.OpenCoreAsync(run, ct, new VlmOptions(Path.GetFullPath(o.LlamaServerExe), vlmModelDir));
        await BenchRunner.ScanLibraryAsync(core, run, ct);
        IndexState start = core.Indexer.State;
        Out("開始前の状態: " + StageText(start));
        if (o.Preflight)
        {
            Out("preflight だけ(埋め込みも VLM も流さない)。");
            File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString(), new UTF8Encoding(false));
            return 0;
        }
        int captionDoneAtStart = StageOf(start, Stage.Caption)?.Done ?? 0;

        // ---- 流す
        using var gpu = new GpuMonitor();
        int baseline = gpu.Current;
        gpu.BeginWindow();
        using var sampler = new ServerSampler(gpu);
        // llama-server のプロセスごとの VRAM(性能カウンタ)。合否の D はこの値。読めるかを、3 時間流す前に確かめる(読めなくても流す: ほかの確認は有効。D は「測れなかった」になる)
        var processMemory = new PdhProcessGpuMemory();
        if (ProcessVramMonitor.Probe(processMemory) is { } probeProblem)
            Out($"**警告: 性能カウンタ(GPU Process Memory の Dedicated Usage)が読めない: {probeProblem}。D(VRAM のピーク)は「測れなかった」になる(GPU 全体の値には戻さない)。**");
        using var processVram = new ProcessVramMonitor(processMemory, sampler.ServerPids);
        processVram.StartBackground(TimeSpan.FromMilliseconds(200));
        var clock = Stopwatch.StartNew();
        Out($"流す: [Embed, Vlm](VRAM 起動前 {baseline}MiB。一時停止 {(o.PauseAfter > 0 ? $"VLM の done が +{o.PauseAfter} 枚になったとき 1 回" : "なし")})");

        bool interrupted = false;
        int pauseRequested = 0, stopRequested = 0;
        long lastLog = Stopwatch.GetTimestamp();
        core.Indexer.StateChanged += s =>
        {
            StageSummary? caption = StageOf(s, Stage.Caption);
            if (o.PauseAfter > 0 && caption is not null && s.Run == IndexRunState.Running && s.CurrentJob == JobKind.Vlm
                && caption.Done - captionDoneAtStart >= o.PauseAfter && Interlocked.Exchange(ref pauseRequested, 1) == 0)
            {
                Out($"  一時停止を求める(VLM の done {caption.Done}、経過 {clock.Elapsed:hh\\:mm\\:ss})");
                _ = Task.Run(core.Indexer.PauseAsync, CancellationToken.None);
            }
            if (o.StopAfter > 0 && caption is not null && s.Run == IndexRunState.Running && s.CurrentJob == JobKind.Vlm
                && caption.Done - captionDoneAtStart >= o.StopAfter && Interlocked.Exchange(ref stopRequested, 1) == 0)
            {
                Out($"  --stop-after: 一時停止して終わる(VLM の done {caption.Done}、経過 {clock.Elapsed:hh\\:mm\\:ss})");
                interrupted = true;
                _ = Task.Run(core.Indexer.PauseAsync, CancellationToken.None);
            }
            if (Stopwatch.GetElapsedTime(lastLog).TotalSeconds >= 60)
            {
                lastLog = Stopwatch.GetTimestamp();
                Console.WriteLine($"  [{clock.Elapsed:hh\\:mm\\:ss}] {StageText(s)}  実行 {s.Run}、{JobText(s)}" + (s.Remaining is { } rem ? $"、残り約 {IndexPanelText(rem)}" : ""));
            }
        };
        using CancellationTokenRegistration reg = ct.Register(() =>
        {
            interrupted = true;
            Console.WriteLine("中断の要求: 一時停止して終わる(同じコマンドをもう一度実行すると、続きから進む)");
            _ = Task.Run(core.Indexer.PauseAsync, CancellationToken.None);
        });

        bool pauseHappened = false, pauseGone = false, pauseVram = false;
        await core.Indexer.StartAsync([JobKind.Embed, JobKind.Vlm]);
        IndexState state = core.Indexer.State;

        if (state.Run == IndexRunState.Paused && !interrupted)
        {
            pauseHappened = true;
            Out($"  一時停止した(VLM の done {StageOf(state, Stage.Caption)?.Done}、経過 {clock.Elapsed:hh\\:mm\\:ss})");
            pauseGone = await sampler.WaitUntilGoneAsync(TimeSpan.FromSeconds(VlmFullLogic.PauseReleaseWaitSeconds), CancellationToken.None);
            await Task.Delay(1500, CancellationToken.None);
            int vramNow = gpu.Current;
            pauseVram = vramNow <= baseline + VlmFullLogic.PauseVramSlackMiB;
            Out($"  一時停止の確認: llama-server {(pauseGone ? "終わった" : "**まだ動いている**")}、VRAM {vramNow}MiB(起動前 {baseline}MiB。{(pauseVram ? "戻った" : "**戻っていない**")})");
            // 参考(合否に使わない。どの値なら解放と言えるかの線は、データを見てから決める): llama-server だった pid のインスタンスが残っていないか、ベンチ自身(ORT の CUDA のセッション)の Dedicated Usage
            try
            {
                foreach (string line in ProcessVramReference.AfterPause(processMemory.ReadDedicatedBytes(), processVram.ValuedPids(), Environment.ProcessId))
                    Out("  参考(合否に使わない): " + line);
            }
            catch (ProcessGpuMemoryException e) { Out("  参考(合否に使わない): 性能カウンタが読めなかった: " + e.Message); }
            Out("  再開する");
            await core.Indexer.ResumeAsync();
            state = core.Indexer.State;
        }

        clock.Stop();
        if (interrupted)
        {
            Out($"中断した(状態 {state.Run})。経過 {clock.Elapsed:hh\\:mm\\:ss}。{StageText(state)}");
            File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString(), new UTF8Encoding(false));
            return 1;
        }

        // ---- 結果
        Out("");
        Out($"終了: 状態 {state.Run}、経過 {clock.Elapsed:hh\\:mm\\:ss}({clock.Elapsed.TotalHours:F2} 時間)");
        Out("最後の状態: " + StageText(state));
        if (state.LastError is { } err) Out("直近の失敗: " + err);
        if (state.Engine.Phase == EnginePhase.Failed) Out("準備の失敗: " + state.Engine.Message);
        int vlmRuns = CountVlmRuns(core);
        StageSummary? cap = StageOf(state, Stage.Caption);
        StageSummary? emb = StageOf(state, Stage.Embed);
        ProcessVramMeasurement vram = processVram.AllResult();
        int gpuWideDelta = Math.Max(gpu.AllPeak, sampler.VramWhileServerMiB) - baseline;
        var obs = new VlmFullObservation(
            cap?.Pending ?? int.MaxValue, cap?.Done ?? 0, cap?.Failed ?? 0, emb?.Failed ?? int.MaxValue, state.Run,
            clock.Elapsed.TotalSeconds, vram.PeakMiB, sampler.PrivatePeakMiB, sampler.WorkingSetPeakMiB,
            o.PauseAfter > 0, pauseHappened, pauseGone, pauseVram, vlmRuns,
            vram.Problem, gpuWideDelta, vram.Describe());
        Out($"VRAM(llama-server のプロセスごと。性能カウンタ Dedicated Usage): " + (vram.PeakMiB is { } pk ? $"ピーク {pk}MiB" : "**測れなかった**" + (vram.ObservedPeakMiB is { } part ? $"(取れた値の最大 {part}MiB。使わない)" : "")) + "。" + vram.Describe());
        Out($"VRAM(参考。合否に使わない。GPU 全体): 200ms 間隔のピーク {gpu.AllPeak}MiB、llama-server が動いている間の 1 秒おきの標本の最大 {sampler.VramWhileServerMiB}MiB、起動前 {baseline}MiB(ほかのプロセスの分が混ざる)");
        Out("");
        Out("== 合格の見方(結果を見る前に決めたもの。VlmFullLogic.Evaluate) ==");
        IReadOnlyList<CheckResult> results = VlmFullLogic.Evaluate(obs);
        foreach (CheckResult c in results) Out($"  [{(c.Passed ? "合格" : c.Unmeasured ? "**測れなかった**" : "**不合格**")}] {c.Name}: {c.Detail}");
        bool all = results.All(c => c.Passed);
        bool anyFailed = results.Any(c => !c.Passed && !c.Unmeasured);
        Out(all ? "判定: 運転は合格。次に captions-check(評価との比較)と、ベンチの run。"
            : anyFailed ? "判定: 通らない項目がある。"
            : "判定: **測れなかった項目がある**(D)。ほかの項目は通っている。D は測り直す(実行のやり直しが要る)。終了コード 7。");
        File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString(), new UTF8Encoding(false));
        Console.WriteLine("結果: " + outDir);
        return VlmFullLogic.ExitCode(results, state.Run);
    }

    private static StageSummary? StageOf(IndexState s, Stage stage) => s.Stages.FirstOrDefault(x => x.Stage == stage);

    private static string StageText(IndexState s)
    {
        string Line(string label, StageSummary? st) => st is null ? "" : $"{label} {st.Done}/{st.Pending + st.Done + st.Failed + st.Outdated}(待ち {st.Pending}、失敗 {st.Failed}、要再作成 {st.Outdated})";
        return $"{Line("埋め込み", StageOf(s, Stage.Embed))}、{Line("VLM", StageOf(s, Stage.Caption))}";
    }

    private static string JobText(IndexState s) => s.CurrentJob is { } j ? j.ToString() : "-";

    private static string IndexPanelText(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} 時間 {t.Minutes} 分" : $"{t.Minutes} 分";

    private static int CountVlmRuns(OhanamiCore core)
    {
        using SqliteConnection conn = core.DatabaseForTests.CreateReadConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM runs WHERE job_kind = 'vlm'";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
