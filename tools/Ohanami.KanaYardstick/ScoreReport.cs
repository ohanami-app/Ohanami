using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ohanami.KanaYardstick;

/// <summary>採点の入力。</summary>
/// <param name="Work">作業フォルダ(manifest.json と results.jsonl がある。report.txt などをここに書く)。</param>
/// <param name="TextTokens">プロンプトの文章のトークン数。画像トークン = prompt_tokens − この値。モデルのトークナイザごとに違う(Qwen3-VL-8B + 製品のプロンプトは 536。ADR 0025)。</param>
/// <param name="BaseCondition">進む線の表で、基準にする条件の名前。</param>
/// <param name="VariantCondition">進む線の表で、比べる条件の名前。</param>
internal sealed record ScoreOptions(string Work, int TextTokens, string BaseCondition, string VariantCondition);

/// <summary>1 枚の結果(1 つの条件で流した 1 枚)。</summary>
internal sealed class ScoredImage
{
    public Spec Spec = null!;
    public string Condition = "";
    public string Status = "";
    public string Reference = "", Hypothesis = "";
    public Tally[] ByScript = [new(), new(), new()];
    public double Seconds;
    public int? PromptTokens;
    public int? ImageTokens;
    public double EffectiveScale = double.NaN;
    public int Attempts;
    public string FinishReason = "";
    public bool Loop, Main;
    public string Alignment = "";

    public Tally All
    {
        get { var t = new Tally(); foreach (Tally x in ByScript) t.Add(x); return t; }
    }
}

/// <summary>
/// results.jsonl を採点して、report.txt(表)・per-image.tsv・alignments.txt を書く。
/// 集計に使うのは「主」= 1 回目で finish_reason = stop になり、繰り返しの疑い(余計な字が正解の字数の半分超)でない出力だけ。
/// 再試行の出力・失敗・繰り返しの疑いは、混ぜずに最後の節で別に数える。
/// </summary>
internal static class ScoreReport
{
    /// <summary>画像トークンがこの値以上なら「上限に当たった」と数える(上限 3584 の画像が実際に取る値は、4K で 3476 付近)。</summary>
    private const int CapNearThreshold = 3400;

