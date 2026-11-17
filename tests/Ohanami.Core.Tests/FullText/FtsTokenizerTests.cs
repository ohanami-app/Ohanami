using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ohanami.Core.FullText;
using Xunit;

namespace Ohanami.Core.Tests;

public sealed class FtsTokenizerTests
{
    /// <summary>境界記号を読める形にする(開始 → ^、終了 → $)。</summary>
    private static string Show(string t) =>
        t.Replace(Tokenizer.StartPad.ToString(), "^").Replace(Tokenizer.EndPad.ToString(), "$");

    /// <summary>文書(索引)側のトークン。</summary>
    private static List<string> Doc(string s) =>
        Tokenizer.TokenizeDocument(NormalizedText.Create(s)).Select(t => Show(t.Text)).ToList();

    /// <summary>クエリ側のトークン(1文字クエリのときは "single:" + その文字)。</summary>
    private static List<string> Qry(string s)
    {
        var q = Tokenizer.TokenizeQuery(NormalizedText.Create(s));
        if (q.SingleChar is not null) return new List<string> { "single:" + q.SingleChar };
        return q.Tokens.Select(t => Show(t.Text)).ToList();
    }

    // ---------- 文書側 ----------

    [Fact]
    public void Document_TwoCharJapanese_HasBoundaryBigrams()
    {
        Assert.Equal(new[] { "^水", "水玉", "玉$" }, Doc("水玉"));
        Assert.Equal(new[] { "^水", "水$" }, Doc("水"));
    }

    [Fact]
    public void Document_JapaneseAndAlnumMixed()
    {
        Assert.Equal(new[] { "^が", "がち", "ちゃ", "ゃ$", "10", "^連", "連$" }, Doc("ガチャ10連"));   // カタカナはひらがなに写る(ADR 0031)
        Assert.Equal(new[] { "hp", "^回", "回復", "復$" }, Doc("HP回復"));
        Assert.Equal(new[] { "use", "hp", "potion" }, Doc("Use HP potion"));
    }

    [Fact]
    public void FullWidthAndHalfWidth_AreUnified()
    {
        Assert.Equal(Doc("HP"), Doc("ＨＰ"));
        Assert.Equal(Doc("ガチャ"), Doc("ｶﾞﾁｬ"));
        Assert.Equal(Doc("ガチャ"), Doc("ガチャ")); // 結合濁点は NFKC で合成される
    }

    [Fact]
    public void Voiced_And_Unvoiced_AreDifferentTokens()
    {
        Assert.NotEqual(Doc("ガチャ"), Doc("カチャ"));
        Assert.NotEqual(Doc("パン"), Doc("ハン"));
        Assert.NotEqual(Doc("バン"), Doc("パン"));
    }

    [Fact]
    public void Katakana_And_Hiragana_AreTheSameTokens_ButTheVoicedMarkStillDecides()
    {
        // カタカナはひらがなに写る(ADR 0031)。文書側もクエリ側も同じ写像を通るので、どちらの書き方でも同じトークンになる
        Assert.Equal(Doc("ほるてばるど"), Doc("ホルテバルド"));
        Assert.Equal(Doc("ほるてばるど"), Doc("ﾎﾙﾃﾊﾞﾙﾄﾞ"));          // 半角カナ(NFKC で全角になってから写る)
        Assert.Equal(Qry("ほるてばるど"), Qry("ホルテバルド"));
        Assert.Equal(new[] { "single:が" }, Qry("ガ"));
        Assert.Equal(new[] { "single:が" }, Qry("が"));
        Assert.NotEqual(Doc("ほるてばるど"), Doc("ホルテハルド"));    // 濁点の有無は区別したまま
        Assert.NotEqual(Doc("が"), Doc("か"));
    }

