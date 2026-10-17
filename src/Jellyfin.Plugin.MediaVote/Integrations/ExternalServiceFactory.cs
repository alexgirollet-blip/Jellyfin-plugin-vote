using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Jellyfin.Plugin.MediaVote.Configuration;
using Jellyfin.Plugin.MediaVote.Core;
using Jellyfin.Plugin.MediaVote.Integrations.Arr;

namespace Jellyfin.Plugin.MediaVote.Integrations;

/// <summary>Builds the Radarr/Sonarr/Jellyseerr clients from the current configuration.</summary>
public sealed class ExternalServiceFactory
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly Func<PluginConfiguration> _config;

    public ExternalServiceFactory(IHttpClientFactory httpFactory, Func<PluginConfiguration> config)
    {
        _httpFactory = httpFactory;
        _config = config;
    }

    /// <summary>
    /// Every enabled instance. <paramref name="readOnly"/> = true for notify-only, dry runs, building a vote and the
    /// status page: such a backend can only send GET requests.
    /// </summary>
    public ArrRemovalBackend Create(bool readOnly)
    {
        var c = _config();
        var arr = new List<ArrClient>();
        foreach (var kind in new[] { ArrKind.Radarr, ArrKind.Sonarr })
        {
            arr.AddRange(UniqueNames(c.InstancesOf(kind)).Select(p => CreateArr(p.Instance, p.Name, readOnly)));
        }

        var seerr = UniqueNames(c.InstancesOf(ArrKind.Jellyseerr)).Select(p => new JellyseerrClient(Http(p.Instance, p.Name, readOnly), p.Name, c.JellyseerrAction)).ToList();
        return new ArrRemovalBackend(arr, seerr, readOnly);
    }

    /// <summary>A client for the connection test (always read-only).</summary>
    public ArrClient CreateArr(ArrInstanceConfig instance, string? name = null, bool readOnly = true)
    {
        var c = _config();
        bool exclusion = instance.Kind == ArrKind.Radarr ? c.RadarrAddImportExclusion : c.SonarrAddImportListExclusion;
        var n = name ?? instance.DisplayName;
        return new ArrClient(Http(instance, n, readOnly), instance.Kind, n, exclusion);
    }

    public JellyseerrClient CreateSeerr(ArrInstanceConfig instance)
        => new(Http(instance, instance.DisplayName, true), instance.DisplayName, _config().JellyseerrAction);

    /// <summary>Instance names must be unique so results can name the instance; duplicates get " (2)", " (3)".</summary>
    private static IEnumerable<(ArrInstanceConfig Instance, string Name)> UniqueNames(IEnumerable<ArrInstanceConfig> instances)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in instances)
        {
            var name = i.DisplayName;
            int n = seen.GetValueOrDefault(name) + 1;
            seen[name] = n;
            yield return (i, n == 1 ? name : name + " (" + n + ")");
        }
    }

    private ArrHttp Http(ArrInstanceConfig instance, string name, bool readOnly)
        => new(_httpFactory.CreateClient("MediaVote"), name, instance.Url, instance.ApiKey, readOnly);
}
