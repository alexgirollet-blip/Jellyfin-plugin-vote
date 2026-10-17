using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaVote.Core;

namespace Jellyfin.Plugin.MediaVote.Integrations;

/// <summary>
/// Short-lived cache of the Radarr/Sonarr libraries (read-only GETs) for the admin page's "removable" badges, so typing
/// in a search box does not list every movie and series again. Resolution never uses this cache: it always reads fresh.
/// </summary>
public sealed class ArrLibraryCache : IDisposable
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);
    private readonly ExternalServiceFactory _factory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (DateTime At, ArrLibrary Library)? _cached;

    public ArrLibraryCache(ExternalServiceFactory factory) => _factory = factory;

    public void Invalidate() => _cached = null;

    public void Dispose() => _lock.Dispose();

    public async Task<ArrLibrary> GetAsync(CancellationToken ct)
    {
        var c = _cached;
        if (c is not null && DateTime.UtcNow - c.Value.At < MaxAge)
        {
            return c.Value.Library;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            c = _cached;
            if (c is not null && DateTime.UtcNow - c.Value.At < MaxAge)
            {
                return c.Value.Library;
            }

            var lib = await _factory.Create(readOnly: true).LoadAsync(ct).ConfigureAwait(false);
            _cached = (DateTime.UtcNow, lib);
            return lib;
        }
        finally
        {
            _lock.Release();
        }
    }
}
