using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>
/// Query for the shared media browser (admin picker and voting views). Filtering, sorting and paging happen on the
/// server over the cached item list, so phones only ever receive one page.
/// </summary>
public sealed class ItemQuery
{
    public string? Search { get; set; }

    /// <summary>"Movie", "Series" or null for both.</summary>
    public string? Type { get; set; }

    public string? Genre { get; set; }

    /// <summary>Library (collection folder) id.</summary>
    public string? Library { get; set; }

    /// <summary>Drive key (mount point).</summary>
    public string? Drive { get; set; }

    /// <summary>"" (any), "never" (nobody watched), "notsince" (nobody in <see cref="Months"/>), "mine" (watched by me), "notmine".</summary>
    public string? Watched { get; set; }

    /// <summary>For Watched = "notsince".</summary>
    public int Months { get; set; } = 12;

    /// <summary>Only titles added at least this many months ago (0 = off).</summary>
    public int AddedBeforeMonths { get; set; }

    /// <summary>Series where some but not all episodes were watched.</summary>
    public bool HalfFinished { get; set; }

    public double? MinGb { get; set; }

    public double? MaxGb { get; set; }

    public int? MinYear { get; set; }

    public int? MaxYear { get; set; }

    /// <summary>Only titles Sonarr/Radarr can remove (admin, or delete votes).</summary>
    public bool RemovableOnly { get; set; }

    /// <summary>name, size, added, year, lastplayed, playcount, never, drive, votes.</summary>
    public string? Sort { get; set; }

    public bool Descending { get; set; }

    /// <summary>Only items the current user has voted on.</summary>
    public bool MineOnly { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; } = 30;
}

/// <summary>Who is browsing, and what they may see.</summary>
public sealed class BrowseContext
{
    public Guid ViewerId { get; set; }

    public DateTime NowUtc { get; set; }

    /// <summary>False when watch details are hidden from this viewer: watch filters and sorts are then ignored, so nothing leaks.</summary>
    public bool WatchFiltersAllowed { get; set; } = true;

    /// <summary>False when voters see counts only: "not watched since" and "last played" would reveal when titles were watched.</summary>
    public bool WatchTimesAllowed { get; set; } = true;

    /// <summary>Vote score per item (null outside a vote, or when tallies are hidden).</summary>
    public Func<Guid, int>? Score { get; set; }

    /// <summary>Items this viewer voted on (for "My votes").</summary>
    public ISet<Guid>? MyVoteItems { get; set; }

    /// <summary>Drive key → friendly name (for sorting by drive).</summary>
    public Func<string, string>? DriveName { get; set; }
}

/// <summary>Filtering and sorting shared by the admin picker and the voting views. Pure: no Jellyfin types.</summary>
public static class BrowseEngine
{
    private const double Gb = 1024d * 1024 * 1024;

