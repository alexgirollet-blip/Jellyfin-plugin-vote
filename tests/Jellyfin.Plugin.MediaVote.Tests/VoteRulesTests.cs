using System;
using System.Linq;
using Jellyfin.Plugin.MediaVote.Core;
using Xunit;

namespace Jellyfin.Plugin.MediaVote.Tests;

public class VoteRulesTests
{
    private static readonly Voter Alice = new(Guid.NewGuid(), "alice");
    private static readonly Voter Bob = new(Guid.NewGuid(), "bob");

    [Fact]
    public void AllUsers_AnyActiveUserIsEligible()
    {
        var s = Build.Session();
        Assert.True(VoteRules.IsEligible(s, Alice.UserId, userIsActive: true));
        Assert.False(VoteRules.IsEligible(s, Alice.UserId, userIsActive: false));
        Assert.False(VoteRules.IsEligible(s, Guid.Empty, userIsActive: true));
    }

    [Fact]
    public void SpecificUsers_OnlyListedUsersAreEligible()
    {
        var s = Build.Session();
        s.Settings.AllUsers = false;
        s.Settings.EligibleUserIds.Add(Alice.UserId);
        Assert.True(VoteRules.IsEligible(s, Alice.UserId, true));
        Assert.False(VoteRules.IsEligible(s, Bob.UserId, true));
    }

    [Fact]
    public void IneligibleUser_CannotVote()
    {
        var s = Build.Session();
        s.Settings.AllUsers = false;
        s.Settings.EligibleUserIds.Add(Alice.UserId);
        var r = VoteRules.Cast(s, Bob, true, s.Candidates[0].ItemId, VoteKind.Remove, Build.Now);
        Assert.False(r.Ok);
        Assert.Equal(VoteErrors.NotEligible, r.Error);
        Assert.Empty(s.Votes);
    }

    [Fact]
    public void DisabledUser_CannotVote()
    {
        var s = Build.Session();
        var r = VoteRules.Cast(s, Alice, userIsActive: false, s.Candidates[0].ItemId, VoteKind.Remove, Build.Now);
        Assert.Equal(VoteErrors.NotEligible, r.Error);
    }

    [Fact]
    public void Cast_StoresUserNameAndTimestamp()
    {
        var s = Build.Session();
        var r = VoteRules.Cast(s, Alice, true, s.Candidates[0].ItemId, VoteKind.Remove, Build.Now);
        Assert.True(r.Ok);
        var v = Assert.Single(s.Votes);
        Assert.Equal("alice", v.UserName);
        Assert.Equal(Alice.UserId, v.UserId);
        Assert.Equal(Build.Now, v.CastUtc);
    }

    [Fact]
    public void VoteLimit_IsEnforced()
    {
        var s = Build.Session(items: 5, votesPerUser: 2);
        Assert.True(VoteRules.Cast(s, Alice, true, s.Candidates[0].ItemId, VoteKind.Remove, Build.Now).Ok);
        Assert.True(VoteRules.Cast(s, Alice, true, s.Candidates[1].ItemId, VoteKind.Remove, Build.Now).Ok);
        var third = VoteRules.Cast(s, Alice, true, s.Candidates[2].ItemId, VoteKind.Remove, Build.Now);
        Assert.Equal(VoteErrors.NoVotesRemaining, third.Error);
        Assert.Equal(0, VoteRules.RemainingVotes(s, Alice.UserId, VoteKind.Remove));
        Assert.Equal(2, VoteRules.RemainingVotes(s, Bob.UserId, VoteKind.Remove));
    }

    [Fact]
    public void DuplicateVotes_RejectedByDefault()
    {
        var s = Build.Session();
        var item = s.Candidates[0].ItemId;
        Assert.True(VoteRules.Cast(s, Alice, true, item, VoteKind.Remove, Build.Now).Ok);
        Assert.Equal(VoteErrors.DuplicateNotAllowed, VoteRules.Cast(s, Alice, true, item, VoteKind.Remove, Build.Now).Error);
    }

    [Fact]
    public void DuplicateVotes_AllowedWhenAdminEnablesIt()
    {
        var s = Build.Session(multiple: true, votesPerUser: 3);
        var item = s.Candidates[0].ItemId;
        for (int i = 0; i < 3; i++)
        {
            Assert.True(VoteRules.Cast(s, Alice, true, item, VoteKind.Remove, Build.Now).Ok);
        }

        Assert.Equal(VoteErrors.NoVotesRemaining, VoteRules.Cast(s, Alice, true, item, VoteKind.Remove, Build.Now).Error);
    }

    [Fact]
    public void DeadlinePassed_RejectsVotesAndWithdrawals()
    {
        var s = Build.Session();
        var item = s.Candidates[0].ItemId;
        Assert.True(VoteRules.Cast(s, Alice, true, item, VoteKind.Remove, Build.Now).Ok);
        var atDeadline = s.Settings.DeadlineUtc;
        Assert.Equal(VoteErrors.DeadlinePassed, VoteRules.Cast(s, Bob, true, item, VoteKind.Remove, atDeadline).Error);
        Assert.Equal(VoteErrors.DeadlinePassed, VoteRules.Withdraw(s, Alice, true, item, VoteKind.Remove, atDeadline.AddSeconds(1)).Error);
        Assert.Single(s.Votes);
    }

    [Fact]
    public void CancelledSession_RejectsVotes()
    {
        var s = Build.Session();
        s.Status = SessionStatus.Cancelled;
        Assert.Equal(VoteErrors.SessionNotOpen, VoteRules.Cast(s, Alice, true, s.Candidates[0].ItemId, VoteKind.Remove, Build.Now).Error);
    }

