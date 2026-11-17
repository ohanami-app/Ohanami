using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Xunit;

namespace S3Fts.Tests;

public sealed class FtsIndexTests(ITestOutputHelper output)
{
    private const string Gacha = "ガチャ10連を引く";

    private static FtsIndex NewIndex(params string[] docs)
    {
        var idx = new FtsIndex(":memory:");
        foreach (string d in docs) idx.Add(d);
        return idx;
    }

    private static List<long> Ids(IReadOnlyList<SearchHit> hits) => hits.Select(h => h.Id).ToList();

    // ---------- FTS5 の有無 ----------

    [Fact]
    public void Fts5_IsAvailable_InBundledSqlite()
    {
        using var idx = new FtsIndex(":memory:"); // 使えなければコンストラクタが InvalidOperationException
        string d = idx.DescribeSqlite();
        output.WriteLine(d);
        Assert.Contains("ENABLE_FTS5", d);
    }

    // ---------- unicode61 の remove_diacritics ----------

    /// <summary>
    /// remove_diacritics の 0 / 1 / 2 のそれぞれで、仮名の濁点・半濁点が同一視されないことを実測する。
    /// 同一視される設定があれば、このテストが失敗する(そのときは設定で切る)。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Unicode61_DoesNotConflateKanaVoicing(int removeDiacritics)
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        Exec(conn, $"CREATE VIRTUAL TABLE t USING fts5(x, tokenize = 'unicode61 remove_diacritics {removeDiacritics}')");
        // 清音・濁音・半濁音の3つ組を1つずつ別の行に入れる
        string[][] triples =
        [
            ["か", "が"], ["き", "ぎ"], ["さ", "ざ"], ["た", "だ"], ["は", "ば", "ぱ"], ["ひ", "び", "ぴ"], ["ふ", "ぶ", "ぷ"],
            ["カ", "ガ"], ["ハ", "バ", "パ"], ["フ", "ブ", "プ"], ["ウ", "ヴ"],
        ];
        var all = triples.SelectMany(t => t).ToList();
        for (int i = 0; i < all.Count; i++) Exec(conn, $"INSERT INTO t(rowid, x) VALUES ({i + 1}, '{all[i]}')");
        for (int i = 0; i < all.Count; i++)
        {
            var hits = ColumnLongs(conn, $"SELECT rowid FROM t WHERE t MATCH '\"{all[i]}\"'");
            Assert.Equal(new[] { (long)(i + 1) }, hits); // 自分自身だけに当たる
        }
        // 参考: ラテン文字の発音区別符号(設定による違い)
        Exec(conn, "INSERT INTO t(rowid, x) VALUES (1000, 'pokémon')");
        bool latinFolded = ColumnLongs(conn, "SELECT rowid FROM t WHERE t MATCH '\"pokemon\"'").Contains(1000);
        output.WriteLine($"remove_diacritics={removeDiacritics}: 仮名の濁点は区別される / 'pokemon' で 'pokémon' に当たるか = {latinFolded}");
    }

    // ---------- 検索の基本 ----------

    [Fact]
    public void TwoCharQuery_Finds_毛玉()
    {
        using var idx = NewIndex("毛玉をなでる", "玉ねぎ", "毛布");
        var hits = idx.Search("毛玉");
        Assert.Equal(new long[] { 1 }, Ids(hits));
        Assert.Equal(SearchPath.Fts, hits[0].Path);
        Assert.Equal(new[] { new TextRange(0, 2) }, hits[0].Ranges);
    }

    [Fact]
    public void MixedJapaneseAndAlnum()
    {
        using var idx = NewIndex(Gacha, "HP回復した", "ガチャを回す", "MP回復", "HPが回復した");
        Assert.Equal(new long[] { 1 }, Ids(idx.Search("ガチャ10連")));
        Assert.Equal(new long[] { 1 }, Ids(idx.Search("チャ10連"))); // クエリ側の「連」は1文字の run。文書側は「連を…」
        Assert.Equal(new long[] { 1 }, Ids(idx.Search("ャ10連を")));
        Assert.Empty(idx.Search("ガチャ10連続")); // 連のあとが違う
        Assert.Empty(idx.Search("ガチャ1連")); // 数字が違う
        Assert.Equal(new long[] { 1 }, Ids(idx.Search("10連")));
        Assert.Equal(new long[] { 2 }, Ids(idx.Search("HP回復"))); // 「HPが回復」(5)には当たらない
        Assert.Equal(new long[] { 2, 5 }, Ids(idx.Search("hp")).OrderBy(x => x).ToList());
        Assert.Equal(new long[] { 2, 4, 5 }, Ids(idx.Search("回復")).OrderBy(x => x).ToList());
        Assert.Equal(new long[] { 2, 5 }, Ids(idx.Search("回復した")).OrderBy(x => x).ToList());
        Assert.Empty(idx.Search("P回復")); // 英語は単語単位。「MP」「HP」の一部の P には当たらない(部分一致が要るなら1文字 LIKE か別途)
    }

    [Fact]
    public void FullWidthAndHalfWidthMixed()
    {
        using var idx = NewIndex("HPが回復した", "ＨＰ全回復", "ガチャの結果", "ｶﾞﾁｬの結果", "ガチャ10連");
        Assert.Equal(new long[] { 1, 2 }, Ids(idx.Search("ＨＰ")).OrderBy(x => x).ToList());
        Assert.Equal(new long[] { 1, 2 }, Ids(idx.Search("HP")).OrderBy(x => x).ToList());
        Assert.Equal(new long[] { 3, 4, 5 }, Ids(idx.Search("ｶﾞﾁｬ")).OrderBy(x => x).ToList());
        Assert.Equal(new long[] { 3, 4, 5 }, Ids(idx.Search("ガチャ")).OrderBy(x => x).ToList());
        Assert.Equal(new long[] { 5 }, Ids(idx.Search("ｶﾞﾁｬ１０連")));
    }

    [Fact]
    public void PhraseAcrossWrappedNewline_IsFound()
    {
        using var idx = NewIndex("この指輪で\nもスピードアップの効果があるのでしょうか");
        var hits = idx.Search("指輪でも");
        Assert.Single(hits);
        Assert.Equal(new[] { new TextRange(2, 5) }, hits[0].Ranges); // 指(2) 輪 で \n も(6) → 長さ5(改行を含む)
        Assert.Single(idx.Search("でもスピード"));
        Assert.Single(idx.Search("指輪で\nもスピード")); // クエリ側に改行があっても同じ
    }

    // ---------- 引けてはいけないもの ----------

    [Fact]
    public void Unvoiced_DoesNotFindVoiced()
    {
        using var idx = NewIndex("ガチャを回す", "バナナ", "パン");
        Assert.Empty(idx.Search("カチャ"));
        Assert.Empty(idx.Search("ハナナ"));
        Assert.Empty(idx.Search("ハン"));
        Assert.Empty(idx.Search("バン"));
        Assert.Single(idx.Search("パン"));
    }

    [Fact]
    public void BigramOrderMatters()
    {
        using var idx = NewIndex("ガチャを回す", "毛玉をなでる", "あいうえ");
        Assert.Empty(idx.Search("ャチガ")); // 逆順
        Assert.Empty(idx.Search("玉毛"));
        Assert.Empty(idx.Search("チャガ")); // 各 bigram(チャ, ャガ)のうち ャガ が無い
        Assert.Empty(idx.Search("あえ")); // 飛び飛びの文字
        Assert.Empty(idx.Search("いあう"));
        Assert.Single(idx.Search("チャを回"));
    }

    [Fact]
    public void BigramsOfDifferentRuns_AreNotGlued_ByPunctuation()
    {
        // run の境界記号が無いと、「はい。いいえ」の索引(はい いい いえ)に「はいい」(はい いい)が偽陽性で当たる
        using var idx = NewIndex("はい。いいえ", "はいいいえ");
        var hits = idx.Search("はいい");
        Assert.Equal(new long[] { 2 }, Ids(hits));
        Assert.Equal(new long[] { 1, 2 }, Ids(idx.Search("いいえ")).OrderBy(x => x).ToList());
        Assert.Equal(new long[] { 1 }, Ids(idx.Search("はい。いいえ"))); // 句読点ごと打てば当たる
    }

    [Fact]
    public void PrivateUseChar_InTheText_CannotFakeTheBoundaryToken()
    {
        using var idx = NewIndex("あい", "あい");
        Assert.Equal(new long[] { 2 }, Ids(idx.Search("あい")));
    }

    // ---------- 1文字のクエリ ----------

    [Fact]
    public void SingleCharQuery_GoesToLike_AndFindsSubstrings()
    {
        using var idx = NewIndex("毛玉をなでる", "玉ねぎ", "毛布", "ガチャ");
        var hits = idx.Search("玉");
        Assert.All(hits, h => Assert.Equal(SearchPath.Like, h.Path));
        Assert.Equal(new long[] { 1, 2 }, Ids(hits));
        Assert.Equal(new[] { new TextRange(1, 1) }, hits[0].Ranges);
        Assert.Equal(new long[] { 3 }, Ids(idx.Search("布")));
        // 半角の1文字は NFKC 後の1文字(ｶﾞ → ガ)で引く
        Assert.Equal(new long[] { 4 }, Ids(idx.Search("ｶﾞ")));
        // 英数字の1文字も LIKE(部分一致)
        using var idx2 = NewIndex("HPが回復した", "MP回復", "ガチャ");
        var h2 = idx2.Search("p");
        Assert.All(h2, h => Assert.Equal(SearchPath.Like, h.Path));
        Assert.Equal(new long[] { 1, 2 }, Ids(h2));
    }

    [Fact]
    public void TwoTokenOrLongerQuery_NeverUsesLike()
    {
        using var idx = NewIndex("毛玉をなでる");
        Assert.All(idx.Search("毛玉"), h => Assert.Equal(SearchPath.Fts, h.Path));
        Assert.All(idx.Search("10"), h => Assert.Equal(SearchPath.Fts, h.Path)); // 2文字の単語は FTS
        Assert.Equal(QueryKind.SingleChar, QueryPlanner.Plan("毛").Kind);
        Assert.Equal(QueryKind.SingleChar, QueryPlanner.Plan("ｶﾞ").Kind);
        Assert.Equal(QueryKind.SingleChar, QueryPlanner.Plan("a").Kind);
        Assert.Equal(QueryKind.SingleChar, QueryPlanner.Plan("「毛」").Kind); // 括弧は区切り
        Assert.Equal(QueryKind.Phrase, QueryPlanner.Plan("毛玉").Kind);
        Assert.Equal(QueryKind.Phrase, QueryPlanner.Plan("a b").Kind);
        Assert.Equal(QueryKind.Phrase, QueryPlanner.Plan("毛。玉").Kind);
    }

    [Fact]
    public void EscapeLike_EscapesSpecials()
    {
        Assert.Equal("a\\%b\\_c\\\\d", FtsIndex.EscapeLike("a%b_c\\d"));
    }

    // ---------- ハイライト ----------

    [Fact]
    public void Highlight_HalfWidthKana_RangeCoversBothOriginalChars()
    {
        // ｶﾞ(2文字)→ガ。「ｶﾞﾁｬ」(4文字)
        Assert.Equal(new[] { new TextRange(0, 4) }, Highlighter.Find("ｶﾞﾁｬ10連", "ガチャ"));
        Assert.Equal(new[] { new TextRange(0, 3) }, Highlighter.Find("ｶﾞﾁｬ10連", "ガチ")); // ｶﾞ(2文字) + ﾁ
        Assert.Equal(new[] { new TextRange(2, 2) }, Highlighter.Find("ｶﾞﾁｬ10連", "チャ"));
        Assert.Equal(new[] { new TextRange(0, 2) }, Highlighter.Find("ｶﾞﾁｬ10連", "ｶﾞ")); // 1文字クエリ
        Assert.Equal(new[] { new TextRange(0, 7) }, Highlighter.Find("ｶﾞﾁｬ10連", "ｶﾞﾁｬ10連"));
    }

    [Fact]
    public void Highlight_ExpandedChars_AreMappedToTheWholeOriginalChar()
    {
        // ㈱ は1文字だが、NFKC で「(株)」の3文字になる
        Assert.Equal(new[] { new TextRange(0, 1) }, Highlighter.Find("㈱ガチャ", "株"));
        Assert.Equal(new[] { new TextRange(1, 3) }, Highlighter.Find("㈱ガチャ", "ガチャ"));
        Assert.Equal(new[] { new TextRange(0, 1) }, Highlighter.Find("㈱ガチャ", "(株)")); // クエリ側も同じ正規化
        // 展開された文字をまたぐ範囲: A ㈱ B ㈱ C の「株 B 株」→ 元の2番目の㈱の末尾まで(位置1から長さ3)
        Assert.Equal(new[] { new TextRange(1, 3) }, Highlighter.Find("A㈱B㈱C", "株B株"));
    }

    [Fact]
    public void Highlight_RangeCrossingANewline_IncludesTheNewline()
    {
        string t = "この指輪で\nもスピードアップ";
        Assert.Equal(new[] { new TextRange(2, 5) }, Highlighter.Find(t, "指輪でも"));
        Assert.Equal("指輪で\nも", t.Substring(2, 5));
        // 半角カナ + 改行 + 長さが変わる文字の組み合わせ
        string h = "ﾊﾟﾜｰ\nｱｯﾌﾟ";
        Assert.Equal(new[] { new TextRange(0, 9) }, Highlighter.Find(h, "パワーアップ"));
        Assert.Equal("ﾊﾟﾜｰ\nｱｯﾌﾟ", h.Substring(0, 9));
        Assert.Equal(new[] { new TextRange(3, 3) }, Highlighter.Find(h, "ーア")); // ｰ(3) \n(4) ｱ(5)
    }

    [Fact]
    public void Highlight_MultipleAndOverlappingMatches()
    {
        Assert.Equal(new[] { new TextRange(0, 3), new TextRange(4, 3) }, Highlighter.Find("ガチャ ガチャ", "ガチャ"));
        Assert.Equal(new[] { new TextRange(0, 3) }, Highlighter.Find("あああ", "ああ")); // 重なる (0,2) と (1,2) は1つに
        Assert.Equal(new[] { new TextRange(4, 2) }, Highlighter.Find("Use HP potion", "ｈｐ"));
    }

    [Fact]
    public void Highlight_SurrogatePair_UsesUtf16Positions()
    {
        // 𠮷(U+20BB7)は UTF-16 で2文字
        Assert.Equal(new[] { new TextRange(0, 3) }, Highlighter.Find("𠮷野家", "𠮷野"));
        Assert.Equal(new[] { new TextRange(2, 2) }, Highlighter.Find("𠮷野家", "野家"));
    }

    [Fact]
    public void Highlight_CombiningMarks_AreKeptTogetherWithTheBase()
    {
        // か + 結合濁点(2文字)→ が。「が」「ち」「ゃ」= 元の4文字全体
        Assert.Equal(new[] { new TextRange(0, 4) }, Highlighter.Find("がちゃ", "がちゃ"));
        Assert.Equal(new[] { new TextRange(0, 3) }, Highlighter.Find("がちゃ", "がち"));
    }

    [Fact]
    public void Highlight_NoMatch_IsEmpty()
    {
        Assert.Empty(Highlighter.Find("毛玉をなでる", "ガチャ"));
        Assert.Empty(Highlighter.Find("", "ガチャ"));
        Assert.Empty(Highlighter.Find("はい。いいえ", "はいい"));
    }

    // ---------- 想定外の入力 ----------

    [Fact]
    public void Add_RejectsNull_Control_AndBrokenSurrogates()
    {
        using var idx = NewIndex();
        Assert.Throws<ArgumentNullException>(() => idx.Add(null!));
        Assert.Throws<ArgumentException>(() => idx.Add("a\u0000b"));
        Assert.Throws<ArgumentException>(() => idx.Add("a\u001Fb"));
        Assert.Throws<ArgumentException>(() => idx.Add("\uD800"));
        idx.Add("a\tb\r\nc"); // タブと改行は OK
        Assert.Single(idx.QueryStrings("SELECT text FROM docs")); // 失敗した Add の行は残っていない(最後の1行だけ)
    }

    [Fact]
    public void Search_RejectsNull_Control_Empty_AndPunctuationOnlyQueries()
    {
        using var idx = NewIndex("毛玉");
        Assert.Throws<ArgumentNullException>(() => idx.Search(null!));
        Assert.Throws<ArgumentException>(() => idx.Search("a\u0001b"));
        Assert.Throws<ArgumentException>(() => idx.Search(""));
        Assert.Throws<ArgumentException>(() => idx.Search("  \n"));
        var e = Assert.Throws<ArgumentException>(() => idx.Search("「」。！？"));
        Assert.Contains("検索できる文字", e.Message);
    }

    [Fact]
    public void QueryWithFtsSyntaxChars_IsSafe()
    {
        using var idx = NewIndex("毛玉 NEAR OR AND \"quote\" *star", "ふつう");
        // FTS5 の演算子や引用符・ワイルドカードは区切り扱いになり、構文として解釈されない
        Assert.Equal(new long[] { 1 }, Ids(idx.Search("\"quote\"")));
        Assert.Equal(new long[] { 1 }, Ids(idx.Search("near or and")));
        Assert.Equal(new long[] { 1 }, Ids(idx.Search("*star")));
        Assert.Equal(new long[] { 1 }, Ids(idx.Search("毛玉 NEAR")));
        Assert.Empty(idx.Search("毛玉 OR")); // 文書では NEAR と OR の間に単語があるので、フレーズとしては連続しない
    }

    // ---------- FTS と Highlighter の整合(乱数。シード固定) ----------

    [Fact]
    public void FtsHits_And_HighlighterMatches_AreConsistent_OnRandomText()
    {
        string[] alphabet =
        [
            "あ", "い", "う", "え", "ガ", "ｶﾞ", "カ", "ー", "。", " ", "\n", "a", "b", "1", "ﾊﾟ", "㈱", "「", "」", "々", "𠮷",
        ];
        var rnd = new Random(20261105);
        string Gen(int min, int max)
        {
            int len = rnd.Next(min, max + 1);
            return string.Concat(Enumerable.Range(0, len).Select(_ => alphabet[rnd.Next(alphabet.Length)]));
        }

        var docs = Enumerable.Range(0, 300).Select(_ => Gen(1, 14)).ToList();
        using var idx = NewIndex(docs.ToArray());
        int checkedQueries = 0, withHits = 0;
        for (int q = 0; q < 600; q++)
        {
            string query = Gen(1, 5);
            IReadOnlyList<SearchHit> hits;
            try { hits = idx.Search(query); }
            catch (ArgumentException) { continue; } // 検索できる文字がないクエリ
            var fts = hits.Select(h => h.Id).ToHashSet();
            for (int i = 0; i < docs.Count; i++)
            {
                bool highlighted = Highlighter.Find(docs[i], query).Count > 0;
                Assert.True(highlighted == fts.Contains(i + 1),
                    $"不整合: query={Escape(query)} doc={Escape(docs[i])} fts={fts.Contains(i + 1)} highlight={highlighted}");
            }
            checkedQueries++;
            if (hits.Count > 0) withHits++;
        }
        output.WriteLine($"queries checked={checkedQueries}, with hits={withHits}, docs={docs.Count}");
        Assert.True(withHits > 50, "ヒットするクエリが少なすぎて、整合の検査にならない");
    }

    private static string Escape(string s) => string.Concat(s.Select(c => c < 0x20 || c > 0x7e ? $"\\u{(int)c:X4}" : c.ToString()));

    // ---------- SearchAll(複数の語の AND、関連度順。段階3の評価用) ----------

    private static List<long> AllIds(FtsIndex idx, params string[] terms) => idx.SearchAll(terms).Select(h => h.Id).ToList();

    [Fact]
    public void SearchAll_AndOfPhrases_RequiresEveryTerm()
    {
        using var idx = NewIndex("黒髪の少女と猫がいる", "黒髪の少女", "猫だけがいる", "金髪の少女と犬");
        Assert.Equal([1L], AllIds(idx, "黒髪", "猫"));      // 両方ある文書だけ
        Assert.Equal([1L, 2L], AllIds(idx, "黒髪", "少女").OrderBy(x => x).ToList()); // 両方ある文書(順序は下のテストで見る)
        Assert.Empty(AllIds(idx, "黒髪", "犬"));
    }

    [Fact]
    public void SearchAll_OrdersByBm25_ShorterAndMoreFrequentFirst_ScoreLowerIsBetter()
    {
        using var idx = NewIndex("ガチャを引く。ガチャを引く。ガチャを引く", "ガチャを引く", "ガチャの結果。たくさんの長い文章がここに入っていて、関係のない言葉がずっと続く");
        var hits = idx.SearchAll(["ガチャ"]);
        Assert.Equal(3, hits.Count);
        Assert.True(hits[0].Score <= hits[1].Score && hits[1].Score <= hits[2].Score, "Score は昇順(小さいほど良い)");
        Assert.Equal(3L, hits[2].Id); // 長くて1回しか出ない文書が最後
    }

    [Fact]
    public void SearchAll_SingleCharTerm_FiltersWithLike_AndRanksByBm25WhenAPhraseIsPresent()
    {
        using var idx = NewIndex("森の中の洞窟", "洞窟の中", "森と洞窟と森");
        Assert.Equal([1L, 3L], AllIds(idx, "洞窟", "森").OrderBy(x => x).ToList()); // 「森」は LIKE で絞り込み、順位は BM25
        Assert.Equal([1L, 2L], AllIds(idx, "洞窟", "中").OrderBy(x => x).ToList()); // 2文字の語 + 1文字の語(3 には「中」が無い)
    }

    [Fact]
    public void SearchAll_OnlySingleChars_RanksByOccurrenceCountThenId()
    {
        using var idx = NewIndex("森", "森と森と森", "森と森", "花");
        var hits = idx.SearchAll(["森"]);
        Assert.Equal([2L, 3L, 1L], hits.Select(h => h.Id).ToList()); // 出現回数 3 → 2 → 1
        Assert.Equal([-3.0, -2.0, -1.0], hits.Select(h => h.Score).ToList());
        Assert.Equal([2L, 3L], AllIds(idx, "森", "と")); // 複数の1文字: 両方を含む文書だけ。回数の合計 森3+と2 → 森2+と1
    }

    [Fact]
    public void BarSeparator_PreventsBigramsAcrossFields_ButNewlineDoesNot()
    {
        // 段階3の評価では、ocr・description・tags を「|」でつないで 1 文書にする。「|」は区切りになり、境界をまたぐ一致が出ない。
        // 一方、改行は日本語の文字の間では除去される(S3 の仕様)ので、改行でつなぐと境界をまたいで当たってしまう。
        using var idx = NewIndex("あか|いろ", "あか\nいろ");
        Assert.Equal([2L], AllIds(idx, "かい"));
        Assert.Equal([1L, 2L], AllIds(idx, "あか").OrderBy(x => x).ToList());
        Assert.Equal([1L, 2L], AllIds(idx, "いろ").OrderBy(x => x).ToList());
    }

    [Fact]
    public void IdeographicComma_SeparatesTags_ButSpaceAndNewlineDoNot()
    {
        // 段階3の評価では、tags を「、」でつなぐ。「黒髪」+「ロングヘア」が「黒髪ロング」に当たってはいけない。
        // 空白・改行でつなぐと、日本語の文字の間なので S3 の正規化で除かれ、並び順しだいでタグどうしの bigram がつながる。
        using var idx = NewIndex("黒髪、ロングヘア", "黒髪 ロングヘア", "黒髪\nロングヘア");
        Assert.Equal([2L, 3L], AllIds(idx, "黒髪ロング").OrderBy(x => x).ToList());
        Assert.Equal([1L, 2L, 3L], AllIds(idx, "黒髪", "ロングヘア").OrderBy(x => x).ToList());   // 語ごとの AND は、どれでも当たる
        Assert.Equal([1L, 2L, 3L], AllIds(idx, "ロングヘア").OrderBy(x => x).ToList());
    }

    [Fact]
    public void SearchAll_EmptyOrInvalidTerms_Throw()
    {
        using var idx = NewIndex("テスト");
        Assert.Throws<ArgumentException>(() => idx.SearchAll([]));
        Assert.ThrowsAny<ArgumentException>(() => idx.SearchAll(["\u0001"]));
    }

    // ---------- SQLite 直叩きのヘルパ ----------

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<long> ColumnLongs(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var rd = cmd.ExecuteReader();
        var list = new List<long>();
        while (rd.Read()) list.Add(rd.GetInt64(0));
        return list;
    }
}