    /// <summary>進む線(ADR 0019 の測定で、結果を見る前に決めたもの): 基準の条件の濁点・半濁点だけの誤りが 5% 以上、かつ比べる条件がその 1/3 以下。別の測定では、測る前に決め直す。</summary>
    private const double GateBaseMinimum = 0.05;
    private const double GateRatio = 1.0 / 3.0;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(ScoreOptions o)
    {
        string work = Path.GetFullPath(o.Work);
        List<Spec> specs = JsonSerializer.Deserialize<List<Spec>>(File.ReadAllText(Path.Combine(work, "manifest.json"))) ?? throw new InvalidDataException("manifest.json が空");
        Dictionary<string, Spec> byId = specs.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var images = new List<ScoredImage>();
        foreach (string line in File.ReadLines(Path.Combine(work, "results.jsonl")))
        {
            if (line.Length == 0) continue;
            JsonNode n = JsonNode.Parse(line) ?? throw new InvalidDataException("results.jsonl の行が JSON でない");
            string id = (string?)n["id"] ?? throw new InvalidDataException("id が無い");
            Spec s = byId.TryGetValue(id, out Spec? found) ? found : throw new InvalidDataException($"manifest.json に無い id: {id}");
            var im = new ScoredImage { Spec = s, Condition = (string?)n["cond"] ?? throw new InvalidDataException("cond が無い"), Status = (string?)n["status"] ?? throw new InvalidDataException("status が無い") };
            im.Reference = Scoring.Normalize(string.Concat(s.Lines));
            im.Hypothesis = im.Status == "ok" ? Scoring.Normalize((string?)n["ocr"] ?? throw new InvalidDataException("ok の行に ocr が無い")) : "";
            im.Seconds = (double)n["wall_ms"]! / 1000.0;
            im.PromptTokens = n["prompt_tokens"]?.GetValue<int>();
            im.Attempts = (int)n["attempt_count"]!;
            im.FinishReason = n["finish_reason"]?.ToString() ?? "";
            im.ImageTokens = im.PromptTokens - o.TextTokens;
            if (im.ImageTokens is { } t) im.EffectiveScale = Math.Sqrt(t * 1024.0 / (s.W * (double)s.H));
            (im.ByScript, im.Alignment) = Scoring.Count(im.Reference, im.Hypothesis);
            Tally all = im.All;
            im.Loop = im.Status == "ok" && all.Inserted > 0.5 * all.Ref;
            im.Main = im.Status == "ok" && im.Attempts == 1 && !im.Loop;
            images.Add(im);
        }

        string report = Render(images, o);
        File.WriteAllText(Path.Combine(work, "report.txt"), report, new UTF8Encoding(false));

        var det = new StringBuilder();
        det.AppendLine("cond\tid\tmain\tstatus\tattempts\tref_len\tvoiced\tzu\tcer\tdak_err\tdak_err_zu\tother_sub_voiced\tother_sub_plain\tdel_voiced\tdel_plain\tins\tsec\tprompt_tokens\timg_tokens\teff_scale\tfinish\tref\thyp");
        foreach (ScoredImage i in images.OrderBy(i => i.Condition, StringComparer.Ordinal).ThenBy(i => i.Spec.Id, StringComparer.Ordinal))
        {
            Tally a = i.All;
            det.AppendLine(string.Join("\t", i.Condition, i.Spec.Id, i.Main ? 1 : 0, i.Status, i.Attempts, a.Ref, a.Voiced, a.Zu, F(Cer(i)), a.Dak, a.DakZu, a.OtherSubVoiced, a.OtherSubPlain,
                a.DeletedVoiced, a.DeletedPlain, a.Inserted, F(i.Seconds), i.PromptTokens, i.ImageTokens, F(i.EffectiveScale), i.FinishReason, i.Reference, i.Hypothesis));
        }
        File.WriteAllText(Path.Combine(work, "per-image.tsv"), det.ToString(), new UTF8Encoding(false));

        var errs = new StringBuilder();
        foreach (ScoredImage i in images.OrderBy(i => i.Condition, StringComparer.Ordinal).ThenBy(i => i.Spec.Id, StringComparer.Ordinal))
        {
            errs.AppendLine($"## {i.Condition} {i.Spec.Id}");
            errs.AppendLine("ref: " + i.Reference);
            errs.AppendLine("hyp: " + i.Hypothesis);
            errs.AppendLine("align: " + i.Alignment);
        }
        File.WriteAllText(Path.Combine(work, "alignments.txt"), errs.ToString(), new UTF8Encoding(false));
        Console.Write(report);
        return 0;
    }

