using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Power;

namespace Ohanami.Core.Models;

/// <summary>取る 1 ファイル: 取得元の URL・置き場・期待するサイズと SHA-256。</summary>
internal sealed record DownloadItem(ModelSets Set, string Name, string Url, string Destination, string Sha256, long Size);

/// <summary>ダウンローダーの調整できる値(既定が製品の値。テストが短い時間・小さい値に替える)。</summary>
internal sealed record ModelDownloadOptions
{
    /// <summary>サーバーに接続して、応答の頭(ヘッダ)が来るまでの上限。</summary>
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>本文の読み取りで、1 バイトも来ない時間の上限。超えたら接続を切って、続きから再開する。</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>切れた・止まったあとの、再開までの待ち。</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>進みが無いまま、この回数だけ続けて切れたら、諦めて止める(進んだら数え直す)。</summary>
    public int MaxFailuresWithoutProgress { get; init; } = 3;

    /// <summary>User-Agent。固定の文字列(アプリの名前だけ。利用者や PC を表す値は付けない)。</summary>
    public string UserAgent { get; init; } = "Ohanami";

    /// <summary>置き場(ルート)の空き(バイト)。分からなければ null(確かめずに進む)。</summary>
    public Func<string, long?> FreeSpace { get; init; } = DefaultFreeSpace;

    /// <summary>本文を読むバッファの大きさ。</summary>
    public int BufferSize { get; init; } = 256 * 1024;

