using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>Result of an admin or user operation.</summary>
public sealed record OpResult(bool Ok, string? Error = null, IReadOnlyList<string>? Errors = null)
{
    public static OpResult Success { get; } = new(true);

    public static OpResult Fail(string error) => new(false, error, new[] { error });

    public static OpResult Fail(IReadOnlyList<string> errors) => new(false, errors.FirstOrDefault(), errors);
}

/// <summary>
/// Orchestrates vote sessions. Contains no Jellyfin types: everything external is behind interfaces,
/// so this class is covered by unit tests. All rules are enforced here on the server.
/// </summary>
public sealed class VoteService : IDisposable
{
    private readonly IVoteStore _store;
    private readonly IUserDirectory _users;
    private readonly IMediaGateway _media;
    private readonly Func<bool, IRemovalBackend> _backend;
    private readonly Func<bool> _isPaused;
    private readonly INotifier _notifier;
    private readonly IAuditLog _audit;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _resolveLock = new(1, 1);

    public VoteService(
        IVoteStore store,
        IUserDirectory users,
        IMediaGateway media,
        Func<bool, IRemovalBackend> backend,
        INotifier notifier,
        IAuditLog audit,
        IClock clock,
        Func<bool>? isPaused = null)
    {
        _store = store;
        _users = users;
        _media = media;
        _backend = backend;
        _isPaused = isPaused ?? (() => false);
        _notifier = notifier;
        _audit = audit;
        _clock = clock;
    }

    public IClock Clock => _clock;

    /// <inheritdoc />
    public void Dispose() => _resolveLock.Dispose();

    public IReadOnlyList<VoteSession> All => _store.GetAll();

    public VoteSession? Get(Guid id) => _store.Get(id);

    /// <summary>The single open session, if any. Only one vote can be open at a time.</summary>
    public VoteSession? GetOpenSession()
        => _store.GetAll().Where(s => s.Status == SessionStatus.Open).OrderByDescending(s => s.CreatedUtc).FirstOrDefault();

    /// <summary>The most recently resolved session.</summary>
    public VoteSession? GetLatestResolved()
        => _store.GetAll().Where(s => s.Status == SessionStatus.Resolved).OrderByDescending(s => s.Result?.ResolvedUtc ?? s.CreatedUtc).FirstOrDefault();

    public int CountEligible(VoteSession session)
        => session.Settings.AllUsers
            ? _users.GetActiveUserIds().Count
            : session.Settings.EligibleUserIds.Distinct().Count(_users.IsActive);

    public bool IsEligible(VoteSession session, Guid userId)
        => VoteRules.IsEligible(session, userId, _users.IsActive(userId));

    // ------------------------------------------------------------------ admin: lifecycle

    public async Task<(OpResult Result, VoteSession? Session)> CreateAsync(
        SessionSettings settings,
        string adminName,
        IEnumerable<CandidateItem> rawScope,
        ISet<Guid> protectedItemIds,
        ISet<string> protectedLibraryIds,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        if (GetOpenSession() is not null)
        {
            return (OpResult.Fail("Another vote is already open. Close or cancel it first."), null);
        }

        settings.Title = settings.Title?.Trim() ?? string.Empty;
        settings.EligibleUserIds = settings.EligibleUserIds.Distinct().ToList();
        if (settings.Outcome != OutcomeMode.DeleteViaArr)
        {
            settings.DeleteConfirmed = false;
            settings.IncludeUnmanagedTitles = false;
        }

        var candidates = CandidateFilter.Apply(rawScope, settings, protectedItemIds, protectedLibraryIds, now);

        // Which titles Sonarr/Radarr manage: read-only GETs, stored on each candidate for the badge and the report.
        var library = await LoadLibraryAsync(cancellationToken).ConfigureAwait(false);
        Removability.Annotate(candidates, library);
        if (settings.Outcome == OutcomeMode.DeleteViaArr)
        {
            var down = library.Unreachable(ArrKind.Radarr).Concat(library.Unreachable(ArrKind.Sonarr)).ToList();
            if (down.Count > 0)
            {
                return (OpResult.Fail($"{string.Join(", ", down)} could not be reached, so Media Vote cannot tell which titles can be removed. Test the connection in Settings, or use \"Notify only\"."), null);
            }

            if (!settings.IncludeUnmanagedTitles)
            {
                candidates = candidates.Where(c => c.Removable == true).ToList();
            }
        }

        var errors = VoteRules.ValidateSettings(settings, now, candidates.Count);
        if (errors.Count > 0)
        {
            return (OpResult.Fail(errors), null);
        }

        var session = new VoteSession
        {
            CreatedUtc = now,
            CreatedBy = adminName,
            Settings = settings,
            Candidates = candidates
        };
        _store.Add(session);
        _audit.Write(
            "session_created",
            adminName,
            session.Id,
            string.Create(CultureInfo.InvariantCulture, $"\"{settings.Title}\", {candidates.Count} items, deadline {settings.DeadlineUtc:u}, N={settings.WinnerCount}, outcome {settings.Outcome}"));

        await _notifier.VoteOpenedAsync(session, cancellationToken).ConfigureAwait(false);
        _store.Update(session.Id, s => s.OpenedNotified = true);
        return (OpResult.Success, _store.Get(session.Id));
    }

