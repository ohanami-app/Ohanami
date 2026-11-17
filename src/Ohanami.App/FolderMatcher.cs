using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ohanami.Core;

namespace Ohanami.App;

/// <summary>
/// 画像のパスから、それを含む登録済みのフォルダを引く(「元のファイルが見つからない」と分かったとき、そのフォルダだけを走査し直すため)。
/// パスの比較は大文字小文字を区別しない(Windows)。<c>C:\a</c> は <c>C:\ab\x.png</c> を含まない(区切りの境目で比べる)。
/// 登録のとき入れ子のフォルダは拒否されるので、含むフォルダは高々 1 つだが、念のため一致したものをすべて返す。
/// </summary>
internal static class FolderMatcher
{
    /// <summary>paths のどれかを含む登録済みのフォルダの id(重複なし)。どれにも含まれなければ空。</summary>
    public static IReadOnlyList<long> FolderIdsContaining(IEnumerable<FolderInfo> folders, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(paths);
        List<(long Id, string Root)> roots = folders.Select(f => (f.Id, WithSeparator(f.Path))).ToList();
        var ids = new List<long>();
        foreach (string path in paths)
        {
            string full = Normalize(path);
            foreach ((long id, string root) in roots)
            {
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !ids.Contains(id)) ids.Add(id);
            }
        }
        return ids;
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    private static string WithSeparator(string folder) => Normalize(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
}