    public static List<CandidateItem> Apply(IEnumerable<CandidateItem> source, ItemQuery q, BrowseContext ctx)
    {
        var items = source;
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

        if (!string.IsNullOrWhiteSpace(q.Library))
        {
            items = items.Where(c => string.Equals(c.LibraryId, q.Library, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(q.Drive))
        {
            items = items.Where(c => c.DriveBytes.ContainsKey(q.Drive));
        }

        if (q.MinGb is double min)
        {
            items = items.Where(c => c.SizeBytes / Gb >= min);
        }

        if (q.MaxGb is double max)
        {
            items = items.Where(c => c.SizeBytes / Gb <= max);
        }

        if (q.MinYear is int y1)
        {
            items = items.Where(c => c.Year >= y1);
        }

        if (q.MaxYear is int y2)
        {
            items = items.Where(c => c.Year is not null && c.Year <= y2);
        }

        if (q.AddedBeforeMonths > 0)
        {
            var cutoff = ctx.NowUtc.AddMonths(-q.AddedBeforeMonths);
            items = items.Where(c => c.DateAddedUtc is not null && c.DateAddedUtc <= cutoff);
        }

        if (q.RemovableOnly)
        {
            items = items.Where(c => c.Removable == true);
        }

        if (q.MineOnly)
        {
            var mine = ctx.MyVoteItems ?? new HashSet<Guid>();
            items = items.Where(c => mine.Contains(c.ItemId));
        }

        if (ctx.WatchFiltersAllowed)
        {
            switch ((q.Watched ?? string.Empty).ToLowerInvariant())
            {
                case "never":
                    items = items.Where(NeverWatched);
                    break;
                case "notsince" when ctx.WatchTimesAllowed:
                    var since = ctx.NowUtc.AddMonths(-Math.Max(1, q.Months));
                    items = items.Where(c => c.LastPlayedUtc is null || c.LastPlayedUtc < since);
                    break;
                case "mine":
                    items = items.Where(c => c.ViewerIds.Contains(ctx.ViewerId));
                    break;
                case "notmine":
                    items = items.Where(c => !c.ViewerIds.Contains(ctx.ViewerId));
                    break;
            }

            if (q.HalfFinished)
            {
                items = items.Where(IsHalfFinished);
            }
        }

        return Sort(items, q, ctx);
    }

    public static bool NeverWatched(CandidateItem c) => c.PlayCount == 0 && c.LastPlayedUtc is null && c.ViewerIds.Count == 0;

    /// <summary>A series with some, but not all, episodes watched by anyone.</summary>
    public static bool IsHalfFinished(CandidateItem c)
        => c.Kind == MediaKind.Series && c.EpisodeCount > 0 && c.PlayedEpisodeCount > 0 && c.PlayedEpisodeCount < c.EpisodeCount;

    private static List<CandidateItem> Sort(IEnumerable<CandidateItem> items, ItemQuery q, BrowseContext ctx)
    {
        string sort = (q.Sort ?? "name").ToLowerInvariant();
        bool desc = q.Descending;
        if ((sort == "votes" && ctx.Score is null) || (!ctx.WatchFiltersAllowed && sort is "lastplayed" or "playcount" or "never") || (!ctx.WatchTimesAllowed && sort == "lastplayed"))
        {
            sort = "name"; // cannot sort by hidden data: plain A–Z, so the order reveals nothing
            desc = false;
        }
        var score = ctx.Score ?? (_ => 0);
        var driveName = ctx.DriveName ?? (k => k);
        string FirstDrive(CandidateItem c) => c.DriveBytes.Count == 0 ? "\uffff" : c.DriveBytes.OrderByDescending(p => p.Value).Select(p => driveName(p.Key)).First();

        IOrderedEnumerable<CandidateItem> ordered = sort switch
        {
            "year" => Order(items, c => c.Year ?? 0, desc),
            "size" => Order(items, c => c.SizeBytes, desc),
            "votes" => Order(items, c => score(c.ItemId), desc),
            "added" => Order(items, c => c.DateAddedUtc ?? DateTime.MinValue, desc),
            "lastplayed" => Order(items, c => c.LastPlayedUtc ?? DateTime.MinValue, desc),
            "playcount" => Order(items, c => c.PlayCount, desc),
            // Never watched first, then the ones watched longest ago.
            "never" => items.OrderBy(c => NeverWatched(c) ? 0 : 1).ThenBy(c => c.LastPlayedUtc ?? DateTime.MinValue),
            "drive" => desc ? items.OrderByDescending(FirstDrive, StringComparer.OrdinalIgnoreCase) : items.OrderBy(FirstDrive, StringComparer.OrdinalIgnoreCase),
            _ => desc ? items.OrderByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase) : items.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
        };
        return ordered.ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.ItemId).ToList();
    }

    private static IOrderedEnumerable<CandidateItem> Order<TKey>(IEnumerable<CandidateItem> items, Func<CandidateItem, TKey> key, bool desc)
        => desc ? items.OrderByDescending(key) : items.OrderBy(key);
}

