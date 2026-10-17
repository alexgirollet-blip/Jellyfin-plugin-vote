using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaVote.Configuration;
using Jellyfin.Plugin.MediaVote.Core;
using Jellyfin.Plugin.MediaVote.Integrations.Arr;
using Xunit;

namespace Jellyfin.Plugin.MediaVote.Tests;

/// <summary>
/// The real HTTP clients against mocked Sonarr/Radarr/Jellyseerr servers (several instances, routed by host).
/// Request shapes follow the Sonarr v3/v4 and Radarr v5 source.
/// </summary>
public class ArrServiceTests
{
    private const string Key = "secret-api-key-123456";
    private readonly MockHttp _http = new();

    // host -> (entries json by id). Mutable so DELETE can remove an entry.
    private readonly Dictionary<string, Dictionary<int, string>> _servers = new();
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _deleteFailures = new();
    private readonly HashSet<string> _down = new();

    public ArrServiceTests()
    {
        _servers["radarr"] = new() { [42] = "{\"id\":42,\"title\":\"The Matrix\",\"year\":1999,\"tmdbId\":603,\"imdbId\":\"tt0133093\",\"path\":\"/movies/The Matrix (1999)\",\"sizeOnDisk\":8000000000}" };
        _servers["sonarr"] = new() { [7] = "{\"id\":7,\"title\":\"Breaking Bad\",\"year\":2008,\"tvdbId\":81189,\"tmdbId\":1396,\"path\":\"/tv/Breaking Bad\",\"statistics\":{\"sizeOnDisk\":50000000000}}" };
        _servers["anime"] = new() { [3] = "{\"id\":3,\"title\":\"Cowboy Bebop\",\"year\":1998,\"tvdbId\":76885,\"path\":\"/anime/Cowboy Bebop\",\"statistics\":{\"sizeOnDisk\":20000000000}}" };
        _http.Responder = Respond;
    }

    private (HttpStatusCode, string) Respond(HttpRequestMessage req)
    {
        var host = req.RequestUri!.Host;
        if (_down.Contains(host))
        {
            throw new HttpRequestException("Connection refused");
        }

        var path = req.RequestUri.AbsolutePath;
        if (host == "seerr")
        {
            if (path == "/api/v1/movie/603")
            {
                return (HttpStatusCode.OK, "{\"title\":\"The Matrix\",\"mediaInfo\":{\"id\":9}}");
            }

            return req.Method == HttpMethod.Delete ? (HttpStatusCode.NoContent, string.Empty) : (HttpStatusCode.NotFound, string.Empty);
        }

        if (!_servers.TryGetValue(host, out var db))
        {
            return (HttpStatusCode.NotFound, string.Empty);
        }

        string resource = host == "radarr" ? "movie" : "series";
        if (path == "/api/v3/system/status")
        {
            return (HttpStatusCode.OK, "{\"appName\":\"" + (host == "radarr" ? "Radarr" : "Sonarr") + "\",\"version\":\"5.14.0\"}");
        }

        if (path == "/api/v3/" + resource && req.Method == HttpMethod.Get)
        {
            return (HttpStatusCode.OK, "[" + string.Join(",", db.Values) + "]");
        }

        if (path.StartsWith("/api/v3/" + resource + "/", StringComparison.Ordinal))
        {
            int id = int.Parse(path.Split('/').Last(), System.Globalization.CultureInfo.InvariantCulture);
            if (req.Method == HttpMethod.Get)
            {
                return db.TryGetValue(id, out var json) ? (HttpStatusCode.OK, json) : (HttpStatusCode.NotFound, string.Empty);
            }

            if (req.Method == HttpMethod.Delete)
            {
                if (_deleteFailures.TryGetValue(host, out var f))
                {
                    return f;
                }

                // Sonarr v4 answers 500 for an id that does not exist (repository Get throws); Radarr 404.
                if (!db.Remove(id))
                {
                    return host == "radarr" ? (HttpStatusCode.NotFound, "{\"message\":\"Movie with ID " + id + " does not exist\"}") : (HttpStatusCode.InternalServerError, "{\"message\":\"Expected query to return 1 rows but returned 0\"}");
                }

                return (HttpStatusCode.OK, string.Empty);
            }
        }

        if (path == "/api/v3/rootfolder")
        {
            return (HttpStatusCode.OK, "[{\"path\":\"/tv/\",\"freeSpace\":1000000000000,\"accessible\":true}]");
        }

        if (path == "/api/v3/diskspace")
        {
            return (HttpStatusCode.OK, "[{\"path\":\"/tv\",\"freeSpace\":1000000000000,\"totalSpace\":8000000000000}]");
        }

        return (HttpStatusCode.NotFound, string.Empty);
    }

