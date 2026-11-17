using System;
using System.Text;

namespace Ohanami.Core.Tests;

/// <summary>
/// 識別子の検査(SourceIdentifierTests)のための、ソースからコメントを取り除く簡易処理。
/// 検査の対象は識別子と文字列リテラルで、コメントは対象にしない。
/// C#: // と /* */ を除く。文字列リテラル("..."、@"..."、'...')の中の // は除かない。補間文字列・raw 文字列は特別扱いしない(通常の文字列として読む)。
/// XML: コメント(&lt;!-- --&gt;)を除く。
/// </summary>
internal static class CommentStripper
{
    public static string StripCSharp(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sb = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new FormatException("/* が閉じていない");
                // 行数がずれないよう、改行は残す
                for (int k = i; k < end + 2; k++) if (source[k] == '\n') sb.Append('\n');
                i = end + 2;
                continue;
            }
            if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                i = CopyVerbatimString(source, i, sb);
                continue;
            }
            if (c == '"' || c == '\'')
            {
                i = CopyQuoted(source, i, sb, c);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    public static string StripXml(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sb = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            if (string.CompareOrdinal(source, i, "<!--", 0, 4) == 0)
            {
                int end = source.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (end < 0) throw new FormatException("<!-- が閉じていない");
                for (int k = i; k < end + 3; k++) if (source[k] == '\n') sb.Append('\n');
                i = end + 3;
                continue;
            }
            sb.Append(source[i]);
            i++;
        }
        return sb.ToString();
    }

    private static int CopyQuoted(string s, int start, StringBuilder sb, char quote)
    {
        sb.Append(s[start]);
        int i = start + 1;
        while (i < s.Length)
        {
            char c = s[i];
            sb.Append(c);
            i++;
            if (c == '\\' && i < s.Length) { sb.Append(s[i]); i++; continue; }
            if (c == quote) break;
            if (c == '\n') break; // 閉じていない(想定外だが、後続の読み取りを巻き込まない)
        }
        return i;
    }

    private static int CopyVerbatimString(string s, int start, StringBuilder sb)
    {
        sb.Append("@\"");
        int i = start + 2;
        while (i < s.Length)
        {
            char c = s[i];
            sb.Append(c);
            i++;
            if (c == '"')
            {
                if (i < s.Length && s[i] == '"') { sb.Append('"'); i++; continue; }
                break;
            }
        }
        return i;
    }
}
