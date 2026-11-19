using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core;
using Ohanami.Core.Indexing;

namespace Ohanami.Bench;

/// <summary>run の設定。パスはすべてフルパス。</summary>
internal sealed record RunOptions(string ImagesDirectory, string ModelsDirectory, string Root, string SpikesDirectory, string Label);

/// <summary>コンソールへ出す診断ログ(Core の <see cref="ICoreLog"/>)。</summary>
internal sealed class ConsoleLog : ICoreLog
{
    public void Info(string message) => Console.WriteLine("  [info] " + message);

    public void Warning(string message) => Console.WriteLine("  [警告] " + message);

    public void Error(string message, Exception? exception = null) => Console.Error.WriteLine("  [エラー] " + message + (exception is null ? "" : " (" + exception.GetType().Name + ")"));
}

/// <summary>
/// 検索の回帰ベンチの実行: OhanamiCore を開き、画像のフォルダを走査・埋め込み(済みなら再利用)して、known-item と qrels のクエリを製品の
/// <see cref="SearchService"/> に投げ、順位と precision@10 を集める。DB・サムネイルのキャッシュは <see cref="RunOptions.Root"/> に置く(リポジトリの外)。
/// 画像のフォルダは読み取りだけ(走査と埋め込みはファイルを読むだけで、書き換えない)。
/// </summary>
internal static class BenchRunner
{
    /// <summary>models/ の下の、自前変換した SigLIP2 のディレクトリ名(Core の ModelCatalog.DirectoryName と同じ。Core の内部の型なのでここに書く)。</summary>
    public const string ModelDirectoryName = "ViT-SO400M-16-SigLIP2-384__ohanami";

    public static async Task<(RunInfo Info, RunData Data, IReadOnlyList<string> PathNames, int PrecisionQueries)> RunAsync(RunOptions o, CancellationToken ct)
    {
        var stages = new StageClock();   // 段階ごとの所要時間(summary に出す)
        Collected c = await CollectAsync(o, stages, ct);
        return Aggregate(o, c, stages);
    }

    /// <summary>検索までに集めたもの(<see cref="CollectAsync"/> の結果)。集計(<see cref="Aggregate"/>)と、3 本目の試験(<see cref="ThirdPath"/>)が使う。</summary>
    internal sealed record Collected(
        BenchInputs.InputSet Inputs,
        IReadOnlyList<string> UniqueQueries,
        IReadOnlyDictionary<string, SearchResponse> Responses,
        IReadOnlyDictionary<string, Dictionary<string, SearchResult>> ByName,
        int TotalImages, int Embedded, double IndexSeconds, long Captions, IReadOnlyList<TimeSpan> QueryTimes);

