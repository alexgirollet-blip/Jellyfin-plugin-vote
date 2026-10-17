using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.MediaVote.Configuration;
using Jellyfin.Plugin.MediaVote.Core;
using Jellyfin.Plugin.MediaVote.Services;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaVote.Web;

/// <summary>Detection / registration status of one optional integration.</summary>
public sealed record IntegrationStatus(string Name, bool Detected, string? Version, bool Enabled, bool Registered, string Message);

/// <summary>
/// Optional entry points, detected at runtime. Media Vote NEVER writes to another plugin's saved settings.
/// <list type="bullet">
/// <item>Plugin Pages: <c>PluginInterface.RegisterPage(JObject)</c> / <c>RemovePage(string)</c>, in memory only,
///   and only while the "sidebar page" entry point is on.</item>
/// <item>Home Screen Sections: <c>PluginInterface.RegisterSection(JObject)</c>, in memory only, only while the
///   section entry point is on (HSS has no removal API; the results callback returns nothing when disabled).</item>
/// <item>Custom Tabs: read-only. The admin adds the Vote tab by hand with <see cref="CustomTabsInspector.Snippet"/>;
///   saved tabs are inspected to warn about the 1.0.0.0 leftover and other tabs that would break the home page.</item>
/// </list>
/// </summary>
public sealed class EntryPointRegistrar
{
    public const string PluginPagesId = "Jellyfin.Plugin.MediaVote";
    public const string SectionId = "MediaVote";
    public static readonly Guid CustomTabsPluginId = Guid.Parse("fbacd0b6-fd46-4a05-b0a4-2045d6a135b0");

