using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaVote.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaVote.Services;

/// <summary>
/// Resolves votes at their deadline. Checks every 30 seconds (and immediately at start-up), so a vote
/// whose deadline passed while the server was offline is resolved on the next start. Resolution is
/// idempotent (the session is claimed under the store lock), so overlapping checks are harmless.
/// The "Media Vote: resolve due votes" scheduled task is a second, manual way to trigger the same check.
/// </summary>
public sealed class DeadlineService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly VoteService _votes;
    private readonly ILogger<DeadlineService> _logger;
    private readonly ITemplateStore? _templates;
    private readonly INotifier? _notifier;

    public DeadlineService(VoteService votes, ILogger<DeadlineService> logger, ITemplateStore? templates = null, INotifier? notifier = null)
    {
        _votes = votes;
        _logger = logger;
        _templates = templates;
        _notifier = notifier;
    }

    /// <summary>One check: mark interrupted runs (first time only), send reminders, resolve due votes.</summary>
    public static async Task TickAsync(VoteService votes, ILogger logger, CancellationToken ct, ITemplateStore? templates = null, INotifier? notifier = null)
    {
        try
        {
            if (KillSwitch.IsActive)
            {
                // Kill switch also pauses resolution (and therefore any deletion). Due votes resolve once it is removed.
                return;
            }

            var hours = Plugin.Instance?.Configuration.ClosingSoonHours ?? 24;
            if (hours > 0)
            {
                await votes.NotifyClosingSoonAsync(TimeSpan.FromHours(hours), ct).ConfigureAwait(false);
            }

            await votes.ResolveDueAsync(ct).ConfigureAwait(false);

            // Prepared removal lists whose auto-approve timer expired (off by default).
            await votes.AutoApproveDueAsync(ct).ConfigureAwait(false);

            // Titles Sonarr/Radarr removed: forget them in Jellyfin once their files are really gone (never deletes files).
            votes.ReconcilePendingCleanups();

            // Recurring templates only prepare a draft; it never opens without the admin.
            if (templates is not null)
            {
                var zone = TimeZoneHelper.Find(Plugin.Instance?.Configuration.TimeZoneId);
                foreach (var t in TemplateSchedule.DueDrafts(templates, votes.Clock.UtcNow, zone))
                {
                    logger.LogInformation("Media Vote: recurring template {Name} prepared a draft for review", t.Name);
                    if (notifier is not null)
                    {
                        await notifier.DraftReadyAsync(t.Name, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // the timer must keep running
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "Media Vote: error while checking vote deadlines");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give Jellyfin a moment to finish loading libraries before the first resolution.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _votes.MarkInterruptedRuns();
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await TickAsync(_votes, _logger, stoppingToken, _templates, _notifier).ConfigureAwait(false);
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
