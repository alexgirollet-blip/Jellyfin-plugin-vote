using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaVote.Configuration;
using Jellyfin.Plugin.MediaVote.Core;

namespace Jellyfin.Plugin.MediaVote.Integrations.Arr;

/// <summary>An entry matched in an external app.</summary>
public sealed record ArrMatch(int Id, string Title, int? Year, bool? Monitored, string? Path);

/// <summary>Outcome of a lookup: a match, "not in this app", or "cannot match (missing ids)".</summary>
public sealed record ArrLookup(ArrMatch? Match, string? CannotMatchReason);

/// <summary>Shared plan/execute logic for Radarr and Sonarr (both expose the v3 API).</summary>
public abstract class ArrServiceBase : IExternalService
{
    protected ArrServiceBase(ArrHttp http, ArrAction action)
    {
        Http = http;
        Action = action;
    }

    public abstract string Name { get; }

    public bool HasConnection => Http.IsConfigured;

    public ArrAction Action { get; }

    public string ActionName => Action switch
    {
        ArrAction.Unmonitor => "Unmonitor",
        ArrAction.RemoveAndExclude => "Remove and add import exclusion",
        _ => "Do nothing"
    };

    public bool IsNoOp => Action == ArrAction.Nothing;

    protected ArrHttp Http { get; }

    public abstract bool AppliesTo(MediaKind kind);

    /// <summary>Read-only lookup (GET only).</summary>
    public abstract Task<ArrLookup> LookupAsync(MediaItemSnapshot item, CancellationToken ct);

    /// <summary>GET system/status; returns "AppName version".</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        using var doc = await Http.GetAsync("/api/v3/system/status", ct).ConfigureAwait(false)
            ?? throw new ArrException($"{Name}: /api/v3/system/status not found. Check the base URL.");
        var root = doc.RootElement;
        return $"{Str(root, "appName") ?? Name} {Str(root, "version")}".Trim();
    }

    public async Task<ExternalStepResult> PlanAsync(MediaItemSnapshot item, CancellationToken cancellationToken)
    {
        var r = NewResult();
        var lookup = await LookupAsync(item, cancellationToken).ConfigureAwait(false);
        if (lookup.CannotMatchReason is not null)
        {
            r.Status = IsNoOp ? ExternalStepStatus.Skipped : ExternalStepStatus.Failed;
            r.Message = lookup.CannotMatchReason;
            return r;
        }

        if (lookup.Match is null)
        {
            r.Status = ExternalStepStatus.NotFound;
            r.Message = $"Not in {Name}.";
            return r;
        }

        Fill(r, lookup.Match);
        if (IsNoOp)
        {
            r.Status = ExternalStepStatus.Skipped;
            r.Message = $"Matched {Describe(lookup.Match)}; admin chose \"Do nothing\".";
        }
        else
        {
            r.Status = ExternalStepStatus.WouldDo;
            r.Message = Action == ArrAction.Unmonitor
                ? $"Would unmonitor {Describe(lookup.Match)}."
                : $"Would remove {Describe(lookup.Match)} from {Name} and add an import exclusion ({Name} would not delete files).";
        }

        return r;
    }

    public async Task<ExternalStepResult> ExecuteAsync(MediaItemSnapshot item, CancellationToken cancellationToken)
    {
        var r = NewResult();
        if (IsNoOp)
        {
            r.Status = ExternalStepStatus.Skipped;
            return r;
        }

        var lookup = await LookupAsync(item, cancellationToken).ConfigureAwait(false);
        if (lookup.CannotMatchReason is not null)
        {
            r.Status = ExternalStepStatus.Failed;
            r.Message = lookup.CannotMatchReason;
            return r;
        }

        if (lookup.Match is null)
        {
            r.Status = ExternalStepStatus.NotFound;
            r.Message = $"Not in {Name}; nothing to do.";
            return r;
        }

        Fill(r, lookup.Match);
        if (Action == ArrAction.Unmonitor)
        {
            await UnmonitorAsync(lookup.Match.Id, cancellationToken).ConfigureAwait(false);
            r.Message = $"Unmonitored {Describe(lookup.Match)}.";
        }
        else
        {
            await RemoveAndExcludeAsync(lookup.Match.Id, cancellationToken).ConfigureAwait(false);
            r.Message = $"Removed {Describe(lookup.Match)} from {Name} with import exclusion (files left for Jellyfin to delete).";
        }

        r.Status = ExternalStepStatus.Done;
        return r;
    }

    protected abstract Task UnmonitorAsync(int id, CancellationToken ct);

    protected abstract Task RemoveAndExcludeAsync(int id, CancellationToken ct);

    protected static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    protected static int? Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    protected static bool? Bool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;

    protected static ArrMatch ToMatch(JsonElement e)
        => new(Int(e, "id") ?? 0, Str(e, "title") ?? "?", Int(e, "year"), Bool(e, "monitored"), Str(e, "path"));

    private static string Describe(ArrMatch m)
        => m.Year is null ? $"\"{m.Title}\"" : string.Create(CultureInfo.InvariantCulture, $"\"{m.Title} ({m.Year})\"");

    private void Fill(ExternalStepResult r, ArrMatch m)
    {
        r.MatchedTitle = m.Year is null ? m.Title : string.Create(CultureInfo.InvariantCulture, $"{m.Title} ({m.Year})");
        r.ExternalId = m.Id.ToString(CultureInfo.InvariantCulture);
    }

    private ExternalStepResult NewResult() => new() { Service = Name, Action = ActionName };
}

