using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaVote.Configuration;

/// <summary>Action for Radarr / Sonarr when a winning item is deleted.</summary>
public enum ArrAction
{
    /// <summary>Set the movie/series to unmonitored so it is not downloaded again (default).</summary>
    Unmonitor = 0,

    /// <summary>Remove from the app and add an import (list) exclusion. Files are NOT deleted by the app; Jellyfin deletes them.</summary>
    RemoveAndExclude = 1,

    /// <summary>Do nothing in this app.</summary>
    Nothing = 2
}

/// <summary>Action for Jellyseerr when a winning item is deleted.</summary>
public enum SeerrAction
{
    /// <summary>Leave Jellyseerr alone (default).</summary>
    Nothing = 0,

    /// <summary>Delete the Jellyseerr media record (and with it its requests) so the title can be requested again later.</summary>
    RemoveMediaRecord = 1
}

/// <summary>Webhook payload style.</summary>
public enum WebhookKind
{
    Discord = 0,
    Generic = 1
}

/// <summary>
/// Plugin configuration (stored by Jellyfin as XML in the plugin configurations folder).
/// API keys live only here. They are never logged and never sent back to the browser in full.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Current configuration schema version. See <see cref="Migrate"/>.</summary>
    public const int CurrentConfigVersion = 2;

    /// <summary>Schema version of this saved configuration (0 = written by 1.0.0.0 or new).</summary>
    public int ConfigVersion { get; set; }

    // ---------------- entry points (staged rollout: all optional ones OFF by default) ----------------

    public bool EnableBanner { get; set; }

    /// <summary>
    /// Whether the admin has added the Vote tab to Custom Tabs by hand (Media Vote never writes to Custom Tabs).
    /// Only controls whether the injected script is needed for the tab's mount element.
    /// </summary>
    public bool EnableCustomTab { get; set; }

    public bool EnablePluginPage { get; set; }

    public bool EnableHomeSection { get; set; }

    /// <summary>The standalone page (/MediaVote/vote) needs no other plugin and injects nothing, so it is on by default.</summary>
    public bool EnableStandalonePage { get; set; } = true;

    /// <summary>
    /// Preview mode (ON by default): every entry point renders nothing for non-administrators, and the
    /// server refuses non-admin voting calls, so friends see nothing while the admin tests. "Go live" turns it off.
    /// </summary>
    public bool AdminPreviewOnly { get; set; } = true;

    public string TabTitle { get; set; } = "Vote";

    // ---------------- general ----------------

    /// <summary>IANA time zone used to interpret deadlines entered by the admin.</summary>
    public string TimeZoneId { get; set; } = "Europe/London";

    /// <summary>Hours before the deadline when the "closing soon" reminder starts.</summary>
    public int ClosingSoonHours { get; set; } = 24;

    /// <summary>Days the result announcement banner stays visible to voters.</summary>
    public int ResultBannerDays { get; set; } = 7;

    /// <summary>Use the Playback Reporting plugin's database (if installed) to enrich play statistics.</summary>
    public bool UsePlaybackReporting { get; set; } = true;

    /// <summary>Items that can never be voted on or deleted by a vote.</summary>
    public Guid[] ProtectedItemIds { get; set; } = Array.Empty<Guid>();

    /// <summary>Libraries (collection folder ids) whose items can never be voted on or deleted by a vote.</summary>
    public string[] ProtectedLibraryIds { get; set; } = Array.Empty<string>();

    // ---------------- Radarr ----------------

    public string RadarrUrl { get; set; } = string.Empty;

    public string RadarrApiKey { get; set; } = string.Empty;

    public ArrAction RadarrAction { get; set; } = ArrAction.Unmonitor;

    // ---------------- Sonarr ----------------

    public string SonarrUrl { get; set; } = string.Empty;

    public string SonarrApiKey { get; set; } = string.Empty;

    public ArrAction SonarrAction { get; set; } = ArrAction.Unmonitor;

    // ---------------- Jellyseerr ----------------

    public string JellyseerrUrl { get; set; } = string.Empty;

    public string JellyseerrApiKey { get; set; } = string.Empty;

    public SeerrAction JellyseerrAction { get; set; } = SeerrAction.Nothing;

    // ---------------- webhook ----------------

    public string WebhookUrl { get; set; } = string.Empty;

    public WebhookKind WebhookKind { get; set; } = WebhookKind.Discord;

    public bool WebhookOnOpened { get; set; } = true;

    public bool WebhookOnClosingSoon { get; set; } = true;

    public bool WebhookOnResults { get; set; } = true;

    /// <summary>Optional public URL of the server, used for links in webhook messages.</summary>
    public string PublicServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Upgrades a configuration saved by an older version. 1.0.0.0 defaulted every entry point (including the
    /// automatic Custom Tabs write) to ON; on upgrade all optional entry points are switched OFF and preview mode
    /// is switched ON, so an upgrade can never re-enable anything risky by itself.
    /// </summary>
    /// <returns>True if the configuration was changed.</returns>
    public bool Migrate()
    {
        if (ConfigVersion >= CurrentConfigVersion)
        {
            return false;
        }

        EnableBanner = false;
        EnableCustomTab = false;
        EnablePluginPage = false;
        EnableHomeSection = false;
        AdminPreviewOnly = true;
        ConfigVersion = CurrentConfigVersion;
        return true;
    }

    /// <summary>Normalises values. Every entry point may be off: the admin page and the standalone page always work.</summary>
    /// <returns>True if something was corrected.</returns>
    public bool Normalize()
    {
        bool changed = false;
        if (string.IsNullOrWhiteSpace(TabTitle))
        {
            TabTitle = "Vote";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(TimeZoneId))
        {
            TimeZoneId = "Europe/London";
            changed = true;
        }

        ClosingSoonHours = Math.Clamp(ClosingSoonHours, 0, 24 * 14);
        ResultBannerDays = Math.Clamp(ResultBannerDays, 0, 365);
        ProtectedItemIds ??= Array.Empty<Guid>();
        ProtectedLibraryIds ??= Array.Empty<string>();
        return changed;
    }
}
