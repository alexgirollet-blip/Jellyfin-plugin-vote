using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaVote.Core;
using Xunit;

namespace Jellyfin.Plugin.MediaVote.Tests;

public class DriveTests
{
    // Shape of /proc/self/mountinfo inside a linuxserver/jellyfin container on ZimaOS (device numbers made up).
    private const string MountInfo = @"
600 500 0:52 / / rw,relatime master:300 - overlay overlay rw,lowerdir=/x
601 600 0:55 / /proc rw,nosuid - proc proc rw
602 600 0:56 / /dev rw,nosuid - tmpfs tmpfs rw,size=65536k
610 600 8:17 / /LM1 rw,relatime - exfat /dev/sdb1 rw,fmask=0022,dmask=0022
611 600 8:33 / /LM2 rw,relatime - exfat /dev/sdc1 rw,fmask=0022,dmask=0022
612 600 259:2 /DATA/Media /Media rw,relatime - ext4 /dev/nvme0n1p2 rw
613 600 259:2 /DATA/AppData/jellyfin/config /config rw,relatime - ext4 /dev/nvme0n1p2 rw
614 600 8:17 /LargeMedia/Extras /extras\040folder rw,relatime - exfat /dev/sdb1 rw
garbage line
";

    private static readonly string[] Libraries = { "/LM1/Movies", "/LM1/TV Shows", "/LM1/Anime", "/LM2/Movies 2", "/LM2/TV Shows 2", "/Media/BicenVids" };

    [Fact]
    public void Parse_SkipsPseudoFilesystemsAndGarbage_AndUnescapesSpaces()
    {
        var t = MountTable.Parse(MountInfo);
        Assert.DoesNotContain(t.Mounts, m => m.FsType is "proc" or "tmpfs");
        Assert.Contains(t.Mounts, m => m.MountPoint == "/extras folder");
        Assert.Equal("exfat", t.Mounts.First(m => m.MountPoint == "/LM1").FsType);
    }

    [Fact]
    public void Resolve_UsesTheLongestMountPrefix_OnFolderBoundaries()
    {
        var t = MountTable.Parse(MountInfo);
        Assert.Equal("/LM1", t.Resolve("/LM1/TV Shows/Breaking Bad/Season 1/e1.mkv")!.MountPoint);
        Assert.Equal("/Media", t.Resolve("/Media/BicenVids/x.mp4")!.MountPoint);
        Assert.Equal("/", t.Resolve("/LM10/not-a-prefix-match")!.MountPoint); // "/LM1" must not match "/LM10"
        Assert.Equal("/extras folder", t.Resolve("/extras folder/a")!.MountPoint);
        Assert.Null(t.Resolve(null));
    }

    [Fact]
    public void Group_OneDrivePerFilesystem_WithDefaultNames()
    {
        var t = MountTable.Parse(MountInfo);
        var drives = DriveGrouping.Group(t, Libraries, new Dictionary<string, string>());
        Assert.Equal(new[] { "8TB 1", "8TB 2", "System drive" }, drives.Select(d => d.Name).ToArray());
        Assert.Equal("/LM1", drives[0].Key);
        Assert.All(drives, d => Assert.Null(d.SharedWarning));
        Assert.Equal("/LM2", DriveGrouping.KeyFor(t, drives, "/LM2/Movies 2/Film (2001)/film.mkv"));
    }

    [Fact]
    public void Group_TwoFoldersOnOneFilesystem_AreOneDrive()
    {
        var t = MountTable.Parse(MountInfo);
        var drives = DriveGrouping.Group(t, new[] { "/Media/BicenVids", "/config/data" }, new Dictionary<string, string>());
        var d = Assert.Single(drives);
        Assert.Equal(new[] { "/Media", "/config" }, d.MountPoints.ToArray());
        Assert.Equal("System drive", d.Name);
    }

    [Fact]
    public void Group_FlagsLm1AndLm2SharingAFilesystem()
    {
        var shared = MountInfo.Replace("8:33 / /LM2", "8:17 / /LM2", System.StringComparison.Ordinal);
        var drives = DriveGrouping.Group(MountTable.Parse(shared), Libraries, new Dictionary<string, string>());
        var lm = drives.Single(d => d.MountPoints.Contains("/LM1"));
        Assert.Contains("/LM2", lm.MountPoints);
        Assert.Contains("SAME filesystem", lm.SharedWarning);
    }

    [Fact]
    public void Group_AdminNamesWin()
    {
        var drives = DriveGrouping.Group(MountTable.Parse(MountInfo), Libraries, new Dictionary<string, string> { ["/LM2"] = "Big Seagate" });
        Assert.Contains(drives, d => d.Key == "/LM2" && d.Name == "Big Seagate");
    }

    [Theory]
    [InlineData(90, 10, 90.0)]
    [InlineData(0, 0, 0.0)]
    [InlineData(1, 2, 33.3)]
    public void Percent_MatchesDf(long used, long free, double expected) => Assert.Equal(expected, DriveMath.Percent(used, free));

    [Theory]
    [InlineData(true, 84.9, DriveLevel.Ok)]
    [InlineData(true, 85.1, DriveLevel.Warning)]
    [InlineData(true, 95.5, DriveLevel.Critical)]
    [InlineData(false, 99, DriveLevel.Unknown)]
    public void Levels(bool known, double pct, DriveLevel expected) => Assert.Equal(expected, DriveMath.Level(known, pct, 85, 95));

    [Fact]
    public void Projection_ShowsBeforeAndAfter()
    {
        const long tb = 1L << 40;
        var d = new DriveStat { Name = "8TB 1", Known = true, UsedBytes = (long)(6.6 * tb), FreeBytes = (long)(0.7 * tb) };
        Assert.Equal(90.4, d.UsedPercent);
        long freed = 134L << 30;
        Assert.Equal("frees 134.0 GB on 8TB 1 (90% to 89%)", DriveMath.Projection(d, freed));
        Assert.Equal(d.UsedBytes, DriveMath.After(d, long.MaxValue).Free - d.FreeBytes); // never frees more than is used
        Assert.Equal("6.6 TB", DriveMath.Bytes(d.UsedBytes));
    }
}
