using System;
using System.Globalization;

namespace Ohanami.Core.Data;

/// <summary>
/// DB に入れる時刻の書式(UTC の ISO 8601、ミリ秒、末尾 Z。例: 2030-01-02T03:04:05.678Z)。
/// この書式は文字列として並べて時刻順になる。すべての時刻の列(added_at、last_scan_at、created_at、missing_since、started_at、updated_at など)は
/// これで書く(別の書式を作らない)。
/// </summary>
internal static class DbTime
{
    private const string Pattern = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Format(DateTimeOffset t) => t.UtcDateTime.ToString(Pattern, CultureInfo.InvariantCulture);

    /// <summary>DB から読んだ時刻。書式が違えば例外(黙って別の解釈をしない)。</summary>
    public static DateTimeOffset Parse(string text)
    {
        if (!DateTimeOffset.TryParseExact(text, Pattern, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset t))
        {
            throw new FormatException($"DB の時刻「{text}」が書式 {Pattern} と違う。");
        }
        return t;
    }
}
