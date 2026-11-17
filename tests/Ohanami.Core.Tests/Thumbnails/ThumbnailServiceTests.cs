using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.Embedding;
using Ohanami.Core.Thumbnails;
using Ohanami.Core.Tests.Data;
using SkiaSharp;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>サムネイルのサービス: ディスクキャッシュ(鍵・再利用・破損)と、原本が使えないときの結果(DB と一時ディレクトリは実物)。</summary>
public sealed class ThumbnailServiceTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private sealed class Env : IDisposable
    {
        public TempDirectory Dir { get; } = new();
        public OhanamiDatabase Db { get; }
        public ThumbnailService Service { get; }
        public long Folder { get; private set; }
        public string ThumbRoot => Dir.File("thumbs");

        public Env()
        {
            Db = OhanamiDatabase.Open(Dir.File("test.db"));
            Service = new ThumbnailService(Db, ThumbRoot);
            Folder = SearchTestSupport.AddFolderAsync(Db).GetAwaiter().GetResult();
        }

        /// <summary>原本のファイルを置き、images と image_paths(走査のときの更新日時・サイズ)を作る。ハッシュは内容の SHA-256。</summary>
        public string AddImage(string name, byte[] bytes, bool missing = false)
        {
            string hash = ThumbnailTestImages.HashOf(bytes);
            string path = AddPathTo(hash, name, bytes, missing);
            return hash;
        }

        /// <summary>同じハッシュの、別のパス(同じ内容のコピー)を足す。</summary>
        public string AddPathTo(string hash, string name, byte[] bytes, bool missing = false)
        {
            string path = Dir.File(name);
            File.WriteAllBytes(path, bytes);
            var info = new FileInfo(path);
            Db.WriteAsync((conn, tx) =>
            {
                Exec(conn, tx, "INSERT OR IGNORE INTO images (hash, size, format, first_seen_at) VALUES ($h, $s, 'png', '2026-11-05T00:00:00.000Z')", ("$h", hash), ("$s", info.Length));
                Exec(conn, tx, "INSERT INTO image_paths (path, hash, folder_id, mtime_ticks, size, missing_since) VALUES ($p, $h, $f, $m, $s, $x)",
                    ("$p", path), ("$h", hash), ("$f", Folder), ("$m", info.LastWriteTimeUtc.Ticks), ("$s", info.Length), ("$x", missing ? "2026-11-05T01:00:00.000Z" : DBNull.Value));
            }).GetAwaiter().GetResult();
            return path;
        }

        /// <summary>DB には行が無いが、ハッシュを持つ画像(原本の無い画像)。</summary>
        public void AddImageRowOnly(string hash) => Db.WriteAsync((conn, tx) =>
            Exec(conn, tx, "INSERT INTO images (hash, size, format, first_seen_at) VALUES ($h, 1, 'png', '2026-11-05T00:00:00.000Z')", ("$h", hash))).GetAwaiter().GetResult();

        public void SetPath(string oldPath, string newPath) => Db.WriteAsync((conn, tx) =>
            Exec(conn, tx, "UPDATE image_paths SET path = $n WHERE path = $o", ("$n", newPath), ("$o", oldPath))).GetAwaiter().GetResult();

        public void Dispose()
        {
            Db.Dispose();
            Dir.Dispose();
        }

        private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql, params (string, object)[] args)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach ((string n, object v) in args) cmd.Parameters.AddWithValue(n, v);
            cmd.ExecuteNonQuery();
        }
    }

    private static byte[] Png(int w = 1000, int h = 500) => ThumbnailTestImages.Gradient(w, h);

    // ---------------------------------------------------------------- 作る・キャッシュを使う

    [Fact]
    public async Task After_stop_new_requests_are_unavailable_results_and_running_work_is_waited_for()
    {
        using var env = new Env();
        string hash = env.AddImage("a.png", Png(2000, 1000));
        Task<ThumbnailResult> running = env.Service.GetAsync(hash, 256, 144, Ct);

        await env.Service.StopAsync(); // 進行中の仕事が終わるのを待つ

        Assert.True(running.IsCompleted);
        ThumbnailResult after = await env.Service.GetAsync(env.AddImage("b.png", Png(300, 200)), 256, 144, Ct);
        Assert.Equal(ThumbnailStatus.SourceUnavailable, after.Status); // DB に触らず、結果で返す(例外にしない)
    }

    [Fact]
    public async Task First_request_decodes_the_original_fits_the_box_and_writes_the_cache_then_the_second_reads_it()
    {
        using var env = new Env();
        string hash = env.AddImage("a.png", Png(1000, 500));

        ThumbnailResult first = await env.Service.GetAsync(hash, 256, 144, Ct);
        Assert.Equal(ThumbnailStatus.Ok, first.Status);
        Assert.False(first.FromCache);
        Assert.Null(first.Message);
        Assert.Equal((256, 128), (first.Pixels!.Width, first.Pixels.Height));

        // キャッシュの場所: <根>/v1/<ハッシュの先頭 2 桁>/<ハッシュ>_<幅>x<高さ>.jpg(箱の大きさで名前が決まる。実際の大きさではない)
        string expected = Path.Combine(env.ThumbRoot, "v1", hash[..2], $"{hash}_256x144.jpg");
        Assert.Equal(expected, env.Service.CachePath(hash, 256, 144));
        Assert.True(File.Exists(expected));
        Assert.Equal(1, ThumbnailService.CacheFormatVersion);

        ThumbnailResult second = await env.Service.GetAsync(hash, 256, 144, Ct);
        Assert.Equal(ThumbnailStatus.Ok, second.Status);
        Assert.True(second.FromCache);
        Assert.Equal((first.Pixels.Width, first.Pixels.Height), (second.Pixels!.Width, second.Pixels.Height));
        double mae = Enumerable.Range(0, first.Pixels.Bgra.Length).Where(i => i % 4 != 3).Average(i => Math.Abs(first.Pixels.Bgra[i] - second.Pixels.Bgra[i]));
        Assert.True(mae < 4.0, $"キャッシュ(JPEG)から読んだ画素が、作った画素と違いすぎる: {mae:F2}");
        Assert.Empty(Directory.EnumerateFiles(env.ThumbRoot, "*.tmp", SearchOption.AllDirectories));      // 一時ファイルが残らない
    }

    [Fact]
    public async Task The_cache_is_keyed_by_content_hash_so_it_survives_the_original_moving_or_disappearing()
    {
        using var env = new Env();
        byte[] bytes = Png();
        string hash = env.AddImage("shots-a.png", bytes);
        ThumbnailResult created = await env.Service.GetAsync(hash, 256, 144, Ct);
        Assert.False(created.FromCache);

        // 原本が別のパスに移動した(DB のパスも変わり、元のファイルは無くなった): 同じ内容なので、キャッシュを再利用する
        string moved = Path.Combine(env.Dir.Path, "renamed.png");
        File.Move(env.Dir.File("shots-a.png"), moved);
        env.SetPath(env.Dir.File("shots-a.png"), moved);
        ThumbnailResult afterMove = await env.Service.GetAsync(hash, 256, 144, Ct);
        Assert.True(afterMove.FromCache);

        // 原本が完全に消えても(DB の印は missing になる)、キャッシュがあれば返る
        File.Delete(moved);
        await env.Db.WriteAsync((conn, tx) =>
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE image_paths SET missing_since = '2026-11-05T02:00:00.000Z'";
            cmd.ExecuteNonQuery();
        }, Ct);
        ThumbnailResult afterDelete = await env.Service.GetAsync(hash, 256, 144, Ct);
        Assert.Equal(ThumbnailStatus.Ok, afterDelete.Status);
        Assert.True(afterDelete.FromCache);
    }

    [Fact]
    public async Task A_different_box_size_is_a_different_cache_entry()
    {
        using var env = new Env();
        string hash = env.AddImage("a.png", Png(1000, 500));
        ThumbnailResult small = await env.Service.GetAsync(hash, 256, 144, Ct);
        ThumbnailResult big = await env.Service.GetAsync(hash, 512, 288, Ct);
        Assert.False(big.FromCache);                                                    // 256×144 のキャッシュは使わない
        Assert.Equal((256, 128), (small.Pixels!.Width, small.Pixels.Height));
        Assert.Equal((512, 256), (big.Pixels!.Width, big.Pixels.Height));
        Assert.True(File.Exists(env.Service.CachePath(hash, 256, 144)));
        Assert.True(File.Exists(env.Service.CachePath(hash, 512, 288)));
        Assert.NotEqual(env.Service.CachePath(hash, 256, 144), env.Service.CachePath(hash, 144, 256));   // 幅と高さの順も区別する
        Assert.True((await env.Service.GetAsync(hash, 512, 288, Ct)).FromCache);
    }

    [Fact]
    public async Task Small_originals_are_not_enlarged_and_extreme_shapes_work()
    {
        using var env = new Env();
        string tiny = env.AddImage("tiny.png", ThumbnailTestImages.Solid(1, 1, SKColors.Lime));
        string wide = env.AddImage("wide.png", ThumbnailTestImages.Gradient(4000, 10));
        string small = env.AddImage("small.png", ThumbnailTestImages.Gradient(100, 50));
        Assert.Equal((1, 1), Size((await env.Service.GetAsync(tiny, 256, 144, Ct)).Pixels!));
        Assert.Equal((256, 1), Size((await env.Service.GetAsync(wide, 256, 144, Ct)).Pixels!));
        Assert.Equal((100, 50), Size((await env.Service.GetAsync(small, 256, 144, Ct)).Pixels!));
        // キャッシュから読んでも同じ大きさ
        Assert.Equal((1, 1), Size((await env.Service.GetAsync(tiny, 256, 144, Ct)).Pixels!));
        Assert.Equal((100, 50), Size((await env.Service.GetAsync(small, 256, 144, Ct)).Pixels!));
    }

    private static (int, int) Size(ThumbnailPixels p) => (p.Width, p.Height);

    // ---------------------------------------------------------------- キャッシュの破損

    [Theory]
    [InlineData("empty")]
    [InlineData("garbage")]
    [InlineData("truncated")]
    [InlineData("png-instead-of-jpeg")]
    [InlineData("larger-than-the-box")]
    public async Task A_broken_cache_file_is_discarded_and_the_thumbnail_is_made_again(string kind)
    {
        using var env = new Env();
        string hash = env.AddImage("a.png", Png(1000, 500));
        await env.Service.GetAsync(hash, 256, 144, Ct);
        string cache = env.Service.CachePath(hash, 256, 144);
        byte[] good = File.ReadAllBytes(cache);
        byte[] broken = kind switch
        {
            "empty" => [],
            "garbage" => Enumerable.Range(0, 500).Select(i => (byte)(i * 7)).ToArray(),
            "truncated" => good[..(good.Length / 2)],
            "png-instead-of-jpeg" => ThumbnailTestImages.Gradient(100, 50),
            _ => ThumbnailRenderer.EncodeJpeg(ThumbnailRenderer.Render(ThumbnailTestImages.Gradient(400, 300), 400, 300)),   // 箱 256×144 より大きい JPEG
        };
        File.WriteAllBytes(cache, broken);

        ThumbnailResult r = await env.Service.GetAsync(hash, 256, 144, Ct);
        Assert.Equal(ThumbnailStatus.Ok, r.Status);
        Assert.False(r.FromCache);                                                      // 作り直した
        Assert.Equal((256, 128), Size(r.Pixels!));
        ThumbnailPixels reread = ThumbnailRenderer.DecodeJpeg(File.ReadAllBytes(cache));   // キャッシュが正しい JPEG に置き換わった
        Assert.Equal((256, 128), Size(reread));
        Assert.True((await env.Service.GetAsync(hash, 256, 144, Ct)).FromCache);
    }

    [Fact]
    public async Task A_broken_cache_with_no_original_is_reported_as_unavailable_not_returned()
    {
        using var env = new Env();
        string hash = ThumbnailTestImages.HashOf([1, 2, 3]);
        env.AddImageRowOnly(hash);
        string cache = env.Service.CachePath(hash, 256, 144);
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllBytes(cache, [0, 1, 2]);
        ThumbnailResult r = await env.Service.GetAsync(hash, 256, 144, Ct);
        Assert.Equal(ThumbnailStatus.SourceUnavailable, r.Status);
        Assert.False(File.Exists(cache));                                               // 壊れたキャッシュは捨てた
    }

    // ---------------------------------------------------------------- 原本が使えない・壊れている

    [Fact]
    public async Task An_image_with_no_path_record_gives_SourceUnavailable_as_a_result_not_an_exception()
    {
        using var env = new Env();
        string hash = ThumbnailTestImages.HashOf([9]);
        env.AddImageRowOnly(hash);
        ThumbnailResult r = await env.Service.GetAsync(hash, 256, 144, Ct);
        Assert.Equal(ThumbnailStatus.SourceUnavailable, r.Status);
        Assert.Null(r.Pixels);
        Assert.False(r.FromCache);
        Assert.Contains("記録が無い", r.Message, StringComparison.Ordinal);
        // 画像そのものが DB に無くても、同じ(例外にしない)
        Assert.Equal(ThumbnailStatus.SourceUnavailable, (await env.Service.GetAsync(ThumbnailTestImages.HashOf([8]), 256, 144, Ct)).Status);
    }

    [Fact]
    public async Task Paths_marked_missing_are_not_used_even_if_the_file_still_exists()
    {
        using var env = new Env();
        string hash = env.AddImage("a.png", Png(), missing: true);
        ThumbnailResult r = await env.Service.GetAsync(hash, 256, 144, Ct);
        Assert.Equal(ThumbnailStatus.SourceUnavailable, r.Status);
        Assert.False(File.Exists(env.Service.CachePath(hash, 256, 144)));
    }

    [Fact]
    public async Task A_deleted_original_is_unavailable_and_the_next_existing_path_is_used_when_there_is_one()
    {
        using var env = new Env();
        byte[] bytes = Png();
        string hash = env.AddImage("a.png", bytes);
        env.AddPathTo(hash, "b-copy.png", bytes);                                       // 同じ内容の別パス(1 ハッシュに複数パス)
        File.Delete(env.Dir.File("a.png"));                                             // 先のパスだけが消えた
        ThumbnailResult viaCopy = await env.Service.GetAsync(hash, 256, 144, Ct);
        Assert.Equal(ThumbnailStatus.Ok, viaCopy.Status);

        // どちらも消えたら(キャッシュも無い箱の大きさで)、失敗の結果。理由に両方のパスが出る
        File.Delete(env.Dir.File("b-copy.png"));
        ThumbnailResult none = await env.Service.GetAsync(hash, 128, 72, Ct);
        Assert.Equal(ThumbnailStatus.SourceUnavailable, none.Status);
        Assert.Contains("a.png", none.Message, StringComparison.Ordinal);
        Assert.Contains("b-copy.png", none.Message, StringComparison.Ordinal);
        Assert.Contains("ファイルが無い", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_changed_since_the_scan_is_not_used_and_nothing_is_cached_for_the_old_hash()
    {
        using var env = new Env();
        string hash = env.AddImage("a.png", Png(1000, 500));
        // 走査のあとで、別の内容に書き換えられた(サイズが違う)
        File.WriteAllBytes(env.Dir.File("a.png"), Png(300, 300));
        ThumbnailResult r = await env.Service.GetAsync(hash, 256, 144, Ct);
        Assert.Equal(ThumbnailStatus.SourceUnavailable, r.Status);
        Assert.Contains("書き換え", r.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(env.Service.CachePath(hash, 256, 144)));               // 別の内容の絵を、古いハッシュのキャッシュに入れない

        // サイズは同じで更新日時だけが違う場合も同じ
        string hash2 = env.AddImage("b.png", Png(200, 100));
        File.SetLastWriteTimeUtc(env.Dir.File("b.png"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(ThumbnailStatus.SourceUnavailable, (await env.Service.GetAsync(hash2, 256, 144, Ct)).Status);
    }

    [Fact]
    public async Task A_broken_original_gives_SourceUndecodable_and_is_not_cached()
    {
        using var env = new Env();
        string garbage = env.AddImage("garbage.png", Enumerable.Range(0, 2000).Select(i => (byte)(i * 31)).ToArray());
        byte[] good = Png(600, 400);
        string truncated = env.AddImage("truncated.png", good[..(good.Length / 2)]);
        string zero = env.AddImage("zero.png", []);

        foreach (string hash in new[] { garbage, truncated, zero })
        {
            ThumbnailResult r = await env.Service.GetAsync(hash, 256, 144, Ct);
            Assert.Equal(ThumbnailStatus.SourceUndecodable, r.Status);
            Assert.Null(r.Pixels);
            Assert.NotNull(r.Message);
            Assert.False(File.Exists(env.Service.CachePath(hash, 256, 144)));
        }
    }

    // ---------------------------------------------------------------- 同時・キャンセル・書き込みの失敗

    [Fact]
    public async Task Concurrent_requests_for_the_same_key_all_succeed_and_leave_one_clean_cache_file()
    {
        using var env = new Env();
        string hash = env.AddImage("a.png", Png(2000, 1000));
        ThumbnailResult[] results = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => env.Service.GetAsync(hash, 256, 144, Ct)));
        Assert.All(results, r => Assert.Equal(ThumbnailStatus.Ok, r.Status));
        Assert.All(results, r => Assert.Equal((256, 128), Size(r.Pixels!)));
        Assert.Single(Directory.EnumerateFiles(env.ThumbRoot, "*", SearchOption.AllDirectories));      // 一時ファイルも残らない
        Assert.Equal(0, env.Service.CacheWriteFailures);
    }

    [Fact]
    public async Task Many_different_images_in_parallel_are_all_made_correctly()
    {
        using var env = new Env();
        var hashes = new List<string>();
        for (int i = 0; i < 40; i++) hashes.Add(env.AddImage($"img{i}.png", ThumbnailTestImages.Gradient(300 + i * 13, 200 + i * 7)));
        ThumbnailResult[] results = await Task.WhenAll(hashes.Select(h => Task.Run(() => env.Service.GetAsync(h, 128, 72, Ct), Ct)));
        Assert.All(results, r => Assert.Equal(ThumbnailStatus.Ok, r.Status));
        Assert.Equal(40, Directory.EnumerateFiles(env.ThumbRoot, "*.jpg", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task A_cancelled_request_throws_but_the_work_finishes_and_the_cache_is_kept()
    {
        using var env = new Env();
        string hash = env.AddImage("a.png", Png(3000, 1500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.Service.GetAsync(hash, 256, 144, new CancellationToken(canceled: true)));

        using var cts = new CancellationTokenSource();
        Task<ThumbnailResult> pending = env.Service.GetAsync(hash, 256, 144, cts.Token);
        await cts.CancelAsync();
        try { await pending; } catch (OperationCanceledException) { /* 間に合わなければ、取り消される */ }

        // 次の要求は成功する(作りかけの仕事が壊れていない)
        ThumbnailResult r = await env.Service.GetAsync(hash, 256, 144, Ct);
        Assert.Equal(ThumbnailStatus.Ok, r.Status);
    }

    [Fact]
    public async Task A_cache_that_cannot_be_written_does_not_stop_the_thumbnail_and_is_counted()
    {
        using var env = new Env();
        // 根のパスにファイルがあるので、ディレクトリを作れない
        File.WriteAllText(env.ThumbRoot, "not a directory");
        string hash = env.AddImage("a.png", Png());
        ThumbnailResult r = await env.Service.GetAsync(hash, 256, 144, Ct);
        Assert.Equal(ThumbnailStatus.Ok, r.Status);
        Assert.False(r.FromCache);
        Assert.Equal(1, env.Service.CacheWriteFailures);
    }

    // ---------------------------------------------------------------- デコード済みの画素から(将来の入口)

    [Fact]
    public async Task CreateFrom_makes_the_thumbnail_from_decoded_pixels_and_fills_the_cache()
    {
        using var env = new Env();
        string hash = ThumbnailTestImages.HashOf([42]);              // DB に原本の記録は無い
        var pixels = new byte[640 * 360 * 3];
        for (int i = 0; i < pixels.Length; i += 3) { pixels[i] = 200; pixels[i + 1] = 100; pixels[i + 2] = 50; }
        ThumbnailPixels made = env.Service.CreateFrom(hash, new RgbImage(640, 360, pixels), 256, 144);
        Assert.Equal((256, 144), Size(made));
        Assert.Equal([50, 100, 200, 255], made.Bgra[..4]);
        Assert.True(File.Exists(env.Service.CachePath(hash, 256, 144)));

        ThumbnailResult r = await env.Service.GetAsync(hash, 256, 144, Ct);     // 原本の記録が無くても、キャッシュから返る
        Assert.Equal(ThumbnailStatus.Ok, r.Status);
        Assert.True(r.FromCache);
    }

    // ---------------------------------------------------------------- 引数の検査

    [Fact]
    public async Task Invalid_arguments_are_rejected_and_the_hash_cannot_escape_the_cache_directory()
    {
        using var env = new Env();
        string ok = ThumbnailTestImages.HashOf([1]);
        await Assert.ThrowsAsync<ArgumentNullException>(() => env.Service.GetAsync(null!, 256, 144, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => env.Service.GetAsync("", 256, 144, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => env.Service.GetAsync(ok[..63], 256, 144, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => env.Service.GetAsync(ok.ToUpperInvariant(), 256, 144, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => env.Service.GetAsync(new string('g', 64), 256, 144, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => env.Service.GetAsync(@"..\..\evil" + new string('a', 55), 256, 144, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => env.Service.GetAsync(ok, 0, 144, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => env.Service.GetAsync(ok, 256, 0, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => env.Service.GetAsync(ok, 8193, 144, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => env.Service.GetAsync(ok, 256, -5, Ct));
        Assert.Throws<ArgumentException>(() => env.Service.CreateFrom("x", new RgbImage(1, 1, new byte[3]), 256, 144));
    }
}
