using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core;
using Ohanami.Core.Models;

namespace Ohanami.App;

/// <summary>
/// セットアップの画面が使う、モデルのダウンロードの口(<see cref="ModelDownloader"/> を包む)。テストは、ネットワークに出ない偽物に替える。
/// 取る前の見積もり(<see cref="Plan"/>・<see cref="FreeSpace"/>)は、ネットワークに出ない。取る(<see cref="RunAsync"/>)のは、利用者が、セットアップの画面の「ダウンロード」か「2 つともダウンロード」(片方がそろっていれば「残りをダウンロード」)を押したときだけ。
/// </summary>
internal interface IModelDownloads
{
    /// <summary>取る必要のあるファイルと、大きさ・残り・要る空きを返す(ネットワークには出ない)。</summary>
    ModelDownloadPlan Plan(string embeddingDirectory, string vlmDirectory, ModelSets sets);

    /// <summary>置き場のドライブの空き(バイト)。分からなければ null。</summary>
    long? FreeSpace(string directory);

    /// <summary>
    /// 取る。失敗は <see cref="ModelDownloadException"/>、中止は <see cref="OperationCanceledException"/>(途中までのファイルは残り、次に続きから取る)。
    /// </summary>
    Task<ModelDownloadSummary> RunAsync(string embeddingDirectory, string vlmDirectory, ModelSets sets, IProgress<ModelDownloadProgress> progress, CancellationToken ct);
}

/// <summary>
/// 製品の実装。**ダウンロードを始める呼び出し(<c>ModelDownloader</c> を作って、取る)は、アプリの中でここの 1 か所だけ**(NetworkBoundaryGuardTests が見張る。
/// 呼ぶのは、セットアップの画面の、「ダウンロード」と一括(「2 つともダウンロード」「残りをダウンロード」)のボタンだけ)。
/// </summary>
internal sealed class SystemModelDownloads(ICoreLog log) : IModelDownloads
{
    public ModelDownloadPlan Plan(string embeddingDirectory, string vlmDirectory, ModelSets sets) =>
        ModelDownloader.Plan(embeddingDirectory, vlmDirectory, sets);

    public long? FreeSpace(string directory)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(directory));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public async Task<ModelDownloadSummary> RunAsync(string embeddingDirectory, string vlmDirectory, ModelSets sets, IProgress<ModelDownloadProgress> progress, CancellationToken ct)
    {
        using var downloader = new ModelDownloader(log);
        return await downloader.DownloadAsync(embeddingDirectory, vlmDirectory, sets, progress, ct).ConfigureAwait(true);
    }
}

/// <summary>
/// 取る速さ(直近の数秒の平均)。進み具合を受け取るたびに <see cref="Add"/> を呼ぶ。時刻は引数で渡す(テストできる)。
/// </summary>
internal sealed class TransferRate
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(1);
    private readonly Queue<(DateTime Time, long Bytes)> _samples = new();

    /// <summary>この時刻に、ここまで済んだ(通しのバイト数)。直近の <see cref="Window"/> より古い記録は捨てる(2 つは残す)。</summary>
    public void Add(DateTime time, long bytes)
    {
        _samples.Enqueue((time, bytes));
        while (_samples.Count > 2 && time - _samples.Peek().Time > Window) _samples.Dequeue();
    }

    /// <summary>記録を捨てる(段階が替わって、速さを測り直すとき)。</summary>
    public void Reset() => _samples.Clear();

    /// <summary>1 秒あたりのバイト数。記録が 1 秒に満たない間・進んでいない間は null。</summary>
    public double? BytesPerSecond
    {
        get
        {
            if (_samples.Count < 2) return null;
            (DateTime t0, long b0) = _samples.Peek();
            (DateTime t1, long b1) = _samples.ToArray()[^1];
            TimeSpan span = t1 - t0;
            if (span < MinSpan || b1 <= b0) return null;
            return (b1 - b0) / span.TotalSeconds;
        }
    }
}
