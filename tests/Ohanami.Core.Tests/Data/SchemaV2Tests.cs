using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Xunit;

namespace Ohanami.Core.Tests.Data;

/// <summary>
/// スキーマ v2(ADR 0006・0030)。v1 → v2 のマイグレーション(データ保持、適用前の複製、新規と移行の一致)と、captions / search_text / index_meta の制約。
/// fts_docs(FTS5)はマイグレーションが作らない派生データなので、表の検査にも比較にも入れない。
/// </summary>
public sealed class SchemaV2Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>版 1 だけの定義(v1 の DB を作る用)。製品の Default は v3。</summary>
    private static readonly DatabaseSchema V1Only = new(1, DatabaseSchema.Default.CreateInitial, [], SchemaV1.Tables);

    /// <summary>版 2 までの定義(v1 → v2 のマイグレーションを、v3 と切り離して確かめる用)。</summary>
    private static readonly DatabaseSchema V2Only = new(2, DatabaseSchema.Default.CreateInitial, [SchemaV2.FromV1], [.. SchemaV1.Tables, .. SchemaV2.Tables]);

    private static readonly string Hash1 = new('a', 64);
    private static readonly string Hash2 = new('b', 64);

    private static List<string> SchemaDump(string path, Func<string, bool>? exclude = null)
    {
        using SqliteConnection raw = TestDb.OpenRaw(path);
        using SqliteCommand cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT name, type || ' ' || name || ' ' || coalesce(sql, '') FROM sqlite_master ORDER BY type, name";
        var list = new List<string>();
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (exclude is not null && exclude(r.GetString(0))) continue;
            list.Add(r.GetString(1));
        }
        return list;
    }

    private static Task SeedV1(OhanamiDatabase db) => db.WriteAsync((c, tx) =>
    {
        TestDb.Exec(c, tx, "INSERT INTO folders(id, path, added_at) VALUES (1, 'C:\\shots', 't')");
        TestDb.Exec(c, tx, $"INSERT INTO images(hash, size, format, first_seen_at) VALUES ('{Hash1}', 100, 'png', 't'), ('{Hash2}', 200, 'jpeg', 't')");
        TestDb.Exec(c, tx, $"INSERT INTO image_paths(path, hash, folder_id, mtime_ticks, size) VALUES ('C:\\shots\\a.png', '{Hash1}', 1, 5, 100)");
        TestDb.Exec(c, tx, "INSERT INTO models(id, kind, name, spec_json, created_at) VALUES ('m1', 'image-embedding', 'n', '{}', 't')");
        TestDb.Exec(c, tx, "INSERT INTO runs(id, job_kind, started_at, app_version, runtime_json) VALUES (1, 'embed', 't', 'v', '{}')");
        TestDb.Exec(c, tx, $"INSERT INTO stage_status(hash, stage, status, model_id, run_id, updated_at) VALUES ('{Hash1}', 'embed', 'done', 'm1', 1, 't')");
        TestDb.Exec(c, tx, $"INSERT INTO image_vectors(hash, model_id, embedding) VALUES ('{Hash1}', 'm1', x'00')");
    }, Ct);

    private static async Task<OhanamiDatabase> OpenSeeded(string path)
    {
        var db = OhanamiDatabase.Open(path);
        await SeedV1(db);
        return db;
    }

    // ---------------------------------------------------------------- マイグレーション

    [Fact]
    public async Task A_v1_database_with_data_is_migrated_to_v2_keeping_every_row_and_a_copy_stays()
    {
        using var dir = new TempDirectory();
        string path = dir.File("a.db");
        Dictionary<string, string> v1Sql;
        using (var v1 = OhanamiDatabase.Open(path, V1Only))
        {
            await SeedV1(v1);
        }
        v1Sql = SchemaDump(path).ToDictionary(l => l.Split(' ')[1], l => l);

        using (var db = OhanamiDatabase.Open(path, V2Only))
        {
            Assert.Equal(2, db.SchemaVersion);
            using SqliteConnection read = db.CreateReadConnection();
            Assert.Equal(1, TestDb.Long(read, "SELECT count(*) FROM folders"));
            Assert.Equal(2, TestDb.Long(read, "SELECT count(*) FROM images"));
            Assert.Equal(1, TestDb.Long(read, "SELECT count(*) FROM image_paths"));
            Assert.Equal(1, TestDb.Long(read, "SELECT count(*) FROM models"));
            Assert.Equal(1, TestDb.Long(read, "SELECT count(*) FROM runs"));
            Assert.Equal(1, TestDb.Long(read, "SELECT count(*) FROM stage_status"));
            Assert.Equal(1, TestDb.Long(read, "SELECT count(*) FROM image_vectors"));
            foreach (string table in SchemaV2.Tables) Assert.Equal(0, TestDb.Long(read, $"SELECT count(*) FROM {table}")); // 新しい表は空で使える
            Assert.Empty(TestDb.Text(read, "PRAGMA foreign_key_check"));
        }

        // 既存の表の定義は 1 文字も変わらない(追加だけ)
        Dictionary<string, string> v2Sql = SchemaDump(path).ToDictionary(l => l.Split(' ')[1], l => l);
        foreach ((string name, string sql) in v1Sql) Assert.Equal(sql, v2Sql[name]);
        foreach (string added in SchemaV2.Tables) Assert.True(v2Sql.ContainsKey(added) && !v1Sql.ContainsKey(added));

        using SqliteConnection raw = TestDb.OpenRaw(path);
        Assert.Equal(2, TestDb.Long(raw, "PRAGMA user_version"));

        // 移行の前の複製が残り、版 1 のままデータを持ち、v2 の表は無い
        string backup = path + ".bak-v1";
        Assert.True(File.Exists(backup));
        using SqliteConnection old = TestDb.OpenRaw(backup);
        Assert.Equal(1, TestDb.Long(old, "PRAGMA user_version"));
        Assert.Equal(2, TestDb.Long(old, "SELECT count(*) FROM images"));
        Assert.Equal(0, TestDb.Long(old, "SELECT count(*) FROM sqlite_master WHERE name IN ('captions', 'search_text', 'index_meta')"));
    }

    [Fact]
    public void A_fresh_database_has_exactly_the_same_schema_as_a_migrated_v1_database_ignoring_fts_docs()
    {
        using var dir = new TempDirectory();
        string fresh = dir.File("fresh.db");
        string migrated = dir.File("migrated.db");
        using (OhanamiDatabase.Open(fresh)) { }
        using (OhanamiDatabase.Open(migrated, V1Only)) { }
        using (OhanamiDatabase.Open(migrated)) { }

        static bool IsFtsDocs(string name) => name.StartsWith("fts_docs", StringComparison.Ordinal);
        Assert.Equal(SchemaDump(migrated, IsFtsDocs), SchemaDump(fresh, IsFtsDocs));
        // マイグレーションは fts_docs も、その影の表も作らない(FtsIndex が規約から作る。ADR 0030)。比較から除くのは念のため
        Assert.DoesNotContain(SchemaDump(fresh), l => l.Split(' ')[1].StartsWith("fts_docs", StringComparison.Ordinal));
        Assert.DoesNotContain(SchemaDump(migrated), l => l.Split(' ')[1].StartsWith("fts_docs", StringComparison.Ordinal));
    }

    [Fact]
    public void The_expected_tables_include_the_v2_tables_but_not_fts_docs_or_its_shadow_tables()
    {
        IReadOnlyList<string> expected = DatabaseSchema.Default.ExpectedTables;
        foreach (string t in SchemaV1.Tables.Concat(SchemaV2.Tables)) Assert.Contains(t, expected);
        Assert.DoesNotContain(expected, t => t.StartsWith("fts_docs", StringComparison.Ordinal));
        Assert.Equal(3, DatabaseSchema.Default.CurrentVersion);   // v3 は SchemaV3Tests
        Assert.Equal(2, DatabaseSchema.Default.Migrations.Count);
    }

    [Fact]
    public void A_newer_database_is_refused_by_code_that_only_knows_v1_and_no_copy_is_made()
    {
        using var dir = new TempDirectory();
        string path = dir.File("a.db");
        using (OhanamiDatabase.Open(path)) { }
        Assert.Throws<UnknownSchemaVersionException>(() => OhanamiDatabase.Open(path, V1Only));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.bak-*"));
    }

    [Fact]
    public async Task Opening_a_current_database_again_does_not_migrate_or_copy()
    {
        using var dir = new TempDirectory();
        string path = dir.File("a.db");
        using (OhanamiDatabase.Open(path)) { }
        using (var db = OhanamiDatabase.Open(path))
        {
            Assert.Equal(3, db.SchemaVersion);
            await db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "INSERT INTO index_meta(key, value) VALUES ('k', 'v')"), Ct);
        }
        using (var db = OhanamiDatabase.Open(path))
        {
            using SqliteConnection read = db.CreateReadConnection();
            Assert.Equal("v", TestDb.Text(read, "SELECT value FROM index_meta WHERE key = 'k'"));
        }
        Assert.Empty(Directory.GetFiles(dir.Path, "*.bak-*"));
    }

    // ---------------------------------------------------------------- captions

    private static (TempDirectory Dir, OhanamiDatabase Db) NewSeededV2()
    {
        var dir = new TempDirectory();
        OhanamiDatabase db = OpenSeeded(dir.File("a.db")).GetAwaiter().GetResult();
        db.WriteAsync((c, tx) =>
        {
            TestDb.Exec(c, tx, "INSERT INTO models(id, kind, name, spec_json, created_at) VALUES ('vlm-a', 'vlm', 'a', '{}', 't'), ('vlm-b', 'vlm', 'b', '{}', 't')");
        }, Ct).GetAwaiter().GetResult();
        return (dir, db);
    }

    private static string InsertCaption(string hash, string model, int attempt = 1, string run = "1") =>
        $"INSERT INTO captions(hash, model_id, ocr, description, tags_json, finish_reason, attempt, settings_json, prompt_tokens, run_id, created_at) " +
        $"VALUES ('{hash}', '{model}', '', 'd', '[]', 'stop', {attempt}, '{{}}', NULL, {run}, 't')";

    [Fact]
    public async Task Captions_are_kept_per_hash_and_model_so_a_second_model_does_not_replace_the_first()
    {
        var (dir, db) = NewSeededV2();
        using (dir) using (db)
        {
            await db.WriteAsync((c, tx) =>
            {
                TestDb.Exec(c, tx, InsertCaption(Hash1, "vlm-a"));
                TestDb.Exec(c, tx, InsertCaption(Hash1, "vlm-b"));
                TestDb.Exec(c, tx, InsertCaption(Hash2, "vlm-a"));
            }, Ct);
            using SqliteConnection read = db.CreateReadConnection();
            Assert.Equal(3, TestDb.Long(read, "SELECT count(*) FROM captions"));
            Assert.Equal(2, TestDb.Long(read, $"SELECT count(*) FROM captions WHERE hash = '{Hash1}'"));
        }
    }

    [Fact]
    public async Task The_same_hash_and_model_cannot_have_two_captions()
    {
        var (dir, db) = NewSeededV2();
        using (dir) using (db)
        {
            await db.WriteAsync((c, tx) => TestDb.Exec(c, tx, InsertCaption(Hash1, "vlm-a")), Ct);
            await Assert.ThrowsAsync<SqliteException>(() => db.WriteAsync((c, tx) => TestDb.Exec(c, tx, InsertCaption(Hash1, "vlm-a")), Ct));
        }
    }

    [Fact]
    public async Task Captions_reject_an_unknown_image_model_or_run_and_a_zero_attempt()
    {
        var (dir, db) = NewSeededV2();
        using (dir) using (db)
        {
            string ghost = new('c', 64);
            await Assert.ThrowsAsync<SqliteException>(() => db.WriteAsync((c, tx) => TestDb.Exec(c, tx, InsertCaption(ghost, "vlm-a")), Ct));          // images に無い
            await Assert.ThrowsAsync<SqliteException>(() => db.WriteAsync((c, tx) => TestDb.Exec(c, tx, InsertCaption(Hash1, "no-such-model")), Ct));  // models に無い
            await Assert.ThrowsAsync<SqliteException>(() => db.WriteAsync((c, tx) => TestDb.Exec(c, tx, InsertCaption(Hash1, "vlm-a", run: "999")), Ct)); // runs に無い
            await Assert.ThrowsAsync<SqliteException>(() => db.WriteAsync((c, tx) => TestDb.Exec(c, tx, InsertCaption(Hash1, "vlm-a", attempt: 0)), Ct)); // 試行は 1 以上
            using SqliteConnection read = db.CreateReadConnection();
            Assert.Equal(0, TestDb.Long(read, "SELECT count(*) FROM captions"));
        }
    }

    [Fact]
    public async Task Captions_accept_an_empty_ocr_and_keep_text_exactly()
    {
        var (dir, db) = NewSeededV2();
        using (dir) using (db)
        {
            await db.WriteAsync((c, tx) =>
            {
                using SqliteCommand cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO captions(hash, model_id, ocr, description, tags_json, finish_reason, attempt, settings_json, prompt_tokens, run_id, created_at) " +
                                  "VALUES ($h, 'vlm-a', $ocr, $d, $t, 'stop', 2, '{}', 1234, 1, 't')";
                cmd.Parameters.AddWithValue("$h", Hash1);
                cmd.Parameters.AddWithValue("$ocr", "");
                cmd.Parameters.AddWithValue("$d", "説明\nの 2 行目 ｶﾞ");
                cmd.Parameters.AddWithValue("$t", "[\"銀髪\",\"ロングヘア\"]");
                cmd.ExecuteNonQuery();
            }, Ct);
            using SqliteConnection read = db.CreateReadConnection();
            Assert.Equal("", TestDb.Text(read, "SELECT ocr FROM captions"));
            Assert.Equal("説明\nの 2 行目 ｶﾞ", TestDb.Text(read, "SELECT description FROM captions"));    // 加工せずに保存する(正規化は索引の側)
            Assert.Equal("[\"銀髪\",\"ロングヘア\"]", TestDb.Text(read, "SELECT tags_json FROM captions"));
            Assert.Equal(1234, TestDb.Long(read, "SELECT prompt_tokens FROM captions"));
        }
    }

    private static string QueryPlan(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using SqliteDataReader r = cmd.ExecuteReader();
        var details = new List<string>();
        while (r.Read()) details.Add(r.GetString(3));
        return string.Join(" | ", details);
    }

    [Fact]
    public void Captions_are_looked_up_by_model_and_hash_through_the_unique_index_and_by_hash_through_its_own_index()
    {
        var (dir, db) = NewSeededV2();
        using (dir) using (db)
        {
            using SqliteConnection read = db.CreateReadConnection();
            string byModelAndHash = QueryPlan(read, $"SELECT * FROM captions WHERE model_id = 'vlm-a' AND hash = '{Hash1}'");
            Assert.Contains("sqlite_autoindex_captions", byModelAndHash, StringComparison.Ordinal);
            Assert.DoesNotContain("SCAN", byModelAndHash, StringComparison.Ordinal);
            string byHash = QueryPlan(read, $"SELECT * FROM captions WHERE hash = '{Hash1}'");
            Assert.Contains("ix_captions_hash", byHash, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- search_text と index_meta

    [Fact]
    public async Task Search_text_has_one_row_per_image_with_a_stable_integer_id_for_fts_docs()
    {
        var (dir, db) = NewSeededV2();
        using (dir) using (db)
        {
            await db.WriteAsync((c, tx) =>
            {
                TestDb.Exec(c, tx, $"INSERT INTO search_text(hash, norm_text, model_id) VALUES ('{Hash1}', 't1', 'vlm-a')");
                TestDb.Exec(c, tx, $"INSERT INTO search_text(hash, norm_text, model_id) VALUES ('{Hash2}', 't2', 'vlm-a')");
            }, Ct);
            using (SqliteConnection read = db.CreateReadConnection())
            {
                Assert.Equal(2, TestDb.Long(read, "SELECT count(DISTINCT id) FROM search_text"));
                Assert.Equal(1, TestDb.Long(read, "SELECT id FROM search_text WHERE norm_text = 't1'")); // INTEGER PRIMARY KEY = rowid の別名
            }
            await Assert.ThrowsAsync<SqliteException>(() => db.WriteAsync((c, tx) =>
                TestDb.Exec(c, tx, $"INSERT INTO search_text(hash, norm_text, model_id) VALUES ('{Hash1}', 'dup', 'vlm-a')"), Ct));           // 1 画像 1 行
            await Assert.ThrowsAsync<SqliteException>(() => db.WriteAsync((c, tx) =>
                TestDb.Exec(c, tx, $"INSERT INTO search_text(hash, norm_text, model_id) VALUES ('{new string('c', 64)}', 'x', 'vlm-a')"), Ct)); // images に無い
            await Assert.ThrowsAsync<SqliteException>(() => db.WriteAsync((c, tx) =>
                TestDb.Exec(c, tx, "INSERT INTO search_text(hash, norm_text, model_id) VALUES ('" + new string('d', 64) + "', 'x', 'nope')"), Ct));
        }
    }

    [Fact]
    public async Task Index_meta_is_a_key_value_table_with_a_unique_key()
    {
        var (dir, db) = NewSeededV2();
        using (dir) using (db)
        {
            await db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "INSERT INTO index_meta(key, value) VALUES ('fts_spec', 'fts-spec/1')"), Ct);
            await Assert.ThrowsAsync<SqliteException>(() => db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "INSERT INTO index_meta(key, value) VALUES ('fts_spec', 'other')"), Ct));
            await Assert.ThrowsAsync<SqliteException>(() => db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "INSERT INTO index_meta(key, value) VALUES ('no_value', NULL)"), Ct));
        }
    }

    [Fact]
    public void The_new_tables_are_strict_and_add_no_check_on_enum_like_columns()
    {
        var (dir, db) = NewSeededV2();
        using (dir) using (db)
        {
            using SqliteConnection read = db.CreateReadConnection();
            foreach (string table in SchemaV2.Tables)
            {
                string sql = TestDb.Text(read, $"SELECT sql FROM sqlite_master WHERE name = '{table}'");
                Assert.Contains("STRICT", sql, StringComparison.Ordinal);
            }
            // finish_reason の値(stop 以外)を CHECK で決め打ちしない(決定 A)。成功の判定は C# 側で行う
            string captions = TestDb.Text(read, "SELECT sql FROM sqlite_master WHERE name = 'captions'");
            Assert.DoesNotContain("'stop'", captions, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Deleting_an_image_that_has_captions_is_refused_by_the_foreign_key()
    {
        var (dir, db) = NewSeededV2();
        using (dir) using (db)
        {
            await db.WriteAsync((c, tx) =>
            {
                TestDb.Exec(c, tx, InsertCaption(Hash2, "vlm-a"));
            }, Ct);
            await Assert.ThrowsAsync<SqliteException>(() => db.WriteAsync((c, tx) => TestDb.Exec(c, tx, $"DELETE FROM images WHERE hash = '{Hash2}'"), Ct));
        }
    }
}