    public OpResult Cancel(Guid id, string adminName)
        => _store.Update(id, s =>
        {
            if (s.Status != SessionStatus.Open)
            {
                return OpResult.Fail("Only an open vote can be cancelled.");
            }

            s.Status = SessionStatus.Cancelled;
            s.CancelledUtc = _clock.UtcNow;
            _audit.Write("session_cancelled", adminName, id, s.Settings.Title);
            return OpResult.Success;
        });

    public OpResult Extend(Guid id, DateTime newDeadlineUtc, string adminName)
        => _store.Update(id, s =>
        {
            if (s.Status != SessionStatus.Open)
            {
                return OpResult.Fail("Only an open vote can be extended.");
            }

            if (newDeadlineUtc <= _clock.UtcNow)
            {
                return OpResult.Fail("The new deadline must be in the future.");
            }

            var old = s.Settings.DeadlineUtc;
            s.Settings.DeadlineUtc = newDeadlineUtc;
            s.ClosingSoonNotified = false;
            _audit.Write("session_extended", adminName, id, string.Create(CultureInfo.InvariantCulture, $"{old:u} -> {newDeadlineUtc:u}"));
            return OpResult.Success;
        });

    /// <summary>Closes the vote now and resolves it.</summary>
    public async Task<OpResult> CloseEarlyAsync(Guid id, string adminName, CancellationToken cancellationToken)
    {
        var r = _store.Update(id, s =>
        {
            if (s.Status != SessionStatus.Open)
            {
                return OpResult.Fail("Only an open vote can be closed.");
            }

            s.Settings.DeadlineUtc = _clock.UtcNow;
            _audit.Write("session_closed_early", adminName, id, s.Settings.Title);
            return OpResult.Success;
        });
        if (!r.Ok)
        {
            return r;
        }

        await ResolveAsync(id, cancellationToken).ConfigureAwait(false);
        return OpResult.Success;
    }

    // ------------------------------------------------------------------ user: voting

    public OpResult Cast(Guid sessionId, Voter voter, Guid itemId, VoteKind kind)
        => UserOp(sessionId, voter, "vote_cast", itemId, kind, (s, active, now) => VoteRules.Cast(s, voter, active, itemId, kind, now));

    public OpResult Withdraw(Guid sessionId, Voter voter, Guid itemId, VoteKind kind)
        => UserOp(sessionId, voter, "vote_withdrawn", itemId, kind, (s, active, now) => VoteRules.Withdraw(s, voter, active, itemId, kind, now));

    public OpResult Move(Guid sessionId, Voter voter, Guid fromItemId, Guid toItemId, VoteKind kind)
        => UserOp(sessionId, voter, "vote_moved", toItemId, kind, (s, active, now) => VoteRules.Move(s, voter, active, fromItemId, toItemId, kind, now), fromItemId);

    public OpResult DismissResult(Guid sessionId, Guid userId)
    {
        if (_store.Get(sessionId) is null)
        {
            return OpResult.Fail(VoteErrors.SessionNotFound);
        }

        return _store.Update(sessionId, s =>
        {
            if (!s.ResultDismissedBy.Contains(userId))
            {
                s.ResultDismissedBy.Add(userId);
            }

            return OpResult.Success;
        });
    }

