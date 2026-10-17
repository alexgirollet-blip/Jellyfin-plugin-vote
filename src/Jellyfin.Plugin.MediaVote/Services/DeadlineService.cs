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

    public DeadlineService(VoteService votes, ILogger<DeadlineService> logger)
    {
        _votes = votes;
        _logger = logger;
    }

    /// <summary>One check: mark interrupted runs (first time only), send reminders, resolve due votes.</summary>
    public static async Task TickAsync(VoteService votes, ILogger logger, CancellationToken ct)
    {
        try
        {
            var hours = Plugin.Instance?.Configuration.ClosingSoonHours ?? 24;
            if (hours > 0)
            {
                await votes.NotifyClosingSoonAsync(TimeSpan.FromHours(hours), ct).ConfigureAwait(false);
            }

            await votes.ResolveDueAsync(ct).ConfigureAwait(false);
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
            await TickAsync(_votes, _logger, stoppingToken).ConfigureAwait(false);
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
