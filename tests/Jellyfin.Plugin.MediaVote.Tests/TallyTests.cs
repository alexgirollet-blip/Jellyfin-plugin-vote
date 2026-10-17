using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaVote.Core;
using Xunit;

namespace Jellyfin.Plugin.MediaVote.Tests;

public class TallyTests
{
    private static List<ItemTally> Scores(params int[] scores)
        => Tally.Rank(scores.Select(sc => new ItemTally { ItemId = Guid.NewGuid(), RemoveVotes = Math.Max(0, sc), KeepVotes = Math.Max(0, -sc) }));

    [Fact]
    public void Count_ScoreIsRemoveMinusKeep_AndKeepsNames()
    {
        var s = Build.Session(keep: true);
        var a = s.Candidates[0].ItemId;
        Build.AddVotes(s, a, 3);
        Build.AddVotes(s, a, 1, VoteKind.Keep);
        var t = Tally.Count(s).First(x => x.ItemId == a);
        Assert.Equal(3, t.RemoveVotes);
        Assert.Equal(1, t.KeepVotes);
        Assert.Equal(2, t.Score);
        Assert.Equal(3, t.RemoveVoters.Count);
        Assert.Single(t.KeepVoters);
    }

    [Fact]
    public void Count_IgnoresKeepVotesWhenFeatureOff()
    {
        var s = Build.Session(keep: false);
        var a = s.Candidates[0].ItemId;
        Build.AddVotes(s, a, 2);
        Build.AddVotes(s, a, 5, VoteKind.Keep);
        Assert.Equal(2, Tally.Count(s).First(x => x.ItemId == a).Score);
    }

    [Fact]
    public void Count_IncludesZeroVoteCandidates_AndRanksByScore()
    {
        var s = Build.Session(items: 3);
        Build.AddVotes(s, s.Candidates[2].ItemId, 2);
        Build.AddVotes(s, s.Candidates[1].ItemId, 1);
        var ranking = Tally.Count(s);
        Assert.Equal(3, ranking.Count);
        Assert.Equal(s.Candidates[2].ItemId, ranking[0].ItemId);
        Assert.Equal(s.Candidates[1].ItemId, ranking[1].ItemId);
        Assert.Equal(0, ranking[2].Score);
    }

    [Fact]
    public void SelectWinners_TopN()
    {
        var r = Scores(5, 4, 3, 2);
        var w = Tally.SelectWinners(r, 2, TiePolicy.FlagForAdmin);
        Assert.Equal(new[] { 5, 4 }, w.Winners.Select(x => x.Score));
        Assert.False(w.TieCrossesCutoff);
    }

    [Fact]
    public void SelectWinners_ZeroAndNegativeScoresNeverWin()
    {
        var r = Scores(2, 0, -1);
        var w = Tally.SelectWinners(r, 3, TiePolicy.FlagForAdmin);
        Assert.Single(w.Winners);
        Assert.Equal(2, w.Winners[0].Score);
    }

    [Fact]
    public void SelectWinners_NoVotes_NoWinners()
    {
        var w = Tally.SelectWinners(Scores(0, 0, 0), 2, TiePolicy.FlagForAdmin);
        Assert.Empty(w.Winners);
        Assert.False(w.TieCrossesCutoff);
    }

    [Fact]
    public void SelectWinners_TieThatFitsExactly_IsNotAProblem()
    {
        var w = Tally.SelectWinners(Scores(5, 3, 3, 1), 3, TiePolicy.FlagForAdmin);
        Assert.Equal(new[] { 5, 3, 3 }, w.Winners.Select(x => x.Score));
        Assert.False(w.TieCrossesCutoff);
    }

    [Fact]
    public void SelectWinners_TieCrossingCutoff_DefaultActsOnNoneOfTheTie()
    {
        // N = 2: one clear winner (5), then three items tied at 3 for one remaining slot.
        var w = Tally.SelectWinners(Scores(5, 3, 3, 3, 1), 2, TiePolicy.FlagForAdmin);
        Assert.Equal(new[] { 5 }, w.Winners.Select(x => x.Score));
        Assert.True(w.TieCrossesCutoff);
        Assert.Equal(3, w.TiedAtCutoff.Count);
        Assert.Equal(1, w.OpenSlots);
    }

    [Fact]
    public void SelectWinners_EverythingTiedForSingleSlot()
    {
        var w = Tally.SelectWinners(Scores(2, 2), 1, TiePolicy.FlagForAdmin);
        Assert.Empty(w.Winners);
        Assert.Equal(2, w.TiedAtCutoff.Count);
        Assert.Equal(1, w.OpenSlots);
    }

    [Fact]
    public void SelectWinners_IncludeAllTiedPolicy_ExceedsN()
    {
        var w = Tally.SelectWinners(Scores(5, 3, 3, 3, 1), 2, TiePolicy.IncludeAllTied);
        Assert.Equal(4, w.Winners.Count);
        Assert.False(w.TieCrossesCutoff);
    }

    [Fact]
    public void SelectWinners_FewerPositiveThanN()
    {
        var w = Tally.SelectWinners(Scores(4, 1, 0), 5, TiePolicy.FlagForAdmin);
        Assert.Equal(2, w.Winners.Count);
    }

    [Theory]
    [InlineData(5, 10, 50, true)]
    [InlineData(4, 10, 50, false)]
    [InlineData(0, 10, 0, true)]
    [InlineData(0, 0, 10, false)]
    public void Turnout(int voters, int eligible, int min, bool met)
        => Assert.Equal(met, Tally.TurnoutMet(voters, eligible, min));

    [Fact]
    public void VoterCount_CountsDistinctUsers()
    {
        var s = Build.Session();
        var u = Guid.NewGuid();
        s.Votes.Add(new VoteRecord { UserId = u, ItemId = s.Candidates[0].ItemId });
        s.Votes.Add(new VoteRecord { UserId = u, ItemId = s.Candidates[1].ItemId });
        s.Votes.Add(new VoteRecord { UserId = Guid.NewGuid(), ItemId = s.Candidates[1].ItemId });
        Assert.Equal(2, Tally.VoterCount(s));
        Assert.Equal(66.7, Tally.TurnoutPercent(2, 3));
    }
}