    private ArrHttp Http(string host, bool readOnly = false) => new(new HttpClient(_http), host, "http://" + host + ":1234/", Key, readOnly);

    private ArrRemovalBackend Backend(bool readOnly = false, bool seerr = false, bool exclusion = true)
    {
        var arr = new List<ArrClient>
        {
            new(Http("radarr", readOnly), ArrKind.Radarr, "Radarr", exclusion),
            new(Http("sonarr", readOnly), ArrKind.Sonarr, "Sonarr", exclusion),
            new(Http("anime", readOnly), ArrKind.Sonarr, "Sonarr Anime", exclusion)
        };
        var seer = seerr ? new List<JellyseerrClient> { new(Http("seerr", readOnly), "Jellyseerr", SeerrAction.RemoveMediaRecord) } : new List<JellyseerrClient>();
        return new ArrRemovalBackend(arr, seer, readOnly);
    }

    private static MatchKey Movie(string tmdb = "603") => new(MediaKind.Movie, new Dictionary<string, string> { ["Tmdb"] = tmdb }, "The Matrix", 1999, "/LM1/Movies/The Matrix (1999)");

    private static MatchKey Series(string tvdb) => new(MediaKind.Series, new Dictionary<string, string> { ["Tvdb"] = tvdb }, "x", null, null);

    [Fact]
    public async Task Load_ReadsEveryInstance_WithGetOnly_AndTheKeyInTheHeader()
    {
        var lib = await Backend().LoadAsync(CancellationToken.None);
        Assert.Equal(3, lib.Entries.Count);
        Assert.Equal(1, lib.ConfiguredCount(ArrKind.Radarr));
        Assert.Equal(2, lib.ConfiguredCount(ArrKind.Sonarr));
        Assert.All(_http.Requests, q =>
        {
            Assert.Equal(HttpMethod.Get, q.Method);
            Assert.Equal(Key, q.ApiKey);
            Assert.DoesNotContain(Key, q.Url, StringComparison.Ordinal);
        });
        Assert.Equal(50000000000, lib.Entries.First(e => e.Title == "Breaking Bad").SizeOnDisk); // statistics.sizeOnDisk
        Assert.Equal(8000000000, lib.Entries.First(e => e.Title == "The Matrix").SizeOnDisk); // top-level sizeOnDisk
    }

    [Fact]
    public async Task Matching_UsesProviderIds_AcrossInstances()
    {
        var lib = await Backend().LoadAsync(CancellationToken.None);
        Assert.Equal("Radarr", ArrMatcher.Match(Movie(), lib).Entry!.Instance);
        Assert.Equal("Sonarr", ArrMatcher.Match(Series("81189"), lib).Entry!.Instance);
        Assert.Equal("Sonarr Anime", ArrMatcher.Match(Series("76885"), lib).Entry!.Instance);
        Assert.Equal(ArrMatchState.NotManaged, ArrMatcher.Match(Series("1"), lib).State);
    }

