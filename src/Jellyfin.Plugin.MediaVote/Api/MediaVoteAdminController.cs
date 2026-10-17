using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaVote.Configuration;
using Jellyfin.Plugin.MediaVote.Core;
using Jellyfin.Plugin.MediaVote.Integrations;
using Jellyfin.Plugin.MediaVote.Integrations.Arr;
using Jellyfin.Plugin.MediaVote.Services;
using Jellyfin.Plugin.MediaVote.Storage;
using Jellyfin.Plugin.MediaVote.Web;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaVote.Api;

/// <summary>Create-vote request: settings plus the deadline as local wall-clock text ("yyyy-MM-ddTHH:mm").</summary>
public sealed class CreateSessionRequest
{
    public SessionSettings Settings { get; set; } = new();

    public string DeadlineLocal { get; set; } = string.Empty;
}

/// <summary>Extend request.</summary>
public sealed class ExtendRequest
{
    public string DeadlineLocal { get; set; } = string.Empty;
}

/// <summary>Preview-mode request.</summary>
public sealed class VisibilityRequest
{
    public bool PreviewOnly { get; set; } = true;
}

/// <summary>Tie-break request.</summary>
public sealed class TieRequest
{
    public List<Guid> ItemIds { get; set; } = new();
}

/// <summary>Connection test request. An empty or masked key means "use the saved key of instance <see cref="Id"/>".</summary>
public sealed class TestRequest
{
    public string? Id { get; set; }

    public ArrKind Kind { get; set; }

    public string Url { get; set; } = string.Empty;

    public string? ApiKey { get; set; }
}

/// <summary>A Radarr/Sonarr/Jellyseerr instance as exchanged with the admin page (key masked on the way out).</summary>
public sealed class ArrInstanceDto
{
    public string? Id { get; set; }

    public ArrKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

/// <summary>Settings as exchanged with the admin page. Secrets are masked on the way out.</summary>
public sealed class SettingsDto
{
    public bool EnableBanner { get; set; }

    public bool EnableCustomTab { get; set; }

    public bool EnablePluginPage { get; set; }

    public bool EnableHomeSection { get; set; }

    public bool EnableStandalonePage { get; set; }

    public string TabTitle { get; set; } = "Vote";

    public string TimeZoneId { get; set; } = "Europe/London";

    public int ClosingSoonHours { get; set; }

    public int ResultBannerDays { get; set; }

    public bool UsePlaybackReporting { get; set; }

    public List<Guid> ProtectedItemIds { get; set; } = new();

    /// <summary>Display names for <see cref="ProtectedItemIds"/> (output only).</summary>
    public Dictionary<Guid, string> ProtectedItemNames { get; set; } = new();

    public List<string> ProtectedLibraryIds { get; set; } = new();

    public List<ArrInstanceDto> ArrInstances { get; set; } = new();

    public bool RadarrAddImportExclusion { get; set; } = true;

    public bool SonarrAddImportListExclusion { get; set; } = true;

    public SeerrAction JellyseerrAction { get; set; }

    public int MaxRemovalsPerVote { get; set; } = 5;

    public string WebhookUrl { get; set; } = string.Empty;

    public WebhookKind WebhookKind { get; set; }

    public bool WebhookOnOpened { get; set; }

    public bool WebhookOnClosingSoon { get; set; }

    public bool WebhookOnResults { get; set; }

