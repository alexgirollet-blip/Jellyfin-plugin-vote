using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaVote.Configuration;
using Jellyfin.Plugin.MediaVote.Core;

namespace Jellyfin.Plugin.MediaVote.Integrations.Arr;

/// <summary>A root folder as reported by Sonarr/Radarr (GET /api/v3/rootfolder).</summary>
public sealed record ArrRootFolder(string Path, long? FreeSpace, long? TotalSpace);

/// <summary>
/// One Radarr or Sonarr instance (API v3: Radarr v3–v5, Sonarr v3–v4).
/// <list type="bullet">
/// <item>GET /api/v3/movie and /api/v3/series: the whole library, for matching.</item>
/// <item>GET /api/v3/movie/{id}, /api/v3/series/{id}: re-check and verification (404 = gone).</item>
/// <item>DELETE /api/v3/movie/{id}?deleteFiles=true&amp;addImportExclusion=…</item>
/// <item>DELETE /api/v3/series/{id}?deleteFiles=true&amp;addImportListExclusion=…</item>
/// <item>GET /api/v3/system/status, /api/v3/rootfolder and /api/v3/diskspace for the Status tab.</item>
/// </list>
/// Verified against the Sonarr (v3 branch, v4 main) and Radarr (v5, develop) source: DELETE answers 200 as soon as
/// the database row is gone; the files are deleted (or moved to the recycle bin) afterwards in the background, and a
/// failure there is only logged by Sonarr/Radarr. Media Vote therefore verifies the entry is gone AND later checks the
/// files are gone from disk before it lets Jellyfin forget the title.
/// </summary>
public sealed class ArrClient
{
    private readonly ArrHttp _http;
    private readonly bool _addExclusion;

    public ArrClient(ArrHttp http, ArrKind kind, string name, bool addExclusion)
    {
        if (kind == ArrKind.Jellyseerr)
        {
            throw new ArgumentException("Use JellyseerrClient for Jellyseerr.", nameof(kind));
        }

        _http = http;
        Kind = kind;
        Name = name;
        _addExclusion = addExclusion;
    }

    public ArrKind Kind { get; }

    public string Name { get; }

    private string Resource => Kind == ArrKind.Radarr ? "movie" : "series";

    public async Task<List<ArrEntry>> ListAsync(CancellationToken ct)
    {
        using var doc = await _http.GetAsync("/api/v3/" + Resource, ct).ConfigureAwait(false)
            ?? throw new ArrException($"{Name}: /api/v3/{Resource} not found. Check the base URL (include any URL base such as /sonarr).");
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArrException($"{Name}: unexpected answer from /api/v3/{Resource}.");
        }

