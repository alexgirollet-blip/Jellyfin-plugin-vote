using System;
using System.Threading;
using System.IO;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.MediaVote.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaVote.Web;

/// <summary>
/// The payload File Transformation 3.x passes to our callback. It builds
/// <c>new JObject { { "contents", fileText } }</c> and converts it with Newtonsoft
/// <c>JObject.ToObject(parameterType)</c>. Newtonsoft honours <see cref="DataMemberAttribute"/>, so the JSON
/// name is pinned to "contents" without referencing Newtonsoft; <see cref="JsonPropertyNameAttribute"/>
/// covers System.Text.Json.
/// </summary>
[DataContract]
public sealed class TransformPayload
{
    [DataMember(Name = "contents")]
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}

/// <summary>How the client script reaches jellyfin-web.</summary>
public enum InjectionMode
{
    NotStarted,

    /// <summary>Registered with File Transformation.</summary>
    FileTransformation,

    /// <summary>Not needed: neither the banner nor the Vote tab is enabled.</summary>
    NotNeeded,

    /// <summary>Skipped because the DISABLE file exists.</summary>
    DisabledByFile,

    /// <summary>Not possible (File Transformation missing, self-test failed, registration failed).</summary>
    Unavailable
}

/// <summary>Current injection status for the settings page.</summary>
public sealed record InjectionStatus(InjectionMode Mode, string Message, bool SelfTestPassed, bool LeftoverDirectInjection);

/// <summary>
/// Injects one script tag into jellyfin-web's index.html, ONLY through File Transformation (never by writing
/// files). Rules that make it impossible for this to blank the web client:
/// <list type="bullet">
/// <item>The callback never returns content shorter than it received; on any problem it returns the original
///   contents unchanged (null only if it received null, which File Transformation treats as "unchanged").</item>
/// <item>It only inserts one marked tag before &lt;/body&gt; and leaves everything else as is.</item>
/// <item>A self-test of the callback runs before registration; if it fails, nothing is registered.</item>
/// <item>The DISABLE kill switch is checked at registration and on every call.</item>
/// <item>The transformation is registered only while an entry point that needs it (banner or Vote tab) is on.</item>
/// </list>
/// </summary>
public sealed class WebInjectionService
{
    /// <summary>Fixed id of our File Transformation registration.</summary>
    public const string TransformationId = "7d1c3a5e-8f0b-4e6a-9d2c-1b5e7f3a9c41";

    /// <summary>Marker attribute: present once our tag is in the HTML.</summary>
    public const string Marker = "data-mediavote-script";

    private const string FtAssembly = "Jellyfin.Plugin.FileTransformation";
    private const string FtInterface = "Jellyfin.Plugin.FileTransformation.PluginInterface";

    private static readonly object CallbackLock = new();
    private static string _scriptTag = BuildTag(string.Empty, "1");
    private static ILogger? _staticLogger;

    private readonly IApplicationPaths _paths;
    private readonly IServerConfigurationManager _serverConfig;
    private static int _warned;
    private readonly ILogger<WebInjectionService> _logger;
    private bool _registered;

    public WebInjectionService(IApplicationPaths paths, IServerConfigurationManager serverConfig, ILogger<WebInjectionService> logger)
    {
        _paths = paths;
        _serverConfig = serverConfig;
        _logger = logger;
        _staticLogger = logger;
    }

    public InjectionStatus Status { get; private set; } = new(InjectionMode.NotStarted, "Not started yet.", false, false);

