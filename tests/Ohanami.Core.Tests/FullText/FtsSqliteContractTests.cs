using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Ohanami.Core.FullText;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// 全文検索の規約と、SQLite(同梱の FTS5 unicode61)の実際の動きとの突き合わせ。DB 層を通さず、テストの中だけの小さな容れ物(<see cref="MiniFts"/>。
/// メモリ上の一時表)で、<see cref="SearchPlan"/> の MATCH 式・LIKE・出現回数が、基準の実装(<see cref="SearchPlan.Matches"/> など)と一致することを確かめる。
/// スキーマ(captions・fts_docs・search_text)の設計ではない。
/// </summary>
public sealed class FtsSqliteContractTests
{
    /// <summary>docs(id, text, norm_text) と FTS5(tokens)。評価のコードの FtsIndex の検索の部分を、SearchPlan を使う形で写したテスト用の容れ物。</summary>
    private sealed class MiniFts : IDisposable
    {
        private readonly SqliteConnection _conn;

        public MiniFts(params string[] docs)
        {
            _conn = new SqliteConnection("Data Source=:memory:");
            _conn.Open();
            Exec("CREATE TABLE docs (id INTEGER PRIMARY KEY, text TEXT NOT NULL, norm_text TEXT NOT NULL)");
            Exec($"CREATE VIRTUAL TABLE fts USING fts5(tokens, tokenize = '{FtsSpec.TokenizerSpec}')");
            foreach (string d in docs) Add(d);
        }

        public long Add(string text)
        {
            NormalizedText n = NormalizedText.Create(text);
            using var tx = _conn.BeginTransaction();
            long id;
            using (SqliteCommand cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO docs (text, norm_text) VALUES ($t, $n) RETURNING id";
                cmd.Parameters.AddWithValue("$t", text);
                cmd.Parameters.AddWithValue("$n", n.Text);
                id = (long)cmd.ExecuteScalar()!;
            }
            using (SqliteCommand cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO fts (rowid, tokens) VALUES ($id, $tok)";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$tok", QueryPlanner.TokensToIndexText(Tokenizer.TokenizeDocument(n)));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return id;
        }

        /// <summary>空白区切りの AND。Score は小さいほど良い(フレーズがあれば FTS5 の rank = BM25 の符号反転、すべて1文字なら -出現回数)。同じ Score は id 順。</summary>
        public List<(long Id, double Score)> SearchAll(string query)
        {
            SearchPlan plan = QueryPlanner.PlanSearch(query);
            using SqliteCommand cmd = _conn.CreateCommand();
            var where = new List<string>();
            string[] singles = plan.SingleChars.ToArray();
            for (int i = 0; i < singles.Length; i++)
            {
                where.Add($"docs.norm_text LIKE $l{i} ESCAPE '\\'");
                cmd.Parameters.AddWithValue($"$l{i}", SearchPlan.LikePattern(singles[i]));
                cmd.Parameters.AddWithValue($"$c{i}", singles[i]);
            }
            if (plan.FtsMatch is not null)
            {
                where.Insert(0, "fts MATCH $q");
                cmd.Parameters.AddWithValue("$q", plan.FtsMatch);
                cmd.CommandText = $"SELECT docs.id, fts.rank FROM fts JOIN docs ON docs.id = fts.rowid WHERE {string.Join(" AND ", where)} ORDER BY fts.rank, docs.id";
            }
            else
            {
                string tf = string.Join(" + ", Enumerable.Range(0, singles.Length).Select(i => $"(length(docs.norm_text) - length(replace(docs.norm_text, $c{i}, '')))"));
                cmd.CommandText = $"SELECT docs.id, -CAST(({tf}) AS REAL) AS score FROM docs WHERE {string.Join(" AND ", where)} ORDER BY score, docs.id";
            }
            var hits = new List<(long, double)>();
            using SqliteDataReader rd = cmd.ExecuteReader();
            while (rd.Read()) hits.Add((rd.GetInt64(0), rd.GetDouble(1)));
            return hits;
        }

