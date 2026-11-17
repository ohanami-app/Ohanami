using System;
using System.Linq;
using Xunit;

namespace S3Fts.Tests;

public class NormalizationTests
{
    /// <summary>S1 の採点(S1Vlm.TextNorm.Normalize。NFKC → 小文字化 → 空白除去)と、NFKC・小文字化の部分が同じであること。</summary>
    [Theory]
    [InlineData("ｶﾞﾁｬ 10連\nＨＰ㈱ ﾊﾟﾜｰ")]
    [InlineData("この指輪で\nもスピードアップの効果があるのでしょうか")]
    [InlineData("Hello　World\tÉCOLE ﬁ ㌔ ① Ⅻ")]
    [InlineData("𠮷野家 é が")]
    [InlineData("")]
    public void SameAsS1Scoring_AfterRemovingWhitespace(string s)
    {
        string mine = new string(NormalizedText.Create(s).Text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Equal(S1Vlm.TextNorm.Normalize(s), mine);
    }

    [Fact]
    public void HalfWidthKana_WithVoicedMark_IsOneNormalizedChar_MappedToBothOriginalChars()
    {
        var n = NormalizedText.Create("ｶﾞ");
        Assert.Equal("ガ", n.Text);
        Assert.Equal(new TextRange(0, 2), n.ToOriginal(0, 1));
    }

    [Fact]
    public void Expansion_MapsEveryExpandedCharToTheWholeOriginalChar()
    {
        var n = NormalizedText.Create("a㈱b"); // ㈱ は1文字 → "(株)" の3文字
        Assert.Equal("a(株)b", n.Text);
        Assert.Equal(new TextRange(1, 1), n.ToOriginal(1, 2));
        Assert.Equal(new TextRange(1, 1), n.ToOriginal(2, 3));
        Assert.Equal(new TextRange(1, 1), n.ToOriginal(1, 4));
        Assert.Equal(new TextRange(0, 3), n.ToOriginal(0, 5));
    }

    [Fact]
    public void Lowercase_IsInvariant()
    {
        Assert.Equal("hp", NormalizedText.Create("ＨＰ").Text);
        Assert.Equal("i", NormalizedText.Create("I").Text); // トルコ語のロケールに依存しない
    }

    [Fact]
    public void Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => NormalizedText.Create(null!));
    }

    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("a\u0007b")]
    [InlineData("a\u001Bb")]
    [InlineData("a\u007Fb")]
    [InlineData("a\u0090b")]
    public void ControlChars_Throw(string s)
    {
        var e = Assert.Throws<ArgumentException>(() => NormalizedText.Create(s));
        Assert.Contains("制御文字", e.Message);
    }

    // 注: 不正なサロゲートは [InlineData] の文字列に入れられない(属性のメタデータで U+FFFD に置き換わる)ので、コードで渡す。
    [Fact]
    public void BrokenSurrogates_Throw()
    {
        foreach (string s in new[] { "\uD800", "a\uDC00b", "\uD800a", "ab\uD800" })
        {
            var e = Assert.Throws<ArgumentException>(() => NormalizedText.Create(s));
            Assert.Contains("サロゲート", e.Message);
        }
    }

    [Theory]
    [InlineData("a\tb")]
    [InlineData("a\r\nb")]
    [InlineData("a\nb")]
    [InlineData("a\u0085b")]
    public void WhitespaceControlChars_AreAllowed(string s)
    {
        NormalizedText.Create(s); // 例外にならない
    }
}
