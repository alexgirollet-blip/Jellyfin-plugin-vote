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

public class ArrServiceTests
{
    private const string Key = "secret-api-key-123456";
    private readonly MockHttp _http = new();

    private ArrHttp Http(string name, string url = "http://radarr.local:7878/") => new(new HttpClient(_http), name, url, Key);

    private static MediaItemSnapshot Movie(string? tmdb = "603", string? imdb = "tt0133093")
    {
        var s = new MediaItemSnapshot { ItemId = Guid.NewGuid(), Name = "The Matrix", Kind = MediaKind.Movie };
        if (tmdb is not null)
        {
            s.ProviderIds["Tmdb"] = tmdb;
        }

        if (imdb is not null)
        {
            s.ProviderIds["Imdb"] = imdb;
        }

        return s;
    }

    private static MediaItemSnapshot Series(string? tvdb = "81189", string? tmdb = "1396")
    {
        var s = new MediaItemSnapshot { ItemId = Guid.NewGuid(), Name = "Breaking Bad", Kind = MediaKind.Series };
        if (tvdb is not null)
        {
            s.ProviderIds["Tvdb"] = tvdb;
        }

        if (tmdb is not null)
        {
            s.ProviderIds["Tmdb"] = tmdb;
        }

        return s;
    }

    private void RespondRadarrMovie()
        => _http.Responder = req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/v3/movie"
            ? (HttpStatusCode.OK, "[{\"id\":42,\"title\":\"The Matrix\",\"year\":1999,\"monitored\":true,\"tmdbId\":603,\"imdbId\":\"tt0133093\",\"path\":\"/movies/The Matrix (1999)\"}]")
            : (HttpStatusCode.Accepted, "{}");

