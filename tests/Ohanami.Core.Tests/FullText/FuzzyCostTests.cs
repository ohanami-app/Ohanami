using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.FullText;
using Ohanami.Core.Tests.Captions;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// あいまい一致のコストの実測(ADR 0032 の「コストの上限」: 1 万件で 1 回の検索が 200 ms を超えたら、この作り方をやめる)。
/// 合成の文書(私物ではない): 1 件 約 500 字の日本語(ひらがな・カタカナ・漢字・句読点・空白・英数字を混ぜた、決まった種の疑似乱数)× 1 万件を DB の search_text に入れ、
/// 実物の SQLite から norm_text を読んで総当たりで当てる時間を測る。
/// <para>
/// **時間の判定は、環境変数 OHANAMI_PERF_TESTS=1 のときだけ動く**(普段のテストでは走らない。負荷のかかった実行で、時間に頼るテストがときどき落ちたため)。
/// 合成の 1 万件の生成と、直列・並列で 4 語 × 10 回の測定は、そちらに入れる。並列と直列が同じ結果を返すこと(<see cref="The_parallel_scan_returns_exactly_what_the_serial_scan_returns"/>)は、
/// 時間に頼らないので、普段どおり走る(2000 件)。測定の数字は、実行すると %TEMP% の fuzzy-cost.txt にも書く。
/// </para>
/// </summary>
public sealed class FuzzyCostTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>時間に頼るテストを動かすか(環境変数 OHANAMI_PERF_TESTS=1)。</summary>
    private static bool PerfEnabled => Environment.GetEnvironmentVariable("OHANAMI_PERF_TESTS") == "1";

    private const int Documents = 10_000;
    private const int CharsPerDocument = 500;
    private readonly CaptionFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    /// <summary>決まった種から作る合成の文書(正規化済みの形で DB に入れる)。ひらがな 60%・カタカナ 15%・漢字 15%・英数字 3%・句読点や記号 5%・空白 2%。</summary>
    internal static string SyntheticDocument(Random rng, int length)
    {
        var sb = new StringBuilder(length + 8);
        while (sb.Length < length)
        {
            int roll = rng.Next(100);
            if (roll < 60) sb.Append((char)(0x3041 + rng.Next(0x56)));                  // ひらがな
            else if (roll < 75) sb.Append((char)(0x30A1 + rng.Next(0x56)));             // カタカナ
            else if (roll < 90) sb.Append((char)(0x4E00 + rng.Next(300)));              // 漢字(300 種)
            else if (roll < 93) sb.Append((char)('a' + rng.Next(26)));                  // 英数字
            else if (roll < 98) sb.Append("、。！…「」"[rng.Next(6)]);                    // 句読点・記号
            else sb.Append(' ');
        }
        return sb.ToString();
    }

    private async Task<List<string>> SeedAsync(int count)
    {
        await _fx.AddImagesAsync(count);
        var rng = new Random(20261110);
        var docs = new List<string>(count);
        await _fx.Db.WriteAsync((conn, tx) =>
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO search_text(hash, norm_text, model_id) VALUES ($h, $n, NULL)";
            SqliteParameter h = cmd.Parameters.Add("$h", SqliteType.Text), n = cmd.Parameters.Add("$n", SqliteType.Text);
            for (int i = 1; i <= count; i++)
            {
                string doc = SyntheticDocument(rng, CharsPerDocument).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
                docs.Add(doc);
                h.Value = CaptionFixture.Hash(i);
                n.Value = doc;
                cmd.ExecuteNonQuery();
            }
        }, TestContext.Current.CancellationToken);
        return docs;
    }

    [Fact]
    public async Task One_fuzzy_scan_over_ten_thousand_documents_of_about_five_hundred_characters_stays_under_the_200ms_limit()
    {
        Assert.SkipUnless(PerfEnabled, "環境変数 OHANAMI_PERF_TESTS=1 が無い(時間の判定は手元で測るときだけ。負荷で揺れるため、普段のテストでは走らせない)");
        await SeedAsync(Documents);
        var lines = new List<string>();
        void Out(string line) { output.WriteLine(line); lines.Add(line); }
        // 構成によって速さが違うので、どちらで測ったかを残す。上限(200 ms)は製品の構成(Release)の値。Debug は遅いので、桁が外れていないかを見るだけの緩い線にする
#if DEBUG
        const string Config = "Debug";
        const double LimitMs = 1000;
#else
        const string Config = "Release";
        const double LimitMs = 200;
#endif
        int threads = FuzzyScan.DefaultMaxThreads;
        Out($"構成: {Config}、.NET {Environment.Version}、論理 CPU {Environment.ProcessorCount}、並列の上限(FuzzyScan.DefaultMaxThreads = 論理 CPU の半分): {threads}");
        string[] words = ["とろんぷ", "とろんぷ、ねむ", "植木鉢のかご", "ノルテリアの花"];   // 4 文字・境界つき・長めの語
        double worstMedian = 0, worstMax = 0;
        foreach (string word in words)
        {
            FuzzyQuery query = FuzzyMatcher.TryPlan(word)!;
            foreach (int t in new[] { 1, threads })
            {
                // ウォームアップ(JIT)の 1 回は測らない。そのあと 10 回測って、平均・中央値・最大を出す
                Scan(query, t);
                var times = new List<double>();
                int hits = 0;
                for (int k = 0; k < 10; k++)
                {
                    var sw = Stopwatch.StartNew();
                    hits = Scan(query, t);
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
                double median = times.Order().ElementAt(times.Count / 2);
                Out($"語「{word}」(項目 {query.Items.Length})、スレッド {t}: {Documents} 件 × 約 {CharsPerDocument} 字、当たり {hits} 件、{times.Count} 回の平均 {times.Average():F1} ms、中央値 {median:F1} ms、最大 {times.Max():F1} ms");
                if (t == threads) { worstMedian = Math.Max(worstMedian, median); worstMax = Math.Max(worstMax, times.Max()); }
            }
        }
        Out($"全部の語の、スレッド {threads} での 中央値の最大 {worstMedian:F1} ms、最大の最大 {worstMax:F1} ms(上限 {LimitMs:F0} ms)");
        System.IO.File.WriteAllLines(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fuzzy-cost.txt"), lines);
        // 測定の判定(事前に決めた規則)は最大で行った。テストとして残す線は、マシンの負荷の揺れで落ちないよう、中央値にする
        Assert.True(worstMedian <= LimitMs, $"全部の語の中央値の最大が {worstMedian:F1} ms(上限 {LimitMs:F0} ms)");
    }

    [Fact]
    public async Task The_parallel_scan_returns_exactly_what_the_serial_scan_returns()
    {
        await SeedAsync(2_000);
        foreach (string word in new[] { "とろんぷ", "とろんぷ、ねむ", "植木鉢のかご" })
        {
            FuzzyQuery query = FuzzyMatcher.TryPlan(word)!;
            string[] serial = FuzzyScan.ScanTable(_fx.Db, [query], 1, TestContext.Current.CancellationToken);
            foreach (int t in new[] { 2, 3, 8 })
                Assert.Equal(serial, FuzzyScan.ScanTable(_fx.Db, [query], t, TestContext.Current.CancellationToken));
            Assert.Equal(serial.Order(StringComparer.Ordinal).ToArray(), serial);   // 並びは hash の序数順
        }
    }

    /// <summary>実物の SQLite から norm_text を読んで、あいまい一致の走査に当てる(検索 1 回の fallback のコスト)。当たりの件数を返す。</summary>
    private int Scan(FuzzyQuery query, int threads) => FuzzyScan.ScanTable(_fx.Db, [query], threads, TestContext.Current.CancellationToken).Length;
}
