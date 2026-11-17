using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Captions;
using Ohanami.Core.Data;
using Ohanami.Core.Models;
using Ohanami.Core.Vlm;

namespace Ohanami.Bench;

/// <summary>DB の captions の 1 行(製品が作った結果)。</summary>
internal sealed record StoredCaption(string Ocr, string Description, IReadOnlyList<string> Tags, int Attempt, string SettingsJson);

/// <summary>DB の stage_status(caption)の failed の行(製品が最終的に失敗とした画像)。</summary>
internal sealed record StoredFailure(string Error, string SettingsJson);

/// <summary>VLM の評価(spikes/S1-vlm)の結果から決まる「期待」: 画像 1 枚の、期待する ocr・description・tags(<see cref="Source"/>)と、製品の何回目の試行で得られるはずか。</summary>
/// <param name="Source">期待の元(VLM の評価の全件の run、またはその 16 枚の再試行の run のどちらかの 1 枚)。</param>
/// <param name="Attempt">1 = ペナルティなし(VLM の評価の全件の run が成功)、2 = dry_multiplier 0.8(再試行の run が成功)、3 = repeat_penalty 1.1(その run が成功)。</param>
internal sealed record ExpectedCaption(VlmEvalImage Source, int Attempt);

/// <summary>1 枚の確認の結果。<paramref name="AttemptMismatch"/> は、結果は一致しても、得られた試行の回が期待と違う(製品の再試行の梯子が評価の再試行の実験と違う)こと。</summary>
internal sealed record CaptionsCheckRow(string FileName, string Hash, string Kind, int ExpectedAttempt, int? ProductAttempt, bool AttemptMismatch, string Detail)
{
    /// <summary>評価の結果に期待があり、製品に結果が無い(まだ処理されていない)。</summary>
    public const string NotProcessed = "未処理";
}

/// <summary>分布の要約(秒)。百分位は「並べて、先頭から p×件数 番目」(評価の集計と同じ方式)。</summary>
internal sealed record Distribution(int Count, double Mean, double P50, double P90, double P99, double Max, double Total)
{
    public static Distribution From(IEnumerable<double> values)
    {
        double[] v = values.Order().ToArray();
        if (v.Length == 0) return new Distribution(0, 0, 0, 0, 0, 0, 0);
        double At(double p) => v[Math.Min(v.Length - 1, (int)(p * v.Length))];
        return new Distribution(v.Length, v.Average(), At(0.5), At(0.9), At(0.99), v[^1], v.Sum());
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Count} 枚、平均 {Mean:F1}、p50 {P50:F1}、p90 {P90:F1}、p99 {P99:F1}、最大 {Max:F1}、合計 {Total:F0} 秒");
}

/// <summary>runs の 1 行。</summary>
internal sealed record RunRow(long Id, string JobKind, string StartedAt, string? EndedAt, string RuntimeJson);

/// <summary>評価の入力(DB と VLM の評価の run から集めたもの)。</summary>
/// <param name="Rows">画像ごとの比較(評価の全件の画像 1 枚ずつ)。</param>
/// <param name="ProductWallFirstAttempt">製品が 1 回目で成功した画像の、処理時間(秒)。</param>
/// <param name="VlmEvalWallSameImages">同じ画像の、評価の処理時間(秒)。</param>
/// <param name="Runs">runs の全行。</param>
/// <param name="ReprocessedStageRows">stage_status の ocr・caption の行のうち attempts &gt; 1 のもの(同じ画像を 2 回処理した印)。</param>
/// <param name="OcrDone">stage_status(ocr)の done の数。</param>
/// <param name="CaptionDone">stage_status(caption)の done の数。</param>
/// <param name="ExpectedVlmRuns">VLM の runs の行の下限(一時停止・再開を 1 回入れたなら 2)。</param>
/// <param name="TimesAvailable">false なら、時間の項目を見ない(取り込みの行の確認用: 取り込みの settings_json には wall_ms が無い。通しの確認では常に true)。</param>
internal sealed record CaptionsCheckInput(
    IReadOnlyList<CaptionsCheckRow> Rows, IReadOnlyList<double> ProductWallFirstAttempt, IReadOnlyList<double> VlmEvalWallSameImages,
    IReadOnlyList<RunRow> Runs, int ReprocessedStageRows, int OcrDone, int CaptionDone, int ExpectedVlmRuns, bool TimesAvailable = true);

