using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.MediaVote.Configuration;
using Jellyfin.Plugin.MediaVote.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MediaVote.Services;

/// <summary>
/// <see cref="IMediaGateway"/> using Jellyfin's library manager. Deletion uses
/// <c>ILibraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true)</c>,
/// the same call Jellyfin's own "Delete media" action makes, so the item, its children
/// (seasons/episodes) and its folder on disk are removed properly.
/// </summary>
public sealed class JellyfinMediaGateway : IMediaGateway
{
    private readonly ILibraryManager _libraryManager;

    public JellyfinMediaGateway(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public MediaItemSnapshot? GetItem(Guid itemId)
    {
        var item = JellyfinCall.Run("look up an item", () => _libraryManager.GetItemById(itemId));
        if (item is not (Movie or Series))
        {
            return null;
        }

        var paths = JellyfinCall.Run("work out the files of " + item.Name, () => item.GetDeletePaths().Select(p => p.FullName).Distinct().ToList());
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

    public PermissionCheck CheckDeletePermission(MediaItemSnapshot item)
    {
        if (item.DeletePaths.Count == 0)
        {
            return new PermissionCheck(false, "Jellyfin reports no file location for this item, so its files could not be deleted.");
        }

        return PermissionProbe.CheckDeletable(item.DeletePaths);
    }

    public void Delete(Guid itemId)
    {
        var item = JellyfinCall.Run("look up an item", () => _libraryManager.GetItemById(itemId)) ?? throw new InvalidOperationException("Item no longer exists.");
        if (item is not (Movie or Series))
        {
            throw new InvalidOperationException("Only whole movies and series can be deleted by a vote.");
        }

        JellyfinCall.Run("delete " + item.Name, () =>
        {
            _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
            return true;
        });
    }
}
