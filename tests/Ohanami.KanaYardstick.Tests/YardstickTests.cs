using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Media.Imaging;
using Xunit;

namespace Ohanami.KanaYardstick.Tests;

/// <summary>画像の仕様の作り方。同じ種から同じ文ができることは、測った数字との比較の前提(文が変わると、別の物差しになる)。</summary>
public class ImageSpecTests
{
    [Fact]
    public void Build_Makes90ImagesWithUniqueIds()
    {
        IReadOnlyList<Spec> specs = ImageSpecs.Build(ImageSpecs.DefaultSeed);
        Assert.Equal(90, specs.Count);
        Assert.Equal(90, specs.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(15, specs.GroupBy(s => (s.Height, s.Bg)).Count());
        Assert.All(specs.GroupBy(s => (s.Height, s.Bg)), g => Assert.Equal(6, g.Count()));
    }

    [Fact]
    public void Build_IsDeterministicForTheSameSeedAndDiffersForAnother()
    {
        IReadOnlyList<Spec> a = ImageSpecs.Build(12345), b = ImageSpecs.Build(12345), c = ImageSpecs.Build(12346);
        Assert.Equal(a.SelectMany(s => s.Lines), b.SelectMany(s => s.Lines));
        Assert.NotEqual(a.SelectMany(s => s.Lines), c.SelectMany(s => s.Lines));
    }

    /// <summary>既定の種の最初の画像の 1 行目。生成の手順(乱数の使い方)を変えると、これが変わる = 測った数字と比べられなくなる。</summary>
    [Fact]
    public void Build_DefaultSeedReproducesTheMeasuredText()
    {
        Spec first = ImageSpecs.Build(ImageSpecs.DefaultSeed)[0];
        Assert.Equal("1280x720-dark-h12-0", first.Id);
        Assert.Equal(unchecked(ImageSpecs.DefaultSeed * 1000), first.Seed);
        Assert.Equal("ヤイポブグヂゼミネポボヒクガポラムザデゴパベザフノモグゾグブ", first.Lines[0]);
    }

    [Fact]
    public void Build_LinesAlternateScriptsAndNeverRepeatACharacterTwiceInARow()
    {
        foreach (Spec s in ImageSpecs.Build(ImageSpecs.DefaultSeed))
        {
            Assert.True(s.Lines.Length % 2 == 0, s.Id);
            for (int i = 0; i < s.Lines.Length; i++)
            {
                string line = s.Lines[i];
                bool katakana = line.All(c => c is >= 'ァ' and <= 'ヶ');
                bool hiragana = line.All(c => c is >= 'ぁ' and <= 'ゖ');
                Assert.True(katakana || hiragana, $"{s.Id} 行 {i}: 1 つの行に 2 つの文字種が混ざっている");
                if (i > 0) Assert.NotEqual(katakana, s.Lines[i - 1].All(c => c is >= 'ァ' and <= 'ヶ'));
                for (int k = 1; k < line.Length; k++) Assert.NotEqual(line[k - 1], line[k]);
                string asHiragana = new(line.Select(c => katakana ? (char)(c - 0x60) : c).ToArray());
                Assert.All(asHiragana, c => Assert.True(ImageSpecs.Voiced.Contains(c) || ImageSpecs.Plain.Contains(c), $"{s.Id}: 想定外の字 {c}"));
                int voiced = asHiragana.Count(ImageSpecs.Voiced.Contains);
                Assert.InRange(voiced / (double)asHiragana.Length, 0.5, 0.7);
            }
            // 1 枚に約 120 字
            Assert.InRange(s.Lines.Sum(l => l.Length), 118, 130);
        }
    }

    [Fact]
    public void Voiced_HoldsEveryVoicedAndSemiVoicedKanaAndPlainHoldsTheirPlainSounds()
    {
        Assert.All(ImageSpecs.Voiced, c => Assert.NotEqual(0, Scoring.Decompose(c).Mark));
        Assert.All(ImageSpecs.Plain, c => Assert.Equal(0, Scoring.Decompose(c).Mark));
        foreach (char v in ImageSpecs.Voiced) Assert.Contains((char)Scoring.Decompose(v).Base, ImageSpecs.Plain);
    }
}

/// <summary>採点の数え方。濁点・半濁点だけの誤りと、ほかの誤りを分ける(ADR 0019)。</summary>
public class ScoringTests
{
    private static Tally Count(string reference, string hypothesis) => Sum(Scoring.Count(Scoring.Normalize(reference), Scoring.Normalize(hypothesis)).ByScript);

