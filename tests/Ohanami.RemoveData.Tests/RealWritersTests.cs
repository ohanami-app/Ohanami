using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.App;
using Ohanami.Core;
using Ohanami.Core.Data;
using Ohanami.Core.Embedding;
using Ohanami.Core.Models;
using Xunit;

namespace Ohanami.RemoveData.Tests;

/// <summary>
/// 端から端まで: アプリの**本物の書き手**で作った根を、道具で消す。道具の名前の表が、実際にアプリの書く名前とずれていれば、ここで落ちる
/// (手で書いた名前だけで作った根は、表と同じ思い込みを写すだけで、ずれに気づけない)。
/// </summary>
public sealed class RealWritersTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_data_folder_made_by_the_apps_own_writers_is_removed_completely()
    {
        using TempArea area = TempArea.Create();
        string root = area.Combine("data");
        string embeddingDirectory = Path.Combine(root, "models", OhanamiCore.ModelDirectoryName);
        Directory.CreateDirectory(root);

        // データベースと、移行の前のバックアップ(VACUUM INTO): 版 1 の DB を作って閉じ、今の定義で開き直す
        string dbPath = Path.Combine(root, "ohanami.db");
        var v1Only = new DatabaseSchema(1, DatabaseSchema.Default.CreateInitial, [], SchemaV1.Tables);
        using (OhanamiDatabase.Open(dbPath, v1Only)) { }
        using (OhanamiDatabase db = OhanamiDatabase.Open(dbPath))
        {
            // サムネイルのキャッシュ(.jpg。書くときの一時ファイルは、改名で消える)
            string hash = Convert.ToHexStringLower(SHA256.HashData([1, 2, 3]));
            var thumbnails = new ThumbnailService(db, Path.Combine(root, "thumbs"));
            thumbnails.CreateFrom(hash, new RgbImage(64, 36, new byte[64 * 36 * 3]), 32, 18);
        }

        // 診断ログ(ohanami-yyyyMMdd.log)
        new FileCoreLog(Path.Combine(root, "logs")).Info("end-to-end test");

        // 取りかけのモデル(.part): 偽のサーバーで、途中で切れて止まる
        string partDestination = Path.Combine(embeddingDirectory, ModelFiles.InspectEmbedding(embeddingDirectory).Files[0].RelativePath.Replace('/', '\\'));
        await LeavePartFileAsync(partDestination);

        // 名前で置く物(書き手が、起動したプロセスや OS の都合に結びついていて、テストで本物を動かせない物)。名前の型は、
        // Core.Tests の LlamaServerTests(ログの名前)と ChildProcessGuardTests(pid ファイルの名前)が、書き手の側から見張っている。
        // -wal / -shm は SQLite が、DB を開いている間だけ作る(閉じると消える)ので、名前で置く。
        foreach (string name in new[] { "llama-server.pid", "llama-server.pid." + DataRootLayout.Hex32 + ".tmp", "ohanami.db-wal", "ohanami.db-shm" })
            DataRootLayout.WriteFile(Path.Combine(root, name));
        DataRootLayout.WriteFile(Path.Combine(root, "logs", "llama-server-20261120-010203-004.log"));

        // 書き手が実際に作った物が、そろっている(根の中が空振りでない)
        Assert.True(File.Exists(dbPath));
        Assert.True(File.Exists(dbPath + ".bak-v1"));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "thumbs"), "*.jpg", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "logs"), "ohanami-*.log"));
        Assert.True(File.Exists(partDestination + ".part"));

        ScriptResult r = ScriptEntrance.RunScript(area.AppFolder, ["-DataRoot", root, "-Mode", "All", "-Yes"]);

        Assert.Equal(0, r.ExitCode);
        Assert.False(Directory.Exists(root));
    }

    // ---------------------------------------------------------------- 偽のサーバー(ネットワークには出ない)

    private static async Task LeavePartFileAsync(string destination)
    {
        byte[] data = new byte[60_000];
        new Random(1).NextBytes(data);
        var item = new DownloadItem(ModelSets.FeatureVector, "model.bin", "https://models.example.test/repo/resolve/abc123/model.bin", destination,
            Convert.ToHexStringLower(SHA256.HashData(data)), data.Length);
        var options = new ModelDownloadOptions
        {
            StallTimeout = TimeSpan.FromMilliseconds(150),
            HeaderTimeout = TimeSpan.FromSeconds(5),
            RetryDelay = TimeSpan.Zero,
            MaxFailuresWithoutProgress = 2,
            FreeSpace = _ => null,
            BufferSize = 8192,
        };
        using var downloader = new ModelDownloader(new CutOffServer(data, firstChunk: 8192), options, log: null);
        await Assert.ThrowsAsync<ModelDownloadException>(() => downloader.DownloadItemsAsync([item], progress: null, Ct));
    }

    /// <summary>最初の要求で <c>firstChunk</c> バイトだけ返して接続が切れ、続きの要求は 1 バイトも返さずに切れる(進みが無いので、ダウンローダーは止まる)。</summary>
    private sealed class CutOffServer(byte[] data, int firstChunk) : HttpMessageHandler
    {
        private int _count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int n = Interlocked.Increment(ref _count) - 1;
            if (n == 0)
            {
                var content = new StreamContent(new CutOffBody(data, firstChunk));
                content.Headers.ContentLength = data.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            int from = (int)(request.Headers.Range?.Ranges.First().From ?? 0);
            byte[] tail = data[from..];
            var partial = new StreamContent(new CutOffBody(tail, 0));
            partial.Headers.ContentLength = tail.Length;
            partial.Headers.ContentRange = new ContentRangeHeaderValue(from, data.Length - 1, data.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = partial });
        }
    }

    private sealed class CutOffBody(byte[] data, int cutAfter) : Stream
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

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served >= cutAfter) throw new IOException("接続が切れた(偽)");
            int n = Math.Min(Math.Min(buffer.Length, 4096), cutAfter - _served);
            data.AsMemory(_served, n).CopyTo(buffer);
            _served += n;
            return ValueTask.FromResult(n);
        }
    }
}
