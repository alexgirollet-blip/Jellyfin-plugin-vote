using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>Error codes returned by <see cref="VoteRules"/>. The client shows friendly text for each.</summary>
public static class VoteErrors
{
    public const string SessionNotFound = "session_not_found";
    public const string SessionNotOpen = "session_not_open";
    public const string DeadlinePassed = "deadline_passed";
    public const string NotEligible = "not_eligible";
    public const string ItemNotVotable = "item_not_votable";
    public const string NoVotesRemaining = "no_votes_remaining";
    public const string DuplicateNotAllowed = "duplicate_not_allowed";
    public const string KeepVotesDisabled = "keep_votes_disabled";
    public const string NoSuchVote = "no_such_vote";
}

/// <summary>Outcome of a rule check.</summary>
public readonly record struct RuleResult(bool Ok, string? Error)
{
    public static RuleResult Success => new(true, null);

    public static RuleResult Fail(string error) => new(false, error);
}

/// <summary>Identity of the user acting. Always taken from the authenticated request, never from the body.</summary>
public readonly record struct Voter(Guid UserId, string UserName);

/// <summary>
/// Pure server-side vote rules: eligibility, vote limits, duplicates and deadline.
/// No Jellyfin dependency, so it is fully unit-testable.
/// </summary>
public static class VoteRules
{
    /// <summary>True while the session accepts votes at <paramref name="nowUtc"/>.</summary>
    public static bool IsOpen(VoteSession session, DateTime nowUtc)
        => session.Status == SessionStatus.Open && nowUtc < session.Settings.DeadlineUtc;

    /// <summary>True when the session is still open but its deadline has passed and it must be resolved.</summary>
    public static bool IsDueForResolution(VoteSession session, DateTime nowUtc)
        => session.Status == SessionStatus.Open && nowUtc >= session.Settings.DeadlineUtc;

    /// <summary>True when the "closing soon" reminder should be shown/sent.</summary>
    public static bool IsClosingSoon(VoteSession session, DateTime nowUtc, TimeSpan window)
        => IsOpen(session, nowUtc) && session.Settings.DeadlineUtc - nowUtc <= window;

    /// <summary>Whether a user may vote in this session.</summary>
    /// <param name="session">The session.</param>
    /// <param name="userId">User id.</param>
    /// <param name="userIsActive">False if the Jellyfin account is disabled or no longer exists.</param>
    public static bool IsEligible(VoteSession session, Guid userId, bool userIsActive)
    {
        if (!userIsActive || userId == Guid.Empty)
        {
            return false;
        }

        return session.Settings.AllUsers || session.Settings.EligibleUserIds.Contains(userId);
    }

    public static int UsedVotes(VoteSession session, Guid userId, VoteKind kind)
        => session.Votes.Count(v => v.UserId == userId && v.Kind == kind);

    public static int Allowance(VoteSession session, VoteKind kind)
        => kind == VoteKind.Remove
            ? Math.Max(0, session.Settings.VotesPerUser)
            : (session.Settings.AllowKeepVotes ? Math.Max(0, session.Settings.KeepVotesPerUser) : 0);

    public static int RemainingVotes(VoteSession session, Guid userId, VoteKind kind)
        => Math.Max(0, Allowance(session, kind) - UsedVotes(session, userId, kind));

    /// <summary>Checks and applies a new vote. Mutates <paramref name="session"/> only on success.</summary>
    public static RuleResult Cast(VoteSession session, Voter voter, bool userIsActive, Guid itemId, VoteKind kind, DateTime nowUtc)
    {
        var check = CheckCanAct(session, voter, userIsActive, nowUtc);
        if (!check.Ok)
        {
            return check;
        }

        if (!session.Candidates.Any(c => c.ItemId == itemId))
        {
            return RuleResult.Fail(VoteErrors.ItemNotVotable);
        }

        if (kind == VoteKind.Keep && !session.Settings.AllowKeepVotes)
        {
            return RuleResult.Fail(VoteErrors.KeepVotesDisabled);
        }

        if (RemainingVotes(session, voter.UserId, kind) <= 0)
        {
            return RuleResult.Fail(VoteErrors.NoVotesRemaining);
        }

        var userVotesOnItem = session.Votes.Where(v => v.UserId == voter.UserId && v.ItemId == itemId).ToList();

        // A user can never vote both "remove" and "keep" on the same item.
        if (userVotesOnItem.Any(v => v.Kind != kind))
        {
            return RuleResult.Fail(VoteErrors.DuplicateNotAllowed);
        }

        if (!session.Settings.AllowMultipleVotesPerItem && userVotesOnItem.Count > 0)
        {
            return RuleResult.Fail(VoteErrors.DuplicateNotAllowed);
        }

        session.Votes.Add(new VoteRecord
        {
            UserId = voter.UserId,
            UserName = voter.UserName,
            ItemId = itemId,
            Kind = kind,
            CastUtc = nowUtc
        });

        return RuleResult.Success;
    }

