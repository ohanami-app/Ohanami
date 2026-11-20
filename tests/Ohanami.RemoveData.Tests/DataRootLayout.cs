using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Ohanami.Core;

namespace Ohanami.RemoveData.Tests;

/// <summary>
/// アプリが根(データの場所)に書く物の名前を、すべてそろえた根(設計の「全部入りの根」)。
/// モデルのフォルダ・ファイルの名前は写さず、Core の定数と <c>ModelFiles.Inspect*</c> から作る。
/// 中身は、パスから作った短い固定のバイト列(ファイルごとに違うので、バイト単位の比較ができる)。
/// </summary>
internal sealed record DataRootLayout(
    string Root,
    IReadOnlyList<string> ModelFiles,
    IReadOnlyList<string> OtherFiles)
{
    public const string Hex32 = "0123456789abcdef0123456789abcdef";
    public const string Hex64 = Hex32 + Hex32;

    public string EmbeddingDirectory => Path.Combine(Root, "models", OhanamiCore.ModelDirectoryName);

    public string VlmDirectory => Path.Combine(Root, "models", VlmOptions.ModelDirectoryName);

    public string DatabasePath => Path.Combine(Root, "ohanami.db");

    public IEnumerable<string> AllFiles => ModelFiles.Concat(OtherFiles);

    public static DataRootLayout CreateFull(string root)
    {
        var models = new List<string>();
        var others = new List<string>();

        string embedding = Path.Combine(root, "models", OhanamiCore.ModelDirectoryName);
        foreach (ModelFileStatus f in Core.ModelFiles.InspectEmbedding(embedding).Files) AddModel(models, embedding, f.RelativePath);
        string vlm = Path.Combine(root, "models", VlmOptions.ModelDirectoryName);
        foreach (ModelFileStatus f in Core.ModelFiles.InspectVlm(vlm).Files) AddModel(models, vlm, f.RelativePath);

        string[] rootFiles =
        [
            "ohanami.db", "ohanami.db-wal", "ohanami.db-shm",
            "ohanami.db.bak-v3",
            "llama-server.pid", "llama-server.pid." + Hex32 + ".tmp",
        ];
        foreach (string name in rootFiles) others.Add(Path.Combine(root, name));
        others.Add(Path.Combine(root, "logs", "ohanami-20261120.log"));
        others.Add(Path.Combine(root, "logs", "llama-server-20261120-010203-004.log"));
        string thumbDir = Path.Combine(root, "thumbs", "v1", "ab");
        others.Add(Path.Combine(thumbDir, Hex64 + "_320x180.jpg"));
        others.Add(Path.Combine(thumbDir, Hex64 + "_160x90.jpg." + Hex32 + ".tmp"));

        foreach (string path in models.Concat(others)) WriteFile(path);
        return new DataRootLayout(root, models, others);
    }

    // モデルのファイルと、取りかけの .part の両方
    private static void AddModel(List<string> models, string modelDirectory, string relativePath)
    {
        string path = Path.Combine(modelDirectory, relativePath.Replace('/', '\\'));
        models.Add(path);
        models.Add(path + ".part");
    }

    public static void WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("test data for " + path));
    }

    /// <summary>根の下のファイルの、相対パス → SHA-256。リンク(再解析ポイント)の中は数えない。</summary>
    public static Dictionary<string, string> SnapshotFiles(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return result;
        foreach (string file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            result[Path.GetRelativePath(root, file)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        return result;
    }
}
