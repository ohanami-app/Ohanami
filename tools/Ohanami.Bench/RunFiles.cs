using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Ohanami.Bench;

/// <summary>
/// 1 回の実行の結果(クエリごとの表)。run のフォルダに TSV で保存し、比較のときに読み戻す。
/// <paramref name="Scores"/> は経路ごとのスコアの行。古い形式の run・基準線には無い(null)。比較(<see cref="Comparison"/>)は使わない。
/// </summary>
internal sealed record RunData(IReadOnlyList<KnownItemRow> KnownItems, IReadOnlyList<PrecisionRow> Precision, IReadOnlyList<ScoreRow>? Scores = null);

/// <summary>
/// 実行結果の TSV(UTF-8 BOM なし、タブ区切り、1 行目は列名)。経路は行の値(path 列)なので、経路が増えても列は変わらず、行が増える。
/// <list type="bullet">
/// <item>known-items.tsv: set / query / filename / path / rank(経路の母集団に入っていなければ空)</item>
/// <item>precision.tsv: query / path / relevant / judged_miss / unjudged / returned / top10(ファイル名を '|' でつなぐ)</item>
/// <item>scores.tsv(無い run もある): query / filename / targets / judged / path / score / rank / top10 / path_top1(<see cref="ScoreRow"/>)</item>
/// </list>
/// known-items.tsv と precision.tsv の列は変えない(古い基準線と、今の run を比べられるように)。新しい情報は別のファイル(scores.tsv)に置き、
/// 読むときはあれば読む(<see cref="Read"/>)。基準線には scores.tsv も写す(<c>baseline</c>)が、比較は使わない。
/// </summary>
internal static class RunFiles
{
    public const string KnownItemsFile = "known-items.tsv";
    public const string PrecisionFile = "precision.tsv";
    public const string SummaryFile = "summary.txt";
    public const string ScoresFile = "scores.tsv";

    private const string KnownHeader = "set\tquery\tfilename\tpath\trank";
    private const string PrecisionHeader = "query\tpath\trelevant\tjudged_miss\tunjudged\treturned\ttop10";
    private const string ScoresHeader = "query\tfilename\ttargets\tjudged\tpath\tscore\trank\ttop10\tpath_top1";

