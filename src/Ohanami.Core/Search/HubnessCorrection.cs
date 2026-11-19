using System;
using System.IO;
using System.Linq;
using Ohanami.Core.Embedding;
using Ohanami.Core.Embedding.Generated;
using Ohanami.Core.FullText;

namespace Ohanami.Core.Search;

/// <summary>
/// 特徴ベクトル検索の点から、どの言葉にも近い画像の分を引く補正をかける検索語の関門(ADR 0051)。
/// かける検索語 = 「日本語の文字がある」かつ「<see cref="TextCleaner"/> で整えたあとのコードポイントが <see cref="MaxCodePoints"/> 以下」。
/// それ以外(英字だけ・長い文)は補正なしで、検索語のベクトルとの cosine がそのまま点になる。
/// 「日本語の文字」= 全文検索の <see cref="CharClassifier.IsJapanese"/>(ADR 0029)。全文検索の側は NFKC などの正規化をかけた文字に当てるので、
/// ここでも <see cref="NormalizedText"/> の結果に当てる(半角カナは、正規化で全角のカナになってから日本語の文字になる)。
/// 字数は UTF-16 の長さではなくコードポイントの数で、空白も 1 字と数える。
/// </summary>
internal static class HubnessGate
{
    /// <summary>補正をかける検索語の最大の字数(コードポイント。この値を含む)。</summary>
    public const int MaxCodePoints = 8;

    /// <summary><see cref="TextCleaner"/> で整えたあとのコードポイントの数。</summary>
    public static int CodePointCount(string query) => TextCleaner.Clean(query).EnumerateRunes().Count();

    /// <summary>全文検索の側と同じ正規化をかけたあとに、日本語の文字がある。</summary>
    public static bool HasJapanese(string query) => NormalizedText.Create(query).Text.EnumerateRunes().Any(CharClassifier.IsJapanese);

    /// <summary>この検索語に補正をかけるか。</summary>
    public static bool InScope(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return HasJapanese(query) && CodePointCount(query) <= MaxCodePoints;
    }
}

/// <summary>
/// 補正の中身: 参照の語(<c>tools/Ohanami.Bench/hubness-reference-ja.txt</c>)のテキスト埋め込みの平均 μ(正規化し直さない)。
/// 画像 r の補正値 b(r) = μ · r で、点 = 検索語との cosine − b(r)。b(r) は画像だけで決まり、検索語に依らない。
/// μ は <see cref="HubnessReferenceMean"/>(Bench の hubness-mean コマンドが生成した C# のソース)に、作ったときの model_id と一緒にある。
/// 検索の表のベクトルを作ったモデルと μ を作ったモデルが違えば、補正は意味を持たないので、作る時点で例外にする(黙って補正なしにしない)。
/// </summary>
internal sealed class HubnessCorrection
{
    /// <summary>製品が使う補正(生成された μ)。</summary>
    public static HubnessCorrection Default { get; } = new(HubnessReferenceMean.ModelId, HubnessReferenceMean.Mean);

    private readonly float[] _mean;

    /// <param name="modelId">μ を作った埋め込みモデルの model_id。</param>
    /// <param name="mean">μ(長さ = モデルの次元。すべて有限)。コピーして持つ。</param>
    public HubnessCorrection(string modelId, ReadOnlySpan<float> mean)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        if (mean.Length == 0) throw new ArgumentException("μ が空", nameof(mean));
        foreach (float x in mean) if (!float.IsFinite(x)) throw new InvalidDataException("μ に有限でない値がある。");
        ModelId = modelId;
        _mean = mean.ToArray();
    }

    /// <summary>μ を作った埋め込みモデルの model_id。</summary>
    public string ModelId { get; }

    /// <summary>μ の次元。</summary>
    public int Dim => _mean.Length;

    /// <summary>
    /// 検索の表(<paramref name="tableModelId"/>・<paramref name="tableDim"/>)のベクトルに、この補正をかけてよいかを確かめる。違えば例外。
    /// </summary>
    public void EnsureMatches(string? tableModelId, int tableDim)
    {
        if (!string.Equals(ModelId, tableModelId, StringComparison.Ordinal))
            throw new InvalidOperationException($"補正の μ を作ったモデル({ModelId})と、ベクトルの表のモデル({tableModelId})が違う。モデルを替えたときは、μ を作り直す(Bench の hubness-mean)。");
        if (tableDim != Dim)
            throw new InvalidOperationException($"補正の μ の次元({Dim})と、ベクトルの表の次元({tableDim})が違う。");
    }

    /// <summary>この検索語に補正をかけるか(<see cref="HubnessGate.InScope"/>)。</summary>
    public bool AppliesTo(string query) => HubnessGate.InScope(query);

    /// <summary>画像のベクトル <paramref name="image"/> の補正値 b(r) = μ · r。double で積和する(順位の決め手になる小さい差を、float の積和で崩さないため)。</summary>
    public double Bias(ReadOnlySpan<float> image)
    {
        if (image.Length != _mean.Length) throw new ArgumentException($"画像のベクトルの長さが {image.Length}(μ は {_mean.Length})");
        double sum = 0;
        for (int i = 0; i < _mean.Length; i++) sum += (double)_mean[i] * image[i];
        return sum;
    }
}
