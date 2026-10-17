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

/// <summary>
/// Create-vote request: settings plus the deadline, either as a picker choice (<see cref="Deadline"/>, preferred)
/// or as local wall-clock text ("yyyy-MM-ddTHH:mm"). The server always does the time zone maths.
/// </summary>
public sealed class CreateSessionRequest
{
    public SessionSettings Settings { get; set; } = new();

    public DeadlineSpec? Deadline { get; set; }

    public string DeadlineLocal { get; set; } = string.Empty;
}

/// <summary>Extend request (same deadline forms as <see cref="CreateSessionRequest"/>).</summary>
public sealed class ExtendRequest
{
    public DeadlineSpec? Deadline { get; set; }

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

    public bool RequireApprovalForDeletes { get; set; } = true;

    public int AutoApproveHours { get; set; }

    public string JellyseerrPublicUrl { get; set; } = string.Empty;

    public bool WebhookOnApproval { get; set; } = true;

    public bool WebhookIncludeNonVoters { get; set; } = true;

    public List<DriveNameConfig> DriveNames { get; set; } = new();

    public int DriveWarnPercent { get; set; } = 85;

    public int DriveCriticalPercent { get; set; } = 95;

    public WatchVisibility WatchDetailsForVoters { get; set; }
}

/// <summary>Template save request.</summary>
public sealed class TemplateRequest
{
    public string? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public SessionSettings Settings { get; set; } = new();

    public DeadlineSpec Deadline { get; set; } = DeadlineSpec.Default;

    public RecurrenceKind Recurrence { get; set; }

