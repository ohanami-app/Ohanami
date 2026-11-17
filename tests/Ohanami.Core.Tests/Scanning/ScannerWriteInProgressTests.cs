using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ohanami.Core.Scanning;
using Xunit;

namespace Ohanami.Core.Tests.Scanning;

/// <summary>
/// 書き込みの途中かもしれないファイルの扱い(ADR 0010): 更新時刻が「0 ≤ 今 − 更新時刻 &lt; 2 秒」のものと、
/// 読んだあとにハンドルの値が列挙の値と違うもの(サイズが違う、更新時刻が許容差 2 秒を超えて違う)は、今回は登録せず(記録せず)次の走査に回し、
/// <see cref="ScanSummary.TransientlyUnreadable"/> に数える。未来の更新時刻・許容差の内の違いは、ふつうに登録する。
/// 時刻は <see cref="TestTimeProvider"/>(2026-11-05 12:00:00Z で止まっている)。
/// </summary>
public sealed class ScannerWriteInProgressTests : IDisposable
{
    private readonly ScanFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private DateTime Now => _fx.Time.GetUtcNow().UtcDateTime;

    private async Task<ScanSummary> ScanAsync()
    {
        if (_fx.Folders.List().Count == 0) await _fx.RegisterRootAsync();
        return await _fx.ScanAsync();
    }

    // ---------------------------------------------------------------- 更新から 2 秒以内

    [Fact]
    public async Task A_file_modified_less_than_two_seconds_ago_is_not_registered_yet_and_is_counted_as_transient()
    {
        _fx.Write("fresh.png", ScanFixture.FakePng("fresh"), Now.AddSeconds(-1));

        ScanSummary first = await ScanAsync();

        Assert.Equal(0, first.Added);
        Assert.Equal(1, first.TransientlyUnreadable);
        Assert.Equal(0, _fx.Long("SELECT count(*) FROM images"));
        Assert.Equal(0, _fx.Long("SELECT count(*) FROM image_paths"));
        Assert.Equal(0, _fx.Long("SELECT count(*) FROM file_issues"));   // 記録しない(恒久的な問題ではない)

        _fx.Time.Advance(TimeSpan.FromSeconds(3));                       // 更新から 2 秒を過ぎた(次の走査)
        ScanSummary second = await ScanAsync();

        Assert.Equal(1, second.Added);
        Assert.Equal(0, second.TransientlyUnreadable);
    }

    [Theory]
    [InlineData(0.0, true)]       // 今と同じ更新時刻
    [InlineData(1.999, true)]
    [InlineData(2.0, false)]      // ちょうど 2 秒: 「< 2 秒」ではないので登録する
    [InlineData(60.0, false)]
    public async Task The_two_second_window_is_zero_inclusive_to_two_exclusive(double ageSeconds, bool deferred)
    {
        _fx.Write("a.png", ScanFixture.FakePng("a"), Now.AddSeconds(-ageSeconds));

        ScanSummary s = await ScanAsync();

        Assert.Equal(deferred ? 0 : 1, s.Added);
        Assert.Equal(deferred ? 1 : 0, s.TransientlyUnreadable);
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(10.0)]
    [InlineData(3600.0)]
    public async Task A_file_with_a_modification_time_in_the_future_is_registered_normally(double aheadSeconds)
    {
        // 「2 秒以内」を絶対値で判定すると、NAS の時計のずれ・時計の狂った機器からのコピーのファイルが永久に登録されない
        _fx.Write("future.png", ScanFixture.FakePng("future"), Now.AddSeconds(aheadSeconds));

        ScanSummary s = await ScanAsync();

        Assert.Equal(1, s.Added);
        Assert.Equal(0, s.TransientlyUnreadable);
    }

    [Fact]
    public async Task A_fresh_change_to_a_known_file_keeps_the_old_row_until_the_file_settles()
    {
        string path = _fx.Write("a.png", ScanFixture.FakePng("a-v1"), Now.AddMinutes(-10));
        await ScanAsync();
        string oldHash = _fx.Text("SELECT hash FROM image_paths")!;

        _fx.Write("a.png", ScanFixture.FakePng("a-v2-longer-content"), Now.AddSeconds(-1));   // 書き換え中(直近の更新)
        ScanSummary busy = await ScanAsync();

        Assert.Equal(0, busy.Changed);
        Assert.Equal(1, busy.TransientlyUnreadable);
        Assert.Equal(oldHash, _fx.Text("SELECT hash FROM image_paths"));   // 古い行のまま

        _fx.Time.Advance(TimeSpan.FromSeconds(5));
        ScanSummary settled = await ScanAsync();

        Assert.Equal(1, settled.Changed);
        Assert.Equal(ScanFixture.Sha256Hex(File.ReadAllBytes(path)), _fx.Text("SELECT hash FROM image_paths"));
    }

