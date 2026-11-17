using System.IO;
using System.Linq;
using Ohanami.Core.Models;
using Ohanami.Core.Tests.Data;
using Xunit;

namespace Ohanami.Core.Tests.Models;

/// <summary>要るモデルのファイルと、置き場での状態を返す読むだけの口(<see cref="ModelFiles"/>)。存在とサイズだけを見る。</summary>
public sealed class ModelFilesTests
{
    private static void Write(string directory, string relative, int size)
    {
        string path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    [Fact]
    public void An_empty_directory_reports_every_file_as_missing()
    {
        using var dir = new TempDirectory();

        ModelSetStatus status = ModelFiles.Inspect("テスト用", dir.Path, [("a/one.bin", 10), ("two.bin", 20)]);

        Assert.False(status.IsComplete);
        Assert.All(status.Files, f => Assert.Equal((ModelFileState.Missing, (long?)null), (f.State, f.ActualSize)));
        Assert.Equal(["a/one.bin", "two.bin"], status.MissingFiles);
        Assert.Equal("テスト用", status.DisplayName);
    }

    [Fact]
    public void A_directory_that_does_not_exist_is_not_an_error()
    {
        using var dir = new TempDirectory();

        ModelSetStatus status = ModelFiles.Inspect("テスト用", Path.Combine(dir.Path, "none"), [("one.bin", 10)]);

        Assert.Equal(ModelFileState.Missing, status.Files.Single().State);
    }

    [Fact]
    public void A_file_with_the_defined_size_is_present_and_a_file_with_another_size_is_a_mismatch_not_missing()
    {
        using var dir = new TempDirectory();
        Write(dir.Path, "a/one.bin", 10);
        Write(dir.Path, "two.bin", 5);

        ModelSetStatus status = ModelFiles.Inspect("テスト用", dir.Path, [("a/one.bin", 10), ("two.bin", 20)]);

        Assert.Equal(ModelFileState.Present, status.Files[0].State);
        Assert.Equal((ModelFileState.SizeMismatch, (long?)5), (status.Files[1].State, status.Files[1].ActualSize));
        Assert.False(status.IsComplete);
        Assert.Empty(status.MissingFiles);   // サイズの違いは「無い」ではない(途中のファイル・別のファイル。使い始めるときの検証が失敗にする)
    }

    [Fact]
    public void A_complete_set_is_complete()
    {
        using var dir = new TempDirectory();
        Write(dir.Path, "a/one.bin", 10);
        Write(dir.Path, "two.bin", 20);

        Assert.True(ModelFiles.Inspect("テスト用", dir.Path, [("a/one.bin", 10), ("two.bin", 20)]).IsComplete);
    }

    [Fact]
    public void The_product_catalogs_list_the_files_the_core_verifies()
    {
        using var dir = new TempDirectory();

        ModelSetStatus embedding = ModelFiles.InspectEmbedding(dir.Path);
        ModelSetStatus vlm = ModelFiles.InspectVlm(dir.Path);

        Assert.Equal(
            ModelCatalog.Current.Files.Select(f => (f.RelativePath, f.Size)).ToArray(),
            embedding.Files.Select(f => (f.RelativePath, f.ExpectedSize)).ToArray());
        Assert.Equal(
            [(VlmCatalog.Current.Gguf.RelativePath, VlmCatalog.Current.Gguf.Size), (VlmCatalog.Current.Mmproj.RelativePath, VlmCatalog.Current.Mmproj.Size)],
            vlm.Files.Select(f => (f.RelativePath, f.ExpectedSize)).ToArray());
        Assert.Equal(OhanamiCore.ModelLabels.FeatureVector, embedding.DisplayName);
        Assert.Equal(OhanamiCore.ModelLabels.Vlm, vlm.DisplayName);
        Assert.Equal(VlmCatalog.HfRepository, vlm.SourceRepository);
        Assert.Equal(ModelCatalog.RepositoryUrl(VlmCatalog.HfRepository, VlmCatalog.HfRevision), vlm.SourceUrl);
        Assert.Equal(ModelCatalog.HfRepository, embedding.SourceRepository);
        Assert.Equal(ModelCatalog.HfRevision, embedding.SourceRevision);
        Assert.Equal(ModelCatalog.RepositoryUrl(ModelCatalog.HfRepository, ModelCatalog.HfRevision), embedding.SourceUrl);
        Assert.Equal($"https://huggingface.co/{ModelCatalog.HfRepository}/tree/{ModelCatalog.HfRevision}", embedding.SourceUrl);   // 入手先の画面は、コミットのファイル一覧
        Assert.Equal(4, embedding.Files.Count);
        Assert.Equal(2, vlm.Files.Count);
        Assert.False(embedding.IsComplete);
        Assert.False(vlm.IsComplete);
    }
}