    private readonly IPluginManager _pluginManager;
    private readonly ILogger<EntryPointRegistrar> _logger;
    private readonly Dictionary<string, IntegrationStatus> _status = new();
    private readonly object _lock = new();
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
            lock (_lock)
            {
                return _status.Values.ToList();
            }
        }
    }

    /// <summary>Latest read-only findings about Custom Tabs' saved tabs.</summary>
    public IReadOnlyList<CustomTabFinding> CustomTabFindings { get; private set; } = Array.Empty<CustomTabFinding>();

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Applies the current settings. Never throws.</summary>
    public void Apply()
    {
        Safe("Custom Tabs", InspectCustomTabs);
        if (KillSwitch.IsActive)
        {
            Safe("Plugin Pages", RemovePluginPage);
            Set("Plugin Pages", ReflectionBridge.FindAssembly("Jellyfin.Plugin.PluginPages") is not null, null, Config.EnablePluginPage, false, "Disabled by file: nothing registered.");
            Set("Home Screen Sections", ReflectionBridge.FindAssembly("Jellyfin.Plugin.HomeScreenSections") is not null, null, Config.EnableHomeSection, _sectionRegistered, "Disabled by file: the section shows nothing.");
            return;
        }

        Safe("Plugin Pages", SyncPluginPage);
        Safe("Home Screen Sections", SyncHomeSection);
    }

    /// <summary>Removes everything Media Vote registered with other plugins (uninstall). Never throws.</summary>
    public void UnregisterAll()
    {
        Safe("Plugin Pages", RemovePluginPage);
    }

    /// <summary>Re-reads Custom Tabs' saved tabs (read-only).</summary>
    public void InspectCustomTabs()
    {
        var local = _pluginManager.Plugins.FirstOrDefault(p => p.Id == CustomTabsPluginId);
        if (local?.Instance is not IHasPluginConfiguration plugin)
        {
            CustomTabFindings = Array.Empty<CustomTabFinding>();
            Set("Custom Tabs", false, null, Config.EnableCustomTab, false, "Not installed. The Vote tab needs the Custom Tabs plugin; the standalone page works without it.");
            return;
        }

        var version = local.Version;
        var tabs = ReadTabs(plugin.Configuration);
        CustomTabFindings = tabs is null
            ? Array.Empty<CustomTabFinding>()
            : CustomTabsInspector.Inspect(tabs, version);
        bool hasOurTab = CustomTabFindings.Any(f => f.Kind == "MediaVoteTab");
        string message = tabs is null
            ? "Installed, but its saved tabs could not be read (different version). Media Vote does not change Custom Tabs; add the tab by hand."
            : CustomTabsInspector.NeedsAction(CustomTabFindings)
                ? "ACTION NEEDED: see the Custom Tabs warnings below."
                : hasOurTab ? "A Vote tab with the safe snippet is present." : "No Vote tab yet. Add one by hand with the snippet below if you want it.";
        Set("Custom Tabs", true, version?.ToString(), Config.EnableCustomTab, hasOurTab, message);
        foreach (var f in CustomTabFindings.Where(f => f.Kind != "MediaVoteTab"))
        {
            _logger.LogWarning("Media Vote: Custom Tabs check: {Message}", f.Message);
        }
    }

    /// <summary>Reads Custom Tabs' configuration <c>Tabs[]</c> (Title, ContentHtml) by reflection. Read-only.</summary>
    internal static List<CustomTabInfo>? ReadTabs(object? configuration)
    {
        if (configuration is null)
        {
            return null;
        }

        var tabsProp = configuration.GetType().GetProperty("Tabs", BindingFlags.Public | BindingFlags.Instance);
        if (tabsProp?.GetValue(configuration) is not IEnumerable tabs)
        {
            return null;
        }

        var list = new List<CustomTabInfo>();
        int i = 0;
        foreach (var t in tabs)
        {
            if (t is null)
            {
                i++;
                continue;
            }

            var type = t.GetType();
            var title = type.GetProperty("Title")?.GetValue(t) as string ?? string.Empty;
            var html = type.GetProperty("ContentHtml")?.GetValue(t) as string ?? string.Empty;
            list.Add(new CustomTabInfo(i++, title, html));
        }

        return list;
    }

    private void SyncPluginPage()
    {
        var asm = ReflectionBridge.FindAssembly("Jellyfin.Plugin.PluginPages");
        var cfg = Config;
        if (asm is null)
        {
            Set("Plugin Pages", false, null, cfg.EnablePluginPage, false, "Not installed (optional). The standalone page /MediaVote/vote works without it.");
            return;
        }

        var version = asm.GetName().Version?.ToString();
        if (!cfg.EnablePluginPage)
        {
            RemovePluginPage();
            Set("Plugin Pages", true, version, false, false, "Off. Turn on \"Vote page in the sidebar\" to register it.");
            return;
        }

        if (!_pagesRegistered)
        {
            var error = ReflectionBridge.InvokeWithJObject(asm, "Jellyfin.Plugin.PluginPages.PluginInterface", "RegisterPage", new Dictionary<string, object?>
            {
                ["Id"] = PluginPagesId,
                ["Url"] = "/MediaVote/page",
                ["DisplayText"] = "Vote",
                ["Icon"] = "how_to_vote",
                ["IsEnabledAssembly"] = typeof(EntryPointRegistrar).Assembly.FullName,
                ["IsEnabledClass"] = nameof(PluginPagesCallbacks),
                ["IsEnabledMethod"] = nameof(PluginPagesCallbacks.IsMediaVotePageEnabled)
            });
            _pagesRegistered = error is null;
            if (error is not null)
            {
                Set("Plugin Pages", true, version, true, false, "Registration failed: " + error);
                return;
            }
        }

        Set("Plugin Pages", true, version, true, true, cfg.AdminPreviewOnly
            ? "Registered, but hidden from everyone while preview mode is on (Plugin Pages cannot show a page to admins only). Test it at #/userpluginsettings.html?pageUrl=/MediaVote/page."
            : "\"Vote\" page shown in the sidebar under Plugin Settings.");
    }

    private void RemovePluginPage()
    {
        if (!_pagesRegistered)
        {
            return;
        }

        var asm = ReflectionBridge.FindAssembly("Jellyfin.Plugin.PluginPages");
        var error = asm is null ? null : ReflectionBridge.InvokeStatic(asm, "Jellyfin.Plugin.PluginPages.PluginInterface", "RemovePage", PluginPagesId);
        if (error is null)
        {
            _pagesRegistered = false;
        }
        else
        {
            _logger.LogWarning("Media Vote: could not remove the Plugin Pages entry: {Error}", error);
        }
    }

    private void SyncHomeSection()
    {
        var asm = ReflectionBridge.FindAssembly("Jellyfin.Plugin.HomeScreenSections");
        var cfg = Config;
        if (asm is null)
        {
            Set("Home Screen Sections", false, null, cfg.EnableHomeSection, false, "Not installed (optional).");
            return;
        }

        var version = asm.GetName().Version?.ToString();
        if (!cfg.EnableHomeSection)
        {
            Set("Home Screen Sections", true, version, false, _sectionRegistered, _sectionRegistered ? "Off: the registered section now returns nothing." : "Off.");
            return;
        }

        if (!_sectionRegistered)
        {
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
            if (error is not null)
            {
                Set("Home Screen Sections", true, version, true, false, "Registration failed: " + error);
                return;
            }
        }

        Set("Home Screen Sections", true, version, true, true, "Section registered. Also enable \"Media Vote: current leaders\" in Home Screen Sections' own settings." + (cfg.AdminPreviewOnly ? " Preview mode: only admins see results." : string.Empty));
    }

    private void Set(string name, bool detected, string? version, bool enabled, bool registered, string message)
    {
        lock (_lock)
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
            Set(name, true, null, false, false, "Error: " + ex.Message);
        }
    }
}

/// <summary>Called by Plugin Pages (by reflection) to decide whether to show our sidebar page. Never throws.</summary>
public static class PluginPagesCallbacks
{
    public static bool IsMediaVotePageEnabled(string id)
    {
        try
        {
            var c = Plugin.Instance?.Configuration;
            // Plugin Pages asks without a user, so in preview mode the page is hidden from everyone.
            return c is not null && AccessRules.ShowSidebarPage(c.EnablePluginPage, c.AdminPreviewOnly, KillSwitch.IsActive);
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
