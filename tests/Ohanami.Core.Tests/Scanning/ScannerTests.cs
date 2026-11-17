using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using Xunit;

namespace Ohanami.Core.Tests.Scanning;

/// <summary>スキャナ(ADR 0003・0007・0010)。一時ディレクトリに、テストの中で作ったファイルを置いて走査する。</summary>
public sealed class ScannerTests : IDisposable
{
    private readonly ScanFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- 新規・形式・拡張子

    [Fact]
    public async Task New_files_of_every_supported_format_are_added_with_their_sha256_and_format()
    {
        byte[] png = ScanFixture.RealImage(SKEncodedImageFormat.Png, 1);
        byte[] jpg = ScanFixture.RealImage(SKEncodedImageFormat.Jpeg, 2);
        byte[] jpeg = ScanFixture.RealImage(SKEncodedImageFormat.Jpeg, 3);
        byte[] webp = ScanFixture.RealImage(SKEncodedImageFormat.Webp, 4);
        _fx.Write("a.png", png);
        _fx.Write("b.JPG", jpg);              // 拡張子の大文字小文字は区別しない
        _fx.Write("sub/c.jpeg", jpeg);        // サブディレクトリも再帰する
        _fx.Write("sub/deeper/d.WebP", webp);
        await _fx.RegisterRootAsync();

        ScanSummary s = await _fx.ScanAsync();

        Assert.Equal(4, s.Added);
        Assert.Equal(0, s.Changed + s.Missing + s.Unreadable + s.Unsupported + s.Unreachable);
        Assert.Equal(4, _fx.Long("SELECT count(*) FROM images"));
        Assert.Equal(4, _fx.Long("SELECT count(*) FROM image_paths WHERE missing_since IS NULL"));
        Assert.Equal("png", _fx.Text($"SELECT format FROM images WHERE hash = '{ScanFixture.Sha256Hex(png)}'"));
        Assert.Equal("jpeg", _fx.Text($"SELECT format FROM images WHERE hash = '{ScanFixture.Sha256Hex(jpg)}'"));
        Assert.Equal("jpeg", _fx.Text($"SELECT format FROM images WHERE hash = '{ScanFixture.Sha256Hex(jpeg)}'"));
        Assert.Equal("webp", _fx.Text($"SELECT format FROM images WHERE hash = '{ScanFixture.Sha256Hex(webp)}'"));
        Assert.Equal(png.Length, _fx.Long($"SELECT size FROM images WHERE hash = '{ScanFixture.Sha256Hex(png)}'"));
        Assert.NotNull(_fx.Text("SELECT last_scan_at FROM folders"));
        Assert.Null(_fx.Text("SELECT last_error FROM folders"));
    }

    [Fact]
    public async Task Hash_matches_a_known_sha256_of_known_bytes()
    {
        // PNG の先頭 8 バイト + "ohanami-known-content" の SHA-256(Python の hashlib で別に求めた値)
        _fx.Write("known.png", ScanFixture.FakePng("ohanami-known-content"));
        await _fx.RegisterRootAsync();
        await _fx.ScanAsync();
        Assert.Equal("18fb43ae31f31056f041c7a1a9a60ce4ab232c914b344bd26d343d432a4c1311", _fx.Text("SELECT hash FROM images"));
    }

    [Fact]
    public async Task Files_with_other_extensions_are_ignored_and_not_recorded()
    {
        _fx.Write("note.txt", ScanFixture.FakePng("x"));
        _fx.Write("anim.gif", ScanFixture.FakePng("y"));
        _fx.Write("noext", ScanFixture.FakePng("z"));
        await _fx.RegisterRootAsync();
        ScanSummary s = await _fx.ScanAsync();
        Assert.Equal(0, s.Added + s.Unsupported + s.Unreadable);
        Assert.Equal(0, _fx.Long("SELECT count(*) FROM images") + _fx.Long("SELECT count(*) FROM file_issues"));
    }

    [Fact]
    public async Task Content_decides_the_format_not_the_extension()
    {
        byte[] jpegBytes = ScanFixture.RealImage(SKEncodedImageFormat.Jpeg, 5);
        _fx.Write("really-a-jpeg.png", jpegBytes);
        await _fx.RegisterRootAsync();
        ScanSummary s = await _fx.ScanAsync();
        Assert.Equal(1, s.Added);
        Assert.Equal("jpeg", _fx.Text("SELECT format FROM images"));
    }

