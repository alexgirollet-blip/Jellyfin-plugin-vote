using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>Query for the paged item list shown to voters.</summary>
public sealed class ItemQuery
{
    public string? Search { get; set; }

    /// <summary>"Movie", "Series" or null for both.</summary>
    public string? Type { get; set; }

    public string? Genre { get; set; }

    /// <summary>name, year, size, votes, added, lastplayed, playcount.</summary>
    public string? Sort { get; set; }

    public bool Descending { get; set; }

    /// <summary>Only items the current user has voted on.</summary>
    public bool MineOnly { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; } = 30;
}

/// <summary>One votable item as the voter sees it.</summary>
public sealed class ItemView
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public MediaKind Kind { get; set; }

    public string Library { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTime? DateAddedUtc { get; set; }

    public DateTime? LastPlayedUtc { get; set; }

    public int PlayCount { get; set; }

    public List<string> Genres { get; set; } = new();

    public string? ImageTag { get; set; }

    /// <summary>For "Delete via Sonarr/Radarr" votes: false when Sonarr/Radarr do not manage the title (it can only be removed by hand).</summary>
    public bool? Removable { get; set; }

    /// <summary>Null when live tallies are hidden.</summary>
    public int? RemoveVotes { get; set; }

    public int? KeepVotes { get; set; }

    /// <summary>Empty when names are hidden from this viewer.</summary>
    public List<string> RemoveVoters { get; set; } = new();

    public List<string> KeepVoters { get; set; } = new();

    public int MyRemoveVotes { get; set; }

    public int MyKeepVotes { get; set; }
}

/// <summary>Paged result.</summary>
public sealed class ItemPage
{
    public int Total { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; }

    public List<ItemView> Items { get; set; } = new();

    public List<string> Genres { get; set; } = new();
}

/// <summary>Builds viewer-specific item views. Enforces the "hide names" and "hide tallies" toggles server-side.</summary>
public static class ViewBuilder
{
    public const int MaxPageSize = 100;

    /// <param name="isVisible">Optional filter: items the viewer may see under their library access.</param>
    public static ItemPage BuildPage(VoteSession session, Guid viewerId, bool viewerIsAdmin, ItemQuery q, Func<Guid, bool>? isVisible = null)
    {
        var tallies = Tally.Count(session).ToDictionary(t => t.ItemId);
        bool resolved = session.Status != SessionStatus.Open;
        bool showTallies = viewerIsAdmin || session.Settings.ShowLiveTallies || resolved;
        bool showNames = showTallies && (viewerIsAdmin || session.Settings.ShowVoterNames);

        IEnumerable<CandidateItem> items = session.Candidates;
        if (isVisible is not null)
        {
            items = items.Where(c => isVisible(c.ItemId));
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            items = items.Where(c => c.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        if (string.Equals(q.Type, "Movie", StringComparison.OrdinalIgnoreCase))
        {
            items = items.Where(c => c.Kind == MediaKind.Movie);
        }
        else if (string.Equals(q.Type, "Series", StringComparison.OrdinalIgnoreCase))
        {
            items = items.Where(c => c.Kind == MediaKind.Series);
        }

        if (!string.IsNullOrWhiteSpace(q.Genre))
        {
            items = items.Where(c => c.Genres.Contains(q.Genre, StringComparer.OrdinalIgnoreCase));
        }

        var myVotes = session.Votes.Where(v => v.UserId == viewerId).ToList();
        if (q.MineOnly)
        {
            var mine = myVotes.Select(v => v.ItemId).ToHashSet();
            items = items.Where(c => mine.Contains(c.ItemId));
        }

        int Score(CandidateItem c) => tallies.TryGetValue(c.ItemId, out var t) ? t.Score : 0;

        string sort = (q.Sort ?? "name").ToLowerInvariant();
        if (sort == "votes" && !showTallies)
        {
            sort = "name"; // cannot sort by hidden data
        }

        IOrderedEnumerable<CandidateItem> ordered = sort switch
        {
            "year" => items.OrderBy(c => c.Year ?? 0),
            "size" => items.OrderBy(c => c.SizeBytes),
            "votes" => items.OrderBy(Score),
            "added" => items.OrderBy(c => c.DateAddedUtc ?? DateTime.MinValue),
            "lastplayed" => items.OrderBy(c => c.LastPlayedUtc ?? DateTime.MinValue),
            "playcount" => items.OrderBy(c => c.PlayCount),
            _ => items.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
        };
        if (q.Descending)
        {
            ordered = sort switch
            {
                "year" => items.OrderByDescending(c => c.Year ?? 0),
                "size" => items.OrderByDescending(c => c.SizeBytes),
                "votes" => items.OrderByDescending(Score),
                "added" => items.OrderByDescending(c => c.DateAddedUtc ?? DateTime.MinValue),
                "lastplayed" => items.OrderByDescending(c => c.LastPlayedUtc ?? DateTime.MinValue),
                "playcount" => items.OrderByDescending(c => c.PlayCount),
                _ => items.OrderByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase)
            };
        }

        var list = ordered.ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        int pageSize = Math.Clamp(q.PageSize, 1, MaxPageSize);
        int page = Math.Max(0, q.Page);

        return new ItemPage
        {
            Total = list.Count,
            Page = page,
            PageSize = pageSize,
            Genres = session.Candidates.SelectMany(c => c.Genres).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList(),
            Items = list.Skip(page * pageSize).Take(pageSize).Select(c =>
            {
                tallies.TryGetValue(c.ItemId, out var t);
                return new ItemView
                {
                    Id = c.ItemId,
                    Name = c.Name,
                    Year = c.Year,
                    Kind = c.Kind,
                    Library = c.LibraryName,
                    SizeBytes = c.SizeBytes,
                    DateAddedUtc = c.DateAddedUtc,
                    LastPlayedUtc = c.LastPlayedUtc,
                    PlayCount = c.PlayCount,
                    Genres = c.Genres,
                    ImageTag = c.PrimaryImageTag,
                    Removable = session.Settings.Outcome == OutcomeMode.DeleteViaArr ? c.Removable : null,
                    RemoveVotes = showTallies ? t?.RemoveVotes ?? 0 : null,
                    KeepVotes = showTallies && session.Settings.AllowKeepVotes ? t?.KeepVotes ?? 0 : null,
                    RemoveVoters = showNames && t is not null ? t.RemoveVoters.ToList() : new List<string>(),
                    KeepVoters = showNames && t is not null ? t.KeepVoters.ToList() : new List<string>(),
                    MyRemoveVotes = myVotes.Count(v => v.ItemId == c.ItemId && v.Kind == VoteKind.Remove),
                    MyKeepVotes = myVotes.Count(v => v.ItemId == c.ItemId && v.Kind == VoteKind.Keep)
                };
            }).ToList()
        };
    }
}
