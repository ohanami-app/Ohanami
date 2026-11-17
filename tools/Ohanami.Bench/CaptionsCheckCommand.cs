using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.FullText;
using Ohanami.Core.Models;

namespace Ohanami.Bench;

/// <param name="EvalRunDirectory">VLM の評価の全件の run のフォルダ(spikes/S1-vlm/runs/qwen3vl-8b-q4km-tags-all)。</param>
/// <param name="RetryDryDir">評価の 16 枚の再試行の run(dry_multiplier 0.8)。null なら、期待は VLM の評価の全件の run だけ(失敗した 16 枚は期待なし)。</param>
/// <param name="RetryRepDir">同じく repeat_penalty 1.1 の run。</param>
/// <param name="Root">通し(<c>vlm-full</c>)の DB のあるルート(<c>&lt;root&gt;/db/bench.db</c>)。</param>
/// <param name="ExpectedVlmRuns">VLM の runs の行の下限(一時停止・再開を 1 回入れたら 2)。</param>
/// <param name="IgnoreTimes">時間の項目を見ない(取り込みの行の確認用)。</param>
internal sealed record CaptionsCheckOptions(string EvalRunDirectory, string? RetryDryDir, string? RetryRepDir, string Root, int ExpectedVlmRuns, bool IgnoreTimes = false);

