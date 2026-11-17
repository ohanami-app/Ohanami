using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Ohanami.Core.Data;
using Ohanami.Core.Vlm;

namespace Ohanami.Bench;

/// <summary>vlm-check の比較に使う 1 枚(VLM の評価(spikes/S1-vlm)の結果と、選ばれた理由 = "sample"(samples.tsv の 33 枚)か "random"(無作為))。</summary>
internal sealed record VlmCheckImage(VlmEvalImage VlmEval, string Origin);

/// <summary>1 枚の比較の結果。<see cref="Kind"/> は下の定数のどれか。</summary>
internal sealed record VlmCompare(string Kind, bool OcrSame, bool DescriptionSame, bool TagsSame, string Detail)
{
    /// <summary>評価と製品の ocr・description・tags がすべて一致した。</summary>
    public const string Match = "一致";

    /// <summary>どちらも成功したが、ocr・description・tags のどれかが違う。</summary>
    public const string Differs = "差あり";

    /// <summary>評価は成功したが、製品は失敗した(length・HTTP 400・パース失敗など)。</summary>
    public const string ProductFailed = "製品だけ失敗";

    /// <summary>評価は失敗したが、製品は成功した。</summary>
    public const string VlmEvalFailed = "評価だけ失敗";

    /// <summary>どちらも失敗した(失敗の理由が同じ種類かは Detail)。</summary>
    public const string BothFailed = "両方失敗";

    public bool IsMatch => Kind is Match or BothFailed;
}

/// <summary>vlm-check の純粋な部分(DB も GPU も llama-server も使わない): 画像の選び方と、評価の結果と製品の結果の比較。</summary>
/// <summary>big(4K の 1 枚)の判定。</summary>
internal enum BigVerdict { Ok, ServerFailure, OverLimit, Unmeasured }

internal static class VlmCheckLogic
{
    /// <summary>
    /// big の判定。VRAM は llama-server のプロセスごとの値(性能カウンタ Dedicated Usage)で見る。取れなかった(<paramref name="processVramMiB"/> が null)ときは、GPU 全体の値に戻さず <see cref="BigVerdict.Unmeasured"/>。
    /// サーバの異常が先(その場合は VRAM の値も当てにならない)。
    /// </summary>
    public static BigVerdict JudgeBig(bool serverFailure, int? processVramMiB, int limitMiB) =>
        serverFailure ? BigVerdict.ServerFailure
        : processVramMiB is not { } v ? BigVerdict.Unmeasured
        : v <= limitMiB ? BigVerdict.Ok
        : BigVerdict.OverLimit;

    /// <summary>big の終了コード: 0 = 内側、6 = 足切りの外・サーバの異常、7 = VRAM を測れなかった。</summary>
    public static int BigExitCode(BigVerdict verdict) => verdict switch
    {
        BigVerdict.Ok => 0,
        BigVerdict.Unmeasured => 7,
        _ => 6,
    };

    /// <summary>製品の max_tokens(<see cref="VlmClient.MaxTokens"/>)。VLM の評価の run は 4096 だった。</summary>
    public static int ProductMaxTokens => VlmClient.MaxTokens;

