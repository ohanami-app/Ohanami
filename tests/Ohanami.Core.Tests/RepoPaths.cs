using System;
using System.IO;

namespace Ohanami.Core.Tests;

/// <summary>リポジトリのルート(Ohanami.sln があるディレクトリ)を、テストの実行場所から上へたどって探す。見つからなければ例外。</summary>
internal static class RepoPaths
{
    private static readonly Lazy<string> RootValue = new(FindRoot);

    public static string Root => RootValue.Value;

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ohanami.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"Ohanami.sln が見つからない(探し始めた場所: {AppContext.BaseDirectory})");
    }
}
