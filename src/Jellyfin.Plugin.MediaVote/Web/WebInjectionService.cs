using System;
using System.IO;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MediaVote.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaVote.Web;

/// <summary>The payload File Transformation passes to our callback ({"contents": "..."}).</summary>
public sealed class TransformPayload
{
    public string? Contents { get; set; }
}

/// <summary>How the client script reaches jellyfin-web.</summary>
public enum InjectionMode
{
    NotStarted,
    FileTransformation,
    DirectIndexHtml,
    Failed
}

/// <summary>Current injection status for the settings page.</summary>
public sealed record InjectionStatus(InjectionMode Mode, string Message, string? IndexHtmlPath, string? DockerFix);

/// <summary>
/// Injects one script tag into jellyfin-web's index.html.
/// Preferred: an additive File Transformation registration (index.html is never modified on disk;
/// other plugins' transformations on the same file still run, in any order).
/// Fallback: insert the same tag directly into index.html in Jellyfin's web folder, only if missing.
/// </summary>
public sealed class WebInjectionService
{
    /// <summary>Fixed id of our File Transformation registration.</summary>
    public const string TransformationId = "7d1c3a5e-8f0b-4e6a-9d2c-1b5e7f3a9c41";

    /// <summary>Marker attribute: present once our tag is in the HTML.</summary>
    public const string Marker = "data-mediavote-script";

    private static string _scriptTag = BuildTag(string.Empty, "1");

    private readonly IApplicationPaths _paths;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly ILogger<WebInjectionService> _logger;

    public WebInjectionService(IApplicationPaths paths, IServerConfigurationManager serverConfig, ILogger<WebInjectionService> logger)
    {
        _paths = paths;
        _serverConfig = serverConfig;
        _logger = logger;
    }

    public InjectionStatus Status { get; private set; } = new(InjectionMode.NotStarted, "Not started yet.", null, null);

    public string IndexHtmlPath => Path.Combine(_paths.WebPath, "index.html");

    /// <summary>
    /// File Transformation callback (invoked by reflection). Purely additive and idempotent:
    /// inserts our tag before &lt;/body&gt; once, leaves everything else untouched.
    /// </summary>
    public static string TransformIndexHtml(TransformPayload payload)
        => InsertTag(payload?.Contents ?? string.Empty, _scriptTag);

    /// <summary>Inserts <paramref name="tag"/> before the last &lt;/body&gt; if our marker is not present yet.</summary>
    public static string InsertTag(string html, string tag)
    {
        if (string.IsNullOrEmpty(html) || html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        int idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? html + tag : html.Insert(idx, tag);
    }

    /// <summary>Removes our tag (used to clean up a direct injection once File Transformation is available).</summary>
    public static string RemoveTag(string html)
        => Regex.Replace(html, "<script[^>]*" + Marker + "[^>]*></script>", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(2));

    public static string BuildTag(string baseUrl, string version)
        => $"<script {Marker}=\"{version}\" src=\"{baseUrl}/MediaVote/client.js?v={version}\" defer></script>";

    public void Run()
    {
        var baseUrl = _serverConfig.GetNetworkConfiguration().BaseUrl?.Trim().Trim('/') ?? string.Empty;
        baseUrl = string.IsNullOrEmpty(baseUrl) ? string.Empty : "/" + baseUrl;
        var version = Plugin.Instance?.Version?.ToString() ?? "1";
        _scriptTag = BuildTag(baseUrl, version);

        var ft = ReflectionBridge.FindAssembly("Jellyfin.Plugin.FileTransformation");
        if (ft is not null)
        {
            var error = ReflectionBridge.InvokeWithJObject(ft, "Jellyfin.Plugin.FileTransformation.PluginInterface", "RegisterTransformation", new
            {
                id = TransformationId,
                fileNamePattern = "index.html",
                callbackAssembly = typeof(WebInjectionService).Assembly.FullName,
                callbackClass = typeof(WebInjectionService).FullName,
                callbackMethod = nameof(TransformIndexHtml)
            });
            if (error is null)
            {
                CleanUpDirectInjection();
                Status = new InjectionStatus(InjectionMode.FileTransformation, $"Injected through File Transformation {ft.GetName().Version}.", IndexHtmlPath, null);
                _logger.LogInformation("Media Vote: client script registered with File Transformation");
                return;
            }

            _logger.LogWarning("Media Vote: File Transformation registration failed ({Error}); trying direct index.html injection", error);
        }

        TryDirectInjection(ft is null ? "File Transformation is not installed." : "File Transformation registration failed.");
    }

    private void TryDirectInjection(string reason)
    {
        var path = IndexHtmlPath;
        if (!File.Exists(path))
        {
            Status = new InjectionStatus(InjectionMode.Failed, $"{reason} jellyfin-web index.html was not found at {path} (is the web client disabled?).", path, null);
            return;
        }

        var check = PermissionProbe.CheckWritableFile(path);
        if (!check.Ok)
        {
            Status = new InjectionStatus(InjectionMode.Failed, $"{reason} index.html is not writable: {check.Problem}", path, DockerFix(path));
            _logger.LogWarning("Media Vote: cannot inject client script. {Reason} {Problem}", reason, check.Problem);
            return;
        }

        try
        {
            var html = File.ReadAllText(path);
            var updated = InsertTag(RemoveTag(html), _scriptTag);
            if (!string.Equals(html, updated, StringComparison.Ordinal))
            {
                var backup = path + ".mediavote-backup";
                if (!File.Exists(backup))
                {
                    File.Copy(path, backup);
                }

                File.WriteAllText(path, updated);
            }

            Status = new InjectionStatus(InjectionMode.DirectIndexHtml, $"{reason} The script tag was added directly to {path}. Jellyfin updates replace this file; Media Vote re-adds the tag at every start.", path, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = new InjectionStatus(InjectionMode.Failed, $"{reason} Writing index.html failed: {ex.Message}", path, DockerFix(path));
        }
    }

    private void CleanUpDirectInjection()
    {
        try
        {
            var path = IndexHtmlPath;
            if (File.Exists(path) && PermissionProbe.CheckWritableFile(path).Ok)
            {
                var html = File.ReadAllText(path);
                if (html.Contains(Marker, StringComparison.Ordinal))
                {
                    File.WriteAllText(path, RemoveTag(html));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Media Vote: could not remove old direct injection");
        }
    }

    private static string DockerFix(string path)
        => "Recommended: install the File Transformation plugin (already used by Home Screen Sections, Plugin Pages and Custom Tabs), then restart Jellyfin. "
           + "Alternative for the linuxserver/jellyfin image: copy the file out once with "
           + $"`docker cp jellyfin:{path} /DATA/AppData/jellyfin/index.html`, run `chown 1000:1000 /DATA/AppData/jellyfin/index.html` (use your PUID:PGID), "
           + $"add the volume `/DATA/AppData/jellyfin/index.html:{path}` to the container (ZimaOS: App settings > Volumes), and restart. "
           + "Remove that volume before upgrading Jellyfin, or the old web client page will be kept.";
}