        public List<long> Ids(string query) => SearchAll(query).Select(h => h.Id).ToList();

        public List<long> SortedIds(string query) => Ids(query).Order().ToList();

        private void Exec(string sql)
        {
            using SqliteCommand cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public void Dispose() => _conn.Dispose();
    }

    private static List<long> ColumnLongs(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using SqliteDataReader rd = cmd.ExecuteReader();
        var list = new List<long>();
        while (rd.Read()) list.Add(rd.GetInt64(0));
        return list;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---------- FTS5 の有無と、unicode61 の設定 ----------

    [Fact]
    public void Fts5_IsAvailable_InBundledSqlite()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        Exec(conn, "CREATE VIRTUAL TABLE temp.probe USING fts5(x)");
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM pragma_compile_options WHERE compile_options = 'ENABLE_FTS5'";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    /// <summary>
    /// remove_diacritics の 0 / 1 / 2 のそれぞれで、仮名の濁点・半濁点が同一視されないことを実測する(<see cref="FtsSpec.TokenizerSpec"/> の前提)。
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
        string[][] triples =
        [
            ["か", "が"], ["き", "ぎ"], ["さ", "ざ"], ["た", "だ"], ["は", "ば", "ぱ"], ["ひ", "び", "ぴ"], ["ふ", "ぶ", "ぷ"],
            ["カ", "ガ"], ["ハ", "バ", "パ"], ["フ", "ブ", "プ"], ["ウ", "ヴ"],
        ];
        var all = triples.SelectMany(t => t).ToList();
        for (int i = 0; i < all.Count; i++) Exec(conn, $"INSERT INTO t(rowid, x) VALUES ({i + 1}, '{all[i]}')");
        for (int i = 0; i < all.Count; i++)
            Assert.Equal([(long)(i + 1)], ColumnLongs(conn, $"SELECT rowid FROM t WHERE t MATCH '\"{all[i]}\"'"));   // 自分自身だけに当たる
    }

    [Fact]
    public void TheTokenizerSpec_IsTheOneTheKanaTestCovers()
    {
        Assert.Equal("unicode61 remove_diacritics 1", FtsSpec.TokenizerSpec);
    }

    // ---------- 一致の意味 = 基準の実装 ----------

    [Fact]
    public void Phrase_And_SingleChar_ResultsEqualTheReferenceMatches_OnRandomText()
    {
        string[] alphabet =
        [
            "あ", "い", "う", "え", "ガ", "ｶﾞ", "カ", "ー", "。", " ", "\n", "a", "b", "1", "ﾊﾟ", "㈱", "「", "」", "々", "𠮷",
        ];
        var rnd = new Random(20261105);
        string Gen(int min, int max) => string.Concat(Enumerable.Range(0, rnd.Next(min, max + 1)).Select(_ => alphabet[rnd.Next(alphabet.Length)]));

        var docs = Enumerable.Range(0, 300).Select(_ => Gen(1, 14)).ToList();
        var normalized = docs.Select(NormalizedText.Create).ToList();
        using var idx = new MiniFts(docs.ToArray());
        int checkedQueries = 0, withHits = 0, multi = 0;
        for (int q = 0; q < 800; q++)
        {
            string query = Gen(1, 5);
            if (q % 4 == 3) query = Gen(1, 3) + " " + Gen(1, 3);     // 空白区切りの 2 語
            SearchPlan plan;
            try { plan = QueryPlanner.PlanSearch(query); }
            catch (ArgumentException) { continue; }                    // 検索できる文字がない語がある
            HashSet<long> fts = idx.Ids(query).ToHashSet();
            for (int i = 0; i < docs.Count; i++)
            {
                bool expected = plan.Matches(normalized[i]);
                Assert.True(expected == fts.Contains(i + 1),
                    $"不整合: query={FtsQueryTests.Escape(query)} doc={FtsQueryTests.Escape(docs[i])} reference={expected} sqlite={fts.Contains(i + 1)}");
            }
            checkedQueries++;
            if (fts.Count > 0) withHits++;
            if (plan.Terms.Count > 1) multi++;
        }
        Assert.True(withHits > 50, $"ヒットするクエリが少なすぎて、整合の検査にならない({withHits}/{checkedQueries})");
        Assert.True(multi > 20, $"2 語のクエリが少なすぎる({multi})");
    }

