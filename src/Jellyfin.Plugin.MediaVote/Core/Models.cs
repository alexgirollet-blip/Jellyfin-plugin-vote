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
[JsonConverter(typeof(OutcomeModeJsonConverter))]
public enum OutcomeMode
{
    /// <summary>Produce a report for the admin; nothing is removed anywhere (default).</summary>
    NotifyOnly,

    /// <summary>
    /// Ask Sonarr (series) and Radarr (movies) to delete the winners and their files. Media Vote itself never deletes,
    /// moves or changes a media file. Votes saved by 1.0.x as "DeleteAutomatically" are read as this mode.
    /// </summary>
    DeleteViaArr
}

/// <summary>Reads <see cref="OutcomeMode"/> as a string, accepting the 1.0.x name "DeleteAutomatically".</summary>
public sealed class OutcomeModeJsonConverter : JsonConverter<OutcomeMode>
{
    public override OutcomeMode Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number)
        {
            return reader.GetInt32() == 1 ? OutcomeMode.DeleteViaArr : OutcomeMode.NotifyOnly;
        }

        var text = reader.GetString();
        return string.Equals(text, "DeleteViaArr", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "DeleteAutomatically", StringComparison.OrdinalIgnoreCase)
            ? OutcomeMode.DeleteViaArr
            : OutcomeMode.NotifyOnly;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, OutcomeMode value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
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
    IncludeAllTied,

    /// <summary>The larger title wins the free place(s). Exactly equal sizes are still flagged for the admin.</summary>
    LargerSizeWins,

    /// <summary>The title watched longest ago (never watched first) wins. Equal dates are flagged for the admin.</summary>
    LeastRecentlyWatchedWins,

    /// <summary>The title added longest ago wins. Equal dates are flagged for the admin.</summary>
    OldestAddedWins
}

/// <summary>How many winners are acted on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TargetMode
{
    /// <summary>The top N (default).</summary>
    FixedCount,

    /// <summary>Winners in rank order until the target drive has at least TargetValue GB free.</summary>
    FreeSpace,

    /// <summary>Winners in rank order until the target drive is below TargetValue % full.</summary>
    BelowPercent
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

    /// <summary>Sonarr/Radarr deleted it (with its files) and confirmed it is gone.</summary>
    Removed,

    /// <summary>No single Sonarr/Radarr entry matches it (not managed, ambiguous, or no instance configured): remove it by hand.</summary>
    NotMatched,

    /// <summary>A Sonarr/Radarr instance could not be reached; nothing was deleted.</summary>
    ArrUnreachable,

    /// <summary>Sonarr/Radarr refused the delete; their message is shown verbatim.</summary>
    DeleteRefused,

    /// <summary>The delete call returned, but Sonarr/Radarr still list the title (or could not be asked).</summary>
    VerificationFailed,

    /// <summary>A configured Jellyseerr step failed, so the title was not deleted.</summary>
    ExternalStepFailed,

    /// <summary>The title (or its library) is protected and is never removed by a vote.</summary>
    Protected,

    /// <summary>Not attempted (kill switch, maximum titles per vote reached, or the vote was paused).</summary>
    NotAttempted,

    /// <summary>Part of a tie across the cut-off; waiting for the admin.</summary>
    TiedAwaitingAdmin,

    /// <summary>1.0.x only: deleted by Jellyfin. Kept so old results still load.</summary>
    Deleted,

    /// <summary>1.0.x only: an external step failed. Kept so old results still load.</summary>
    SkippedExternalFailure,

    /// <summary>1.0.x only: Jellyfin's own write-permission check blocked deletion. Kept so old results still load.</summary>
    PreflightFailed,

    /// <summary>1.0.x only: Jellyfin's deletion failed. Kept so old results still load.</summary>
    DeleteFailed
}

