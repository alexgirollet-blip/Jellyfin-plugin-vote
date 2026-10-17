using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaVote.Core;
using Jellyfin.Plugin.MediaVote.Storage;

namespace Jellyfin.Plugin.MediaVote.Tests;

public sealed class FakeClock : IClock
{
    public FakeClock(DateTime now) => UtcNow = now;

    public DateTime UtcNow { get; set; }
}

public sealed class FakeUsers : IUserDirectory
{
    public Dictionary<Guid, (string Name, bool Active, bool Admin)> Users { get; } = new();

    public Guid Add(string name, bool active = true, bool admin = false)
    {
        var id = Guid.NewGuid();
        Users[id] = (name, active, admin);
        return id;
    }

    public bool IsActive(Guid userId) => Users.TryGetValue(userId, out var u) && u.Active;

    public bool IsAdministrator(Guid userId) => Users.TryGetValue(userId, out var u) && u.Admin;

    public string GetUserName(Guid userId) => Users.TryGetValue(userId, out var u) ? u.Name : "unknown";

    public IReadOnlyList<Guid> GetActiveUserIds() => Users.Where(u => u.Value.Active).Select(u => u.Key).ToList();
}

public sealed class FakeAudit : IAuditLog
{
    public List<(string Action, string Actor, string Details)> Entries { get; } = new();

    public void Write(string action, string actor, Guid? sessionId, string details) => Entries.Add((action, actor, details));
}

public sealed class FakeNotifier : INotifier
{
    public List<string> Events { get; } = new();

    public Task VoteOpenedAsync(VoteSession session, CancellationToken cancellationToken)
    {
        Events.Add("opened");
        return Task.CompletedTask;
    }

    public List<string> LastNonVoters { get; } = new();

    public Task ClosingSoonAsync(VoteSession session, IReadOnlyList<string> nonVoters, CancellationToken cancellationToken)
    {
        Events.Add("closing");
        LastNonVoters.Clear();
        LastNonVoters.AddRange(nonVoters);
        return Task.CompletedTask;
    }

    public Task ApprovalNeededAsync(VoteSession session, CancellationToken cancellationToken)
    {
        Events.Add("approval");
        return Task.CompletedTask;
    }

    public Task DraftReadyAsync(string templateName, CancellationToken cancellationToken)
    {
        Events.Add("draft:" + templateName);
        return Task.CompletedTask;
    }

    public Task ResultsAsync(VoteSession session, CancellationToken cancellationToken)
    {
        Events.Add("results");
        return Task.CompletedTask;
    }
}

/// <summary>Records every call in order so tests can assert the order of operations.</summary>
public sealed class CallLog
{
    public List<string> Calls { get; } = new();
}

public sealed class FakeMedia : IMediaGateway
{
    private static int _nextId = 1000;
    private readonly CallLog _log;

    public FakeMedia(CallLog log) => _log = log;

    public Dictionary<Guid, MediaItemSnapshot> Items { get; } = new();

    /// <summary>Paths that "exist on disk". Media Vote may only check them, never change them.</summary>
    public HashSet<string> OnDisk { get; } = new(StringComparer.Ordinal);

    /// <summary>Items whose stale Jellyfin entry was dropped (never deleting files).</summary>
    public List<Guid> Forgotten { get; } = new();

    public MediaItemSnapshot Add(Guid id, string name, MediaKind kind = MediaKind.Movie, long size = 1000, bool isProtected = false)
    {
        int n = Interlocked.Increment(ref _nextId);
        var s = new MediaItemSnapshot
        {
            ItemId = id,
            Name = name,
            Kind = kind,
            SizeBytes = size,
            IsProtected = isProtected,
            DeletePaths = new List<string> { "/media/" + name },
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Tmdb"] = n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Tvdb"] = (n + 500000).ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
        Items[id] = s;
        OnDisk.Add("/media/" + name);
        return s;
    }

    public MediaItemSnapshot? GetItem(Guid itemId) => Items.TryGetValue(itemId, out var i) ? i : null;

    public bool AnyPathExists(IReadOnlyCollection<string> paths) => paths.Any(OnDisk.Contains);

    public CleanupResult CleanUpAfterRemoval(Guid itemId, IReadOnlyCollection<string> paths)
    {
        _log.Calls.Add("cleanup:" + (Items.TryGetValue(itemId, out var i) ? i.Name : itemId.ToString()));
        if (!Items.TryGetValue(itemId, out var item))
        {
            return new CleanupResult(JellyfinCleanup.Done, "gone");
        }

        if (AnyPathExists(paths.Concat(item.DeletePaths).ToList()))
        {
            return new CleanupResult(JellyfinCleanup.Pending, "files still on disk");
        }

        Items.Remove(itemId);
        Forgotten.Add(itemId);
        return new CleanupResult(JellyfinCleanup.Done, "forgotten");
    }
}

