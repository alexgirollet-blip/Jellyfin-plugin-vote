using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaVote.Core;
using Xunit;

namespace Jellyfin.Plugin.MediaVote.Tests;

/// <summary>Removal goes only through Sonarr/Radarr: order of operations, isolation, verification, Jellyfin clean-up.</summary>
public class ResolutionExecutorTests
{
    private readonly CallLog _log = new();
    private readonly FakeMedia _media;
    private readonly FakeArr _arr;
    private readonly FakeAudit _audit = new();
    private readonly FakeClock _clock = new(Build.Now);

    public ResolutionExecutorTests()
    {
        _media = new FakeMedia(_log);
        _arr = new FakeArr(_log, _media);
    }

    private ResolutionExecutor Executor(IRemovalBackend? backend = null, Func<bool>? paused = null)
        => new(_media, backend ?? _arr, _audit, _clock, paused);

    private (List<ItemTally> Winners, Dictionary<Guid, CandidateItem> Candidates) Setup(params (string Name, MediaKind Kind)[] items)
    {
        var winners = new List<ItemTally>();
        var candidates = new Dictionary<Guid, CandidateItem>();
        foreach (var (name, kind) in items)
        {
            var id = Guid.NewGuid();
            var snap = _media.Add(id, name, kind);
            _arr.AddFor(snap);
            winners.Add(new ItemTally { ItemId = id, RemoveVotes = 3 });
            candidates[id] = new CandidateItem { ItemId = id, Name = name, Kind = kind, ProviderIds = new Dictionary<string, string>(snap.ProviderIds, StringComparer.OrdinalIgnoreCase), Path = snap.DeletePaths[0] };
        }

        return (winners, candidates);
    }

    private static Guid IdOf(string name, Dictionary<Guid, CandidateItem> c) => c.Values.First(x => x.Name == name).ItemId;

    private Task<List<ItemOutcome>> Remove(List<ItemTally> w, Dictionary<Guid, CandidateItem> c, ExecutionLimits? limits = null, IReadOnlyDictionary<Guid, ItemOutcome>? previous = null)
        => Executor().RunAsync(Guid.NewGuid(), w, c, ExecutionMode.Remove, limits ?? ExecutionLimits.None, previous);

    [Fact]
    public async Task Remove_DeletesViaArr_VerifiesGone_ThenForgetsJellyfinEntryWithoutTouchingFiles()
    {
        var (w, c) = Setup(("Movie A", MediaKind.Movie), ("Show B", MediaKind.Series));
        var outcomes = await Remove(w, c);

        Assert.All(outcomes, o => Assert.Equal(ItemOutcomeStatus.Removed, o.Status));
        Assert.All(outcomes, o => Assert.Equal(JellyfinCleanup.Done, o.JellyfinCleanup));
        Assert.Equal(2, _arr.DeletedIds.Count);
        Assert.Equal(2, _media.Forgotten.Count);
        Assert.Equal("Radarr", outcomes[0].ArrInstance);
        Assert.Equal("Sonarr", outcomes[1].ArrInstance);

        // Per item: re-check (GET) → delete → verify (GET) → Jellyfin clean-up.
        var a = _log.Calls.SkipWhile(x => x != "arr:get:Movie A").Take(4).ToList();
        Assert.Equal(new[] { "arr:get:Movie A", "arr:delete:Movie A", "arr:get:Movie A", "cleanup:Movie A" }, a);
        Assert.Contains(_audit.Entries, e => e.Action == "arr_delete_requested");
        Assert.Contains(_audit.Entries, e => e.Action == "arr_removed");
    }

    [Fact]
    public async Task Remove_FilesStillOnDisk_JellyfinEntryIsKeptUntilTheyAreGone()
    {
        _arr.DeletesFilesAtOnce = false; // Sonarr/Radarr delete files in the background
        var (w, c) = Setup(("Movie A", MediaKind.Movie));
        var o = Assert.Single(await Remove(w, c));

        Assert.Equal(ItemOutcomeStatus.Removed, o.Status);
        Assert.Equal(JellyfinCleanup.Pending, o.JellyfinCleanup);
        Assert.Empty(_media.Forgotten);
        Assert.Contains("/media/Movie A", _media.OnDisk);
    }

    [Fact]
    public async Task Remove_NotManaged_IsNotMatched_AndNothingIsDeleted()
    {
        var (w, c) = Setup(("Movie A", MediaKind.Movie));
        _arr.Entries.Clear();
        var o = Assert.Single(await Remove(w, c));

        Assert.Equal(ItemOutcomeStatus.NotMatched, o.Status);
        Assert.True(o.NeedsAttention);
        Assert.Contains("by hand", o.Message);
        Assert.Equal(0, _arr.DeleteCalls);
        Assert.Empty(_media.Forgotten);
    }

