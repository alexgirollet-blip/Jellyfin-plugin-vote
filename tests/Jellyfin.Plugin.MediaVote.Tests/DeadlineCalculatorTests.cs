using System;
using Jellyfin.Plugin.MediaVote.Core;
using Xunit;

namespace Jellyfin.Plugin.MediaVote.Tests;

public class DeadlineCalculatorTests
{
    private static readonly TimeZoneInfo London = TimeZoneHelper.Find("Europe/London");

    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static DeadlineResult Rel(DateTime now, int amount, string unit, string? time = "20:00")
        => DeadlineCalculator.Compute(new DeadlineSpec { Kind = DeadlineKind.Relative, Amount = amount, Unit = unit, Time = time }, now, London);

    private static DeadlineResult Preset(DateTime now, string preset)
        => DeadlineCalculator.Compute(new DeadlineSpec { Kind = DeadlineKind.Preset, Preset = preset }, now, London);

    [Fact]
    public void In7DaysAt2000_AcrossTheOctober2026ClockChange_Is2000GmtNot1900()
    {
        // Tue 20 Oct 2026 12:00 UTC (13:00 BST). UK clocks go back on Sun 25 Oct 2026 at 02:00 BST.
        var r = Rel(Utc(2026, 10, 20, 12), 7, "days");
        Assert.Null(r.Error);
        Assert.Equal(Utc(2026, 10, 27, 20), r.Utc); // 20:00 GMT = 20:00 UTC (adding 168 hours would give 19:00 GMT)
        Assert.Equal("2026-10-27T20:00", r.LocalInput);
        Assert.Equal("Closes Tuesday 27 Oct 2026 at 20:00 GMT (in 7 days)", r.Description);
    }

    [Fact]
    public void BeforeTheChange_StillBst()
    {
        var r = Rel(Utc(2026, 10, 20, 12), 4, "days");
        Assert.Equal(Utc(2026, 10, 24, 19), r.Utc); // Sat 24 Oct 20:00 BST
        Assert.Equal("Closes Saturday 24 Oct 2026 at 20:00 BST (in 4 days)", r.Description);
    }

    [Fact]
    public void OnTheChangeDay_Evening_IsGmt()
    {
        var r = Rel(Utc(2026, 10, 20, 12), 5, "days");
        Assert.Equal(Utc(2026, 10, 25, 20), r.Utc);
    }

    [Fact]
    public void Hours_AreRealElapsedHours_EvenOverTheChange()
    {
        // 23:30 BST on Sat 24 Oct + 6 hours = 05:30 UTC (04:30 GMT): six real hours, not six wall-clock hours.
        var r = Rel(Utc(2026, 10, 24, 22, 30), 6, "hours", null);
        Assert.Equal(Utc(2026, 10, 25, 4, 30), r.Utc);
    }

    [Fact]
    public void AmbiguousLocalTime_UsesTheFirstOccurrence()
    {
        var r = DeadlineCalculator.Compute(new DeadlineSpec { Kind = DeadlineKind.Exact, Local = "2026-10-25T01:30" }, Utc(2026, 10, 20, 12), London);
        Assert.Equal(Utc(2026, 10, 25, 0, 30), r.Utc); // 01:30 BST
    }

    [Fact]
    public void SpringForwardGap_IsRejected()
    {
        var r = DeadlineCalculator.Compute(new DeadlineSpec { Kind = DeadlineKind.Relative, Amount = 1, Unit = "days", Time = "01:30" }, Utc(2026, 3, 28, 12), London);
        Assert.Null(r.Utc);
        Assert.Contains("does not exist", r.Error);
    }

    [Fact]
    public void Weeks_AreSevenCalendarDays()
        => Assert.Equal(Utc(2026, 11, 3, 20), Rel(Utc(2026, 10, 20, 12), 2, "weeks").Utc);

