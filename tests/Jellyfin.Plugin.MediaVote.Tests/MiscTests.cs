using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.MediaVote.Api;
using Jellyfin.Plugin.MediaVote.Configuration;
using Jellyfin.Plugin.MediaVote.Core;
using Jellyfin.Plugin.MediaVote.Integrations;
using Jellyfin.Plugin.MediaVote.Storage;
using Jellyfin.Plugin.MediaVote.Web;
using Xunit;

namespace Jellyfin.Plugin.MediaVote.Tests;

public class StorageTests
{
    [Fact]
    public void Store_PersistsAndReloads_AtomicallyWithoutTempFile()
    {
        using var dir = new TempDir();
        var store = new JsonVoteStore(dir.Path);
        var s = Build.Session();
        store.Add(s);
        store.Update(s.Id, x =>
        {
            x.Votes.Add(new VoteRecord { UserName = "alice", ItemId = x.Candidates[0].ItemId });
            return 0;
        });

        var reloaded = new JsonVoteStore(dir.Path).Get(s.Id)!;
        Assert.Equal("alice", Assert.Single(reloaded.Votes).UserName);
        Assert.False(File.Exists(Path.Combine(dir.Path, "votes.json.tmp")));
    }

    [Fact]
    public void Store_FailedMutation_DoesNotChangeState()
    {
        using var dir = new TempDir();
        var store = new JsonVoteStore(dir.Path);
        var s = Build.Session();
        store.Add(s);
        Assert.Throws<InvalidOperationException>(() => store.Update<int>(s.Id, x =>
        {
            x.Votes.Add(new VoteRecord());
            throw new InvalidOperationException("boom");
        }));
        Assert.Empty(store.Get(s.Id)!.Votes);
    }

    [Fact]
    public void Store_CorruptFile_IsBackedUpNotOverwritten()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "votes.json"), "{ not json");
        Assert.ThrowsAny<JsonException>(() => new JsonVoteStore(dir.Path));
        Assert.Single(Directory.GetFiles(dir.Path, "votes.json.corrupt-*"));
    }

    [Fact]
    public void AuditLog_AppendsAndReadsNewestFirst()
    {
        using var dir = new TempDir();
        var log = new FileAuditLog(dir.Path, new FakeClock(Build.Now));
        log.Write("vote_cast", "alice", null, "first");
        log.Write("deleted", "system", null, "second");
        var entries = log.Read(10);
        Assert.Equal("second", entries[0].Details);
        Assert.Equal("first", entries[1].Details);
    }
}

public class FilterAndViewTests
{
    private static List<CandidateItem> Scope() => new()
    {
        new CandidateItem { ItemId = Guid.NewGuid(), Name = "Big Movie", Kind = MediaKind.Movie, LibraryId = "m", SizeBytes = 9000, PlayCount = 0 },
        new CandidateItem { ItemId = Guid.NewGuid(), Name = "Old Show", Kind = MediaKind.Series, LibraryId = "t", SizeBytes = 5000, PlayCount = 3, LastPlayedUtc = Build.Now.AddMonths(-13) },
        new CandidateItem { ItemId = Guid.NewGuid(), Name = "Fresh Show", Kind = MediaKind.Series, LibraryId = "t", SizeBytes = 100, PlayCount = 1, LastPlayedUtc = Build.Now.AddDays(-3) },
        new CandidateItem { ItemId = Guid.NewGuid(), Name = "Kids Movie", Kind = MediaKind.Movie, LibraryId = "kids", SizeBytes = 200 }
    };

    private static List<CandidateItem> Apply(SessionSettings s, List<CandidateItem> scope, ISet<Guid>? protectedIds = null, ISet<string>? protectedLibs = null)
        => CandidateFilter.Apply(scope, s, protectedIds ?? new HashSet<Guid>(), protectedLibs ?? new HashSet<string>(), Build.Now);