    // ------------------------------------------------------------------ resolution

    /// <summary>Resolves every open session whose deadline has passed (also used at start-up).</summary>
    public async Task ResolveDueAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        foreach (var s in _store.GetAll().Where(s => VoteRules.IsDueForResolution(s, now)).ToList())
        {
            await ResolveAsync(s.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Marks executions interrupted by a crash or restart. They are never retried automatically.</summary>
    public void MarkInterruptedRuns()
    {
        foreach (var s in _store.GetAll().Where(s => s.Result?.Execution == ExecutionState.Running).ToList())
        {
            _store.Update(s.Id, x =>
            {
                if (x.Result is { Execution: ExecutionState.Running } r)
                {
                    r.Execution = ExecutionState.Interrupted;
                    r.NeedsAdminAttention = true;
                    r.Notes.Add("The server stopped while winners were being processed. Check the audit log, then use Retry for items that were not completed.");
                    _audit.Write("execution_interrupted", "system", x.Id, x.Settings.Title);
                }

                return true;
            });
        }
    }

    /// <summary>Sends "closing soon" notifications for sessions inside the reminder window.</summary>
    public async Task NotifyClosingSoonAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        foreach (var s in _store.GetAll().Where(s => !s.ClosingSoonNotified && VoteRules.IsClosingSoon(s, now, window)).ToList())
        {
            bool claimed = _store.Update(s.Id, x =>
            {
                if (x.ClosingSoonNotified)
                {
                    return false;
                }

                x.ClosingSoonNotified = true;
                return true;
            });
            if (claimed)
            {
                await _notifier.ClosingSoonAsync(_store.Get(s.Id)!, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Computes the result of a session without changing anything. Used for dry runs.</summary>
    public async Task<SessionResult> DryRunAsync(Guid id, CancellationToken cancellationToken)
    {
        var s = _store.Get(id) ?? throw new KeyNotFoundException("Vote not found.");
        var result = BuildResult(s, dryRun: true);
        var winners = Tally.SelectWinners(result.Ranking, s.Settings.WinnerCount, s.Settings.TiePolicy);
        var executor = new ResolutionExecutor(_media, _backend(true), NullAudit.Instance, _clock);
        result.Items = await executor.RunAsync(s.Id, winners.Winners, s.Candidates.ToDictionary(c => c.ItemId), ExecutionMode.DryRun, ExecutionLimits.None, cancellationToken: cancellationToken).ConfigureAwait(false);
        AddTiedOutcomes(result, winners, s);
        var removable = result.Items.Where(i => i.Status == ItemOutcomeStatus.Reported && i.ArrId is not null).ToList();
        result.TotalBytesSelected = removable.Sum(Freed);
        if (s.Settings.Outcome == OutcomeMode.DeleteViaArr)
        {
            int cap = s.Settings.MaxRemovals > 0 ? s.Settings.MaxRemovals : int.MaxValue;
            result.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"Sonarr/Radarr would delete {Math.Min(removable.Count, cap)} title(s) with their files, freeing about {result.TotalBytesSelected / 1073741824.0:0.0} GB.")
                + (removable.Count > cap ? string.Create(CultureInfo.InvariantCulture, $" Only the first {cap} (the vote's safety maximum).") : string.Empty));
        }
        else
        {
            result.Notes.Add("Notify only: nothing would be removed. " + removable.Count + " of these titles could be removed via Sonarr/Radarr.");
        }

        int manual = result.Items.Count(i => i.Status == ItemOutcomeStatus.Reported && i.ArrId is null);
        if (manual > 0)
        {
            result.Notes.Add(manual + " title(s) are not removable automatically and would have to be removed by hand.");
        }

        result.Notes.Add("Dry run: only read-only (GET) requests were made. Nothing was changed in Jellyfin, Sonarr, Radarr or Jellyseerr.");
        if (!result.TurnoutMet)
        {
            result.Notes.Add("Minimum turnout is not met right now: if the vote closed now, nothing would be acted on.");
        }

        return result;
    }

    /// <summary>Resolves a session: computes ranking, applies turnout and tie rules, then acts on winners.</summary>
    public async Task ResolveAsync(Guid id, CancellationToken cancellationToken)
    {
        await _resolveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Claim: flip to Resolved under the store lock so a session is never resolved twice.
            var claimed = _store.Update(id, s =>
            {
                if (s.Status != SessionStatus.Open)
                {
                    return false;
                }

                s.Status = SessionStatus.Resolved;
                s.Result = BuildResult(s, dryRun: false);
                return true;
            });
            if (!claimed)
            {
                return;
            }

            var session = _store.Get(id)!;
            var result = session.Result!;
            _audit.Write("session_resolved", "system", id, string.Create(CultureInfo.InvariantCulture, $"turnout {result.VoterCount}/{result.EligibleVoterCount} ({result.TurnoutPercent}%)"));

            if (!result.TurnoutMet)
            {
                _store.Update(id, s =>
                {
                    s.Result!.Execution = ExecutionState.Skipped;
                    s.Result.NeedsAdminAttention = true;
                    s.Result.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"Minimum turnout of {s.Settings.MinTurnoutPercent}% was not met ({s.Result.TurnoutPercent}%). Nothing was acted on. You can resolve anyway from the results page."));
                    return true;
                });
                _audit.Write("turnout_not_met", "system", id, string.Empty);
            }
            else
            {
                await ExecuteWinnersAsync(id, null, cancellationToken).ConfigureAwait(false);
            }

            await _notifier.ResultsAsync(_store.Get(id)!, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    /// <summary>Admin override when minimum turnout was not met.</summary>
    public async Task<OpResult> ResolveAnywayAsync(Guid id, string adminName, CancellationToken cancellationToken)
    {
        var s = _store.Get(id);
        if (s?.Result is null || s.Status != SessionStatus.Resolved || s.Result.TurnoutMet || s.Result.Execution != ExecutionState.Skipped)
        {
            return OpResult.Fail("This vote is not waiting on a turnout decision.");
        }

        _audit.Write("turnout_overridden", adminName, id, string.Empty);
        _store.Update(id, x =>
        {
            x.Result!.TurnoutMet = true;
            x.Result.Notes.Add($"Turnout requirement overridden by {adminName}.");
            return true;
        });
        await _resolveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteWinnersAsync(id, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _resolveLock.Release();
        }

        return OpResult.Success;
    }

    /// <summary>Admin breaks a tie by choosing which tied items to act on (up to the open slots).</summary>
    public async Task<OpResult> BreakTieAsync(Guid id, IReadOnlyCollection<Guid> chosen, string adminName, CancellationToken cancellationToken)
    {
        var s = _store.Get(id);
        if (s?.Result is null || s.Result.TiedItemIds.Count == 0)
        {
            return OpResult.Fail("This vote has no tie to break.");
        }

        var tied = s.Result.TiedItemIds.ToHashSet();
        var picks = chosen.Distinct().ToList();
        if (picks.Any(p => !tied.Contains(p)))
        {
            return OpResult.Fail("Only tied items can be chosen.");
        }

        if (picks.Count > s.Result.TieSlots)
        {
            return OpResult.Fail(string.Create(CultureInfo.InvariantCulture, $"Choose at most {s.Result.TieSlots} item(s)."));
        }

        _audit.Write("tie_broken", adminName, id, "chosen: " + string.Join(", ", picks));
        _store.Update(id, x =>
        {
            foreach (var o in x.Result!.Items.Where(o => o.Status == ItemOutcomeStatus.TiedAwaitingAdmin))
            {
                if (!picks.Contains(o.ItemId))
                {
                    o.Status = ItemOutcomeStatus.Reported;
                    o.Message = $"Tie broken by {adminName}: kept.";
                }
            }

            x.Result.TiedItemIds.Clear();
            x.Result.TieSlots = 0;
            return true;
        });

        if (picks.Count > 0)
        {
            await _resolveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ExecuteWinnersAsync(id, picks, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _resolveLock.Release();
            }
        }

        RefreshAttention(id);
        return OpResult.Success;
    }

    /// <summary>
    /// Retries titles that were not removed (not matched, unreachable, refused, not verified, Jellyseerr failure, not
    /// attempted, and 1.0.x failures). Each one is looked up again first, so nothing can be deleted twice.
    /// </summary>
    public async Task<OpResult> RetryAsync(Guid id, string adminName, CancellationToken cancellationToken)
    {
        var s = _store.Get(id);
        if (s?.Result is null || s.Settings.Outcome != OutcomeMode.DeleteViaArr)
        {
            return OpResult.Fail("Only \"Delete via Sonarr/Radarr\" votes can be retried.");
        }

        if (_isPaused())
        {
            return OpResult.Fail("Media Vote is disabled by the DISABLE file. Remove it and restart Jellyfin first.");
        }

        var retryable = s.Result.Items
            .Where(o => IsRetryable(o.Status))
            .Select(o => o.ItemId)
            .ToList();
        if (s.Result.Execution == ExecutionState.Interrupted)
        {
            // Winners the interrupted run never reached have no outcome yet.
            var done = s.Result.Items.Select(o => o.ItemId).ToHashSet();
            retryable.AddRange(Tally.SelectWinners(s.Result.Ranking, s.Settings.WinnerCount, s.Settings.TiePolicy).Winners.Select(w => w.ItemId).Where(i => !done.Contains(i)));
        }
        if (retryable.Count == 0)
        {
            return OpResult.Fail("Nothing to retry.");
        }

        _audit.Write("retry", adminName, id, string.Join(", ", retryable));
        await _resolveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteWinnersAsync(id, retryable, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _resolveLock.Release();
        }

        return OpResult.Success;
    }

    /// <summary>
    /// Finishes Jellyfin clean-up for titles Sonarr/Radarr removed: once their files are gone from disk the stale
    /// Jellyfin entry is dropped (never deleting files). Files still present an hour after the delete are flagged,
    /// because Sonarr/Radarr delete files in the background and only log a failure.
    /// </summary>
    public void ReconcilePendingCleanups()
    {
        var now = _clock.UtcNow;
        foreach (var s in _store.GetAll().Where(s => s.Result?.Items.Any(o => o.JellyfinCleanup == JellyfinCleanup.Pending) == true).ToList())
        {
            foreach (var o in s.Result!.Items.Where(o => o.JellyfinCleanup == JellyfinCleanup.Pending).ToList())
            {
                CleanupResult r;
                try
                {
                    r = _media.CleanUpAfterRemoval(o.ItemId, o.Paths);
                }
#pragma warning disable CA1031 // background clean-up must never throw
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    r = new CleanupResult(JellyfinCleanup.Failed, ErrorText.Describe(ex));
                }

                if (r.State == JellyfinCleanup.Pending && o.CompletedUtc is { } done && now - done > TimeSpan.FromHours(1))
                {
                    r = new CleanupResult(JellyfinCleanup.FilesStillOnDisk, "Sonarr/Radarr removed the title over an hour ago, but its files are still on disk: " + string.Join(" | ", o.Paths) + ". Check Sonarr/Radarr's log (its background file delete failed, the folder is shared with another title, or a recycle bin is set) and delete them by hand if needed.");
                }

                if (r.State == JellyfinCleanup.Pending)
                {
                    continue;
                }

                _audit.Write("jellyfin_cleanup_" + r.State.ToString().ToLowerInvariant(), "system", s.Id, $"{o.Name} ({o.ItemId}): {r.Message}");
                _store.Update(s.Id, x =>
                {
                    var item = x.Result?.Items.FirstOrDefault(e => e.ItemId == o.ItemId);
                    if (item is not null)
                    {
                        item.JellyfinCleanup = r.State;
                        item.CleanupNote = r.Message;
                        item.NeedsAttention = item.NeedsAttention || r.State == JellyfinCleanup.FilesStillOnDisk;
                        x.Result!.NeedsAdminAttention = ComputeAttention(x.Result);
                    }

                    return true;
                });
            }
        }
    }

    // ------------------------------------------------------------------ internals

    private SessionResult BuildResult(VoteSession s, bool dryRun)
    {
        var ranking = Tally.Count(s);
        int eligible = CountEligible(s);
        int voters = Tally.VoterCount(s);
        return new SessionResult
        {
            ResolvedUtc = _clock.UtcNow,
            DryRun = dryRun,
            Outcome = s.Settings.Outcome,
            Ranking = ranking,
            EligibleVoterCount = eligible,
            VoterCount = voters,
            TurnoutPercent = Tally.TurnoutPercent(voters, eligible),
            TurnoutMet = Tally.TurnoutMet(voters, eligible, s.Settings.MinTurnoutPercent)
        };
    }

    /// <param name="onlyItems">Null = initial run over the selected winners; otherwise re-run just these items.</param>
    private async Task ExecuteWinnersAsync(Guid id, IReadOnlyCollection<Guid>? onlyItems, CancellationToken cancellationToken)
    {
        var s = _store.Get(id)!;
        var result = s.Result!;
        var candidates = s.Candidates.ToDictionary(c => c.ItemId);
        List<ItemTally> toRun;
        WinnerSelection? selection = null;
        if (onlyItems is null)
        {
            selection = Tally.SelectWinners(result.Ranking, s.Settings.WinnerCount, s.Settings.TiePolicy);
            toRun = selection.Winners;
        }
        else
        {
            var byId = result.Ranking.ToDictionary(t => t.ItemId);
            toRun = onlyItems.Where(byId.ContainsKey).Select(i => byId[i]).ToList();
        }

        var mode = s.Settings.Outcome == OutcomeMode.DeleteViaArr ? ExecutionMode.Remove : ExecutionMode.Report;
        _store.Update(id, x =>
        {
            x.Result!.Execution = toRun.Count == 0 && selection is not null && !selection.TieCrossesCutoff ? ExecutionState.Skipped : ExecutionState.Running;
            return true;
        });

        var runIds = toRun.Select(t => t.ItemId).ToHashSet();
        var previous = result.Items.Where(o => runIds.Contains(o.ItemId)).ToDictionary(o => o.ItemId);
        int alreadyRemoved = result.Items.Count(o => !runIds.Contains(o.ItemId) && o.Status is ItemOutcomeStatus.Removed or ItemOutcomeStatus.Deleted);
        var limits = new ExecutionLimits(s.Settings.MaxRemovals > 0 ? s.Settings.MaxRemovals : int.MaxValue, alreadyRemoved);

        // Save each title as soon as it is done, so a crash mid-run never loses what already happened.
        void SaveOne(ItemOutcome o) => _store.Update(id, x =>
        {
            x.Result!.Items.RemoveAll(e => e.ItemId == o.ItemId);
            x.Result.Items.Add(o);
            return true;
        });

        var executor = new ResolutionExecutor(_media, _backend(mode != ExecutionMode.Remove), _audit, _clock, _isPaused);
        var outcomes = await executor.RunAsync(id, toRun, candidates, mode, limits, previous, SaveOne, cancellationToken).ConfigureAwait(false);

        _store.Update(id, x =>
        {
            var r = x.Result!;
            foreach (var o in outcomes)
            {
                r.Items.RemoveAll(e => e.ItemId == o.ItemId);
                r.Items.Add(o);
            }

            if (selection is not null)
            {
                AddTiedOutcomes(r, selection, x);
                if (selection.Winners.Count == 0 && !selection.TieCrossesCutoff)
                {
                    r.Notes.Add("No item received a positive score, so nothing was selected.");
                }
            }

            var rank = r.Ranking.Select((t, i) => (t.ItemId, i)).ToDictionary(p => p.ItemId, p => p.i);
            r.Items = r.Items.OrderBy(o => rank.TryGetValue(o.ItemId, out var i) ? i : int.MaxValue).ToList();
            r.TotalBytesFreed = r.Items.Where(o => o.Status is ItemOutcomeStatus.Removed or ItemOutcomeStatus.Deleted).Sum(Freed);
            r.TotalBytesSelected = r.Items.Where(o => o.Status != ItemOutcomeStatus.TiedAwaitingAdmin).Sum(Freed);
            if (r.Execution == ExecutionState.Running)
            {
                r.Execution = ExecutionState.Completed;
            }

            r.NeedsAdminAttention = ComputeAttention(r);
            return true;
        });
    }

    private static void AddTiedOutcomes(SessionResult r, WinnerSelection selection, VoteSession s)
    {
        if (!selection.TieCrossesCutoff)
        {
            return;
        }

        r.TiedItemIds = selection.TiedAtCutoff.Select(t => t.ItemId).ToList();
        r.TieSlots = selection.OpenSlots;
        var candidates = s.Candidates.ToDictionary(c => c.ItemId);
        foreach (var t in selection.TiedAtCutoff)
        {
            candidates.TryGetValue(t.ItemId, out var c);
            r.Items.RemoveAll(e => e.ItemId == t.ItemId);
            r.Items.Add(new ItemOutcome
            {
                ItemId = t.ItemId,
                Name = c?.Name ?? t.ItemId.ToString(),
                Kind = c?.Kind ?? MediaKind.Movie,
                Score = t.Score,
                SizeBytes = c?.SizeBytes ?? 0,
                Status = ItemOutcomeStatus.TiedAwaitingAdmin,
                Message = string.Create(CultureInfo.InvariantCulture, $"Tied at score {t.Score} for {selection.OpenSlots} remaining slot(s). Nothing in this tie was acted on; an admin must break it.")
            });
        }

        r.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"A tie crosses the cut-off: {selection.TiedAtCutoff.Count} items share score {selection.TiedAtCutoff[0].Score} for {selection.OpenSlots} slot(s)."));
    }

