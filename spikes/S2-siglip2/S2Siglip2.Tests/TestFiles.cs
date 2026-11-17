using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace S2Siglip2.Tests;

/// <summary>
/// テストが使うファイルの場所と、models/ のファイルの検証。
/// モデルのファイルは models/ に置き(Git の管理外)、MANIFEST.tsv の sha256 と突き合わせて本物かを確かめる。
/// ファイルが無い・ハッシュが違う場合は、skip ではなく失敗にする。
/// </summary>
internal static class TestFiles
{
    /// <summary>
    /// models/ の下のモデルのディレクトリ名。既定は Immich の ONNX。自前変換の ONNX(tools/convert-siglip2/)で試すときは、
    /// 環境変数 S2_MODEL_REPO=ViT-SO400M-16-SigLIP2-384__ohanami を指定する
    /// (正解の埋め込みは、自前の ONNX から作り直したものが Immich 用とバイト同一だったので、既定の reference/data のままでよい)。
    /// </summary>
    public static string ModelRepo => Environment.GetEnvironmentVariable("S2_MODEL_REPO") is { Length: > 0 } v ? v : "ViT-SO400M-16-SigLIP2-384__webli";

    /// <summary>
    /// 正解の埋め込み(image-embeddings.f32 / text-embeddings.f32 / text-embeddings.tsv)の場所。既定は reference/data(Immich の ONNX 由来)。
    /// 環境変数 S2_GOLDEN_EMBEDDINGS_DIR があれば、そこ(reference/gen_embeddings.py を S2_EMBEDDINGS_OUT つきで実行して別の場所に作ったもの)。
    /// </summary>
    public static string EmbeddingData(string file) =>
        Environment.GetEnvironmentVariable("S2_GOLDEN_EMBEDDINGS_DIR") is { Length: > 0 } d ? Path.Combine(d, file) : Reference("data", file);

    private static readonly Lazy<string> RootLazy = new(FindRoot);

    /// <summary>リポジトリのルート(docs/ と spikes/ があるディレクトリ)。</summary>
    public static string Root => RootLazy.Value;

    public static string Reference(params string[] parts) =>
        Path.Combine(new[] { Root, "spikes", "S2-siglip2", "reference" }.Concat2(parts));

    public static string ModelPath(string relative) =>
        Path.Combine(Root, "models", ModelRepo, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "docs")) && Directory.Exists(Path.Combine(dir, "spikes"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("リポジトリのルートが見つからない(docs/ と spikes/ があるディレクトリを探した)");
    }

    private static readonly Dictionary<string, string> Verified = new();

    /// <summary>models/<repo>/<relative> が MANIFEST.tsv の sha256 と一致することを確かめて、パスを返す。</summary>
    public static string VerifiedModelFile(string relative)
    {
        string path = ModelPath(relative);
        lock (Verified)
        {
            if (Verified.ContainsKey(path)) return path;
            if (!File.Exists(path)) throw new FileNotFoundException($"モデルのファイルが無い(spikes/tools/fetch_models.py で取得する): {path}");
            string manifest = Path.Combine(Root, "models", "MANIFEST.tsv");
            if (!File.Exists(manifest)) throw new FileNotFoundException($"MANIFEST.tsv が無い: {manifest}");
            string? expected = null;
            foreach (string line in File.ReadLines(manifest))
            {
                string[] c = line.Split('\t');
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

internal static class PathExtensions
{
    public static string[] Concat2(this string[] head, string[] tail)
    {
        var r = new string[head.Length + tail.Length];
        head.CopyTo(r, 0);
        tail.CopyTo(r, head.Length);
        return r;
    }
}
