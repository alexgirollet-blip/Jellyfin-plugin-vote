using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaVote.Core;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaVote.Configuration;

/// <summary>One Radarr, Sonarr or Jellyseerr server. Several of each are allowed (for example a separate Anime Sonarr).</summary>
public class ArrInstanceConfig
{
    /// <summary>Stable id used by the admin page to keep the masked key when editing.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public ArrKind Kind { get; set; } = ArrKind.Sonarr;

    /// <summary>Friendly name shown in results, for example "Sonarr Anime".</summary>
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>API key: stored only here, never logged, sent to the browser masked.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Kind.ToString() : Name.Trim();
}

/// <summary>Action for Jellyseerr when a winning item is deleted.</summary>
public enum SeerrAction
{
    /// <summary>Leave Jellyseerr alone (default).</summary>
    Nothing = 0,

    /// <summary>Delete the Jellyseerr media record (and with it its requests) so the title can be requested again later.</summary>
    RemoveMediaRecord = 1
}

/// <summary>A friendly name for a drive, keyed by one of its mount points (for example "/LM1" = "8TB 1").</summary>
public class DriveNameConfig
{
    public string MountPoint { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

/// <summary>Watch details voters may see on title cards.</summary>
public enum WatchVisibility
{
    /// <summary>Who played it last and when, play count and viewers (default).</summary>
    NamesAndTime = 0,

    /// <summary>Play count and number of viewers only.</summary>
    CountsOnly = 1,

    /// <summary>Nothing about watching.</summary>
    Hidden = 2
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
    public const int CurrentConfigVersion = 3;

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

    // ---------------- Sonarr / Radarr / Jellyseerr ----------------

    /// <summary>
    /// Every configured Radarr, Sonarr and Jellyseerr server. Media Vote never deletes media files itself: with
    /// "Delete via Sonarr/Radarr" it asks the instance that manages a title to delete it (deleteFiles=true).
    /// </summary>
    public ArrInstanceConfig[] ArrInstances { get; set; } = Array.Empty<ArrInstanceConfig>();

    /// <summary>Radarr: add an import exclusion when deleting, so lists do not add the movie again (default on).</summary>
    public bool RadarrAddImportExclusion { get; set; } = true;

    /// <summary>Sonarr: add an import list exclusion when deleting, so lists do not add the series again (default on).</summary>
    public bool SonarrAddImportListExclusion { get; set; } = true;

    public SeerrAction JellyseerrAction { get; set; } = SeerrAction.Nothing;

    /// <summary>Most titles one vote may remove through Sonarr/Radarr (safety cap, 1–100).</summary>
    public int MaxRemovalsPerVote { get; set; } = 5;

    // ---------------- drives and title cards ----------------

    /// <summary>Friendly drive names set by the admin. Unnamed drives use the defaults (/LM1 = "8TB 1", /LM2 = "8TB 2", /Media = "System drive") or their mount point.</summary>
    public DriveNameConfig[] DriveNames { get; set; } = Array.Empty<DriveNameConfig>();

    /// <summary>Drive bar turns amber above this percentage.</summary>
    public int DriveWarnPercent { get; set; } = 85;

    /// <summary>Drive bar turns red above this percentage.</summary>
    public int DriveCriticalPercent { get; set; } = 95;

    /// <summary>Watch details voters see on title cards (admins always see everything).</summary>
    public WatchVisibility WatchDetailsForVoters { get; set; } = WatchVisibility.NamesAndTime;

    /// <summary>1.0.x single-instance fields, read only to migrate them into <see cref="ArrInstances"/>, then cleared.</summary>
    public string RadarrUrl { get; set; } = string.Empty;

    /// <summary>1.0.x, migrated.</summary>
    public string RadarrApiKey { get; set; } = string.Empty;

    /// <summary>1.0.x, migrated.</summary>
    public string SonarrUrl { get; set; } = string.Empty;

    /// <summary>1.0.x, migrated.</summary>
    public string SonarrApiKey { get; set; } = string.Empty;

    /// <summary>1.0.x, migrated.</summary>
    public string JellyseerrUrl { get; set; } = string.Empty;

    /// <summary>1.0.x, migrated.</summary>
    public string JellyseerrApiKey { get; set; } = string.Empty;

    /// <summary>Enabled instances of one kind with a URL and key.</summary>
    public IReadOnlyList<ArrInstanceConfig> InstancesOf(ArrKind kind)
        => (ArrInstances ?? Array.Empty<ArrInstanceConfig>())
            .Where(i => i is not null && i.Kind == kind && i.Enabled && !string.IsNullOrWhiteSpace(i.Url) && !string.IsNullOrWhiteSpace(i.ApiKey))
            .ToList();

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

        if (ConfigVersion < 2)
        {
            EnableBanner = false;
            EnableCustomTab = false;
            EnablePluginPage = false;
            EnableHomeSection = false;
            AdminPreviewOnly = true;
        }

        if (ConfigVersion < 3)
        {
            // 1.0.x had one URL/key per app; keep them as the first instance of each kind.
            var list = (ArrInstances ?? Array.Empty<ArrInstanceConfig>()).ToList();
            void Move(ArrKind kind, string url, string key)
            {
                if (!string.IsNullOrWhiteSpace(url) && !list.Any(i => i.Kind == kind && string.Equals(i.Url, url, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(new ArrInstanceConfig { Kind = kind, Name = kind.ToString(), Url = url.Trim(), ApiKey = key?.Trim() ?? string.Empty });
                }
            }

            Move(ArrKind.Radarr, RadarrUrl, RadarrApiKey);
            Move(ArrKind.Sonarr, SonarrUrl, SonarrApiKey);
            Move(ArrKind.Jellyseerr, JellyseerrUrl, JellyseerrApiKey);
            ArrInstances = list.ToArray();
            RadarrUrl = RadarrApiKey = SonarrUrl = SonarrApiKey = JellyseerrUrl = JellyseerrApiKey = string.Empty;
        }

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
        ArrInstances = (ArrInstances ?? Array.Empty<ArrInstanceConfig>()).Where(i => i is not null).ToArray();
        MaxRemovalsPerVote = Math.Clamp(MaxRemovalsPerVote, 1, 100);
        DriveNames = (DriveNames ?? Array.Empty<DriveNameConfig>()).Where(d => d is not null && !string.IsNullOrWhiteSpace(d.MountPoint)).ToArray();
        DriveWarnPercent = Math.Clamp(DriveWarnPercent, 1, 99);
        DriveCriticalPercent = Math.Clamp(DriveCriticalPercent, DriveWarnPercent, 100);
        return changed;
    }
}
