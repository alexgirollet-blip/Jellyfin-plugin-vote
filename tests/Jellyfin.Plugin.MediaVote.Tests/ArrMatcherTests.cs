using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaVote.Core;
using Xunit;

namespace Jellyfin.Plugin.MediaVote.Tests;

public class ArrMatcherTests
{
    private static ArrLibrary Lib(params ArrEntry[] entries)
        => new(entries, new Dictionary<ArrKind, int> { [ArrKind.Radarr] = 1, [ArrKind.Sonarr] = 2 });

    private static ArrEntry Series(int id, string title, int? tvdb = null, int? tmdb = null, string? imdb = null, string instance = "Sonarr", int? year = 2008, string? path = null)
        => new() { Instance = instance, Kind = ArrKind.Sonarr, Id = id, Title = title, TvdbId = tvdb, TmdbId = tmdb, ImdbId = imdb, Year = year, Path = path ?? "/tv/" + title };

    private static MatchKey Key(MediaKind kind, string name, int? year, string? path, params (string K, string V)[] ids)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in ids)
        {
            d[k] = v;
        }

        return new MatchKey(kind, d, name, year, path);
    }

    [Fact]
    public void AllIdsPointingAtOneEntry_Matches()
    {
        var lib = Lib(Series(1, "Breaking Bad", tvdb: 81189, tmdb: 1396, imdb: "tt0903747"));
        var r = ArrMatcher.Match(Key(MediaKind.Series, "Breaking Bad", 2008, null, ("Tvdb", "81189"), ("Tmdb", "1396"), ("Imdb", "tt0903747")), lib);
        Assert.Equal(ArrMatchState.Matched, r.State);
        Assert.Equal(1, r.Entry!.Id);
    }

    [Fact]
    public void IdsThatDisagree_AreAmbiguous()
    {
        var lib = Lib(Series(1, "A", tvdb: 100), Series(2, "B", tmdb: 200));
        Assert.Equal(ArrMatchState.Ambiguous, ArrMatcher.Match(Key(MediaKind.Series, "A", null, null, ("Tvdb", "100"), ("Tmdb", "200")), lib).State);
    }

    [Fact]
    public void SameTitleInTwoInstances_IsAmbiguous()
    {
        var lib = Lib(Series(1, "A", tvdb: 100), Series(5, "A", tvdb: 100, instance: "Sonarr Anime"));
        var r = ArrMatcher.Match(Key(MediaKind.Series, "A", null, null, ("Tvdb", "100")), lib);
        Assert.Equal(ArrMatchState.Ambiguous, r.State);
        Assert.Null(r.Entry);
    }

    [Fact]
    public void IdsPresentButNoEntry_IsNotManaged_NoTitleGuessing()
    {
        // Same title/year/folder exists, but the ids differ: never fall back to the name.
        var lib = Lib(Series(1, "A", tvdb: 100, path: "/tv/A"));
        Assert.Equal(ArrMatchState.NotManaged, ArrMatcher.Match(Key(MediaKind.Series, "A", 2008, "/LM1/TV Shows/A", ("Tvdb", "999")), lib).State);
    }

    [Fact]
    public void NoIds_FallsBackToTitleYearAndFolder()
    {
        var lib = Lib(Series(1, "The Office (US)", path: "/tv/The Office (US)", year: 2005));
        var r = ArrMatcher.Match(Key(MediaKind.Series, "Office (US)", 2005, "/LM1/TV Shows/The Office (US)"), lib);
        Assert.Equal(ArrMatchState.Matched, r.State);
        Assert.Contains("title, year and folder", r.Reason);
    }

    [Fact]
    public void NoIds_WrongYearOrFolder_IsNotMatched()
    {
        var lib = Lib(Series(1, "A", path: "/tv/A", year: 2005));
        Assert.Equal(ArrMatchState.NotManaged, ArrMatcher.Match(Key(MediaKind.Series, "A", 2006, "/LM1/TV Shows/A"), lib).State);
        Assert.Equal(ArrMatchState.NotManaged, ArrMatcher.Match(Key(MediaKind.Series, "A", 2005, "/LM1/TV Shows/A (2005)"), lib).State);
        Assert.Equal(ArrMatchState.NotManaged, ArrMatcher.Match(Key(MediaKind.Series, "A", null, "/LM1/TV Shows/A"), lib).State);
    }

    [Fact]
    public void NoIds_TwoEntriesSameTitleYearFolder_IsAmbiguous()
    {
        var lib = Lib(Series(1, "A", path: "/tv/A", year: 2005), Series(2, "A", path: "/anime/A", year: 2005, instance: "Sonarr Anime"));
        Assert.Equal(ArrMatchState.Ambiguous, ArrMatcher.Match(Key(MediaKind.Series, "A", 2005, "/x/A"), lib).State);
    }

    [Fact]
    public void MoviesNeverMatchSeries_AndZeroOrJunkIdsAreIgnored()
    {
        var lib = Lib(Series(1, "A", tmdb: 5));
        Assert.Equal(ArrMatchState.NotManaged, ArrMatcher.Match(Key(MediaKind.Movie, "A", null, null, ("Tmdb", "5")), lib).State);
        var junk = ArrMatcher.Match(Key(MediaKind.Series, "A", null, null, ("Tvdb", "0"), ("Imdb", "nope")), lib);
        Assert.Equal(ArrMatchState.NotManaged, junk.State);
        Assert.Contains("fallback", junk.Reason);
    }

    [Fact]
    public void NothingConfigured_OrUnreachable_IsReportedAsSuch()
    {
        var none = new ArrLibrary(Array.Empty<ArrEntry>(), new Dictionary<ArrKind, int>());
        Assert.Equal(ArrMatchState.NotConfigured, ArrMatcher.Match(Key(MediaKind.Movie, "A", null, null, ("Tmdb", "1")), none).State);
        var down = new ArrLibrary(Array.Empty<ArrEntry>(), new Dictionary<ArrKind, int> { [ArrKind.Radarr] = 1 }, new Dictionary<ArrKind, List<string>> { [ArrKind.Radarr] = new() { "Radarr" } });
        Assert.Equal(ArrMatchState.Unreachable, ArrMatcher.Match(Key(MediaKind.Movie, "A", null, null, ("Tmdb", "1")), down).State);
    }

    [Fact]
    public void ImdbMatching_IsCaseInsensitive()
    {
        var lib = new ArrLibrary(new[] { new ArrEntry { Instance = "Radarr", Kind = ArrKind.Radarr, Id = 9, Title = "M", ImdbId = "TT0133093" } }, new Dictionary<ArrKind, int> { [ArrKind.Radarr] = 1 });
        Assert.Equal(9, ArrMatcher.Match(Key(MediaKind.Movie, "M", null, null, ("Imdb", "tt0133093")), lib).Entry!.Id);
    }

    [Theory]
    [InlineData("The Office (US)", "office us")]
    [InlineData("Amélie", "amelie")]
    [InlineData("The", "the")]
    [InlineData("  Spider-Man: No Way Home ", "spider man no way home")]
    public void NormalizeTitle(string input, string expected) => Assert.Equal(expected, ArrMatcher.NormalizeTitle(input));

    [Fact]
    public void Removability_MarksCandidates()
    {
        var lib = new ArrLibrary(new[] { new ArrEntry { Instance = "Radarr", Kind = ArrKind.Radarr, Id = 1, Title = "M", TmdbId = 1 } }, new Dictionary<ArrKind, int> { [ArrKind.Radarr] = 1, [ArrKind.Sonarr] = 1 });
        var a = new CandidateItem { Name = "M", Kind = MediaKind.Movie, ProviderIds = new() { ["Tmdb"] = "1" } };
        var b = new CandidateItem { Name = "S", Kind = MediaKind.Series, ProviderIds = new() { ["Tvdb"] = "2" } };
        Removability.Annotate(new[] { a, b }, lib);
        Assert.True(a.Removable);
        Assert.False(b.Removable);
        Assert.Contains("Not managed by Sonarr", b.RemovalNote);
    }
}
