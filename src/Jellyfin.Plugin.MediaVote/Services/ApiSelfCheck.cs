using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaVote.Core;
using Jellyfin.Plugin.MediaVote.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaVote.Services;

/// <summary>
/// Calls each Jellyfin API Media Vote depends on once and records the result
/// ("Users: OK, 16 found" or the exact error). Runs at start-up and on demand from the Status tab.
/// A broken API only produces a failed line; it never stops the plugin or the server.
/// </summary>
public sealed class ApiSelfCheck
{
    private readonly JellyfinUserDirectory _users;
    private readonly LibraryCatalog _catalog;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<ApiSelfCheck> _logger;

    public ApiSelfCheck(JellyfinUserDirectory users, LibraryCatalog catalog, IApplicationPaths paths, ILogger<ApiSelfCheck> logger)
    {
        _users = users;
        _catalog = catalog;
        _paths = paths;
        _logger = logger;
    }

    public IReadOnlyList<SelfCheckResult> Results { get; private set; } = Array.Empty<SelfCheckResult>();

    public DateTime? LastRunUtc { get; private set; }

    /// <summary>Runs all checks. Never throws.</summary>
    public IReadOnlyList<SelfCheckResult> Run()
    {
        try
        {
            Results = SelfCheckRunner.Run(Checks());
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Results = new[] { new SelfCheckResult("Self-check", false, ErrorText.Describe(ex)) };
        }

        LastRunUtc = DateTime.UtcNow;
        foreach (var r in Results)
        {
            if (r.Ok)
            {
                _logger.LogInformation("Media Vote self-check: {Name}: {Detail}", r.Name, r.Detail);
            }
            else
            {
                _logger.LogError("Media Vote self-check FAILED: {Name}: {Detail}", r.Name, r.Detail);
            }
        }

        return Results;
    }

    private IEnumerable<(string Name, Func<string> Check)> Checks()
    {
        yield return ("Jellyfin version", () =>
        {
            var running = typeof(ILibraryManager).Assembly.GetName().Version;
            var expected = new Version(ErrorText.BuiltForJellyfin);
            if (running is null)
            {
                return "unknown";
            }

            if (running.Major != expected.Major || running.Minor != expected.Minor || running.Build != expected.Build)
            {
                throw new InvalidOperationException($"server runs {running.ToString(3)}, this build is for {expected}. Features may fail; install the matching Media Vote build.");
            }

            return $"OK, {running.ToString(3)} (this build targets {expected})";
        });

        yield return ("Users", () =>
        {
            var users = _users.ListUsers();
            return $"OK, {users.Count} found ({users.Count(u => u.Disabled)} disabled, {users.Count(u => u.Admin)} admin)";
        });

        yield return ("Libraries", () =>
        {
            var libs = _catalog.GetLibraries();
            return $"OK, {libs.Count} movie/TV libraries";
        });

        yield return ("Library items (sample)", () => $"OK, {_catalog.SampleScope(20)} titles read");

        yield return ("Play statistics (one user)", () =>
        {
            var (user, played) = _catalog.SamplePlayStats();
            return $"OK, {played} played items for {user}";
        });

        yield return ("Deletion API", () =>
        {
            // Only checks that the exact method Media Vote calls exists in this Jellyfin build. Nothing is deleted.
            var m = typeof(ILibraryManager).GetMethod(nameof(ILibraryManager.DeleteItem), new[] { typeof(BaseItem), typeof(DeleteOptions), typeof(bool) });
            return m is null
                ? throw new MissingMethodException("ILibraryManager.DeleteItem(BaseItem, DeleteOptions, bool) not found")
                : "OK, present (not called)";
        });

        yield return ("Vote data folder", () =>
        {
            var dir = PluginServiceRegistrator.DataDirectory(_paths);
            var check = PermissionProbe.CheckWritableDirectory(dir);
            return check.Ok ? "OK, writable: " + dir : throw new InvalidOperationException(check.Problem);
        });

        yield return ("index.html transformation self-test", () =>
        {
            var problem = WebInjectionService.SelfTest(WebInjectionService.BuildTag(string.Empty, "test"));
            return problem is null ? "OK, adds exactly one tag and never removes content" : throw new InvalidOperationException(problem);
        });
    }
}
