using System;
using System.Collections.Generic;
using System.Net.Http;
using Jellyfin.Plugin.MediaVote.Configuration;
using Jellyfin.Plugin.MediaVote.Core;
using Jellyfin.Plugin.MediaVote.Integrations.Arr;

namespace Jellyfin.Plugin.MediaVote.Integrations;

/// <summary>Builds the Radarr/Sonarr/Jellyseerr services from the current configuration, in execution order.</summary>
public sealed class ExternalServiceFactory
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly Func<PluginConfiguration> _config;

    public ExternalServiceFactory(IHttpClientFactory httpFactory, Func<PluginConfiguration> config)
    {
        _httpFactory = httpFactory;
        _config = config;
    }

    /// <summary>Radarr, Sonarr, then Jellyseerr: the *arr step always runs before Jellyseerr.</summary>
    public IReadOnlyList<IExternalService> Create()
        => new IExternalService[] { CreateRadarr(), CreateSonarr(), CreateJellyseerr() };

    public RadarrService CreateRadarr(string? url = null, string? key = null)
    {
        var c = _config();
        return new RadarrService(new ArrHttp(Client(), "Radarr", url ?? c.RadarrUrl, key ?? c.RadarrApiKey), c.RadarrAction);
    }

    public SonarrService CreateSonarr(string? url = null, string? key = null)
    {
        var c = _config();
        return new SonarrService(new ArrHttp(Client(), "Sonarr", url ?? c.SonarrUrl, key ?? c.SonarrApiKey), c.SonarrAction);
    }

    public JellyseerrService CreateJellyseerr(string? url = null, string? key = null)
    {
        var c = _config();
        return new JellyseerrService(new ArrHttp(Client(), "Jellyseerr", url ?? c.JellyseerrUrl, key ?? c.JellyseerrApiKey), c.JellyseerrAction);
    }

    private HttpClient Client() => _httpFactory.CreateClient("MediaVote");
}