/// <summary>A title's share of one drive, for the card.</summary>
public sealed class ItemDriveView
{
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Bytes of this title on the drive.</summary>
    public long Bytes { get; set; }

    public bool Known { get; set; }

    public long UsedBytes { get; set; }

    public long TotalBytes { get; set; }

    public double UsedPercent { get; set; }

    public DriveLevel Level { get; set; }
}

/// <summary>A drive in the browser's filter and in the selection tray.</summary>
public sealed class DriveView
{
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Known { get; set; }

    public long UsedBytes { get; set; }

    public long FreeBytes { get; set; }

    public long TotalBytes { get; set; }

    public double UsedPercent { get; set; }

    public DriveLevel Level { get; set; }

    public string FsType { get; set; } = string.Empty;

    public List<string> MountPoints { get; set; } = new();

    public string? SharedWarning { get; set; }

    public static DriveView From(DriveStat d) => new()
    {
        Key = d.Key, Name = d.Name, Known = d.Known, UsedBytes = d.UsedBytes, FreeBytes = d.FreeBytes, TotalBytes = d.TotalBytes,
        UsedPercent = d.UsedPercent, Level = d.Level, FsType = d.FsType, MountPoints = d.MountPoints.ToList(), SharedWarning = d.SharedWarning
    };
}

/// <summary>One title as the viewer sees it (voting views and the admin picker).</summary>
public sealed class ItemView
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public MediaKind Kind { get; set; }

    public string Library { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTime? DateAddedUtc { get; set; }

    /// <summary>Null when watch details are hidden from this viewer.</summary>
    public DateTime? LastPlayedUtc { get; set; }

    /// <summary>Null when hidden ("counts only" or "hidden"), or unknown.</summary>
    public string? LastPlayedBy { get; set; }

    /// <summary>Null when watch details are hidden.</summary>
    public int? PlayCount { get; set; }

    /// <summary>Distinct users who played it; null when hidden.</summary>
    public int? ViewerCount { get; set; }

    /// <summary>True when nobody has played it; null when hidden.</summary>
    public bool? NeverWatched { get; set; }

    public int EpisodeCount { get; set; }

    public int? PlayedEpisodeCount { get; set; }

    public List<ItemDriveView> Drives { get; set; } = new();

    public List<string> Genres { get; set; } = new();

    public string? ImageTag { get; set; }

    /// <summary>Removable via Sonarr/Radarr: always for admins; for voters only in delete votes.</summary>
    public bool? Removable { get; set; }

    /// <summary>Admin only: what Sonarr/Radarr entry it matched, or why not.</summary>
    public string? RemovalNote { get; set; }

    /// <summary>Null when live tallies are hidden (and outside a vote).</summary>
    public int? RemoveVotes { get; set; }

    public int? KeepVotes { get; set; }

    /// <summary>Empty when names are hidden from this viewer.</summary>
    public List<string> RemoveVoters { get; set; } = new();

    public List<string> KeepVoters { get; set; } = new();

    public int MyRemoveVotes { get; set; }

    public int MyKeepVotes { get; set; }
}

/// <summary>Paged result, with the filter options (facets).</summary>
public sealed class ItemPage
{
    public int Total { get; set; }

    public long TotalBytes { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; }

    public List<ItemView> Items { get; set; } = new();

    public List<string> Genres { get; set; } = new();

    public List<LibraryFacet> Libraries { get; set; } = new();

    public List<DriveView> Drives { get; set; } = new();

    /// <summary>What watch details this viewer gets ("NamesAndTime", "CountsOnly", "Hidden").</summary>
    public string WatchDetails { get; set; } = "NamesAndTime";
}

/// <summary>A library in the browser's filter.</summary>
public sealed record LibraryFacet(string Id, string Name);

