using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaVote.Core;
using Xunit;

namespace Jellyfin.Plugin.MediaVote.Tests;

public class ResolutionExecutorTests
{
    private readonly CallLog _log = new();
    private readonly FakeMedia _media;
    private readonly FakeService _radarr;
    private readonly FakeService _sonarr;
    private readonly FakeService _seerr;
    private readonly FakeAudit _audit = new();

    public ResolutionExecutorTests()
    {
        _media = new FakeMedia(_log);
        _radarr = new FakeService(_log, "Radarr", k => k == MediaKind.Movie);
        _sonarr = new FakeService(_log, "Sonarr", k => k == MediaKind.Series);
        _seerr = new FakeService(_log, "Jellyseerr", _ => true);
    }

    private ResolutionExecutor Executor(params IExternalService[] services)
        => new(_media, services.Length > 0 ? services : new IExternalService[] { _radarr, _sonarr, _seerr }, _audit, new FakeClock(Build.Now));

    private (List<ItemTally> Winners, Dictionary<Guid, CandidateItem> Candidates) Setup(params (string Name, MediaKind Kind)[] items)
    {
        var winners = new List<ItemTally>();
        var candidates = new Dictionary<Guid, CandidateItem>();
        foreach (var (name, kind) in items)
        {
            var id = Guid.NewGuid();
            _media.Add(id, name, kind);
            winners.Add(new ItemTally { ItemId = id, RemoveVotes = 3 });
            candidates[id] = new CandidateItem { ItemId = id, Name = name, Kind = kind };
        }

        return (winners, candidates);
    }

    private Guid IdOf(string name, Dictionary<Guid, CandidateItem> c) => c.Values.First(x => x.Name == name).ItemId;

    [Fact]
    public async Task Delete_RunsArrThenSeerrThenJellyfin_PerItem()
    {
        var (w, c) = Setup(("Movie1", MediaKind.Movie), ("Show1", MediaKind.Series));
        var outcomes = await Executor().RunAsync(Guid.NewGuid(), w, c, ExecutionMode.Delete, CancellationToken.None);

        Assert.All(outcomes, o => Assert.Equal(ItemOutcomeStatus.Deleted, o.Status));
        Assert.Equal(
            new[]
            {
                "exec:Radarr:Movie1", "exec:Jellyseerr:Movie1", "delete:Movie1",
                "exec:Sonarr:Show1", "exec:Jellyseerr:Show1", "delete:Show1"
            },
            _log.Calls);
    }

    [Fact]
    public async Task Delete_ExternalFailure_SkipsThatItem_AndContinuesWithNext()
    {
        var (w, c) = Setup(("A", MediaKind.Movie), ("B", MediaKind.Movie));
        _radarr.FailFor.Add("A");
        var outcomes = await Executor().RunAsync(Guid.NewGuid(), w, c, ExecutionMode.Delete, CancellationToken.None);

        Assert.Equal(ItemOutcomeStatus.SkippedExternalFailure, outcomes[0].Status);
        Assert.Equal(ItemOutcomeStatus.Deleted, outcomes[1].Status);
        Assert.DoesNotContain("delete:A", _log.Calls);
        Assert.DoesNotContain("exec:Jellyseerr:A", _log.Calls); // later services skipped for a failed item
        Assert.Contains("delete:B", _log.Calls);
        Assert.Contains(_audit.Entries, e => e.Action == "external_failed");
    }

    [Fact]
    public async Task Delete_SeerrFailure_AlsoPreventsDeletion()
    {
        var (w, c) = Setup(("A", MediaKind.Movie));
        _seerr.FailFor.Add("A");
        var outcomes = await Executor().RunAsync(Guid.NewGuid(), w, c, ExecutionMode.Delete, CancellationToken.None);
        Assert.Equal(ItemOutcomeStatus.SkippedExternalFailure, outcomes[0].Status);
        Assert.Empty(_media.Deleted);
    }