    private static bool ComputeAttention(SessionResult r)
        => r.TiedItemIds.Count > 0
           || !r.TurnoutMet
           || r.Execution == ExecutionState.Interrupted
           || r.Items.Any(o => o.NeedsAttention || o.Status is ItemOutcomeStatus.SkippedExternalFailure or ItemOutcomeStatus.DeleteFailed or ItemOutcomeStatus.PreflightFailed);

    /// <summary>Statuses Retry may pick up. Removed and protected titles are never retried.</summary>
    public static bool IsRetryable(ItemOutcomeStatus status)
        => status is ItemOutcomeStatus.NotMatched or ItemOutcomeStatus.ArrUnreachable or ItemOutcomeStatus.DeleteRefused
            or ItemOutcomeStatus.VerificationFailed or ItemOutcomeStatus.ExternalStepFailed or ItemOutcomeStatus.NotAttempted
            or ItemOutcomeStatus.SkippedExternalFailure or ItemOutcomeStatus.DeleteFailed or ItemOutcomeStatus.PreflightFailed;

    /// <summary>Space a title frees: Sonarr/Radarr's size on disk when known, else Jellyfin's.</summary>
    private static long Freed(ItemOutcome o) => o.ArrSizeOnDisk > 0 ? o.ArrSizeOnDisk : o.SizeBytes;

