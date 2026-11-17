using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace S3Fts;

public enum SearchPath { Fts, Like }

public sealed record SearchHit(long Id, string Text, SearchPath Path, IReadOnlyList<TextRange> Ranges);

/// <summary>
/// SQLite(Microsoft.Data.Sqlite 同梱)+ FTS5 unicode61 による日本語全文検索のスパイク実装。
/// docs : 元のテキストと、正規化後のテキスト(1文字クエリの LIKE 用)
/// fts  : FTS5。bigram 化したトークンの空白区切り(rowid = docs.id)
/// </summary>
public sealed class FtsIndex : IDisposable
{
    private readonly SqliteConnection _conn;

    /// <summary>
    /// unicode61 の設定。remove_diacritics は明示する(既定値に頼らない)。
    /// 1 は「ラテン文字の発音区別符号だけ」を除去する。仮名の濁点・半濁点(カ と ガ、ハ と パ)は区別されたままのはず
    /// ——これは FtsIndexTests の remove_diacritics の検証で実測して確かめている。
    /// </summary>
    public const string TokenizerSpec = "unicode61 remove_diacritics 1";

    public FtsIndex(string dataSource)
    {
        _conn = new SqliteConnection($"Data Source={dataSource}");
        _conn.Open();
        EnsureFts5Available(_conn);
        Exec("CREATE TABLE docs (id INTEGER PRIMARY KEY, text TEXT NOT NULL, norm_text TEXT NOT NULL)");
        Exec($"CREATE VIRTUAL TABLE fts USING fts5(tokens, tokenize = '{TokenizerSpec}')");
    }

    /// <summary>FTS5 が使えなければ、握りつぶさず例外にする。</summary>
    private static void EnsureFts5Available(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM pragma_compile_options WHERE compile_options = 'ENABLE_FTS5'";
        bool compiled = cmd.ExecuteScalar() is not null;
        try
        {
            cmd.CommandText = "CREATE VIRTUAL TABLE temp.fts5_probe USING fts5(x); DROP TABLE temp.fts5_probe;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException e)
        {
            throw new InvalidOperationException($"この SQLite では FTS5 が使えない(ENABLE_FTS5 コンパイルオプション: {compiled})。{e.Message}", e);
        }
    }

    /// <summary>SQLite のバージョンと、コンパイルオプションのうち FTS・UNICODE 関連のもの(報告用)。</summary>
    public string DescribeSqlite()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version()";
        string v = (string)cmd.ExecuteScalar()!;
        cmd.CommandText = "SELECT group_concat(compile_options, ',') FROM pragma_compile_options WHERE compile_options LIKE '%FTS%' OR compile_options LIKE '%UNICODE%'";
        string opts = (string?)cmd.ExecuteScalar() ?? "";
        return $"SQLite {v}; {opts}";
    }

