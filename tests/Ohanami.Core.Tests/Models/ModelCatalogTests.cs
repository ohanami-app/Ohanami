using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ohanami.Core.Models;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>ModelCatalog の値が models/MANIFEST.tsv(コミット済み)の自前変換のモデルの行と同じこと。ファイルの中身は読まない。</summary>
public sealed class ModelCatalogTests
{
    private sealed record ManifestRow(string Repo, string Revision, string FileName, long Size, string Sha256, string Match);

    private static List<ManifestRow> ReadManifest()
    {
        // 検査の対象は「コミットされた MANIFEST.tsv」。OHANAMI_MODELS_DIR の指す場所の MANIFEST ではなく、リポジトリのものを読む。
        string path = Path.Combine(RepoPaths.Root, "models", "MANIFEST.tsv");
        Assert.True(File.Exists(path), $"MANIFEST.tsv が無い: {path}");
        var rows = new List<ManifestRow>();
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            if (line.Length == 0) continue;
            string[] c = line.Split('\t');
            Assert.True(c.Length == 6, $"MANIFEST.tsv の列数が 6 でない: {line}");
            rows.Add(new ManifestRow(c[0], c[1], c[2], long.Parse(c[3], System.Globalization.CultureInfo.InvariantCulture), c[4], c[5]));
        }
        return rows;
    }

    [Fact]
    public void Catalog_files_match_the_manifest_rows_of_the_converted_model()
    {
        List<ManifestRow> rows = ReadManifest().Where(r => r.Repo.EndsWith("/" + ModelCatalog.DirectoryName, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(rows);
        Assert.Single(rows.Select(r => r.Repo).Distinct());
        Assert.Equal("ohanami-local/" + ModelCatalog.DirectoryName, rows[0].Repo);

        foreach (ModelFile file in ModelCatalog.Current.Files)
        {
            ManifestRow row = Assert.Single(rows, r => r.FileName == file.RelativePath);
            Assert.Equal("OK", row.Match);
            Assert.Equal(row.Size, file.Size);
            Assert.Equal(row.Sha256, file.Sha256);
        }
    }

    [Fact]
    public void Catalog_covers_exactly_the_four_roles_and_the_sides_are_split_as_designed()
    {
        Assert.Equal(4, ModelCatalog.Current.Files.Count);
        Assert.Equal(["visual/model.onnx"], ModelCatalog.Current.FilesOf(ModelSide.Image).Select(f => f.RelativePath).ToArray());
        Assert.Equal(["textual/model.onnx", "textual/model.onnx_data", "textual/tokenizer.json"],
            ModelCatalog.Current.FilesOf(ModelSide.Text).Select(f => f.RelativePath).ToArray());
        // テキスト側は約 2.87GB、画像側は約 1.71GB(MANIFEST.tsv の値)
        Assert.InRange(ModelCatalog.Current.FilesOf(ModelSide.Text).Sum(f => f.Size), 2_850_000_000L, 2_900_000_000L);
        Assert.InRange(ModelCatalog.Current.FilesOf(ModelSide.Image).Sum(f => f.Size), 1_700_000_000L, 1_720_000_000L);
    }

    [Fact]
    public void Catalog_name_and_family_are_stable_identifiers()
    {
        Assert.Equal("ViT-SO400M-16-SigLIP2-384__ohanami", ModelCatalog.DirectoryName);
        Assert.StartsWith(ModelCatalog.Family + ":", ModelCatalog.Current.ModelId, StringComparison.Ordinal);
    }
}
