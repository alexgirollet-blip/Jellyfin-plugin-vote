using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>Live view of a library item at resolution time (not the frozen candidate snapshot).</summary>
public sealed class MediaItemSnapshot
{
    public Guid ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? Year { get; set; }

    public MediaKind Kind { get; set; }

    public string LibraryName { get; set; } = string.Empty;

    /// <summary>The title's folder/file on disk (as Jellyfin sees it). Only ever checked for existence, never written.</summary>
    public List<string> DeletePaths { get; set; } = new();

    public long SizeBytes { get; set; }

    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsProtected { get; set; }

    public string? ProviderId(string name)
        => ProviderIds.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}

/// <summary>Result of dropping a stale Jellyfin entry after Sonarr/Radarr deleted the files.</summary>
public sealed record CleanupResult(JellyfinCleanup State, string Message);

/// <summary>
/// Access to Jellyfin's library for resolution. Media Vote never deletes, moves or changes media files: Sonarr and
/// Radarr do that. Jellyfin is only asked to refresh, and to forget an entry whose files are already gone.
/// </summary>
public interface IMediaGateway
{
    /// <summary>Returns the live item or null if it no longer exists.</summary>
    MediaItemSnapshot? GetItem(Guid itemId);

    /// <summary>True if any of the paths still exists on disk (read-only check).</summary>
    bool AnyPathExists(IReadOnlyCollection<string> paths);

    /// <summary>
    /// Removes the Jellyfin entry for an item WITHOUT deleting files, and only when none of <paramref name="paths"/>
    /// exists any more. Otherwise asks Jellyfin to refresh the library and reports <see cref="JellyfinCleanup.Pending"/>.
    /// Never throws.
    /// </summary>
    CleanupResult CleanUpAfterRemoval(Guid itemId, IReadOnlyCollection<string> paths);
}

/// <summary>Base for failures talking to Sonarr/Radarr/Jellyseerr (message is safe to show: never contains a key).</summary>
public class ExternalServiceException : Exception
{
    public ExternalServiceException()
    {
    }

    public ExternalServiceException(string message)
        : base(message)
    {
    }

    public ExternalServiceException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The Sonarr/Radarr/Jellyseerr side of removal. A read-only backend (notify-only, dry run, building a vote) can only
/// send GET requests; any other request throws before it leaves the server.
/// </summary>
public interface IRemovalBackend
{
    bool ReadOnly { get; }

    /// <summary>Lists every movie/series of every enabled Radarr/Sonarr instance (GET only). Never throws: unreachable instances are recorded in the result.</summary>
    Task<ArrLibrary> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Asks the instance for this entry again (GET by id). Null when it no longer exists. Throws <see cref="ExternalServiceException"/> when the instance cannot be asked.</summary>
    Task<ArrEntry?> RefetchAsync(ArrEntry entry, CancellationToken cancellationToken);

    /// <summary>Deletes the entry in its instance with deleteFiles=true (and the configured exclusion). Throws <see cref="ExternalServiceException"/> with the service's message.</summary>
    Task DeleteAsync(ArrEntry entry, CancellationToken cancellationToken);

    /// <summary>True when a Jellyseerr instance is configured and its action is not "Do nothing".</summary>
    bool HasSeerrStep { get; }

    /// <summary>Plans (GET only) or runs the Jellyseerr step on every Jellyseerr instance. Never throws: failures are returned as Failed steps.</summary>
    Task<List<ExternalStepResult>> SeerrAsync(MatchKey key, bool execute, CancellationToken cancellationToken);
}

/// <summary>Drive usage, cached for about a minute. Never throws: an unreadable drive is reported as not <see cref="DriveStat.Known"/>.</summary>
public interface IDriveStats
{
    IReadOnlyList<DriveStat> GetDrives();
}

/// <summary>Audit log sink. Every vote and every deletion action is written here.</summary>
public interface IAuditLog
{
    void Write(string action, string actor, Guid? sessionId, string details);
}

/// <summary>Clock abstraction so deadline logic is testable.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>System clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>User lookups needed by the vote service.</summary>
public interface IUserDirectory
{
    /// <summary>True if the user exists and is not disabled.</summary>
    bool IsActive(Guid userId);

    bool IsAdministrator(Guid userId);

    string GetUserName(Guid userId);

    /// <summary>Ids of all active (non-disabled) users.</summary>
    IReadOnlyList<Guid> GetActiveUserIds();
}

/// <summary>Outgoing notifications (webhooks). Implementations must never throw.</summary>
public interface INotifier
{
    Task VoteOpenedAsync(VoteSession session, CancellationToken cancellationToken);

    /// <param name="nonVoters">Eligible users who have not voted yet (for the "who has not voted" reminder).</param>
    Task ClosingSoonAsync(VoteSession session, IReadOnlyList<string> nonVoters, CancellationToken cancellationToken);

    Task ResultsAsync(VoteSession session, CancellationToken cancellationToken);

    /// <summary>A "Delete via Sonarr/Radarr" vote prepared its list and waits for the admin.</summary>
    Task ApprovalNeededAsync(VoteSession session, CancellationToken cancellationToken);

    /// <summary>A recurring vote template created a draft that waits for the admin to review and open it.</summary>
    Task DraftReadyAsync(string templateName, CancellationToken cancellationToken);
}

/// <summary>Persistence for vote sessions.</summary>
public interface IVoteStore
{
    IReadOnlyList<VoteSession> GetAll();

    VoteSession? Get(Guid id);

    /// <summary>Runs <paramref name="mutate"/> under the store lock on the latest copy, then persists atomically.</summary>
    T Update<T>(Guid id, Func<VoteSession, T> mutate);

    void Add(VoteSession session);
}
