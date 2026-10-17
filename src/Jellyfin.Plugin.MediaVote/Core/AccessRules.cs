namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>
/// Who may see and use Media Vote right now. One place for the safety rules, used by the API,
/// the Home Screen Sections callback and the Plugin Pages callback.
/// </summary>
public static class AccessRules
{
    /// <summary>
    /// True when Media Vote must render nothing for this user: kill switch active, or preview mode and the
    /// user is not an administrator.
    /// </summary>
    public static bool IsHiddenFor(bool killSwitchActive, bool previewOnly, bool isAdmin)
        => killSwitchActive || (previewOnly && !isAdmin);

    /// <summary>Reason a voting call is refused, or null when allowed. Codes: "disabled", "preview".</summary>
    public static (string Code, string Message)? BlockReason(bool killSwitchActive, bool previewOnly, bool isAdmin)
    {
        if (killSwitchActive)
        {
            return ("disabled", "Media Vote is switched off on this server right now.");
        }

        if (previewOnly && !isAdmin)
        {
            return ("preview", "Media Vote is in preview: only administrators can use it right now.");
        }

        return null;
    }

    /// <summary>
    /// Plugin Pages asks "show the page?" without telling us who is asking, so in preview mode the sidebar page
    /// is hidden from everyone (admins can open it directly).
    /// </summary>
    public static bool ShowSidebarPage(bool enabled, bool previewOnly, bool killSwitchActive)
        => enabled && !previewOnly && !killSwitchActive;
}
