using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.Thumbnails;
using Ohanami.Core.Tests.Data;
using SkiaSharp;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// サムネイルの速さと画質の測定(手元専用。私物の画像を使うのでコミットされない画像のディレクトリを環境変数で指す)。結果は出力に出るだけで、判定はしない。
///   OHANAMI_IMAGES_DIR     = 画像のディレクトリ(直下の *.png *.jpg *.jpeg *.webp を、ファイル名の順に全部)
///   OHANAMI_THUMB_OUT      = (任意)縮小方法ごとの結果を PNG で書き出すディレクトリ。画質の比較(Pillow の Lanczos との差)に使う
///   OHANAMI_THUMB_SAMPLE   = (任意)書き出す枚数(既定 40。画像を等間隔に抜く)
/// WPF の WIC の測定値: 原本の PNG を縮小デコードして 1 枚の平均 6.8ms(p95 12.1ms)、ディスクキャッシュ(JPEG 品質 85)の読みは 0.56ms/枚、
/// 256×144 で 1 万枚 118MB(ここまでは ADR 0041 の数字)。8 ワーカーで 702 枚/秒(手元の画像で測った値。画像は公開していない)。同じ条件(箱 256×144、画像 1030 枚、8 ワーカー)で測る。
/// </summary>
public sealed class ThumbnailBenchmarkTests(ITestOutputHelper output)
{
    private const int BoxW = 256, BoxH = 144, Workers = 8;

    private static string[] ImageFiles()
    {
        string? dir = Environment.GetEnvironmentVariable("OHANAMI_IMAGES_DIR");
        Assert.SkipUnless(!string.IsNullOrEmpty(dir), "環境変数 OHANAMI_IMAGES_DIR が無い(手元の画像での測定。通常は skip)");
        string[] files = Directory.GetFiles(dir!).Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        return files;
    }

    private static double Pct(List<double> sorted, double p) => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    private void Report(string title, List<double> ms)
    {
        var s = ms.OrderBy(x => x).ToList();
        output.WriteLine($"{title}: {s.Count} 枚、平均 {s.Average():F1} ms、中央値 {Pct(s, 0.5):F1} ms、p95 {Pct(s, 0.95):F1} ms、最大 {s[^1]:F1} ms");
    }

