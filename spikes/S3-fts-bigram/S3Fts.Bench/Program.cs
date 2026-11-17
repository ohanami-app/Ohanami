using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace S3Fts.Bench;

/// <summary>
/// 合成文書(OCR 約300字 + 説明文 約120字)を FtsIndex に入れて、投入時間・DB サイズ・検索時間を測る。
/// 使い方: dotnet run -c Release --project S3Fts.Bench -- &lt;件数&gt; [DB を置くディレクトリ]
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[0], out int docCount) || docCount <= 0)
        {
            Console.Error.WriteLine("使い方: S3Fts.Bench <件数> [DBを置くディレクトリ]");
            return 2;
        }
        string dir = args.Length > 1 ? args[1] : Path.GetTempPath();
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, $"s3bench_{docCount}.db");
        foreach (string f in Directory.GetFiles(dir, $"s3bench_{docCount}.db*")) File.Delete(f);

        Console.WriteLine($"件数={docCount}  DB={dbPath}  Release={!IsDebug()}  .NET={Environment.Version}");

        // ---- 合成コーパス(生成時間は測定に含めない) ----
        var gen = new CorpusGenerator(seed: 20261105);
        var sw = Stopwatch.StartNew();
        List<string> docs = new(docCount);
        for (int i = 0; i < docCount; i++) docs.Add(gen.NextDocument());
        Console.WriteLine($"コーパス生成: {sw.Elapsed.TotalSeconds:F1}s, 平均 {docs.Average(d => d.Length):F0} 文字/件(UTF-16)、合計 {docs.Sum(d => (long)d.Length) / 1e6:F1} M 文字");

        // ---- 投入 ----
        using var idx = new FtsIndex(dbPath);
        Console.WriteLine(idx.DescribeSqlite());
        sw.Restart();
        FtsIndex.BatchStats st = idx.AddBatch(docs);
        TimeSpan load = sw.Elapsed;
        Console.WriteLine($"投入(1トランザクション): 合計 {load.TotalSeconds:F2}s = 正規化+トークン化 {st.Prep.TotalSeconds:F2}s + SQLite {st.Sql.TotalSeconds:F2}s  ({docCount / load.TotalSeconds:F0} 件/s)");
        sw.Restart();
        idx.Optimize();
        Console.WriteLine($"FTS5 optimize: {sw.Elapsed.TotalSeconds:F2}s");
        sw.Restart();
        idx.Vacuum();
        Console.WriteLine($"VACUUM: {sw.Elapsed.TotalSeconds:F2}s");
        long size = new FileInfo(dbPath).Length;
        Console.WriteLine($"DB ファイルサイズ(optimize + VACUUM 後): {size / 1048576.0:F1} MiB ({size / (double)docCount:F0} B/件)");
        var sizes = idx.TableSizes();
        if (sizes is null)
        {
            Console.WriteLine("(dbstat が使えないので、検索測定のあとに docs テーブルを DROP + VACUUM して fts だけのサイズを測る)");
        }
        else
        {
            long docsBytes = sizes.Where(kv => kv.Key == "docs").Sum(kv => kv.Value);
            long ftsBytes = sizes.Where(kv => kv.Key.StartsWith("fts", StringComparison.Ordinal)).Sum(kv => kv.Value);
            Console.WriteLine($"  内訳: docs テーブル(元テキスト + 正規化テキスト) {docsBytes / 1048576.0:F1} MiB / fts(fts_data, fts_idx, fts_docsize, fts_config) {ftsBytes / 1048576.0:F1} MiB");
            foreach (var kv in sizes.OrderByDescending(k => k.Value).Take(6)) Console.WriteLine($"    {kv.Key}: {kv.Value / 1048576.0:F1} MiB");
        }
        long rawChars = docs.Sum(d => (long)d.Length);
        Console.WriteLine($"  参考: 元テキストの UTF-8 バイト数の合計 ≈ {docs.Sum(d => (long)Encoding.UTF8.GetByteCount(d)) / 1048576.0:F1} MiB");

        // ---- 検索 ----
        // 2文字・4〜6文字は、実在する文書の一部から切り出す(必ず1件以上ヒットする)。
        var rnd = new Random(77);
        string[] two = gen.PickSubstrings(docs, 20, 2, 2, rnd);
        string[] mid = gen.PickSubstrings(docs, 20, 4, 6, rnd);
        string[] mixed = CorpusGenerator.MixedQueries.SelectMany(q => Enumerable.Repeat(q, 4)).ToArray(); // 5種類 × 4回 = 20回
        string[] one = gen.OneCharQueries(20);

        Console.WriteLine();
        Console.WriteLine("検索(各20回。ms。ids = SQL だけ / top50 = 上位50件の本文取得 + ハイライト計算 / all = 全ヒットの本文取得 + ハイライト計算)");
        Console.WriteLine("カテゴリ         | ヒット数(中央値 / 最大) | ids 中央値 / 最大 | top50 中央値 / 最大 | all 中央値 / 最大");
        Measure("2文字(FTS)", idx, two);
        Measure("4〜6文字(FTS)", idx, mid);
        Measure("日英混在(FTS)", idx, mixed);
        Measure("1文字(LIKE)", idx, one);

        Console.WriteLine();
        Console.WriteLine("使ったクエリ(例):");
        Console.WriteLine("  2文字: " + string.Join(" ", two.Take(8)));
        Console.WriteLine("  4〜6文字: " + string.Join(" ", mid.Take(6)));
        Console.WriteLine("  日英混在: " + string.Join(" ", CorpusGenerator.MixedQueries));
        Console.WriteLine("  1文字: " + string.Join(" ", one));
        Console.WriteLine($"Process private bytes (参考): {Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0:F0} MiB");
        if (sizes is null)
        {
            idx.ExecuteRaw("DROP TABLE docs");
            idx.Vacuum();
            long ftsOnly = new FileInfo(dbPath).Length;
            Console.WriteLine($"fts だけのサイズ(docs を削除して VACUUM): {ftsOnly / 1048576.0:F1} MiB / docs テーブル(元テキスト + 正規化テキスト + 主キー)= {(size - ftsOnly) / 1048576.0:F1} MiB");
        }
        idx.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string f in Directory.GetFiles(dir, $"s3bench_{docCount}.db*")) File.Delete(f);
        return 0;
    }

    private static bool IsDebug()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    private static void Measure(string label, FtsIndex idx, string[] queries)
    {
        // ウォームアップ(SQLite のページキャッシュと JIT)。測定には含めない。
        idx.SearchIds(queries[0]);
        idx.Search(queries[0], 50);

        var hits = new List<double>();
        var ids = new List<double>();
        var top = new List<double>();
        var all = new List<double>();
        foreach (string q in queries)
        {
            var sw = Stopwatch.StartNew();
            IReadOnlyList<long> r = idx.SearchIds(q);
            ids.Add(sw.Elapsed.TotalMilliseconds);
            hits.Add(r.Count);
            sw.Restart();
            idx.Search(q, 50);
            top.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            idx.Search(q);
            all.Add(sw.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"{label,-16} | {Median(hits),9:F0} / {hits.Max(),-9:F0} | {Median(ids),8:F1} / {ids.Max(),-8:F1} | {Median(top),8:F1} / {top.Max(),-8:F1} | {Median(all),8:F1} / {all.Max(),-8:F1}");
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        int n = s.Count;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
    }
}

