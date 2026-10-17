using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.MediaVote.Core;

namespace Jellyfin.Plugin.MediaVote.Services;

/// <summary>
/// Checks whether the Jellyfin process can delete a path, WITHOUT writing anything.
/// On Linux it uses access(2) with W_OK|X_OK, which honours the effective uid ("abc" in the
/// linuxserver image), group membership and read-only mounts. Deleting a folder tree needs
/// write+execute on its parent and on every folder inside it.
/// </summary>
public static class PermissionProbe
{
    private const int WOk = 2;
    private const int XOk = 1;
    private const int MaxDirectories = 20000;

    public static PermissionCheck CheckDeletable(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var r = CheckOne(path);
            if (!r.Ok)
            {
                return r;
            }
        }

        return PermissionCheck.Allowed;
    }

    /// <summary>Checks whether a folder is writable (used for index.html fallback and diagnostics).</summary>
    public static PermissionCheck CheckWritableDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new PermissionCheck(false, $"Folder does not exist: {directory}", directory);
        }

        return Writable(directory);
    }

    /// <summary>Checks whether an existing file can be overwritten.</summary>
    public static PermissionCheck CheckWritableFile(string file)
    {
        if (!File.Exists(file))
        {
            return new PermissionCheck(false, $"File does not exist: {file}", file);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            return AccessCheck(file, WOk);
        }

        return (File.GetAttributes(file) & FileAttributes.ReadOnly) != 0
            ? new PermissionCheck(false, $"{file} is read-only.", file)
            : PermissionCheck.Allowed;
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

    private static PermissionCheck CheckOne(string path)
    {
        bool isDir = Directory.Exists(path);
        bool isFile = !isDir && File.Exists(path);
        if (!isDir && !isFile)
        {
            return new PermissionCheck(false, $"Path not found on disk: {path}. Is the drive mounted inside the container?", path);
        }

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        if (parent is not null)
        {
            var p = Writable(parent);
            if (!p.Ok)
            {
                return p;
            }
        }

        if (!isDir)
        {
            return PermissionCheck.Allowed;
        }

        // Every folder inside the tree must be writable for a recursive delete to succeed.
        int count = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var w = Writable(dir);
            if (!w.Ok)
            {
                return w;
            }

            if (++count > MaxDirectories)
            {
                break;
            }

            IEnumerable<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
            }
            catch (UnauthorizedAccessException)
            {
                return Denied(dir, "cannot list folder contents");
            }

            foreach (var sub in subdirs)
            {
                // Do not follow symlinks out of the tree.
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0)
                {
                    stack.Push(sub);
                }
            }
        }

        return PermissionCheck.Allowed;
    }

    private static PermissionCheck Writable(string directory)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            return AccessCheck(directory, WOk | XOk);
        }

        // Windows fallback: create and remove a probe file.
        try
        {
            var probe = Path.Combine(directory, ".mediavote-probe-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return PermissionCheck.Allowed;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Denied(directory, ex.Message);
        }
    }

    private static PermissionCheck AccessCheck(string path, int mode)
    {
        try
        {
            if (access(path, mode) == 0)
            {
                return PermissionCheck.Allowed;
            }

            int errno = Marshal.GetLastPInvokeError();
            string reason = errno switch
            {
                13 => "permission denied",
                30 => "read-only file system (check the Docker volume is not mounted :ro)",
                2 => "not found",
                _ => "error " + errno
            };
            return Denied(path, reason);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return PermissionCheck.Allowed; // cannot check; Jellyfin's own delete will still report failures
        }
    }

    private static PermissionCheck Denied(string path, string reason)
        => new(
            false,
            $"Jellyfin ({DescribeProcessUser()}) cannot write to \"{path}\": {reason}. In the linuxserver image the process runs as user \"abc\" (PUID/PGID); make the media folder writable by that user.",
            path);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int access([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int mode);

    [DllImport("libc")]
    private static extern uint getuid();

    [DllImport("libc")]
    private static extern uint getgid();
}