    [Fact]
    public void NewlineOrSpaceBetweenJapanese_IsRemoved()
    {
        Assert.Equal(Doc("この帽子にもポップコーン"), Doc("この帽子に\nもポップコーン"));
        Assert.Equal(Doc("この帽子にもポップコーン"), Doc("この帽子に \r\n 　もポップコーン")); // 全角空白も
    }

    [Fact]
    public void SpaceBetweenWords_IsKept_AndJapaneseToWordSpaceStaysASeparator()
    {
        Assert.Equal(new[] { "hello", "world" }, Doc("hello\nworld"));
        Assert.Equal(new[] { "^が", "がち", "ちゃ", "ゃ$", "10" }, Doc("ガチャ 10"));
        // 空白を挟んで英単語と日本語がつながっていても、日本語側の bigram はつながらない
        Assert.Equal(new[] { "a", "^あ", "あい", "い$" }, Doc("a あ\nい"));
    }

    [Fact]
    public void Punctuation_SplitsRuns_AndEachRunHasItsOwnBoundaries()
    {
        Assert.Equal(new[] { "^こ", "こん", "んに", "にち", "ちは", "は$" }, Doc("こんにちは"));
        Assert.Equal(new[] { "^あ", "あ$", "^い", "い$" }, Doc("あ。い"));
        Assert.Equal(new[] { "^ね", "ね$", "^あ", "あり", "りが", "がと", "とう", "う$" }, Doc("ね」「ありがとう"));
        // 中黒は区切り
        Assert.Equal(new[] { "^げ", "げー", "ーむ", "む$", "^ゆ", "ゆに", "にっ", "っと", "と$" }, Doc("ゲーム・ユニット"));
    }

    [Fact]
    public void LongVowelMark_And_IterationMark_AreJapanese()
    {
        Assert.Equal(new[] { "^ば", "ばー", "ーべ", "べ$" }, Doc("バーベ"));   // 長音符 ー は写らず、日本語の文字のまま
        Assert.Equal(new[] { "^々", "々$" }, Doc("々"));
        Assert.Equal(new[] { "^人", "人々", "々の", "の$" }, Doc("人々の"));
    }

    [Fact]
    public void SurrogatePairKanji_IsJapanese()
    {
        Assert.Equal(new[] { "^\U00020BB7", "\U00020BB7野", "野家", "家$" }, Doc("𠮷野家"));
    }

    [Fact]
    public void PrivateUseChar_NeverBecomesAToken_SoItCannotCollideWithTheBoundaryTokens()
    {
        Assert.Equal(new[] { "^あ", "あ$", "^い", "い$" }, Doc("あ\uE000い"));
        Assert.Empty(Doc("\uE000"));
        Assert.Empty(Doc("\uE001"));
    }

    [Fact]
    public void ExpandedByNfkc_UsesTheExpandedChars()
    {
        // ㈱ → (株)。括弧は区切り、株は日本語の1文字
        Assert.Equal(new[] { "^株", "株$" }, Doc("㈱"));
    }

    [Fact]
    public void Empty_And_OnlyPunctuation_GiveNoTokens()
    {
        Assert.Empty(Doc(""));
        Assert.Empty(Doc("「」、。！？ \n"));
    }

    // ---------- クエリ側 ----------

    [Fact]
    public void Query_EdgesAreNotBoundaries_ButInteriorNeighboursAre()
    {
        Assert.Equal(new[] { "水玉" }, Qry("水玉"));
        Assert.Equal(new[] { "がち", "ちゃ" }, Qry("ガチャ"));
        // 「連」は 10 と隣り合う側だけ境界になる
        Assert.Equal(new[] { "がち", "ちゃ", "ゃ$", "10", "^連" }, Qry("ガチャ10連"));
        Assert.Equal(new[] { "がち", "ちゃ", "ゃ$", "10", "^連", "連$", "20" }, Qry("ガチャ10連20"));
        Assert.Equal(new[] { "hp", "^回", "回復" }, Qry("HP回復"));
        Assert.Equal(new[] { "あ$", "^い" }, Qry("あ。い"));
    }

