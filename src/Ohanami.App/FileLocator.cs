using System;
using System.Collections.Generic;
using System.IO;

namespace Ohanami.App;

/// <summary>
/// 検索結果のパスは、DB の <c>missing_since</c> の印だけで決まっていて、ファイルが今も在るかは確かめていない。
/// 実在の確認は、ファイルを使うとき(ドラッグ・コピー・エクスプローラーで表示)にここで行う。同じ画像に複数のパスがあれば、最初に在るものを使う。
/// </summary>
internal static class FileLocator
{
    /// <summary>候補のうち、最初に実在するファイル。無ければ null。</summary>
    public static string? FirstExisting(IReadOnlyList<string> candidates) => FirstExisting(candidates, File.Exists);

    internal static string? FirstExisting(IReadOnlyList<string> candidates, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(exists);
        foreach (string path in candidates)
        {
            if (exists(path)) return path;
        }
        return null;
    }

    /// <summary>
    /// 画像ごと(候補のパスの並びごと)の最初に実在するパス(重複は 1 つにする)と、見つからなかった項目の数。
    /// 同じファイルを別の画像として 2 度渡さない(パスの比較は大文字小文字を区別しない: Windows)。
    /// </summary>
    public static (string[] Paths, int Missing) Resolve(IEnumerable<IReadOnlyList<string>> items) => Resolve(items, File.Exists);

    internal static (string[] Paths, int Missing) Resolve(IEnumerable<IReadOnlyList<string>> items, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(items);
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int missing = 0;
        foreach (IReadOnlyList<string> candidates in items)
        {
            string? path = FirstExisting(candidates, exists);
            if (path is null) missing++;
            else if (seen.Add(path)) found.Add(path);
        }
        return (found.ToArray(), missing);
    }
}