    private static Tally Sum(Tally[] t)
    {
        var all = new Tally();
        foreach (Tally x in t) all.Add(x);
        return all;
    }

    [Fact]
    public void Normalize_AppliesNfkcLowercaseAndRemovesWhitespace()
    {
        Assert.Equal("がabc", Scoring.Normalize("が ABC　"));
        Assert.Equal("ｱ".Normalize(NormalizationForm.FormKC), Scoring.Normalize("ｱ"));
    }

    [Fact]
    public void ExactMatchHasNoErrors()
    {
        Tally t = Count("がぎぐげご", "がぎぐげご");
        Assert.Equal(5, t.Ref);
        Assert.Equal(5, t.Voiced);
        Assert.Equal(0, t.Errors);
    }

    [Fact]
    public void LostAddedAndSwappedMarksAreCountedAsDakutenOnlyErrors()
    {
        Tally lost = Count("が", "か");
        Assert.Equal((1, 1, 0, 0), (lost.Dak, lost.DakLost, lost.DakAdded, lost.DakSwap));
        Tally added = Count("か", "が");
        Assert.Equal((1, 0, 1, 0), (added.Dak, added.DakLost, added.DakAdded, added.DakSwap));
        Assert.Equal(0, added.Voiced);   // 正解の字が付かない字なので、濁字には数えない
        Tally swapped = Count("ば", "ぱ");
        Assert.Equal((1, 0, 0, 1), (swapped.Dak, swapped.DakLost, swapped.DakAdded, swapped.DakSwap));
        Tally katakana = Count("ビ", "ヒ");
        Assert.Equal(1, katakana.DakLost);
    }

    [Fact]
    public void ZuCharactersAreCountedSeparatelyFromTheOtherVoicedCharacters()
    {
        Tally t = Count("ぢづヂヅがぎ", "ちつチツかき");
        Assert.Equal(6, t.Voiced);
        Assert.Equal(4, t.Zu);
        Assert.Equal(6, t.Dak);
        Assert.Equal(4, t.DakZu);
        Assert.Equal(2, t.DakWithoutZu);
        Assert.Equal(2, t.VoicedWithoutZu);
    }

    [Fact]
    public void OtherSubstitutionsAndDeletionsAreSplitByWhetherTheReferenceCharacterIsVoiced()
    {
        Tally t = Count("がかぎき", "たたぎ");   // が→た(別の字へ。濁字)、か→た(清音)、ぎ一致、き削除(清音)
        Assert.Equal(0, t.Dak);
        Assert.Equal(1, t.OtherSubVoiced);
        Assert.Equal(1, t.OtherSubPlain);
        Assert.Equal(1, t.DeletedPlain);
        Assert.Equal(0, t.DeletedVoiced);
        Assert.Equal(3, t.Errors);
        Tally voicedDeleted = Count("がか", "か");
        Assert.Equal(1, voicedDeleted.DeletedVoiced);
    }

    [Fact]
    public void InsertionsAreCountedAsExtraCharactersWithAPunctuationSubCount()
    {
        Tally t = Count("かき", "かさ。き");
        Assert.Equal(2, t.Inserted);
        Assert.Equal(1, t.InsertedPunctuation);
        Assert.Equal(2, t.Errors);
    }

    [Fact]
    public void CharactersAreSplitIntoHiraganaKatakanaAndOther()
    {
        (Tally[] by, string alignment) = Scoring.Count(Scoring.Normalize("がガa"), Scoring.Normalize("かガa"));
        Assert.Equal(1, by[Scoring.Hiragana].Ref);
        Assert.Equal(1, by[Scoring.Katakana].Ref);
        Assert.Equal(1, by[Scoring.OtherScript].Ref);
        Assert.Equal(1, by[Scoring.Hiragana].Dak);
        Assert.Equal(0, by[Scoring.Katakana].Dak);
        Assert.Equal("が→か[濁]", alignment);
    }

