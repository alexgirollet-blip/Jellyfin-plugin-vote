using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>Which app an instance is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArrKind
{
    Radarr,
    Sonarr,
    Jellyseerr
}

/// <summary>A movie (Radarr) or series (Sonarr) as listed by one instance. Read from GET /api/v3/movie or /api/v3/series.</summary>
public sealed class ArrEntry
{
    public string Instance { get; set; } = string.Empty;

    public ArrKind Kind { get; set; }

    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? Path { get; set; }

    public int? TmdbId { get; set; }

    public int? TvdbId { get; set; }

    public string? ImdbId { get; set; }

    public long SizeOnDisk { get; set; }

    public string Describe()
        => (Year is null ? Title : string.Create(CultureInfo.InvariantCulture, $"{Title} ({Year})")) + " in " + Instance + string.Create(CultureInfo.InvariantCulture, $" (id {Id})");
}

/// <summary>Result of matching one Jellyfin title against the Radarr/Sonarr libraries.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArrMatchState
{
    /// <summary>Exactly one Radarr/Sonarr entry matches: it can be removed automatically.</summary>
    Matched,

    /// <summary>No configured instance manages this title: it can only be removed by hand.</summary>
    NotManaged,

    /// <summary>More than one entry could be this title, or its ids disagree: treated as not matched.</summary>
    Ambiguous,

    /// <summary>No Radarr (movies) or Sonarr (series) instance is configured.</summary>
    NotConfigured,

    /// <summary>At least one instance for this kind could not be reached, so the answer is unknown.</summary>
    Unreachable
}

/// <summary>Outcome of <see cref="ArrMatcher.Match"/>.</summary>
public sealed record ArrMatchResult(ArrMatchState State, ArrEntry? Entry, string Reason)
{
    public bool IsMatched => State == ArrMatchState.Matched && Entry is not null;
}

/// <summary>What the matcher needs to know about a Jellyfin title.</summary>
public sealed record MatchKey(MediaKind Kind, IReadOnlyDictionary<string, string> ProviderIds, string Name, int? Year, string? Path)
{
    public static MatchKey From(CandidateItem c) => new(c.Kind, c.ProviderIds, c.Name, c.Year, c.Path);

    public static MatchKey From(MediaItemSnapshot s) => new(s.Kind, s.ProviderIds, s.Name, s.Year, s.DeletePaths.FirstOrDefault());
}

/// <summary>
/// Matches Jellyfin titles to Radarr/Sonarr entries. Pure logic, no I/O.
/// <list type="number">
/// <item>Provider ids first: movies by TMDb and IMDb, series by TVDB, TMDb and IMDb. Every id the title has is used, and
///   all of them must point at the same single entry; two different entries (in one instance or across instances) is
///   <see cref="ArrMatchState.Ambiguous"/>, never a guess.</item>
/// <item>Only when the title has none of those ids: title + year + folder name must all agree, with exactly one entry.
///   Folder names are compared rather than full paths because Jellyfin and Sonarr/Radarr usually see different mount
///   points (for example /LM1/TV Shows/X in Jellyfin and /tv/X in Sonarr).</item>
/// </list>
/// </summary>
public static class ArrMatcher
{
    public static ArrKind KindFor(MediaKind kind) => kind == MediaKind.Series ? ArrKind.Sonarr : ArrKind.Radarr;

    /// <param name="key">The Jellyfin title.</param>
    /// <param name="library">Entries from every reachable instance, plus which instances are configured / unreachable.</param>
    public static ArrMatchResult Match(MatchKey key, ArrLibrary library)
    {
        var kind = KindFor(key.Kind);
        string app = kind.ToString();
        if (library.ConfiguredCount(kind) == 0)
        {
            return new(ArrMatchState.NotConfigured, null, $"No {app} instance is configured, so this {Noun(key.Kind)} cannot be removed automatically.");
        }

        var unreachable = library.Unreachable(kind);
        if (unreachable.Count > 0)
        {
            return new(ArrMatchState.Unreachable, null, $"{string.Join(", ", unreachable)} could not be reached, so Media Vote cannot tell whether it manages this {Noun(key.Kind)}.");
        }

        int? tmdb = IntId(key.ProviderIds, "Tmdb");
        int? tvdb = key.Kind == MediaKind.Series ? IntId(key.ProviderIds, "Tvdb") : null;
        string? imdb = ImdbId(key.ProviderIds);

        if (tmdb is not null || tvdb is not null || imdb is not null)
        {
            var distinct = library.ById(kind, tvdb, tmdb, imdb);
            if (distinct.Count == 1)
            {
                return new(ArrMatchState.Matched, distinct[0], "Matched by provider id: " + distinct[0].Describe() + ".");
            }

            if (distinct.Count > 1)
            {
                return new(ArrMatchState.Ambiguous, null, "Ambiguous: " + string.Join("; ", distinct.Select(e => e.Describe())) + " all match its ids. Not removed automatically.");
            }

            return new(ArrMatchState.NotManaged, null, $"Not managed by {app} (no entry with its {IdList(tvdb, tmdb, imdb)}).");
        }

        // Fallback only when Jellyfin has no usable ids at all.
        string title = NormalizeTitle(key.Name);
        string? folder = FolderName(key.Path);
        if (title.Length == 0 || key.Year is null || folder is null)
        {
            return new(ArrMatchState.NotManaged, null, $"Cannot match in {app}: Jellyfin has no TMDb/TVDB/IMDb id for it, and the title, year or folder needed for the fallback is missing. Refresh its metadata.");
        }

        var byName = library.ByTitle(kind, title).Where(e => e.Year == key.Year && FolderName(e.Path) == folder).ToList();
        if (byName.Count == 1)
        {
            return new(ArrMatchState.Matched, byName[0], "Matched by title, year and folder (no provider ids in Jellyfin): " + byName[0].Describe() + ".");
        }

        if (byName.Count > 1)
        {
            return new(ArrMatchState.Ambiguous, null, "Ambiguous: several entries share its title, year and folder name. Not removed automatically.");
        }

        return new(ArrMatchState.NotManaged, null, $"Not managed by {app} (no provider ids in Jellyfin, and no entry with the same title, year and folder).");
    }

