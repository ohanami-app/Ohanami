using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.FullText;

namespace Ohanami.Core;

/// <summary>
/// 見つからない画像の記録を、明示的に消す(消えたファイルの記録は残す。消す操作は利用者が明示的にする。ADR 0007)。
/// <list type="bullet">
/// <item><b>対象</b>: 現存するパス(<c>missing_since</c> が無いパス)が 1 つも無い画像。<b>パスの行が 0 行の画像を含む</b>(フォルダの解除は image_paths を CASCADE で消し、images と結果は残す。
///   書き込みの途中の内容のハッシュも、パスが 0 行になる)。SQL は「NOT EXISTS(missing_since が NULL のパス)」の形。
///   根に到達できないフォルダ(外付けドライブを抜いた、など)の配下のパスは、印が付かない(走査が「見えない」と判断しない)ので、対象にならない。無効なフォルダの配下も同じ。</item>
/// <item><b>消すもの</b>: その画像の images の行と、それにつながる結果(image_tags、captions、stage_status、image_vectors、search_text と fts_docs の行、image_paths)。1 つのトランザクション。
///   hash を参照する表に ON DELETE は無く(CASCADE は folder_id だけ)、foreign_keys は接続ごとに ON なので、<b>子の表から順に明示的に消してから images を消す</b>。
///   索引の行は <see cref="FtsIndex.RemoveImage"/>(索引が使えないときは、外部キーのために search_text だけ直接消す。fts_docs は派生データで、次の作り直しで揃う)。
///   どの画像にも付かなくなった手動タグの名前(tags の行)も消す(<c>ManualTags</c> が最後の 1 枚から外したときと同じ)。サムネイルのディスクキャッシュ(ハッシュが鍵)は、残しても害が無いので消さない。</item>
/// <item>対象は<b>消すトランザクションの中で</b>決める(数えてから消すまでの間に、走査が戻した画像を消さない)。消したあと、<c>onRemoved</c> に消した hash を渡す
///   (メモリ上のベクトルの表から外す。<see cref="Search.VectorTable.Remove"/>)。</item>
/// </list>
/// ジョブの実行中に消さない・待機から動き出さない、といった状態の規則は <see cref="Indexing.Indexer"/> が持つ(消すのは <see cref="Indexing.Indexer.PurgeMissingAsync"/>)。
/// </summary>
internal sealed class MissingImagePurger
{
    private const int Chunk = 500;

    private readonly OhanamiDatabase _db;
    private readonly FtsIndex _index;
    private readonly Action<IReadOnlyList<string>> _onRemoved;

