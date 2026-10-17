# Media Vote: a Jellyfin plugin

Let your users vote on which movies and shows to remove from your Jellyfin server.

An administrator starts a vote for selected users. Users vote from a banner on their home page, a **Vote** tab, a **Vote** sidebar page or a standalone page. When the deadline passes, the top **N** titles are either **reported to the admin** (the default) or **deleted through Sonarr and Radarr**: the Sonarr (series) or Radarr (movies) instance that manages each winning title deletes it with its files, and Jellyfin then forgets it. **Media Vote itself never deletes, moves or changes a media file.**

> ## ⚠️ Read this first
>
> - **1.1.0.0: a much easier vote builder.** One media browser everywhere (grid/list, real filters and sorts, sizes, drives, who watched it last), a deadline picker that says in plain words when the vote closes, per-drive storage bars, a free-space target, an approval step before anything is deleted, presets, tie-break rules, templates and recurring drafts, history and statistics. See [What's new in 1.1.0.0](#whats-new-in-1100). Every 1.0.2.0 safety rule is unchanged.
> - **1.0.2.0: deletion only through Sonarr/Radarr.** Jellyfin cannot write to exFAT media drives, so Media Vote no longer deletes anything itself; "Delete automatically" became **"Delete via Sonarr/Radarr"**. See [Why deletion goes only through Sonarr/Radarr](#why-deletion-goes-only-through-sonarrradarr-exfat). It also fixes the vote-results banner coming back after you close it.
> - **Media Vote requires Jellyfin 10.11.10.** It is built against the 10.11.10 packages with `targetAbi 10.11.10.0`. Jellyfin changes plugin APIs even inside 10.11.x, so a build only works on the Jellyfin version it was built for. See [Jellyfin version requirement](#jellyfin-version-requirement).
> - **1.0.0.0 was withdrawn.** It broke servers: user lists failed with HTTP 500, and its Vote tab in Custom Tabs blanked the home page. It has been removed from the plugin repository. **If you installed it, do [Upgrading from an earlier version](#upgrading-from-an-earlier-version) first.**
> - **Safety comes first.** After install, nothing is visible to your users. Every optional entry point is **off**, and **preview mode** shows Media Vote to administrators only until you press **Go live**. A [`DISABLE` file](#emergency-recovery-zimaos--docker) switches everything off instantly.

---

## Contents

1. [What's new in 1.1.0.0](#whats-new-in-1100)
2. [Jellyfin version requirement](#jellyfin-version-requirement)
3. [Upgrading from an earlier version](#upgrading-from-an-earlier-version)
4. [Install on a fresh server](#install-on-a-fresh-server)
5. [Safe test procedure (do this before going live)](#safe-test-procedure-do-this-before-going-live)
6. [Emergency recovery (ZimaOS / Docker)](#emergency-recovery-zimaos--docker)
7. [What Media Vote writes, and how to undo it](#what-media-vote-writes-and-how-to-undo-it)
8. [Entry points](#entry-points)
9. [The Vote tab (Custom Tabs): manual setup and why](#the-vote-tab-custom-tabs-manual-setup-and-why)
10. [Admin guide](#admin-guide)
11. [User guide](#user-guide)
12. [Vote rules](#vote-rules)
13. [Resolution and deletion safety](#resolution-and-deletion-safety)
14. [Radarr, Sonarr and Jellyseerr](#radarr-sonarr-and-jellyseerr)
15. [Why deletion goes only through Sonarr/Radarr (exFAT)](#why-deletion-goes-only-through-sonarrradarr-exfat)
16. [Supported and unsupported clients](#supported-and-unsupported-clients)
17. [Compatibility with other plugins](#compatibility-with-other-plugins)
18. [Extra features: implemented and skipped](#extra-features-implemented-and-skipped)
19. [Known limitations](#known-limitations)
20. [What has and has not been tested](#what-has-and-has-not-been-tested)
21. [Manual test checklist](#manual-test-checklist)
22. [REST API](#rest-api)
23. [Development, CI and releases](#development-ci-and-releases)
24. [Sources this plugin relies on](#sources-this-plugin-relies-on)

---

## What's new in 1.1.0.0

| Area | What you get |
|---|---|
| **Deadline picker** | Quick choices (*In 1 hour*, *Tonight 20:00*, *Tomorrow 20:00*, *In 3 days*, *1 week*, *2 weeks*, *End of this month*), a flexible "In N hours / days / weeks at HH:MM" row, and an exact date-and-time fallback. It always says the result in plain words, for example **"Closes Friday 23 Oct 2026 at 20:00 BST (in 7 days)"**. The **server** works out the date, so it is right across the clock change (25 Oct 2026). Your last choice is remembered. Default: in 7 days at 20:00. |
| **One media browser** | The same browser in the admin's hand-pick step, the voting overlay, the Vote tab and the sidebar page. Grid or list; each title shows size, which drive it is on with that drive's fullness, who played it last and when (or **Never watched**), plays and viewers, date added, and (admins) whether Sonarr/Radarr can remove it. Filtering, sorting and paging happen on the server, so large libraries stay fast. |
| **Hand-pick tray** | *Select all in this filter*, *Clear*, and a tray with the count, total size, the split per drive and what each drive would look like afterwards ("frees 134 GB on 8TB 1 (90% to 89%)"). |
| **Drives** | Media Vote finds which physical drive each library lives on (read-only, from the container's mount table) and shows used/free space with warning (85%) and critical (95%) levels. Default names: `/LM1` = **8TB 1**, `/LM2` = **8TB 2**, `/Media` = **System drive**; rename them in Settings. |
| **Storage tab** | Every drive with its fullness, its biggest libraries, and what the open vote's current leaders would free. |
| **Free-space target** | Instead of "the top N", act on the leaders until a drive has *X GB free* or is *below Y % full* (still capped by the safety maximum). |
| **Approval step** | For *Delete via Sonarr/Radarr* votes, **on by default**: when the vote closes, Media Vote prepares the final list with read-only requests and waits for you to **Approve** or **Reject**. Optional auto-approve after N hours. |
| **Presets** | *Never watched*, *Not watched in 12 months*, *Added 6+ months ago, watched by nobody*, *Largest titles*, *Fullest drive only*, *Half-finished series nobody continues*. |
| **Tie-break rules** | Besides "let me decide" and "act on all tied": *larger title wins*, *watched longest ago wins*, *added longest ago wins*. |
| **Templates and recurring votes** | Save a vote's settings as a template; make it weekly or monthly. A recurring template produces a **draft for you to review**; it never opens a vote by itself. |
| **History and statistics** | Space freed per vote and in total, turnout, most-voted titles, who takes part. |
| **Results** | Results show each title's size and the total freed, plus a **Request it again** link (Jellyseerr) for removed titles. |
| **Webhook extras** | The closing-soon message can name who hasn't voted yet; a message when a vote waits for your approval or a recurring draft is ready. |

---

## Jellyfin version requirement

| Media Vote | Built against `Jellyfin.Controller` / `Jellyfin.Model` | `targetAbi` | Runs on |
|---|---|---|---|
| **1.1.0.0** | **10.11.10** | **10.11.10.0** | Jellyfin **10.11.10**. CI also checks the DLL against the newest 10.11.x (non-blocking); Jellyfin itself refuses it on anything older than 10.11.10. |
| 1.0.2.0 | 10.11.10 | 10.11.10.0 | Jellyfin 10.11.10. |
| 1.0.1.0 | 10.11.10 | 10.11.10.0 | Jellyfin 10.11.10. |
| 1.0.0.0 (withdrawn) | 10.11.0 | 10.11.0.0 | Broken on 10.11.10 (see below). |

**Why a build is tied to one Jellyfin version.** A Jellyfin plugin is a .NET DLL that calls Jellyfin's internal classes directly, and Jellyfin does not keep those APIs stable between patch releases. Between 10.11.0 and 10.11.10, ten public members were removed, among them `IUserManager.Users`, which became `IUserManager.GetUsers()`. Media Vote 1.0.0.0 was compiled against 10.11.0, so on a 10.11.10 server .NET threw:

```
System.MissingMethodException: Method not found:
'IEnumerable`1<User> MediaBrowser.Controller.Library.IUserManager.get_Users()'
```

That error happened on every page that lists users and when creating a vote.

**How 1.0.1.0 prevents a repeat:**
- **CI version check:** fails if the csproj package version, `build.yaml` `targetAbi`, the test packages and the plugin's own "built for" constant disagree (`.github/scripts/check_versions.py`).
- **CI binary compatibility check** (`tools/ApiCompat`): loads the built DLL together with the real Jellyfin runtime assemblies of the target version and JIT-compiles every method. A missing member fails the build, exactly as it would fail on the server. Run against the released 1.0.0.0 DLL, this tool reproduces the exact `MissingMethodException` from the server log.
- **CI early warning:** a non-blocking job builds and checks against the **latest** published 10.11.x and reports if it no longer works.
- **Start-up self-check:** the plugin calls every Jellyfin API it depends on once at start-up and shows the result on the *Status & setup* tab, for example "Users: OK, 16 found", or the exact error. One broken API never stops the plugin or the server.

**Rebuilding for another Jellyfin version** (for example 10.11.11):
1. Change `<JellyfinPackageVersion>` in `src/Jellyfin.Plugin.MediaVote/Jellyfin.Plugin.MediaVote.csproj`. Change the `Jellyfin.Controller`/`Jellyfin.Model` versions in `tests/Jellyfin.Plugin.MediaVote.Tests/Jellyfin.Plugin.MediaVote.Tests.csproj`. Change `BuiltForJellyfin` in `src/Jellyfin.Plugin.MediaVote/Core/ErrorText.cs`.
2. Set `targetAbi: "<version>.0"` in `build.yaml`.
3. Run `python3 .github/scripts/check_versions.py && dotnet build -c Release && dotnet test -c Release`.
4. Run the binary check:
   ```sh
   dotnet build tools/ApiCompat -c Release -p:JellyfinVersion=<version>
   dotnet tools/ApiCompat/bin/Release/net9.0/ApiCompat.dll src/Jellyfin.Plugin.MediaVote/bin/Release/net9.0/Jellyfin.Plugin.MediaVote.dll
   ```
5. Fix anything the compiler or the check reports, bump the plugin version, and release as described in [Publishing a release](#publishing-a-release).

---

## Upgrading from an earlier version

**From 1.0.2.0:** just update in *Dashboard → Plugins* and restart. Your settings, instances and votes are kept. New settings start at safe defaults:
- **approval before deleting is on** (no auto-approve) for every *Delete via Sonarr/Radarr* vote you create from now on;
- drive names default to *8TB 1*, *8TB 2* and *System drive* for `/LM1`, `/LM2` and `/Media` (any other drive is named after its folder);
- voters see who watched a title last and when, as admins do; choose *play and viewer counts only* or *nothing about watching* in *Settings → Approval and results*.

A vote that was already open when you update keeps working with the rules it was created with: its list of titles was frozen at creation, so its cards have no drive, "last played by" or viewer details, and a *Delete via Sonarr/Radarr* vote created under 1.0.2.0 does **not** wait for approval (you confirmed it when you created it). Cancel and re-create it if you want the new behaviour.

**From 1.0.1.0:** just update in *Dashboard → Plugins* and restart. Your entry points, preview state and votes are kept.
- Your single Radarr / Sonarr / Jellyseerr URL and key move into the new **instance list** (*Settings → Sonarr, Radarr and Jellyseerr*). The old copies of the keys are removed from the settings file.
- The old Radarr/Sonarr *action* setting (Unmonitor / Remove + exclusion) is gone: Sonarr/Radarr now delete the title and its files themselves.
- Old votes and results still open; a 1.0.x "Delete automatically" vote is treated as "Delete via Sonarr/Radarr", and its *Retry* now goes through Sonarr/Radarr.

**From 1.0.0.0:** 1.0.0.0 wrote a "Vote" tab into **Custom Tabs' own saved settings**. That tab blanks the home screen, even after Media Vote is uninstalled; [why](#the-vote-tab-custom-tabs-manual-setup-and-why). Media Vote never touches Custom Tabs any more, so you remove the tab yourself.

1. **Remove the leftover Vote tab.** Go to **Dashboard → Plugins → Custom Tabs**. Delete the tab whose content starts with `<div data-mediavote-mount="tab" class="mediavote-tab-host"></div><script>`, usually titled "Vote", then **Save**. If the dashboard itself is unusable, see [Emergency recovery](#emergency-recovery-zimaos--docker), step 3.
2. Install the current version as below. Your existing votes are kept: they live in `/config/data/mediavote`.
3. On first start, the settings 1.0.0.0 saved are **reset**: every optional entry point goes off and preview mode goes on. Your Radarr/Sonarr/Jellyseerr connections, protected items and votes are kept.
4. Open *Status & setup*. A "Custom Tabs check" warning stays there as long as the old tab exists.

---

## Install on a fresh server

**Plugin repository URL.** Paste this into **Jellyfin → Dashboard → Plugins → Repositories → +**:

```
https://raw.githubusercontent.com/alexgirollet-blip/Jellyfin-plugin-vote/main/manifest.json
```

> The manifest lists each version once it has been released (1.0.1.0 now; 1.0.2.0 and 1.1.0.0 after their tags; see [Publishing a release](#publishing-a-release)). The broken 1.0.0.0 has been removed from it.

1. Make sure Jellyfin is **10.11.10** (*Dashboard → General*, or check that other plugins show 10.11.10.0).
2. **Dashboard → Plugins → Repositories → +**: name `Media Vote`, the URL above → **Save**.
3. **Dashboard → Plugins → Catalog → Media Vote** → **Install**.
4. **Restart Jellyfin** (ZimaOS: restart the Jellyfin app).
5. Open **Dashboard → Plugins → Media Vote** → **Status & setup** and continue with the [safe test procedure](#safe-test-procedure-do-this-before-going-live).

At this point nothing is visible to your users and nothing has been injected into the web client: every optional entry point is off and preview mode is on. Only the admin page and the standalone page `/MediaVote/vote` work, and in preview mode the standalone page shows nothing to non-admins either.

**After turning an entry point on, or updating:**
- **Browsers:** hard refresh (Ctrl+F5 / Cmd+Shift+R).
- **Jellyfin Desktop app:** quit it and reopen it.
- **Phones and iPads (official app):** fully close the app (swipe it away) and reopen it. If something still doesn't appear, sign out and back in.

---

## Safe test procedure (do this before going live)

Follow the steps in order and stop at the first problem. If anything ever looks wrong for your users, create the `DISABLE` file ([Emergency recovery](#emergency-recovery-zimaos--docker), step 1).

1. **Status & setup:** all *Jellyfin API self-check* lines should be **OK** ("Users: OK, N found", "Libraries: OK", and so on). If one fails, it shows the exact error. Report it, and don't turn on entry points yet.
2. **Prove the kill switch works:**
   1. On the ZimaOS host, run
      ```sh
      touch /DATA/AppData/jellyfin/config/data/mediavote/DISABLE
      ```
      then restart Jellyfin. *Status & setup* should show a red **"Disabled by file"** banner.
   2. Remove the file again
      ```sh
      rm /DATA/AppData/jellyfin/config/data/mediavote/DISABLE
      ```
      and restart Jellyfin.
3. **Create a test vote** in *New vote*. Leave **Notify only** on, select only yourself or a test account, and pick a couple of titles. Open `/MediaVote/vote` as an admin and vote.
4. **Turn on ONE entry point** (*Settings → Entry points*), save, and hard-refresh your browser. Start with the **home banner**, which needs File Transformation. Check:
   - the home page, Media Bar, Home Screen Sections, Jellyfin Enhanced and your existing Custom Tabs all still work;
   - you, as an admin, see the banner;
   - *Status & setup* shows "Registered with File Transformation (self-test passed)".
5. Log in as a **non-admin** (another browser or a private window). You must see **nothing** from Media Vote: preview mode.
6. Repeat steps 4–5 for the next entry point you want:
   - **Sidebar page (Plugin Pages):** while in preview mode it is hidden from everyone. Open it directly at `#/userpluginsettings.html?pageUrl=/MediaVote/page`.
   - **Current leaders row (Home Screen Sections):** also enable it in HSS's own settings.
7. **Go live:** *Status & setup → Go live* → confirm. Your users now see the enabled entry points. Ask them to close and reopen the app.
8. **Only after going live**, add the Vote tab by hand if you want it; see [The Vote tab](#the-vote-tab-custom-tabs-manual-setup-and-why).
9. **Connect Sonarr and Radarr** (*Settings → Sonarr, Radarr and Jellyseerr*): add each instance (for example *Sonarr*, *Sonarr Anime*, *Radarr*), press **Test connection** on each, save, then check the read-only card on *Status & setup* (version, root folders, free space).
10. Before your first **Delete via Sonarr/Radarr** vote:
    1. Make a vote with only **one small, harmless title** you are happy to lose, *Delete via Sonarr/Radarr*, N = 1.
    2. Press **Dry run**: it lists exactly which Sonarr/Radarr entry would be deleted and how many GB, using read-only requests only.
    3. Close the vote and check the title disappears from Sonarr/Radarr, from the drive and, a minute later, from Jellyfin. See [Manual test checklist](#manual-test-checklist).

---

## Emergency recovery (ZimaOS / Docker)

Use these from the ZimaOS host shell (SSH, or the ZimaOS terminal). The container's `/config` is `/DATA/AppData/jellyfin/config` on the host.

1. **Switch Media Vote off without uninstalling it (kill switch):**
   ```sh
   touch /DATA/AppData/jellyfin/config/data/mediavote/DISABLE
   ```
   Then restart Jellyfin. While the file exists:
   - nothing is injected into the web client and no entry point is registered;
   - the served `client.js` is an empty script, so even a cached page does nothing;
   - users see nothing, and voting calls are refused;
   - **vote resolution and any request to Sonarr/Radarr are paused** (Retry is refused too).

   The admin page keeps working. To re-enable, run `rm /DATA/AppData/jellyfin/config/data/mediavote/DISABLE` and restart. Inside the container, the file is `/config/data/mediavote/DISABLE`.
2. **Remove the plugin completely, even if Jellyfin's dashboard doesn't load:**
   ```sh
   docker stop jellyfin            # or stop the app in ZimaOS
   ls /DATA/AppData/jellyfin/config/data/plugins/        # find "Media Vote_1.x.x.x"
   rm -rf "/DATA/AppData/jellyfin/config/data/plugins/Media Vote_1.1.0.0"
   docker start jellyfin
   ```
   Use the folder name you actually see; the container name may differ in ZimaOS. Optional extras:
   - `/DATA/AppData/jellyfin/config/data/plugins/configurations/Jellyfin.Plugin.MediaVote.xml` holds the settings, including API keys;
   - `/DATA/AppData/jellyfin/config/data/mediavote/` holds votes and the audit log.

   Delete them too if you want a clean slate.
3. **Home screen blank because of a Custom Tabs tab** (1.0.0.0's Vote tab, or *any* tab containing an apostrophe, backslash or `$`; see [why](#the-vote-tab-custom-tabs-manual-setup-and-why)):
   - **If the dashboard works:** go to *Dashboard → Plugins → Custom Tabs*, delete or fix the tab, then Save.
   - **If it doesn't:**
     1. Stop Jellyfin.
     2. Open `/DATA/AppData/jellyfin/config/data/plugins/configurations/Jellyfin.Plugin.CustomTabs.xml` in a text editor.
     3. Delete the whole `<TabConfig>…</TabConfig>` block whose `ContentHtml` contains `data-mediavote-mount`. Make a backup copy of the file first.
     4. Start Jellyfin.

     (Custom Tabs' file name and layout are its own; check before editing.)

---

## What Media Vote writes, and how to undo it

Media Vote writes **nothing** into Jellyfin's web files, **never** changes another plugin's saved settings, and **never deletes, moves or changes a media file**. Unit tests fail the build if code that could write another plugin's configuration, or delete or move a file outside Media Vote's own data folder, is added back.

| What | Where | Undo |
|---|---|---|
| Votes, results, templates, audit log | `/config/data/mediavote/` (`votes.json`, `templates.json`, `audit.jsonl`) | Delete the folder. |
| Drive usage | **Read only**: `/proc/self/mountinfo` and the filesystem's free-space figures. Nothing is written. | — |
| Its own settings, including API keys | `/config/data/plugins/configurations/Jellyfin.Plugin.MediaVote.xml` (Jellyfin's standard plugin config) | Delete the file. |
| `index.html` script tag | **In memory only**, through File Transformation, and only while the banner or the Vote-tab toggle is on | Turn both off, uninstall, or create `DISABLE`. Nothing on disk to undo. |
| Plugin Pages sidebar entry | **In memory only** (`RegisterPage`), only while that entry point is on | Removed automatically when turned off or on uninstall (`OnUninstalling` calls `RemovePage`). |
| Home Screen Sections row | **In memory only** (`RegisterSection`). HSS has no removal API, so the row returns nothing when off. | Disappears after a Jellyfin restart. |
| Custom Tabs | **Nothing.** You add and remove the Vote tab yourself. | Delete the tab in Custom Tabs. |
| Sonarr / Radarr / Jellyseerr | Only when a **Delete via Sonarr/Radarr** vote resolves **and is approved** (when approval is on), or you press Retry: Jellyseerr media record (if configured), then Sonarr/Radarr `DELETE …?deleteFiles=true` with the import (list) exclusion you chose. **Never** in Notify only, Dry run, Preview or the Status tab: those use read-only clients that cannot send anything but GET. | Not undoable: that is the purpose of the feature. Re-add the title in Sonarr/Radarr (remove the exclusion first) to get it back. |
| Jellyfin's own entry for a removed title | Only after Sonarr/Radarr removed it **and its files are gone from disk**: `ILibraryManager.DeleteItem(item, DeleteFileLocation = false)` drops Jellyfin's database rows and cached images/metadata for that title, exactly what a library scan does for a folder that disappeared. Until then, Media Vote only asks Jellyfin to refresh that library. | Nothing to undo: the files were already gone. |
| Media files | **Never.** | — |

On uninstall, Jellyfin calls the plugin's `OnUninstalling()`. Media Vote then removes its File Transformation registration (`RemoveTransformation`) and its Plugin Pages entry (`RemovePage`). Votes and settings are kept, in case you reinstall. Delete them as shown above if you don't want them.

*1.0.0.0 only:* if File Transformation was missing, 1.0.0.0 could add a `data-mediavote-script` tag to the web client's `index.html` and keep a backup `index.html.mediavote-backup`. Current versions detect such a leftover tag and tell you on the Status tab. In the linuxserver image that file isn't writable, so it is very unlikely you have it.

---

## Entry points

All entry points open the same voting screen. Fresh installs and upgrades from 1.0.0.0 start with every optional entry point **off**.

| Entry point | Default | Needs | Notes |
|---|---|---|---|
| Admin page (Dashboard → Plugins → Media Vote) | always | — | Works even with the `DISABLE` file. |
| Standalone page `/MediaVote/vote` | on | — | Has its own Jellyfin sign-in and injects nothing. In preview mode, non-admins see nothing. |
| Home banner | **off** | File Transformation | Turning it on registers the `index.html` transformation, after its self-test passes. |
| Vote tab | **off** | Custom Tabs; **you add the tab by hand** | Tick "I have added the Vote tab" so the script is loaded for it. See below. |
| Sidebar page | **off** | Plugin Pages | Plugin Pages cannot show a page to admins only, so it is hidden from everyone in preview mode. |
| Current leaders row | **off** | Home Screen Sections | Also enable the section in HSS's settings. Shows nothing to non-admins in preview mode. |

**Preview mode** is on by default:
- the client script renders nothing for non-admins and stops all its work for them (no observers, no timers);
- the server refuses non-admin voting calls (`403 preview`);
- the HSS row returns nothing for non-admins;
- the sidebar page is hidden.

**Go live** (*Status & setup*, with in-page confirmation) turns preview mode off. **Back to preview** turns it on again.

### How the banner stays on top

These placement rules were checked against each plugin's current source:
- **jellyfin-web 10.11:** the home view is `#indexPage` → `#homeTab` (`data-index="0"`) → `.sections`. Jellyfin and Home Screen Sections both re-render by replacing the **contents** of `.sections` (`innerHTML`); HSS also adds the `homeSectionsContainer` class. Media Vote inserts its banner as the **first child of `#homeTab`, before `.sections`**, so those re-renders never remove it.
- **Media Bar:** it appends `#slides-container` to `<body>` with `position:absolute; top:0` and pushes `.homeSectionsContainer` down using `margin-top` and `z-index: 6`. Its slide content uses `z-index` up to 10. The banner uses `position: relative; z-index: 11` (CSS variable `--mediavote-banner-z`), so it sits at the top of the home content, above the slideshow, and stays below Jellyfin's header and dialogs. Media Bar measures where the rows start, so it adapts to the banner's height.
- A debounced `MutationObserver`, `hashchange`/`popstate`, and jellyfin-web's `viewshow` event re-check placement after every navigation and every home re-render. The banner has a fixed id, so it is **never inserted twice**. If another script inserts something above it, the banner moves back to the top, but at most 5 times per page view, so it never gets into a tug-of-war with another plugin.
- It is removed when you leave the home page, when no vote is active, and when the user is not eligible.
- The DOM lookup sits behind a small *host adapter* list. A Jellyfin 12 / MUI adapter can be added there later without touching anything else.

---

## The Vote tab (Custom Tabs): manual setup and why

**Root cause of the 1.0.0.0 blank home screen.** This was verified against the Custom Tabs **0.2.10.0** source and the real jellyfin-web **10.11.10** build.
- Custom Tabs 0.2.x does **not** render tabs at runtime. Its File Transformation callback `TransformationPatches.HomeHtmlChunk` pastes every tab's raw `ContentHtml` into jellyfin-web's compiled `home-html.<hash>.chunk.js`.
- In that file, the home page template is a **single-quoted JavaScript string**: `e.default='<div id="indexPage" …'`.
- The insertion uses .NET `Regex.Replace`, and only the literal `'undefined'` is escaped.
- 1.0.0.0's tab contained an inline `<script>` full of single quotes, such as `getElementById('mediavote-loader')`. The first `'` ended the JavaScript string, so the home page module failed to parse (`SyntaxError: Unexpected identifier 'mediavote'`) and the home screen rendered blank.
- Because the tab lived in **Custom Tabs' own settings**, it kept breaking the page after Media Vote was uninstalled.
- Scripts in tab HTML are never executed by 0.2.x anyway.
- `tests/client/customtabs-chunk.js` reproduces this on the real 10.11.10 chunk (`tests/client/fixtures/`).

The same thing happens with **any** Custom Tabs tab, not only Media Vote's, if its HTML contains a single quote `'` (even in normal text such as "don't"), a backslash `\`, or a `$` sequence. On the *Status & setup* tab, Media Vote's read-only "Custom Tabs check" warns about such tabs.

**Safe manual setup:**
1. Go live first (otherwise friends see an empty tab).
2. **Dashboard → Plugins → Custom Tabs → add tab**, with the title `Vote`.
3. Paste **exactly** this as its content. The *Settings → Vote tab* card has it with a Copy button.
   ```html
   <div data-mediavote-mount="tab" class="mediavote-tab-host">Media Vote is loading. If this stays empty, reload the page.</div>
   ```
   It has no script, no single quote, no backslash, no `$` and no line break. The test above proves it keeps the 10.11.10 home page valid.
4. Save in Custom Tabs. In Media Vote, tick *"I have added the Vote tab to Custom Tabs by hand"* and save.
5. Reload Jellyfin.

The globally injected Media Vote script finds the mount element by itself, including when Custom Tabs renders or re-renders it late. It replaces the fallback text with the voting screen, or with "No active vote right now" when there is no vote or the user isn't in it.

If the script isn't loaded (both toggles off, the `DISABLE` file, or preview mode for that user), the tab simply shows the plain fallback text. An empty or unmounted tab cannot break the page, because the snippet is plain HTML.

**To remove the tab:** delete it in Custom Tabs and untick the setting.

---

## Admin guide

Open **Dashboard → Plugins → Media Vote**. The page also works on a phone.

**Each section loads on its own.** If one call fails, that section shows the error message and a **Retry** button, and the rest of the page keeps working. Server errors come back as readable text (for example "Jellyfin could not list users: …"), never a bare "HTTP 500".

### Create a vote (*New vote* tab)

| Field | Notes |
|---|---|
| Start from a template | Fills the form from a saved template (see *Save as template* below). **Delete template** removes it. |
| Title / description | Shown to voters in the banner and the voting screen. |
| Who can vote | **All users** (any enabled account), or **selected users**. Disabled accounts can never vote. |
| Scope | All libraries · specific libraries · movies only · series only · hand-picked titles. **Only whole Movies and whole Series** are votable, never seasons or episodes. **Collections (BoxSets) are not votable.** |
| Hand-picked titles | Opens the **media browser** (below): browse, filter and tick titles, or press **Select all in this filter**. |
| Filters | *Never watched by anyone* · *not watched by anyone in X months* · *only the N largest titles*. |
| Votes per user | 1–1000. |
| What to act on | **The top N titles** (default), **winners until a drive has at least … GB free**, or **winners until a drive is below … % full**. With a target, winners are taken in rank order, counting only what each title frees *on that drive*, until the target is met; the *Most titles one vote may remove* safety maximum still applies. If the drive's usage can't be read at the deadline, **nothing** is selected. |
| Minimum turnout | Optional percentage of eligible voters who must vote, otherwise nothing is acted on. |
| Deadline | The **deadline picker** (below). Times are in the configured time zone (default **Europe/London**). |
| Several votes on one title | Off by default. When on, a user may stack several of their votes on one title. |
| Keep votes | Optional. Each keep vote subtracts one from a title's score, and keep votes have their own allowance per user. A user can never vote both remove and keep on the same title. |
| Voter names / live tallies | Both **shown by default**. Admins always see names. When live tallies are hidden, counts and names are hidden from voters until the vote closes, and voters can't sort by votes. |
| If a tie crosses the cut-off | **Ask me** (default): act on none of the tied titles until you break the tie. **The larger title wins** · **The title watched longest ago wins** (never watched first) · **The title added longest ago wins**: the rule fills the free places; titles still exactly equal under the rule are left for you. **Act on all tied titles** (may exceed N). |
| **Outcome** | **Notify only** (default): a report with names, sizes and the matching Sonarr/Radarr entries; nothing is removed anywhere. **Delete via Sonarr/Radarr**: Sonarr (series) or Radarr (movies) deletes each winner with its files. It requires an explicit confirmation checkbox (the server rejects it otherwise), N may not exceed *Most titles one vote may remove* (Settings, default 5), and every Sonarr/Radarr instance must be reachable when the vote is created. With *approval* on (the default), nothing is deleted until you approve the final list. |
| Titles Sonarr/Radarr don't manage | For a *Delete via Sonarr/Radarr* vote they are **left out by default** (they could not be removed automatically). Tick *Also let people vote on titles Sonarr/Radarr do not manage* to include them; if one wins it is listed for you to remove by hand. |
| Save these settings as a template | Give it a name and optionally make it **weekly** or **monthly** with a first date. A recurring template **never opens a vote by itself**: at each date it prepares a draft, shows it on the *Votes* tab (and in the webhook), and you press **Review and open** (or **Skip this time**). The deadline is saved as a rule ("in 7 days at 20:00"), so it is worked out again each time. |

Use **Preview** to see how many titles match, their total size, the **most disk space that could be freed** (the N largest), and which titles are **not removable automatically** (badge, plus a *removable only* filter). The exact figure depends on the winners; see it with **Dry run**.

Which titles Sonarr/Radarr manage is worked out with read-only requests when the list is built: by **provider ids first** (TMDb and IMDb for movies; TVDB, TMDb and IMDb for series), and only when Jellyfin has none of those ids, by **title + year + folder name** together. Anything that could be more than one entry (for example the same series in two Sonarr instances) counts as **not matched**.

Only **one vote can be open at a time**. The list of votable titles is frozen when the vote is created.

**Selected users** lists **all** users on the server: enabled and disabled, with disabled accounts clearly marked (they can't vote). It has a search box, **Select all enabled**, **Select none**, and a running count of selected users. If the user list can't be loaded, the picker shows the error and a Retry button.

#### The deadline picker

- **Quick choices:** *In 1 hour* · *Tonight 20:00* · *Tomorrow 20:00* · *In 3 days* · *1 week* · *2 weeks* · *End of this month* (all at 20:00 except *In 1 hour*).
- **Flexible:** "In **N** hours / days / weeks at **HH:MM**". The time box is hidden for hours (N hours means exactly N hours from now). Days and weeks count calendar days and keep the wall-clock time, so "in 7 days at 20:00" is 20:00 local time even if the clocks change in between.
- **Exact date and time** as a fallback.
- Below it, the result **in plain words**, worked out by the server: for example *"Closes Friday 23 Oct 2026 at 20:00 BST (in 7 days)"*, or a clear message when the time is in the past or doesn't exist (clocks going forward).
- Your last choice is remembered in this browser (if the browser blocks storage, only for this page visit). The first default is **in 7 days at 20:00**. The same picker is used for **Extend**.

#### The media browser (hand-picking)

The same browser voters use, in **picker mode**:

- **Grid or list** view; **search**; filters for **type**, **library**, **drive**, **genre**, **watched** (never · not in X months · by me · not by me), **added more than X months ago**, **half-finished series**, **size** (min/max GB), **year** (from/to) and **removable by Sonarr/Radarr only**; sorts by **title, size, date added, last played, never watched first, play count, drive, year** (and **votes** for voters, when tallies are shown). Filtering, sorting and paging run on the server (cached for a short time), and the page loads more as you scroll.
- **Presets** in one tap: *Never watched*, *Not watched in 12 months*, *Added 6+ months ago, watched by nobody*, *Largest titles*, *Fullest drive only*, *Half-finished series nobody continues*.
- Each card: poster, title, year, size, the drive(s) it is on with a **fullness bar** (green, amber above 85%, red above 95%), **last played by whom and when** or **Never watched**, plays and number of viewers, date added, and a **removable / not removable by Sonarr/Radarr** indicator.
- **Select all in this filter** (up to 10,000), **Clear**, and a **tray** with the count, the total size, the split per drive, and the projection for each drive ("frees 134.0 GB on 8TB 1 (90% to 89%)").
- If the browser can't load (for example while the `DISABLE` file exists), a simple title search appears instead.

### Manage votes (*Votes* tab)

- **Alerts** at the top: votes **waiting for your approval** (with count, size and any auto-approve time; **Review**), and **recurring drafts** ready to review (**Review and open** / **Skip this time**).
- **Extend:** a new deadline with the deadline picker. **Close now:** resolve immediately. **Cancel:** nothing is deleted, and the votes stay in history. Each of these asks for an in-page confirmation.
- **Approve / Reject** (*Delete via Sonarr/Radarr* votes with approval on): when the vote closes, Media Vote prepares the final list **using read-only requests only** (the same as a dry run: exact Sonarr/Radarr entry and GB for each title) and waits. **Approve** runs the removal exactly as described in [Order of operations](#order-of-operations-delete-via-sonarrradarr) (fresh lookups, re-check before each delete). **Reject** removes nothing and keeps the result in history. With *auto-approve after N hours* set, the list is approved automatically when that time passes, unless you approved or rejected it first; the kill switch also pauses auto-approval.
- **Dry run:** shows exactly which Sonarr/Radarr entry (instance and id) each winner would be, how many GB Sonarr/Radarr would free in total, which winners you would have to remove by hand, and the Jellyseerr step. It changes nothing: it uses a read-only client that can only send GET requests.
- **Details:** the ranking with every voter's name, every vote with its time, who has not voted yet, the result (with each title's size and the total freed), and the external steps for each title.
- **Tie-break:** pick up to the free number of slots among the tied titles. Unchosen tied titles are kept.
- **Resolve anyway:** acts on the winners although minimum turnout was not met.
- **Retry titles not removed:** for *Delete via Sonarr/Radarr* votes, retries titles that were not matched, hit an unreachable instance, were refused, could not be verified, failed the Jellyseerr step, or were never attempted. Every title is looked up again first, so nothing is ever deleted twice.
- **CSV:** downloads the ranking and outcome. It is formula-injection safe and opens cleanly in Excel or Numbers. Downloads may not work inside the iOS app; use a browser.
- **History** lists every vote. The **Audit log** tab lists every vote cast, moved or withdrawn, every admin action (including approvals, rejections and templates), and every deletion and external step, newest first. It is stored in `/config/data/mediavote/audit.jsonl`, rotated at 5 MB.

### Storage tab

Read-only. For every detected drive: its name, fullness bar, used / free / total, level (OK, warning, critical, or **unknown** when it can't be read), the folders on it, and its five biggest libraries. If two folders that look like separate drives (for example `/LM1` and `/LM2`) turn out to be **the same filesystem**, a warning says so. If a vote is open, it also shows what its **current leaders** would free on each drive, before and after.

### History tab

Statistics from every stored vote: space freed per vote and in total (with a running total), voters and turnout per vote, the **most-voted titles** across votes, and **participation per user** (votes cast, votes taken part in out of votes they could vote in).

### Settings tab

- **Entry points:** turn them on one at a time; see [Safe test procedure](#safe-test-procedure-do-this-before-going-live).
- **Vote tab card:** the exact snippet, a Copy button and step-by-step instructions.
- **General:** time zone (default `Europe/London`), closing-soon reminder (default 24 h), result banner days (default 7), and Playback Reporting data (on, used only if that plugin is installed).
- **Protected libraries and titles:** these are never votable and never removed. Protection is re-checked right before anything is sent to Sonarr/Radarr.
- **Sonarr, Radarr and Jellyseerr:** a list of instances (add as many as you have, for example a separate Anime Sonarr), each with name, URL, masked API key, *Use this instance* and **Test connection**; the import (list) exclusion options; the Jellyseerr step; and **Most titles one vote may remove** (1–100, default 5). See [below](#radarr-sonarr-and-jellyseerr).
- **Approval and results:**
  - **Wait for my approval** before a *Delete via Sonarr/Radarr* vote removes anything (**on** by default, recommended);
  - **Approve automatically after … hours** (0 = never, the default; up to 720);
  - **Public Jellyseerr address** for the *Request it again* links in results (optional; for example `https://requests.example.com`). Without it, no link is shown. The link uses the title's TMDb id;
  - **Watch details voters see:** *who watched it last and when, plays and viewers* (default) · *play and viewer counts only* · *nothing about watching*. Admins always see everything. Filters and sorts that would give hidden details away are removed for voters and ignored by the server: with *counts only*, "not watched in X months" and "last played"; with *nothing*, every watch filter and sort.
- **Drives:** every detected drive with an editable **name** (defaults: `/LM1` = *8TB 1*, `/LM2` = *8TB 2*, `/Media` = *System drive*), and the **warning** (default 85%) and **critical** (default 95%) thresholds.
- **Webhook:** Discord or generic JSON (the URL is stored masked), with a switch for each message: vote opened · closing soon · results · approval needed and recurring drafts ready · *add "Not voted yet: …" to the closing-soon message*.

### Status & setup tab

- the "Disabled by file" banner when `DISABLE` exists;
- preview state and the **Go live** / **Back to preview** buttons;
- the state of every entry point;
- the **Jellyfin API self-check** (with "Run again"), including a "Media folders (information only)" line: if Jellyfin cannot write to your media folders it says so and that this is **expected and fine**;
- a read-only **Sonarr, Radarr and Jellyseerr** card: reachable or not, version, root folders with free space (and total size where Sonarr/Radarr report it), with *Check again*;
- the File Transformation registration state;
- the read-only **Custom Tabs check** (1.0.0.0 leftover tab, and tabs that would break the home page);
- detected optional plugins, the server time zone, the process user, the data folder and the kill-switch path.

---

## User guide

1. Open Jellyfin. While a vote is open for you, a banner such as **"Vote: Spring clean-up · You have 3 votes left · closes Saturday 1 June 2026, 20:00"** appears at the top of the home page. It shows **"Not voted yet"** until you vote, and **"Closing soon"** near the deadline.
2. Tap the banner, or open the **Vote** tab or the **Vote** sidebar page. They all show the same **media browser**.
3. Browse every votable title in a **grid** or a **list** (your choice is remembered). Each title shows its poster, title, year, type and size; **which drive it is on**, with a bar showing how full that drive is; **who played it last and when**, or **Never watched**; how many plays and viewers; and when it was added. (Your admin may show only counts, or nothing about watching.)
4. **Search**, filter by **type**, **library**, **drive**, **genre** or **watched** (never · not in 6/12/24 months · by me · not by me), and **sort** by title, votes, size, date added, year, drive, last played, play count or never-watched first. **My votes** shows only the titles you voted on. More titles load as you scroll.
5. Tap **Vote to remove**. Your remaining votes are shown at the top and bottom. Tap **Voted ✓ · undo** to take a vote back. If you have no votes left and tap another title, you are offered to **move** one of your votes to it.
6. You can change your votes until the deadline. After the vote closes, a result banner lists what was removed or flagged, **with each title's size and the total space freed**. Where the admin set it up, removed titles have a **Request it again** link (Jellyseerr). Close the banner with **✕** and it stays closed.
7. In a *Delete via Sonarr/Radarr* vote that includes titles Sonarr/Radarr don't manage, those show a **"Not removable automatically"** badge.

The browser works with a keyboard and screen readers: every control has a label, focus is visible, the result counts are announced, and nothing depends on colour alone (drive bars also say the percentage).

Your votes are **not anonymous**: your name and the time of each vote are stored. Admins always see who voted for what, and other participants see names too unless the admin turned that off.

---

## Vote rules

All rules are enforced **on the server**, in `Core/VoteRules.cs` and `Core/VoteService.cs`, never only in the browser. The user is always taken from the authentication token.

- **Eligibility:** *all users* means any enabled account; otherwise only the selected users. Disabled or deleted accounts cannot vote. A user only sees titles they are allowed to see under their library access and parental controls.
- **Limits:** a user cannot exceed their remove-vote allowance or their keep-vote allowance. By default a user can put at most one vote on a given title.
- **Deadline:** votes, withdrawals and moves are refused from the deadline onwards, even before the vote has been resolved.
- **Score:** remove votes minus keep votes. Only titles with a **score above 0** can win.
- **Ranking:** by score, highest first. Equal scores are a tie; by default nothing (name, date, size) breaks ties automatically.
- **Winners:** the top N. If titles share the N-th place score and they do not all fit, the tie **crosses the cut-off**:
  - **Default (FlagForAdmin):** the clear winners above the tie are acted on. **None of the tied titles is acted on**, and the vote is flagged *needs attention* so the admin can break the tie.
  - **LargerSizeWins / LeastRecentlyWatchedWins / OldestAddedWins** (1.1.0.0): the rule picks which tied titles fill the free places (largest first; watched longest ago first, never watched before anything; added longest ago first). If the rule itself can't separate the titles competing for the last place (same size, same date), those are flagged for the admin as with the default.
  - **IncludeAllTied:** all tied titles are acted on, which may exceed N.
  - If the tied titles exactly fill the remaining places, there is no tie problem.
- **Free-space target** (1.1.0.0, instead of N): titles with a score above 0 are taken in rank order until the chosen drive would have at least X GB free, or be below Y % full. Only the part of a title stored on that drive counts. It never takes more than *Most titles one vote may remove* for a delete vote, and if the drive's usage can't be read, nothing is selected. Ties are **not** treated specially in this mode: equal scores are taken in the ranking's order (more remove votes first, otherwise a fixed but arbitrary order). Use the approval step to check the list.
- **Turnout:** distinct users with at least one vote, divided by eligible users. If a minimum is set and not met, nothing is acted on and the admin is told.

### Deadline and time zone

The admin picks the deadline with the [deadline picker](#the-deadline-picker). The browser only sends the choice ("tomorrow at 20:00", "in 3 days at 18:30", or an exact local time); **the server** works out the moment in the configured zone (default Europe/London), stores it in UTC, and sends back the plain-words description shown under the picker.

- **Calendar days, not 24-hour blocks:** "in 7 days at 20:00" chosen on Tuesday 20 Oct 2026 is **20:00 GMT on Tuesday 27 Oct 2026**, after the clocks go back on Sunday 25 Oct, not 19:00 or 21:00. "In N hours" is exactly N hours.
- **Clocks go forward** (for example 01:30 on the last Sunday of March): that time does not exist and is rejected with a message.
- **Clocks go back** (for example 01:30 on the last Sunday of October): that time happens twice, and the **first** occurrence (BST) is used, so a vote never closes later than expected.
- A deadline in the past, or "Tonight 20:00" after 20:00, is refused with a message.

If the server cannot find the time zone, the *Status & setup* tab warns and UTC is used.

## Resolution and deletion safety

Media Vote **never deletes, moves or changes a media file**. Titles are removed only by the Sonarr (series) or Radarr (movies) instance that manages them; see [why](#why-deletion-goes-only-through-sonarrradarr-exfat).

- **When:** a background timer checks every 30 seconds, and once about 20 seconds after start-up. If the server was **offline at the deadline**, the vote is resolved on the next start. The scheduled task *Media Vote: resolve due votes* runs the same check manually, or hourly as a backup.
- **Resolved only once:** the vote is marked resolved under the store lock before anything is acted on, so overlapping checks cannot resolve it twice.
- **Progress is saved after every title**, so a crash mid-run never loses what already happened. An interrupted run is marked **Interrupted** at the next start and **never retried automatically**. Check the audit log, then use *Retry*.
- **Kill switch:** while the `DISABLE` file exists, resolution and every request to Sonarr/Radarr are paused, and Retry is refused. A vote whose deadline passed resolves once the file is removed.
- **Approval (1.1.0.0, on by default):** a *Delete via Sonarr/Radarr* vote stops after working out its winners. The final list is prepared with read-only requests, and nothing below runs until the admin presses **Approve** (or the optional auto-approve timer expires). **Reject** removes nothing.
- **Safeguards:** Notify only by default · approval before deleting · explicit confirmation checkbox · a maximum number of titles per vote (Settings, default 5, enforced when creating the vote and again while removing) · Dry run · re-check right before each delete · audit log · kill switch. **Notify only, Dry run, Preview and the Status tab use a read-only client**: any request other than GET throws before it leaves the server (unit-tested).

### Order of operations (Delete via Sonarr/Radarr)

After approval (when approval is on), for each winner, **in rank order**, and **each one isolated**: a failure on one title never stops the others.

1. **Skip** it if the `DISABLE` file exists, the title or its library is protected, or the vote's maximum is reached (*Not attempted* / *Protected*).
2. **Match** it to exactly one Sonarr/Radarr entry across all your instances (fresh read-only lists, never cached). Not managed, ambiguous or no instance configured → **Not matched**: listed for you to remove by hand. An unreachable instance → **Sonarr/Radarr unreachable**.
3. **Re-check** right before acting: `GET /api/v3/series/{id}` or `/movie/{id}`.
4. **Jellyseerr** (if configured): delete its media/request record. If that fails, the title is **not** deleted (*Jellyseerr step failed*).
5. **Delete**: `DELETE /api/v3/series/{id}?deleteFiles=true&addImportListExclusion=…` (Sonarr) or `DELETE /api/v3/movie/{id}?deleteFiles=true&addImportExclusion=…` (Radarr). This call is made **once**: there is no automatic retry, and every later attempt starts by looking the title up again, so the same entry can never be deleted twice. If Sonarr/Radarr answer with an error, their message is shown **verbatim** (*Sonarr/Radarr refused the delete*).
6. **Verify**: ask again. Still listed → *Could not confirm removal*. Gone → **Removed**.
7. **Reconcile Jellyfin.** Sonarr and Radarr delete the files **in the background**, after their API has already answered (verified in their source). So Media Vote checks the title's folder (read-only) and, once it is gone, drops the Jellyfin entry with `DeleteItem(…, DeleteFileLocation = false)`, which never touches files, the same call Jellyfin's own library scan makes for a folder that disappeared. Until then it asks Jellyfin to refresh that library and checks again every 30 seconds. If the files are **still there an hour later** (Sonarr/Radarr's background delete failed, the folder is shared with another title, or a recycle bin is set), the title is flagged **FILES STILL ON DISK** for you to check.
8. Every step is written to the **audit log** (`arr_delete_requested`, `arr_removed`, `arr_delete_refused`, `jellyfin_cleanup_done`, …).

**Collections:** removing a movie that belongs to a BoxSet (for example one made by CollectionImport) leaves that collection smaller, or empty if it was the only member. Media Vote does not edit or delete collections.

---

## Radarr, Sonarr and Jellyseerr

Configure them under **Settings → Sonarr, Radarr and Jellyseerr**. Add one entry per server: several of each kind are supported (for example *Sonarr* and *Sonarr Anime*); every enabled instance is tried. Each has a **Test connection** button, and the Status tab shows their state read-only.

- **Base URL:** for example `http://192.168.1.20:8989`. Include any URL base, such as `http://host/sonarr`.
- **API key:** stored only in the plugin configuration. It is never logged, never sent back to the browser in full (shown as `••••••` plus the last 4 characters), and never in the repository. It is sent in the `X-Api-Key` header, never in a URL. Leaving the masked value unchanged keeps the saved key.

| App | Exact API calls |
|---|---|
| Radarr (v3 API: Radarr 3–5) | List: `GET /api/v3/movie`. Re-check / verify: `GET /api/v3/movie/{id}` (404 = gone). Delete: `DELETE /api/v3/movie/{id}?deleteFiles=true&addImportExclusion=true\|false`. Status: `GET /api/v3/system/status`, `GET /api/v3/rootfolder`, `GET /api/v3/diskspace`. |
| Sonarr (v3 API: Sonarr 3 and 4) | List: `GET /api/v3/series`. Re-check / verify: `GET /api/v3/series/{id}` (404 = gone). Delete: `DELETE /api/v3/series/{id}?deleteFiles=true&addImportListExclusion=true\|false`. Status: as Radarr. |
| Jellyseerr (optional) | Lookup: `GET /api/v1/movie/{tmdbId}` or `GET /api/v1/tv/{tmdbId}` → `mediaInfo.id`. Remove: `DELETE /api/v1/media/{mediaId}`, so the title can be requested again (the key needs *Manage Requests*). Test: `GET /api/v1/auth/me`, `GET /api/v1/status`. |

- The parameter names differ on purpose: Radarr's is `addImportExclusion`, Sonarr's is `addImportListExclusion`. Both default to on in Settings, so lists do not add the title back.
- **Verified against the source**: Sonarr's v3 branch and v4, Radarr v5 and current develop. Two details matter:
  - Both answer the DELETE as soon as the database row is gone, and delete (or recycle-bin) the files afterwards in the background; a failure there is only written to *their* log. That is why Media Vote verifies the entry is gone **and** checks the folder disappears before Jellyfin forgets the title.
  - Sonarr v4 answers HTTP 500 (not 404) to a DELETE for an id that no longer exists. Media Vote always re-checks with a GET first.
- **Unauthorised, unreachable or timed-out** requests (30 seconds) are reported with a clear message; the app's own error text is shown verbatim.
- Covered by unit tests with mocked Sonarr/Radarr/Jellyseerr servers (several instances, exact URLs and parameters, read-only guard, delete then verify, failure isolation). It has **not** been run against your real instances.

---

## Why deletion goes only through Sonarr/Radarr (exFAT)

Many home servers keep media on external drives formatted **exFAT** (ZimaOS setups often do). exFAT has no Unix owners or permissions: the kernel shows every file and folder with the owner and mode given by the mount options. Mounted with `fmask=0022,dmask=0022` and no `uid`/`gid`, everything is permanently **root:root, mode 755**, and `chown`/`chmod` are impossible ("Operation not permitted", even with sudo).

Jellyfin in the linuxserver image runs as the `PUID`/`PGID` user, not root, so on such drives it can **never** write. 1.0.x's safety check reported exactly that (`Jellyfin (uid …) cannot write to "…": permission denied`) and correctly deleted nothing.

Rather than asking you to change mounts, owners or the container's PUID/PGID, Media Vote now leaves all file removal to **Sonarr and Radarr**, which already write to those drives (they import your downloads there). Media Vote:
- never deletes, moves or changes a media file (unit tests scan the compiled plugin to prove it);
- only checks, read-only, whether a title's folder still exists;
- shows "Media folders: not writable by Jellyfin — expected and fine" on the Status tab instead of treating it as a problem.

**`index.html`:** Media Vote never writes to Jellyfin's web folder. The banner and the Vote tab need **File Transformation**. Without it, they are unavailable, while the admin page, the standalone page and the sidebar page still work.

## Supported and unsupported clients

| Client | Works? | How |
|---|---|---|
| Official **Jellyfin app on iPhone and iPad** | ✅ | The app is an Expo/React Native wrapper. Its `NativeShellWebView` loads the server URL (`source={{ uri: server.urlString }}`) in a WKWebView, so the server's injected web client runs. |
| Official **Jellyfin app on Android phones and tablets** | ✅ | `WebViewFragment` calls `loadUrl("<server>/")`, which loads the server's jellyfin-web in an Android WebView. |
| **Desktop browsers** (Chrome, Edge, Firefox, Safari) | ✅ | Standard jellyfin-web. |
| **Jellyfin Desktop** (and current Jellyfin Media Player) | ✅ | Qt WebEngine. After connecting it navigates to the server's web client (`find-webclient.js` → `window.location = resolvedUrl`). Old JMP builds configured to use a *bundled* web client will not show the banner, tab or page; use the standalone page. |
| Swiftfin, Streamyfin, Android TV, Roku, Tizen/webOS apps, Kodi | ❌ | Native clients that do not run jellyfin-web, so none of the entry points appear. They are not used on this server, so no effort was spent on them. Users of those apps can open `https://<server>/MediaVote/vote` in a browser. |

**Limits inside the mobile apps:**
- **No hard refresh:** fully close and reopen the app after installing or updating; sign out and back in if the banner still does not appear.
- **Files:** CSV downloads (admin) may not work in the app's web view; use a browser.
- **Hardware back button (Android):** the system back button navigates away from the home page, which also closes the voting overlay.

**Web-view-safe by design:**
- No `window.open`, pop-ups, new tabs, `alert`/`confirm`/`prompt`, clipboard or fullscreen APIs. All dialogs are in-page.
- Nothing relies on hover, right-click or shortcuts.
- Nothing important lives in `localStorage`; all vote state is on the server.
- Links to titles use in-app hash routes (`#/details?id=…`).
- Touch-first:
  - 44 px tap targets and 16 px inputs, so iOS does not zoom.
  - Safe-area insets respected, with a full-screen overlay sized with `dvh` and `visualViewport` so the on-screen keyboard does not hide the search box.
  - Background scroll is locked while the overlay is open.
  - Posters load lazily and lists are paginated, 30 titles at a time with *load more* and auto-load.
- On desktop: a focus trap inside the overlay, Escape closes it, and buttons and live regions are labelled for screen readers.

---

## Compatibility with other plugins

Media Vote is **purely additive and order-independent**:
- It never writes another plugin's settings, and never edits files.
- It registers one in-memory File Transformation callback for `index.html`, and only while the banner or the Vote tab is on. The callback:
  - only inserts its own marked `<script>` once before `</body>`;
  - **never returns less content than it received**: on any problem it returns the original unchanged;
  - checks the kill switch on every call;
  - must pass a self-test (sample HTML → original plus exactly one tag) before it is registered.
- Everything is namespaced `mediavote-`, and the only global is `window.MediaVote`.
- There is no global CSS, and colours are read from the active theme.
- Nothing of jellyfin-web or other plugins is patched.
- The client script wraps every handler in try/catch. After repeated failures it disconnects its observer and timers and stops. A `404`/`500` for `client.js` is harmless.
- Optional integrations are found at runtime by reflection, so there is no hard dependency on any other plugin, and Media Vote does not reference Newtonsoft.Json.
- The DLL references only Jellyfin 10.11.10 and .NET 9 framework assemblies.

| Plugin | Notes |
|---|---|
| File Transformation 3.0.0.0 | `PluginInterface.RegisterTransformation(JObject)` / `RemoveTransformation(Guid)`. The callback payload `{"contents": …}` is deserialised by FT with Newtonsoft `JObject.ToObject`. Media Vote's `TransformPayload` pins the name with `[DataMember(Name="contents")]`, and a unit test deserialises it with Newtonsoft 13.0.1 exactly as FT does. |
| Custom Tabs 0.2.10.0 | **Read-only.** Media Vote never changes its settings. See [The Vote tab](#the-vote-tab-custom-tabs-manual-setup-and-why) for the apostrophe/`$`/`\` hazard of 0.2.x. 0.3.x renders tab content at runtime, so the hazard warning is not shown for it. |
| Plugin Pages 3.0.0.0 | `RegisterPage` / `RemovePage`, in memory, only while on. The `IsEnabled` callback never throws, and is false in preview mode. |
| Home Screen Sections 3.0.0.0 | `RegisterSection`, only while on. The results callback never throws, and returns nothing when off, disabled, or in preview mode for non-admins. |
| Media Bar 2.4.13.0 | The banner sits above the slideshow (`z-index` 11 > slides ≤ 10). |
| Jellyfin Enhanced 12.x | Key events inside Media Vote's UI are stopped at `window` capture, so JE shortcuts don't fire there. Media Vote registers no global shortcuts. |
| JavaScript Injector, Streamyfin, Playback Reporting, Reports, Intro Skipper, WhisperSubs, others | No interaction. Playback Reporting's database is only ever read (optional). |

---

## Extra features: implemented and skipped

| Feature | Status |
|---|---|
| Minimum turnout (report to admin, *Resolve anyway* override) | ✅ |
| Protected titles and libraries (never votable, never deletable; re-checked before deletion) | ✅ |
| "Keep" votes that subtract from the score (admin toggle, own allowance) | ✅ |
| Build filters: never watched · not watched in X months · largest files | ✅ |
| Closing-soon reminder and "you haven't voted yet" indicator | ✅ |
| CSV export | ✅ |
| Dry run (exact Sonarr/Radarr entries and GB that would go; read-only) | ✅ |
| Several Sonarr/Radarr/Jellyseerr instances (for example an Anime Sonarr) | ✅ 1.0.2.0 |
| "Removable via Sonarr/Radarr" check before the vote opens (badge, filter, manual-removal list) | ✅ 1.0.2.0 |
| **G** Deadline picker: quick choices, flexible row, exact fallback, plain-words result, server-side DST-correct computation, remembered choice | ✅ 1.1.0.0 |
| **H** One shared media browser (admin hand-pick, overlay, Vote tab, sidebar page): grid/list, server-side filters/sorts/paging, card details, picker tray, voting mode, watch-details setting | ✅ 1.1.0.0 |
| **I** Drives: mount-table detection, grouping by filesystem, shared-filesystem warning, names, thresholds, projections, 60 s cache, "unknown" | ✅ 1.1.0.0 (Linux only; see limitations) |
| **J1** Storage tab | ✅ 1.1.0.0 |
| **J2** Free-space target ("until 8TB 1 has 500 GB free" / "below 85 % full") | ✅ 1.1.0.0 (ties not special-cased; see [Vote rules](#vote-rules)) |
| **J3** Approval step for deletes (default on, optional auto-approve) | ✅ 1.1.0.0 |
| **J4** Smart presets (6) | ✅ 1.1.0.0 (admin hand-pick browser; voters get the same filters without the preset buttons) |
| **J5** Tie-break rules (larger size, watched longest ago, added longest ago) | ✅ 1.1.0.0 |
| **J6** Templates and recurring votes (weekly/monthly drafts the admin reviews; never auto-open) | ✅ 1.1.0.0 |
| **J7** History and statistics (space freed, turnout, most-voted titles, participation) | ✅ 1.1.0.0 (tables, no charts) |
| **J8** Result announcements with sizes, total freed, "Request it again" link | ✅ 1.1.0.0 (link needs the public Jellyseerr address and a TMDb id) |
| **J9** Webhook extras: non-voters in closing-soon, approval needed, draft ready | ✅ 1.1.0.0 |
| Several simultaneous open votes | ⛔ Skipped on purpose: one open vote at a time keeps the banner and tab unambiguous. |
| Push notifications to phones | ⛔ Skipped (no plugin API for it; use the webhook). |
| Translations | ⛔ English only. |

---

## Known limitations

- **Untested live:** the Sonarr/Radarr removal (1.0.2.0) and everything new in 1.1.0.0 have **not** been run against real Sonarr, Radarr or Jellyseerr instances, real drives, or a live Jellyfin server; see the next section. Do the single-title test in the [checklist](#manual-test-checklist) first.
- **Drive detection needs Linux** (`/proc/self/mountinfo`, as in the Docker image). Elsewhere drives show as unknown, the drive filter and bars are missing, and a free-space target selects nothing. Usage comes from the filesystem (like `df`), cached for about a minute, so it can lag just after a removal. A Docker volume mounted from a folder of a bigger disk shows that whole disk.
- **Free-space target and ties:** in target mode, equal scores are taken in the ranking's order; the tie rules apply only to "top N".
- **Frozen lists:** watch details, drives and removability are captured when a vote is created; votes created before 1.1.0.0 have no drive or "last played by" details.
- **Recurring templates** create a draft at the chosen time (checked every 30 seconds while Jellyfin runs, or at the next start); they never open a vote by themselves. The deadline rule is re-applied when you press *Review and open*.
- **Statistics** only cover votes stored by this plugin (history starts with your first vote) and "space freed" uses Sonarr/Radarr's size when known, otherwise Jellyfin's.
- **Removal needs Sonarr/Radarr:** titles they don't manage (home videos, things added by hand) can only be removed by you; the report lists them with their folder.
- **Sonarr/Radarr delete files in the background.** If their file delete fails they only log it; Media Vote notices (the folder is still there an hour later) and flags it, but cannot fix it. A recycle bin configured in Sonarr/Radarr moves files instead of freeing space.
- **Shared folders:** Sonarr/Radarr refuse to delete files of a title whose folder contains another title's folder (they log an error); Media Vote then flags "files still on disk".
- **Jellyfin version:** each build works only on the Jellyfin version it was built for (10.11.10 here); see [Jellyfin version requirement](#jellyfin-version-requirement).
- **File Transformation:** the banner and the Vote tab need it. Media Vote never edits `index.html` itself.
- **Custom Tabs 0.2.x** pastes tab HTML into the home page's JavaScript. Media Vote cannot make other tabs safe; it can only warn.
- **Plugin Pages** cannot restrict a page to admins, so in preview mode the sidebar page is hidden from everyone.
- **Home Screen Sections** has no removal API. A registered row stays (empty) until the next restart after being turned off.
- **After the script stops itself** for a non-admin (preview mode), logging in as an admin in the same tab needs a page reload.
- **Play statistics** come from Jellyfin's per-user data (titles marked played), plus optional Playback Reporting data.
- **Downloads in the app:** CSV download may not work inside the iOS/Android app; use a browser.
- **Android back button:** it leaves the home page.

---

## What has and has not been tested

**Automated, in this repository (all passing):**
- **Unit tests** (`dotnet test`):
  - vote rules, ties, deadlines, time zones, storage, webhooks, CSV;
  - **new in 1.1.0.0:**
    - deadline calculator: every quick choice, flexible hours/days/weeks, exact times, "in 7 days at 20:00" across the 25 Oct 2026 clock change (20:00 GMT, not 19:00), "in 6 hours" across it (six real hours), the spring-forward gap, the repeated hour, past times, the plain-words description with BST/GMT;
    - drives: parsing a real-shaped container `mountinfo` (pseudo filesystems skipped, `\040` spaces), longest-prefix matching on folder boundaries (`/LM1` never matches `/LM10`), grouping by filesystem, the `/LM1` + `/LM2` same-filesystem warning, default and admin names, percentages like `df`, warning/critical levels, projections;
    - the browse engine: every filter, sort and preset, hidden tallies and hidden watch details (filters and sorts ignored, so nothing leaks; *counts only* ignores the time-based ones), paging, facets, per-drive bars;
    - tie rules, free-space and percentage targets (including unknown drive usage → nothing selected, and the safety maximum), the approval flow (prepare read-only, approve, reject, auto-approve timer, kill switch), recurring template dates (weekly, monthly, short months keeping the chosen day) and "only a draft, once", statistics, webhook texts (non-voters, approval), "Request it again" links;
  - **new in 1.0.2.0:**
    - matching: provider ids first, title + year + folder only without ids, ambiguous ids or duplicates across instances → not matched, unreachable instance → unknown;
    - the removal flow against an in-memory Sonarr/Radarr and against **mocked HTTP servers** with the real clients: several instances, exact URLs and parameter names (`addImportExclusion` vs `addImportListExclusion`), delete then verify, verbatim error messages, one failure never stopping the others, Jellyseerr before the delete, maximum per vote, kill switch, never deleting twice on Retry, files still on disk → Jellyfin entry kept, then forgotten once they are gone, flagged after an hour;
    - **notify-only and dry runs make no state-changing call** (read-only client throws on anything but GET);
    - IL scans of the compiled plugin: no file delete/move outside its own data folder, `DeleteItem` only from the gateway, `DeleteFileLocation` only ever set to `false`;
    - settings migration from 1.0.1.0 (single URLs → instances, keys not duplicated) and an XML round-trip like Jellyfin's;
    - old 1.0.x results ("DeleteAutomatically", "PreflightFailed") still load;
  - from 1.0.1.0: fresh-install defaults, preview and kill-switch rules, the transformation callback never returning less than its input, the File Transformation payload, self-check isolation, readable errors, Custom Tabs leftover detection, no writes to other plugins' settings, `targetAbi` matching the compiled Jellyfin version.
- **Binary compatibility** (`tools/ApiCompat`): the DLL JIT-compiles against the real Jellyfin 10.11.10 assemblies, including the new members (`IProviderManager.QueueRefresh`, `MetadataRefreshOptions`, `DirectoryService`, `DeleteOptions.DeleteFromExternalProvider`, `VirtualFolderInfo.Locations`).
- **Custom Tabs regression** (`tests/client/customtabs-chunk.js`): Custom Tabs 0.2.10.0's transformation on the real 10.11.10 `home-html` chunk; the 1.0.0.0 tab → `SyntaxError`, the current snippet → valid page.
- **Headless Chromium smoke tests** (mock jellyfin-web):
  - **new in 1.1.0.0:** the shared browser in the voting overlay (grid/list switch, filters and sorts sent to the server, drive bars, "Never watched", lazy loading), the admin hand-pick browser (presets, select all in filter, tray totals per drive), the deadline picker (chips, flexible row with the time box hidden for hours, exact fallback, server description, remembered choice), target fields, templates, approval alerts with Approve/Reject, Storage and History tabs, new settings, results with sizes and request links;
  - **new:** the vote-results banner stays closed after ✕, a home re-render, a fresh status, and a failing dismiss call (this test fails on the 1.0.1.0 script); a re-rendered Vote tab is filled again;
  - **new (admin page):** instance editor (add an Anime Sonarr, masked key kept, test uses the saved key), removable badges and filter in the preview, unmanaged titles left out of delete votes, safety maximum shown, read-only Sonarr/Radarr status card, result wording (verbatim errors, manual-removal list, files still on disk, Retry, no pre-flight wording);
  - from 1.0.1.0: preview mode and "disabled" render nothing, `client.js` 404/500 harmless, late mounts filled, independent admin sections with Retry, the full user picker, Go live, the snippet card.

**Not tested, because it needs your live server, instances or devices:**
- **1.1.0.0:** drive detection and usage on your real ZimaOS drives (`/LM1`, `/LM2`, `/Media`); the browser's speed on your full library; the picker and browser on real phones; the approval flow and auto-approve timer on a running server; recurring drafts firing on a running server; Jellyseerr "Request it again" links on your Jellyseerr. There is also no automated test of `DriveService` itself (it reads the real `/proc/self/mountinfo` and `statvfs`); its parts (parsing, grouping, maths) are tested.
- Real Sonarr, Radarr and Jellyseerr: the actual delete, how long their background file delete takes on your drives, recycle-bin behaviour, the root-folder free space they report.
- Jellyfin 10.11.10 forgetting the removed title (`DeleteItem` with `DeleteFileLocation = false`) and the library refresh request.
- Installing 1.0.2.0 / 1.1.0.0 and the start-up task on Jellyfin 10.11.10; `OnUninstalling` being called.
- The real jellyfin-web DOM with Media Bar, Home Screen Sections, Jellyfin Enhanced and Custom Tabs; official iOS/Android apps and Jellyfin Desktop; Discord webhooks.

---

## Manual test checklist

Do these **in order**. The first steps are the safest. Stop at the first problem: create the `DISABLE` file and report it.

1. **If you had 1.0.0.0:** delete its Vote tab in *Custom Tabs* and Save. Check that the home page is normal for you and for a non-admin.
2. **Install / update** to 1.1.0.0 from the repository and restart Jellyfin. Check that the home page, Media Bar, HSS, Jellyfin Enhanced and Custom Tabs still look and work exactly as before, for both an admin and a non-admin.
3. **Status & setup:** every self-check line is OK (Users shows your real user count; "Media folders" may say *not writable by Jellyfin — expected and fine*). *Preview mode is ON* on a fresh install (an update from 1.0.1.0 keeps your choice). The Custom Tabs check shows no warnings (after step 1).
4. **Kill switch:** run `touch /DATA/AppData/jellyfin/config/data/mediavote/DISABLE` and restart. Check:
   - the Status tab shows "Disabled by file";
   - `https://<server>/MediaVote/client.js` returns only a comment;
   - `/MediaVote/vote` shows nothing for a non-admin.

   Then `rm /DATA/AppData/jellyfin/config/data/mediavote/DISABLE`, restart, and check the Status tab no longer says "Disabled by file".
5. **Entry points one at a time, in preview mode** (*Settings*, save, hard-refresh each time):
   1. **Banner:** as admin it is the top element of the home page, above Media Bar and HSS; as a non-admin (private window, phone app): nothing at all.
   2. **Sidebar page:** in nobody's menu while in preview; as admin `#/userpluginsettings.html?pageUrl=/MediaVote/page` shows the voting screen or "No active vote right now".
   3. **HSS row** (also enable it in HSS): admins see it, non-admins don't.
6. **Sonarr / Radarr connections:** *Settings → Sonarr, Radarr and Jellyseerr*. Your 1.0.1.0 URLs appear as instances. Add any extra instance (for example an Anime Sonarr). Press **Test connection** on each (*Connected: Sonarr 4.x*), save, then on *Status & setup* check each instance is OK with its version, root folders and free space.
7. **Notify-only test vote** for yourself only: *New vote*, Preview (titles Sonarr/Radarr don't manage carry a badge; *removable only* filters them), create, vote on the standalone page, **Close now**. The report names each winner's Sonarr/Radarr entry. Nothing changes anywhere (check the title is still in Sonarr/Radarr and on disk).
8. **Results banner:** with the banner on, after that vote closes the results banner appears; press ✕, navigate around and reload: it stays closed.
9. **Dry run:** create a *Delete via Sonarr/Radarr* vote with **one small, harmless title** (for example a short film you can re-download), N = 1, tick the confirmation. Vote for it, then **Dry run**: it names the exact entry (instance and id) and the GB that would go, and says only read-only requests were made. Check the audit log has no `arr_` entries.
10. **One real removal:** **Close now** on that vote. Check, in this order:
    - the result says **Removed by Sonarr/Radarr**, the audit log has `arr_delete_requested` then `arr_removed`;
    - the title is gone from Sonarr/Radarr (and, with the exclusion on, listed under its import (list) exclusions);
    - its folder is gone from the drive (ZimaOS Files or `ls`);
    - within a minute or two the result shows *Jellyfin entry removed*, and the title is gone from Jellyfin for you and for a non-admin;
    - other titles in the same library are untouched.

    If it says *FILES STILL ON DISK* after an hour, check Sonarr/Radarr's log (*System → Logs*).
11. **1.1.0.0: drives.** *Settings → Drives* lists **8TB 1** (`/LM1`), **8TB 2** (`/LM2`) and **System drive** (`/Media`) with no "SAME filesystem" warning. On the *Storage* tab, compare each drive's used/free/total with ZimaOS's storage page or `df -h /LM1 /LM2 /Media` inside the container (`docker exec jellyfin df -h /LM1 /LM2 /Media`): they should match to within a GB or so. Rename one drive, save, and check the new name appears on title cards.
12. **1.1.0.0: deadline picker** on a desktop browser **and on a phone**: try every quick choice and the flexible row; check the plain-words line (for a vote created before 25 Oct, "in 7 days at 20:00" should say **GMT** if it lands after the clock change). Reload: your last choice is pre-selected.
13. **1.1.0.0: browser.** As admin, *New vote → Hand-picked titles*: switch grid/list, try each preset and a few filters/sorts, **Select all in this filter**, and check the tray's per-drive totals. As a voter (in preview, yourself), open the overlay and check cards show size, drive bar, last played (or *Never watched*), plays and date added. Set *Watch details voters see* to *counts only*, reload as a non-admin later after Go live: no names or times, and no "not watched in X months" filter.
14. **1.1.0.0: approval flow** with one harmless title (as in steps 9–10): create a *Delete via Sonarr/Radarr* vote with N = 1, close it. The *Votes* tab shows "waiting for your approval"; **Details** lists the exact Sonarr/Radarr entry; the title is still in Sonarr/Radarr and on disk. **Reject** once on a first vote (nothing removed, `removal_rejected` in the audit log), then repeat with a second vote and **Approve**: the removal runs as in step 10.
15. **1.1.0.0: results** show each title's size and the total freed; with *Public Jellyseerr address* set, removed titles have a working **Request it again** link.
16. **Go live**, after the in-page confirmation. Non-admins now see the enabled entry points (ask them to close and reopen the app). Check on iPhone, iPad, Android, a desktop browser and Jellyfin Desktop: banner position, voting, the overlay and the keyboard; Jellyfin Enhanced shortcuts don't fire while typing in Media Vote.
17. **Vote tab (optional):** add the exact snippet in Custom Tabs, tick the setting, reload. The home page still loads, and the tab shows the voting screen or "No active vote right now".
18. **Back to preview** hides everything from non-admins again.
19. **Uninstall test (optional):** uninstall from the dashboard and restart. The banner, sidebar page and script disappear, and the home page and other plugins are fine.

---

## REST API

All routes are relative to the server base URL, and responses are camelCase JSON. Errors are `{ "error": code, "message": readable text }`, with codes such as `jellyfin_api_error`, `jellyfin_version_mismatch`, `server_error`, `preview` and `disabled`.

**User** (`[Authorize]`; acts only as the token's user; refused with `403 preview` for non-admins in preview mode and `403 disabled` with the kill switch):

| Method & route | Purpose |
|---|---|
| `GET /MediaVote/Status` | Active vote summary for this user, last result, entry points, plus `preview`, `hidden` and `disabled` flags. |
| `GET /MediaVote/Items?…` | Paged votable titles with facets (libraries, genres, drives). Query: `search`, `type`, `library`, `drive`, `genre`, `watched` (`never`·`notsince`·`mine`·`notmine`), `months`, `addedBeforeMonths`, `halfFinished`, `minGb`, `maxGb`, `minYear`, `maxYear`, `sort` (`name`·`votes`·`size`·`added`·`year`·`drive`·`lastplayed`·`playcount`·`never`), `descending`, `mineOnly`, `page`, `pageSize` (max 100). |
| `POST /MediaVote/Vote` · `Unvote` · `Move` | Cast, withdraw or move a vote. |
| `POST /MediaVote/Results/{id}/Dismiss` | Hide the result banner (the client also remembers it locally, so it never comes back). |

**Anonymous:** `client.js` (inert while `DISABLE` exists), `client.css`, `page`, `vote`.

**Admin** (`RequiresElevation`), all under `/MediaVote/Admin/`:

| Method | Routes |
|---|---|
| GET | `Overview` (includes self-check, kill switch, preview, entry points, Custom Tabs findings and snippet, maximum titles per vote, votes awaiting approval, pending recurring drafts), `Users`, `Libraries`, `Search` (with `removable`), `Browse` and `Browse/Ids` (the admin browser; same query as `Items` plus `removableOnly`), `Drives`, `Storage`, `Stats`, `Templates`, `Sessions/{id}`, `Sessions/{id}/Csv`, `Audit`, `Settings`, `ArrStatus` (read-only state of every Sonarr/Radarr/Jellyseerr instance) |
| DELETE | `Templates/{id}` |
| POST | `Deadline` (`{ kind, preset, amount, unit, time, local }` → UTC time and plain-words description; `Sessions` and `Extend` accept the same `deadline` object), `Preview` (with removability counts), `Sessions`, `Sessions/{id}/Extend`, `/Close`, `/Cancel`, `/DryRun`, `/BreakTie`, `/Retry`, `/ResolveAnyway`, `/Approve`, `/Reject`, `Templates` (create or update), `Templates/{id}/Skip`, `Settings` (with `arrInstances`), `Test/{Radarr\|Sonarr\|Jellyseerr}` (`{ id, url, apiKey }`, read-only), `SelfCheck`, `Visibility` (`{ "previewOnly": false }` = Go live), `Reapply` |

---

## Development, CI and releases

```
src/Jellyfin.Plugin.MediaVote/
  Core/           pure logic: rules, tallies, winner planner (tie rules, targets), executor, deadline calculator, drives
                  (mount table, grouping, maths), browse engine, templates, statistics, access rules, self-check, error text
  Storage/        JSON vote store, template store and audit log
  Integrations/   Radarr/Sonarr/Jellyseerr clients, webhook
  Services/       Jellyfin adapters, drive service, kill switch, API self-check, deadline timer, scheduled tasks
  Web/            File Transformation callback, entry points (Plugin Pages, HSS), Custom Tabs read-only check
  Api/            REST controllers and error filter
  Assets/         client.js, client.css, page.html, standalone.html
  Configuration/  settings and the admin page
tests/Jellyfin.Plugin.MediaVote.Tests/   xUnit tests
tests/client/                            Chromium smoke tests and the Custom Tabs chunk regression test
tools/ApiCompat/                         binary compatibility checker (JIT-compiles the DLL against a Jellyfin version)
```

```sh
python3 .github/scripts/check_versions.py
dotnet build -c Release && dotnet test -c Release
dotnet build tools/ApiCompat -c Release -p:JellyfinVersion=10.11.10
dotnet tools/ApiCompat/bin/Release/net9.0/ApiCompat.dll src/Jellyfin.Plugin.MediaVote/bin/Release/net9.0/Jellyfin.Plugin.MediaVote.dll
dotnet tools/ApiCompat/bin/Release/net9.0/ApiCompat.dll --list src/Jellyfin.Plugin.MediaVote/bin/Release/net9.0/Jellyfin.Plugin.MediaVote.dll  # every Jellyfin member used
node tests/client/customtabs-chunk.js
NODE_PATH=$(npm root -g) node tests/client/smoke.js
NODE_PATH=$(npm root -g) node tests/client/admin-smoke.js
```

CI (`.github/workflows/build.yml`) runs:
- the version check;
- the build and unit tests;
- the **blocking** binary check against the `targetAbi` version;
- the JS parse checks, the Custom Tabs regression test and the smoke tests;
- a **non-blocking** job against the latest published 10.11.x.

The release workflow also runs the version check and the binary check before publishing.

### Publishing a release

Releases are stacked: **1.0.2.0 first, then 1.1.0.0.** Each tag publishes one version; `manifest.json` keeps every published version, so Jellyfin offers the newest one that fits the server.

**1.0.2.0** (if not already done):
1. Merge the 1.0.2.0 PR into `main`.
2. Tag and push:
   ```sh
   git fetch origin && git checkout main && git pull
   git tag -a v1.0.2.0 -m "Media Vote 1.0.2.0: delete only through Sonarr/Radarr; result banner fix"
   git push origin v1.0.2.0
   ```
3. Wait for the *Release* workflow to go green (Actions tab). `manifest.json` on `main` now lists 1.0.2.0.

**1.1.0.0:**
1. After 1.0.2.0 is merged, the 1.1.0.0 PR retargets to `main` by itself (if GitHub doesn't, change its base to `main`). Pull `main` into it if GitHub reports a conflict (for example in `manifest.json`, which the 1.0.2.0 release changed; keep `main`'s version), let CI go green, then merge.
2. Tag and push:
   ```sh
   git fetch origin && git checkout main && git pull
   git tag -a v1.1.0.0 -m "Media Vote 1.1.0.0: shared media browser, deadline picker, drives, approval step, templates"
   git push origin v1.1.0.0
   ```
3. `.github/workflows/release.yml` then:
   - checks versions, tests and runs the binary check;
   - builds `media-vote_1.1.0.0.zip`;
   - creates the GitHub Release (notes from the tag message; for a lightweight tag, from the `build.yaml` changelog);
   - adds 1.1.0.0 (`targetAbi 10.11.10.0`, MD5 checksum) to `manifest.json` on `main`, next to the earlier versions.
4. Check the Actions tab. When it is green, Jellyfin offers 1.1.0.0 as an update (*Dashboard → Plugins*, then restart). Work through the 1.1.0.0 steps of the [manual test checklist](#manual-test-checklist).

---

### Jellyfin 12

The csproj has a `JellyfinTarget` switch (`-p:JellyfinTarget=12`), and the client's DOM knowledge is isolated in `hostAdapters`. A Jellyfin 12 build needs a new host adapter, its own `targetAbi`, and the same version and binary checks.

---

## Sources this plugin relies on

- **Jellyfin plugin template:** the net9.0 / `targetAbi 10.11.0.0` / `Jellyfin.Controller` setup, `IHasWebPages` config page pattern and `build.yaml` → [jellyfin/jellyfin-plugin-template](https://github.com/jellyfin/jellyfin-plugin-template)
- **Jellyfin 10.11.1 server source:**
  - `MediaBrowser.Common/Api/Policies.cs` (`RequiresElevation`);
  - `Jellyfin.Api/Constants/InternalClaimTypes.cs` (`Jellyfin-UserId`);
  - `LibraryController.DeleteItem` and `LibraryManager.DeleteItem` (the deletion path);
  - `BasePlugin` (the plugin data folder is versioned, hence Media Vote's own data folder)
  - → [jellyfin/jellyfin @ v10.11.1](https://github.com/jellyfin/jellyfin/tree/v10.11.1)
- **jellyfin-web 10.11.1:** `controllers/home.html`, `hometab.js` (the home DOM) → [jellyfin/jellyfin-web @ v10.11.1](https://github.com/jellyfin/jellyfin-web/tree/v10.11.1)
- **NuGet package versions** → [Jellyfin.Controller on NuGet](https://www.nuget.org/packages/Jellyfin.Controller)
- **File Transformation:** `PluginInterface.RegisterTransformation(JObject)`, payload fields, callback invocation, middleware pipeline → [IAmParadox27/jellyfin-plugin-file-transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
- **Plugin Pages:** `PluginInterface.RegisterPage`, `PluginPage` fields, `IsEnabled` callback, page fragment loading → [IAmParadox27/jellyfin-plugin-pages](https://github.com/IAmParadox27/jellyfin-plugin-pages)
- **Home Screen Sections:** `PluginInterface.RegisterSection`, `SectionRegisterPayload`, `loadSections` patch and its rendering into `.sections` → [IAmParadox27/jellyfin-plugin-home-sections](https://github.com/IAmParadox27/jellyfin-plugin-home-sections)
- **Custom Tabs:** `PluginConfiguration.Tabs`, `addCustomTabs.js` (scripts executed in tab HTML) → [IAmParadox27/jellyfin-plugin-custom-tabs](https://github.com/IAmParadox27/jellyfin-plugin-custom-tabs)
- **Media Bar:** `slideshowpure.js` / `slideshowpure.css` (`#slides-container` on `<body>`, `.homeSectionsContainer` margin and z-index) → [IAmParadox27/jellyfin-plugin-media-bar](https://github.com/IAmParadox27/jellyfin-plugin-media-bar)
- **Jellyfin Enhanced:** `js/enhanced/events.js` (`JE.keyListener`, a capture-phase document listener ignoring INPUT/TEXTAREA) → [n00bcodr/Jellyfin-Enhanced](https://github.com/n00bcodr/Jellyfin-Enhanced)
- **Official iOS app:** `components/NativeShellWebView.tsx` → [jellyfin/jellyfin-ios](https://github.com/jellyfin/jellyfin-ios)
- **Official Android app:** `webapp/WebViewFragment.kt` → [jellyfin/jellyfin-android](https://github.com/jellyfin/jellyfin-android)
- **Jellyfin Desktop:** `native/find-webclient.js`, `SettingsComponent.cpp` → [jellyfin/jellyfin-desktop](https://github.com/jellyfin/jellyfin-desktop)
- **Radarr API v3** → [OpenAPI spec](https://github.com/Radarr/Radarr/blob/develop/src/Radarr.Api.V3/openapi.json)
- **Sonarr API v3** → [OpenAPI spec](https://github.com/Sonarr/Sonarr/blob/develop/src/Sonarr.Api.V3/openapi.json)
- **Jellyseerr / Seerr API v1** → [seerr-api.yml](https://github.com/seerr-team/seerr/blob/develop/seerr-api.yml)
- **Jellyfin plugin repository manifest format and JPRM** → [oddstr13/jellyfin-plugin-repository-manager](https://github.com/oddstr13/jellyfin-plugin-repository-manager)
- **1.0.1.0 additions:**
  - **Jellyfin 10.11.10:** `Jellyfin.Controller`/`Jellyfin.Model` 10.11.10 on NuGet, and the server DLLs from `linuxserver/jellyfin:10.11.10`. Their public API was diffed against 10.11.0 (`IUserManager.Users` → `GetUsers()`), and `BasePlugin.OnUninstalling()` was confirmed to exist.
  - **Custom Tabs 0.2.10.0** (tag `0.2.10.0`): `Helpers/TransformationPatches.cs` (`HomeHtmlChunk`), `Inject/addCustomTabs.js`, `Configuration/PluginConfiguration.cs` → [IAmParadox27/jellyfin-plugin-custom-tabs](https://github.com/IAmParadox27/jellyfin-plugin-custom-tabs/tree/0.2.10.0)
  - **jellyfin-web 10.11.10** build: `home-html.8ce38bc7d6dc073656d4.chunk.js` from the linuxserver image (fixture in `tests/client/fixtures/`).
  - **File Transformation 3.0.0.0** (tag `3.0.0.0`): `Helpers/TransformationHelper.cs` (callback payload, null/empty handling), `PluginInterface.cs` (`RemoveTransformation`), `Infrastructure/FileTransformationMiddleware.cs` → [IAmParadox27/jellyfin-plugin-file-transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/tree/3.0.0.0)
  - **Plugin Pages 3.0.0.0** (`RemovePage`) and **Home Screen Sections 3.0.0.0** (no removal API), checked at their tags.
- **1.0.2.0 additions** (read in the source):
  - **Sonarr** `main` (v4.0.20) and branch `v3`: `src/Sonarr.Api.V3/Series/SeriesController.cs` (`DeleteSeries(int id, bool deleteFiles = false, bool addImportListExclusion = false)`), `SeriesModule.cs` (v3), `src/NzbDrone.Core/MediaFiles/MediaFileDeletionService.cs` (files deleted asynchronously by an `IHandleAsync<SeriesDeletedEvent>` handler), `RootFolderResource.cs` / `DiskSpaceResource.cs`, `SonarrErrorPipeline.cs` (error shapes) → [Sonarr/Sonarr](https://github.com/Sonarr/Sonarr)
  - **Radarr** `develop` and tag `v5.28.0.10274`: `src/Radarr.Api.V3/Movies/MovieController.cs` (`DeleteMovie(int id, bool deleteFiles = false, bool addImportExclusion = false)`), `MediaFileDeletionService.cs` (asynchronous), `MovieResource.cs` → [Radarr/Radarr](https://github.com/Radarr/Radarr)
  - **Jellyseerr/Seerr** `develop`: `server/routes/media.ts` (`DELETE /api/v1/media/{id}`, 204), `server/middleware/auth.ts` (`X-API-Key`) → [seerr-team/seerr](https://github.com/seerr-team/seerr)
  - **Jellyfin 10.11.10** (tag `v10.11.10`): `Emby.Server.Implementations/Library/LibraryManager.cs` `DeleteItem` (media files are deleted only when `DeleteFileLocation` is true), `MediaBrowser.Controller/Entities/Folder.cs` `ValidateChildrenInternal2` (a scan drops vanished folders with `DeleteFileLocation = false`; an empty library folder is skipped), `MediaBrowser.Providers/Manager/ProviderManager.cs` `QueueRefresh` → [jellyfin/jellyfin](https://github.com/jellyfin/jellyfin/tree/v10.11.10)

---

## License

See [LICENSE](LICENSE).