    internal static string Render(IReadOnlyList<ScoredImage> images, ScoreOptions o)
    {
        var sb = new StringBuilder();
        void W(string x = "") { sb.AppendLine(x); }

        string[] conds = images.Select(i => i.Condition).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToArray();
        W("条件: " + string.Join(" / ", conds));
        W("集計に使うのは「主」= 1 回目で finish_reason=stop になり、繰り返しの疑い(余計な字が正解の字数の半分超)でない出力だけ。");
        W("再試行(attempt 2・3 = dry_multiplier / repeat_penalty の付いた再実行)の出力・失敗・繰り返しの疑いは、混ぜずに最後の節で別に数える。");
        W($"プロンプトの文章のトークン数 = {o.TextTokens}(画像トークン = prompt_tokens − この値)。");
        W();
        W("== 件数の内訳 ==");
        W("条件 | 全 | 主 | 再試行で成功 | 失敗 | 繰り返しの疑い");
        foreach (string c in conds)
        {
            var l = images.Where(i => i.Condition == c).ToList();
            W($"{c} | {l.Count} | {l.Count(i => i.Main)} | {l.Count(i => i.Status == "ok" && i.Attempts > 1)} | {l.Count(i => i.Status != "ok")} | {l.Count(i => i.Loop)}");
        }
        W();
        W("表の列: n=画像数 / 字=正解の字数 / 濁字=正解のうち濁点・半濁点の付く字 / CER=(置換+挿入+削除)÷字 / 濁誤=濁点・半濁点だけの誤り÷濁字(%。[]は Wilson 95% 区間。括弧は 落/付加/取違 の数)");
        W("濁誤(ぢづ除く)=ぢ・づ・ヂ・ヅを除いた濁字の濁点・半濁点だけの誤り(%) / ぢづ誤=ぢ・づ・ヂ・ヅの濁点・半濁点だけの誤り÷ぢ・づ・ヂ・ヅの字数(%。括弧は字数。判定ではこの 2 つを別々に出す)");
        W("濁他=濁字の濁点・半濁点以外の誤り(別の字への置換+読み落とし)÷濁字(%) / 清誤=付かない字(清音)の誤り(置換+読み落とし)÷清音字(%) / 余計=挿入÷字(%。括弧は記号の数)");
        W("秒=1 枚の平均 / 画像tok=平均(最小-最大) / 上限付近=画像トークン ≥ 3400 の枚数 / 実倍率=元の大きさに対する線形の倍率(画像トークンから逆算)");
        W();

        List<ScoredImage> main = images.Where(i => i.Main).ToList();
        string H(ScoredImage i) => i.Spec.Height.ToString("00", Inv);
        Table(sb, "全体", main, i => "全体", null);
        Table(sb, "字の高さ(px)ごと", main, i => H(i), null);
        Table(sb, "字の高さ × 背景", main, i => H(i) + " " + i.Spec.Bg, null);
        Table(sb, "背景ごと", main, i => i.Spec.Bg, null);
        Table(sb, "画面の大きさごと(実倍率 = sqrt((prompt_tokens − 文章のトークン数) ÷ (横÷32 × 縦÷32)) の平均)", main, i => $"{i.Spec.W}x{i.Spec.H}", null);
        W("-- 拡大したあとの字の高さ(= 元の字の高さ × その画像の実倍率)で並べる。条件の点を同じ表に置く。区切り: ≤14 / 14-18 / 18-22 / 22-28 / 28-36 / 36-48 / 48-64 / >64 px。行の頭の英字は並べ替え用。 --");
        Table(sb, "拡大後の字の高さごと(条件を並べる)", main, i => EffectiveBin(i), null);
        Table(sb, "拡大後の字の高さごと × ひらがな", main, i => EffectiveBin(i), Scoring.Hiragana);
        Table(sb, "拡大後の字の高さごと × カタカナ", main, i => EffectiveBin(i), Scoring.Katakana);
        Table(sb, "合算: 元の字の高さ 12-16 / 20-24 / 32 px(誤り率の低い升を合わせて見る)", main, i => i.Spec.Height <= 16 ? "12-16" : i.Spec.Height <= 24 ? "20-24" : "32", null);
        Table(sb, "合算: 拡大後の字の高さ ≤22 / 22-36 / >36 px", main, i => { double e = EffectiveHeight(i); return e <= 22 ? "a ≤22" : e <= 36 ? "b 22-36" : "c >36"; }, null);
        Table(sb, "フォントごと", main, i => i.Spec.Font, null);
        Table(sb, "字の高さ × ひらがな", main, i => H(i), Scoring.Hiragana);
        Table(sb, "字の高さ × カタカナ", main, i => H(i), Scoring.Katakana);
        Table(sb, "ひらがな(全体)", main, i => "全体", Scoring.Hiragana);
        Table(sb, "カタカナ(全体)", main, i => "全体", Scoring.Katakana);

        W($"== 進む線(ADR 0019 の測定で結果を見る前に決めたもの。別の測定では測る前に決め直す): 濁点・半濁点だけの誤りが {o.BaseCondition} で {GateBaseMinimum * 100:0}% 以上 かつ {o.VariantCondition} が {o.BaseCondition} の 1/3 以下 ==");
        W($"字の高さ(主の画像、背景と画面の大きさを合わせた全体) | 濁字({o.BaseCondition}/{o.VariantCondition}) | {o.BaseCondition} 濁誤% [区間] | {o.VariantCondition} 濁誤% [区間] | 比 | 5%以上 | 1/3以下 | 合否 | ぢづ除く {o.BaseCondition} → {o.VariantCondition} (%) | ぢづ {o.BaseCondition} → {o.VariantCondition} (%) | 時間 {o.VariantCondition}/{o.BaseCondition}(1 枚の平均秒の比)");
        foreach (int hh in main.Select(i => i.Spec.Height).Distinct().OrderBy(x => x))
        {
            var baseImages = main.Where(i => i.Condition == o.BaseCondition && i.Spec.Height == hh).ToList();
            var variantImages = main.Where(i => i.Condition == o.VariantCondition && i.Spec.Height == hh).ToList();
            Tally a = Sum(baseImages), b = Sum(variantImages);
            if (a.Voiced == 0 || b.Voiced == 0) { W($"{hh} | {o.BaseCondition} か {o.VariantCondition} の画像が無い"); continue; }
            double ra = a.Dak / (double)a.Voiced, rb = b.Dak / (double)b.Voiced;
            double ta = baseImages.Average(i => i.Seconds), tb = variantImages.Average(i => i.Seconds);
            bool c5 = ra >= GateBaseMinimum, c3 = rb <= ra * GateRatio;
            W($"{hh} | {a.Voiced}/{b.Voiced} | {Pct(a.Dak, a.Voiced)} {Scoring.WilsonInterval(a.Dak, a.Voiced)} | {Pct(b.Dak, b.Voiced)} {Scoring.WilsonInterval(b.Dak, b.Voiced)} | {(ra == 0 ? "-" : (rb / ra).ToString("0.00", Inv))} | {(c5 ? "はい" : "いいえ")} | {(c3 ? "はい" : "いいえ")} | {(c5 && c3 ? "合" : "否")}"
                + $" | {Pct(a.DakWithoutZu, a.VoicedWithoutZu)} → {Pct(b.DakWithoutZu, b.VoicedWithoutZu)} | {Pct(a.DakZu, a.Zu)} → {Pct(b.DakZu, b.Zu)} | {(tb / ta).ToString("0.0", Inv)} 倍({ta.ToString("0.0", Inv)}s → {tb.ToString("0.0", Inv)}s)");
        }
        W();
        W("(参考)同じ線を 字の高さ × 背景 で見たとき");
        W($"字の高さ 背景 | 濁字({o.BaseCondition}/{o.VariantCondition}) | {o.BaseCondition} 濁誤% | {o.VariantCondition} 濁誤% | 合否");
        foreach (var g in main.GroupBy(i => (i.Spec.Height, i.Spec.Bg)).OrderBy(g => g.Key.Height).ThenBy(g => g.Key.Bg, StringComparer.Ordinal))
        {
            Tally a = Sum(g.Where(i => i.Condition == o.BaseCondition)), b = Sum(g.Where(i => i.Condition == o.VariantCondition));
            if (a.Voiced == 0 || b.Voiced == 0) continue;
            double ra = a.Dak / (double)a.Voiced, rb = b.Dak / (double)b.Voiced;
            W($"{g.Key.Height.ToString("00", Inv)} {g.Key.Bg} | {a.Voiced}/{b.Voiced} | {Pct(a.Dak, a.Voiced)} | {Pct(b.Dak, b.Voiced)} | {(ra >= GateBaseMinimum && rb <= ra * GateRatio ? "合" : "否")}");
        }
        W();

        W("== 同じ画像での 1 枚の時間と画像トークン(基準と比べる条件の両方が主の画像) ==");
        var pairs = main.Where(i => i.Condition == o.BaseCondition || i.Condition == o.VariantCondition)
            .GroupBy(i => i.Spec.Id).Where(g => g.Count() == 2)
            .Select(g => (a: g.First(i => i.Condition == o.BaseCondition), b: g.First(i => i.Condition == o.VariantCondition))).ToList();
        if (pairs.Count > 0)
            W($"対 {pairs.Count} 組。時間 {o.VariantCondition}/{o.BaseCondition} の中央値 {Median(pairs.Select(p => p.b.Seconds / p.a.Seconds)).ToString("0.0", Inv)} 倍(平均秒 {pairs.Average(p => p.a.Seconds).ToString("0.0", Inv)} → {pairs.Average(p => p.b.Seconds).ToString("0.0", Inv)})。"
              + $"画像トークン {o.BaseCondition} 平均 {pairs.Average(p => p.a.ImageTokens ?? 0).ToString("0", Inv)} → {o.VariantCondition} 平均 {pairs.Average(p => p.b.ImageTokens ?? 0).ToString("0", Inv)}。"
              + $"CER の差({o.VariantCondition} − {o.BaseCondition})の平均 {(pairs.Average(p => Cer(p.b) - Cer(p.a)) * 100).ToString("0.00", Inv)}pt。{o.VariantCondition} の方が良い {pairs.Count(p => Cer(p.b) < Cer(p.a))} 組 / 同じ {pairs.Count(p => Cer(p.b) == Cer(p.a))} 組 / 悪い {pairs.Count(p => Cer(p.b) > Cer(p.a))} 組");
        W();

        W("== 主でない出力(混ぜずに別に数える) ==");
        W("条件 | id | 状態 | 試行数 | 最後の finish | CER% | 濁誤 | 余計 | 秒");
        foreach (ScoredImage i in images.Where(i => !i.Main).OrderBy(i => i.Condition, StringComparer.Ordinal).ThenBy(i => i.Spec.Id, StringComparer.Ordinal))
        {
            Tally a = i.All;
            string kind = i.Status != "ok" ? "失敗" : i.Loop ? "繰り返しの疑い" : "再試行で成功";
            W($"{i.Condition} | {i.Spec.Id} | {kind} | {i.Attempts} | {i.FinishReason} | {(Cer(i) * 100).ToString("0.0", Inv)} | {a.Dak} | {a.Inserted} | {i.Seconds.ToString("0.0", Inv)}");
        }
        W();
        return sb.ToString();
    }

