using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaVote.Core;
using Xunit;

namespace Jellyfin.Plugin.MediaVote.Tests;

public sealed class VoteServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new(Build.Now);
    private readonly FakeUsers _users = new();
    private readonly CallLog _log = new();
    private readonly FakeMedia _media;
    private readonly FakeArr _arr;
    private readonly FakeAudit _audit = new();
    private readonly FakeNotifier _notifier = new();
    private readonly VoteService _svc;
    private readonly List<CandidateItem> _scope = new();

    public VoteServiceTests()
    {
        _media = new FakeMedia(_log);
        _arr = new FakeArr(_log, _media);
        _svc = new VoteService(Build.Store(_dir), _users, _media, ro => _arr.View(ro), _notifier, _audit, _clock, () => Paused);
        for (int i = 0; i < 4; i++)
        {
            var id = Guid.NewGuid();
            var snap = _media.Add(id, "M" + i);
            _arr.AddFor(snap);
            _scope.Add(new CandidateItem { ItemId = id, Name = "M" + i, Kind = MediaKind.Movie, LibraryId = "lib", SizeBytes = 1000, ProviderIds = new Dictionary<string, string>(snap.ProviderIds), Path = snap.DeletePaths[0] });
        }
    }

    public void Dispose()
    {
        _svc.Dispose();
        _dir.Dispose();
    }

    private async Task<VoteSession> CreateAsync(Action<SessionSettings>? tweak = null)
    {
        var settings = new SessionSettings { Title = "Clean-up", DeadlineUtc = Build.Now.AddDays(2), VotesPerUser = 2, WinnerCount = 1 };
        tweak?.Invoke(settings);
        var (r, s) = await _svc.CreateAsync(settings, "admin", _scope, new HashSet<Guid>(), new HashSet<string>(), CancellationToken.None);
        Assert.True(r.Ok, string.Join("; ", r.Errors ?? Array.Empty<string>()));
        return s!;
    }

    private bool Paused { get; set; }

    private List<Guid> Removed => _media.Forgotten;

    private Voter NewVoter(string name) => new(_users.Add(name), name);

    [Fact]
    public async Task Create_SendsOpenedNotification_AndAudits()
    {
        var s = await CreateAsync();
        Assert.Equal(4, s.Candidates.Count);
        Assert.Contains("opened", _notifier.Events);
        Assert.Contains(_audit.Entries, e => e.Action == "session_created");
    }

    [Fact]
    public async Task OnlyOneOpenVoteAtATime()
    {
        await CreateAsync();
        var (r, _) = await _svc.CreateAsync(new SessionSettings { Title = "2", DeadlineUtc = Build.Now.AddDays(1) }, "admin", _scope, new HashSet<Guid>(), new HashSet<string>(), CancellationToken.None);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task ProtectedItems_AreExcludedFromCandidates()
    {
        var protectedId = _scope[0].ItemId;
        var (r, s) = await _svc.CreateAsync(new SessionSettings { Title = "x", DeadlineUtc = Build.Now.AddDays(1) }, "admin", _scope, new HashSet<Guid> { protectedId }, new HashSet<string>(), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.DoesNotContain(s!.Candidates, c => c.ItemId == protectedId);
    }

    [Fact]
    public async Task VotesAreAudited_WithUserName()
    {
        var s = await CreateAsync();
        var alice = NewVoter("alice");
        Assert.True(_svc.Cast(s.Id, alice, s.Candidates[0].ItemId, VoteKind.Remove).Ok);
        Assert.Contains(_audit.Entries, e => e.Action == "vote_cast" && e.Actor == "alice");
    }

    [Fact]
    public async Task ResolveAtDeadline_NotifyOnly_ReportsWinner_DeletesNothing()
    {
        var s = await CreateAsync();
        var a = NewVoter("a");
        var b = NewVoter("b");
        _svc.Cast(s.Id, a, s.Candidates[1].ItemId, VoteKind.Remove);
        _svc.Cast(s.Id, b, s.Candidates[1].ItemId, VoteKind.Remove);
        _svc.Cast(s.Id, b, s.Candidates[2].ItemId, VoteKind.Remove);

        _clock.UtcNow = s.Settings.DeadlineUtc.AddMinutes(1);
        await _svc.ResolveDueAsync(CancellationToken.None);

        var r = _svc.Get(s.Id)!;
        Assert.Equal(SessionStatus.Resolved, r.Status);
        var winner = Assert.Single(r.Result!.Items);
        Assert.Equal(s.Candidates[1].ItemId, winner.ItemId);
        Assert.Equal(ItemOutcomeStatus.Reported, winner.Status);
        Assert.Empty(Removed);
        Assert.Equal(ExecutionState.Completed, r.Result.Execution);
        Assert.Contains("results", _notifier.Events);
    }

    [Fact]
    public async Task ServerOfflineAtDeadline_ResolvesOnNextCheck()
    {
        var s = await CreateAsync(x => { x.Outcome = OutcomeMode.DeleteViaArr; x.DeleteConfirmed = true; });
        _svc.Cast(s.Id, NewVoter("a"), s.Candidates[0].ItemId, VoteKind.Remove);

        // Days later (server was down): the first check after start-up resolves it.
        _clock.UtcNow = s.Settings.DeadlineUtc.AddDays(3);
        _svc.MarkInterruptedRuns();
        await _svc.ResolveDueAsync(CancellationToken.None);

        var r = _svc.Get(s.Id)!;
        Assert.Equal(ItemOutcomeStatus.Removed, Assert.Single(r.Result!.Items).Status);
        Assert.Equal(new[] { s.Candidates[0].ItemId }, Removed);
    }

    [Fact]
    public async Task Resolution_IsIdempotent()
    {
        var s = await CreateAsync(x => { x.Outcome = OutcomeMode.DeleteViaArr; x.DeleteConfirmed = true; });
        _svc.Cast(s.Id, NewVoter("a"), s.Candidates[0].ItemId, VoteKind.Remove);
        _clock.UtcNow = s.Settings.DeadlineUtc;
        await Task.WhenAll(_svc.ResolveAsync(s.Id, CancellationToken.None), _svc.ResolveAsync(s.Id, CancellationToken.None), _svc.ResolveDueAsync(CancellationToken.None));
        Assert.Single(Removed);
        Assert.Single(_notifier.Events, e => e == "results");
    }

    [Fact]
    public async Task VotingAfterDeadline_IsRejectedEvenBeforeResolution()
    {
        var s = await CreateAsync();
        _clock.UtcNow = s.Settings.DeadlineUtc.AddSeconds(1);
        var r = _svc.Cast(s.Id, NewVoter("late"), s.Candidates[0].ItemId, VoteKind.Remove);
        Assert.Equal(VoteErrors.DeadlinePassed, r.Error);
    }

    [Fact]
    public async Task Tie_IsFlagged_ThenAdminBreaksIt()
    {
        var s = await CreateAsync(x => { x.Outcome = OutcomeMode.DeleteViaArr; x.DeleteConfirmed = true; });
        _svc.Cast(s.Id, NewVoter("a"), s.Candidates[0].ItemId, VoteKind.Remove);
        _svc.Cast(s.Id, NewVoter("b"), s.Candidates[1].ItemId, VoteKind.Remove);
        await _svc.CloseEarlyAsync(s.Id, "admin", CancellationToken.None);

        var r = _svc.Get(s.Id)!.Result!;
        Assert.True(r.NeedsAdminAttention);
        Assert.Equal(2, r.TiedItemIds.Count);
        Assert.Equal(1, r.TieSlots);
        Assert.All(r.Items, i => Assert.Equal(ItemOutcomeStatus.TiedAwaitingAdmin, i.Status));
        Assert.Empty(Removed);

        Assert.False((await _svc.BreakTieAsync(s.Id, r.TiedItemIds, "admin", CancellationToken.None)).Ok); // too many
        Assert.True((await _svc.BreakTieAsync(s.Id, new[] { s.Candidates[1].ItemId }, "admin", CancellationToken.None)).Ok);

        var after = _svc.Get(s.Id)!.Result!;
        Assert.Equal(new[] { s.Candidates[1].ItemId }, Removed);
        Assert.Equal(ItemOutcomeStatus.Removed, after.Items.First(i => i.ItemId == s.Candidates[1].ItemId).Status);
        Assert.Equal(ItemOutcomeStatus.Reported, after.Items.First(i => i.ItemId == s.Candidates[0].ItemId).Status);
        Assert.Empty(after.TiedItemIds);
        Assert.False(after.NeedsAdminAttention);
    }

    [Fact]
    public async Task MinimumTurnoutNotMet_NothingHappens_UntilAdminOverrides()
    {
        for (int i = 0; i < 9; i++)
        {
            _users.Add("u" + i);
        }

        var s = await CreateAsync(x => { x.MinTurnoutPercent = 50; x.Outcome = OutcomeMode.DeleteViaArr; x.DeleteConfirmed = true; });
        _svc.Cast(s.Id, NewVoter("only"), s.Candidates[0].ItemId, VoteKind.Remove);
        await _svc.CloseEarlyAsync(s.Id, "admin", CancellationToken.None);

        var r = _svc.Get(s.Id)!.Result!;
        Assert.False(r.TurnoutMet);
        Assert.Equal(ExecutionState.Skipped, r.Execution);
        Assert.True(r.NeedsAdminAttention);
        Assert.Empty(Removed);

        Assert.True((await _svc.ResolveAnywayAsync(s.Id, "admin", CancellationToken.None)).Ok);
        Assert.Single(Removed);
    }

    [Fact]
    public async Task Retry_ProcessesFailedItems()
    {
        var s = await CreateAsync(x => { x.Outcome = OutcomeMode.DeleteViaArr; x.DeleteConfirmed = true; });
        _svc.Cast(s.Id, NewVoter("a"), s.Candidates[0].ItemId, VoteKind.Remove);
        var entry = _arr.Entries.First(e => e.Title == s.Candidates[0].Name);
        _arr.DeleteRefuses[entry.Id] = "Radarr answered HTTP 500: disk full";
        await _svc.CloseEarlyAsync(s.Id, "admin", CancellationToken.None);
        Assert.Equal(ItemOutcomeStatus.DeleteRefused, _svc.Get(s.Id)!.Result!.Items[0].Status);
        Assert.True(_svc.Get(s.Id)!.Result!.NeedsAdminAttention);

        _arr.DeleteRefuses.Clear();
        Assert.True((await _svc.RetryAsync(s.Id, "admin", CancellationToken.None)).Ok);
        Assert.Equal(ItemOutcomeStatus.Removed, _svc.Get(s.Id)!.Result!.Items[0].Status);
    }

    [Fact]
    public async Task DryRun_ChangesNothing()
    {
        var s = await CreateAsync(x => { x.Outcome = OutcomeMode.DeleteViaArr; x.DeleteConfirmed = true; });
        _svc.Cast(s.Id, NewVoter("a"), s.Candidates[0].ItemId, VoteKind.Remove);
        int auditBefore = _audit.Entries.Count;
        var dry = await _svc.DryRunAsync(s.Id, CancellationToken.None);

        Assert.True(dry.DryRun);
        Assert.Equal(ItemOutcomeStatus.Reported, Assert.Single(dry.Items).Status);
        Assert.Equal(SessionStatus.Open, _svc.Get(s.Id)!.Status);
        Assert.Empty(Removed);
        Assert.Equal(auditBefore, _audit.Entries.Count);
        Assert.Equal(0, _arr.DeleteCalls);
        Assert.Contains(dry.Notes, n => n.Contains("only read-only (GET)", StringComparison.Ordinal));
        Assert.True(dry.TotalBytesSelected > 0);
    }

    [Fact]
    public async Task InterruptedRun_IsMarked_NotRetriedAutomatically()
    {
        var s = await CreateAsync();
        var store = Build.Store(_dir);
        store.Update(s.Id, x =>
        {
            x.Status = SessionStatus.Resolved;
            x.Result = new SessionResult { Execution = ExecutionState.Running };
            return true;
        });

        using var svc2 = new VoteService(store, _users, _media, ro => _arr.View(ro), _notifier, _audit, _clock);
        svc2.MarkInterruptedRuns();
        var r = svc2.Get(s.Id)!.Result!;
        Assert.Equal(ExecutionState.Interrupted, r.Execution);
        Assert.True(r.NeedsAdminAttention);
    }

    [Fact]
    public async Task ClosingSoon_NotifiesOnce()
    {
        var s = await CreateAsync();
        _clock.UtcNow = s.Settings.DeadlineUtc.AddHours(-1);
        await _svc.NotifyClosingSoonAsync(TimeSpan.FromHours(24), CancellationToken.None);
        await _svc.NotifyClosingSoonAsync(TimeSpan.FromHours(24), CancellationToken.None);
        Assert.Single(_notifier.Events, e => e == "closing");
    }

    [Fact]
    public async Task Extend_And_Cancel()
    {
        var s = await CreateAsync();
        Assert.False(_svc.Extend(s.Id, Build.Now.AddMinutes(-5), "admin").Ok);
        Assert.True(_svc.Extend(s.Id, Build.Now.AddDays(10), "admin").Ok);
        Assert.Equal(Build.Now.AddDays(10), _svc.Get(s.Id)!.Settings.DeadlineUtc);
        Assert.True(_svc.Cancel(s.Id, "admin").Ok);
        Assert.Equal(SessionStatus.Cancelled, _svc.Get(s.Id)!.Status);
        Assert.False(_svc.Cast(s.Id, NewVoter("x"), s.Candidates[0].ItemId, VoteKind.Remove).Ok);
    }

    [Fact]
    public async Task EligibleCount_IgnoresDisabledUsers()
    {
        var active = _users.Add("active");
        var disabled = _users.Add("disabled", active: false);
        var s = await CreateAsync(x => { x.AllUsers = false; x.EligibleUserIds = new List<Guid> { active, disabled }; });
        Assert.Equal(1, _svc.CountEligible(s));
        Assert.False(_svc.Cast(s.Id, new Voter(disabled, "disabled"), s.Candidates[0].ItemId, VoteKind.Remove).Ok);
    }

    [Fact]
    public async Task NotifyOnly_MakesNoStateChangingCalls()
    {
        var s = await CreateAsync();
        _svc.Cast(s.Id, NewVoter("a"), s.Candidates[0].ItemId, VoteKind.Remove);
        await _svc.CloseEarlyAsync(s.Id, "admin", CancellationToken.None);

        var item = Assert.Single(_svc.Get(s.Id)!.Result!.Items);
        Assert.Equal(ItemOutcomeStatus.Reported, item.Status);
        Assert.NotNull(item.ArrId); // the report names the Sonarr/Radarr entry
        Assert.Equal(0, _arr.DeleteCalls);
        Assert.DoesNotContain(_log.Calls, c => c.Contains("delete", StringComparison.OrdinalIgnoreCase) || c.StartsWith("cleanup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteVote_LeavesOutTitlesArrDoesNotManage_UnlessAsked()
    {
        _arr.Entries.RemoveAt(0);
        var s = await CreateAsync(x => { x.Outcome = OutcomeMode.DeleteViaArr; x.DeleteConfirmed = true; });
        Assert.Equal(3, s.Candidates.Count);
        Assert.All(s.Candidates, c => Assert.True(c.Removable));

        _svc.Cancel(s.Id, "admin");
        var s2 = await CreateAsync(x => { x.Outcome = OutcomeMode.DeleteViaArr; x.DeleteConfirmed = true; x.IncludeUnmanagedTitles = true; });
        Assert.Equal(4, s2.Candidates.Count);
        Assert.Single(s2.Candidates, c => c.Removable == false);
    }

    [Fact]
    public async Task DeleteVote_RefusedWhenAnInstanceIsDown()
    {
        _arr.UnreachableInstances.Add("Radarr");
        var settings = new SessionSettings { Title = "x", DeadlineUtc = Build.Now.AddDays(2), Outcome = OutcomeMode.DeleteViaArr, DeleteConfirmed = true };
        var (r, _) = await _svc.CreateAsync(settings, "admin", _scope, new HashSet<Guid>(), new HashSet<string>(), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("could not be reached", r.Error);
    }

    [Fact]
    public async Task DeleteVote_WinnersAboveTheMaximum_AreRejected()
    {
        var settings = new SessionSettings { Title = "x", DeadlineUtc = Build.Now.AddDays(2), Outcome = OutcomeMode.DeleteViaArr, DeleteConfirmed = true, WinnerCount = 4, MaxRemovals = 3 };
        var (r, _) = await _svc.CreateAsync(settings, "admin", _scope, new HashSet<Guid>(), new HashSet<string>(), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("at most 3", r.Error);
    }

    [Fact]
    public async Task KillSwitch_PausesRemoval_AndRetryIsRefused()
    {
        var s = await CreateAsync(x => { x.Outcome = OutcomeMode.DeleteViaArr; x.DeleteConfirmed = true; });
        _svc.Cast(s.Id, NewVoter("a"), s.Candidates[0].ItemId, VoteKind.Remove);
        Paused = true;
        await _svc.CloseEarlyAsync(s.Id, "admin", CancellationToken.None);
        Assert.Equal(ItemOutcomeStatus.NotAttempted, _svc.Get(s.Id)!.Result!.Items[0].Status);
        Assert.Equal(0, _arr.DeleteCalls);
        Assert.False((await _svc.RetryAsync(s.Id, "admin", CancellationToken.None)).Ok);

        Paused = false;
        Assert.True((await _svc.RetryAsync(s.Id, "admin", CancellationToken.None)).Ok);
        Assert.Equal(ItemOutcomeStatus.Removed, _svc.Get(s.Id)!.Result!.Items[0].Status);
    }

    [Fact]
    public async Task PendingCleanup_FinishesOnceFilesAreGone_AndIsFlaggedIfTheyStay()
    {
        _arr.DeletesFilesAtOnce = false;
        var s = await CreateAsync(x => { x.Outcome = OutcomeMode.DeleteViaArr; x.DeleteConfirmed = true; x.WinnerCount = 2; x.VotesPerUser = 2; });
        var v = NewVoter("a");
        _svc.Cast(s.Id, v, s.Candidates[0].ItemId, VoteKind.Remove);
        _svc.Cast(s.Id, v, s.Candidates[1].ItemId, VoteKind.Remove);
        await _svc.CloseEarlyAsync(s.Id, "admin", CancellationToken.None);
        Assert.All(_svc.Get(s.Id)!.Result!.Items, i => Assert.Equal(JellyfinCleanup.Pending, i.JellyfinCleanup));

        // Sonarr/Radarr's background delete finished for the first title only.
        _media.OnDisk.Remove("/media/" + s.Candidates[0].Name);
        _svc.ReconcilePendingCleanups();
        var r = _svc.Get(s.Id)!.Result!;
        Assert.Equal(JellyfinCleanup.Done, r.Items.First(i => i.ItemId == s.Candidates[0].ItemId).JellyfinCleanup);
        Assert.Equal(JellyfinCleanup.Pending, r.Items.First(i => i.ItemId == s.Candidates[1].ItemId).JellyfinCleanup);
        Assert.Contains(s.Candidates[0].ItemId, _media.Forgotten);

        _clock.UtcNow = _clock.UtcNow.AddHours(2);
        _svc.ReconcilePendingCleanups();
        var still = _svc.Get(s.Id)!.Result!.Items.First(i => i.ItemId == s.Candidates[1].ItemId);
        Assert.Equal(JellyfinCleanup.FilesStillOnDisk, still.JellyfinCleanup);
        Assert.True(still.NeedsAttention);
        Assert.DoesNotContain(s.Candidates[1].ItemId, _media.Forgotten);
    }

    [Fact]
    public void LegacyResults_StillLoad()
    {
        var json = "{\"Id\":\"" + Guid.NewGuid() + "\",\"Status\":\"Resolved\",\"Settings\":{\"Title\":\"Old\",\"Outcome\":\"DeleteAutomatically\",\"DeleteConfirmed\":true},\"Result\":{\"Outcome\":\"DeleteAutomatically\",\"Items\":[{\"Name\":\"X\",\"Status\":\"PreflightFailed\"}]}}";
        var s = System.Text.Json.JsonSerializer.Deserialize<VoteSession>(json)!;
        Assert.Equal(OutcomeMode.DeleteViaArr, s.Settings.Outcome);
        Assert.Equal(ItemOutcomeStatus.PreflightFailed, s.Result!.Items[0].Status);
        Assert.True(VoteService.IsRetryable(s.Result.Items[0].Status));
    }
}