    /// <summary>
    /// 入力を読み、Core を開き、走査・埋め込み(済みは再利用)・全文検索の索引を整え、クエリごとに製品の検索を全件で 1 回ずつ流す。
    /// 経路が Ok でない・全件が返らない・埋め込みが済んでいない・入力のファイル名がフォルダの画像に無い、のどれかは例外(黙って測らない)。
    /// </summary>
    internal static async Task<Collected> CollectAsync(RunOptions o, StageClock stages, CancellationToken ct)
    {
        // ---- 入力(先に読んで、形式の誤りを GPU を使う前に見つける)
        BenchInputs.InputSet inputs = BenchInputs.Load(o.SpikesDirectory);
        Qrels qrels = inputs.Qrels;
        Console.WriteLine(inputs.Summary);

        stages.Mark("入力の読み込み");

        await using OhanamiCore core = await OpenCoreAsync(o, ct);
        stages.Mark("Core の起動");

        // ---- 走査と埋め込み(2 回目からは再利用: 済みの画像は待ち行列に入らない)
        var clock = Stopwatch.StartNew();
        await ScanLibraryAsync(core, o, ct);
        stages.Mark("走査");
        await EmbedAllAsync(core, ct);
        stages.Mark("埋め込み");
        double indexSeconds = clock.Elapsed.TotalSeconds;

        // ベクトルの読み込みと、埋め込みの追加が表に反映されるのを待つ
        int expected = core.Indexer.State.Stages.Single(s => s.Stage == Stage.Embed).Done;
        await WaitUntilAsync(() => core.Status.VectorsLoaded && core.Status.VectorCount == expected, TimeSpan.FromMinutes(2), $"ベクトルの表が {expected} 件にならない(今 {core.Status.VectorCount})", ct);
        stages.Mark("ベクトルの読み込み待ち");

        // 全文検索の索引(規約が DB と違えば captions から作り直す。裏で行われるので、済むのを待つ)
        await core.FullTextIndex.EnsureAsync(ct);
        (int ftsIndexed, int ftsSkipped) = core.FullTextIndex.LastRebuild;
        long captions = CountCaptions(core);
        Console.WriteLine($"全文検索の索引: captions {captions} 件(作り直し {core.FullTextIndex.RebuildCount} 回: 索引に入れた {ftsIndexed}、飛ばした {ftsSkipped})");
        stages.Mark("全文検索の索引");

        // ---- 検索(クエリごとに 1 回。全件を返す = Limit は母集団以上)
        List<string> uniqueQueries = inputs.Items.Select(i => i.Query).Concat(qrels.Queries).Distinct(StringComparer.Ordinal).ToList();
        Console.WriteLine($"検索: {uniqueQueries.Count} 種類のクエリ");
        var responses = new Dictionary<string, SearchResponse>(StringComparer.Ordinal);
        var byName = new Dictionary<string, Dictionary<string, SearchResult>>(StringComparer.Ordinal);
        var queryTimes = new List<TimeSpan>();
        foreach (string q in uniqueQueries)
        {
            SearchResponse r = await core.Search.SearchAsync(new SearchQuery(q, Limit: 1_000_000), ct);
            foreach (PathOutcome p in r.Paths)
                if (p.State != PathState.Ok) throw new InvalidOperationException($"経路 {p.PathId} が {p.State}: {p.Message}(クエリ「{q}」)");
            if (r.TotalImages != r.Results.Count) throw new InvalidOperationException($"全件が返っていない(結果 {r.Results.Count} / 全 {r.TotalImages})。");
            if (r.IndexedImages != r.TotalImages) throw new InvalidOperationException($"埋め込みが済んでいない画像がある({r.IndexedImages} / {r.TotalImages})。");
            responses[q] = r;
            byName[q] = Metrics.IndexByFileName(r);
            queryTimes.Add(r.Elapsed);
        }
        stages.Mark($"検索({uniqueQueries.Count} クエリ)");
        SearchResponse any = responses.Values.First();

        // qrels と known-item のファイル名がフォルダの画像に実在するか(綴りの違い・別のフォルダのものを黙って無視しない)
        HashSet<string> present = byName[uniqueQueries[0]].Keys.ToHashSet(StringComparer.Ordinal);
        string[] missingFiles = qrels.Files.Concat(inputs.Items.Select(i => i.Filename)).Distinct(StringComparer.Ordinal).Where(f => !present.Contains(f)).ToArray();
        if (missingFiles.Length > 0)
            throw new InvalidDataException($"入力のファイル名がフォルダの画像に無い {missingFiles.Length} 件: {string.Join("、", missingFiles.Take(5))}(known-item を作ったときと同じ画像のフォルダか確かめる)");

        return new Collected(inputs, uniqueQueries, responses, byName, any.TotalImages, expected, indexSeconds, captions, queryTimes);
    }

    /// <summary>集めた検索の結果から、known-item の順位・precision@10・経路ごとのスコアの行を作る(純粋な計算。<paramref name="stages"/> に「集計」を記録して、<see cref="RunInfo"/> を作る)。</summary>
    internal static (RunInfo Info, RunData Data, IReadOnlyList<string> PathNames, int PrecisionQueries) Aggregate(RunOptions o, Collected c, StageClock stages)
    {
        RunData data = BuildData(c, out List<string> pathNames);
        stages.Mark("集計");
        return (MakeInfo(o, c, stages), data, pathNames, c.Inputs.Qrels.Queries.Count());
    }

    /// <summary>summary の見出し用の情報(件数・検索 1 回あたりの時間・段階ごとの所要時間)。</summary>
    internal static RunInfo MakeInfo(RunOptions o, Collected c, StageClock stages)
    {
        string notes = $"件数: 画像 {c.TotalImages}、qrels の画像 {c.Inputs.Qrels.Files.Count()} 種類。全文検索の索引: captions {c.Captions} 件" + Environment.NewLine +
            $"検索 1 回あたり(クエリ {c.QueryTimes.Count} 回): 平均 {c.QueryTimes.Average(x => x.TotalMilliseconds):F0} ms、最大 {c.QueryTimes.Max(x => x.TotalMilliseconds):F0} ms";
        return new RunInfo(o.Label, DateTimeOffset.Now, o.ImagesDirectory, c.TotalImages, c.Embedded, c.IndexSeconds, notes, stages.Stages);
    }

