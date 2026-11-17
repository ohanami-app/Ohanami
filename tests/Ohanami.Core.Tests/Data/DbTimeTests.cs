using System;
using Ohanami.Core.Data;
using Xunit;

namespace Ohanami.Core.Tests.Data;

/// <summary>DB の時刻の書式(UTC、ミリ秒、末尾 Z)。すべての時刻の列がこの書式を使う。</summary>
public sealed class DbTimeTests
{
    [Fact]
    public void Format_is_utc_iso8601_with_milliseconds_and_z()
    {
        Assert.Equal("2026-11-05T12:00:00.000Z", DbTime.Format(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("2026-11-05T03:04:05.678Z", DbTime.Format(new DateTimeOffset(2026, 11, 5, 12, 4, 5, 678, TimeSpan.FromHours(9)))); // 他のオフセットは UTC に直す
    }

    [Fact]
    public void Formatted_times_sort_as_strings_in_time_order()
    {
        var a = DbTime.Format(new DateTimeOffset(2026, 1, 2, 3, 4, 5, 999, TimeSpan.Zero));
        var b = DbTime.Format(new DateTimeOffset(2026, 1, 2, 3, 4, 6, 0, TimeSpan.Zero));
        Assert.True(string.CompareOrdinal(a, b) < 0);
    }

    [Fact]
    public void Parse_round_trips_and_rejects_other_formats()
    {
        var t = new DateTimeOffset(2026, 11, 5, 12, 34, 56, 789, TimeSpan.Zero);
        Assert.Equal(t, DbTime.Parse(DbTime.Format(t)));
        Assert.Throws<FormatException>(() => DbTime.Parse("2026-11-05T12:00:00.0000000+00:00"));
        Assert.Throws<FormatException>(() => DbTime.Parse("2026-11-05 12:00:00"));
        Assert.Throws<FormatException>(() => DbTime.Parse(""));
    }
}