    public MissingImagePurger(OhanamiDatabase db, FtsIndex index, Action<IReadOnlyList<string>> onRemoved)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(onRemoved);
        _db = db;
        _index = index;
        _onRemoved = onRemoved;
    }

    private const string TargetCondition = "NOT EXISTS (SELECT 1 FROM image_paths p WHERE p.hash = i.hash AND p.missing_since IS NULL)";

    /// <summary>消せる画像(見つからない画像)の数。</summary>
    public Task<int> CountAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return _db.ReadSnapshot(conn =>
            {
                using SqliteCommand cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT count(*) FROM images i WHERE {TargetCondition}";
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            });
        }, ct);

    /// <summary>一覧の上限の既定。</summary>
    public const int DefaultListLimit = 200;

    /// <summary>
    /// 消せる画像(見つからない画像)の一覧(読むだけ)。条件は消すときと同じ <see cref="TargetCondition"/>。無くなった日時(パスの行の見つからない印の最大)の新しい順で、
    /// 日時の無いもの(パスの行が 0 行)は最後。同じ日時は hash 順。<see cref="MissingImageList.Total"/> と <see cref="MissingImageList.WithManualTags"/> は、上限で切る前の数
    /// (1 つの読み取りの中で数えるので、一覧と食い違わない)。
    /// </summary>
    public Task<MissingImageList> ListAsync(int limit = DefaultListLimit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return _db.ReadSnapshot(conn =>
            {
                int total, withManual;
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"""
                        SELECT count(*),
                               coalesce(sum(EXISTS (SELECT 1 FROM image_tags it WHERE it.hash = i.hash AND it.source = $manual)), 0)
                        FROM images i WHERE {TargetCondition}
                        """;
                    cmd.Parameters.AddWithValue("$manual", DbEnums.ToDb(TagSource.Manual));
                    using SqliteDataReader r = cmd.ExecuteReader();
                    r.Read();
                    total = r.GetInt32(0);
                    withManual = r.GetInt32(1);
                }

                var items = new List<MissingImageItem>();
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"""
                        SELECT i.hash,
                               (SELECT p.path FROM image_paths p WHERE p.hash = i.hash ORDER BY p.missing_since DESC, p.path COLLATE NOCASE, p.path LIMIT 1),
                               (SELECT max(p.missing_since) FROM image_paths p WHERE p.hash = i.hash) AS since,
                               (SELECT count(*) FROM image_tags it WHERE it.hash = i.hash AND it.source = $manual),
                               EXISTS (SELECT 1 FROM captions c WHERE c.hash = i.hash)
                        FROM images i
                        WHERE {TargetCondition}
                        ORDER BY since DESC, i.hash
                        LIMIT $limit
                        """;
                    cmd.Parameters.AddWithValue("$manual", DbEnums.ToDb(TagSource.Manual));
                    cmd.Parameters.AddWithValue("$limit", limit);
                    using SqliteDataReader r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        items.Add(new MissingImageItem(
                            r.GetString(0),
                            r.IsDBNull(1) ? null : r.GetString(1),
                            r.IsDBNull(2) ? null : DbTime.Parse(r.GetString(2)),
                            r.GetInt32(3),
                            r.GetInt64(4) != 0));
                    }
                }
                return new MissingImageList(items, total, withManual);
            });
        }, ct);
    }

    /// <summary>見つからない画像の記録を消す。消した画像の数を返す。<paramref name="ct"/> は、索引の準備を待つ間だけ(始めた書き込みは確定する)。</summary>
    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        bool indexUsable = await _index.TryEnsureUsableAsync(ct).ConfigureAwait(false);
        IReadOnlyList<string> removed = await _db.WriteAsync((conn, tx) =>
        {
            if (indexUsable) _index.RequireCurrent(conn, tx);
            var hashes = new List<string>();
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"SELECT i.hash FROM images i WHERE {TargetCondition} ORDER BY i.hash";
                using SqliteDataReader r = cmd.ExecuteReader();
                while (r.Read()) hashes.Add(r.GetString(0));
            }
            if (hashes.Count == 0) return hashes;

            var tagIds = new HashSet<long>();
            foreach (string[] chunk in hashes.Chunk(Chunk))
            {
                (string list, SqliteParameter[] parameters) = InList(chunk);
                using (SqliteCommand cmd = Command(conn, tx, $"SELECT DISTINCT tag_id FROM image_tags WHERE hash IN ({list})", parameters))
                using (SqliteDataReader r = cmd.ExecuteReader())
                    while (r.Read()) tagIds.Add(r.GetInt64(0));

                // 子の表から順に(hash を参照する表に ON DELETE は無い)
                foreach (string table in new[] { "image_tags", "captions", "stage_status", "image_vectors" })
                    Execute(conn, tx, $"DELETE FROM {table} WHERE hash IN ({list})", parameters);
                if (indexUsable)
                    foreach (string hash in chunk) _index.RemoveImage(conn, tx, hash);   // fts_docs と search_text の行
                Execute(conn, tx, $"DELETE FROM search_text WHERE hash IN ({list})", parameters);   // 索引が使えないとき(外部キーのため)。使えたときは、もう無い
                Execute(conn, tx, $"DELETE FROM image_paths WHERE hash IN ({list})", parameters);   // 残っているのは、見つからない印のパスだけ
                Execute(conn, tx, $"DELETE FROM images WHERE hash IN ({list})", parameters);
            }

            // どの画像にも付かなくなった手動タグの名前
            foreach (long[] chunk in tagIds.Chunk(Chunk))
            {
                var names = new List<string>();
                var parameters = new List<SqliteParameter>();
                for (int i = 0; i < chunk.Length; i++)
                {
                    names.Add("$t" + i.ToString(CultureInfo.InvariantCulture));
                    parameters.Add(new SqliteParameter(names[i], chunk[i]));
                }
                Execute(conn, tx,
                    $"DELETE FROM tags WHERE id IN ({string.Join(", ", names)}) AND NOT EXISTS (SELECT 1 FROM image_tags it WHERE it.tag_id = tags.id)",
                    [.. parameters]);
            }
            return hashes;
        }, CancellationToken.None).ConfigureAwait(false);

        if (removed.Count > 0) _onRemoved(removed);
        return removed.Count;
    }

    private static (string List, SqliteParameter[] Parameters) InList(string[] hashes)
    {
        var names = new string[hashes.Length];
        var parameters = new SqliteParameter[hashes.Length];
        for (int i = 0; i < hashes.Length; i++)
        {
            names[i] = "$h" + i.ToString(CultureInfo.InvariantCulture);
            parameters[i] = new SqliteParameter(names[i], hashes[i]);
        }
        return (string.Join(", ", names), parameters);
    }

    private static SqliteCommand Command(SqliteConnection conn, SqliteTransaction tx, string sql, SqliteParameter[] parameters)
    {
        SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        // 同じ SqliteParameter を別のコマンドに付け直せないので、値から作り直す
        foreach (SqliteParameter p in parameters) cmd.Parameters.AddWithValue(p.ParameterName, p.Value);
        return cmd;
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql, SqliteParameter[] parameters)
    {
        using SqliteCommand cmd = Command(conn, tx, sql, parameters);
        cmd.ExecuteNonQuery();
    }
}
