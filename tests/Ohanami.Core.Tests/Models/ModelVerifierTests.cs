using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Models;
using Ohanami.Core.Tests.Data;
using Xunit;

namespace Ohanami.Core.Tests;

/// <summary>
/// モデルのファイルの検証。小さな合成ファイルで動作を確かめる(速い)。実物の大きなファイルは Category=Model のテスト
/// (テキスト側だけで約 2.87GB を読むので、通常の実行には入れない)。
/// </summary>
public sealed class ModelVerifierTests
{
    private static readonly string[] Paths = ["visual/model.onnx", "textual/model.onnx", "textual/model.onnx_data", "textual/tokenizer.json"];

    /// <summary>ランダムな中身の小さな 4 ファイルをディレクトリに作り、それに合う EmbeddingModelSpec を返す。</summary>
    private static EmbeddingModelSpec CreateFiles(string dir)
    {
        var files = new List<ModelFile>();
        var rng = new Random(12345);
        foreach ((ModelRole role, int i) in ModelRoles.All.Select((r, i) => (r, i)))
        {
            byte[] bytes = new byte[(3 << 20) + i * 1000]; // 3 MiB 余り(バッファの境界をまたぐ)
            rng.NextBytes(bytes);
            string full = Path.Combine(dir, Paths[i].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            files.Add(new ModelFile(role, Paths[i], Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length));
        }
        return new EmbeddingModelSpec("fam", files, "pre-v1", "clean-v1", 1152, "l2");
    }

    private static string FullPath(string dir, string relative) => Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Untouched_files_are_accepted_on_both_sides()
    {
        using var tmp = new TempDirectory();
        EmbeddingModelSpec spec = CreateFiles(tmp.Path);
        IReadOnlyList<VerifiedModelFile> text = await ModelVerifier.VerifyTextSideAsync(tmp.Path, spec, TestContext.Current.CancellationToken);
        IReadOnlyList<VerifiedModelFile> image = await ModelVerifier.VerifyImageSideAsync(tmp.Path, spec, TestContext.Current.CancellationToken);
        Assert.Equal(["textual/model.onnx", "textual/model.onnx_data", "textual/tokenizer.json"], text.Select(v => v.File.RelativePath).ToArray());
        Assert.Equal(["visual/model.onnx"], image.Select(v => v.File.RelativePath).ToArray());
    }

    [Fact]
    public async Task The_two_sides_are_verified_independently()
    {
        using var tmp = new TempDirectory();
        EmbeddingModelSpec spec = CreateFiles(tmp.Path);

        // 画像側のファイルを消しても、テキスト側は通る(起動時にテキスト側だけで Ready になれる)。画像側は失敗する
        File.Delete(FullPath(tmp.Path, "visual/model.onnx"));
        await ModelVerifier.VerifyTextSideAsync(tmp.Path, spec, TestContext.Current.CancellationToken);
        var missing = await Assert.ThrowsAsync<ModelMismatchException>(() => ModelVerifier.VerifyImageSideAsync(tmp.Path, spec, TestContext.Current.CancellationToken));
        Assert.Contains("visual/model.onnx", missing.Message, StringComparison.Ordinal);

        // 逆に、テキスト側を壊しても画像側は通る
        using var tmp2 = new TempDirectory();
        EmbeddingModelSpec spec2 = CreateFiles(tmp2.Path);
        FlipByte(FullPath(tmp2.Path, "textual/model.onnx_data"), 1000);
        await ModelVerifier.VerifyImageSideAsync(tmp2.Path, spec2, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ModelMismatchException>(() => ModelVerifier.VerifyTextSideAsync(tmp2.Path, spec2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_missing_file_is_reported_with_its_name_the_place_and_the_expected_values()
    {
        using var tmp = new TempDirectory();
        EmbeddingModelSpec spec = CreateFiles(tmp.Path);
        ModelFile target = spec.File(ModelRole.Tokenizer);
        File.Delete(FullPath(tmp.Path, target.RelativePath));
        var e = await Assert.ThrowsAsync<ModelMismatchException>(() => ModelVerifier.VerifyTextSideAsync(tmp.Path, spec, TestContext.Current.CancellationToken));
        Assert.Contains(target.RelativePath, e.Message, StringComparison.Ordinal);
        Assert.Contains(target.Sha256, e.Message, StringComparison.Ordinal);
        Assert.Equal(target.RelativePath, e.Subject);
        Assert.Equal(target.Sha256, e.Expected);
    }

    [Fact]
    public async Task A_missing_directory_is_reported_as_missing_files()
    {
        EmbeddingModelSpec spec = ModelCatalog.Current;
        string nowhere = Path.Combine(Path.GetTempPath(), "ohanami-no-such-dir-" + Guid.NewGuid().ToString("N"));
        var e = await Assert.ThrowsAsync<ModelMismatchException>(() => ModelVerifier.VerifyImageSideAsync(nowhere, spec, TestContext.Current.CancellationToken));
        Assert.Contains("visual/model.onnx", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_of_the_wrong_size_is_reported_with_both_sizes_before_hashing()
    {
        using var tmp = new TempDirectory();
        EmbeddingModelSpec spec = CreateFiles(tmp.Path);
        ModelFile target = spec.File(ModelRole.TextualOnnx);
        using (var fs = new FileStream(FullPath(tmp.Path, target.RelativePath), FileMode.Append)) fs.WriteByte(0);
        var e = await Assert.ThrowsAsync<ModelMismatchException>(() => ModelVerifier.VerifyTextSideAsync(tmp.Path, spec, TestContext.Current.CancellationToken));
        Assert.Contains(target.RelativePath, e.Message, StringComparison.Ordinal);
        Assert.Contains(target.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), e.Message, StringComparison.Ordinal);
        Assert.Contains((target.Size + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), e.Message, StringComparison.Ordinal);
        Assert.Contains("サイズ", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5_000)]
    [InlineData(-1)] // 最後のバイト
    public async Task Changing_a_single_byte_is_detected_and_reports_the_expected_and_actual_hashes(int offset)
    {
        using var tmp = new TempDirectory();
        EmbeddingModelSpec spec = CreateFiles(tmp.Path);
        ModelFile target = spec.File(ModelRole.TextualExternalData);
        string path = FullPath(tmp.Path, target.RelativePath);
        FlipByte(path, offset < 0 ? target.Size - 1 : offset);
        string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        Assert.NotEqual(target.Sha256, actual);

        var e = await Assert.ThrowsAsync<ModelMismatchException>(() => ModelVerifier.VerifyTextSideAsync(tmp.Path, spec, TestContext.Current.CancellationToken));
        Assert.Contains(target.RelativePath, e.Message, StringComparison.Ordinal);
        Assert.Contains(target.Sha256, e.Message, StringComparison.Ordinal);
        Assert.Contains(actual, e.Message, StringComparison.Ordinal);
        Assert.Equal(target.Sha256, e.Expected);
        Assert.Equal(actual, e.Actual);
    }

    [Fact]
    public async Task Cancellation_stops_the_verification()
    {
        using var tmp = new TempDirectory();
        EmbeddingModelSpec spec = CreateFiles(tmp.Path);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelVerifier.VerifyTextSideAsync(tmp.Path, spec, cts.Token));
    }

    /// <summary>
    /// 実物の tokenizer.json(34MB)の一時コピーの 1 バイトを変えると、カタログの値で検出される
    /// (実物のサイズ・ハッシュのまま、改ざんの検出が働くことの確認。大きな ONNX は Category=Model のテスト)。
    /// </summary>
    [Fact]
    public async Task A_tampered_copy_of_the_real_tokenizer_json_is_detected_with_the_catalog_values()
    {
        string source = TestFiles.VerifiedModelFile("textual/tokenizer.json"); // 本物であること(MANIFEST の sha256)を先に確かめる
        using var tmp = new TempDirectory();
        string copy = FullPath(tmp.Path, "textual/tokenizer.json");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(source, copy);
        ModelFile entry = ModelCatalog.Current.File(ModelRole.Tokenizer);

        // 無傷のコピーは通る
        await ModelVerifier.VerifyAsync(tmp.Path, [entry], TestContext.Current.CancellationToken);

        FlipByte(copy, 12345);
        var e = await Assert.ThrowsAsync<ModelMismatchException>(() => ModelVerifier.VerifyAsync(tmp.Path, [entry], TestContext.Current.CancellationToken));
        Assert.Contains("textual/tokenizer.json", e.Message, StringComparison.Ordinal);
        Assert.Contains(entry.Sha256, e.Message, StringComparison.Ordinal);
        Assert.Equal(entry.Sha256, e.Expected);
        Assert.NotNull(e.Actual);
        Assert.NotEqual(entry.Sha256, e.Actual);
    }

    // ---------------------------------------------------------------- 実物(重い。通常の実行から除ける)

    [Fact]
    [Trait("Category", "Model")]
    public async Task The_real_model_files_pass_verification_on_both_sides()
    {
        string dir = Path.Combine(TestFiles.ModelsDir, ModelCatalog.DirectoryName);
        IReadOnlyList<VerifiedModelFile> text = await ModelVerifier.VerifyTextSideAsync(dir, ModelCatalog.Current, TestContext.Current.CancellationToken);
        IReadOnlyList<VerifiedModelFile> image = await ModelVerifier.VerifyImageSideAsync(dir, ModelCatalog.Current, TestContext.Current.CancellationToken);
        Assert.Equal(3, text.Count);
        Assert.Single(image);
    }

    private static void FlipByte(string path, long offset)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        fs.Position = offset;
        int b = fs.ReadByte();
        fs.Position = offset;
        fs.WriteByte((byte)(b ^ 0x01));
    }
}
