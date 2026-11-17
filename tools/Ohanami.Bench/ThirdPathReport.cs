using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Ohanami.Bench;

/// <summary>今の外れ(融合で 20 位より下の known-item)の分類の割り当て 1 行(測る前にリポジトリの外のファイルに固定する)。</summary>
internal sealed record ThirdClassRow(string Set, string Query, string Filename, string Class);

/// <summary>
/// 分類の割り当て 1 行(集合 + 番号の形。測る前に固定した <c>classification.tsv</c> の形。ファイル名・クエリを含まない)。<paramref name="Index"/> は、集合の中の known-item の並び(ファイルの行の順)の 1 始まりの番号。
/// <paramref name="FusedRank"/> は、固定したときの融合の順位(分類を作ったときの run のもの)。番号の数え方を取り違えていないことの確かめに使う(今の融合の順位と一致しなければ止まる)。
/// <paramref name="Doubtful"/> は、分類に迷ったもの。
/// </summary>
internal sealed record ThirdClassIndexRow(string Set, int Index, double FusedRank, string Class, bool Doubtful);

/// <summary>known-item 1 件の、経路ごとの順位(同点は平均順位。経路の母集団に入っていなければ null)。<paramref name="Index"/> は集合の中の 1 始まりの番号。</summary>
/// <param name="ThirdDesc">診断(説明文だけの入れ方)の 3 本目の単独の順位(診断の埋め込みを渡さなければ null)。</param>
/// <param name="Fused3Desc">診断(説明文だけの入れ方)を足した融合の順位。</param>
internal sealed record ThirdEntry(string Set, int Index, string Query, string Filename, double? Visual, double? Fulltext, double? Third, double? Fused, double? Fused3, string Class,
    double? ThirdDesc = null, double? Fused3Desc = null, bool Doubtful = false)
{
    public const string HitClass = "(今の当たり)";

    /// <summary>今の融合で 20 位より下(圏外を含む)= 今の外れ。</summary>
    public bool Outlier => !(Fused is <= ThirdPathReport.Top);
}

/// <summary>集合ごとの数え上げ。U = 今の外れのうち 3 本目の単独で 20 位以内、H3 = 今の当たりのうち 3 本目の単独で 20 位以内、G = 外れから入った数(fused3 で 20 位以内)、L = 当たりから落ちた数。</summary>
internal sealed record ThirdSetCounts(string Set, int Items, int Outliers, int OutliersThirdTop, int Hits, int HitsThirdTop, int Gained, int Lost);

/// <summary>precision@10 の 1 つの経路の合計(未判定は外れとして数えた下限。上限は未判定がすべて当たりのとき)。</summary>
internal sealed record ThirdPrecision(string Path, int Queries, int Relevant, int JudgedMiss, int Unjudged, int Returned)
{
    public double Lower => Queries == 0 ? 0 : (double)Relevant / (Queries * Metrics.PrecisionTop);

    public double Upper => Queries == 0 ? 0 : (double)(Relevant + Unjudged) / (Queries * Metrics.PrecisionTop);
}

/// <summary>全文の返した件数で分けた層の、fused3 − fused の当たりの差(クエリごとの差の合計)と、上がったクエリ数・下がったクエリ数。</summary>
internal sealed record ThirdLayer(string Name, int Queries, int Delta, int Up, int Down);

/// <summary>3 本目の試し測りの数字(ADR 0035)。</summary>
internal sealed record ThirdPathStats(
    IReadOnlyList<ThirdEntry> Entries,
    IReadOnlyList<ThirdSetCounts> Sets,
    IReadOnlyDictionary<string, ThirdSetCounts> OutlierClasses,
    ThirdPrecision Fused, ThirdPrecision Fused3, ThirdPrecision Third,
    int Queries, int Delta, int Up, int Down,
    IReadOnlyList<ThirdLayer> Layers,
    double DeltaSd, double DeltaSe, double DeltaSeInPrecision,
    int UnjudgedQueries, int ClassesNotOutlier,
    IReadOnlyList<ThirdSetCounts>? DiagnosticSets = null)
{
    /// <summary>診断(説明文だけ)の埋め込みがあったか。</summary>
    public bool HasDiagnostic => DiagnosticSets is not null;
}

