using System;
using LocalEnvManager.Models;
using Xunit;

namespace LocalEnvManager.Tests;

public sealed class RelativeTimeTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 14, 30, 0);

    [Fact]
    public void NullFormatsAsDash() => Assert.Equal("—", RelativeTime.Format(null, Now));

    [Fact]
    public void UnderOneMinuteIsJustNow() => Assert.Equal("刚刚", RelativeTime.Format(Now.AddSeconds(-30), Now));

    [Fact]
    public void MinutesAgo() => Assert.Equal("3 分钟前", RelativeTime.Format(Now.AddMinutes(-3), Now));

    [Fact]
    public void SameDayShowsTime() => Assert.Equal("09:15", RelativeTime.Format(Now.Date.AddHours(9).AddMinutes(15), Now));

    [Fact]
    public void YesterdayShowsLabel() => Assert.Equal("昨天 22:05", RelativeTime.Format(Now.Date.AddDays(-1).AddHours(22).AddMinutes(5), Now));

    [Fact]
    public void ThisYearShowsMonthDay() => Assert.Equal("11-08 10:00", RelativeTime.Format(new DateTime(2026, 11, 8, 10, 0, 0), Now));

    [Fact]
    public void OlderYearShowsDate() => Assert.Equal("2025-12-31", RelativeTime.Format(new DateTime(2025, 12, 31, 10, 0, 0), Now));

    [Fact]
    public void FutureClampsToJustNow() => Assert.Equal("刚刚", RelativeTime.Format(Now.AddMinutes(5), Now));

    [Fact]
    public void FullTimestampOrNull()
    {
        Assert.Equal("从未运行", RelativeTime.FormatFull(null));
        Assert.Equal("2026-11-13 14:30:00", RelativeTime.FormatFull(Now));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(45, "45s")]
    [InlineData(83, "1m23s")]
    [InlineData(3600, "1h00m")]
    [InlineData(3725, "1h02m")]
    public void ElapsedFormats(int seconds, string expected)
        => Assert.Equal(expected, RelativeTime.FormatElapsed(TimeSpan.FromSeconds(seconds)));
}