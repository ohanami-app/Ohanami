using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace S1Vlm;

public sealed record ImageScore(
    SampleRow Sample,
    string Status,           // ok / no_ref / parse_failed / not_run / ref_missing
    CompareResult? Result,
    CompareResult? AsEmpty = null); // parse_failed のとき: 出力が空だったとみなした場合の比較結果(参照がある場合)

public sealed class GroupAggregate
{
    public string Name = "";
    public int Images;              // run がある画像数(not_run を除く)
    public int ParseFailed;
    public int RefMissing;
    public int Scored;              // 参照が空でなく CER を出した画像数
    public int NoRef;               // 参照が空の画像数
    public int NoRefNonEmptyOutput; // うち出力が空でなかった数
    public List<int> NoRefOutLens = new();
    public long SumDist, SumRefLen;
    public List<double> Cers = new();
    public long SumCommon, SumRefBi, SumOutBi;
    public List<double> Recalls = new();
    public List<double> Precisions = new();
}

public static class ScoreCommand
{
    public static readonly string[] ValueOptions = { "run", "refs", "samples", "out" };
    public static readonly string[] FlagOptions = { "skip-missing-refs" };

    public static int Execute(Args a)
    {
        string runDir = a.Require("run");
        string refsDir = a.GetOr("refs", "refs");
        string samplesPath = a.GetOr("samples", "samples.tsv");
        string outPath = a.GetOr("out", Path.Combine(runDir, "score.tsv"));
        bool skipMissing = a.Flag("skip-missing-refs");

        var samples = SamplesTsv.Read(samplesPath);
        var scores = ScoreRun(runDir, refsDir, samples, skipMissing);
        WriteScoreTsv(outPath, scores);
        // samples.tsv にない画像の run 出力は集計に混ぜない。黙って無視せず、件数と名前を表示する。
        List<string> ignored = RunFiles.FindExtraOutputs(runDir, samples);
        if (ignored.Count > 0)
        {
            Console.WriteLine($"注意: samples.tsv にない画像の run 出力が {ignored.Count} 件ある。採点・集計には含めていない(無視):");
            foreach (string n in ignored) Console.WriteLine("  " + n);
        }
        Console.WriteLine($"画像ごとの採点を書き出した: {outPath}");
        PrintAggregates(scores, Console.Out);
        return 0;
    }