    [Fact]
    public void ProtectedLibrariesAndItems_AreNeverVotable()
    {
        var scope = Scope();
        var r = Apply(new SessionSettings(), scope, new HashSet<Guid> { scope[0].ItemId }, new HashSet<string> { "kids" });
        Assert.Equal(new[] { "Fresh Show", "Old Show" }, r.Select(c => c.Name));
    }

    [Fact]
    public void ScopeModes()
    {
        var scope = Scope();
        Assert.All(Apply(new SessionSettings { Scope = ScopeMode.MoviesOnly }, scope), c => Assert.Equal(MediaKind.Movie, c.Kind));
        Assert.All(Apply(new SessionSettings { Scope = ScopeMode.SeriesOnly }, scope), c => Assert.Equal(MediaKind.Series, c.Kind));
        Assert.All(Apply(new SessionSettings { Scope = ScopeMode.SpecificLibraries, LibraryIds = new() { "t" } }, scope), c => Assert.Equal("t", c.LibraryId));
        Assert.Equal("Kids Movie", Assert.Single(Apply(new SessionSettings { Scope = ScopeMode.HandPicked, HandPickedItemIds = new() { scope[3].ItemId } }, scope)).Name);
    }

    [Fact]
    public void WatchFilters_AndLargest()
    {
        var scope = Scope();
        Assert.Equal(new[] { "Big Movie", "Kids Movie" }, Apply(new SessionSettings { FilterNeverWatched = true }, scope).Select(c => c.Name));
        Assert.Equal(new[] { "Big Movie", "Kids Movie", "Old Show" }, Apply(new SessionSettings { FilterNotWatchedMonths = 12 }, scope).Select(c => c.Name));
        Assert.Equal(new[] { "Big Movie", "Old Show" }, Apply(new SessionSettings { FilterLargestCount = 2 }, scope).Select(c => c.Name));
    }

    [Fact]
    public void View_HidesNamesFromOthers_ButNotFromAdmins()
    {
        var s = Build.Session();
        s.Settings.ShowVoterNames = false;
        var alice = Guid.NewGuid();
        s.Votes.Add(new VoteRecord { UserId = alice, UserName = "alice", ItemId = s.Candidates[0].ItemId });

        var asOther = ViewBuilder.BuildPage(s, Guid.NewGuid(), false, new ItemQuery { PageSize = 50 }).Items.First(i => i.Id == s.Candidates[0].ItemId);
        Assert.Equal(1, asOther.RemoveVotes);
        Assert.Empty(asOther.RemoveVoters);

        var asAdmin = ViewBuilder.BuildPage(s, Guid.NewGuid(), true, new ItemQuery { PageSize = 50 }).Items.First(i => i.Id == s.Candidates[0].ItemId);
        Assert.Equal(new[] { "alice" }, asAdmin.RemoveVoters);

        var asSelf = ViewBuilder.BuildPage(s, alice, false, new ItemQuery { PageSize = 50 }).Items.First(i => i.Id == s.Candidates[0].ItemId);
        Assert.Equal(1, asSelf.MyRemoveVotes);
    }

    [Fact]
    public void View_HidesTalliesWhenDisabled_AndIgnoresVoteSort()
    {
        var s = Build.Session();
        s.Settings.ShowLiveTallies = false;
        Build.AddVotes(s, s.Candidates[3].ItemId, 3);
        var page = ViewBuilder.BuildPage(s, Guid.NewGuid(), false, new ItemQuery { Sort = "votes", Descending = true, PageSize = 50 });
        Assert.All(page.Items, i => Assert.Null(i.RemoveVotes));
        Assert.Equal("Item4", page.Items[0].Name); // name sort descending, not votes
    }

