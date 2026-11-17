using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ohanami.Bench;

/// <summary>
/// known-item の 1 件: 探したい 1 枚(ファイル名)と、そのとき打つクエリ。<paramref name="Set"/> は集合の名前
/// ("visual" = 見た目の 45 件、"dialogue" = セリフの 10 件、"one-image" = 同じ 1 枚を複数の語で探す、任意のファイル <see cref="BenchInputs.OneImageKnownItemsFile"/>)。
/// </summary>
internal sealed record KnownItem(string Set, string Filename, string Query);

/// <summary>
/// spikes/S2-siglip2 の入力ファイル(known-items.tsv、known-items-dialogue.tsv、qrels.tsv)の読み込み。
/// 形式が想定と違えば例外(黙って読み飛ばさない)。ファイル名は Unicode の NFC にそろえる(Windows のファイル名と TSV の綴りの差を避ける)。
/// </summary>
internal static class BenchInputs
{
    /// <summary>同じ 1 枚(探したい画像)を、複数の語で探す known-item。known-items.tsv と同じ形式で、spikes/S2-siglip2 に置く任意のファイル(無ければ測らない)。集合の名前は <see cref="OneImageSet"/>。</summary>
    public const string OneImageKnownItemsFile = "known-items-one-image.tsv";

    public const string OneImageSet = "one-image";

    /// <summary>
    /// 文脈で探す known-item(利用者が記憶で書く 10 件ほど)。形式は known-items.tsv と同じで、spikes/S2-siglip2 に置く任意のファイル(無ければ測らない)。
    /// 集合の名前は <see cref="ContextSet"/>。
    /// </summary>
    public const string ContextKnownItemsFile = "known-items-context.tsv";

    public const string ContextSet = "context";

    /// <summary>known-items.tsv・known-items-dialogue.tsv の 2 つの集合(visual・dialogue)のほかに、任意のファイルから読む集合(one-image・context)。要約と判定の対象にする集合名。</summary>
    public static readonly string[] OptionalSets = [OneImageSet, ContextSet];

    /// <summary>
    /// 測る入力のすべて: known-item(見た目・セリフ、任意の one-image・context。この順)と qrels。<paramref name="Summary"/> は、読んだ件数の 1 行(コンソール用)。
    /// 任意のファイルは、無ければ空(あって壊れていれば例外)。
    /// </summary>
    public sealed record InputSet(IReadOnlyList<KnownItem> Items, Qrels Qrels, string Summary);

    /// <summary>spikes/S2-siglip2 のディレクトリから、測る入力を読む(<see cref="InputSet"/>)。</summary>
    public static InputSet Load(string spikesDirectory)
    {
        List<KnownItem> visual = ReadKnownItems(Path.Combine(spikesDirectory, "known-items.tsv"), "visual", out int visualSkipped);
        List<KnownItem> dialogue = ReadKnownItems(Path.Combine(spikesDirectory, "known-items-dialogue.tsv"), "dialogue", out int dialogueSkipped);
        string oneImagePath = Path.Combine(spikesDirectory, OneImageKnownItemsFile);
        List<KnownItem> oneImage = ReadOptionalKnownItems(oneImagePath, OneImageSet, out int oneImageSkipped);
        string contextPath = Path.Combine(spikesDirectory, ContextKnownItemsFile);
        List<KnownItem> context = ReadOptionalKnownItems(contextPath, ContextSet, out int contextSkipped);
        Qrels qrels = ReadQrels(Path.Combine(spikesDirectory, "qrels.tsv"));
        string summary = $"入力: known-item 見た目 {visual.Count} 件(クエリなしで除外 {visualSkipped})、セリフ {dialogue.Count} 件(除外 {dialogueSkipped})、qrels {qrels.Count} 組・{qrels.Queries.Count()} クエリ"
            + (File.Exists(oneImagePath) ? $"、one-image {oneImage.Count} 件(除外 {oneImageSkipped})" : $"(one-image のファイル {OneImageKnownItemsFile} は無い。測らない)")
            + (File.Exists(contextPath) ? $"、context {context.Count} 件(除外 {contextSkipped})" : $"(context のファイル {ContextKnownItemsFile} は無い。測らない)");
        return new InputSet([.. visual, .. dialogue, .. oneImage, .. context], qrels, summary);
    }

    public static string Nfc(string s) => s.Normalize(NormalizationForm.FormC);

    /// <summary>
    /// 任意の known-item のファイル。<paramref name="path"/> が無ければ空(<paramref name="skippedWithoutQuery"/> は 0)。あれば <see cref="ReadKnownItems"/> と同じ規則で読む
    /// (形式の誤りは例外。「無い」と「壊れている」を区別する)。
    /// </summary>
    public static List<KnownItem> ReadOptionalKnownItems(string path, string set, out int skippedWithoutQuery)
    {
        if (!File.Exists(path)) { skippedWithoutQuery = 0; return []; }
        return ReadKnownItems(path, set, out skippedWithoutQuery);
    }

