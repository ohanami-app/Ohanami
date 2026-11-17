using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Xunit;

namespace Ohanami.Core.Tests.Data;

/// <summary>
/// スキーマ v1 の制約と、書き込み・読み取りの接続の挙動。
/// 方針(決定 A): 構造の制約(NOT NULL / PK / FK / UNIQUE)と、変わらない形式の制約(hash の長さ、enabled の 0/1)は DB が守る。
/// 列挙の値は DB は検査せず、C# の明示的な変換が検査する。
/// </summary>
public sealed class SchemaConstraintTests : IDisposable
{
    private static readonly string Hash1 = new('a', 64);
    private static readonly string Hash2 = new('b', 64);

    private readonly TempDirectory _dir = new();
    private readonly OhanamiDatabase _db;

    public SchemaConstraintTests()
    {
        _db = OhanamiDatabase.Open(_dir.File("a.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        _dir.Dispose();
    }

    private Task Write(string sql) => _db.WriteAsync((c, tx) => TestDb.Exec(c, tx, sql), TestContext.Current.CancellationToken);

    private long Count(string sql)
    {
        using var read = _db.CreateReadConnection();
        return TestDb.Long(read, sql);
    }

    private Task SeedImage(string hash) =>
        Write($"INSERT INTO images(hash, size, format, first_seen_at) VALUES ('{hash}', 10, '{DbEnums.ToDb(ImageFormat.Png)}', 't')");

    private Task SeedModelAndRun() => Write(
        $"INSERT INTO models(id, kind, name, spec_json, created_at) VALUES ('m1', '{DbEnums.ToDb(ModelKind.ImageEmbedding)}', 'test', '{{}}', 't');" +
        $"INSERT INTO runs(job_kind, started_at, app_version, runtime_json) VALUES ('{DbEnums.ToDb(JobKind.Embed)}', 't', 'test', '{{}}');");

    [Fact]
    public async Task Foreign_keys_are_enforced_on_the_writer()
    {
        // images に無い hash の image_paths は入れられない
        await Write("INSERT INTO folders(id, path, added_at) VALUES (1, 'C:\\a', 't')");
        await Assert.ThrowsAsync<SqliteException>(() => Write(
            $"INSERT INTO image_paths(path, hash, folder_id, mtime_ticks, size) VALUES ('C:\\a\\1.png', '{Hash1}', 1, 0, 0)"));
        Assert.Equal(0, Count("SELECT count(*) FROM image_paths"));
    }

    [Fact]
    public async Task Hash_length_check_and_enabled_check_are_enforced()
    {
        await Assert.ThrowsAsync<SqliteException>(() => Write("INSERT INTO images(hash, size, format, first_seen_at) VALUES ('short', 1, 'png', 't')"));
        await Assert.ThrowsAsync<SqliteException>(() => Write("INSERT INTO folders(path, enabled, added_at) VALUES ('C:\\a', 2, 't')"));
        await Write("INSERT INTO folders(path, enabled, added_at) VALUES ('C:\\a', 0, 't')");
    }

    [Fact]
    public async Task Enum_columns_have_no_check_constraint_and_unknown_values_are_caught_on_read()
    {
        // DB は知らない値を受け入れる(CHECK が無い)。落とすのは読み取り側の明示的な変換。
        await Write($"INSERT INTO images(hash, size, format, first_seen_at) VALUES ('{Hash1}', 1, 'avif', 't')");
        using var read = _db.CreateReadConnection();
        string stored = TestDb.Text(read, "SELECT format FROM images");
        Assert.Equal("avif", stored);
        var e = Assert.Throws<UnknownEnumValueException>(() => DbEnums.ImageFormatFromDb(stored));
        Assert.Equal("images.format", e.Where);
        Assert.Equal("avif", e.Value);
    }

    [Fact]
    public async Task Strict_tables_reject_values_of_the_wrong_type()
    {
        await Assert.ThrowsAsync<SqliteException>(() => Write("INSERT INTO images(hash, size, format, first_seen_at) VALUES ('" + Hash1 + "', 'not a number', 'png', 't')"));
    }

    [Fact]
    public async Task Paths_are_unique_ignoring_ascii_case_and_cascade_with_the_folder()
    {
        await SeedImage(Hash1);
        await Write("INSERT INTO folders(id, path, added_at) VALUES (1, 'C:\\Shots', 't')");
        await Assert.ThrowsAsync<SqliteException>(() => Write("INSERT INTO folders(path, added_at) VALUES ('c:\\shots', 't')"));

        await Write($"INSERT INTO image_paths(path, hash, folder_id, mtime_ticks, size) VALUES ('C:\\Shots\\A.png', '{Hash1}', 1, 5, 10)");
        await Assert.ThrowsAsync<SqliteException>(() => Write(
            $"INSERT INTO image_paths(path, hash, folder_id, mtime_ticks, size) VALUES ('c:\\shots\\a.PNG', '{Hash1}', 1, 5, 10)"));

        // 同じ内容(hash)の別パスは入れられる(1 ハッシュに複数パス)
        await Write($"INSERT INTO image_paths(path, hash, folder_id, mtime_ticks, size) VALUES ('C:\\Shots\\copy.png', '{Hash1}', 1, 5, 10)");
        Assert.Equal(2, Count("SELECT count(*) FROM image_paths"));

        // フォルダを消すと、パスは消え、画像(images)は残る
        await Write("DELETE FROM folders WHERE id = 1");
        Assert.Equal(0, Count("SELECT count(*) FROM image_paths"));
        Assert.Equal(1, Count("SELECT count(*) FROM images"));
    }

    [Fact]
    public async Task Stage_status_round_trips_through_the_explicit_enum_conversion()
    {
        await SeedImage(Hash1);
        await SeedModelAndRun();
        await _db.WriteAsync((c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO stage_status(hash, stage, status, model_id, run_id, settings_json, error, attempts, updated_at) " +
                              "VALUES ($h, $stage, $status, 'm1', 1, NULL, NULL, 1, 't')";
            cmd.Parameters.AddWithValue("$h", Hash1);
            cmd.Parameters.AddWithValue("$stage", DbEnums.ToDb(Stage.Embed));
            cmd.Parameters.AddWithValue("$status", DbEnums.ToDb(StageStatus.Done));
            cmd.ExecuteNonQuery();
        }, TestContext.Current.CancellationToken);

        using var read = _db.CreateReadConnection();
        using var q = read.CreateCommand();
        q.CommandText = "SELECT stage, status, model_id, run_id, attempts FROM stage_status WHERE hash = $h";
        q.Parameters.AddWithValue("$h", Hash1);
        using var r = q.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(Stage.Embed, DbEnums.StageFromDb(r.GetString(0)));
        Assert.Equal(StageStatus.Done, DbEnums.StageStatusFromDb(r.GetString(1)));
        Assert.Equal("m1", r.GetString(2));
        Assert.Equal(1, r.GetInt64(3));
        Assert.Equal(1, r.GetInt64(4));
        Assert.False(r.Read());
    }

    [Fact]
    public async Task Stage_status_requires_an_existing_model_run_and_image()
    {
        await SeedImage(Hash1);
        await SeedModelAndRun();
        const string insert = "INSERT INTO stage_status(hash, stage, status, model_id, run_id, updated_at) VALUES ('{0}', 'embed', 'done', '{1}', {2}, 't')";
        await Assert.ThrowsAsync<SqliteException>(() => Write(string.Format(insert, Hash1, "no-such-model", 1)));
        await Assert.ThrowsAsync<SqliteException>(() => Write(string.Format(insert, Hash1, "m1", 999)));
        await Assert.ThrowsAsync<SqliteException>(() => Write(string.Format(insert, Hash2, "m1", 1)));
        await Write(string.Format(insert, Hash1, "m1", 1));
        // 同じ (hash, stage) は 1 行だけ
        await Assert.ThrowsAsync<SqliteException>(() => Write(string.Format(insert, Hash1, "m1", 1)));
    }

    [Fact]
    public async Task Image_vectors_round_trip_a_full_size_embedding_and_are_unique_per_hash_and_model()
    {
        await SeedImage(Hash1);
        await SeedModelAndRun();
        var vector = new float[1152];
        for (int i = 0; i < vector.Length; i++) vector[i] = (i % 17) / 17f - 0.5f;
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        Assert.Equal(4608, bytes.Length);

        void Insert(SqliteConnection c, SqliteTransaction tx)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO image_vectors(hash, model_id, embedding) VALUES ($h, 'm1', $e)";
            cmd.Parameters.AddWithValue("$h", Hash1);
            cmd.Parameters.AddWithValue("$e", bytes);
            cmd.ExecuteNonQuery();
        }
        await _db.WriteAsync(Insert, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SqliteException>(() => _db.WriteAsync(Insert, TestContext.Current.CancellationToken)); // UNIQUE (hash, model_id)

        using var read = _db.CreateReadConnection();
        using var q = read.CreateCommand();
        q.CommandText = "SELECT embedding FROM image_vectors";
        byte[] back = (byte[])q.ExecuteScalar()!;
        Assert.Equal(bytes, back);
    }

    [Fact]
    public async Task Tags_and_image_tags_exist_and_are_empty_and_enforce_their_keys()
    {
        Assert.Equal(0, Count("SELECT count(*) FROM tags"));
        Assert.Equal(0, Count("SELECT count(*) FROM image_tags"));
        await SeedImage(Hash1);
        await Write($"INSERT INTO tags(id, name, kind) VALUES (1, 'キャラA', '{DbEnums.ToDb(TagKind.Character)}')");
        await Assert.ThrowsAsync<SqliteException>(() => Write($"INSERT INTO tags(name, kind) VALUES ('キャラA', '{DbEnums.ToDb(TagKind.Character)}')")); // UNIQUE (name, kind)
        await Write($"INSERT INTO tags(name, kind) VALUES ('キャラA', '{DbEnums.ToDb(TagKind.Manual)}')"); // 種類が違えば同名も可
        await Write($"INSERT INTO image_tags(hash, tag_id, source, score) VALUES ('{Hash1}', 1, '{DbEnums.ToDb(TagSource.Manual)}', NULL)");
        await Assert.ThrowsAsync<SqliteException>(() => Write($"INSERT INTO image_tags(hash, tag_id, source) VALUES ('{Hash1}', 999, 'manual')"));
    }

    [Fact]
    public async Task A_failing_write_rolls_back_everything_in_that_call()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.WriteAsync((c, tx) =>
        {
            TestDb.Exec(c, tx, "INSERT INTO folders(path, added_at) VALUES ('C:\\a', 't')");
            TestDb.Exec(c, tx, "INSERT INTO folders(path, added_at) VALUES ('C:\\b', 't')");
            throw new InvalidOperationException("途中で失敗");
        }, TestContext.Current.CancellationToken));
        Assert.Equal(0, Count("SELECT count(*) FROM folders"));
        await Write("INSERT INTO folders(path, added_at) VALUES ('C:\\c', 't')"); // 書き込みの接続は壊れていない
        Assert.Equal(1, Count("SELECT count(*) FROM folders"));
    }

