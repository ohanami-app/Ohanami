using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ohanami.Core.Models;

namespace Ohanami.Core;

/// <summary>モデルのファイル 1 つの、置き場での状態。</summary>
public enum ModelFileState
{
    /// <summary>在る。サイズも定義と同じ。</summary>
    Present,

    /// <summary>無い。</summary>
    Missing,

    /// <summary>在るが、サイズが定義と違う(途中のファイル・別のファイル)。</summary>
    SizeMismatch,
}

/// <summary>
/// モデルのファイル 1 つの、置き場での状態(<see cref="ModelFiles"/>)。<paramref name="RelativePath"/> はモデルのディレクトリからの相対パス(区切りは "/")、
/// <paramref name="ExpectedSize"/> は定義のサイズ(バイト)、<paramref name="ActualSize"/> は在るときのサイズ(無ければ null)。
/// </summary>
public sealed record ModelFileStatus(string RelativePath, long ExpectedSize, ModelFileState State, long? ActualSize);

/// <summary>
/// モデル 1 組(特徴ベクトルのモデル・VLM)の、要るファイルと、置き場での状態。<paramref name="DisplayName"/> は画面に出す名前(<see cref="OhanamiCore.ModelLabels"/> と同じ)、
/// <paramref name="Directory"/> は調べたディレクトリ。
/// </summary>
public sealed record ModelSetStatus(string DisplayName, string Directory, IReadOnlyList<ModelFileStatus> Files)
{
    /// <summary>ファイルを手に入れる Hugging Face のリポジトリ(例: <c>owner/name</c>)。まだ決まっていなければ null。</summary>
    public string? SourceRepository { get; init; }

    /// <summary>入手先のリビジョン(コミット)。入手先が決まっていなければ null。</summary>
    public string? SourceRevision { get; init; }

    /// <summary>入手先の画面のアドレス(リビジョンがあれば、そのリビジョンのファイルの一覧)。入手先が決まっていなければ null。</summary>
    public string? SourceUrl => SourceRepository is null ? null : ModelCatalog.RepositoryUrl(SourceRepository, SourceRevision);

    /// <summary>要るファイルが全部、在って、サイズも定義と同じ。</summary>
    public bool IsComplete => Files.All(f => f.State == ModelFileState.Present);

    /// <summary>無いファイルの相対パス(サイズが違うものは含めない)。</summary>
    public IReadOnlyList<string> MissingFiles => [.. Files.Where(f => f.State == ModelFileState.Missing).Select(f => f.RelativePath)];
}

/// <summary>
/// 要るモデルのファイルと、それぞれが置き場に在るかを返す、**読むだけ**の口。<c>OhanamiCore</c> を開かずに呼べる(起動の最初に、そろっているかを見るため)。
/// 調べるのは存在とサイズだけで、中身(ハッシュ)は読まない(4.6GB と 6.2GB を毎回読まない)。中身の検証は、モデルを使い始めるときに Core が行う。
/// </summary>
public static class ModelFiles
{
    /// <summary>特徴ベクトルのモデル(SigLIP2)の、要るファイルと状態。<paramref name="modelDirectory"/> は <c>ViT-SO400M-16-SigLIP2-384__ohanami</c> に当たるディレクトリ。</summary>
    public static ModelSetStatus InspectEmbedding(string modelDirectory) =>
        Inspect(ModelCatalog.DisplayName, modelDirectory, ModelCatalog.Current.Files.Select(f => (f.RelativePath, f.Size)))
            with { SourceRepository = ModelCatalog.HfRepository, SourceRevision = ModelCatalog.HfRevision };

    /// <summary>VLM(GGUF と mmproj)の、要るファイルと状態。<paramref name="modelDirectory"/> は <c>Qwen3-VL-8B-Instruct-GGUF</c> に当たるディレクトリ。</summary>
    public static ModelSetStatus InspectVlm(string modelDirectory)
    {
        VlmSpec spec = VlmCatalog.Current;
        return Inspect(VlmCatalog.DisplayName, modelDirectory, [(spec.Gguf.RelativePath, spec.Gguf.Size), (spec.Mmproj.RelativePath, spec.Mmproj.Size)])
            with { SourceRepository = VlmCatalog.HfRepository, SourceRevision = VlmCatalog.HfRevision };
    }

    /// <summary>ファイルの一覧(相対パスとサイズ)を渡して調べる(テストが、小さいファイルで確かめる)。</summary>
    internal static ModelSetStatus Inspect(string displayName, string modelDirectory, IEnumerable<(string RelativePath, long Size)> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        return new ModelSetStatus(displayName, modelDirectory, [.. files.Select(f => Inspect(modelDirectory, f.RelativePath, f.Size))]);
    }

    private static ModelFileStatus Inspect(string directory, string relativePath, long expectedSize)
    {
        string path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(path);
        if (!info.Exists) return new ModelFileStatus(relativePath, expectedSize, ModelFileState.Missing, null);
        return new ModelFileStatus(
            relativePath, expectedSize, info.Length == expectedSize ? ModelFileState.Present : ModelFileState.SizeMismatch, info.Length);
    }
}
