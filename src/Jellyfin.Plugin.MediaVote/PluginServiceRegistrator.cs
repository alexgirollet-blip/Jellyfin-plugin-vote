using System.IO;
using Jellyfin.Plugin.MediaVote.Configuration;
using Jellyfin.Plugin.MediaVote.Core;
using Jellyfin.Plugin.MediaVote.Integrations;
using Jellyfin.Plugin.MediaVote.Services;
using Jellyfin.Plugin.MediaVote.Storage;
using Jellyfin.Plugin.MediaVote.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaVote;

/// <summary>Registers Media Vote services with Jellyfin's dependency injection container.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// Data lives in &lt;Jellyfin data&gt;/mediavote (e.g. /config/data/mediavote in the linuxserver image),
    /// NOT in the versioned plugin folder, so it survives plugin updates.
    /// </summary>
    public static string DataDirectory(IApplicationPaths paths) => Path.Combine(paths.DataPath, "mediavote");

    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient("MediaVote");
        serviceCollection.AddSingleton<IClock, SystemClock>();
        serviceCollection.AddSingleton(sp => new JsonVoteStore(DataDirectory(sp.GetRequiredService<IApplicationPaths>())));
        serviceCollection.AddSingleton(sp => new FileAuditLog(DataDirectory(sp.GetRequiredService<IApplicationPaths>()), sp.GetRequiredService<IClock>()));
        serviceCollection.AddSingleton<JellyfinUserDirectory>();
        serviceCollection.AddSingleton<JellyfinMediaGateway>();
        serviceCollection.AddSingleton<DriveService>();
        serviceCollection.AddSingleton<LibraryCatalog>();
        serviceCollection.AddSingleton(sp => new ExternalServiceFactory(sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(), CurrentConfig));
        serviceCollection.AddSingleton(sp => new WebhookNotifier(
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
            CurrentConfig,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("MediaVote.Webhook")));
        serviceCollection.AddSingleton<ArrLibraryCache>();
        serviceCollection.AddSingleton<ITemplateStore>(sp => new JsonTemplateStore(DataDirectory(sp.GetRequiredService<IApplicationPaths>())));
        serviceCollection.AddSingleton<INotifier>(sp => sp.GetRequiredService<WebhookNotifier>());
        serviceCollection.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<ExternalServiceFactory>();
            return new VoteService(
                sp.GetRequiredService<JsonVoteStore>(),
                sp.GetRequiredService<JellyfinUserDirectory>(),
                sp.GetRequiredService<JellyfinMediaGateway>(),
                readOnly => factory.Create(readOnly),
                sp.GetRequiredService<WebhookNotifier>(),
                sp.GetRequiredService<FileAuditLog>(),
                sp.GetRequiredService<IClock>(),
                () => KillSwitch.IsActive,
                sp.GetRequiredService<DriveService>());
        });
        serviceCollection.AddSingleton<WebInjectionService>();
        serviceCollection.AddSingleton<EntryPointRegistrar>();
        serviceCollection.AddSingleton<ApiSelfCheck>();
        serviceCollection.AddHostedService<DeadlineService>();
    }

    private static PluginConfiguration CurrentConfig() => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
