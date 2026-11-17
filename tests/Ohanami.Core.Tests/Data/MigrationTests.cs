using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Xunit;

namespace Ohanami.Core.Tests.Data;

/// <summary>
/// マイグレーションの枠(ADR 0005)。本物の v1 → v2 は SchemaV2Tests。ここでは、版 1 の上にテスト用の仮の版 2/3 の定義を載せて枠を確かめる。
/// 守り: 適用前のバックアップ(VACUUM INTO)、適用中は foreign_keys = OFF、各段のあとの foreign_key_check、新規作成と移行の経路を 1 つにする。
/// </summary>
public sealed class MigrationTests
{
    /// <summary>版 1 だけの定義(v1 の DB を作る・開く用。製品の Default は新しい版)。マイグレーションの枠のテストは、この上に仮の版 2 を載せて確かめる。</summary>
    private static readonly DatabaseSchema V1Only = new(1, DatabaseSchema.Default.CreateInitial, [], SchemaV1.Tables);

    private static Migration AddFoldersColumn(int from, string column) =>
        new(from, (c, tx) => DatabaseSchema.Exec(c, tx, $"ALTER TABLE folders ADD COLUMN {column} TEXT"));

    private static DatabaseSchema SchemaV2(Migration? step = null) => new(
        2,
        DatabaseSchema.Default.CreateInitial,
        [step ?? AddFoldersColumn(1, "note")],
        SchemaV1.Tables);

    private static List<string> SchemaDump(string path)
    {
        using SqliteConnection raw = TestDb.OpenRaw(path);
        using SqliteCommand cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT type || ' ' || name || ' ' || coalesce(sql, '') FROM sqlite_master ORDER BY type, name";
        var list = new List<string>();
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    [Fact]
    public async Task V1_database_is_migrated_to_v2_and_data_is_kept()
    {
        using var dir = new TempDirectory();
        string path = dir.File("a.db");
        using (var db = OhanamiDatabase.Open(path, V1Only))
        {
            await db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "INSERT INTO folders(path, added_at) VALUES ('C:\\shots', 't')"), TestContext.Current.CancellationToken);
        }

