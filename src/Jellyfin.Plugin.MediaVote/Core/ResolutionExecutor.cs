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
    /// <summary>Notify-only outcome: build the admin report. Read-only (GET) calls only.</summary>
    Report,

    /// <summary>Dry run: show exactly which Sonarr/Radarr entries would be deleted and how much space that frees. GET calls only.</summary>
    DryRun,

    /// <summary>"Delete via Sonarr/Radarr".</summary>
    Remove
}

/// <summary>Limits for one run.</summary>
/// <param name="MaxRemovals">Most titles this vote may remove in total.</param>
/// <param name="AlreadyRemoved">Titles this vote already removed in earlier runs (counted against the maximum).</param>
public sealed record ExecutionLimits(int MaxRemovals, int AlreadyRemoved)
{
    public static ExecutionLimits None { get; } = new(int.MaxValue, 0);
}

/// <summary>
/// Processes winners. Media Vote never deletes, moves or changes a media file: removal is done only by the Sonarr
/// (series) or Radarr (movies) instance that manages the title.
/// <para>Remove mode, per winner in rank order, each one isolated (a failure never stops the others):</para>
/// <list type="number">
/// <item>Skip if the DISABLE file exists, the title is protected, or the vote's maximum is reached.</item>
/// <item>Match it to exactly one Sonarr/Radarr entry (provider ids first). Not matched or ambiguous: listed for removal by hand.</item>
/// <item>Re-check right before acting that the entry still exists (GET by id).</item>
/// <item>Jellyseerr step, where configured. If it fails, the title is not deleted.</item>
/// <item>DELETE with deleteFiles=true. Called once: there is no automatic retry, and every later attempt starts by
///   looking the title up again, so the same entry can never be deleted twice.</item>
/// <item>Verify by asking Sonarr/Radarr again that the entry is gone.</item>
/// <item>Reconcile Jellyfin: once the files are gone from disk, drop the stale entry without deleting files; until then a
///   library refresh is requested and the check is repeated later.</item>
/// </list>
/// Report and DryRun use a read-only backend and never change anything anywhere.
/// </summary>
public sealed class ResolutionExecutor
{
    private readonly IMediaGateway _media;
    private readonly IRemovalBackend _backend;
    private readonly IAuditLog _audit;
    private readonly IClock _clock;
    private readonly Func<bool> _isPaused;

    public ResolutionExecutor(IMediaGateway media, IRemovalBackend backend, IAuditLog audit, IClock clock, Func<bool>? isPaused = null)
    {
        _media = media;
        _backend = backend;
        _audit = audit;
        _clock = clock;
        _isPaused = isPaused ?? (() => false);
    }