    private static double EffectiveHeight(ScoredImage i) => i.Spec.Height * (double.IsNaN(i.EffectiveScale) ? 1.0 : i.EffectiveScale);

    private static string EffectiveBin(ScoredImage i)
    {
        double e = EffectiveHeight(i);
        return e <= 14 ? "a ≤14" : e <= 18 ? "b 14-18" : e <= 22 ? "c 18-22" : e <= 28 ? "d 22-28" : e <= 36 ? "e 28-36" : e <= 48 ? "f 36-48" : e <= 64 ? "g 48-64" : "h >64";
    }

    private static string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.0000", Inv);

    private static string Pct(int n, int d) => d == 0 ? "-" : (n * 100.0 / d).ToString("0.00", Inv);

    private static double Cer(ScoredImage i) { Tally a = i.All; return a.Ref == 0 ? 0 : a.Errors / (double)a.Ref; }

    private static double Median(IEnumerable<double> xs)
    {
        double[] a = xs.OrderBy(x => x).ToArray();
        return a.Length == 0 ? double.NaN : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    private static Tally Sum(IEnumerable<ScoredImage> l)
    {
        var t = new Tally();
        foreach (ScoredImage i in l) t.Add(i.All);
        return t;
    }

    /// <param name="script">null = 全部(挿入はそれ以外の文字の分も含む)、<see cref="Scoring.Hiragana"/> / <see cref="Scoring.Katakana"/> = その文字種の正解の字と、その文字種の挿入。</param>
    private static void Table(StringBuilder sb, string title, List<ScoredImage> all, Func<ScoredImage, string> key, int? script)
    {
        sb.AppendLine("== " + title + " ==");
        sb.AppendLine("条件 | 区分 | n | 字 | 濁字 | CER% | 濁誤% [95%区間] (落/付加/取違) | 濁誤%(ぢづ除く) | ぢづ誤%(字数) | 濁他% | 清誤% | 余計% (記号) | 秒 | 画像tok 平均(最小-最大) | 上限付近 | 実倍率");
        foreach (var g in all.GroupBy(i => (Cond: i.Condition, Key: key(i))).OrderBy(g => g.Key.Key, StringComparer.Ordinal).ThenBy(g => g.Key.Cond, StringComparer.Ordinal))
        {
            var l = g.ToList();
            var t = new Tally();
            foreach (ScoredImage i in l)
            {
                if (script is { } s) t.Add(i.ByScript[s]); else t.Add(i.All);
            }
            var toks = l.Where(i => i.ImageTokens.HasValue).Select(i => i.ImageTokens!.Value).ToList();
            var eff = l.Where(i => !double.IsNaN(i.EffectiveScale)).Select(i => i.EffectiveScale).ToList();
            sb.AppendLine(string.Join(" | ",
                g.Key.Cond, g.Key.Key, l.Count, t.Ref, t.Voiced, Pct(t.Errors, t.Ref),
                $"{Pct(t.Dak, t.Voiced)} {Scoring.WilsonInterval(t.Dak, t.Voiced)} ({t.DakLost}/{t.DakAdded}/{t.DakSwap})",
                Pct(t.DakWithoutZu, t.VoicedWithoutZu),
                $"{Pct(t.DakZu, t.Zu)} ({t.Zu})",
                Pct(t.OtherSubVoiced + t.DeletedVoiced, t.Voiced),
                Pct(t.OtherSubPlain + t.DeletedPlain, t.Plain),
                $"{Pct(t.Inserted, t.Ref)} ({t.InsertedPunctuation})",
                l.Average(i => i.Seconds).ToString("0.0", Inv),
                toks.Count == 0 ? "-" : $"{toks.Average().ToString("0", Inv)}({toks.Min()}-{toks.Max()})",
                $"{toks.Count(x => x >= CapNearThreshold)}/{l.Count}",
                eff.Count == 0 ? "-" : eff.Average().ToString("0.00", Inv)));
        }
        sb.AppendLine();
    }
}