    private async Task<ArrLibrary> LoadLibraryAsync(CancellationToken ct)
    {
        try
        {
            return await _backend(true).LoadAsync(ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // building a vote must still work (as notify-only) when Sonarr/Radarr misbehave
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            var lib = new ArrLibrary(Array.Empty<ArrEntry>(), new Dictionary<ArrKind, int>());
            lib.Errors.Add(ex.Message);
            return lib;
        }
    }

    private void RefreshAttention(Guid id)
        => _store.Update(id, x =>
        {
            if (x.Result is not null)
            {
                x.Result.NeedsAdminAttention = ComputeAttention(x.Result);
            }

            return true;
        });

    private OpResult UserOp(Guid sessionId, Voter voter, string auditAction, Guid itemId, VoteKind kind, Func<VoteSession, bool, DateTime, RuleResult> op, Guid? fromItemId = null)
    {
        if (_store.Get(sessionId) is null)
        {
            return OpResult.Fail(VoteErrors.SessionNotFound);
        }

        bool active = _users.IsActive(voter.UserId);
        var result = _store.Update(sessionId, s => op(s, active, _clock.UtcNow));
        if (result.Ok)
        {
            var details = fromItemId is null
                ? $"{kind} vote on {itemId}"
                : $"{kind} vote moved {fromItemId} -> {itemId}";
            _audit.Write(auditAction, voter.UserName, sessionId, details);
            return OpResult.Success;
        }

        return OpResult.Fail(result.Error!);
    }

    /// <summary>Audit sink that discards everything (dry runs must leave no trace of actions).</summary>
    private sealed class NullAudit : IAuditLog
    {
        public static readonly NullAudit Instance = new();

        public void Write(string action, string actor, Guid? sessionId, string details)
        {
        }
    }
}