    [Fact]
    public void View_PagesSearchesAndFiltersByVisibility()
    {
        var s = Build.Session(items: 25);
        var page = ViewBuilder.BuildPage(s, Guid.NewGuid(), false, new ItemQuery { Page = 1, PageSize = 10 });
        Assert.Equal(25, page.Total);
        Assert.Equal(10, page.Items.Count);

        var hidden = s.Candidates[0].ItemId;
        var visible = ViewBuilder.BuildPage(s, Guid.NewGuid(), false, new ItemQuery { PageSize = 100 }, id => id != hidden);
        Assert.Equal(24, visible.Total);

        var search = ViewBuilder.BuildPage(s, Guid.NewGuid(), false, new ItemQuery { Search = "item1", PageSize = 100 });
        Assert.All(search.Items, i => Assert.StartsWith("Item1", i.Name, StringComparison.Ordinal));
        Assert.Equal(ViewBuilder.MaxPageSize, ViewBuilder.BuildPage(Build.Session(items: 150), Guid.NewGuid(), false, new ItemQuery { PageSize = 5000 }).PageSize);
    }
}

public class HelperTests
{
    [Fact]
    public void TimeZone_LondonSummerAndWinter()
    {
        var london = TimeZoneHelper.Find("Europe/London");
        Assert.Equal(new DateTime(2026, 7, 1, 19, 0, 0, DateTimeKind.Utc), TimeZoneHelper.LocalToUtc(new DateTime(2026, 7, 1, 20, 0, 0), london).Utc);
        Assert.Equal(new DateTime(2026, 12, 1, 20, 0, 0, DateTimeKind.Utc), TimeZoneHelper.LocalToUtc(new DateTime(2026, 12, 1, 20, 0, 0), london).Utc);
    }

    [Fact]
    public void TimeZone_SpringForwardGap_IsRejected()
    {
        var london = TimeZoneHelper.Find("Europe/London");
        var (utc, error) = TimeZoneHelper.LocalToUtc(new DateTime(2026, 3, 29, 1, 30, 0), london);
        Assert.Null(utc);
        Assert.NotNull(error);
    }

    [Fact]
    public void TimeZone_FallBackAmbiguity_UsesFirstOccurrence()
    {
        var london = TimeZoneHelper.Find("Europe/London");
        var (utc, _) = TimeZoneHelper.LocalToUtc(new DateTime(2026, 10, 25, 1, 30, 0), london);
        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void Csv_EscapesQuotesAndFormulaInjection()
    {
        Assert.Equal("\"a \"\"b\"\"\"", ResultCsv.Escape("a \"b\""));
        Assert.Equal("\"'=HYPERLINK(1)\"", ResultCsv.Escape("=HYPERLINK(1)"));
        var s = Build.Session();
        Build.AddVotes(s, s.Candidates[0].ItemId, 2);
        var csv = ResultCsv.Build(s);
        Assert.StartsWith("Rank,Title", csv, StringComparison.Ordinal);
        Assert.Equal(s.Candidates.Count + 1, csv.Trim().Split('\n').Length);
    }

    [Fact]
    public void Mask_HidesSecrets()
    {
        Assert.Equal(string.Empty, MediaVoteAdminController.Mask(string.Empty));
        var masked = MediaVoteAdminController.Mask("abcdef0123456789");
        Assert.StartsWith(MediaVoteAdminController.MaskPrefix, masked, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef", masked, StringComparison.Ordinal);
        Assert.EndsWith("6789", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Webhook_DiscordPayloadShape()
    {
        var s = Build.Session();
        var json = JsonDocument.Parse(WebhookNotifier.BuildPayload(WebhookKind.Discord, "vote_opened", s, "Title", "Body"));
        Assert.Equal("Title", json.RootElement.GetProperty("embeds")[0].GetProperty("title").GetString());
        var generic = JsonDocument.Parse(WebhookNotifier.BuildPayload(WebhookKind.Generic, "vote_opened", s, "Title", "Body"));
        Assert.Equal("vote_opened", generic.RootElement.GetProperty("event").GetString());
    }
}
