using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaVote.Configuration;
using Jellyfin.Plugin.MediaVote.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaVote.Services;

/// <summary>A library (collection folder) for the admin UI.</summary>
public sealed record LibraryInfo(string Id, string Name, string? CollectionType, bool Protected);

/// <summary>
/// Reads votable items (whole Movies and whole Series only; never seasons, episodes or BoxSets)
/// from Jellyfin, with sizes and play statistics. Play statistics come from Jellyfin's own user data,
/// optionally enriched with the Playback Reporting plugin's database.
/// </summary>
public sealed class LibraryCatalog
{
    private static readonly BaseItemKind[] VotableKinds = { BaseItemKind.Movie, BaseItemKind.Series };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<LibraryCatalog> _logger;
    private readonly ConcurrentDictionary<(Guid Session, Guid User), (DateTime At, HashSet<Guid> Ids)> _visibleCache = new();
    private readonly object _scopeLock = new();
    private (DateTime At, List<CandidateItem> Items)? _scopeCache;

    private readonly DriveService? _drives;

    public LibraryCatalog(ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager, IApplicationPaths paths, ILogger<LibraryCatalog> logger, DriveService? drives = null)
    {
        _drives = drives;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _paths = paths;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public IReadOnlyList<LibraryInfo> GetLibraries()
    {
        var protectedIds = new HashSet<string>(Config.ProtectedLibraryIds, StringComparer.OrdinalIgnoreCase);
        return JellyfinCall.Run("list libraries", () => _libraryManager.GetVirtualFolders())
            .Where(f => f.CollectionType is null || f.CollectionType is CollectionTypeOptions.movies or CollectionTypeOptions.tvshows or CollectionTypeOptions.homevideos)
            .Select(f => new LibraryInfo(NormalizeId(f.ItemId), f.Name, f.CollectionType?.ToString(), protectedIds.Contains(NormalizeId(f.ItemId))))
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Folders of the movie/TV libraries (as Jellyfin sees them), for the read-only "media folders" status line.</summary>
    public IReadOnlyList<(string Library, string Path)> GetLibraryFolders()
        => JellyfinCall.Run("list library folders", () => _libraryManager.GetVirtualFolders())
            .Where(f => f.CollectionType is null || f.CollectionType is CollectionTypeOptions.movies or CollectionTypeOptions.tvshows or CollectionTypeOptions.homevideos)
            .SelectMany(f => (f.Locations ?? Array.Empty<string>()).Select(l => (f.Name, l)))
            .ToList();

    /// <summary>
    /// Cached <see cref="BuildScope"/> over all libraries. Building reads play data for every user,
    /// so the admin's preview and the subsequent create share one result for a couple of minutes.
    /// </summary>
    public List<CandidateItem> GetScopeCached(TimeSpan maxAge)
    {
        lock (_scopeLock)
        {
            if (_scopeCache is { } c && DateTime.UtcNow - c.At < maxAge)
            {
                return c.Items;
            }

            var items = BuildScope();
            _scopeCache = (DateTime.UtcNow, items);
            return items;
        }
    }

    /// <summary>All votable items in every library, with stats. Filtering by scope happens in <see cref="CandidateFilter"/>.</summary>
    public List<CandidateItem> BuildScope(IReadOnlyCollection<string>? onlyLibraryIds = null)
    {
        var libraries = GetLibraries().Where(l => onlyLibraryIds is null || onlyLibraryIds.Contains(l.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        var items = new Dictionary<Guid, (BaseItem Item, LibraryInfo Lib)>();
        foreach (var lib in libraries)
        {
            if (!Guid.TryParse(lib.Id, out var libGuid))
            {
                continue;
            }

            var list = JellyfinCall.Run("read the items of library " + lib.Name, () => _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = VotableKinds,
                Recursive = true,
                IsVirtualItem = false,
                AncestorIds = new[] { libGuid }
            }));
            foreach (var item in list)
            {
                if (item is Movie or Series)
                {
                    items.TryAdd(item.Id, (item, lib));
                }
            }
        }

        // Series size, episode count, per-drive bytes and the episode->series map from one episode query.
        var seriesSize = new Dictionary<Guid, long>();
        var seriesEpisodes = new Dictionary<Guid, int>();
        var seriesDrives = new Dictionary<Guid, Dictionary<string, long>>();
        var episodeToSeries = new Dictionary<Guid, Guid>();
        string? DriveOf(string? path)
        {
            try
            {
                return _drives?.KeyFor(path);
            }
#pragma warning disable CA1031 // drive info is informational
            catch (Exception)
#pragma warning restore CA1031
            {
                return null;
            }
        }

        if (items.Values.Any(v => v.Item is Series))
        {
            foreach (var ep in JellyfinCall.Run("read episodes", () => _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Episode }, Recursive = true, IsVirtualItem = false })).OfType<Episode>())
            {
                episodeToSeries[ep.Id] = ep.SeriesId;
                long size = ep.Size ?? 0;
                seriesSize[ep.SeriesId] = seriesSize.GetValueOrDefault(ep.SeriesId) + size;
                seriesEpisodes[ep.SeriesId] = seriesEpisodes.GetValueOrDefault(ep.SeriesId) + 1;
                if (DriveOf(ep.Path) is { } key)
                {
                    if (!seriesDrives.TryGetValue(ep.SeriesId, out var d))
                    {
                        d = new Dictionary<string, long>(StringComparer.Ordinal);
                        seriesDrives[ep.SeriesId] = d;
                    }

                    d[key] = d.GetValueOrDefault(key) + size;
                }
            }
        }