    [Fact]
    public void Query_LeadingAndTrailingSeparatorsAreIgnored()
    {
        Assert.Equal(Qry("ガチャ"), Qry("  「ガチャ」。\n"));
        Assert.Equal(Qry("ガチャ10連"), Qry("「ガチャ10連」 "));
        Assert.Equal(new[] { "single:毛" }, Qry("「毛」"));
    }

    [Fact]
    public void Query_SingleChar()
    {
        Assert.Equal(new[] { "single:毛" }, Qry("毛"));
        Assert.Equal(new[] { "single:が" }, Qry("ｶﾞ"));   // 半角カナは NFKC で ガ になり、ひらがなに写る
        Assert.Equal(new[] { "single:a" }, Qry("A"));
        Assert.Equal(new[] { "single:株" }, Qry("㈱")); // (株) → 括弧は区切り
        // 1文字でも他の要素と隣り合えば FTS のフレーズ
        Assert.Equal(new[] { "連$", "10" }, Qry("連 10"));
        Assert.Equal(new[] { "10", "^連" }, Qry("10 連"));
        Assert.Equal(new[] { "^連", "連$" }, Qry("10連20").Skip(1).Take(2).ToArray());
    }

    [Fact]
    public void Query_WrappedNewlineIsRemovedBetweenJapanese()
    {
        Assert.Equal(Qry("帽子にも"), Qry("帽子に\nも"));
    }

    [Fact]
    public void Query_WithoutSearchableChars_Throws()
    {
        Assert.Throws<ArgumentException>(() => Qry(""));
        Assert.Throws<ArgumentException>(() => Qry(" \n"));
        Assert.Throws<ArgumentException>(() => Qry("「」。！？"));
    }
    // ---------- 索引の区切り(FtsSpec。ADR 0029) ----------

    [Fact]
    public void TheIndexSeparators_AreSeparators_ThatNeitherJoinNorStartAToken()
    {
        foreach (string sep in new[] { FtsSpec.Tag, FtsSpec.Field })
        {
            Assert.Single(sep.EnumerateRunes());
            Rune r = sep.EnumerateRunes().Single();
            Assert.False(CharClassifier.IsJapanese(r));
            Assert.False(CharClassifier.IsWordChar(r));
            Assert.False(Rune.IsWhiteSpace(r));          // 空白ではない(日本語どうしの間で除去されない)
            Assert.Empty(Doc(sep));                      // 区切りだけの文書にトークンは無い
        }
    }

    [Fact]
    public void Tags_JoinedWithTheTagSeparator_DoNotMakeBigramsAcrossTags()
    {
        // 「銀髪」+「ロングヘア」: 区切りを挟むので「髪ロ」の bigram は作られず、それぞれが境界記号つきの run になる
        Assert.Equal(new[] { "^銀", "銀髪", "髪$", "^ろ", "ろん", "んぐ", "ぐへ", "へあ", "あ$" }, Doc("銀髪" + FtsSpec.Tag + "ロングヘア"));
        // 空白・改行でつなぐと、日本語どうしの間なので除去されて、1 つの run になる(タグどうしの bigram がつながる)
        Assert.Equal(Doc("銀髪ロングヘア"), Doc("銀髪 ロングヘア"));
        Assert.Equal(Doc("銀髪ロングヘア"), Doc("銀髪\nロングヘア"));
        Assert.NotEqual(Doc("銀髪ロングヘア"), Doc("銀髪" + FtsSpec.Tag + "ロングヘア"));
    }

    [Fact]
    public void Fields_JoinedWithTheFieldSeparator_DoNotMakeBigramsAcrossFields()
    {
        Assert.Equal(new[] { "^あ", "あか", "か$", "^い", "いろ", "ろ$" }, Doc("あか" + FtsSpec.Field + "いろ"));
        Assert.Equal(Doc("あかいろ"), Doc("あか\nいろ"));
    }
}