    /// <summary>
    /// known-item の順位・precision@10・経路ごとのスコアの行(<see cref="RunData"/>)。経路の並びは <paramref name="pathNames"/>(融合 + 結果の経路)。
    /// <paramref name="c"/> の Responses は、製品の検索の結果でも、経路を足した結果(3 本目の試験)でもよい。
    /// </summary>
    internal static RunData BuildData(Collected c, out List<string> pathNames)
    {
        Qrels qrels = c.Inputs.Qrels;
        IReadOnlyList<KnownItem> items = c.Inputs.Items;
        pathNames = Metrics.PathNames(c.Responses.Values.First());

        var known = new List<KnownItemRow>();
        foreach (KnownItem item in items)
            known.AddRange(Metrics.KnownItemRows(item.Set, item.Query, item.Filename, c.Responses[item.Query], c.ByName[item.Query]));
        var precision = new List<PrecisionRow>();
        foreach (string q in qrels.Queries.Order(StringComparer.Ordinal))
            foreach (string path in pathNames)
                precision.Add(Metrics.Precision(c.Responses[q], path, q, qrels));

        // 経路ごとのスコア: クエリごとに、qrels の判定済みの画像と、そのクエリを使う known-item の目的の画像
        var targetSets = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (KnownItem item in items)
        {
            Dictionary<string, string> m = targetSets.TryGetValue(item.Query, out Dictionary<string, string>? existing) ? existing : targetSets[item.Query] = new(StringComparer.Ordinal);
            m[item.Filename] = m.TryGetValue(item.Filename, out string? sets) ? string.Join('|', sets.Split('|').Append(item.Set).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) : item.Set;
        }
        ILookup<string, string> judgedFiles = qrels.Rows.ToLookup(r => r.Query, r => r.File, StringComparer.Ordinal);
        var scores = new List<ScoreRow>();
        foreach (string q in c.UniqueQueries.Order(StringComparer.Ordinal))
        {
            IReadOnlyDictionary<string, string> targets = targetSets.TryGetValue(q, out Dictionary<string, string>? t) ? t : new Dictionary<string, string>();
            scores.AddRange(Metrics.ScoreRows(q, c.Responses[q], c.ByName[q], judgedFiles[q].Concat(targets.Keys), targets, qrels));
        }
        return new RunData(known, precision, scores, RepeatMetrics.TopRows(c));
    }

    /// <summary>ベンチの DB で Core を開き、起動が済む(Ready)のを待つ。DB・サムネイルは <see cref="RunOptions.Root"/> の下(リポジトリの外)。</summary>
    internal static async Task<OhanamiCore> OpenCoreAsync(RunOptions o, CancellationToken ct, VlmOptions? vlm = null)
    {
        string modelDir = Path.Combine(o.ModelsDirectory, ModelDirectoryName);
        if (!Directory.Exists(o.ImagesDirectory)) throw new DirectoryNotFoundException($"画像のフォルダが無い: {o.ImagesDirectory}");
        if (!Directory.Exists(modelDir)) throw new DirectoryNotFoundException($"モデルのディレクトリが無い: {modelDir}(--models か環境変数 OHANAMI_MODELS_DIR)");
        string dbDir = Path.Combine(o.Root, "db");
        Directory.CreateDirectory(dbDir);
        var options = new CoreOptions(
            DatabasePath: Path.Combine(dbDir, "bench.db"),
            ThumbnailDirectory: Path.Combine(o.Root, "thumbs"),
            ModelDirectory: modelDir,
            NativeDirectory: AppContext.BaseDirectory,
            Log: new ConsoleLog(),
            Vlm: vlm,
            WatchFolders: false);   // 測定の途中で監視が走査を始めないように

        Console.WriteLine($"DB: {options.DatabasePath}");
        OhanamiCore core = await OhanamiCore.OpenAsync(options, ct);
        try
        {
            CoreStatus started = await core.WaitUntilStartedAsync(ct);
            if (started.Phase != CorePhase.Ready) throw new InvalidOperationException("Core が起動できなかった: " + started.Reason);
            return core;
        }
        catch
        {
            await core.DisposeAsync();
            throw;
        }
    }