/// <summary>
/// <c>captions-check</c>: 通しのあとの確認(<see cref="CaptionsCheckLogic"/> の合格の見方で評価する)。DB の captions と stage_status と runs を読み、VLM の評価の run の JSON と、評価の全 1030 枚で比べる。
/// 取り込み(import-s1)の行とではなく、評価の JSON と比べる(同じ DB に取り込みの行があると、通しの結果を取り込みの結果で上書きしていないかが分からないため。通しは別の root の別の DB で行う)。
/// 結果は <c>&lt;root&gt;/captions-check/&lt;時刻&gt;/</c>(report.txt、rows.tsv、diffs.txt。私物の OCR を含むので、リポジトリの外)。
/// </summary>
internal static class CaptionsCheckCommand
{
    public static int Run(CaptionsCheckOptions o)
    {
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string outDir = Path.Combine(o.Root, "captions-check", stamp);
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        void Out(string line)
        {
            Console.WriteLine(line);
            report.AppendLine(line);
        }

        IReadOnlyList<VlmEvalImage> evalImages = VlmEvalRun.ReadImages(o.EvalRunDirectory);
        Dictionary<string, VlmEvalImage> dry = o.RetryDryDir is null ? [] : VlmEvalRun.ReadImages(o.RetryDryDir).ToDictionary(i => i.FileName, StringComparer.Ordinal);
        Dictionary<string, VlmEvalImage> rep = o.RetryRepDir is null ? [] : VlmEvalRun.ReadImages(o.RetryRepDir).ToDictionary(i => i.FileName, StringComparer.Ordinal);
        Dictionary<string, double> evalWall = ReadVlmEvalWalls(o.EvalRunDirectory);
        Out($"評価: {evalImages.Count} 枚(成功 {evalImages.Count(i => i.ParsedOk)}、失敗 {evalImages.Count(i => !i.ParsedOk)})。再試行の run: dry {dry.Count} 枚、rep {rep.Count} 枚");

        string dbPath = Path.Combine(o.Root, "db", "bench.db");
        if (!File.Exists(dbPath)) throw new FileNotFoundException($"DB が無い: {dbPath}(先に vlm-full を実行する)");
        string modelId = VlmCatalog.Current.ModelId;
        using OhanamiDatabase db = OhanamiDatabase.Open(dbPath);
        using SqliteConnection conn = db.CreateReadConnection();

        // ファイル名 → ハッシュ(現存するパス。ファイル名は NFC。同じファイル名が別の画像にあれば例外)
        var hashOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string hash, string path) in Query(conn, "SELECT hash, path FROM image_paths WHERE missing_since IS NULL", r => (r.GetString(0), r.GetString(1))))
        {
            string name = BenchInputs.Nfc(Path.GetFileName(path));
            if (hashOf.TryGetValue(name, out string? other) && other != hash) throw new InvalidDataException($"同じファイル名「{name}」が別の画像にある(入れ子のフォルダ?)。この道具はファイル名で指す。");
            hashOf[name] = hash;
        }

        var captions = new Dictionary<string, StoredCaption>(StringComparer.Ordinal);
        foreach ((string hash, StoredCaption c) in Query(conn,
                     $"SELECT hash, ocr, description, tags_json, attempt, settings_json FROM captions WHERE model_id = '{modelId}'",
                     r => (r.GetString(0), new StoredCaption(r.GetString(1), r.GetString(2), FtsIndex.ParseTags(r.GetString(0), r.GetString(3)), (int)r.GetInt64(4), r.GetString(5)))))
            captions[hash] = c;
        var failures = new Dictionary<string, StoredFailure>(StringComparer.Ordinal);
        foreach ((string hash, StoredFailure f) in Query(conn,
                     "SELECT hash, error, settings_json FROM stage_status WHERE stage = 'caption' AND status = 'failed'",
                     r => (r.GetString(0), new StoredFailure(r.GetString(1), r.IsDBNull(2) ? "{}" : r.GetString(2)))))
            failures[hash] = f;
        Out($"DB: captions {captions.Count} 件(model_id {modelId})、失敗の記録 {failures.Count} 件");

        // 画像ごとの分類
        var rows = new List<CaptionsCheckRow>();
        var firstAttemptWall = new List<double>();
        var evalWallSame = new List<double>();
        var retriedWall = new List<double>();
        var allWall = new List<double>();
        foreach (VlmEvalImage image in evalImages)
        {
            if (!hashOf.TryGetValue(image.FileName, out string? hash)) throw new InvalidDataException($"評価の「{image.FileName}」が DB に無い(通しのフォルダが評価のときと違う)");
            ExpectedCaption? expected = CaptionsCheckLogic.Expect(image, dry.GetValueOrDefault(image.FileName), rep.GetValueOrDefault(image.FileName));
            StoredCaption? caption = captions.GetValueOrDefault(hash);
            StoredFailure? failure = caption is null ? failures.GetValueOrDefault(hash) : null;
            rows.Add(CaptionsCheckLogic.Classify(image.FileName, hash, expected, caption, failure));

            string? settings = caption?.SettingsJson ?? failure?.SettingsJson;
            if (settings is not null && !o.IgnoreTimes)
            {
                double wall = CaptionsCheckLogic.WallSeconds(settings);
                allWall.Add(wall);
                if (caption is { Attempt: 1 })
                {
                    firstAttemptWall.Add(wall);
                    evalWallSame.Add(evalWall.TryGetValue(image.FileName, out double sw) ? sw : throw new InvalidDataException($"評価の summary.tsv に「{image.FileName}」が無い"));
                }
                else retriedWall.Add(wall);
            }
        }

        var runs = Query(conn, "SELECT id, job_kind, started_at, ended_at, runtime_json FROM runs ORDER BY id",
            r => new RunRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4))).ToList();
        int reprocessed = (int)Scalar(conn, "SELECT count(*) FROM stage_status WHERE stage IN ('ocr', 'caption') AND attempts > 1");
        int ocrDone = (int)Scalar(conn, "SELECT count(*) FROM stage_status WHERE stage = 'ocr' AND status = 'done'");
        int captionDone = (int)Scalar(conn, "SELECT count(*) FROM stage_status WHERE stage = 'caption' AND status = 'done'");

        var input = new CaptionsCheckInput(rows, firstAttemptWall, evalWallSame, runs, reprocessed, ocrDone, captionDone, o.ExpectedVlmRuns, !o.IgnoreTimes);
        IReadOnlyList<CheckResult> results = CaptionsCheckLogic.Evaluate(input);

        Out("");
        Out("== 分類(評価の全 " + rows.Count + " 枚) ==");
        foreach (IGrouping<string, CaptionsCheckRow> g in rows.GroupBy(r => r.Kind).OrderByDescending(g => g.Count()))
            Out($"  {g.Key}: {g.Count()}");
        Out($"  製品の成功の試行の回: 1 回目 {rows.Count(r => r.ProductAttempt == 1)}、2 回目 {rows.Count(r => r.ProductAttempt == 2)}、3 回目 {rows.Count(r => r.ProductAttempt == 3)}");
        Out("");
        Out("== 処理時間(秒。再試行のぶんを含む、1 枚の合計) ==");
        Out("  全体: " + Distribution.From(allWall));
        Out("  1 回目で成功: " + Distribution.From(firstAttemptWall));
        Out("  同じ画像の評価: " + Distribution.From(evalWallSame));
        Out("  再試行した画像(失敗を含む): " + Distribution.From(retriedWall));
        Out("");
        Out("== runs ==");
        foreach (RunRow r in runs)
            Out($"  #{r.Id} {r.JobKind} {r.StartedAt} → {r.EndedAt ?? "(終了の印なし)"}  {Shorten(r.RuntimeJson)}");
        Out("");
        Out("== 合格の見方(結果を見る前に決めたもの。CaptionsCheckLogic.Evaluate) ==");
        foreach (CheckResult c in results) Out($"  [{(c.Passed ? "合格" : "**不合格**")}] {c.Name}: {c.Detail}");
        bool all = results.All(c => c.Passed);
        Out(all ? "判定: すべて合格。" : "判定: 通らない項目がある。差の内容は diffs.txt。");

        var tsv = new StringBuilder("filename\thash\tkind\texpected_attempt\tproduct_attempt\tattempt_mismatch\n");
        var diffs = new StringBuilder();
        foreach (CaptionsCheckRow r in rows)
        {
            tsv.Append(string.Join('\t', r.FileName, r.Hash, r.Kind, r.ExpectedAttempt.ToString(CultureInfo.InvariantCulture),
                r.ProductAttempt?.ToString(CultureInfo.InvariantCulture) ?? "", r.AttemptMismatch ? "y" : "n")).Append('\n');
            if (!(r.Kind == VlmCompare.Match && !r.AttemptMismatch)) diffs.Append(r.FileName).Append(": ").Append(r.Kind).Append(": ").Append(r.Detail).Append('\n');
        }
        File.WriteAllText(Path.Combine(outDir, "rows.tsv"), tsv.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "diffs.txt"), diffs.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString(), new UTF8Encoding(false));
        Console.WriteLine("結果: " + outDir + "(diffs.txt は私物の画像の文字を含む。リポジトリに入れない)");
        return all ? 0 : 5;
    }

    /// <summary>評価の summary.tsv の filename と wall_ms(秒)。</summary>
    public static Dictionary<string, double> ReadVlmEvalWalls(string evalRun)
    {
        string path = Path.Combine(evalRun, "summary.tsv");
        string[] lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
        string[] header = lines[0].TrimEnd('\r').Split('\t');
        int iName = Array.IndexOf(header, "filename"), iWall = Array.IndexOf(header, "wall_ms");
        if (iName < 0 || iWall < 0) throw new InvalidDataException($"{path} に filename と wall_ms の列が必要");
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string line in lines.Skip(1).Where(l => l.Length > 0))
        {
            string[] c = line.TrimEnd('\r').Split('\t');
            map[BenchInputs.Nfc(c[iName])] = double.Parse(c[iWall], CultureInfo.InvariantCulture) / 1000.0;
        }
        return map;
    }

    private static string Shorten(string json) => json.Length <= 160 ? json : json[..160] + "…";

    private static IEnumerable<T> Query<T>(SqliteConnection conn, string sql, Func<SqliteDataReader, T> read)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using SqliteDataReader r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(read(r));
        return list;
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