    [Fact]
    public async Task Delete_JellyfinDeleteFailure_StopsEverythingAfter()
    {
        var (w, c) = Setup(("A", MediaKind.Movie), ("B", MediaKind.Movie), ("C", MediaKind.Movie));
        _media.DeleteThrows.Add(IdOf("B", c));
        var outcomes = await Executor().RunAsync(Guid.NewGuid(), w, c, ExecutionMode.Delete, CancellationToken.None);

        Assert.Equal(ItemOutcomeStatus.Deleted, outcomes[0].Status);
        Assert.Equal(ItemOutcomeStatus.DeleteFailed, outcomes[1].Status);
        Assert.Equal(ItemOutcomeStatus.NotAttempted, outcomes[2].Status);
        Assert.DoesNotContain(_log.Calls, x => x.EndsWith(":C", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_PreflightPermissionFailure_DeletesNothingAtAll()
    {
        var (w, c) = Setup(("A", MediaKind.Movie), ("B", MediaKind.Movie));
        _media.NotWritable.Add(IdOf("B", c));
        var outcomes = await Executor().RunAsync(Guid.NewGuid(), w, c, ExecutionMode.Delete, CancellationToken.None);

        Assert.Equal(ItemOutcomeStatus.NotAttempted, outcomes[0].Status);
        Assert.Equal(ItemOutcomeStatus.PreflightFailed, outcomes[1].Status);
        Assert.Contains("permission", outcomes[1].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_log.Calls); // no external call, no deletion
    }

    [Fact]
    public async Task Delete_MissingItem_DeletesNothing()
    {
        var (w, c) = Setup(("A", MediaKind.Movie), ("B", MediaKind.Movie));
        _media.Items.Remove(IdOf("A", c));
        var outcomes = await Executor().RunAsync(Guid.NewGuid(), w, c, ExecutionMode.Delete, CancellationToken.None);
        Assert.Equal(ItemOutcomeStatus.PreflightFailed, outcomes[0].Status);
        Assert.Equal(ItemOutcomeStatus.NotAttempted, outcomes[1].Status);
        Assert.Empty(_media.Deleted);
    }

    [Fact]
    public async Task Delete_ProtectedItem_IsNeverDeleted()
    {
        var (w, c) = Setup(("A", MediaKind.Movie));
        _media.Items[IdOf("A", c)].IsProtected = true;
        var outcomes = await Executor().RunAsync(Guid.NewGuid(), w, c, ExecutionMode.Delete, CancellationToken.None);
        Assert.Equal(ItemOutcomeStatus.PreflightFailed, outcomes[0].Status);
        Assert.Empty(_media.Deleted);
    }

    [Fact]
    public async Task Delete_UnconfiguredOrNoOpServices_DoNotBlock()
    {
        var (w, c) = Setup(("A", MediaKind.Movie));
        var radarr = new FakeService(_log, "Radarr", k => k == MediaKind.Movie, hasConnection: false);
        var seerr = new FakeService(_log, "Jellyseerr", _ => true, noOp: true);
        var outcomes = await Executor(radarr, seerr).RunAsync(Guid.NewGuid(), w, c, ExecutionMode.Delete, CancellationToken.None);

        Assert.Equal(ItemOutcomeStatus.Deleted, outcomes[0].Status);
        Assert.Equal(ExternalStepStatus.NotConfigured, outcomes[0].ExternalSteps[0].Status);
        Assert.Equal(ExternalStepStatus.Skipped, outcomes[0].ExternalSteps[1].Status);
        Assert.Equal(0, radarr.ExecuteCount);
        Assert.Equal(0, seerr.ExecuteCount);
    }

    [Theory]
    [InlineData(ExecutionMode.Report)]
    [InlineData(ExecutionMode.DryRun)]
    public async Task ReportAndDryRun_NeverExecuteOrDelete(ExecutionMode mode)
    {
        var (w, c) = Setup(("A", MediaKind.Movie), ("S", MediaKind.Series));
        var outcomes = await Executor().RunAsync(Guid.NewGuid(), w, c, mode, CancellationToken.None);

        Assert.All(outcomes, o => Assert.Equal(ItemOutcomeStatus.Reported, o.Status));
        Assert.Equal(0, _radarr.ExecuteCount + _sonarr.ExecuteCount + _seerr.ExecuteCount);
        Assert.Empty(_media.Deleted);
        Assert.Equal(1, _radarr.PlanCount);
        Assert.Equal(1, _sonarr.PlanCount);
        Assert.Equal(2, _seerr.PlanCount);
        Assert.All(_log.Calls, call => Assert.StartsWith("plan:", call, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Report_ShowsPathsSizesAndPermissionProblems()
    {
        var (w, c) = Setup(("A", MediaKind.Movie));
        _media.NotWritable.Add(IdOf("A", c));
        var o = Assert.Single(await Executor().RunAsync(Guid.NewGuid(), w, c, ExecutionMode.Report, CancellationToken.None));
        Assert.Equal(new[] { "/media/A" }, o.Paths);
        Assert.Equal(1000, o.SizeBytes);
        Assert.Contains("permission", o.Message, StringComparison.OrdinalIgnoreCase);
    }
}