/// <summary>Builds viewer-specific item views. Enforces the "hide names", "hide tallies" and watch-detail settings server-side.</summary>
public static class ViewBuilder
{
    public const int MaxPageSize = 100;

    /// <summary>Watch details for this viewer: admins always see everything.</summary>
    public static string WatchLevel(bool viewerIsAdmin, string configured) => viewerIsAdmin ? "NamesAndTime" : configured;

    /// <summary>The voting list.</summary>
    /// <param name="isVisible">Optional filter: items the viewer may see under their library access.</param>
    public static ItemPage BuildPage(VoteSession session, Guid viewerId, bool viewerIsAdmin, ItemQuery q, Func<Guid, bool>? isVisible = null, IReadOnlyList<DriveStat>? drives = null, string watchDetails = "NamesAndTime", DateTime? nowUtc = null)
    {
        var tallies = Tally.Count(session).ToDictionary(t => t.ItemId);
        bool resolved = session.Status != SessionStatus.Open;
        bool showTallies = viewerIsAdmin || session.Settings.ShowLiveTallies || resolved;
        bool showNames = showTallies && (viewerIsAdmin || session.Settings.ShowVoterNames);
        bool deleting = session.Settings.Outcome == OutcomeMode.DeleteViaArr;
        string watch = WatchLevel(viewerIsAdmin, watchDetails);
        var myVotes = session.Votes.Where(v => v.UserId == viewerId).ToList();

        IEnumerable<CandidateItem> items = session.Candidates;
        if (isVisible is not null)
        {
            items = items.Where(c => isVisible(c.ItemId));
        }

        if (!viewerIsAdmin && !deleting)
        {
            q.RemovableOnly = false;
        }

        var ctx = new BrowseContext
        {
            ViewerId = viewerId,
            NowUtc = nowUtc ?? DateTime.UtcNow,
            WatchFiltersAllowed = watch != "Hidden",
            WatchTimesAllowed = watch == "NamesAndTime",
            Score = showTallies ? id => tallies.TryGetValue(id, out var t) ? t.Score : 0 : null,
            MyVoteItems = myVotes.Select(v => v.ItemId).ToHashSet(),
            DriveName = DriveNames(drives)
        };
        var list = BrowseEngine.Apply(items, q, ctx);
        var page = Page(list, q, session.Candidates, drives, watch);
        page.Items = Slice(list, q).Select(c =>
        {
            tallies.TryGetValue(c.ItemId, out var t);
            var v = Project(c, viewerId, watch, drives, viewerIsAdmin || deleting, viewerIsAdmin);
            v.RemoveVotes = showTallies ? t?.RemoveVotes ?? 0 : null;
            v.KeepVotes = showTallies && session.Settings.AllowKeepVotes ? t?.KeepVotes ?? 0 : null;
            v.RemoveVoters = showNames && t is not null ? t.RemoveVoters.ToList() : new List<string>();
            v.KeepVoters = showNames && t is not null ? t.KeepVoters.ToList() : new List<string>();
            v.MyRemoveVotes = myVotes.Count(x => x.ItemId == c.ItemId && x.Kind == VoteKind.Remove);
            v.MyKeepVotes = myVotes.Count(x => x.ItemId == c.ItemId && x.Kind == VoteKind.Keep);
            return v;
        }).ToList();
        return page;
    }

    /// <summary>The admin's hand-picked titles browser (no vote): everything visible, removability always shown.</summary>
    public static ItemPage BuildBrowsePage(IReadOnlyList<CandidateItem> scope, Guid adminId, ItemQuery q, IReadOnlyList<DriveStat>? drives, DateTime nowUtc)
    {
        var ctx = new BrowseContext { ViewerId = adminId, NowUtc = nowUtc, WatchFiltersAllowed = true, DriveName = DriveNames(drives) };
        var list = BrowseEngine.Apply(scope, q, ctx);
        var page = Page(list, q, scope, drives, "NamesAndTime");
        page.Items = Slice(list, q).Select(c => Project(c, adminId, "NamesAndTime", drives, true, true)).ToList();
        return page;
    }

