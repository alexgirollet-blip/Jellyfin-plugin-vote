using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>How winners are processed.</summary>
public enum ExecutionMode
{
    /// <summary>Notify-only outcome: build the admin report. Read-only calls only.</summary>
    Report,

    /// <summary>Dry run: show exactly what would be deleted and which external actions would run. Read-only calls only.</summary>
    DryRun,

    /// <summary>Automatic deletion.</summary>
    Delete
}

/// <summary>
/// Processes winners in a strict order of operations.
/// <para>Delete mode:</para>
/// <list type="number">
/// <item>Pre-flight for ALL winners: item still exists, not protected, every delete path writable.
///   If any check fails, nothing at all is deleted.</item>
/// <item>Per item, in rank order: re-check existence and permission; run Radarr/Sonarr step; run Jellyseerr step;
///   then delete through Jellyfin. If an external step fails, that item is skipped and the next item is processed.
///   If the Jellyfin deletion (or the re-check) fails, processing stops and the remaining items are not attempted.</item>
/// </list>
/// Report and DryRun modes only call <see cref="IExternalService.PlanAsync"/> (read-only) and never delete.
/// </summary>
public sealed class ResolutionExecutor
{
    private readonly IMediaGateway _media;
    private readonly IReadOnlyList<IExternalService> _services;
    private readonly IAuditLog _audit;
    private readonly IClock _clock;

    /// <param name="media">Library gateway.</param>
    /// <param name="services">External services in execution order (Radarr, Sonarr, then Jellyseerr).</param>
    /// <param name="audit">Audit sink.</param>
    /// <param name="clock">Clock.</param>
    public ResolutionExecutor(IMediaGateway media, IReadOnlyList<IExternalService> services, IAuditLog audit, IClock clock)
    {
        _media = media;
        _services = services;
        _audit = audit;
        _clock = clock;
    }

    public async Task<List<ItemOutcome>> RunAsync(
        Guid sessionId,
        IReadOnlyList<ItemTally> winners,
        IReadOnlyDictionary<Guid, CandidateItem> candidates,
        ExecutionMode mode,
        CancellationToken cancellationToken)
    {
        var outcomes = winners.Select(w => NewOutcome(w, candidates)).ToList();
        if (mode != ExecutionMode.Delete)
        {
            foreach (var outcome in outcomes)
            {
                await ReportAsync(outcome, cancellationToken).ConfigureAwait(false);
            }

            return outcomes;
        }

        // ---- Phase 1: pre-flight for every winner. Any failure aborts the whole run. ----
        var live = new Dictionary<Guid, MediaItemSnapshot>();
        bool preflightFailed = false;
        foreach (var outcome in outcomes)
        {
            var (item, problem) = Preflight(outcome.ItemId);
            if (item is not null)
            {
                FillFromLive(outcome, item);
                live[outcome.ItemId] = item;
            }

            if (problem is not null)
            {
                outcome.Status = ItemOutcomeStatus.PreflightFailed;
                outcome.Message = problem;
                preflightFailed = true;
                _audit.Write("preflight_failed", "system", sessionId, $"{outcome.Name} ({outcome.ItemId}): {problem}");
            }
        }

        if (preflightFailed)
        {
            foreach (var o in outcomes.Where(o => o.Status != ItemOutcomeStatus.PreflightFailed))
            {
                o.Status = ItemOutcomeStatus.NotAttempted;
                o.Message = "Not attempted: a pre-flight check failed for another winner, so nothing was deleted.";
            }

            return outcomes;
        }

        // ---- Phase 2: per item, external steps first, then Jellyfin deletion. ----
        bool stop = false;
        foreach (var outcome in outcomes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stop)
            {
                outcome.Status = ItemOutcomeStatus.NotAttempted;
                outcome.Message = "Not attempted: an earlier deletion failed, so processing stopped.";
                continue;
            }

            // Re-check right before acting: the library may have changed since pre-flight.
            var (item, problem) = Preflight(outcome.ItemId);
            if (item is null || problem is not null)
            {
                outcome.Status = ItemOutcomeStatus.PreflightFailed;
                outcome.Message = problem ?? "Item no longer exists.";
                _audit.Write("recheck_failed", "system", sessionId, $"{outcome.Name} ({outcome.ItemId}): {outcome.Message}");
                stop = true;
                continue;
            }

            bool externalFailed = false;
            foreach (var service in _services.Where(s => s.AppliesTo(item.Kind)))
            {
                ExternalStepResult step;
                if (!service.HasConnection)
                {
                    step = new ExternalStepResult { Service = service.Name, Action = service.ActionName, Status = ExternalStepStatus.NotConfigured };
                }
                else if (service.IsNoOp)
                {
                    step = new ExternalStepResult { Service = service.Name, Action = service.ActionName, Status = ExternalStepStatus.Skipped, Message = "Admin chose \"Do nothing\"." };
                }
                else
                {
                    step = await SafeExecuteAsync(service, item, cancellationToken).ConfigureAwait(false);
                    _audit.Write("external_" + step.Status.ToLowerInvariant(), "system", sessionId, $"{service.Name} {service.ActionName} for {item.Name} ({item.ItemId}): {step.Message}");
                }

                outcome.ExternalSteps.Add(step);
                if (step.IsFailure)
                {
                    externalFailed = true;
                    break;
                }
            }

            if (externalFailed)
            {
                outcome.Status = ItemOutcomeStatus.SkippedExternalFailure;
                outcome.Message = "Not deleted: a Radarr/Sonarr/Jellyseerr step failed. Fix it and retry from the results page.";
                continue;
            }

            try
            {
                _media.Delete(item.ItemId);
                outcome.Status = ItemOutcomeStatus.Deleted;
                outcome.CompletedUtc = _clock.UtcNow;
                outcome.Message = null;
                _audit.Write("deleted", "system", sessionId, string.Create(CultureInfo.InvariantCulture, $"{item.Name} ({item.ItemId}), {item.SizeBytes} bytes, paths: {string.Join(" | ", item.DeletePaths)}"));
            }
#pragma warning disable CA1031 // any failure must stop the run and be reported, never crash the server
            catch (Exception ex)
#pragma warning restore CA1031
            {
                outcome.Status = ItemOutcomeStatus.DeleteFailed;
                outcome.Message = "Jellyfin could not delete this item: " + ex.Message;
                _audit.Write("delete_failed", "system", sessionId, $"{item.Name} ({item.ItemId}): {ex.GetType().Name}: {ex.Message}");
                stop = true;
            }
        }

