using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Ohanami.Core;
using Ohanami.Core.Search;

namespace Ohanami.Bench;

/// <summary>3 本目の経路の 1 件の説明(コサイン)。</summary>
internal sealed record ThirdDetail(double Cosine) : PathDetail;

/// <summary>
/// 3 本目の経路(VLM の説明文のテキスト埋め込み)の試し測りの入力(ADR 0035): Python が書く埋め込みのファイルの読み書き。
/// <list type="bullet">
/// <item><c>vectors.f32</c>: 行 × 次元の float32(リトルエンディアン)を並べただけ。すべての行は長さ 1(L2 正規化済み)。</item>
/// <item><c>index.tsv</c>: 1 行目は <c>kind\tkey\trow</c>。kind は doc(key = 画像のハッシュ)か query(key = クエリの文字列)。同じ説明文の画像は同じ row を指す(同じ文字列は 1 回だけ埋め込む)。</item>
/// <item><c>meta.json</c>: <c>model</c>(モデルの名前)と <c>dim</c>(次元)は必須。ほか(リビジョン・件数など)は記録用で、読まない。</item>
/// </list>
/// 私物の説明文から作ったものなので、リポジトリの外に置く。形式の誤り(次元・行数・長さが 1 でない・重複する key)は例外にする。
/// </summary>
internal sealed class ThirdVectors
{
    public const string IndexFile = "index.tsv";
    public const string VectorsFile = "vectors.f32";
    public const string MetaFile = "meta.json";

    /// <summary>行の長さが 1 から外れてよい幅(float32 の丸めと、Python 側の正規化の誤差)。</summary>
    private const double NormTolerance = 1e-3;

    public string Model { get; }

    /// <summary>入れ方の名前(meta.json の variant。desc-tags = 主、desc-only = 診断。無ければ null)。</summary>
    public string? Variant { get; }

    public int Dim { get; }

    /// <summary>画像のハッシュ → ベクトル(説明文が空の画像は入らない)。</summary>
    public IReadOnlyDictionary<string, float[]> Docs { get; }

    /// <summary>クエリの文字列 → ベクトル。</summary>
    public IReadOnlyDictionary<string, float[]> Queries { get; }

    public ThirdVectors(string model, int dim, IReadOnlyDictionary<string, float[]> docs, IReadOnlyDictionary<string, float[]> queries, string? variant = null)
    {
        Model = model;
        Variant = variant;
        Dim = dim;
        Docs = docs;
        Queries = queries;
    }

