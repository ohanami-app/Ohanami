using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ohanami.Bench;

/// <summary>
/// 検索の回帰ベンチの入り口。使い方は <see cref="Usage"/>。
/// 使うのは「変更が precision@10 と known-item を下げないことの確認」だけ(数字を上げるために回すと、偏った小さなクエリに過適合する)。
/// 終了コード: 0 = 正常、1 = 使い方の誤り・入力の誤り・実行の失敗、3 = 比較で基準線より下がった集計がある。
/// </summary>
internal static class Program
{
    private const string Usage = """
        Ohanami.Bench: 検索の回帰ベンチ(手元専用。私物の画像と qrels に依存する)
        S1 = VLM の評価(spikes/S1-vlm)、S2 = 検索の評価の記録(spikes/S2-siglip2)。コマンドの名前の import-s1・check-s2 の S1・S2 はこれ。

          run      [--images <dir>] [--models <dir>] [--root <dir>] [--spikes <dir>] [--label <name>]
                   画像のフォルダを走査・埋め込み(済みは再利用)し、known-item と precision@10 を測って、<root>/runs/<時刻>-<label>/ に保存する。
                   基準線があれば、続けて比較する。
          third-path --embeddings <dir> [--diagnostic-embeddings <dir>] [--classes <tsv>] [--images <dir>] [--models <dir>] [--root <dir>] [--spikes <dir>] [--label <name>]
                   3 本目の経路(VLM の説明文のテキスト埋め込み)の試し測り(ADR 0035)。Python が書いた埋め込みのファイル(vectors.f32・index.tsv・meta.json。私物の説明文から作ったもの。
                   リポジトリの外)を読み、製品の検索の結果に 3 本目(cosine の降順)を足して、今の 2 本と同じ RRF(k = 60、重みなし)で融合した fused3 を、run と同じ形で <root>/runs/<時刻>-<label>/ に保存する。
                   先に、同じ RankedPath.Rrf で今の 2 本を融合して、製品の fused と全クエリで一致することを確かめる(一致しなければ止まる)。--embeddings は主(説明文 + タグ)、--diagnostic-embeddings は診断(説明文だけ。known-item の順位だけ)。--classes は、今の外れの分類の割り当て(set・query・filename・class の TSV か、set・index・fused_rank_at_classification・class・doubtful の TSV。後者は集合の中の番号で指し、今の融合の順位と一致しなければ止まる。
                   測る前に固定したもの)。出力は数字だけの要約(known-item ごとの順位の表、U・H3・G・L、precision@10 の Δ と 3 層・対の標準誤差)と、クエリ・ファイル名つきの third-path-known-items.tsv。判定・採否はしない。
          hubness-mean [--reference <参照の語の一覧>] [--models <dir>] [--out <生成する .g.cs>]
                   特徴ベクトル検索の補正(ADR 0051)の μ を作る。参照の語の一覧(既定 = tools/Ohanami.Bench/hubness-reference-ja.txt)の各語を、製品と同じテキスト埋め込み(CPU)でベクトルにして平均を取り、
                   Core の生成ソース(既定 = src/Ohanami.Core/Embedding/Generated/HubnessReferenceMean.g.cs)に、model_id・一覧の SHA-256・語数と一緒に書く。DB・GPU・画像は使わない。
          import-s1 --s1-run <VLM の評価の run のフォルダ> [--images <dir>] [--models <dir>] [--root <dir>] [--spikes <dir>]
                   評価の VLM の出力(1030 枚)を、製品の captions と全文検索の索引に取り込む(VLM を回さずに全文検索・融合を測るため)。
                   取り込む前に、製品の VLM の構成が VLM の評価の run と同じことを確かめ、違えば止まる。何回実行しても同じ結果。
          check-s2 --s2 <評価の記録の runs のフォルダ> [<run のフォルダ>] [--root <dir>]
                   run の結果(全文検索・融合)を、評価の記録の数字と突き合わせる(合格線は SearchEvalCheck に書いてある)。省略すると直近の run。外れたら終了コード 4。
          judge    [--images <dir>] [--root <dir>] [--spikes <dir>] [--path <経路>] [<run のフォルダ>]
                   run の上位 10 件(融合・各経路)のうち、qrels に無い画像だけを並べた判定ページ(単体の HTML)を <root>/judge/ に作る。省略すると直近の run。
                   --path = その経路(例: fused3)の上位 10 件だけから集める(ほかの経路にしか出ない画像は入れない)。run に無い経路なら止まる。
                   ページでチェックを付けると、qrels の形の TSV が下に出る。それを spikes/S2-siglip2/qrels.tsv の末尾に貼り足す。
          vlm-check --s1-run <VLM の評価の run のフォルダ> [--images <dir>] [--models <dir>] [--llama <llama-server.exe>] [--root <dir>] [--samples <tsv>]
                   [--random <枚数>] [--seed <数>] [--image-max-tokens <数>] [--stage compare,tokens,big,formats]
                   製品の LlamaServer + VlmClient で実物を確かめる(GPU を使う。VRAM 約 6GB)。compare = 評価の samples 33 枚 + 無作為の数十枚を流して、評価の ocr・description・tags と
                   一致する数を数える。tokens = 文章のトークン数と評価の画像トークンの最大。big = 4K の合成画像の時間と VRAM のピーク。formats = PNG/JPEG/WebP/アルファ/EXIF の送り方。
                   llama-server を更新するときの確認にも使う。結果は <root>/vlm-check/<時刻>-m<N>/(私物の OCR を含む。リポジトリの外)。差があれば終了コード 5、4K が足切りの外なら 6、
                   VRAM を測れなかった(llama-server のプロセスごとの性能カウンタが読めない)なら 7(GPU 全体の値には戻さない)。
          vlm-full [--images <dir>] [--models <dir>] [--llama <llama-server.exe>] [--root <dir>] [--pause-after <枚数>] [--stop-after <枚数>] [--preflight]
                   1030 枚の通し(GPU を約 3.3〜4.4 時間使う): 製品の経路([Embed, Vlm])を、利用者の実際のデータとは別の root の DB に流す。VLM の done が +N 枚(既定 100)になったら、
                   一時停止 → llama-server の終了と VRAM の解放の確認 → 再開を 1 回行う。--preflight は検証と走査だけ(GPU を使わない)。Ctrl-C で一時停止して終わる(同じコマンドで続きから)。
                   既定の --root = %LOCALAPPDATA%\Ohanami\bench-full(<root>/db/bench.db。ベンチの run もこの root を指せば、通しの DB を測れる)。
                   VRAM(D)は llama-server のプロセスごとの値(性能カウンタ GPU Process Memory の Dedicated Usage)で判定する。測れなければ D は「測れなかった」で、終了コード 7。
          gpu-process-memory
                   性能カウンタ GPU Process Memory の Dedicated Usage を 1 回読んで、プロセスごとに一覧する(GPU は使わない。vlm-full・vlm-check が使う読み取りが動くかの確認)。読めなければ終了コード 7。
          captions-check --s1-run <VLM の評価の全件の run> [--retry-dry <dir>] [--retry-rep <dir>] [--root <dir>] [--expect-vlm-runs <数>] [--ignore-times]
                   通しのあとの確認: DB の captions を、VLM の評価の run の JSON と全画像で比べる(合格の見方は結果を見る前に CaptionsCheckLogic に書いてある)。再試行の run は、省略すると VLM の評価の run の隣の
                   qwen3vl-8b-q4km-tags-retry-dry0.8 / -rep1.1。差があれば終了コード 5。
          sleep-fixture [--out <dir>] [--embed-count <数>] [--vlm-count <数>]
                   スリープ・強制終了の手動確認(ADR 0026)用の合成画像のフォルダを、場面ごとに作る(GPU は使わない。私物ではない)。
                   既定の --out = %LOCALAPPDATA%\Ohanami\sleep-check-fixture。既にあるファイルは作り直さない。
          sleep-report --scenario <場面> --folder <フィクスチャの場面のフォルダ> [--data <dir>] [--since <UTC の時刻> | --minutes <数>] [--vram-limit-mib <数>]
                   スリープ・強制終了の確認の材料(Windows のスリープの記録・App のログ・DB・プロセス・VRAM)を集めて、時系列と合否を出す(読むだけ。App が動いていてもよい)。
                   場面: sleep-vlm / sleep-load / sleep-embed / kill-app-after-kill / kill-app-final / kill-server / cutoff-stopped / cutoff-recovered。
                   既定の --data = %LOCALAPPDATA%\Ohanami\sleep-check(App を OHANAMI_DATA_DIR で起動した場所)、--minutes = 180。不合格は終了コード 6。
          stage-count --folder <フィクスチャの場面のフォルダ> [--stage embed|ocr|caption] [--data <dir>]
                   そのフォルダの画像のうち、段階が done のものの数を 1 行(数字だけ)で出す(既定の段階 = caption)。スリープ・強制終了の確認のスクリプト(tools/*-when.ps1)が「done が N 枚になったら」を待つのに使う。
                   sleep-report と同じ DB の読み方(読み取り専用。App が動いていてもよい)。既定の --data = %LOCALAPPDATA%\Ohanami\sleep-check。
          baseline [--root <dir>] [<run のフォルダ>]
                   run の結果を基準線にする(<root>/baseline/ に写す)。省略すると直近の run。
          compare  [--root <dir>] [<基準線のフォルダ> [<run のフォルダ>]]
                   基準線と run を比べて、下がったクエリを一覧にする。省略すると <root>/baseline と直近の run。

        既定: --images = 環境変数 OHANAMI_IMAGES_DIR(画像のフォルダ。読み取りだけ)、--models = 環境変数 OHANAMI_MODELS_DIR(models/ に当たるディレクトリ)、
              --llama = 環境変数 OHANAMI_RUNTIME_DIR の llama.cpp/b<ビルド>/llama-server.exe、
              --root = %LOCALAPPDATA%\Ohanami\bench(DB・サムネイル・結果。リポジトリの中は拒否)、--spikes = リポジトリの spikes/S2-siglip2。
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return args.Length == 0 ? 1 : 0; }
            (string command, Dictionary<string, string> flags, List<string> positional) = ParseArgs(args);
            string root = Path.GetFullPath(flags.GetValueOrDefault("--root") ?? DefaultRoot());
            return command switch
            {
                "run" => await RunAsync(flags, root, cts.Token),
                "third-path" => await ThirdPathAsync(flags, root, cts.Token),
                "hubness-mean" => HubnessMeanCommand(flags),
                "import-s1" => await ImportVlmEvalAsync(flags, root, cts.Token),
                "check-s2" => CheckSearchEval(flags, root, positional),
                "judge" => Judge(flags, root, positional),
                "vlm-check" => await VlmCheckAsync(flags, root, cts.Token),
                "vlm-full" => await VlmFullAsync(flags, cts.Token),
                "captions-check" => CaptionsCheck(flags),
                "gpu-process-memory" => GpuProcessMemory(),
                "sleep-fixture" => SleepFixtureCommand(flags),
                "sleep-report" => SleepReport(flags),
                "stage-count" => StageCount(flags),
                "baseline" => SetBaseline(root, positional),
                "compare" => Compare(root, positional),
                _ => Fail($"知らないコマンド: {command}"),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("中断した。");
            return 1;
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or ArgumentException or TimeoutException)
        {
            Console.Error.WriteLine("失敗: " + e.Message);
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 1;
    }

    private static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ohanami", "bench");

    private static (string Command, Dictionary<string, string> Flags, List<string> Positional) ParseArgs(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();
        string[] known = ["--images", "--models", "--root", "--spikes", "--label", "--s1-run", "--s2", "--llama", "--samples", "--random", "--seed", "--image-max-tokens", "--stage",
            "--pause-after", "--stop-after", "--retry-dry", "--retry-rep", "--expect-vlm-runs",
            "--out", "--embed-count", "--vlm-count", "--scenario", "--folder", "--data", "--since", "--minutes", "--vram-limit-mib",
            "--embeddings", "--diagnostic-embeddings", "--classes", "--path"];
        string[] switches = ["--preflight", "--ignore-times"];   // 値を取らない印
        for (int i = 1; i < args.Length; i++)
        {
            if (switches.Contains(args[i]))
            {
                flags[args[i]] = "true";
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (!known.Contains(args[i])) throw new ArgumentException($"知らないオプション: {args[i]}");
                if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} の値が無い");
                flags[args[i]] = args[++i];
            }
            else positional.Add(args[i]);
        }
        return (args[0], flags, positional);
    }

    // ------------------------------------------------------------------ hubness-mean

    private static int HubnessMeanCommand(Dictionary<string, string> flags)
    {
        string models = Required(flags, "--models", "OHANAMI_MODELS_DIR", "models/ に当たるディレクトリ");
        string referencePath = Path.GetFullPath(flags.GetValueOrDefault("--reference") ?? HubnessMean.FindInRepository(Path.Combine("tools", "Ohanami.Bench", HubnessMean.ReferenceFileName)));
        string outPath = Path.GetFullPath(flags.GetValueOrDefault("--out") ?? HubnessMean.FindInRepository(HubnessMean.GeneratedRelativePath));
        (IReadOnlyList<string> words, string sha) = HubnessMean.ReadReference(referencePath);
        float[] mean = HubnessMean.ComputeMean(models, words, Console.WriteLine);
        string modelId = Ohanami.Core.Models.ModelCatalog.Current.ModelId;
        File.WriteAllText(outPath, HubnessMean.RenderSource(modelId, sha, words.Count, mean), new System.Text.UTF8Encoding(false));
        Console.WriteLine($"μ を書いた: {outPath}");
        Console.WriteLine($"  参照 {words.Count} 語、一覧の SHA-256 {sha}、model_id {modelId}、次元 {mean.Length}");
        return 0;
    }

    // ------------------------------------------------------------------ run

    private static async Task<int> RunAsync(Dictionary<string, string> flags, string root, CancellationToken ct)
    {
        string images = Required(flags, "--images", "OHANAMI_IMAGES_DIR", "画像のフォルダ");
        string models = Required(flags, "--models", "OHANAMI_MODELS_DIR", "models/ に当たるディレクトリ");
        string spikes = flags.GetValueOrDefault("--spikes") ?? FindSpikesDirectory();
        string label = flags.GetValueOrDefault("--label") ?? "run";
        if (label.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))) throw new ArgumentException("--label は英数字と - _ だけ");
        RefuseInsideRepository(root);

        var options = new RunOptions(Path.GetFullPath(images), Path.GetFullPath(models), root, Path.GetFullPath(spikes), label);
        (RunInfo info, RunData data, IReadOnlyList<string> pathNames, int precisionQueries) = await BenchRunner.RunAsync(options, ct);

        string summary = Report.Summary(info, data, pathNames, precisionQueries);
        string runDir = Path.Combine(root, "runs", info.At.ToLocalTime().ToString("yyyyMMdd-HHmmss") + "-" + label);
        RunFiles.Write(runDir, data, summary);
        Console.WriteLine();
        Console.WriteLine(summary);
        Console.WriteLine("保存: " + runDir);

        string baselineDir = Path.Combine(root, "baseline");
        if (!Directory.Exists(baselineDir))
        {
            Console.WriteLine("基準線はまだ無い。これを基準線にするなら: Ohanami.Bench baseline");
            return 0;
        }
        Console.WriteLine();
        return PrintComparison(baselineDir, runDir);
    }

    // ------------------------------------------------------------------ third-path

    private static async Task<int> ThirdPathAsync(Dictionary<string, string> flags, string root, CancellationToken ct)
    {
        string images = Required(flags, "--images", "OHANAMI_IMAGES_DIR", "画像のフォルダ");
        string models = Required(flags, "--models", "OHANAMI_MODELS_DIR", "models/ に当たるディレクトリ");
        string spikes = flags.GetValueOrDefault("--spikes") ?? FindSpikesDirectory();
        string label = flags.GetValueOrDefault("--label") ?? "third-path";
        if (label.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))) throw new ArgumentException("--label は英数字と - _ だけ");
        string embeddings = Path.GetFullPath(flags.GetValueOrDefault("--embeddings") ?? Path.Combine(root, "third-path"));
        string? diagnosticPath = flags.TryGetValue("--diagnostic-embeddings", out string? dp) ? Path.GetFullPath(dp) : null;
        string? classesPath = flags.TryGetValue("--classes", out string? cp) ? Path.GetFullPath(cp) : null;
        RefuseInsideRepository(root);
        RefuseInsideRepository(embeddings);   // 私物の説明文から作ったもの
        if (diagnosticPath is not null) RefuseInsideRepository(diagnosticPath);
        if (classesPath is not null) RefuseInsideRepository(classesPath);   // 今の外れの分類(私物のファイル名を含む)

        // 入力は GPU・DB を開く前に読んで、形式の誤りを先に見つける
        ThirdVectors vectors = ThirdVectors.Read(embeddings);
        ThirdVectors? diagnostic = diagnosticPath is null ? null : ThirdVectors.Read(diagnosticPath);
        IReadOnlyList<ThirdClassRow>? classes = null;
        IReadOnlyList<ThirdClassIndexRow>? classesByIndex = null;
        List<string> notes = [];
        if (classesPath is not null)
        {
            (classes, classesByIndex) = ThirdPathReport.ReadClassAssignments(classesPath);
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(classesPath)));
            notes.Add($"分類の割り当て: {Path.GetFileName(classesPath)}(SHA-256 {sha}、{(classesByIndex is null ? "query・filename の形" : "集合 + 番号の形")}、{(classes?.Count ?? classesByIndex!.Count)} 行)");
        }
        Console.WriteLine($"3 本目の埋め込み(主。入れ方 {vectors.Variant ?? "不明"}): モデル {vectors.Model}、次元 {vectors.Dim}、画像 {vectors.Docs.Count} 枚、クエリ {vectors.Queries.Count} 件(説明文が空の画像は含まない)");
        if (diagnostic is not null) Console.WriteLine($"3 本目の埋め込み(診断。入れ方 {diagnostic.Variant ?? "不明"}): モデル {diagnostic.Model}、次元 {diagnostic.Dim}、画像 {diagnostic.Docs.Count} 枚、クエリ {diagnostic.Queries.Count} 件");
        if (diagnostic is not null && vectors.Variant is not null && diagnostic.Variant is not null && vectors.Variant == diagnostic.Variant)
            throw new ArgumentException($"主と診断の埋め込みが同じ入れ方({vectors.Variant})。--embeddings と --diagnostic-embeddings を取り違えていないか");

        var options = new RunOptions(Path.GetFullPath(images), Path.GetFullPath(models), root, Path.GetFullPath(spikes), label);
        var stages = new StageClock();
        BenchRunner.Collected c = await BenchRunner.CollectAsync(options, stages, ct);
        RunData data = ThirdPath.Build(c, vectors, vectors.Model, out int reproduced, out int thirdPopulation, diagnostic);
        ThirdPathStats stats = ThirdPathReport.Compute(data, c.Inputs.Qrels, c.Inputs.Items, classes, classesByIndex);
        stages.Mark("3 本目の融合と集計");

        RunInfo info = BenchRunner.MakeInfo(options, c, stages);
        List<string> pathNames = [Metrics.Fused, "visual", "fulltext", ThirdPath.PathId, ThirdPath.Fused3];   // 診断の経路(third-desc・fused3-desc)は known-item の行だけで、要約の本体の表には出さない(下の診断の節)
        string summary = Report.Summary(info, data, pathNames, c.Inputs.Qrels.Queries.Count())
            + Environment.NewLine + ThirdPathReport.Render(stats, vectors.Model, vectors.Dim, thirdPopulation, c.TotalImages, reproduced, notes, vectors.Variant,
                diagnostic is null ? null : diagnostic.Docs.Count);
        string runDir = Path.Combine(root, "runs", info.At.ToLocalTime().ToString("yyyyMMdd-HHmmss") + "-" + label);
        RunFiles.Write(runDir, data, summary);
        File.WriteAllText(Path.Combine(runDir, "third-path-known-items.tsv"), ThirdPathReport.RenderEntriesTsv(stats), new System.Text.UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine(summary);
        Console.WriteLine("保存: " + runDir);
        return 0;
    }

    // ------------------------------------------------------------------ import-s1 / check-s2

    private static async Task<int> ImportVlmEvalAsync(Dictionary<string, string> flags, string root, CancellationToken ct)
    {
        string evalRunDir = Required(flags, "--s1-run", "OHANAMI_S1_RUN", "VLM の評価の run のフォルダ");
        string images = Required(flags, "--images", "OHANAMI_IMAGES_DIR", "画像のフォルダ");
        string models = Required(flags, "--models", "OHANAMI_MODELS_DIR", "models/ に当たるディレクトリ");
        string spikes = flags.GetValueOrDefault("--spikes") ?? FindSpikesDirectory();
        RefuseInsideRepository(root);
        var run = new RunOptions(Path.GetFullPath(images), Path.GetFullPath(models), root, Path.GetFullPath(spikes), "import-s1");
        return await VlmEvalImporter.RunAsync(new ImportOptions(Path.GetFullPath(evalRunDir), run), ct);
    }

    private static int CheckSearchEval(Dictionary<string, string> flags, string root, List<string> positional)
    {
        string evalRunsDir = Path.GetFullPath(Required(flags, "--s2", "OHANAMI_S2_RUNS", "評価の記録の runs のフォルダ(spikes/S2-siglip2/runs)"));
        string run = positional.Count > 0 ? Path.GetFullPath(positional[0]) : LatestRun(root);
        RunData data = RunFiles.Read(run);
        string known = Path.Combine(evalRunsDir, "stage3-known-only-1030", "stage3-known-items.tsv");
        string dialogue = Path.Combine(evalRunsDir, "dialogue-stage3", "stage3-known-items.tsv");
        string precision = Path.Combine(evalRunsDir, "stage3-1030-judged", "stage3-precision-by-query.tsv");
        SearchEvalCheck.Report report = SearchEvalCheck.Evaluate(data, SearchEvalCheck.ReadKnownItems(known), SearchEvalCheck.ReadKnownItems(dialogue), SearchEvalCheck.ReadPrecisionByQuery(precision));
        Console.WriteLine($"記録の数字との突き合わせ: run = {run}、記録 = {evalRunsDir}");
        foreach (CheckResult c in report.Criteria) Console.WriteLine($"  [{(c.Passed ? "合格" : "**不合格**")}] {c.Name}: {c.Detail}");
        if (report.Details.Length > 0)
        {
            Console.WriteLine("差のあった行:");
            Console.Write(report.Details);
        }
        Console.WriteLine(report.AllPassed ? "判定: すべての合格線を通った(記録の数字を、製品の検索で再現できた)。" : "判定: 通らない合格線がある。SearchPlan → SQL → 融合 → 索引文書の組み立ての順に、差のあるクエリから原因を調べる。");
        return report.AllPassed ? 0 : 4;
    }

    /// <summary>通しの既定の root(ベンチの root とは別。通しの DB をベンチの DB と混ぜない)。</summary>
    private static string DefaultFullRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ohanami", "bench-full");

    /// <summary>--root が利用者の実際のデータ(%LOCALAPPDATA%\Ohanami そのもの、または ohanami.db のある場所)なら拒否する(通しは別の DB に流す)。</summary>
    private static void RefuseUserData(string root)
    {
        string user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ohanami");
        string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(Norm(root), Norm(user), StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(root, "ohanami.db")))
            throw new ArgumentException($"--root {root} は利用者の実際のデータの場所に見える(%LOCALAPPDATA%\\Ohanami そのもの、または ohanami.db がある)。通しは別の root(既定 {DefaultFullRoot()})に流す。");
    }

    private static async Task<int> VlmFullAsync(Dictionary<string, string> flags, CancellationToken ct)
    {
        string root = Path.GetFullPath(flags.GetValueOrDefault("--root") ?? DefaultFullRoot());
        string images = Required(flags, "--images", "OHANAMI_IMAGES_DIR", "画像のフォルダ");
        string models = Required(flags, "--models", "OHANAMI_MODELS_DIR", "models/ に当たるディレクトリ");
        string exe = ResolveLlamaServer(flags);
        RefuseInsideRepository(root);
        RefuseUserData(root);
        int pauseAfter = flags.TryGetValue("--pause-after", out string? pa)
            ? (int.TryParse(pa, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : throw new ArgumentException($"--pause-after は 0 以上の整数: {pa}"))
            : 100;
        int stopAfter = flags.TryGetValue("--stop-after", out string? sa)
            ? (int.TryParse(sa, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int m) ? m : throw new ArgumentException($"--stop-after は 0 以上の整数: {sa}"))
            : 0;
        var o = new VlmFullOptions(Path.GetFullPath(images), Path.GetFullPath(models), Path.GetFullPath(exe), root, pauseAfter, flags.ContainsKey("--preflight"), stopAfter);
        return await VlmFull.RunAsync(o, ct);
    }

    private static string DefaultSleepData() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ohanami", "sleep-check");

    private static int ParsePositive(Dictionary<string, string> flags, string flag, int fallback) =>
        !flags.TryGetValue(flag, out string? v) ? fallback
        : int.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n > 0 ? n
        : throw new ArgumentException($"{flag} は 1 以上の整数: {v}");

    /// <summary>性能カウンタ(プロセスごとの専用 VRAM)を 1 回読んで、(pid, アダプタ)ごとに一覧にする。アダプタをまたいで足さない。</summary>
    private static int GpuProcessMemory()
    {
        IReadOnlyDictionary<GpuInstance, long> map;
        try { map = new PdhProcessGpuMemory().ReadDedicatedBytes(); }
        catch (ProcessGpuMemoryException e)
        {
            Console.Error.WriteLine("性能カウンタ(GPU Process Memory の Dedicated Usage)を読めなかった: " + e.Message);
            return 7;
        }
        // 読み取り 1 回の時間(vlm-full・vlm-check は 200ms 間隔で読む)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int repeat = 20;
        for (int i = 0; i < repeat; i++) _ = new PdhProcessGpuMemory().ReadDedicatedBytes();
        Console.WriteLine($"{PdhProcessGpuMemory.CounterPath}: {map.Count} インスタンス、{map.Keys.Select(k => k.Pid).Distinct().Count()} プロセス(読み取り 1 回あたり平均 {sw.Elapsed.TotalMilliseconds / repeat:F1} ms、{repeat} 回の平均)");

        static string NameOf(int pid)
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName; }
            catch (ArgumentException) { return "(もう終わった)"; }
        }
        Console.WriteLine("(pid, アダプタ)ごと。値の大きい順:");
        foreach ((GpuInstance inst, long bytes) in map.OrderByDescending(kv => kv.Value).Take(25))
            Console.WriteLine($"  pid {inst.Pid,6}  {bytes / 1024.0 / 1024.0,9:F1} MiB  {inst.Adapter}  {NameOf(inst.Pid)}");
        if (map.Count > 25) Console.WriteLine($"  …(残り {map.Count - 25} インスタンス)");

        // 1 つのプロセスが 2 つ以上のアダプタで 0 でない値を持つ例(合計すると誤った値になる)
        var multi = map.Where(kv => kv.Value > 0).GroupBy(kv => kv.Key.Pid).Where(g => g.Count() >= 2).ToList();
        Console.WriteLine(multi.Count == 0
            ? "2 つ以上のアダプタで 0 でない値を持つプロセス: 無い"
            : $"2 つ以上のアダプタで 0 でない値を持つプロセス: {multi.Count}(足すと誤った値になる。vlm-full・vlm-check は、llama-server がこうなったら「測れなかった」にする)");
        foreach (var g in multi.OrderByDescending(g => g.Sum(kv => kv.Value)))
            Console.WriteLine($"  pid {g.Key,6} {NameOf(g.Key)}: " + string.Join(" + ", g.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value / 1024.0 / 1024.0:F1}({kv.Key.Adapter})")) + $" = {g.Sum(kv => kv.Value) / 1024.0 / 1024.0:F1} MiB(合計)");
        return 0;
    }

    private static int SleepFixtureCommand(Dictionary<string, string> flags)
    {
        string outDir = flags.GetValueOrDefault("--out") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ohanami", "sleep-check-fixture");
        return SleepFixture.Run(outDir, ParsePositive(flags, "--embed-count", 1500), ParsePositive(flags, "--vlm-count", 30));
    }

    private static int SleepReport(Dictionary<string, string> flags)
    {
        string scenarioText = flags.GetValueOrDefault("--scenario") ?? throw new ArgumentException("--scenario が要る(sleep-vlm / sleep-load / sleep-embed / kill-app-after-kill / kill-app-final / kill-server / cutoff-stopped / cutoff-recovered)");
        SleepScenario scenario = scenarioText switch
        {
            "sleep-vlm" => SleepScenario.SleepVlm,
            "sleep-load" => SleepScenario.SleepLoad,
            "sleep-embed" => SleepScenario.SleepEmbed,
            "kill-app-after-kill" => SleepScenario.KillAppAfterKill,
            "kill-app-final" => SleepScenario.KillAppFinal,
            "kill-server" => SleepScenario.KillServer,
            "cutoff-stopped" => SleepScenario.CutoffStopped,
            "cutoff-recovered" => SleepScenario.CutoffRecovered,
            _ => throw new ArgumentException($"知らない場面: {scenarioText}"),
        };
        string folder = flags.GetValueOrDefault("--folder") ?? throw new ArgumentException("--folder が要る(sleep-fixture が作った場面のフォルダ)");
        DateTimeOffset to = DateTimeOffset.UtcNow;
        DateTimeOffset from = flags.TryGetValue("--since", out string? since)
            ? DateTimeOffset.Parse(since, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal)
            : to.AddMinutes(-ParsePositive(flags, "--minutes", 180));
        var o = new SleepReportOptions(Path.GetFullPath(flags.GetValueOrDefault("--data") ?? DefaultSleepData()), scenario, folder, from, to, ParsePositive(flags, "--vram-limit-mib", 2000));
        return SleepCheckCommand.Run(o);
    }

    private static int StageCount(Dictionary<string, string> flags)
    {
        string folder = flags.GetValueOrDefault("--folder") ?? throw new ArgumentException("--folder が要る(sleep-fixture が作った場面のフォルダ)");
        string data = Path.GetFullPath(flags.GetValueOrDefault("--data") ?? DefaultSleepData());
        int count = SleepCheckCommand.CountDone(Path.Combine(data, "ohanami.db"), folder, flags.GetValueOrDefault("--stage") ?? "caption");
        Console.WriteLine(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return 0;
    }

    private static int CaptionsCheck(Dictionary<string, string> flags)
    {
        string root = Path.GetFullPath(flags.GetValueOrDefault("--root") ?? DefaultFullRoot());
        string evalRunDir = Path.GetFullPath(Required(flags, "--s1-run", "OHANAMI_S1_RUN", "VLM の評価の全件の run のフォルダ"));
        RefuseInsideRepository(root);
        string? Sibling(string flag, string name)
        {
            if (flags.TryGetValue(flag, out string? v)) return Path.GetFullPath(v);
            string candidate = Path.Combine(Path.GetDirectoryName(evalRunDir.TrimEnd(Path.DirectorySeparatorChar))!, name);
            return Directory.Exists(candidate) ? candidate : null;
        }
        int expectedRuns = flags.TryGetValue("--expect-vlm-runs", out string? er)
            ? (int.TryParse(er, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : throw new ArgumentException($"--expect-vlm-runs は 0 以上の整数: {er}"))
            : 2;
        var o = new CaptionsCheckOptions(evalRunDir, Sibling("--retry-dry", "qwen3vl-8b-q4km-tags-retry-dry0.8"), Sibling("--retry-rep", "qwen3vl-8b-q4km-tags-retry-rep1.1"), root, expectedRuns, flags.ContainsKey("--ignore-times"));
        return CaptionsCheckCommand.Run(o);
    }

    private static string ResolveLlamaServer(Dictionary<string, string> flags)
    {
        string? exe = flags.GetValueOrDefault("--llama");
        if (exe is not null) return exe;
        string runtime = Environment.GetEnvironmentVariable("OHANAMI_RUNTIME_DIR") ?? throw new ArgumentException("llama-server.exe が要る: --llama か環境変数 OHANAMI_RUNTIME_DIR");
        return Path.Combine(runtime, "llama.cpp", "b" + Ohanami.Core.Models.VlmCatalog.LlamaServerBuild.ToString(System.Globalization.CultureInfo.InvariantCulture), "llama-server.exe");
    }

    private static async Task<int> VlmCheckAsync(Dictionary<string, string> flags, string root, CancellationToken ct)
    {
        string evalRunDir = Required(flags, "--s1-run", "OHANAMI_S1_RUN", "VLM の評価の run のフォルダ");
        string images = Required(flags, "--images", "OHANAMI_IMAGES_DIR", "画像のフォルダ");
        string models = Required(flags, "--models", "OHANAMI_MODELS_DIR", "models/ に当たるディレクトリ");
        string spikes = flags.GetValueOrDefault("--spikes") ?? FindSpikesDirectory();
        string exe = ResolveLlamaServer(flags);
        string samples = flags.GetValueOrDefault("--samples") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(spikes))!, "S1-vlm", "samples.tsv");
        int Int(string flag, int fallback) =>
            flags.TryGetValue(flag, out string? v) ? (int.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : throw new ArgumentException($"{flag} は 0 以上の整数: {v}")) : fallback;
        string[] stages = (flags.GetValueOrDefault("--stage") ?? string.Join(",", VlmCheck.AllStages)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? unknown = stages.FirstOrDefault(x => !VlmCheck.AllStages.Contains(x));
        if (unknown is not null) throw new ArgumentException($"--stage の「{unknown}」を知らない({string.Join(",", VlmCheck.AllStages)})");
        RefuseInsideRepository(root);
        var o = new VlmCheckOptions(
            Path.GetFullPath(evalRunDir), Path.GetFullPath(images), Path.GetFullPath(models), Path.GetFullPath(exe), root, Path.GetFullPath(samples),
            Int("--random", 40), Int("--seed", 20261106), Int("--image-max-tokens", Ohanami.Core.Models.VlmCatalog.ImageMaxTokens), stages.ToHashSet(StringComparer.Ordinal));
        return await VlmCheck.RunAsync(o, ct);
    }

    private static int Judge(Dictionary<string, string> flags, string root, List<string> positional)
    {
        string images = Path.GetFullPath(Required(flags, "--images", "OHANAMI_IMAGES_DIR", "画像のフォルダ"));
        string spikes = Path.GetFullPath(flags.GetValueOrDefault("--spikes") ?? FindSpikesDirectory());
        string run = positional.Count > 0 ? Path.GetFullPath(positional[0]) : LatestRun(root);
        string? onlyPath = flags.GetValueOrDefault("--path");
        RefuseInsideRepository(root);

        RunData data = RunFiles.Read(run);
        if (onlyPath is not null)
        {
            // 指定した経路の行だけにする。run に無い経路なら、黙って空のページにせず止める
            var kept = data.Precision.Where(r => r.Path == onlyPath).ToList();
            if (kept.Count == 0) throw new ArgumentException($"--path の「{onlyPath}」が run に無い(あるのは {string.Join("、", data.Precision.Select(r => r.Path).Distinct(StringComparer.Ordinal))})");
            data = new RunData(data.KnownItems, kept);
        }
        Qrels qrels = BenchInputs.ReadQrels(Path.Combine(spikes, "qrels.tsv"));
        var targets = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        IEnumerable<KnownItem> known = BenchInputs.ReadKnownItems(Path.Combine(spikes, "known-items.tsv"), "visual", out _)
            .Concat(BenchInputs.ReadKnownItems(Path.Combine(spikes, "known-items-dialogue.tsv"), "dialogue", out _));
        // 任意のファイル(one-image の集合・文脈の集合)。あれば、目的の画像の枠に使う
        foreach ((string file, string set) in new[] { (BenchInputs.OneImageKnownItemsFile, BenchInputs.OneImageSet), (BenchInputs.ContextKnownItemsFile, BenchInputs.ContextSet) })
        {
            string optional = Path.Combine(spikes, file);
            if (File.Exists(optional)) known = known.Concat(BenchInputs.ReadKnownItems(optional, set, out _));
        }
        foreach (KnownItem item in known)
            targets[item.Query] = [.. targets.GetValueOrDefault(item.Query) ?? [], item.Filename];

        // 画像は元のファイルを file:/// で参照する(コピーしない)。無ければ、黙って壊れた img を出さずに止める
        string UrlOf(string file)
        {
            string full = Path.Combine(images, file);
            if (!File.Exists(full)) throw new InvalidDataException($"画像が無い: {full}(--images が run のときと同じフォルダか確かめる。入れ子のフォルダの画像は扱えない)");
            return new Uri(full).AbsoluteUri;
        }
        List<JudgeBlock> blocks = JudgePlan.Build(data.Precision, qrels, targets, UrlOf);
        int unjudged = blocks.Sum(b => b.Candidates.Count);
        Console.WriteLine($"判定が要る画像: {blocks.Count} クエリ、{unjudged} 枚(run = {run})");
        foreach (JudgeBlock b in blocks) Console.WriteLine($"  「{b.Query}」: {b.Candidates.Count} 枚({string.Join("、", b.Candidates.Select(c => c.File))})");
        if (blocks.Count == 0)
        {
            Console.WriteLine("未判定の画像は無い。ページは作らない。");
            return 0;
        }

        string stamp = DateTimeOffset.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string pageId = JudgePlan.PageId(blocks);
        string scope = onlyPath is null ? "上位 10 件" : $"{JudgePlan.Label(onlyPath)}の上位 10 件";
        string html = JudgePage.Build(blocks, "qrels の判定(未判定の画像)", $"run: {Path.GetFileName(run)}。{scope}のうち qrels に無い画像 {unjudged} 枚。", pageId, "judge-page:" + stamp);
        string dir = Path.Combine(root, "judge");
        Directory.CreateDirectory(dir);
        string page = Path.Combine(dir, $"judge-{stamp}-{pageId}.html");
        File.WriteAllText(page, html, new System.Text.UTF8Encoding(false));
        Console.WriteLine("判定ページ: " + page);
        Console.WriteLine("  ブラウザで開き、当たりにチェックを付け(当たりが無いクエリは「当たり 0 件で確定」)、下の欄の TSV を spikes/S2-siglip2/qrels.tsv の末尾に貼り足す。");
        Console.WriteLine("  貼り足したあとは、run をやり直して(または judge をもう一度)、未判定が 0 になったか確かめる。");
        return 0;
    }

    private static string Required(Dictionary<string, string> flags, string flag, string env, string what)
    {
        string? v = flags.GetValueOrDefault(flag) ?? Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrWhiteSpace(v)) throw new ArgumentException($"{what} が要る: {flag} か環境変数 {env}");
        return v;
    }

    /// <summary>実行ファイルから親をたどって、spikes/S2-siglip2/qrels.tsv があるディレクトリを探す。</summary>
    private static string FindSpikesDirectory()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "spikes", "S2-siglip2");
            if (File.Exists(Path.Combine(candidate, "qrels.tsv"))) return candidate;
        }
        throw new ArgumentException("spikes/S2-siglip2 が見つからない。--spikes で指す。");
    }

    /// <summary>--root がリポジトリの中なら拒否する(私物の画像に依存する DB・結果をリポジトリに置かない)。</summary>
    private static void RefuseInsideRepository(string root)
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (!(Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))) continue;
            string repo = d.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if ((root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(repo, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"--root {root} はリポジトリ {repo} の中。私物の画像に依存する DB と結果は、リポジトリの外に置く。");
            return;
        }
    }

    // ------------------------------------------------------------------ baseline / compare

    internal static int SetBaseline(string root, List<string> positional)
    {
        string run = positional.Count > 0 ? Path.GetFullPath(positional[0]) : LatestRun(root);
        _ = RunFiles.Read(run); // 読めることを確かめてから写す
        string baseline = Path.Combine(root, "baseline");
        if (Directory.Exists(baseline)) Directory.Delete(baseline, recursive: true);
        Directory.CreateDirectory(baseline);
        foreach (string f in new[] { RunFiles.KnownItemsFile, RunFiles.PrecisionFile, RunFiles.SummaryFile })
            File.Copy(Path.Combine(run, f), Path.Combine(baseline, f));
        // run にある scores.tsv は、あれば写す(古い形式の run には無い。比較は使わないが、あとで基準線のスコアを見られるように)
        if (File.Exists(Path.Combine(run, RunFiles.ScoresFile))) File.Copy(Path.Combine(run, RunFiles.ScoresFile), Path.Combine(baseline, RunFiles.ScoresFile));
        File.WriteAllText(Path.Combine(baseline, "from.txt"), run + "\n");
        Console.WriteLine($"基準線にした: {run} → {baseline}");
        return 0;
    }

    private static int Compare(string root, List<string> positional)
    {
        string baseline = positional.Count > 0 ? Path.GetFullPath(positional[0]) : Path.Combine(root, "baseline");
        string run = positional.Count > 1 ? Path.GetFullPath(positional[1]) : LatestRun(root);
        return PrintComparison(baseline, run);
    }

    private static int PrintComparison(string baselineDir, string runDir)
    {
        Comparison c = Comparison.Compare(RunFiles.Read(baselineDir), RunFiles.Read(runDir));
        Console.WriteLine($"基準線: {baselineDir}");
        Console.WriteLine($"今回  : {runDir}");
        Console.WriteLine(c.Render());
        return c.AggregateWorse ? 3 : 0;
    }

    private static string LatestRun(string root)
    {
        string runs = Path.Combine(root, "runs");
        string? latest = Directory.Exists(runs) ? Directory.GetDirectories(runs).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal).LastOrDefault() : null;
        return latest ?? throw new InvalidOperationException($"run の結果が無い({runs})。先に run を実行する。");
    }
}
