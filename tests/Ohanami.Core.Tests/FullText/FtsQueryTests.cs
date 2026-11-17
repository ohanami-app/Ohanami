using System;
using System.Collections.Generic;
using System.Linq;
using Ohanami.Core.FullText;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// クエリの計画(QueryPlanner / SearchPlan)とハイライト。DB を使わない純粋な部分(一致の意味とハイライトの検査を、
/// 「基準の実装」の <see cref="SearchPlan.Matches"/> に向けて移した)。FTS5 / LIKE の結果との一致は FtsSqliteContractTests。
/// </summary>
public sealed class FtsQueryTests
{
    private const string Gacha = "ガチャ10連を引く";

    private static bool M(string doc, string query) => QueryPlanner.PlanSearch(query).Matches(NormalizedText.Create(doc));

    private static bool M1(string doc, string term) => QueryPlanner.Plan(term).Matches(NormalizedText.Create(doc));

    /// <summary>1 始まりの文書番号のうち、クエリ全体に一致するもの。</summary>
    private static List<int> Hits(string[] docs, string query) =>
        Enumerable.Range(0, docs.Length).Where(i => M(docs[i], query)).Select(i => i + 1).ToList();

    // ---------- 一致の基本 ----------

    [Fact]
    public void TwoCharQuery_Finds_水玉()
    {
        string[] docs = ["水玉をなでる", "玉ねぎ", "毛布"];
        Assert.Equal([1], Hits(docs, "水玉"));
        Assert.Equal([new TextRange(0, 2)], Highlighter.Find(docs[0], "水玉"));
    }

    [Fact]
    public void MixedJapaneseAndAlnum()
    {
        string[] docs = [Gacha, "HP回復した", "ガチャを回す", "MP回復", "HPが回復した"];
        Assert.Equal([1], Hits(docs, "ガチャ10連"));
        Assert.Equal([1], Hits(docs, "チャ10連"));           // クエリ側の「連」は1文字の run。文書側は「連を…」
        Assert.Equal([1], Hits(docs, "ャ10連を"));
        Assert.Empty(Hits(docs, "ガチャ10連続"));              // 連のあとが違う
        Assert.Empty(Hits(docs, "ガチャ1連"));                // 数字が違う
        Assert.Equal([1], Hits(docs, "10連"));
        Assert.Equal([2], Hits(docs, "HP回復"));             // 「HPが回復」(5)には当たらない
        Assert.Equal([2, 5], Hits(docs, "hp"));
        Assert.Equal([2, 4, 5], Hits(docs, "回復"));
        Assert.Equal([2, 5], Hits(docs, "回復した"));
        Assert.Empty(Hits(docs, "P回復"));                  // 英語は単語単位。「MP」「HP」の一部の P には当たらない
    }

    [Fact]
    public void FullWidthAndHalfWidthMixed()
    {
        string[] docs = ["HPが回復した", "ＨＰ全回復", "ガチャの結果", "ｶﾞﾁｬの結果", "ガチャ10連"];
        Assert.Equal([1, 2], Hits(docs, "ＨＰ"));
        Assert.Equal([1, 2], Hits(docs, "HP"));
        Assert.Equal([3, 4, 5], Hits(docs, "ｶﾞﾁｬ"));
        Assert.Equal([3, 4, 5], Hits(docs, "ガチャ"));
        Assert.Equal([5], Hits(docs, "ｶﾞﾁｬ１０連"));
    }

    [Fact]
    public void PhraseAcrossWrappedNewline_IsFound()
    {
        string doc = "この帽子に\nもポップコーンの効果があるのだろうか";
        Assert.True(M(doc, "帽子にも"));
        Assert.Equal([new TextRange(2, 5)], Highlighter.Find(doc, "帽子にも"));   // 衣(2) 装 に \n も(6) → 長さ5(改行を含む)
        Assert.True(M(doc, "にもポップ"));
        Assert.True(M(doc, "帽子に\nもポップ"));                                 // クエリ側に改行があっても同じ
    }