    [Fact]
    public async Task Remove_AmbiguousAcrossInstances_IsNotMatched()
    {
        var (w, c) = Setup(("Show B", MediaKind.Series));
        _arr.Configured[ArrKind.Sonarr] = 2;
        _arr.AddFor(_media.Items[w[0].ItemId], instance: "Sonarr Anime", id: 77);
        var o = Assert.Single(await Remove(w, c));

        Assert.Equal(ItemOutcomeStatus.NotMatched, o.Status);
        Assert.Contains("Ambiguous", o.Message);
        Assert.Equal(0, _arr.DeleteCalls);
    }

    [Fact]
    public async Task Remove_SecondInstance_IsUsedWhenTheTitleLivesThere()
    {
        var (w, c) = Setup(("Anime C", MediaKind.Series));
        _arr.Configured[ArrKind.Sonarr] = 2;
        _arr.Entries[0].Instance = "Sonarr Anime";
        var o = Assert.Single(await Remove(w, c));

        Assert.Equal(ItemOutcomeStatus.Removed, o.Status);
        Assert.Equal("Sonarr Anime", o.ArrInstance);
    }

    [Fact]
    public async Task Remove_OneFailureNeverStopsTheOthers()
    {
        var (w, c) = Setup(("A", MediaKind.Movie), ("B", MediaKind.Movie), ("C", MediaKind.Series));
        var b = _arr.Entries.First(e => e.Title == "B");
        _arr.DeleteRefuses[b.Id] = "Radarr answered HTTP 500: Access to the path '/movies/B' is denied.";
        var outcomes = await Remove(w, c);

        Assert.Equal(ItemOutcomeStatus.Removed, outcomes[0].Status);
        Assert.Equal(ItemOutcomeStatus.DeleteRefused, outcomes[1].Status);
        Assert.Equal("Radarr answered HTTP 500: Access to the path '/movies/B' is denied.", outcomes[1].Message); // verbatim
        Assert.True(outcomes[1].NeedsAttention);
        Assert.Equal(ItemOutcomeStatus.Removed, outcomes[2].Status);
    }

    [Fact]
    public async Task Remove_StillListedAfterDelete_IsVerificationFailed()
    {
        var (w, c) = Setup(("A", MediaKind.Movie));
        _arr.DeleteIgnored.Add(_arr.Entries[0].Id);
        var o = Assert.Single(await Remove(w, c));

        Assert.Equal(ItemOutcomeStatus.VerificationFailed, o.Status);
        Assert.Empty(_media.Forgotten);
        Assert.Equal(1, _arr.DeleteCalls); // never retried automatically
    }

    [Fact]
    public async Task Remove_InstanceUnreachable_NothingDeleted_ThenRetryWorks()
    {
        var (w, c) = Setup(("A", MediaKind.Movie));
        _arr.UnreachableInstances.Add("Radarr");
        var o = Assert.Single(await Remove(w, c));
        Assert.Equal(ItemOutcomeStatus.ArrUnreachable, o.Status);
        Assert.Equal(0, _arr.DeleteCalls);

        _arr.UnreachableInstances.Clear();
        var again = Assert.Single(await Remove(w, c, previous: new Dictionary<Guid, ItemOutcome> { [o.ItemId] = o }));
        Assert.Equal(ItemOutcomeStatus.Removed, again.Status);
    }

    [Fact]
    public async Task Retry_AfterAnEarlierDelete_NeverDeletesTwice()
    {
        var (w, c) = Setup(("A", MediaKind.Movie));
        var entry = _arr.Entries[0];
        _arr.DeleteIgnored.Add(entry.Id);
        var first = Assert.Single(await Remove(w, c));
        Assert.Equal(ItemOutcomeStatus.VerificationFailed, first.Status);

        // Later Sonarr/Radarr finished the delete by itself.
        _arr.Entries.Clear();
        _media.OnDisk.Clear();
        var retry = Assert.Single(await Remove(w, c, previous: new Dictionary<Guid, ItemOutcome> { [first.ItemId] = first }));

        Assert.Equal(ItemOutcomeStatus.Removed, retry.Status);
        Assert.Contains("earlier attempt", retry.Message);
        Assert.Equal(1, _arr.DeleteCalls);
        Assert.Equal(JellyfinCleanup.Done, retry.JellyfinCleanup);
    }

    [Fact]
    public async Task Remove_JellyseerrFailure_KeepsTheTitle()
    {
        var (w, c) = Setup(("A", MediaKind.Movie), ("B", MediaKind.Movie));
        _arr.HasSeerrStep = true;
        _arr.SeerrFailsFor.Add("A");
        var outcomes = await Remove(w, c);

        Assert.Equal(ItemOutcomeStatus.ExternalStepFailed, outcomes[0].Status);
        Assert.Equal(ItemOutcomeStatus.Removed, outcomes[1].Status);
        Assert.True(_log.Calls.IndexOf("seerr:exec:B") < _log.Calls.IndexOf("arr:delete:B"), "Jellyseerr runs before the Sonarr/Radarr delete");
        Assert.DoesNotContain("arr:delete:A", _log.Calls);
    }

