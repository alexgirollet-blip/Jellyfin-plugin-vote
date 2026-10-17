using System;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>
/// Converts the admin's wall-clock deadline (e.g. Europe/London) to UTC and back.
/// Clock-change rules: a time that does not exist (spring-forward gap, e.g. 01:30 on the last Sunday
/// of March in London) is rejected; a time that occurs twice (autumn fall-back) uses the FIRST
/// occurrence (summer time), so the vote never closes later than the admin expects.
/// </summary>
public static class TimeZoneHelper
{
    public static TimeZoneInfo Find(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }

    /// <summary>Converts a local wall-clock time to UTC.</summary>
    /// <returns>UTC time, or null with an error message if the time does not exist.</returns>
    public static (DateTime? Utc, string? Error) LocalToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            return (null, "That time does not exist because the clocks go forward then. Pick a time at least an hour later.");
        }

        if (zone.IsAmbiguousTime(unspecified))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(unspecified);
            var largest = offsets[0] > offsets[1] ? offsets[0] : offsets[1]; // summer offset = first occurrence
            return (DateTime.SpecifyKind(unspecified - largest, DateTimeKind.Utc), null);
        }

        return (TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), null);
    }

    public static DateTime UtcToLocal(DateTime utc, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
}
