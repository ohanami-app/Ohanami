using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;

namespace Ohanami.Bench;

/// <param name="DataDir">App のデータのディレクトリ(App を <c>OHANAMI_DATA_DIR</c> で起動した場所。<c>ohanami.db</c>・<c>logs\</c>・<c>llama-server.pid</c>)。</param>
/// <param name="Folder">フィクスチャの場面のフォルダ(<c>sleep-fixture</c> が作ったサブフォルダ)。DB の集計は、このフォルダの画像だけ。</param>
internal sealed record SleepReportOptions(string DataDir, SleepScenario Scenario, string Folder, DateTimeOffset From, DateTimeOffset To, int VramLimitMiB);

/// <summary>
/// <c>sleep-report</c>: スリープ・強制終了の確認の材料(Windows のスリープの記録・App のログ・DB・プロセス・VRAM)を集めて、時系列と合否を出す(判定は <see cref="SleepCheckLogic"/>)。
/// 読むだけ(DB は読み取り専用で開く。App が動いていても使える)。GPU は使わない(<c>nvidia-smi</c> で VRAM を 1 回読む)。結果は <c>&lt;data&gt;\sleep-check\</c> にも保存する。
/// </summary>
internal static class SleepCheckCommand
{
    private static readonly Regex LogLineRegex = new(@"^(?<t>\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z) \[(?<level>info|warn|error)\] (?<msg>.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex SpawnLogName = new(@"^llama-server-(?<t>\d{8}-\d{6}-\d{3})\.log$", RegexOptions.CultureInvariant);
    private static readonly Regex Digits4 = new(@"\d{4}", RegexOptions.CultureInvariant);

    public static int Run(SleepReportOptions o)
    {
        string dbPath = Path.Combine(o.DataDir, "ohanami.db");
        if (!File.Exists(dbPath)) throw new FileNotFoundException($"DB が無い: {dbPath}(--data は、App を OHANAMI_DATA_DIR で起動した場所)");
        string folder = Path.GetFullPath(o.Folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"フィクスチャのフォルダが無い: {folder}(sleep-fixture で作った場面のフォルダ)");

        var report = new StringBuilder();
        void Out(string line)
        {
            Console.WriteLine(line);
            report.AppendLine(line);
        }

        (List<SleepLogLine> log, int unreadable) = ReadLog(Path.Combine(o.DataDir, "logs"), o.From, o.To);
        List<SleepPeriod> sleeps = ReadSleeps(o.From, o.To);
        (List<SleepRunRow> runs, Dictionary<string, SleepStageTally> stages, SleepOcrMatch? ocr) = ReadDb(dbPath, folder);
        int files = Directory.EnumerateFiles(folder, "*.png").Count();
        List<DateTimeOffset> spawns = ReadSpawns(Path.Combine(o.DataDir, "logs"), o.From, o.To);
        int[] pids = Process.GetProcessesByName("llama-server").Select(p => p.Id).ToArray();
        bool app = Process.GetProcessesByName("Ohanami.App").Length > 0;
        bool pidFile = File.Exists(Path.Combine(o.DataDir, "llama-server.pid"));
        int? vram = ReadVram();

        var snapshot = new SleepSnapshot(o.From, o.To, log, sleeps, runs, stages, files, ocr, spawns, pids, app, pidFile, vram, o.VramLimitMiB);
        IReadOnlyList<SleepCheck> checks = SleepCheckLogic.Evaluate(o.Scenario, snapshot);
        SleepVerdict overall = SleepCheckLogic.Overall(checks);

        Out($"== sleep-report: {o.Scenario}({DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz})==");
        Out($"データ: {o.DataDir}");
        Out($"フィクスチャ: {folder}({files.ToString(CultureInfo.InvariantCulture)} 枚)");
        Out($"対象の時間(UTC): {o.From:yyyy-MM-dd HH:mm:ss} 〜 {o.To:yyyy-MM-dd HH:mm:ss}。ログ {log.Count.ToString(CultureInfo.InvariantCulture)} 行" + (unreadable > 0 ? $"(読めない行 {unreadable.ToString(CultureInfo.InvariantCulture)} 行。強制終了で途切れた行は、ここに数える)" : ""));
        Out("");
        Out("-- 時系列(UTC。スリープ・復帰と、ログのうち llama-server・ジョブ・スリープ・失敗に関わる行) --");
        foreach (string line in Timeline(snapshot)) Out("  " + line);
        Out("");
        Out("-- DB(このフォルダの画像だけ) --");
        foreach ((string stage, SleepStageTally t) in stages.OrderBy(k => k.Key, StringComparer.Ordinal))
            Out($"  {stage}: 期待 {t.Expected.ToString(CultureInfo.InvariantCulture)}、done {t.Done.ToString(CultureInfo.InvariantCulture)}、failed {t.Failed.ToString(CultureInfo.InvariantCulture)}");
        foreach (SleepRunRow r in runs.Where(r => r.Started >= o.From.AddMinutes(-1) || r.Ended is null))
            Out($"  runs {r.Id.ToString(CultureInfo.InvariantCulture)}: {r.JobKind}、開始 {r.Started:HH:mm:ss}Z、" + (r.Ended is { } e ? $"終了 {e:HH:mm:ss}Z" : "終了なし(ended_at が空)"));
        Out("");
        Out("-- 今の状態 --");
        Out($"  llama-server: {(pids.Length == 0 ? "居ない" : "pid " + string.Join(", ", pids))}、Ohanami.App: {(app ? "動いている" : "動いていない")}、pid ファイル: {(pidFile ? "ある" : "無い")}、VRAM(GPU 全体): {(vram is { } v ? v.ToString(CultureInfo.InvariantCulture) + " MiB" : "不明")}");
        Out("");
        Out("-- 判定 --");
        foreach (SleepCheck c in checks) Out($"  [{Mark(c.Verdict)}] {c.Name}: {c.Detail}");
        Out("");
        Out($"== 全体: {Mark(overall)} ==");
        string dir = Path.Combine(o.DataDir, "sleep-check");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{o.Scenario}.txt");
        File.WriteAllText(file, report.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"保存: {file}");
        return overall == SleepVerdict.Fail ? 6 : 0;
    }

    private static string Mark(SleepVerdict v) => v switch
    {
        SleepVerdict.Pass => "合格",
        SleepVerdict.Fail => "不合格",
        SleepVerdict.Warn => "注意",
        SleepVerdict.Info => "情報",
        _ => "対象外",
    };

    // ------------------------------------------------------------------ 時系列

    private static readonly string[] Interesting =
    [
        "llama-server", "スリープ", "kill", "起動し直", "異常", "失敗", "孤児", "ジョブ", "埋め込み", "VLM", "pid", "復帰", "再開", "一時停止", "通知", "セッション",
    ];

    internal static IEnumerable<string> Timeline(SleepSnapshot s)
    {
        var items = new List<(DateTimeOffset Time, string Text)>();
        foreach (SleepPeriod p in s.Sleeps)
        {
            items.Add((p.Start, $"★ Windows: スリープに入った({SleepCheckLogic.StateText(p)})"));
            items.Add((p.End, $"★ Windows: 復帰した(スリープ {((int)p.Length.TotalSeconds).ToString(CultureInfo.InvariantCulture)} 秒、解除元 {p.WakeSource ?? "不明"})"));
        }
        foreach (DateTimeOffset t in s.LlamaSpawns) items.Add((t, "llama-server のログができた(起動の開始)"));
        foreach (SleepLogLine l in s.Log)
        {
            if (l.Level == "info" && !Interesting.Any(k => l.Message.Contains(k, StringComparison.Ordinal))) continue;   // warn・error は全部、info は関係する行だけ
            items.Add((l.Time, $"[{l.Level}] {l.Message}"));
        }
        foreach ((DateTimeOffset time, string text) in items.OrderBy(i => i.Time)) yield return $"{time:HH:mm:ss.fff}Z {text}";
    }

    // ------------------------------------------------------------------ 材料

    /// <summary>App の診断ログ(<c>ohanami-yyyyMMdd.log</c>、UTC の日付)から、時間の範囲の行を読む。形に合わない行(強制終了で途切れた行)は数だけ返す。</summary>
    private static (List<SleepLogLine> Lines, int Unreadable) ReadLog(string logDir, DateTimeOffset from, DateTimeOffset to)
    {
        var lines = new List<SleepLogLine>();
        int unreadable = 0;
        if (!Directory.Exists(logDir)) return (lines, unreadable);
        for (DateTime day = from.UtcDateTime.Date; day <= to.UtcDateTime.Date; day = day.AddDays(1))
        {
            string path = Path.Combine(logDir, "ohanami-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
            if (!File.Exists(path)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);   // App が書いている最中でも読む
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                Match m = LogLineRegex.Match(line);
                if (!m.Success) { unreadable++; continue; }
                DateTimeOffset t = DateTimeOffset.ParseExact(m.Groups["t"].Value, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                if (t < from || t > to) continue;
                lines.Add(new SleepLogLine(t, m.Groups["level"].Value, m.Groups["msg"].Value));
            }
        }
        return (lines, unreadable);
    }

    /// <summary>llama-server のログのファイル名(ローカル時刻 <c>yyyyMMdd-HHmmss-fff</c>)から、起動の開始の時刻。</summary>
    private static List<DateTimeOffset> ReadSpawns(string logDir, DateTimeOffset from, DateTimeOffset to)
    {
        var spawns = new List<DateTimeOffset>();
        if (!Directory.Exists(logDir)) return spawns;
        foreach (string path in Directory.EnumerateFiles(logDir, "llama-server-*.log"))
        {
            Match m = SpawnLogName.Match(Path.GetFileName(path));
            if (!m.Success) continue;
            DateTime local = DateTime.ParseExact(m.Groups["t"].Value, "yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
            var t = new DateTimeOffset(local).ToUniversalTime();
            if (t >= from.AddMinutes(-10) && t <= to) spawns.Add(t);
        }
        spawns.Sort();
        return spawns;
    }

    /// <summary>
    /// Windows のイベントログ(System)の Microsoft-Windows-Power-Troubleshooter ID 1(「低電力状態から再開しました」)。スリープの時刻と復帰の時刻は、その本文の UTC。
    /// 読むのは Windows 標準の <c>wevtutil</c>(<c>System.Diagnostics.EventLog</c> は NuGet が要るので使わない)。新しい順に最大 100 件。
    /// </summary>
    private static List<SleepPeriod> ReadSleeps(DateTimeOffset from, DateTimeOffset to)
    {
        var psi = new ProcessStartInfo("wevtutil") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (string a in new[] { "qe", "System", "/q:*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and (EventID=1)]]", "/f:xml", "/rd:true", "/c:100" }) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("wevtutil を起動できない");
        Task<string> error = p.StandardError.ReadToEndAsync();
        string xml = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"wevtutil が失敗した(終了コード {p.ExitCode.ToString(CultureInfo.InvariantCulture)}): {error.GetAwaiter().GetResult()}");
        return ParseSleeps(xml, from, to);
    }

    /// <summary><c>wevtutil qe /f:xml</c> の出力(<c>&lt;Event&gt;</c> が並ぶだけで、ルートが無い)から、範囲に重なるスリープを読む(テストから呼ぶ)。</summary>
    internal static List<SleepPeriod> ParseSleeps(string eventsXml, DateTimeOffset from, DateTimeOffset to)
    {
        var periods = new List<SleepPeriod>();
        if (string.IsNullOrWhiteSpace(eventsXml)) return periods;
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        XElement root = XElement.Parse("<Events>" + eventsXml + "</Events>");
        foreach (XElement e in root.Elements(ns + "Event"))
        {
            XElement data = e.Element(ns + "EventData") ?? throw new InvalidDataException("Power-Troubleshooter のイベントに EventData が無い");
            string? Get(string name) => data.Elements(ns + "Data").FirstOrDefault(d => (string?)d.Attribute("Name") == name)?.Value;
            string sleepText = Get("SleepTime") ?? throw new InvalidDataException("SleepTime が無い");
            string wakeText = Get("WakeTime") ?? throw new InvalidDataException("WakeTime が無い");
            DateTimeOffset start = DateTimeOffset.Parse(sleepText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            DateTimeOffset end = DateTimeOffset.Parse(wakeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            if (end < from || start > to) continue;
            int? state = int.TryParse(Get("EffectiveState"), NumberStyles.None, CultureInfo.InvariantCulture, out int st) ? st : null;
            long? hiberPages = long.TryParse(Get("HiberPagesWritten"), NumberStyles.None, CultureInfo.InvariantCulture, out long hp) ? hp : null;
            periods.Add(new SleepPeriod(start, end, state, Get("WakeSourceText"), hiberPages));
        }
        periods.Sort((a, b) => a.Start.CompareTo(b.Start));
        return periods;
    }

    private static (List<SleepRunRow> Runs, Dictionary<string, SleepStageTally> Stages, SleepOcrMatch? Ocr) ReadDb(string dbPath, string folder)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        conn.Open();
        Dictionary<string, string> nameOf = FolderImages(conn, folder);

        var times = new Dictionary<string, List<DateTimeOffset>>(StringComparer.Ordinal);
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach ((string hash, string stage, string status, string? error, string updated) in Query(conn,
                     "SELECT hash, stage, status, error, updated_at FROM stage_status",
                     r => (r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4))))
        {
            if (!nameOf.ContainsKey(hash)) continue;
            if (!times.ContainsKey(stage)) { times[stage] = []; errors[stage] = []; }
            switch (status)
            {
                case "done": times[stage].Add(DbTime.Parse(updated)); break;
                case "failed": errors[stage].Add(error ?? "(error なし)"); break;
                default: throw new InvalidDataException($"未知の stage_status.status: {status}");
            }
        }
        var stages = new Dictionary<string, SleepStageTally>(StringComparer.Ordinal);
        foreach (string stage in new[] { "embed", "ocr", "caption" })
        {
            times.TryGetValue(stage, out List<DateTimeOffset>? done);
            errors.TryGetValue(stage, out List<string>? failed);
            stages[stage] = new SleepStageTally(nameOf.Count, (done ?? []).OrderBy(t => t).ToList(), failed ?? []);
        }

        int matched = 0, total = 0;
        foreach ((string hash, string ocr) in Query(conn, "SELECT hash, ocr FROM captions", r => (r.GetString(0), r.GetString(1))))
        {
            if (!nameOf.TryGetValue(hash, out string? name)) continue;
            total++;
            Match number = Digits4.Match(name);
            if (number.Success && ocr.Contains(number.Value, StringComparison.Ordinal)) matched++;
        }

        List<SleepRunRow> runs = Query(conn, "SELECT id, job_kind, started_at, ended_at FROM runs ORDER BY id",
            r => new SleepRunRow(r.GetInt64(0), r.GetString(1), DbTime.Parse(r.GetString(2)), r.IsDBNull(3) ? null : DbTime.Parse(r.GetString(3)))).ToList();
        return (runs, stages, new SleepOcrMatch(matched, total));
    }

    /// <summary>フォルダの画像: hash → ファイル名(そのフォルダの直下の、現存するパス = missing_since が空)。<c>sleep-report</c> と <c>stage-count</c> が同じ数え方をするための共通部分。</summary>
    private static Dictionary<string, string> FolderImages(SqliteConnection conn, string folder)
    {
        string prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var nameOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string hash, string path) in Query(conn, "SELECT hash, path FROM image_paths WHERE missing_since IS NULL", r => (r.GetString(0), r.GetString(1))))
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) nameOf[hash] = Path.GetFileName(path);
        }
        return nameOf;
    }

    /// <summary>
    /// <c>stage-count</c>: フォルダの画像のうち、その段階(embed / ocr / caption)が done のものの数。スリープ・強制終了の確認のスクリプト(<c>tools/*-when.ps1</c>)が、
    /// 「done が N 枚になったら」を待つのに使う(sqlite3 の CLI に頼らない。<c>sleep-report</c> と同じ DB の読み方 = 読み取り専用で、App が動いていてもよい)。
    /// </summary>
    internal static int CountDone(string dbPath, string folder, string stage)
    {
        if (stage is not ("embed" or "ocr" or "caption")) throw new ArgumentException($"知らない段階: {stage}(embed / ocr / caption)");
        if (!File.Exists(dbPath)) throw new FileNotFoundException($"DB が無い: {dbPath}(--data は、App を OHANAMI_DATA_DIR で起動した場所)");
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());   // 閉じたらファイルを離す
        conn.Open();
        Dictionary<string, string> images = FolderImages(conn, folder);
        int done = 0;
        // stage は上で 3 つの固定の文字列のどれかに限っているので、SQL に埋めてよい
        foreach (string hash in Query(conn, $"SELECT hash FROM stage_status WHERE stage = '{stage}' AND status = 'done'", r => r.GetString(0)))
        {
            if (images.ContainsKey(hash)) done++;
        }
        return done;
    }

    private static IEnumerable<T> Query<T>(SqliteConnection conn, string sql, Func<SqliteDataReader, T> read)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using SqliteDataReader r = cmd.ExecuteReader();
        var rows = new List<T>();
        while (r.Read()) rows.Add(read(r));
        return rows;
    }

    /// <summary>GPU 全体の使用量(MiB)。nvidia-smi が無い・失敗なら null(判定は「対象外」にする)。</summary>
    private static int? ReadVram()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using Process? p = Process.Start(psi);
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10_000)) { p.Kill(); return null; }
            return p.ExitCode == 0 && int.TryParse(output.Split('\n')[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int mib) ? mib : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
