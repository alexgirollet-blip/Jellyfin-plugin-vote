using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaVote.Configuration;
using Jellyfin.Plugin.MediaVote.Core;
using Jellyfin.Plugin.MediaVote.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaVote.Api;

/// <summary>Request body for voting.</summary>
public sealed class VoteRequest
{
    public Guid ItemId { get; set; }

    public Guid FromItemId { get; set; }

    public VoteKind Kind { get; set; } = VoteKind.Remove;
}

/// <summary>
/// User endpoints. Every action acts only as the authenticated user (id from the token);
/// eligibility, vote limits and the deadline are enforced in <see cref="VoteService"/>/<see cref="VoteRules"/>.
/// Also serves the client assets (anonymous, no data).
/// </summary>
[ApiController]
[Route("MediaVote")]
public sealed class MediaVoteController : MediaVoteControllerBase
{
    private readonly VoteService _votes;
    private readonly JellyfinUserDirectory _users;
    private readonly LibraryCatalog _catalog;

    public MediaVoteController(VoteService votes, JellyfinUserDirectory users, LibraryCatalog catalog)
    {
        _votes = votes;
        _users = users;
        _catalog = catalog;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    // ------------------------------------------------------------------ assets (anonymous)

    [HttpGet("client.js")]
    [AllowAnonymous]
    public ActionResult ClientJs() => Asset("client.js", "application/javascript; charset=utf-8");

    [HttpGet("client.css")]
    [AllowAnonymous]
    public ActionResult ClientCss() => Asset("client.css", "text/css; charset=utf-8");

    /// <summary>HTML fragment loaded by Plugin Pages into its sidebar page.</summary>
    [HttpGet("page")]
    [AllowAnonymous]
    public ActionResult PageFragment() => Asset("page.html", "text/html; charset=utf-8");

    /// <summary>Standalone voting page with its own Jellyfin login (works without Custom Tabs / Plugin Pages).</summary>
    [HttpGet("vote")]
    [AllowAnonymous]
    public ActionResult Standalone()
        => Config.EnableStandalonePage ? Asset("standalone.html", "text/html; charset=utf-8") : NotFound();

    // ------------------------------------------------------------------ user API

    /// <summary>Everything the banner/tab/page needs in one call.</summary>
    [HttpGet("Status")]
    [Authorize]
    public ActionResult Status()
    {
        var userId = CurrentUserId;
        var cfg = Config;
        var now = _votes.Clock.UtcNow;
        var zone = TimeZoneHelper.Find(cfg.TimeZoneId);
        var session = _votes.GetOpenSession();
        object? active = null;
        if (session is not null)
        {
            bool eligible = _votes.IsEligible(session, userId);
            var s = session.Settings;
            active = new
            {
                id = session.Id,
                title = s.Title,
                description = s.Description,
                deadlineUtc = DateTime.SpecifyKind(s.DeadlineUtc, DateTimeKind.Utc),
                deadlineLocal = TimeZoneHelper.UtcToLocal(s.DeadlineUtc, zone).ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.GetCultureInfo("en-GB")),
                timeZone = zone.Id,
                eligible,
                votesPerUser = s.VotesPerUser,
                votesUsed = VoteRules.UsedVotes(session, userId, VoteKind.Remove),
                votesRemaining = eligible ? VoteRules.RemainingVotes(session, userId, VoteKind.Remove) : 0,
                allowKeepVotes = s.AllowKeepVotes,
                keepVotesPerUser = s.AllowKeepVotes ? s.KeepVotesPerUser : 0,
                keepVotesRemaining = eligible ? VoteRules.RemainingVotes(session, userId, VoteKind.Keep) : 0,
                allowMultipleVotesPerItem = s.AllowMultipleVotesPerItem,
                showLiveTallies = s.ShowLiveTallies,
                showVoterNames = s.ShowVoterNames,
                winnerCount = s.WinnerCount,
                outcome = s.Outcome,
                hasVoted = session.Votes.Any(v => v.UserId == userId),
                closingSoon = VoteRules.IsClosingSoon(session, now, TimeSpan.FromHours(cfg.ClosingSoonHours)),
                candidateCount = session.Candidates.Count
            };
        }

        object? lastResult = null;
        var latest = _votes.GetLatestResolved();
        if (latest?.Result is { } r && (session is null || latest.Id != session.Id))
        {
            bool recent = now - r.ResolvedUtc <= TimeSpan.FromDays(cfg.ResultBannerDays);
            lastResult = new
            {
                id = latest.Id,
                title = latest.Settings.Title,
                resolvedUtc = DateTime.SpecifyKind(r.ResolvedUtc, DateTimeKind.Utc),
                outcome = r.Outcome,
                turnoutPercent = r.TurnoutPercent,
                turnoutMet = r.TurnoutMet,
                showBanner = recent && _votes.IsEligible(latest, userId) && !latest.ResultDismissedBy.Contains(userId),
                items = r.Items.Select(i => new { id = i.ItemId, name = i.Name, kind = i.Kind, score = i.Score, status = PublicStatus(i.Status, r.Outcome) }).ToList()
            };
        }

        return Json(new
        {
            serverTimeUtc = now,
            isAdmin = _users.IsAdministrator(userId),
            entryPoints = new { banner = cfg.EnableBanner, tab = cfg.EnableCustomTab, page = cfg.EnablePluginPage, standalone = cfg.EnableStandalonePage },
            closingSoonHours = cfg.ClosingSoonHours,
            active,
            lastResult
        });
    }