/// <summary>Removing the stale Jellyfin entry after Sonarr/Radarr deleted the files. Files are never touched by Media Vote.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JellyfinCleanup
{
    /// <summary>Nothing to do (not removed).</summary>
    None,

    /// <summary>The files were gone, so the Jellyfin entry was removed (without deleting files).</summary>
    Done,

    /// <summary>The files were still on disk right after the delete; a library refresh was requested and Media Vote checks again every hour.</summary>
    Pending,

    /// <summary>The files are still on disk an hour or more after Sonarr/Radarr reported success: check Sonarr/Radarr.</summary>
    FilesStillOnDisk,

    /// <summary>Jellyfin refused to drop the entry; it will disappear at the next library scan.</summary>
    Failed
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

    /// <summary>Name of the user who played it most recently (Jellyfin user data), if known.</summary>
    public string? LastPlayedBy { get; set; }

    /// <summary>Users who have played it (any episode, for a series).</summary>
    public List<Guid> ViewerIds { get; set; } = new();

    /// <summary>Series only: number of episodes on disk.</summary>
    public int EpisodeCount { get; set; }

    /// <summary>Series only: episodes that at least one user has played.</summary>
    public int PlayedEpisodeCount { get; set; }

    /// <summary>Bytes on each drive (key = the drive's mount point, see <see cref="DriveStat.Key"/>). A series can span drives.</summary>
    public Dictionary<string, long> DriveBytes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>True when exactly one Sonarr/Radarr entry manages it, false when none does, null when unknown (checked read-only when the vote is built).</summary>
    public bool? Removable { get; set; }

    /// <summary>Why it is or is not removable automatically (matched entry, or the reason).</summary>
    public string? RemovalNote { get; set; }
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

    /// <summary>Must be true for <see cref="OutcomeMode.DeleteViaArr"/>; set by an explicit checkbox.</summary>
    public bool DeleteConfirmed { get; set; }

    /// <summary>
    /// For <see cref="OutcomeMode.DeleteViaArr"/>: also let people vote on titles Sonarr/Radarr do not manage (off by default).
    /// If such a title wins it is listed for removal by hand; it is never deleted automatically.
    /// </summary>
    public bool IncludeUnmanagedTitles { get; set; }

    /// <summary>Safety cap on titles Sonarr/Radarr may delete for this vote. Copied from the settings by the server when the vote is created.</summary>
    public int MaxRemovals { get; set; }

    /// <summary>Top N (default), or a free-space target on one drive; <see cref="WinnerCount"/> is then the maximum.</summary>
    public TargetMode TargetMode { get; set; } = TargetMode.FixedCount;

    /// <summary>Drive key (mount point) for a free-space target.</summary>
    public string? TargetDrive { get; set; }

    /// <summary>GB free (FreeSpace) or percent full (BelowPercent).</summary>
    public double TargetValue { get; set; }

    /// <summary>"Delete via Sonarr/Radarr": prepare the list at the deadline and wait for the admin's approval. Server-set from Settings.</summary>
    public bool RequireApproval { get; set; }

    /// <summary>Approve automatically this many hours after the deadline if the admin has not acted (0 = never). Server-set.</summary>
    public int AutoApproveHours { get; set; }

    /// <summary>Template this vote was created from, if any.</summary>
    public string? TemplateId { get; set; }

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

    /// <summary>Instance that manages the title (for example "Sonarr Anime").</summary>
    public string? ArrInstance { get; set; }

    /// <summary>Sonarr series id / Radarr movie id.</summary>
    public int? ArrId { get; set; }

    public string? ArrTitle { get; set; }

    /// <summary>Size on disk as Sonarr/Radarr report it (what their delete frees).</summary>
    public long ArrSizeOnDisk { get; set; }

    /// <summary>True when the admin has to look at this title (failure, or a manual deletion).</summary>
    public bool NeedsAttention { get; set; }

    public JellyfinCleanup JellyfinCleanup { get; set; }

    public string? CleanupNote { get; set; }
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

    /// <summary>Titles prepared for approval, in rank order.</summary>
    public List<Guid> PlannedItemIds { get; set; } = new();

    /// <summary>When the auto-approve timer fires (null = never).</summary>
    public DateTime? ApprovalDueUtc { get; set; }

    public string? ApprovedBy { get; set; }

    public DateTime? ApprovedUtc { get; set; }
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
    Skipped,

    /// <summary>"Delete via Sonarr/Radarr": the final list is ready and waits for the admin's approval. Nothing has been touched.</summary>
    AwaitingApproval,

    /// <summary>The admin rejected the prepared list: nothing was removed.</summary>
    Rejected
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
