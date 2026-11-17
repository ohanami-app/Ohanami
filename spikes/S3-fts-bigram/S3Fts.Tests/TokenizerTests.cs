using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace S3Fts.Tests;

public class TokenizerTests
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
        Assert.Equal(new[] { "^毛", "毛玉", "玉$" }, Doc("毛玉"));
        Assert.Equal(new[] { "^毛", "毛$" }, Doc("毛"));
    }

    [Fact]
    public void Document_JapaneseAndAlnumMixed()
    {
        Assert.Equal(new[] { "^ガ", "ガチ", "チャ", "ャ$", "10", "^連", "連$" }, Doc("ガチャ10連"));
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
    public void NewlineOrSpaceBetweenJapanese_IsRemoved()
    {
        Assert.Equal(Doc("この指輪でもスピードアップ"), Doc("この指輪で\nもスピードアップ"));
        Assert.Equal(Doc("この指輪でもスピードアップ"), Doc("この指輪で \r\n 　もスピードアップ")); // 全角空白も
    }

    [Fact]
    public void SpaceBetweenWords_IsKept_AndJapaneseToWordSpaceStaysASeparator()
    {
        Assert.Equal(new[] { "hello", "world" }, Doc("hello\nworld"));
        Assert.Equal(new[] { "^ガ", "ガチ", "チャ", "ャ$", "10" }, Doc("ガチャ 10"));
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
        Assert.Equal(new[] { "^ゲ", "ゲー", "ーム", "ム$", "^ユ", "ユニ", "ニッ", "ット", "ト$" }, Doc("ゲーム・ユニット"));
    }

    [Fact]
    public void LongVowelMark_And_IterationMark_AreJapanese()
    {
        Assert.Equal(new[] { "^パ", "パワ", "ワー", "ーア", "ア$" }, Doc("パワーア"));
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
        Assert.Equal(new[] { "^あ", "あ$", "^い", "い$" }, Doc("あい"));
        Assert.Empty(Doc(""));
        Assert.Empty(Doc(""));
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
        Assert.Equal(new[] { "毛玉" }, Qry("毛玉"));
        Assert.Equal(new[] { "ガチ", "チャ" }, Qry("ガチャ"));
        // 「連」は 10 と隣り合う側だけ境界になる
        Assert.Equal(new[] { "ガチ", "チャ", "ャ$", "10", "^連" }, Qry("ガチャ10連"));
        Assert.Equal(new[] { "ガチ", "チャ", "ャ$", "10", "^連", "連$", "20" }, Qry("ガチャ10連20"));
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
        Assert.Equal(new[] { "single:ガ" }, Qry("ｶﾞ"));
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
        Assert.Equal(Qry("指輪でも"), Qry("指輪で\nも"));
    }

    [Fact]
    public void Query_WithoutSearchableChars_Throws()
    {
        Assert.Throws<ArgumentException>(() => Qry(""));
        Assert.Throws<ArgumentException>(() => Qry(" \n"));
        Assert.Throws<ArgumentException>(() => Qry("「」。！？"));
    }
}
