using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.Search;
using Ohanami.Core.Tests.Data;
using Xunit;
using static Ohanami.Core.Tests.SearchTestSupport;

namespace Ohanami.Core.Tests;

/// <summary>検索していないときの新しい順の一覧(SearchService.ListRecentAsync。ADR 0041)。DB は実物。</summary>
public sealed class RecentListTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static long Ticks(int minutes) => new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc).AddMinutes(minutes).Ticks;

    private static async Task<(TempDirectory Dir, OhanamiDatabase Db, SearchService Service, long Folder)> Environment()
    {
        var (dir, db) = NewDb();
        string model = await RegisterModelAsync(db);
        long folder = await AddFolderAsync(db);
        var table = new VectorTable(model, Dim);
        await table.LoadAsync(db, Ct);
        var path = new VisualPath(table, (_, _) => Task.FromResult(Unit(0)));
        return (dir, db, new SearchService(db, [path], table), folder);
    }

    /// <summary>画像 1 枚を、パスごとの更新日時(分。基準からの差)で入れる。ベクトルは入れない(埋め込みの前の画像)。</summary>
    private static async Task AddAsync(OhanamiDatabase db, long folder, int id, params (string Path, int Minutes)[] paths)
    {
        await AddImageAsync(db, folder, Hash(id), paths.Select(p => p.Path).ToArray());
        await db.WriteAsync((conn, tx) =>
        {
            foreach ((string path, int minutes) in paths)
            {
                using SqliteCommand cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE image_paths SET mtime_ticks = $t WHERE path = $p";
                cmd.Parameters.AddWithValue("$t", Ticks(minutes));
                cmd.Parameters.AddWithValue("$p", path);
                cmd.ExecuteNonQuery();
            }
        });
    }

    [Fact]
    public async Task Images_come_newest_first_by_file_modification_time_including_ones_not_embedded_yet()
    {
        var (dir, db, service, folder) = await Environment();
        using (dir) using (db)
        {
            await AddAsync(db, folder, 1, (@"C:\shots\old.png", 10));
            await AddAsync(db, folder, 2, (@"C:\shots\newest.png", 300));
            await AddAsync(db, folder, 3, (@"C:\shots\middle.png", 100));

            RecentResponse r = await service.ListRecentAsync(300, Ct);

            Assert.Equal([Hash(2), Hash(3), Hash(1)], r.Images.Select(x => x.Hash).ToArray());   // ベクトルが 1 つも無くても出る
            Assert.Equal(3, r.TotalImages);
            Assert.Equal(new DateTimeOffset(new DateTime(Ticks(300), DateTimeKind.Utc)), r.Images[0].ModifiedAt);
            Assert.Equal(TimeSpan.Zero, r.Images[0].ModifiedAt.Offset);
            Assert.True(r.Elapsed >= TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task Images_without_a_present_path_are_left_out_like_in_search()
    {
        var (dir, db, service, folder) = await Environment();
        using (dir) using (db)
        {
            await AddAsync(db, folder, 1, (@"C:\shots\kept.png", 50));
            // パスがすべて missing_since の画像
            await AddImageAsync(db, folder, Hash(2), [], [@"C:\shots\gone.png"]);
            // パスの行が 1 つも無い画像(images の行だけ)
            await db.WriteAsync((conn, tx) =>
            {
                using SqliteCommand cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO images (hash, size, format, first_seen_at) VALUES ($h, 1, 'png', '2026-11-05T00:00:00.000Z')";
                cmd.Parameters.AddWithValue("$h", Hash(3));
                cmd.ExecuteNonQuery();
            }, Ct);

            RecentResponse r = await service.ListRecentAsync(300, Ct);

            Assert.Equal([Hash(1)], r.Images.Select(x => x.Hash).ToArray());
            Assert.Equal(1, r.TotalImages);   // 「見える画像」の数(見つからない画像は数えない)
        }
    }

    [Fact]
    public async Task Without_a_limit_every_image_is_returned_newest_first_even_far_beyond_the_old_cap_of_300()
    {
        // 新しい順の一覧に上限は設けない(ADR 0041。App は上限なしで呼ぶ)。既定の引数も上限なし
        const int count = 2500;
        var (dir, db, service, folder) = await Environment();
        using (dir) using (db)
        {
            await db.WriteAsync((conn, tx) =>
            {
                using SqliteCommand ci = conn.CreateCommand();
                ci.Transaction = tx;
                ci.CommandText = "INSERT INTO images (hash, size, format, first_seen_at) VALUES ($h, 100, 'png', '2026-11-05T00:00:00.000Z')";
                SqliteParameter ih = ci.Parameters.Add("$h", SqliteType.Text);
                using SqliteCommand cp = conn.CreateCommand();
                cp.Transaction = tx;
                cp.CommandText = "INSERT INTO image_paths (path, hash, folder_id, mtime_ticks, size) VALUES ($p, $h, $f, $t, 100)";
                SqliteParameter pp = cp.Parameters.Add("$p", SqliteType.Text);
                SqliteParameter ph = cp.Parameters.Add("$h", SqliteType.Text);
                cp.Parameters.AddWithValue("$f", folder);
                SqliteParameter pt = cp.Parameters.Add("$t", SqliteType.Integer);
                for (int i = 1; i <= count; i++)
                {
                    ih.Value = Hash(i); ci.ExecuteNonQuery();
                    pp.Value = $@"C:\shots\{i:D5}.png"; ph.Value = Hash(i); pt.Value = Ticks(i); cp.ExecuteNonQuery();   // 番号が大きいほど新しい
                }
            }, Ct);

            RecentResponse byDefault = await service.ListRecentAsync(ct: Ct);

            Assert.Equal(count, byDefault.Images.Count);
            Assert.Equal(count, byDefault.TotalImages);
            Assert.Equal(Enumerable.Range(1, count).Reverse().Select(Hash).ToArray(), byDefault.Images.Select(x => x.Hash).ToArray());
            Assert.Equal(count, (await service.ListRecentAsync(int.MaxValue, Ct)).Images.Count);   // App が渡す値(ResultsPanel.RecentLimit)
            Assert.Equal(300, (await service.ListRecentAsync(300, Ct)).Images.Count);                // 上限を指定すれば、切る
        }
    }

    [Fact]
    public async Task The_limit_keeps_the_newest_and_the_total_still_counts_everything()
    {
        var (dir, db, service, folder) = await Environment();
        using (dir) using (db)
        {
            for (int i = 1; i <= 5; i++) await AddAsync(db, folder, i, ($@"C:\shots\{i}.png", i * 10));

            RecentResponse r = await service.ListRecentAsync(3, Ct);

            Assert.Equal([Hash(5), Hash(4), Hash(3)], r.Images.Select(x => x.Hash).ToArray());
            Assert.Equal(5, r.TotalImages);
            Assert.Equal(5, (await service.ListRecentAsync(300, Ct)).Images.Count);   // 上限より少なければ全部
        }
    }

    [Fact]
    public async Task Images_with_the_same_time_are_ordered_by_path_descending_ignoring_case_and_the_order_is_stable()
    {
        var (dir, db, service, folder) = await Environment();
        using (dir) using (db)
        {
            // 同じ更新日時(コピーで時刻を保ったときなど)。パスの降順 = 連番なら大きい番号が先
            await AddAsync(db, folder, 1, (@"C:\shots\shot 001.png", 70));
            await AddAsync(db, folder, 2, (@"C:\shots\shot 003.png", 70));
            await AddAsync(db, folder, 3, (@"C:\shots\Shot 002.png", 70));   // 大文字小文字は区別しない: 001 < 002 < 003
            await AddAsync(db, folder, 4, (@"C:\shots\zzz newer.png", 71));  // 1 分新しいものが先頭

            string[] first = (await service.ListRecentAsync(300, Ct)).Images.Select(x => x.Hash).ToArray();

            Assert.Equal([Hash(4), Hash(2), Hash(3), Hash(1)], first);
            for (int i = 0; i < 3; i++) Assert.Equal(first, (await service.ListRecentAsync(300, Ct)).Images.Select(x => x.Hash).ToArray());
        }
    }

    [Fact]
    public async Task An_image_with_several_paths_is_ordered_by_its_newest_present_path_and_lists_that_path_first()
    {
        var (dir, db, service, folder) = await Environment();
        using (dir) using (db)
        {
            // 画像 1: 古いパスと新しいパス(新しいほうの日時で並ぶ)。missing のパスは出ない
            await AddAsync(db, folder, 1, (@"C:\a\old copy.png", 5), (@"C:\b\new copy.png", 200), (@"C:\c\mid copy.png", 100));
            await AddImageAsync(db, folder, Hash(1), [], [@"C:\d\gone newest.png"]);
            await db.WriteAsync((conn, tx) =>
            {
                using SqliteCommand cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE image_paths SET mtime_ticks = $t WHERE path = $p";
                cmd.Parameters.AddWithValue("$t", Ticks(9999));   // missing のパスは、新しくても数えない
                cmd.Parameters.AddWithValue("$p", @"C:\d\gone newest.png");
                cmd.ExecuteNonQuery();
            }, Ct);
            await AddAsync(db, folder, 2, (@"C:\shots\single.png", 150));

            RecentResponse r = await service.ListRecentAsync(300, Ct);

            Assert.Equal([Hash(1), Hash(2)], r.Images.Select(x => x.Hash).ToArray());   // 200 > 150(古いパスの 5 や missing の 9999 ではない)
            Assert.Equal(new DateTimeOffset(new DateTime(Ticks(200), DateTimeKind.Utc)), r.Images[0].ModifiedAt);
            // 先頭は並べた日時のもとのパス、残りはパスの昇順
            Assert.Equal([@"C:\b\new copy.png", @"C:\a\old copy.png", @"C:\c\mid copy.png"], r.Images[0].Paths);
            Assert.Equal(2, r.TotalImages);   // 画像の数(パスの数ではない)
        }
    }

    [Fact]
    public async Task When_the_newest_time_is_shared_by_two_paths_of_one_image_the_greater_path_is_the_representative()
    {
        var (dir, db, service, folder) = await Environment();
        using (dir) using (db)
        {
            await AddAsync(db, folder, 1, (@"C:\a\copy.png", 80), (@"C:\b\copy.png", 80), (@"C:\c\older.png", 10));

            RecentImage image = Assert.Single((await service.ListRecentAsync(300, Ct)).Images);

            Assert.Equal([@"C:\b\copy.png", @"C:\a\copy.png", @"C:\c\older.png"], image.Paths);
        }
    }

    [Fact]
    public async Task An_empty_library_gives_an_empty_list_and_bad_arguments_are_rejected_while_search_still_rejects_an_empty_query()
    {
        var (dir, db, service, _) = await Environment();
        using (dir) using (db)
        {
            RecentResponse r = await service.ListRecentAsync(300, Ct);
            Assert.Empty(r.Images);
            Assert.Equal(0, r.TotalImages);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListRecentAsync(0, Ct));
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListRecentAsync(300, cts.Token));
            // SearchAsync の契約は変えない(空のクエリは例外)
            await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new SearchQuery(""), Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new SearchQuery("   "), Ct));
        }
    }
}