    [Fact]
    public async Task OneInstanceDown_MakesItsKindUnknown_NotNotManaged()
    {
        _down.Add("anime");
        var lib = await Backend().LoadAsync(CancellationToken.None);
        Assert.Equal(ArrMatchState.Unreachable, ArrMatcher.Match(Series("81189"), lib).State);
        Assert.Equal(ArrMatchState.Matched, ArrMatcher.Match(Movie(), lib).State); // Radarr unaffected
        Assert.Contains(lib.Errors, e => e.StartsWith("Sonarr Anime: Could not reach", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_UsesTheExactParameterNames_PerApp()
    {
        var b = Backend(exclusion: true);
        var lib = await b.LoadAsync(CancellationToken.None);
        await b.DeleteAsync(ArrMatcher.Match(Movie(), lib).Entry!, CancellationToken.None);
        await b.DeleteAsync(ArrMatcher.Match(Series("81189"), lib).Entry!, CancellationToken.None);

        var deletes = _http.Requests.Where(r => r.Method == HttpMethod.Delete).Select(r => r.Url).ToList();
        Assert.Equal("http://radarr:1234/api/v3/movie/42?deleteFiles=true&addImportExclusion=true", deletes[0]);
        Assert.Equal("http://sonarr:1234/api/v3/series/7?deleteFiles=true&addImportListExclusion=true", deletes[1]);
    }

    [Fact]
    public async Task Delete_ExclusionOff_IsSentAsFalse()
    {
        var b = Backend(exclusion: false);
        var lib = await b.LoadAsync(CancellationToken.None);
        await b.DeleteAsync(ArrMatcher.Match(Movie(), lib).Entry!, CancellationToken.None);
        Assert.EndsWith("addImportExclusion=false", _http.Requests.Single(r => r.Method == HttpMethod.Delete).Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnlyBackend_CannotSendAnythingButGet()
    {
        var b = Backend(readOnly: true, seerr: true);
        var lib = await b.LoadAsync(CancellationToken.None);
        var entry = ArrMatcher.Match(Movie(), lib).Entry!;
        await Assert.ThrowsAsync<ReadOnlyViolationException>(() => b.DeleteAsync(entry, CancellationToken.None));
        var steps = await b.SeerrAsync(Movie(), execute: true, CancellationToken.None); // execute is ignored when read-only
        Assert.Equal(ExternalStepStatus.WouldDo, Assert.Single(steps).Status);

        // Even a client built by hand refuses a DELETE before it leaves the server.
        await Assert.ThrowsAsync<ReadOnlyViolationException>(() => Http("radarr", readOnly: true).DeleteAsync("/api/v3/movie/42", CancellationToken.None));
        Assert.All(_http.Requests, q => Assert.Equal(HttpMethod.Get, q.Method));
    }

    [Fact]
    public async Task EndToEnd_DeleteThenVerify_WithTheRealClients()
    {
        var media = new FakeMedia(new CallLog());
        var id = Guid.NewGuid();
        var snap = media.Add(id, "The Matrix");
        snap.ProviderIds["Tmdb"] = "603";
        media.OnDisk.Clear(); // files already gone (e.g. moved to Radarr's recycle bin)
        var winners = new List<ItemTally> { new() { ItemId = id, RemoveVotes = 1 } };
        var cands = new Dictionary<Guid, CandidateItem> { [id] = new() { ItemId = id, Name = "The Matrix", Kind = MediaKind.Movie, ProviderIds = new(snap.ProviderIds) } };

        var o = Assert.Single(await new ResolutionExecutor(media, Backend(seerr: true), new FakeAudit(), new FakeClock(Build.Now))
            .RunAsync(Guid.NewGuid(), winners, cands, ExecutionMode.Remove, ExecutionLimits.None));

        Assert.Equal(ItemOutcomeStatus.Removed, o.Status);
        Assert.Equal(8000000000, o.ArrSizeOnDisk);
        Assert.False(_servers["radarr"].ContainsKey(42));
        var calls = _http.Requests.Select(r => r.Method + " " + new Uri(r.Url).Host + new Uri(r.Url).AbsolutePath).ToList();
        int seerrDelete = calls.IndexOf("DELETE seerr/api/v1/media/9");
        int radarrDelete = calls.IndexOf("DELETE radarr/api/v3/movie/42");
        Assert.True(seerrDelete >= 0 && seerrDelete < radarrDelete, "Jellyseerr runs before the Radarr delete");
        Assert.Equal("GET radarr/api/v3/movie/42", calls[radarrDelete + 1]); // verification
        Assert.Contains(id, media.Forgotten);
    }

    [Fact]
    public async Task DeleteRefused_ShowsTheirMessageVerbatim()
    {
        _deleteFailures["radarr"] = (HttpStatusCode.BadRequest, "[{\"propertyName\":\"Path\",\"errorMessage\":\"Folder is in use by another movie\"}]");
        var b = Backend();
        var lib = await b.LoadAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<ArrException>(() => b.DeleteAsync(ArrMatcher.Match(Movie(), lib).Entry!, CancellationToken.None));
        Assert.Equal("Radarr answered HTTP 400: Folder is in use by another movie", ex.Message.Replace("radarr", "Radarr", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractMessage_HandlesArrErrorShapes()
    {
        Assert.Equal("Series with ID 5 does not exist", ArrHttp.ExtractMessage("{\"message\":\"Series with ID 5 does not exist\"}"));
        Assert.Equal("Boom — stack", ArrHttp.ExtractMessage("{\"message\":\"Boom\",\"description\":\"stack\"}"));
        Assert.Equal("A — B", ArrHttp.ExtractMessage("[{\"errorMessage\":\"A\"},{\"errorMessage\":\"B\"}]"));
        Assert.Equal("Bad Gateway", ArrHttp.ExtractMessage("Bad Gateway"));
        Assert.Equal(string.Empty, ArrHttp.ExtractMessage(null));
    }

    [Fact]
    public async Task Refetch_404_MeansGone()
    {
        var b = Backend();
        var gone = new ArrEntry { Instance = "Radarr", Kind = ArrKind.Radarr, Id = 999, Title = "x" };
        Assert.Null(await b.RefetchAsync(gone, CancellationToken.None));
    }

    [Fact]
    public async Task ConnectionTest_ChecksTheAppName()
    {
        Assert.Equal("Radarr 5.14.0", await new ArrClient(Http("radarr"), ArrKind.Radarr, "Radarr", true).TestAsync(CancellationToken.None));
        var ex = await Assert.ThrowsAsync<ArrException>(() => new ArrClient(Http("sonarr"), ArrKind.Radarr, "Radarr", true).TestAsync(CancellationToken.None));
        Assert.Contains("not Radarr", ex.Message);
    }

    [Fact]
    public async Task RootFolders_TakeTotalSizeFromDiskspace()
    {
        var roots = await new ArrClient(Http("sonarr"), ArrKind.Sonarr, "Sonarr", true).RootFoldersAsync(CancellationToken.None);
        var r = Assert.Single(roots);
        Assert.Equal(1000000000000, r.FreeSpace);
        Assert.Equal(8000000000000, r.TotalSpace);
    }

    [Fact]
    public async Task Jellyseerr_PlanIsReadOnly_AndMissingTmdbIsNotAFailure()
    {
        var client = new JellyseerrClient(Http("seerr"), "Jellyseerr", SeerrAction.RemoveMediaRecord);
        var plan = await client.RunAsync(Movie(), execute: false, CancellationToken.None);
        Assert.Equal(ExternalStepStatus.WouldDo, plan.Status);
        Assert.Equal("9", plan.ExternalId);
        var noIds = await client.RunAsync(new MatchKey(MediaKind.Movie, new Dictionary<string, string>(), "x", null, null), execute: true, CancellationToken.None);
        Assert.Equal(ExternalStepStatus.NotFound, noIds.Status);
        Assert.All(_http.Requests, q => Assert.Equal(HttpMethod.Get, q.Method));
    }

    [Fact]
    public void NormalizeBaseUrl_RejectsNonHttp()
    {
        Assert.Equal("http://x:1", ArrHttp.NormalizeBaseUrl(" http://x:1/ "));
        Assert.Null(ArrHttp.NormalizeBaseUrl("ftp://x"));
        Assert.Null(ArrHttp.NormalizeBaseUrl("not a url"));
    }
}
