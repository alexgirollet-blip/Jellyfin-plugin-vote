using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>Kind of votable media. Only whole movies and whole series are votable.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MediaKind
{
    Movie,
    Series
}

/// <summary>Lifecycle state of a vote session.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionStatus
{
    /// <summary>Accepting votes until the deadline.</summary>
    Open,

    /// <summary>Deadline passed (or closed early) and the result has been computed.</summary>
    Resolved,

    /// <summary>Cancelled by an admin; nothing happens.</summary>
    Cancelled
}

/// <summary>What happens to the winners once the vote is resolved.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutcomeMode
{
    /// <summary>Produce a report for the admin; delete nothing (default).</summary>
    NotifyOnly,

    /// <summary>Delete winners from Jellyfin and from disk, after the Radarr/Sonarr/Jellyseerr steps.</summary>
    DeleteAutomatically
}

/// <summary>How the admin selects the votable items.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeMode
{
    AllLibraries,
    SpecificLibraries,
    MoviesOnly,
    SeriesOnly,
    HandPicked
}

/// <summary>What happens when a tie crosses the winner cut-off.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TiePolicy
{
    /// <summary>Act on none of the tied items; flag them for the admin to break (default).</summary>
    FlagForAdmin,

    /// <summary>Act on every tied item, even if that exceeds N.</summary>
    IncludeAllTied
}

/// <summary>A vote either asks for removal or (optionally) to keep an item.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VoteKind
{
    Remove,
    Keep
}

/// <summary>Per-item state in the outcome of a resolution.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ItemOutcomeStatus
{
    /// <summary>Notify-only / dry-run: listed for the admin, nothing changed.</summary>
    Reported,

    /// <summary>Deleted from Jellyfin and disk.</summary>
    Deleted,

    /// <summary>A Radarr/Sonarr/Jellyseerr step failed, so this item was not deleted.</summary>
    SkippedExternalFailure,

    /// <summary>Pre-flight check failed (missing, protected, no write permission).</summary>
    PreflightFailed,

    /// <summary>The Jellyfin deletion itself failed.</summary>
    DeleteFailed,

    /// <summary>Not attempted because an earlier failure stopped the run.</summary>
    NotAttempted,

    /// <summary>Part of a tie across the cut-off; waiting for the admin.</summary>
    TiedAwaitingAdmin
}

/// <summary>Snapshot of a votable item, frozen when the vote is created.</summary>
public sealed class CandidateItem
{
    public Guid ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public MediaKind Kind { get; set; }

    public string LibraryId { get; set; } = string.Empty;

    public string LibraryName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTime? DateAddedUtc { get; set; }

    public DateTime? LastPlayedUtc { get; set; }

    public int PlayCount { get; set; }

    public List<string> Genres { get; set; } = new();

    public string? PrimaryImageTag { get; set; }

    public string? Path { get; set; }

    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A single vote. Votes are not anonymous: user and time are always stored.</summary>
public sealed class VoteRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public Guid ItemId { get; set; }

    public VoteKind Kind { get; set; } = VoteKind.Remove;

    public DateTime CastUtc { get; set; }
}

/// <summary>Settings chosen by the admin when creating a vote.</summary>
public sealed class SessionSettings
{
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool AllUsers { get; set; } = true;

    public List<Guid> EligibleUserIds { get; set; } = new();

    public ScopeMode Scope { get; set; } = ScopeMode.AllLibraries;

    public List<string> LibraryIds { get; set; } = new();

    public List<Guid> HandPickedItemIds { get; set; } = new();

    /// <summary>Optional filter: only items nobody has ever played.</summary>
    public bool FilterNeverWatched { get; set; }

    /// <summary>Optional filter: only items not played by anyone in this many months (0 = off).</summary>
    public int FilterNotWatchedMonths { get; set; }

    /// <summary>Optional filter: keep only the N largest items (0 = off).</summary>
    public int FilterLargestCount { get; set; }

    public int VotesPerUser { get; set; } = 3;

    public bool AllowMultipleVotesPerItem { get; set; }

    public bool AllowKeepVotes { get; set; }

    public int KeepVotesPerUser { get; set; } = 1;

    public DateTime DeadlineUtc { get; set; }

    public int WinnerCount { get; set; } = 1;

    public OutcomeMode Outcome { get; set; } = OutcomeMode.NotifyOnly;

    /// <summary>Must be true for <see cref="OutcomeMode.DeleteAutomatically"/>; set by an explicit checkbox.</summary>
    public bool DeleteConfirmed { get; set; }

