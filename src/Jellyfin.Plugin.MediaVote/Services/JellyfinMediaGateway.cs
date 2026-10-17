using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaVote.Configuration;
using Jellyfin.Plugin.MediaVote.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaVote.Services;

/// <summary>
/// <see cref="IMediaGateway"/> using Jellyfin's library manager. Media Vote NEVER deletes, moves or changes media files:
/// on the user's exFAT drives Jellyfin cannot write at all, and Sonarr/Radarr do the deleting.
/// <para>
/// After Sonarr/Radarr removed a title, the stale Jellyfin entry is dropped with
/// <c>ILibraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false })</c>,
/// and only once none of the title's paths exists on disk any more. In Jellyfin 10.11.10 (LibraryManager.DeleteItem)
/// media files are deleted only when DeleteFileLocation is true, so this removes database rows and Jellyfin's own
/// cached metadata/images, never media. It is the same call Jellyfin's own library scan makes for a folder that has
/// disappeared (Folder.ValidateChildrenInternal2). If the files are still there, a refresh of the library is queued
/// instead (IProviderManager.QueueRefresh on the collection folder) and the check is repeated later.
/// </para>
/// </summary>
public sealed class JellyfinMediaGateway : IMediaGateway
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<JellyfinMediaGateway> _logger;

    public JellyfinMediaGateway(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ILogger<JellyfinMediaGateway> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public MediaItemSnapshot? GetItem(Guid itemId)
    {
        var item = JellyfinCall.Run("look up an item", () => _libraryManager.GetItemById(itemId));
        if (item is not (Movie or Series))
        {
            return null;
        }

        var paths = JellyfinCall.Run("work out the folder of " + item.Name, () => item.GetDeletePaths().Select(p => p.FullName).Distinct().ToList());
        var libraries = JellyfinCall.Run("find the library of " + item.Name, () => _libraryManager.GetCollectionFolders(item));
        var protectedLibs = new HashSet<string>(Config.ProtectedLibraryIds, StringComparer.OrdinalIgnoreCase);
        bool isProtected = Config.ProtectedItemIds.Contains(item.Id)
            || libraries.Any(l => protectedLibs.Contains(l.Id.ToString("N", CultureInfo.InvariantCulture)));

        return new MediaItemSnapshot
        {
            ItemId = item.Id,
            Name = item.Name ?? "?",
            Year = item.ProductionYear,
            Kind = item is Series ? MediaKind.Series : MediaKind.Movie,
            LibraryName = string.Join(", ", libraries.Select(l => l.Name)),
            DeletePaths = paths,
            SizeBytes = LibraryCatalog.SizeOnDisk(paths),
            ProviderIds = new Dictionary<string, string>(item.ProviderIds ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            IsProtected = isProtected
        };
    }

    public bool AnyPathExists(IReadOnlyCollection<string> paths)
        => paths.Any(p => !string.IsNullOrWhiteSpace(p) && (Directory.Exists(p) || File.Exists(p)));

    public CleanupResult CleanUpAfterRemoval(Guid itemId, IReadOnlyCollection<string> paths)
    {
        try
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                return new CleanupResult(JellyfinCleanup.Done, "Jellyfin no longer lists it.");
            }

            if (item is not (Movie or Series))
            {
                return new CleanupResult(JellyfinCleanup.Failed, "Not a movie or series; left for Jellyfin's library scan.");
            }

            // Use the item's own current paths as well as the ones recorded at the time of removal.
            var all = paths.Concat(item.GetDeletePaths().Select(p => p.FullName)).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
            if (all.Count == 0)
            {
                QueueRefresh(item);
                return new CleanupResult(JellyfinCleanup.Pending, "Jellyfin reports no folder for it; a library refresh was requested.");
            }

            if (AnyPathExists(all))
            {
                QueueRefresh(item);
                return new CleanupResult(JellyfinCleanup.Pending, "The files are still on disk (Sonarr/Radarr delete them in the background). Media Vote checks again shortly and then removes the Jellyfin entry; a library refresh was requested.");
            }

            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false });
            return new CleanupResult(JellyfinCleanup.Done, "Files are gone from disk; removed the Jellyfin entry (no files touched).");
        }
#pragma warning disable CA1031 // clean-up is best effort and must never throw into the resolution
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Media Vote: could not clean up Jellyfin entry {ItemId}", itemId);
            return new CleanupResult(JellyfinCleanup.Failed, "Jellyfin could not drop the entry (" + ErrorText.Describe(ex) + "). It disappears at the next library scan.");
        }
    }

    private void QueueRefresh(BaseItem item)
    {
        try
        {
            foreach (var folder in _libraryManager.GetCollectionFolders(item))
            {
                _providerManager.QueueRefresh(
                    folder.Id,
                    new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                    {
                        MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                        ImageRefreshMode = MetadataRefreshMode.ValidationOnly
                    },
                    RefreshPriority.Normal);
            }
        }
#pragma warning disable CA1031 // a refresh request is a convenience
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Media Vote: could not queue a library refresh");
        }
    }
}