    [Fact]
    public void MonthAndYearEnds()
    {
        Assert.Equal(Utc(2027, 1, 1, 20), Rel(Utc(2026, 12, 31, 12), 1, "days").Utc); // into the new year
        Assert.Equal(Utc(2026, 3, 1, 20), Rel(Utc(2026, 2, 28, 12), 1, "days").Utc); // 2026 is not a leap year
        Assert.Equal(Utc(2028, 2, 29, 20), Rel(Utc(2028, 2, 28, 12), 1, "days").Utc); // 2028 is
    }

    [Fact]
    public void EndOfMonth_IsTheLastDayAt2000Local_RollingOverWhenPast()
    {
        Assert.Equal(Utc(2026, 10, 31, 20), Preset(Utc(2026, 10, 20, 12), DeadlinePresets.EndOfMonth).Utc); // GMT by then
        Assert.Equal(Utc(2027, 2, 28, 20), Preset(Utc(2027, 2, 3, 9), DeadlinePresets.EndOfMonth).Utc);
        Assert.Equal(Utc(2026, 7, 31, 19), Preset(Utc(2026, 7, 1, 9), DeadlinePresets.EndOfMonth).Utc); // BST
        Assert.Equal(Utc(2027, 1, 31, 20), Preset(Utc(2026, 12, 31, 21), DeadlinePresets.EndOfMonth).Utc); // after 20:00 on 31 Dec
    }

    [Fact]
    public void Presets()
    {
        var now = Utc(2026, 6, 10, 9, 10); // 10:10 BST
        Assert.Equal(Utc(2026, 6, 10, 10, 10), Preset(now, DeadlinePresets.InOneHour).Utc);
        Assert.Equal(Utc(2026, 6, 10, 19), Preset(now, DeadlinePresets.Tonight).Utc);
        Assert.Equal(Utc(2026, 6, 11, 19), Preset(now, DeadlinePresets.Tomorrow).Utc);
        Assert.Equal(Utc(2026, 6, 13, 19), Preset(now, DeadlinePresets.InThreeDays).Utc);
        Assert.Equal(Utc(2026, 6, 17, 19), Preset(now, DeadlinePresets.InOneWeek).Utc);
        Assert.Equal(Utc(2026, 6, 24, 19), Preset(now, DeadlinePresets.InTwoWeeks).Utc);
    }

    [Fact]
    public void TonightAfter2000_IsRejectedWithAHint()
    {
        var r = Preset(Utc(2026, 6, 10, 19, 30), DeadlinePresets.Tonight); // 20:30 BST
        Assert.Null(r.Utc);
        Assert.Contains("Tomorrow", r.Error);
    }

    [Fact]
    public void PastExactTime_IsRejected()
    {
        var r = DeadlineCalculator.Compute(new DeadlineSpec { Kind = DeadlineKind.Exact, Local = "2026-06-01T10:00" }, Utc(2026, 6, 10, 9), London);
        Assert.Contains("past", r.Error);
    }

    [Theory]
    [InlineData(0, "days")]
    [InlineData(400, "days")]
    [InlineData(3, "months")]
    public void BadInput_IsRejected(int amount, string unit) => Assert.NotNull(Rel(Utc(2026, 6, 10, 9), amount, unit).Error);

    [Fact]
    public void BadTime_IsRejected() => Assert.Contains("HH:MM", Rel(Utc(2026, 6, 10, 9), 2, "days", "25:00").Error);

    [Fact]
    public void Default_IsIn7DaysAt2000()
    {
        var r = DeadlineCalculator.Compute(null, Utc(2026, 6, 10, 9), London);
        Assert.Equal(Utc(2026, 6, 17, 19), r.Utc);
    }

    [Theory]
    [InlineData(30, "30 minutes")]
    [InlineData(90, "2 hours")]
    [InlineData(60 * 30, "1 day 6 hours")]
    [InlineData(60 * 48, "2 days")]
    [InlineData(60 * 24 * 7, "7 days")]
    public void Until_ReadsNaturally(int minutes, string expected) => Assert.Equal(expected, DeadlineCalculator.Until(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void OtherZones_ShowTheOffset()
        => Assert.EndsWith("UTC+02:00", DeadlineCalculator.Describe(Utc(2026, 6, 10, 9), TimeZoneHelper.Find("Europe/Paris")), StringComparison.Ordinal);
}
