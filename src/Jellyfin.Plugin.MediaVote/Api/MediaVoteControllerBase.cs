using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaVote.Api;

/// <summary>Shared helpers. JSON in and out uses our own camelCase options, independent of Jellyfin's global settings.</summary>
[MediaVoteErrorFilter]
public abstract class MediaVoteControllerBase : ControllerBase
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>The authenticated user's id, taken only from the auth token claims (never from the request).</summary>
    protected Guid CurrentUserId
    {
        get
        {
            var value = User.FindFirst("Jellyfin-UserId")?.Value;
            return Guid.TryParse(value, out var id) ? id : Guid.Empty;
        }
    }

    protected ActionResult Json(object? value, int status = 200)
        => new ContentResult { Content = JsonSerializer.Serialize(value, Options), ContentType = "application/json; charset=utf-8", StatusCode = status };

    protected ActionResult Error(int status, string code, string? message = null)
        => Json(new { error = code, message = message ?? code }, status);

    protected async Task<T?> ReadBodyAsync<T>()
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(Request.Body, Options, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}