/// <summary>
/// 通し(vlm-full)のあとの確認: 製品が作った captions を、VLM の評価の run の JSON と全画像で比べる。
/// <b>合格の見方は、結果を見る前に決めてここに書いてある</b>(<see cref="Evaluate"/> の各項目。数字は VLM の評価の run から決まる):
/// <list type="number">
/// <item>完走: 処理されていない画像が 0。</item>
/// <item>評価との一致: VLM の評価の run が成功した画像(と、16 枚は評価の再試行の run で救えた結果)は、製品の結果と完全一致。評価は max_tokens 4096、製品は 2048 だが、評価の成功の completion_tokens の最大は 1860 なので、
///   製品だけ失敗する画像は無い見込み。差のある画像は 3 枚まで(GPU の非決定性の余地)、製品だけ失敗は 0 枚。</item>
/// <item>試行の回: 結果が得られた試行(1 = そのまま、2 = dry_multiplier、3 = repeat_penalty)が、評価の実験から決まる期待と一致(不一致 0)。</item>
/// <item>length の失敗: 製品が最終的に失敗にした画像は、全部 length で、2 枚まで(評価の実験では 16 枚とも 3 回までの再試行で救えたので、見込みは 0)。</item>
/// <item>再試行で救えた数: 2 回目 = 期待の数 ±1、3 回目 = 期待の数 ±1(評価の実験では 14 枚と 2 枚)。</item>
/// <item>時間: 1 回目で成功した画像の処理時間の p50 が、同じ画像の評価の p50 の 0.75〜1.25 倍、p90 が評価の p90 の 1.25 倍以下。</item>
/// <item>runs: VLM の行が期待の数以上、全部 ended_at がある、runtime_json に llama-server のビルド(12110)がある。</item>
/// <item>再処理なし: stage_status の ocr・caption に attempts &gt; 1 の行が無い(一時停止・再開で同じ画像を 2 回処理していない)。</item>
/// <item>段階の一致: ocr の done と caption の done が同じ数。</item>
/// </list>
/// </summary>
internal static class CaptionsCheckLogic
{
    public const int MaxDiffers = 3;
    public const int MaxLengthFailures = 2;
    public const double TimeP50Low = 0.75, TimeP50High = 1.25, TimeP90High = 1.25;

    /// <summary>VLM の評価の全件の run の画像 1 枚と、その再試行の run の同じ画像(あれば)から、期待を決める。期待が無い(評価の実験でも救えなかった)なら null。</summary>
    public static ExpectedCaption? Expect(VlmEvalImage all, VlmEvalImage? retryDry, VlmEvalImage? retryRep)
    {
        ArgumentNullException.ThrowIfNull(all);
        if (all.ParsedOk) return new ExpectedCaption(all, 1);
        if (retryDry is { ParsedOk: true }) return new ExpectedCaption(retryDry, 2);
        if (retryRep is { ParsedOk: true }) return new ExpectedCaption(retryRep, 3);
        return null;
    }

    /// <summary>1 枚の分類。<paramref name="caption"/> と <paramref name="failure"/> の両方が無ければ未処理。</summary>
    public static CaptionsCheckRow Classify(string fileName, string hash, ExpectedCaption? expected, StoredCaption? caption, StoredFailure? failure)
    {
        if (caption is null && failure is null)
            return new CaptionsCheckRow(fileName, hash, CaptionsCheckRow.NotProcessed, expected?.Attempt ?? 0, null, false, "製品に結果も失敗の記録も無い");

        if (expected is null)
        {
            return caption is not null
                ? new CaptionsCheckRow(fileName, hash, VlmCompare.VlmEvalFailed, 0, caption.Attempt, false, $"評価の実験では救えなかった画像を、製品は {caption.Attempt} 回目で成功させた")
                : new CaptionsCheckRow(fileName, hash, VlmCompare.BothFailed, 0, null, false, "評価の実験でも救えず、製品も失敗: " + failure!.Error);
        }

        if (caption is null)
            return new CaptionsCheckRow(fileName, hash, VlmCompare.ProductFailed, expected.Attempt, null, false, "製品は失敗: " + failure!.Error);

        VlmCompare c = VlmCheckLogic.CompareCaptions(expected.Source, new VlmCaption(caption.Ocr, caption.Description, caption.Tags));
        bool mismatch = caption.Attempt != expected.Attempt;
        string detail = c.Detail;
        if (mismatch) detail = (detail.Length > 0 ? detail + " / " : "") + $"試行の回が違う(期待 {expected.Attempt}、実際 {caption.Attempt})";
        return new CaptionsCheckRow(fileName, hash, c.Kind, expected.Attempt, caption.Attempt, mismatch, detail);
    }

    /// <summary>settings_json の <c>attempts[].wall_ms</c> の合計(秒)。形が違えば例外(黙って 0 にしない)。</summary>
    public static double WallSeconds(string settingsJson)
    {
        JsonNode? root = JsonNode.Parse(settingsJson);
        if (root?["attempts"] is not JsonArray attempts || attempts.Count == 0) throw new InvalidDataException("settings_json に attempts が無い: " + settingsJson);
        double ms = 0;
        foreach (JsonNode? a in attempts)
        {
            if (a?["wall_ms"] is not JsonValue v || !v.TryGetValue(out double w)) throw new InvalidDataException("attempts の要素に wall_ms が無い: " + settingsJson);
            ms += w;
        }
        return ms / 1000.0;
    }

