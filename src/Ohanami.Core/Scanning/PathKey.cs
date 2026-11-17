using System;
using System.IO;
using System.Text;

namespace Ohanami.Core.Scanning;

/// <summary>
/// パスの比較キー。SQLite の <c>COLLATE NOCASE</c>(ASCII の大文字小文字だけを同一視する)と同じ規則でそろえる
/// (ASCII 以外の大文字小文字の違いは別のパスとして扱う)。DB の一意制約と、メモリ上の突き合わせを一致させるため。
/// </summary>
internal static class PathKey
{
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        StringBuilder? sb = null;
        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i];
            if (c is >= 'A' and <= 'Z')
            {
                sb ??= new StringBuilder(path, 0, i, path.Length);
                sb.Append((char)(c + ('a' - 'A')));
            }
            else
            {
                sb?.Append(c);
            }
        }
        return sb is null ? path : sb.ToString();
    }

    /// <summary>末尾の区切りを 1 つにそろえたキー(「配下か」の判定用。C:\Shots と C:\Shots2 を取り違えない)。</summary>
    public static string DirectoryKey(string directory)
    {
        string key = Normalize(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return key + Path.DirectorySeparatorChar;
    }

    /// <summary>pathKey(<see cref="Normalize"/> 済み)が directoryKey(<see cref="DirectoryKey"/> 済み)の配下にあるか。ディレクトリ自身も含める。</summary>
    public static bool IsUnder(string pathKey, string directoryKey) =>
        pathKey.StartsWith(directoryKey, StringComparison.Ordinal)
        || string.Equals(pathKey + Path.DirectorySeparatorChar, directoryKey, StringComparison.Ordinal);
}
