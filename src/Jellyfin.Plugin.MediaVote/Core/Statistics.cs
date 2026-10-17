using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>One resolved vote in the history.</summary>
public sealed record SessionStat(Guid Id, string Title, DateTime ResolvedUtc, long FreedBytes, long CumulativeFreedBytes, int Voters, int Eligible, double TurnoutPercent, int Removed, string Outcome);

/// <summary>A title that got votes across votes.</summary>
public sealed record TitleStat(string Name, int RemoveVotes, int Votes, int Sessions);

/// <summary>How much one user took part.</summary>
public sealed record UserStat(string Name, int VotesCast, int SessionsVoted, int SessionsEligible, double ParticipationPercent);

/// <summary>The admin's history and statistics page.</summary>
public sealed class HistoryStats
{
    public List<SessionStat> Sessions { get; set; } = new();

    public long TotalFreedBytes { get; set; }

    public List<TitleStat> MostVoted { get; set; } = new();

    public List<UserStat> Participation { get; set; } = new();
}

/// <summary>Builds <see cref="HistoryStats"/> from the stored votes. Pure.</summary>
public static class Statistics
{
    /// <param name="sessions">Every stored vote.</param>
    /// <param name="activeUsers">Ids and names of current, enabled users ("all users" votes count them as eligible).</param>
    public static HistoryStats Build(IReadOnlyList<VoteSession> sessions, IReadOnlyDictionary<Guid, string> activeUsers)
    {
        var resolved = sessions.Where(s => s.Status == SessionStatus.Resolved && s.Result is not null).OrderBy(s => s.Result!.ResolvedUtc).ToList();
        var stats = new HistoryStats();
        long cumulative = 0;
        foreach (var s in resolved)
        {
            var r = s.Result!;
            long freed = r.Items.Where(i => i.Status is ItemOutcomeStatus.Removed or ItemOutcomeStatus.Deleted).Sum(i => i.ArrSizeOnDisk > 0 ? i.ArrSizeOnDisk : i.SizeBytes);
            cumulative += freed;
            stats.Sessions.Add(new SessionStat(s.Id, s.Settings.Title, r.ResolvedUtc, freed, cumulative, r.VoterCount, r.EligibleVoterCount, r.TurnoutPercent, r.Items.Count(i => i.Status is ItemOutcomeStatus.Removed or ItemOutcomeStatus.Deleted), r.Outcome.ToString()));
        }

        stats.TotalFreedBytes = cumulative;
        var all = sessions.Where(s => s.Status != SessionStatus.Cancelled).ToList();
        stats.MostVoted = all
            .SelectMany(s => s.Votes.Select(v => (Session: s.Id, Name: s.Candidates.FirstOrDefault(c => c.ItemId == v.ItemId)?.Name ?? v.ItemId.ToString(), v.Kind)))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TitleStat(g.First().Name, g.Count(x => x.Kind == VoteKind.Remove), g.Count(), g.Select(x => x.Session).Distinct().Count()))
            .OrderByDescending(t => t.RemoveVotes).ThenByDescending(t => t.Votes).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToList();

        var users = new Dictionary<Guid, (string Name, int Votes, int Voted, int Eligible)>();
        foreach (var s in all)
        {
            var eligible = s.Settings.AllUsers ? activeUsers.Keys.ToList() : s.Settings.EligibleUserIds.ToList();
            var voters = s.Votes.GroupBy(v => v.UserId).ToDictionary(g => g.Key, g => (g.Count(), g.First().UserName));
            foreach (var u in eligible.Union(voters.Keys))
            {
                (string Name, int Votes, int Voted, int Eligible) cur = users.TryGetValue(u, out var existing)
                    ? existing
                    : (activeUsers.GetValueOrDefault(u) ?? (voters.TryGetValue(u, out var vv) ? vv.Item2 : u.ToString()), 0, 0, 0);
                bool voted = voters.TryGetValue(u, out var v);
                users[u] = (cur.Name, cur.Votes + (voted ? v.Item1 : 0), cur.Voted + (voted ? 1 : 0), cur.Eligible + (eligible.Contains(u) ? 1 : 0));
            }
        }

        stats.Participation = users.Values
            .Select(u => new UserStat(u.Name, u.Votes, u.Voted, u.Eligible, u.Eligible == 0 ? 0 : Math.Round(100.0 * Math.Min(u.Voted, u.Eligible) / u.Eligible, 1)))
            .OrderByDescending(u => u.ParticipationPercent).ThenByDescending(u => u.VotesCast).ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return stats;
    }
}
