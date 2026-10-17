using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaVote.Core;

namespace Jellyfin.Plugin.MediaVote.Integrations.Arr;

/// <summary>Raised for any HTTP or protocol failure talking to Radarr/Sonarr/Jellyseerr. Never contains the API key.</summary>
public sealed class ArrException : ExternalServiceException
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

    /// <summary>True when the service answered (it is reachable) but refused the request.</summary>
    public bool Answered { get; init; }
}

/// <summary>Thrown when a state-changing call is attempted on a read-only (dry-run / notify-only) client. A bug guard.</summary>
public sealed class ReadOnlyViolationException : InvalidOperationException
{
    public ReadOnlyViolationException(string message)
        : base(message)
    {
    }

    public ReadOnlyViolationException()
    {
    }

    public ReadOnlyViolationException(string message, Exception inner)
        : base(message, inner)
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

    public ArrHttp(HttpClient http, string serviceName, string baseUrl, string apiKey, bool readOnly = false)
    {
        _http = http;
        _serviceName = serviceName;
        _baseUrl = NormalizeBaseUrl(baseUrl) ?? string.Empty;
        _apiKey = apiKey?.Trim() ?? string.Empty;
        ReadOnly = readOnly;
    }

    public bool IsConfigured => _baseUrl.Length > 0 && _apiKey.Length > 0;

    /// <summary>When true, only GET requests can be sent: notify-only and dry runs use read-only clients.</summary>
    public bool ReadOnly { get; }

    public string ServiceName => _serviceName;

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
        if (ReadOnly && method != HttpMethod.Get)
        {
            throw new ReadOnlyViolationException($"Blocked a {method} request to {_serviceName}: this is a read-only (notify-only or dry-run) operation.");
        }

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
        string body = string.Empty;
        try
        {
            body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
        }

        var said = ExtractMessage(body);
        throw new ArrException($"{_serviceName} answered HTTP {(int)resp.StatusCode}{hint}" + (said.Length > 0 ? ": " + said : "."))
        {
            Answered = resp.StatusCode is not (HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        };
    }

    /// <summary>
    /// The service's own error text, verbatim: "message" (+ "description") of an error object, the "errorMessage" of
    /// each validation failure, or the raw body (first 500 characters) when it is not JSON.
    /// </summary>
    public static string ExtractMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var parts = new List<string>();
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "message", "description", "error", "title" })
                {
                    if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) && !parts.Contains(v.GetString()!))
                    {
                        parts.Add(v.GetString()!.Trim());
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in root.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("errorMessage", out var m) && m.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(m.GetString()!.Trim());
                    }
                }
            }

            if (parts.Count > 0)
            {
                return string.Join(" — ", parts);
            }
        }
        catch (JsonException)
        {
        }

        var text = body.Trim();
        return text.Length > 500 ? text[..500] : text;
    }
}
