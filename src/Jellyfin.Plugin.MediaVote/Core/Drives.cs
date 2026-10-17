using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>One line of /proc/self/mountinfo.</summary>
public sealed record MountEntry(int MountId, int ParentId, string Device, string Root, string MountPoint, string FsType, string Source);

/// <summary>How full a drive is, against the admin's thresholds.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriveLevel
{
    Unknown,
    Ok,
    Warning,
    Critical
}

/// <summary>A drive (one filesystem) as shown to the admin and on title cards.</summary>
public sealed class DriveStat
{
    /// <summary>Stable key: the shortest mount point of this filesystem among the media folders (for example "/LM1").</summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Every mount point (inside the container) that belongs to this filesystem.</summary>
    public List<string> MountPoints { get; set; } = new();

    /// <summary>Kernel device "major:minor" (mounts with the same device are the same filesystem).</summary>
    public string Device { get; set; } = string.Empty;

    public string FsType { get; set; } = string.Empty;

    public bool Known { get; set; }

    public long TotalBytes { get; set; }

    public long UsedBytes { get; set; }

    /// <summary>Space available to Jellyfin's user (what df shows as "Avail").</summary>
    public long FreeBytes { get; set; }

    /// <summary>Used / (used + available), as df computes "Use%".</summary>
    public double UsedPercent => DriveMath.Percent(UsedBytes, FreeBytes);

    public DriveLevel Level { get; set; }

    /// <summary>Set when two media mount points that look like separate drives are in fact one filesystem.</summary>
    public string? SharedWarning { get; set; }
}

/// <summary>Pure size and percentage maths for drives.</summary>
public static class DriveMath
{
    public static double Percent(long used, long free)
    {
        long denom = used + free;
        return denom <= 0 ? 0 : Math.Round(used * 100.0 / denom, 1);
    }

    public static DriveLevel Level(bool known, double usedPercent, int warnPercent, int criticalPercent)
        => !known ? DriveLevel.Unknown : usedPercent > criticalPercent ? DriveLevel.Critical : usedPercent > warnPercent ? DriveLevel.Warning : DriveLevel.Ok;

    /// <summary>Usage after freeing <paramref name="freedBytes"/> on the drive.</summary>
    public static (long Used, long Free, double Percent) After(DriveStat d, long freedBytes)
    {
        long freed = Math.Clamp(freedBytes, 0, Math.Max(0, d.UsedBytes));
        long used = d.UsedBytes - freed;
        long free = d.FreeBytes + freed;
        return (used, free, Percent(used, free));
    }

    /// <summary>"Removing the selected titles frees 134 GB on 8TB 1 (90% to 88%)".</summary>
    public static string Projection(DriveStat d, long freedBytes)
    {
        if (!d.Known)
        {
            return "Frees " + Bytes(freedBytes) + " on " + d.Name + " (drive usage unknown)";
        }

        var after = After(d, freedBytes);
        return string.Create(CultureInfo.InvariantCulture, $"frees {Bytes(freedBytes)} on {d.Name} ({d.UsedPercent:0}% to {after.Percent:0}%)");
    }

    /// <summary>Binary units with one decimal from GB up: "134.0 GB", "6.6 TB".</summary>
    public static string Bytes(long b)
    {
        if (b <= 0)
        {
            return "0 B";
        }

        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        int i = Math.Min(u.Length - 1, (int)Math.Floor(Math.Log(b) / Math.Log(1024)));
        double v = b / Math.Pow(1024, i);
        return v.ToString(i >= 3 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + u[i];
    }
}

/// <summary>Parses /proc/self/mountinfo and maps paths to the filesystem they live on.</summary>
public sealed class MountTable
{
    private static readonly HashSet<string> PseudoFs = new(StringComparer.Ordinal)
    {
        "proc", "sysfs", "cgroup", "cgroup2", "devpts", "mqueue", "tmpfs", "devtmpfs", "securityfs", "pstore", "bpf", "debugfs", "tracefs",
        "fusectl", "configfs", "hugetlbfs", "autofs", "binfmt_misc", "nsfs", "rpc_pipefs", "selinuxfs", "efivarfs", "ramfs"
    };

    private readonly List<MountEntry> _mounts;

    public MountTable(IEnumerable<MountEntry> mounts)
    {
        // Longest mount point first, so the first prefix match is the most specific mount.
        _mounts = mounts.Where(m => !PseudoFs.Contains(m.FsType)).OrderByDescending(m => m.MountPoint.Length).ToList();
    }