    [Fact]
    public void WilsonInterval_MatchesKnownValues()
    {
        Assert.Equal("[0.0-3.7]", Scoring.WilsonInterval(0, 100));
        Assert.Equal("", Scoring.WilsonInterval(0, 0));
        Assert.Equal("[40.4-59.6]", Scoring.WilsonInterval(50, 100));
    }
}

/// <summary>採点の入出力と表。</summary>
public class ScoreReportTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "kana-yardstick-test-" + Guid.NewGuid().ToString("N"));

    public ScoreReportTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); }
        catch (IOException) { }
    }

    private static Spec MakeSpec(string id, int height, params string[] lines) => new(id, 1280, 720, "dark", height, "Yu Gothic", 1, lines);

    private static string ResultLine(string cond, string id, string status, string? ocr, int attempts = 1, int promptTokens = 1416, double wallMs = 15000)
    {
        var o = new Dictionary<string, object?> { ["cond"] = cond, ["id"] = id, ["status"] = status, ["attempt_count"] = attempts, ["prompt_tokens"] = promptTokens, ["wall_ms"] = wallMs, ["finish_reason"] = "stop" };
        if (ocr is not null) o["ocr"] = ocr;
        return JsonSerializer.Serialize(o);
    }

    [Fact]
    public void Run_WritesReportAndCountsOnlyMainOutputsInTheTables()
    {
        // 正解 10 字(濁字 10)。c1 は 2 字で濁点が落ち、c2 は全部合っている。再試行で成功した 1 枚は表に混ぜない
        Spec a = MakeSpec("a", 12, "がぎぐげござじずぜぞ"), b = MakeSpec("b", 12, "ばびぶべぼぱぴぷぺぽ");
        File.WriteAllText(Path.Combine(_work, "manifest.json"), JsonSerializer.Serialize(new[] { a, b }));
        File.WriteAllLines(Path.Combine(_work, "results.jsonl"),
        [
            ResultLine("c1", "a", "ok", "かぎぐげこざじずぜぞ"),
            ResultLine("c1", "b", "ok", "ばびぶべぼぱぴぷぺぽ", attempts: 2),
            ResultLine("c2", "a", "ok", "がぎぐげござじずぜぞ", promptTokens: 4136, wallMs: 90000),
            ResultLine("c2", "b", "failed", null, attempts: 3),
        ]);

        int code = ScoreReport.Run(new ScoreOptions(_work, 536, "c1", "c2"));

        Assert.Equal(0, code);
        string report = File.ReadAllText(Path.Combine(_work, "report.txt"));
        Assert.Contains("c1 | 全体 | 1 | 10 | 10 | 20.00 | 20.00 [", report, StringComparison.Ordinal);   // 主は a だけ。濁点が落ちた 2 字 = 20%(CER も 20%)
        Assert.Contains("c2 | 全体 | 1 | 10 | 10 | 0.00 | 0.00 [", report, StringComparison.Ordinal);
        Assert.Contains("c1 | b | 再試行で成功 | 2", report, StringComparison.Ordinal);
        Assert.Contains("c2 | b | 失敗 | 3", report, StringComparison.Ordinal);
        Assert.Contains("12 | 10/10 | 20.00 [", report, StringComparison.Ordinal);
        Assert.Contains("| 5%以上 |", report, StringComparison.Ordinal);
        Assert.Contains("| はい | はい | 合 |", report, StringComparison.Ordinal);   // c1 20% ≥ 5% かつ c2 0% ≤ c1 の 1/3
        Assert.Contains("6.0 倍", report, StringComparison.Ordinal);                // 時間 15 秒 → 90 秒
        Assert.True(File.Exists(Path.Combine(_work, "per-image.tsv")));
        Assert.True(File.Exists(Path.Combine(_work, "alignments.txt")));
        Assert.Contains("が→か[濁]", File.ReadAllText(Path.Combine(_work, "alignments.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_RejectsAResultThatIsNotInTheManifest()
    {
        File.WriteAllText(Path.Combine(_work, "manifest.json"), JsonSerializer.Serialize(new[] { MakeSpec("a", 12, "がぎ") }));
        File.WriteAllLines(Path.Combine(_work, "results.jsonl"), [ResultLine("c1", "zzz", "ok", "がぎ")]);
        Assert.Throws<InvalidDataException>(() => ScoreReport.Run(new ScoreOptions(_work, 536, "c1", "c2")));
    }

    [Fact]
    public void ReportSplitsZuCharactersFromTheOtherVoicedCharacters()
    {
        // 正解 ぢづがぎ。c1 は ぢづ だけ間違える
        File.WriteAllText(Path.Combine(_work, "manifest.json"), JsonSerializer.Serialize(new[] { MakeSpec("a", 12, "ぢづがぎ") }));
        File.WriteAllLines(Path.Combine(_work, "results.jsonl"), [ResultLine("c1", "a", "ok", "ちつがぎ"), ResultLine("c2", "a", "ok", "ぢづがぎ")]);
        ScoreReport.Run(new ScoreOptions(_work, 536, "c1", "c2"));
        string report = File.ReadAllText(Path.Combine(_work, "report.txt"));
        Assert.Contains("c1 | 全体 | 1 | 4 | 4 | 50.00 | 50.00 [", report, StringComparison.Ordinal);
        Assert.Contains("| 0.00 | 100.00 (2) |", report, StringComparison.Ordinal);   // ぢづ除く 0%、ぢづ 100%(2 字)
    }
}

/// <summary>描画。WPF なので STA のスレッドで呼ぶ。</summary>
public class ImageRendererTests
{
    private static T OnSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = f(); }
            catch (Exception e) { error = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new InvalidOperationException("STA のスレッドで失敗", error);
        return result;
    }

    private static byte[] Pixels(BitmapSource b)
    {
        int stride = (b.PixelWidth * b.Format.BitsPerPixel + 7) / 8;
        var data = new byte[stride * b.PixelHeight];
        b.CopyPixels(data, stride, 0);
        return data;
    }

    [Fact]
    public void Render_DrawsTheTextOnTheBackgroundAndIsDeterministic()
    {
        (int w, int h, byte[] first, byte[] second) = OnSta(() =>
        {
            ImageRenderer.CheckFonts();
            Spec spec = ImageSpecs.Build(ImageSpecs.DefaultSeed).First(s => s.Bg == "dark" && s.Height == 24);
            BitmapSource a = ImageRenderer.Render(spec), b = ImageRenderer.Render(spec);
            return (a.PixelWidth, a.PixelHeight, Pixels(a), Pixels(b));
        });
        Assert.Equal((1280, 720), (w, h));
        Assert.Equal(first, second);
        // 左上は暗い単色の背景(B, G, R = 0x26, 0x1D, 0x1B)で、字の画素(明るい)もある
        Assert.Equal(new byte[] { 0x26, 0x1D, 0x1B }, first[..3]);
        Assert.Contains(Enumerable.Range(0, first.Length / 3).Select(i => first[i * 3 + 1]), g => g > 0xC0);
    }

    [Fact]
    public void Render_BusyBackgroundHasManyColors()
    {
        int distinct = OnSta(() =>
        {
            Spec spec = ImageSpecs.Build(ImageSpecs.DefaultSeed).First(s => s.Bg == "busy");
            byte[] px = Pixels(ImageRenderer.Render(spec));
            return Enumerable.Range(0, px.Length / 3).Select(i => (px[i * 3], px[i * 3 + 1], px[i * 3 + 2])).Distinct().Count();
        });
        Assert.True(distinct > 1000, distinct.ToString());
    }

    [Fact]
    public void SavePng_WritesAPngThatReadsBack()
    {
        string path = Path.Combine(Path.GetTempPath(), "kana-yardstick-test-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            (int w, int h) = OnSta(() =>
            {
                Spec spec = ImageSpecs.Build(ImageSpecs.DefaultSeed).First(s => s.Bg == "light");
                ImageRenderer.SavePng(ImageRenderer.Render(spec), path);
                var decoder = new PngBitmapDecoder(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
            });
            Assert.Equal((1280, 720), (w, h));
        }
        finally { File.Delete(path); }
    }
}