        using (var db = OhanamiDatabase.Open(path, SchemaV2()))
        {
            Assert.Equal(2, db.SchemaVersion);
            using var read = db.CreateReadConnection();
            Assert.Equal(1, TestDb.Long(read, "SELECT count(*) FROM folders WHERE note IS NULL")); // 追加した列が使える
        }
        using var raw = TestDb.OpenRaw(path);
        Assert.Equal(2, TestDb.Long(raw, "PRAGMA user_version"));
    }

    [Fact]
    public void Fresh_v2_database_has_exactly_the_same_schema_as_a_migrated_v1_database()
    {
        // 新規作成も「版 1 を作る → マイグレーションを順に適用」なので、食い違いが構造的に起きない
        using var dir = new TempDirectory();
        string fresh = dir.File("fresh.db");
        string migrated = dir.File("migrated.db");
        using (OhanamiDatabase.Open(fresh, SchemaV2())) { }
        using (OhanamiDatabase.Open(migrated, V1Only)) { }
        using (OhanamiDatabase.Open(migrated, SchemaV2())) { }

        Assert.Equal(SchemaDump(migrated), SchemaDump(fresh));
        using var a = TestDb.OpenRaw(fresh);
        using var b = TestDb.OpenRaw(migrated);
        Assert.Equal(2, TestDb.Long(a, "PRAGMA user_version"));
        Assert.Equal(2, TestDb.Long(b, "PRAGMA user_version"));
        Assert.Equal(TestDb.Long(a, "PRAGMA application_id"), TestDb.Long(b, "PRAGMA application_id"));
    }

    [Fact]
    public void Creating_a_new_database_does_not_make_a_backup()
    {
        using var dir = new TempDirectory();
        string path = dir.File("a.db");
        using (OhanamiDatabase.Open(path, SchemaV2())) { }
        Assert.Empty(Directory.GetFiles(dir.Path, "*.bak-*"));
    }

    [Fact]
    public async Task Existing_database_is_copied_with_vacuum_into_before_migrating()
    {
        using var dir = new TempDirectory();
        string path = dir.File("a.db");
        using (var db = OhanamiDatabase.Open(path, V1Only))
        {
            await db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "INSERT INTO folders(path, added_at) VALUES ('C:\\keep', 't')"), TestContext.Current.CancellationToken);
        }
        using (OhanamiDatabase.Open(path, SchemaV2())) { }

        string backup = path + ".bak-v1";
        Assert.True(File.Exists(backup));
        // 複製は移行前の版 1 のまま(新しい列が無い)で、データを含み、そのまま開ける
        using (var raw = TestDb.OpenRaw(backup))
        {
            Assert.Equal(1, TestDb.Long(raw, "PRAGMA user_version"));
            Assert.Equal(1, TestDb.Long(raw, "SELECT count(*) FROM folders"));
            Assert.Equal(0, TestDb.Long(raw, "SELECT count(*) FROM pragma_table_info('folders') WHERE name = 'note'"));
        }
        using var reopened = OhanamiDatabase.Open(backup, V1Only);
        Assert.Equal(1, reopened.SchemaVersion);
    }

    [Fact]
    public void Backup_never_overwrites_an_existing_file()
    {
        using var dir = new TempDirectory();
        string path = dir.File("a.db");
        using (OhanamiDatabase.Open(path, V1Only)) { }
        File.WriteAllText(path + ".bak-v1", "earlier backup");

        using (OhanamiDatabase.Open(path, SchemaV2())) { }

        Assert.Equal("earlier backup", File.ReadAllText(path + ".bak-v1"));
        Assert.True(File.Exists(path + ".bak-v1-2"));
    }

    [Fact]
    public async Task Foreign_keys_are_off_while_migrating_and_on_again_afterwards()
    {
        using var dir = new TempDirectory();
        string path = dir.File("a.db");
        using (OhanamiDatabase.Open(path, V1Only)) { }

        long seenInside = -1;
        var step = new Migration(1, (c, tx) =>
        {
            seenInside = TestDb.Long(c, "PRAGMA foreign_keys");
            DatabaseSchema.Exec(c, tx, "ALTER TABLE folders ADD COLUMN note TEXT");
        });
        using var db = OhanamiDatabase.Open(path, SchemaV2(step));

        Assert.Equal(0, seenInside);
        using var read = db.CreateReadConnection();
        Assert.Equal(1, TestDb.Long(read, "PRAGMA foreign_keys"));
        // 書き込みの接続も ON に戻っている(親の無い行を入れられない)
        await Assert.ThrowsAsync<SqliteException>(() => db.WriteAsync((c, tx) =>
            TestDb.Exec(c, tx, "INSERT INTO image_paths(path, hash, folder_id, mtime_ticks, size) VALUES ('x', '" + new string('a', 64) + "', 1, 0, 0)"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Migration_that_leaves_a_foreign_key_violation_is_rejected_and_rolled_back()
    {
        using var dir = new TempDirectory();
        string path = dir.File("a.db");
        using (var db = OhanamiDatabase.Open(path, V1Only))
        {
            await db.WriteAsync((c, tx) => TestDb.Exec(c, tx, "INSERT INTO folders(id, path, added_at) VALUES (1, 'C:\\a', 't')"), TestContext.Current.CancellationToken);
        }

        // foreign_keys = OFF の間に、親の無い行を作ってしまう段(表の作り直しの失敗を想定)
        var bad = new Migration(1, (c, tx) =>
        {
            DatabaseSchema.Exec(c, tx, "ALTER TABLE folders ADD COLUMN note TEXT");
            DatabaseSchema.Exec(c, tx, "INSERT INTO image_paths(path, hash, folder_id, mtime_ticks, size) VALUES ('C:\\a\\x.png', '" + new string('a', 64) + "', 1, 0, 0)");
        });
        var e = Assert.Throws<InvalidOperationException>(() => OhanamiDatabase.Open(path, SchemaV2(bad)));
        Assert.Contains("外部キー", e.Message);
        Assert.Contains("image_paths", e.Message);

        using var raw = TestDb.OpenRaw(path);
        Assert.Equal(1, TestDb.Long(raw, "PRAGMA user_version"));
        Assert.Equal(0, TestDb.Long(raw, "SELECT count(*) FROM image_paths"));
        Assert.Equal(0, TestDb.Long(raw, "SELECT count(*) FROM pragma_table_info('folders') WHERE name = 'note'"));
        Assert.True(File.Exists(path + ".bak-v1")); // 複製は残る
    }

    [Fact]
    public void Failed_migration_rolls_back_and_leaves_the_old_version()
    {
        using var dir = new TempDirectory();
        string path = dir.File("a.db");
        using (OhanamiDatabase.Open(path, V1Only)) { }

        var bad = new Migration(1, (c, tx) =>
        {
            DatabaseSchema.Exec(c, tx, "ALTER TABLE folders ADD COLUMN note TEXT");
            throw new InvalidOperationException("途中で失敗");
        });
        Assert.Throws<InvalidOperationException>(() => OhanamiDatabase.Open(path, SchemaV2(bad)));

        using var raw = TestDb.OpenRaw(path);
        Assert.Equal(1, TestDb.Long(raw, "PRAGMA user_version"));
        Assert.Equal(0, TestDb.Long(raw, "SELECT count(*) FROM pragma_table_info('folders') WHERE name = 'note'"));
        // 元の版のアプリでは、まだ開ける
        using var again = OhanamiDatabase.Open(path, V1Only);
        Assert.Equal(1, again.SchemaVersion);
    }

    [Fact]
    public void Migrations_run_in_order_across_several_versions()
    {
        using var dir = new TempDirectory();
        string path = dir.File("a.db");
        using (OhanamiDatabase.Open(path, V1Only)) { }

        var order = new List<int>();
        var schema = new DatabaseSchema(
            3,
            (c, tx) => throw new InvalidOperationException("既存の DB では新規作成は使わない"),
            [
                new Migration(1, (c, tx) => { order.Add(1); DatabaseSchema.Exec(c, tx, "ALTER TABLE folders ADD COLUMN a TEXT"); }),
                new Migration(2, (c, tx) => { order.Add(2); DatabaseSchema.Exec(c, tx, "ALTER TABLE folders ADD COLUMN b TEXT"); }),
            ],
            SchemaV1.Tables);
        using (var db = OhanamiDatabase.Open(path, schema))
        {
            Assert.Equal(3, db.SchemaVersion);
        }
        Assert.Equal([1, 2], order);
        Assert.True(File.Exists(path + ".bak-v1")); // 複製は移行を始める前の版(1)の名前で 1 つだけ
        Assert.Single(Directory.GetFiles(dir.Path, "*.bak-*"));
    }

    [Fact]
    public void New_database_with_several_versions_applies_every_migration_from_v1()
    {
        using var dir = new TempDirectory();
        string path = dir.File("a.db");
        var order = new List<int>();
        var schema = new DatabaseSchema(
            3,
            DatabaseSchema.Default.CreateInitial,
            [
                new Migration(1, (c, tx) => { order.Add(1); DatabaseSchema.Exec(c, tx, "ALTER TABLE folders ADD COLUMN a TEXT"); }),
                new Migration(2, (c, tx) => { order.Add(2); DatabaseSchema.Exec(c, tx, "ALTER TABLE folders ADD COLUMN b TEXT"); }),
            ],
            SchemaV1.Tables);
        using (var db = OhanamiDatabase.Open(path, schema))
        {
            Assert.Equal(3, db.SchemaVersion);
        }
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public void Schema_definition_with_a_gap_in_migrations_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentException>(() => new DatabaseSchema(3, (c, tx) => { }, [AddFoldersColumn(1, "a")], SchemaV1.Tables)); // 数が足りない
        Assert.Throws<ArgumentException>(() => new DatabaseSchema(3, (c, tx) => { }, [AddFoldersColumn(1, "a"), AddFoldersColumn(3, "b")], SchemaV1.Tables)); // 2 → 3 が無い
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatabaseSchema(0, (c, tx) => { }, [], SchemaV1.Tables));
    }

    [Fact]
    public void Database_newer_than_the_schema_definition_is_still_rejected()
    {
        using var dir = new TempDirectory();
        string path = dir.File("a.db");
        using (OhanamiDatabase.Open(path, SchemaV2())) { }
        Assert.Throws<UnknownSchemaVersionException>(() => OhanamiDatabase.Open(path, V1Only)); // v1 のコードで v2 の DB を開く
        Assert.Empty(Directory.GetFiles(dir.Path, "*.bak-*")); // 拒否のときは複製を作らない
    }
}
