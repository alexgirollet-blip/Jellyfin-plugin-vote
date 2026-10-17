using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>Applies protection and the admin's "build the vote" filters to a raw scope list.</summary>
public static class CandidateFilter
{
    public static List<CandidateItem> Apply(
        IEnumerable<CandidateItem> scope,
        SessionSettings settings,
        ISet<Guid> protectedItemIds,
        ISet<string> protectedLibraryIds,
        DateTime nowUtc)
    {
        var list = scope
            .Where(c => !protectedItemIds.Contains(c.ItemId))
            .Where(c => !protectedLibraryIds.Contains(c.LibraryId))
            .GroupBy(c => c.ItemId)
            .Select(g => g.First())
            .ToList();

        if (settings.Scope == ScopeMode.MoviesOnly)
        {
            list = list.Where(c => c.Kind == MediaKind.Movie).ToList();
        }
        else if (settings.Scope == ScopeMode.SeriesOnly)
        {
            list = list.Where(c => c.Kind == MediaKind.Series).ToList();
        }
        else if (settings.Scope == ScopeMode.SpecificLibraries)
        {
            var libs = new HashSet<string>(settings.LibraryIds, StringComparer.OrdinalIgnoreCase);
            list = list.Where(c => libs.Contains(c.LibraryId)).ToList();
        }
        else if (settings.Scope == ScopeMode.HandPicked)
        {
            var picked = new HashSet<Guid>(settings.HandPickedItemIds);
            list = list.Where(c => picked.Contains(c.ItemId)).ToList();
        }

        if (settings.FilterNeverWatched)
        {
            list = list.Where(c => c.PlayCount == 0 && c.LastPlayedUtc is null).ToList();
        }

        if (settings.FilterNotWatchedMonths > 0)
        {
            var cutoff = nowUtc.AddMonths(-settings.FilterNotWatchedMonths);
            list = list.Where(c => c.LastPlayedUtc is null || c.LastPlayedUtc < cutoff).ToList();
        }

        if (settings.FilterLargestCount > 0)
        {
            list = list.OrderByDescending(c => c.SizeBytes).Take(settings.FilterLargestCount).ToList();
        }

        return list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
