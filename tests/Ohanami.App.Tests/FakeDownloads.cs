using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core;

namespace Ohanami.App.Tests;

/// <summary>
/// ネットワークに出ない、偽のダウンロードの口。見積もり(<see cref="Plan"/>)は、実際のカタログ(名前と大きさ)と、置き場の実際の状態から作る(テストの置き場は存在しないので、全部が「無い」)。
/// 取る(<see cref="RunAsync"/>)は、テストが <see cref="Report"/>・<see cref="Complete"/>・<see cref="Fail"/> で動かす。呼ばれた回数と組は <see cref="Calls"/>。
/// </summary>
internal sealed class FakeDownloads : IModelDownloads
{
    private TaskCompletionSource<ModelDownloadSummary> _run = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IProgress<ModelDownloadProgress>? _progress;

    /// <summary>取る口が呼ばれた組(呼ばれた順)。押す前は空。</summary>
    public List<ModelSets> Calls { get; } = [];

    /// <summary>置き場のドライブの空き。</summary>
    public long? Free { get; set; } = 500_000_000_000;

    /// <summary>前回の続き(.part)の大きさ。0 でなければ、見積もりの最初のファイルに付ける。</summary>
    public long Partial { get; set; }

    /// <summary>最後に渡された中止の合図。</summary>
    public CancellationToken LastToken { get; private set; }

    public ModelDownloadPlan Plan(string embeddingDirectory, string vlmDirectory, ModelSets sets)
    {
        var files = new List<ModelDownloadFile>();
        if (sets.HasFlag(ModelSets.FeatureVector)) files.AddRange(Of(ModelSets.FeatureVector, ModelFiles.InspectEmbedding(embeddingDirectory)));
        if (sets.HasFlag(ModelSets.Vlm)) files.AddRange(Of(ModelSets.Vlm, ModelFiles.InspectVlm(vlmDirectory)));
        if (Partial > 0 && files.Count > 0) files[0] = files[0] with { PartialBytes = Partial };
        return new ModelDownloadPlan(files, embeddingDirectory, vlmDirectory);
    }

    private static IEnumerable<ModelDownloadFile> Of(ModelSets set, ModelSetStatus status) =>
        status.Files.Where(f => f.State != ModelFileState.Present).Select(f => new ModelDownloadFile(set, f.RelativePath, f.ExpectedSize, f.State, 0));

    /// <summary>置き場(フォルダ)ごとの空き。指定すると <see cref="Free"/> より優先する(2 つの置き場が別のドライブのときの確かめ)。</summary>
    public Func<string, long?>? FreeOf { get; set; }

    public long? FreeSpace(string directory) => FreeOf is { } of ? of(directory) : Free;

    public Task<ModelDownloadSummary> RunAsync(string embeddingDirectory, string vlmDirectory, ModelSets sets, IProgress<ModelDownloadProgress> progress, CancellationToken ct)
    {
        Calls.Add(sets);
        _progress = progress;
        LastToken = ct;
        _run = new TaskCompletionSource<ModelDownloadSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ModelDownloadSummary> run = _run;
        ct.Register(() => run.TrySetCanceled(ct));
        return run.Task;
    }

    /// <summary>進み具合を、取る側から届ける。</summary>
    public void Report(ModelDownloadProgress p) => _progress!.Report(p);

    /// <summary>取り終わる。</summary>
    public void Complete() => _run.TrySetResult(new ModelDownloadSummary(1, 1));

    /// <summary>失敗する。</summary>
    public void Fail(ModelDownloadException e) => _run.TrySetException(e);
}