    [Fact]
    public async Task Unsupported_content_is_recorded_in_file_issues_and_not_in_images()
    {
        _fx.Write("fake.png", System.Text.Encoding.UTF8.GetBytes("this is not an image at all"));
        await _fx.RegisterRootAsync();
        ScanSummary s = await _fx.ScanAsync();

        Assert.Equal(1, s.Unsupported);
        Assert.Equal(0, s.Added);
        Assert.Equal(0, _fx.Long("SELECT count(*) FROM images"));
        Assert.Equal("unsupported", _fx.Text("SELECT kind FROM file_issues"));
        Assert.Contains("先頭", _fx.Text("SELECT message FROM file_issues")!);
    }

    [Fact]
    public async Task Unsupported_file_with_same_mtime_and_size_is_counted_but_not_read_again()
    {
        _fx.Write("fake.png", System.Text.Encoding.UTF8.GetBytes("not an image"));
        await _fx.RegisterRootAsync();
        await _fx.ScanAsync();
        string? firstUpdated = _fx.Text("SELECT updated_at FROM file_issues");

        _fx.Time.Advance(TimeSpan.FromHours(1));
        ScanSummary s = await _fx.ScanAsync();

        Assert.Equal(1, s.Unsupported);
        Assert.Equal(firstUpdated, _fx.Text("SELECT updated_at FROM file_issues")); // 読み直していない
    }

    [Fact]
    public async Task Webp_container_without_webp_fourcc_is_unsupported()
    {
        // "RIFF" で始まるが "WEBP" ではない(WAV など)
        byte[] riff = [.. "RIFF"u8.ToArray(), 0, 0, 0, 0, .. "WAVE"u8.ToArray(), 1, 2, 3];
        _fx.Write("x.webp", riff);
        await _fx.RegisterRootAsync();
        ScanSummary s = await _fx.ScanAsync();
        Assert.Equal(1, s.Unsupported);
        Assert.Equal(0, s.Added);
    }

    [Fact]
    public async Task Empty_folder_scans_cleanly_and_updates_last_scan_at()
    {
        await _fx.RegisterRootAsync();
        ScanSummary s = await _fx.ScanAsync();
        Assert.Equal(new ScanSummary(0, 0, 0, 0, 0, 0, 0, s.Elapsed), s);
        Assert.Equal("2026-11-05T12:00:00.000Z", _fx.Text("SELECT last_scan_at FROM folders"));
        Assert.Null(_fx.Text("SELECT last_error FROM folders"));
    }

    // ---- 差分

    [Fact]
    public async Task Rescan_of_unchanged_files_does_nothing_and_hashes_nothing()
    {
        for (int i = 0; i < 5; i++) _fx.Write($"f{i}.png", ScanFixture.FakePng("same-" + i));
        await _fx.RegisterRootAsync();
        await _fx.ScanAsync();

        ScanProgress? last = null;
        ScanSummary s = await _fx.ScanAsync(onProgress: p => last = p);

        Assert.Equal(0, s.Added + s.Changed + s.Missing + s.Unreadable + s.Unsupported + s.Unreachable);
        Assert.NotNull(last);
        Assert.Equal(5, last!.FilesSeen);
        Assert.Equal(0, last.Hashed); // (mtime_ticks, size) が同じなので、ハッシュを取り直していない
    }