    // ---------------------------------------------------------------- ハンドルの値と列挙の値

    [Fact]
    public async Task A_file_that_grew_between_enumeration_and_open_is_not_registered_with_the_stale_size()
    {
        string path = _fx.Write("grow.png", ScanFixture.FakePng("grow"), Now.AddMinutes(-10));
        await _fx.RegisterRootAsync();
        long enumeratedSize = new FileInfo(path).Length;
        bool grown = false;
        _fx.Scanner.BeforeOpenForTests = p =>
        {
            if (grown) return;
            grown = true;
            File.AppendAllText(p, "appended-after-enumeration");    // 列挙のあと・開く前に書き足された(サイズが違う。更新時刻は戻す)
            File.SetLastWriteTimeUtc(p, Now.AddMinutes(-10));
        };

        ScanSummary s = await _fx.ScanAsync();

        Assert.True(new FileInfo(path).Length > enumeratedSize);
        Assert.Equal(0, s.Added);
        Assert.Equal(1, s.TransientlyUnreadable);
        Assert.Equal(0, _fx.Long("SELECT count(*) FROM image_paths"));

        _fx.Scanner.BeforeOpenForTests = null;
        ScanSummary next = await _fx.ScanAsync();                    // 書き足しが終わったあとの走査が、新しいサイズ・内容で登録する

        Assert.Equal(1, next.Added);
        Assert.Equal(new FileInfo(path).Length, _fx.Long("SELECT size FROM image_paths"));
        Assert.Equal(ScanFixture.Sha256Hex(File.ReadAllBytes(path)), _fx.Text("SELECT hash FROM image_paths"));
    }

    [Fact]
    public async Task A_modification_time_that_differs_by_more_than_the_tolerance_defers_the_file_but_a_small_difference_does_not()
    {
        string far = _fx.Write("far.png", ScanFixture.FakePng("far"), Now.AddMinutes(-10));
        string near = _fx.Write("near.png", ScanFixture.FakePng("near"), Now.AddMinutes(-10));
        await _fx.RegisterRootAsync();
        _fx.Scanner.BeforeOpenForTests = p =>
        {
            // サイズは同じまま、更新時刻だけが動く(書き手が、サイズの変わらない上書きをした、など)
            if (p == far) File.SetLastWriteTimeUtc(p, Now.AddMinutes(-10).AddSeconds(10));   // 許容差 2 秒を超える
            if (p == near) File.SetLastWriteTimeUtc(p, Now.AddMinutes(-10).AddSeconds(1));   // 許容差の内(FS の刻みの違いなど)
        };

        ScanSummary s = await _fx.ScanAsync();

        Assert.Equal(1, s.Added);                                    // near だけ登録される
        Assert.Equal(1, s.TransientlyUnreadable);
        Assert.Equal(near, _fx.Text("SELECT path FROM image_paths"), ignoreCase: true);
        // DB に入る mtime は列挙の値のまま(ハンドルの値ではない)
        Assert.Equal(Now.AddMinutes(-10).Ticks, _fx.Long("SELECT mtime_ticks FROM image_paths"));
    }

    [Fact]
    public async Task A_sharing_violation_is_both_unreadable_and_transient()
    {
        string path = _fx.Write("busy.png", ScanFixture.FakePng("busy"), Now.AddMinutes(-10));
        await _fx.RegisterRootAsync();
        await using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        ScanSummary s = await _fx.ScanAsync();

        Assert.Equal(1, s.Unreadable);                               // 一時的な共有違反を含む数え方
        Assert.Equal(1, s.TransientlyUnreadable);
        Assert.Equal(0, _fx.Long("SELECT count(*) FROM file_issues"));
    }

    [Fact]
    public async Task Transient_counts_are_summed_over_folders()
    {
        string other = Path.GetFullPath(Path.Combine(_fx.Root, "..", Path.GetFileName(_fx.Root) + "-other"));
        Directory.CreateDirectory(other);
        try
        {
            await _fx.RegisterRootAsync();
            await _fx.Folders.AddAsync(other, TestContext.Current.CancellationToken);
            _fx.Write("a.png", ScanFixture.FakePng("a"), Now.AddSeconds(-1));
            string b = Path.Combine(other, "b.png");
            File.WriteAllBytes(b, ScanFixture.FakePng("b"));
            File.SetLastWriteTimeUtc(b, Now.AddSeconds(-1));

            ScanSummary s = await _fx.ScanAsync();

            Assert.Equal(2, s.TransientlyUnreadable);
            Assert.Equal(0, s.Added);
        }
        finally
        {
            Directory.Delete(other, true);
        }
    }

    // ---------------------------------------------------------------- 別のファイルシステム(FAT / exFAT。手元専用)

    private const string OtherFsVariable = "OHANAMI_FS_TEST_DIR";