/// <summary>
/// 日本語中心の合成文書。語彙は固定の乱数で作った擬似単語(かな・カナ・漢字)を Zipf 分布で選ぶので、
/// 出現頻度の偏りは自然言語に近い。助詞・句読点・カギ括弧・折り返しの改行・英数字(HP、LV99、10連など)を混ぜる。
/// </summary>
public sealed class CorpusGenerator
{
    public static readonly string[] MixedQueries = ["ガチャ10連", "HP回復", "LV99到達", "ステータスUP", "MP50消費"];

    private static readonly string[] Particles = ["は", "が", "を", "に", "で", "の", "と", "も", "へ", "から", "まで", "って"];
    private static readonly string[] Endings = ["です", "ます", "だよ", "かな", "ね", "よ", "した", "する", "ない", "だった", "している", "れる"];
    private static readonly string[] AsciiWords = ["HP", "MP", "LV", "EXP", "UP", "SKIP", "AUTO", "OK", "NEW", "BOSS", "Wi-Fi", "ID", "A", "B"];

    private readonly Random _rnd;
    private readonly string[] _vocab;
    private readonly double[] _cdf;

    public CorpusGenerator(int seed)
    {
        _rnd = new Random(seed);
        var hira = Enumerable.Range(0x3041, 0x3093 - 0x3041 + 1).Select(c => ((char)c).ToString()).ToArray(); // ぁ〜ん
        var kata = Enumerable.Range(0x30A1, 0x30F3 - 0x30A1 + 1).Select(c => ((char)c).ToString()).ToArray(); // ァ〜ン
        var kanji = Enumerable.Range(0x4E00, 2500).Select(c => ((char)c).ToString()).ToArray(); // 基本ブロックの先頭 2500 字
        var words = new List<string>();
        var seen = new HashSet<string>();
        while (words.Count < 6000)
        {
            int kind = _rnd.Next(10);
            string w;
            if (kind < 4) w = Pick(hira, 1, 4);
            else if (kind < 6) w = Pick(kata, 2, 6);
            else if (kind < 9) w = Pick(kanji, 1, 3) + (_rnd.Next(3) == 0 ? Pick(hira, 1, 2) : "");
            else w = Pick(kata, 2, 4) + Pick(kanji, 1, 2);
            if (seen.Add(w)) words.Add(w);
        }
        _vocab = words.ToArray();
        _cdf = new double[_vocab.Length];
        double sum = 0;
        for (int i = 0; i < _vocab.Length; i++) { sum += 1.0 / (i + 1); _cdf[i] = sum; }
        for (int i = 0; i < _cdf.Length; i++) _cdf[i] /= sum;
    }

