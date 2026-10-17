using System;
using System.IO;
using System.Reflection;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>Turns exceptions into short, readable messages for the admin (never a stack trace).</summary>
public static class ErrorText
{
    /// <summary>The Jellyfin version this build was compiled against (kept in sync with build.yaml targetAbi by CI).</summary>
    public const string BuiltForJellyfin = "10.11.10";

    public static bool IsVersionMismatch(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is MissingMemberException or TypeLoadException or FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                return true;
            }
        }

        return false;
    }

    public static string Describe(Exception ex)
    {
        var inner = ex is TargetInvocationException { InnerException: { } ti } ? ti : ex;
        if (IsVersionMismatch(inner))
        {
            return $"This Media Vote build (made for Jellyfin {BuiltForJellyfin}) does not match the Jellyfin version running on this server: {Root(inner).Message} "
                   + "Install the Media Vote release built for your Jellyfin version.";
        }

        if (inner is JellyfinApiException)
        {
            return inner.Message;
        }

        return inner.Message;
    }

    private static Exception Root(Exception ex)
    {
        var e = ex;
        while (e.InnerException is not null && !(e is MissingMemberException or TypeLoadException))
        {
            e = e.InnerException;
        }

        return e;
    }
}

/// <summary>A Jellyfin API call made by Media Vote failed. The message says which call.</summary>
public sealed class JellyfinApiException : Exception
{
    public JellyfinApiException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public JellyfinApiException(string message)
        : base(message)
    {
    }

    public JellyfinApiException()
    {
    }
}

/// <summary>Wraps a Jellyfin API call so a failure explains what Media Vote was trying to do.</summary>
public static class JellyfinCall
{
    public static T Run<T>(string what, Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is not JellyfinApiException and not OperationCanceledException)
        {
            var detail = ErrorText.IsVersionMismatch(ex) ? ErrorText.Describe(ex) : ex.Message;
            throw new JellyfinApiException($"Jellyfin could not {what}: {detail}", ex);
        }
    }
}
