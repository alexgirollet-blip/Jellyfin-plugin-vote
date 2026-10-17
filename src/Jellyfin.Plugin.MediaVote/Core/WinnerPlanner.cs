using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>
/// Decides which titles a resolved vote acts on: the top N with the chosen tie rule, or, in free-space target mode,
/// winners in rank order until the target drive reaches the target (never more than the vote's maximum).
/// </summary>
public static class WinnerPlanner
{
    private const double Gib = 1024d * 1024 * 1024;

    public static WinnerSelection Plan(SessionSettings s, IReadOnlyList<ItemTally> ranking, IReadOnlyDictionary<Guid, CandidateItem> candidates, IReadOnlyList<DriveStat>? drives)
        => s.TargetMode == TargetMode.FixedCount
            ? FixedCount(ranking, s.WinnerCount, s.TiePolicy, candidates)
            : Target(s, ranking, candidates, drives);

    /// <summary>Top N. Tie rules other than FlagForAdmin/IncludeAllTied break a tie at the cut-off by size or date.</summary>
    public static WinnerSelection FixedCount(IReadOnlyList<ItemTally> ranking, int winnerCount, TiePolicy policy, IReadOnlyDictionary<Guid, CandidateItem>? candidates = null)
    {
        var basic = Tally.SelectWinners(ranking, winnerCount, policy is TiePolicy.IncludeAllTied ? TiePolicy.IncludeAllTied : TiePolicy.FlagForAdmin);
        if (!basic.TieCrossesCutoff || policy is TiePolicy.FlagForAdmin or TiePolicy.IncludeAllTied || candidates is null)
        {
            return basic;
        }

        // Order the tied titles by the rule; the best ones take the free places.
        Func<ItemTally, IComparable> key = policy switch
        {
            TiePolicy.LargerSizeWins => t => -(candidates.TryGetValue(t.ItemId, out var c) ? c.SizeBytes : 0),
            TiePolicy.LeastRecentlyWatchedWins => t => candidates.TryGetValue(t.ItemId, out var c) ? c.LastPlayedUtc ?? DateTime.MinValue : DateTime.MaxValue,
            _ => t => candidates.TryGetValue(t.ItemId, out var c) ? c.DateAddedUtc ?? DateTime.MaxValue : DateTime.MaxValue
        };
        var sorted = basic.TiedAtCutoff.OrderBy(key).ThenBy(t => t.ItemId).ToList();
        int slots = basic.OpenSlots;
        var result = new WinnerSelection();
        result.Winners.AddRange(basic.Winners);
        var boundary = key(sorted[slots - 1]);
        var next = key(sorted[slots]);
        string rule = RuleName(policy);
        if (boundary.CompareTo(next) != 0)
        {
            result.Winners.AddRange(sorted.Take(slots));
            result.Notes.Add($"Tie at the cut-off broken by the rule \"{rule}\".");
            return result;
        }

        // The rule cannot separate the titles at the boundary: those still go to the admin.
        var clear = sorted.Where(t => key(t).CompareTo(boundary) < 0).ToList();
        result.Winners.AddRange(clear);
        result.TiedAtCutoff.AddRange(sorted.Where(t => key(t).CompareTo(boundary) == 0));
        result.OpenSlots = slots - clear.Count;
        result.Notes.Add($"The rule \"{rule}\" could not separate every tied title; the rest is left to the admin.");
        return result;
    }

    /// <summary>Free-space target on one drive. Only titles with data on that drive count; with "Delete via Sonarr/Radarr" only removable ones.</summary>
    public static WinnerSelection Target(SessionSettings s, IReadOnlyList<ItemTally> ranking, IReadOnlyDictionary<Guid, CandidateItem> candidates, IReadOnlyList<DriveStat>? drives)
    {
        var sel = new WinnerSelection();
        var d = (drives ?? Array.Empty<DriveStat>()).FirstOrDefault(x => string.Equals(x.Key, s.TargetDrive, StringComparison.Ordinal));
        if (d is null || !d.Known)
        {
            sel.Notes.Add($"Free-space target: the usage of drive \"{s.TargetDrive}\" is unknown, so nothing was selected.");
            return sel;
        }

        int max = Math.Max(0, s.WinnerCount);
        long freed = 0;
        int skippedOther = 0;
        int skippedUnremovable = 0;
        if (Reached(s, d, 0))
        {
            sel.Notes.Add($"Free-space target already met on {d.Name} ({Describe(s)}): nothing needs to be removed.");
            return sel;
        }

        foreach (var t in ranking.Where(t => t.Score > 0))
        {
            if (sel.Winners.Count >= max || Reached(s, d, freed))
            {
                break;
            }

            if (!candidates.TryGetValue(t.ItemId, out var c) || !c.DriveBytes.TryGetValue(d.Key, out var bytes) || bytes <= 0)
            {
                skippedOther++;
                continue;
            }

            if (s.Outcome == OutcomeMode.DeleteViaArr && c.Removable != true)
            {
                skippedUnremovable++;
                continue;
            }

            sel.Winners.Add(t);
            freed += bytes;
        }

        var after = DriveMath.After(d, freed);
        sel.Notes.Add(Reached(s, d, freed)
            ? string.Create(CultureInfo.InvariantCulture, $"Free-space target met on {d.Name} ({Describe(s)}): {sel.Winners.Count} title(s) {DriveMath.Projection(d, freed)}, {DriveMath.Bytes(after.Free)} free.")
            : string.Create(CultureInfo.InvariantCulture, $"Free-space target NOT met on {d.Name} ({Describe(s)}): {sel.Winners.Count} title(s) (maximum {max}) {DriveMath.Projection(d, freed)}."));
        if (skippedOther > 0)
        {
            sel.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"{skippedOther} winning title(s) are not on {d.Name} and do not count towards the target."));
        }

        if (skippedUnremovable > 0)
        {
            sel.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"{skippedUnremovable} winning title(s) on {d.Name} are not removable via Sonarr/Radarr and were skipped."));
        }

        return sel;
    }

    public static bool Reached(SessionSettings s, DriveStat d, long freed)
        => s.TargetMode == TargetMode.FreeSpace
            ? d.FreeBytes + freed >= s.TargetValue * Gib
            : DriveMath.After(d, freed).Percent < s.TargetValue;

    public static string Describe(SessionSettings s)
        => s.TargetMode == TargetMode.FreeSpace
            ? string.Create(CultureInfo.InvariantCulture, $"at least {s.TargetValue:0.#} GB free")
            : string.Create(CultureInfo.InvariantCulture, $"below {s.TargetValue:0.#}% full");

    private static string RuleName(TiePolicy p) => p switch
    {
        TiePolicy.LargerSizeWins => "larger size wins",
        TiePolicy.LeastRecentlyWatchedWins => "least recently watched wins",
        TiePolicy.OldestAddedWins => "oldest added wins",
        _ => p.ToString()
    };
}
