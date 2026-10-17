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

    public Task ClosingSoonAsync(VoteSession session, CancellationToken cancellationToken)
    {
        Events.Add("closing");
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
    private readonly CallLog _log;

    public FakeMedia(CallLog log) => _log = log;

    public Dictionary<Guid, MediaItemSnapshot> Items { get; } = new();

    public HashSet<Guid> NotWritable { get; } = new();

    public HashSet<Guid> DeleteThrows { get; } = new();

    public List<Guid> Deleted { get; } = new();

    public MediaItemSnapshot Add(Guid id, string name, MediaKind kind = MediaKind.Movie, long size = 1000, bool isProtected = false)
    {
        var s = new MediaItemSnapshot
        {
            ItemId = id,
            Name = name,
            Kind = kind,
            SizeBytes = size,
            IsProtected = isProtected,
            DeletePaths = new List<string> { "/media/" + name },
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tmdb"] = "1", ["Tvdb"] = "2" }
        };
        Items[id] = s;
        return s;
    }

    public MediaItemSnapshot? GetItem(Guid itemId) => Items.TryGetValue(itemId, out var i) ? i : null;

    public PermissionCheck CheckDeletePermission(MediaItemSnapshot item)
        => NotWritable.Contains(item.ItemId) ? new PermissionCheck(false, "permission denied") : PermissionCheck.Allowed;

    public void Delete(Guid itemId)
    {
        _log.Calls.Add("delete:" + Items[itemId].Name);
        if (DeleteThrows.Contains(itemId))
        {
            throw new IOException("disk error");
        }

        Deleted.Add(itemId);
        Items.Remove(itemId);
    }
}

public sealed class FakeService : IExternalService
{
    private readonly CallLog _log;

    public FakeService(CallLog log, string name, Func<MediaKind, bool> applies, bool hasConnection = true, bool noOp = false)
    {
        _log = log;
        Name = name;
        _applies = applies;
        HasConnection = hasConnection;
        IsNoOp = noOp;
    }

    private readonly Func<MediaKind, bool> _applies;

    public string Name { get; }

    public bool HasConnection { get; }

    public string ActionName => "Unmonitor";

    public bool IsNoOp { get; }

    public HashSet<string> FailFor { get; } = new();

    public int ExecuteCount { get; private set; }

    public int PlanCount { get; private set; }

    public bool AppliesTo(MediaKind kind) => _applies(kind);

    public Task<ExternalStepResult> PlanAsync(MediaItemSnapshot item, CancellationToken cancellationToken)
    {
        PlanCount++;
        _log.Calls.Add($"plan:{Name}:{item.Name}");
        return Task.FromResult(new ExternalStepResult { Service = Name, Action = ActionName, Status = ExternalStepStatus.WouldDo });
    }

    public Task<ExternalStepResult> ExecuteAsync(MediaItemSnapshot item, CancellationToken cancellationToken)
    {
        ExecuteCount++;
        _log.Calls.Add($"exec:{Name}:{item.Name}");
        if (FailFor.Contains(item.Name))
        {
            throw new InvalidOperationException(Name + " is down");
        }

        return Task.FromResult(new ExternalStepResult { Service = Name, Action = ActionName, Status = ExternalStepStatus.Done });
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