    public static void Write(string dir, RunData data, string summaryText)
    {
        Directory.CreateDirectory(dir);
        var utf8 = new UTF8Encoding(false);
        var sb = new StringBuilder(KnownHeader).Append('\n');
        foreach (KnownItemRow r in data.KnownItems)
        {
            CheckCell(r.Set); CheckCell(r.Query); CheckCell(r.Filename); CheckCell(r.Path);
            sb.Append(r.Set).Append('\t').Append(r.Query).Append('\t').Append(r.Filename).Append('\t').Append(r.Path).Append('\t')
              .Append(r.Rank is null ? "" : r.Rank.Value.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }
        File.WriteAllText(Path.Combine(dir, KnownItemsFile), sb.ToString(), utf8);

        sb.Clear().Append(PrecisionHeader).Append('\n');
        foreach (PrecisionRow r in data.Precision)
        {
            CheckCell(r.Query); CheckCell(r.Path);
            foreach (string f in r.Top) { CheckCell(f); if (f.Contains('|')) throw new ArgumentException($"ファイル名に '|' がある: {f}"); }
            sb.Append(r.Query).Append('\t').Append(r.Path).Append('\t')
              .Append(r.Relevant.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(r.JudgedMiss.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(r.Unjudged.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(r.Returned.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(string.Join('|', r.Top)).Append('\n');
        }
        File.WriteAllText(Path.Combine(dir, PrecisionFile), sb.ToString(), utf8);

        if (data.Scores is not null)
        {
            sb.Clear().Append(ScoresHeader).Append('\n');
            foreach (ScoreRow r in data.Scores)
            {
                CheckCell(r.Query); CheckCell(r.Filename); CheckCell(r.Targets); CheckCell(r.Path);
                sb.Append(r.Query).Append('\t').Append(r.Filename).Append('\t').Append(r.Targets).Append('\t')
                  .Append(r.Judged is null ? "" : r.Judged.Value ? "1" : "0").Append('\t').Append(r.Path).Append('\t')
                  .Append(Num(r.Score)).Append('\t').Append(Num(r.Rank)).Append('\t').Append(r.Top10 ? "1" : "0").Append('\t').Append(Num(r.PathTop1)).Append('\n');
            }
            File.WriteAllText(Path.Combine(dir, ScoresFile), sb.ToString(), utf8);
        }
        File.WriteAllText(Path.Combine(dir, SummaryFile), summaryText, utf8);
    }

    private static string Num(double? v) => v is null ? "" : v.Value.ToString("R", CultureInfo.InvariantCulture);

    public static RunData Read(string dir)
    {
        var known = new List<KnownItemRow>();
        foreach ((string[] c, int line) in ReadRows(Path.Combine(dir, KnownItemsFile), KnownHeader, 5))
        {
            double? rank = null;
            if (c[4].Length > 0)
            {
                if (!double.TryParse(c[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) throw new InvalidDataException($"{KnownItemsFile} の {line} 行目: rank が数でない: {c[4]}");
                rank = v;
            }
            known.Add(new KnownItemRow(c[0], c[1], c[2], c[3], rank));
        }
        var prec = new List<PrecisionRow>();
        foreach ((string[] c, int line) in ReadRows(Path.Combine(dir, PrecisionFile), PrecisionHeader, 7))
        {
            int Int(int i) => int.TryParse(c[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : throw new InvalidDataException($"{PrecisionFile} の {line} 行目: 整数でない: {c[i]}");
            prec.Add(new PrecisionRow(c[0], c[1], Int(2), Int(3), Int(4), Int(5), c[6].Length == 0 ? [] : c[6].Split('|')));
        }
        // scores.tsv は後から足した。古い形式の run・基準線には無い(無ければ null)。あって壊れていれば例外
        List<ScoreRow>? scores = null;
        if (File.Exists(Path.Combine(dir, ScoresFile)))
        {
            scores = [];
            foreach ((string[] c, int line) in ReadRows(Path.Combine(dir, ScoresFile), ScoresHeader, 9))
            {
                double? Dbl(int i)
                {
                    if (c[i].Length == 0) return null;
                    return double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : throw new InvalidDataException($"{ScoresFile} の {line} 行目: 数でない: {c[i]}");
                }
                bool? judged = c[3] switch { "" => null, "1" => true, "0" => false, _ => throw new InvalidDataException($"{ScoresFile} の {line} 行目: judged が 1・0・空のどれでもない: {c[3]}") };
                bool top10 = c[7] switch { "1" => true, "0" => false, _ => throw new InvalidDataException($"{ScoresFile} の {line} 行目: top10 が 1・0 のどちらでもない: {c[7]}") };
                scores.Add(new ScoreRow(c[0], c[1], c[2], judged, c[4], Dbl(5), Dbl(6), top10, Dbl(8)));
            }
        }
        return new RunData(known, prec, scores);
    }

    private static IEnumerable<(string[] Cells, int Line)> ReadRows(string path, string header, int columns)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"実行結果のファイルが無い: {path}");
        string[] lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != header) throw new InvalidDataException($"{path} の 1 行目が想定と違う(想定「{header}」)。");
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            string[] c = line.Split('\t');
            if (c.Length != columns) throw new InvalidDataException($"{path} の {i + 1} 行目: 列が {columns} 個でない({c.Length} 個)");
            yield return (c, i + 1);
        }
    }

    private static void CheckCell(string s)
    {
        if (s.Contains('\t') || s.Contains('\n') || s.Contains('\r')) throw new ArgumentException($"タブか改行が入っている: {s}");
    }
}