    public static ItemView Project(CandidateItem c, Guid viewerId, string watch, IReadOnlyList<DriveStat>? drives, bool showRemovable, bool isAdmin)
    {
        bool names = watch == "NamesAndTime";
        bool counts = watch != "Hidden";
        var byKey = (drives ?? Array.Empty<DriveStat>()).ToDictionary(d => d.Key, StringComparer.Ordinal);
        return new ItemView
        {
            Id = c.ItemId,
            Name = c.Name,
            Year = c.Year,
            Kind = c.Kind,
            Library = c.LibraryName,
            SizeBytes = c.SizeBytes,
            DateAddedUtc = c.DateAddedUtc,
            LastPlayedUtc = names ? c.LastPlayedUtc : null,
            LastPlayedBy = names ? c.LastPlayedBy : null,
            PlayCount = counts ? c.PlayCount : null,
            ViewerCount = counts ? c.ViewerIds.Count : null,
            NeverWatched = counts ? BrowseEngine.NeverWatched(c) : null,
            EpisodeCount = c.EpisodeCount,
            PlayedEpisodeCount = counts && c.Kind == MediaKind.Series ? c.PlayedEpisodeCount : null,
            Drives = c.DriveBytes.OrderByDescending(p => p.Value).Select(p =>
            {
                byKey.TryGetValue(p.Key, out var d);
                return new ItemDriveView
                {
                    Key = p.Key, Name = d?.Name ?? p.Key, Bytes = p.Value, Known = d?.Known ?? false,
                    UsedBytes = d?.UsedBytes ?? 0, TotalBytes = d?.TotalBytes ?? 0, UsedPercent = d?.UsedPercent ?? 0, Level = d?.Level ?? DriveLevel.Unknown
                };
            }).ToList(),
            Genres = c.Genres,
            ImageTag = c.PrimaryImageTag,
            Removable = showRemovable ? c.Removable : null,
            RemovalNote = isAdmin ? c.RemovalNote : null
        };
    }

    private static ItemPage Page(List<CandidateItem> list, ItemQuery q, IEnumerable<CandidateItem> all, IReadOnlyList<DriveStat>? drives, string watch)
    {
        var allList = all as IReadOnlyCollection<CandidateItem> ?? all.ToList();
        var used = allList.SelectMany(c => c.DriveBytes.Keys).ToHashSet(StringComparer.Ordinal);
        return new ItemPage
        {
            Total = list.Count,
            TotalBytes = list.Sum(c => c.SizeBytes),
            Page = Math.Max(0, q.Page),
            PageSize = Math.Clamp(q.PageSize, 1, MaxPageSize),
            Genres = allList.SelectMany(c => c.Genres).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList(),
            Libraries = allList.Where(c => !string.IsNullOrEmpty(c.LibraryId)).GroupBy(c => c.LibraryId, StringComparer.OrdinalIgnoreCase).Select(g => new LibraryFacet(g.Key, g.First().LibraryName)).OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Drives = (drives ?? Array.Empty<DriveStat>()).Where(d => used.Contains(d.Key)).Select(DriveView.From).ToList(),
            WatchDetails = watch
        };
    }

    private static IEnumerable<CandidateItem> Slice(List<CandidateItem> list, ItemQuery q)
    {
        int pageSize = Math.Clamp(q.PageSize, 1, MaxPageSize);
        return list.Skip(Math.Max(0, q.Page) * pageSize).Take(pageSize);
    }

    private static Func<string, string> DriveNames(IReadOnlyList<DriveStat>? drives)
    {
        var map = (drives ?? Array.Empty<DriveStat>()).ToDictionary(d => d.Key, d => d.Name, StringComparer.Ordinal);
        return k => map.TryGetValue(k, out var n) ? n : k;
    }
}