    [Fact]
    public void ItemOutsideCandidates_IsRejected()
    {
        var s = Build.Session();
        Assert.Equal(VoteErrors.ItemNotVotable, VoteRules.Cast(s, Alice, true, Guid.NewGuid(), VoteKind.Remove, Build.Now).Error);
    }

    [Fact]
    public void Withdraw_OnlyRemovesOwnVote()
    {
        var s = Build.Session();
        var item = s.Candidates[0].ItemId;
        VoteRules.Cast(s, Alice, true, item, VoteKind.Remove, Build.Now);
        Assert.Equal(VoteErrors.NoSuchVote, VoteRules.Withdraw(s, Bob, true, item, VoteKind.Remove, Build.Now).Error);
        Assert.True(VoteRules.Withdraw(s, Alice, true, item, VoteKind.Remove, Build.Now).Ok);
        Assert.Empty(s.Votes);
    }

    [Fact]
    public void Move_IsAtomic_RollsBackWhenTargetInvalid()
    {
        var s = Build.Session();
        var a = s.Candidates[0].ItemId;
        VoteRules.Cast(s, Alice, true, a, VoteKind.Remove, Build.Now);
        var r = VoteRules.Move(s, Alice, true, a, Guid.NewGuid(), VoteKind.Remove, Build.Now);
        Assert.False(r.Ok);
        Assert.Equal(a, Assert.Single(s.Votes).ItemId);
    }

    [Fact]
    public void Move_MovesVote()
    {
        var s = Build.Session(votesPerUser: 1);
        var a = s.Candidates[0].ItemId;
        var b = s.Candidates[1].ItemId;
        VoteRules.Cast(s, Alice, true, a, VoteKind.Remove, Build.Now);
        Assert.True(VoteRules.Move(s, Alice, true, a, b, VoteKind.Remove, Build.Now).Ok);
        Assert.Equal(b, Assert.Single(s.Votes).ItemId);
    }

    [Fact]
    public void KeepVotes_DisabledByDefault()
    {
        var s = Build.Session();
        Assert.Equal(VoteErrors.KeepVotesDisabled, VoteRules.Cast(s, Alice, true, s.Candidates[0].ItemId, VoteKind.Keep, Build.Now).Error);
    }

    [Fact]
    public void KeepVotes_HaveOwnBudget_AndCannotMixWithRemoveOnSameItem()
    {
        var s = Build.Session(keep: true, votesPerUser: 1);
        var a = s.Candidates[0].ItemId;
        var b = s.Candidates[1].ItemId;
        Assert.True(VoteRules.Cast(s, Alice, true, a, VoteKind.Remove, Build.Now).Ok);
        Assert.Equal(VoteErrors.DuplicateNotAllowed, VoteRules.Cast(s, Alice, true, a, VoteKind.Keep, Build.Now).Error);
        Assert.True(VoteRules.Cast(s, Alice, true, b, VoteKind.Keep, Build.Now).Ok);
        Assert.Equal(VoteErrors.NoVotesRemaining, VoteRules.Cast(s, Alice, true, s.Candidates[2].ItemId, VoteKind.Keep, Build.Now).Error);
    }

    [Fact]
    public void Deadline_OpenDueAndClosingSoon()
    {
        var s = Build.Session();
        var deadline = s.Settings.DeadlineUtc;
        Assert.True(VoteRules.IsOpen(s, deadline.AddTicks(-1)));
        Assert.False(VoteRules.IsOpen(s, deadline));
        Assert.True(VoteRules.IsDueForResolution(s, deadline));
        Assert.False(VoteRules.IsDueForResolution(s, deadline.AddTicks(-1)));
        Assert.True(VoteRules.IsClosingSoon(s, deadline.AddHours(-2), TimeSpan.FromHours(24)));
        Assert.False(VoteRules.IsClosingSoon(s, deadline.AddHours(-25), TimeSpan.FromHours(24)));
        s.Status = SessionStatus.Resolved;
        Assert.False(VoteRules.IsDueForResolution(s, deadline.AddDays(5)));
    }

    [Fact]
    public void ValidateSettings_RequiresConfirmationForDeletion()
    {
        var s = new SessionSettings { Title = "x", DeadlineUtc = Build.Now.AddDays(1), Outcome = OutcomeMode.DeleteAutomatically };
        Assert.Contains(VoteRules.ValidateSettings(s, Build.Now, 3), e => e.Contains("confirmation", StringComparison.OrdinalIgnoreCase));
        s.DeleteConfirmed = true;
        Assert.Empty(VoteRules.ValidateSettings(s, Build.Now, 3));
    }

    [Fact]
    public void ValidateSettings_RejectsPastDeadlineAndEmptyScope()
    {
        var s = new SessionSettings { Title = "x", DeadlineUtc = Build.Now.AddMinutes(-1) };
        var errors = VoteRules.ValidateSettings(s, Build.Now, 0);
        Assert.Contains(errors, e => e.Contains("future", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("No votable", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultOutcome_IsNotifyOnly()
    {
        Assert.Equal(OutcomeMode.NotifyOnly, new SessionSettings().Outcome);
        Assert.Equal(TiePolicy.FlagForAdmin, new SessionSettings().TiePolicy);
        Assert.False(new SessionSettings().AllowMultipleVotesPerItem);
        Assert.True(new SessionSettings().ShowVoterNames);
        Assert.True(new SessionSettings().ShowLiveTallies);
    }
}
