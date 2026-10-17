using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>The winners chosen from a ranking, with tie information.</summary>
public sealed class WinnerSelection
{
    /// <summary>Items to act on, in rank order.</summary>
    public List<ItemTally> Winners { get; } = new();

    /// <summary>Items tied across the cut-off that were NOT selected (FlagForAdmin).</summary>
    public List<ItemTally> TiedAtCutoff { get; } = new();

    /// <summary>Number of winner slots still open for the tied items.</summary>
    public int OpenSlots { get; set; }

    public bool TieCrossesCutoff => TiedAtCutoff.Count > 0;

    /// <summary>Explanations (tie-break rule used, free-space target reached or not, titles skipped).</summary>
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Vote counting, ranking and tie handling.
/// Rules (documented in the README):
/// <list type="bullet">
/// <item>Score = remove votes − keep votes.</item>
/// <item>Only items with a score above zero can win.</item>
/// <item>Ranked by score, highest first. Equal scores are a tie; nothing (name, date) breaks ties automatically.</item>
/// <item>The top N win. If items share the score of the N-th place and not all of them fit,
///   the tie "crosses the cut-off". Default policy: none of the tied items is acted on, they are
///   flagged for the admin; the clear winners above the tie are still acted on.</item>
/// </list>
/// </summary>
public static class Tally
{
    public static List<ItemTally> Count(VoteSession session, bool includeNames = true)
    {
        var byItem = session.Candidates.ToDictionary(c => c.ItemId, c => new ItemTally { ItemId = c.ItemId });
        foreach (var vote in session.Votes.OrderBy(v => v.CastUtc))
        {
            if (!byItem.TryGetValue(vote.ItemId, out var t))
            {
                continue; // vote on an item no longer in the candidate list: ignored
            }

            if (vote.Kind == VoteKind.Remove)
            {
                t.RemoveVotes++;
                if (includeNames)
                {
                    t.RemoveVoters.Add(vote.UserName);
                }
            }
            else if (session.Settings.AllowKeepVotes)
            {
                t.KeepVotes++;
                if (includeNames)
                {
                    t.KeepVoters.Add(vote.UserName);
                }
            }
        }

        return Rank(byItem.Values);
    }

    /// <summary>Sorts by score (desc), then remove votes (desc). Ties keep a stable, deterministic order by id for display only.</summary>
    public static List<ItemTally> Rank(IEnumerable<ItemTally> tallies)
        => tallies
            .OrderByDescending(t => t.Score)
            .ThenByDescending(t => t.RemoveVotes)
            .ThenBy(t => t.ItemId)
            .ToList();

    /// <summary>Selects the top <paramref name="winnerCount"/> items according to <paramref name="policy"/>.</summary>
    public static WinnerSelection SelectWinners(IReadOnlyList<ItemTally> ranking, int winnerCount, TiePolicy policy)
    {
        var selection = new WinnerSelection();
        var eligible = ranking.Where(t => t.Score > 0).OrderByDescending(t => t.Score).ToList();
        if (winnerCount <= 0 || eligible.Count == 0)
        {
            return selection;
        }

        if (eligible.Count <= winnerCount)
        {
            selection.Winners.AddRange(eligible);
            return selection;
        }

        int cutoffScore = eligible[winnerCount - 1].Score;
        var above = eligible.Where(t => t.Score > cutoffScore).ToList();
        var atCutoff = eligible.Where(t => t.Score == cutoffScore).ToList();

        selection.Winners.AddRange(above);
        if (above.Count + atCutoff.Count <= winnerCount)
        {
            // Everyone at the cut-off fits: no tie problem.
            selection.Winners.AddRange(atCutoff);
            return selection;
        }

        if (policy == TiePolicy.IncludeAllTied)
        {
            selection.Winners.AddRange(atCutoff);
            return selection;
        }

        selection.TiedAtCutoff.AddRange(atCutoff);
        selection.OpenSlots = winnerCount - above.Count;
        return selection;
    }

    /// <summary>Distinct users who cast at least one vote (remove or keep).</summary>
    public static int VoterCount(VoteSession session)
        => session.Votes.Select(v => v.UserId).Distinct().Count();

    public static double TurnoutPercent(int voters, int eligible)
        => eligible <= 0 ? 0 : Math.Round(100.0 * voters / eligible, 1);

    /// <summary>Whether the minimum turnout requirement is met. A minimum of 0 always passes.</summary>
    public static bool TurnoutMet(int voters, int eligible, int minPercent)
        => minPercent <= 0 || (eligible > 0 && 100.0 * voters / eligible >= minPercent);
}
