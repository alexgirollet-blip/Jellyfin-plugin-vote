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
    private readonly FakeAudit _audit = new();
    private readonly FakeNotifier _notifier = new();
    private readonly VoteService _svc;
    private readonly List<CandidateItem> _scope = new();

    public VoteServiceTests()
    {
        _media = new FakeMedia(_log);
        var services = new IExternalService[] { new FakeService(_log, "Radarr", k => k == MediaKind.Movie) };
        _svc = new VoteService(Build.Store(_dir), _users, _media, () => services, _notifier, _audit, _clock);
        for (int i = 0; i < 4; i++)
        {
            var id = Guid.NewGuid();
            _media.Add(id, "M" + i);
            _scope.Add(new CandidateItem { ItemId = id, Name = "M" + i, Kind = MediaKind.Movie, LibraryId = "lib", SizeBytes = 1000 });
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
        Assert.Empty(_media.Deleted);
        Assert.Equal(ExecutionState.Completed, r.Result.Execution);
        Assert.Contains("results", _notifier.Events);
    }

    [Fact]
    public async Task ServerOfflineAtDeadline_ResolvesOnNextCheck()
    {
        var s = await CreateAsync(x => { x.Outcome = OutcomeMode.DeleteAutomatically; x.DeleteConfirmed = true; });
        _svc.Cast(s.Id, NewVoter("a"), s.Candidates[0].ItemId, VoteKind.Remove);

        // Days later (server was down): the first check after start-up resolves it.
        _clock.UtcNow = s.Settings.DeadlineUtc.AddDays(3);
        _svc.MarkInterruptedRuns();
        await _svc.ResolveDueAsync(CancellationToken.None);

        var r = _svc.Get(s.Id)!;
        Assert.Equal(ItemOutcomeStatus.Deleted, Assert.Single(r.Result!.Items).Status);
        Assert.Equal(new[] { s.Candidates[0].ItemId }, _media.Deleted);
    }

    [Fact]
    public async Task Resolution_IsIdempotent()
    {
        var s = await CreateAsync(x => { x.Outcome = OutcomeMode.DeleteAutomatically; x.DeleteConfirmed = true; });
        _svc.Cast(s.Id, NewVoter("a"), s.Candidates[0].ItemId, VoteKind.Remove);
        _clock.UtcNow = s.Settings.DeadlineUtc;
        await Task.WhenAll(_svc.ResolveAsync(s.Id, CancellationToken.None), _svc.ResolveAsync(s.Id, CancellationToken.None), _svc.ResolveDueAsync(CancellationToken.None));
        Assert.Single(_media.Deleted);
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
        var s = await CreateAsync(x => { x.Outcome = OutcomeMode.DeleteAutomatically; x.DeleteConfirmed = true; });
        _svc.Cast(s.Id, NewVoter("a"), s.Candidates[0].ItemId, VoteKind.Remove);
        _svc.Cast(s.Id, NewVoter("b"), s.Candidates[1].ItemId, VoteKind.Remove);
        await _svc.CloseEarlyAsync(s.Id, "admin", CancellationToken.None);

        var r = _svc.Get(s.Id)!.Result!;
        Assert.True(r.NeedsAdminAttention);
        Assert.Equal(2, r.TiedItemIds.Count);
        Assert.Equal(1, r.TieSlots);
        Assert.All(r.Items, i => Assert.Equal(ItemOutcomeStatus.TiedAwaitingAdmin, i.Status));
        Assert.Empty(_media.Deleted);

        Assert.False((await _svc.BreakTieAsync(s.Id, r.TiedItemIds, "admin", CancellationToken.None)).Ok); // too many
        Assert.True((await _svc.BreakTieAsync(s.Id, new[] { s.Candidates[1].ItemId }, "admin", CancellationToken.None)).Ok);

        var after = _svc.Get(s.Id)!.Result!;
        Assert.Equal(new[] { s.Candidates[1].ItemId }, _media.Deleted);
        Assert.Equal(ItemOutcomeStatus.Deleted, after.Items.First(i => i.ItemId == s.Candidates[1].ItemId).Status);
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

        var s = await CreateAsync(x => { x.MinTurnoutPercent = 50; x.Outcome = OutcomeMode.DeleteAutomatically; x.DeleteConfirmed = true; });
        _svc.Cast(s.Id, NewVoter("only"), s.Candidates[0].ItemId, VoteKind.Remove);
        await _svc.CloseEarlyAsync(s.Id, "admin", CancellationToken.None);

        var r = _svc.Get(s.Id)!.Result!;
        Assert.False(r.TurnoutMet);
        Assert.Equal(ExecutionState.Skipped, r.Execution);
        Assert.True(r.NeedsAdminAttention);
        Assert.Empty(_media.Deleted);

        Assert.True((await _svc.ResolveAnywayAsync(s.Id, "admin", CancellationToken.None)).Ok);
        Assert.Single(_media.Deleted);
    }

    [Fact]
    public async Task Retry_ProcessesFailedItems()
    {
        var s = await CreateAsync(x => { x.Outcome = OutcomeMode.DeleteAutomatically; x.DeleteConfirmed = true; });
        _svc.Cast(s.Id, NewVoter("a"), s.Candidates[0].ItemId, VoteKind.Remove);
        _media.DeleteThrows.Add(s.Candidates[0].ItemId);
        await _svc.CloseEarlyAsync(s.Id, "admin", CancellationToken.None);
        Assert.Equal(ItemOutcomeStatus.DeleteFailed, _svc.Get(s.Id)!.Result!.Items[0].Status);

        _media.DeleteThrows.Clear();
        Assert.True((await _svc.RetryAsync(s.Id, "admin", CancellationToken.None)).Ok);
        Assert.Equal(ItemOutcomeStatus.Deleted, _svc.Get(s.Id)!.Result!.Items[0].Status);
    }

    [Fact]
    public async Task DryRun_ChangesNothing()
    {
        var s = await CreateAsync(x => { x.Outcome = OutcomeMode.DeleteAutomatically; x.DeleteConfirmed = true; });
        _svc.Cast(s.Id, NewVoter("a"), s.Candidates[0].ItemId, VoteKind.Remove);
        int auditBefore = _audit.Entries.Count;
        var dry = await _svc.DryRunAsync(s.Id, CancellationToken.None);

        Assert.True(dry.DryRun);
        Assert.Equal(ItemOutcomeStatus.Reported, Assert.Single(dry.Items).Status);
        Assert.Equal(SessionStatus.Open, _svc.Get(s.Id)!.Status);
        Assert.Empty(_media.Deleted);
        Assert.Equal(auditBefore, _audit.Entries.Count);
        Assert.DoesNotContain(_log.Calls, c => c.StartsWith("exec:", StringComparison.Ordinal));
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

        using var svc2 = new VoteService(store, _users, _media, () => Array.Empty<IExternalService>(), _notifier, _audit, _clock);
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
}