    // ---------- SearchAll の意味(空白区切りの AND、1文字は LIKE、全部1文字なら出現回数) ----------

    [Fact]
    public void SearchAll_AndOfPhrases_RequiresEveryTerm()
    {
        using var idx = new MiniFts("銀髪の少女と猫がいる", "銀髪の少女", "猫だけがいる", "金髪の少女と犬");
        Assert.Equal([1L], idx.Ids("銀髪 猫"));
        Assert.Equal([1L, 2L], idx.SortedIds("銀髪 少女"));
        Assert.Empty(idx.Ids("銀髪 犬"));
    }

    [Fact]
    public void SearchAll_OrdersByBm25_ShorterAndMoreFrequentFirst_ScoreLowerIsBetter()
    {
        using var idx = new MiniFts("ガチャを引く。ガチャを引く。ガチャを引く", "ガチャを引く", "ガチャの結果。たくさんの長い文章がここに入っていて、関係のない言葉がずっと続く");
        List<(long Id, double Score)> hits = idx.SearchAll("ガチャ");
        Assert.Equal(3, hits.Count);
        Assert.True(hits[0].Score <= hits[1].Score && hits[1].Score <= hits[2].Score, "Score は昇順(小さいほど良い)");
        Assert.Equal(3L, hits[2].Id);    // 長くて1回しか出ない文書が最後
    }

    [Fact]
    public void SearchAll_SingleCharTerm_FiltersWithLike_AndRanksByBm25WhenAPhraseIsPresent()
    {
        using var idx = new MiniFts("森の中の洞窟", "洞窟の中", "森と洞窟と森");
        Assert.Equal([1L, 3L], idx.SortedIds("洞窟 森"));   // 「森」は LIKE で絞り込み、順位は BM25
        Assert.Equal([1L, 2L], idx.SortedIds("洞窟 中"));   // 2文字の語 + 1文字の語(3 には「中」が無い)
    }

    [Fact]
    public void SearchAll_OnlySingleChars_RanksByOccurrenceCountThenId_AndEqualsThePlansCount()
    {
        string[] docs = ["森", "森と森と森", "森と森", "花"];
        using var idx = new MiniFts(docs);
        List<(long Id, double Score)> hits = idx.SearchAll("森");
        Assert.Equal([2L, 3L, 1L], hits.Select(h => h.Id).ToList());   // 出現回数 3 → 2 → 1
        Assert.Equal([-3.0, -2.0, -1.0], hits.Select(h => h.Score).ToList());
        Assert.Equal([2L, 3L], idx.Ids("森 と"));                        // 複数の1文字: 両方を含む文書だけ。回数の合計 森3+と2 → 森2+と1
        // SQL の出現回数 = SearchPlan.CountOccurrences
        foreach (string query in new[] { "森", "森 と", "と", "花 森" })
        {
            SearchPlan plan = QueryPlanner.PlanSearch(query);
            foreach ((long id, double score) in idx.SearchAll(query))
                Assert.Equal(-plan.CountOccurrences(NormalizedText.Create(docs[id - 1])), score);
        }
    }

    [Fact]
    public void TheFieldSeparator_PreventsBigramsAcrossFields_ButNewlineDoesNot()
    {
        // ocr・description・tags を FtsSpec.Field でつないで 1 文書にする。区切りをまたぐ一致は出ない。
        // 一方、改行は日本語の文字の間では除去される(評価のコードの仕様)ので、改行でつなぐと境界をまたいで当たってしまう。
        using var idx = new MiniFts("あか" + FtsSpec.Field + "いろ", "あか\nいろ");
        Assert.Equal([2L], idx.Ids("かい"));
        Assert.Equal([1L, 2L], idx.SortedIds("あか"));
        Assert.Equal([1L, 2L], idx.SortedIds("いろ"));
    }