    /// <summary>
    /// known-items.tsv の形式(UTF-8、タブ区切り、1 行目は列名で filename と query_ja が必須)を読む。query_ja が空の行は読み飛ばし、その数を
    /// <paramref name="skippedWithoutQuery"/> に返す(日本語のクエリが無い行は、この集合では測れない)。
    /// </summary>
    public static List<KnownItem> ReadKnownItems(string path, string set, out int skippedWithoutQuery)
    {
        string[] lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
        if (lines.Length == 0) throw new InvalidDataException($"{path} が空");
        string[] header = lines[0].TrimStart('﻿').TrimEnd('\r').Split('\t');
        int iFile = Array.IndexOf(header, "filename");
        int iJa = Array.IndexOf(header, "query_ja");
        if (iFile < 0 || iJa < 0) throw new InvalidDataException($"{path} の 1 行目に filename と query_ja の列が必要: {lines[0]}");
        var items = new List<KnownItem>();
        skippedWithoutQuery = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            string[] c = line.Split('\t');
            string Get(int idx) => idx < c.Length ? c[idx].Trim() : "";
            string file = Get(iFile), ja = Get(iJa);
            if (file.Length == 0) throw new InvalidDataException($"{path} の {i + 1} 行目: filename が空");
            if (ja.Length == 0) { skippedWithoutQuery++; continue; }
            items.Add(new KnownItem(set, Nfc(file), ja));
        }
        return items;
    }

    public static Qrels ReadQrels(string path) => Qrels.Parse(File.ReadAllLines(path, new UTF8Encoding(false, true)), path);
}

/// <summary>qrels の 1 行。<paramref name="Source"/> はその判定をどこで付けたか(例: "precision-page:20261105113812"、"judge-page:20261106103000")。</summary>
internal sealed record QrelsRow(string Query, string File, bool Relevant, string Source);

/// <summary>
/// qrels.tsv(クエリ × 画像 × 当たり/外れの判定)。列は query_ja / filename / relevant(1 か 0)/ source。
/// 同じ (クエリ, 画像) が 2 回あれば、判定が同じなら 1 件にまとめ(source は先に出たほう)、違えば例外(黙ってどちらかを選ばない)。
/// source は保持して、<see cref="Render"/> で書き戻せる。
/// </summary>
internal sealed class Qrels
{
    public const string Header = "query_ja\tfilename\trelevant\tsource";

    private readonly Dictionary<(string Query, string File), (bool Relevant, string Source)> _map = new();

    public int Count => _map.Count;

    public IEnumerable<string> Queries => _map.Keys.Select(k => k.Query).Distinct(StringComparer.Ordinal);

    public IEnumerable<string> Files => _map.Keys.Select(k => k.File).Distinct(StringComparer.Ordinal);

    /// <summary>判定済みならその結果(当たり = true)。未判定は null。</summary>
    public bool? TryGet(string query, string file) => _map.TryGetValue((query, BenchInputs.Nfc(file)), out var r) ? r.Relevant : null;

    /// <summary>全行(読み込んだ順)。</summary>
    public IEnumerable<QrelsRow> Rows => _map.Select(kv => new QrelsRow(kv.Key.Query, kv.Key.File, kv.Value.Relevant, kv.Value.Source));

    /// <summary>qrels.tsv の形に書き出す(1 行目は列名、末尾は改行)。タブ・改行が入っている行があれば例外。</summary>
    public string Render()
    {
        var sb = new StringBuilder(Header).Append('\n');
        foreach (QrelsRow r in Rows)
        {
            foreach (string cell in new[] { r.Query, r.File, r.Source })
                if (cell.Contains('\t') || cell.Contains('\n') || cell.Contains('\r')) throw new ArgumentException($"タブか改行が入っている(TSV にできない): {cell}");
            sb.Append(r.Query).Append('\t').Append(r.File).Append('\t').Append(r.Relevant ? '1' : '0').Append('\t').Append(r.Source).Append('\n');
        }
        return sb.ToString();
    }

    public static Qrels Parse(IEnumerable<string> lines, string source = "(入力)")
    {
        List<string> list = lines.Select(l => l.TrimEnd('\r')).ToList();
        if (list.Count == 0) throw new InvalidDataException($"{source} が空");
        if (list[0].TrimStart('﻿') != Header) throw new InvalidDataException($"{source} の 1 行目は「{Header}」(タブ区切り)であること: {list[0]}");
        var result = new Qrels();
        for (int i = 1; i < list.Count; i++)
        {
            string line = list[i];
            if (line.Length == 0 || line == Header) continue;
            string[] c = line.Split('\t');
            if (c.Length != 4) throw new InvalidDataException($"{source} の {i + 1} 行目: 列が 4 つでない: {line}");
            string query = c[0], file = BenchInputs.Nfc(c[1].Trim());
            if (query.Length == 0) throw new InvalidDataException($"{source} の {i + 1} 行目: query_ja が空");
            if (file.Length == 0) throw new InvalidDataException($"{source} の {i + 1} 行目: filename が空");
            bool relevant = c[2].Trim() switch
            {
                "1" => true,
                "0" => false,
                _ => throw new InvalidDataException($"{source} の {i + 1} 行目: relevant は 1 か 0: 「{c[2]}」"),
            };
            if (result._map.TryGetValue((query, file), out var old))
            {
                if (old.Relevant != relevant) throw new InvalidDataException($"{source} の {i + 1} 行目: 「{query}」×「{file}」の判定が食い違う");
                continue;
            }
            result._map[(query, file)] = (relevant, c[3]);
        }
        return result;
    }
}