    public string PublicServerUrl { get; set; } = string.Empty;
}

/// <summary>Admin-only endpoints (RequiresElevation = Jellyfin administrator).</summary>
[ApiController]
[Route("MediaVote/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class MediaVoteAdminController : MediaVoteControllerBase
{
    /// <summary>Prefix of a masked secret. Sending it back unchanged keeps the stored value.</summary>
    public const string MaskPrefix = "••••••";

    private static readonly TimeSpan ScopeCacheAge = TimeSpan.FromMinutes(3);

    private readonly VoteService _votes;
    private readonly JellyfinUserDirectory _users;
    private readonly LibraryCatalog _catalog;
    private readonly FileAuditLog _audit;
    private readonly ExternalServiceFactory _external;
    private readonly WebInjectionService _injection;
    private readonly EntryPointRegistrar _registrar;
    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _library;
    private readonly ApiSelfCheck _selfCheck;
    private readonly ArrLibraryCache _arrCache;
    private readonly Microsoft.Extensions.Logging.ILogger<MediaVoteAdminController> _logger;

    public MediaVoteAdminController(
        VoteService votes,
        JellyfinUserDirectory users,
        LibraryCatalog catalog,
        FileAuditLog audit,
        ExternalServiceFactory external,
        WebInjectionService injection,
        EntryPointRegistrar registrar,
        IApplicationPaths paths,
        ILibraryManager library,
        ApiSelfCheck selfCheck,
        ArrLibraryCache arrCache,
        Microsoft.Extensions.Logging.ILogger<MediaVoteAdminController> logger)
    {
        _arrCache = arrCache;
        _selfCheck = selfCheck;
        _logger = logger;
        _paths = paths;
        _library = library;
        _votes = votes;
        _users = users;
        _catalog = catalog;
        _audit = audit;
        _external = external;
        _injection = injection;
        _registrar = registrar;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private string AdminName => _users.GetUserName(CurrentUserId);

    private TimeZoneInfo Zone => TimeZoneHelper.Find(Config.TimeZoneId);

    public static string Mask(string? secret)
        => string.IsNullOrEmpty(secret) ? string.Empty : MaskPrefix + (secret.Length > 8 ? secret[^4..] : string.Empty);

    // ------------------------------------------------------------------ overview & lookups

    [HttpGet("Overview")]
    public ActionResult Overview()
    {
        var zone = Zone;
        var sessions = _votes.All.OrderByDescending(s => s.CreatedUtc).Select(s => new
        {
            id = s.Id,
            title = s.Settings.Title,
            status = s.Status,
            createdUtc = s.CreatedUtc,
            createdBy = s.CreatedBy,
            deadlineUtc = s.Settings.DeadlineUtc,
            deadlineLocal = LocalText(s.Settings.DeadlineUtc, zone),
            outcome = s.Settings.Outcome,
            candidates = s.Candidates.Count,
            voters = Tally.VoterCount(s),
            votes = s.Votes.Count,
            needsAttention = s.Result?.NeedsAdminAttention ?? false,
            resolvedUtc = s.Result?.ResolvedUtc
        }).ToList();

        return Json(new
        {
            nowUtc = _votes.Clock.UtcNow,
            nowLocal = LocalText(_votes.Clock.UtcNow, zone),
            defaultDeadlineInput = TimeZoneHelper.UtcToLocal(_votes.Clock.UtcNow, zone).Date.AddDays(7).AddHours(20).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            customTabSnippet = CustomTabsInspector.Snippet,
            customTabFindings = _registrar.CustomTabFindings,
            killSwitch = new { active = KillSwitch.IsActive, path = KillSwitch.FilePath },
            previewOnly = Config.AdminPreviewOnly,
            entryPoints = new
            {
                banner = Config.EnableBanner,
                tab = Config.EnableCustomTab,
                page = Config.EnablePluginPage,
                section = Config.EnableHomeSection,
                standalone = Config.EnableStandalonePage
            },
            selfCheck = _selfCheck.Results,
            selfCheckUtc = _selfCheck.LastRunUtc,
            pluginVersion = Plugin.Instance?.Version?.ToString(),
            builtForJellyfin = ErrorText.BuiltForJellyfin,
            timeZone = zone.Id,
            timeZoneFound = !string.Equals(zone.Id, "UTC", StringComparison.Ordinal) || string.Equals(Config.TimeZoneId, "UTC", StringComparison.OrdinalIgnoreCase),
            injection = _injection.Status,
            integrations = _registrar.Status,
            processUser = PermissionProbe.DescribeProcessUser(),
            maxRemovalsPerVote = Config.MaxRemovalsPerVote,
            arrInstanceCount = Config.ArrInstances.Count(i => i.Enabled),
            dataDirectory = PluginServiceRegistrator.DataDirectory(_paths),
            openSessionId = _votes.GetOpenSession()?.Id,
            sessions
        });
    }

    [HttpGet("Users")]
    public ActionResult Users()
        => Json(_users.ListUsers().Select(u => new { id = u.Id, name = u.Name, disabled = u.Disabled, admin = u.Admin }));

    [HttpGet("Libraries")]
    public ActionResult Libraries() => Json(_catalog.GetLibraries());

    [HttpGet("Search")]
    public async Task<ActionResult> Search([FromQuery] string? term)
    {
        var found = _catalog.Search(term ?? string.Empty).ToList();
        Removability.Annotate(found, await _arrCache.GetAsync(HttpContext.RequestAborted).ConfigureAwait(false));
        return Json(found.Select(c => new { id = c.ItemId, name = c.Name, year = c.Year, kind = c.Kind, path = c.Path, removable = c.Removable, removalNote = c.RemovalNote }));
    }

    [HttpPost("Preview")]
    public async Task<ActionResult> Preview()
    {
        var req = await ReadBodyAsync<CreateSessionRequest>().ConfigureAwait(false);
        if (req is null)
        {
            return Error(400, "bad_request", "Invalid request body.");
        }

        var cfg = Config;
        var candidates = CandidateFilter.Apply(_catalog.GetScopeCached(ScopeCacheAge), req.Settings, cfg.ProtectedItemIds.ToHashSet(), cfg.ProtectedLibraryIds.ToHashSet(StringComparer.OrdinalIgnoreCase), _votes.Clock.UtcNow);
        var library = await _arrCache.GetAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        Removability.Annotate(candidates, library);
        int all = candidates.Count;
        int notRemovable = candidates.Count(c => c.Removable != true);
        bool deleting = req.Settings.Outcome == OutcomeMode.DeleteViaArr;
        if (deleting && !req.Settings.IncludeUnmanagedTitles)
        {
            candidates = candidates.Where(c => c.Removable == true).ToList();
        }

        int n = Math.Max(1, req.Settings.WinnerCount);
        return Json(new
        {
            count = candidates.Count,
            totalBytes = candidates.Sum(c => c.SizeBytes),
            maxFreedBytes = candidates.OrderByDescending(c => c.SizeBytes).Take(n).Sum(c => c.SizeBytes),
            movies = candidates.Count(c => c.Kind == MediaKind.Movie),
            series = candidates.Count(c => c.Kind == MediaKind.Series),
            notRemovable,
            excludedNotRemovable = deleting && !req.Settings.IncludeUnmanagedTitles ? all - candidates.Count : 0,
            maxRemovals = cfg.MaxRemovalsPerVote,
            arrErrors = library.Errors,
            items = candidates.Take(200).Select(c => new { id = c.ItemId, name = c.Name, year = c.Year, kind = c.Kind, library = c.LibraryName, sizeBytes = c.SizeBytes, lastPlayedUtc = c.LastPlayedUtc, playCount = c.PlayCount, removable = c.Removable, removalNote = c.RemovalNote })
        });
    }

    // ------------------------------------------------------------------ sessions

    [HttpPost("Sessions")]
    public async Task<ActionResult> Create(CancellationToken cancellationToken)
    {
        var req = await ReadBodyAsync<CreateSessionRequest>().ConfigureAwait(false);
        if (req is null)
        {
            return Error(400, "bad_request", "Invalid request body.");
        }

        var (deadline, err) = ParseLocal(req.DeadlineLocal);
        if (deadline is null)
        {
            return Json(new { ok = false, errors = new[] { err } }, 400);
        }

        req.Settings.DeadlineUtc = deadline.Value;
        var cfg = Config;
        req.Settings.MaxRemovals = cfg.MaxRemovalsPerVote; // server-side safety cap, never from the browser
        var (result, session) = await _votes.CreateAsync(
            req.Settings,
            AdminName,
            _catalog.GetScopeCached(ScopeCacheAge),
            cfg.ProtectedItemIds.ToHashSet(),
            cfg.ProtectedLibraryIds.ToHashSet(StringComparer.OrdinalIgnoreCase),
            cancellationToken).ConfigureAwait(false);
        return result.Ok ? Json(new { ok = true, id = session!.Id }) : Json(new { ok = false, errors = result.Errors }, 400);
    }

    [HttpGet("Sessions/{id}")]
    public ActionResult Session([FromRoute] Guid id)
    {
        var s = _votes.Get(id);
        if (s is null)
        {
            return Error(404, VoteErrors.SessionNotFound);
        }

        var zone = Zone;
        var names = s.Candidates.ToDictionary(c => c.ItemId, c => c.Name);
        return Json(new
        {
            session = s,
            deadlineLocal = LocalText(s.Settings.DeadlineUtc, zone),
            deadlineLocalInput = TimeZoneHelper.UtcToLocal(s.Settings.DeadlineUtc, zone).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            eligibleCount = _votes.CountEligible(s),
            voterCount = Tally.VoterCount(s),
            ranking = s.Result?.Ranking ?? Tally.Count(s),
            votes = s.Votes.OrderByDescending(v => v.CastUtc).Select(v => new { v.UserName, itemId = v.ItemId, item = names.GetValueOrDefault(v.ItemId, v.ItemId.ToString()), v.Kind, castUtc = v.CastUtc, castLocal = LocalText(v.CastUtc, zone) }),
            nonVoters = NonVoters(s)
        });
    }

    [HttpPost("Sessions/{id}/Cancel")]
    public ActionResult Cancel([FromRoute] Guid id) => Op(_votes.Get(id) is null ? OpResult.Fail(VoteErrors.SessionNotFound) : _votes.Cancel(id, AdminName));

    [HttpPost("Sessions/{id}/Extend")]
    public async Task<ActionResult> Extend([FromRoute] Guid id)
    {
        var req = await ReadBodyAsync<ExtendRequest>().ConfigureAwait(false);
        var (deadline, err) = ParseLocal(req?.DeadlineLocal);
        if (deadline is null)
        {
            return Error(400, "bad_deadline", err);
        }

        return Op(_votes.Get(id) is null ? OpResult.Fail(VoteErrors.SessionNotFound) : _votes.Extend(id, deadline.Value, AdminName));
    }

    [HttpPost("Sessions/{id}/Close")]
    public async Task<ActionResult> Close([FromRoute] Guid id, CancellationToken cancellationToken)
        => Op(_votes.Get(id) is null ? OpResult.Fail(VoteErrors.SessionNotFound) : await _votes.CloseEarlyAsync(id, AdminName, cancellationToken).ConfigureAwait(false));

    [HttpPost("Sessions/{id}/DryRun")]
    public async Task<ActionResult> DryRun([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        if (_votes.Get(id) is null)
        {
            return Error(404, VoteErrors.SessionNotFound);
        }

        return Json(await _votes.DryRunAsync(id, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("Sessions/{id}/BreakTie")]
    public async Task<ActionResult> BreakTie([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var req = await ReadBodyAsync<TieRequest>().ConfigureAwait(false);
        if (req is null)
        {
            return Error(400, "bad_request");
        }

        return Op(await _votes.BreakTieAsync(id, req.ItemIds, AdminName, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("Sessions/{id}/Retry")]
    public async Task<ActionResult> Retry([FromRoute] Guid id, CancellationToken cancellationToken)
        => Op(await _votes.RetryAsync(id, AdminName, cancellationToken).ConfigureAwait(false));

    [HttpPost("Sessions/{id}/ResolveAnyway")]
    public async Task<ActionResult> ResolveAnyway([FromRoute] Guid id, CancellationToken cancellationToken)
        => Op(await _votes.ResolveAnywayAsync(id, AdminName, cancellationToken).ConfigureAwait(false));

    [HttpGet("Sessions/{id}/Csv")]
    public ActionResult Csv([FromRoute] Guid id)
    {
        var s = _votes.Get(id);
        if (s is null)
        {
            return Error(404, VoteErrors.SessionNotFound);
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ResultCsv.Build(s))).ToArray();
        return File(bytes, "text/csv; charset=utf-8", "media-vote-" + s.CreatedUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv");
    }

    [HttpGet("Audit")]
    public ActionResult Audit([FromQuery] Guid? sessionId, [FromQuery] int max = 300)
    {
        var zone = Zone;
        return Json(_audit.Read(Math.Clamp(max, 1, 5000), sessionId).Select(e => new { e.TimeUtc, timeLocal = LocalText(e.TimeUtc, zone), e.Action, e.Actor, e.SessionId, e.Details }));
    }

    // ------------------------------------------------------------------ settings

    [HttpGet("Settings")]
    public ActionResult GetSettings()
    {
        var c = Config;
        return Json(new SettingsDto
        {
            EnableBanner = c.EnableBanner,
            EnableCustomTab = c.EnableCustomTab,
            EnablePluginPage = c.EnablePluginPage,
            EnableHomeSection = c.EnableHomeSection,
            EnableStandalonePage = c.EnableStandalonePage,
            TabTitle = c.TabTitle,
            TimeZoneId = c.TimeZoneId,
            ClosingSoonHours = c.ClosingSoonHours,
            ResultBannerDays = c.ResultBannerDays,
            UsePlaybackReporting = c.UsePlaybackReporting,
            ProtectedItemIds = c.ProtectedItemIds.ToList(),
            ProtectedItemNames = c.ProtectedItemIds.Distinct().ToDictionary(id => id, id => ProtectedName(id)),
            ProtectedLibraryIds = c.ProtectedLibraryIds.ToList(),
            ArrInstances = c.ArrInstances.Select(i => new ArrInstanceDto { Id = i.Id, Kind = i.Kind, Name = i.Name, Url = i.Url, ApiKey = Mask(i.ApiKey), Enabled = i.Enabled }).ToList(),
            RadarrAddImportExclusion = c.RadarrAddImportExclusion,
            SonarrAddImportListExclusion = c.SonarrAddImportListExclusion,
            JellyseerrAction = c.JellyseerrAction,
            MaxRemovalsPerVote = c.MaxRemovalsPerVote,
            WebhookUrl = Mask(c.WebhookUrl),
            WebhookKind = c.WebhookKind,
            WebhookOnOpened = c.WebhookOnOpened,
            WebhookOnClosingSoon = c.WebhookOnClosingSoon,
            WebhookOnResults = c.WebhookOnResults,
            PublicServerUrl = c.PublicServerUrl
        });
    }

    [HttpPost("Settings")]
    public async Task<ActionResult> SaveSettings()
    {
        var d = await ReadBodyAsync<SettingsDto>().ConfigureAwait(false);
        if (d is null || Plugin.Instance is null)
        {
            return Error(400, "bad_request");
        }

        var errors = new List<string>();
        foreach (var i in d.ArrInstances ?? new List<ArrInstanceDto>())
        {
            var label = string.IsNullOrWhiteSpace(i.Name) ? i.Kind.ToString() : i.Name.Trim();
            if (string.IsNullOrWhiteSpace(i.Url) || ArrHttp.NormalizeBaseUrl(i.Url) is null)
            {
                errors.Add($"{label}: the URL must start with http:// or https://");
            }
        }

        if (d.MaxRemovalsPerVote < 1 || d.MaxRemovalsPerVote > 100)
        {
            errors.Add("\"Most titles one vote may remove\" must be between 1 and 100.");
        }

        if (TimeZoneHelper.Find(d.TimeZoneId).Id == "UTC" && !string.Equals(d.TimeZoneId, "UTC", StringComparison.OrdinalIgnoreCase) && !string.Equals(d.TimeZoneId, "Etc/UTC", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Unknown time zone \"{d.TimeZoneId}\". Use an IANA name such as Europe/London.");
        }

        if (errors.Count > 0)
        {
            return Json(new { ok = false, errors }, 400);
        }

        var c = Plugin.Instance.Configuration;
        c.EnableBanner = d.EnableBanner;
        c.EnableCustomTab = d.EnableCustomTab;
        c.EnablePluginPage = d.EnablePluginPage;
        c.EnableHomeSection = d.EnableHomeSection;
        c.EnableStandalonePage = d.EnableStandalonePage;
        c.TabTitle = (d.TabTitle ?? "Vote").Trim();
        c.TimeZoneId = (d.TimeZoneId ?? "Europe/London").Trim();
        c.ClosingSoonHours = d.ClosingSoonHours;
        c.ResultBannerDays = d.ResultBannerDays;
        c.UsePlaybackReporting = d.UsePlaybackReporting;
        c.ProtectedItemIds = d.ProtectedItemIds.Distinct().ToArray();
        c.ProtectedLibraryIds = d.ProtectedLibraryIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var old = c.ArrInstances.ToDictionary(i => i.Id, StringComparer.Ordinal);
        c.ArrInstances = (d.ArrInstances ?? new List<ArrInstanceDto>()).Select(i =>
        {
            old.TryGetValue(i.Id ?? string.Empty, out var prev);
            return new ArrInstanceConfig
            {
                Id = prev?.Id ?? Guid.NewGuid().ToString("N"),
                Kind = i.Kind,
                Name = (i.Name ?? string.Empty).Trim(),
                Url = ArrHttp.NormalizeBaseUrl(i.Url) ?? string.Empty,
                ApiKey = KeepOrReplace(prev?.ApiKey ?? string.Empty, i.ApiKey),
                Enabled = i.Enabled
            };
        }).ToArray();
        c.RadarrAddImportExclusion = d.RadarrAddImportExclusion;
        c.SonarrAddImportListExclusion = d.SonarrAddImportListExclusion;
        c.JellyseerrAction = d.JellyseerrAction;
        c.MaxRemovalsPerVote = d.MaxRemovalsPerVote;
        c.WebhookUrl = KeepOrReplace(c.WebhookUrl, d.WebhookUrl);
        c.WebhookKind = d.WebhookKind;
        c.WebhookOnOpened = d.WebhookOnOpened;
        c.WebhookOnClosingSoon = d.WebhookOnClosingSoon;
        c.WebhookOnResults = d.WebhookOnResults;
        c.PublicServerUrl = (d.PublicServerUrl ?? string.Empty).Trim();
        Plugin.Instance.UpdateConfiguration(c);
        _arrCache.Invalidate();
        _audit.Write("settings_saved", AdminName, null, "entry points: banner=" + c.EnableBanner + " tab=" + c.EnableCustomTab + " page=" + c.EnablePluginPage + " section=" + c.EnableHomeSection + " standalone=" + c.EnableStandalonePage
            + "; instances: " + string.Join(", ", c.ArrInstances.Select(i => i.Kind + " \"" + i.DisplayName + "\"" + (i.Enabled ? string.Empty : " (off)"))) + "; max removals " + c.MaxRemovalsPerVote);
        return Json(new { ok = true });
    }

    [HttpPost("Test/{service}")]
    public async Task<ActionResult> Test([FromRoute] string service, CancellationToken cancellationToken)
    {
        var req = await ReadBodyAsync<TestRequest>().ConfigureAwait(false) ?? new TestRequest();
        if (Enum.TryParse<ArrKind>(service, true, out var kind))
        {
            req.Kind = kind;
        }

        var saved = Config.ArrInstances.FirstOrDefault(i => i.Id == req.Id);
        var key = string.IsNullOrWhiteSpace(req.ApiKey) || req.ApiKey.StartsWith(MaskPrefix, StringComparison.Ordinal) ? saved?.ApiKey ?? string.Empty : req.ApiKey;
        var instance = new ArrInstanceConfig { Kind = req.Kind, Name = req.Kind.ToString(), Url = req.Url, ApiKey = key };
        try
        {
            // Connection tests are read-only (GET system/status).
            string message = req.Kind == ArrKind.Jellyseerr
                ? await _external.CreateSeerr(instance).TestAsync(cancellationToken).ConfigureAwait(false)
                : await _external.CreateArr(instance).TestAsync(cancellationToken).ConfigureAwait(false);
            return Json(new { ok = true, message = "Connected: " + message });
        }
        catch (ArrException ex)
        {
            return Json(new { ok = false, message = ex.Message });
        }
    }

    /// <summary>Read-only state of every Radarr/Sonarr/Jellyseerr instance: reachable, version, root folders with free space.</summary>
    [HttpGet("ArrStatus")]
    public async Task<ActionResult> ArrStatus(CancellationToken cancellationToken)
    {
        var list = new List<object>();
        foreach (var i in Config.ArrInstances)
        {
            if (!i.Enabled)
            {
                list.Add(new { name = i.DisplayName, kind = i.Kind, enabled = false, ok = false, message = "Switched off in Settings.", rootFolders = Array.Empty<object>() });
                continue;
            }

            try
            {
                if (i.Kind == ArrKind.Jellyseerr)
                {
                    var v = await _external.CreateSeerr(i).TestAsync(cancellationToken).ConfigureAwait(false);
                    list.Add(new { name = i.DisplayName, kind = i.Kind, enabled = true, ok = true, message = "Connected: " + v, rootFolders = Array.Empty<object>() });
                }
                else
                {
                    var client = _external.CreateArr(i);
                    var v = await client.TestAsync(cancellationToken).ConfigureAwait(false);
                    var roots = await client.RootFoldersAsync(cancellationToken).ConfigureAwait(false);
                    list.Add(new { name = i.DisplayName, kind = i.Kind, enabled = true, ok = true, message = "Connected: " + v, rootFolders = roots.Select(r => new { path = r.Path, freeSpace = r.FreeSpace, totalSpace = r.TotalSpace }).ToArray() });
                }
            }
            catch (ArrException ex)
            {
                list.Add(new { name = i.DisplayName, kind = i.Kind, enabled = true, ok = false, message = ex.Message, rootFolders = Array.Empty<object>() });
            }
        }

        return Json(new { instances = list });
    }

    /// <summary>Re-applies the entry points (and re-reads Custom Tabs, read-only).</summary>
    [HttpPost("Reapply")]
    public ActionResult Reapply()
    {
        StartupTask.ApplyAll(_injection, _registrar, _logger);
        return Json(new { injection = _injection.Status, integrations = _registrar.Status, customTabFindings = _registrar.CustomTabFindings });
    }

    /// <summary>Runs the Jellyfin API self-check again.</summary>
    [HttpPost("SelfCheck")]
    public ActionResult SelfCheck() => Json(new { selfCheck = _selfCheck.Run(), selfCheckUtc = _selfCheck.LastRunUtc });

    /// <summary>Preview mode on/off ("Go live"). Body: { "previewOnly": false }.</summary>
    [HttpPost("Visibility")]
    public async Task<ActionResult> Visibility()
    {
        var req = await ReadBodyAsync<VisibilityRequest>().ConfigureAwait(false);
        if (req is null || Plugin.Instance is null)
        {
            return Error(400, "bad_request");
        }

        var c = Plugin.Instance.Configuration;
        c.AdminPreviewOnly = req.PreviewOnly;
        Plugin.Instance.UpdateConfiguration(c);
        _audit.Write(req.PreviewOnly ? "preview_on" : "went_live", AdminName, null, req.PreviewOnly ? "Preview mode switched on" : "Preview mode switched off (live for all users)");
        return Json(new { ok = true, previewOnly = c.AdminPreviewOnly });
    }

    // ------------------------------------------------------------------ helpers

    private string ProtectedName(Guid id)
    {
        try
        {
            return _library.GetItemById(id)?.Name ?? "(missing) " + id;
        }
#pragma warning disable CA1031 // a name lookup must not break the settings page
        catch (Exception)
#pragma warning restore CA1031
        {
            return id.ToString();
        }
    }

    private static string KeepOrReplace(string current, string? incoming)
    {
        if (incoming is null || incoming.StartsWith(MaskPrefix, StringComparison.Ordinal))
        {
            return current; // unchanged (masked value echoed back)
        }

        return incoming.Trim();
    }

    private static string LocalText(DateTime utc, TimeZoneInfo zone)
        => TimeZoneHelper.UtcToLocal(utc, zone).ToString("ddd d MMM yyyy HH:mm", CultureInfo.GetCultureInfo("en-GB"));

    private (DateTime? Utc, string? Error) ParseLocal(string? local)
    {
        if (string.IsNullOrWhiteSpace(local)
            || !DateTime.TryParseExact(local.Trim(), new[] { "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return (null, "Enter the deadline as a date and time.");
        }

        return TimeZoneHelper.LocalToUtc(parsed, Zone);
    }

    private List<string> NonVoters(VoteSession s)
    {
        var voted = s.Votes.Select(v => v.UserId).ToHashSet();
        var eligible = s.Settings.AllUsers ? _users.GetActiveUserIds() : s.Settings.EligibleUserIds.Where(_users.IsActive).ToList();
        return eligible.Where(u => !voted.Contains(u)).Select(_users.GetUserName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private ActionResult Op(OpResult r)
        => r.Ok ? Json(new { ok = true }) : Json(new { ok = false, error = r.Error, errors = r.Errors }, r.Error == VoteErrors.SessionNotFound ? 404 : 400);
}