        return doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(ToEntry).Where(e => e.Id > 0).ToList();
    }

    /// <summary>Null when the entry no longer exists (HTTP 404).</summary>
    public async Task<ArrEntry?> GetAsync(int id, CancellationToken ct)
    {
        using var doc = await _http.GetAsync("/api/v3/" + Resource + "/" + id.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        return doc is null || doc.RootElement.ValueKind != JsonValueKind.Object ? null : ToEntry(doc.RootElement);
    }

    /// <summary>Deletes the entry AND its files in this instance. Throws <see cref="ArrException"/> (with the instance's message) on failure.</summary>
    public Task DeleteAsync(int id, CancellationToken ct)
    {
        string exclusion = Kind == ArrKind.Radarr ? "addImportExclusion" : "addImportListExclusion";
        return _http.DeleteAsync(
            "/api/v3/" + Resource + "/" + id.ToString(CultureInfo.InvariantCulture) + "?deleteFiles=true&" + exclusion + "=" + (_addExclusion ? "true" : "false"),
            ct);
    }

    /// <summary>GET system/status; returns "AppName version".</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        using var doc = await _http.GetAsync("/api/v3/system/status", ct).ConfigureAwait(false)
            ?? throw new ArrException($"{Name}: /api/v3/system/status not found. Check the base URL.");
        var root = doc.RootElement;
        var app = Str(root, "appName") ?? Kind.ToString();
        if (!string.Equals(app, Kind.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArrException($"This URL answers as {app}, not {Kind}. Check the base URL and the instance type.") { Answered = true };
        }

        return $"{app} {Str(root, "version")}".Trim();
    }

    /// <summary>
    /// Root folders with free space. /rootfolder has no total size, so the total comes from /diskspace (the disk whose
    /// path is the longest prefix of the root folder).
    /// </summary>
    public async Task<List<ArrRootFolder>> RootFoldersAsync(CancellationToken ct)
    {
        var disks = new List<(string Path, long? Total)>();
        using (var dd = await _http.GetAsync("/api/v3/diskspace", ct).ConfigureAwait(false))
        {
            if (dd is not null && dd.RootElement.ValueKind == JsonValueKind.Array)
            {
                disks.AddRange(dd.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => (Str(e, "path") ?? string.Empty, Long(e, "totalSpace"))));
            }
        }

        using var doc = await _http.GetAsync("/api/v3/rootfolder", ct).ConfigureAwait(false);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new List<ArrRootFolder>();
        }

        return doc.RootElement.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .Select(e =>
            {
                var path = Str(e, "path") ?? "?";
                var disk = disks.Where(d => d.Path.Length > 0 && path.StartsWith(d.Path, StringComparison.Ordinal)).OrderByDescending(d => d.Path.Length).FirstOrDefault();
                return new ArrRootFolder(path, Long(e, "freeSpace"), disk.Total);
            })
            .ToList();
    }

    internal static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static int? Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    internal static long? Long(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;

    private ArrEntry ToEntry(JsonElement e)
    {
        long size = Long(e, "sizeOnDisk") ?? 0;
        if (size == 0 && e.TryGetProperty("statistics", out var stats))
        {
            size = Long(stats, "sizeOnDisk") ?? 0;
        }

        return new ArrEntry
        {
            Instance = Name,
            Kind = Kind,
            Id = Int(e, "id") ?? 0,
            Title = Str(e, "title") ?? "?",
            Year = Int(e, "year") is > 0 and var y ? y : null,
            Path = Str(e, "path"),
            TmdbId = Int(e, "tmdbId") is > 0 and var tm ? tm : null,
            TvdbId = Int(e, "tvdbId") is > 0 and var tv ? tv : null,
            ImdbId = string.IsNullOrWhiteSpace(Str(e, "imdbId")) ? null : Str(e, "imdbId"),
            SizeOnDisk = size
        };
    }
}

/// <summary>
/// One Jellyseerr instance (API v1):
/// GET /api/v1/movie/{tmdbId} or /api/v1/tv/{tmdbId} → mediaInfo.id ;
/// DELETE /api/v1/media/{mediaId} (removes the media record and its requests so the title can be requested again) ;
/// GET /api/v1/auth/me and /api/v1/status for the connection test.
/// </summary>
public sealed class JellyseerrClient
{
    private readonly ArrHttp _http;

    public JellyseerrClient(ArrHttp http, string name, SeerrAction action)
    {
        _http = http;
        Name = name;
        Action = action;
    }

    public string Name { get; }

    public SeerrAction Action { get; }

    public string ActionName => Action == SeerrAction.RemoveMediaRecord ? "Remove media/request record" : "Do nothing";

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

    /// <summary>Read-only lookup of the media record id.</summary>
    public async Task<(int? MediaId, string? Title, string? CannotMatch)> LookupAsync(MatchKey key, CancellationToken ct)
    {
        string? tmdb = key.ProviderIds.FirstOrDefault(p => string.Equals(p.Key, "Tmdb", StringComparison.OrdinalIgnoreCase)).Value;
        if (tmdb is null || !int.TryParse(tmdb.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId) || tmdbId <= 0)
        {
            return (null, null, "Cannot match in Jellyseerr: no TMDb id in Jellyfin. Refresh the item's metadata.");
        }

        string path = (key.Kind == MediaKind.Movie ? "/api/v1/movie/" : "/api/v1/tv/") + tmdbId.ToString(CultureInfo.InvariantCulture);
        using var doc = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (doc is null)
        {
            return (null, null, null);
        }

        var root = doc.RootElement;
        string? title = ArrClient.Str(root, "title") ?? ArrClient.Str(root, "name");
        if (root.TryGetProperty("mediaInfo", out var mi) && ArrClient.Int(mi, "id") is int id)
        {
            return (id, title, null);
        }

        return (null, title, null);
    }

