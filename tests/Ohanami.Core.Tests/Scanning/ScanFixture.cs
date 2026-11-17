using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Ohanami.Core.Data;
using Ohanami.Core.Scanning;
using Ohanami.Core.Tests.Data;
using SkiaSharp;
using Xunit;

namespace Ohanami.Core.Tests.Scanning;

/// <summary>時刻を手で進められる TimeProvider(Microsoft.Extensions.TimeProvider.Testing は使わない)。</summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// スキャナ・フォルダ登録のテスト用の部品: 一時ディレクトリ(「ライブラリ」の根)、DB、FolderCatalog、Scanner、画像・ファイルの作成、DB の中身の読み取り。
/// </summary>
internal sealed class ScanFixture : IDisposable
{
    private readonly TempDirectory _dbDir = new();
    private readonly TempDirectory _libraryDir = new();

    public OhanamiDatabase Db { get; }
    public TestTimeProvider Time { get; } = new();
    public FolderCatalog Folders { get; }
    public Scanner Scanner { get; }

    /// <summary>登録するフォルダの根(中にファイルを作る)。</summary>
    public string Root => _libraryDir.Path;

    public ScanFixture()
    {
        Db = OhanamiDatabase.Open(_dbDir.File("test.db"));
        Folders = new FolderCatalog(Db, Time);
        Scanner = new Scanner(Db, Time);
    }

    public void Dispose()
    {
        Db.Dispose();
        _libraryDir.Dispose();
        _dbDir.Dispose();
    }

    // ---- 走査

    public async Task<ScanSummary> ScanAsync(IReadOnlyCollection<long>? ids = null, Action<ScanProgress>? onProgress = null) =>
        await Scanner.ScanAsync(ids, onProgress, TestContext.Current.CancellationToken);

    /// <summary>キャンセルのテスト用。呼び出し側が渡したトークンで走査する。</summary>
    public async Task<ScanSummary> ScanCancellableAsync(Action<ScanProgress>? onProgress, CancellationToken ct) =>
        await Scanner.ScanAsync(null, onProgress, ct);

    /// <summary>根を登録して、そのフォルダの id を返す。</summary>
    public async Task<long> RegisterRootAsync()
    {
        FolderInfo f = await Folders.AddAsync(Root, TestContext.Current.CancellationToken);
        return f.Id;
    }

    // ---- ファイルの作成

    public string PathOf(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>ファイルを書く。mtime を指定すると、その時刻にする(指定しなければ、いまの時刻)。</summary>
    public string Write(string relative, byte[] bytes, DateTime? mtimeUtc = null)
    {
        string path = PathOf(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        if (mtimeUtc is not null) File.SetLastWriteTimeUtc(path, mtimeUtc.Value);
        return path;
    }

    // ---- 画像のバイト列

    /// <summary>SkiaSharp で作った小さな画像。seed が違えば色が違い、バイト列も違う(seed は 0〜255)。</summary>
    public static byte[] RealImage(SKEncodedImageFormat format, int seed)
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(new SKColor((byte)(seed * 53), (byte)(seed * 29 + 7), (byte)(seed * 91 + 3)));
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>PNG の先頭 8 バイト + tag。形式の判定は通るが、画像としては壊れている(走査はデコードしない)。tag が違えばバイト列も違う。</summary>
    public static byte[] FakePng(string tag) => [.. PngSignature, .. System.Text.Encoding.UTF8.GetBytes(tag)];

    public static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // ---- DB の読み取り

    public long Long(string sql)
    {
        using SqliteConnection conn = Db.CreateReadConnection();
        return TestDb.Long(conn, sql);
    }

    public string? Text(string sql)
    {
        using SqliteConnection conn = Db.CreateReadConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        object? v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    public List<string> Column(string sql)
    {
        using SqliteConnection conn = Db.CreateReadConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var list = new List<string>();
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.IsDBNull(0) ? "(null)" : Convert.ToString(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        return list;
    }

    // ---- アクセス拒否の再現(icacls。現在のユーザーに対する Deny を足す / 外す)

    /// <summary>path の読み取り(ファイルなら中身、ディレクトリなら一覧)を、現在のユーザーに対して拒否する。Dispose で元に戻す。</summary>
    public static IDisposable DenyRead(string path)
    {
        string user = Environment.UserDomainName + "\\" + Environment.UserName;
        RunIcacls($"\"{path}\" /deny \"{user}\":(RD)");
        return new Restorer(() => RunIcacls($"\"{path}\" /remove:d \"{user}\""));
    }

    /// <summary>ディレクトリのジャンクション(管理者権限が要らないリンク)を作る。link は存在しないこと。</summary>
    public static void CreateJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"mklink /J が失敗した(終了コード {p.ExitCode}): {output}");
    }

    private sealed class Restorer(Action restore) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            restore();
        }
    }

    private static void RunIcacls(string arguments)
    {
        var psi = new ProcessStartInfo("icacls.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"icacls {arguments} が失敗した(終了コード {p.ExitCode}): {output}");
    }
}
