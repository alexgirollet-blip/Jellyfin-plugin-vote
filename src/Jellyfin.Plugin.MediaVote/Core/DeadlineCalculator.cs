using System;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>How a deadline was chosen in the picker.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeadlineKind
{
    /// <summary>One of the quick chips (see <see cref="DeadlinePresets"/>).</summary>
    Preset,

    /// <summary>"In [N] [hours|days|weeks] at [HH:MM]".</summary>
    Relative,

    /// <summary>An exact local date and time ("yyyy-MM-ddTHH:mm").</summary>
    Exact
}

/// <summary>Quick-chip ids understood by <see cref="DeadlineCalculator"/>.</summary>
public static class DeadlinePresets
{
    public const string InOneHour = "in1h";
    public const string Tonight = "tonight";
    public const string Tomorrow = "tomorrow";
    public const string InThreeDays = "in3d";
    public const string InOneWeek = "in1w";
    public const string InTwoWeeks = "in2w";
    public const string EndOfMonth = "endofmonth";
}

/// <summary>The deadline as chosen in the picker. The server turns it into UTC; the browser never does time zone maths.</summary>
public sealed class DeadlineSpec
{
    public DeadlineKind Kind { get; set; } = DeadlineKind.Relative;

    /// <summary>For <see cref="DeadlineKind.Preset"/>.</summary>
    public string? Preset { get; set; }

    /// <summary>For <see cref="DeadlineKind.Relative"/>: how many units.</summary>
    public int Amount { get; set; } = 7;

    /// <summary>For <see cref="DeadlineKind.Relative"/>: "hours", "days" or "weeks".</summary>
    public string Unit { get; set; } = "days";

    /// <summary>Wall-clock time "HH:mm" (24 h) for days/weeks; ignored for hours.</summary>
    public string? Time { get; set; } = "20:00";

    /// <summary>For <see cref="DeadlineKind.Exact"/>: "yyyy-MM-ddTHH:mm" in the configured time zone.</summary>
    public string? Local { get; set; }

    /// <summary>The default: in 7 days at 20:00.</summary>
    public static DeadlineSpec Default => new() { Kind = DeadlineKind.Relative, Amount = 7, Unit = "days", Time = "20:00" };
}

/// <summary>Result of <see cref="DeadlineCalculator.Compute"/>.</summary>
/// <param name="Utc">The deadline in UTC, or null when <paramref name="Error"/> is set.</param>
/// <param name="LocalInput">"yyyy-MM-ddTHH:mm" in the configured zone (for the exact-time field).</param>
/// <param name="Description">Plain words, for example "Closes Friday 24 Oct 2026 at 20:00 BST (in 7 days)".</param>
/// <param name="Error">Why the choice is not usable (in the past, a time that does not exist, bad input).</param>
public sealed record DeadlineResult(DateTime? Utc, string? LocalInput, string? Description, string? Error);

/// <summary>
/// Turns a picker choice into a UTC deadline. Days and weeks are calendar days in the configured zone, then the
/// wall-clock time is applied there, so "in 7 days at 20:00" is 20:00 local even across a clock change (UK clocks go
/// back on Sunday 25 October 2026: 20:00 is 19:00 UTC before and 20:00 UTC after). Hours are real elapsed hours.
/// A time that does not exist (spring-forward gap) is rejected; a time that happens twice uses the first occurrence.
/// </summary>
public static class DeadlineCalculator
{
    private static readonly CultureInfo EnGb = CultureInfo.GetCultureInfo("en-GB");