/// <summary>
/// 3 本目の試し測りの集計と文章(ADR 0035)。<b>判定はしない</b>: 数字を並べるだけで、「効いた」「効き目が小さい」の書き分けや採否は、人がこの数字を見て決める(自動で決めない)。
/// 文章には、クエリ・ファイル名を入れない(集合 + 番号と数字だけ。私物の画像から来る文字を、報告に出さないため)。
/// </summary>
internal static class ThirdPathReport
{
    /// <summary>「上位 20 位以内」の線(known-item の集計と同じ)。</summary>
    public const double Top = 20;

    // ---------------------------------------------------------------- 分類の割り当ての読み込み

    public const string ClassHeader = "set\tquery\tfilename\tclass";

    /// <summary>分類の割り当て(UTF-8、タブ区切り、1 行目は set / query / filename / class)を読む。同じ (set, query, filename) が 2 行あれば例外。</summary>
    public static IReadOnlyList<ThirdClassRow> ReadClasses(string path)
    {
        string[] lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
        if (lines.Length == 0 || lines[0].TrimStart('﻿').TrimEnd('\r') != ClassHeader) throw new InvalidDataException($"{path} の 1 行目が想定と違う(想定「{ClassHeader}」)");
        var rows = new List<ThirdClassRow>();
        var seen = new HashSet<(string, string, string)>();
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            string[] c = line.Split('\t');
            if (c.Length != 4 || c.Any(x => x.Trim().Length == 0)) throw new InvalidDataException($"{path} の {i + 1} 行目: 列が 4 個で、どれも空でない必要がある");
            var row = new ThirdClassRow(c[0].Trim(), c[1].Trim(), BenchInputs.Nfc(c[2].Trim()), c[3].Trim());
            if (!seen.Add((row.Set, row.Query, row.Filename))) throw new InvalidDataException($"{path} の {i + 1} 行目: 同じ (set, query, filename) が 2 回ある");
            rows.Add(row);
        }
        return rows;
    }

    public const string ClassIndexHeader = "set\tindex\tfused_rank_at_classification\tclass\tdoubtful";

    /// <summary>
    /// 集合 + 番号の形(<see cref="ClassIndexHeader"/>。UTF-8、タブ区切り)の分類の割り当てを読む。index は 1 以上の整数、fused_rank_at_classification は数、class は空でない、doubtful は 0 か 1。
    /// 同じ (set, index) が 2 行あれば例外。
    /// </summary>
    public static IReadOnlyList<ThirdClassIndexRow> ReadClassesByIndex(string path)
    {
        string[] lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
        if (lines.Length == 0 || lines[0].TrimStart('﻿').TrimEnd('\r') != ClassIndexHeader) throw new InvalidDataException($"{path} の 1 行目が想定と違う(想定「{ClassIndexHeader}」)");
        var rows = new List<ThirdClassIndexRow>();
        var seen = new HashSet<(string, int)>();
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            string[] c = line.Split('\t');
            if (c.Length != 5) throw new InvalidDataException($"{path} の {i + 1} 行目: 列が 5 個でない({c.Length} 個)");
            if (c[0].Trim().Length == 0 || c[3].Trim().Length == 0) throw new InvalidDataException($"{path} の {i + 1} 行目: set と class は空でない必要がある");
            if (!int.TryParse(c[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 1) throw new InvalidDataException($"{path} の {i + 1} 行目: index が 1 以上の整数でない");
            if (!double.TryParse(c[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double rank)) throw new InvalidDataException($"{path} の {i + 1} 行目: fused_rank_at_classification が数でない");
            bool doubtful = c[4].Trim() switch { "0" => false, "1" => true, _ => throw new InvalidDataException($"{path} の {i + 1} 行目: doubtful は 0 か 1") };
            var row = new ThirdClassIndexRow(c[0].Trim(), index, rank, c[3].Trim(), doubtful);
            if (!seen.Add((row.Set, row.Index))) throw new InvalidDataException($"{path} の {i + 1} 行目: 同じ (set, index) が 2 回ある");
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// 分類の割り当てのファイルを、1 行目で形を見分けて読む(<see cref="ClassHeader"/> = query・filename で指す形、<see cref="ClassIndexHeader"/> = 集合 + 番号の形)。
    /// 戻り値の片方だけが null でない。どちらの 1 行目でもなければ例外。
    /// </summary>
    public static (IReadOnlyList<ThirdClassRow>? ByName, IReadOnlyList<ThirdClassIndexRow>? ByIndex) ReadClassAssignments(string path)
    {
        string first = File.ReadLines(path, new UTF8Encoding(false, true)).FirstOrDefault()?.TrimStart('﻿').TrimEnd('\r') ?? "";
        if (first == ClassIndexHeader) return (null, ReadClassesByIndex(path));
        if (first == ClassHeader) return (ReadClasses(path), null);
        throw new InvalidDataException($"{path} の 1 行目が、分類の割り当ての 2 つの形のどちらでもない(「{ClassHeader}」か「{ClassIndexHeader}」)");
    }

    // ---------------------------------------------------------------- 集計

    /// <summary>
    /// <paramref name="data"/>(経路: fused・visual・fulltext・third・fused3)から、数字を集める。<paramref name="classes"/> があれば、今の外れに分類を付ける
    /// (割り当てが無い外れがあれば例外。割り当てにある (set, query, filename) が known-item に無くても例外。今は外れでない割り当ては、数えて報告する)。
    /// </summary>
    public static ThirdPathStats Compute(RunData data, Qrels qrels, IReadOnlyList<KnownItem> items, IReadOnlyList<ThirdClassRow>? classes, IReadOnlyList<ThirdClassIndexRow>? classesByIndex = null)
    {
        if (classes is not null && classesByIndex is not null) throw new ArgumentException("分類の割り当ては、query・filename の形か、集合 + 番号の形の、どちらか一方");
        var rank = new Dictionary<(string Set, string Query, string File, string Path), double?>();
        foreach (KnownItemRow r in data.KnownItems) rank[(r.Set, r.Query, r.Filename, r.Path)] = r.Rank;
        double? Rank(KnownItem it, string path) => rank.TryGetValue((it.Set, it.Query, it.Filename, path), out double? v) ? v : throw new InvalidDataException($"known-item の順位の行が無い(経路 {path}、集合 {it.Set})");

        var classOf = new Dictionary<(string, string, string), string>();
        if (classes is not null)
        {
            var keys = items.Select(i => (i.Set, i.Query, i.Filename)).ToHashSet();
            int unknown = classes.Count(c => !keys.Contains((c.Set, c.Query, c.Filename)));
            if (unknown > 0) throw new InvalidDataException($"分類の割り当てに、known-item に無い行が {unknown} 件ある(綴り・集合の名前の違い)");
            foreach (ThirdClassRow c in classes) classOf[(c.Set, c.Query, c.Filename)] = c.Class;
        }

        // 集合 + 番号の形: 番号は、集合の中の known-item の並び(1 始まり)。行の存在と、固定したときの融合の順位が今と一致することを確かめる(番号の数え方の取り違えを、黙って通さない)
        var byIndex = new Dictionary<(string Set, int Index), ThirdClassIndexRow>();
        var coveredSets = new HashSet<string>(StringComparer.Ordinal);
        if (classesByIndex is not null)
        {
            foreach (ThirdClassIndexRow r in classesByIndex) { byIndex[(r.Set, r.Index)] = r; coveredSets.Add(r.Set); }
            foreach (ThirdClassIndexRow r in classesByIndex)
                if (items.Count(i => i.Set == r.Set) < r.Index) throw new InvalidDataException($"分類の割り当ての 集合 {r.Set} の番号 {r.Index} が、known-item の数({items.Count(i => i.Set == r.Set)})を超えている");
        }

        bool hasDiagnostic = data.KnownItems.Any(r => r.Path == ThirdPath.DiagPathId);
        var entries = new List<ThirdEntry>();
        var setIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int notOutlierButClassed = 0, unassigned = 0, rankMismatches = 0;
        string? firstMismatch = null;
        foreach (KnownItem it in items)
        {
            setIndex[it.Set] = setIndex.GetValueOrDefault(it.Set) + 1;
            double? fused = Rank(it, Metrics.Fused);
            bool outlier = !(fused is <= Top);
            string cls = ThirdEntry.HitClass;
            bool doubtful = false;
            if (classesByIndex is not null)
            {
                if (byIndex.TryGetValue((it.Set, setIndex[it.Set]), out ThirdClassIndexRow? ci))
                {
                    if (fused is null || Math.Abs(fused.Value - ci.FusedRank) > 1e-9)
                    {
                        rankMismatches++;
                        firstMismatch ??= $"集合 {it.Set} の番号 {ci.Index}(ファイル {ci.FusedRank.ToString("0.#", CultureInfo.InvariantCulture)}、今 {(fused is null ? "圏外" : fused.Value.ToString("0.#", CultureInfo.InvariantCulture))})";
                    }
                    if (outlier) { cls = ci.Class; doubtful = ci.Doubtful; }
                    else notOutlierButClassed++;
                }
                else if (outlier)
                {
                    if (coveredSets.Contains(it.Set)) { unassigned++; cls = "(割り当てなし)"; }
                    else cls = "(未分類。割り当ての無い集合)";   // 割り当ての範囲の外の集合(例: context)の外れは、止めずに「未分類」にする
                }
            }
            else if (outlier)
            {
                if (classes is null) cls = "(分類なし)";
                else if (classOf.TryGetValue((it.Set, it.Query, it.Filename), out string? c)) cls = c;
                else { unassigned++; cls = "(割り当てなし)"; }
            }
            else if (classOf.ContainsKey((it.Set, it.Query, it.Filename))) notOutlierButClassed++;
            entries.Add(new ThirdEntry(it.Set, setIndex[it.Set], it.Query, it.Filename, Rank(it, "visual"), Rank(it, "fulltext"), Rank(it, ThirdPath.PathId), fused, Rank(it, ThirdPath.Fused3), cls,
                hasDiagnostic ? Rank(it, ThirdPath.DiagPathId) : null, hasDiagnostic ? Rank(it, ThirdPath.DiagFused3) : null, doubtful));
        }
        if (rankMismatches > 0)
            throw new InvalidDataException($"分類の割り当ての fused_rank_at_classification が、今の融合の順位と一致しない {rankMismatches} 件(最初: {firstMismatch})。番号の数え方か、融合が分類を作ったときから変わっている。止まる");
        if (unassigned > 0) throw new InvalidDataException($"今の外れのうち、分類の割り当てが無いものが {unassigned} 件ある(集合と番号は、最初の表の「(割り当てなし)」)。測る前に固定したファイルに足す");

        ThirdSetCounts Counts(string name, IEnumerable<ThirdEntry> es, Func<ThirdEntry, double?>? third = null, Func<ThirdEntry, double?>? fused3 = null)
        {
            third ??= e => e.Third;
            fused3 ??= e => e.Fused3;
            ThirdEntry[] a = es.ToArray();
            ThirdEntry[] outliers = a.Where(e => e.Outlier).ToArray();
            ThirdEntry[] hits = a.Where(e => !e.Outlier).ToArray();
            return new ThirdSetCounts(name, a.Length, outliers.Length, outliers.Count(e => third(e) is <= Top), hits.Length, hits.Count(e => third(e) is <= Top),
                outliers.Count(e => fused3(e) is <= Top), hits.Count(e => !(fused3(e) is <= Top)));
        }
        string[] setNames = [.. entries.Select(e => e.Set).Distinct(StringComparer.Ordinal)];
        List<ThirdSetCounts> sets = [.. setNames.Select(s => Counts(s, entries.Where(e => e.Set == s))), Counts("全体", entries)];
        List<ThirdSetCounts>? diagSets = hasDiagnostic
            ? [.. setNames.Select(s => Counts(s, entries.Where(e => e.Set == s), e => e.ThirdDesc, e => e.Fused3Desc)), Counts("全体", entries, e => e.ThirdDesc, e => e.Fused3Desc)]
            : null;
        var classCounts = new Dictionary<string, ThirdSetCounts>(StringComparer.Ordinal);
        // 分類ごと。迷いのあるもの(doubtful)は「A?」のように別の行にし、迷いの合計も出す(迷いを含めた/除いた読みを、どちらも並べられるように)
        string KeyOf(ThirdEntry e) => e.Doubtful ? e.Class + "?" : e.Class;
        foreach (string cls in entries.Where(e => e.Outlier).Select(KeyOf).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            classCounts[cls] = Counts(cls, entries.Where(e => e.Outlier && KeyOf(e) == cls));
        if (entries.Any(e => e.Outlier && e.Doubtful)) classCounts["(迷いの合計)"] = Counts("(迷いの合計)", entries.Where(e => e.Outlier && e.Doubtful));

        // ---- precision@10
        List<string> queries = [.. qrels.Queries.Order(StringComparer.Ordinal)];
        PrecisionRow Prec(string q, string path) => data.Precision.SingleOrDefault(r => r.Query == q && r.Path == path) ?? throw new InvalidDataException($"precision の行が無い(経路 {path})");
        ThirdPrecision Sum(string path) => new(path, queries.Count, queries.Sum(q => Prec(q, path).Relevant), queries.Sum(q => Prec(q, path).JudgedMiss), queries.Sum(q => Prec(q, path).Unjudged), queries.Sum(q => Prec(q, path).Returned));

        var diffs = new List<(string Query, int D, int FulltextReturned)>();
        foreach (string q in queries) diffs.Add((q, Prec(q, ThirdPath.Fused3).Relevant - Prec(q, Metrics.Fused).Relevant, Prec(q, "fulltext").Returned));
        int delta = diffs.Sum(x => x.D);
        double mean = diffs.Count == 0 ? 0 : (double)delta / diffs.Count;
        double sd = diffs.Count < 2 ? 0 : Math.Sqrt(diffs.Sum(x => (x.D - mean) * (x.D - mean)) / (diffs.Count - 1));
        double se = sd * Math.Sqrt(diffs.Count);                                   // 当たりの合計の差(件数)の標準誤差 = クエリごとの差の SD × √クエリ数
        double seP = diffs.Count == 0 ? 0 : sd / (Metrics.PrecisionTop * Math.Sqrt(diffs.Count));   // precision@10 の平均の差の標準誤差
        ThirdLayer Layer(string name, Func<int, bool> take)
        {
            var l = diffs.Where(x => take(x.FulltextReturned)).ToArray();
            return new ThirdLayer(name, l.Length, l.Sum(x => x.D), l.Count(x => x.D > 0), l.Count(x => x.D < 0));
        }
        ThirdLayer[] layers = [Layer("全文 0 件", n => n == 0), Layer("全文 1〜9 件", n => n is >= 1 and <= 9), Layer("全文 10 件", n => n >= 10)];
        int unjudgedQueries = queries.Count(q => Prec(q, ThirdPath.Fused3).Unjudged > 0);

        return new ThirdPathStats(entries, sets, classCounts, Sum(Metrics.Fused), Sum(ThirdPath.Fused3), Sum(ThirdPath.PathId),
            queries.Count, delta, diffs.Count(x => x.D > 0), diffs.Count(x => x.D < 0), layers, sd, se, seP, unjudgedQueries, notOutlierButClassed, diagSets);
    }

    // ---------------------------------------------------------------- 文章

    private static string R(double? r) => r is null ? "-" : r.Value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>summary に出す文章。クエリ・ファイル名は入れない。</summary>
    public static string Render(ThirdPathStats s, string model, int dim, int thirdPopulation, int totalImages, int reproducedQueries, IReadOnlyList<string>? notes = null, string? variant = null, int? diagnosticPopulation = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[3 本目の経路の試し測り(ADR 0035)。数字を並べるだけで、判定・採否はしない。クエリ・ファイル名は書かない(集合と番号)]");
        sb.AppendLine($"  モデル: {model}(次元 {dim})。主の入れ方: {variant ?? "(meta.json に variant が無い)"}。3 本目の母集団: 説明文の埋め込みがある画像 {thirdPopulation} / {totalImages} 枚");
        sb.AppendLine($"  今の融合(見た目 + 全文)の再現: 同じ RankedPath.Rrf(k = 60、重みなし)で作り直した順位とスコアが、製品の fused と {reproducedQueries} クエリのすべてで一致した");
        if (notes is not null) foreach (string n in notes) sb.AppendLine("  " + n);
        sb.AppendLine();

        sb.AppendLine("[最初の表: known-item ごとの順位(同点は平均順位。- は経路の母集団に入っていない)。fused = 今の融合、fused3 = 3 本目を足した融合]");
        sb.AppendLine("  集合 #   見た目   全文  3本目  fused  fused3  分類");
        foreach (ThirdEntry e in s.Entries)
            sb.AppendLine($"  {e.Set,-8}{e.Index,2}  {R(e.Visual),7} {R(e.Fulltext),6} {R(e.Third),6} {R(e.Fused),6} {R(e.Fused3),7}  {e.Class}");
        sb.AppendLine();

        sb.AppendLine($"[数え上げ(上位 {Top:0} 位以内)。U = 今の外れのうち 3 本目の単独で 20 位以内、H3 = 今の当たりのうち 3 本目の単独で 20 位以内、G = 外れから 20 位以内に入った数、L = 当たりから 20 位の外へ落ちた数]");
        sb.AppendLine("  集合       件数  外れ  U   当たり  H3   G   L");
        foreach (ThirdSetCounts c in s.Sets)
            sb.AppendLine($"  {c.Set,-10}{c.Items,4}  {c.Outliers,4}  {c.OutliersThirdTop,2}  {c.Hits,5}  {c.HitsThirdTop,3}  {c.Gained,2}  {c.Lost,2}");
        if (s.OutlierClasses.Count > 0)
        {
            sb.AppendLine("  今の外れの分類ごと:");
            sb.AppendLine("  分類                 外れ  U   G");
            foreach ((string name, ThirdSetCounts c) in s.OutlierClasses)
                sb.AppendLine($"  {name,-20}{c.Outliers,4}  {c.OutliersThirdTop,2}  {c.Gained,2}");
        }
        if (s.ClassesNotOutlier > 0) sb.AppendLine($"  注意: 割り当てにあるが、今は外れでない known-item が {s.ClassesNotOutlier} 件ある");
        sb.AppendLine();

        sb.AppendLine($"[precision@10: qrels {s.Queries} クエリ。未判定は外れとして数えた下限(上限は未判定がすべて当たり)]");
        foreach (ThirdPrecision p in new[] { s.Fused, s.Fused3, s.Third })
            sb.AppendLine($"  {p.Path,-7} 当たり {p.Relevant,3}  判定済みの外れ {p.JudgedMiss,3}  未判定 {p.Unjudged,2}  返した件数 {p.Returned,3}  下限 {p.Lower:F3}  上限 {p.Upper:F3}");
        sb.AppendLine($"  fused3 − fused の当たり: Δ = {s.Delta:+0;-0;0}、上がったクエリ {s.Up}、下がったクエリ {s.Down}(差の標準偏差 {s.DeltaSd:F2}、当たりの合計の差の標準誤差 {s.DeltaSe:F1} 件 = precision@10 で ±{s.DeltaSeInPrecision:F3})");
        sb.AppendLine("  全文の返した件数の層ごと(クエリごとの fused3 − fused の当たりの差):");
        foreach (ThirdLayer l in s.Layers)
            sb.AppendLine($"    {l.Name,-12} クエリ {l.Queries,2}  Δ = {l.Delta:+0;-0;0}  上がった {l.Up}  下がった {l.Down}");
        // 注意書き(判定は数字を見せる前に頼む)は、未判定が残っているときだけ。0 なら、事実だけを書く
        sb.AppendLine(s.Fused3.Unjudged > 0
            ? $"  fused3 の上位 10 件に未判定がある: {s.Fused3.Unjudged} 組({s.UnjudgedQueries} クエリ)。判定は、この数字を見せる前に、判定する人へ頼む"
            : "  fused3 の上位 10 件の未判定: 0 組");
        sb.AppendLine();

        if (s.DiagnosticSets is { } diag)
        {
            sb.AppendLine($"[診断(後知恵): 説明文だけの入れ方(third-desc・fused3-desc)。known-item の順位だけ。precision・判定は無い。主の判断には使わない。3 本目の母集団 {diagnosticPopulation?.ToString(CultureInfo.InvariantCulture) ?? "?"} 枚]");
            sb.AppendLine("  集合 #   3本目(説明文のみ)  fused3(説明文のみ)");
            foreach (ThirdEntry e in s.Entries)
                sb.AppendLine($"  {e.Set,-8}{e.Index,2}  {R(e.ThirdDesc),16} {R(e.Fused3Desc),18}");
            sb.AppendLine("  診断の入れ方での数え上げ(U・H3・G・L の意味は上と同じ。外れ・当たりは、今の融合のまま):");
            sb.AppendLine("  集合       件数  外れ  U   当たり  H3   G   L");
            foreach (ThirdSetCounts c in diag)
                sb.AppendLine($"  {c.Set,-10}{c.Items,4}  {c.Outliers,4}  {c.OutliersThirdTop,2}  {c.Hits,5}  {c.HitsThirdTop,3}  {c.Gained,2}  {c.Lost,2}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>known-item 1 件ごとの詳細(クエリ・ファイル名つき。私物の画像から来る文字を含むので、リポジトリの外に置く)の TSV。</summary>
    public static string RenderEntriesTsv(ThirdPathStats s)
    {
        var sb = new StringBuilder("set\tn\tquery\tfilename\tclass\tvisual\tfulltext\tthird\tfused\tfused3\tthird_desc\tfused3_desc\n");
        foreach (ThirdEntry e in s.Entries)
        {
            foreach (string cell in new[] { e.Set, e.Query, e.Filename, e.Class })
                if (cell.Contains('\t') || cell.Contains('\n') || cell.Contains('\r')) throw new InvalidDataException("タブか改行が入っている: " + cell);
            sb.Append(e.Set).Append('\t').Append(e.Index.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(e.Query).Append('\t').Append(e.Filename).Append('\t').Append(e.Class);
            foreach (double? r in new[] { e.Visual, e.Fulltext, e.Third, e.Fused, e.Fused3, e.ThirdDesc, e.Fused3Desc })
                sb.Append('\t').Append(r is null ? "" : r.Value.ToString("R", CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
