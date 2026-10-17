using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaVote.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MediaVote;

/// <summary>Media Vote: let users vote on which movies and shows to remove.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "1ffa7b73-334b-4a42-b6c8-0f7b48457bb8";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        if (Configuration.Normalize())
        {
            SaveConfiguration();
        }
    }

    /// <summary>Raised after the configuration changes (entry points may need re-registering).</summary>
    public event EventHandler? ConfigurationSaved;

    public static Plugin? Instance { get; private set; }

    public override string Name => "Media Vote";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description => "Let selected users vote on which movies and shows to remove from the server.";

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration c)
        {
            c.Normalize();
        }

        base.UpdateConfiguration(configuration);
        ConfigurationSaved?.Invoke(this, EventArgs.Empty);
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return new[]
        {
            new PluginPageInfo
            {
                Name = "MediaVote",
                DisplayName = "Media Vote",
                EmbeddedResourcePath = ns + ".Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuIcon = "how_to_vote"
            }
        };
    }
}
