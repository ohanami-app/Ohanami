using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.ML.OnnxRuntime;

namespace S2Siglip2.Probe;

/// <summary>
/// ONNX Runtime での確認(README の手順1・2。判定の線は ADR 0011)を、1つのコマンドで測る。
/// <code>
/// dotnet run -c Release --project S2Siglip2.Probe -- gpu-measure [--images &lt;dir&gt;] [--out &lt;dir&gt;] [--limit N] ...
/// </code>
/// 流れ(すべて同じプロセスの中。結果は --out の report.txt に UTF-8 で):
/// <list type="number">
/// <item>環境: CUDA の DLL(cudart64_13 / cublas64_13 / cublasLt64_13)を ORT の隣に置く。cuDNN なし。nvidia-smi と ORT の版を記録</item>
/// <item>フェーズA(セッションを1つ作って壊す): (a) 作成できるか・時間 (b) Run が通るか (c) ノードの配置(CPU に落ちたノードの有無。VERBOSE ログ)。
///   VRAM を「作成前 → 推論中 → Dispose 後」の3点で測る。判定: Dispose 後が「作成前 + 600 MiB」以下ならプロセス内で動かしてよい</item>
/// <item>フェーズB(もう一度作って、画像を全部回す): 1枚ごとに「ファイル読み込み」「デコード + リサイズ + 正規化」「Run」を測り、
///   VRAM(GPU 全体と自プロセス)とワーキングセットを記録する。100枚目と最後の差が 100 MiB 以内なら合格。埋め込みは embeddings.f32 に保存する(known-item の評価用)</item>
/// </list>
/// --provider cpu を付けると、GPU を使わずに同じ流れを CPU EP で通す(測定コードの動作確認用。VRAM の監視もしない)。
/// </summary>
public static class GpuMeasure
{
    private const int DisposeLimitMiB = 600;
    private const int LoopDriftLimitMiB = 100;
    private const double MaxMsPerImageFor10kPerHour = 360.0;