    public static IReadOnlyList<CheckResult> Evaluate(CaptionsCheckInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        IReadOnlyList<CaptionsCheckRow> rows = input.Rows;
        int Count(string kind) => rows.Count(r => r.Kind == kind);
        var results = new List<CheckResult>();

        int notProcessed = Count(CaptionsCheckRow.NotProcessed);
        results.Add(new CheckResult("1. 完走", notProcessed == 0, $"{rows.Count} 枚中、未処理 {notProcessed}"));

        int differs = Count(VlmCompare.Differs), productOnly = Count(VlmCompare.ProductFailed), match = Count(VlmCompare.Match);
        int expectedOk = rows.Count(r => r.ExpectedAttempt > 0);
        results.Add(new CheckResult("2. 評価との一致", differs <= MaxDiffers && productOnly == 0,
            $"期待のある {expectedOk} 枚中、一致 {match}、差あり {differs}(許容 {MaxDiffers} まで)、製品だけ失敗 {productOnly}(許容 0)。評価だけ失敗(期待が無いのに成功) {Count(VlmCompare.VlmEvalFailed)}、両方失敗 {Count(VlmCompare.BothFailed)}"));

        int mismatch = rows.Count(r => r.AttemptMismatch);
        results.Add(new CheckResult("3. 試行の回", mismatch == 0, $"試行の回が期待と違う画像 {mismatch} 枚(許容 0)"));

        List<CaptionsCheckRow> failed = rows.Where(r => r.Kind is VlmCompare.ProductFailed or VlmCompare.BothFailed).ToList();
        int nonLength = failed.Count(r => !r.Detail.Contains("finish_reason=length", StringComparison.Ordinal));
        results.Add(new CheckResult("4. length の失敗", failed.Count <= MaxLengthFailures && nonLength == 0,
            $"製品が最終的に失敗にした画像 {failed.Count} 枚(許容 {MaxLengthFailures} まで。見込みは 0)、length 以外の理由 {nonLength} 枚(許容 0)"));

        int exp2 = rows.Count(r => r.ExpectedAttempt == 2), exp3 = rows.Count(r => r.ExpectedAttempt == 3);
        int got2 = rows.Count(r => r.ProductAttempt == 2), got3 = rows.Count(r => r.ProductAttempt == 3);
        results.Add(new CheckResult("5. 再試行で救えた数", Math.Abs(got2 - exp2) <= 1 && Math.Abs(got3 - exp3) <= 1 && got2 + got3 >= exp2 + exp3 - 1,
            $"2 回目(dry_multiplier)で成功 {got2} 枚(期待 {exp2} ±1)、3 回目(repeat_penalty)で成功 {got3} 枚(期待 {exp3} ±1)"));

        Distribution p = Distribution.From(input.ProductWallFirstAttempt), s = Distribution.From(input.VlmEvalWallSameImages);
        if (!input.TimesAvailable)
        {
            results.Add(new CheckResult("6. 時間(1 回目で成功した画像)", true, "見ない(--ignore-times。取り込みの行の確認用。通しの確認では使わない)", Weak: true));
        }
        else
        {
        bool timeOk = p.Count > 0 && s.Count > 0 && p.P50 >= TimeP50Low * s.P50 && p.P50 <= TimeP50High * s.P50 && p.P90 <= TimeP90High * s.P90;
        results.Add(new CheckResult("6. 時間(1 回目で成功した画像)", timeOk,
            $"製品: {p} / 評価(同じ画像): {s}。合格線: p50 が評価の {TimeP50Low}〜{TimeP50High} 倍、p90 が評価の {TimeP90High} 倍以下"));
        }

        List<RunRow> vlmRuns = input.Runs.Where(r => r.JobKind == "vlm").ToList();
        int open = vlmRuns.Count(r => r.EndedAt is null), noBuild = vlmRuns.Count(r => !r.RuntimeJson.Contains("12110", StringComparison.Ordinal));
        results.Add(new CheckResult("7. runs", vlmRuns.Count >= input.ExpectedVlmRuns && open == 0 && noBuild == 0,
            $"VLM の行 {vlmRuns.Count}(期待 {input.ExpectedVlmRuns} 以上)、ended_at が無い {open}(許容 0)、runtime_json に build 12110 が無い {noBuild}(許容 0)"));

        results.Add(new CheckResult("8. 再処理なし", input.ReprocessedStageRows == 0, $"attempts > 1 の ocr・caption の行 {input.ReprocessedStageRows}(許容 0)"));
        results.Add(new CheckResult("9. 段階の一致", input.OcrDone == input.CaptionDone, $"ocr の done {input.OcrDone}、caption の done {input.CaptionDone}(同じ数のはず)"));
        return results;
    }

}