    [Fact]
    public void Unvoiced_DoesNotFindVoiced()
    {
        string[] docs = ["ガチャを回す", "バナナ", "パン"];
        Assert.Empty(Hits(docs, "カチャ"));
        Assert.Empty(Hits(docs, "ハナナ"));
        Assert.Empty(Hits(docs, "ハン"));
        Assert.Empty(Hits(docs, "バン"));
        Assert.Equal([3], Hits(docs, "パン"));
    }

    [Fact]
    public void BigramOrderMatters()
    {
        string[] docs = ["ガチャを回す", "水玉をなでる", "あいうえ"];
        Assert.Empty(Hits(docs, "ャチガ"));   // 逆順
        Assert.Empty(Hits(docs, "玉毛"));
        Assert.Empty(Hits(docs, "チャガ"));   // 各 bigram(チャ, ャガ)のうち ャガ が無い
        Assert.Empty(Hits(docs, "あえ"));     // 飛び飛びの文字
        Assert.Empty(Hits(docs, "いあう"));
        Assert.Equal([1], Hits(docs, "チャを回"));
    }

    [Fact]
    public void BigramsOfDifferentRuns_AreNotGlued_ByPunctuation()
    {
        // run の境界記号が無いと、「はい。いいえ」の索引(はい いい いえ)に「はいい」(はい いい)が偽陽性で当たる
        string[] docs = ["はい。いいえ", "はいいいえ"];
        Assert.Equal([2], Hits(docs, "はいい"));
        Assert.Equal([1, 2], Hits(docs, "いいえ"));
        Assert.Equal([1], Hits(docs, "はい。いいえ"));   // 句読点ごと打てば当たる
    }

    [Fact]
    public void PrivateUseChar_InTheText_CannotFakeTheBoundaryToken()
    {
        string[] docs = ["あい", "あい"];
        Assert.Equal([2], Hits(docs, "あい"));
    }

    // ---------- 1 文字のクエリと、語の種類 ----------

    [Fact]
    public void SingleCharQuery_FindsSubstrings()
    {
        string[] docs = ["水玉をなでる", "玉ねぎ", "毛布", "ガチャ"];
        Assert.Equal([1, 2], Hits(docs, "玉"));
        Assert.Equal([new TextRange(1, 1)], Highlighter.Find(docs[0], "玉"));
        Assert.Equal([3], Hits(docs, "布"));
        Assert.Equal([4], Hits(docs, "ｶﾞ"));              // 半角の1文字は NFKC 後の1文字(ｶﾞ → ガ)で引く
        // 英数字の1文字も部分一致
        Assert.Equal([1, 2], Hits(["HPが回復した", "MP回復", "ガチャ"], "p"));
    }

    [Fact]
    public void PlanKinds_OneCharIsSingleChar_AndEverythingElseIsAPhrase()
    {
        Assert.Equal(QueryKind.SingleChar, QueryPlanner.Plan("毛").Kind);
        Assert.Equal(QueryKind.SingleChar, QueryPlanner.Plan("ｶﾞ").Kind);
        Assert.Equal(QueryKind.SingleChar, QueryPlanner.Plan("a").Kind);
        Assert.Equal(QueryKind.SingleChar, QueryPlanner.Plan("「毛」").Kind);   // 括弧は区切り
        Assert.Equal(QueryKind.Phrase, QueryPlanner.Plan("水玉").Kind);
        Assert.Equal(QueryKind.Phrase, QueryPlanner.Plan("10").Kind);          // 2文字の単語は FTS
        Assert.Equal(QueryKind.Phrase, QueryPlanner.Plan("a b").Kind);
        Assert.Equal(QueryKind.Phrase, QueryPlanner.Plan("毛。玉").Kind);
        Assert.Equal("\"水玉\"", QueryPlanner.Plan("水玉").FtsMatch);
        Assert.Null(QueryPlanner.Plan("毛").FtsMatch);
        Assert.Equal("毛", QueryPlanner.Plan("毛").SingleChar);
    }

    [Fact]
    public void EscapeLike_EscapesSpecials()
    {
        Assert.Equal("a\\%b\\_c\\\\d", QueryPlanner.EscapeLike("a%b_c\\d"));
        Assert.Equal("%\\%%", SearchPlan.LikePattern("%"));
        Assert.Equal("%森%", SearchPlan.LikePattern("森"));
    }

    // ---------- 検索の文法: 空白区切りの AND ----------