    public static int Run(Args a)
    {
        string provider = a.GetOr("provider", "cuda");
        if (provider is not ("cuda" or "cpu")) throw new ArgumentException("--provider は cuda か cpu");
        bool cuda = provider == "cuda";
        string imagesDir = a.GetOr("images", Path.Combine(Paths.Root, "spikes", "images"));
        string outDir = a.Get("out") ?? Path.Combine(Paths.Root, "spikes", "S2-siglip2", "runs", $"{DateTime.Now:yyyyMMdd-HHmmss}-{provider}");
        int limit = a.GetInt("limit", int.MaxValue);
        string cudaDir = a.GetOr("cuda-dir", Path.Combine(Paths.Root, "runtime", "llama.cpp", "b12110"));
        string? cudnnDir = a.Get("cudnn-dir");
        bool allowCudnnOnPath = a.Flag("allow-cudnn-on-path");
        string arena = a.GetOr("arena", "kNextPowerOfTwo");
        long gpuMemLimitMiB = a.GetInt("gpu-mem-limit-mib", 0);
        bool strict = a.Flag("strict-cuda");
        bool skipDisposeTest = a.Flag("skip-dispose-test");
        bool keepPath = a.Flag("keep-path");
        bool prepareOnly = a.Flag("prepare-only");
        int cpuThreads = a.GetInt("threads", 4);
        int settleSeconds = a.GetInt("settle-seconds", 5);
        int gpuIndex = a.GetInt("gpu", 0);
        a.RequireAllUsed();

        Directory.CreateDirectory(outDir);
        using var report = new Report(Path.Combine(outDir, "report.txt"));
        report.Line($"# S2 ORT 測定({provider})  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.Line($"出力先: {outDir}");
        report.Line($"画像: {imagesDir}(上限 {(limit == int.MaxValue ? "なし" : limit.ToString())})");
        report.Line($".NET {Environment.Version}、OS {Environment.OSVersion}、論理コア {Environment.ProcessorCount}、プロセス {Environment.ProcessId}");
        report.Line($"ORT(managed){typeof(InferenceSession).Assembly.GetName().Version}");
        report.Line($"前処理の仕様の版: {Siglip2ImagePreprocessor.SpecVersion}");

        string visualOnnx = Paths.VerifiedModelFile("visual/model.onnx", report);
        report.Line($"モデル: visual/model.onnx {new FileInfo(visualOnnx).Length:N0} B");

        string? nativeDir = null;
        var logCapture = OrtLogCapture.Initialize(Path.Combine(outDir, "ort-verbose.log"), verbose: cuda);
        GpuMonitor? gpu = null;
        try
        {
            if (cuda)
            {
                if (keepPath) report.Line("PATH: そのまま(--keep-path)。PATH 上の CUDA Toolkit の DLL が使われる可能性がある");
                else
                {
                    var removed = CudaEnvironment.SanitizePath();
                    report.Line(removed.Count == 0 ? "PATH: CUDA / cuDNN の DLL があるディレクトリは無かった" : "PATH から外した(CUDA Toolkit の DLL に頼らないため): " + string.Join("; ", removed));
                }
                nativeDir = CudaEnvironment.Prepare(cudaDir, cudnnDir, allowCudnnOnPath, report);
                LogNvidiaSmi(report, gpuIndex);
                gpu = new GpuMonitor(gpuIndex);
                Thread.Sleep(1500);
            }
            if (prepareOnly)
            {
                if (!cuda) throw new ArgumentException("--prepare-only は --provider cuda のときだけ");
                SettleNoise(gpu!, report);
                report.Line($"(--prepare-only: 環境の用意と点検だけ。セッションは作っていない)GPU 全体の使用量 {gpu!.Current} MiB");
                report.Line("このプロセスがロードした CUDA 関連の DLL: " + string.Join(" | ", CudaEnvironment.LoadedCudaModules().Select(x => x.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])));
                return 0;
            }
            using var sampler = new ProcMemSampler();
            var settings = new Siglip2Model.CudaSettings
            {
                DeviceId = gpuIndex,
                ConvAlgoSearch = "HEURISTIC",
                ArenaExtendStrategy = arena,
                GpuMemLimitBytes = gpuMemLimitMiB * 1024 * 1024,
                IntraOpThreads = 2,
                DisableCpuFallback = strict,
                VerboseLog = true,
            };
            report.Line($"CUDA EP の設定: cudnn_conv_algo_search={settings.ConvAlgoSearch}, arena_extend_strategy={settings.ArenaExtendStrategy}, " +
                        $"gpu_mem_limit={(gpuMemLimitMiB > 0 ? gpuMemLimitMiB + " MiB" : "指定なし")}, disable_cpu_ep_fallback={(strict ? "1" : "0(既定。配置はログで確かめる)")}");

            var decoder = new SkiaImageDecoder();
            string[] files = ListImages(imagesDir).Take(limit).ToArray();
            if (files.Length == 0) throw new InvalidOperationException($"画像が無い: {imagesDir}");
            report.Line($"画像 {files.Length} 枚");
            report.Line();

            Func<Siglip2Model> open = () => cuda ? Siglip2Model.OpenVisualCuda(visualOnnx, settings) : Siglip2Model.OpenVisualCpu(visualOnnx, cpuThreads);
            var results = new List<string>();

            // ---------------- フェーズA: 作る → 推論 → Dispose
            report.Line("## フェーズA: セッションの作成 → 推論 → Dispose(同じプロセスで VRAM を3点で測る)");
            double? vramBefore = null, vramAfterDispose = null;
            (double? mib, string? why) procBefore = cuda ? GpuProcessMemory.Read(Environment.ProcessId) : (null, null);
            if (cuda)
            {
                SettleNoise(gpu!, report);
                vramBefore = gpu!.Current;
                report.Line($"[作成前] GPU 全体 {vramBefore:F0} MiB、自プロセス {Fmt(procBefore)}、{ProcMem.Now()}");
            }
            else report.Line($"[作成前] {ProcMem.Now()}");

            logCapture.ResetPlacement();
            Siglip2Model? model = null;
            var swCreate = Stopwatch.StartNew();
            gpu?.BeginWindow();
            sampler.BeginWindow();
            try
            {
                model = open();
            }
            catch (Exception e)
            {
                report.Line("(a) セッションの作成に失敗した");
                report.Line(nativeDir is not null ? CudaEnvironment.ExplainFailure(e, nativeDir) : e.ToString());
                report.Line("判定: (a) NG → cuDNN 9 の CUDA 13 版を足して再試行する(--cudnn-dir <展開した bin のディレクトリ>)");
                return 2;
            }
            double createMs = swCreate.Elapsed.TotalMilliseconds;
            var createMem = sampler.EndWindow();
            report.Line($"(a) セッションの作成: 成功。{createMs / 1000.0:F2} 秒(1.7GB のロードと CUDA の初期化を含む)");
            if (cuda)
            {
                Thread.Sleep(500);
                report.Line($"[作成後] GPU 全体 {gpu!.Current} MiB(作成中のピーク {gpu.EndWindow()} MiB)、自プロセス {Fmt(GpuProcessMemory.Read(Environment.ProcessId))}、作成中のメモリのピーク: プライベート {createMem.PrivateMiB:F0} / ワーキングセット {createMem.WorkingSetMiB:F0} MiB");
            }
            else report.Line($"[作成後] {ProcMem.Now()}(作成中のピーク: プライベート {createMem.PrivateMiB:F0} / ワーキングセット {createMem.WorkingSetMiB:F0} MiB)");

            report.Line("このプロセスがロードした CUDA 関連の DLL(実際に使われたファイル):");
            foreach (string m in CudaEnvironment.LoadedCudaModules()) report.Line("    " + m);

            // (c) ノードの配置
            var placement = ReportPlacement(report, logCapture, cuda);
            results.Add(placement.Summary);
            results.Insert(0, $"(a) セッション作成: OK({createMs / 1000.0:F2} 秒)");

            // (b) Run が通るか + 推論中のピーク
            byte[] firstBytes = File.ReadAllBytes(files[0]);
            float[] tensor0 = Siglip2ImagePreprocessor.Preprocess(firstBytes, decoder);
            gpu?.BeginWindow();
            sampler.BeginWindow();
            float[] emb0;
            var swFirst = Stopwatch.StartNew();
            try
            {
                emb0 = model.EmbedImage(tensor0);
            }
            catch (Exception e)
            {
                report.Line("(b) Run に失敗した");
                report.Line(nativeDir is not null ? CudaEnvironment.ExplainFailure(e, nativeDir) : e.ToString());
                report.Line("判定: (b) NG → cuDNN 9 の CUDA 13 版を足して再試行する(--cudnn-dir)");
                model.Dispose();
                return 3;
            }
            double firstRunMs = swFirst.Elapsed.TotalMilliseconds;
            report.Line($"(b) 1 枚目の Run: 成功。{firstRunMs:F0} ms(初回。カーネルの初期化を含む)、埋め込みのノルム {Math.Sqrt(emb0.Sum(x => (double)x * x)):F6}");
            results.Insert(1, $"(b) Run: OK(初回 {firstRunMs:F0} ms)");
            var runTimes = new List<double>();
            for (int i = 0; i < Math.Min(8, files.Length); i++)
            {
                float[] t = Siglip2ImagePreprocessor.Preprocess(File.ReadAllBytes(files[i]), decoder);
                var sw = Stopwatch.StartNew();
                model.EmbedImage(t);
                runTimes.Add(sw.Elapsed.TotalMilliseconds);
            }
            var inferMem = sampler.EndWindow();
            if (cuda)
            {
                Thread.Sleep(300);
                report.Line($"[推論中] GPU 全体のピーク {gpu!.EndWindow()} MiB(8 回の Run の窓)、自プロセス {Fmt(GpuProcessMemory.Read(Environment.ProcessId))}、メモリのピーク: プライベート {inferMem.PrivateMiB:F0} / ワーキングセット {inferMem.WorkingSetMiB:F0} MiB");
            }
            else report.Line($"[推論中] メモリのピーク: プライベート {inferMem.PrivateMiB:F0} / ワーキングセット {inferMem.WorkingSetMiB:F0} MiB");
            report.Line($"  Run(ウォームアップ後の {runTimes.Count} 回): 中央値 {Median(runTimes):F0} ms");

            // Dispose
            if (!skipDisposeTest)
            {
                var swDispose = Stopwatch.StartNew();
                model.Dispose();
                model = null;
                report.Line($"InferenceSession を Dispose した({swDispose.Elapsed.TotalMilliseconds:F0} ms)");
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var checkpoints = new List<string>();
                for (int s = 1; s <= settleSeconds; s++)
                {
                    Thread.Sleep(1000);
                    if (cuda && (s == 1 || s == 3 || s == settleSeconds)) checkpoints.Add($"+{s}秒 GPU 全体 {gpu!.Current} MiB");
                }
                if (cuda)
                {
                    vramAfterDispose = gpu!.Current;
                    var procAfter = GpuProcessMemory.Read(Environment.ProcessId);
                    report.Line($"[Dispose 後] {string.Join("、", checkpoints)}、自プロセス {Fmt(procAfter)}、{ProcMem.Now()}");
                    double delta = vramAfterDispose.Value - vramBefore!.Value;
                    string verdict = delta <= DisposeLimitMiB ? "合格(プロセス内で動かしてよい。CUDA コンテキストの分は戻らない)" : "不合格(ADR 0011 の「別プロセスのワーカーにする」案に切り替える)";
                    report.Line($"判定(Dispose): 作成前 {vramBefore:F0} → Dispose 後 {vramAfterDispose:F0} MiB(差 {delta:+0;-0;0} MiB、基準 ≤ +{DisposeLimitMiB} MiB)→ {verdict}");
                    if (procBefore.mib.HasValue && procAfter.MiB.HasValue)
                    {
                        report.Line($"  自プロセスの専用 VRAM: 作成前 {procBefore.mib:F0} → Dispose 後 {procAfter.MiB:F0} MiB(差 {procAfter.MiB - procBefore.mib:+0;-0;0})");
                    }
                    results.Add($"Dispose: {verdict.Split('(')[0]}(差 {delta:+0;-0;0} MiB)");
                }
                else report.Line($"[Dispose 後] {ProcMem.Now()}");
            }
            else
            {
                model.Dispose();
                model = null;
                report.Line("(Dispose のテストは --skip-dispose-test で省略した)");
            }
            report.Line();

            // ---------------- フェーズB: もう一度作って、全部回す
            report.Line($"## フェーズB: セッションを作り直して、{files.Length} 枚を順に回す");
            logCapture.ResetPlacement();
            var swCreate2 = Stopwatch.StartNew();
            try
            {
                model = open();
            }
            catch (Exception e)
            {
                report.Line("フェーズBのセッションの作成に失敗した");
                report.Line(nativeDir is not null ? CudaEnvironment.ExplainFailure(e, nativeDir) : e.ToString());
                return 4;
            }
            double create2Ms = swCreate2.Elapsed.TotalMilliseconds;
            report.Line($"セッションの再作成: {create2Ms / 1000.0:F2} 秒(1回目 {createMs / 1000.0:F2} 秒)");
            results.Add($"セッション作成の時間: 1回目 {createMs / 1000.0:F2} 秒、2回目 {create2Ms / 1000.0:F2} 秒");

            var embeddings = new List<float[]>(files.Length);
            var names = new List<string>(files.Length);
            var rows = new StringBuilder("index\tfilename\tread_ms\tprep_ms\trun_ms\tvram_gpu_mib\tprivate_mib\tworking_set_mib\n");
            var readMs = new List<double>();
            var prepMs = new List<double>();
            var runMs = new List<double>();
            int errors = 0;
            var checkpointsAt = new SortedSet<int> { 1, 10, 50, 100 };
            for (int k = 200; k < files.Length; k += 100) checkpointsAt.Add(k);
            checkpointsAt.Add(files.Length);
            var cpRows = new Dictionary<int, (double? Gpu, double? Proc, double Priv, double Ws)>();
            var loopSw = Stopwatch.StartNew();
            gpu?.BeginWindow();
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);
                var sw = Stopwatch.StartNew();
                byte[] bytes;
                float[] tensor;
                try
                {
                    bytes = File.ReadAllBytes(files[i]);
                    double rd = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    tensor = Siglip2ImagePreprocessor.Preprocess(bytes, decoder);
                    double prep = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    float[] emb = model.EmbedImage(tensor);
                    double run = sw.Elapsed.TotalMilliseconds;
                    readMs.Add(rd);
                    prepMs.Add(prep);
                    runMs.Add(run);
                    embeddings.Add(emb);
                    names.Add(name);
                    var mem = ProcMem.Now();
                    int vr = cuda ? gpu!.Current : -1;
                    rows.Append(CultureInfo.InvariantCulture, $"{i}\t{name}\t{rd:F2}\t{prep:F2}\t{run:F2}\t{(cuda ? vr.ToString() : "")}\t{mem.PrivateMiB:F0}\t{mem.WorkingSetMiB:F0}\n");
                }
                catch (Exception e) when (e is InvalidDataException or IOException)
                {
                    errors++;
                    report.Line($"  画像 {name} を処理できなかった(スキップして続ける): {e.GetType().Name}: {e.Message}");
                }
                if (checkpointsAt.Contains(i + 1) && embeddings.Count > 0)
                {
                    var mem = ProcMem.Now();
                    double? g = cuda ? gpu!.Current : null;
                    double? proc = cuda ? GpuProcessMemory.Read(Environment.ProcessId).MiB : null;
                    cpRows[i + 1] = (g, proc, mem.PrivateMiB, mem.WorkingSetMiB);
                    int peak = cuda ? gpu!.EndWindow() : -1;
                    report.Line($"  {i + 1,5} 枚目: " + (cuda ? $"GPU 全体 {g:F0} MiB(直前からのピーク {peak} MiB)、自プロセス {(proc.HasValue ? proc.Value.ToString("F0") : "取得不可")} MiB、" : "")
                                + $"プライベート {mem.PrivateMiB:F0} MiB、ワーキングセット {mem.WorkingSetMiB:F0} MiB、経過 {loopSw.Elapsed.TotalSeconds:F0} 秒");
                    gpu?.BeginWindow();
                }
            }
            double loopSec = loopSw.Elapsed.TotalSeconds;
            int n = embeddings.Count;
            report.Line($"完了: {n} 枚(スキップ {errors})、{loopSec:F1} 秒、1枚あたり平均 {loopSec * 1000 / Math.Max(1, n):F0} ms");
            report.Line($"1枚の時間(ms): ファイル読み込み 中央値 {Median(readMs):F1} / 最大 {readMs.Max():F1}、デコード + リサイズ + 正規化 中央値 {Median(prepMs):F1} / 最大 {prepMs.Max():F1}、Run 中央値 {Median(runMs):F1} / p95 {Percentile(runMs, 95):F1} / 最大 {runMs.Max():F1}");
            double perImage = Median(readMs) + Median(prepMs) + Median(runMs);
            string limiter = Median(runMs) >= Median(prepMs) + Median(readMs) ? "Run(推論)が律速" : "デコード + リサイズ(CPU)が律速";
            report.Line($"  → 逐次処理の1枚 {perImage:F0} ms(足切り: 1万枚が1時間以内 = 1枚 {MaxMsPerImageFor10kPerHour:F0} ms 以下)、{limiter}。1万枚の見積もり {perImage * 10000 / 3600000.0:F2} 時間");
            results.Add($"1枚の時間: 読み込み {Median(readMs):F1} + デコード・リサイズ {Median(prepMs):F1} + Run {Median(runMs):F1} = {perImage:F0} ms(1万枚 {perImage * 10000 / 3600000.0:F2} 時間。{(perImage <= MaxMsPerImageFor10kPerHour ? "足切りに合格" : "足切りを超えた")}、{limiter})");

