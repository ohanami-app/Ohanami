using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Ohanami.Core.Models;
using Ohanami.Core.Tests.Data;
using Ohanami.Core.Vlm;
using Xunit;

namespace Ohanami.Core.Tests.Vlm;

public sealed class VlmVerifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static VlmSpec SpecFor(string gguf, byte[] ggufBytes, string mmproj, byte[] mmprojBytes) => new(
        "test-vlm",
        new VlmFile(gguf, Sha(ggufBytes), ggufBytes.Length),
        new VlmFile(mmproj, Sha(mmprojBytes), mmprojBytes.Length),
        promptSha256: new string('a', 64), schemaSha256: new string('b', 64), temperature: "0", chatTemplateKwargs: "",
        imageMinTokens: null, imageMaxTokens: 3584, hfRepository: "x/y", hfRevision: new string('c', 40));

    private static (TempDirectory Dir, VlmSpec Spec, byte[] Gguf, byte[] Mmproj) Model()
    {
        var dir = new TempDirectory();
        byte[] gguf = new byte[3000]; new Random(1).NextBytes(gguf);
        byte[] mmproj = new byte[1000]; new Random(2).NextBytes(mmproj);
        File.WriteAllBytes(dir.File("m.gguf"), gguf);
        File.WriteAllBytes(dir.File("mm.gguf"), mmproj);
        return (dir, SpecFor("m.gguf", gguf, "mm.gguf", mmproj), gguf, mmproj);
    }

    // ---------------------------------------------------------------- GGUF・mmproj

    [Fact]
    public async Task Correct_model_files_pass()
    {
        (TempDirectory dir, VlmSpec spec, _, _) = Model();
        using (dir) await new VlmVerifier().VerifyModelAsync(dir.Path, spec, Ct);
    }

    [Fact]
    public async Task A_wrong_hash_a_wrong_size_and_a_missing_file_are_model_mismatches_naming_the_file()
    {
        (TempDirectory dir, VlmSpec spec, byte[] gguf, _) = Model();
        using (dir)
        {
            // ハッシュ違い(サイズは同じ): 1 バイト変える
            byte[] bad = (byte[])gguf.Clone(); bad[10] ^= 0xFF;
            File.WriteAllBytes(dir.File("m.gguf"), bad);
            ModelMismatchException hashEx = await Assert.ThrowsAsync<ModelMismatchException>(() => new VlmVerifier().VerifyModelAsync(dir.Path, spec, Ct));
            Assert.Equal("m.gguf", hashEx.Subject);
            Assert.Contains("sha256 が違う", hashEx.Message, StringComparison.Ordinal);

            // サイズ違い
            File.WriteAllBytes(dir.File("m.gguf"), gguf[..2999]);
            ModelMismatchException sizeEx = await Assert.ThrowsAsync<ModelMismatchException>(() => new VlmVerifier().VerifyModelAsync(dir.Path, spec, Ct));
            Assert.Contains("サイズが違う", sizeEx.Message, StringComparison.Ordinal);

            // ファイル無し
            File.Delete(dir.File("mm.gguf"));
            File.WriteAllBytes(dir.File("m.gguf"), gguf);
            ModelMismatchException missing = await Assert.ThrowsAsync<ModelMismatchException>(() => new VlmVerifier().VerifyModelAsync(dir.Path, spec, Ct));
            Assert.Equal("mm.gguf", missing.Subject);
            Assert.Contains("ファイルが無い", missing.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_file_verified_once_is_not_read_again_until_its_size_or_modified_time_changes()
    {
        (TempDirectory dir, VlmSpec spec, byte[] gguf, _) = Model();
        using (dir)
        {
            var verifier = new VlmVerifier();
            await verifier.VerifyModelAsync(dir.Path, spec, Ct);

            // 内容を同じサイズで壊し、更新日時だけ元に戻す: 同じ(パス・サイズ・更新日時)なので、このプロセスの中では再検証しない(決めきれない点 3 の (a))
            DateTime when = File.GetLastWriteTimeUtc(dir.File("m.gguf"));
            byte[] bad = (byte[])gguf.Clone(); bad[10] ^= 0xFF;
            File.WriteAllBytes(dir.File("m.gguf"), bad);
            File.SetLastWriteTimeUtc(dir.File("m.gguf"), when);
            await verifier.VerifyModelAsync(dir.Path, spec, Ct);   // 例外にならない(これが契約。ディスクの改変の検出は、プロセスの最初の 1 回)

            // 更新日時が変われば、再検証して見つける
            File.SetLastWriteTimeUtc(dir.File("m.gguf"), when.AddSeconds(5));
            await Assert.ThrowsAsync<ModelMismatchException>(() => verifier.VerifyModelAsync(dir.Path, spec, Ct));

            // 新しい VlmVerifier(= 新しいプロセス)は、必ず読む
            File.SetLastWriteTimeUtc(dir.File("m.gguf"), when);
            await Assert.ThrowsAsync<ModelMismatchException>(() => new VlmVerifier().VerifyModelAsync(dir.Path, spec, Ct));
        }
    }

    [Fact]
    public async Task A_failed_verification_is_not_remembered()
    {
        (TempDirectory dir, VlmSpec spec, byte[] gguf, _) = Model();
        using (dir)
        {
            var verifier = new VlmVerifier();
            byte[] bad = (byte[])gguf.Clone(); bad[0] ^= 1;
            File.WriteAllBytes(dir.File("m.gguf"), bad);
            await Assert.ThrowsAsync<ModelMismatchException>(() => verifier.VerifyModelAsync(dir.Path, spec, Ct));
            await Assert.ThrowsAsync<ModelMismatchException>(() => verifier.VerifyModelAsync(dir.Path, spec, Ct)); // 2 回目も落ちる

            File.WriteAllBytes(dir.File("m.gguf"), gguf);
            await verifier.VerifyModelAsync(dir.Path, spec, Ct);
        }
    }

    // ---------------------------------------------------------------- llama-server.exe

    [Fact]
    public async Task The_server_exe_passes_when_the_hash_and_the_version_match()
    {
        string sha = Sha(await File.ReadAllBytesAsync(FakeLlama.ExePath, Ct));

        LlamaServerVersion v = await new VlmVerifier().VerifyLlamaServerAsync(FakeLlama.ExePath, sha, 12110, "cd959bae9", Ct);

        Assert.Equal(12110, v.Build);
        Assert.Equal("cd959bae9", v.Commit);
        Assert.Contains("version: 0.9.0-dev (build 12110, commit cd959bae9)", v.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_different_exe_hash_or_build_or_commit_or_a_missing_exe_is_a_mismatch()
    {
        string sha = Sha(await File.ReadAllBytesAsync(FakeLlama.ExePath, Ct));
        var verifier = new VlmVerifier();

        ModelMismatchException hash = await Assert.ThrowsAsync<ModelMismatchException>(() => verifier.VerifyLlamaServerAsync(FakeLlama.ExePath, new string('0', 64), 12110, "cd959bae9", Ct));
        Assert.Contains("sha256 が違う", hash.Message, StringComparison.Ordinal);
        Assert.Equal(new string('0', 64), hash.Expected);

        ModelMismatchException build = await Assert.ThrowsAsync<ModelMismatchException>(() => verifier.VerifyLlamaServerAsync(FakeLlama.ExePath, sha, 12111, "cd959bae9", Ct));
        Assert.Contains("ビルドが違う", build.Message, StringComparison.Ordinal);
        Assert.Equal("build 12110, commit cd959bae9", build.Actual);

        await Assert.ThrowsAsync<ModelMismatchException>(() => verifier.VerifyLlamaServerAsync(FakeLlama.ExePath, sha, 12110, "deadbeef0", Ct));

        using var dir = new TempDirectory();
        ModelMismatchException none = await Assert.ThrowsAsync<ModelMismatchException>(() => verifier.VerifyLlamaServerAsync(dir.File("nope.exe"), sha, 12110, "cd959bae9", Ct));
        Assert.Contains("llama-server.exe が無い", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_version_is_read_from_the_output_whichever_stream_it_came_from()
    {
        LlamaServerVersion v = VlmVerifier.ParseVersion("[stdout]\n\n[stderr]\nversion: 0.9.0-dev (build 12110, commit cd959bae9)\nbuilt with Clang 20.1.8 for Windows x86_64\n");

        Assert.Equal((12110, "cd959bae9"), (v.Build, v.Commit));
        Assert.Throws<ModelMismatchException>(() => VlmVerifier.ParseVersion("version: unknown"));
        Assert.Throws<ModelMismatchException>(() => VlmVerifier.ParseVersion(""));
    }

    [Fact]
    public async Task Version_capture_fails_on_a_nonzero_exit_and_on_timeout()
    {
        ModelMismatchException bad = await Assert.ThrowsAsync<ModelMismatchException>(() =>
            VlmVerifier.CaptureVersionTextAsync(FakeLlama.ExePath, Ct, TimeSpan.FromSeconds(30), FakeLlama.Env(("FAKE_LLAMA_VERSION_EXIT", "7"))));
        Assert.Contains("終了コード 7", bad.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            VlmVerifier.CaptureVersionTextAsync(FakeLlama.ExePath, Ct, TimeSpan.FromMilliseconds(500), FakeLlama.Env(("FAKE_LLAMA_VERSION_HANG_MS", "20000"))));
    }
}