    [Fact]
    public void Whitespace_SplitsTheQueryIntoTerms_ThatAreAnded()
    {
        string[] docs = ["銀髪の少女と猫がいる", "銀髪の少女", "猫だけがいる", "金髪の少女と犬"];
        Assert.Equal([1], Hits(docs, "銀髪 猫"));            // 両方ある文書だけ
        Assert.Equal([1, 2], Hits(docs, "銀髪 少女"));
        Assert.Empty(Hits(docs, "銀髪 犬"));
        Assert.Equal([1, 2], Hits(docs, "銀髪　少女"));       // 全角空白
        Assert.Equal([1, 2], Hits(docs, "銀髪\t少女"));
        Assert.Equal([1, 2], Hits(docs, "  銀髪   少女 \n"));
        Assert.Equal(2, QueryPlanner.PlanSearch("銀髪　少女").Terms.Count);
    }

    [Fact]
    public void ASpaceBetweenTerms_IsAnAnd_NotAPhrase()
    {
        // 評価のコードでは Search("near or and") は 1 つのフレーズ(near or and が連続)だったが、製品の文法(SearchAll と同じ)では空白は AND。順序・連続を問わない
        string[] docs = ["and near or", "near or and", "near"];
        Assert.Equal([1, 2], Hits(docs, "near or and"));
        Assert.True(M1(docs[1], "near or and"));    // 1 語としての Plan(空白を区切りとするフレーズ)は連続を要求する
        Assert.False(M1(docs[0], "near or and"));
        Assert.Equal([1], Hits(["水玉 OR near", "水玉"], "水玉 OR"));            // 評価のコードでは「水玉 OR」はフレーズで、どちらにも当たらなかった
    }

    [Fact]
    public void SingleCharTerms_FilterWithoutNeedingAPhrase()
    {
        string[] docs = ["森の中の洞窟", "洞窟の中", "森と洞窟と森"];
        Assert.Equal([1, 3], Hits(docs, "洞窟 森"));
        Assert.Equal([1, 2], Hits(docs, "洞窟 中"));
        SearchPlan p = QueryPlanner.PlanSearch("洞窟 森");
        Assert.Single(p.Phrases);
        Assert.Equal(["森"], p.SingleChars);
        Assert.Equal("\"洞窟\"", p.FtsMatch);
        Assert.False(p.RankedByOccurrences);
    }

    [Fact]
    public void FtsMatch_AndsTheQuotedPhrases_AndIsNullWhenEveryTermIsOneChar()
    {
        Assert.Equal("\"銀髪\" AND \"ろん んぐ ぐへ へあ\"", QueryPlanner.PlanSearch("銀髪 ロングヘア").FtsMatch);   // 語ごとに bigram のフレーズ
        SearchPlan singles = QueryPlanner.PlanSearch("森 と");
        Assert.Null(singles.FtsMatch);
        Assert.True(singles.RankedByOccurrences);
        Assert.Equal(["森", "と"], singles.SingleChars);
    }

    [Fact]
    public void OccurrenceCount_IsTheSumOfTheOneCharTermsOccurrences_NotDividedByLength()
    {
        SearchPlan p = QueryPlanner.PlanSearch("森");
        Assert.Equal(1, p.CountOccurrences(NormalizedText.Create("森")));
        Assert.Equal(3, p.CountOccurrences(NormalizedText.Create("森と森と森")));
        Assert.Equal(0, p.CountOccurrences(NormalizedText.Create("花")));
        SearchPlan two = QueryPlanner.PlanSearch("森 と");
        Assert.Equal(5, two.CountOccurrences(NormalizedText.Create("森と森と森")));   // 森3 + と2
        Assert.Equal(3, two.CountOccurrences(NormalizedText.Create("森と森")));       // 森2 + と1
        // 半角は NFKC 後の文字で数える。サロゲートペアの文字も1回
        // ガ・が・ｶﾞ は、どれも が になる(ひらがなとカタカナの同一視。ADR 0031)
        Assert.Equal(3, QueryPlanner.PlanSearch("ｶﾞ").CountOccurrences(NormalizedText.Create("ガがｶﾞ")));
        Assert.Equal(2, QueryPlanner.PlanSearch("ｶﾞ").CountOccurrences(NormalizedText.Create("ガかｶﾞ")));   // か(濁点なし)は別
        Assert.Equal(2, QueryPlanner.PlanSearch("𠮷").CountOccurrences(NormalizedText.Create("𠮷野𠮷")));
    }

