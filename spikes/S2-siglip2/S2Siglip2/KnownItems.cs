using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace S2Siglip2;

/// <summary>known-items.tsv の1行(探したい1枚と、そのとき打つクエリ)。空のクエリは null。</summary>
public sealed record KnownItemRow(string Filename, string? QueryJa, string? QueryEn);

/// <summary>
/// known-item の評価(ADR 0037)。
/// 「探したい1枚」と「そのとき打つクエリ」の組で、1030枚の中での正解の順位を測る(recall@k は使わない)。
/// </summary>
public static class KnownItems
{
    /// <summary>
    /// known-items.tsv を読む(UTF-8、タブ区切り、1行目は列名)。必須の列は filename と query_ja。query_en は無くてもよい。
    /// クエリが空の行は、そのクエリを「無し」(null)にする。列名が想定と違えば例外。
    /// </summary>
    public static List<KnownItemRow> Read(string path)
    {
        string[] lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
        if (lines.Length == 0) throw new InvalidDataException($"{path} が空");
        string[] header = lines[0].TrimStart('﻿').Split('\t');
        int iFile = Array.IndexOf(header, "filename");
        int iJa = Array.IndexOf(header, "query_ja");
        int iEn = Array.IndexOf(header, "query_en");
        if (iFile < 0 || iJa < 0) throw new InvalidDataException($"{path} の列名に filename と query_ja が必要: {lines[0]}");
        var rows = new List<KnownItemRow>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            string[] c = lines[i].Split('\t');
            string Get(int idx) => idx >= 0 && idx < c.Length ? c[idx].Trim() : "";
            string file = Get(iFile);
            if (file.Length == 0) throw new InvalidDataException($"{path} の {i + 1} 行目: filename が空");
            string ja = Get(iJa), en = Get(iEn);
            rows.Add(new KnownItemRow(file, ja.Length == 0 ? null : ja, en.Length == 0 ? null : en));
        }
        return rows;
    }

    /// <summary>
    /// 説明文の最初の1文(補助のクエリ)。最初の「。」「！」「？」「!」「?」または改行の手前まで(終端の記号は含めない)。
    /// 終端が無ければ全体。前後の空白は除く。空・空白だけなら例外(黙って空のクエリにしない)。
    /// </summary>
    public static string FirstSentence(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        int end = description.IndexOfAny(['。', '！', '？', '!', '?', '\n', '\r']);
        string s = (end >= 0 ? description[..end] : description).Trim();
        if (s.Length == 0) throw new InvalidDataException("説明文の最初の1文が空");
        return s;
    }

    /// <summary>1つのクエリの結果。</summary>
    public sealed record RankResult(int Rank, int TiedCount, double TargetScore, int Top1Index, double Top1Score, int[] TopIndices);

    /// <summary>
    /// クエリの埋め込みと、全画像の埋め込み(どちらも L2 正規化済みで、内積 = cos)から、目標の画像の順位(1 始まり)を出す。
    /// 順位 = 目標より厳密にスコアが大きい画像の数 + 1。同点(浮動小数点で完全に等しい)の数は TiedCount(目標自身を除く)に入れる。
    /// </summary>
    public static RankResult Rank(float[] query, IReadOnlyList<float[]> images, int targetIndex, int topCount = 5)
    {
        if (targetIndex < 0 || targetIndex >= images.Count) throw new ArgumentOutOfRangeException(nameof(targetIndex));
        var scores = new double[images.Count];
        for (int i = 0; i < images.Count; i++) scores[i] = Dot(query, images[i]);
        double target = scores[targetIndex];
        int greater = 0, tied = 0;
        for (int i = 0; i < scores.Length; i++)
        {
            if (i == targetIndex) continue;
            if (scores[i] > target) greater++;
            else if (scores[i] == target) tied++;
        }
        int[] top = Enumerable.Range(0, scores.Length).OrderByDescending(i => scores[i]).ThenBy(i => i).Take(topCount).ToArray();
        return new RankResult(greater + 1, tied, target, top[0], scores[top[0]], top);
    }

    /// <summary>クエリと全画像のスコア(内積。どちらも L2 正規化済みなので cos)。</summary>
    public static double[] Scores(float[] query, IReadOnlyList<float[]> images)
    {
        var scores = new double[images.Count];
        for (int i = 0; i < images.Count; i++) scores[i] = Dot(query, images[i]);
        return scores;
    }

    private static double Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("埋め込みの長さが違う");
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += (double)a[i] * b[i];
        return s;
    }

    /// <summary>判定の集計。線は ADR 0037: 上位 20 位以内が 2/3 以上 → 使える、上位 5 位以内が 2/3 以上 → 強い、上位 20 位以内が 1/3 未満 → 使えない。</summary>
    public sealed record Verdict(int Count, int Top1, int Top5, int Top20, double MedianRank, string Label)
    {
        public double Top20Ratio => Count == 0 ? 0 : (double)Top20 / Count;
        public double Top5Ratio => Count == 0 ? 0 : (double)Top5 / Count;
    }

    public static Verdict Evaluate(IReadOnlyList<int> ranks) => EvaluateMean(ranks.Select(r => (double)r).ToList());

    /// <summary>小数の順位(同点の平均順位)用。規則は Evaluate と同じ(「≤ 20」などの比較をそのまま小数で行う)。</summary>
    public static Verdict EvaluateMean(IReadOnlyList<double> ranks)
    {
        int n = ranks.Count;
        if (n == 0) return new Verdict(0, 0, 0, 0, double.NaN, "クエリが無い");
        int t1 = ranks.Count(r => r <= 1), t5 = ranks.Count(r => r <= 5), t20 = ranks.Count(r => r <= 20);
        var sorted = ranks.OrderBy(r => r).ToList();
        double median = n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
        string label;
        if (t5 * 3 >= n * 2) label = "強い(上位5位以内が2/3以上)";
        else if (t20 * 3 >= n * 2) label = "見た目の経路として使える(上位20位以内が2/3以上)";
        else if (t20 * 3 < n) label = "使えない(上位20位以内が1/3未満)";
        else label = "どちらともいえない(上位20位以内が1/3以上2/3未満)";
        return new Verdict(n, t1, t5, t20, median, label);
    }
}

