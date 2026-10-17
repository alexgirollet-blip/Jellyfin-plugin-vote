using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.MediaVote.Services;

/// <summary>Result of a read-only write-access check.</summary>
public sealed record WriteAccess(bool Writable, string Detail);

/// <summary>
/// Read-only, informational checks of what the Jellyfin process can write. Nothing here blocks anything: media is
/// removed by Sonarr/Radarr, so Jellyfin not being able to write to the media drives (for example exFAT drives that are
/// always owned by root) is expected and fine. Uses access(2) on Linux and never creates or changes a file.
/// </summary>
public static class PermissionProbe
{
    private const int WOk = 2;
    private const int XOk = 1;

    public static WriteAccess CheckWritableDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new WriteAccess(false, "folder not found (is the drive mounted in the container?)");
        }

        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()))
        {
            return new WriteAccess((new DirectoryInfo(directory).Attributes & FileAttributes.ReadOnly) == 0, "checked folder attributes only");
        }

        try
        {
            if (access(directory, WOk | XOk) == 0)
            {
                return new WriteAccess(true, "writable");
            }

            int errno = Marshal.GetLastPInvokeError();
            return new WriteAccess(false, errno switch
            {
                13 => "not writable (permission denied)",
                30 => "read-only file system",
                _ => "not writable (error " + errno + ")"
            });
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new WriteAccess(false, "could not be checked");
        }
    }

    public static string DescribeProcessUser()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            try
            {
                return $"uid {getuid()}, gid {getgid()}";
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }
        }

        return Environment.UserName;
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int access([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int mode);

    [DllImport("libc")]
    private static extern uint getuid();

    [DllImport("libc")]
    private static extern uint getgid();
}