    [Fact]
    public void AllTermsMustMatch_ForTheWholeQueryToMatch()
    {
        SearchPlan p = QueryPlanner.PlanSearch("森 洞窟");
        Assert.True(p.Matches(NormalizedText.Create("森の洞窟")));
        Assert.False(p.Matches(NormalizedText.Create("森だけ")));
        Assert.False(p.Matches(NormalizedText.Create("洞窟だけ")));
    }

    [Fact]
    public void DuplicateTerms_AreKept_AndCountedTwiceInTheOccurrenceRank()
    {
        SearchPlan p = QueryPlanner.PlanSearch("森 森");
        Assert.Equal(2, p.Terms.Count);
        Assert.Equal(4, p.CountOccurrences(NormalizedText.Create("森と森")));
    }

    // ---------- 想定外の入力 ----------

    [Fact]
    public void Plan_RejectsNull_Control_Empty_AndPunctuationOnlyQueries()
    {
        Assert.Throws<ArgumentNullException>(() => QueryPlanner.PlanSearch(null!));
        Assert.Throws<ArgumentNullException>(() => QueryPlanner.Plan(null!));
        Assert.Throws<ArgumentException>(() => QueryPlanner.PlanSearch("a\u0001b"));
        Assert.Throws<ArgumentException>(() => QueryPlanner.PlanSearch(""));
        Assert.Throws<ArgumentException>(() => QueryPlanner.PlanSearch("  \n"));
        Assert.Throws<ArgumentException>(() => QueryPlanner.PlanSearch("\uD800"));
        var e = Assert.Throws<ArgumentException>(() => QueryPlanner.PlanSearch("「」。！？"));
        Assert.Contains("検索できる文字", e.Message);
    }

    [Fact]
    public void ATermWithoutSearchableChars_RejectsTheWholeQuery_InsteadOfBeingDropped()
    {
        // 語を黙って落とすと、利用者が打ったものと違う検索になる。評価のコードの SearchAll と同じく例外にする(UI がどう見せるかは App 側の判断)
        Assert.Throws<ArgumentException>(() => QueryPlanner.PlanSearch("猫 。"));
        Assert.Throws<ArgumentException>(() => QueryPlanner.PlanSearch("猫 ！？ 犬"));
    }

    [Fact]
    public void QueryWithFtsSyntaxChars_IsSafe()
    {
        // FTS5 の演算子や引用符・ワイルドカードは区切り扱いになり、構文として解釈されない。MATCH 式はトークンを引用符で囲んだものだけ
        string[] docs = ["水玉 NEAR OR AND \"quote\" *star", "ふつう"];
        Assert.Equal([1], Hits(docs, "\"quote\""));
        Assert.Equal([1], Hits(docs, "*star"));
        Assert.Equal([1], Hits(docs, "水玉 NEAR"));
        Assert.Equal("\"quote\"", QueryPlanner.Plan("\"quote\"").FtsMatch);
        Assert.Equal("\"near\" AND \"or\" AND \"and\"", QueryPlanner.PlanSearch("NEAR OR AND").FtsMatch);
        Assert.DoesNotContain("*", QueryPlanner.Plan("*star").FtsMatch);
    }

    // ---------- ハイライト ----------

    [Fact]
    public void Highlight_HalfWidthKana_RangeCoversBothOriginalChars()
    {
        // ｶﾞ(2文字)→ガ。「ｶﾞﾁｬ」(4文字)
        Assert.Equal([new TextRange(0, 4)], Highlighter.Find("ｶﾞﾁｬ10連", "ガチャ"));
        Assert.Equal([new TextRange(0, 3)], Highlighter.Find("ｶﾞﾁｬ10連", "ガチ"));     // ｶﾞ(2文字) + ﾁ
        Assert.Equal([new TextRange(2, 2)], Highlighter.Find("ｶﾞﾁｬ10連", "チャ"));
        Assert.Equal([new TextRange(0, 2)], Highlighter.Find("ｶﾞﾁｬ10連", "ｶﾞ"));      // 1文字クエリ
        Assert.Equal([new TextRange(0, 7)], Highlighter.Find("ｶﾞﾁｬ10連", "ｶﾞﾁｬ10連"));
    }

