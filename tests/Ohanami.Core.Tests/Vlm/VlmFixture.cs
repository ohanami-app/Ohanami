using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Captions;
using Ohanami.Core.FullText;
using Ohanami.Core.Indexing;
using Ohanami.Core.Models;
using Ohanami.Core.Power;
using Ohanami.Core.Tests.Data;
using Ohanami.Core.Tests.Indexing;
using Ohanami.Core.Tests.Scanning;
using Ohanami.Core.Vlm;
using Xunit;

namespace Ohanami.Core.Tests.Vlm;

/// <summary>
/// VlmRunner のテスト一式: 走査用のフィクスチャ(DB・フォルダ・スキャナ)に、偽の llama-server(別プロセス)・小さなモデルのファイル・VlmRunner を載せる。
/// 偽サーバの動きは <c>FAKE_LLAMA_COMPLETIONS</c> などの環境変数(<see cref="FakeLlama.Env"/>)で決める。
/// </summary>
internal sealed class VlmFixture : IDisposable
{
    public ScanFixture Scan { get; } = new();
    public TempDirectory Work { get; } = new();
    public TestLog Log { get; } = new();
    public VlmSpec Spec { get; }
    public string ModelDirectory { get; }
    public VlmRunnerSettings Settings { get; }
    public ChildProcessGuard Guard { get; }
    public VlmVerifier Verifier { get; } = new();
    public FtsIndex Fts { get; }
    public CaptionStore Captions { get; }
    public VlmRunner Runner { get; }
    public PowerEvents Power { get; } = new();

    public VlmFixture(string completions, TimeSpan? requestTimeout = null, IReadOnlyDictionary<string, string>? extraEnv = null, bool breakModel = false, string? exePath = null)
    {
        ModelDirectory = Work.File("model");
        Directory.CreateDirectory(ModelDirectory);
        byte[] gguf = new byte[2000]; new Random(1).NextBytes(gguf);
        byte[] mmproj = new byte[500]; new Random(2).NextBytes(mmproj);
        File.WriteAllBytes(Path.Combine(ModelDirectory, "m.gguf"), gguf);
        File.WriteAllBytes(Path.Combine(ModelDirectory, "mm.gguf"), mmproj);
        Spec = new VlmSpec(
            "test-vlm", new VlmFile("m.gguf", Sha(gguf), gguf.Length), new VlmFile("mm.gguf", Sha(mmproj), mmproj.Length),
            promptSha256: new string('a', 64), schemaSha256: new string('b', 64), temperature: "0", chatTemplateKwargs: "",
            imageMinTokens: null, imageMaxTokens: 3584, hfRepository: "x/y", hfRevision: new string('c', 40));
        if (breakModel) File.WriteAllBytes(Path.Combine(ModelDirectory, "m.gguf"), new byte[2000]); // ハッシュが違うファイル

        var env = new Dictionary<string, string> { ["FAKE_LLAMA_COMPLETIONS"] = completions, ["FAKE_LLAMA_MARKER"] = Work.File("marker.txt") };
        if (extraEnv is not null) foreach (KeyValuePair<string, string> kv in extraEnv) env[kv.Key] = kv.Value;
        string exe = exePath ?? FakeLlama.ExePath;
        Settings = new VlmRunnerSettings(
            exe, ModelDirectory, Work.File("logs"),
            File.Exists(exe) ? Sha(File.ReadAllBytes(exe)) : new string('0', 64), 12110, "cd959bae9",
            HealthTimeout: TimeSpan.FromSeconds(30), RequestTimeout: requestTimeout ?? TimeSpan.FromSeconds(30), ServerEnvironment: env, VersionTimeout: TimeSpan.FromSeconds(30));
        Guard = new ChildProcessGuard(Work.File("llama-server.pid"), Log);
        Fts = new FtsIndex(Scan.Db, Log);
        Captions = new CaptionStore(Scan.Db, Fts);
        Runner = new VlmRunner(Scan.Db, Spec, "Test VLM", Settings, Guard, Verifier, Captions, Scan.Time, Log, Power);
    }

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public RunRecorder NewRecorder(Action<EngineStatus>? onEngine = null, Action<string>? onSkipped = null) =>
        new(Scan.Db, JobKind.Vlm, "test-1.0", Scan.Time, onEngine: onEngine, onSkipped: onSkipped);

    /// <summary>登録したフォルダに、中身が全部違う画像を置いて走査する。mtime を、新しいものほど大きくする(i が大きいほど新しい)。ハッシュ(= 内容の SHA-256)を、i の順に返す。</summary>
    public async Task<List<string>> AddImagesAsync(int count, string prefix = "img")
    {
        var hashes = new List<string>();
        DateTime baseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = ScanFixture.FakePng($"{prefix}-{i}");
            Scan.Write($"{prefix}{i}.png", bytes, baseTime.AddMinutes(i));
            hashes.Add(Sha(bytes));
        }
        if (Scan.Folders.List().Count == 0) await Scan.RegisterRootAsync();
        await Scan.ScanAsync();
        return hashes;
    }

    public string Text(string sql)
    {
        using SqliteConnection read = Scan.Db.CreateReadConnection();
        return TestDb.Text(read, sql);
    }

    public long Count(string sql) => Scan.Long(sql);

    public long StageCount(string stage, string status) =>
        Count($"SELECT count(*) FROM stage_status WHERE stage = '{stage}' AND status = '{status}'");

    public void Dispose()
    {
        Runner.ReleaseAsync().GetAwaiter().GetResult();
        Power.Dispose();
        Guard.Dispose();
        Scan.Dispose();
        Work.Dispose();
    }
}
