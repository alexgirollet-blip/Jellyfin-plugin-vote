using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaVote.Configuration;
using Jellyfin.Plugin.MediaVote.Core;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaVote.Services;

/// <summary>
/// Which drive (filesystem) every media folder is on, with total/used/free space. Read-only: parses
/// /proc/self/mountinfo (longest mount-point prefix wins, mounts of the same device are one drive) and asks the
/// kernel for usage with statvfs (via <see cref="DriveInfo"/>). Cached for 60 seconds; a drive that cannot be read is
/// shown as "unknown" and never breaks anything.
/// </summary>
public sealed class DriveService : IDriveStats
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DriveService> _logger;
    private readonly object _lock = new();
    private (DateTime At, MountTable Table, List<DriveStat> Drives)? _cache;

    public DriveService(ILibraryManager libraryManager, ILogger<DriveService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Replaceable for tests: returns the mountinfo text.</summary>
    internal Func<string> ReadMountInfo { get; set; } = () => File.ReadAllText("/proc/self/mountinfo");

    /// <summary>Replaceable for tests: (total, available to user, free including reserved) for a mount point.</summary>
    internal Func<string, (long Total, long Available, long Free)> ReadUsage { get; set; } = path =>
    {
        var d = new DriveInfo(path);
        return (d.TotalSize, d.AvailableFreeSpace, d.TotalFreeSpace);
    };

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public IReadOnlyList<DriveStat> GetDrives() => Snapshot().Drives;

    /// <summary>The drive key for a path (null when unknown).</summary>
    public string? KeyFor(string? path)
    {
        var (table, drives) = Snapshot();
        return DriveGrouping.KeyFor(table, drives, path);
    }

    /// <summary>Forget the cache (after the admin renames drives or changes thresholds).</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _cache = null;
        }
    }

    public (MountTable Table, List<DriveStat> Drives) Snapshot()
    {
        lock (_lock)
        {
            if (_cache is { } c && DateTime.UtcNow - c.At < MaxAge)
            {
                return (c.Table, c.Drives);
            }

            var (table, drives) = Build();
            _cache = (DateTime.UtcNow, table, drives);
            return (table, drives);
        }
    }

    private (MountTable Table, List<DriveStat> Drives) Build()
    {
        MountTable table;
        try
        {
            table = MountTable.Parse(ReadMountInfo());
        }
#pragma warning disable CA1031 // not Linux, or /proc unreadable: drives are simply unknown
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogInformation("Media Vote: could not read /proc/self/mountinfo ({Error}); drive usage unknown", ex.Message);
            table = new MountTable(Array.Empty<MountEntry>());
        }

        List<string> folders;
        try
        {
            folders = _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType is null || f.CollectionType is CollectionTypeOptions.movies or CollectionTypeOptions.tvshows or CollectionTypeOptions.homevideos)
                .SelectMany(f => f.Locations ?? Array.Empty<string>())
                .ToList();
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Media Vote: could not list library folders for drive detection");
            folders = new List<string>();
        }

        var cfg = Config;
        var names = cfg.DriveNames.GroupBy(n => n.MountPoint.Trim(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Name, StringComparer.Ordinal);
        var drives = DriveGrouping.Group(table, folders, names);
        foreach (var d in drives)
        {
            try
            {
                var (total, available, free) = ReadUsage(d.Key);
                d.TotalBytes = total;
                d.FreeBytes = available;
                d.UsedBytes = Math.Max(0, total - free);
                d.Known = total > 0;
            }
#pragma warning disable CA1031 // an unreadable drive is "unknown"
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogInformation("Media Vote: could not read usage of {Drive}: {Error}", d.Key, ex.Message);
                d.Known = false;
            }

            d.Level = DriveMath.Level(d.Known, d.UsedPercent, cfg.DriveWarnPercent, cfg.DriveCriticalPercent);
        }

        return (table, drives);
    }
}