    [Fact]
    public async Task Radarr_Plan_UsesOnlyGet_AndDescribesUnmonitor()
    {
        RespondRadarrMovie();
        var svc = new RadarrService(Http("Radarr"), ArrAction.Unmonitor);
        var r = await svc.PlanAsync(Movie(), CancellationToken.None);

        Assert.Equal(ExternalStepStatus.WouldDo, r.Status);
        Assert.Equal("42", r.ExternalId);
        Assert.Contains("unmonitor", r.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(_http.Requests, q => Assert.Equal(HttpMethod.Get, q.Method));
        Assert.Equal("http://radarr.local:7878/api/v3/movie?tmdbId=603", _http.Requests[0].Url);
    }

    [Fact]
    public async Task ApiKey_IsSentInHeader_NeverInUrl()
    {
        RespondRadarrMovie();
        await new RadarrService(Http("Radarr"), ArrAction.Unmonitor).ExecuteAsync(Movie(), CancellationToken.None);
        Assert.All(_http.Requests, q =>
        {
            Assert.Equal(Key, q.ApiKey);
            Assert.DoesNotContain(Key, q.Url, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Radarr_Unmonitor_SendsEditorPut()
    {
        RespondRadarrMovie();
        var r = await new RadarrService(Http("Radarr"), ArrAction.Unmonitor).ExecuteAsync(Movie(), CancellationToken.None);

        Assert.Equal(ExternalStepStatus.Done, r.Status);
        var put = Assert.Single(_http.Requests, q => q.Method == HttpMethod.Put);
        Assert.EndsWith("/api/v3/movie/editor", put.Url, StringComparison.Ordinal);
        Assert.Contains("\"movieIds\":[42]", put.Body, StringComparison.Ordinal);
        Assert.Contains("\"monitored\":false", put.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(_http.Requests, q => q.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Radarr_RemoveAndExclude_DeletesWithoutFiles_AndAddsExclusion()
    {
        RespondRadarrMovie();
        await new RadarrService(Http("Radarr"), ArrAction.RemoveAndExclude).ExecuteAsync(Movie(), CancellationToken.None);
        var del = Assert.Single(_http.Requests, q => q.Method == HttpMethod.Delete);
        Assert.EndsWith("/api/v3/movie/42?deleteFiles=false&addImportExclusion=true", del.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Radarr_FallsBackToImdb_WhenNoTmdb()
    {
        RespondRadarrMovie();
        var r = await new RadarrService(Http("Radarr"), ArrAction.Unmonitor).PlanAsync(Movie(tmdb: null), CancellationToken.None);
        Assert.Equal(ExternalStepStatus.WouldDo, r.Status);
        Assert.Equal("http://radarr.local:7878/api/v3/movie", _http.Requests[0].Url);
    }

    [Fact]
    public async Task Radarr_NoProviderIds_IsAFailure_SoTheItemIsNotDeleted()
    {
        var r = await new RadarrService(Http("Radarr"), ArrAction.Unmonitor).ExecuteAsync(Movie(tmdb: null, imdb: null), CancellationToken.None);
        Assert.Equal(ExternalStepStatus.Failed, r.Status);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Radarr_NotInApp_IsNotFound_NotAFailure()
    {
        _http.Responder = _ => (HttpStatusCode.OK, "[]");
        var r = await new RadarrService(Http("Radarr"), ArrAction.Unmonitor).ExecuteAsync(Movie(), CancellationToken.None);
        Assert.Equal(ExternalStepStatus.NotFound, r.Status);
        Assert.False(r.IsFailure);
        Assert.All(_http.Requests, q => Assert.Equal(HttpMethod.Get, q.Method));
    }

    [Fact]
    public async Task Radarr_Unauthorized_ThrowsHelpfulError_WithoutKey()
    {
        _http.Responder = _ => (HttpStatusCode.Unauthorized, "{\"error\":\"Unauthorized\"}");
        var ex = await Assert.ThrowsAsync<ArrException>(() => new RadarrService(Http("Radarr"), ArrAction.Unmonitor).ExecuteAsync(Movie(), CancellationToken.None));
        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        Assert.Contains("API key", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Radarr_DoNothing_MakesNoStateChangingCalls()
    {
        RespondRadarrMovie();
        var svc = new RadarrService(Http("Radarr"), ArrAction.Nothing);
        Assert.True(svc.IsNoOp);
        var r = await svc.ExecuteAsync(Movie(), CancellationToken.None);
        Assert.Equal(ExternalStepStatus.Skipped, r.Status);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Radarr_Test_ReadsSystemStatus()
    {
        _http.Responder = req => req.RequestUri!.AbsolutePath == "/api/v3/system/status" ? (HttpStatusCode.OK, "{\"appName\":\"Radarr\",\"version\":\"5.14.0\"}") : (HttpStatusCode.NotFound, string.Empty);
        Assert.Equal("Radarr 5.14.0", await new RadarrService(Http("Radarr"), ArrAction.Unmonitor).TestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Sonarr_LooksUpByTvdb_AndUnmonitorsSeries()
    {
        _http.Responder = req => req.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, "[{\"id\":7,\"title\":\"Breaking Bad\",\"year\":2008,\"tvdbId\":81189,\"monitored\":true}]")
            : (HttpStatusCode.Accepted, "{}");
        var r = await new SonarrService(Http("Sonarr", "http://sonarr:8989"), ArrAction.Unmonitor).ExecuteAsync(Series(), CancellationToken.None);

        Assert.Equal(ExternalStepStatus.Done, r.Status);
        Assert.Equal("http://sonarr:8989/api/v3/series?tvdbId=81189", _http.Requests[0].Url);
        var put = Assert.Single(_http.Requests, q => q.Method == HttpMethod.Put);
        Assert.EndsWith("/api/v3/series/editor", put.Url, StringComparison.Ordinal);
        Assert.Contains("\"seriesIds\":[7]", put.Body, StringComparison.Ordinal);
        Assert.Contains("\"monitored\":false", put.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sonarr_RemoveAndExclude_UsesImportListExclusion()
    {
        _http.Responder = req => req.Method == HttpMethod.Get ? (HttpStatusCode.OK, "[{\"id\":7,\"title\":\"Breaking Bad\"}]") : (HttpStatusCode.OK, "{}");
        await new SonarrService(Http("Sonarr", "http://sonarr:8989"), ArrAction.RemoveAndExclude).ExecuteAsync(Series(), CancellationToken.None);
        var del = Assert.Single(_http.Requests, q => q.Method == HttpMethod.Delete);
        Assert.EndsWith("/api/v3/series/7?deleteFiles=false&addImportListExclusion=true", del.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sonarr_FallsBackToTmdbMatch()
    {
        _http.Responder = _ => (HttpStatusCode.OK, "[{\"id\":1,\"title\":\"Other\",\"tmdbId\":5},{\"id\":7,\"title\":\"Breaking Bad\",\"tmdbId\":1396}]");
        var r = await new SonarrService(Http("Sonarr", "http://sonarr:8989"), ArrAction.Unmonitor).PlanAsync(Series(tvdb: null), CancellationToken.None);
        Assert.Equal("7", r.ExternalId);
        Assert.Equal("http://sonarr:8989/api/v3/series", _http.Requests[0].Url);
    }

    [Fact]
    public void Sonarr_AppliesOnlyToSeries_RadarrOnlyToMovies()
    {
        Assert.True(new SonarrService(Http("Sonarr"), ArrAction.Unmonitor).AppliesTo(MediaKind.Series));
        Assert.False(new SonarrService(Http("Sonarr"), ArrAction.Unmonitor).AppliesTo(MediaKind.Movie));
        Assert.True(new RadarrService(Http("Radarr"), ArrAction.Unmonitor).AppliesTo(MediaKind.Movie));
        Assert.False(new RadarrService(Http("Radarr"), ArrAction.Unmonitor).AppliesTo(MediaKind.Series));
    }

    [Fact]
    public async Task Jellyseerr_RemovesMediaRecord()
    {
        _http.Responder = req => req.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, "{\"id\":603,\"title\":\"The Matrix\",\"mediaInfo\":{\"id\":12,\"tmdbId\":603,\"status\":5}}")
            : (HttpStatusCode.NoContent, string.Empty);
        var svc = new JellyseerrService(Http("Jellyseerr", "https://requests.example"), SeerrAction.RemoveMediaRecord);
        var r = await svc.ExecuteAsync(Movie(), CancellationToken.None);

        Assert.Equal(ExternalStepStatus.Done, r.Status);
        Assert.Equal("https://requests.example/api/v1/movie/603", _http.Requests[0].Url);
        Assert.Equal("https://requests.example/api/v1/media/12", Assert.Single(_http.Requests, q => q.Method == HttpMethod.Delete).Url);
    }

    [Fact]
    public async Task Jellyseerr_UsesTvEndpointForSeries_AndPlanIsReadOnly()
    {
        _http.Responder = _ => (HttpStatusCode.OK, "{\"id\":1396,\"name\":\"Breaking Bad\",\"mediaInfo\":{\"id\":3}}");
        var r = await new JellyseerrService(Http("Jellyseerr", "http://seerr:5055"), SeerrAction.RemoveMediaRecord).PlanAsync(Series(), CancellationToken.None);
        Assert.Equal(ExternalStepStatus.WouldDo, r.Status);
        Assert.Equal("http://seerr:5055/api/v1/tv/1396", Assert.Single(_http.Requests).Url);
        Assert.Equal(HttpMethod.Get, _http.Requests[0].Method);
    }

    [Fact]
    public async Task Jellyseerr_NoRecord_IsNotFound()
    {
        _http.Responder = _ => (HttpStatusCode.OK, "{\"id\":603,\"title\":\"The Matrix\"}");
        var r = await new JellyseerrService(Http("Jellyseerr", "http://seerr:5055"), SeerrAction.RemoveMediaRecord).ExecuteAsync(Movie(), CancellationToken.None);
        Assert.Equal(ExternalStepStatus.NotFound, r.Status);
        Assert.DoesNotContain(_http.Requests, q => q.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Jellyseerr_DefaultDoesNothing()
    {
        var svc = new JellyseerrService(Http("Jellyseerr", "http://seerr:5055"), SeerrAction.Nothing);
        Assert.True(svc.IsNoOp);
        Assert.Equal(ExternalStepStatus.Skipped, (await svc.ExecuteAsync(Movie(), CancellationToken.None)).Status);
        Assert.Empty(_http.Requests);
    }

    [Theory]
    [InlineData("http://radarr:7878/", "http://radarr:7878")]
    [InlineData(" https://host/radarr ", "https://host/radarr")]
    [InlineData("ftp://x", null)]
    [InlineData("radarr:7878", null)]
    [InlineData("", null)]
    public void NormalizeBaseUrl(string input, string? expected) => Assert.Equal(expected, ArrHttp.NormalizeBaseUrl(input));

    [Fact]
    public void Unconfigured_HasNoConnection()
        => Assert.False(new RadarrService(new ArrHttp(new HttpClient(_http), "Radarr", string.Empty, string.Empty), ArrAction.Unmonitor).HasConnection);
}