    /// <summary>画像のフォルダを登録して走査する(読めない・未対応・消えたファイルがあれば止める)。</summary>
    internal static async Task ScanLibraryAsync(OhanamiCore core, RunOptions o, CancellationToken ct)
    {
        await RegisterFolderAsync(core, o.ImagesDirectory, ct);
        FolderInfo folder = core.Folders.List().Single();
        ScanSummary scan = await core.Indexer.ScanAsync([folder.Id], ct);
        Console.WriteLine($"走査: 追加 {scan.Added}、変更 {scan.Changed}、消えた {scan.Missing}、読めない {scan.Unreadable}、未対応 {scan.Unsupported}({scan.Elapsed.TotalSeconds:F1} 秒)");
        if (scan.Unreadable > 0 || scan.Unsupported > 0 || scan.Missing > 0)
            throw new InvalidOperationException("走査で読めない・未対応・消えたファイルがあった。この道具は全画像が索引に入っていることを前提にする。");
    }

    private static long CountCaptions(OhanamiCore core)
    {
        using Microsoft.Data.Sqlite.SqliteConnection conn = core.DatabaseForTests.CreateReadConnection();
        using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM captions";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task RegisterFolderAsync(OhanamiCore core, string imagesDir, CancellationToken ct)
    {
        string full = Path.GetFullPath(imagesDir).TrimEnd(Path.DirectorySeparatorChar);
        IReadOnlyList<FolderInfo> folders = core.Folders.List();
        if (folders.Count == 0) { await core.Folders.AddAsync(full, ct); return; }
        if (folders.Count == 1 && string.Equals(folders[0].Path.TrimEnd(Path.DirectorySeparatorChar), full, StringComparison.OrdinalIgnoreCase)) return;
        throw new InvalidOperationException($"ベンチの DB に別のフォルダが登録されている({string.Join("、", folders.Select(f => f.Path))})。--root を変えるか、DB を消す。");
    }

    /// <summary>埋め込みを待ち行列が空になるまで流す。済みの画像は飛ばされる(結果は (hash, model_id) で DB にあるため)。失敗・要再作成があれば例外。</summary>
    private static async Task EmbedAllAsync(OhanamiCore core, CancellationToken ct)
    {
        StageSummary stage = core.Indexer.State.Stages.Single(s => s.Stage == Stage.Embed);
        Console.WriteLine($"埋め込み: 済み {stage.Done}、待ち {stage.Pending}、失敗 {stage.Failed}、要再作成 {stage.Outdated}");
        if (stage.Failed > 0) throw new InvalidOperationException($"埋め込みに失敗した画像が {stage.Failed} 枚ある。原因を直してから(IndexState の LastError / ログを見る)。");
        if (stage.Outdated > 0)
        {
            Console.WriteLine("  モデルの版が変わっている: 要再作成の画像を待ち行列に戻す");
            await core.Indexer.RequeueOutdatedAsync(JobKind.Embed, ct);
        }
        stage = core.Indexer.State.Stages.Single(s => s.Stage == Stage.Embed);
        if (stage.Pending == 0 && stage.Outdated == 0) return;

        var last = Stopwatch.StartNew();
        core.Indexer.StateChanged += s =>
        {
            if (last.Elapsed < TimeSpan.FromSeconds(5)) return;
            last.Restart();
            StageSummary st = s.Stages.Single(x => x.Stage == Stage.Embed);
            Console.WriteLine($"  埋め込み中: 済み {st.Done}、待ち {st.Pending}{(s.ImagesPerSecond is { } v ? $"、{v:F1} 枚/秒" : "")}");
        };
        await core.Indexer.StartAsync([JobKind.Embed], ct);
        IndexState state = core.Indexer.State;
        stage = state.Stages.Single(s => s.Stage == Stage.Embed);
        if (state.Run == IndexRunState.Failed) throw new InvalidOperationException("埋め込みのジョブが失敗した: " + state.LastError);
        if (stage.Pending > 0 || stage.Failed > 0 || stage.Outdated > 0 || state.SkippedImages > 0)
            throw new InvalidOperationException($"埋め込みが終わっていない(待ち {stage.Pending}、失敗 {stage.Failed}、要再作成 {stage.Outdated}、飛ばした {state.SkippedImages})。LastError: {state.LastError}");
        Console.WriteLine($"埋め込み完了: 済み {stage.Done}");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failure, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException(failure);
            await Task.Delay(50, ct);
        }
    }
}