    private static long? DefaultFreeSpace(string root)
    {
        try { return new DriveInfo(root).AvailableFreeSpace; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// モデルのダウンロード。**利用者が「ダウンロード」を押したときだけ**呼ぶ(押す前には、ネットワークに出ない)。<c>OhanamiCore</c> を開かずに使える(セットアップの画面から)。
/// 送るのは GET だけ。Cookie・認証・Referer・利用者や PC を表す値は付けない(User-Agent は固定の文字列)。
/// <list type="bullet">
/// <item>URL は <c>https://huggingface.co/&lt;リポジトリ&gt;/resolve/&lt;コミット&gt;/&lt;ファイル&gt;</c>(<see cref="ModelCatalog"/>・<see cref="VlmCatalog"/> の定数から作る)。
///   続きの取得もこの URL から始める(配信元の署名つき URL は覚えない)。</item>
/// <item><c>&lt;ファイル&gt;.part</c> に書く。続きは <c>Range: bytes=N-</c>。206 は Content-Range(開始・全体のサイズ)を確かめ、200 は Content-Length が定義と違えば本文を読む前に止める。
///   Range を無視して 200 が来たら、足さずに 0 から書き直す。判定は標準のヘッダのサイズと、最後の SHA-256(HF だけの X-Linked-* は読まない)。</item>
/// <item>取り終わったら <see cref="ModelVerifier.VerifyPathAsync"/> で .part を検証し、同じフォルダの中で改名する。不一致なら .part を消して止める(自動では繰り返さない)。
///   取りながらのハッシュはしない。</item>
/// <item>本文の読み取りに、止まりの検出を持つ(<see cref="ModelDownloadOptions.StallTimeout"/>。ResponseHeadersRead のとき HttpClient.Timeout は本文に効かない)。
///   切れた・止まったときは、続きから再開する(進みが無いまま続けて切れたら止める)。</item>
/// <item>始める前に、置き場の空きと残りを比べる。取っている間は <see cref="SleepGuard"/> でスリープを抑える。</item>
/// </list>
/// </summary>
public sealed class ModelDownloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly ModelDownloadOptions _options;
    private readonly ICoreLog? _log;
    private string _lastHost = "";

    /// <summary>既定の通信の設定(Cookie なし・自動展開なし・リダイレクトを追う)で作る。</summary>
    public ModelDownloader(ICoreLog? log = null) : this(CreateHandler(), new ModelDownloadOptions(), log, ownsHandler: true) { }

    internal ModelDownloader(HttpMessageHandler handler, ModelDownloadOptions options, ICoreLog? log, bool ownsHandler = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _log = log;
        _http = new HttpClient(handler, disposeHandler: ownsHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => _http.Dispose();

    private static SocketsHttpHandler CreateHandler() => new()
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    // ---------------------------------------------------------------- 見積もり(ネットワークには出ない)

    /// <summary>
    /// 取る必要のあるファイル(無い・サイズが違うもの)と、大きさ・残り・要る空きを返す。<paramref name="embeddingDirectory"/> は
    /// <c>ViT-SO400M-16-SigLIP2-384__ohanami</c> に当たるディレクトリ、<paramref name="vlmDirectory"/> は <c>Qwen3-VL-8B-Instruct-GGUF</c> に当たるディレクトリ
    /// (どちらも、検証が読む場所と同じ。<c>AppPaths</c> が決める)。
    /// </summary>
    public static ModelDownloadPlan Plan(string embeddingDirectory, string vlmDirectory, ModelSets sets)
    {
        var files = new List<ModelDownloadFile>();
        foreach (DownloadItem item in BuildItems(embeddingDirectory, vlmDirectory, sets))
        {
            ModelFileState state = StateOf(item);
            if (state == ModelFileState.Present) continue;
            files.Add(new ModelDownloadFile(item.Set, item.Name, item.Size, state, PartialBytesOf(item)));
        }
        return new ModelDownloadPlan(files, embeddingDirectory, vlmDirectory);
    }

    /// <summary>取得元の URL(コミットを指す)。パスの各部分は URL エンコードする。</summary>
    internal static string UrlOf(string repository, string revision, string relativePath) =>
        ModelCatalog.HfBaseUrl + "/" + repository + "/resolve/" + revision + "/"
        + string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));

    internal static List<DownloadItem> BuildItems(string embeddingDirectory, string vlmDirectory, ModelSets sets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(vlmDirectory);
        var items = new List<DownloadItem>();
        if (sets.HasFlag(ModelSets.FeatureVector))
        {
            foreach (ModelFile f in ModelCatalog.Current.Files)
            {
                items.Add(new DownloadItem(ModelSets.FeatureVector, f.RelativePath,
                    UrlOf(ModelCatalog.HfRepository, ModelCatalog.HfRevision, f.RelativePath), ModelVerifier.PathOf(embeddingDirectory, f), f.Sha256, f.Size));
            }
        }
        if (sets.HasFlag(ModelSets.Vlm))
        {
            VlmSpec spec = VlmCatalog.Current;
            foreach (VlmFile f in new[] { spec.Gguf, spec.Mmproj })
            {
                items.Add(new DownloadItem(ModelSets.Vlm, f.RelativePath,
                    UrlOf(VlmCatalog.HfRepository, VlmCatalog.HfRevision, f.RelativePath),
                    Path.Combine(vlmDirectory, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)), f.Sha256, f.Size));
            }
        }
        return items;
    }

    private static ModelFileState StateOf(DownloadItem item)
    {
        var info = new FileInfo(item.Destination);
        if (!info.Exists) return ModelFileState.Missing;
        return info.Length == item.Size ? ModelFileState.Present : ModelFileState.SizeMismatch;
    }

    private static long PartialBytesOf(DownloadItem item)
    {
        var info = new FileInfo(item.Destination + ".part");
        return info.Exists ? info.Length : 0;
    }

    // ---------------------------------------------------------------- ダウンロード

    /// <summary>
    /// 取る必要のあるファイル(<see cref="Plan"/> と同じ)を、順に取って、検証して、置き場に置く。取るものが無ければ、何もしない(ネットワークに出ない)。
    /// 失敗は <see cref="ModelDownloadException"/>。キャンセルは <see cref="OperationCanceledException"/>(.part は残す=次に続きから取る)。
    /// </summary>
    public Task<ModelDownloadSummary> DownloadAsync(
        string embeddingDirectory, string vlmDirectory, ModelSets sets, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        List<DownloadItem> needed = [.. BuildItems(embeddingDirectory, vlmDirectory, sets).Where(i => StateOf(i) != ModelFileState.Present)];
        return DownloadItemsAsync(needed, progress, ct);
    }

    internal async Task<ModelDownloadSummary> DownloadItemsAsync(IReadOnlyList<DownloadItem> items, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return new ModelDownloadSummary(0, 0);

        CheckFreeSpace(items);

        SleepGuard? sleepGuard = null;
        try { sleepGuard = SleepGuard.Start(); }
        catch (InvalidOperationException e) { _log?.Warning("ダウンロード中のスリープの抑止を始められなかった(続ける): " + e.Message); }

        try
        {
            long totalBytes = items.Sum(i => i.Size);
            long doneBefore = 0, received = 0;
            int downloaded = 0;
            for (int index = 0; index < items.Count; index++)
            {
                received += await DownloadOneAsync(items[index], index, items.Count, doneBefore, totalBytes, progress, ct).ConfigureAwait(false);
                doneBefore += items[index].Size;
                downloaded++;
            }
            return new ModelDownloadSummary(downloaded, received);
        }
        finally
        {
            sleepGuard?.Dispose();
        }
    }

    private void CheckFreeSpace(IReadOnlyList<DownloadItem> items)
    {
        foreach (var group in items.GroupBy(i => Path.GetPathRoot(Path.GetFullPath(i.Destination)) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            long remaining = group.Sum(i => i.Size - Math.Min(PartialBytesOf(i), i.Size));
            if (remaining <= 0) continue;
            long needed = remaining + ModelDownloadPlan.FreeSpaceMargin;
            if (_options.FreeSpace(group.Key) is { } available && available < needed)
            {
                throw new ModelDownloadException(ModelDownloadFailure.InsufficientSpace,
                    $"置き場の空きが足りない: {group.Key} の空きは {FormatMiB(available)}、要るのは {FormatMiB(needed)}(残り {FormatMiB(remaining)} + 余裕 {FormatMiB(ModelDownloadPlan.FreeSpaceMargin)})。",
                    neededBytes: needed, availableBytes: available);
            }
        }
    }

    /// <summary>1 ファイルを取って検証して置く。今回ネットワークから受け取ったバイト数を返す。</summary>
    private async Task<long> DownloadOneAsync(
        DownloadItem item, int index, int count, long doneBefore, long totalBytes, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        string part = item.Destination + ".part";
        try { Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ModelDownloadException(ModelDownloadFailure.Io, $"置き場のフォルダを作れない: {Path.GetDirectoryName(item.Destination)}: {e.Message}", item.Name, inner: e);
        }

        long have = LengthOf(part);
        if (have > item.Size) { DeleteQuietly(part); have = 0; }
        _lastHost = new Uri(item.Url).Host;

        long received = 0;
        int failuresWithoutProgress = 0;
        bool restartedAfter416 = false;
        var reportClock = Stopwatch.StartNew();

        void Report(ModelDownloadPhase phase, long fileBytes, bool force)
        {
            if (progress is null) return;
            if (!force && reportClock.ElapsedMilliseconds < 100) return;
            reportClock.Restart();
            progress.Report(new ModelDownloadProgress(phase, item.Name, index, count, doneBefore + fileBytes, totalBytes));
        }

        while (have < item.Size)
        {
            ct.ThrowIfCancellationRequested();
            Report(ModelDownloadPhase.Connecting, have, force: true);
            long before = have;
            try
            {
                (long newHave, long got, bool retryFromZero) = await TransferAsync(item, part, have, restartedAfter416, (bytes) => Report(ModelDownloadPhase.Downloading, bytes, force: false), ct)
                    .ConfigureAwait(false);
                received += got;
                have = newHave;
                if (retryFromZero) restartedAfter416 = true;
            }
            catch (TransientFailure t)
            {
                have = LengthOf(part);
                received += Math.Max(0, have - before);
                failuresWithoutProgress = have > before ? 0 : failuresWithoutProgress + 1;
                _log?.Warning($"ダウンロードが切れた({item.Name}、{t.Host}): {t.Message}。{have.ToString(CultureInfo.InvariantCulture)} バイトまで取れている。");
                if (failuresWithoutProgress >= _options.MaxFailuresWithoutProgress)
                {
                    throw new ModelDownloadException(t.IsStall ? ModelDownloadFailure.Stalled : ModelDownloadFailure.Network,
                        t.IsStall
                            ? $"{item.Name}: データが {_options.StallTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} 秒以上来なくなり、再開しても進まない(接続先 {t.Host})。"
                            : $"{item.Name}: 接続できない、または途中で切れて、再開しても進まない(接続先 {t.Host}): {t.Message}",
                        item.Name, host: t.Host, inner: t.InnerException);
                }
                await Task.Delay(_options.RetryDelay, ct).ConfigureAwait(false);
            }
        }

        // 検証 → 同じフォルダの中で改名
        Report(ModelDownloadPhase.Verifying, item.Size, force: true);
        try
        {
            await ModelVerifier.VerifyPathAsync(part, item.Name, item.Sha256, item.Size, ct).ConfigureAwait(false);
        }
        catch (ModelMismatchException m)
        {
            long actual = LengthOf(part);
            DeleteQuietly(part);
            bool sizeWrong = actual != item.Size;
            throw new ModelDownloadException(sizeWrong ? ModelDownloadFailure.SizeMismatch : ModelDownloadFailure.HashMismatch,
                sizeWrong
                    ? $"{item.Name}: 取り終わったファイルのサイズが定義と違う(期待 {item.Size.ToString(CultureInfo.InvariantCulture)} バイト、実際 {actual.ToString(CultureInfo.InvariantCulture)} バイト)。取ったものは消した。"
                    : $"{item.Name}: 取り終わったファイルの SHA-256 が定義と違う(期待 {item.Sha256}、実際 {m.Actual})。取ったものは消した。自動では取り直さない。",
                item.Name, host: _lastHost, inner: m);
        }

        try { File.Move(part, item.Destination, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ModelDownloadException(ModelDownloadFailure.Io, $"{item.Name}: 取ったファイルを置き場に動かせない({item.Destination}): {e.Message}", item.Name, inner: e);
        }
        Report(ModelDownloadPhase.Completed, item.Size, force: true);
        _log?.Info($"ダウンロードが済んだ: {item.Name}({item.Size.ToString(CultureInfo.InvariantCulture)} バイト、今回受け取ったのは {received.ToString(CultureInfo.InvariantCulture)} バイト)");
        return received;
    }

    /// <summary>
    /// 1 回の接続で、<paramref name="have"/> バイトの続きを取れるだけ取る。戻り値: (取れたあとの .part のサイズ、今回受け取った量、416 で 0 からやり直す印)。
    /// 切れた・止まったときは <see cref="TransientFailure"/>(呼び出し側が、続きから再開するか、諦めるかを決める)。
    /// </summary>
    private async Task<(long Have, long Received, bool RestartedAfter416)> TransferAsync(
        DownloadItem item, string part, long have, bool alreadyRestarted, Action<long> reportBytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);

        HttpResponseMessage response;
        using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            headerCts.CancelAfter(_options.HeaderTimeout);
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerCts.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                throw new TransientFailure(e.Message, _lastHost, isStall: false, e);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TransientFailure($"応答の頭が {_options.HeaderTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} 秒以内に来なかった", _lastHost, isStall: false);
            }
        }

        using (response)
        {
            string host = response.RequestMessage?.RequestUri?.Host ?? _lastHost;
            _lastHost = host;
            int status = (int)response.StatusCode;

            long expectedBody;
            FileMode mode;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                if (have == 0)
                    throw new ModelDownloadException(ModelDownloadFailure.UnexpectedResponse, $"{item.Name}: 範囲を頼んでいないのに 206 が返った。", item.Name, status, host);
                ContentRangeHeaderValue? cr = response.Content.Headers.ContentRange;
                if (cr is null || cr.From != have || cr.Length is null || cr.To is null)
                    throw new ModelDownloadException(ModelDownloadFailure.UnexpectedResponse,
                        $"{item.Name}: 206 の Content-Range が、頼んだ範囲(先頭 {have.ToString(CultureInfo.InvariantCulture)} バイトの続き)と合わない: {cr}", item.Name, status, host);
                if (cr.Length != item.Size)
                    throw new ModelDownloadException(ModelDownloadFailure.SizeMismatch,
                        $"{item.Name}: 取得元のファイルの全体のサイズ {cr.Length?.ToString(CultureInfo.InvariantCulture)} バイトが、定義の {item.Size.ToString(CultureInfo.InvariantCulture)} バイトと違う。取る前に止めた。", item.Name, status, host);
                expectedBody = item.Size - have;
                if (cr.To != item.Size - 1 || (response.Content.Headers.ContentLength is { } len206 && len206 != expectedBody))
                    throw new ModelDownloadException(ModelDownloadFailure.UnexpectedResponse, $"{item.Name}: 206 の範囲の終わり・長さが、残りと合わない。", item.Name, status, host);
                mode = FileMode.Append;
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                if (response.Content.Headers.ContentLength is { } len && len != item.Size)
                    throw new ModelDownloadException(ModelDownloadFailure.SizeMismatch,
                        $"{item.Name}: 取得元のファイルのサイズ {len.ToString(CultureInfo.InvariantCulture)} バイトが、定義の {item.Size.ToString(CultureInfo.InvariantCulture)} バイトと違う。取る前に止めた。", item.Name, status, host);
                if (have > 0) _log?.Warning($"{item.Name}: 範囲を頼んだが 200 が返った(範囲を無視された)。足さずに 0 から書き直す。");
                have = 0;
                expectedBody = item.Size;
                mode = FileMode.Create;
            }
            else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0 && !alreadyRestarted)
            {
                _log?.Warning($"{item.Name}: 範囲が受け付けられなかった(416)。.part を消して 0 からやり直す。");
                DeleteQuietly(part);
                return (0, 0, true);
            }
            else
            {
                throw new ModelDownloadException(ModelDownloadFailure.HttpStatus,
                    $"{item.Name}: サーバー({host})が状態 {status.ToString(CultureInfo.InvariantCulture)} を返した。", item.Name, status, host);
            }