    /// <summary>Removes one of the user's own votes on an item (the most recent one).</summary>
    public static RuleResult Withdraw(VoteSession session, Voter voter, bool userIsActive, Guid itemId, VoteKind kind, DateTime nowUtc)
    {
        var check = CheckCanAct(session, voter, userIsActive, nowUtc);
        if (!check.Ok)
        {
            return check;
        }

        var vote = session.Votes
            .Where(v => v.UserId == voter.UserId && v.ItemId == itemId && v.Kind == kind)
            .OrderByDescending(v => v.CastUtc)
            .FirstOrDefault();

        if (vote is null)
        {
            return RuleResult.Fail(VoteErrors.NoSuchVote);
        }

        session.Votes.Remove(vote);
        return RuleResult.Success;
    }

    /// <summary>Atomically moves one vote from one item to another. Either both happen or neither.</summary>
    public static RuleResult Move(VoteSession session, Voter voter, bool userIsActive, Guid fromItemId, Guid toItemId, VoteKind kind, DateTime nowUtc)
    {
        var snapshot = session.Votes.ToList();
        var withdrawn = Withdraw(session, voter, userIsActive, fromItemId, kind, nowUtc);
        if (!withdrawn.Ok)
        {
            return withdrawn;
        }

        var cast = Cast(session, voter, userIsActive, toItemId, kind, nowUtc);
        if (!cast.Ok)
        {
            session.Votes.Clear();
            session.Votes.AddRange(snapshot);
        }

        return cast;
    }

    /// <summary>Validates admin-entered settings. Returns a list of human-readable problems (empty = valid).</summary>
    public static List<string> ValidateSettings(SessionSettings s, DateTime nowUtc, int candidateCount)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(s.Title))
        {
            errors.Add("A title is required.");
        }
        else if (s.Title.Length > 200)
        {
            errors.Add("The title must be 200 characters or fewer.");
        }

        if (s.Description is { Length: > 2000 })
        {
            errors.Add("The description must be 2000 characters or fewer.");
        }

        if (!s.AllUsers && s.EligibleUserIds.Count == 0)
        {
            errors.Add("Select at least one eligible voter, or choose all users.");
        }

        if (s.VotesPerUser < 1 || s.VotesPerUser > 1000)
        {
            errors.Add("Votes per user must be between 1 and 1000.");
        }

        if (s.AllowKeepVotes && (s.KeepVotesPerUser < 1 || s.KeepVotesPerUser > 1000))
        {
            errors.Add("Keep votes per user must be between 1 and 1000.");
        }

        if (s.WinnerCount < 1 || s.WinnerCount > 1000)
        {
            errors.Add("The number of winners must be between 1 and 1000.");
        }

        if (s.DeadlineUtc <= nowUtc)
        {
            errors.Add("The deadline must be in the future.");
        }

        if (s.MinTurnoutPercent < 0 || s.MinTurnoutPercent > 100)
        {
            errors.Add("Minimum turnout must be between 0 and 100%.");
        }

        if (s.FilterNotWatchedMonths < 0 || s.FilterLargestCount < 0)
        {
            errors.Add("Filters cannot be negative.");
        }

        if (s.Outcome == OutcomeMode.DeleteViaArr && !s.DeleteConfirmed)
        {
            errors.Add("\"Delete via Sonarr/Radarr\" needs the explicit confirmation checkbox.");
        }

        if (s.Outcome == OutcomeMode.DeleteViaArr && s.MaxRemovals > 0 && s.WinnerCount > s.MaxRemovals)
        {
            errors.Add($"\"Delete via Sonarr/Radarr\" can remove at most {s.MaxRemovals} title(s) per vote (Settings). Lower the number of winners or raise the maximum.");
        }

        if (s.Scope == ScopeMode.SpecificLibraries && s.LibraryIds.Count == 0)
        {
            errors.Add("Select at least one library.");
        }

        if (s.Scope == ScopeMode.HandPicked && s.HandPickedItemIds.Count == 0)
        {
            errors.Add("Pick at least one item.");
        }

        if (candidateCount == 0)
        {
            errors.Add("No votable movies or series match this scope and filters.");
        }

        return errors;
    }

    private static RuleResult CheckCanAct(VoteSession session, Voter voter, bool userIsActive, DateTime nowUtc)
    {
        if (session.Status != SessionStatus.Open)
        {
            return RuleResult.Fail(VoteErrors.SessionNotOpen);
        }

        if (nowUtc >= session.Settings.DeadlineUtc)
        {
            return RuleResult.Fail(VoteErrors.DeadlinePassed);
        }

        if (!IsEligible(session, voter.UserId, userIsActive))
        {
            return RuleResult.Fail(VoteErrors.NotEligible);
        }

        return RuleResult.Success;
    }
}
