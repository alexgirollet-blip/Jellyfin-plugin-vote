using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaVote.Core;

/// <summary>One tab as stored in the Custom Tabs plugin's configuration (read-only view).</summary>
public sealed record CustomTabInfo(int Index, string Title, string ContentHtml);

/// <summary>A problem found in Custom Tabs' saved tabs. Media Vote only reports; it never edits them.</summary>
public sealed record CustomTabFinding(int Index, string Title, string Kind, string Message);

/// <summary>
/// Read-only analysis of Custom Tabs' saved tabs.
/// <para>
/// Why this matters (verified against Custom Tabs 0.2.10.0 and the jellyfin-web 10.11.10 build): Custom Tabs
/// 0.2.x does not render tabs at runtime. Its File Transformation callback <c>HomeHtmlChunk</c> pastes every
/// tab's raw ContentHtml into jellyfin-web's compiled <c>home-html.*.chunk.js</c>, inside the single-quoted
/// JavaScript string that holds the home page template (<c>e.default='&lt;div id="indexPage" …'</c>), using
/// .NET <c>Regex.Replace</c>. Only the literal <c>'undefined'</c> is escaped. So a tab whose HTML contains a
/// single quote <c>'</c> (or a backslash, or a <c>$</c> substitution token) breaks the JavaScript of the home
/// page: the chunk fails to parse and the home screen renders blank. Media Vote 1.0.0.0 wrote such a tab
/// (an inline script full of single quotes); it kept breaking the page after uninstall because the tab stayed
/// in Custom Tabs' own settings. Scripts inside tab HTML are never executed by 0.2.x anyway.
/// </para>
/// </summary>
public static class CustomTabsInspector
{
    public const string MountAttribute = "data-mediavote-mount";

    /// <summary>
    /// The only HTML Media Vote recommends pasting into a Custom Tabs tab: a mount element with plain fallback
    /// text. No script, no single quotes, no backslashes, no dollar signs, no line breaks.
    /// </summary>
    public const string Snippet = "<div data-mediavote-mount=\"tab\" class=\"mediavote-tab-host\">Media Vote is loading. If this stays empty, reload the page.</div>";

    /// <summary>Custom Tabs versions from 0.3.0.0 inject empty tab containers and render content at runtime.</summary>
    public static readonly Version RuntimeRenderingSince = new(0, 3, 0, 0);

    /// <summary>
    /// True if <paramref name="html"/> can be pasted into Custom Tabs 0.2.x without breaking the home page:
    /// it is inserted into a single-quoted JS string with Regex.Replace semantics.
    /// </summary>
    public static bool IsSafeForChunkInjection(string? html)
        => html is not null && html.AsSpan().IndexOfAny(UnsafeChars) < 0;

    private static readonly System.Buffers.SearchValues<char> UnsafeChars = System.Buffers.SearchValues.Create("'\\$\r\n\u2028\u2029");

    public static IReadOnlyList<CustomTabFinding> Inspect(IEnumerable<CustomTabInfo> tabs, Version? customTabsVersion)
    {
        var findings = new List<CustomTabFinding>();
        bool injectsIntoChunk = customTabsVersion is null || customTabsVersion < RuntimeRenderingSince;
        foreach (var t in tabs)
        {
            var html = t.ContentHtml ?? string.Empty;
            bool ours = html.Contains(MountAttribute, StringComparison.Ordinal);
            bool hasScript = html.Contains("<script", StringComparison.OrdinalIgnoreCase);
            if (ours && hasScript)
            {
                findings.Add(new CustomTabFinding(
                    t.Index,
                    t.Title,
                    "LeftoverV100",
                    $"Tab {t.Index + 1} (\"{t.Title}\") was written by Media Vote 1.0.0.0 and contains an inline script. "
                    + "Delete this tab in Dashboard > Plugins > Custom Tabs and save. If you want a Vote tab, add a new one with the snippet shown on this page."));
            }
            else if (ours)
            {
                findings.Add(new CustomTabFinding(t.Index, t.Title, "MediaVoteTab", $"Tab {t.Index + 1} (\"{t.Title}\") is a Media Vote tab using the safe snippet."));
            }

            if (injectsIntoChunk && !IsSafeForChunkInjection(html))
            {
                findings.Add(new CustomTabFinding(
                    t.Index,
                    t.Title,
                    "BreaksHomePage",
                    $"Tab {t.Index + 1} (\"{t.Title}\") contains a single quote, backslash, dollar sign or line break. "
                    + $"Custom Tabs {customTabsVersion?.ToString() ?? "0.2.x"} pastes tab HTML into the home page's JavaScript, so this can blank the home screen. Remove those characters (use &#39; for an apostrophe)."));
            }
        }

        return findings;
    }

    /// <summary>True if any finding means the home page may currently be broken or needs the admin's action.</summary>
    public static bool NeedsAction(IEnumerable<CustomTabFinding> findings)
        => findings.Any(f => f.Kind is "LeftoverV100" or "BreaksHomePage");
}