        return outcomes;
    }

    private static ItemOutcome NewOutcome(ItemTally w, IReadOnlyDictionary<Guid, CandidateItem> candidates)
    {
        candidates.TryGetValue(w.ItemId, out var c);
        return new ItemOutcome
        {
            ItemId = w.ItemId,
            Name = c?.Name ?? w.ItemId.ToString(),
            Kind = c?.Kind ?? MediaKind.Movie,
            Score = w.Score,
            SizeBytes = c?.SizeBytes ?? 0,
            Paths = c?.Path is null ? new List<string>() : new List<string> { c.Path },
            Status = ItemOutcomeStatus.NotAttempted
        };
    }

    private static void FillFromLive(ItemOutcome outcome, MediaItemSnapshot item)
    {
        outcome.Name = item.Name;
        outcome.Kind = item.Kind;
        outcome.Paths = item.DeletePaths.ToList();
        if (item.SizeBytes > 0)
        {
            outcome.SizeBytes = item.SizeBytes;
        }
    }

    private static async Task<ExternalStepResult> SafeExecuteAsync(IExternalService service, MediaItemSnapshot item, CancellationToken ct)
    {
        try
        {
            return await service.ExecuteAsync(item, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return new ExternalStepResult { Service = service.Name, Action = service.ActionName, Status = ExternalStepStatus.Failed, Message = ex.Message };
        }
    }

    private static async Task<ExternalStepResult> SafePlanAsync(IExternalService service, MediaItemSnapshot item, CancellationToken ct)
    {
        try
        {
            return await service.PlanAsync(item, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return new ExternalStepResult { Service = service.Name, Action = service.ActionName, Status = ExternalStepStatus.Failed, Message = ex.Message };
        }
    }

    private (MediaItemSnapshot? Item, string? Problem) Preflight(Guid itemId)
    {
        try
        {
            return PreflightCore(itemId);
        }
#pragma warning disable CA1031 // a lookup failure is a pre-flight failure: nothing gets deleted
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return (null, "Pre-flight check could not run: " + ErrorText.Describe(ex));
        }
    }

    private (MediaItemSnapshot? Item, string? Problem) PreflightCore(Guid itemId)
    {
        var item = _media.GetItem(itemId);
        if (item is null)
        {
            return (null, "The item no longer exists in Jellyfin.");
        }

        if (item.IsProtected)
        {
            return (item, "The item (or its library) is protected and can never be deleted by a vote.");
        }

        var perm = _media.CheckDeletePermission(item);
        if (!perm.Ok)
        {
            return (item, perm.Problem ?? "No write permission.");
        }

        return (item, null);
    }

    private async Task ReportAsync(ItemOutcome outcome, CancellationToken ct)
    {
        outcome.Status = ItemOutcomeStatus.Reported;
        MediaItemSnapshot? item;
        try
        {
            item = _media.GetItem(outcome.ItemId);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            outcome.Message = "Could not read this item from Jellyfin: " + ErrorText.Describe(ex);
            return;
        }

        if (item is null)
        {
            outcome.Message = "The item no longer exists in Jellyfin.";
            return;
        }

        FillFromLive(outcome, item);
        var notes = new List<string>();
        if (item.IsProtected)
        {
            notes.Add("Protected: would never be deleted.");
        }

        PermissionCheck perm;
        try
        {
            perm = _media.CheckDeletePermission(item);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            perm = new PermissionCheck(false, ErrorText.Describe(ex));
        }

        if (!perm.Ok)
        {
            notes.Add("Write permission problem: " + perm.Problem);
        }

        foreach (var service in _services.Where(s => s.AppliesTo(item.Kind)))
        {
            ExternalStepResult step = service.HasConnection
                ? await SafePlanAsync(service, item, ct).ConfigureAwait(false)
                : new ExternalStepResult { Service = service.Name, Action = service.ActionName, Status = ExternalStepStatus.NotConfigured };
            outcome.ExternalSteps.Add(step);
        }

        outcome.Message = notes.Count > 0 ? string.Join(" ", notes) : null;
    }
}