    /// <param name="sessionId">Vote id (audit).</param>
    /// <param name="winners">Items to process, in rank order.</param>
    /// <param name="candidates">The vote's frozen candidate snapshot.</param>
    /// <param name="mode">Report, dry run or remove.</param>
    /// <param name="limits">Maximum titles per vote.</param>
    /// <param name="previous">Earlier outcomes of these items (a retry), used to recognise an entry an earlier attempt already deleted.</param>
    /// <param name="onItemDone">Called after each item so progress is saved even if the server stops mid-run.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<List<ItemOutcome>> RunAsync(
        Guid sessionId,
        IReadOnlyList<ItemTally> winners,
        IReadOnlyDictionary<Guid, CandidateItem> candidates,
        ExecutionMode mode,
        ExecutionLimits limits,
        IReadOnlyDictionary<Guid, ItemOutcome>? previous = null,
        Action<ItemOutcome>? onItemDone = null,
        CancellationToken cancellationToken = default)
    {
        if (mode == ExecutionMode.Remove && _backend.ReadOnly)
        {
            throw new InvalidOperationException("Remove mode needs a backend that may delete.");
        }

        var outcomes = new List<ItemOutcome>();
        if (winners.Count == 0)
        {
            return outcomes;
        }

        ArrLibrary library;
        try
        {
            library = await _backend.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // LoadAsync should never throw; if it does, every item reports it instead of crashing
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            library = new ArrLibrary(Array.Empty<ArrEntry>(), new Dictionary<ArrKind, int> { [ArrKind.Radarr] = 1, [ArrKind.Sonarr] = 1 }, new Dictionary<ArrKind, List<string>> { [ArrKind.Radarr] = new() { "Radarr" }, [ArrKind.Sonarr] = new() { "Sonarr" } });
            library.Errors.Add(ex.Message);
        }

        int removed = limits.AlreadyRemoved;
        foreach (var w in winners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates.TryGetValue(w.ItemId, out var candidate);
            var outcome = NewOutcome(w, candidate);
            ItemOutcome? prev = null;
            previous?.TryGetValue(w.ItemId, out prev);
            if (mode == ExecutionMode.Remove)
            {
                if (await RemoveOneAsync(sessionId, outcome, candidate, library, removed, limits.MaxRemovals, prev, cancellationToken).ConfigureAwait(false))
                {
                    removed++;
                }
            }
            else
            {
                await DescribeOneAsync(outcome, candidate, library, mode, cancellationToken).ConfigureAwait(false);
            }

            outcomes.Add(outcome);
            onItemDone?.Invoke(outcome);
        }

        return outcomes;
    }

    /// <returns>True if Sonarr/Radarr removed the title in this call.</returns>
    private async Task<bool> RemoveOneAsync(Guid sessionId, ItemOutcome outcome, CandidateItem? candidate, ArrLibrary library, int removedSoFar, int max, ItemOutcome? prev, CancellationToken ct)
    {
        bool deleteSent = false;
        try
        {
            if (_isPaused())
            {
                Set(outcome, ItemOutcomeStatus.NotAttempted, "Paused: the DISABLE file exists, so nothing was sent to Sonarr/Radarr. Remove the file, restart Jellyfin, then use Retry.", attention: true);
                return false;
            }

            var (live, lookupError) = GetLive(outcome.ItemId);
            if (live is not null)
            {
                FillFromLive(outcome, live);
                if (live.IsProtected)
                {
                    Set(outcome, ItemOutcomeStatus.Protected, "Protected (Settings): never removed by a vote.", attention: false);
                    return false;
                }
            }

            if (removedSoFar >= max)
            {
                Set(outcome, ItemOutcomeStatus.NotAttempted, string.Create(CultureInfo.InvariantCulture, $"Not attempted: this vote reached its safety maximum of {max} title(s). Raise \"Most titles one vote may remove\" in Settings and use Retry if it should go."), attention: true);
                return false;
            }

            var key = KeyFor(live, candidate, outcome);
            var match = ArrMatcher.Match(key, library);
            ArrEntry? entry = match.Entry;
            if (!match.IsMatched)
            {
                // A retry of a title an earlier attempt already deleted: its old entry is simply gone now.
                if (await GoneSincePreviousAttemptAsync(prev, library, ct).ConfigureAwait(false) is { } gone)
                {
                    FillArr(outcome, gone);
                    _audit.Write("arr_confirmed_gone", "system", sessionId, $"{outcome.Name} ({outcome.ItemId}): {gone.Describe()} no longer exists (deleted by an earlier attempt).");
                    await MarkRemovedAsync(sessionId, outcome, live, "Confirmed gone from " + gone.Instance + " on re-check: an earlier attempt already deleted it.").ConfigureAwait(false);
                    return true;
                }

                var status = match.State == ArrMatchState.Unreachable ? ItemOutcomeStatus.ArrUnreachable : ItemOutcomeStatus.NotMatched;
                Set(outcome, status, match.Reason + (status == ItemOutcomeStatus.NotMatched ? " Nothing was deleted; remove it by hand if it should go." : " Nothing was deleted; use Retry when it is back."), attention: true);
                if (lookupError is not null)
                {
                    outcome.Message += " (" + lookupError + ")";
                }

                _audit.Write(status == ItemOutcomeStatus.ArrUnreachable ? "arr_unreachable" : "arr_not_matched", "system", sessionId, $"{outcome.Name} ({outcome.ItemId}): {match.Reason}");
                return false;
            }

            FillArr(outcome, entry!);

            // Re-check right before acting.
            ArrEntry? fresh;
            try
            {
                fresh = await _backend.RefetchAsync(entry!, ct).ConfigureAwait(false);
            }
            catch (ExternalServiceException ex)
            {
                Set(outcome, ItemOutcomeStatus.ArrUnreachable, ex.Message + " Nothing was deleted; use Retry when it is back.", attention: true);
                _audit.Write("arr_unreachable", "system", sessionId, $"{outcome.Name} ({outcome.ItemId}): {ex.Message}");
                return false;
            }

            if (fresh is null)
            {
                _audit.Write("arr_already_gone", "system", sessionId, $"{outcome.Name} ({outcome.ItemId}): {entry!.Describe()} disappeared before the delete; nothing was sent.");
                await MarkRemovedAsync(sessionId, outcome, live, entry!.Instance + " no longer lists it (removed outside Media Vote just now); nothing was deleted by Media Vote.").ConfigureAwait(false);
                return false;
            }

            // Jellyseerr first, where configured. A failure keeps the title.
            if (_backend.HasSeerrStep)
            {
                var steps = await _backend.SeerrAsync(key, execute: true, ct).ConfigureAwait(false);
                outcome.ExternalSteps.AddRange(steps);
                foreach (var st in steps)
                {
                    _audit.Write("jellyseerr_" + st.Status.ToLowerInvariant(), "system", sessionId, $"{st.Service} for {outcome.Name} ({outcome.ItemId}): {st.Message}");
                }

                if (steps.Any(s => s.IsFailure))
                {
                    Set(outcome, ItemOutcomeStatus.ExternalStepFailed, "Not deleted: the Jellyseerr step failed (" + string.Join(" ", steps.Where(s => s.IsFailure).Select(s => s.Message)) + "). Fix it, then use Retry.", attention: true);
                    return false;
                }
            }

            _audit.Write("arr_delete_requested", "system", sessionId, string.Create(CultureInfo.InvariantCulture, $"{outcome.Name} ({outcome.ItemId}) → {fresh.Describe()}, deleteFiles=true, {fresh.SizeOnDisk} bytes"));
            deleteSent = true;
            string? deleteError = null;
            try
            {
                await _backend.DeleteAsync(fresh, ct).ConfigureAwait(false);
            }
            catch (ExternalServiceException ex)
            {
                deleteError = ex.Message;
            }

            // Verify: ask again.
            ArrEntry? after;
            try
            {
                after = await _backend.RefetchAsync(fresh, ct).ConfigureAwait(false);
            }
            catch (ExternalServiceException ex)
            {
                Set(outcome, ItemOutcomeStatus.VerificationFailed, "Could not ask " + fresh.Instance + " whether it is gone: " + ex.Message + (deleteError is null ? string.Empty : " The delete call also failed: " + deleteError) + " Check " + fresh.Instance + "; Retry looks it up again and never deletes twice.", attention: true);
                _audit.Write("arr_verify_failed", "system", sessionId, $"{outcome.Name} ({outcome.ItemId}): {ex.Message}");
                return false;
            }

            if (after is not null)
            {
                if (deleteError is not null)
                {
                    Set(outcome, ItemOutcomeStatus.DeleteRefused, deleteError, attention: true);
                    _audit.Write("arr_delete_refused", "system", sessionId, $"{outcome.Name} ({outcome.ItemId}): {deleteError}");
                }
                else
                {
                    Set(outcome, ItemOutcomeStatus.VerificationFailed, fresh.Instance + " accepted the delete but still lists the title. Check " + fresh.Instance + "; Retry looks it up again and never deletes twice.", attention: true);
                    _audit.Write("arr_verify_failed", "system", sessionId, $"{outcome.Name} ({outcome.ItemId}): still listed after delete");
                }

                return false;
            }

            _audit.Write("arr_removed", "system", sessionId, string.Create(CultureInfo.InvariantCulture, $"{outcome.Name} ({outcome.ItemId}): {fresh.Describe()} deleted with its files and confirmed gone, {fresh.SizeOnDisk} bytes"));
            await MarkRemovedAsync(sessionId, outcome, live, deleteError is null
                ? "Deleted with its files by " + fresh.Instance + " and confirmed gone."
                : "Confirmed gone from " + fresh.Instance + " (its delete call reported: " + deleteError + ").").ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // one title's failure must never stop the others or the server
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var status = deleteSent ? ItemOutcomeStatus.VerificationFailed : ItemOutcomeStatus.NotAttempted;
            Set(outcome, status, (deleteSent ? "The delete was sent, then something went wrong: " : "Not attempted: ") + ErrorText.Describe(ex) + " Retry looks it up again and never deletes twice.", attention: true);
            _audit.Write("item_error", "system", sessionId, $"{outcome.Name} ({outcome.ItemId}): {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private async Task<ArrEntry?> GoneSincePreviousAttemptAsync(ItemOutcome? prev, ArrLibrary library, CancellationToken ct)
    {
        if (prev?.ArrId is not int id || string.IsNullOrEmpty(prev.ArrInstance))
        {
            return null;
        }

        var kind = prev.Kind == MediaKind.Series ? ArrKind.Sonarr : ArrKind.Radarr;
        if (library.Unreachable(kind).Contains(prev.ArrInstance))
        {
            return null;
        }

        var old = new ArrEntry { Instance = prev.ArrInstance, Kind = kind, Id = id, Title = prev.ArrTitle ?? prev.Name, SizeOnDisk = prev.ArrSizeOnDisk };
        try
        {
            return await _backend.RefetchAsync(old, ct).ConfigureAwait(false) is null ? old : null;
        }
        catch (ExternalServiceException)
        {
            return null;
        }
    }

    private Task MarkRemovedAsync(Guid sessionId, ItemOutcome outcome, MediaItemSnapshot? live, string message)
    {
        Set(outcome, ItemOutcomeStatus.Removed, message, attention: false);
        outcome.CompletedUtc = _clock.UtcNow;
        if (live is null)
        {
            outcome.JellyfinCleanup = JellyfinCleanup.Done;
            outcome.CleanupNote = "Jellyfin no longer lists it.";
            return Task.CompletedTask;
        }

        CleanupResult r;
        try
        {
            r = _media.CleanUpAfterRemoval(live.ItemId, live.DeletePaths);
        }
#pragma warning disable CA1031 // the removal already happened; a cleanup problem is only reported
        catch (Exception ex)
#pragma warning restore CA1031
        {
            r = new CleanupResult(JellyfinCleanup.Failed, "Jellyfin clean-up failed: " + ErrorText.Describe(ex) + " The title disappears at the next library scan.");
        }

        outcome.JellyfinCleanup = r.State;
        outcome.CleanupNote = r.Message;
        _audit.Write("jellyfin_cleanup_" + r.State.ToString().ToLowerInvariant(), "system", sessionId, $"{outcome.Name} ({outcome.ItemId}): {r.Message}");
        return Task.CompletedTask;
    }

    private async Task DescribeOneAsync(ItemOutcome outcome, CandidateItem? candidate, ArrLibrary library, ExecutionMode mode, CancellationToken ct)
    {
        outcome.Status = ItemOutcomeStatus.Reported;
        try
        {
            var (live, lookupError) = GetLive(outcome.ItemId);
            if (live is not null)
            {
                FillFromLive(outcome, live);
            }

            if (live?.IsProtected == true)
            {
                outcome.Message = "Protected (Settings): would never be removed.";
                return;
            }

            var key = KeyFor(live, candidate, outcome);
            var match = ArrMatcher.Match(key, library);
            if (match.IsMatched)
            {
                FillArr(outcome, match.Entry!);
                outcome.Message = (mode == ExecutionMode.DryRun ? "Would be deleted with its files by " : "Removable via ") + match.Entry!.Describe() + string.Create(CultureInfo.InvariantCulture, $", {Gb(match.Entry.SizeOnDisk)}.");
            }
            else
            {
                outcome.NeedsAttention = mode == ExecutionMode.Report;
                outcome.Message = "Not removable automatically: " + match.Reason + " Remove it by hand if it should go.";
            }

            if (lookupError is not null)
            {
                outcome.Message += " (" + lookupError + ")";
            }

            if (_backend.HasSeerrStep)
            {
                outcome.ExternalSteps.AddRange(await _backend.SeerrAsync(key, execute: false, ct).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // a report line must never break the report
        catch (Exception ex)
#pragma warning restore CA1031
        {
            outcome.Message = "Could not check this title: " + ErrorText.Describe(ex);
        }
    }

    private (MediaItemSnapshot? Item, string? Error) GetLive(Guid itemId)
    {
        try
        {
            return (_media.GetItem(itemId), null);
        }
#pragma warning disable CA1031 // a Jellyfin lookup failure is reported per item
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return (null, "Jellyfin lookup failed: " + ErrorText.Describe(ex));
        }
    }

    /// <summary>Match on the live Jellyfin item when it exists (ids may have been refreshed), else on the frozen snapshot.</summary>
    private static MatchKey KeyFor(MediaItemSnapshot? live, CandidateItem? c, ItemOutcome o)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in c?.ProviderIds ?? new Dictionary<string, string>())
        {
            ids[k] = v;
        }

        foreach (var (k, v) in live?.ProviderIds ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                ids[k] = v;
            }
        }

        return new MatchKey(live?.Kind ?? c?.Kind ?? o.Kind, ids, live?.Name ?? c?.Name ?? o.Name, live?.Year ?? c?.Year, live?.DeletePaths.FirstOrDefault() ?? c?.Path);
    }

    private static ItemOutcome NewOutcome(ItemTally w, CandidateItem? c) => new()
    {
        ItemId = w.ItemId,
        Name = c?.Name ?? w.ItemId.ToString(),
        Kind = c?.Kind ?? MediaKind.Movie,
        Score = w.Score,
        SizeBytes = c?.SizeBytes ?? 0,
        Paths = c?.Path is null ? new List<string>() : new List<string> { c.Path },
        Status = ItemOutcomeStatus.NotAttempted
    };

    private static void FillFromLive(ItemOutcome outcome, MediaItemSnapshot item)
    {
        outcome.Name = item.Name;
        outcome.Kind = item.Kind;
        if (item.DeletePaths.Count > 0)
        {
            outcome.Paths = item.DeletePaths.ToList();
        }

        if (item.SizeBytes > 0)
        {
            outcome.SizeBytes = item.SizeBytes;
        }
    }

    private static void FillArr(ItemOutcome outcome, ArrEntry e)
    {
        outcome.ArrInstance = e.Instance;
        outcome.ArrId = e.Id;
        outcome.ArrTitle = e.Year is null ? e.Title : string.Create(CultureInfo.InvariantCulture, $"{e.Title} ({e.Year})");
        if (e.SizeOnDisk > 0)
        {
            outcome.ArrSizeOnDisk = e.SizeOnDisk;
        }
    }

    private static void Set(ItemOutcome o, ItemOutcomeStatus status, string message, bool attention)
    {
        o.Status = status;
        o.Message = message;
        o.NeedsAttention = attention;
    }

    private static string Gb(long bytes) => bytes <= 0 ? "size unknown" : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1073741824.0:0.0} GB");
}
