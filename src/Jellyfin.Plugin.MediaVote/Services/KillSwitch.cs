using System;
using System.IO;

namespace Jellyfin.Plugin.MediaVote.Services;

/// <summary>
/// Emergency kill switch. If a file named <c>DISABLE</c> exists in Media Vote's data folder
/// (<c>/config/data/mediavote/DISABLE</c> in the linuxserver image; on ZimaOS the host path is
/// <c>/DATA/AppData/jellyfin/config/data/mediavote/DISABLE</c>), the plugin injects nothing into the web
/// client, registers no entry point with other plugins, and its client script does nothing.
/// The admin page and API keep working so the problem can be investigated.
/// </summary>
public static class KillSwitch
{
    public const string FileName = "DISABLE";

    private static string? _dataDirectory;

    /// <summary>Set once at start-up.</summary>
    public static void Initialise(string dataDirectory) => _dataDirectory = dataDirectory;

    public static string? FilePath => _dataDirectory is null ? null : Path.Combine(_dataDirectory, FileName);

    /// <summary>True when the DISABLE file exists. Never throws.</summary>
    public static bool IsActive => IsActiveIn(_dataDirectory);

    public static bool IsActiveIn(string? dataDirectory)
    {
        if (string.IsNullOrEmpty(dataDirectory))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(dataDirectory, FileName));
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