            // 100枚目と最後の比較
            int refAt = cpRows.Keys.Where(k => k >= 100).DefaultIfEmpty(cpRows.Keys.Min()).Min();
            int lastAt = cpRows.Keys.Max();
            if (refAt != lastAt)
            {
                var r0 = cpRows[refAt];
                var r1 = cpRows[lastAt];
                double dWs = r1.Ws - r0.Ws;
                string vramPart = "";
                bool vramOk = true;
                if (cuda)
                {
                    double? dGpu = r1.Gpu - r0.Gpu;
                    double? dProc = r1.Proc.HasValue && r0.Proc.HasValue ? r1.Proc - r0.Proc : null;
                    double basis = dProc ?? dGpu ?? 0;
                    vramOk = Math.Abs(basis) <= LoopDriftLimitMiB;
                    vramPart = $"VRAM(GPU 全体)差 {dGpu:+0;-0;0} MiB、VRAM(自プロセス)差 {(dProc.HasValue ? dProc.Value.ToString("+0;-0;0") : "取得不可")} MiB(判定には{(dProc.HasValue ? "自プロセス" : "GPU 全体")}の値を使う)、";
                }
                bool wsOk = Math.Abs(dWs) <= LoopDriftLimitMiB;
                string v = vramOk && wsOk ? "合格" : "不合格(arena_extend_strategy=kSameAsRequested と gpu_mem_limit で抑えられるか見る)";
                if (!cuda) v = "(CPU の動作確認なので参考値)" + v;
                report.Line($"判定(ループ): {refAt} 枚目 → {lastAt} 枚目: {vramPart}ワーキングセット差 {dWs:+0;-0;0} MiB(基準 ±{LoopDriftLimitMiB} MiB)→ {v}");
                results.Add($"ループ({refAt} → {lastAt} 枚目): {v.Split('(')[0]}");
            }
            else report.Line("(100 枚目と最後の比較は、枚数が足りないので省略)");

