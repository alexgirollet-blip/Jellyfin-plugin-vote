using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>How often a template prepares a new vote.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecurrenceKind
{
    None,
    Weekly,
    Monthly
}

/// <summary>Saved vote settings ("Monthly clean-up"), optionally recurring.</summary>
public sealed class VoteTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>Everything chosen in "New vote" except the deadline.</summary>
    public SessionSettings Settings { get; set; } = new();

    /// <summary>The deadline as a picker choice, re-evaluated when the vote is opened (for example "in 7 days at 20:00").</summary>
    public DeadlineSpec Deadline { get; set; } = DeadlineSpec.Default;

    public RecurrenceKind Recurrence { get; set; } = RecurrenceKind.None;

    /// <summary>Next time (local wall clock, "yyyy-MM-ddTHH:mm") a draft is prepared.</summary>
    public string? NextRunLocal { get; set; }

    /// <summary>Day of month the monthly schedule was set up on (kept so 31 → 30 → 31 does not drift).</summary>
    public int AnchorDay { get; set; }

    /// <summary>Set when a recurring draft is waiting for the admin: nothing opens until they review it.</summary>
    public DateTime? DraftPendingSinceUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Persistence for templates.</summary>
public interface ITemplateStore
{
    IReadOnlyList<VoteTemplate> GetAll();

    void Save(VoteTemplate template);

    bool Delete(string id);
}

/// <summary>Recurring schedule maths. Pure.</summary>
public static class TemplateSchedule
{
    /// <summary>The next run after <paramref name="currentLocal"/>: +7 days, or the anchor day next month (clamped to the month's length).</summary>
    public static DateTime Next(DateTime currentLocal, RecurrenceKind kind, int anchorDay)
    {
        if (kind == RecurrenceKind.Weekly)
        {
            return currentLocal.AddDays(7);
        }

        var firstOfNext = new DateTime(currentLocal.Year, currentLocal.Month, 1).AddMonths(1);
        int day = Math.Min(anchorDay <= 0 ? currentLocal.Day : anchorDay, DateTime.DaysInMonth(firstOfNext.Year, firstOfNext.Month));
        return firstOfNext.AddDays(day - 1) + currentLocal.TimeOfDay;
    }

    /// <summary>
    /// Marks every recurring template whose next run has passed as "draft pending" (if no draft is already pending) and
    /// moves its next run forward, skipping runs missed while the server was off. Returns the templates that just got a draft.
    /// </summary>
    public static List<VoteTemplate> DueDrafts(ITemplateStore store, DateTime nowUtc, TimeZoneInfo zone)
    {
        var ready = new List<VoteTemplate>();
        foreach (var t in store.GetAll().Where(t => t.Recurrence != RecurrenceKind.None && !string.IsNullOrWhiteSpace(t.NextRunLocal)))
        {
            if (!DateTime.TryParseExact(t.NextRunLocal, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var nextLocal))
            {
                continue;
            }

            var (dueUtc, _) = TimeZoneHelper.LocalToUtc(nextLocal, zone);
            if (dueUtc is null || dueUtc > nowUtc)
            {
                continue;
            }

            var nowLocal = TimeZoneHelper.UtcToLocal(nowUtc, zone);
            while (nextLocal <= nowLocal)
            {
                nextLocal = Next(nextLocal, t.Recurrence, t.AnchorDay);
            }

            t.NextRunLocal = nextLocal.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
            bool newDraft = t.DraftPendingSinceUtc is null;
            if (newDraft)
            {
                t.DraftPendingSinceUtc = nowUtc;
                ready.Add(t);
            }

            store.Save(t);
        }

        return ready;
    }

    /// <summary>Plain-words schedule: "Every week · next draft Mon 2 Nov 2026 09:00".</summary>
    public static string Describe(VoteTemplate t)
    {
        if (t.Recurrence == RecurrenceKind.None)
        {
            return "Not recurring";
        }

        var when = DateTime.TryParseExact(t.NextRunLocal, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var n)
            ? n.ToString("ddd d MMM yyyy HH:mm", CultureInfo.GetCultureInfo("en-GB"))
            : "?";
        return (t.Recurrence == RecurrenceKind.Weekly ? "Every week" : "Every month") + " · next draft " + when;
    }
}