    [Fact]
    [Trait("Category", "Private")]
    public void Resize_methods_single_thread_speed_and_optional_png_dump_for_quality()
    {
        string[] files = ImageFiles();
        string? outDir = Environment.GetEnvironmentVariable("OHANAMI_THUMB_OUT");
        int sample = int.TryParse(Environment.GetEnvironmentVariable("OHANAMI_THUMB_SAMPLE"), out int n) ? n : 40;
        var sampleIdx = Enumerable.Range(0, Math.Min(sample, files.Length)).Select(i => i * files.Length / Math.Min(sample, files.Length)).ToHashSet();
        if (outDir is not null) Directory.CreateDirectory(outDir);

        output.WriteLine($"画像 {files.Length} 枚({files.Sum(f => new FileInfo(f).Length) / 1e6:F0} MB)、箱 {BoxW}x{BoxH}、1 スレッド。ファイルの読みは含めず、デコード + 縮小の時間");
        foreach (ResizeMethod method in Enum.GetValues<ResizeMethod>())
        {
            var times = new List<double>();
            for (int i = 0; i < files.Length; i++)
            {
                byte[] bytes = File.ReadAllBytes(files[i]);
                if (i == 0) ThumbnailRenderer.Render(bytes, BoxW, BoxH, method); // JIT・初期化
                var sw = Stopwatch.StartNew();
                ThumbnailPixels p = ThumbnailRenderer.Render(bytes, BoxW, BoxH, method);
                times.Add(sw.Elapsed.TotalMilliseconds);
                if (outDir is not null && sampleIdx.Contains(i))
                {
                    var info = new SKImageInfo(p.Width, p.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                    using SKImage img = SKImage.FromPixelCopy(info, p.Bgra, info.RowBytes);
                    using SKData png = img.Encode(SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(Path.Combine(outDir, $"{i:D4}_{method}.png"), png.ToArray());
                }
            }
            Report($"[{method}] デコード + 縮小", times);
        }
        if (outDir is not null) output.WriteLine($"縮小結果の PNG を書いた: {outDir}(元の画像は同じ番号のファイル名の順 = {string.Join(", ", sampleIdx.OrderBy(x => x).Take(3).Select(i => Path.GetFileName(files[i])))} …)");

        // ファイルの読みだけ(ページキャッシュが温かい状態)
        var read = new List<double>();
        foreach (string f in files)
        {
            var sw = Stopwatch.StartNew();
            _ = File.ReadAllBytes(f);
            read.Add(sw.Elapsed.TotalMilliseconds);
        }
        Report("ファイルの読み(温かい)", read);
    }

    [Fact]
    [Trait("Category", "Private")]
    public async Task Service_creates_and_reads_the_disk_cache_with_eight_workers()
    {
        string[] files = ImageFiles();
        var (dir, db) = SearchTestSupport.NewDb();
        using (dir) using (db)
        {
            // 画像ごとに DB の行を作る(ハッシュはパスから作った偽物でよい=内容ハッシュでなくても、サービスは鍵として使うだけ)
            long folder = await SearchTestSupport.AddFolderAsync(db);
            var hashes = new string[files.Length];
            await db.WriteAsync((conn, tx) =>
            {
                for (int i = 0; i < files.Length; i++)
                {
                    hashes[i] = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(files[i])));
                    var info = new FileInfo(files[i]);
                    using SqliteCommand cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO images (hash, size, format, first_seen_at) VALUES ($h, $s, 'png', '2026-11-05T00:00:00.000Z'); " +
                                      "INSERT INTO image_paths (path, hash, folder_id, mtime_ticks, size) VALUES ($p, $h, $f, $m, $s)";
                    cmd.Parameters.AddWithValue("$h", hashes[i]);
                    cmd.Parameters.AddWithValue("$s", info.Length);
                    cmd.Parameters.AddWithValue("$p", files[i]);
                    cmd.Parameters.AddWithValue("$f", folder);
                    cmd.Parameters.AddWithValue("$m", info.LastWriteTimeUtc.Ticks);
                    cmd.ExecuteNonQuery();
                }
            }, TestContext.Current.CancellationToken);

            var service = new ThumbnailService(db, dir.File("thumbs"));
            foreach (string f in files) _ = File.ReadAllBytes(f); // ページキャッシュを温める(上の測定値の条件に合わせる)

            for (int round = 1; round <= 2; round++)
            {
                string label = round == 1 ? "原本から作る(キャッシュなし)" : "ディスクキャッシュから読む";
                var times = new System.Collections.Concurrent.ConcurrentBag<double>();
                int gc2 = GC.CollectionCount(2), gc0 = GC.CollectionCount(0);
                var total = Stopwatch.StartNew();
                await Parallel.ForEachAsync(Enumerable.Range(0, files.Length), new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = TestContext.Current.CancellationToken },
                    async (i, ct) =>
                    {
                        var sw = Stopwatch.StartNew();
                        ThumbnailResult r = await service.GetAsync(hashes[i], BoxW, BoxH, ct);
                        times.Add(sw.Elapsed.TotalMilliseconds);
                        Assert.Equal(ThumbnailStatus.Ok, r.Status);
                        Assert.Equal(round == 2, r.FromCache);
                    });
                double sec = total.Elapsed.TotalSeconds;
                Report($"[{label}] 1 枚の GetAsync(待ち時間を含む)", times.ToList());
                output.WriteLine($"[{label}] {files.Length} 枚を {Workers} ワーカーで {sec:F2} 秒 = {files.Length / sec:F0} 枚/秒。GC: gen0 {GC.CollectionCount(0) - gc0} 回、gen2 {GC.CollectionCount(2) - gc2} 回");
            }

            long bytes = Directory.EnumerateFiles(dir.File("thumbs"), "*.jpg", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            output.WriteLine($"キャッシュ: {files.Length} 枚で {bytes / 1e6:F1} MB、平均 {bytes / files.Length / 1024.0:F1} KB/枚 → 1 万枚で約 {bytes / (double)files.Length * 10000 / 1e6:F0} MB");
            Assert.Equal(0, service.CacheWriteFailures);

            // キャッシュの読みだけを 1 スレッドで(ReadAllBytes + JPEG デコード。上の測定値は 0.56ms/枚)
            var readTimes = new List<double>();
            foreach (string cf in Directory.EnumerateFiles(dir.File("thumbs"), "*.jpg", SearchOption.AllDirectories))
            {
                var sw = Stopwatch.StartNew();
                _ = ThumbnailRenderer.DecodeJpeg(File.ReadAllBytes(cf));
                readTimes.Add(sw.Elapsed.TotalMilliseconds);
            }
            Report("キャッシュ 1 ファイルの読み + JPEG デコード(1 スレッド)", readTimes);
        }
    }
}