/// <summary>
/// Radarr v3 API:
/// GET /api/v3/movie?tmdbId= ; GET /api/v3/movie (IMDb fallback) ;
/// PUT /api/v3/movie/editor {movieIds, monitored:false} ;
/// DELETE /api/v3/movie/{id}?deleteFiles=false&amp;addImportExclusion=true ; GET /api/v3/system/status.
/// </summary>
public sealed class RadarrService : ArrServiceBase
{
    public RadarrService(ArrHttp http, ArrAction action)
        : base(http, action)
    {
    }

    public override string Name => "Radarr";

    public override bool AppliesTo(MediaKind kind) => kind == MediaKind.Movie;

    public override async Task<ArrLookup> LookupAsync(MediaItemSnapshot item, CancellationToken ct)
    {
        var tmdb = item.ProviderId("Tmdb");
        var imdb = item.ProviderId("Imdb");
        if (tmdb is not null && int.TryParse(tmdb, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
        {
            using var doc = await Http.GetAsync("/api/v3/movie?tmdbId=" + tmdbId.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            var first = doc?.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().FirstOrDefault() : default;
            return new ArrLookup(first.ValueKind == JsonValueKind.Object ? ToMatch(first) : null, null);
        }

        if (imdb is not null)
        {
            using var doc = await Http.GetAsync("/api/v3/movie", ct).ConfigureAwait(false);
            if (doc?.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in doc.RootElement.EnumerateArray())
                {
                    if (string.Equals(Str(m, "imdbId"), imdb, StringComparison.OrdinalIgnoreCase))
                    {
                        return new ArrLookup(ToMatch(m), null);
                    }
                }
            }

            return new ArrLookup(null, null);
        }

        return new ArrLookup(null, "Cannot match in Radarr: the movie has no TMDb or IMDb id in Jellyfin. Refresh its metadata.");
    }

    protected override Task UnmonitorAsync(int id, CancellationToken ct)
        => Http.PutJsonAsync("/api/v3/movie/editor", new { movieIds = new[] { id }, monitored = false }, ct);

    protected override Task RemoveAndExcludeAsync(int id, CancellationToken ct)
        => Http.DeleteAsync("/api/v3/movie/" + id.ToString(CultureInfo.InvariantCulture) + "?deleteFiles=false&addImportExclusion=true", ct);
}

/// <summary>
/// Sonarr v3 API (also used by Sonarr v4):
/// GET /api/v3/series?tvdbId= ; GET /api/v3/series (TMDb/IMDb fallback) ;
/// PUT /api/v3/series/editor {seriesIds, monitored:false, monitorNewItems:"none"} ;
/// DELETE /api/v3/series/{id}?deleteFiles=false&amp;addImportListExclusion=true ; GET /api/v3/system/status.
/// </summary>
public sealed class SonarrService : ArrServiceBase
{
    public SonarrService(ArrHttp http, ArrAction action)
        : base(http, action)
    {
    }

    public override string Name => "Sonarr";

    public override bool AppliesTo(MediaKind kind) => kind == MediaKind.Series;

    public override async Task<ArrLookup> LookupAsync(MediaItemSnapshot item, CancellationToken ct)
    {
        var tvdb = item.ProviderId("Tvdb");
        var tmdb = item.ProviderId("Tmdb");
        var imdb = item.ProviderId("Imdb");
        if (tvdb is not null && int.TryParse(tvdb, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tvdbId))
        {
            using var doc = await Http.GetAsync("/api/v3/series?tvdbId=" + tvdbId.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            var first = doc?.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().FirstOrDefault() : default;
            return new ArrLookup(first.ValueKind == JsonValueKind.Object ? ToMatch(first) : null, null);
        }

        if (tmdb is not null || imdb is not null)
        {
            using var doc = await Http.GetAsync("/api/v3/series", ct).ConfigureAwait(false);
            if (doc?.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in doc.RootElement.EnumerateArray())
                {
                    var sTmdb = Int(s, "tmdbId");
                    if ((tmdb is not null && sTmdb is not null && sTmdb.Value.ToString(CultureInfo.InvariantCulture) == tmdb)
                        || (imdb is not null && string.Equals(Str(s, "imdbId"), imdb, StringComparison.OrdinalIgnoreCase)))
                    {
                        return new ArrLookup(ToMatch(s), null);
                    }
                }
            }

            return new ArrLookup(null, null);
        }

        return new ArrLookup(null, "Cannot match in Sonarr: the series has no TVDB, TMDb or IMDb id in Jellyfin. Refresh its metadata.");
    }

    protected override Task UnmonitorAsync(int id, CancellationToken ct)
        => Http.PutJsonAsync("/api/v3/series/editor", new { seriesIds = new[] { id }, monitored = false, monitorNewItems = "none" }, ct);

    protected override Task RemoveAndExcludeAsync(int id, CancellationToken ct)
        => Http.DeleteAsync("/api/v3/series/" + id.ToString(CultureInfo.InvariantCulture) + "?deleteFiles=false&addImportListExclusion=true", ct);
}

/// <summary>
/// Jellyseerr API v1:
/// GET /api/v1/movie/{tmdbId} or /api/v1/tv/{tmdbId} → mediaInfo.id ;
/// DELETE /api/v1/media/{mediaId} (removes the media record and its requests; needs MANAGE_REQUESTS) ;
/// GET /api/v1/auth/me for the connection test.
/// </summary>
public sealed class JellyseerrService : IExternalService
{
    private readonly ArrHttp _http;

    public JellyseerrService(ArrHttp http, SeerrAction action)
    {
        _http = http;
        Action = action;
    }

    public string Name => "Jellyseerr";

    public bool HasConnection => _http.IsConfigured;

    public SeerrAction Action { get; }

    public string ActionName => Action == SeerrAction.RemoveMediaRecord ? "Remove media/request record" : "Do nothing";

    public bool IsNoOp => Action == SeerrAction.Nothing;

    public bool AppliesTo(MediaKind kind) => true;

    public async Task<string> TestAsync(CancellationToken ct)
    {
        using var me = await _http.GetAsync("/api/v1/auth/me", ct).ConfigureAwait(false)
            ?? throw new ArrException("Jellyseerr: /api/v1/auth/me not found. Check the base URL.");
        string version = string.Empty;
        using (var status = await _http.GetAsync("/api/v1/status", ct).ConfigureAwait(false))
        {
            if (status is not null && status.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
            {
                version = v.GetString() ?? string.Empty;
            }
        }

        return ("Jellyseerr " + version).Trim();
    }

    /// <summary>Read-only lookup of the Jellyseerr media record id.</summary>
    public async Task<(int? MediaId, string? Title, string? CannotMatch)> LookupAsync(MediaItemSnapshot item, CancellationToken ct)
    {
        var tmdb = item.ProviderId("Tmdb");
        if (tmdb is null || !int.TryParse(tmdb, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
        {
            return (null, null, "Cannot match in Jellyseerr: no TMDb id in Jellyfin. Refresh the item's metadata.");
        }

        string path = (item.Kind == MediaKind.Movie ? "/api/v1/movie/" : "/api/v1/tv/") + tmdbId.ToString(CultureInfo.InvariantCulture);
        using var doc = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (doc is null)
        {
            return (null, null, null);
        }

        var root = doc.RootElement;
        string? title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()
            : root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        if (root.TryGetProperty("mediaInfo", out var mi) && mi.ValueKind == JsonValueKind.Object
            && mi.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
        {
            return (id.GetInt32(), title, null);
        }

        return (null, title, null);
    }

    public async Task<ExternalStepResult> PlanAsync(MediaItemSnapshot item, CancellationToken cancellationToken)
    {
        var r = new ExternalStepResult { Service = Name, Action = ActionName };
        var (mediaId, title, cannot) = await LookupAsync(item, cancellationToken).ConfigureAwait(false);
        r.MatchedTitle = title;
        if (cannot is not null)
        {
            r.Status = IsNoOp ? ExternalStepStatus.Skipped : ExternalStepStatus.Failed;
            r.Message = cannot;
        }
        else if (mediaId is null)
        {
            r.Status = ExternalStepStatus.NotFound;
            r.Message = "No Jellyseerr media/request record.";
        }
        else
        {
            r.ExternalId = mediaId.Value.ToString(CultureInfo.InvariantCulture);
            r.Status = IsNoOp ? ExternalStepStatus.Skipped : ExternalStepStatus.WouldDo;
            r.Message = IsNoOp
                ? "Has a Jellyseerr record; admin chose \"Do nothing\"."
                : "Would delete Jellyseerr media record " + r.ExternalId + " so it can be requested again.";
        }

        return r;
    }

    public async Task<ExternalStepResult> ExecuteAsync(MediaItemSnapshot item, CancellationToken cancellationToken)
    {
        var r = new ExternalStepResult { Service = Name, Action = ActionName };
        if (IsNoOp)
        {
            r.Status = ExternalStepStatus.Skipped;
            return r;
        }

        var (mediaId, title, cannot) = await LookupAsync(item, cancellationToken).ConfigureAwait(false);
        r.MatchedTitle = title;
        if (cannot is not null)
        {
            r.Status = ExternalStepStatus.Failed;
            r.Message = cannot;
            return r;
        }

        if (mediaId is null)
        {
            r.Status = ExternalStepStatus.NotFound;
            r.Message = "No Jellyseerr record; nothing to do.";
            return r;
        }

        r.ExternalId = mediaId.Value.ToString(CultureInfo.InvariantCulture);
        await _http.DeleteAsync("/api/v1/media/" + r.ExternalId, cancellationToken).ConfigureAwait(false);
        r.Status = ExternalStepStatus.Done;
        r.Message = "Deleted Jellyseerr media record " + r.ExternalId + ".";
        return r;
    }
}