/// <summary>全画像の埋め込みの保存形式: embeddings.f32(float32 リトルエンディアン、行 = 画像)と embedding-names.tsv(index TAB filename)。</summary>
public static class EmbeddingStore
{
    public static void Write(string dir, IReadOnlyList<string> names, IReadOnlyList<float[]> embeddings)
    {
        if (names.Count != embeddings.Count) throw new ArgumentException("名前と埋め込みの数が違う");
        Directory.CreateDirectory(dir);
        using (var fs = File.Create(Path.Combine(dir, "embeddings.f32")))
        {
            var buf = new byte[Siglip2Model.EmbeddingDim * sizeof(float)];
            foreach (float[] e in embeddings)
            {
                if (e.Length != Siglip2Model.EmbeddingDim) throw new ArgumentException("埋め込みの次元が 1152 でない");
                Buffer.BlockCopy(e, 0, buf, 0, buf.Length);
                fs.Write(buf);
            }
        }
        var sb = new StringBuilder("index\tfilename\n");
        for (int i = 0; i < names.Count; i++) sb.Append(i).Append('\t').Append(names[i]).Append('\n');
        File.WriteAllText(Path.Combine(dir, "embedding-names.tsv"), sb.ToString(), new UTF8Encoding(false));
    }

    public static (List<string> Names, List<float[]> Embeddings) Read(string dir)
    {
        string[] lines = File.ReadAllLines(Path.Combine(dir, "embedding-names.tsv"), new UTF8Encoding(false, true));
        var names = lines.Skip(1).Where(l => l.Length > 0).Select(l => l.Split('\t', 2)[1]).ToList();
        byte[] bytes = File.ReadAllBytes(Path.Combine(dir, "embeddings.f32"));
        int rowBytes = Siglip2Model.EmbeddingDim * sizeof(float);
        if (bytes.Length != names.Count * rowBytes) throw new InvalidDataException($"embeddings.f32 のサイズ {bytes.Length} が、名前 {names.Count} 件 × {rowBytes} と違う");
        var embs = new List<float[]>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            var e = new float[Siglip2Model.EmbeddingDim];
            Buffer.BlockCopy(bytes, i * rowBytes, e, 0, rowBytes);
            embs.Add(e);
        }
        return (names, embs);
    }
}