    [Fact]
    public async Task Changed_content_updates_the_path_row_and_keeps_the_old_image()
    {
        byte[] before = ScanFixture.FakePng("before");
        byte[] after = ScanFixture.FakePng("after-longer");
        string path = _fx.Write("a.png", before, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await _fx.RegisterRootAsync();
        await _fx.ScanAsync();

        _fx.Write("a.png", after, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        ScanSummary s = await _fx.ScanAsync();

        Assert.Equal(1, s.Changed);
        Assert.Equal(0, s.Added);
        Assert.Equal(ScanFixture.Sha256Hex(after), _fx.Text($"SELECT hash FROM image_paths WHERE path = '{path}'"));
        Assert.Equal(2, _fx.Long("SELECT count(*) FROM images")); // 古い images は残る(結果を捨てない)
        Assert.Equal(1, _fx.Long("SELECT count(*) FROM image_paths"));
    }

    [Fact]
    public async Task Touching_a_file_updates_mtime_but_is_not_a_content_change()
    {
        byte[] bytes = ScanFixture.FakePng("touch");
        string path = _fx.Write("a.png", bytes, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await _fx.RegisterRootAsync();
        await _fx.ScanAsync();

        File.SetLastWriteTimeUtc(path, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        ScanSummary s = await _fx.ScanAsync();

        Assert.Equal(0, s.Changed + s.Added);
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, _fx.Long($"SELECT mtime_ticks FROM image_paths WHERE path = '{path}'"));
    }

    [Fact]
    public async Task Same_content_at_two_paths_is_one_image_with_two_paths()
    {
        byte[] bytes = ScanFixture.RealImage(SKEncodedImageFormat.Png, 9);
        _fx.Write("original.png", bytes);
        _fx.Write("copies/copy.png", bytes);
        await _fx.RegisterRootAsync();

        ScanSummary s = await _fx.ScanAsync();

        Assert.Equal(2, s.Added);
        Assert.Equal(1, _fx.Long("SELECT count(*) FROM images"));
        Assert.Equal(2, _fx.Long($"SELECT count(*) FROM image_paths WHERE hash = '{ScanFixture.Sha256Hex(bytes)}'"));
    }

    [Fact]
    public async Task Renamed_file_is_a_new_path_and_the_old_path_goes_missing()
    {
        byte[] bytes = ScanFixture.FakePng("rename");
        string oldPath = _fx.Write("old.png", bytes);
        await _fx.RegisterRootAsync();
        await _fx.ScanAsync();

        string newPath = _fx.PathOf("new.png");
        File.Move(oldPath, newPath);
        ScanSummary s = await _fx.ScanAsync();

        Assert.Equal(1, s.Added);
        Assert.Equal(1, s.Missing);
        Assert.NotNull(_fx.Text($"SELECT missing_since FROM image_paths WHERE path = '{oldPath}'"));
        Assert.Null(_fx.Text($"SELECT missing_since FROM image_paths WHERE path = '{newPath}'"));
        Assert.Equal(1, _fx.Long("SELECT count(*) FROM images"));
    }

    // ---- 削除・再出現

    [Fact]
    public async Task Deleted_file_gets_missing_since_and_keeps_its_rows_and_reappearing_clears_it()
    {
        byte[] bytes = ScanFixture.FakePng("delete-me");
        DateTime mtime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        string path = _fx.Write("gone.png", bytes, mtime);
        await _fx.RegisterRootAsync();
        await _fx.ScanAsync();

        File.Delete(path);
        _fx.Time.Advance(TimeSpan.FromDays(1));
        ScanSummary s1 = await _fx.ScanAsync();
        Assert.Equal(1, s1.Missing);
        Assert.Equal("2026-11-06T12:00:00.000Z", _fx.Text($"SELECT missing_since FROM image_paths WHERE path = '{path}'"));
        Assert.Equal(1, _fx.Long("SELECT count(*) FROM images")); // 行は消さない

        // もう一度走査しても、Missing として数え直さない(既に印がある)
        ScanSummary s2 = await _fx.ScanAsync();
        Assert.Equal(0, s2.Missing);

        // 同じ内容・同じ mtime で戻ってきた: 印が外れる。内容を読み直さない
        _fx.Write("gone.png", bytes, mtime);
        ScanProgress? last = null;
        ScanSummary s3 = await _fx.ScanAsync(onProgress: p => last = p);
        Assert.Equal(0, s3.Added + s3.Changed);
        Assert.Equal(0, last!.Hashed);
        Assert.Null(_fx.Text($"SELECT missing_since FROM image_paths WHERE path = '{path}'"));
    }

    [Fact]
    public async Task File_that_became_a_non_image_is_marked_missing_and_recorded_as_unsupported()
    {
        string path = _fx.Write("a.png", ScanFixture.RealImage(SKEncodedImageFormat.Png, 11), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await _fx.RegisterRootAsync();
        await _fx.ScanAsync();

        _fx.Write("a.png", System.Text.Encoding.UTF8.GetBytes("now it is text"), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        ScanSummary s = await _fx.ScanAsync();

        Assert.Equal(1, s.Unsupported);
        Assert.NotNull(_fx.Text($"SELECT missing_since FROM image_paths WHERE path = '{path}'")); // 検索の対象から外れる
        Assert.Equal("unsupported", _fx.Text($"SELECT kind FROM file_issues WHERE path = '{path}'"));
        Assert.Equal(1, _fx.Long("SELECT count(*) FROM images")); // 結果は残す
    }

    [Fact]
    public async Task Stale_file_issue_is_removed_when_the_file_disappears_or_becomes_a_valid_image()
    {
        string p1 = _fx.Write("bad1.png", System.Text.Encoding.UTF8.GetBytes("text one"));
        string p2 = _fx.Write("bad2.png", System.Text.Encoding.UTF8.GetBytes("text two"));
        await _fx.RegisterRootAsync();
        await _fx.ScanAsync();
        Assert.Equal(2, _fx.Long("SELECT count(*) FROM file_issues"));

        File.Delete(p1);
        _fx.Write("bad2.png", ScanFixture.RealImage(SKEncodedImageFormat.Png, 12), new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        ScanSummary s = await _fx.ScanAsync();

        Assert.Equal(0, _fx.Long("SELECT count(*) FROM file_issues"));
        Assert.Equal(1, s.Added);
        Assert.Equal(1, _fx.Long($"SELECT count(*) FROM image_paths WHERE path = '{p2}'"));
    }

    // ---- 読めないファイル

    [Fact]
    public async Task File_locked_by_another_process_is_skipped_not_recorded_and_picked_up_next_time()
    {
        string path = _fx.Write("locked.png", ScanFixture.FakePng("locked"));
        await _fx.RegisterRootAsync();

        ScanSummary locked;
        using (var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            locked = await _fx.ScanAsync();
        }
        Assert.Equal(1, locked.Unreadable);
        Assert.Equal(0, locked.Added);
        Assert.Equal(0, _fx.Long("SELECT count(*) FROM images"));
        Assert.Equal(0, _fx.Long("SELECT count(*) FROM file_issues")); // 一時的な共有違反は記録しない

        ScanSummary after = await _fx.ScanAsync();
        Assert.Equal(1, after.Added);
        Assert.Equal(0, after.Unreadable);
    }

    [Fact]
    public async Task File_with_access_denied_is_recorded_as_unreadable_and_retried_every_time()
    {
        string path = _fx.Write("denied.png", ScanFixture.FakePng("denied"));
        await _fx.RegisterRootAsync();

        using (ScanFixture.DenyRead(path))
        {
            ScanSummary s1 = await _fx.ScanAsync();
            Assert.Equal(1, s1.Unreadable);
            Assert.Equal("unreadable", _fx.Text("SELECT kind FROM file_issues"));
            Assert.Equal(0, _fx.Long("SELECT count(*) FROM images"));

            ScanSummary s2 = await _fx.ScanAsync(); // 毎回再試行する(権限は mtime を変えないため)
            Assert.Equal(1, s2.Unreadable);
        }

        ScanSummary s3 = await _fx.ScanAsync();
        Assert.Equal(1, s3.Added);
        Assert.Equal(0, _fx.Long("SELECT count(*) FROM file_issues")); // 読めたので記録は消える
    }

    // ---- 到達できない場所

    [Fact]
    public async Task Subdirectory_that_cannot_be_listed_is_unreachable_and_its_files_are_not_marked_missing()
    {
        string inSecret = _fx.Write("secret/s.png", ScanFixture.FakePng("secret"));
        string inOpen = _fx.Write("open/o.png", ScanFixture.FakePng("open"));
        string secretDir = _fx.PathOf("secret");
        await _fx.RegisterRootAsync();
        await _fx.ScanAsync();

        File.Delete(inOpen); // 到達できる場所のファイルは、消えれば missing になる(掃き出しは動いている)
        using (ScanFixture.DenyRead(secretDir))
        {
            ScanSummary s = await _fx.ScanAsync();

            Assert.Equal(1, s.Unreachable);
            Assert.Equal(1, s.Missing); // open/o.png だけ
            Assert.Null(_fx.Text($"SELECT missing_since FROM image_paths WHERE path = '{inSecret}'")); // 配下は対象外
            Assert.NotNull(_fx.Text($"SELECT missing_since FROM image_paths WHERE path = '{inOpen}'"));
            string error = _fx.Text("SELECT last_error FROM folders")!;
            Assert.Contains(secretDir, error);
            Assert.Contains("1 件", error);
        }

        ScanSummary after = await _fx.ScanAsync(); // 戻したら、エラーは消え、そのまま
        Assert.Equal(0, after.Unreachable + after.Missing + after.Added);
        Assert.Null(_fx.Text("SELECT last_error FROM folders"));
        Assert.Null(_fx.Text($"SELECT missing_since FROM image_paths WHERE path = '{inSecret}'"));
    }

    [Fact]
    public async Task Vanished_root_is_unreachable_and_nothing_is_marked_missing()
    {
        string rootCopy = Path.Combine(Path.GetTempPath(), "ohanami-tests-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootCopy);
        try
        {
            File.WriteAllBytes(Path.Combine(rootCopy, "a.png"), ScanFixture.FakePng("a"));
            File.WriteAllBytes(Path.Combine(rootCopy, "b.png"), ScanFixture.FakePng("b"));
            FolderInfo folder = await _fx.Folders.AddAsync(rootCopy, Ct);
            Assert.Equal(2, (await _fx.ScanAsync()).Added);

            Directory.Delete(rootCopy, recursive: true); // ドライブの取り外しの代わり
            ScanSummary s = await _fx.ScanAsync();

            Assert.Equal(1, s.Unreachable);
            Assert.Equal(0, s.Missing);
            Assert.Equal(2, _fx.Long("SELECT count(*) FROM image_paths WHERE missing_since IS NULL"));
            Assert.Contains(rootCopy, _fx.Text("SELECT last_error FROM folders")!);
            Assert.Equal(2, _fx.Folders.List().Single(f => f.Id == folder.Id).ImageCount);
            Assert.NotNull(_fx.Folders.List().Single().LastError);

            // 戻ってきたら、エラーは消える
            Directory.CreateDirectory(rootCopy);
            File.WriteAllBytes(Path.Combine(rootCopy, "a.png"), ScanFixture.FakePng("a"));
            ScanSummary back = await _fx.ScanAsync();
            Assert.Equal(0, back.Unreachable);
            Assert.Null(_fx.Text("SELECT last_error FROM folders"));
            Assert.Equal(1, back.Missing); // b.png は到達できる場所で見えなくなった
        }
        finally
        {
            if (Directory.Exists(rootCopy)) Directory.Delete(rootCopy, recursive: true);
        }
    }

    // ---- リンク(ジャンクション / シンボリックリンク)は辿らず、件数を数える

    [Fact]
    public async Task Junctions_are_not_followed_but_are_counted_and_not_reported_as_errors()
    {
        string target = Path.Combine(Path.GetTempPath(), "ohanami-tests-link-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        string link = _fx.PathOf("linked");
        try
        {
            File.WriteAllBytes(Path.Combine(target, "outside.png"), ScanFixture.FakePng("outside"));
            _fx.Write("inside.png", ScanFixture.FakePng("inside"));
            ScanFixture.CreateJunction(link, target);
            await _fx.RegisterRootAsync();

            ScanProgress? last = null;
            ScanSummary s = await _fx.ScanAsync(onProgress: p => last = p);

            Assert.Equal(1, s.Added); // inside.png だけ。リンク先の outside.png は索引に入らない
            Assert.Equal(1, s.SkippedLinks);
            Assert.Equal(0, s.Unreachable);
            Assert.Null(_fx.Text("SELECT last_error FROM folders")); // エラーではない
            Assert.Equal(1, last!.SkippedLinks);

            ScanSummary again = await _fx.ScanAsync(); // 走査のたびに数える
            Assert.Equal(1, again.SkippedLinks);
            Assert.Equal(0, again.Added + again.Missing);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link); // リンクだけを消す(リンク先は消えない)
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task Folder_without_links_reports_zero_skipped_links()
    {
        _fx.Write("sub/a.png", ScanFixture.FakePng("a"));
        await _fx.RegisterRootAsync();
        Assert.Equal(0, (await _fx.ScanAsync()).SkippedLinks);
    }

    // ---- 対象のフォルダ・キャンセル・進捗

    [Fact]
    public async Task Disabled_folders_are_skipped_unless_named_explicitly_and_unknown_ids_throw()
    {
        _fx.Write("a.png", ScanFixture.FakePng("a"));
        long id = await _fx.RegisterRootAsync();
        await _fx.Folders.SetEnabledAsync(id, false, Ct);

        ScanSummary skipped = await _fx.ScanAsync();
        Assert.Equal(0, skipped.Added);
        Assert.Equal(0, _fx.Long("SELECT count(*) FROM images"));

        ScanSummary named = await _fx.ScanAsync([id]);
        Assert.Equal(1, named.Added);

        await Assert.ThrowsAsync<FolderRegistrationException>(() => _fx.ScanAsync([id + 999]));
    }

    [Fact]
    public async Task Cancelled_scan_does_not_sweep_missing_files()
    {
        string path = _fx.Write("a.png", ScanFixture.FakePng("a"));
        _fx.Write("b.png", ScanFixture.FakePng("b"));
        await _fx.RegisterRootAsync();
        await _fx.ScanAsync();

        File.Delete(path);
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _fx.ScanCancellableAsync(_ => cts.Cancel(), cts.Token)); // 列挙が終わった直後(最初の進捗)で止める

        Assert.Null(_fx.Text($"SELECT missing_since FROM image_paths WHERE path = '{path}'")); // 「見えない」と判断しない

        ScanSummary full = await _fx.ScanAsync();
        Assert.Equal(1, full.Missing);
    }

    [Fact]
    public async Task Many_files_are_written_in_batches_and_progress_never_goes_backwards()
    {
        const int n = 300;
        for (int i = 0; i < n; i++) _fx.Write($"d{i % 7}/f{i}.png", ScanFixture.FakePng("bulk-" + i));
        await _fx.RegisterRootAsync();

        var snapshots = new List<ScanProgress>();
        ScanSummary s = await _fx.ScanAsync(onProgress: p => { lock (snapshots) snapshots.Add(p); });

        Assert.Equal(n, s.Added);
        Assert.Equal(n, _fx.Long("SELECT count(*) FROM images"));
        Assert.Equal(n, _fx.Long("SELECT count(DISTINCT hash) FROM image_paths"));
        ScanProgress[] ordered = snapshots.ToArray();
        Assert.True(ordered.Length >= 3, "列挙後・バッチごと・掃き出し後に進捗が出る");
        for (int i = 1; i < ordered.Length; i++)
        {
            Assert.True(ordered[i].Hashed >= ordered[i - 1].Hashed);
            Assert.True(ordered[i].Added >= ordered[i - 1].Added);
        }
        ScanProgress final = ordered[^1];
        Assert.Equal(n, final.FilesSeen);
        Assert.Equal(n, final.Hashed);
        Assert.Equal(n, final.Added);
    }

    [Fact]
    public async Task Unregistering_a_folder_removes_its_paths_but_keeps_images()
    {
        _fx.Write("a.png", ScanFixture.FakePng("a"));
        _fx.Write("bad.png", System.Text.Encoding.UTF8.GetBytes("text"));
        long id = await _fx.RegisterRootAsync();
        await _fx.ScanAsync();
        Assert.Equal(1, _fx.Long("SELECT count(*) FROM image_paths"));
        Assert.Equal(1, _fx.Long("SELECT count(*) FROM file_issues"));

        await _fx.Folders.RemoveAsync(id, Ct);

        Assert.Equal(0, _fx.Long("SELECT count(*) FROM image_paths"));
        Assert.Equal(0, _fx.Long("SELECT count(*) FROM file_issues"));
        Assert.Equal(1, _fx.Long("SELECT count(*) FROM images")); // 画像と結果は残す
    }
}