    /// <summary>
    /// File Transformation callback (invoked by reflection for index.html). Never throws, never returns content
    /// shorter than its input, and returns the original contents unchanged when it cannot add the tag safely.
    /// </summary>
    public static string? TransformIndexHtml(TransformPayload payload)
    {
        string? original = null;
        try
        {
            original = payload?.Contents;
            if (string.IsNullOrEmpty(original))
            {
                // Nothing usable received: hand back exactly what we got (null keeps File Transformation 3.x from writing).
                WarnOnce("Media Vote: index.html transformation received an empty payload; returning it unchanged");
                return original;
            }

            if (KillSwitch.IsActive || !(Plugin.Instance?.Configuration is { } c && (c.EnableBanner || c.EnableCustomTab)))
            {
                return original;
            }

            string tag;
            lock (CallbackLock)
            {
                tag = _scriptTag;
            }

            var result = SafeInsert(original, tag);
            if (ReferenceEquals(result, original) && !original.Contains(Marker, StringComparison.Ordinal))
            {
                WarnOnce("Media Vote: index.html has no </body> or the insert failed its integrity check; returning the page unchanged");
            }

            return result;
        }
#pragma warning disable CA1031 // must never throw into File Transformation's pipeline
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _staticLogger?.LogWarning(ex, "Media Vote: index.html transformation failed; returning the page unchanged");
            return original;
        }
    }

    /// <summary>Logs a warning the first time only: the callback can run for every page load.</summary>
    private static void WarnOnce(string message)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            _staticLogger?.LogWarning("{Message}", message);
        }
    }

    /// <summary>
    /// Inserts <paramref name="tag"/> once before the last &lt;/body&gt;. Returns the original string instance
    /// (unchanged) if the marker is already present, there is no &lt;/body&gt;, or the result would not contain
    /// the whole original. The result is never shorter than the input.
    /// </summary>
    public static string SafeInsert(string html, string tag)
    {
        if (string.IsNullOrEmpty(html) || string.IsNullOrEmpty(tag) || html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        int idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return html;
        }

        var result = html.Insert(idx, tag);
        bool intact = result.Length == html.Length + tag.Length
            && string.CompareOrdinal(result, 0, html, 0, idx) == 0
            && string.CompareOrdinal(result, idx + tag.Length, html, idx, html.Length - idx) == 0;
        return intact ? result : html;
    }

    public static string BuildTag(string baseUrl, string version)
        => $"<script {Marker}=\"{version}\" src=\"{baseUrl}/MediaVote/client.js?v={version}\" defer></script>";

    /// <summary>
    /// Self-test of the callback with sample HTML: the output must contain the original HTML plus exactly one
    /// marker tag, a second pass must be a no-op, and empty/null payloads must leave the file unchanged.
    /// </summary>
    public static string? SelfTest(string tag)
    {
        const string sample = "<!DOCTYPE html><html><head><title>t</title></head><body><div id=\"reactRoot\"></div><script src=\"main.jellyfin.bundle.js\"></script></body></html>";
        var once = SafeInsert(sample, tag);
        if (once.Length != sample.Length + tag.Length)
        {
            return "the tag was not inserted exactly once";
        }

        if (once.Replace(tag, string.Empty, StringComparison.Ordinal) != sample)
        {
            return "the original HTML was not preserved";
        }

        if (CountOccurrences(once, Marker) != 1)
        {
            return "the marker does not occur exactly once";
        }

        if (!ReferenceEquals(SafeInsert(once, tag), once))
        {
            return "a second pass changed the page";
        }

        if (SafeInsert(string.Empty, tag).Length != 0 || SafeInsert("<html>no body end", tag) != "<html>no body end")
        {
            return "empty or unusual input was modified";
        }

        return null;
    }

    /// <summary>Registers or removes the transformation to match the current settings. Never throws.</summary>
    public void Apply()
    {
        try
        {
            ApplyCore();
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Media Vote: web injection setup failed; the web client is left untouched");
            Status = new InjectionStatus(InjectionMode.Unavailable, "Setup failed: " + ex.Message + ". Nothing was injected.", false, DetectLeftoverDirectInjection());
        }
    }

    /// <summary>Removes our File Transformation registration (used on uninstall and when no entry point needs it).</summary>
    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        var ft = ReflectionBridge.FindAssembly(FtAssembly);
        var error = ft is null ? "File Transformation not loaded" : ReflectionBridge.InvokeStatic(ft, FtInterface, "RemoveTransformation", Guid.Parse(TransformationId));
        if (error is null)
        {
            _registered = false;
        }
        else
        {
            _logger.LogWarning("Media Vote: could not remove the File Transformation registration: {Error}", error);
        }
    }

    private void ApplyCore()
    {
        var leftover = DetectLeftoverDirectInjection();
        var cfg = Plugin.Instance?.Configuration;
        if (KillSwitch.IsActive)
        {
            Unregister();
            Status = new InjectionStatus(InjectionMode.DisabledByFile, $"Disabled by file ({KillSwitch.FilePath}). Nothing is injected. Delete the file and restart Jellyfin to re-enable.", false, leftover);
            return;
        }

        if (cfg is null || !(cfg.EnableBanner || cfg.EnableCustomTab))
        {
            Unregister();
            Status = new InjectionStatus(InjectionMode.NotNeeded, "Not needed: the home banner and the Vote tab are both off, so nothing is injected into the web client.", false, leftover);
            return;
        }

        var baseUrl = _serverConfig.GetNetworkConfiguration().BaseUrl?.Trim().Trim('/') ?? string.Empty;
        baseUrl = string.IsNullOrEmpty(baseUrl) ? string.Empty : "/" + baseUrl;
        var tag = BuildTag(baseUrl, Plugin.Instance?.Version?.ToString() ?? "1");
        var selfTest = SelfTest(tag);
        if (selfTest is not null)
        {
            Unregister();
            Status = new InjectionStatus(InjectionMode.Unavailable, "Self-test of the index.html transformation failed (" + selfTest + "), so it was NOT registered.", false, leftover);
            _logger.LogError("Media Vote: transformation self-test failed: {Problem}. Not registering.", selfTest);
            return;
        }

        lock (CallbackLock)
        {
            _scriptTag = tag;
        }

        if (_registered)
        {
            Status = new InjectionStatus(InjectionMode.FileTransformation, "Registered with File Transformation (self-test passed).", true, leftover);
            return;
        }

        var ft = ReflectionBridge.FindAssembly(FtAssembly);
        if (ft is null)
        {
            Status = new InjectionStatus(InjectionMode.Unavailable, "File Transformation is not installed, so the home banner and the Vote tab cannot work. Media Vote never edits index.html itself. The admin page, the standalone page and the Plugin Pages page still work.", true, leftover);
            return;
        }

        var error = ReflectionBridge.InvokeWithJObject(ft, FtInterface, "RegisterTransformation", new
        {
            id = TransformationId,
            fileNamePattern = "index.html",
            callbackAssembly = typeof(WebInjectionService).Assembly.FullName,
            callbackClass = typeof(WebInjectionService).FullName,
            callbackMethod = nameof(TransformIndexHtml)
        });
        _registered = error is null;
        Status = error is null
            ? new InjectionStatus(InjectionMode.FileTransformation, $"Registered with File Transformation {ft.GetName().Version} (self-test passed).", true, leftover)
            : new InjectionStatus(InjectionMode.Unavailable, "File Transformation registration failed: " + error, true, leftover);
        if (error is not null)
        {
            _logger.LogWarning("Media Vote: File Transformation registration failed: {Error}", error);
        }
    }

    /// <summary>
    /// Read-only: true if index.html on disk still contains a tag written by 1.0.0.0's direct-injection fallback.
    /// 1.0.1.0 never writes to index.html; the admin is told how to remove it.
    /// </summary>
    private bool DetectLeftoverDirectInjection()
    {
        try
        {
            var path = Path.Combine(_paths.WebPath, "index.html");
            return File.Exists(path) && File.ReadAllText(path).Contains(Marker, StringComparison.Ordinal);
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private static int CountOccurrences(string s, string value)
    {
        int count = 0;
        for (int i = s.IndexOf(value, StringComparison.Ordinal); i >= 0; i = s.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