    public TiePolicy TiePolicy { get; set; } = TiePolicy.FlagForAdmin;

    /// <summary>Show voter names to other participants (admins always see them).</summary>
    public bool ShowVoterNames { get; set; } = true;

    /// <summary>Show live tallies while the vote is open.</summary>
    public bool ShowLiveTallies { get; set; } = true;

    /// <summary>Minimum percentage of eligible voters that must vote (0 = off).</summary>
    public int MinTurnoutPercent { get; set; }
}

/// <summary>Tally for one item.</summary>
public sealed class ItemTally
{
    public Guid ItemId { get; set; }

    public int RemoveVotes { get; set; }

    public int KeepVotes { get; set; }

    public int Score => RemoveVotes - KeepVotes;

    public List<string> RemoveVoters { get; set; } = new();

    public List<string> KeepVoters { get; set; } = new();
}

/// <summary>Result of a single external (Radarr/Sonarr/Jellyseerr) step.</summary>
public sealed class ExternalStepResult
{
    public string Service { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    /// <summary>One of: Done, WouldDo, NotFound, NotConfigured, Skipped, Failed.</summary>
    public string Status { get; set; } = string.Empty;

    public string? MatchedTitle { get; set; }

    public string? ExternalId { get; set; }

    public string? Message { get; set; }

    [JsonIgnore]
    public bool IsFailure => Status == ExternalStepStatus.Failed;
}

/// <summary>String constants for <see cref="ExternalStepResult.Status"/>.</summary>
public static class ExternalStepStatus
{
    public const string Done = "Done";
    public const string WouldDo = "WouldDo";
    public const string NotFound = "NotFound";
    public const string NotConfigured = "NotConfigured";
    public const string Skipped = "Skipped";
    public const string Failed = "Failed";
}

/// <summary>Outcome for one winning item.</summary>
public sealed class ItemOutcome
{
    public Guid ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    public MediaKind Kind { get; set; }

    public int Score { get; set; }

    public ItemOutcomeStatus Status { get; set; }

    public string? Message { get; set; }

    public List<string> Paths { get; set; } = new();

    public long SizeBytes { get; set; }

    public List<ExternalStepResult> ExternalSteps { get; set; } = new();

    public DateTime? CompletedUtc { get; set; }
}

/// <summary>The computed result of a vote.</summary>
public sealed class SessionResult
{
    public DateTime ResolvedUtc { get; set; }

    public bool DryRun { get; set; }

    public OutcomeMode Outcome { get; set; }

    public int EligibleVoterCount { get; set; }

    public int VoterCount { get; set; }

    public double TurnoutPercent { get; set; }

    public bool TurnoutMet { get; set; } = true;

    /// <summary>True when an admin must act (tie, turnout not met, failure).</summary>
    public bool NeedsAdminAttention { get; set; }

    public List<string> Notes { get; set; } = new();

    public List<ItemTally> Ranking { get; set; } = new();

    public List<ItemOutcome> Items { get; set; } = new();

    /// <summary>Items tied across the cut-off (only with <see cref="TiePolicy.FlagForAdmin"/>).</summary>
    public List<Guid> TiedItemIds { get; set; } = new();

    /// <summary>Number of free winner slots the tied items compete for.</summary>
    public int TieSlots { get; set; }

    public long TotalBytesFreed { get; set; }

    /// <summary>Total size of all winners (what would be / was freed).</summary>
    public long TotalBytesSelected { get; set; }

    public ExecutionState Execution { get; set; } = ExecutionState.NotStarted;
}

/// <summary>Progress of acting on the winners. Used to detect a run interrupted by a crash/restart.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionState
{
    NotStarted,
    Running,
    Completed,
    Interrupted,

    /// <summary>Nothing to execute (turnout not met, no winners).</summary>
    Skipped
}

/// <summary>A vote session with its ballots and (once resolved) its result.</summary>
public sealed class VoteSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public SessionStatus Status { get; set; } = SessionStatus.Open;

    public DateTime CreatedUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public SessionSettings Settings { get; set; } = new();

    public List<CandidateItem> Candidates { get; set; } = new();

    public List<VoteRecord> Votes { get; set; } = new();

    public SessionResult? Result { get; set; }

    public bool OpenedNotified { get; set; }

    public bool ClosingSoonNotified { get; set; }

    public DateTime? CancelledUtc { get; set; }

    /// <summary>Users who dismissed the result banner.</summary>
    public List<Guid> ResultDismissedBy { get; set; } = new();
}
