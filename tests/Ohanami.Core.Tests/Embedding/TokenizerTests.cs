using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Ohanami.Core.Embedding;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// トークナイザの一致。正解は tools/siglip2-reference/gen_tokenizer_corpus.py が Python(tokenizers)で一度だけ生成した
/// TestData/tokenizer-corpus.tsv。1件でも違えば実装の誤りで、回避のための前段のサニタイズは入れない。
/// </summary>
public sealed class TokenizerTests(ITestOutputHelper output)
{
    private sealed record Row(int No, string Category, string Text, string Cleaned, int[] Ids);

    private static readonly Lazy<List<Row>> Corpus = new(LoadCorpus);
    private static readonly Lazy<Siglip2Tokenizer> Tokenizer =
        new(() => Siglip2Tokenizer.Load(TestFiles.VerifiedModelFile("textual/tokenizer.json")));

    private static List<Row> LoadCorpus()
    {
        string path = TestFiles.TestData("tokenizer-corpus.tsv");
        var rows = new List<Row>();
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] c = line.Split('\t');
            if (c.Length != 5) throw new InvalidDataException($"列数が 5 でない: {line}");
            rows.Add(new Row(
                int.Parse(c[0]),
                c[1],
                JsonSerializer.Deserialize<string>(c[2])!,
                JsonSerializer.Deserialize<string>(c[3])!,
                c[4].Split(' ').Select(int.Parse).ToArray()));
        }
        return rows;
    }

    [Fact]
    public void Corpus_HasAtLeast500Rows_AndAllRequiredCategories()
    {
        var rows = Corpus.Value;
        Assert.True(rows.Count >= 500, $"コーパスが {rows.Count} 件しかない");
        string[] required =
        [
            "ja_short", "en", "mixed", "digits", "width", "rare", "ascii_punct", "fullwidth_punct", "whitespace", "whitespace_each",
            "empty", "added_tokens", "merge_order", "long", "long_boundary", "rand_ja", "rand_mixed", "rand_ascii", "rand_exotic",
        ];
        foreach (string cat in required) Assert.Contains(rows, r => r.Category == cat);
        Assert.All(rows, r => Assert.Equal(64, r.Ids.Length));
        output.WriteLine($"コーパス {rows.Count} 件、{rows.Select(r => r.Category).Distinct().Count()} カテゴリ");
    }

    [Fact]
    public void Clean_MatchesPython_ForEveryRow()
    {
        var bad = new List<string>();
        foreach (Row r in Corpus.Value)
        {
            string actual = TextCleaner.Clean(r.Text);
            if (!string.Equals(actual, r.Cleaned, StringComparison.Ordinal))
                bad.Add($"#{r.No} [{r.Category}] 入力={Esc(r.Text)} 期待={Esc(r.Cleaned)} 実際={Esc(actual)}");
        }
        Assert.True(bad.Count == 0, $"{bad.Count} 件が不一致:\n" + string.Join("\n", bad.Take(20)));
    }

    [Fact]
    public void Encode_MatchesPython_ForEveryRow()
    {
        Siglip2Tokenizer tok = Tokenizer.Value;
        var bad = new List<string>();
        foreach (Row r in Corpus.Value)
        {
            int[] actual = tok.Encode(r.Text);
            if (!actual.AsSpan().SequenceEqual(r.Ids))
            {
                int first = Enumerable.Range(0, 64).First(i => actual[i] != r.Ids[i]);
                bad.Add($"#{r.No} [{r.Category}] 入力={Esc(r.Text)} 最初の違い: 位置 {first} 期待 {r.Ids[first]} 実際 {actual[first]}");
            }
        }
        Assert.True(bad.Count == 0, $"{bad.Count}/{Corpus.Value.Count} 件が不一致:\n" + string.Join("\n", bad.Take(20)));
        output.WriteLine($"全 {Corpus.Value.Count} 件で id の列が完全に一致した");
    }

    [Fact]
    public void MergeOrder_Cases_AreInTheCorpus_AndMatch()
    {
        // 「ああああ」「wwww」「1000000」(同じ rank の merge を左から)が正解データに含まれ、一致していること
        Siglip2Tokenizer tok = Tokenizer.Value;
        foreach (string text in new[] { "ああああ", "wwww", "1000000" })
        {
            Row row = Corpus.Value.First(r => r.Text == text);
            Assert.Equal(row.Ids, tok.Encode(text));
        }
    }

    [Fact]
    public void Truncation_KeepsEosAt63_AndPadsWith0()
    {
        Siglip2Tokenizer tok = Tokenizer.Value;
        foreach (Row r in Corpus.Value.Where(r => r.Category is "long" or "long_boundary"))
        {
            int[] ids = tok.Encode(r.Text);
            int eosAt = Array.IndexOf(ids, tok.EosId);
            Assert.True(eosAt is >= 0 and <= 63);
            Assert.All(ids.Skip(eosAt + 1), id => Assert.Equal(tok.PadId, id));
        }
        // 63 token ちょうど / 64 token(切り詰めが起きる)の両方がコーパスにある
        Assert.Contains(Corpus.Value.Where(r => r.Category == "long_boundary"), r => r.Ids[63] == tok.EosId);
        Assert.Contains(Corpus.Value.Where(r => r.Category == "long_boundary"), r => r.Ids[62] == tok.EosId);
    }

    [Fact]
    public void Empty_IsEosThenPads()
    {
        Siglip2Tokenizer tok = Tokenizer.Value;
        int[] ids = tok.Encode("");
        Assert.Equal(tok.EosId, ids[0]);
        Assert.All(ids.Skip(1), id => Assert.Equal(tok.PadId, id));
        Assert.Equal(ids, tok.Encode("   \t\n"));
        Assert.Equal(ids, tok.Encode("!!!")); // ASCII 句読点だけ → 除去で空
    }

    [Fact]
    public void BrokenSurrogates_Throw()
    {
        Siglip2Tokenizer tok = Tokenizer.Value;
        foreach (string s in new[] { "\uD800", "a\uDC00b", "\uD800a", "ab\uD800" })
        {
            Assert.Throws<ArgumentException>(() => tok.Encode(s));
            Assert.Throws<ArgumentException>(() => TextCleaner.Clean(s));
        }
        Assert.Throws<ArgumentNullException>(() => tok.Encode(null!));
    }

    [Fact]
    public void KnownDifferences_AreDocumented_NotHidden()
    {
        // 参照の Python と違う入力(コーパスから外してある): İ(U+0130)と Σ(U+03A3)。ここでは「違う」ことを確認して記録する。
        // İ: Python の lower() は2文字("i̇" = i + U+0307)。Ohanami は1対1の写像なので U+0130 は表にない(そのまま)。
        Assert.Equal("İ", TextCleaner.Clean("İ"));
        // Σ: Python は語末で ς、それ以外で σ。Ohanami は常に σ。
        Assert.Equal("σ", TextCleaner.Clean("Σ"));
        Assert.Equal("ασ", TextCleaner.Clean("ΑΣ")); // Python は "ας"(語末)
    }

    /// <summary>
    /// 検索は複数の呼び出しから同じトークナイザ・TextCleaner を共有する(読み込んだあとは読み取りだけの状態)。
    /// 並行に回した結果が、コーパスの正解(= 逐次の結果)と一致すること。
    /// </summary>
    [Fact]
    public void Encode_InParallel_MatchesPython_ForEveryRow()
    {
        Siglip2Tokenizer tok = Tokenizer.Value;
        List<Row> rows = Corpus.Value;
        const int Rounds = 8;
        var actual = new int[rows.Count * Rounds][];
        var cleaned = new string[rows.Count * Rounds];
        Parallel.For(0, actual.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            Row r = rows[i % rows.Count];
            actual[i] = tok.Encode(r.Text);
            cleaned[i] = TextCleaner.Clean(r.Text);
        });
        for (int i = 0; i < actual.Length; i++)
        {
            Row r = rows[i % rows.Count];
            Assert.True(actual[i].AsSpan().SequenceEqual(r.Ids), $"#{r.No} [{r.Category}] の並行の結果が正解と違う");
            Assert.Equal(r.Cleaned, cleaned[i]);
        }
        output.WriteLine($"{rows.Count} 件 × {Rounds} 回を 8 スレッドで並行に回した結果が、正解と一致");
    }

    [Fact]
    public void TokenizerJson_LoadTime_IsMeasured()
    {
        string path = TestFiles.VerifiedModelFile("textual/tokenizer.json");
        var times = new List<double>();
        Siglip2Tokenizer? tok = null;
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            tok = Siglip2Tokenizer.Load(path);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        output.WriteLine($"tokenizer.json({new FileInfo(path).Length / 1e6:F1} MB)の読み込み: 1回目 {times[0]:F0} ms(JIT 込み)、2〜5回目の中央値 {Median(times.Skip(1).ToList()):F0} ms、最小 {times.Min():F0} ms");
        output.WriteLine($"語彙 {tok!.VocabSize}、merges {tok.MergeCount}、added_tokens {tok.AddedTokenCount}");
        Assert.Equal(256000, tok.VocabSize);
        Assert.Equal(580604, tok.MergeCount);
        Assert.Equal(249, tok.AddedTokenCount);
        Assert.Equal(0, tok.PadId);
        Assert.Equal(1, tok.EosId);
    }

    /// <summary>
    /// 参考(判定には使わない): 参照の Python(Unicode 13.0)の小文字化の表と、.NET の ToLowerInvariant の違い。
    /// 表を使う理由(ランタイムの Unicode のバージョンに依存しない)を、違いの数で示す。
    /// </summary>
    [Fact]
    public void LowercaseTable_VersusDotNet_IsReportedNotHidden()
    {
        int[] pairs = Ohanami.Core.Embedding.Generated.PythonTextTables.LowercasePairs;
        var table = new Dictionary<int, int>();
        for (int i = 0; i < pairs.Length; i += 2) table[pairs[i]] = pairs[i + 1];
        int onlyDotNet = 0, onlyPython = 0, different = 0;
        var examples = new List<string>();
        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF) continue;
            string c = char.ConvertFromUtf32(cp);
            string dn = c.ToLowerInvariant();
            bool py = table.TryGetValue(cp, out int pyLower);
            string pyStr = py ? char.ConvertFromUtf32(pyLower) : c;
            if (dn == pyStr) continue;
            if (py && dn == c) onlyPython++;
            else if (!py) onlyDotNet++;
            else different++;
            if (examples.Count < 12) examples.Add($"U+{cp:X4} .NET→U+{(char.ConvertToUtf32(dn, 0)):X4} Python→U+{(py ? pyLower : cp):X4}");
        }
        output.WriteLine($"小文字化の違い(.NET {Environment.Version} と Python {Ohanami.Core.Embedding.Generated.PythonTextTables.PythonVersion}/Unicode {Ohanami.Core.Embedding.Generated.PythonTextTables.UnicodeVersion}): .NET だけが写す {onlyDotNet}、Python だけが写す {onlyPython}、両方が写すが先が違う {different}");
        foreach (string e in examples) output.WriteLine("  " + e);
        Assert.True(onlyDotNet + onlyPython + different >= 0);
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private static string Esc(string s) => "\"" + string.Concat(s.Select(c => c < 0x20 || c > 0x7e ? $"\\u{(int)c:X4}" : c.ToString())) + "\"";
}
