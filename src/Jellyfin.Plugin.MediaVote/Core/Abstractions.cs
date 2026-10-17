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

    /// <summary>Paths Jellyfin will delete (movie folder/file, series folder).</summary>
    public List<string> DeletePaths { get; set; } = new();

    public long SizeBytes { get; set; }

    public Dictionary<string, string> ProviderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsProtected { get; set; }

    public string? ProviderId(string name)
        => ProviderIds.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}

/// <summary>Result of a write-permission check on the paths that would be deleted.</summary>
public sealed record PermissionCheck(bool Ok, string? Problem = null, string? Path = null)
{
    public static PermissionCheck Allowed { get; } = new(true);
}

/// <summary>Access to Jellyfin's library for resolution. Implemented with ILibraryManager.</summary>
public interface IMediaGateway
{
    /// <summary>Returns the live item or null if it no longer exists.</summary>
    MediaItemSnapshot? GetItem(Guid itemId);

    /// <summary>Checks that every path that would be deleted is writable by the Jellyfin process.</summary>
    PermissionCheck CheckDeletePermission(MediaItemSnapshot item);

    /// <summary>Deletes the item from the library and its files from disk. Throws on failure.</summary>
    void Delete(Guid itemId);
}

/// <summary>A Radarr, Sonarr or Jellyseerr integration.</summary>
public interface IExternalService
{
    string Name { get; }

    /// <summary>True when a base URL and API key are configured.</summary>
    bool HasConnection { get; }

    /// <summary>Human-readable action the admin selected (e.g. "Unmonitor").</summary>
    string ActionName { get; }

    /// <summary>True when the admin chose "Do nothing" for this service.</summary>
    bool IsNoOp { get; }

    bool AppliesTo(MediaKind kind);

    /// <summary>Looks the item up and describes what would be done. MUST only use read-only (GET) calls.</summary>
    Task<ExternalStepResult> PlanAsync(MediaItemSnapshot item, CancellationToken cancellationToken);

    /// <summary>Performs the configured, state-changing action.</summary>
    Task<ExternalStepResult> ExecuteAsync(MediaItemSnapshot item, CancellationToken cancellationToken);
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

    Task ClosingSoonAsync(VoteSession session, CancellationToken cancellationToken);

    Task ResultsAsync(VoteSession session, CancellationToken cancellationToken);
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
