using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Ohanami.Core.Tests.Data;

/// <summary>テストごとの一時ディレクトリ。Dispose で消す(DB は接続プールを使わないので、閉じていれば消せる)。</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ohanami-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* 後始末の失敗でテストを落とさない */ }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>テストが DB の中身を直接見る・壊すための、素の接続とクエリの補助。</summary>
internal static class TestDb
{
    public static SqliteConnection OpenRaw(string path)
    {
        var csb = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        return conn;
    }

    public static long Long(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static string Text(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    public static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static void Exec(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
