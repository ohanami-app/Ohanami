using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Ohanami.Core.Tests;

/// <summary>
/// テストが使うファイルの場所と、models/ のファイルの検証。
/// 正解データは tests/Ohanami.Core.Tests/TestData/(コミット済み。Data/ は DB のテストのコード)。モデルのファイルは models/ に置き(Git の管理外)、MANIFEST.tsv の sha256 と突き合わせて本物かを確かめる。
/// ファイルが無い・ハッシュが違う場合は、skip ではなく失敗にする(「あるはずのものが無い」を黙って通さない)。
/// </summary>
internal static class TestFiles
{
    /// <summary>
    /// 画像などのファイルを書いて、更新時刻を 1 時間前にする。走査は「更新から 2 秒以内のファイル」を書き込みの途中かもしれないとして今回は登録しない
    /// (ADR 0010)ので、本物の時計の Core で「書いてすぐ走査する」テストは、古い更新時刻で書く。
    /// </summary>
    public static void WriteAged(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
    }

    /// <summary>models/ の下の、製品が使うモデルのディレクトリ名。MANIFEST.tsv の repo 列は「…/この名前」で終わる。</summary>
    public const string ModelRepo = "ViT-SO400M-16-SigLIP2-384__ohanami";

    /// <summary>
    /// models/ の場所。環境変数 OHANAMI_MODELS_DIR があればそこ、無ければリポジトリのルートの models/。
    /// git の worktree のように models/ のモデルのファイルが無い場所でテストを動かすときに、元のリポジトリの models/ を指す。
    /// </summary>
    public static string ModelsDir =>
        Environment.GetEnvironmentVariable("OHANAMI_MODELS_DIR") is { Length: > 0 } d ? d : Path.Combine(RepoPaths.Root, "models");

    /// <summary>正解データ(tests/Ohanami.Core.Tests/TestData/ の下)。</summary>
    public static string TestData(params string[] parts)
    {
        var all = new string[parts.Length + 3];
        all[0] = RepoPaths.Root;
        all[1] = "tests";
        all[2] = Path.Combine("Ohanami.Core.Tests", "TestData");
        parts.CopyTo(all, 3);
        return Path.Combine(all);
    }

    public static string ModelPath(string relative) => Path.Combine(ModelsDir, ModelRepo, relative.Replace('/', Path.DirectorySeparatorChar));

    private static readonly Dictionary<string, string> Verified = new();

    /// <summary>models/&lt;repo&gt;/&lt;relative&gt; が MANIFEST.tsv の sha256 と一致することを確かめて、パスを返す。</summary>
    public static string VerifiedModelFile(string relative)
    {
        string path = ModelPath(relative);
        lock (Verified)
        {
            if (Verified.ContainsKey(path)) return path;
            if (!File.Exists(path))
                throw new FileNotFoundException($"モデルのファイルが無い(通常のテストに要る tokenizer.json は pwsh tools/runtime/Get-Runtime.ps1 で取る。実物のモデルは tests/README.md。場所は環境変数 OHANAMI_MODELS_DIR で変えられる): {path}");
            string manifest = Path.Combine(ModelsDir, "MANIFEST.tsv");
            if (!File.Exists(manifest)) throw new FileNotFoundException($"MANIFEST.tsv が無い: {manifest}");
            string? expected = null;
            foreach (string line in File.ReadLines(manifest))
            {
                string[] c = line.Split('	');
                if (c.Length >= 6 && c[0].EndsWith("/" + ModelRepo, StringComparison.Ordinal) && c[2] == relative)
                {
                    if (c[5] != "OK") throw new InvalidDataException($"MANIFEST.tsv の {relative} の sha256_match が OK でない: {c[5]}");
                    expected = c[4];
                }
            }
            if (expected is null) throw new InvalidDataException($"MANIFEST.tsv に {relative} の行が無い");
            string actual;
            using (var fs = File.OpenRead(path)) actual = Convert.ToHexStringLower(SHA256.HashData(fs));
            if (actual != expected) throw new InvalidDataException($"{relative} の sha256 が MANIFEST.tsv と違う(期待 {expected}、実際 {actual})");
            Verified[path] = actual;
            return path;
        }
    }
}