    [Fact]
    public async Task Remove_ProtectedTitle_IsNeverSentToArr()
    {
        var (w, c) = Setup(("A", MediaKind.Movie));
        _media.Items[w[0].ItemId].IsProtected = true;
        var o = Assert.Single(await Remove(w, c));
        Assert.Equal(ItemOutcomeStatus.Protected, o.Status);
        Assert.Equal(0, _arr.DeleteCalls);
    }

    [Fact]
    public async Task Remove_StopsAtTheVotesMaximum()
    {
        var (w, c) = Setup(("A", MediaKind.Movie), ("B", MediaKind.Movie), ("C", MediaKind.Movie));
        var outcomes = await Remove(w, c, new ExecutionLimits(MaxRemovals: 3, AlreadyRemoved: 1));

        Assert.Equal(ItemOutcomeStatus.Removed, outcomes[0].Status);
        Assert.Equal(ItemOutcomeStatus.Removed, outcomes[1].Status);
        Assert.Equal(ItemOutcomeStatus.NotAttempted, outcomes[2].Status);
        Assert.Contains("maximum", outcomes[2].Message);
        Assert.Equal(2, _arr.DeleteCalls);
    }

    [Fact]
    public async Task Remove_KillSwitch_SendsNothing()
    {
        var (w, c) = Setup(("A", MediaKind.Movie));
        var o = Assert.Single(await Executor(paused: () => true).RunAsync(Guid.NewGuid(), w, c, ExecutionMode.Remove, ExecutionLimits.None));
        Assert.Equal(ItemOutcomeStatus.NotAttempted, o.Status);
        Assert.Contains("DISABLE", o.Message);
        Assert.Equal(0, _arr.DeleteCalls);
        Assert.DoesNotContain(_log.Calls, x => x.StartsWith("arr:get", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_TitleGoneFromJellyfin_StillMatchesByTheFrozenIds()
    {
        var (w, c) = Setup(("A", MediaKind.Movie));
        _media.Items.Clear();
        var o = Assert.Single(await Remove(w, c));
        Assert.Equal(ItemOutcomeStatus.Removed, o.Status);
        Assert.Equal(JellyfinCleanup.Done, o.JellyfinCleanup);
    }

    [Theory]
    [InlineData(ExecutionMode.Report)]
    [InlineData(ExecutionMode.DryRun)]
    public async Task ReportAndDryRun_MakeOnlyReadOnlyCalls(ExecutionMode mode)
    {
        var (w, c) = Setup(("A", MediaKind.Movie), ("B", MediaKind.Series));
        _arr.HasSeerrStep = true;
        _arr.Entries.RemoveAll(e => e.Title == "B");
        var outcomes = await Executor(_arr.View(readOnly: true)).RunAsync(Guid.NewGuid(), w, c, mode, ExecutionLimits.None);

        Assert.All(outcomes, o => Assert.Equal(ItemOutcomeStatus.Reported, o.Status));
        Assert.Equal(0, _arr.DeleteCalls);
        Assert.DoesNotContain(_log.Calls, x => x.Contains("delete", StringComparison.OrdinalIgnoreCase) || x.StartsWith("seerr:exec", StringComparison.Ordinal) || x.StartsWith("cleanup", StringComparison.Ordinal));
        Assert.Empty(_media.Forgotten);
        Assert.NotNull(outcomes[0].ArrId);
        Assert.Contains(mode == ExecutionMode.DryRun ? "Would be deleted" : "Removable via", outcomes[0].Message);
        Assert.Null(outcomes[1].ArrId);
        Assert.Contains("Not removable automatically", outcomes[1].Message);
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task RemoveMode_RefusesAReadOnlyBackend()
        => await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            var (w, c) = Setup(("A", MediaKind.Movie));
            return Executor(_arr.View(readOnly: true)).RunAsync(Guid.NewGuid(), w, c, ExecutionMode.Remove, ExecutionLimits.None);
        });

    [Fact]
    public async Task OnItemDone_IsCalledForEveryTitle()
    {
        var (w, c) = Setup(("A", MediaKind.Movie), ("B", MediaKind.Movie));
        var seen = new List<string>();
        await Executor().RunAsync(Guid.NewGuid(), w, c, ExecutionMode.Remove, ExecutionLimits.None, onItemDone: o => seen.Add(o.Name + ":" + o.Status));
        Assert.Equal(new[] { "A:Removed", "B:Removed" }, seen);
    }
}