        var stats = ReadPlayStats(episodeToSeries);
        return items.Values.Select(v =>
        {
            stats.TryGetValue(v.Item.Id, out var st);
            var image = v.Item.GetImageInfo(ImageType.Primary, 0);
            bool isSeries = v.Item is Series;
            long itemSize = isSeries ? seriesSize.GetValueOrDefault(v.Item.Id) : v.Item.Size ?? 0;
            var driveBytes = isSeries
                ? seriesDrives.GetValueOrDefault(v.Item.Id) ?? new Dictionary<string, long>(StringComparer.Ordinal)
                : (DriveOf(v.Item.Path) is { } mk ? new Dictionary<string, long>(StringComparer.Ordinal) { [mk] = itemSize } : new Dictionary<string, long>(StringComparer.Ordinal));
            return new CandidateItem
            {
                LastPlayedBy = st?.LastBy,
                ViewerIds = st?.Viewers.ToList() ?? new List<Guid>(),
                EpisodeCount = isSeries ? seriesEpisodes.GetValueOrDefault(v.Item.Id) : 0,
                PlayedEpisodeCount = isSeries ? st?.PlayedEpisodes.Count ?? 0 : 0,
                DriveBytes = driveBytes,
                ItemId = v.Item.Id,
                Name = v.Item.Name ?? "?",
                Year = v.Item.ProductionYear,
                Kind = v.Item is Series ? MediaKind.Series : MediaKind.Movie,
                LibraryId = v.Lib.Id,
                LibraryName = v.Lib.Name,
                SizeBytes = itemSize,
                DateAddedUtc = v.Item.DateCreated,
                LastPlayedUtc = st?.Last,
                PlayCount = st?.Count ?? 0,
                Genres = (v.Item.Genres ?? Array.Empty<string>()).ToList(),
                PrimaryImageTag = image is null ? null : image.DateModified.Ticks.ToString("x", CultureInfo.InvariantCulture),
                Path = v.Item.Path,
                ProviderIds = new Dictionary<string, string>(v.Item.ProviderIds ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
            };
        }).ToList();
    }

