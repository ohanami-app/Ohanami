using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace S1Vlm;

/// <summary>テストフレームワークを使わない、数件の assert。</summary>
public static class SelfTest
{
    private static int _checks;
    private static readonly List<string> Failures = new();

    private static void Eq<T>(T expected, T actual, string what)
    {
        _checks++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            Failures.Add($"{what}: 期待 {expected} / 実際 {actual}");
    }

    private static void Near(double expected, double? actual, string what)
    {
        _checks++;
        if (actual is null || Math.Abs(expected - actual.Value) > 1e-9)
            Failures.Add($"{what}: 期待 {expected} / 実際 {(actual is null ? "null" : actual.Value.ToString())}");
    }

    private static int Lev(string a, string b) => TextNorm.Levenshtein(TextNorm.ToCodePoints(a), TextNorm.ToCodePoints(b));

    public static int Run()
    {
        // ---- 正規化: NFKC → 小文字化 → 空白・改行の除去
        Eq("abc123", TextNorm.Normalize("ＡＢＣ　１２３"), "NFKC(全角英数と全角空白)");
        Eq("abcdef", TextNorm.Normalize("A b\tC\r\nd\n e F"), "空白・タブ・改行の除去と小文字化");
        Eq("ガ", TextNorm.Normalize("ｶﾞ"), "NFKC(半角カナ+濁点→全角1文字)");
        Eq("せりふ", TextNorm.Normalize("せ り\nふ"), "日本語の途中の改行・空白の除去");
        Eq("", TextNorm.Normalize(" 　\r\n\t"), "空白だけ→空");
        Eq("(株)", TextNorm.Normalize("㈱"), "NFKC(㈱→(株))");
        Eq("i", TextNorm.Normalize("I"), "ToLowerInvariant");

        // ---- レーベンシュタイン距離(コードポイント単位)
        Eq(3, Lev("kitten", "sitting"), "kitten/sitting");
        Eq(0, Lev("", ""), "空と空");
        Eq(3, Lev("", "abc"), "空とabc");
        Eq(3, Lev("abc", ""), "abcと空");
        Eq(0, Lev("こんにちは", "こんにちは"), "同一");
        Eq(2, Lev("こんにちは", "こんばんは"), "こんにちは/こんばんは");
        Eq(1, Lev("𠮷a", "a"), "サロゲートペア1文字は1コードポイント");
        Eq(1, Lev("𠮷a", "𠮷b"), "サロゲートペアを含む置換");
        Eq(2, TextNorm.ToCodePoints("𠮷a").Length, "コードポイント数");

        // ---- bigram(多重集合)
        var bg = TextNorm.Bigrams(TextNorm.ToCodePoints("abcd"));
        Eq(3, TextNorm.Total(bg), "abcd の bigram 数");
        var aaa = TextNorm.Bigrams(TextNorm.ToCodePoints("aaa"));
        Eq(2, aaa[('a', 'a')], "aaa の aa は2個(多重集合)");
        Eq(0, TextNorm.Total(TextNorm.Bigrams(TextNorm.ToCodePoints("a"))), "1文字の bigram は0");
        Eq(0, TextNorm.Total(TextNorm.Bigrams(Array.Empty<int>())), "空の bigram は0");
        // 参照 aaaa(aa x3) と出力 aaa(aa x2): 共通2
        Eq(2, TextNorm.CommonCount(TextNorm.Bigrams(TextNorm.ToCodePoints("aaa")), TextNorm.Bigrams(TextNorm.ToCodePoints("aaaa"))), "共通部分は min の合計");
        // 参照 abab(ab,ba,ab) と出力 abba(ab,bb,ba): 共通 ab1 + ba1 = 2
        Eq(2, TextNorm.CommonCount(TextNorm.Bigrams(TextNorm.ToCodePoints("abba")), TextNorm.Bigrams(TextNorm.ToCodePoints("abab"))), "abba/abab の共通");

        // ---- 比較(CER・再現率・適合率)
        CompareResult same = Scoring.Compare("こんにちは 世界", "こんにちは世界");
        Eq(0, same.Distance, "空白差は無視される(距離)");
        Near(0.0, same.Cer, "空白差は無視される(CER)");
        Near(1.0, same.Recall, "同一の再現率");
        Near(1.0, same.Precision, "同一の適合率");

        CompareResult one = Scoring.Compare("こんばんは", "こんにちは");
        Eq(5, one.RefLen, "参照の文字数");
        Near(2.0 / 5.0, one.Cer, "CER=2/5");
        // ref bigram: こん んに にち ちは / out: こん んば ばん んは → 共通は こん のみ
        Eq(4, one.RefBigrams, "参照の bigram 数");
        Eq(1, one.Common, "共通 bigram");
        Near(0.25, one.Recall, "再現率=1/4");
        Near(0.25, one.Precision, "適合率=1/4");

        CompareResult emptyOut = Scoring.Compare("", "ab");
        Near(1.0, emptyOut.Cer, "出力が空なら CER=1");
        Eq(null, emptyOut.Precision, "出力の bigram が0なら適合率は出さない");
        Near(0.0, emptyOut.Recall, "出力が空なら再現率=0");

        CompareResult emptyRef = Scoring.Compare("あい", "");
        Eq(null, emptyRef.Cer, "参照が空なら CER は出さない");
        Eq(2, emptyRef.OutLen, "参照が空のとき出力の文字数を報告");
        Eq(null, emptyRef.Recall, "参照の bigram が0なら再現率は出さない");

        // ---- score の通し(一時ディレクトリにダミーの run 出力と参照を作る)
        ScoreEndToEnd();

        if (Failures.Count > 0)
        {
            Console.Error.WriteLine($"selftest 失敗: {Failures.Count} 件(全 {_checks} チェック)");
            foreach (string f in Failures) Console.Error.WriteLine("  NG " + f);
            return 1;
        }
        Console.WriteLine($"selftest OK ({_checks} チェック)");
        return 0;
    }

