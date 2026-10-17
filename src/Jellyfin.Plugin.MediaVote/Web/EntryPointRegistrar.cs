using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.MediaVote.Configuration;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaVote.Web;

/// <summary>Detection / registration status of one optional integration.</summary>
public sealed record IntegrationStatus(string Name, bool Detected, string? Version, bool Enabled, bool Registered, string Message);

/// <summary>
/// Registers the optional entry points with other plugins, detected at runtime:
/// <list type="bullet">
/// <item>Plugin Pages: <c>PluginInterface.RegisterPage(JObject)</c> (in-memory; done at every start).</item>
/// <item>Home Screen Sections: <c>PluginInterface.RegisterSection(JObject)</c> with a results callback.</item>
/// <item>Custom Tabs: it has no registration API, but it stores its tabs in its plugin configuration and runs
///   scripts inside tab HTML. We append ONE tab marked with <c>data-mediavote-mount</c> (never touching other tabs)
///   and remove only that tab when the toggle is turned off.</item>
/// </list>
/// </summary>
public sealed class EntryPointRegistrar
{
    public const string PluginPagesId = "Jellyfin.Plugin.MediaVote";
    public const string SectionId = "MediaVote";
    public const string TabMarker = "data-mediavote-mount";
    public static readonly Guid CustomTabsPluginId = Guid.Parse("fbacd0b6-fd46-4a05-b0a4-2045d6a135b0");

    private readonly IPluginManager _pluginManager;
    private readonly ILogger<EntryPointRegistrar> _logger;
    private readonly Dictionary<string, IntegrationStatus> _status = new();
    private bool _pagesRegistered;
    private bool _sectionRegistered;

    public EntryPointRegistrar(IPluginManager pluginManager, ILogger<EntryPointRegistrar> logger)
    {
        _pluginManager = pluginManager;
        _logger = logger;
    }

