using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace S2Siglip2.Tests;

/// <summary>known-item の評価ロジック(順位・判定・説明文の最初の1文・TSV と埋め込みの入出力)。モデルは要らない。</summary>
public class KnownItemsTests
{
    [Theory]
    [InlineData("赤い座布団が縁側の上に置かれており、背景は穏やかな庭の風景。猫は少し眠そう。", "赤い座布団が縁側の上に置かれており、背景は穏やかな庭の風景")]
    [InlineData("終端が無い説明", "終端が無い説明")]
    [InlineData("  前後に空白。次の文。", "前後に空白")]
    [InlineData("改行で\n切れる。", "改行で")]
    [InlineData("本当ですか？はい。", "本当ですか")]
    [InlineData("Hello! world", "Hello")]
    public void FirstSentence_CutsAtTheFirstTerminator(string description, string expected)
    {
        Assert.Equal(expected, KnownItems.FirstSentence(description));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("。次の文")]
    public void FirstSentence_RejectsEmpty(string description)
    {
        Assert.Throws<InvalidDataException>(() => KnownItems.FirstSentence(description));
    }

    private static float[] Unit(params float[] v)
    {
        double n = Math.Sqrt(v.Sum(x => (double)x * x));
        return v.Select(x => (float)(x / n)).ToArray();
    }

    [Fact]
    public void Rank_IsOnePlusTheNumberOfStrictlyBetterImages()
    {
        var images = new List<float[]>
        {
            Unit(1, 0, 0), // 0
            Unit(0, 1, 0), // 1
            Unit(1, 1, 0), // 2
            Unit(0, 0, 1), // 3
            Unit(1, 0.1f, 0), // 4
        };
        float[] q = Unit(1, 0, 0);
        // スコア: 0 → 1.0、4 → 約 0.995、2 → 約 0.707、1 と 3 → 0
        Assert.Equal(1, KnownItems.Rank(q, images, 0).Rank);
        Assert.Equal(2, KnownItems.Rank(q, images, 4).Rank);
        Assert.Equal(3, KnownItems.Rank(q, images, 2).Rank);
        var r = KnownItems.Rank(q, images, 3);
        Assert.Equal(4, r.Rank);        // 厳密に大きいのは 0, 4, 2 の3枚
        Assert.Equal(1, r.TiedCount);   // 画像 1 と同点
        Assert.Equal(0, r.Top1Index);
        Assert.Equal(new[] { 0, 4, 2, 1, 3 }, r.TopIndices);
    }

    [Fact]
    public void Rank_ExactDuplicates_AreTied_NotBetter()
    {
        var images = new List<float[]> { Unit(1, 0), Unit(1, 0), Unit(0, 1) };
        var r = KnownItems.Rank(Unit(1, 0), images, 1);
        Assert.Equal(1, r.Rank);
        Assert.Equal(1, r.TiedCount);
    }

    [Fact]
    public void Evaluate_FollowsTheSpecRules()
    {
        // 6 件中: 上位5位以内 4 件(2/3)→ 強い
        Assert.StartsWith("強い", KnownItems.Evaluate([1, 2, 5, 5, 30, 200]).Label);
        // 上位20位以内 4/6 だが 上位5位以内は 3/6 → 使える
        var usable = KnownItems.Evaluate([1, 3, 5, 10, 20, 100]);
        Assert.StartsWith("見た目の経路として使える", usable.Label);
        Assert.Equal((3, 5), (usable.Top5, usable.Top20));
        // 上位20位以内が 1/3 未満 → 使えない
        Assert.StartsWith("使えない", KnownItems.Evaluate([1, 30, 40, 50, 60, 70, 80]).Label);
        // その間
        Assert.StartsWith("どちらともいえない", KnownItems.Evaluate([1, 2, 30, 40, 50, 60]).Label);
        Assert.Equal("クエリが無い", KnownItems.Evaluate([]).Label);
        var v = KnownItems.Evaluate([4, 1, 100]);
        Assert.Equal((3, 1, 2, 2), (v.Count, v.Top1, v.Top5, v.Top20));
        Assert.Equal(4, v.MedianRank);
    }

    [Fact]
    public void ReadTsv_AllowsMissingEnglishColumn_AndEmptyQueries()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "filename\tquery_ja\nスクリーンショット 1.png\t青い毛玉\nb.png\t\n", new System.Text.UTF8Encoding(false));
            var rows = KnownItems.Read(path);
            Assert.Equal(2, rows.Count);
            Assert.Equal(new KnownItemRow("スクリーンショット 1.png", "青い毛玉", null), rows[0]);
            Assert.Equal(new KnownItemRow("b.png", null, null), rows[1]);

            File.WriteAllText(path, "filename\tquery_ja\tquery_en\na.png\tねこ\tcat\n", new System.Text.UTF8Encoding(false));
            Assert.Equal(new KnownItemRow("a.png", "ねこ", "cat"), KnownItems.Read(path).Single());

            File.WriteAllText(path, "name\tquery\n", new System.Text.UTF8Encoding(false));
            Assert.Throws<InvalidDataException>(() => KnownItems.Read(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void KnownItemsFile_InTheRepo_HasTheExpectedShape()
    {
        string path = Path.Combine(TestFiles.Root, "spikes", "S2-siglip2", "known-items.tsv");
        var rows = KnownItems.Read(path);
        // S1 の評価セットの33枚。同じ画像の行を増やして複数のクエリを書くことがあるので、行数ではなく画像の数で見る
        Assert.Equal(33, rows.Select(r => r.Filename).Distinct().Count());
    }

    [Fact]
    public void ReadTsv_AllowsSeveralRowsForTheSameImage()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "filename\tquery_ja\tquery_en\na.png\tねこ\tcat\na.png\t白い猫\t\nb.png\tいぬ\tdog\n", new System.Text.UTF8Encoding(false));
            var rows = KnownItems.Read(path);
            Assert.Equal(3, rows.Count);
            Assert.Equal(2, rows.Count(r => r.Filename == "a.png"));
            Assert.Equal("白い猫", rows[1].QueryJa);
            Assert.Null(rows[1].QueryEn);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EmbeddingStore_RoundTrips()
    {
        string dir = Path.Combine(Path.GetTempPath(), "s2emb_" + Guid.NewGuid().ToString("N"));
        try
        {
            var rnd = new Random(1);
            var embs = Enumerable.Range(0, 3).Select(_ => Siglip2Model.L2Normalize(Enumerable.Range(0, Siglip2Model.EmbeddingDim).Select(__ => (float)rnd.NextDouble() - 0.5f).ToArray())).ToList();
            var names = new List<string> { "スクリーンショット a.png", "b.png", "c d.png" };
            EmbeddingStore.Write(dir, names, embs);
            var (n2, e2) = EmbeddingStore.Read(dir);
            Assert.Equal(names, n2);
            for (int i = 0; i < 3; i++) Assert.Equal(embs[i], e2[i]);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