/// <summary>
/// In-memory Sonarr/Radarr/Jellyseerr. Records every call; a read-only instance throws on delete like the real one.
/// </summary>
public sealed class FakeArr : IRemovalBackend
{
    private readonly CallLog _log;
    private readonly FakeMedia? _media;

    public FakeArr(CallLog log, FakeMedia? media = null, bool readOnly = false)
    {
        _log = log;
        _media = media;
        ReadOnly = readOnly;
    }

    public bool ReadOnly { get; }

    public List<ArrEntry> Entries { get; } = new();

    public Dictionary<ArrKind, int> Configured { get; } = new() { [ArrKind.Radarr] = 1, [ArrKind.Sonarr] = 1 };

    public HashSet<string> UnreachableInstances { get; } = new();

    /// <summary>Entry ids whose DELETE fails with this message (entry stays).</summary>
    public Dictionary<int, string> DeleteRefuses { get; } = new();

    /// <summary>Entry ids whose DELETE "succeeds" but the entry stays listed.</summary>
    public HashSet<int> DeleteIgnored { get; } = new();

    /// <summary>When true, a delete also removes the files (as if Sonarr/Radarr's background delete finished at once).</summary>
    public bool DeletesFilesAtOnce { get; set; } = true;

    public bool HasSeerrStep { get; set; }

    public HashSet<string> SeerrFailsFor { get; } = new();

    public List<int> DeletedIds { get; } = new();

    public int DeleteCalls { get; private set; }

    /// <summary>Adds an entry that matches a FakeMedia item by its TMDb/TVDB id.</summary>
    public ArrEntry AddFor(MediaItemSnapshot item, string? instance = null, int? id = null)
    {
        var kind = item.Kind == MediaKind.Series ? ArrKind.Sonarr : ArrKind.Radarr;
        var e = new ArrEntry
        {
            Instance = instance ?? kind.ToString(),
            Kind = kind,
            Id = id ?? (Entries.Count + 1),
            Title = item.Name,
            Path = item.DeletePaths.FirstOrDefault(),
            TmdbId = int.Parse(item.ProviderIds["Tmdb"], System.Globalization.CultureInfo.InvariantCulture),
            TvdbId = kind == ArrKind.Sonarr ? int.Parse(item.ProviderIds["Tvdb"], System.Globalization.CultureInfo.InvariantCulture) : null,
            SizeOnDisk = item.SizeBytes
        };
        Entries.Add(e);
        return e;
    }

    public Task<ArrLibrary> LoadAsync(CancellationToken cancellationToken)
    {
        _log.Calls.Add("arr:list");
        var unreachable = new Dictionary<ArrKind, List<string>>();
        foreach (var name in UnreachableInstances)
        {
            var kind = name.StartsWith("Sonarr", StringComparison.Ordinal) ? ArrKind.Sonarr : ArrKind.Radarr;
            if (!unreachable.TryGetValue(kind, out var l))
            {
                l = new List<string>();
                unreachable[kind] = l;
            }

            l.Add(name);
        }

        var reachable = Entries.Where(e => !UnreachableInstances.Contains(e.Instance)).Select(Copy).ToList();
        return Task.FromResult(new ArrLibrary(reachable, Configured, unreachable));
    }

    public Task<ArrEntry?> RefetchAsync(ArrEntry entry, CancellationToken cancellationToken)
    {
        _log.Calls.Add("arr:get:" + entry.Title);
        if (UnreachableInstances.Contains(entry.Instance))
        {
            throw new ExternalServiceException("Could not reach " + entry.Instance + ": connection refused");
        }

        var e = Entries.FirstOrDefault(x => x.Instance == entry.Instance && x.Id == entry.Id);
        return Task.FromResult(e is null ? null : Copy(e));
    }

    public Task DeleteAsync(ArrEntry entry, CancellationToken cancellationToken)
    {
        if (ReadOnly)
        {
            throw new InvalidOperationException("read-only backend: delete blocked");
        }

        DeleteCalls++;
        _log.Calls.Add("arr:delete:" + entry.Title);
        if (DeleteRefuses.TryGetValue(entry.Id, out var msg))
        {
            throw new ExternalServiceException(msg);
        }

        if (!DeleteIgnored.Contains(entry.Id))
        {
            var e = Entries.First(x => x.Instance == entry.Instance && x.Id == entry.Id);
            Entries.Remove(e);
            DeletedIds.Add(entry.Id);
            if (DeletesFilesAtOnce && e.Path is not null)
            {
                _media?.OnDisk.Remove(e.Path);
            }
        }

        return Task.CompletedTask;
    }