    public async Task<ExternalStepResult> RunAsync(MatchKey key, bool execute, CancellationToken ct)
    {
        var r = new ExternalStepResult { Service = Name, Action = ActionName };
        if (Action == SeerrAction.Nothing)
        {
            r.Status = ExternalStepStatus.Skipped;
            r.Message = "Admin chose \"Do nothing\".";
            return r;
        }

        var (mediaId, title, cannot) = await LookupAsync(key, ct).ConfigureAwait(false);
        r.MatchedTitle = title;
        if (cannot is not null)
        {
            // No TMDb id: there is nothing Jellyseerr could match, so there is nothing to clean up there.
            r.Status = ExternalStepStatus.NotFound;
            r.Message = cannot;
            return r;
        }

        if (mediaId is null)
        {
            r.Status = ExternalStepStatus.NotFound;
            r.Message = "No Jellyseerr media/request record; nothing to do.";
            return r;
        }

        r.ExternalId = mediaId.Value.ToString(CultureInfo.InvariantCulture);
        if (!execute)
        {
            r.Status = ExternalStepStatus.WouldDo;
            r.Message = "Would delete Jellyseerr media record " + r.ExternalId + " so it can be requested again.";
            return r;
        }

        await _http.DeleteAsync("/api/v1/media/" + r.ExternalId, ct).ConfigureAwait(false);
        r.Status = ExternalStepStatus.Done;
        r.Message = "Deleted Jellyseerr media record " + r.ExternalId + ".";
        return r;
    }
}

/// <summary><see cref="IRemovalBackend"/> over every configured Radarr, Sonarr and Jellyseerr instance.</summary>
public sealed class ArrRemovalBackend : IRemovalBackend
{
    private readonly IReadOnlyList<ArrClient> _arr;
    private readonly IReadOnlyList<JellyseerrClient> _seerr;

    public ArrRemovalBackend(IReadOnlyList<ArrClient> arr, IReadOnlyList<JellyseerrClient> seerr, bool readOnly)
    {
        _arr = arr;
        _seerr = seerr;
        ReadOnly = readOnly;
    }

    public bool ReadOnly { get; }

    public IReadOnlyList<ArrClient> ArrClients => _arr;

    public IReadOnlyList<JellyseerrClient> SeerrClients => _seerr;

    public bool HasSeerrStep => _seerr.Any(s => s.Action != SeerrAction.Nothing);

    public async Task<ArrLibrary> LoadAsync(CancellationToken cancellationToken)
    {
        var entries = new List<ArrEntry>();
        var configured = new Dictionary<ArrKind, int>();
        var unreachable = new Dictionary<ArrKind, List<string>>();
        var errors = new List<string>();
        foreach (var client in _arr)
        {
            configured[client.Kind] = configured.GetValueOrDefault(client.Kind) + 1;
            try
            {
                entries.AddRange(await client.ListAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (ArrException ex)
            {
                if (!unreachable.TryGetValue(client.Kind, out var list))
                {
                    list = new List<string>();
                    unreachable[client.Kind] = list;
                }

                list.Add(client.Name);
                errors.Add(client.Name + ": " + ex.Message);
            }
        }

        var lib = new ArrLibrary(entries, configured, unreachable);
        lib.Errors.AddRange(errors);
        return lib;
    }

    public Task<ArrEntry?> RefetchAsync(ArrEntry entry, CancellationToken cancellationToken)
        => Client(entry).GetAsync(entry.Id, cancellationToken);

    public Task DeleteAsync(ArrEntry entry, CancellationToken cancellationToken)
    {
        if (ReadOnly)
        {
            throw new ReadOnlyViolationException("Delete blocked: this is a read-only (notify-only or dry-run) operation.");
        }

        return Client(entry).DeleteAsync(entry.Id, cancellationToken);
    }

    public async Task<List<ExternalStepResult>> SeerrAsync(MatchKey key, bool execute, CancellationToken cancellationToken)
    {
        var results = new List<ExternalStepResult>();
        foreach (var s in _seerr)
        {
            try
            {
                results.Add(await s.RunAsync(key, execute && !ReadOnly, cancellationToken).ConfigureAwait(false));
            }
            catch (ArrException ex)
            {
                results.Add(new ExternalStepResult { Service = s.Name, Action = s.ActionName, Status = ExternalStepStatus.Failed, Message = ex.Message });
            }
        }

        return results;
    }

    private ArrClient Client(ArrEntry entry)
        => _arr.FirstOrDefault(c => c.Kind == entry.Kind && string.Equals(c.Name, entry.Instance, StringComparison.Ordinal))
           ?? throw new ArrException($"{entry.Instance} is no longer configured in Media Vote.");
}
