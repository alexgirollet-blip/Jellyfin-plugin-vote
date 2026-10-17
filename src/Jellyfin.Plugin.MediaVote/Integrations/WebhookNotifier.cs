using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaVote.Configuration;
using Jellyfin.Plugin.MediaVote.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaVote.Integrations;

/// <summary>Optional Discord or generic JSON webhook for "vote opened", "closing soon" and "results".</summary>
public sealed class WebhookNotifier : INotifier
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly Func<PluginConfiguration> _config;
    private readonly ILogger _logger;

    public WebhookNotifier(IHttpClientFactory httpFactory, Func<PluginConfiguration> config, ILogger logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public Task VoteOpenedAsync(VoteSession session, CancellationToken cancellationToken)
        => _config().WebhookOnOpened ? SendAsync("vote_opened", session, $"🗳️ New vote: {session.Settings.Title}", OpenedText(session), cancellationToken) : Task.CompletedTask;

    public Task ClosingSoonAsync(VoteSession session, IReadOnlyList<string> nonVoters, CancellationToken cancellationToken)
        => _config().WebhookOnClosingSoon ? SendAsync("vote_closing_soon", session, $"⏰ Vote closing soon: {session.Settings.Title}", ClosingText(session, nonVoters, _config().WebhookIncludeNonVoters), cancellationToken) : Task.CompletedTask;

    public Task ApprovalNeededAsync(VoteSession session, CancellationToken cancellationToken)
        => _config().WebhookOnApproval ? SendAsync("approval_needed", session, $"✋ Approval needed: {session.Settings.Title}", ApprovalText(session), cancellationToken) : Task.CompletedTask;

    public Task DraftReadyAsync(string templateName, CancellationToken cancellationToken)
        => _config().WebhookOnApproval
            ? SendAsync("draft_ready", new VoteSession { Settings = new SessionSettings { Title = templateName } }, $"📝 Recurring vote ready: {templateName}", "A recurring vote is ready. Review and open it from Dashboard → Plugins → Media Vote; it does not open by itself.", cancellationToken)
            : Task.CompletedTask;

    /// <summary>"Closing soon" text, with the "who has not voted yet" reminder when enabled. Public for unit tests.</summary>
    public static string ClosingText(VoteSession s, IReadOnlyList<string> nonVoters, bool includeNames)
    {
        var text = $"Voting closes {FormatLocal(s.Settings.DeadlineUtc)}. Have your say!";
        if (includeNames && nonVoters.Count > 0)
        {
            text += "\nNot voted yet: " + string.Join(", ", nonVoters.Take(30)) + (nonVoters.Count > 30 ? " and " + (nonVoters.Count - 30) + " more" : string.Empty) + ".";
        }

        return text;
    }

    /// <summary>"Approval needed" text listing the prepared titles. Public for unit tests.</summary>
    public static string ApprovalText(VoteSession s)
    {
        var r = s.Result;
        if (r is null)
        {
            return "A removal list is waiting for your approval.";
        }

        var planned = r.Items.Where(i => r.PlannedItemIds.Contains(i.ItemId)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{planned.Count} title(s), {DriveMath.Bytes(planned.Sum(i => i.ArrSizeOnDisk > 0 ? i.ArrSizeOnDisk : i.SizeBytes))}, are ready to be deleted by Sonarr/Radarr. Nothing happens until you approve in Dashboard → Plugins → Media Vote.");
        foreach (var i in planned.Take(25))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"• {i.Name} — {DriveMath.Bytes(i.ArrSizeOnDisk > 0 ? i.ArrSizeOnDisk : i.SizeBytes)}");
        }

        if (r.ApprovalDueUtc is { } due)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Approved automatically {FormatLocal(due)} unless you act first.");
        }

        return sb.ToString();
    }

    public Task ResultsAsync(VoteSession session, CancellationToken cancellationToken)
        => _config().WebhookOnResults ? SendAsync("vote_results", session, $"📋 Vote results: {session.Settings.Title}", ResultsText(session), cancellationToken) : Task.CompletedTask;

    /// <summary>Builds the JSON body. Public for unit tests.</summary>
    public static string BuildPayload(WebhookKind kind, string evt, VoteSession s, string title, string text)
    {
        object body = kind == WebhookKind.Discord
            ? new { content = (string?)null, embeds = new[] { new { title = Trim(title, 256), description = Trim(text, 4000) } } }
            : new
            {
                @event = evt,
                sessionId = s.Id,
                title = s.Settings.Title,
                deadlineUtc = s.Settings.DeadlineUtc,
                status = s.Status.ToString(),
                message = text,
                results = s.Result?.Items.Select(i => new { i.Name, kind = i.Kind.ToString(), status = i.Status.ToString(), i.Score, i.SizeBytes })
            };
        return JsonSerializer.Serialize(body);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string OpenedText(VoteSession s)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(s.Settings.Description))
        {
            sb.AppendLine(s.Settings.Description);
        }

        sb.Append(CultureInfo.InvariantCulture, $"{s.Candidates.Count} titles, {s.Settings.VotesPerUser} vote(s) each. Closes {FormatLocal(s.Settings.DeadlineUtc)}. Vote from the banner on your Jellyfin home page.");
        return sb.ToString();
    }

    private static string ResultsText(VoteSession s)
    {
        var r = s.Result;
        if (r is null)
        {
            return "No result.";
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Turnout: {r.VoterCount}/{r.EligibleVoterCount} ({r.TurnoutPercent}%).");
        foreach (var i in r.Items)
        {
            string verb = i.Status switch
            {
                ItemOutcomeStatus.Removed or ItemOutcomeStatus.Deleted => "removed via " + (i.ArrInstance ?? "Sonarr/Radarr"),
                ItemOutcomeStatus.Reported => r.Outcome == OutcomeMode.NotifyOnly ? "selected (admin will review)" : "kept",
                ItemOutcomeStatus.TiedAwaitingAdmin => "tied – admin will decide",
                ItemOutcomeStatus.Protected => "kept (protected)",
                ItemOutcomeStatus.NotMatched => "not managed by Sonarr/Radarr – admin will remove it by hand",
                _ => "not removed yet – needs the admin (" + i.Status + ")"
            };
            sb.AppendLine(CultureInfo.InvariantCulture, $"• {i.Name} — {verb}");
        }

        foreach (var n in r.Notes)
        {
            sb.AppendLine(n);
        }

        return sb.ToString();
    }

    private static string FormatLocal(DateTime utc)
    {
        var zone = TimeZoneHelper.Find(Plugin.Instance?.Configuration.TimeZoneId);
        return TimeZoneHelper.UtcToLocal(utc, zone).ToString("ddd d MMM yyyy HH:mm", CultureInfo.GetCultureInfo("en-GB")) + " (" + zone.Id + ")";
    }

    private async Task SendAsync(string evt, VoteSession s, string title, string text, CancellationToken ct)
    {
        var cfg = _config();
        if (string.IsNullOrWhiteSpace(cfg.WebhookUrl) || !Uri.TryCreate(cfg.WebhookUrl.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }

        try
        {
            using var http = _httpFactory.CreateClient("MediaVote");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var content = new StringContent(BuildPayload(cfg.WebhookKind, evt, s, title, text), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(uri, content, timeout.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Media Vote webhook for {Event} returned HTTP {Status}", evt, (int)resp.StatusCode);
            }
        }
#pragma warning disable CA1031 // a webhook must never break voting or resolution
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Never log the URL: Discord webhook URLs contain a secret token.
            _logger.LogWarning("Media Vote webhook for {Event} failed: {Message}", evt, ex.Message);
        }
    }
}