    /// <summary>ディレクトリから読む。</summary>
    public static ThirdVectors Read(string dir)
    {
        string metaPath = Path.Combine(dir, MetaFile), indexPath = Path.Combine(dir, IndexFile), vectorsPath = Path.Combine(dir, VectorsFile);
        foreach (string f in new[] { metaPath, indexPath, vectorsPath })
            if (!File.Exists(f)) throw new FileNotFoundException($"3 本目の埋め込みのファイルが無い: {f}");

        using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(metaPath, new UTF8Encoding(false, true)));
        string model = meta.RootElement.TryGetProperty("model", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : throw new InvalidDataException($"{MetaFile} に model(文字列)が無い");
        int dim = meta.RootElement.TryGetProperty("dim", out JsonElement d) && d.TryGetInt32(out int dv) && dv > 0 ? dv : throw new InvalidDataException($"{MetaFile} に dim(正の整数)が無い");

        string[] lines = File.ReadAllLines(indexPath, new UTF8Encoding(false, true));
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != "kind\tkey\trow") throw new InvalidDataException($"{IndexFile} の 1 行目が想定と違う(想定「kind\\tkey\\trow」)");
        var entries = new List<(string Kind, string Key, int Row)>();
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            string[] c = line.Split('\t');
            if (c.Length != 3) throw new InvalidDataException($"{IndexFile} の {i + 1} 行目: 列が 3 個でない({c.Length} 個)");
            if (c[0] is not ("doc" or "query")) throw new InvalidDataException($"{IndexFile} の {i + 1} 行目: kind が doc でも query でもない: {c[0]}");
            if (!int.TryParse(c[2], NumberStyles.None, CultureInfo.InvariantCulture, out int row)) throw new InvalidDataException($"{IndexFile} の {i + 1} 行目: row が非負の整数でない");
            entries.Add((c[0], c[1], row));
        }

        long bytes = new FileInfo(vectorsPath).Length;
        long rowBytes = (long)dim * sizeof(float);
        if (bytes % rowBytes != 0) throw new InvalidDataException($"{VectorsFile} の大きさ {bytes} が、次元 {dim} の行の大きさ {rowBytes} の整数倍でない");
        int rows = checked((int)(bytes / rowBytes));
        if (entries.Count > 0 && entries.Max(e => e.Row) >= rows) throw new InvalidDataException($"{IndexFile} の row が、{VectorsFile} の行数 {rows} を超えている");

        var cache = new Dictionary<int, float[]>();
        using FileStream fs = File.OpenRead(vectorsPath);
        byte[] buffer = new byte[rowBytes];
        float[] Row(int row)
        {
            if (cache.TryGetValue(row, out float[]? v)) return v;
            fs.Seek(row * rowBytes, SeekOrigin.Begin);
            fs.ReadExactly(buffer);
            var vec = new float[dim];
            Buffer.BlockCopy(buffer, 0, vec, 0, buffer.Length);
            double norm = 0;
            foreach (float x in vec)
            {
                if (float.IsNaN(x) || float.IsInfinity(x)) throw new InvalidDataException($"{VectorsFile} の行 {row} に NaN か無限大がある");
                norm += (double)x * x;
            }
            norm = Math.Sqrt(norm);
            if (Math.Abs(norm - 1) > NormTolerance) throw new InvalidDataException($"{VectorsFile} の行 {row} の長さが 1 でない({norm:F6})。L2 正規化して書く");
            return cache[row] = vec;
        }

        var docs = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var queries = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach ((string kind, string key, int row) in entries)
        {
            Dictionary<string, float[]> target = kind == "doc" ? docs : queries;
            if (!target.TryAdd(key, Row(row))) throw new InvalidDataException($"{IndexFile} に同じ {kind} の key が 2 回ある");
        }
        string? variant = meta.RootElement.TryGetProperty("variant", out JsonElement vr) && vr.ValueKind == JsonValueKind.String ? vr.GetString() : null;
        return new ThirdVectors(model, dim, docs, queries, variant);
    }

    /// <summary>ディレクトリに書く(テスト用。Python の書き出しと同じ形式)。同じ文字列のベクトルを共有させたいときは、同じ配列の参照を渡す(row が同じになる)。</summary>
    public static void Write(string dir, string model, int dim, IReadOnlyList<(string Kind, string Key, float[] Vector)> rows, string? variant = null)
    {
        Directory.CreateDirectory(dir);
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(Path.Combine(dir, MetaFile), variant is null ? JsonSerializer.Serialize(new { model, dim }) : JsonSerializer.Serialize(new { model, dim, variant }), utf8);
        var rowOf = new Dictionary<float[], int>(ReferenceEqualityComparer.Instance);
        var unique = new List<float[]>();
        var sb = new StringBuilder("kind\tkey\trow\n");
        foreach ((string kind, string key, float[] vector) in rows)
        {
            if (vector.Length != dim) throw new ArgumentException($"次元が違う: {vector.Length} / {dim}");
            if (!rowOf.TryGetValue(vector, out int row)) { row = unique.Count; rowOf[vector] = row; unique.Add(vector); }
            sb.Append(kind).Append('\t').Append(key).Append('\t').Append(row.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        File.WriteAllText(Path.Combine(dir, IndexFile), sb.ToString(), utf8);
        using FileStream fs = File.Create(Path.Combine(dir, VectorsFile));
        var bytes = new byte[dim * sizeof(float)];
        foreach (float[] v in unique)
        {
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
            fs.Write(bytes);
        }
    }
}

/// <summary>1 つのクエリに 3 本目の経路を足した結果。</summary>
/// <param name="Augmented">fused3 の順(Rank・FusedScore は fused3)に並べ替え、各画像に 3 本目の Hit(PathId = <see cref="ThirdPath.PathId"/>)を足した検索の結果。経路の一覧にも 3 本目を足す。</param>
/// <param name="ThirdPopulation">3 本目の経路が順位を付けた画像の数(説明文の埋め込みがある画像)。</param>
internal sealed record ThirdAugmentation(SearchResponse Augmented, int ThirdPopulation);

/// <summary>
/// 3 本目の経路の試し測り(ADR 0035)。製品の検索の結果(全件)に、説明文の埋め込みの cosine の経路を足して、今の 2 本と同じ RRF(k = 60、重みなし)で融合した
/// <c>fused3</c> を作る。<b>先に、同じ <see cref="RankedPath.Rrf"/> で今の 2 本(見た目・全文)を融合して、製品の順位・スコアと全クエリで一致する</b>ことを確かめる
/// (一致しなければ例外。融合の再現が違うまま fused3 を測らない)。製品のコードは変えない。
/// </summary>
internal static class ThirdPath
{
    /// <summary>主(説明文 + タグ)の 3 本目の経路の名前と、それを足した融合の名前。</summary>
    public const string PathId = "third";
    public const string Fused3 = "fused3";

    /// <summary>診断(説明文だけ。後知恵)の 3 本目の経路の名前と、それを足した融合の名前。known-item の順位だけに使う(precision・判定には使わない)。</summary>
    public const string DiagPathId = "third-desc";
    public const string DiagFused3 = "fused3-desc";

    /// <summary>順位・スコアの再現の許容(同じ関数を同じ順で呼ぶので、ほぼ 0。浮動小数の誤差だけ)。</summary>
    private const double ReproduceTolerance = 1e-12;

    /// <summary>
    /// 製品の検索の結果 <paramref name="product"/>(全件。経路はすべて Ok)に、3 本目の経路を足す。
    /// <paramref name="queryVector"/> は「検索クエリ: 」つきで埋め込んだクエリ(正規化済み)、<paramref name="docs"/> は画像のハッシュ → 説明文のベクトル。
    /// </summary>
    public static ThirdAugmentation Augment(SearchResponse product, float[] queryVector, IReadOnlyDictionary<string, float[]> docs, string modelId, string pathId = PathId)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(queryVector);
        ArgumentNullException.ThrowIfNull(docs);
        IReadOnlyList<SearchResult> results = product.Results;
        int n = results.Count;
        if (n == 0) throw new ArgumentException("検索の結果が空", nameof(product));
        if (product.TotalImages != n) throw new ArgumentException($"全件ではない(結果 {n} / 全 {product.TotalImages})", nameof(product));

        // ---- 製品の経路の RankedPath を、結果の Hits から作り直す(経路の並びは product.Paths の順 = 製品の融合の順)
        var paths = new List<RankedPath>();
        foreach (PathOutcome outcome in product.Paths)
        {
            if (outcome.State != PathState.Ok) throw new ArgumentException($"経路 {outcome.PathId} が {outcome.State}", nameof(product));
            var hits = new List<(int Index, double Score)>();
            for (int i = 0; i < n; i++)
            {
                PathHit? h = results[i].Hits.FirstOrDefault(x => x.PathId == outcome.PathId);
                if (h is not null) hits.Add((i, h.Score));
            }
            paths.Add(RankedPath.Sparse(n, hits));
        }

        // ---- 先に、今の融合を再現できることを確かめる(同じ Rrf、同じ k、同じ順)
        RankedPath fused = RankedPath.Rrf(paths, SearchService.RrfK);
        int mismatches = 0;
        for (int i = 0; i < n; i++)
        {
            if (!fused.Present[i] || Math.Abs(fused.MeanRank[i] - results[i].Rank) > ReproduceTolerance || Math.Abs(fused.Score[i] - results[i].FusedScore) > ReproduceTolerance) mismatches++;
        }
        if (mismatches > 0)
            throw new InvalidOperationException($"今の融合(見た目 + 全文)を再現できない: {n} 枚のうち {mismatches} 枚の順位かスコアが製品と違う(クエリ「{product.Query}」)。fused3 は測らない");

        // ---- 3 本目: 説明文のベクトルがある画像だけに、cosine で順位を付ける(空の説明文の画像は母集団に入らない)
        var thirdHits = new List<(int Index, double Score)>();
        for (int i = 0; i < n; i++)
        {
            if (docs.TryGetValue(results[i].Hash, out float[]? doc)) thirdHits.Add((i, Dot(queryVector, doc)));
        }
        RankedPath third = RankedPath.Sparse(n, thirdHits);

        RankedPath fused3 = RankedPath.Rrf([.. paths, third], SearchService.RrfK);

        // ---- fused3 の順に並べた検索の結果
        var thirdScore = new Dictionary<int, double>(thirdHits.Select(h => KeyValuePair.Create(h.Index, h.Score)));
        var augmented = new List<SearchResult>(n);
        foreach (int i in fused3.Order)
        {
            SearchResult r = results[i];
            IReadOnlyList<PathHit> hits = thirdScore.TryGetValue(i, out double cos)
                ? [.. r.Hits, new PathHit(pathId, modelId, third.MeanRank[i], third.Score[i], new ThirdDetail(cos))]
                : r.Hits;
            augmented.Add(new SearchResult(r.Hash, r.Paths, fused3.MeanRank[i], fused3.Score[i], hits));
        }
        PathOutcome[] outcomes = [.. product.Paths, new PathOutcome(pathId, modelId, thirdHits.Count, PathState.Ok, null)];
        return new ThirdAugmentation(new SearchResponse(product.Query, augmented, outcomes, product.IndexedImages, product.TotalImages, product.Elapsed), thirdHits.Count);
    }

    private static double Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException($"ベクトルの次元が違う({a.Length} / {b.Length})");
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += (double)a[i] * b[i];
        return sum;
    }

    /// <summary>
    /// 集めた検索の結果(製品)に 3 本目を足して、run と同じ形の行(known-items・precision・scores)を作る。経路は 製品の fused・visual・fulltext、主の 3 本目の <c>third</c>、<c>fused3</c>。
    /// すべてのクエリで、今の融合の再現を確かめる(<see cref="Augment"/>)。<paramref name="reproduced"/> は、確かめたクエリの数。
    /// <paramref name="diagnostic"/>(説明文だけの埋め込み)があれば、<c>third-desc</c>・<c>fused3-desc</c> の **known-item の順位の行だけ**を足す(診断。precision・scores には入れない)。
    /// </summary>
    public static RunData Build(BenchRunner.Collected product, ThirdVectors vectors, string modelId, out int reproduced, out int thirdPopulation, ThirdVectors? diagnostic = null)
    {
        (BenchRunner.Collected main, thirdPopulation) = AugmentAll(product, vectors, modelId, PathId);
        reproduced = product.UniqueQueries.Count;

        RunData productData = BenchRunner.BuildData(product, out _);
        RunData augmentedData = BenchRunner.BuildData(main, out _);

        // 製品の行(fused・visual・fulltext)に、足した結果から third と fused3(= 足した結果の fused)を足す
        var known = new List<KnownItemRow>(productData.KnownItems);
        known.AddRange(augmentedData.KnownItems.Where(r => r.Path == PathId));
        known.AddRange(augmentedData.KnownItems.Where(r => r.Path == Metrics.Fused).Select(r => r with { Path = Fused3 }));
        var precision = new List<PrecisionRow>(productData.Precision);
        precision.AddRange(augmentedData.Precision.Where(r => r.Path == PathId));
        precision.AddRange(augmentedData.Precision.Where(r => r.Path == Metrics.Fused).Select(r => r with { Path = Fused3 }));
        var scores = new List<ScoreRow>(productData.Scores!);
        scores.AddRange(augmentedData.Scores!.Where(r => r.Path == PathId));
        scores.AddRange(augmentedData.Scores!.Where(r => r.Path == Metrics.Fused).Select(r => r with { Path = Fused3 }));

        if (diagnostic is not null)
        {
            (BenchRunner.Collected diag, _) = AugmentAll(product, diagnostic, diagnostic.Model, DiagPathId);
            RunData diagData = BenchRunner.BuildData(diag, out _);
            known.AddRange(diagData.KnownItems.Where(r => r.Path == DiagPathId));
            known.AddRange(diagData.KnownItems.Where(r => r.Path == Metrics.Fused).Select(r => r with { Path = DiagFused3 }));
        }
        return new RunData(known, precision, scores, productData.Tops);
    }

    /// <summary>すべてのクエリに 3 本目(<paramref name="pathId"/>)を足した結果(製品の Collected の Responses・ByName を差し替えたもの)と、3 本目の母集団の大きさ。</summary>
    private static (BenchRunner.Collected Collected, int Population) AugmentAll(BenchRunner.Collected product, ThirdVectors vectors, string modelId, string pathId)
    {
        var responses = new Dictionary<string, SearchResponse>(StringComparer.Ordinal);
        var byName = new Dictionary<string, Dictionary<string, SearchResult>>(StringComparer.Ordinal);
        int population = -1;
        foreach (string q in product.UniqueQueries)
        {
            if (!vectors.Queries.TryGetValue(q, out float[]? qv)) throw new InvalidDataException($"クエリの埋め込みが無い(3 本目のファイルに、このクエリの query の行が無い): 「{q}」");
            ThirdAugmentation a = Augment(product.Responses[q], qv, vectors.Docs, modelId, pathId);
            responses[q] = a.Augmented;
            byName[q] = Metrics.IndexByFileName(a.Augmented);
            if (population >= 0 && population != a.ThirdPopulation) throw new InvalidOperationException("クエリによって、3 本目の母集団の大きさが違う(想定外)");
            population = a.ThirdPopulation;
        }
        return (product with { Responses = responses, ByName = byName }, population);
    }
}
