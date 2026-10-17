using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaVote.Core;
using Xunit;

namespace Jellyfin.Plugin.MediaVote.Tests;

public class BrowseEngineTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private const long Gb = 1L << 30;

    private static readonly List<CandidateItem> Items = new()
    {
        new() { ItemId = Guid.NewGuid(), Name = "Alpha", Kind = MediaKind.Movie, Year = 1999, SizeBytes = 40 * Gb, LibraryId = "movies", LibraryName = "Movies", DateAddedUtc = Now.AddMonths(-20), Genres = new() { "Drama" }, DriveBytes = new() { ["/LM1"] = 40 * Gb }, Removable = true },
        new() { ItemId = Guid.NewGuid(), Name = "Bravo", Kind = MediaKind.Movie, Year = 2015, SizeBytes = 5 * Gb, LibraryId = "movies", LibraryName = "Movies", DateAddedUtc = Now.AddMonths(-2), LastPlayedUtc = Now.AddDays(-3), LastPlayedBy = "sam", PlayCount = 4, ViewerIds = new() { Other }, Genres = new() { "Comedy" }, DriveBytes = new() { ["/LM2"] = 5 * Gb }, Removable = true },
        new() { ItemId = Guid.NewGuid(), Name = "Charlie", Kind = MediaKind.Series, Year = 2008, SizeBytes = 300 * Gb, LibraryId = "tv", LibraryName = "TV", DateAddedUtc = Now.AddMonths(-30), LastPlayedUtc = Now.AddMonths(-14), LastPlayedBy = "alex", PlayCount = 12, ViewerIds = new() { Me, Other }, EpisodeCount = 60, PlayedEpisodeCount = 20, DriveBytes = new() { ["/LM1"] = 200 * Gb, ["/LM2"] = 100 * Gb }, Removable = false },
        new() { ItemId = Guid.NewGuid(), Name = "Delta", Kind = MediaKind.Series, Year = 2020, SizeBytes = 80 * Gb, LibraryId = "tv", LibraryName = "TV", DateAddedUtc = Now.AddMonths(-8), LastPlayedUtc = Now.AddMonths(-1), PlayCount = 30, ViewerIds = new() { Me }, EpisodeCount = 10, PlayedEpisodeCount = 10, DriveBytes = new() { ["/LM2"] = 80 * Gb }, Removable = true }
    };

    private static string[] Names(ItemQuery q, bool watchAllowed = true, Func<Guid, int>? score = null, bool timesAllowed = true)
        => BrowseEngine.Apply(Items, q, new BrowseContext { ViewerId = Me, NowUtc = Now, WatchFiltersAllowed = watchAllowed, WatchTimesAllowed = timesAllowed, Score = score, DriveName = k => k == "/LM1" ? "8TB 1" : "8TB 2" }).Select(c => c.Name).ToArray();

    [Fact]
    public void Sorts()
    {
        Assert.Equal(new[] { "Charlie", "Delta", "Alpha", "Bravo" }, Names(new ItemQuery { Sort = "size", Descending = true }));
        Assert.Equal(new[] { "Bravo", "Alpha", "Delta", "Charlie" }, Names(new ItemQuery { Sort = "size" }));
        Assert.Equal(new[] { "Bravo", "Delta", "Alpha", "Charlie" }, Names(new ItemQuery { Sort = "added", Descending = true }));
        Assert.Equal(new[] { "Alpha", "Charlie", "Delta", "Bravo" }, Names(new ItemQuery { Sort = "lastplayed" })); // never/longest ago first
        Assert.Equal(new[] { "Alpha", "Charlie", "Delta", "Bravo" }, Names(new ItemQuery { Sort = "never" }));
        Assert.Equal(new[] { "Delta", "Charlie", "Bravo", "Alpha" }, Names(new ItemQuery { Sort = "playcount", Descending = true }));
        Assert.Equal(new[] { "Alpha", "Charlie", "Bravo", "Delta" }, Names(new ItemQuery { Sort = "drive" })); // by the drive holding most of it
        Assert.Equal(new[] { "Alpha", "Charlie", "Bravo", "Delta" }, Names(new ItemQuery { Sort = "year" }));
    }

    [Fact]
    public void SortByVotes_OnlyWhenScoresAreVisible()
    {
        var scores = Items.ToDictionary(i => i.ItemId, i => i.Name == "Delta" ? 5 : 0);
        Assert.Equal("Delta", Names(new ItemQuery { Sort = "votes", Descending = true }, score: id => scores[id])[0]);
        Assert.Equal("Alpha", Names(new ItemQuery { Sort = "votes", Descending = true })[0]); // hidden tallies: falls back to name
    }

    [Fact]
    public void Filters()
    {
        Assert.Equal(new[] { "Charlie", "Delta" }, Names(new ItemQuery { Type = "Series" }));
        Assert.Equal(new[] { "Alpha", "Bravo" }, Names(new ItemQuery { Library = "movies" }));
        Assert.Equal(new[] { "Alpha", "Charlie" }, Names(new ItemQuery { Drive = "/LM1" })); // a series spanning both drives is on both
        Assert.Equal(new[] { "Bravo" }, Names(new ItemQuery { Genre = "comedy" }));
        Assert.Equal(new[] { "Alpha", "Delta" }, Names(new ItemQuery { MinGb = 30, MaxGb = 100 }));
        Assert.Equal(new[] { "Bravo", "Delta" }, Names(new ItemQuery { MinYear = 2010 }));
        Assert.Equal(new[] { "Alpha", "Bravo", "Delta" }, Names(new ItemQuery { RemovableOnly = true }));
        Assert.Equal(new[] { "Charlie" }, Names(new ItemQuery { Search = "arl" }));
    }

    [Fact]
    public void WatchFilters_AndPresets()
    {
        Assert.Equal(new[] { "Alpha" }, Names(new ItemQuery { Watched = "never" }));
        Assert.Equal(new[] { "Alpha", "Charlie" }, Names(new ItemQuery { Watched = "notsince", Months = 12 }));
        Assert.Equal(new[] { "Charlie", "Delta" }, Names(new ItemQuery { Watched = "mine" }));
        Assert.Equal(new[] { "Alpha", "Bravo" }, Names(new ItemQuery { Watched = "notmine" }));
        // "Added 6+ months ago and watched by nobody"
        Assert.Equal(new[] { "Alpha" }, Names(new ItemQuery { AddedBeforeMonths = 6, Watched = "never" }));
        // "Half-finished series nobody continues": some episodes watched, none in 6 months
        Assert.Equal(new[] { "Charlie" }, Names(new ItemQuery { Type = "Series", HalfFinished = true, Watched = "notsince", Months = 6 }));
    }

    [Fact]
    public void HiddenWatchDetails_IgnoreWatchFiltersAndSorts()
    {
        Assert.Equal(4, Names(new ItemQuery { Watched = "never" }, watchAllowed: false).Length);
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie", "Delta" }, Names(new ItemQuery { Sort = "lastplayed", Descending = true }, watchAllowed: false));
    }

    [Fact]
    public void CountsOnly_IgnoresTimeBasedFiltersAndSorts()
    {
        Assert.Equal(4, Names(new ItemQuery { Watched = "notsince", Months = 12 }, timesAllowed: false).Length);
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie", "Delta" }, Names(new ItemQuery { Sort = "lastplayed" }, timesAllowed: false));
        Assert.Equal(new[] { "Alpha" }, Names(new ItemQuery { Watched = "never" }, timesAllowed: false)); // counts still show "never watched"
        Assert.Equal(new[] { "Delta", "Charlie", "Bravo", "Alpha" }, Names(new ItemQuery { Sort = "playcount", Descending = true }, timesAllowed: false));
    }

    [Fact]
    public void ViewBuilder_AppliesWatchVisibility()
    {
        var c = Items[1];
        var full = ViewBuilder.Project(c, Me, "NamesAndTime", null, false, false);
        Assert.Equal("sam", full.LastPlayedBy);
        Assert.Equal(1, full.ViewerCount);
        var counts = ViewBuilder.Project(c, Me, "CountsOnly", null, false, false);
        Assert.Null(counts.LastPlayedBy);
        Assert.Null(counts.LastPlayedUtc);
        Assert.Equal(4, counts.PlayCount);
        var hidden = ViewBuilder.Project(c, Me, "Hidden", null, false, false);
        Assert.Null(hidden.PlayCount);
        Assert.Null(hidden.NeverWatched);
        Assert.Null(hidden.Removable);
        Assert.Null(hidden.RemovalNote);
    }

    [Fact]
    public void ViewBuilder_DriveBarsCarryUsage()
    {
        var drives = new List<DriveStat> { new() { Key = "/LM1", Name = "8TB 1", Known = true, UsedBytes = 90, FreeBytes = 10, Level = DriveLevel.Warning }, new() { Key = "/LM2", Name = "8TB 2", Known = true, UsedBytes = 65, FreeBytes = 35 } };
        var v = ViewBuilder.Project(Items[2], Me, "NamesAndTime", drives, true, true);
        Assert.Equal(new[] { "8TB 1", "8TB 2" }, v.Drives.Select(d => d.Name).ToArray());
        Assert.Equal(90.0, v.Drives[0].UsedPercent);
        Assert.Equal(DriveLevel.Warning, v.Drives[0].Level);
        Assert.Equal(200 * Gb, v.Drives[0].Bytes);
    }

    [Fact]
    public void BrowsePage_HasFacetsAndPaging()
    {
        var page = ViewBuilder.BuildBrowsePage(Items, Me, new ItemQuery { PageSize = 2, Page = 1, Sort = "name" }, new List<DriveStat> { new() { Key = "/LM1", Name = "8TB 1" }, new() { Key = "/LM2", Name = "8TB 2" }, new() { Key = "/Media", Name = "System drive" } }, Now);
        Assert.Equal(4, page.Total);
        Assert.Equal(new[] { "Charlie", "Delta" }, page.Items.Select(i => i.Name).ToArray());
        Assert.Equal(2, page.Libraries.Count);
        Assert.Equal(2, page.Drives.Count); // only drives that hold any of these titles
        Assert.Equal(425 * Gb, page.TotalBytes);
    }
}
