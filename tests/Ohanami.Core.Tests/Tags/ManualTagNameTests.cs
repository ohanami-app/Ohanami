using System;
using Ohanami.Core.Tags;
using Xunit;

namespace Ohanami.Core.Tests.Tags;

/// <summary>手動タグの名前の決まり(ADR 0036): Trim + NFKC、大文字・小文字は打ったまま、拒む名前(理由つき)、長さの数え方。</summary>
public sealed class ManualTagNameTests
{
    [Theory]
    [InlineData("猫", "猫")]
    [InlineData("  猫  ", "猫")]
    [InlineData("　猫　", "猫")]                       // 全角空白の前後
    [InlineData("ＨＰ回復", "HP回復")]                          // 全角英数 → 半角
    [InlineData("ｶﾞﾁｬ", "ガチャ")]                            // 半角カナ → 全角カナ
    [InlineData("①", "1")]                                   // NFKC の互換分解
    [InlineData("Cat", "Cat")]                               // 大文字・小文字は打ったまま
    [InlineData("cAT", "cAT")]
    [InlineData("ＣＡＴ", "CAT")]                              // 全角の大文字 → 半角の大文字(小文字にはしない)
    [InlineData("銀髪 ロング", "銀髪 ロング")]                   // 中の空白は残す
    public void Names_are_trimmed_and_nfkc_normalized_but_the_case_is_kept(string input, string expected) =>
        Assert.Equal(expected, ManualTagName.Normalize(input));

    [Theory]
    [InlineData("猫")]
    [InlineData("  ＨＰ回復 ")]
    [InlineData("ｶﾞﾁｬ ＡＢＣ")]
    [InlineData("😀絵文字")]
    [InlineData("㌔")]
    public void Normalizing_twice_changes_nothing(string input)
    {
        string once = ManualTagName.Normalize(input);
        Assert.Equal(once, ManualTagName.Normalize(once));
    }

    [Theory]
    [InlineData("", "空")]
    [InlineData("   ", "空")]
    [InlineData("　", "空")]
    [InlineData("a\tb", "制御文字")]                            // タブも拒む
    [InlineData("a\nb", "制御文字")]
    [InlineData("a\r\nb", "制御文字")]
    [InlineData("a\u0000b", "制御文字")]
    [InlineData("a\u2028b", "制御文字")]                        // 行区切り・段落区切り
    [InlineData("a\u2029b", "制御文字")]
    [InlineData("a|b", "|")]
    [InlineData("a、b", "、")]
    [InlineData("｜", "|")]                                   // 全角の縦線は NFKC で「|」になる(正規化のあとで拒む)
    [InlineData("a﹑b", "、")]                            // 小さな読点(互換)は NFKC で「、」になる
    public void Names_that_break_the_rules_are_rejected_with_a_reason(string input, string reasonPart)
    {
        var e = Assert.Throws<InvalidTagNameException>(() => ManualTagName.Normalize(input));
        Assert.Contains(reasonPart, e.Reason, StringComparison.Ordinal);
        Assert.Equal(input, e.Name);
        Assert.Contains(e.Reason, e.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<ArgumentException>(e);
    }

    [Fact]
    public void An_unpaired_surrogate_is_rejected_with_a_reason()
    {
        // 対になっていないサロゲートは、テストのデータ(InlineData)に渡せない(渡す途中で U+FFFD になる)ので、ここで組み立てる
        foreach (string input in new[] { "a" + (char)0xD800 + "b", "a" + (char)0xDC00, "" + (char)0xD83D, (char)0xDE00 + "x" })
        {
            var e = Assert.Throws<InvalidTagNameException>(() => ManualTagName.Normalize(input));
            Assert.Contains("サロゲート", e.Reason, StringComparison.Ordinal);
        }
        Assert.Equal("a😀b", ManualTagName.Normalize("a" + (char)0xD83D + (char)0xDE00 + "b"));   // 対になっていれば通る
    }

    [Fact]
    public void The_length_limit_is_64_characters_after_normalization_counted_in_code_points()
    {
        Assert.Equal(64, ManualTagName.MaxLength);
        Assert.Equal(new string('あ', 64), ManualTagName.Normalize(new string('あ', 64)));
        var e = Assert.Throws<InvalidTagNameException>(() => ManualTagName.Normalize(new string('あ', 65)));
        Assert.Contains("64", e.Reason, StringComparison.Ordinal);
        Assert.Contains("65", e.Reason, StringComparison.Ordinal);

        // サロゲートペアは 1 文字(UTF-16 では 2 コードユニットだが、64 文字までは通る)
        string emoji64 = string.Concat(System.Linq.Enumerable.Repeat("😀", 64));
        Assert.Equal(128, emoji64.Length);
        Assert.Equal(emoji64, ManualTagName.Normalize(emoji64));
        Assert.Throws<InvalidTagNameException>(() => ManualTagName.Normalize(emoji64 + "😀"));

        // 長さは正規化のあとで数える: 「㌔」(1 文字)は NFKC で「キロ」(2 文字)になるので、33 個は 66 文字
        Assert.Equal(64, ManualTagName.Normalize(new string('㌔', 32)).Length);
        Assert.Throws<InvalidTagNameException>(() => ManualTagName.Normalize(new string('㌔', 33)));

        // 前後の空白は数えない
        Assert.Equal(new string('a', 64), ManualTagName.Normalize("  " + new string('a', 64) + "  "));
    }

    [Fact]
    public void Null_is_an_argument_error_not_a_rejected_name() =>
        Assert.Throws<ArgumentNullException>(() => ManualTagName.Normalize(null!));

    [Fact]
    public void The_exception_names_the_rule_in_japanese_for_the_user()
    {
        var e = Assert.Throws<InvalidTagNameException>(() => ManualTagName.Normalize("a|b"));
        Assert.StartsWith("タグの名前として使えない: ", e.Message, StringComparison.Ordinal);
        Assert.Contains("区切り", e.Reason, StringComparison.Ordinal);
    }
}