    private string Pick(string[] pool, int min, int max)
    {
        int n = _rnd.Next(min, max + 1);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(pool[_rnd.Next(pool.Length)]);
        return sb.ToString();
    }

    private string Word()
    {
        double u = _rnd.NextDouble();
        int lo = 0, hi = _cdf.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_cdf[mid] < u) lo = mid + 1; else hi = mid;
        }
        return _vocab[lo];
    }

    private string Sentence()
    {
        var sb = new StringBuilder();
        int clauses = _rnd.Next(2, 5);
        for (int c = 0; c < clauses; c++)
        {
            int words = _rnd.Next(1, 4);
            for (int w = 0; w < words; w++)
            {
                sb.Append(Word());
                if (_rnd.Next(12) == 0) sb.Append(AsciiWords[_rnd.Next(AsciiWords.Length)]).Append(_rnd.Next(3) == 0 ? _rnd.Next(1, 100).ToString() : "");
                if (_rnd.Next(30) == 0) sb.Append(' ');
                sb.Append(Particles[_rnd.Next(Particles.Length)]);
            }
            sb.Append(Word()).Append(Endings[_rnd.Next(Endings.Length)]);
            sb.Append(c == clauses - 1 ? (_rnd.Next(5) == 0 ? "!?" : "。") : "、");
        }
        return sb.ToString();
    }

    /// <summary>セリフ風のテキストを target 字程度作る。約25字ごとに折り返しの改行を入れる(日本語どうしの間にも入る)。</summary>
    private string Paragraph(int target, bool quoted)
    {
        var sb = new StringBuilder();
        while (sb.Length < target)
        {
            string s = Sentence();
            if (quoted && _rnd.Next(3) == 0) s = "「" + s + "」";
            sb.Append(s);
            if (_rnd.Next(4) == 0) sb.Append('\n');
        }
        string t = sb.ToString();
        var wrapped = new StringBuilder();
        int col = 0;
        foreach (char ch in t)
        {
            wrapped.Append(ch);
            if (ch == '\n') { col = 0; continue; }
            if (++col >= 25 && _rnd.Next(3) == 0) { wrapped.Append('\n'); col = 0; }
        }
        return wrapped.ToString();
    }

    /// <summary>OCR 約300字 + 説明文 約120字。約2%の文書に、日英混在クエリが当たる文言を埋め込む。</summary>
    public string NextDocument()
    {
        string ocr = Paragraph(300, quoted: true);
        string desc = Paragraph(120, quoted: false);
        if (_rnd.Next(50) == 0)
        {
            string planted = MixedQueries[_rnd.Next(MixedQueries.Length)];
            ocr = ocr.Insert(_rnd.Next(ocr.Length), "\n" + planted + "を使った。\n");
        }
        return ocr + "\n" + desc;
    }

    /// <summary>文書から、日本語だけの連続部分(空白・改行・英数字・句読点を含まない)の部分文字列を切り出す。必ず元の文書にヒットする。</summary>
    public string[] PickSubstrings(List<string> docs, int count, int minLen, int maxLen, Random rnd)
    {
        var result = new List<string>();
        var seen = new HashSet<string>();
        int guard = 0;
        while (result.Count < count && guard++ < 100000)
        {
            string d = docs[rnd.Next(docs.Count)];
            int len = rnd.Next(minLen, maxLen + 1);
            int start = rnd.Next(Math.Max(1, d.Length - len));
            string sub = d.Substring(start, Math.Min(len, d.Length - start));
            if (sub.Length != len) continue;
            if (!sub.All(ch => CharClassifier.IsJapanese(new Rune(ch)))) continue;
            if (seen.Add(sub)) result.Add(sub);
        }
        if (result.Count < count) throw new InvalidOperationException("クエリを作れなかった");
        return result.ToArray();
    }

    /// <summary>1文字クエリ: よく出る文字(助詞など)から稀な漢字まで。</summary>
    public string[] OneCharQueries(int count)
    {
        string[] common = ["の", "に", "を", "は", "が", "と", "で", "も", "し", "る"];
        var rare = Enumerable.Range(0, count - common.Length).Select(i => ((char)(0x4E00 + 100 + i * 120)).ToString());
        return common.Concat(rare).ToArray();
    }
}