    private static void ScoreEndToEnd()
    {
        string root = Path.Combine(Path.GetTempPath(), "s1vlm-selftest-" + Guid.NewGuid().ToString("N"));
        string runDir = Path.Combine(root, "run");
        string refsDir = Path.Combine(root, "refs");
        Directory.CreateDirectory(runDir);
        Directory.CreateDirectory(refsDir);
        try
        {
            var samples = new List<SampleRow>
            {
                new("a.png", 10, 10, "dialog_plain", "plain"),
                new("b.png", 10, 10, "dialog_plain", "plain"),
                new("c.png", 10, 10, "no_text", "no_text"),
                new("d.png", 10, 10, "no_text", "no_text"),
                new("e.png", 10, 10, "large", "hard"),
                new("f.png", 10, 10, "dialog_plain", "plain"), // パース失敗(参照 4 文字)
            };
            // a: 完全一致 / b: 1文字違い(参照4文字→CER 0.25) / c: 参照空・出力空 / d: 参照空・出力あり / e: 未実行
            WriteRun(runDir, "a", true, "あいうえ");
            WriteRun(runDir, "b", true, "あいうお");
            WriteRun(runDir, "c", true, "");
            WriteRun(runDir, "d", true, "でっち上げ");
            WriteRun(runDir, "f", false, "");
            File.WriteAllText(Path.Combine(refsDir, "f.txt"), "かきくけ");
            File.WriteAllText(Path.Combine(refsDir, "a.txt"), "あい\nうえ");
            File.WriteAllText(Path.Combine(refsDir, "b.txt"), "あいうえ");
            File.WriteAllText(Path.Combine(refsDir, "c.txt"), "");
            File.WriteAllText(Path.Combine(refsDir, "d.txt"), "");

            // samples.tsv にない画像の出力(評価セットから外した画像)は、採点に混ざらず、検出されて件数が分かる
            WriteRun(runDir, "removed", true, "混ざってはいけない");
            File.WriteAllText(Path.Combine(runDir, "run-info.json"), "{}");
            List<string> extra = RunFiles.FindExtraOutputs(runDir, samples);
            Eq(1, extra.Count, "samples.tsv にない出力は1件");
            Eq("removed.json", extra.Count > 0 ? extra[0] : "", "samples.tsv にない出力の名前(run-info.json は除く)");

            List<ImageScore> sc = ScoreCommand.ScoreRun(runDir, refsDir, samples, false);
            Eq(samples.Count, sc.Count, "採点の行数は samples.tsv の画像数のまま");
            Eq("ok", sc[0].Status, "a: ok");
            Near(0.0, sc[0].Result!.Cer, "a: CER 0");
            Near(0.25, sc[1].Result!.Cer, "b: CER 0.25");
            Eq("no_ref", sc[2].Status, "c: 参照が空");
            Eq(0, sc[2].Result!.OutLen, "c: 出力の文字数0");
            Eq(5, sc[3].Result!.OutLen, "d: 出力の文字数5");
            Eq("not_run", sc[4].Status, "e: 未実行");

            var plain = ScoreCommand.Aggregate(sc, s => s.Group)["plain"];
            Eq(2, plain.Scored, "plain: 採点2枚");
            Eq(1, (int)plain.SumDist, "plain: 距離合計1");
            Eq(8, (int)plain.SumRefLen, "plain: 参照文字数合計8");
            // パース失敗(f)は、[A]除外では plain に含まれず件数だけ、[B]空とみなす集計では CER=1(距離4/参照4文字)で含まれる
            Eq(1, plain.ParseFailed, "[A] plain: パース失敗1枚(除外)");
            Eq("parse_failed", sc[5].Status, "f: パース失敗");
            var plainB = ScoreCommand.Aggregate(sc, s => s.Group, true)["plain"];
            Eq(3, plainB.Scored, "[B] plain: 空とみなして3枚");
            Eq(5, (int)plainB.SumDist, "[B] plain: 距離合計 1+4");
            Eq(12, (int)plainB.SumRefLen, "[B] plain: 参照文字数合計 8+4");
            Eq(2, plainB.Precisions.Count, "[B] plain: 適合率は空出力の画像では計算しない(2枚だけ)");
            Near(1.0, plainB.Cers[2], "[B] f の CER=1");
            Near(0.0, plainB.Recalls[2], "[B] f の再現率=0");
            var nt = ScoreCommand.Aggregate(sc, s => s.Group)["no_text"];
            Eq(1, nt.NoRefNonEmptyOutput, "no_text: 出力が空でない画像1枚");

            // 参照がない場合は止まる
            File.Delete(Path.Combine(refsDir, "b.txt"));
            bool threw = false;
            try { ScoreCommand.ScoreRun(runDir, refsDir, samples, false); }
            catch (InvalidOperationException) { threw = true; }
            Eq(true, threw, "参照が欠けていたら例外");

            // score.tsv の書き出しが通る
            ScoreCommand.WriteScoreTsv(Path.Combine(root, "score.tsv"), ScoreCommand.ScoreRun(runDir, refsDir, samples, true));
            Eq(true, File.Exists(Path.Combine(root, "score.tsv")), "score.tsv が書ける");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { /* 一時ディレクトリの掃除失敗は無視してよい */ }
        }
    }

    private static void WriteRun(string runDir, string stem, bool ok, string ocr)
    {
        var node = new JsonObject
        {
            ["parsed"] = new JsonObject { ["ok"] = ok, ["ocr"] = ocr, ["description"] = "x", ["error"] = null },
        };
        RunFiles.WriteJson(Path.Combine(runDir, stem + ".json"), node);
    }
}
