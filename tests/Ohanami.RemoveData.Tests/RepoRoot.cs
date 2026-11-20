using System;
using System.IO;

namespace Ohanami.RemoveData.Tests;

/// <summary>リポジトリのルート(Ohanami.sln のあるフォルダ)。テストの実行ファイルのフォルダから親へたどって探す。</summary>
internal static class RepoRoot
{
    public static string Path { get; } = Find();

    public static string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    private static string Find()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Ohanami.sln"))) return dir.FullName;
        throw new InvalidOperationException("リポジトリのルート(Ohanami.sln)が見つからない: " + AppContext.BaseDirectory);
    }
}
