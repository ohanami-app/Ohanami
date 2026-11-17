using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace S2Siglip2.Probe;

/// <summary>
/// テキスト側を CPU EP で動かしたときの RAM(プライベートバイトとワーキングセット)を、段階ごとに測る。
/// <code>
/// dotnet run -c Release --project S2Siglip2.Probe -- ram-text [--threads 4] [--runs 40] [--out &lt;dir&gt;]
/// </code>
/// 新しいプロセスで、(1)起動直後 (2)tokenizer.json の読み込み後 (3)セッション作成前 (4)セッション作成後(作成中のピークも)
/// (5)Run 中(ピーク)(6)Run 後と GC 後、を記録する。外部データ(fp32 の重み 2.8GB)はメモリマップされるかもしれないので、
/// プライベートバイトとワーキングセットの両方を見る(マップされたファイルはワーキングセットに出て、プライベートには出ない)。
/// </summary>
public static class RamText
{
    public static int Run(Args a)
    {
        int threads = a.GetInt("threads", 4);
        int runs = a.GetInt("runs", 40);
        string outDir = a.Get("out") ?? Path.Combine(Paths.Root, "spikes", "S2-siglip2", "runs", $"{DateTime.Now:yyyyMMdd-HHmmss}-ram-text");
        a.RequireAllUsed();

        using var report = new Report(Path.Combine(outDir, "report.txt"));
        using var sampler = new ProcMemSampler(50);
        report.Line($"# テキスト側(CPU EP、{threads} スレッド)の RAM  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.Line($".NET {Environment.Version}、プロセス {Environment.ProcessId}、論理コア {Environment.ProcessorCount}");

        void Mark(string label) => report.Line($"{label,-40} {ProcMem.Now()}");

        Mark("(1) 起動直後");
        string onnx = Paths.VerifiedModelFile("textual/model.onnx", report);
        string tokJson = Paths.VerifiedModelFile("textual/tokenizer.json", report);
        // 外部データの断片(330 個)も照合する(model.onnx のスタブだけでは本物と言えない)
        int fragments = 0;
        foreach (string f in Directory.GetFiles(Path.GetDirectoryName(onnx)!))
        {
            string n = Path.GetFileName(f);
            if (Paths.IsTextualExternalData(n))
            {
                Paths.VerifiedModelFile("textual/" + n);
                fragments++;
            }
        }
        report.Line($"  外部データの断片 {fragments} 個も MANIFEST.tsv と照合済み");
        Mark("(1b) モデルのハッシュ照合の後");

        sampler.BeginWindow();
        var tok = Siglip2Tokenizer.Load(tokJson);
        var tokMem = sampler.EndWindow();
        Mark("(2) tokenizer.json の読み込み後");
        report.Line($"  読み込み中のピーク: プライベート {tokMem.PrivateMiB:F0} MiB / ワーキングセット {tokMem.WorkingSetMiB:F0} MiB");

        GC.Collect();
        Thread.Sleep(500);
        Mark("(3) セッション作成前(GC 後)");

        sampler.BeginWindow();
        var swCreate = System.Diagnostics.Stopwatch.StartNew();
        using Siglip2Model model = Siglip2Model.OpenTextualCpu(onnx, threads);
        double createSec = swCreate.Elapsed.TotalSeconds;
        var createMem = sampler.EndWindow();
        Thread.Sleep(500);
        Mark("(4) セッション作成後");
        report.Line($"  作成にかかった時間 {createSec:F2} 秒。作成中のピーク: プライベート {createMem.PrivateMiB:F0} MiB / ワーキングセット {createMem.WorkingSetMiB:F0} MiB");

        // 実際のクエリ(トークナイザの正解コーパスから)で Run する
        var queries = File.ReadLines(Paths.Reference("data", "tokenizer-corpus.tsv")).Skip(1)
            .Select(l => l.Split('\t')).Where(c => c[1] is "ja_short" or "mixed" or "en")
            .Select(c => JsonSerializer.Deserialize<string>(c[2])!).Take(runs).ToList();
        sampler.BeginWindow();
        var times = new List<double>();
        for (int i = 0; i < queries.Count; i++)
        {
            int[] ids = tok.Encode(queries[i]);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            model.EmbedText(ids);
            times.Add(sw.Elapsed.TotalMilliseconds);
            if (i == 0) Mark("(5a) 最初の Run の直後");
        }
        var runMem = sampler.EndWindow();
        Mark($"(5) {queries.Count} 回の Run の後");
        report.Line($"  Run 中のピーク: プライベート {runMem.PrivateMiB:F0} MiB / ワーキングセット {runMem.WorkingSetMiB:F0} MiB");
        var sorted = times.OrderBy(x => x).ToList();
        report.Line($"  Run の時間: 初回 {times[0]:F0} ms、2回目以降の中央値 {times.Skip(1).OrderBy(x => x).ElementAt((times.Count - 1) / 2):F0} ms、最大 {sorted.Last():F0} ms");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Thread.Sleep(2000);
        Mark("(6) GC と 2 秒待った後");

        var p = ProcMem.Now();
        report.Line();
        report.Line("## まとめ");
        report.Line($"- fp32 の重み(textual の外部データ + model.onnx)の合計: {Directory.GetFiles(Path.GetDirectoryName(onnx)!).Where(f => !f.EndsWith(".json")).Sum(f => new FileInfo(f).Length) / 1048576.0:F0} MiB");
        report.Line($"- 常駐(Run 後): プライベート {p.PrivateMiB:F0} MiB、ワーキングセット {p.WorkingSetMiB:F0} MiB(プロセス全体。.NET のランタイムと tokenizer の語彙を含む)");
        report.Line("- 解釈: プライベートが重みの大きさに近ければ ORT が重みをコピーして持っている。ワーキングセットだけが大きければ、ファイルがマップされている(解放されうる共有ページ)");
        return 0;
    }
}