    /// <summary>First draft time for a recurring template ("yyyy-MM-ddTHH:mm", local).</summary>
    public string? FirstRunLocal { get; set; }
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
    private readonly DriveService _drives;
    private readonly ITemplateStore _templates;
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
        DriveService drives,
        ITemplateStore templates,
        Microsoft.Extensions.Logging.ILogger<MediaVoteAdminController> logger)
    {
        _templates = templates;
        _arrCache = arrCache;
        _drives = drives;
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
            requireApproval = Config.RequireApprovalForDeletes,
            awaitingApproval = _votes.AwaitingApproval().Select(a => new
            {
                id = a.Id,
                title = a.Settings.Title,
                count = a.Result!.PlannedItemIds.Count,
                bytes = a.Result.Items.Where(i => a.Result.PlannedItemIds.Contains(i.ItemId)).Sum(i => i.ArrSizeOnDisk > 0 ? i.ArrSizeOnDisk : i.SizeBytes),
                dueUtc = a.Result.ApprovalDueUtc,
                dueLocal = a.Result.ApprovalDueUtc is { } due ? LocalText(due, zone) : null
            }),
            pendingDrafts = _templates.GetAll().Where(t => t.DraftPendingSinceUtc is not null).Select(t => new { id = t.Id, name = t.Name, sinceLocal = LocalText(t.DraftPendingSinceUtc!.Value, zone) }),
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

    /// <summary>The shared media browser for hand-picking titles: filtered, sorted and paged on the server.</summary>
    [HttpGet("Browse")]
    public async Task<ActionResult> Browse([FromQuery] ItemQuery q)
    {
        var scope = await BrowseScopeAsync().ConfigureAwait(false);
        return Json(ViewBuilder.BuildBrowsePage(scope, CurrentUserId, q ?? new ItemQuery(), SafeDrives(), _votes.Clock.UtcNow));
    }

    /// <summary>Every title matching the filter (for "select all in current filter"): id, size and bytes per drive.</summary>
    [HttpGet("Browse/Ids")]
    public async Task<ActionResult> BrowseIds([FromQuery] ItemQuery q)
    {
        var scope = await BrowseScopeAsync().ConfigureAwait(false);
        var ctx = new BrowseContext { ViewerId = CurrentUserId, NowUtc = _votes.Clock.UtcNow };
        var list = BrowseEngine.Apply(scope, q ?? new ItemQuery(), ctx).Take(10000).ToList();
        return Json(new
        {
            count = list.Count,
            totalBytes = list.Sum(c => c.SizeBytes),
            items = list.Select(c => new { id = c.ItemId, name = c.Name, year = c.Year, kind = c.Kind, sizeBytes = c.SizeBytes, drives = c.DriveBytes, removable = c.Removable })
        });
    }

    /// <summary>Detected drives with usage (read-only, cached about a minute).</summary>
    [HttpGet("Drives")]
    public ActionResult Drives() => Json(new { drives = SafeDrives().Select(DriveView.From), warnPercent = Config.DriveWarnPercent, criticalPercent = Config.DriveCriticalPercent });

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

        var (deadline, err) = ResolveDeadline(req.Deadline, req.DeadlineLocal);
        if (deadline is null)
        {
            return Json(new { ok = false, errors = new[] { err } }, 400);
        }

        req.Settings.DeadlineUtc = deadline.Value;
        var cfg = Config;
        req.Settings.MaxRemovals = cfg.MaxRemovalsPerVote; // server-side safety cap, never from the browser
        req.Settings.RequireApproval = req.Settings.Outcome == OutcomeMode.DeleteViaArr && cfg.RequireApprovalForDeletes;
        req.Settings.AutoApproveHours = req.Settings.RequireApproval ? cfg.AutoApproveHours : 0;
        var (result, session) = await _votes.CreateAsync(
            req.Settings,
            AdminName,
            _catalog.GetScopeCached(ScopeCacheAge),
            cfg.ProtectedItemIds.ToHashSet(),
            cfg.ProtectedLibraryIds.ToHashSet(StringComparer.OrdinalIgnoreCase),
            cancellationToken).ConfigureAwait(false);
        if (result.Ok && !string.IsNullOrEmpty(req.Settings.TemplateId) && _templates.GetAll().FirstOrDefault(t => t.Id == req.Settings.TemplateId) is { DraftPendingSinceUtc: not null } tpl)
        {
            tpl.DraftPendingSinceUtc = null; // the recurring draft has been reviewed and opened
            _templates.Save(tpl);
        }

        return result.Ok ? Json(new { ok = true, id = session!.Id }) : Json(new { ok = false, errors = result.Errors }, 400);
    }

    /// <summary>Approve a prepared "Delete via Sonarr/Radarr" list.</summary>
    [HttpPost("Sessions/{id}/Approve")]
    public async Task<ActionResult> Approve([FromRoute] Guid id, CancellationToken cancellationToken)
        => Op(_votes.Get(id) is null ? OpResult.Fail(VoteErrors.SessionNotFound) : await _votes.ApproveAsync(id, AdminName, cancellationToken).ConfigureAwait(false));

    /// <summary>Reject a prepared list: nothing is removed.</summary>
    [HttpPost("Sessions/{id}/Reject")]
    public ActionResult Reject([FromRoute] Guid id) => Op(_votes.Get(id) is null ? OpResult.Fail(VoteErrors.SessionNotFound) : _votes.Reject(id, AdminName));

    // ------------------------------------------------------------------ templates, history, storage

    [HttpGet("Templates")]
    public ActionResult Templates()
        => Json(_templates.GetAll().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => new
        {
            t.Id,
            t.Name,
            t.Settings,
            t.Deadline,
            t.Recurrence,
            t.NextRunLocal,
            schedule = TemplateSchedule.Describe(t),
            draftPending = t.DraftPendingSinceUtc is not null
        }));

    [HttpPost("Templates")]
    public async Task<ActionResult> SaveTemplate()
    {
        var req = await ReadBodyAsync<TemplateRequest>().ConfigureAwait(false);
        if (req is null || string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 100)
        {
            return Json(new { ok = false, errors = new[] { "Give the template a name (up to 100 characters)." } }, 400);
        }

        var existing = _templates.GetAll().FirstOrDefault(t => t.Id == req.Id);
        var t = existing ?? new VoteTemplate { CreatedBy = AdminName };
        t.Name = req.Name.Trim();
        req.Settings.DeleteConfirmed = false; // the confirmation is always given again when a vote is opened
        req.Settings.TemplateId = t.Id;
        t.Settings = req.Settings;
        t.Deadline = req.Deadline ?? DeadlineSpec.Default;
        t.Recurrence = req.Recurrence;
        t.UpdatedUtc = _votes.Clock.UtcNow;
        if (t.Recurrence != RecurrenceKind.None)
        {
            if (string.IsNullOrWhiteSpace(req.FirstRunLocal)
                || !DateTime.TryParseExact(req.FirstRunLocal.Trim(), "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
            {
                return Json(new { ok = false, errors = new[] { "Choose when the first recurring draft should be prepared." } }, 400);
            }

            t.NextRunLocal = first.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
            t.AnchorDay = first.Day;
        }
        else
        {
            t.NextRunLocal = null;
            t.DraftPendingSinceUtc = null;
        }

        _templates.Save(t);
        _audit.Write("template_saved", AdminName, null, t.Name + " · " + TemplateSchedule.Describe(t));
        return Json(new { ok = true, id = t.Id });
    }

    [HttpDelete("Templates/{id}")]
    public ActionResult DeleteTemplate([FromRoute] string id)
    {
        bool ok = _templates.Delete(id);
        if (ok)
        {
            _audit.Write("template_deleted", AdminName, null, id);
        }

        return ok ? Json(new { ok = true }) : Error(404, "not_found", "Template not found.");
    }

    /// <summary>Skip a recurring draft without opening a vote.</summary>
    [HttpPost("Templates/{id}/Skip")]
    public ActionResult SkipDraft([FromRoute] string id)
    {
        var t = _templates.GetAll().FirstOrDefault(x => x.Id == id);
        if (t is null)
        {
            return Error(404, "not_found", "Template not found.");
        }

        t.DraftPendingSinceUtc = null;
        _templates.Save(t);
        _audit.Write("template_draft_skipped", AdminName, null, t.Name);
        return Json(new { ok = true });
    }

    /// <summary>History and statistics (admin only).</summary>
    [HttpGet("Stats")]
    public ActionResult Stats()
    {
        var users = _users.ListUsers().Where(u => !u.Disabled).ToDictionary(u => u.Id, u => u.Name);
        var st = Statistics.Build(_votes.All, users);
        var zone = Zone;
        return Json(new
        {
            totalFreedBytes = st.TotalFreedBytes,
            sessions = st.Sessions.Select(x => new { x.Id, x.Title, resolvedLocal = LocalText(x.ResolvedUtc, zone), x.ResolvedUtc, x.FreedBytes, x.CumulativeFreedBytes, x.Voters, x.Eligible, x.TurnoutPercent, x.Removed, x.Outcome }),
            mostVoted = st.MostVoted,
            participation = st.Participation
        });
    }

    /// <summary>Storage overview: every drive, its largest libraries, and what the open vote's current top titles would free.</summary>
    [HttpGet("Storage")]
    public ActionResult Storage()
    {
        var drives = SafeDrives();
        var scope = _catalog.GetScopeCached(ScopeCacheAge);
        var perDrive = drives.Select(d => new
        {
            drive = DriveView.From(d),
            libraries = scope.Where(c => c.DriveBytes.ContainsKey(d.Key))
                .GroupBy(c => c.LibraryName)
                .Select(g => new { name = g.Key, bytes = g.Sum(c => c.DriveBytes[d.Key]), titles = g.Count() })
                .OrderByDescending(x => x.bytes).Take(5).ToList()
        }).ToList();

        object? openVote = null;
        var open = _votes.GetOpenSession();
        if (open is not null)
        {
            var ranking = Tally.Count(open);
            var cands = open.Candidates.ToDictionary(c => c.ItemId);
            var plan = WinnerPlanner.Plan(open.Settings, ranking, cands, drives);
            var freed = drives.Select(d => (Drive: d, Bytes: plan.Winners.Sum(w => cands.TryGetValue(w.ItemId, out var c) && c.DriveBytes.TryGetValue(d.Key, out var b) ? b : 0))).Where(x => x.Bytes > 0).ToList();
            openVote = new
            {
                title = open.Settings.Title,
                winners = plan.Winners.Count,
                notes = plan.Notes,
                titles = plan.Winners.Select(w => cands.TryGetValue(w.ItemId, out var c) ? c.Name : w.ItemId.ToString()),
                projections = freed.Select(x => new { key = x.Drive.Key, name = x.Drive.Name, bytes = x.Bytes, text = DriveMath.Projection(x.Drive, x.Bytes), percentAfter = DriveMath.After(x.Drive, x.Bytes).Percent })
            };
        }

        return Json(new { drives = perDrive, openVote, warnPercent = Config.DriveWarnPercent, criticalPercent = Config.DriveCriticalPercent });
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
        var (deadline, err) = ResolveDeadline(req?.Deadline, req?.DeadlineLocal);
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
            PublicServerUrl = c.PublicServerUrl,
            RequireApprovalForDeletes = c.RequireApprovalForDeletes,
            AutoApproveHours = c.AutoApproveHours,
            JellyseerrPublicUrl = c.JellyseerrPublicUrl,
            WebhookOnApproval = c.WebhookOnApproval,
            WebhookIncludeNonVoters = c.WebhookIncludeNonVoters,
            DriveNames = c.DriveNames.ToList(),
            DriveWarnPercent = c.DriveWarnPercent,
            DriveCriticalPercent = c.DriveCriticalPercent,
            WatchDetailsForVoters = c.WatchDetailsForVoters
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
        c.RequireApprovalForDeletes = d.RequireApprovalForDeletes;
        c.AutoApproveHours = Math.Clamp(d.AutoApproveHours, 0, 24 * 30);
        c.JellyseerrPublicUrl = ArrHttp.NormalizeBaseUrl(d.JellyseerrPublicUrl) ?? string.Empty;
        c.WebhookOnApproval = d.WebhookOnApproval;
        c.WebhookIncludeNonVoters = d.WebhookIncludeNonVoters;
        c.DriveNames = (d.DriveNames ?? new List<DriveNameConfig>()).Where(n => !string.IsNullOrWhiteSpace(n.MountPoint)).Select(n => new DriveNameConfig { MountPoint = n.MountPoint.Trim(), Name = (n.Name ?? string.Empty).Trim() }).ToArray();
        c.DriveWarnPercent = Math.Clamp(d.DriveWarnPercent, 1, 99);
        c.DriveCriticalPercent = Math.Clamp(d.DriveCriticalPercent, c.DriveWarnPercent, 100);
        c.WatchDetailsForVoters = d.WatchDetailsForVoters;
        Plugin.Instance.UpdateConfiguration(c);
        _arrCache.Invalidate();
        _drives.Invalidate();
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

    /// <summary>All votable (unprotected) titles with removability, for the admin browser.</summary>
    private async Task<List<CandidateItem>> BrowseScopeAsync()
    {
        var cfg = Config;
        var prot = cfg.ProtectedItemIds.ToHashSet();
        var protLibs = cfg.ProtectedLibraryIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scope = _catalog.GetScopeCached(ScopeCacheAge).Where(c => !prot.Contains(c.ItemId) && !protLibs.Contains(c.LibraryId)).ToList();
        Removability.Annotate(scope, await _arrCache.GetAsync(HttpContext.RequestAborted).ConfigureAwait(false));
        return scope;
    }

    private IReadOnlyList<DriveStat> SafeDrives()
    {
        try
        {
            return _drives.GetDrives();
        }
#pragma warning disable CA1031 // drive bars are informational
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(_logger, ex, "Media Vote: drive detection failed");
            return Array.Empty<DriveStat>();
        }
    }

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

    /// <summary>Plain-words preview of a deadline choice ("Closes Friday 24 Oct 2026 at 20:00 BST (in 7 days)").</summary>
    [HttpPost("Deadline")]
    public async Task<ActionResult> Deadline()
    {
        var spec = await ReadBodyAsync<DeadlineSpec>().ConfigureAwait(false);
        var r = DeadlineCalculator.Compute(spec, _votes.Clock.UtcNow, Zone);
        return Json(new { ok = r.Error is null, deadlineUtc = r.Utc, localInput = r.LocalInput, description = r.Description, error = r.Error });
    }

    private (DateTime? Utc, string? Error) ResolveDeadline(DeadlineSpec? spec, string? local)
    {
        if (spec is not null)
        {
            var r = DeadlineCalculator.Compute(spec, _votes.Clock.UtcNow, Zone);
            return (r.Utc, r.Error);
        }

        return ParseLocal(local);
    }

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