    [HttpGet("Items")]
    [Authorize]
    public ActionResult Items([FromQuery] string? search, [FromQuery] string? type, [FromQuery] string? genre, [FromQuery] string? sort, [FromQuery] bool desc = false, [FromQuery] bool mine = false, [FromQuery] int page = 0, [FromQuery] int pageSize = 30)
    {
        var userId = CurrentUserId;
        var session = _votes.GetOpenSession();
        if (session is null)
        {
            return Error(404, VoteErrors.SessionNotOpen, "There is no active vote right now.");
        }

        bool isAdmin = _users.IsAdministrator(userId);
        if (!_votes.IsEligible(session, userId) && !isAdmin)
        {
            return Error(403, VoteErrors.NotEligible, "You are not taking part in this vote.");
        }

        var visible = _catalog.GetVisibleIds(session.Id, userId, session.Candidates.Select(c => c.ItemId).ToList());
        var result = ViewBuilder.BuildPage(
            session,
            userId,
            isAdmin,
            new ItemQuery { Search = Limit(search, 100), Type = type, Genre = Limit(genre, 100), Sort = sort, Descending = desc, MineOnly = mine, Page = page, PageSize = pageSize },
            visible.Contains);
        return Json(result);
    }

    [HttpPost("Vote")]
    [Authorize]
    public async Task<ActionResult> Vote()
    {
        var body = await ReadBodyAsync<VoteRequest>().ConfigureAwait(false);
        return Apply(body, (s, voter, b) => _votes.Cast(s.Id, voter, b.ItemId, b.Kind), b => b.ItemId);
    }

    [HttpPost("Unvote")]
    [Authorize]
    public async Task<ActionResult> Unvote()
    {
        var body = await ReadBodyAsync<VoteRequest>().ConfigureAwait(false);
        return Apply(body, (s, voter, b) => _votes.Withdraw(s.Id, voter, b.ItemId, b.Kind), null);
    }

    [HttpPost("Move")]
    [Authorize]
    public async Task<ActionResult> Move()
    {
        var body = await ReadBodyAsync<VoteRequest>().ConfigureAwait(false);
        return Apply(body, (s, voter, b) => _votes.Move(s.Id, voter, b.FromItemId, b.ItemId, b.Kind), b => b.ItemId);
    }

    [HttpPost("Results/{id}/Dismiss")]
    [Authorize]
    public ActionResult Dismiss([FromRoute] Guid id)
    {
        var r = _votes.DismissResult(id, CurrentUserId);
        return r.Ok ? Json(new { ok = true }) : Error(404, r.Error!);
    }

    internal static string PublicStatus(ItemOutcomeStatus status, OutcomeMode outcome) => status switch
    {
        ItemOutcomeStatus.Deleted => "removed",
        ItemOutcomeStatus.Reported when outcome == OutcomeMode.NotifyOnly => "flagged",
        ItemOutcomeStatus.TiedAwaitingAdmin => "tied",
        ItemOutcomeStatus.Reported => "kept",
        _ => "pending"
    };

    private static string? Limit(string? s, int max) => s is null ? null : (s.Length > max ? s[..max] : s);

    private ActionResult Apply(VoteRequest? body, Func<VoteSession, Voter, VoteRequest, OpResult> op, Func<VoteRequest, Guid>? visibilityTarget)
    {
        if (body is null)
        {
            return Error(400, "bad_request", "Invalid request body.");
        }

        var userId = CurrentUserId;
        if (userId == Guid.Empty)
        {
            return Error(401, "unauthorized");
        }

        var session = _votes.GetOpenSession();
        if (session is null)
        {
            return Error(409, VoteErrors.SessionNotOpen, "There is no active vote right now.");
        }

        if (visibilityTarget is not null)
        {
            var visible = _catalog.GetVisibleIds(session.Id, userId, session.Candidates.Select(c => c.ItemId).ToList());
            if (!visible.Contains(visibilityTarget(body)))
            {
                return Error(400, VoteErrors.ItemNotVotable, "That title is not part of this vote.");
            }
        }

        var voter = new Voter(userId, _users.GetUserName(userId));
        var r = op(session, voter, body);
        if (!r.Ok)
        {
            int status = r.Error switch
            {
                VoteErrors.NotEligible => 403,
                VoteErrors.SessionNotFound => 404,
                VoteErrors.SessionNotOpen or VoteErrors.DeadlinePassed => 409,
                _ => 400
            };
            return Error(status, r.Error!);
        }

        var fresh = _votes.Get(session.Id)!;
        return Json(new
        {
            ok = true,
            votesRemaining = VoteRules.RemainingVotes(fresh, userId, VoteKind.Remove),
            keepVotesRemaining = VoteRules.RemainingVotes(fresh, userId, VoteKind.Keep)
        });
    }

    private ActionResult Asset(string name, string contentType)
    {
        var asm = Assembly.GetExecutingAssembly();
        var stream = asm.GetManifestResourceStream("Jellyfin.Plugin.MediaVote.Assets." + name);
        if (stream is null)
        {
            return NotFound();
        }

        // Revalidate every time so phones pick up plugin updates; ETag keeps it cheap.
        var etag = "\"" + (Plugin.Instance?.Version?.ToString() ?? "0") + "-" + name + "\"";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.ETag = etag;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (Request.Headers.IfNoneMatch.ToString() == etag)
        {
            stream.Dispose();
            return StatusCode(304);
        }

        return File(stream, contentType);
    }
}