    public Task<List<ExternalStepResult>> SeerrAsync(MatchKey key, bool execute, CancellationToken cancellationToken)
    {
        _log.Calls.Add((execute && !ReadOnly ? "seerr:exec:" : "seerr:plan:") + key.Name);
        var r = new ExternalStepResult { Service = "Jellyseerr", Action = "Remove media/request record" };
        if (SeerrFailsFor.Contains(key.Name))
        {
            r.Status = ExternalStepStatus.Failed;
            r.Message = "Jellyseerr is down";
        }
        else
        {
            r.Status = execute && !ReadOnly ? ExternalStepStatus.Done : ExternalStepStatus.WouldDo;
        }

        return Task.FromResult(new List<ExternalStepResult> { r });
    }

    /// <summary>The same state seen through a read-only backend (what notify-only and dry runs get).</summary>
    public IRemovalBackend View(bool readOnly) => readOnly ? new ReadOnlyView(this) : this;

    private static ArrEntry Copy(ArrEntry e) => new()
    {
        Instance = e.Instance, Kind = e.Kind, Id = e.Id, Title = e.Title, Year = e.Year, Path = e.Path,
        TmdbId = e.TmdbId, TvdbId = e.TvdbId, ImdbId = e.ImdbId, SizeOnDisk = e.SizeOnDisk
    };

    private sealed class ReadOnlyView : IRemovalBackend
    {
        private readonly FakeArr _inner;

        public ReadOnlyView(FakeArr inner) => _inner = inner;

        public bool ReadOnly => true;

        public bool HasSeerrStep => _inner.HasSeerrStep;

        public Task<ArrLibrary> LoadAsync(CancellationToken cancellationToken) => _inner.LoadAsync(cancellationToken);

        public Task<ArrEntry?> RefetchAsync(ArrEntry entry, CancellationToken cancellationToken) => _inner.RefetchAsync(entry, cancellationToken);

        public Task DeleteAsync(ArrEntry entry, CancellationToken cancellationToken)
        {
            _inner._log.Calls.Add("BLOCKED-delete:" + entry.Title);
            throw new InvalidOperationException("read-only backend: delete blocked");
        }

        public Task<List<ExternalStepResult>> SeerrAsync(MatchKey key, bool execute, CancellationToken cancellationToken)
        {
            _inner._log.Calls.Add("seerr:plan:" + key.Name);
            return Task.FromResult(new List<ExternalStepResult> { new() { Service = "Jellyseerr", Action = "Remove media/request record", Status = ExternalStepStatus.WouldDo } });
        }
    }
}

/// <summary>Programmable HTTP handler for the Radarr/Sonarr/Jellyseerr tests.</summary>
public sealed class MockHttp : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, string? Body, string? ApiKey)> Requests { get; } = new();

    public Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> Responder { get; set; } = _ => (HttpStatusCode.NotFound, string.Empty);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        request.Headers.TryGetValues("X-Api-Key", out var keys);
        Requests.Add((request.Method, request.RequestUri!.ToString(), body, keys?.FirstOrDefault()));
        var (status, text) = Responder(request);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mediavote-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

public static class Build
{
    public static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    public static VoteSession Session(int items = 5, int votesPerUser = 3, bool multiple = false, bool keep = false, int winners = 1)
    {
        var s = new VoteSession
        {
            CreatedUtc = Now,
            Settings = new SessionSettings
            {
                Title = "Test",
                VotesPerUser = votesPerUser,
                AllowMultipleVotesPerItem = multiple,
                AllowKeepVotes = keep,
                KeepVotesPerUser = 1,
                DeadlineUtc = Now.AddDays(1),
                WinnerCount = winners
            }
        };
        for (int i = 0; i < items; i++)
        {
            s.Candidates.Add(new CandidateItem { ItemId = Guid.NewGuid(), Name = "Item" + i, Kind = i % 2 == 0 ? MediaKind.Movie : MediaKind.Series, SizeBytes = (i + 1) * 1000 });
        }

        return s;
    }

    public static void AddVotes(VoteSession s, Guid item, int count, VoteKind kind = VoteKind.Remove)
    {
        for (int i = 0; i < count; i++)
        {
            s.Votes.Add(new VoteRecord { UserId = Guid.NewGuid(), UserName = "u" + i, ItemId = item, Kind = kind, CastUtc = Now });
        }
    }

    public static JsonVoteStore Store(TempDir dir) => new(dir.Path);
}
