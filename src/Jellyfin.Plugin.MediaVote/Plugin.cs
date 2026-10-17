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
        Services.KillSwitch.Initialise(System.IO.Path.Combine(applicationPaths.DataPath, "mediavote"));
        try
        {
            bool migrated = Configuration.Migrate();
            if (Configuration.Normalize() | migrated)
            {
                SaveConfiguration();
            }
        }
#pragma warning disable CA1031 // a configuration problem must never stop the plugin (or the server) from loading
        catch (Exception)
#pragma warning restore CA1031
        {
            // The in-memory configuration still has safe defaults; saving is retried when the admin saves settings.
        }
    }

    /// <summary>Raised after the configuration changes (entry points may need re-registering).</summary>
    public event EventHandler? ConfigurationSaved;

    public static Plugin? Instance { get; private set; }

    /// <summary>Cleanup actions registered at start-up (remove File Transformation / Plugin Pages registrations).</summary>
    internal static List<Action> UninstallHooks { get; } = new();

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

    /// <summary>
    /// Called by Jellyfin before the plugin is removed. Removes what Media Vote registered in memory with other
    /// plugins. Media Vote never writes other plugins' saved settings, so there is nothing else to undo there.
    /// Vote data in &lt;data&gt;/mediavote and the plugin's own configuration file are kept (see README).
    /// </summary>
    public override void OnUninstalling()
    {
        foreach (var hook in UninstallHooks.ToArray())
        {
            try
            {
                hook();
            }
#pragma warning disable CA1031 // uninstall must always complete
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        base.OnUninstalling();
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
