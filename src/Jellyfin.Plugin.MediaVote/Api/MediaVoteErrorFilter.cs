using System;
using System.Text.Json;
using Jellyfin.Plugin.MediaVote.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaVote.Api;

/// <summary>
/// Applied to every Media Vote controller. Any unhandled exception is logged in full on the server and answered
/// with <c>{ "error": "...", "message": "readable text" }</c> and HTTP 500, so the admin page and the voting UI
/// can show what went wrong instead of a bare "HTTP 500". No stack trace is sent to the browser.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class MediaVoteErrorFilterAttribute : Attribute, IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.ExceptionHandled || context.Exception is OperationCanceledException)
        {
            return;
        }

        var ex = context.Exception;
        var logger = context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("MediaVote.Api");
        logger?.LogError(ex, "Media Vote: {Method} {Path} failed", context.HttpContext.Request.Method, context.HttpContext.Request.Path);
        context.Result = Build(ex);
        context.ExceptionHandled = true;
    }

    /// <summary>Builds the JSON error result (public for tests).</summary>
    public static ContentResult Build(Exception ex)
    {
        string code = ErrorText.IsVersionMismatch(ex) ? "jellyfin_version_mismatch"
            : ex is JellyfinApiException ? "jellyfin_api_error"
            : "server_error";
        var body = JsonSerializer.Serialize(new { error = code, message = "Media Vote error: " + ErrorText.Describe(ex) }, MediaVoteControllerBase.Options);
        return new ContentResult { Content = body, ContentType = "application/json; charset=utf-8", StatusCode = 500 };
    }
}