    /// <summary>
    /// 手元専用(環境変数 OHANAMI_FS_TEST_DIR = FAT / exFAT など別のファイルシステムのドライブ上のディレクトリ。中に GUID 名の一時フォルダを作り、終わったら消す)。
    /// ADR 0010 の前提(FAT / exFAT で成り立つか)の確認: 書き手が閉じたあとの、列挙の size / mtime とハンドルの値が(更新時刻は許容差の内で)一致する。結果は %TEMP%\ohanami-fs-test-report.txt に書く。
    /// </summary>
    [Fact]
    public async Task On_another_file_system_closed_files_match_within_the_tolerance_and_are_registered()
    {
        string? baseDir = Environment.GetEnvironmentVariable(OtherFsVariable);
        Assert.SkipUnless(!string.IsNullOrEmpty(baseDir), "環境変数 OHANAMI_FS_TEST_DIR が無い(FAT / exFAT など別のファイルシステムでの確認。手元専用)");
        string dir = Path.Combine(baseDir!, "ohanami-fs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine($"ファイルシステム: {new DriveInfo(dir).DriveFormat}(ドライブ {new DriveInfo(dir).Name})");
            for (int round = 0; round < 3; round++)
            {
                string path = Path.Combine(dir, $"written{round}.png");
                using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    for (int i = 0; i < 4; i++)
                    {
                        writer.Write(i == 0 ? ScanFixture.FakePng("fs-" + round) : new byte[64 * 1024]);   // 先頭は PNG の署名(走査が画像と認める)
                        writer.Flush();
                        await Task.Delay(300, TestContext.Current.CancellationToken);   // 書き手が間を空けて書く
                    }
                }
                FileInfo enumerated = new DirectoryInfo(dir).EnumerateFiles().Single(f => f.Name == $"written{round}.png");
                using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                long handleMtime = File.GetLastWriteTimeUtc(reader.SafeFileHandle).Ticks;
                double diffMs = (handleMtime - enumerated.LastWriteTimeUtc.Ticks) / (double)TimeSpan.TicksPerMillisecond;
                report.AppendLine($"閉じた直後 {round}: サイズ 列挙 {enumerated.Length} / ハンドル {reader.Length}、更新時刻 列挙 {enumerated.LastWriteTimeUtc:O} / ハンドル {new DateTime(handleMtime, DateTimeKind.Utc):O}(差 {diffMs:0.0} ms)");
                Assert.Equal(enumerated.Length, reader.Length);
                Assert.True(Math.Abs(handleMtime - enumerated.LastWriteTimeUtc.Ticks) <= Scanner.MtimeTolerance.Ticks, report.ToString());
            }

            // 製品の Scanner で: 閉じたあとのファイルは、許容差の内なので、保留にならずに登録される(更新時刻は 1 時間前にしておく。2 秒の規則とは別)
            foreach (string p in Directory.GetFiles(dir)) File.SetLastWriteTimeUtc(p, Now.AddHours(-1));
            await _fx.Folders.AddAsync(dir, TestContext.Current.CancellationToken);
            ScanSummary s = await _fx.ScanAsync();
            report.AppendLine($"走査: 新規 {s.Added}、一時的に読めない {s.TransientlyUnreadable}(3 と 0 のはず)");
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "ohanami-fs-test-report.txt"), report.ToString(), TestContext.Current.CancellationToken);
            Assert.Equal(3, s.Added);
            Assert.Equal(0, s.TransientlyUnreadable);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---------------------------------------------------------------- 書き手が閉じたあとの値(前提の確認)

    [Fact]
    public void After_a_writer_closes_the_enumerated_values_match_a_fresh_handle_within_the_tolerance()
    {
        // 前提の確認(ADR 0010。NTFS の一時フォルダ。FAT / exFAT は未確認): チャンクごとに書いて Flush し、閉じる。
        // 閉じたあとの、ディレクトリの列挙の size / mtime と、新しく開いたハンドルの値が(更新時刻は許容差の内で)一致する
        string path = Path.Combine(_fx.Root, "written.png");
        using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            for (int i = 0; i < 8; i++)
            {
                writer.Write(new byte[256 * 1024]);
                writer.Flush();
            }
        }

        FileInfo enumerated = new DirectoryInfo(_fx.Root).EnumerateFiles().Single(f => f.Name == "written.png");
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        long handleMtime = File.GetLastWriteTimeUtc(reader.SafeFileHandle).Ticks;

        Assert.Equal(enumerated.Length, reader.Length);
        Assert.True(Math.Abs(handleMtime - enumerated.LastWriteTimeUtc.Ticks) <= Scanner.MtimeTolerance.Ticks,
            $"列挙 {enumerated.LastWriteTimeUtc:O}、ハンドル {new DateTime(handleMtime, DateTimeKind.Utc):O}");
    }
}
