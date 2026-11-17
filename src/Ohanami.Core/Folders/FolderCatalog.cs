using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.Scanning;

namespace Ohanami.Core;

/// <summary>
/// 走査するフォルダの登録・解除・有効/無効。
/// 登録時にパスを検査する: 存在するディレクトリ、フルパスに直す、末尾の区切りを除く(ドライブの根 "C:\" は区切りを残す)、
/// 登録済み(大文字小文字を区別しない)と入れ子(登録済みの中、または登録済みを含む)は拒否。拒否は <see cref="FolderRegistrationException"/>。
/// 解除は、そのフォルダの image_paths と file_issues を消す(CASCADE)。images と、ベクトルなどの結果は残す
/// (印を付けて残す。ADR 0007。明示的に消す操作は <see cref="MissingImagePurger"/>)。
/// </summary>
public sealed class FolderCatalog
{
    private readonly OhanamiDatabase _db;
    private readonly TimeProvider _time;
    private readonly Action? _afterChange;

    /// <param name="afterChange">登録・解除・有効/無効が成功したあとに呼ぶ(<see cref="OhanamiCore"/> がフォルダの監視の <c>Refresh</c> をつなぐ。イベントではなく、明示的な 1 本の呼び出し)。例外は呼び出し側が受ける。</param>
    internal FolderCatalog(OhanamiDatabase db, TimeProvider time, Action? afterChange = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        _db = db;
        _time = time;
        _afterChange = afterChange;
    }

    public IReadOnlyList<FolderInfo> List()
    {
        using SqliteConnection conn = _db.CreateReadConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.id, f.path, f.enabled, f.last_scan_at, f.last_error,
                   (SELECT count(*) FROM image_paths p WHERE p.folder_id = f.id AND p.missing_since IS NULL)
            FROM folders f
            ORDER BY f.id
            """;
        var list = new List<FolderInfo>();
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadFolder(r));
        return list;
    }

    /// <summary>パスを検査して登録する。同時に呼ばれても、検査と挿入は 1 つのトランザクションで行う。</summary>
    public async Task<FolderInfo> AddAsync(string path, CancellationToken ct = default)
    {
        string normalized = NormalizeRegistrationPath(path);
        if (!Directory.Exists(normalized))
            throw new FolderRegistrationException($"存在するディレクトリではない: {normalized}");

        string now = Now();
        string newKey = PathKey.DirectoryKey(normalized);
        FolderInfo added = await _db.WriteAsync((conn, tx) =>
        {
            using (SqliteCommand check = conn.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT path FROM folders";
                using SqliteDataReader r = check.ExecuteReader();
                while (r.Read())
                {
                    string existing = r.GetString(0);
                    string existingKey = PathKey.DirectoryKey(existing);
                    if (string.Equals(existingKey, newKey, StringComparison.Ordinal))
                        throw new FolderRegistrationException($"登録済みのフォルダ: {existing}");
                    if (newKey.StartsWith(existingKey, StringComparison.Ordinal))
                        throw new FolderRegistrationException($"登録済みのフォルダ {existing} の中にあるので登録できない(入れ子): {normalized}");
                    if (existingKey.StartsWith(newKey, StringComparison.Ordinal))
                        throw new FolderRegistrationException($"登録済みのフォルダ {existing} を含むので登録できない(入れ子): {normalized}");
                }
            }

            using SqliteCommand insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO folders(path, enabled, added_at) VALUES ($path, 1, $now) RETURNING id";
            insert.Parameters.AddWithValue("$path", normalized);
            insert.Parameters.AddWithValue("$now", now);
            long id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            return new FolderInfo(id, normalized, true, 0, null, null);
        }, ct).ConfigureAwait(false);
        _afterChange?.Invoke();
        return added;
    }

    /// <summary>登録を解除する。そのフォルダの image_paths・file_issues は消え、images と結果(ベクトルなど)は残る。</summary>
    public async Task RemoveAsync(long folderId, CancellationToken ct = default)
    {
        await _db.WriteAsync((conn, tx) =>
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM folders WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", folderId);
            if (cmd.ExecuteNonQuery() == 0) throw new FolderRegistrationException($"フォルダ id {folderId} は登録されていない");
        }, ct).ConfigureAwait(false);
        _afterChange?.Invoke();
    }

    public async Task SetEnabledAsync(long folderId, bool enabled, CancellationToken ct = default)
    {
        await _db.WriteAsync((conn, tx) =>
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE folders SET enabled = $enabled WHERE id = $id";
            cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", folderId);
            if (cmd.ExecuteNonQuery() == 0) throw new FolderRegistrationException($"フォルダ id {folderId} は登録されていない");
        }, ct).ConfigureAwait(false);
        _afterChange?.Invoke();
    }

    /// <summary>
    /// 登録するパスの正規化: 前後の空白を除く → フルパス(区切りは \ にそろう) → 末尾の区切りを除く(ドライブや UNC の根だけは残す)。
    /// 空・不正なパスは <see cref="FolderRegistrationException"/>。
    /// </summary>
    internal static string NormalizeRegistrationPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new FolderRegistrationException("フォルダのパスが空");
        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new FolderRegistrationException($"パスとして不正: {path}", e);
        }
        string root = Path.GetPathRoot(full) ?? "";
        if (full.Length <= root.Length) return full; // "C:\" など
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private string Now() => DbTime.Format(_time.GetUtcNow());

    private static FolderInfo ReadFolder(SqliteDataReader r) => new(
        Id: r.GetInt64(0),
        Path: r.GetString(1),
        Enabled: r.GetInt64(2) == 1,
        ImageCount: (int)r.GetInt64(5),
        LastScanAt: r.IsDBNull(3) ? null : DbTime.Parse(r.GetString(3)),
        LastError: r.IsDBNull(4) ? null : r.GetString(4));
}