    public IReadOnlyList<MountEntry> Mounts => _mounts;

    /// <summary>Parses mountinfo text. Malformed lines are skipped.</summary>
    public static MountTable Parse(string text)
    {
        var list = new List<MountEntry>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // 36 35 98:0 /mnt1 /mnt2 rw,noatime master:1 - ext3 /dev/root rw,errors=continue
            var parts = line.Split(' ');
            int dash = Array.IndexOf(parts, "-");
            if (parts.Length < 10 || dash < 6 || dash + 2 >= parts.Length
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parent))
            {
                continue;
            }

            list.Add(new MountEntry(id, parent, parts[2], Unescape(parts[3]), Unescape(parts[4]), parts[dash + 1], Unescape(parts[dash + 2])));
        }

        return new MountTable(list);
    }

    /// <summary>The mount a path lives on: longest mount point that is the path itself or a parent folder of it.</summary>
    public MountEntry? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var p = path.Replace('\\', '/');
        foreach (var m in _mounts)
        {
            var mp = m.MountPoint;
            if (mp == "/" || p == mp || (p.StartsWith(mp, StringComparison.Ordinal) && p.Length > mp.Length && p[mp.Length] == '/'))
            {
                return m;
            }
        }

        return null;
    }

    /// <summary>mountinfo escapes space, tab, newline and backslash as \040, \011, \012, \134.</summary>
    public static string Unescape(string s)
    {
        if (s.IndexOf('\\', StringComparison.Ordinal) < 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 3 < s.Length && IsOctal(s[i + 1]) && IsOctal(s[i + 2]) && IsOctal(s[i + 3]))
            {
                sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        return sb.ToString();
    }

    private static bool IsOctal(char c) => c is >= '0' and <= '7';
}

/// <summary>Groups the media folders' mounts into drives (one per filesystem) and names them.</summary>
public static class DriveGrouping
{
    /// <summary>Default friendly names by mount point, used until the admin renames a drive.</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["/LM1"] = "8TB 1",
        ["/LM2"] = "8TB 2",
        ["/Media"] = "System drive"
    };

    /// <summary>
    /// One <see cref="DriveStat"/> per filesystem (device) that holds any of <paramref name="folders"/>. The key is the
    /// shortest of its mount points; names come from <paramref name="names"/> (by any of its mount points), then the
    /// defaults, then the mount point itself.
    /// </summary>
    public static List<DriveStat> Group(MountTable table, IEnumerable<string> folders, IReadOnlyDictionary<string, string> names)
    {
        var byDevice = new Dictionary<string, List<MountEntry>>(StringComparer.Ordinal);
        foreach (var f in folders.Distinct(StringComparer.Ordinal))
        {
            var m = table.Resolve(f);
            if (m is null)
            {
                continue;
            }

            if (!byDevice.TryGetValue(m.Device, out var list))
            {
                list = new List<MountEntry>();
                byDevice[m.Device] = list;
            }

            if (!list.Contains(m))
            {
                list.Add(m);
            }
        }

        var drives = new List<DriveStat>();
        foreach (var (device, mounts) in byDevice)
        {
            var points = mounts.Select(m => m.MountPoint).Distinct(StringComparer.Ordinal).OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).ToList();
            string? name = points.Select(p => names.TryGetValue(p, out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : null).FirstOrDefault(n => n is not null)
                ?? points.Select(p => DefaultNames.TryGetValue(p, out var n) ? n : null).FirstOrDefault(n => n is not null);
            var d = new DriveStat
            {
                Key = points[0],
                Name = name ?? points[0],
                MountPoints = points,
                Device = device,
                FsType = mounts[0].FsType
            };

            // Two mount points with different default/friendly names on one filesystem (for example /LM1 and /LM2).
            var distinctNames = points.Select(p => names.TryGetValue(p, out var n) ? n : DefaultNames.TryGetValue(p, out var dn) ? dn : null).Where(n => n is not null).Distinct().ToList();
            if (points.Count > 1 && distinctNames.Count > 1)
            {
                d.SharedWarning = $"{string.Join(" and ", points)} are on the SAME filesystem (device {device}): they are one drive, not {points.Count}.";
            }

            drives.Add(d);
        }

        return drives.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The key of the drive a path is on, or null.</summary>
    public static string? KeyFor(MountTable table, IReadOnlyList<DriveStat> drives, string? path)
    {
        var m = table.Resolve(path);
        if (m is null)
        {
            return null;
        }

        return drives.FirstOrDefault(d => d.Device == m.Device)?.Key;
    }
}
