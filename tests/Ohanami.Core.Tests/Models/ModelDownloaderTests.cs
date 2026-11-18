using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Models;
using Ohanami.Core.Tests.Data;
using Xunit;

namespace Ohanami.Core.Tests.Models;

/// <summary>
/// モデルのダウンローダー(<see cref="ModelDownloader"/>)。**ネットワークには出ない**: HttpMessageHandler の偽物が、サーバーの応答
/// (200・206・Range の無視・途中で切れる・止まる・サイズ違い・ハッシュ違い・各種の状態コード)を作る。ファイルは小さい(数十 KB)。
/// </summary>
public sealed class ModelDownloaderTests
{
    private const string Url = "https://models.example.test/repo/resolve/abc123/dir/file.bin";
    private const string Name = "dir/file.bin";

    // ---------------------------------------------------------------- 偽のサーバー

    private sealed record Seen(HttpMethod Method, string Uri, string? Range, string? UserAgent, string[] HeaderNames);

    private sealed class FakeServer(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(new Seen(request.Method, request.RequestUri!.ToString(), request.Headers.Range?.ToString(),
                    request.Headers.UserAgent.ToString(), [.. request.Headers.Select(h => h.Key)]));
                return Task.FromResult(respond(request, Requests.Count - 1));
            }
        }
    }

    /// <summary>本文の偽物。failAfter バイトで IOException(接続が切れた)、hangAfter バイトで以後は永久に待つ(止まった)、endAfter バイトで本文を終える(途中で切れた)。</summary>
    private sealed class FakeBody(byte[] data, int failAfter = -1, int hangAfter = -1, int endAfter = -1, int chunk = 4096) : Stream
    {
        private int _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (failAfter >= 0 && _served >= failAfter) throw new IOException("接続がリセットされた(偽)");
            if (hangAfter >= 0 && _served >= hangAfter) await Task.Delay(Timeout.Infinite, cancellationToken);
            int limit = data.Length;
            if (failAfter >= 0) limit = Math.Min(limit, failAfter);
            if (hangAfter >= 0) limit = Math.Min(limit, hangAfter);
            if (endAfter >= 0) limit = Math.Min(limit, endAfter);
            int n = Math.Min(Math.Min(chunk, buffer.Length), limit - _served);
            if (n <= 0) return 0;
            data.AsMemory(_served, n).CopyTo(buffer);
            _served += n;
            return n;
        }
    }

    private static HttpResponseMessage Ok(byte[] full, string? finalUrl = null, Func<byte[], Stream>? body = null, long? contentLength = null)
    {
        var content = new StreamContent(body?.Invoke(full) ?? new FakeBody(full));
        content.Headers.ContentLength = contentLength ?? full.Length;
        return With(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }, finalUrl);
    }

    private static HttpResponseMessage Partial(byte[] full, long from, string? finalUrl = null, Func<byte[], Stream>? body = null, long? totalOverride = null, long? fromOverride = null)
    {
        byte[] tail = full[(int)from..];
        var content = new StreamContent(body?.Invoke(tail) ?? new FakeBody(tail));
        content.Headers.ContentLength = tail.Length;
        content.Headers.ContentRange = new ContentRangeHeaderValue(fromOverride ?? from, full.Length - 1, totalOverride ?? full.Length);
        return With(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content }, finalUrl);
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string? finalUrl = null) =>
        With(new HttpResponseMessage(code) { Content = new ByteArrayContent([]) }, finalUrl);

    /// <summary>リダイレクトのあとの最終の URL(配信元のホスト)を、応答の RequestMessage に載せる(本物の HttpClient と同じ)。</summary>
    private static HttpResponseMessage With(HttpResponseMessage response, string? finalUrl)
    {
        if (finalUrl is not null) response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl);
        return response;
    }

    private static int? RangeStart(HttpRequestMessage request) => request.Headers.Range?.Ranges.FirstOrDefault()?.From is { } f ? (int)f : null;

    // ---------------------------------------------------------------- 材料

    private static byte[] Data(int size, int seed = 1)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static ModelDownloadOptions Fast(Func<string, long?>? freeSpace = null) => new()
    {
        StallTimeout = TimeSpan.FromMilliseconds(150),
        HeaderTimeout = TimeSpan.FromSeconds(5),
        RetryDelay = TimeSpan.Zero,
        MaxFailuresWithoutProgress = 3,
        FreeSpace = freeSpace ?? (_ => null),
        BufferSize = 8192,
    };

    private static DownloadItem Item(TempDirectory dir, byte[] data, string? sha = null, string name = Name) =>
        new(ModelSets.FeatureVector, name, Url, Path.Combine(dir.Path, name.Replace('/', Path.DirectorySeparatorChar)), sha ?? Sha(data), data.Length);

    private static async Task<ModelDownloadSummary> Run(FakeServer server, DownloadItem item, ModelDownloadOptions? options = null,
        IProgress<ModelDownloadProgress>? progress = null, CancellationToken? ct = null)
    {
        using var downloader = new ModelDownloader(server, options ?? Fast(), log: null);
        return await downloader.DownloadItemsAsync([item], progress, ct ?? TestContext.Current.CancellationToken);
    }

    // ---------------------------------------------------------------- 取れる場合

    [Fact]
    public async Task A_plain_200_is_written_verified_and_renamed_and_only_get_with_no_identifying_headers_is_sent()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(50_000);
        var server = new FakeServer((_, _) => Ok(data));
        DownloadItem item = Item(dir, data);

        ModelDownloadSummary summary = await Run(server, item);

        Assert.Equal(data, File.ReadAllBytes(item.Destination));
        Assert.False(File.Exists(item.Destination + ".part"));                                   // 改名したので、.part は無い
        Assert.Equal(new ModelDownloadSummary(1, data.Length), summary);
        Seen request = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(Url, request.Uri);                                                          // 毎回、resolve の URL から
        Assert.Null(request.Range);                                                              // 0 バイトからなので、Range なし
        Assert.Equal("Ohanami", request.UserAgent);                                              // 固定の文字列
        Assert.All(request.HeaderNames, h => Assert.Contains(h, new[] { "User-Agent" }));       // Cookie・Authorization・Referer・その他を付けない
    }

    [Fact]
    public async Task A_part_file_is_continued_with_a_range_request_and_a_206()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(60_000);
        DownloadItem item = Item(dir, data);
        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
        File.WriteAllBytes(item.Destination + ".part", data[..25_000]);
        var server = new FakeServer((r, _) => Partial(data, RangeStart(r)!.Value));

        ModelDownloadSummary summary = await Run(server, item);

        Assert.Equal(data, File.ReadAllBytes(item.Destination));
        Assert.Equal("bytes=25000-", Assert.Single(server.Requests).Range);
        Assert.Equal(35_000, summary.BytesReceived);                                              // 続きの分だけ受け取った
    }

    [Fact]
    public async Task When_the_server_ignores_the_range_and_returns_200_the_file_is_rewritten_from_zero_not_appended()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(40_000);
        DownloadItem item = Item(dir, data);
        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
        File.WriteAllBytes(item.Destination + ".part", data[..10_000]);
        var server = new FakeServer((_, _) => Ok(data));

        await Run(server, item);

        Assert.Equal(data, File.ReadAllBytes(item.Destination));                                  // 足していたら、サイズが 50,000 になって検証が落ちる
        Assert.Equal("bytes=10000-", Assert.Single(server.Requests).Range);
    }

    [Fact]
    public async Task A_complete_part_file_is_verified_and_renamed_without_any_request()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(30_000);
        DownloadItem item = Item(dir, data);
        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
        File.WriteAllBytes(item.Destination + ".part", data);
        var server = new FakeServer((_, _) => throw new InvalidOperationException("ネットワークに出てはいけない"));

        await Run(server, item);

        Assert.Equal(data, File.ReadAllBytes(item.Destination));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_part_file_larger_than_the_expected_size_is_discarded_and_the_download_starts_over()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(20_000);
        DownloadItem item = Item(dir, data);
        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
        File.WriteAllBytes(item.Destination + ".part", Data(99_999, 7));
        var server = new FakeServer((_, _) => Ok(data));

        await Run(server, item);

        Assert.Equal(data, File.ReadAllBytes(item.Destination));
        Assert.Null(Assert.Single(server.Requests).Range);
    }

    [Fact]
    public async Task A_416_to_the_range_request_discards_the_part_file_and_starts_over_once()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(20_000);
        DownloadItem item = Item(dir, data);
        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
        File.WriteAllBytes(item.Destination + ".part", data[..5_000]);
        var server = new FakeServer((r, _) => r.Headers.Range is null ? Ok(data) : Status(HttpStatusCode.RequestedRangeNotSatisfiable));

        await Run(server, item);

        Assert.Equal(data, File.ReadAllBytes(item.Destination));
        Assert.Equal(["bytes=5000-", null], server.Requests.Select(s => s.Range));
    }

    [Fact]
    public async Task An_existing_file_of_the_wrong_size_is_replaced_after_the_new_one_is_verified()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(20_000);
        DownloadItem item = Item(dir, data);
        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
        File.WriteAllBytes(item.Destination, new byte[777]);
        var server = new FakeServer((_, _) => Ok(data));

        await Run(server, item);

        Assert.Equal(data, File.ReadAllBytes(item.Destination));
    }

    // ---------------------------------------------------------------- 切れる・止まる

    [Fact]
    public async Task A_dropped_connection_is_resumed_from_where_it_stopped()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(60_000);
        DownloadItem item = Item(dir, data);
        var server = new FakeServer((r, n) => n == 0
            ? Ok(data, body: d => new FakeBody(d, failAfter: 20_480))
            : Partial(data, RangeStart(r)!.Value));

        ModelDownloadSummary summary = await Run(server, item);

        Assert.Equal(data, File.ReadAllBytes(item.Destination));
        Assert.Equal([null, "bytes=20480-"], server.Requests.Select(s => s.Range));                    // 2 回目は、取れた分の続き
        Assert.Equal(60_000, summary.BytesReceived);                                              // 二重に数えない
    }

    [Fact]
    public async Task A_body_that_ends_early_is_resumed()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(50_000);
        DownloadItem item = Item(dir, data);
        var server = new FakeServer((r, n) => n == 0
            ? Ok(data, body: d => new FakeBody(d, endAfter: 12_288))
            : Partial(data, RangeStart(r)!.Value));

        await Run(server, item);

        Assert.Equal(data, File.ReadAllBytes(item.Destination));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task A_stalled_body_is_cut_after_the_stall_timeout_and_resumed()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(50_000);
        DownloadItem item = Item(dir, data);
        var server = new FakeServer((r, n) => n == 0
            ? Ok(data, body: d => new FakeBody(d, hangAfter: 16_384))
            : Partial(data, RangeStart(r)!.Value));

        await Run(server, item);

        Assert.Equal(data, File.ReadAllBytes(item.Destination));
        Assert.Equal([null, "bytes=16384-"], server.Requests.Select(s => s.Range));
    }

    [Fact]
    public async Task A_stall_that_never_makes_progress_stops_with_stalled_and_keeps_the_part_file()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(50_000);
        DownloadItem item = Item(dir, data);
        var server = new FakeServer((r, n) => n == 0
            ? Ok(data, "https://cdn.example.test/signed?token=secret", d => new FakeBody(d, hangAfter: 8192))
            : Partial(data, RangeStart(r)!.Value, "https://cdn.example.test/signed?token=secret", d => new FakeBody(d, hangAfter: 0)));

        ModelDownloadException e = await Assert.ThrowsAsync<ModelDownloadException>(() => Run(server, item));

        Assert.Equal(ModelDownloadFailure.Stalled, e.Kind);
        Assert.Equal("cdn.example.test", e.Host);                                                // 実体の配信元のホスト名
        Assert.DoesNotContain("secret", e.Message, StringComparison.Ordinal);                    // 署名つきの URL の中身は、メッセージに出さない
        Assert.Equal(8192, new FileInfo(item.Destination + ".part").Length);                    // 次に続きから取れる
        Assert.False(File.Exists(item.Destination));
    }

    [Fact]
    public async Task Connection_failures_that_never_progress_stop_with_network_after_the_configured_number_of_tries()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(10_000);
        var server = new FakeServer((_, _) => throw new HttpRequestException("名前を解決できない(偽)"));

        ModelDownloadException e = await Assert.ThrowsAsync<ModelDownloadException>(() => Run(server, Item(dir, data)));

        Assert.Equal(ModelDownloadFailure.Network, e.Kind);
        Assert.Equal(3, server.Requests.Count);                                                  // 進みが無いまま 3 回
        Assert.Equal("models.example.test", e.Host);
    }

    [Fact]
    public async Task A_failure_count_restarts_when_progress_is_made()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(80_000);
        DownloadItem item = Item(dir, data);
        // 4 回切れるが、毎回少し進むので、MaxFailuresWithoutProgress=3 に達しない
        var server = new FakeServer((r, n) => n < 4
            ? (n == 0 ? Ok(data, body: d => new FakeBody(d, failAfter: 8192)) : Partial(data, RangeStart(r)!.Value, body: d => new FakeBody(d, failAfter: 8192)))
            : Partial(data, RangeStart(r)!.Value));

        await Run(server, item);

        Assert.Equal(data, File.ReadAllBytes(item.Destination));
        Assert.Equal(5, server.Requests.Count);
    }

    // ---------------------------------------------------------------- 取る前・取ったあとの確認

    [Theory]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503)]
    public async Task An_unexpected_status_stops_with_the_status_code_and_the_final_host_and_is_not_retried(HttpStatusCode code, int expected)
    {
        using var dir = new TempDirectory();
        byte[] data = Data(10_000);
        var server = new FakeServer((_, _) => Status(code, "https://cdn.example.test/x"));

        ModelDownloadException e = await Assert.ThrowsAsync<ModelDownloadException>(() => Run(server, Item(dir, data)));

        Assert.Equal(ModelDownloadFailure.HttpStatus, e.Kind);
        Assert.Equal(expected, e.StatusCode);
        Assert.Equal("cdn.example.test", e.Host);
        Assert.Equal(Name, e.FileName);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_content_length_different_from_the_catalog_stops_before_the_body_is_read()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(10_000);
        var server = new FakeServer((_, _) => Ok(data, body: _ => new UnreadableBody(), contentLength: 10_001));      // 本文を読んだら、テストが落ちる
        DownloadItem item = Item(dir, data);

        ModelDownloadException e = await Assert.ThrowsAsync<ModelDownloadException>(() => Run(server, item));

        Assert.Equal(ModelDownloadFailure.SizeMismatch, e.Kind);
        Assert.False(File.Exists(item.Destination + ".part"));                                   // 何も書いていない
        Assert.Single(server.Requests);
    }

    /// <summary>読まれてはいけない本文(読まれると InvalidOperationException。ダウンローダーが本文を読む前に止めることの確認)。</summary>
    private sealed class UnreadableBody : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("本文を読んではいけない");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new InvalidOperationException("本文を読んではいけない");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_206_whose_total_size_differs_from_the_catalog_stops_before_the_body_is_read()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(30_000);
        DownloadItem item = Item(dir, data);
        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
        File.WriteAllBytes(item.Destination + ".part", data[..5_000]);
        var server = new FakeServer((r, _) => Partial(data, RangeStart(r)!.Value, totalOverride: 31_000));

        ModelDownloadException e = await Assert.ThrowsAsync<ModelDownloadException>(() => Run(server, item));

        Assert.Equal(ModelDownloadFailure.SizeMismatch, e.Kind);
        Assert.Equal(5_000, new FileInfo(item.Destination + ".part").Length);                    // .part には何も足していない
    }

    [Fact]
    public async Task A_206_that_does_not_start_where_it_was_asked_stops_as_an_unexpected_response()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(30_000);
        DownloadItem item = Item(dir, data);
        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
        File.WriteAllBytes(item.Destination + ".part", data[..5_000]);
        var server = new FakeServer((_, _) => Partial(data, 6_000));                             // 5,000 を頼んだのに 6,000 から

        ModelDownloadException e = await Assert.ThrowsAsync<ModelDownloadException>(() => Run(server, item));

        Assert.Equal(ModelDownloadFailure.UnexpectedResponse, e.Kind);
    }

    [Fact]
    public async Task A_206_to_a_request_without_a_range_is_an_unexpected_response()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(10_000);
        var server = new FakeServer((_, _) => Partial(data, 0));

        ModelDownloadException e = await Assert.ThrowsAsync<ModelDownloadException>(() => Run(server, Item(dir, data)));

        Assert.Equal(ModelDownloadFailure.UnexpectedResponse, e.Kind);
    }

    [Fact]
    public async Task More_data_than_the_catalog_size_stops_with_size_mismatch_and_removes_the_part_file()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(10_000);
        byte[] tooMuch = [.. data, .. new byte[5_000]];
        DownloadItem item = Item(dir, data);
        var server = new FakeServer((_, _) => Ok(tooMuch, contentLength: 10_000));              // 頭は合っているのに、本文が多い

        ModelDownloadException e = await Assert.ThrowsAsync<ModelDownloadException>(() => Run(server, item));

        Assert.Equal(ModelDownloadFailure.SizeMismatch, e.Kind);
        Assert.False(File.Exists(item.Destination + ".part"));
        Assert.False(File.Exists(item.Destination));
    }

    [Fact]
    public async Task A_wrong_sha256_removes_the_part_file_does_not_create_the_file_and_is_not_retried()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(20_000);
        byte[] other = Data(20_000, seed: 99);                                                   // 同じ大きさで、中身が違う
        DownloadItem item = Item(dir, data);
        var server = new FakeServer((_, _) => Ok(other));

        ModelDownloadException e = await Assert.ThrowsAsync<ModelDownloadException>(() => Run(server, item));

        Assert.Equal(ModelDownloadFailure.HashMismatch, e.Kind);
        Assert.Equal(Name, e.FileName);
        Assert.False(File.Exists(item.Destination + ".part"));
        Assert.False(File.Exists(item.Destination));
        Assert.Single(server.Requests);                                                          // 自動では繰り返さない
    }

    [Fact]
    public async Task A_wrong_sha256_does_not_touch_an_existing_good_file()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(20_000);
        DownloadItem item = Item(dir, data, sha: new string('a', 64));                          // 期待する SHA-256 が合わない
        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
        File.WriteAllBytes(item.Destination, new byte[3]);
        var server = new FakeServer((_, _) => Ok(data));

        await Assert.ThrowsAsync<ModelDownloadException>(() => Run(server, item));

        Assert.Equal(3, new FileInfo(item.Destination).Length);                                  // 置き換えは、検証のあと。失敗では、元のファイルを残す
    }

    [Fact]
    public async Task Insufficient_free_space_stops_before_any_request()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(20_000);
        var server = new FakeServer((_, _) => throw new InvalidOperationException("ネットワークに出てはいけない"));
        ModelDownloadOptions options = Fast(freeSpace: _ => 1_000);                              // 残り 20,000 + 余裕に足りない

        ModelDownloadException e = await Assert.ThrowsAsync<ModelDownloadException>(() => Run(server, Item(dir, data), options));

        Assert.Equal(ModelDownloadFailure.InsufficientSpace, e.Kind);
        Assert.Equal(20_000 + ModelDownloadPlan.FreeSpaceMargin, e.NeededBytes);
        Assert.Equal(1_000, e.AvailableBytes);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task The_free_space_check_counts_only_what_is_left_after_a_part_file()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(20_000);
        DownloadItem item = Item(dir, data);
        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
        File.WriteAllBytes(item.Destination + ".part", data[..15_000]);
        var server = new FakeServer((r, _) => Partial(data, RangeStart(r)!.Value));
        ModelDownloadOptions options = Fast(freeSpace: _ => 5_000 + ModelDownloadPlan.FreeSpaceMargin);   // 残り 5,000 + 余裕、ちょうど

        await Run(server, item, options);

        Assert.Equal(data, File.ReadAllBytes(item.Destination));
    }

    // ---------------------------------------------------------------- キャンセル・進み具合

    [Fact]
    public async Task Cancelling_keeps_the_part_file_and_a_later_run_continues_from_it()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(60_000);
        DownloadItem item = Item(dir, data);
        using var cts = new CancellationTokenSource();
        var server = new FakeServer((_, _) => Ok(data, body: d => new CancelAfterBody(d, 16_384, cts)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(server, item, ct: cts.Token));

        long kept = new FileInfo(item.Destination + ".part").Length;
        Assert.InRange(kept, 1, data.Length - 1);
        Assert.False(File.Exists(item.Destination));

        var resume = new FakeServer((r, _) => Partial(data, RangeStart(r)!.Value));
        await Run(resume, item);
        Assert.Equal(data, File.ReadAllBytes(item.Destination));
        Assert.Equal($"bytes={kept}-", Assert.Single(resume.Requests).Range);
    }

    /// <summary>指定のバイト数を渡したあと、キャンセルを要求して、次の読み取りを待たせる(キャンセルの偽物)。</summary>
    private sealed class CancelAfterBody(byte[] data, int cancelAfter, CancellationTokenSource cts) : Stream
    {
        private int _served;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served >= cancelAfter)
            {
                await cts.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            int n = Math.Min(Math.Min(4096, buffer.Length), data.Length - _served);
            data.AsMemory(_served, n).CopyTo(buffer);
            _served += n;
            return n;
        }
    }

    private sealed class Collect : IProgress<ModelDownloadProgress>
    {
        public List<ModelDownloadProgress> Items { get; } = [];

        public void Report(ModelDownloadProgress value)
        {
            lock (Items) Items.Add(value);
        }
    }

    [Fact]
    public async Task Progress_goes_connecting_downloading_verifying_completed_and_the_done_bytes_never_go_backwards()
    {
        using var dir = new TempDirectory();
        byte[] data = Data(200_000);
        var server = new FakeServer((_, _) => Ok(data, body: d => new FakeBody(d, chunk: 1024)));
        var collect = new Collect();

        await Run(server, Item(dir, data), progress: collect);

        List<ModelDownloadProgress> p = collect.Items;
        Assert.Equal(ModelDownloadPhase.Connecting, p[0].Phase);
        Assert.Equal(ModelDownloadPhase.Completed, p[^1].Phase);
        Assert.Equal(ModelDownloadPhase.Verifying, p[^2].Phase);
        Assert.Single(p, x => x.Phase == ModelDownloadPhase.Completed);
        Assert.Equal(data.Length, p[^1].BytesDone);
        Assert.All(p, x => { Assert.Equal(data.Length, x.BytesTotal); Assert.Equal(Name, x.FileName); Assert.Equal(1, x.FileCount); });
        Assert.Equal(p.Select(x => x.BytesDone).Order(), p.Select(x => x.BytesDone));
    }

    [Fact]
    public async Task Several_files_are_downloaded_in_order_and_the_progress_counts_across_them()
    {
        using var dir = new TempDirectory();
        byte[] a = Data(30_000, 1), b = Data(20_000, 2);
        var server = new FakeServer((r, _) => r.RequestUri!.AbsolutePath.EndsWith("a.bin", StringComparison.Ordinal) ? Ok(a) : Ok(b));
        var items = new[]
        {
            new DownloadItem(ModelSets.FeatureVector, "a.bin", "https://models.example.test/x/a.bin", Path.Combine(dir.Path, "a.bin"), Sha(a), a.Length),
            new DownloadItem(ModelSets.Vlm, "sub/b.bin", "https://models.example.test/x/b.bin", Path.Combine(dir.Path, "sub", "b.bin"), Sha(b), b.Length),
        };
        var collect = new Collect();

        using var downloader = new ModelDownloader(server, Fast(), log: null);
        ModelDownloadSummary summary = await downloader.DownloadItemsAsync(items, collect, TestContext.Current.CancellationToken);

        Assert.Equal(new ModelDownloadSummary(2, 50_000), summary);
        Assert.Equal(a, File.ReadAllBytes(items[0].Destination));
        Assert.Equal(b, File.ReadAllBytes(items[1].Destination));                                 // 置き場の下のフォルダは作る
        Assert.Equal(50_000, collect.Items[^1].BytesDone);
        Assert.Equal([0, 1], collect.Items.Select(x => x.FileIndex).Distinct());
    }

    [Fact]
    public async Task Completed_is_reported_once_per_file_after_the_file_is_in_place_and_before_the_next_file_connects()
    {
        using var dir = new TempDirectory();
        byte[] a = Data(30_000, 1), b = Data(20_000, 2);
        var server = new FakeServer((r, _) => r.RequestUri!.AbsolutePath.EndsWith("a.bin", StringComparison.Ordinal) ? Ok(a) : Ok(b));
        var items = new[]
        {
            new DownloadItem(ModelSets.FeatureVector, "a.bin", "https://models.example.test/x/a.bin", Path.Combine(dir.Path, "a.bin"), Sha(a), a.Length),
            new DownloadItem(ModelSets.FeatureVector, "sub/b.bin", "https://models.example.test/x/b.bin", Path.Combine(dir.Path, "sub", "b.bin"), Sha(b), b.Length),
        };
        var seen = new List<(ModelDownloadPhase Phase, int Index, bool InPlace, bool PartLeft)>();
        var progress = new Probe(p =>
        {
            DownloadItem item = items[p.FileIndex];
            seen.Add((p.Phase, p.FileIndex, File.Exists(item.Destination), File.Exists(item.Destination + ".part")));
        });

        using var downloader = new ModelDownloader(server, Fast(), log: null);
        await downloader.DownloadItemsAsync(items, progress, TestContext.Current.CancellationToken);

        var completed = seen.Where(x => x.Phase == ModelDownloadPhase.Completed).ToList();
        Assert.Equal([0, 1], completed.Select(x => x.Index));
        Assert.All(completed, x => Assert.Equal((true, false), (x.InPlace, x.PartLeft)));            // 通知の時点で、置き場にあり、.part は無い
        Assert.DoesNotContain(seen.Where(x => x.Phase != ModelDownloadPhase.Completed), x => x.InPlace);   // 置く前の通知では、まだ無い
        int firstOfSecond = seen.FindIndex(x => x.Index == 1);
        Assert.Equal(ModelDownloadPhase.Completed, seen[firstOfSecond - 1].Phase);                   // 次のファイルの接続の直前に、前のファイルの完了
        Assert.Equal(ModelDownloadPhase.Connecting, seen[firstOfSecond].Phase);
    }

    [Fact]
    public async Task Completed_is_not_reported_for_a_file_that_failed_verification_but_is_for_the_ones_before_it()
    {
        using var dir = new TempDirectory();
        byte[] a = Data(30_000, 1), b = Data(20_000, 2), wrong = Data(20_000, 3);
        var server = new FakeServer((r, _) => r.RequestUri!.AbsolutePath.EndsWith("a.bin", StringComparison.Ordinal) ? Ok(a) : Ok(wrong));
        var items = new[]
        {
            new DownloadItem(ModelSets.FeatureVector, "a.bin", "https://models.example.test/x/a.bin", Path.Combine(dir.Path, "a.bin"), Sha(a), a.Length),
            new DownloadItem(ModelSets.FeatureVector, "b.bin", "https://models.example.test/x/b.bin", Path.Combine(dir.Path, "b.bin"), Sha(b), b.Length),
        };
        var collect = new Collect();

        using var downloader = new ModelDownloader(server, Fast(), log: null);
        ModelDownloadException e = await Assert.ThrowsAsync<ModelDownloadException>(() => downloader.DownloadItemsAsync(items, collect, TestContext.Current.CancellationToken));

        Assert.Equal(ModelDownloadFailure.HashMismatch, e.Kind);
        Assert.Equal(["a.bin"], collect.Items.Where(x => x.Phase == ModelDownloadPhase.Completed).Select(x => x.FileName));
        Assert.Equal(ModelDownloadPhase.Verifying, collect.Items[^1].Phase);                          // b は、確かめるところまで
    }

    /// <summary>通知が届いた時点で、ファイルの置き場を調べるための受け口(同期。<see cref="Progress{T}"/> のように、あとで届けない)。</summary>
    private sealed class Probe(Action<ModelDownloadProgress> onReport) : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value) => onReport(value);
    }

    // ---------------------------------------------------------------- 取るものの見積もりと URL(カタログから)

    [Fact]
    public async Task Nothing_is_sent_when_no_set_is_asked_for_or_nothing_is_missing()
    {
        using var dir = new TempDirectory();
        var server = new FakeServer((_, _) => throw new InvalidOperationException("ネットワークに出てはいけない"));
        using var downloader = new ModelDownloader(server, Fast(), log: null);

        ModelDownloadSummary none = await downloader.DownloadAsync(dir.File("e"), dir.File("v"), ModelSets.None, null, TestContext.Current.CancellationToken);
        ModelDownloadSummary empty = await downloader.DownloadItemsAsync([], null, TestContext.Current.CancellationToken);

        Assert.Equal(new ModelDownloadSummary(0, 0), none);
        Assert.Equal(new ModelDownloadSummary(0, 0), empty);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void The_plan_lists_every_missing_catalog_file_with_sizes_and_the_free_space_it_needs()
    {
        using var dir = new TempDirectory();

        ModelDownloadPlan plan = ModelDownloader.Plan(dir.File("e"), dir.File("v"), ModelSets.FeatureVector | ModelSets.Vlm);

        Assert.Equal(4 + 2, plan.Files.Count);
        Assert.All(plan.Files, f => Assert.Equal((ModelFileState.Missing, 0L), (f.State, f.PartialBytes)));
        long expected = ModelCatalog.Current.Files.Sum(f => f.Size) + VlmCatalog.Current.Gguf.Size + VlmCatalog.Current.Mmproj.Size;
        Assert.Equal(expected, plan.TotalBytes);
        Assert.Equal(expected, plan.RemainingBytes);
        Assert.Equal(expected + ModelDownloadPlan.FreeSpaceMargin, plan.RequiredFreeBytes);
        Assert.False(plan.IsEmpty);
        Assert.Equal(4, ModelDownloader.Plan(dir.File("e"), dir.File("v"), ModelSets.FeatureVector).Files.Count);
        Assert.Equal(2, ModelDownloader.Plan(dir.File("e"), dir.File("v"), ModelSets.Vlm).Files.Count);
        Assert.True(ModelDownloader.Plan(dir.File("e"), dir.File("v"), ModelSets.None).IsEmpty);
        Assert.Equal(0, ModelDownloader.Plan(dir.File("e"), dir.File("v"), ModelSets.None).RequiredFreeBytes);
    }

    [Fact]
    public void The_plan_counts_a_part_file_as_already_received_and_skips_files_that_are_present()
    {
        using var dir = new TempDirectory();
        string vlm = dir.File("v");
        Directory.CreateDirectory(vlm);
        VlmFile gguf = VlmCatalog.Current.Gguf, mmproj = VlmCatalog.Current.Mmproj;
        File.WriteAllBytes(Path.Combine(vlm, mmproj.RelativePath + ".part"), new byte[1000]);
        using (var f = new FileStream(Path.Combine(vlm, gguf.RelativePath), FileMode.Create)) { }   // 空のファイル = サイズ違い

        ModelDownloadPlan plan = ModelDownloader.Plan(dir.File("e"), vlm, ModelSets.Vlm);

        ModelDownloadFile g = plan.Files.Single(x => x.RelativePath == gguf.RelativePath);
        ModelDownloadFile m = plan.Files.Single(x => x.RelativePath == mmproj.RelativePath);
        Assert.Equal(ModelFileState.SizeMismatch, g.State);
        Assert.Equal(ModelFileState.Missing, m.State);
        Assert.Equal(1000, m.PartialBytes);
        Assert.Equal(gguf.Size + mmproj.Size - 1000, plan.RemainingBytes);
    }

    [Fact]
    public void The_download_urls_point_at_the_pinned_commit_and_the_destination_is_where_the_verifier_reads()
    {
        using var dir = new TempDirectory();
        string embedding = dir.File("e"), vlm = dir.File("v");

        List<DownloadItem> items = ModelDownloader.BuildItems(embedding, vlm, ModelSets.FeatureVector | ModelSets.Vlm);

        foreach (ModelFile f in ModelCatalog.Current.Files)
        {
            DownloadItem item = items.Single(i => i.Set == ModelSets.FeatureVector && i.Name == f.RelativePath);
            Assert.Equal($"{ModelCatalog.HfBaseUrl}/{ModelCatalog.HfRepository}/resolve/{ModelCatalog.HfRevision}/{f.RelativePath}", item.Url);
            Assert.Equal(ModelVerifier.PathOf(embedding, f), item.Destination);                  // 検証が読む場所と同じ
            Assert.Equal((f.Sha256, f.Size), (item.Sha256, item.Size));
        }
        foreach (VlmFile f in new[] { VlmCatalog.Current.Gguf, VlmCatalog.Current.Mmproj })
        {
            DownloadItem item = items.Single(i => i.Set == ModelSets.Vlm && i.Name == f.RelativePath);
            Assert.Equal($"{ModelCatalog.HfBaseUrl}/{VlmCatalog.HfRepository}/resolve/{VlmCatalog.HfRevision}/{f.RelativePath}", item.Url);
            Assert.Equal(Path.Combine(vlm, f.RelativePath), item.Destination);
        }
        Assert.All(items, i => Assert.StartsWith("https://", i.Url, StringComparison.Ordinal));
        Assert.All(items, i => Assert.DoesNotContain('?', i.Url));
    }

    [Fact]
    public void Taking_both_sets_in_one_call_goes_through_the_feature_vector_model_first_and_then_the_text_search_model()
    {
        // セットアップの画面の「2 つともダウンロード」は、組を 1 回の呼び出しで渡す。画面が「特徴ベクトル検索のモデルから先に取ります」と言う順は、ここで決まる
        using var dir = new TempDirectory();

        List<DownloadItem> items = ModelDownloader.BuildItems(dir.File("e"), dir.File("v"), ModelSets.FeatureVector | ModelSets.Vlm);
        ModelDownloadPlan plan = ModelDownloader.Plan(dir.File("e"), dir.File("v"), ModelSets.FeatureVector | ModelSets.Vlm);

        ModelSets[] expected = [.. Enumerable.Repeat(ModelSets.FeatureVector, 4), .. Enumerable.Repeat(ModelSets.Vlm, 2)];
        Assert.Equal(expected, items.Select(i => i.Set));
        Assert.Equal(expected, plan.Files.Select(f => f.Set));
    }

    [Fact]
    public void The_two_catalogs_have_no_file_with_the_same_relative_path()
    {
        // セットアップの画面は、取り終えたファイルを、モデルのフォルダからの相対パスだけで覚える(「2 つとも」でも、どちらの組のファイルかを取り違えないため)
        string[] embedding = [.. ModelCatalog.Current.Files.Select(f => f.RelativePath)];
        string[] vlm = [VlmCatalog.Current.Gguf.RelativePath, VlmCatalog.Current.Mmproj.RelativePath];

        Assert.Empty(embedding.Intersect(vlm, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Both_commits_are_real_forty_digit_commits_and_the_siglip2_one_is_the_uploaded_commit()
    {
        // ネットワークには出ない(コミットが、取得元に実在することは、ここでは確かめない)
        Assert.Matches("^[0-9a-f]{40}$", ModelCatalog.HfRevision);
        Assert.Matches("^[0-9a-f]{40}$", VlmCatalog.HfRevision);
        Assert.NotEqual(new string('0', 40), ModelCatalog.HfRevision);
        Assert.NotEqual(new string('0', 40), VlmCatalog.HfRevision);
        Assert.Equal("2fffd6544cbb7b1bfcde13e3f25d3c7c11911043", ModelCatalog.HfRevision);       // ohanami-app/siglip2-so400m-patch16-384-onnx に上げた 1 回のコミット
    }

    [Fact]
    public void Url_path_segments_are_escaped()
    {
        Assert.Equal("https://huggingface.co/a/b/resolve/rev/dir/my%20file%2B1.bin", ModelDownloader.UrlOf("a/b", "rev", "dir/my file+1.bin"));
    }
}
