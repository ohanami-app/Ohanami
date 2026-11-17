using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Models;

namespace Ohanami.Core.Vlm;

/// <summary><c>llama-server --version</c> の結果。<see cref="Text"/> は出力の全文(標準出力と標準エラー。<c>runs.runtime_json</c> に入れる)。</summary>
internal sealed record LlamaServerVersion(string Text, int Build, string Commit);

/// <summary>
/// VLM に要るファイルの検証(ADR 0020)。
/// <list type="bullet">
/// <item>GGUF と mmproj: 存在・サイズ・SHA-256(ストリーム。<see cref="ModelVerifier"/> と同じ読み方。約 6GB で約 8 秒)。
/// <b>このインスタンスの中では、(パス, サイズ, 更新日時, 期待する sha256)が同じなら再検証しない</b>(再開のたびに 8 秒を払わない。ディスクの改変の検出は、プロセスの最初の 1 回)。</item>
/// <item>llama-server.exe: SHA-256 が定数と一致すること + <c>--version</c> の <c>build N, commit X</c> が定数と一致すること。同梱の DLL(ggml-cuda.dll など)は個別にはハッシュしない(配布物のファイルの表が見張る。ADR 0042)。</item>
/// </list>
/// 不一致・ファイル無し・読めない・<c>--version</c> が想定の形でないものは、すべて <see cref="ModelMismatchException"/>(メッセージに期待と実際)。黙って通さない。
/// </summary>
internal sealed class VlmVerifier
{
    private static readonly Regex VersionPattern = new(@"build\s+(\d+),\s*commit\s+([0-9a-fA-F]+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly HashSet<(string Path, long Size, long MtimeTicks, string Sha256)> _verified = [];

    /// <summary>GGUF と mmproj を検証する(検証済みのものは、ファイルが変わっていなければ飛ばす)。</summary>
    public async Task VerifyModelAsync(string modelDirectory, VlmSpec spec, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ArgumentNullException.ThrowIfNull(spec);
        foreach (VlmFile file in new[] { spec.Gguf, spec.Mmproj })
        {
            ct.ThrowIfCancellationRequested();
            await VerifyFileOnceAsync(Path.Combine(modelDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)), file.RelativePath, file.Sha256, file.Size, ct).ConfigureAwait(false);
        }
    }

    private async Task VerifyFileOnceAsync(string path, string name, string sha256, long size, CancellationToken ct)
    {
        (string, long, long, string)? key = null;
        var info = new FileInfo(path);
        if (info.Exists) key = (info.FullName, info.Length, info.LastWriteTimeUtc.Ticks, sha256);
        if (key is { } k)
        {
            lock (_gate)
            {
                if (_verified.Contains(k)) return;
            }
        }

        await ModelVerifier.VerifyPathAsync(path, name, sha256, size, ct).ConfigureAwait(false);

        // 検証中に変わっていないことを確かめてから、検証済みにする(読んでいる間に差し替えられたものを、検証済みにしない)
        var after = new FileInfo(path);
        if (after.Exists && key is { } before && after.Length == before.Item2 && after.LastWriteTimeUtc.Ticks == before.Item3)
        {
            lock (_gate) _verified.Add(before);
        }
    }

    /// <summary>
    /// llama-server.exe を検証する: SHA-256 が <paramref name="expectedSha256"/> と一致し、<c>--version</c> が
    /// <paramref name="expectedBuild"/> / <paramref name="expectedCommit"/> と一致すること。検証済み(同じパス・サイズ・更新日時)なら、<c>--version</c> の結果も覚えていて、再実行しない。
    /// </summary>
    public async Task<LlamaServerVersion> VerifyLlamaServerAsync(
        string exePath, string expectedSha256, int expectedBuild, string expectedCommit, CancellationToken ct = default, TimeSpan? versionTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCommit);
        string full = Path.GetFullPath(exePath);
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            throw new ModelMismatchException($"llama-server.exe が無い: {full}。期待 sha256 {expectedSha256}。", "llama-server.exe", expectedSha256, "(ファイル無し)");
        }

        var key = (info.FullName, info.Length, info.LastWriteTimeUtc.Ticks, expectedSha256 + "|" + expectedBuild.ToString(CultureInfo.InvariantCulture) + "|" + expectedCommit);
        lock (_gate)
        {
            if (_llamaVersion is { } cached && cached.Key == key) return cached.Version;
        }

        string actual = await Sha256OfFileAsync(full, ct).ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
        {
            throw new ModelMismatchException($"llama-server.exe の sha256 が違う: 期待 {expectedSha256}、実際 {actual}(場所: {full})。", "llama-server.exe", expectedSha256, actual);
        }

        string text = await CaptureVersionTextAsync(full, ct, versionTimeout ?? TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        LlamaServerVersion version = ParseVersion(text);
        if (version.Build != expectedBuild || !string.Equals(version.Commit, expectedCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelMismatchException(
                $"llama-server のビルドが違う: 期待 build {expectedBuild.ToString(CultureInfo.InvariantCulture)}, commit {expectedCommit}、実際 build {version.Build.ToString(CultureInfo.InvariantCulture)}, commit {version.Commit}(--version の出力: {OneLine(text)})。",
                "llama-server.exe", $"build {expectedBuild}, commit {expectedCommit}", $"build {version.Build}, commit {version.Commit}");
        }
        lock (_gate) _llamaVersion = (key, version);
        return version;
    }

    private ((string, long, long, string) Key, LlamaServerVersion Version)? _llamaVersion;

    /// <summary><c>--version</c> の出力(標準出力と標準エラーのどちらに出るかはビルドによるので、両方)から、ビルド番号と commit を取り出す。見つからなければ <see cref="ModelMismatchException"/>。</summary>
    internal static LlamaServerVersion ParseVersion(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Match m = VersionPattern.Match(text);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int build))
        {
            throw new ModelMismatchException($"llama-server --version の出力から build と commit を読めない: {OneLine(text)}", "llama-server.exe");
        }
        return new LlamaServerVersion(text.TrimEnd(), build, m.Groups[2].Value);
    }

    /// <summary><c>llama-server --version</c> を実行して、出力の全文(標準出力 + 標準エラー)を返す。タイムアウト・終了コードが 0 でないときは例外。</summary>
    public static async Task<string> CaptureVersionTextAsync(string exePath, CancellationToken ct, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--version");
        if (environment is not null)
        {
            foreach (KeyValuePair<string, string> kv in environment) psi.Environment[kv.Key] = kv.Value;
        }

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"{exePath} --version を起動できなかった");
        Task<string> stdout = p.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* もう終わっている */ }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{exePath} --version が {timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} 秒で終わらない");
        }
        string text = (await stdout.ConfigureAwait(false)).TrimEnd() + "\n" + (await stderr.ConfigureAwait(false)).TrimEnd();
        if (p.ExitCode != 0)
        {
            throw new ModelMismatchException($"llama-server --version が終了コード {p.ExitCode.ToString(CultureInfo.InvariantCulture)} を返した: {OneLine(text)}", "llama-server.exe");
        }
        return text.Trim();
    }

    internal static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        });
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 20];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) hash.AppendData(buffer, 0, read);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string OneLine(string text) => text.Replace("\r\n", " | ", StringComparison.Ordinal).Replace("\n", " | ", StringComparison.Ordinal).Trim();
}