    public static DeadlineResult Compute(DeadlineSpec? spec, DateTime nowUtc, TimeZoneInfo zone)
    {
        spec ??= DeadlineSpec.Default;
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var nowLocal = TimeZoneHelper.UtcToLocal(nowUtc, zone);
        DateTime? utc;
        string? error;
        switch (spec.Kind)
        {
            case DeadlineKind.Exact:
                if (string.IsNullOrWhiteSpace(spec.Local)
                    || !DateTime.TryParseExact(spec.Local.Trim(), new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
                {
                    return Fail("Enter the deadline as a date and time.");
                }

                (utc, error) = TimeZoneHelper.LocalToUtc(exact, zone);
                break;

            case DeadlineKind.Preset:
                (utc, error) = Preset(spec.Preset, nowUtc, nowLocal, zone);
                break;

            default:
                (utc, error) = Relative(spec, nowUtc, nowLocal, zone);
                break;
        }

        if (error is not null || utc is null)
        {
            return Fail(error ?? "Choose a deadline.");
        }

        if (utc.Value <= nowUtc)
        {
            return Fail("That time is in the past. Choose a later deadline.");
        }

        var local = TimeZoneHelper.UtcToLocal(utc.Value, zone);
        return new DeadlineResult(
            utc,
            local.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            "Closes " + Describe(utc.Value, zone) + " (in " + Until(utc.Value - nowUtc) + ")",
            null);
    }

    /// <summary>"Friday 24 Oct 2026 at 20:00 BST".</summary>
    public static string Describe(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeZoneHelper.UtcToLocal(utc, zone);
        return local.ToString("dddd d MMM yyyy 'at' HH:mm", EnGb) + " " + Abbreviation(utc, zone);
    }

    /// <summary>BST/GMT for Europe/London, UTC for UTC, otherwise the offset ("UTC+02:00").</summary>
    public static string Abbreviation(DateTime utc, TimeZoneInfo zone)
    {
        if (zone.Id is "Europe/London" or "GB" or "GMT Standard Time")
        {
            return zone.IsDaylightSavingTime(utc) ? "BST" : "GMT";
        }

        if (zone.Id is "UTC" or "Etc/UTC")
        {
            return "UTC";
        }

        var offset = zone.GetUtcOffset(utc);
        return "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>"7 days", "3 hours", "45 minutes", "1 day 4 hours".</summary>
    public static string Until(TimeSpan span)
    {
        if (span.TotalMinutes < 60)
        {
            int m = Math.Max(1, (int)Math.Round(span.TotalMinutes));
            return Plural(m, "minute");
        }

        if (span.TotalHours < 24)
        {
            int h = (int)Math.Round(span.TotalHours);
            return Plural(h, "hour");
        }

        int days = (int)Math.Round(span.TotalDays);
        if (span.TotalDays < 3)
        {
            int wholeDays = (int)span.TotalDays;
            int hours = (int)Math.Round(span.TotalHours - (wholeDays * 24));
            if (hours == 24)
            {
                wholeDays++;
                hours = 0;
            }

            return hours == 0 ? Plural(wholeDays, "day") : Plural(wholeDays, "day") + " " + Plural(hours, "hour");
        }

        return Plural(days, "day");
    }

    private static (DateTime? Utc, string? Error) Preset(string? preset, DateTime nowUtc, DateTime nowLocal, TimeZoneInfo zone)
    {
        var at20 = new TimeSpan(20, 0, 0);
        return preset switch
        {
            DeadlinePresets.InOneHour => (RoundUpToMinute(nowUtc.AddHours(1)), null),
            DeadlinePresets.Tonight => nowLocal.TimeOfDay >= at20
                ? (null, "It is already past 20:00 today. Choose \"Tomorrow at 20:00\" instead.")
                : TimeZoneHelper.LocalToUtc(nowLocal.Date + at20, zone),
            DeadlinePresets.Tomorrow => TimeZoneHelper.LocalToUtc(nowLocal.Date.AddDays(1) + at20, zone),
            DeadlinePresets.InThreeDays => TimeZoneHelper.LocalToUtc(nowLocal.Date.AddDays(3) + at20, zone),
            DeadlinePresets.InOneWeek => TimeZoneHelper.LocalToUtc(nowLocal.Date.AddDays(7) + at20, zone),
            DeadlinePresets.InTwoWeeks => TimeZoneHelper.LocalToUtc(nowLocal.Date.AddDays(14) + at20, zone),
            DeadlinePresets.EndOfMonth => EndOfMonth(nowLocal, zone),
            _ => (null, "Unknown quick choice.")
        };
    }

    /// <summary>The last day of this month at 20:00; on the last day after 20:00, the end of next month.</summary>
    private static (DateTime? Utc, string? Error) EndOfMonth(DateTime nowLocal, TimeZoneInfo zone)
    {
        var at20 = new TimeSpan(20, 0, 0);
        var last = new DateTime(nowLocal.Year, nowLocal.Month, DateTime.DaysInMonth(nowLocal.Year, nowLocal.Month));
        if (nowLocal.Date == last && nowLocal.TimeOfDay >= at20)
        {
            var next = last.AddDays(1);
            last = new DateTime(next.Year, next.Month, DateTime.DaysInMonth(next.Year, next.Month));
        }

        return TimeZoneHelper.LocalToUtc(last + at20, zone);
    }

    private static (DateTime? Utc, string? Error) Relative(DeadlineSpec spec, DateTime nowUtc, DateTime nowLocal, TimeZoneInfo zone)
    {
        string unit = (spec.Unit ?? "days").Trim().ToLowerInvariant();
        int max = unit switch { "hours" => 24 * 90, "weeks" => 52, _ => 365 };
        if (spec.Amount < 1 || spec.Amount > max)
        {
            return (null, string.Create(CultureInfo.InvariantCulture, $"Enter a number from 1 to {max}."));
        }

        if (unit == "hours")
        {
            return (RoundUpToMinute(nowUtc.AddHours(spec.Amount)), null);
        }

        if (unit is not ("days" or "weeks"))
        {
            return (null, "Choose hours, days or weeks.");
        }

        if (string.IsNullOrWhiteSpace(spec.Time) || !TimeSpan.TryParseExact(spec.Time.Trim(), new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out var time) || time >= TimeSpan.FromDays(1))
        {
            return (null, "Enter the time as HH:MM (24-hour clock).");
        }

        int days = unit == "weeks" ? spec.Amount * 7 : spec.Amount;
        return TimeZoneHelper.LocalToUtc(nowLocal.Date.AddDays(days) + time, zone);
    }

    private static DateTime RoundUpToMinute(DateTime utc)
    {
        var ticks = (utc.Ticks + TimeSpan.TicksPerMinute - 1) / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute;
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private static string Plural(int n, string word) => n.ToString(CultureInfo.InvariantCulture) + " " + word + (n == 1 ? string.Empty : "s");

    private static DeadlineResult Fail(string message) => new(null, null, null, message);
}