    /// <summary>samples.tsv の 1 列目(filename)を読む。1 行目は列名。空行は飛ばす。重複・空のファイル名は例外。NFC にそろえる。</summary>
    public static List<string> ReadSampleNames(string path)
    {
        string[] lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
        if (lines.Length == 0) throw new InvalidDataException($"{path} が空");
        string[] header = lines[0].TrimStart('﻿').TrimEnd('\r').Split('\t');
        if (header.Length == 0 || header[0] != "filename") throw new InvalidDataException($"{path} の 1 行目の先頭の列が filename でない: {lines[0]}");
        var names = new List<string>();
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            string name = BenchInputs.Nfc(line.Split('\t')[0].Trim());
            if (name.Length == 0) throw new InvalidDataException($"{path} の {i + 1} 行目: filename が空");
            names.Add(name);
        }
        var dup = names.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new InvalidDataException($"{path} に同じ画像「{dup.Key}」が 2 回ある");
        return names;
    }

    /// <summary>
    /// 比べる画像を選ぶ: samples の全部(評価の結果に無ければ例外)+ 評価で成功した画像のうち samples に無いものから、<paramref name="seed"/> で決まる無作為の
    /// <paramref name="randomCount"/> 枚(ファイル名の順に並べてから抜くので、同じ seed なら同じ画像)。順序は samples(samples.tsv の順)→ random(ファイル名の順)。
    /// </summary>
    public static List<VlmCheckImage> SelectImages(IReadOnlyList<VlmEvalImage> evalImages, IReadOnlyList<string> sampleNames, int randomCount, int seed)
    {
        ArgumentNullException.ThrowIfNull(evalImages);
        ArgumentNullException.ThrowIfNull(sampleNames);
        ArgumentOutOfRangeException.ThrowIfNegative(randomCount);
        var byName = evalImages.ToDictionary(i => i.FileName, StringComparer.Ordinal);
        var result = new List<VlmCheckImage>();
        foreach (string name in sampleNames)
        {
            if (!byName.TryGetValue(name, out VlmEvalImage? image)) throw new InvalidDataException($"samples の「{name}」が評価の結果に無い");
            result.Add(new VlmCheckImage(image, "sample"));
        }
        var samples = sampleNames.ToHashSet(StringComparer.Ordinal);
        List<VlmEvalImage> pool = evalImages.Where(i => i.ParsedOk && !samples.Contains(i.FileName)).OrderBy(i => i.FileName, StringComparer.Ordinal).ToList();
        if (randomCount > pool.Count) throw new InvalidDataException($"無作為に選べる画像が足りない(要 {randomCount}、評価で成功し samples に無い画像は {pool.Count})");
        var rng = new Random(seed);
        for (int i = 0; i < randomCount; i++)   // 先頭から randomCount 個だけ Fisher–Yates
        {
            int j = rng.Next(i, pool.Count);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        result.AddRange(pool.Take(randomCount).OrderBy(i => i.FileName, StringComparer.Ordinal).Select(i => new VlmCheckImage(i, "random")));
        return result;
    }

    /// <summary>評価の結果と製品の結果を比べる(ocr・description・tags は文字列として完全一致。加工しない)。</summary>
    public static VlmCompare Compare(VlmEvalImage evalImage, VlmResult product)
    {
        ArgumentNullException.ThrowIfNull(evalImage);
        ArgumentNullException.ThrowIfNull(product);
        if (product.Outcome == VlmOutcome.ServerFailure)
            throw new InvalidOperationException("llama-server の異常は、比較の対象にならない(呼び出し側で止める): " + product.Error);

        bool productOk = product.Outcome == VlmOutcome.Ok && product.Caption is not null;
        if (!evalImage.ParsedOk && !productOk) return new VlmCompare(VlmCompare.BothFailed, false, false, false, $"評価: {evalImage.Note} / 製品: {product.Error}");
        if (!evalImage.ParsedOk) return new VlmCompare(VlmCompare.VlmEvalFailed, false, false, false, $"評価: {evalImage.Note}");
        if (!productOk) return new VlmCompare(VlmCompare.ProductFailed, false, false, false, ProductFailureDetail(evalImage, product));

        return CompareCaptions(evalImage, product.Caption!);
    }

    /// <summary>評価が成功した画像の ocr・description・tags と、製品の結果(<paramref name="c"/>)を、文字列として完全一致で比べる(一致か、差ありと差の内容)。</summary>
    public static VlmCompare CompareCaptions(VlmEvalImage evalImage, VlmCaption c)
    {
        ArgumentNullException.ThrowIfNull(evalImage);
        ArgumentNullException.ThrowIfNull(c);
        if (!evalImage.ParsedOk) throw new ArgumentException("評価が成功していない画像は、比べる元が無い", nameof(evalImage));
        bool ocr = string.Equals(evalImage.Ocr, c.Ocr, StringComparison.Ordinal);
        bool desc = string.Equals(evalImage.Description, c.Description, StringComparison.Ordinal);
        bool tags = evalImage.Tags!.SequenceEqual(c.Tags, StringComparer.Ordinal);
        if (ocr && desc && tags) return new VlmCompare(VlmCompare.Match, true, true, true, "");
        var parts = new List<string>();
        if (!ocr) parts.Add("ocr: " + FirstDifference(evalImage.Ocr!, c.Ocr));
        if (!desc) parts.Add("description: " + FirstDifference(evalImage.Description!, c.Description));
        if (!tags) parts.Add("tags: " + TagsDifference(evalImage.Tags!, c.Tags));
        return new VlmCompare(VlmCompare.Differs, ocr, desc, tags, string.Join(" / ", parts));
    }

    private static string ProductFailureDetail(VlmEvalImage evalImage, VlmResult product)
    {
        string text = $"製品: {product.Error}";
        // 製品の max_tokens(2048)が評価(4096)より小さいために length になる場合は、構成の違いではなく既知の差として分けて書く
        if (string.Equals(product.FinishReason, "length", StringComparison.Ordinal) && evalImage.CompletionTokens is > VlmClient.MaxTokens)
            text += $"(評価の completion_tokens は {evalImage.CompletionTokens.Value.ToString(CultureInfo.InvariantCulture)} で、製品の max_tokens {VlmClient.MaxTokens.ToString(CultureInfo.InvariantCulture)} を超える: 既知の差)";
        return text;
    }

    /// <summary>2 つの文字列の、最初に違う位置(UTF-16 の添字)と、その前後の短い抜粋。</summary>
    public static string FirstDifference(string evalText, string product)
    {
        int n = Math.Min(evalText.Length, product.Length);
        int i = 0;
        while (i < n && evalText[i] == product[i]) i++;
        string Excerpt(string s) => s.Length <= i ? "(ここで終わり)" : Quote(s.Substring(Math.Max(0, i - 6), Math.Min(s.Length, i + 12) - Math.Max(0, i - 6)));
        return $"{i.ToString(CultureInfo.InvariantCulture)} 文字目から違う(評価 {evalText.Length.ToString(CultureInfo.InvariantCulture)} 字 {Excerpt(evalText)} / 製品 {product.Length.ToString(CultureInfo.InvariantCulture)} 字 {Excerpt(product)})";
    }

    private static string TagsDifference(IReadOnlyList<string> evalTags, IReadOnlyList<string> product)
    {
        string[] only1 = evalTags.Except(product, StringComparer.Ordinal).ToArray();
        string[] only2 = product.Except(evalTags, StringComparer.Ordinal).ToArray();
        if (only1.Length == 0 && only2.Length == 0) return $"同じ tag で順序が違う({evalTags.Count.ToString(CultureInfo.InvariantCulture)} 個)";
        return $"評価だけ [{string.Join("、", only1)}] / 製品だけ [{string.Join("、", only2)}]";
    }

    private static string Quote(string s) => "「" + s.Replace("\n", "\\n", StringComparison.Ordinal) + "」";

    /// <summary>ファイル名の拡張子から画像の形式を決める(.png / .jpg / .jpeg / .webp。ほかは例外)。</summary>
    public static ImageFormat FormatOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => ImageFormat.Png,
        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
        ".webp" => ImageFormat.Webp,
        var other => throw new InvalidDataException($"未対応の拡張子「{other}」: {fileName}"),
    };

    /// <summary>
    /// 小さい画像のプロンプトのトークン数の測定から、文章のトークン数(テンプレートの印を含む)を求める。画像 (w × h) が (w/32) × (h/32) トークン(Qwen3-VL: 1 トークン = 32 × 32 画素。
    /// w と h が 32 の倍数のとき)になるという仮定の上で、<c>文章 = prompt_tokens − (w/32)×(h/32)</c> が、大きさの違う複数の画像で同じになることを確かめる。
    /// 同じにならなければ <c>Consistent = false</c>(仮定が当たっていない=文章のトークン数は未確定)。
    /// </summary>
    public static TextTokenEstimate EstimateTextTokens(IReadOnlyList<(int Width, int Height, int PromptTokens)> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 2) throw new ArgumentException("大きさの違う画像が 2 つ以上要る", nameof(samples));
        var perSample = new List<int>();
        foreach ((int w, int h, int p) in samples)
        {
            if (w % 32 != 0 || h % 32 != 0 || w <= 0 || h <= 0) throw new ArgumentException($"幅と高さは 32 の倍数(今 {w}×{h})", nameof(samples));
            perSample.Add(p - (w / 32) * (h / 32));
        }
        return new TextTokenEstimate(perSample.Distinct().Count() == 1, perSample[0], perSample);
    }
}

/// <summary><see cref="VlmCheckLogic.EstimateTextTokens"/> の結果。<paramref name="TextTokens"/> は先頭の画像から求めた値、<paramref name="PerSample"/> は画像ごとの値(全部同じなら <paramref name="Consistent"/>)。</summary>
internal sealed record TextTokenEstimate(bool Consistent, int TextTokens, IReadOnlyList<int> PerSample);