    public long Add(string text)
    {
        NormalizedText n = NormalizedText.Create(text); // null・制御文字・不正サロゲートはここで例外
        IReadOnlyList<Token> tokens = Tokenizer.TokenizeDocument(n);
        using var tx = _conn.BeginTransaction();
        long id;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO docs (text, norm_text) VALUES ($t, $n) RETURNING id";
            cmd.Parameters.AddWithValue("$t", text);
            cmd.Parameters.AddWithValue("$n", n.Text);
            id = (long)cmd.ExecuteScalar()!;
        }
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO fts (rowid, tokens) VALUES ($id, $tok)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$tok", QueryPlanner.TokensToIndexText(tokens));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return id;
    }

    /// <summary>ヒット(関連度順。1文字クエリは id 順)。limit を指定すると上位だけを返す。ハイライト範囲つき。</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit = -1)
    {
        QueryPlan plan = QueryPlanner.Plan(query);
        var hits = new List<(long Id, string Text)>();
        using (var cmd = CreateSearchCommand(plan, withText: true, limit))
        {
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) hits.Add((rd.GetInt64(0), rd.GetString(1)));
        }

        SearchPath path = plan.Kind == QueryKind.SingleChar ? SearchPath.Like : SearchPath.Fts;
        var result = new List<SearchHit>(hits.Count);
        foreach (var (id, text) in hits)
        {
            result.Add(new SearchHit(id, text, path, Highlighter.Find(text, query)));
        }
        return result;
    }

    /// <summary>ヒットした id だけ(テキストの読み出しもハイライトもしない)。性能測定用。</summary>
    public IReadOnlyList<long> SearchIds(string query)
    {
        QueryPlan plan = QueryPlanner.Plan(query);
        var ids = new List<long>();
        using var cmd = CreateSearchCommand(plan, withText: false, limit: -1);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) ids.Add(rd.GetInt64(0));
        return ids;
    }

    /// <summary>
    /// 複数の語のすべてを含む文書(AND)を、関連度順に返す(段階3の評価用)。Score は小さいほど良い(FTS5 の rank = BM25 の符号を反転したもの)。
    /// 2文字以上の語は FTS5 のフレーズ(MATCH "a" AND "b")で引き、順位は BM25(FTS5 の rank)。
    /// 1文字の語は LIKE の絞り込み(S3 の方針どおり、FTS では引かない)。すべての語が1文字のときは BM25 が使えないので、
    /// 語の出現回数の合計(多いほど良い。Score = -回数)、同じなら id 順。出現回数は文書の長さで割っていない。
    /// 同じ Score は id 順(決定的)。語が空、または空白だけの語があれば例外。
    /// </summary>
    public IReadOnlyList<(long Id, double Score)> SearchAll(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count == 0) throw new ArgumentException("語が1つも無い", nameof(terms));
        var phrases = new List<string>();
        var singles = new List<string>();
        foreach (string t in terms)
        {
            QueryPlan p = QueryPlanner.Plan(t); // 検索できる文字がない・不正な文字は例外
            if (p.Kind == QueryKind.SingleChar) singles.Add(p.SingleChar!); else phrases.Add(p.FtsMatch!);
        }
        using var cmd = _conn.CreateCommand();
        var where = new List<string>();
        for (int i = 0; i < singles.Count; i++)
        {
            where.Add($"docs.norm_text LIKE $l{i} ESCAPE '\\'");
            cmd.Parameters.AddWithValue($"$l{i}", "%" + EscapeLike(singles[i]) + "%");
            cmd.Parameters.AddWithValue($"$c{i}", singles[i]); // 出現回数の計算用(LIKE のパターンとは別のパラメータ)
        }
        if (phrases.Count > 0)
        {
            where.Insert(0, "fts MATCH $q");
            cmd.Parameters.AddWithValue("$q", string.Join(" AND ", phrases));
            cmd.CommandText = $"SELECT docs.id, fts.rank FROM fts JOIN docs ON docs.id = fts.rowid WHERE {string.Join(" AND ", where)} ORDER BY fts.rank, docs.id";
        }
        else
        {
            // length() は文字(コードポイント)数。1文字の語を取り除いた分の差が出現回数
            var tf = new List<string>();
            for (int i = 0; i < singles.Count; i++) tf.Add($"(length(docs.norm_text) - length(replace(docs.norm_text, $c{i}, '')))");
            cmd.CommandText = $"SELECT docs.id, -CAST(({string.Join(" + ", tf)}) AS REAL) AS score FROM docs WHERE {string.Join(" AND ", where)} ORDER BY score, docs.id";
        }
        var hits = new List<(long, double)>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) hits.Add((rd.GetInt64(0), rd.GetDouble(1)));
        return hits;
    }

    private SqliteCommand CreateSearchCommand(QueryPlan plan, bool withText, int limit)
    {
        var cmd = _conn.CreateCommand();
        string cols = withText ? "docs.id, docs.text" : "docs.id";
        if (plan.Kind == QueryKind.SingleChar)
        {
            cmd.CommandText = $"SELECT {cols} FROM docs WHERE norm_text LIKE $p ESCAPE '\\' ORDER BY docs.id LIMIT $limit";
            cmd.Parameters.AddWithValue("$p", "%" + EscapeLike(plan.SingleChar!) + "%");
        }
        else
        {
            cmd.CommandText = $"SELECT {cols} FROM fts JOIN docs ON docs.id = fts.rowid WHERE fts MATCH $q ORDER BY fts.rank, docs.id LIMIT $limit";
            cmd.Parameters.AddWithValue("$q", plan.FtsMatch);
        }
        cmd.Parameters.AddWithValue("$limit", limit);
        return cmd;
    }

    /// <summary>一括投入の内訳。Prep = 正規化とトークン化(C#)、Sql = SQLite への書き込み。</summary>
    public sealed record BatchStats(long Count, TimeSpan Prep, TimeSpan Sql);

    /// <summary>1つのトランザクションでまとめて投入する(準備済みのコマンドを使い回す)。途中で例外が出たら全体をロールバックする。</summary>
    public BatchStats AddBatch(IEnumerable<string> texts)
    {
        var prep = new System.Diagnostics.Stopwatch();
        var sql = new System.Diagnostics.Stopwatch();
        long count = 0;
        using var tx = _conn.BeginTransaction();
        using var insDoc = _conn.CreateCommand();
        insDoc.Transaction = tx;
        insDoc.CommandText = "INSERT INTO docs (text, norm_text) VALUES ($t, $n) RETURNING id";
        var pt = insDoc.Parameters.Add("$t", SqliteType.Text);
        var pn = insDoc.Parameters.Add("$n", SqliteType.Text);
        using var insFts = _conn.CreateCommand();
        insFts.Transaction = tx;
        insFts.CommandText = "INSERT INTO fts (rowid, tokens) VALUES ($id, $tok)";
        var pid = insFts.Parameters.Add("$id", SqliteType.Integer);
        var ptok = insFts.Parameters.Add("$tok", SqliteType.Text);
        foreach (string text in texts)
        {
            prep.Start();
            NormalizedText n = NormalizedText.Create(text);
            string tokenText = QueryPlanner.TokensToIndexText(Tokenizer.TokenizeDocument(n));
            prep.Stop();
            sql.Start();
            pt.Value = text;
            pn.Value = n.Text;
            long id = (long)insDoc.ExecuteScalar()!;
            pid.Value = id;
            ptok.Value = tokenText;
            insFts.ExecuteNonQuery();
            sql.Stop();
            count++;
        }
        sql.Start();
        tx.Commit();
        sql.Stop();
        return new BatchStats(count, prep.Elapsed, sql.Elapsed);
    }

    /// <summary>FTS5 のセグメントを1つにまとめる(optimize コマンド)。</summary>
    public void Optimize() => Exec("INSERT INTO fts(fts) VALUES('optimize')");

    public void Vacuum() => Exec("VACUUM");

    /// <summary>測定用: 任意の SQL を実行する(結果は読まない)。</summary>
    public void ExecuteRaw(string sql) => Exec(sql);

    /// <summary>テーブル(インデックス)ごとのバイト数。dbstat が使えなければ null。</summary>
    public Dictionary<string, long>? TableSizes()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT name, SUM(pgsize) FROM dbstat GROUP BY name";
            using var rd = cmd.ExecuteReader();
            var d = new Dictionary<string, long>();
            while (rd.Read()) d[rd.GetString(0)] = rd.GetInt64(1);
            return d;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>LIKE の特殊文字(% _ \)のエスケープ(ESCAPE '\' と対で使う)。</summary>
    public static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>テスト用: 任意の SQL を実行して、1列目の文字列を返す。</summary>
    public List<string> QueryStrings(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        using var rd = cmd.ExecuteReader();
        var list = new List<string>();
        while (rd.Read()) list.Add(rd.GetString(0));
        return list;
    }

    public void Dispose() => _conn.Dispose();
}