    /// <summary>Lower-case letters and digits only, leading "the" removed, so "The Office (US)" ≈ "office us".</summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(title.Length);
        foreach (var ch in title.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '_' || ch == '.')
            {
                sb.Append(' ');
            }
        }

        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0] == "the")
        {
            words.RemoveAt(0);
        }

        return string.Join(' ', words);
    }

    /// <summary>The last folder (or file) name of a path, normalised like a title; null when there is no path.</summary>
    public static string? FolderName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        var name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        var n = NormalizeTitle(name);
        return n.Length == 0 ? null : n;
    }

    private static int? IntId(IReadOnlyDictionary<string, string> ids, string name)
    {
        foreach (var (k, v) in ids)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(v?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i > 0)
            {
                return i;
            }
        }

        return null;
    }

    private static string? ImdbId(IReadOnlyDictionary<string, string> ids)
    {
        foreach (var (k, v) in ids)
        {
            if (string.Equals(k, "Imdb", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v) && v.Trim().StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                return v.Trim();
            }
        }

        return null;
    }

    private static string IdList(int? tvdb, int? tmdb, string? imdb)
    {
        var parts = new List<string>();
        if (tvdb is not null)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"TVDB id {tvdb}"));
        }

        if (tmdb is not null)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"TMDb id {tmdb}"));
        }

        if (imdb is not null)
        {
            parts.Add("IMDb id " + imdb);
        }

        return string.Join(" or ", parts);
    }

    private static string Noun(MediaKind kind) => kind == MediaKind.Series ? "series" : "movie";
}

/// <summary>
/// Everything read from the configured Radarr/Sonarr instances at one moment, indexed for matching.
/// Built from read-only GET calls only.
/// </summary>
public sealed class ArrLibrary
{
    private readonly Dictionary<ArrKind, int> _configured;
    private readonly Dictionary<ArrKind, List<string>> _unreachable;
    private readonly Dictionary<(ArrKind, int), List<ArrEntry>> _tmdb = new();
    private readonly Dictionary<(ArrKind, int), List<ArrEntry>> _tvdb = new();
    private readonly Dictionary<(ArrKind, string), List<ArrEntry>> _imdb = new();
    private readonly Dictionary<(ArrKind, string), List<ArrEntry>> _title = new();

    public ArrLibrary(IEnumerable<ArrEntry> entries, IReadOnlyDictionary<ArrKind, int> configured, IReadOnlyDictionary<ArrKind, List<string>>? unreachable = null)
    {
        Entries = entries.ToList();
        _configured = configured.ToDictionary(p => p.Key, p => p.Value);
        _unreachable = (unreachable ?? new Dictionary<ArrKind, List<string>>()).ToDictionary(p => p.Key, p => p.Value.ToList());
        foreach (var e in Entries)
        {
            if (e.TmdbId is > 0)
            {
                Add(_tmdb, (e.Kind, e.TmdbId.Value), e);
            }

            if (e.TvdbId is > 0)
            {
                Add(_tvdb, (e.Kind, e.TvdbId.Value), e);
            }

            if (!string.IsNullOrWhiteSpace(e.ImdbId))
            {
                Add(_imdb, (e.Kind, e.ImdbId.Trim().ToLowerInvariant()), e);
            }

            var t = ArrMatcher.NormalizeTitle(e.Title);
            if (t.Length > 0)
            {
                Add(_title, (e.Kind, t), e);
            }
        }
    }

    /// <summary>An empty library with nothing configured.</summary>
    public static ArrLibrary Empty { get; } = new(Array.Empty<ArrEntry>(), new Dictionary<ArrKind, int>());

    public IReadOnlyList<ArrEntry> Entries { get; }

    /// <summary>Per-instance read errors, for the status page ("instance name: message").</summary>
    public List<string> Errors { get; } = new();

    public int ConfiguredCount(ArrKind kind) => _configured.TryGetValue(kind, out var n) ? n : 0;

    public IReadOnlyList<string> Unreachable(ArrKind kind) => _unreachable.TryGetValue(kind, out var l) ? l : (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>All distinct entries of <paramref name="kind"/> that carry any of the given ids.</summary>
    public List<ArrEntry> ById(ArrKind kind, int? tvdb, int? tmdb, string? imdb)
    {
        var hits = new List<ArrEntry>();
        if (tvdb is not null && _tvdb.TryGetValue((kind, tvdb.Value), out var a))
        {
            hits.AddRange(a);
        }

        if (tmdb is not null && _tmdb.TryGetValue((kind, tmdb.Value), out var b))
        {
            hits.AddRange(b);
        }

        if (imdb is not null && _imdb.TryGetValue((kind, imdb.ToLowerInvariant()), out var c))
        {
            hits.AddRange(c);
        }

        return hits.Distinct().ToList();
    }

    public IReadOnlyList<ArrEntry> ByTitle(ArrKind kind, string normalizedTitle)
        => _title.TryGetValue((kind, normalizedTitle), out var l) ? l : (IReadOnlyList<ArrEntry>)Array.Empty<ArrEntry>();

    private static void Add<TKey>(Dictionary<TKey, List<ArrEntry>> map, TKey key, ArrEntry e)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<ArrEntry>();
            map[key] = list;
        }

        list.Add(e);
    }
}
