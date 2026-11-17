using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Ohanami.Core.Models;

/// <summary>検証できたファイル 1 つ分の結果(ログ用)。</summary>
internal sealed record VerifiedModelFile(ModelFile File, TimeSpan Elapsed);

/// <summary>
/// モデルのファイルの検証: 存在、サイズ、SHA-256(ストリームのまま読む。ファイル全体をメモリに載せない)。
/// ファイルが無い・サイズが違う・ハッシュが違うものは <see cref="ModelMismatchException"/>(メッセージにファイル名と期待値・実際の値)。
/// キャッシュ(サイズ + 更新日時で検証済みとみなす)は持たない。毎回全部を読む(読む速さは約 805MB/秒で、埋め込みの合計 4.58GB が約 5.7 秒。ADR 0020)。
/// 検証の単位は、テキスト側(起動時。Ready の条件)と画像側(裏で。エンコーダを作る時点で参照)に分かれている。
/// </summary>
internal static class ModelVerifier
{
    private const int BufferSize = 1 << 20; // 1 MiB

    /// <summary>テキスト側のファイル(textual/model.onnx、model.onnx_data、tokenizer.json)を検証する。</summary>
    public static Task<IReadOnlyList<VerifiedModelFile>> VerifyTextSideAsync(string modelDirectory, EmbeddingModelSpec spec, CancellationToken ct = default) =>
        VerifyAsync(modelDirectory, spec.FilesOf(ModelSide.Text), ct);

    /// <summary>画像側のファイル(visual/model.onnx)を検証する。</summary>
    public static Task<IReadOnlyList<VerifiedModelFile>> VerifyImageSideAsync(string modelDirectory, EmbeddingModelSpec spec, CancellationToken ct = default) =>
        VerifyAsync(modelDirectory, spec.FilesOf(ModelSide.Image), ct);

    /// <summary>
    /// ファイルが在ってサイズが想定どおりであることだけを確かめる(SHA-256 は読まない。stat だけなので一瞬)。
    /// エンコーダを作るたびに(Pause → Resume など)呼ぶ用。ハッシュの検証(<see cref="VerifyAsync"/>)は起動時に 1 度行う。
    /// 無い・サイズが違えば <see cref="ModelMismatchException"/>。
    /// </summary>
    public static void CheckPresence(string modelDirectory, IEnumerable<ModelFile> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ArgumentNullException.ThrowIfNull(files);
        foreach (ModelFile file in files)
        {
            string path = PathOf(modelDirectory, file);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new ModelMismatchException(
                    $"モデルのファイルが無い: {file.RelativePath}(探した場所: {path})。期待サイズ {file.Size.ToString(CultureInfo.InvariantCulture)} バイト、sha256 {file.Sha256}。",
                    file.RelativePath, file.Sha256, "(ファイル無し)");
            }
            if (info.Length != file.Size)
            {
                throw new ModelMismatchException(
                    $"モデルのファイルのサイズが違う: {file.RelativePath} 期待 {file.Size.ToString(CultureInfo.InvariantCulture)} バイト、実際 {info.Length.ToString(CultureInfo.InvariantCulture)} バイト(場所: {path})。",
                    file.RelativePath, file.Size.ToString(CultureInfo.InvariantCulture), info.Length.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>モデルのディレクトリとファイルの相対パス(区切りは /)から、実際のパスを作る。</summary>
    public static string PathOf(string modelDirectory, ModelFile file) =>
        Path.Combine(modelDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>指定したファイルを順に検証する。最初に見つかった問題で <see cref="ModelMismatchException"/>。</summary>
    public static async Task<IReadOnlyList<VerifiedModelFile>> VerifyAsync(string modelDirectory, IEnumerable<ModelFile> files, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ArgumentNullException.ThrowIfNull(files);
        var results = new List<VerifiedModelFile>();
        foreach (ModelFile file in files)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            await VerifyFileAsync(modelDirectory, file, ct).ConfigureAwait(false);
            results.Add(new VerifiedModelFile(file, sw.Elapsed));
        }
        return results;
    }

    private static Task VerifyFileAsync(string modelDirectory, ModelFile file, CancellationToken ct) =>
        VerifyPathAsync(PathOf(modelDirectory, file), file.RelativePath, file.Sha256, file.Size, ct);

    /// <summary>
    /// 1 つのファイルを検証する(存在・サイズ・SHA-256。ストリームで読む)。埋め込みモデルの <see cref="ModelFile"/> に限らず、VLM の GGUF・mmproj・llama-server.exe にも使う。
    /// <paramref name="name"/> は、メッセージと <see cref="ModelMismatchException.Subject"/> に出す名前。
    /// </summary>
    internal static async Task VerifyPathAsync(string path, string name, string sha256, long expectedSize, CancellationToken ct)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0, // 自前のバッファで読む
            });
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ModelMismatchException(
                $"モデルのファイルが無い: {name}(探した場所: {path})。期待サイズ {expectedSize.ToString(CultureInfo.InvariantCulture)} バイト、sha256 {sha256}。",
                name, sha256, "(ファイル無し)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ModelMismatchException($"モデルのファイルを開けない: {name}(場所: {path}): {e.Message}", e);
        }

        using (stream)
        {
            long size = stream.Length;
            if (size != expectedSize)
            {
                throw new ModelMismatchException(
                    $"モデルのファイルのサイズが違う: {name} 期待 {expectedSize.ToString(CultureInfo.InvariantCulture)} バイト、実際 {size.ToString(CultureInfo.InvariantCulture)} バイト(場所: {path})。",
                    name, expectedSize.ToString(CultureInfo.InvariantCulture), size.ToString(CultureInfo.InvariantCulture));
            }

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                long total = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    total += read;
                }
                if (total != expectedSize)
                {
                    // 読んでいる間にサイズが変わった
                    throw new ModelMismatchException(
                        $"モデルのファイルのサイズが読んでいる間に変わった: {name} 期待 {expectedSize.ToString(CultureInfo.InvariantCulture)} バイト、実際に読んだのは {total.ToString(CultureInfo.InvariantCulture)} バイト。",
                        name, expectedSize.ToString(CultureInfo.InvariantCulture), total.ToString(CultureInfo.InvariantCulture));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, sha256, StringComparison.Ordinal))
            {
                throw new ModelMismatchException(
                    $"モデルのファイルの sha256 が違う: {name} 期待 {sha256}、実際 {actual}(場所: {path})。",
                    name, sha256, actual);
            }
        }
    }
}