    [Fact]
    public void Highlight_FindsTheOtherKanaScript_AndTheRangeIsInTheOriginalText()
    {
        // ひらがなで打てばカタカナの語に、カタカナで打てばひらがなの語に当たる。範囲は書かれたままの文字列の上
        Assert.Equal([new TextRange(1, 6)], Highlighter.Find("「ホルテバルド」", "ほるてばるど"));
        Assert.Equal([new TextRange(1, 6)], Highlighter.Find("「ほるてばるど」", "ホルテバルド"));
        Assert.Equal([new TextRange(1, 3)], Highlighter.Find("あホルテバルド", "ほるて"));        // 語の途中から
        Assert.Equal([new TextRange(0, 8)], Highlighter.Find("ﾎﾙﾃﾊﾞﾙﾄﾞ", "ほるてばるど"));       // 半角 8 文字(濁点は別の文字)
        Assert.Equal([new TextRange(0, 1), new TextRange(2, 1)], Highlighter.Find("カ か", "か"));   // 1 文字クエリは、両方の書き方に当たる
        Assert.Empty(Highlighter.Find("ホルテバルト", "ほるてばるど"));                          // 濁点の有無は別
        SearchPlan plan = QueryPlanner.PlanSearch("ほるてばるど 景色");
        Assert.Equal([new TextRange(0, 6), new TextRange(7, 2)], Highlighter.Find("ホルテバルド 景色", plan));
    }

    [Fact]
    public void A_term_matches_a_document_written_in_the_other_kana_script_in_both_directions()
    {
        Assert.True(M("ホルテバルドの風景", "ほるてばるど"));
        Assert.True(M("ほるてばるどの風景", "ホルテバルド"));
        Assert.True(M("ﾎﾙﾃﾊﾞﾙﾄﾞの風景", "ほるてばるど"));
        Assert.False(M("ホルテバルトの風景", "ほるてばるど"));      // 濁点の有無は区別したまま
        Assert.True(M("ガチャを回す", "がちゃ"));
        Assert.False(M("ガチャを回す", "かちゃ"));
    }

    [Fact]
    public void Highlight_ExpandedChars_AreMappedToTheWholeOriginalChar()
    {
        // ㈱ は1文字だが、NFKC で「(株)」の3文字になる
        Assert.Equal([new TextRange(0, 1)], Highlighter.Find("㈱ガチャ", "株"));
        Assert.Equal([new TextRange(1, 3)], Highlighter.Find("㈱ガチャ", "ガチャ"));
        Assert.Equal([new TextRange(0, 1)], Highlighter.Find("㈱ガチャ", "(株)"));      // クエリ側も同じ正規化
        // 展開された文字をまたぐ範囲: A ㈱ B ㈱ C の「株 B 株」→ 元の2番目の㈱の末尾まで(位置1から長さ3)
        Assert.Equal([new TextRange(1, 3)], Highlighter.Find("A㈱B㈱C", "株B株"));
    }

    [Fact]
    public void Highlight_RangeCrossingANewline_IncludesTheNewline()
    {
        string t = "この帽子に\nもポップコーン";
        Assert.Equal([new TextRange(2, 5)], Highlighter.Find(t, "帽子にも"));
        Assert.Equal("帽子に\nも", t.Substring(2, 5));
        // 半角カナ + 改行 + 長さが変わる文字の組み合わせ
        string h = "ﾎﾟｯﾌﾟ\nｺｰﾝ";
        Assert.Equal([new TextRange(0, 9)], Highlighter.Find(h, "ポップコーン"));
        Assert.Equal("ﾎﾟｯﾌﾟ\nｺｰﾝ", h.Substring(0, 9));
        Assert.Equal([new TextRange(3, 4)], Highlighter.Find(h, "プコ"));              // ﾌ(3) ﾟ(4) \n(5) ｺ(6)
    }