    [Fact]
    public async Task Concurrent_writes_are_serialized_and_all_succeed()
    {
        const int n = 64;
        await Task.WhenAll(Enumerable.Range(0, n).Select(i => Task.Run(() =>
            _db.WriteAsync((c, tx) => TestDb.Exec(c, tx, $"INSERT INTO folders(path, added_at) VALUES ('C:\\f{i}', 't')"), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));
        Assert.Equal(n, Count("SELECT count(*) FROM folders"));
    }

    [Fact]
    public async Task A_cancelled_write_does_not_run()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "INSERT INTO folders(path, added_at) VALUES ('C:\\a', 't')"), cts.Token));
        Assert.Equal(0, Count("SELECT count(*) FROM folders"));
    }

    [Fact]
    public async Task Read_connections_are_query_only_and_have_foreign_keys_on()
    {
        await Write("INSERT INTO folders(path, added_at) VALUES ('C:\\a', 't')");
        using var read = _db.CreateReadConnection();
        Assert.Equal(1, TestDb.Long(read, "PRAGMA foreign_keys"));
        Assert.Throws<SqliteException>(() => TestDb.Exec(read, "INSERT INTO folders(path, added_at) VALUES ('C:\\b', 't')"));
    }

    [Fact]
    public async Task Readers_see_committed_data_while_the_writer_stays_open()
    {
        // WAL: 読み取りは書き込みを妨げず、コミット済みの内容が見える
        using var read = _db.CreateReadConnection();
        Assert.Equal(0, TestDb.Long(read, "SELECT count(*) FROM folders"));
        await Write("INSERT INTO folders(path, added_at) VALUES ('C:\\a', 't')");
        Assert.Equal(1, TestDb.Long(read, "SELECT count(*) FROM folders"));
    }

    [Fact]
    public void Ddl_has_no_check_constraint_on_enum_columns()
    {
        // 決定 A の回帰防止: CHECK は hash の長さ・小文字と enabled の 0/1 だけ。列挙の値を決め打ちする CHECK を足したら落ちる。
        using var read = _db.CreateReadConnection();
        var checks = new List<string>();
        using var cmd = read.CreateCommand();
        cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table'";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string sql = r.GetString(1);
            int i = 0;
            while ((i = sql.IndexOf("CHECK", i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = sql.IndexOf('\n', i);
                checks.Add(r.GetString(0) + ": " + sql[i..(end < 0 ? sql.Length : end)].Trim());
                i += 5;
            }
        }
        Assert.Equal(4, checks.Count);
        Assert.Contains(checks, c => c.StartsWith("images:", StringComparison.Ordinal) && c.Contains("length(hash) = 64", StringComparison.Ordinal));
        Assert.Contains(checks, c => c.StartsWith("images:", StringComparison.Ordinal) && c.Contains("hash = lower(hash)", StringComparison.Ordinal));
        Assert.Contains(checks, c => c.StartsWith("folders:", StringComparison.Ordinal) && c.Contains("enabled IN (0, 1)", StringComparison.Ordinal));
        Assert.Contains(checks, c => c.StartsWith("captions:", StringComparison.Ordinal) && c.Contains("attempt >= 1", StringComparison.Ordinal)); // v2: 試行の番号の下限(構造の制約。列挙の値ではない)
    }

    [Fact]
    public async Task Uppercase_hex_hash_is_rejected_so_one_image_never_gets_two_identifiers()
    {
        string upper = new('A', 64);
        await Assert.ThrowsAsync<SqliteException>(() => Write($"INSERT INTO images(hash, size, format, first_seen_at) VALUES ('{upper}', 1, 'png', 't')"));
        await SeedImage(new string('a', 64));
        Assert.Equal(1, Count("SELECT count(*) FROM images"));
    }

    [Fact]
    public async Task Tag_names_are_unique_ignoring_ascii_case_per_kind_but_japanese_is_exact()
    {
        await Write($"INSERT INTO tags(name, kind) VALUES ('Alice', '{DbEnums.ToDb(TagKind.Character)}')");
        await Assert.ThrowsAsync<SqliteException>(() => Write($"INSERT INTO tags(name, kind) VALUES ('ALICE', '{DbEnums.ToDb(TagKind.Character)}')"));
        await Assert.ThrowsAsync<SqliteException>(() => Write($"INSERT INTO tags(name, kind) VALUES ('alice', '{DbEnums.ToDb(TagKind.Character)}')"));
        await Write($"INSERT INTO tags(name, kind) VALUES ('alice', '{DbEnums.ToDb(TagKind.Manual)}')"); // 種類が違えば同名も可
        // 日本語(ひらがな / カタカナなど)には効かない: 別のタグ
        await Write($"INSERT INTO tags(name, kind) VALUES ('ありす', '{DbEnums.ToDb(TagKind.Character)}')");
        await Write($"INSERT INTO tags(name, kind) VALUES ('アリス', '{DbEnums.ToDb(TagKind.Character)}')");
        Assert.Equal(4, Count("SELECT count(*) FROM tags"));
    }

    [Fact]
    public void Image_vectors_unique_index_starts_with_model_id_so_one_model_is_read_by_the_index_prefix()
    {
        using var read = _db.CreateReadConnection();
        using var cmd = read.CreateCommand();
        cmd.CommandText = """
            SELECT ii.name
            FROM sqlite_master m, pragma_index_info(m.name) ii
            WHERE m.type = 'index' AND m.tbl_name = 'image_vectors' AND m.name LIKE 'sqlite_autoindex%'
            ORDER BY ii.seqno
            """;
        var columns = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) columns.Add(r.GetString(0));
        Assert.Equal(["model_id", "hash"], columns);
    }

    [Fact]
    public async Task Busy_timeout_is_set_on_writer_and_readers()
    {
        long writer = await _db.WriteAsync((c, tx) => TestDb.Long(c, "PRAGMA busy_timeout"), TestContext.Current.CancellationToken);
        Assert.Equal(5000, writer);
        using var read = _db.CreateReadConnection();
        Assert.Equal(5000, TestDb.Long(read, "PRAGMA busy_timeout"));
    }
}