    public IReadOnlyCollection<IntegrationStatus> Status
    {
        get
        {
            lock (_status)
            {
                return _status.Values.ToList();
            }
        }
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>The Custom Tabs tab content: a mount point plus a loader in case the injected script is missing.</summary>
    public static string TabHtml()
        => "<div " + TabMarker + "=\"tab\" class=\"mediavote-tab-host\"></div>"
           + "<script>(function(){if(window.MediaVote&&window.MediaVote.scan){window.MediaVote.scan();return;}"
           + "if(document.getElementById('mediavote-loader'))return;var s=document.createElement('script');s.id='mediavote-loader';"
           + "s.src=(window.ApiClient?window.ApiClient.getUrl('MediaVote/client.js'):'../MediaVote/client.js');document.head.appendChild(s);})();</script>";

    public void RegisterAll()
    {
        Safe("Plugin Pages", RegisterPluginPage);
        Safe("Home Screen Sections", RegisterHomeSection);
        Safe("Custom Tabs", SyncCustomTab);
    }

    /// <summary>Re-applies toggles after the admin saves settings.</summary>
    public void OnConfigurationChanged()
    {
        Safe("Custom Tabs", SyncCustomTab);
        if (!_pagesRegistered)
        {
            Safe("Plugin Pages", RegisterPluginPage);
        }

        if (!_sectionRegistered)
        {
            Safe("Home Screen Sections", RegisterHomeSection);
        }

        // Plugin Pages and the home section read the toggles live, nothing else to do.
        RefreshEnabledFlags();
    }

    private void RegisterPluginPage()
    {
        var asm = ReflectionBridge.FindAssembly("Jellyfin.Plugin.PluginPages");
        if (asm is null)
        {
            Set("Plugin Pages", false, null, Config.EnablePluginPage, false, "Not installed. The sidebar \"Vote\" page needs the Plugin Pages plugin; the standalone page /MediaVote/vote still works.");
            return;
        }

        var version = asm.GetName().Version?.ToString();
        if (_pagesRegistered)
        {
            return;
        }

        // Registered even when the toggle is off: IsEnabled is checked live by Plugin Pages, so toggling needs no restart.
        var error = ReflectionBridge.InvokeWithJObject(asm, "Jellyfin.Plugin.PluginPages.PluginInterface", "RegisterPage", new Dictionary<string, object?>
        {
            ["Id"] = PluginPagesId,
            ["Url"] = "/MediaVote/page",
            ["DisplayText"] = "Vote",
            ["Icon"] = "how_to_vote",
            ["Version"] = 1,
            ["IsEnabledAssembly"] = typeof(EntryPointRegistrar).Assembly.FullName,
            ["IsEnabledClass"] = nameof(PluginPagesCallbacks),
            ["IsEnabledMethod"] = nameof(PluginPagesCallbacks.IsMediaVotePageEnabled)
        });
        _pagesRegistered = error is null;
        Set("Plugin Pages", true, version, Config.EnablePluginPage, _pagesRegistered, error ?? "\"Vote\" page registered in the sidebar (under Plugin Settings).");
    }

    private void RegisterHomeSection()
    {
        var asm = ReflectionBridge.FindAssembly("Jellyfin.Plugin.HomeScreenSections");
        if (asm is null)
        {
            Set("Home Screen Sections", false, null, Config.EnableHomeSection, false, "Not installed (optional).");
            return;
        }

        var version = asm.GetName().Version?.ToString();
        if (_sectionRegistered)
        {
            return;
        }

        var error = ReflectionBridge.InvokeWithJObject(asm, "Jellyfin.Plugin.HomeScreenSections.PluginInterface", "RegisterSection", new Dictionary<string, object?>
        {
            ["id"] = SectionId,
            ["displayText"] = "Media Vote: current leaders",
            ["limit"] = 1,
            ["route"] = null,
            ["additionalData"] = null,
            ["resultsAssembly"] = typeof(EntryPointRegistrar).Assembly.FullName,
            ["resultsClass"] = typeof(HomeSectionResults).FullName,
            ["resultsMethod"] = nameof(HomeSectionResults.GetResults)
        });
        _sectionRegistered = error is null;
        Set("Home Screen Sections", true, version, Config.EnableHomeSection, _sectionRegistered, error ?? "Section \"Media Vote: current leaders\" registered. Enable it in Home Screen Sections' settings to show it.");
    }

    private void SyncCustomTab()
    {
        var plugin = _pluginManager.Plugins.FirstOrDefault(p => p.Id == CustomTabsPluginId)?.Instance as IHasPluginConfiguration;
        if (plugin is null)
        {
            Set("Custom Tabs", false, null, Config.EnableCustomTab, false, "Not installed. The \"Vote\" tab needs the Custom Tabs plugin.");
            return;
        }

        var version = _pluginManager.Plugins.First(p => p.Id == CustomTabsPluginId).Version?.ToString();
        var config = plugin.Configuration;
        var tabsProp = config.GetType().GetProperty("Tabs", BindingFlags.Public | BindingFlags.Instance);
        var elementType = tabsProp?.PropertyType.GetElementType();
        var titleProp = elementType?.GetProperty("Title");
        var htmlProp = elementType?.GetProperty("ContentHtml");
        if (tabsProp is null || elementType is null || titleProp is null || htmlProp is null || !tabsProp.CanWrite)
        {
            Set("Custom Tabs", true, version, Config.EnableCustomTab, false, "This Custom Tabs version stores tabs differently; add the tab manually (see the README).");
            return;
        }

        var current = ((IEnumerable?)tabsProp.GetValue(config))?.Cast<object>().ToList() ?? new List<object>();
        bool Ours(object t) => (htmlProp.GetValue(t) as string)?.Contains(TabMarker, StringComparison.Ordinal) == true;
        var existing = current.Where(Ours).ToList();

        if (Config.EnableCustomTab)
        {
            if (existing.Count == 1 && string.Equals(htmlProp.GetValue(existing[0]) as string, TabHtml(), StringComparison.Ordinal)
                && string.Equals(titleProp.GetValue(existing[0]) as string, Config.TabTitle, StringComparison.Ordinal))
            {
                Set("Custom Tabs", true, version, true, true, "\"" + Config.TabTitle + "\" tab present.");
                return;
            }

            var next = new List<object>();
            bool placed = false;
            foreach (var t in current)
            {
                if (!Ours(t))
                {
                    next.Add(t);
                }
                else if (!placed)
                {
                    // Keep our tab where it already is so other tabs' indices (customTab_N) do not shift.
                    titleProp.SetValue(t, Config.TabTitle);
                    htmlProp.SetValue(t, TabHtml());
                    next.Add(t);
                    placed = true;
                }
            }

            if (!placed)
            {
                var tab = Activator.CreateInstance(elementType)!;
                titleProp.SetValue(tab, Config.TabTitle);
                htmlProp.SetValue(tab, TabHtml());
                next.Add(tab);
            }

            Save(plugin, config, tabsProp, elementType, next);
            Set("Custom Tabs", true, version, true, true, "\"" + Config.TabTitle + "\" tab added to Custom Tabs (appended after your own tabs).");
        }
        else
        {
            if (existing.Count > 0)
            {
                Save(plugin, config, tabsProp, elementType, current.Where(t => !Ours(t)).ToList());
            }

            Set("Custom Tabs", true, version, false, false, "Vote tab disabled; only Media Vote's own tab was removed.");
        }
    }

    private static void Save(IHasPluginConfiguration plugin, object config, PropertyInfo tabsProp, Type elementType, List<object> tabs)
    {
        var array = Array.CreateInstance(elementType, tabs.Count);
        for (int i = 0; i < tabs.Count; i++)
        {
            array.SetValue(tabs[i], i);
        }

        tabsProp.SetValue(config, array);
        plugin.UpdateConfiguration((MediaBrowser.Model.Plugins.BasePluginConfiguration)config);
    }

    private void RefreshEnabledFlags()
    {
        lock (_status)
        {
            if (_status.TryGetValue("Plugin Pages", out var p))
            {
                _status["Plugin Pages"] = p with { Enabled = Config.EnablePluginPage };
            }

            if (_status.TryGetValue("Home Screen Sections", out var h))
            {
                _status["Home Screen Sections"] = h with { Enabled = Config.EnableHomeSection };
            }
        }
    }

    private void Set(string name, bool detected, string? version, bool enabled, bool registered, string message)
    {
        lock (_status)
        {
            _status[name] = new IntegrationStatus(name, detected, version, enabled, registered, message);
        }
    }

    private void Safe(string name, Action action)
    {
        try
        {
            action();
        }
#pragma warning disable CA1031 // an optional integration must never break the plugin
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Media Vote: {Integration} integration failed", name);
            Set(name, true, null, true, false, "Error: " + ex.Message);
        }
    }
}

/// <summary>Called by Plugin Pages (by reflection) to decide whether to show our sidebar page.</summary>
public static class PluginPagesCallbacks
{
    public static bool IsMediaVotePageEnabled(string id) => Plugin.Instance?.Configuration.EnablePluginPage ?? false;
}
