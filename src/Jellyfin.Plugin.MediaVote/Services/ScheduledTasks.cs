using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaVote.Core;
using Jellyfin.Plugin.MediaVote.Web;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaVote.Services;

/// <summary>
/// Runs at server start-up (after all plugins are loaded):
/// <list type="number">
/// <item>Self-check of every Jellyfin API Media Vote uses (results on the Status tab).</item>
/// <item>Kill switch check: with the DISABLE file present, nothing below happens.</item>
/// <item>Web injection (only if the banner or the Vote tab is on, and only after the callback self-test passes).</item>
/// <item>Entry points with Plugin Pages / Home Screen Sections (only those switched on); read-only Custom Tabs check.</item>
/// </list>
/// Every step is isolated; a failure is logged and shown, never thrown.
/// </summary>
public sealed class StartupTask : IScheduledTask
{
    private static bool _subscribed;
    private readonly WebInjectionService _injection;
    private readonly EntryPointRegistrar _registrar;
    private readonly ApiSelfCheck _selfCheck;
    private readonly IServiceProvider _services;
    private readonly ILogger<StartupTask> _logger;

    public StartupTask(WebInjectionService injection, EntryPointRegistrar registrar, ApiSelfCheck selfCheck, IServiceProvider services, ILogger<StartupTask> logger)
    {
        _injection = injection;
        _registrar = registrar;
        _selfCheck = selfCheck;
        _services = services;
        _logger = logger;
    }

    public string Name => "Media Vote: start-up";

    public string Key => "MediaVoteStartup";

    public string Description => "Checks the Jellyfin APIs Media Vote uses and applies the enabled entry points (nothing is injected unless you switch an entry point on).";

    public string Category => "Media Vote";

    /// <summary>Applies everything; shared by start-up, settings changes and the Status tab's "re-apply" button. Never throws.</summary>
    public static void ApplyAll(WebInjectionService injection, EntryPointRegistrar registrar, ILogger logger)
    {
        try
        {
            if (KillSwitch.IsActive)
            {
                logger.LogWarning("Media Vote: DISABLE file found at {Path}: skipping all web injection and entry-point registration", KillSwitch.FilePath);
            }

            injection.Apply();
            registrar.Apply();
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "Media Vote: applying entry points failed");
        }
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            HomeSectionResults.Services = _services;
            _selfCheck.Run();
            progress.Report(40);
            ApplyAll(_injection, _registrar, _logger);
            progress.Report(80);
            if (!_subscribed && Plugin.Instance is not null)
            {
                _subscribed = true;
                var injection = _injection;
                var registrar = _registrar;
                var logger = _logger;
                Plugin.Instance.ConfigurationSaved += (_, _) => ApplyAll(injection, registrar, logger);
                Plugin.UninstallHooks.Add(() =>
                {
                    injection.Unregister();
                    registrar.UnregisterAll();
                });
            }
        }
#pragma warning disable CA1031 // start-up must never fail the server
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Media Vote: start-up task failed: {Message}", ErrorText.Describe(ex));
        }

        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }
}

/// <summary>Manual/backup trigger for vote resolution. The background timer normally does this every 30 s.</summary>
public sealed class ResolveVotesTask : IScheduledTask
{
    private readonly VoteService _votes;
    private readonly ILogger<ResolveVotesTask> _logger;

    public ResolveVotesTask(VoteService votes, ILogger<ResolveVotesTask> logger)
    {
        _votes = votes;
        _logger = logger;
    }

    public string Name => "Media Vote: resolve due votes";

    public string Key => "MediaVoteResolve";

    public string Description => "Resolves votes whose deadline has passed and sends closing-soon reminders.";

    public string Category => "Media Vote";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => DeadlineService.TickAsync(_votes, _logger, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks };
    }
}