    [Fact]
    public void TheTagSeparator_SeparatesTags_ButSpaceAndNewlineDoNot()
    {
        // tags を FtsSpec.Tag でつなぐ。「銀髪」+「ロングヘア」が「銀髪ロング」に当たってはいけない(黙って誤読しない)。
        // 空白・改行でつなぐと、日本語の文字の間なので正規化で除かれ、並び順しだいでタグどうしの bigram がつながる。
        using var idx = new MiniFts("銀髪" + FtsSpec.Tag + "ロングヘア", "銀髪 ロングヘア", "銀髪\nロングヘア");
        Assert.Equal([2L, 3L], idx.SortedIds("銀髪ロング"));
        Assert.Equal([1L, 2L, 3L], idx.SortedIds("銀髪 ロングヘア"));   // 語ごとの AND(空白を入れて打つ)は、どれでも当たる
        Assert.Equal([1L, 2L, 3L], idx.SortedIds("銀髪 ロング"));
        Assert.Equal([1L, 2L, 3L], idx.SortedIds("ロングヘア"));
    }

    [Fact]
    public void QueryWithFtsSyntaxChars_IsSafe_AndOperatorWordsAreOrdinaryWords()
    {
        using var idx = new MiniFts("水玉 NEAR OR AND \"quote\" *star", "ふつう");
        Assert.Equal([1L], idx.Ids("\"quote\""));
        Assert.Equal([1L], idx.Ids("near or and"));    // 空白区切りの AND(評価のコードの Search ではフレーズだった)
        Assert.Equal([1L], idx.Ids("*star"));
        Assert.Equal([1L], idx.Ids("水玉 NEAR"));
        Assert.Equal([1L], idx.Ids("水玉 OR"));
        Assert.Empty(idx.Ids("水玉 ふつう"));
    }

    [Fact]
    public void EscapeLike_MakesPercentAndUnderscoreLiteral_InARealLike()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        Exec(conn, "CREATE TABLE t(id INTEGER PRIMARY KEY, s TEXT)");
        Exec(conn, "INSERT INTO t VALUES (1, '100%'), (2, 'a_b'), (3, 'abc'), (4, 'a\\b')");
        foreach ((string literal, long expected) in new[] { ("%", 1L), ("_", 2L), ("\\", 4L) })
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM t WHERE s LIKE $p ESCAPE '\\'";
            cmd.Parameters.AddWithValue("$p", SearchPlan.LikePattern(literal));
            Assert.Equal(expected, cmd.ExecuteScalar());
        }
    }

    [Fact]
    public void SearchAll_EmptyOrInvalidQueries_Throw()
    {
        using var idx = new MiniFts("テスト");
        Assert.Throws<ArgumentException>(() => idx.SearchAll(""));
        Assert.Throws<ArgumentException>(() => idx.SearchAll("   "));
        Assert.ThrowsAny<ArgumentException>(() => idx.SearchAll("\u0001"));
        Assert.Throws<ArgumentException>(() => idx.SearchAll("テスト 。"));
    }

    [Fact]
    public void Add_RejectsNull_Control_AndBrokenSurrogates()
    {
        using var idx = new MiniFts();
        Assert.Throws<ArgumentNullException>(() => idx.Add(null!));
        Assert.Throws<ArgumentException>(() => idx.Add("a\u0000b"));
        Assert.Throws<ArgumentException>(() => idx.Add("a\u001Fb"));
        Assert.Throws<ArgumentException>(() => idx.Add("\uD800"));
        idx.Add("a\tb\r\nc");   // タブと改行は OK
        Assert.Equal([1L], idx.Ids("a b c"));    // 失敗した Add の行は残っていない(最初の行が id 1)
    }
}