            File.WriteAllText(Path.Combine(outDir, "loop.tsv"), rows.ToString(), new UTF8Encoding(false));
            EmbeddingStore.Write(outDir, names, embeddings);
            report.Line($"保存: loop.tsv(1枚ごとの時間とメモリ)、embeddings.f32 と embedding-names.tsv({n} 枚。known-item の評価用)、ort-verbose.log({logCapture.LineCount} 行)");
            _ = ReportPlacement(report, logCapture, cuda, quiet: true);
            report.Line("ループ後にロードされていた CUDA / cuDNN 関連の DLL(実際に必要だったものの一覧。cuDNN は最初の Run で読まれる):");
            foreach (string m in CudaEnvironment.LoadedCudaModules().Where(x => x.StartsWith("cu", StringComparison.OrdinalIgnoreCase) || x.StartsWith("nvrtc", StringComparison.OrdinalIgnoreCase))) report.Line("    " + m);
            model.Dispose();

            report.Line();
            report.Line("## まとめ(判定の規則。ADR 0011 の表)");
            foreach (string r in results) report.Line("- " + r);
            report.Line(!cuda ? "- (CPU の動作確認なので、CUDA の判定はしていない)"
                : cudnnDir is not null ? "- cuDNN: 今回は cuDNN あり構成で測った(cuDNN の要否は、--cudnn-dir なしの実行で (a)(b)(c) が通るかで決める)"
                : "- cuDNN: cuDNN なしで (a)(b)(c) がすべて通ったので、cuDNN は同梱しない(約 600MB 減る)。(c) だけ NG なら cuDNN ありと速さを比べて決める");
            return 0;
        }
        catch (Exception e)
        {
            report.Line("測定が例外で止まった:");
            report.Line(nativeDir is not null ? CudaEnvironment.ExplainFailure(e, nativeDir) : e.ToString());
            throw;
        }
        finally
        {
            gpu?.Dispose();
            logCapture.Dispose();
        }
    }

    private static (bool AllOnCuda, string Summary) ReportPlacement(Report report, OrtLogCapture cap, bool cuda, bool quiet = false)
    {
        var (found, by) = cap.Placement();
        if (!quiet)
        {
            report.Line("(c) ノードの配置(VERBOSE ログより):");
            foreach (string l in cap.PlacementLines.Take(40)) report.Line("    " + l);
            if (cap.PlacementLines.Count > 40) report.Line($"    … ほか {cap.PlacementLines.Count - 40} 行(ort-verbose.log に全部ある)");
        }
        if (!cuda) return (false, "(c) ノードの配置: CPU の動作確認なので判定しない");
        if (!found) return (false, "(c) ノードの配置: 判定できない(ログに配置の行が無い。ort-verbose.log を見る)");
        int cudaNodes = by.GetValueOrDefault("CUDAExecutionProvider");
        int cpuNodes = by.GetValueOrDefault("CPUExecutionProvider");
        string detail = string.Join("、", by.Select(kv => $"{kv.Key} {kv.Value} ノード"));
        if (cpuNodes == 0 && cudaNodes > 0)
        {
            if (!quiet) report.Line($"(c) 判定: OK — 全ノードが CUDA EP({detail})");
            return (true, $"(c) 全ノードが CUDA EP: OK({detail})");
        }
        if (!quiet) report.Line($"(c) 判定: NG — CPU に落ちたノードがある({detail})。ort-verbose.log で該当ノードを確かめ、cuDNN ありと速さを比べて決める");
        return (false, $"(c) CPU に落ちたノードあり: NG({detail})");
    }

    private static string Fmt((double? MiB, string? Why) v) => v.MiB.HasValue ? $"{v.MiB.Value:F0} MiB" : $"取得不可({v.Why})";

    private static void SettleNoise(GpuMonitor gpu, Report report)
    {
        // 他のプロセスの変動(GPU 全体の値のゆれ)を、2秒間の最小・最大で記録する
        int min = int.MaxValue, max = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            int c = gpu.Current;
            min = Math.Min(min, c);
            max = Math.Max(max, c);
            Thread.Sleep(100);
        }
        report.Line($"[作成前のゆらぎ] GPU 全体の使用量 2 秒間の最小 {min} / 最大 {max} MiB(他のプロセスの変動。S1 の測定などが動いていると大きい)");
        if (max - min > 100) report.Line("  ※ ゆらぎが 100 MiB を超えている。GPU を使う他の作業が動いていないか確認すること(測定が乱れる)");
    }

    private static void LogNvidiaSmi(Report report, int gpuIndex)
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", $"-i {gpuIndex} --query-gpu=name,driver_version,memory.total,memory.used,utilization.gpu --format=csv,noheader")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);
            report.Line($"nvidia-smi: {o}");
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("nvidia-smi を実行できない(GPU の測定には必須): " + e.Message, e);
        }
    }

    public static string[] ListImages(string dir)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"画像のディレクトリが無い: {dir}");
        return Directory.GetFiles(dir)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToArray();
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return double.NaN;
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private static double Percentile(List<double> v, double p)
    {
        if (v.Count == 0) return double.NaN;
        var s = v.OrderBy(x => x).ToList();
        return s[Math.Min(s.Count - 1, (int)Math.Ceiling(p / 100.0 * s.Count) - 1)];
    }
}
