using System;
using Ohanami.Core.FullText;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// 規約の版の上げ忘れの守り: 固定の入力に対する <see cref="FtsDocumentBuilder.Build"/> の出力(正規化後の文書と、投入する語の列)を literal で固定する。
/// <b>このテストが落ちたら</b>、索引文書の作り方が変わっている。意図した変更なら、変えた部分に対応する版(<see cref="FtsSpec.NormalizeVersion"/>・
/// <see cref="FtsSpec.TokenizerVersion"/>・<see cref="FtsSpec.SanitizeVersion"/>。連結の区切りなら <see cref="FtsSpec.SpecHeader"/>)を上げてから、
/// 期待値を直す(版を上げないと、既存の DB の索引が古い規約のまま使われ、新しいクエリと食い違う)。意図しない変更なら、コードを戻す。
/// 語の列の「U+E000」「U+E001」は、日本語の run の前後の境界記号(<see cref="Tokenizer"/>)。
/// </summary>
public sealed class FtsSpecPinTests
{
    public static TheoryData<int, string, string, string[], string, string> Cases => new()
    {
        { 1, "ガチャ10連", "説明", ["銀髪", "ロングヘア"],
            "がちゃ10連|説明|銀髪、ろんぐへあ",
            "が がち ちゃ ゃ 10 連 連 説 説明 明 銀 銀髪 髪 ろ ろん んぐ ぐへ へあ あ" },
        { 2, "", "", [], "||", "" },
        // 半角カナ・全角の英数字・全角の空白(NFKC)
        { 3, "ｶﾞﾁｬ　ＡＢＣ１２３", "Full-Width ＡＢＣ", ["ｶﾞ"],
            "がちゃ abc123|full-width abc|が",
            "が がち ちゃ ゃ abc123 full width abc が が" },
        // 英語(記号は区切り。'.' や ''' で語を割る)
        { 4, "Hello, World! It's 3.14", "english sentence", ["tag one", "tag-two"],
            "hello, world! it's 3.14|english sentence|tag one、tag-two",
            "hello world it s 3 14 english sentence tag one tag two" },
        // サロゲートペア(𠮷・絵文字。絵文字は語を作らない)
        { 5, "𠮷野家 😀", "サロゲートペア", [],
            "𠮷野家 😀|さろげーとぺあ|",
            "𠮷 𠮷野 野家 家 さ さろ ろげ げー ーと とぺ ぺあ あ" },
        // 制御文字は U+FFFD に置き換わる(sanitize-v1。その U+FFFD は語を割る)
        { 6, "a\u0000b\u0001", "制御文字", ["x\u007Fy"],
            "a�b�|制御文字|x�y",
            "a b 制 制御 御文 文字 字 x y" },
        // 1 文字の run(境界記号つきの 1 文字)
        { 7, "あ", "一文字", ["猫"],
            "あ|一文字|猫",
            "あ あ 一 一文 文字 字 猫 猫" },
        // 濁点(結合文字は NFKC で合成)、日本語どうしの間の空白は読み飛ばす
        { 8, "ヴァイオリン ゔ", "濁点 dakuten", ["が", "が"],
            "ゔぁいおりん ゔ|濁点 dakuten|が、が",
            "ゔ ゔぁ ぁい いお おり りん んゔ ゔ 濁 濁点 点 dakuten が が が が" },
        // 括弧・波ダッシュ・音符は区切り(全角の ～ は NFKC で ~ になる)
        { 9, "セリフ「むにゃにゃ～♪」", "波ダッシュ ～ 〜", ["♪"],
            "せりふ「むにゃにゃ~♪」|波だっしゅ ~ 〜|♪",
            "せ せり りふ ふ む むに にゃ ゃに にゃ ゃ 波 波だ だっ っし しゅ ゅ" },
        // 英字の大文字・アクセント(NFKC で合成、小文字化。アクセントの除去は SQLite の unicode61 の側)
        { 10, "Ａｐｐｌｅ ＩＰＨＯＮＥ", "ÀÉÎ café", ["résumé"],
            "apple iphone|àéî café|résumé",
            "apple iphone àéî café résumé" },
        // 改行・タブ(空白として残る)、「、」だけの tag(加工しない)
        { 11, "line1\nline2\ttab", "x", ["、、"],
            "line1\nline2\ttab|x|、、",
            "line1 line2 tab x" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_document_for_a_fixed_input_is_pinned_to_the_literal(int id, string ocr, string description, string[] tags, string normText, string tokens)
    {
        FtsDocument doc = FtsDocumentBuilder.Build(ocr, description, tags);
        Assert.True(id > 0);
        Assert.Equal(normText, doc.NormText);
        Assert.Equal(tokens, doc.Tokens);
    }

    [Fact]
    public void The_pinned_spec_constants_are_the_ones_the_expectations_above_were_made_with()
    {
        // 上の期待値は、この版の組で作った。版を上げたら、期待値も見直す
        Assert.Equal("fts-spec/1", FtsSpec.SpecHeader);
        Assert.Equal("nfkc-lower-kana1", FtsSpec.NormalizeVersion);
        Assert.Equal("bigram-pad-v1", FtsSpec.TokenizerVersion);
        Assert.Equal("sanitize-v1", FtsSpec.SanitizeVersion);
    }
}
