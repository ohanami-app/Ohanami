using System;
using System.Linq;
using Ohanami.Core.FullText;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>索引文書の組み立てと、制御文字などの置換(ADR 0030)。DB は使わない。</summary>
public sealed class FtsDocumentBuilderTests
{
    // ---------------------------------------------------------------- 連結

    [Fact]
    public void The_document_is_ocr_bar_description_bar_tags_joined_with_the_ideographic_comma()
    {
        Assert.Equal("セリフ|説明文|銀髪、ロングヘア、1人", FtsDocumentBuilder.Join("セリフ", "説明文", ["銀髪", "ロングヘア", "1人"]));
        Assert.Equal("|d|", FtsDocumentBuilder.Join("", "d", []));         // tags が空なら末尾が「|」で終わる
        Assert.Equal("o||t", FtsDocumentBuilder.Join("o", "", ["t"]));
        Assert.Equal("、", FtsDocumentBuilder.JoinTags(["", ""]));          // 空の tag も区切りを作る(加工しない)
        Assert.Equal("銀髪", FtsDocumentBuilder.JoinTags(["銀髪"]));
    }

    [Fact]
    public void The_separators_are_the_ones_fixed_by_the_index_spec()
    {
        Assert.Equal("|", FtsSpec.Field);
        Assert.Equal("、", FtsSpec.Tag);
        Assert.Equal("a" + FtsSpec.Field + "b" + FtsSpec.Field + "c" + FtsSpec.Tag + "d", FtsDocumentBuilder.Join("a", "b", ["c", "d"]));
    }

    // ---------------------------------------------------------------- 置換(sanitize-v1)

    [Fact]
    public void Sanitize_replaces_control_characters_other_than_whitespace_with_one_replacement_char_each()
    {
        Assert.Equal("a�b", FtsDocumentBuilder.Sanitize("a\u0000b"));
        Assert.Equal("���", FtsDocumentBuilder.Sanitize("\u0001\u001F\u007F"));
        Assert.Equal("x�y", FtsDocumentBuilder.Sanitize("x\u0090y"));       // C1 の制御文字
        // タブ・改行(空白である制御文字)は残す(CharClassifier.Validate も許す)
        Assert.Equal("a\tb\r\nc\u0085d", FtsDocumentBuilder.Sanitize("a\tb\r\nc\u0085d"));
    }

    [Fact]
    public void Sanitize_replaces_unpaired_surrogates_one_code_unit_at_a_time_and_keeps_valid_pairs()
    {
        Assert.Equal("a�b", FtsDocumentBuilder.Sanitize("a\uD800b"));      // 孤立した上位
        Assert.Equal("a�b", FtsDocumentBuilder.Sanitize("a\uDC00b"));      // 孤立した下位
        Assert.Equal("ab�", FtsDocumentBuilder.Sanitize("ab\uD800"));      // 末尾の孤立した上位
        Assert.Equal("��", FtsDocumentBuilder.Sanitize("\uD800\uD800")); // 上位が続く
        Assert.Equal("𠮷野家", FtsDocumentBuilder.Sanitize("𠮷野家"));            // 正しい対は残す
        Assert.Equal("𠮷�", FtsDocumentBuilder.Sanitize("𠮷\uD800"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ふつうの文字列 ｶﾞﾁｬ 10連\n次の行\t")]
    [InlineData("𠮷野家 😀")]
    public void Sanitize_returns_the_same_string_when_there_is_nothing_to_replace(string s)
    {
        Assert.Same(s, FtsDocumentBuilder.Sanitize(s)); // 変更が無ければ新しい文字列を作らない
    }

    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("\uD800")]
    [InlineData("x\uDC00\uD800y\u0007")]
    [InlineData("ｶﾞ\u0001ﾁｬ\uD83D")]
    public void Sanitize_never_changes_the_length_and_the_result_is_always_acceptable_to_the_validator(string s)
    {
        string t = FtsDocumentBuilder.Sanitize(s);
        Assert.Equal(s.Length, t.Length);                           // 1 対 1 の置換: 元のテキスト上の位置(強調の範囲)は変わらない
        CharClassifier.Validate(t, nameof(t));                      // 例外にならない
        Assert.Equal(t, FtsDocumentBuilder.Sanitize(t));            // 2 回かけても同じ
        for (int i = 0; i < s.Length; i++)                          // 置換しなかった位置は元のまま
            Assert.True(t[i] == s[i] || t[i] == FtsDocumentBuilder.Replacement);
    }

    [Fact]
    public void Sanitize_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => FtsDocumentBuilder.Sanitize(null!));
    }

    // ---------------------------------------------------------------- 索引文書

    [Fact]
    public void Build_never_throws_for_control_characters_and_broken_surrogates_in_any_field()
    {
        FtsDocument doc = FtsDocumentBuilder.Build("a\u0000b\uD800", "d\u0001\uDC00", ["t\u0007", "\uD83D"]);
        Assert.DoesNotContain('\u0000', doc.NormText);
        Assert.DoesNotContain('\u0001', doc.NormText);
        Assert.Contains('�', doc.NormText);
        // 置換した文字は記号(区切り)なので、前後の英数字はそれぞれ単語になる(「a」と「b」が「ab」にならない)
        string[] tokens = doc.Tokens.Split(' ');
        Assert.Contains("a", tokens);
        Assert.Contains("b", tokens);
        Assert.DoesNotContain("ab", tokens);
    }

    [Fact]
    public void The_document_is_one_column_whose_tokens_never_cross_the_separators()
    {
        FtsDocument doc = FtsDocumentBuilder.Build("銀髪", "ロングヘア", ["銀髪", "ロングヘア"]);
        Assert.Equal("銀髪|ろんぐへあ|銀髪、ろんぐへあ", doc.NormText);
        string[] tokens = doc.Tokens.Split(' ');
        string pad(string t) => t.Replace(Tokenizer.StartPad.ToString(), "^").Replace(Tokenizer.EndPad.ToString(), "$");
        Assert.DoesNotContain(tokens, t => pad(t) == "髪ロ");      // 欄・タグの境目をまたぐ bigram は無い
        Assert.Contains("^銀", tokens.Select(pad));
        Assert.Contains("へあ", tokens.Select(pad));
        Assert.Equal(2, tokens.Select(pad).Count(t => t == "へあ")); // 説明文と tags の「ロングヘア」
    }

    [Fact]
    public void An_empty_caption_still_makes_a_document_with_only_separators()
    {
        FtsDocument doc = FtsDocumentBuilder.Build("", "", []);
        Assert.Equal("||", doc.NormText);
        Assert.Equal("", doc.Tokens);
    }

    [Fact]
    public void Build_applies_nfkc_and_lowercase_like_the_query_side_does()
    {
        FtsDocument doc = FtsDocumentBuilder.Build("ＨＰ ｶﾞﾁｬ", "", []);
        Assert.Equal("hp がちゃ||", doc.NormText);   // 半角カナ → ガチャ → ひらがな(ADR 0031)
    }

    [Fact]
    public void Bad_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => FtsDocumentBuilder.JoinTags(null!));
        Assert.Throws<ArgumentException>(() => FtsDocumentBuilder.JoinTags(["a", null!]));
    }
}