    /// <summary>run の出力と参照から、画像ごとの採点を作る。</summary>
    public static List<ImageScore> ScoreRun(string runDir, string refsDir, List<SampleRow> samples, bool skipMissingRefs)
    {
        if (!Directory.Exists(runDir)) throw new DirectoryNotFoundException($"run の出力ディレクトリがない: {runDir}");
        if (!Directory.Exists(refsDir)) throw new DirectoryNotFoundException($"参照ディレクトリがない: {refsDir}(draft-refs で下書きを作る)");

        var result = new List<ImageScore>();
        var missingRefs = new List<string>();
        int ran = 0;
        foreach (SampleRow s in samples)
        {
            string jsonPath = RunFiles.ImageJsonPath(runDir, s);
            if (!File.Exists(jsonPath))
            {
                result.Add(new ImageScore(s, "not_run", null));
                continue;
            }
            ran++;
            RunFiles.ParsedOutput parsed = RunFiles.LoadParsed(jsonPath);
            string refPath = Path.Combine(refsDir, s.Stem + ".txt");
            if (!parsed.Ok)
            {
                // パース失敗の画像は、「出力が空だったとみなす」集計のために参照も読む(参照がないのは異常として扱う)
                if (!File.Exists(refPath))
                {
                    missingRefs.Add(refPath);
                    result.Add(new ImageScore(s, "parse_failed", null, null));
                }
                else
                {
                    string refText = File.ReadAllText(refPath, RunFiles.Utf8NoBom);
                    result.Add(new ImageScore(s, "parse_failed", null, Scoring.Compare("", refText)));
                }
                continue;
            }
            if (!File.Exists(refPath))
            {
                missingRefs.Add(refPath);
                result.Add(new ImageScore(s, "ref_missing", null));
                continue;
            }
            string reference = File.ReadAllText(refPath, RunFiles.Utf8NoBom);
            CompareResult cr = Scoring.Compare(parsed.Ocr!, reference);
            result.Add(new ImageScore(s, cr.RefLen == 0 ? "no_ref" : "ok", cr));
        }
        if (ran == 0) throw new InvalidOperationException($"{runDir} に samples.tsv の画像の出力が1つもない");
        if (missingRefs.Count > 0 && !skipMissingRefs)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"参照ファイルがない画像が {missingRefs.Count} 枚ある(意図的なら --skip-missing-refs):");
            foreach (string m in missingRefs) sb.AppendLine("  " + m);
            throw new InvalidOperationException(sb.ToString().TrimEnd());
        }
        return result;
    }

    private static string F(double? v) => v is null ? "" : v.Value.ToString("0.0000", CultureInfo.InvariantCulture);

    public static void WriteScoreTsv(string path, List<ImageScore> scores)
    {
        var sb = new StringBuilder();
        sb.Append("filename\tstratum\tgroup\tstatus\tref_len\tout_len\tdistance\tcer\tref_bigrams\tout_bigrams\tcommon_bigrams\trecall\tprecision\n");
        foreach (ImageScore s in scores)
        {
            // パース失敗の行は、出力が空だったとみなした場合の値を入れ、status にそう書く
            CompareResult? shown = s.Result ?? s.AsEmpty;
            string statusText = s.Status == "parse_failed" && s.AsEmpty != null ? "parse_failed(as_empty)" : s.Status;
            sb.Append(s.Sample.Filename).Append('\t').Append(s.Sample.Stratum).Append('\t').Append(s.Sample.Group).Append('\t').Append(statusText);
            if (shown is { } r)
            {
                sb.Append('\t').Append(r.RefLen).Append('\t').Append(r.OutLen).Append('\t').Append(r.Distance)
                  .Append('\t').Append(F(r.Cer)).Append('\t').Append(r.RefBigrams).Append('\t').Append(r.OutBigrams)
                  .Append('\t').Append(r.Common).Append('\t').Append(F(r.Recall)).Append('\t').Append(F(r.Precision));
            }
            else
            {
                sb.Append("\t\t\t\t\t\t\t\t\t");
            }
            sb.Append('\n');
        }
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString(), RunFiles.Utf8NoBom);
    }

    /// <param name="failedAsEmpty">true: パース失敗の画像を「出力が空だった」として集計に含める(CER=1、再現率=0、適合率は計算しない)。false: 除外して件数だけ数える。</param>
    public static Dictionary<string, GroupAggregate> Aggregate(List<ImageScore> scores, Func<SampleRow, string> keyOf, bool failedAsEmpty = false)
    {
        var map = new Dictionary<string, GroupAggregate>();
        foreach (ImageScore s in scores)
        {
            if (s.Status == "not_run") continue;
            string key = keyOf(s.Sample);
            if (!map.TryGetValue(key, out var g)) map[key] = g = new GroupAggregate { Name = key };
            g.Images++;
            CompareResult? r0 = s.Result;
            string status = s.Status;
            if (status == "parse_failed")
            {
                g.ParseFailed++;
                if (!failedAsEmpty || s.AsEmpty == null) continue;
                r0 = s.AsEmpty;
                status = r0.RefLen == 0 ? "no_ref" : "ok";
            }
            if (status == "ref_missing") { g.RefMissing++; continue; }
            CompareResult r = r0!;
            if (status == "no_ref")
            {
                g.NoRef++;
                g.NoRefOutLens.Add(r.OutLen);
                if (r.OutLen > 0) g.NoRefNonEmptyOutput++;
                continue;
            }
            g.Scored++;
            g.SumDist += r.Distance;
            g.SumRefLen += r.RefLen;
            g.Cers.Add(r.Cer!.Value);
            g.SumCommon += r.Common;
            g.SumRefBi += r.RefBigrams;
            g.SumOutBi += r.OutBigrams;
            if (r.Recall is { } rc) g.Recalls.Add(rc);
            if (r.Precision is { } pc) g.Precisions.Add(pc);
        }
        return map;
    }

    private static string Pct(double v) => (v * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    private static string Mean(List<double> xs) => xs.Count == 0 ? "n/a" : Pct(xs.Average());

    private static string Ratio(long num, long den) => den == 0 ? "n/a" : Pct((double)num / den);

    public static void PrintAggregates(List<ImageScore> scores, TextWriter w)
    {
        int notRun = scores.Count(s => s.Status == "not_run");
        if (notRun > 0) w.WriteLine($"注意: run の出力がない画像が {notRun} 枚ある(--only などで流していない分。集計に含めない)");
        int failed = scores.Count(s => s.Status == "parse_failed");
        if (failed > 0) w.WriteLine($"!! パース失敗(finish_reason=length を含む)の画像が {failed} 枚ある。集計は2通り出す: [A]=パース失敗を除外 / [B]=パース失敗を出力が空だったとみなして含める");
        foreach (bool asEmpty in new[] { false, true })
        {
            string label = asEmpty ? "[B] パース失敗を「出力が空だった」とみなして含める(CER=100%、再現率=0、適合率は計算しない)" : "[A] パース失敗を除外";
            w.WriteLine();
            w.WriteLine($"################ {label} ################");
            PrintTable(scores, w, "group", sm => sm.Group, new[] { "plain", "hard", "no_text", "suggestive" }, asEmpty);
            w.WriteLine();
            PrintTable(scores, w, "stratum", sm => sm.Stratum, null, asEmpty);
        }
    }

    private static void PrintTable(List<ImageScore> scores, TextWriter w, string title, Func<SampleRow, string> keyOf, string[]? preferredOrder, bool failedAsEmpty)
    {
        var map = Aggregate(scores, keyOf, failedAsEmpty);
        IEnumerable<string> keys;
        if (preferredOrder is not null)
            keys = preferredOrder.Where(map.ContainsKey).Concat(map.Keys.Where(k => !preferredOrder.Contains(k)).OrderBy(k => k, StringComparer.Ordinal));
        else
            keys = map.Keys.OrderBy(k => k, StringComparer.Ordinal);

        w.WriteLine($"== {title} ごとの集計 ==");
        w.WriteLine("(CER/再現率/適合率は micro=全画像の合計から計算、mean=画像ごとの値の単純平均。参照が空の画像は CER を出さない)");
        foreach (string k in keys)
        {
            GroupAggregate g = map[k];
            string failNote = failedAsEmpty ? "パース失敗(空とみなして含めた)" : "パース失敗(除外)";
            w.WriteLine($"[{k}] 画像 {g.Images}枚 / 採点 {g.Scored}枚 / 参照が空 {g.NoRef}枚 / {failNote} {g.ParseFailed}枚 / 参照なし(スキップ) {g.RefMissing}枚");
            if (g.Scored > 0)
            {
                w.WriteLine($"    CER          micro {Ratio(g.SumDist, g.SumRefLen)}   mean {Mean(g.Cers)}");
                w.WriteLine($"    bigram再現率 micro {Ratio(g.SumCommon, g.SumRefBi)}   mean {Mean(g.Recalls)}");
                w.WriteLine($"    bigram適合率 micro {Ratio(g.SumCommon, g.SumOutBi)}   mean {Mean(g.Precisions)}");
            }
            if (g.NoRef > 0)
            {
                w.WriteLine($"    参照が空の画像: 出力が空でなかったもの {g.NoRefNonEmptyOutput}/{g.NoRef}枚、出力の文字数(正規化後) = [{string.Join(", ", g.NoRefOutLens)}]");
            }
            if (g.ParseFailed > 0) w.WriteLine($"    !! パース失敗が {g.ParseFailed} 枚ある(足切り条件)" + (failedAsEmpty ? "。この集計には含まれている" : "。この集計には含まれていない"));
        }
    }
}
