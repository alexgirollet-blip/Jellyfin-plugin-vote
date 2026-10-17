using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaVote.Core;
using Jellyfin.Plugin.MediaVote.Integrations;
using Xunit;

namespace Jellyfin.Plugin.MediaVote.Tests;

/// <summary>1.1.0.0: tie rules, free-space target, approval, templates, statistics, webhook texts.</summary>
public sealed class FeatureTests : IDisposable
{
    private const long Gb = 1L << 30;
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ------------------------------------------------------------------ tie-break rules

    private static (List<ItemTally> Ranking, Dictionary<Guid, CandidateItem> Cands) Tied(params (string Name, long Size, int? PlayedDaysAgo, int AddedDaysAgo, int Score)[] items)
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var cands = new Dictionary<Guid, CandidateItem>();
        var tallies = new List<ItemTally>();
        foreach (var (name, size, played, added, score) in items)
        {
            var id = Guid.NewGuid();
            cands[id] = new CandidateItem { ItemId = id, Name = name, SizeBytes = size, LastPlayedUtc = played is null ? null : now.AddDays(-played.Value), DateAddedUtc = now.AddDays(-added) };
            tallies.Add(new ItemTally { ItemId = id, RemoveVotes = score });
        }

        return (Tally.Rank(tallies), cands);
    }

    private static string[] Names(WinnerSelection s, Dictionary<Guid, CandidateItem> c) => s.Winners.Select(w => c[w.ItemId].Name).ToArray();

    [Fact]
    public void TieRule_LargerSizeWins()
    {
        var (r, c) = Tied(("Top", 1, null, 1, 5), ("Small", 10, null, 1, 3), ("Big", 90, null, 1, 3), ("Mid", 50, null, 1, 3));
        var s = WinnerPlanner.FixedCount(r, 2, TiePolicy.LargerSizeWins, c);
        Assert.Equal(new[] { "Top", "Big" }, Names(s, c));
        Assert.False(s.TieCrossesCutoff);
        Assert.Contains("larger size wins", s.Notes[0]);
    }

    [Fact]
    public void TieRule_LeastRecentlyWatchedWins_NeverWatchedFirst()
    {
        var (r, c) = Tied(("Recent", 1, 2, 1, 3), ("Never", 1, null, 1, 3), ("Old", 1, 400, 1, 3));
        Assert.Equal(new[] { "Never", "Old" }, Names(WinnerPlanner.FixedCount(r, 2, TiePolicy.LeastRecentlyWatchedWins, c), c));
    }

    [Fact]
    public void TieRule_OldestAddedWins()
    {
        var (r, c) = Tied(("New", 1, null, 10, 2), ("Oldest", 1, null, 900, 2), ("Older", 1, null, 300, 2));
        Assert.Equal(new[] { "Oldest" }, Names(WinnerPlanner.FixedCount(r, 1, TiePolicy.OldestAddedWins, c), c));
    }

    [Fact]
    public void TieRule_ThatCannotSeparate_StillGoesToTheAdmin()
    {
        var (r, c) = Tied(("A", 50, null, 1, 3), ("B", 50, null, 1, 3), ("C", 10, null, 1, 3));
        var s = WinnerPlanner.FixedCount(r, 1, TiePolicy.LargerSizeWins, c);
        Assert.Empty(s.Winners);
        Assert.Equal(2, s.TiedAtCutoff.Count);
        Assert.Equal(1, s.OpenSlots);
    }

    [Fact]
    public void TieRule_DefaultStillFlags()
    {
        var (r, c) = Tied(("A", 50, null, 1, 3), ("B", 10, null, 1, 3));
        Assert.True(WinnerPlanner.FixedCount(r, 1, TiePolicy.FlagForAdmin, c).TieCrossesCutoff);
    }

    // ------------------------------------------------------------------ free-space target

    private static DriveStat Lm1(double freeGb = 700, double usedGb = 6600) => new() { Key = "/LM1", Name = "8TB 1", Known = true, FreeBytes = (long)(freeGb * Gb), UsedBytes = (long)(usedGb * Gb) };

    private static (List<ItemTally>, Dictionary<Guid, CandidateItem>) Ranked(params (string Name, string Drive, long Gbytes, bool Removable)[] items)
    {
        var cands = new Dictionary<Guid, CandidateItem>();
        var tallies = new List<ItemTally>();
        int score = 100;
        foreach (var (name, drive, gb, rem) in items)
        {
            var id = Guid.NewGuid();
            cands[id] = new CandidateItem { ItemId = id, Name = name, SizeBytes = gb * Gb, Removable = rem, DriveBytes = new() { [drive] = gb * Gb } };
            tallies.Add(new ItemTally { ItemId = id, RemoveVotes = score-- });
        }

        return (Tally.Rank(tallies), cands);
    }

    [Fact]
    public void Target_FreeSpace_TakesWinnersInRankOrderUntilReached()
    {
        var (r, c) = Ranked(("A", "/LM1", 100, true), ("B", "/LM2", 500, true), ("C", "/LM1", 150, true), ("D", "/LM1", 300, true));
        var s = new SessionSettings { TargetMode = TargetMode.FreeSpace, TargetDrive = "/LM1", TargetValue = 900, WinnerCount = 10, Outcome = OutcomeMode.DeleteViaArr };
        var sel = WinnerPlanner.Plan(s, r, c, new[] { Lm1() });
        Assert.Equal(new[] { "A", "C" }, Names(sel, c)); // 700 + 100 + 150 = 950 >= 900; B is on another drive
        Assert.Contains(sel.Notes, n => n.StartsWith("Free-space target met on 8TB 1", StringComparison.Ordinal));
        Assert.Contains(sel.Notes, n => n.Contains("not on 8TB 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Target_BelowPercent_AndMaximum()
    {
        var (r, c) = Ranked(("A", "/LM1", 100, true), ("B", "/LM1", 100, true), ("C", "/LM1", 100, true));
        var s = new SessionSettings { TargetMode = TargetMode.BelowPercent, TargetDrive = "/LM1", TargetValue = 80, WinnerCount = 2 };
        var sel = WinnerPlanner.Plan(s, r, c, new[] { Lm1() });
        Assert.Equal(2, sel.Winners.Count); // stops at the maximum even though 80% is not reached
        Assert.Contains(sel.Notes, n => n.Contains("NOT met", StringComparison.Ordinal));
    }

    [Fact]
    public void Target_SkipsUnremovableTitlesInDeleteVotes()
    {
        var (r, c) = Ranked(("Manual", "/LM1", 500, false), ("Arr", "/LM1", 300, true));
        var s = new SessionSettings { TargetMode = TargetMode.FreeSpace, TargetDrive = "/LM1", TargetValue = 900, WinnerCount = 5, Outcome = OutcomeMode.DeleteViaArr };
        Assert.Equal(new[] { "Arr" }, Names(WinnerPlanner.Plan(s, r, c, new[] { Lm1() }), c));
    }

    [Fact]
    public void Target_UnknownDriveOrAlreadyMet_SelectsNothing()
    {
        var (r, c) = Ranked(("A", "/LM1", 100, true));
        var s = new SessionSettings { TargetMode = TargetMode.FreeSpace, TargetDrive = "/LM1", TargetValue = 500, WinnerCount = 5 };
        Assert.Empty(WinnerPlanner.Plan(s, r, c, new[] { new DriveStat { Key = "/LM1", Known = false } }).Winners);
        Assert.Empty(WinnerPlanner.Plan(s, r, c, Array.Empty<DriveStat>()).Winners);
        var met = WinnerPlanner.Plan(s, r, c, new[] { Lm1(freeGb: 600) });
        Assert.Empty(met.Winners);
        Assert.Contains("already met", met.Notes[0]);
    }

    [Fact]
    public void Target_IsValidated()
    {
        var now = DateTime.UtcNow;
        var errors = VoteRules.ValidateSettings(new SessionSettings { Title = "x", DeadlineUtc = now.AddDays(1), TargetMode = TargetMode.BelowPercent, TargetValue = 120 }, now, 5);
        Assert.Contains(errors, e => e.Contains("drive", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("percentage", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ approval workflow

    private (VoteService Svc, FakeArr Arr, FakeMedia Media, FakeNotifier Notifier, FakeClock Clock, List<CandidateItem> Scope, FakeUsers Users) ApprovalSetup()
    {
        var log = new CallLog();
        var media = new FakeMedia(log);
        var arr = new FakeArr(log, media);
        var notifier = new FakeNotifier();
        var clock = new FakeClock(Build.Now);
        var users = new FakeUsers();
        var svc = new VoteService(Build.Store(_dir), users, media, ro => arr.View(ro), notifier, new FakeAudit(), clock);
        var scope = new List<CandidateItem>();
        for (int i = 0; i < 3; i++)
        {
            var id = Guid.NewGuid();
            var snap = media.Add(id, "T" + i);
            arr.AddFor(snap);
            scope.Add(new CandidateItem { ItemId = id, Name = "T" + i, Kind = MediaKind.Movie, LibraryId = "lib", SizeBytes = 1000, ProviderIds = new Dictionary<string, string>(snap.ProviderIds), Path = snap.DeletePaths[0] });
        }

        return (svc, arr, media, notifier, clock, scope, users);
    }

    private static async Task<VoteSession> ApprovalVote(VoteService svc, List<CandidateItem> scope, FakeUsers users, int autoHours = 0)
    {
        var settings = new SessionSettings { Title = "x", DeadlineUtc = Build.Now.AddDays(1), Outcome = OutcomeMode.DeleteViaArr, DeleteConfirmed = true, RequireApproval = true, AutoApproveHours = autoHours, WinnerCount = 1 };
        var (r, s) = await svc.CreateAsync(settings, "admin", scope, new HashSet<Guid>(), new HashSet<string>(), CancellationToken.None);
        Assert.True(r.Ok, r.Error);
        svc.Cast(s!.Id, new Voter(users.Add("a"), "a"), scope[0].ItemId, VoteKind.Remove);
        await svc.CloseEarlyAsync(s.Id, "admin", CancellationToken.None);
        return svc.Get(s.Id)!;
    }

    [Fact]
    public async Task Approval_NothingIsTouchedUntilApproved()
    {
        var (svc, arr, media, notifier, _, scope, users) = ApprovalSetup();
        using (svc)
        {
            var s = await ApprovalVote(svc, scope, users);
            Assert.Equal(ExecutionState.AwaitingApproval, s.Result!.Execution);
            Assert.True(s.Result.NeedsAdminAttention);
            Assert.Equal(new[] { scope[0].ItemId }, s.Result.PlannedItemIds.ToArray());
            Assert.Contains("approval", notifier.Events);
            Assert.Equal(0, arr.DeleteCalls);
            Assert.StartsWith("Waiting for your approval.", s.Result.Items.Single().Message, StringComparison.Ordinal);
            Assert.Single(svc.AwaitingApproval());

            Assert.True((await svc.ApproveAsync(s.Id, "admin", CancellationToken.None)).Ok);
            var after = svc.Get(s.Id)!.Result!;
            Assert.Equal(ExecutionState.Completed, after.Execution);
            Assert.Equal(ItemOutcomeStatus.Removed, after.Items.Single().Status);
            Assert.Equal("admin", after.ApprovedBy);
            Assert.Equal(1, arr.DeleteCalls);
            Assert.False((await svc.ApproveAsync(s.Id, "admin", CancellationToken.None)).Ok); // never twice
        }
    }

    [Fact]
    public async Task Approval_RejectKeepsEverything()
    {
        var (svc, arr, _, _, _, scope, users) = ApprovalSetup();
        using (svc)
        {
            var s = await ApprovalVote(svc, scope, users);
            Assert.True(svc.Reject(s.Id, "admin").Ok);
            var r = svc.Get(s.Id)!.Result!;
            Assert.Equal(ExecutionState.Rejected, r.Execution);
            Assert.Equal(ItemOutcomeStatus.Reported, r.Items.Single().Status);
            Assert.False(r.NeedsAdminAttention);
            Assert.Equal(0, arr.DeleteCalls);
            Assert.False((await svc.ApproveAsync(s.Id, "admin", CancellationToken.None)).Ok);
        }
    }

    [Fact]
    public async Task Approval_AutoApproveTimer()
    {
        var (svc, arr, _, _, clock, scope, users) = ApprovalSetup();
        using (svc)
        {
            var s = await ApprovalVote(svc, scope, users, autoHours: 24);
            Assert.Equal(clock.UtcNow.AddHours(24), s.Result!.ApprovalDueUtc);
            await svc.AutoApproveDueAsync(CancellationToken.None);
            Assert.Equal(0, arr.DeleteCalls);
            clock.UtcNow = clock.UtcNow.AddHours(25);
            await svc.AutoApproveDueAsync(CancellationToken.None);
            Assert.Equal(1, arr.DeleteCalls);
            Assert.Equal("auto-approve timer", svc.Get(s.Id)!.Result!.ApprovedBy);
        }
    }

    // ------------------------------------------------------------------ templates and recurrence

    [Theory]
    [InlineData("2026-01-31T09:00", RecurrenceKind.Monthly, 31, "2026-02-28T09:00")]
    [InlineData("2026-02-28T09:00", RecurrenceKind.Monthly, 31, "2026-03-31T09:00")] // keeps the anchor day
    [InlineData("2026-12-15T20:00", RecurrenceKind.Monthly, 15, "2027-01-15T20:00")]
    [InlineData("2026-10-20T09:00", RecurrenceKind.Weekly, 20, "2026-10-27T09:00")]
    public void Recurrence_Next(string from, RecurrenceKind kind, int anchor, string expected)
    {
        var next = TemplateSchedule.Next(DateTime.Parse(from, System.Globalization.CultureInfo.InvariantCulture), kind, anchor);
        Assert.Equal(expected, next.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void RecurringTemplate_OnlyPreparesADraft_OncePerPending()
    {
        var store = new Storage.JsonTemplateStore(_dir.Path);
        store.Save(new VoteTemplate { Id = "m", Name = "Monthly clean-up", Recurrence = RecurrenceKind.Monthly, NextRunLocal = "2026-10-01T09:00", AnchorDay = 1 });
        store.Save(new VoteTemplate { Id = "n", Name = "One-off", Recurrence = RecurrenceKind.None });
        var london = TimeZoneHelper.Find("Europe/London");
        var ready = TemplateSchedule.DueDrafts(store, new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc), london);
        Assert.Equal("m", Assert.Single(ready).Id);
        var t = store.GetAll().Single(x => x.Id == "m");
        Assert.NotNull(t.DraftPendingSinceUtc);
        Assert.Equal("2026-11-01T09:00", t.NextRunLocal);

        // Months later with the draft still pending (server off for a while): no second draft, schedule catches up.
        Assert.Empty(TemplateSchedule.DueDrafts(store, new DateTime(2027, 1, 5, 12, 0, 0, DateTimeKind.Utc), london));
        Assert.Equal("2027-02-01T09:00", store.GetAll().Single(x => x.Id == "m").NextRunLocal);
        Assert.Equal("Every month · next draft Mon 1 Feb 2027 09:00", TemplateSchedule.Describe(store.GetAll().Single(x => x.Id == "m")));
    }

    // ------------------------------------------------------------------ statistics

    [Fact]
    public void Statistics_FreedOverTime_MostVoted_Participation()
    {
        var alex = Guid.NewGuid();
        var sam = Guid.NewGuid();
        VoteSession Make(string title, int day, long freed, params (Guid User, string Name, int Item)[] votes)
        {
            var s = Build.Session(items: 3);
            s.Settings.Title = title;
            s.Settings.AllUsers = true;
            foreach (var (u, n, i) in votes)
            {
                s.Votes.Add(new VoteRecord { UserId = u, UserName = n, ItemId = s.Candidates[i].ItemId, Kind = VoteKind.Remove });
            }

            s.Status = SessionStatus.Resolved;
            s.Result = new SessionResult { ResolvedUtc = Build.Now.AddDays(day), VoterCount = votes.Select(v => v.User).Distinct().Count(), EligibleVoterCount = 2 };
            s.Result.Items.Add(new ItemOutcome { ItemId = s.Candidates[0].ItemId, Name = s.Candidates[0].Name, Status = ItemOutcomeStatus.Removed, ArrSizeOnDisk = freed });
            return s;
        }

        var sessions = new[] { Make("Jan", 0, 10 * Gb, (alex, "alex", 0), (alex, "alex", 1), (sam, "sam", 0)), Make("Feb", 30, 5 * Gb, (alex, "alex", 0)) };
        var st = Statistics.Build(sessions, new Dictionary<Guid, string> { [alex] = "alex", [sam] = "sam" });
        Assert.Equal(15 * Gb, st.TotalFreedBytes);
        Assert.Equal(new[] { 10 * Gb, 15 * Gb }, st.Sessions.Select(x => x.CumulativeFreedBytes).ToArray());
        Assert.Equal("Item0", st.MostVoted[0].Name);
        Assert.Equal(3, st.MostVoted[0].RemoveVotes);
        Assert.Equal(2, st.MostVoted[0].Sessions);
        var a = st.Participation.Single(u => u.Name == "alex");
        Assert.Equal((3, 2, 2, 100.0), (a.VotesCast, a.SessionsVoted, a.SessionsEligible, a.ParticipationPercent));
        Assert.Equal(50.0, st.Participation.Single(u => u.Name == "sam").ParticipationPercent);
    }

    // ------------------------------------------------------------------ webhooks

    [Fact]
    public void Webhook_ClosingSoonListsNonVoters_WhenEnabled()
    {
        var s = Build.Session();
        Assert.Contains("Not voted yet: alex, sam.", WebhookNotifier.ClosingText(s, new[] { "alex", "sam" }, true), StringComparison.Ordinal);
        Assert.DoesNotContain("Not voted yet", WebhookNotifier.ClosingText(s, new[] { "alex" }, false), StringComparison.Ordinal);
    }

    [Fact]
    public void Webhook_ApprovalTextListsThePlannedTitles()
    {
        var s = Build.Session();
        var id = s.Candidates[0].ItemId;
        s.Result = new SessionResult { PlannedItemIds = new() { id } };
        s.Result.Items.Add(new ItemOutcome { ItemId = id, Name = "Film", ArrSizeOnDisk = 4 * Gb });
        var text = WebhookNotifier.ApprovalText(s);
        Assert.Contains("1 title(s), 4.0 GB", text, StringComparison.Ordinal);
        Assert.Contains("• Film — 4.0 GB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestAgainLink()
    {
        var s = Build.Session();
        s.Candidates[0].ProviderIds["Tmdb"] = "603";
        var item = new ItemOutcome { ItemId = s.Candidates[0].ItemId, Kind = MediaKind.Movie };
        Assert.Equal("https://requests.example/movie/603", Api.MediaVoteController.RequestUrl("https://requests.example/", item, s));
        Assert.Null(Api.MediaVoteController.RequestUrl("", item, s));
        item.Kind = MediaKind.Series;
        Assert.Equal("https://r/tv/603", Api.MediaVoteController.RequestUrl("https://r", item, s));
    }
}
