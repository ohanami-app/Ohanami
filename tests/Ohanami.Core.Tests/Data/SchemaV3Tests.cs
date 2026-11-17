using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Captions;
using Ohanami.Core.Data;
using Ohanami.Core.FullText;
using Ohanami.Core.Models;
using Xunit;

namespace Ohanami.Core.Tests.Data;

/// <summary>
/// スキーマ v3(ADR 0036): v2 → v3 のマイグレーション(search_text の作り直し、規約の記録を消す、ほかの表はそのまま、適用前の複製)、
/// search_text.model_id が NULL 可なこと、新規と移行の定義が同じこと、移行のあとの索引の作り直し。
/// </summary>
public sealed class SchemaV3Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DatabaseSchema V2Only = new(2, DatabaseSchema.Default.CreateInitial, [SchemaV2.FromV1], [.. SchemaV1.Tables, .. SchemaV2.Tables]);

    private static readonly string Hash1 = new('a', 64);
    private static readonly string Hash2 = new('b', 64);

    private static Dictionary<string, string> SchemaDump(string path, Func<string, bool>? exclude = null)
    {
        using SqliteConnection raw = TestDb.OpenRaw(path);
        using SqliteCommand cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT name, type || ' ' || name || ' ' || coalesce(sql, '') FROM sqlite_master ORDER BY type, name";
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (exclude is not null && exclude(r.GetString(0))) continue;
            map[r.GetString(0)] = r.GetString(1);
        }
        return map;
    }

    private static Task SeedV2(OhanamiDatabase db) => db.WriteAsync((c, tx) =>
    {
        TestDb.Exec(c, tx, "INSERT INTO folders(id, path, added_at) VALUES (1, 'C:\\shots', 't')");
        TestDb.Exec(c, tx, $"INSERT INTO images(hash, size, format, first_seen_at) VALUES ('{Hash1}', 100, 'png', 't'), ('{Hash2}', 200, 'jpeg', 't')");
        TestDb.Exec(c, tx, $"INSERT INTO image_paths(path, hash, folder_id, mtime_ticks, size) VALUES ('C:\\shots\\a.png', '{Hash1}', 1, 5, 100)");
        TestDb.Exec(c, tx, "INSERT INTO models(id, kind, name, spec_json, created_at) VALUES ('m1', 'image-embedding', 'n', '{}', 't'), ('vlm-a', 'vlm', 'a', '{}', 't')");
        TestDb.Exec(c, tx, "INSERT INTO runs(id, job_kind, started_at, app_version, runtime_json) VALUES (1, 'vlm', 't', 'v', '{}')");
        TestDb.Exec(c, tx, $"INSERT INTO stage_status(hash, stage, status, model_id, run_id, updated_at) VALUES ('{Hash1}', 'embed', 'done', 'm1', 1, 't')");
        TestDb.Exec(c, tx, $"INSERT INTO image_vectors(hash, model_id, embedding) VALUES ('{Hash1}', 'm1', x'00')");
        TestDb.Exec(c, tx, $"INSERT INTO captions(hash, model_id, ocr, description, tags_json, finish_reason, attempt, settings_json, prompt_tokens, run_id, created_at) VALUES ('{Hash1}', 'vlm-a', 'o', 'd', '[]', 'stop', 1, '{{}}', NULL, 1, 't')");
        TestDb.Exec(c, tx, "INSERT INTO tags(id, name, kind) VALUES (1, '猫', 'manual')");
        TestDb.Exec(c, tx, $"INSERT INTO image_tags(hash, tag_id, source) VALUES ('{Hash1}', 1, 'manual')");
        TestDb.Exec(c, tx, $"INSERT INTO search_text(hash, norm_text, model_id) VALUES ('{Hash1}', 'o|d|', 'vlm-a')");
        TestDb.Exec(c, tx, "INSERT INTO index_meta(key, value) VALUES ('fts_spec', 'fts-spec/1'), ('other', 'kept')");
    }, Ct);

    // ---------------------------------------------------------------- マイグレーション

    [Fact]
    public async Task A_v2_database_is_migrated_to_v3_rebuilding_only_search_text_and_a_copy_stays()
    {
        using var dir = new TempDirectory();
        string path = dir.File("a.db");
        using (var v2 = OhanamiDatabase.Open(path, V2Only)) await SeedV2(v2);
        Dictionary<string, string> v2Sql = SchemaDump(path);

        using (var db = OhanamiDatabase.Open(path))
        {
            Assert.Equal(3, db.SchemaVersion);
            using SqliteConnection read = db.CreateReadConnection();
            // ほかの表の行はそのまま
            foreach ((string table, long expected) in new[] { ("folders", 1L), ("images", 2L), ("image_paths", 1L), ("models", 2L), ("runs", 1L), ("stage_status", 1L), ("image_vectors", 1L), ("captions", 1L), ("tags", 1L), ("image_tags", 1L) })
                Assert.Equal(expected, TestDb.Long(read, $"SELECT count(*) FROM {table}"));
            // search_text は派生データなので引き継がない。規約の記録は消える(次に開くと作り直しになる)。ほかの index_meta の行は残る
            Assert.Equal(0, TestDb.Long(read, "SELECT count(*) FROM search_text"));
            Assert.Equal(0, TestDb.Long(read, "SELECT count(*) FROM index_meta WHERE key = 'fts_spec'"));
            Assert.Equal("kept", TestDb.Text(read, "SELECT value FROM index_meta WHERE key = 'other'"));
            Assert.Empty(TestDb.Text(read, "PRAGMA foreign_key_check"));
        }

        // search_text 以外の定義は 1 文字も変わらない
        Dictionary<string, string> v3Sql = SchemaDump(path);
        foreach ((string name, string sql) in v2Sql)
        {
            if (name == "search_text" || name.StartsWith("sqlite_autoindex_search_text", StringComparison.Ordinal)) continue;
            Assert.Equal(sql, v3Sql[name]);
        }
        Assert.Equal(v2Sql.Keys.OrderBy(k => k, StringComparer.Ordinal), v3Sql.Keys.OrderBy(k => k, StringComparer.Ordinal));

        using SqliteConnection raw = TestDb.OpenRaw(path);
        Assert.Equal(3, TestDb.Long(raw, "PRAGMA user_version"));

        // 移行の前の複製: 版 2 のまま、search_text の行も規約の記録も持つ
        string backup = path + ".bak-v2";
        Assert.True(File.Exists(backup));
        using SqliteConnection old = TestDb.OpenRaw(backup);
        Assert.Equal(2, TestDb.Long(old, "PRAGMA user_version"));
        Assert.Equal(1, TestDb.Long(old, "SELECT count(*) FROM search_text"));
        Assert.Equal(1, TestDb.Long(old, "SELECT count(*) FROM index_meta WHERE key = 'fts_spec'"));
    }

    [Fact]
    public void A_fresh_database_and_a_database_migrated_from_v2_have_exactly_the_same_schema_ignoring_fts_docs()
    {
        using var dir = new TempDirectory();
        string fresh = dir.File("fresh.db");
        string migrated = dir.File("migrated.db");
        using (OhanamiDatabase.Open(fresh)) { }
        using (OhanamiDatabase.Open(migrated, V2Only)) { }
        using (OhanamiDatabase.Open(migrated)) { }

        static bool IsFtsDocs(string name) => name.StartsWith("fts_docs", StringComparison.Ordinal);
        Dictionary<string, string> a = SchemaDump(fresh, IsFtsDocs);
        Dictionary<string, string> b = SchemaDump(migrated, IsFtsDocs);
        Assert.Equal(a.OrderBy(p => p.Key, StringComparer.Ordinal), b.OrderBy(p => p.Key, StringComparer.Ordinal));
        // search_text の定義は SchemaV3.SearchTableDdl の 1 か所から(新規も移行も)
        Assert.Contains("model_id  TEXT REFERENCES models(id)", a["search_text"].Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void The_current_schema_is_v3_with_two_gapless_migrations_and_no_new_tables()
    {
        Assert.Equal(3, DatabaseSchema.Default.CurrentVersion);
        Assert.Equal([1, 2], DatabaseSchema.Default.Migrations.Select(m => m.FromVersion));
        Assert.Empty(SchemaV3.Tables);
        foreach (string t in SchemaV1.Tables.Concat(SchemaV2.Tables)) Assert.Contains(t, DatabaseSchema.Default.ExpectedTables);
    }

    [Fact]
    public async Task Migrating_twice_over_an_already_migrated_database_does_nothing_and_makes_no_copy()
    {
        using var dir = new TempDirectory();
        string path = dir.File("a.db");
        using (var v2 = OhanamiDatabase.Open(path, V2Only)) await SeedV2(v2);
        using (OhanamiDatabase.Open(path)) { }
        Assert.Single(Directory.GetFiles(dir.Path, "*.bak-*"));
        using (OhanamiDatabase.Open(path)) { }
        Assert.Single(Directory.GetFiles(dir.Path, "*.bak-*"));
    }

    // ---------------------------------------------------------------- search_text の model_id

    [Fact]
    public async Task Search_text_model_id_may_be_null_but_must_still_point_at_a_model_when_present()
    {
        using var dir = new TempDirectory();
        using var db = OhanamiDatabase.Open(dir.File("a.db"));
        await db.WriteAsync((c, tx) =>
        {
            TestDb.Exec(c, tx, $"INSERT INTO images(hash, size, format, first_seen_at) VALUES ('{Hash1}', 1, 'png', 't'), ('{Hash2}', 1, 'png', 't')");
            TestDb.Exec(c, tx, "INSERT INTO models(id, kind, name, spec_json, created_at) VALUES ('vlm-a', 'vlm', 'a', '{}', 't')");
            TestDb.Exec(c, tx, $"INSERT INTO search_text(hash, norm_text, model_id) VALUES ('{Hash1}', 'x', NULL)");   // 手動タグだけの画像(caption が無い)
            TestDb.Exec(c, tx, $"INSERT INTO search_text(hash, norm_text, model_id) VALUES ('{Hash2}', 'y', 'vlm-a')");
        }, Ct);
        await Assert.ThrowsAsync<SqliteException>(() => db.WriteAsync((c, tx) =>
            TestDb.Exec(c, tx, $"UPDATE search_text SET model_id = 'no-such-model' WHERE hash = '{Hash1}'"), Ct));   // 存在しないモデルは外部キーで拒む

        using SqliteConnection read = db.CreateReadConnection();
        Assert.Equal(1, TestDb.Long(read, "SELECT count(*) FROM search_text WHERE model_id IS NULL"));
        Assert.Equal(0, TestDb.Long(read, "SELECT [notnull] FROM pragma_table_info('search_text') WHERE name = 'model_id'"));
        Assert.Equal(1, TestDb.Long(read, "SELECT [notnull] FROM pragma_table_info('search_text') WHERE name = 'norm_text'"));
        Assert.Contains("STRICT", TestDb.Text(read, "SELECT sql FROM sqlite_master WHERE name = 'search_text'"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 移行のあとの索引

    [Fact]
    public async Task After_the_migration_the_full_text_index_is_rebuilt_from_captions_on_the_next_open_and_search_keeps_working()
    {
        using var dir = new TempDirectory();
        string path = dir.File("a.db");
        VlmSpec spec = VlmCatalog.Current;
        string modelId = spec.ModelId;
        // v2 のアプリが作った DB: caption 2 枚(1 枚は索引に入っている)
        using (var v2 = OhanamiDatabase.Open(path, V2Only))
        {
            await ModelRegistry.EnsureVlmModelRegisteredAsync(v2, "A", spec, new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), Ct);
            await v2.WriteAsync((c, tx) =>
            {
                TestDb.Exec(c, tx, "INSERT INTO runs(id, job_kind, started_at, app_version, runtime_json) VALUES (1, 'vlm', 't', 'v', '{}')");
                TestDb.Exec(c, tx, $"INSERT INTO images(hash, size, format, first_seen_at) VALUES ('{Hash1}', 1, 'png', 't'), ('{Hash2}', 1, 'png', 't')");
            }, Ct);
            var index = new FtsIndex(v2);
            var store = new CaptionStore(v2, index);
            await store.SaveAsync(1, new CaptionResult(Hash1, modelId, "ガチャ10連", "説明", ["銀髪"], "stop", 1, "{}", null), new DateTimeOffset(2026, 11, 6, 0, 1, 0, TimeSpan.Zero), Ct);
            await store.SaveAsync(1, new CaptionResult(Hash2, modelId, "", "猫の絵", [], "stop", 1, "{}", null), new DateTimeOffset(2026, 11, 6, 0, 2, 0, TimeSpan.Zero), Ct);
        }

        using var db = OhanamiDatabase.Open(path);       // v3 への移行(search_text が空になり、規約の記録が消える)
        using (SqliteConnection read = db.CreateReadConnection())
        {
            Assert.Equal(0, TestDb.Long(read, "SELECT count(*) FROM search_text"));
            Assert.Null(FtsIndex.StoredSpec(read, null));
        }

        var after = new FtsIndex(db);
        await after.EnsureAsync(Ct);                     // 規約が無いので作り直す
        Assert.Equal(1, after.RebuildCount);
        Assert.Equal((2, 0), after.LastRebuild);
        using SqliteConnection r2 = db.CreateReadConnection();
        Assert.Equal(2, TestDb.Long(r2, "SELECT count(*) FROM search_text"));
        Assert.Equal(2, TestDb.Long(r2, "SELECT count(*) FROM search_text WHERE model_id = '" + modelId + "'"));
        Assert.Equal(1, TestDb.Long(r2, "SELECT count(*) FROM fts_docs"
            + " WHERE fts_docs MATCH '" + QueryPlanner.PlanSearch("ガチャ").FtsMatch!.Replace("'", "''", StringComparison.Ordinal) + "'"));
    }
}
