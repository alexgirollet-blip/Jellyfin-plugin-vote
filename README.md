# Media Vote: a Jellyfin plugin

Let your users vote on which movies and shows to remove from your Jellyfin server.

An administrator starts a vote for selected users. While it is open, each eligible user sees a banner at the top of their Jellyfin home page; they can also use a **Vote** tab or a **Vote** sidebar page. Users spend a fixed number of votes on whole movies and whole series. When the deadline passes, the top **N** titles are either **reported to the admin** (the default) or **deleted automatically**: from Jellyfin, from disk, and with the matching Radarr/Sonarr/Jellyseerr entries unmonitored or excluded so nothing is downloaded again.

- **Target:** Jellyfin **10.11.x** (`targetAbi 10.11.0.0`, .NET 9). Compiled against `Jellyfin.Controller`/`Jellyfin.Model` **10.11.0**, the oldest 10.11 package, so the same build loads on 10.11.0 through every later 10.11.x, including the linuxserver/jellyfin:10.11.1 image.
- **Clients:** the official Jellyfin app on iPhone, iPad and Android; desktop browsers; Jellyfin Desktop. All of these load the server's jellyfin-web.
- **Status:** feature-complete for 10.11. It has **not yet been tested on a live server or real devices**; see [What has and has not been tested](#what-has-and-has-not-been-tested).

---

## Contents

1. [Install](#install)
2. [Entry points and how to enable each](#entry-points-and-how-to-enable-each)
3. [Admin guide](#admin-guide)
4. [User guide](#user-guide)
5. [Vote rules (counting, ties, eligibility, deadline)](#vote-rules)
6. [Resolution and deletion safety](#resolution-and-deletion-safety)
7. [Radarr, Sonarr and Jellyseerr](#radarr-sonarr-and-jellyseerr)
8. [Docker / ZimaOS permission notes](#docker--zimaos-permission-notes)
9. [Supported and unsupported clients](#supported-and-unsupported-clients)
10. [Compatibility with other plugins](#compatibility-with-other-plugins)
11. [Extra features: implemented and skipped](#extra-features-implemented-and-skipped)
12. [Known limitations](#known-limitations)
13. [What has and has not been tested](#what-has-and-has-not-been-tested)
14. [Manual test checklist](#manual-test-checklist)
15. [REST API](#rest-api)
16. [Development, releases and Jellyfin 12](#development-releases-and-jellyfin-12)
17. [Sources this plugin relies on](#sources-this-plugin-relies-on)

---

## Install

### Plugin repository URL

Paste this into **Jellyfin → Dashboard → Plugins → Repositories → +**:

```
https://raw.githubusercontent.com/alexgirollet-blip/Jellyfin-plugin-vote/main/manifest.json
```

> **Before the first install:** `manifest.json` lists no versions until the first release has been published. Merge this work into `main`, then publish a release as described in [Publishing a release](#publishing-a-release). The workflow builds the zip, attaches it to a GitHub Release and adds the version to `manifest.json` on `main`. From then on the URL above serves it.

### Steps

1. Jellyfin → **Dashboard → Plugins → Repositories** → **+** → Name `Media Vote`, URL as above → **Save**.
2. **Dashboard → Plugins → Catalog** → **Media Vote** (category *General*) → **Install**.
3. **Restart Jellyfin.** Plugins are loaded only at start-up. On ZimaOS, restart the Jellyfin app/container; on the server dashboard you can also use **Dashboard → Restart**.
4. **Recommended:** keep **File Transformation** installed (you already have 3.0.0.0). Media Vote uses it to add its script to the web client without modifying files. Without it, see [Docker / ZimaOS permission notes](#docker--zimaos-permission-notes).
5. **Desktop browsers:** do a hard refresh (Ctrl+F5 on Windows/Linux, Cmd+Shift+R on macOS), or clear the site data for your Jellyfin address.
6. **Jellyfin Desktop app:** quit it completely and reopen it.
7. **Phones and iPads (official Jellyfin app):** you cannot hard-refresh, so **fully close the app** (swipe it away from the app switcher) and reopen it. If the banner still does not appear, **sign out and sign back in** in the app. Tell your friends to do the same after every plugin install or update.
8. Open **Dashboard → Plugins → Media Vote** (also listed in the dashboard menu as *Media Vote*). The **Status & setup** tab shows whether the script injection worked and which optional plugins were detected.

**Updating:** the same as installing. Install the new version from the catalog, restart Jellyfin, then hard-refresh browsers and close/reopen the apps. Vote data is stored in `<Jellyfin data>/mediavote` (`/config/data/mediavote` in the linuxserver image), outside the versioned plugin folder, so it survives updates.

---

## Entry points and how to enable each

Every entry point opens **the same voting component** (`VoteApp` in `client.js`), so they all behave identically. Each one is a toggle under **Media Vote → Settings → Entry points**, and all are on by default. The banner can only be turned off if the Vote tab, the sidebar page or the standalone page is on; the server enforces this rule too.

| Entry point | Needs | How it is set up |
|---|---|---|
| **Home banner** (primary) | File Transformation (recommended) or a writable `index.html` | Automatic. The banner is the first element of the home tab and sits above Media Bar's slideshow and Home Screen Sections' rows. |
| **"Vote" tab** on the home tab bar | Custom Tabs plugin | Automatic when Custom Tabs is detected (see below). Manual copy-paste fallback is on the *Status & setup* tab. |
| **"Vote" page** in the sidebar (under *Plugin Settings*) | Plugin Pages plugin | Automatic, through Plugin Pages' `PluginInterface.RegisterPage`. |
| **"Media Vote: current leaders"** row | Home Screen Sections plugin | Registered automatically through `PluginInterface.RegisterSection`. You must **enable the section in Home Screen Sections' settings** (HSS shows only enabled sections). |
| **Standalone page** `https://<server>/MediaVote/vote` | Nothing | Always available while its toggle is on. It has its own Jellyfin sign-in form, so it works even without File Transformation, Custom Tabs or Plugin Pages. |

The tab, the page and the section are static entries that every user sees. When no vote is open, or the user is not in the current vote, they show **"No active vote right now"** (or "You are not taking part in the current vote") plus the last result, if one exists. They never show an empty or broken page.

### Custom Tabs details

Custom Tabs (0.2.x, verified against its source) has **no API for other plugins to register tabs**. It stores its tabs in its own plugin configuration (`Tabs[]` with `Title` and `ContentHtml`) and runs `<script>` tags inside tab HTML.

Media Vote therefore:
- **Appends one tab**, marked with `data-mediavote-mount`, after your own tabs when the toggle is on.
- **Never edits or reorders your other tabs**, so their `customTab_N` ids stay the same.
- **Removes only its own tab** when you turn the toggle off.
- Changes the tab's title through **Media Vote → Settings → Tab title**.

If you delete Media Vote's tab inside Custom Tabs while the toggle is on, it is added again at the next restart. Turn the toggle off instead.

**Manual fallback** (if automatic setup reports a problem):
1. Go to **Dashboard → Plugins → Custom Tabs** → add a tab with the title `Vote`.
2. Paste this HTML as its content (also shown, ready to copy, on the *Status & setup* tab):

   ```html
   <div data-mediavote-mount="tab" class="mediavote-tab-host"></div><script>(function(){if(window.MediaVote&&window.MediaVote.scan){window.MediaVote.scan();return;}if(document.getElementById('mediavote-loader'))return;var s=document.createElement('script');s.id='mediavote-loader';s.src=(window.ApiClient?window.ApiClient.getUrl('MediaVote/client.js'):'../MediaVote/client.js');document.head.appendChild(s);})();</script>
   ```

3. Save, then reload the web client (or close and reopen the apps).

### How the banner stays on top

These placement rules were checked against each plugin's current source:
- **jellyfin-web 10.11:** the home view is `#indexPage` → `#homeTab` (`data-index="0"`) → `.sections`. Jellyfin and Home Screen Sections both re-render by replacing the **contents** of `.sections` (`innerHTML`); HSS also adds the `homeSectionsContainer` class. Media Vote inserts its banner as the **first child of `#homeTab`, before `.sections`**, so those re-renders never remove it.
- **Media Bar:** it appends `#slides-container` to `<body>` with `position:absolute; top:0` and pushes `.homeSectionsContainer` down using `margin-top` and `z-index: 6`. Its slide content uses `z-index` up to 10. The banner uses `position: relative; z-index: 11` (CSS variable `--mediavote-banner-z`), so it sits at the top of the home content, above the slideshow, and stays below Jellyfin's header and dialogs. Media Bar measures where the rows start, so it adapts to the banner's height.
- A debounced `MutationObserver`, `hashchange`/`popstate`, and jellyfin-web's `viewshow` event re-check placement after every navigation and every home re-render. The banner has a fixed id, so it is **never inserted twice**. If another script inserts something above it, the banner moves back to the top, but at most 5 times per page view, so it never gets into a tug-of-war with another plugin.
- It is removed when you leave the home page, when no vote is active, and when the user is not eligible.
- The DOM lookup sits behind a small *host adapter* list. A Jellyfin 12 / MUI adapter can be added there later without touching anything else.

---

## Admin guide

Open **Dashboard → Plugins → Media Vote**. The page works on a phone too.

### Create a vote (*New vote* tab)

| Field | Notes |
|---|---|
| Title / description | Shown to voters in the banner and the voting screen. |
| Who can vote | **All users** (any enabled account), or **selected users**. Disabled accounts can never vote. |
| Scope | All libraries · specific libraries · movies only · series only · hand-picked titles. **Only whole Movies and whole Series** are votable, never seasons or episodes. **Collections (BoxSets) are not votable.** |
| Filters | *Never watched by anyone* · *not watched by anyone in X months* · *only the N largest titles*. |
| Votes per user | 1–1000. |
| Several votes on one title | Off by default. When on, a user may stack several of their votes on one title. |
| Keep votes | Optional. Each keep vote subtracts one from a title's score, and keep votes have their own allowance per user. A user can never vote both remove and keep on the same title. |
| Deadline | Date and time in the configured time zone (default **Europe/London**). See [Deadline and time zone](#deadline-and-time-zone). |
| Winners (N) | How many top titles to act on. |
| Tie policy | Default: act on **none** of the tied titles and let the admin break the tie. Alternative: act on all tied titles, which may exceed N. |
| Voter names / live tallies | Both **shown by default**. Admins always see names. When live tallies are hidden, counts and names are hidden from voters until the vote closes. |
| Minimum turnout | Optional percentage of eligible voters who must vote, otherwise nothing is acted on. |
| **Outcome** | **Notify only** (default): a report with names, library paths, sizes and matching Radarr/Sonarr/Jellyseerr entries; nothing is deleted. **Delete automatically**: winners are deleted from Jellyfin and disk. Choosing it requires an explicit confirmation checkbox, and the server rejects it otherwise. |

Use **Preview** to see how many titles match, their total size, and the **most disk space that could be freed** (the N largest). The exact figure depends on the winners; see it with **Dry run**.

Only **one vote can be open at a time**. The list of votable titles is frozen when the vote is created.

### Manage votes (*Votes* tab)

- **Extend:** set a new deadline. **Close now:** resolve immediately. **Cancel:** nothing is deleted, and the votes stay in history. Each of these asks for an in-page confirmation.
- **Dry run:** shows exactly what would be deleted, with paths, sizes and write-permission problems, and which Radarr/Sonarr/Jellyseerr actions would run. It changes nothing, and only read-only GET requests are made.
- **Details:** the ranking with every voter's name, every vote with its time, who has not voted yet, the result, and the external steps for each title.
- **Tie-break:** pick up to the free number of slots among the tied titles. Unchosen tied titles are kept.
- **Resolve anyway:** acts on the winners although minimum turnout was not met.
- **Retry failed items:** retries titles skipped because an external step failed, a deletion failed, a pre-flight check failed, or they were never attempted.
- **CSV:** downloads the ranking and outcome. It is formula-injection safe and opens cleanly in Excel or Numbers. Downloads may not work inside the iOS app; use a browser.
- **History** lists every vote. The **Audit log** tab lists every vote cast, moved or withdrawn, every admin action, and every deletion and external step, newest first. It is stored in `/config/data/mediavote/audit.jsonl`, rotated at 5 MB.

### Settings tab

- **Entry points:** see above.
- **Time zone:** an IANA name, default `Europe/London`.
- **Closing-soon reminder:** default 24 h before the deadline.
- **Result banner:** how many days voters see the result banner (default 7).
- **Playback Reporting:** use its data, on by default and only if that plugin is installed.
- **Protected libraries and titles:** these are never votable and can never be deleted by a vote. Protection is re-checked right before any deletion.
- **Radarr / Sonarr / Jellyseerr:** see below.
- **Webhook:** Discord or generic JSON, for *vote opened*, *closing soon* and *results*. The webhook URL is stored masked, like the API keys.

---

## User guide

1. Open Jellyfin. While a vote is open for you, a banner such as **"Vote: Spring clean-up · You have 3 votes left · closes Saturday 1 June 2026, 20:00"** appears at the top of the home page. It shows **"Not voted yet"** until you vote, and **"Closing soon"** near the deadline.
2. Tap the banner, or open the **Vote** tab or the **Vote** sidebar page.
3. Browse every votable title with its poster, title, year, type, size, library, date added, last played and play count. **Search**, filter by **type** or **genre**, **sort** by title, year, size, votes, date added, last played or play count, or show only **My votes**.
4. Tap **Vote to remove**. Your remaining votes are shown at the top and bottom. Tap **Voted ✓ · undo** to take a vote back. If you have no votes left and tap another title, you are offered to **move** one of your votes to it.
5. You can change your votes until the deadline. After the vote closes, a result banner lists what was removed or flagged; you can dismiss it.

Your votes are **not anonymous**: your name and the time of each vote are stored. Admins always see who voted for what, and other participants see names too unless the admin turned that off.

---

## Vote rules

All rules are enforced **on the server**, in `Core/VoteRules.cs` and `Core/VoteService.cs`, never only in the browser. The user is always taken from the authentication token.

- **Eligibility:** *all users* means any enabled account; otherwise only the selected users. Disabled or deleted accounts cannot vote. A user only sees titles they are allowed to see under their library access and parental controls.
- **Limits:** a user cannot exceed their remove-vote allowance or their keep-vote allowance. By default a user can put at most one vote on a given title.
- **Deadline:** votes, withdrawals and moves are refused from the deadline onwards, even before the vote has been resolved.
- **Score:** remove votes minus keep votes. Only titles with a **score above 0** can win.
- **Ranking:** by score, highest first. Equal scores are a tie; nothing (name, date, size) breaks ties automatically.
- **Winners:** the top N. If titles share the N-th place score and they do not all fit, the tie **crosses the cut-off**:
  - **Default (FlagForAdmin):** the clear winners above the tie are acted on. **None of the tied titles is acted on**, and the vote is flagged *needs attention* so the admin can break the tie.
  - **IncludeAllTied:** all tied titles are acted on, which may exceed N.
  - If the tied titles exactly fill the remaining places, there is no tie problem.
- **Turnout:** distinct users with at least one vote, divided by eligible users. If a minimum is set and not met, nothing is acted on and the admin is told.

### Deadline and time zone

The admin enters the deadline as a local time in the configured zone (default Europe/London), and it is stored in UTC.

- **Clocks go forward** (for example 01:30 on the last Sunday of March): that time does not exist and is rejected with a message.
- **Clocks go back** (for example 01:30 on the last Sunday of October): that time happens twice, and the **first** occurrence (BST) is used, so a vote never closes later than expected.

If the server cannot find the time zone, the *Status & setup* tab warns and UTC is used.

---

## Resolution and deletion safety

- **When:** a background timer checks every 30 seconds, and once about 20 seconds after start-up. If the server was **offline at the deadline**, the vote is resolved on the next start. The scheduled task *Media Vote: resolve due votes* runs the same check manually, or hourly as a backup.
- **Resolved only once:** the vote is marked resolved under the store lock before anything is acted on, so overlapping checks cannot resolve it twice.
- **Crash safety:** if the server stops while winners are being processed, the vote is marked **Interrupted** at the next start and **never retried automatically**. Check the audit log, then use *Retry*.

### Order of operations (Delete automatically)

1. **Pre-flight, for every winner, before anything happens:**
   - the title still exists in Jellyfin;
   - it is not protected (the title or its library);
   - every path Jellyfin would delete is writable by the Jellyfin process.

   The write check uses `access(2)` and does not write anything. It requires write and execute permission on the parent folder and on every folder inside a movie or series folder. It reports `permission denied`, `read-only file system` or a missing mount clearly. **If any winner fails pre-flight, nothing at all is deleted** and the admin is told why.
2. **Then, per winner, in rank order:**
   1. **Re-check** existence and permission, because things may have changed.
   2. **Radarr** (movies) or **Sonarr** (series) step.
   3. **Jellyseerr** step.
   4. **Jellyfin deletion** through `ILibraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true)`. This is the same call as Jellyfin's own *Delete media* action, and it removes the item, all its seasons and episodes, its metadata and its folder on disk. A series folder is deleted with every season, episode and file in it.
3. If a **configured external step fails**, that title is **not deleted**: it is reported and processing **continues with the next title**. Steps that already ran for that title (for example Radarr unmonitor succeeded but Jellyseerr failed) are listed in the report.
4. If the **re-check or the Jellyfin deletion fails**, processing **stops**. Nothing further is deleted and the remaining titles are marked *not attempted*.
5. Every action is written to the audit log.

**Notify only** and **Dry run** never delete anything and never call an external app in a way that changes state; they only use GET lookups.

**Collections:** deleting a movie that belongs to a BoxSet (for example one made by CollectionImport) leaves that collection smaller, or empty if it was the only member. Media Vote does not edit or delete collections. Clean up empty ones in Jellyfin, or let your collection tool rebuild them.

---

## Radarr, Sonarr and Jellyseerr

These are used only by **Delete automatically**; in Notify only and Dry run they are only looked up. Configure each one under **Settings**. Each is optional and has a **Test connection** button.

- **Base URL:** for example `http://192.168.1.20:7878`. Include any URL base, such as `http://host/radarr`.
- **API key:** stored only in the plugin configuration. It is never logged, never sent back to the browser in full (shown as `••••••` plus the last 4 characters), and never in the repository. It is sent in the `X-Api-Key` header, never in a URL. Leaving the masked value unchanged keeps the saved key.

| Service | Actions (default first) | Exact API calls (v3 / v1) |
|---|---|---|
| Radarr | **Unmonitor** · Remove + import exclusion · Do nothing | Lookup: `GET /api/v3/movie?tmdbId=` (falls back to `GET /api/v3/movie` matched by IMDb id). Unmonitor: `PUT /api/v3/movie/editor {"movieIds":[id],"monitored":false}`. Remove: `DELETE /api/v3/movie/{id}?deleteFiles=false&addImportExclusion=true`. Test: `GET /api/v3/system/status`. |
| Sonarr (v3 and v4) | **Unmonitor** · Remove + import list exclusion · Do nothing | Lookup: `GET /api/v3/series?tvdbId=` (falls back to `GET /api/v3/series` matched by TMDb/IMDb id). Unmonitor: `PUT /api/v3/series/editor {"seriesIds":[id],"monitored":false,"monitorNewItems":"none"}`. Remove: `DELETE /api/v3/series/{id}?deleteFiles=false&addImportListExclusion=true`. Test: `GET /api/v3/system/status`. |
| Jellyseerr | **Do nothing** · Remove media/request record | Lookup: `GET /api/v1/movie/{tmdbId}` or `GET /api/v1/tv/{tmdbId}` and read `mediaInfo.id`. Remove: `DELETE /api/v1/media/{mediaId}`, which removes the media record and its requests so the title can be requested again; the key needs the *Manage Requests* permission. Test: `GET /api/v1/auth/me` and `GET /api/v1/status`. |

- **Files are never deleted by Radarr or Sonarr** (`deleteFiles=false`); Jellyfin deletes them.
- **Matching** uses the provider ids stored in Jellyfin: TMDb, TVDB and IMDb.
  - "Not found in the app" is **not** a failure; there is nothing to re-download from there.
  - A title with **no usable id** **is** a failure when an action is configured, because it might exist in the app unmatched. That title is not deleted. Fix it by refreshing the title's metadata in Jellyfin.
- **Unauthorised, unreachable or timed-out** requests (30 seconds) are failures with a helpful message.
- Behaviour was verified against the published OpenAPI specs ([Radarr](https://github.com/Radarr/Radarr/blob/develop/src/Radarr.Api.V3/openapi.json), [Sonarr](https://github.com/Sonarr/Sonarr/blob/develop/src/Sonarr.Api.V3/openapi.json), [Seerr/Jellyseerr](https://github.com/seerr-team/seerr/blob/develop/seerr-api.yml)) and covered by unit tests with mocked HTTP responses. It has **not** been run against real instances.

---

## Docker / ZimaOS permission notes

The linuxserver/jellyfin image runs Jellyfin as user **`abc`**, whose uid and gid come from the `PUID`/`PGID` container variables (ZimaOS usually uses 1000/1000 or the uid of your ZimaOS user). Deleting a title needs **write permission on its folder, everything inside it, and the parent folder**.

**What Media Vote does:** before deleting anything it checks every path. On failure it deletes nothing and shows, on the result and in the audit log, a message like:

> Jellyfin (uid 1000, gid 1000) cannot write to "/media/Movies/Film (2001)": permission denied. In the linuxserver image the process runs as user "abc" (PUID/PGID); make the media folder writable by that user.

Use **Dry run** before the deadline to see any permission problems in advance.

**Typical fixes:**

1. Check the volumes in **ZimaOS → App → Jellyfin → Settings**. A media volume mounted **read-only** (`:ro`) shows up as *read-only file system*; make it read-write.
2. Give the `abc` user ownership of the media folders on the host, using your PUID:PGID:
   ```sh
   sudo chown -R 1000:1000 "/media/YourDrive/Movies" "/media/YourDrive/TV"
   sudo chmod -R u+rwX,g+rwX "/media/YourDrive/Movies" "/media/YourDrive/TV"
   ```
3. **External drives formatted exFAT or NTFS** do not store Unix owners. Ownership comes from the mount options (`uid=`, `gid=`, `umask=`). If `chown` "works" but nothing changes, remount the drive with `uid`/`gid` matching PUID/PGID, or reformat it as ext4.
4. If Radarr/Sonarr import files as another user, make them use the same PUID/PGID (or a shared group with `umask 002`) so new files stay deletable by Jellyfin.

### `index.html` (only if File Transformation is missing)

With **File Transformation** installed (recommended, and already on your server), nothing is written to disk. Without it, Media Vote tries to add its `<script>` tag directly to `index.html` in Jellyfin's web folder. That folder is reported by Jellyfin; in the linuxserver image it is `/usr/share/jellyfin/web/index.html`. Inside the image that file belongs to root and is **not writable** by `abc`. In that case the *Status & setup* tab shows this fix:

1. Install File Transformation and restart. **Or**, as an alternative:
2. Copy the file out once: `docker cp jellyfin:/usr/share/jellyfin/web/index.html /DATA/AppData/jellyfin/index.html`
3. Give it to your PUID:PGID: `sudo chown 1000:1000 /DATA/AppData/jellyfin/index.html`
4. Add the volume `/DATA/AppData/jellyfin/index.html` → `/usr/share/jellyfin/web/index.html` to the container (ZimaOS app settings → Volumes), then restart.
5. **Remove that volume before upgrading Jellyfin**, or the old web-client page will be kept.

Media Vote keeps one backup (`index.html.mediavote-backup`), only ever adds a single marked tag, and removes its own tag again once File Transformation is available.

---

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
- It registers one File Transformation callback for `index.html`. The callback only inserts its own marked `<script>` once before `</body>`, never replaces the file, and never assumes it runs first or last.
- **Everything is namespaced `mediavote-`**: element ids, CSS classes, CSS variables, `localStorage` keys (only a non-essential device id on the standalone page) and the `mediavote-status` event. The only global is `window.MediaVote`.
- **No global CSS:** every selector is scoped to a `.mediavote-*` class. Colours are read at runtime from the active theme (page background, text colour and the theme's primary button colour), so custom themes look right. There is no hard-coded colour scheme.
- **Nothing is monkey-patched:** no jellyfin-web internals and no other plugin's globals. Only Media Vote's own elements and listeners are added.
- **Optional integrations are detected at runtime by reflection.** No assembly references other plugins: other plugins' `JObject` parameters are built by calling `Parse` on the parameter type they declare, so Media Vote does not even reference Newtonsoft.Json. The DLL references only Jellyfin 10.11.0 and .NET 9 framework assemblies.

| Plugin | Notes |
|---|---|
| File Transformation 3.0.0.0 | Used for injection via `PluginInterface.RegisterTransformation`. Optional, with an index.html fallback. |
| Home Screen Sections 3.0.0.0 | The banner sits above HSS's container and survives its re-renders. The optional "current leaders" section is registered via `PluginInterface.RegisterSection` and must be enabled in HSS's settings. |
| Plugin Pages 3.0.0.0 | Sidebar "Vote" page via `PluginInterface.RegisterPage`. The page's visibility follows the toggle live, through a callback, with no restart. |
| Custom Tabs 0.2.10.0 | Adds or removes only its own tab in Custom Tabs' configuration (no API exists). Other tabs are never changed. |
| Media Bar 2.4.13.0 | The banner is placed above the slideshow (`z-index` 11 > slides ≤ 10). The `--mediavote-banner-z` variable can be overridden with your own CSS if needed. |
| Jellyfin Enhanced 12.7.0.0 | JE registers shortcuts with a **capture-phase `document` keydown listener**, and only ignores INPUT/TEXTAREA. Media Vote adds a `window` capture listener that stops key events **only when they start inside Media Vote's UI**, including `<select>` elements, so JE shortcuts never fire while you use the voting screen or the admin form. Default actions such as typing, Tab and Enter still work. No global shortcuts of its own. |
| JavaScript Injector 4.0.0.0 | No interaction. If you inject CSS that targets generic elements, it can affect our UI like any page content. |
| Streamyfin plugin 0.66.0.0 | No interaction (Streamyfin is a native client). |
| Playback Reporting 17.0.0.0 | Optional, read-only source for play counts. Its SQLite database is read through the SQLite provider Jellyfin already loads; nothing is written to it. If it is missing or its format differs, Media Vote silently uses Jellyfin's user data only. |
| Reports, DiscontinueWatching, Intro Skipper, Chapter Creator, SkipMe.db, Media Preview, WhisperSubs, metadata providers, CollectionImport | No interaction. Deleted titles disappear from them as with any Jellyfin deletion. CollectionImport collections may become smaller or empty (see above). |

**Known conflicts:** none found from reading the sources.
- If another script also insists on being the first element of the home tab, the banner stops repositioning after 5 tries and may sit second.
- Custom CSS that styles generic elements (for example every `button`) can change Media Vote's look.

---

## Extra features: implemented and skipped

| Feature | Status |
|---|---|
| Minimum turnout (report to admin, *Resolve anyway* override) | ✅ |
| Protected titles and libraries (never votable, never deletable; re-checked before deletion) | ✅ |
| "Keep" votes that subtract from the score (admin toggle, own allowance) | ✅ |
| Per-item info: size, date added, last played by anyone, play count | ✅ |
| Build filters: never watched · not watched in X months · largest files | ✅ |
| Closing-soon reminder and "you haven't voted yet" indicator | ✅ |
| Result announcement banner (dismissible, stored server-side) | ✅ |
| Webhook: Discord or generic, for opened / closing soon / results | ✅ |
| CSV export | ✅ |
| Dry run (exact deletions, permission problems and external actions; read-only) | ✅ |
| Several simultaneous open votes | ⛔ Skipped on purpose: one open vote at a time keeps the banner and tab unambiguous. |
| Push notifications to phones | ⛔ Skipped (no plugin API for it; use the webhook). |
| Translations | ⛔ English only. |

---

## Known limitations

- **No live test yet:** nothing has been run on a live Jellyfin server or real phones (see next section).
- **Jellyfin 12:** not supported yet. The project builds a 10.11 DLL; Jellyfin 12's MUI layout needs a new banner host adapter and a `-p:JellyfinTarget=12` build. See [Jellyfin 12](#jellyfin-12).
- **Web clients only:** the entry points appear only in jellyfin-web-based clients.
- **Play statistics:** Jellyfin's per-user data, counting titles marked played, plus optional Playback Reporting. Series statistics are aggregated from their episodes. Partially watched, never-completed titles may count as "never played".
- **Sizes:** the voting list uses the sizes stored in Jellyfin's database. The admin report and deletion use the real size on disk of the folders that would be deleted.
- **Hidden titles:** titles a user cannot access (library permissions or parental controls) are hidden from that user. The visibility check is cached for 5 minutes per user.
- **Custom Tabs:** if Custom Tabs changes how it stores tabs, automatic setup reports this and the manual HTML still works.
- **Plugin Pages** groups our page under its *Plugin Settings* sidebar heading.
- **Home Screen Sections:** the row has to be enabled once in HSS's own settings.
- **Downloads in the app:** CSV download may not work inside the iOS/Android app.
- **Android back button:** it leaves the home page instead of only closing the overlay.

---

## What has and has not been tested

**Automated, in this repository:**
- **107 unit tests** (`tests/Jellyfin.Plugin.MediaVote.Tests`):
  - vote counting, ranking and tie handling;
  - eligibility, vote limits, duplicates, keep votes, move atomicity;
  - deadline logic, including voting after the deadline but before resolution, and resolving after the server was offline;
  - turnout;
  - London time-zone conversion including both DST edges;
  - idempotent resolution and interrupted runs;
  - **order of operations**: external steps before deletion, external failure skips only that title, delete failure stops the run, pre-flight failure deletes nothing, protected titles;
  - dry run and notify-only never execute or delete;
  - JSON store atomicity and corrupt-file handling;
  - CSV escaping, webhook payloads, index.html injection idempotence, settings rules and key masking;
  - **Radarr/Sonarr/Jellyseerr requests against mocked HTTP responses**: exact URLs, bodies, the API key only in a header, GET-only planning, error messages without the key.
- **Headless-browser smoke tests** (`tests/client/*.js`, Chromium) against a **mock** jellyfin-web page with a fake ApiClient:
  - the banner as the first element of the home tab and above a Media Bar-style slideshow;
  - surviving Home Screen Sections-style re-renders without duplicates;
  - moving back to the top, removal when leaving home, re-adding on return;
  - the overlay: scroll lock, pagination, voting request with the token, focus trap, Escape;
  - a Jellyfin Enhanced-style capture shortcut listener not firing inside our UI but still working outside it;
  - the tab/page mount, the "No active vote" state with the last result, no banner for ineligible users;
  - a 360 px phone: full-screen overlay, no horizontal scroll, 44 px buttons;
  - the standalone page sign-in under a `/jf` base URL;
  - the admin page: defaults, preview, deletion confirmation, masked keys, the banner-toggle rule, no overflow at 375 px.
- The release packaging and manifest generation were run locally. The zip contains only `Jellyfin.Plugin.MediaVote.dll`, and its references are Jellyfin 10.11.0 and .NET 9 assemblies only.

**Not tested, because it needs a live server, real plugins or real devices:**
- Loading the plugin in a real Jellyfin 10.11.1 (linuxserver image) and running its start-up task.
- Real **File Transformation 3.0** callback invocation, real **Plugin Pages / Home Screen Sections / Custom Tabs** registration, and the real jellyfin-web 10.11 DOM together with real Media Bar, HSS, Jellyfin Enhanced and Custom Tabs scripts.
- The official **iOS and Android apps** (WKWebView / Android WebView behaviour, keyboard, safe areas, app restart behaviour) and the **Jellyfin Desktop** app.
- **Real deletion** on your drives and the permission check against your actual mounts. Run a **Dry run** first.
- **Real Radarr, Sonarr and Jellyseerr instances**, and Discord webhooks.
- Reading a real **Playback Reporting** database.

---

## Manual test checklist

Do this on a test vote first, using **Notify only**, with a couple of throw-away titles in a protected-free test library. Tick each item on **(a) iPhone app, (b) iPad app in portrait and landscape, (c) Android app, (d) desktop browser, (e) Jellyfin Desktop app**, with **Media Bar, Home Screen Sections, Jellyfin Enhanced and Custom Tabs all enabled**.

**Install and injection**
- [ ] After install and restart, *Media Vote → Status & setup* shows **FileTransformation: Injected…** and lists Plugin Pages, Home Screen Sections and Custom Tabs as detected and registered.
- [ ] Browsers were hard-refreshed and apps fully closed and reopened; if needed, users signed out and in.
- [ ] Other plugins still work: the Media Bar slideshow, HSS sections, Jellyfin Enhanced features and shortcuts, existing Custom Tabs, Plugin Pages entries, JavaScript Injector scripts.

**Banner**
- [ ] With a vote open and the user eligible, the banner is the **topmost element** of the home page, **above the Media Bar slideshow** and **above HSS's rows**.
- [ ] It stays there after: switching to Favorites and back; opening a title and pressing back; HSS refreshing; Media Bar changing slides; rotating an iPad.
- [ ] It never appears twice.
- [ ] It is gone on other pages, for ineligible users, when no vote is open, and when the banner toggle is off (with another entry point on).
- [ ] It shows "Not voted yet" before you vote and the votes-left count after; "Closing soon" appears inside the reminder window.
- [ ] Banner colours match the current theme, both dark and light; try another theme or custom CSS.

**Voting screen** (open from the banner, the Vote tab and the Vote sidebar page)
- [ ] It is the same UI from all three. On phones the overlay is full screen and respects the notch and home indicator; on desktop it is a centred dialog.
- [ ] The page behind does not scroll while the overlay is open.
- [ ] The search box stays visible with the on-screen keyboard open (iPhone and Android).
- [ ] Typing in search or using the selects does **not** trigger Jellyfin Enhanced shortcuts. Shortcuts still work after closing.
- [ ] Posters load lazily, scrolling loads more titles, and search, type, genre, sort and *My votes* work.
- [ ] Vote, undo, run out of votes, then move a vote. Counts update in the header, the footer and the banner.
- [ ] Voter names and tallies show or hide according to the vote's settings, for a non-admin user.
- [ ] Tapping a title opens its Jellyfin page in-app with no full reload, and back returns to Jellyfin.
- [ ] Desktop: Tab stays inside the overlay, Escape closes it and focus returns to the banner. A screen reader announces the dialog and the vote changes.

**"No active vote" state**
- [ ] With no open vote: the Vote tab and the Vote page show "No active vote right now" plus the last result, on phone, iPad and desktop.
- [ ] As an ineligible user during a vote: "You are not taking part in the current vote."

**Standalone page**
- [ ] `https://<server>/MediaVote/vote` signs in with a Jellyfin account and allows voting. It also works with File Transformation disabled.

**Server rules** (try from a browser console or a second account)
- [ ] Voting after the deadline, over the limit, twice on one title (when not allowed) and as an ineligible user are all refused.

**Resolution**
- [ ] Close a Notify-only vote early. The report lists names, paths, sizes and the matched Radarr/Sonarr/Jellyseerr entries; nothing is deleted or changed in those apps.
- [ ] Create a tie across the cut-off: nothing in the tie is acted on, *needs attention* appears, and the tie-break works.
- [ ] Set a minimum turnout that is not met: nothing happens, and *Resolve anyway* works.
- [ ] Stop Jellyfin before a deadline, start it after: the vote resolves within about a minute of start-up.
- [ ] **Dry run** on a Delete vote shows the exact paths, sizes, permission problems and external actions, and Radarr/Sonarr/Jellyseerr are unchanged afterwards.
- [ ] With one throw-away movie and one throw-away series:
  - Delete automatically removes them from Jellyfin and disk, including every season folder.
  - Radarr/Sonarr show them unmonitored or excluded.
  - The Jellyseerr record is removed if configured.
  - The audit log lists every step.
- [ ] Make one folder read-only: the vote deletes **nothing** and reports the path and reason.
- [ ] Stop Radarr: that movie is skipped with an error, the next title is still processed, and *Retry* works after Radarr is back.
- [ ] Webhook messages arrive (if configured), and the CSV opens correctly.

---

## REST API

All routes are relative to the server base URL. Responses are camelCase JSON.

**User** (`[Authorize]`; acts only as the token's user):

| Method & route | Purpose |
|---|---|
| `GET /MediaVote/Status` | Active vote summary for this user, last result, enabled entry points. |
| `GET /MediaVote/Items?search=&type=&genre=&sort=&desc=&mine=&page=&pageSize=` | Paged votable titles (max 100 per page). |
| `POST /MediaVote/Vote` `{itemId, kind}` | Cast a vote (`Remove` or `Keep`). |
| `POST /MediaVote/Unvote` `{itemId, kind}` | Withdraw one of your votes. |
| `POST /MediaVote/Move` `{fromItemId, itemId, kind}` | Move a vote atomically. |
| `POST /MediaVote/Results/{id}/Dismiss` | Hide the result banner for you. |

**Anonymous (static, no data):** `GET /MediaVote/client.js`, `/MediaVote/client.css`, `/MediaVote/page` (Plugin Pages fragment), `/MediaVote/vote` (standalone page).

**Admin** (`[Authorize(Policy = "RequiresElevation")]`), all under `/MediaVote/Admin/`:

| Method | Routes |
|---|---|
| GET | `Overview`, `Users`, `Libraries`, `Search?term=`, `Sessions/{id}`, `Sessions/{id}/Csv`, `Audit?sessionId=&max=`, `Settings` |
| POST | `Preview`, `Sessions`, `Sessions/{id}/Extend`, `/Close`, `/Cancel`, `/DryRun`, `/BreakTie`, `/Retry`, `/ResolveAnyway`, `Settings`, `Test/{radarr\|sonarr\|jellyseerr}`, `Reinject` |

---

## Development, releases and Jellyfin 12

```
src/Jellyfin.Plugin.MediaVote/
  Core/           Pure logic, no Jellyfin types: rules, tally/ties, executor (order of operations), service, CSV, time zones
  Storage/        JSON vote store (atomic, copy-on-write) and JSON-lines audit log
  Integrations/   Radarr/Sonarr/Jellyseerr clients, webhook notifier
  Services/       Jellyfin adapters (library, users, deletion, permission probe, Playback Reporting), deadline timer, scheduled tasks
  Web/            File Transformation injection, entry-point registration (Plugin Pages, HSS, Custom Tabs)
  Api/            REST controllers
  Assets/         client.js, client.css, page.html, standalone.html (embedded)
  Configuration/  plugin configuration and the admin page
tests/Jellyfin.Plugin.MediaVote.Tests/   xUnit tests
tests/client/                            headless Chromium smoke tests (mock jellyfin-web)
```

```sh
dotnet build -c Release
dotnet test -c Release
NODE_PATH=$(npm root -g) node tests/client/smoke.js        # needs the playwright package + Chromium
NODE_PATH=$(npm root -g) node tests/client/admin-smoke.js
```

CI (`.github/workflows/build.yml`) builds, runs the unit tests and the smoke tests, and uploads a development zip.

### Publishing a release

1. Merge into `main`. The manifest is committed to the default branch.
2. Tag and push:
   ```sh
   git tag -a v1.0.0.0 -m "First release"
   git push origin v1.0.0.0
   ```
   You can also create the tag through **GitHub → Releases → Draft a new release**. The workflow attaches the zip to an existing release.
3. `.github/workflows/release.yml` then:
   - tests;
   - builds with the tag's version as the assembly version;
   - zips `Jellyfin.Plugin.MediaVote.dll` as `media-vote_<version>.zip`;
   - creates the GitHub Release;
   - runs `.github/scripts/update_manifest.py`, which adds the version (changelog from the tag message, `targetAbi` 10.11.0.0, `sourceUrl` of the release zip, MD5 `checksum` as Jellyfin verifies it, and a timestamp) to `manifest.json` on `main`, and pushes it.
4. If `main` is branch-protected, allow GitHub Actions to push, or apply the manifest change by hand.

`build.yaml` holds the same metadata in JPRM format, if you prefer [JPRM](https://github.com/oddstr13/jellyfin-plugin-repository-manager).

### Jellyfin 12

The project is ready for a second build line:
- `Jellyfin.Plugin.MediaVote.csproj` has a `JellyfinTarget` switch. `-p:JellyfinTarget=12` targets net10.0 and Jellyfin 12 packages, and defines `JELLYFIN_12` for any version-specific code.
- The client's DOM knowledge is isolated in `hostAdapters` in `client.js`.
- Custom Tabs already supports 12's MUI header.

To support 12, add a host adapter for the MUI home layout and test it there, add a manifest version with `targetAbi` 12.x, and fix any API differences behind `#if JELLYFIN_12`.

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

## License

See [LICENSE](LICENSE).
