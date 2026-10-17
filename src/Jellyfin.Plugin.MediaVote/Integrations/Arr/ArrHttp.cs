using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MediaVote.Integrations.Arr;

/// <summary>Raised for any HTTP or protocol failure talking to Radarr/Sonarr/Jellyseerr. Never contains the API key.</summary>
public sealed class ArrException : Exception
{
    public ArrException(string message)
        : base(message)
    {
    }

    public ArrException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public ArrException()
    {
    }
}

/// <summary>
/// Small HTTP helper shared by the three clients. Sends the key in the X-Api-Key header
/// (supported by Radarr v3, Sonarr v3/v4 and Jellyseerr), so it never appears in URLs or logs.
/// </summary>
public sealed class ArrHttp
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _serviceName;

    public ArrHttp(HttpClient http, string serviceName, string baseUrl, string apiKey)
    {
        _http = http;
        _serviceName = serviceName;
        _baseUrl = NormalizeBaseUrl(baseUrl) ?? string.Empty;
        _apiKey = apiKey?.Trim() ?? string.Empty;
    }

    public bool IsConfigured => _baseUrl.Length > 0 && _apiKey.Length > 0;

    /// <summary>Returns a trimmed absolute http(s) URL without trailing slash, or null if invalid.</summary>
    public static string? NormalizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return url;
    }

    /// <summary>GET returning parsed JSON, or null on 404.</summary>
    public async Task<JsonDocument?> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = Build(HttpMethod.Get, pathAndQuery);
        using var resp = await Send(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccess(resp, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new ArrException($"{_serviceName} returned something that is not JSON. Check the base URL.", ex);
        }
    }

    public async Task PutJsonAsync(string pathAndQuery, object body, CancellationToken ct)
    {
        using var req = Build(HttpMethod.Put, pathAndQuery);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Send(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = Build(HttpMethod.Delete, pathAndQuery);
        using var resp = await Send(req, ct).ConfigureAwait(false);
        await EnsureSuccess(resp, ct).ConfigureAwait(false);
    }

    private HttpRequestMessage Build(HttpMethod method, string pathAndQuery)
    {
        if (!IsConfigured)
        {
            throw new ArrException($"{_serviceName} is not configured.");
        }

        var req = new HttpRequestMessage(method, _baseUrl + pathAndQuery);
        req.Headers.Add("X-Api-Key", _apiKey);
        req.Headers.Accept.ParseAdd("application/json");
        return req;
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return await _http.SendAsync(req, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ArrException($"Could not reach {_serviceName}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ArrException($"{_serviceName} did not answer within 30 seconds.", ex);
        }
    }

    private async Task EnsureSuccess(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        string hint = resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => " (check the API key)",
            HttpStatusCode.NotFound => " (check the base URL, including any URL base such as /radarr)",
            _ => string.Empty
        };
        string snippet = string.Empty;
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            snippet = body.Length > 200 ? body[..200] : body;
        }
        catch (HttpRequestException)
        {
        }

        throw new ArrException($"{_serviceName} answered HTTP {(int)resp.StatusCode}{hint}. {snippet}".Trim());
    }
}
