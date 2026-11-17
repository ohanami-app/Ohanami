using System;
using System.Linq;
using Ohanami.Core.FullText;
using Xunit;

namespace Ohanami.Core.Tests;

public sealed class FtsNormalizationTests
{
    /// <summary>評価のコードの採点(TextNorm.Normalize。NFKC → 小文字化 → 空白除去)と、NFKC・小文字化の部分が同じであること。カタカナ → ひらがな(ADR 0031)は評価のコードに無いので、期待値の側で別に(下の <see cref="TestKanaFold"/>)写す。</summary>
    [Theory]
    [InlineData("ｶﾞﾁｬ 10連\nＨＰ㈱ ﾊﾟﾜｰ")]
    [InlineData("この帽子に\nもポップコーンの効果があるのだろうか")]
    [InlineData("Hello　World\tÉCOLE ﬁ ㌔ ① Ⅻ")]
    [InlineData("𠮷野家 é が")]
    [InlineData("")]
    public void SameAsEvaluationScoring_AfterRemovingWhitespace(string s)
    {
        string mine = new string(NormalizedText.Create(s).Text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Equal(TestKanaFold(EvaluationNormalize(s)), mine);
    }

    /// <summary>
    /// カタカナ → ひらがなの、テスト側の別の書き方(製品の <see cref="KanaFold"/> を使わない。範囲は ADR 0031 のとおり: U+30A1〜U+30F6 → U+3041〜U+3096、ヽヾ → ゝゞ)。
    /// </summary>
    private static string TestKanaFold(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c >= 'ァ' && c <= 'ヶ') sb.Append((char)(c - 0x30A1 + 0x3041));
            else if (c == 'ヽ') sb.Append('ゝ');
            else if (c == 'ヾ') sb.Append('ゞ');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>評価のコードの TextNorm.Normalize と同じ定義(評価のコードは参照しないので、ここに写した): NFKC → ToLowerInvariant → char.IsWhiteSpace を除去。</summary>
    private static string EvaluationNormalize(string s) =>
        new(s.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());

    [Fact]
    public void HalfWidthKana_WithVoicedMark_IsOneNormalizedChar_MappedToBothOriginalChars()
    {
        var n = NormalizedText.Create("ｶﾞ");
        Assert.Equal("が", n.Text);     // NFKC で ガ、ひらがなに写って が(ADR 0031)
        Assert.Equal(new TextRange(0, 2), n.ToOriginal(0, 1));
    }

    // ---------- ひらがなとカタカナの同一視(ADR 0031) ----------

    [Fact]
    public void Every_katakana_in_the_mapped_range_becomes_the_hiragana_a_fixed_offset_below_and_stays_japanese()
    {
        for (int cp = 0x30A1; cp <= 0x30F6; cp++)
        {
            string kata = ((char)cp).ToString();
            string hira = NormalizedText.Create(kata).Text;
            Assert.Equal(1, hira.Length);
            Assert.Equal(cp - 0x30A1 + 0x3041, hira[0]);                                    // ァ → ぁ … ヶ → ゖ
            Assert.True(CharClassifier.IsJapanese(new System.Text.Rune(hira[0])), $"U+{cp:X4} の写し先が日本語の文字ではない");
            Assert.True(CharClassifier.IsJapanese(new System.Text.Rune(cp)));                // 写す前も日本語の文字(分割の種類は変わらない)
        }
        Assert.Equal("ゝ", NormalizedText.Create("ヽ").Text);                       // ヽ → ゝ
        Assert.Equal("ゞ", NormalizedText.Create("ヾ").Text);                       // ヾ → ゞ
        Assert.True(CharClassifier.IsJapanese(new System.Text.Rune(0x309D)));
        Assert.True(CharClassifier.IsJapanese(new System.Text.Rune(0x309E)));
    }

    [Fact]
    public void Characters_without_a_hiragana_counterpart_and_the_long_vowel_mark_are_left_alone()
    {
        Assert.Equal("こと", NormalizedText.Create("ヿ").Text);      // ヿ(U+30FF)は写像の対象ではないが、NFKC で コト になるので、結果はひらがな
        foreach (string s in new[] { "ー", "ヷ", "ヸ", "ヹ", "ヺ", "・", "ㇰ", "ㇿ", "ぁ", "ん", "ゔ", "漢", "A" })
        {
            string expected = s.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant();
            Assert.Equal(expected, NormalizedText.Create(s).Text);
        }
    }

    [Fact]
    public void The_voiced_and_semi_voiced_marks_are_still_told_apart()
    {
        Assert.NotEqual(NormalizedText.Create("か").Text, NormalizedText.Create("が").Text);
        Assert.NotEqual(NormalizedText.Create("ハ").Text, NormalizedText.Create("パ").Text);
        Assert.Equal(NormalizedText.Create("ヴ").Text, NormalizedText.Create("ゔ").Text);   // ヴ と「う+濁点」(NFKC で ゔ に合成)は同じ
    }

    [Fact]
    public void Hiragana_katakana_and_half_width_katakana_make_the_same_text()
    {
        string expected = "ほるてばるど";
        Assert.Equal(expected, NormalizedText.Create("ホルテバルド").Text);
        Assert.Equal(expected, NormalizedText.Create("ほるてばるど").Text);
        Assert.Equal(expected, NormalizedText.Create("ﾎﾙﾃﾊﾞﾙﾄﾞ").Text);
    }

    [Fact]
    public void The_original_ranges_still_point_at_the_characters_the_user_wrote()
    {
        var n = NormalizedText.Create("あホルテバルド");           // 7 文字。正規化後も 7 文字(1 文字 → 1 文字)
        Assert.Equal("あほるてばるど", n.Text);
        Assert.Equal(new TextRange(1, 3), n.ToOriginal(1, 4));     // 「ほるて」← 「ホルテ」
        Assert.Equal(new TextRange(0, 7), n.ToOriginal(0, 7));
        var h = NormalizedText.Create("ﾎﾙﾃﾊﾞﾙﾄﾞ");                // 半角 8 文字(ﾊﾞ と ﾄﾞ は 2 文字で 1 文字になる)→ 6 文字
        Assert.Equal("ほるてばるど", h.Text);
        Assert.Equal(new TextRange(3, 2), h.ToOriginal(3, 4));     // 「ば」← 「ﾊﾞ」
        Assert.Equal(new TextRange(5, 1), h.ToOriginal(4, 5));     // 「る」← 「ﾙ」
        Assert.Equal(new TextRange(6, 2), h.ToOriginal(5, 6));     // 「ぐ」← 「ｸﾞ」
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
