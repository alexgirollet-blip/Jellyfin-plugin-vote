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
/// Runs at server start-up (after all plugins are loaded): injects the client script and registers the
/// optional entry points. Same pattern as File Transformation's other consumers (Home Screen Sections,
/// Plugin Pages, Custom Tabs). Can also be run manually from Dashboard > Scheduled Tasks.
/// </summary>
public sealed class StartupTask : IScheduledTask
{
    private readonly WebInjectionService _injection;
    private readonly EntryPointRegistrar _registrar;
    private readonly IServiceProvider _services;
    private readonly ILogger<StartupTask> _logger;
    private static bool _subscribed;

    public StartupTask(WebInjectionService injection, EntryPointRegistrar registrar, IServiceProvider services, ILogger<StartupTask> logger)
    {
        _injection = injection;
        _registrar = registrar;
        _services = services;
        _logger = logger;
    }

    public string Name => "Media Vote: start-up";

    public string Key => "MediaVoteStartup";

    public string Description => "Injects the Media Vote script into the web client and registers the Vote tab, sidebar page and home section.";

    public string Category => "Media Vote";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        HomeSectionResults.Services = _services;
        _injection.Run();
        progress.Report(50);
        _registrar.RegisterAll();
        if (!_subscribed && Plugin.Instance is not null)
        {
            _subscribed = true;
            Plugin.Instance.ConfigurationSaved += (_, _) =>
            {
                try
                {
                    _registrar.OnConfigurationChanged();
                }
#pragma warning disable CA1031
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _logger.LogWarning(ex, "Media Vote: re-registering entry points failed");
                }
            };
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