            // 本文: .part に書く。読み取りごとに、止まりを見る
            Stream body;
            try { body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); }
            catch (HttpRequestException e) { throw new TransientFailure(e.Message, host, isStall: false, e); }

            FileStream file;
            try { file = new FileStream(part, mode, FileAccess.Write, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ModelDownloadException(ModelDownloadFailure.Io, $"{item.Name}: 書き込み先を開けない({part}): {e.Message}", item.Name, host: host, inner: e);
            }

            long got = 0;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
            try
            {
                await using (file.ConfigureAwait(false))
                await using (body.ConfigureAwait(false))
                {
                    while (true)
                    {
                        int read;
                        using (var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            stallCts.CancelAfter(_options.StallTimeout);
                            try
                            {
                                read = await body.ReadAsync(buffer.AsMemory(0, _options.BufferSize), stallCts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                throw new TransientFailure("データが来なくなった", host, isStall: true);
                            }
                            catch (Exception e) when (e is IOException or HttpRequestException)
                            {
                                throw new TransientFailure(e.Message, host, isStall: false, e);
                            }
                        }
                        if (read == 0) break;
                        if (got + read > expectedBody)
                        {
                            file.Dispose();
                            DeleteQuietly(part);
                            throw new ModelDownloadException(ModelDownloadFailure.SizeMismatch,
                                $"{item.Name}: 定義より多くのデータが来た(期待 {item.Size.ToString(CultureInfo.InvariantCulture)} バイト)。取ったものは消した。", item.Name, status, host);
                        }
                        try { await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false); }
                        catch (IOException e)
                        {
                            throw new ModelDownloadException(ModelDownloadFailure.Io, $"{item.Name}: ファイルに書けない({part}): {e.Message}", item.Name, host: host, inner: e);
                        }
                        got += read;
                        reportBytes(have + got);
                    }
                    await file.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (got < expectedBody)
                throw new TransientFailure($"本文が途中で終わった({(have + got).ToString(CultureInfo.InvariantCulture)} / {item.Size.ToString(CultureInfo.InvariantCulture)} バイト)", host, isStall: false);
            return (have + got, got, false);
        }
    }

    /// <summary>切れた・止まった(続きから再開できる失敗)。</summary>
    private sealed class TransientFailure(string message, string host, bool isStall, Exception? inner = null) : Exception(message, inner)
    {
        public string Host { get; } = host;

        public bool IsStall { get; } = isStall;
    }

    private static long LengthOf(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* 消せなければ、次の回に、サイズで気づく */ }
    }

    private static string FormatMiB(long bytes) => (bytes / (1024.0 * 1024)).ToString("N0", CultureInfo.InvariantCulture) + "MiB";
}