    [Fact]
    public void Highlight_MultipleAndOverlappingMatches()
    {
        Assert.Equal([new TextRange(0, 3), new TextRange(4, 3)], Highlighter.Find("ガチャ ガチャ", "ガチャ"));
        Assert.Equal([new TextRange(0, 3)], Highlighter.Find("あああ", "ああ"));         // 重なる (0,2) と (1,2) は1つに
        Assert.Equal([new TextRange(4, 2)], Highlighter.Find("Use HP potion", "ｈｐ"));
    }

    [Fact]
    public void Highlight_SurrogatePair_UsesUtf16Positions()
    {
        // 𠮷(U+20BB7)は UTF-16 で2文字
        Assert.Equal([new TextRange(0, 3)], Highlighter.Find("𠮷野家", "𠮷野"));
        Assert.Equal([new TextRange(2, 2)], Highlighter.Find("𠮷野家", "野家"));
    }

    [Fact]
    public void Highlight_CombiningMarks_AreKeptTogetherWithTheBase()
    {
        // か + 結合濁点(2文字)→ が。「が」「ち」「ゃ」= 元の4文字全体
        Assert.Equal([new TextRange(0, 4)], Highlighter.Find("がちゃ".Normalize(System.Text.NormalizationForm.FormD), "がちゃ"));
        Assert.Equal([new TextRange(0, 3)], Highlighter.Find("がちゃ".Normalize(System.Text.NormalizationForm.FormD), "がち"));
    }

    [Fact]
    public void Highlight_NoMatch_IsEmpty()
    {
        Assert.Empty(Highlighter.Find("水玉をなでる", "ガチャ"));
        Assert.Empty(Highlighter.Find("", "ガチャ"));
        Assert.Empty(Highlighter.Find("はい。いいえ", "はいい"));
    }

    [Fact]
    public void Highlight_OfAWholeQuery_UnionsTheTermsRanges()
    {
        string doc = "銀髪の少女と猫";
        SearchPlan plan = QueryPlanner.PlanSearch("銀髪 猫");
        Assert.Equal([new TextRange(0, 2), new TextRange(6, 1)], Highlighter.Find(doc, plan));
        // 重なる範囲は1つにまとまり、一致しなかった語の範囲は無い
        Assert.Equal([new TextRange(0, 3)], Highlighter.Find("あああ", QueryPlanner.PlanSearch("ああ あ")));
        Assert.Equal([new TextRange(0, 2)], Highlighter.Find(doc, QueryPlanner.PlanSearch("銀髪 犬")));
    }

    // ---------- 基準の実装どうしの整合(乱数。シード固定) ----------

    [Fact]
    public void Matches_And_Highlighter_AgreeOnRandomText()
    {
        string[] alphabet =
        [
            "あ", "い", "う", "え", "ガ", "ｶﾞ", "カ", "ー", "。", " ", "\n", "a", "b", "1", "ﾊﾟ", "㈱", "「", "」", "々", "𠮷",
        ];
        var rnd = new Random(20261105);
        string Gen(int min, int max) => string.Concat(Enumerable.Range(0, rnd.Next(min, max + 1)).Select(_ => alphabet[rnd.Next(alphabet.Length)]));

        var docs = Enumerable.Range(0, 300).Select(_ => Gen(1, 14)).ToList();
        int checkedQueries = 0, withHits = 0;
        for (int q = 0; q < 600; q++)
        {
            string query = Gen(1, 5);
            QueryPlan plan;
            try { plan = QueryPlanner.Plan(query); }
            catch (ArgumentException) { continue; }   // 検索できる文字がないクエリ
            int hits = 0;
            foreach (string doc in docs)
            {
                bool matches = plan.Matches(NormalizedText.Create(doc));
                bool highlighted = Highlighter.Find(doc, query).Count > 0;
                Assert.True(matches == highlighted, $"不整合: query={Escape(query)} doc={Escape(doc)} matches={matches} highlight={highlighted}");
                if (matches) hits++;
            }
            checkedQueries++;
            if (hits > 0) withHits++;
        }
        Assert.True(withHits > 50, $"ヒットするクエリが少なすぎて、整合の検査にならない({withHits}/{checkedQueries})");
    }

    internal static string Escape(string s) => string.Concat(s.Select(c => c < 0x20 || c > 0x7e ? $"\\u{(int)c:X4}" : c.ToString()));
}