    /// <summary>Admin search for hand-picking items.</summary>
    public IReadOnlyList<CandidateItem> Search(string term, int limit = 40)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return Array.Empty<CandidateItem>();
        }

        var list = JellyfinCall.Run("search the library", () => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = VotableKinds,
            Recursive = true,
            IsVirtualItem = false,
            SearchTerm = term.Trim(),
            Limit = Math.Clamp(limit, 1, 100)
        }));
        return list.Where(i => i is Movie or Series).Select(i => new CandidateItem
        {
            ItemId = i.Id,
            Name = i.Name ?? "?",
            Year = i.ProductionYear,
            Kind = i is Series ? MediaKind.Series : MediaKind.Movie,
            Path = i.Path
        }).ToList();
    }

    /// <summary>
    /// Ids (out of <paramref name="candidateIds"/>) the user is allowed to see under their library access
    /// and parental controls. Cached for five minutes per session and user.
    /// </summary>
    public HashSet<Guid> GetVisibleIds(Guid sessionId, Guid userId, IReadOnlyCollection<Guid> candidateIds)
    {
        if (_visibleCache.TryGetValue((sessionId, userId), out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(5))
        {
            return cached.Ids;
        }

        var user = JellyfinCall.Run("look up a user", () => _userManager.GetUserById(userId));
        HashSet<Guid> ids;
        if (user is null)
        {
            ids = new HashSet<Guid>();
        }
        else
        {
            ids = JellyfinCall.Run("check which titles the user can see", () => _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                ItemIds = candidateIds.ToArray(),
                IncludeItemTypes = VotableKinds,
                Recursive = true
            }).Select(i => i.Id).ToHashSet());
        }

        _visibleCache[(sessionId, userId)] = (DateTime.UtcNow, ids);
        return ids;
    }

    /// <summary>Total size on disk of the given paths (used for the precise "space freed" of winners).</summary>
    public static long SizeOnDisk(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (var p in paths)
        {
            try
            {
                if (File.Exists(p))
                {
                    total += new FileInfo(p).Length;
                }
                else if (Directory.Exists(p))
                {
                    total += new DirectoryInfo(p).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // size is informational only
            }
        }

        return total;
    }

    private static string NormalizeId(string? id)
        => Guid.TryParse(id, out var g) ? g.ToString("N", CultureInfo.InvariantCulture) : id ?? string.Empty;

    /// <summary>Play statistics per movie/series (episodes are counted towards their series).</summary>
    internal sealed class ItemStats
    {
        public int Count { get; set; }

        public DateTime? Last { get; set; }

        public string? LastBy { get; set; }

        public HashSet<Guid> Viewers { get; } = new();

        public HashSet<Guid> PlayedEpisodes { get; } = new();
    }

    private Dictionary<Guid, ItemStats> ReadPlayStats(Dictionary<Guid, Guid> episodeToSeries)
    {
        var stats = new Dictionary<Guid, ItemStats>();
        ItemStats For(Guid id)
        {
            if (!stats.TryGetValue(id, out var st))
            {
                st = new ItemStats();
                stats[id] = st;
            }

            return st;
        }

        // Primary source: Jellyfin's own per-user data (knows who played what).
        foreach (var user in JellyfinCall.Run("list users", () => _userManager.GetUsers().ToList()))
        {
            foreach (var (id, count, last) in PlayedItemsFor(user))
            {
                bool isEpisode = episodeToSeries.TryGetValue(id, out var seriesId);
                var st = For(isEpisode ? seriesId : id);
                st.Count += count;
                st.Viewers.Add(user.Id);
                if (isEpisode)
                {
                    st.PlayedEpisodes.Add(id);
                }

                if (last is not null && (st.Last is null || last > st.Last))
                {
                    st.Last = last;
                    st.LastBy = user.Username;
                }
            }
        }

        // Optional: Playback Reporting. Take the higher count and the latest date (no user name from there).
        if (Config.UsePlaybackReporting)
        {
            var reporting = new PlaybackReportingReader(_paths.DataPath, _logger).Read();
            var merged = new Dictionary<Guid, (int Count, DateTime? Last)>();
            foreach (var (id, val) in reporting)
            {
                var key = episodeToSeries.TryGetValue(id, out var sid) ? sid : id;
                var cur = merged.GetValueOrDefault(key);
                merged[key] = (cur.Count + val.Count, Max(cur.Last, val.Last));
            }

            foreach (var (id, val) in merged)
            {
                var st = For(id);
                st.Count = Math.Max(st.Count, val.Count);
                if (val.Last is not null && (st.Last is null || val.Last > st.Last))
                {
                    st.Last = val.Last;
                    st.LastBy = null;
                }
            }
        }

        return stats;
    }

    /// <summary>Played movies/episodes of one user with play count and last played date (Jellyfin user data).</summary>
    private List<(Guid Id, int Count, DateTime? Last)> PlayedItemsFor(Jellyfin.Database.Implementations.Entities.User user)
        => JellyfinCall.Run("read play statistics for " + user.Username, () =>
        {
            var result = new List<(Guid, int, DateTime?)>();
            var played = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                Recursive = true,
                IsPlayed = true
            });
            foreach (var item in played)
            {
                var data = _userDataManager.GetUserData(user, item);
                if (data is not null)
                {
                    result.Add((item.Id, Math.Max(data.PlayCount, data.Played ? 1 : 0), data.LastPlayedDate));
                }
            }

            return result;
        });

    /// <summary>Self-check: reads a few votable titles (does not compute sizes or stats).</summary>
    public int SampleScope(int limit)
        => JellyfinCall.Run("read library items", () => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = VotableKinds,
            Recursive = true,
            IsVirtualItem = false,
            Limit = limit
        }).Count(i => i is Movie or Series));

    /// <summary>Self-check: reads play statistics for the first user only.</summary>
    public (string User, int Played) SamplePlayStats()
    {
        var user = JellyfinCall.Run("list users", () => _userManager.GetUsers().FirstOrDefault());
        if (user is null)
        {
            return ("(no users)", 0);
        }

        return (user.Username, PlayedItemsFor(user).Count);
    }

    private static DateTime? Max(DateTime? a, DateTime? b)
        => a is null ? b : b is null ? a : (a > b ? a : b);
}
