using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Ohanami.Core.Data;

/// <summary>スキーマの版 N から N+1 への移行 1 段。トランザクションの中で呼ばれる(失敗したらその段はなかったことになる)。</summary>
internal sealed record Migration(int FromVersion, Action<SqliteConnection, SqliteTransaction> Apply);

/// <summary>
/// スキーマの定義(初期の版 1 の DDL と、版ごとのマイグレーション)。マイグレーションは版ごとに明示的な関数を並べる(リフレクションで探さない)。
/// <para>
/// **新規作成と移行は同じ経路を通る**: 空の DB には「版 1 の DDL を作る → マイグレーションを順に全部適用する」。
/// 最新のスキーマの DDL を別に持たないので、新規の DB と移行した DB のスキーマが食い違うことが構造的に起きない
/// (v2 以降を足すときも、マイグレーションを書くだけでよい)。
/// </para>
/// <para>
/// マイグレーションの適用中は <c>PRAGMA foreign_keys = OFF</c>(表の作り直しに要る。適用の前後で ON に戻る)で、各段のあとに
/// <c>PRAGMA foreign_key_check</c> を行い、1 行でも返れば例外にして、その段を巻き戻す。
/// 既存の DB に適用する前に、<c>VACUUM INTO</c> で複製を取る(<see cref="OhanamiDatabase"/>)。
/// </para>
/// テストでは別の定義(版 2 など)を渡して、マイグレーションの枠を確かめる。
/// </summary>
internal sealed class DatabaseSchema
{
    public int CurrentVersion { get; }

    /// <summary>空の DB に、**版 1** のスキーマを作る(トランザクションの中で呼ばれる)。版 2 以降は <see cref="Migrations"/> が作る。</summary>
    public Action<SqliteConnection, SqliteTransaction> CreateInitial { get; }

    /// <summary>版 1 → 2、2 → 3、… の順に隙間なく並べる。</summary>
    public IReadOnlyList<Migration> Migrations { get; }

    /// <summary>最新の版で存在するべき表(開くときの検査用)。</summary>
    public IReadOnlyList<string> ExpectedTables { get; }

    public DatabaseSchema(
        int currentVersion,
        Action<SqliteConnection, SqliteTransaction> createInitial,
        IReadOnlyList<Migration> migrations,
        IReadOnlyList<string> expectedTables)
    {
        if (currentVersion < 1) throw new ArgumentOutOfRangeException(nameof(currentVersion), "版は 1 以上");
        if (migrations.Count != currentVersion - 1)
            throw new ArgumentException($"マイグレーションの数 {migrations.Count} が、版 {currentVersion} に必要な数 {currentVersion - 1} と違う", nameof(migrations));
        for (int i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].FromVersion != i + 1)
                throw new ArgumentException($"{i} 番目のマイグレーションの元の版が {migrations[i].FromVersion}(期待は {i + 1}。隙間なく並べる)", nameof(migrations));
        }
        CurrentVersion = currentVersion;
        CreateInitial = createInitial;
        Migrations = migrations;
        ExpectedTables = expectedTables;
    }

    /// <summary>製品のスキーマ。現行は v3(v1 の DDL を作ってから、v1 → v2 → v3 のマイグレーションを適用する。新規作成も同じ経路)。</summary>
    public static DatabaseSchema Default { get; } = new(
        SchemaV3.Version,
        (conn, tx) => Exec(conn, tx, SchemaV1.Ddl),
        migrations: [SchemaV2.FromV1, SchemaV3.FromV2],
        expectedTables: [.. SchemaV1.Tables, .. SchemaV2.Tables, .. SchemaV3.Tables]);

    internal static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
