/*
 * Headless smoke test for the admin dashboard page (Configuration/configPage.html) with mocked
 * admin endpoints. Run: NODE_PATH=$(npm root -g) node tests/client/admin-smoke.js
 */
'use strict';
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const ORIGIN = 'http://jf.test';
const pageHtml = fs.readFileSync(path.join(__dirname, '../../src/Jellyfin.Plugin.MediaVote/Configuration/configPage.html'), 'utf8');
const fragment = pageHtml.slice(pageHtml.indexOf('<div id="MediaVoteConfigPage"'), pageHtml.lastIndexOf('</body>'));

const assets = path.join(__dirname, '../../src/Jellyfin.Plugin.MediaVote/Assets');
const GB = 1073741824;
const browseItems = Array.from({ length: 40 }, (_, i) => ({
    id: 'b' + i, name: 'Title ' + i, year: 2000 + (i % 20), kind: i % 3 ? 'Movie' : 'Series', library: 'Movies', sizeBytes: (i + 1) * GB,
    dateAddedUtc: '2025-01-01T00:00:00Z', lastPlayedUtc: i % 4 ? '2026-02-01T00:00:00Z' : null, lastPlayedBy: i % 4 ? 'sam' : null, playCount: i % 4 ? 3 : 0,
    viewerCount: i % 4 ? 2 : 0, neverWatched: !(i % 4), episodeCount: i % 3 ? 0 : 10, playedEpisodeCount: i % 3 ? null : 4,
    drives: [{ key: '/LM1', name: '8TB 1', bytes: (i + 1) * GB, known: true, usedBytes: 6600 * GB, totalBytes: 7450 * GB, usedPercent: 90.4, level: 'Warning' }],
    genres: ['Drama'], imageTag: null, removable: i % 5 !== 0, removalNote: i % 5 ? 'Radarr' : 'Not managed by Radarr'
}));
const drivesFacet = [{ key: '/LM1', name: '8TB 1', known: true, usedBytes: 6600 * GB, freeBytes: 700 * GB, totalBytes: 7450 * GB, usedPercent: 90.4, level: 'Warning', fsType: 'exfat', mountPoints: ['/LM1'], sharedWarning: null },
    { key: '/LM2', name: '8TB 2', known: true, usedBytes: 4800 * GB, freeBytes: 2600 * GB, totalBytes: 7450 * GB, usedPercent: 64.9, level: 'Ok', fsType: 'exfat', mountPoints: ['/LM2'], sharedWarning: null }];

const harness = `<!DOCTYPE html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"></head><body style="background:#fff;color:#111">
<script>
window.ApiClient = { accessToken: function(){return 'admintok';}, getUrl: function(p){ return '${ORIGIN}/' + p.replace(/^\\//,''); }, getCurrentUserId: function(){ return 'u1'; }, serverAddress: function(){ return '${ORIGIN}'; }, serverId: function(){ return 'srv'; } };
window.Dashboard = { showLoadingMsg: function(){}, hideLoadingMsg: function(){} };
</script>
${fragment}
<script>document.querySelector('#MediaVoteConfigPage').dispatchEvent(new Event('pageshow'));</script>
</body></html>`;

let failures = 0;
function check(cond, msg) { console.log((cond ? 'PASS ' : 'FAIL ') + msg); if (!cond) { failures++; } }

(async () => {
    const browser = await chromium.launch();
    const context = await browser.newContext({ viewport: { width: 375, height: 812 } });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', e => errors.push(String(e)));
    const posted = {};
    const state = { usersFail: true, kill: false, preview: true, deadlineCalls: [], browseQueries: [] };
    const settings = {
        enableBanner: true, enableCustomTab: true, enablePluginPage: true, enableHomeSection: true, enableStandalonePage: true, tabTitle: 'Vote',
        timeZoneId: 'Europe/London', closingSoonHours: 24, resultBannerDays: 7, usePlaybackReporting: true, protectedItemIds: [], protectedItemNames: {}, protectedLibraryIds: [],
        arrInstances: [{ id: 'r1', kind: 'Radarr', name: 'Radarr', url: 'http://radarr:7878', apiKey: '••••••1234', enabled: true }],
        radarrAddImportExclusion: true, sonarrAddImportListExclusion: true, jellyseerrAction: 'Nothing', maxRemovalsPerVote: 5,
        webhookUrl: '', webhookKind: 'Discord', webhookOnOpened: true, webhookOnClosingSoon: true, webhookOnResults: true, publicServerUrl: '',
        requireApprovalForDeletes: true, autoApproveHours: 0, jellyseerrPublicUrl: '', webhookOnApproval: true, webhookIncludeNonVoters: true,
        driveNames: [], driveWarnPercent: 85, driveCriticalPercent: 95, watchDetailsForVoters: 'NamesAndTime'
    };
    await page.route(ORIGIN + '/**', async route => {
        const url = new URL(route.request().url());
        const p = url.pathname.replace('/MediaVote/Admin/', '');
        const json = b => route.fulfill({ contentType: 'application/json', body: JSON.stringify(b) });
        if (url.pathname === '/admin') { return route.fulfill({ contentType: 'text/html', body: harness }); }
        if (url.pathname === '/MediaVote/client.js') { state.libLoaded = url.search; return route.fulfill({ contentType: 'application/javascript; charset=utf-8', body: fs.readFileSync(path.join(assets, 'client.js'), 'utf8') }); }
        if (url.pathname === '/MediaVote/client.css') { return route.fulfill({ contentType: 'text/css', body: fs.readFileSync(path.join(assets, 'client.css'), 'utf8') }); }
        if (p === 'Deadline') {
            const spec = JSON.parse(route.request().postData());
            state.deadlineCalls.push(spec);
            if (spec.kind === 'Preset' && spec.preset === 'tonight') { return json({ ok: false, error: 'It is already past 20:00 today. Choose "Tomorrow at 20:00" instead.' }); }
            return json({ ok: true, localInput: '2026-06-08T20:00', description: 'Closes Monday 8 Jun 2026 at 20:00 BST (in 7 days)' });
        }
        if (p === 'Browse') {
            state.browseQueries.push(Object.fromEntries(url.searchParams));
            const page = Number(url.searchParams.get('page') || 0);
            let list = browseItems;
            if (url.searchParams.get('watched') === 'never') { list = list.filter(i => i.neverWatched); }
            return json({ total: list.length, totalBytes: list.reduce((s, i) => s + i.sizeBytes, 0), page, pageSize: 30, items: list.slice(page * 30, page * 30 + 30), genres: ['Drama'], libraries: [{ id: 'l1', name: 'Movies' }, { id: 'l2', name: 'TV' }], drives: drivesFacet, watchDetails: 'NamesAndTime' });
        }
        if (p === 'Browse/Ids') { return json({ count: 3, totalBytes: 6 * GB, items: [1, 2, 3].map(n => ({ id: 'b' + n, name: 'Title ' + n, sizeBytes: 2 * GB, drives: { '/LM1': 2 * GB } })) }); }
        if (p === 'Drives') { return json({ drives: drivesFacet, warnPercent: 85, criticalPercent: 95 }); }
        if (p === 'Templates' && route.request().method() === 'GET') { return json(state.templates || []); }
        if (p === 'Templates') { posted.Templates = JSON.parse(route.request().postData()); state.templates = [{ id: 't1', name: posted.Templates.name, settings: posted.Templates.settings, deadline: posted.Templates.deadline, recurrence: posted.Templates.recurrence, schedule: 'Every month · next draft Sun 1 Nov 2026 09:00', draftPending: false }]; return json({ ok: true, id: 't1' }); }
        if (p === 'Storage') {
            return json({ warnPercent: 85, criticalPercent: 95, drives: drivesFacet.map(d => ({ drive: d, libraries: [{ name: 'Movies', bytes: 4000 * GB, titles: 900 }] })),
                openVote: { title: 'Spring clean', winners: 2, titles: ['Big', 'Bigger'], notes: [], projections: [{ key: '/LM1', name: '8TB 1', bytes: 134 * GB, text: 'frees 134.0 GB on 8TB 1 (90% to 89%)', percentAfter: 88.6 }] } });
        }
        if (p === 'Stats') {
            return json({ totalFreedBytes: 300 * GB, sessions: [{ id: 's1', title: 'Winter', resolvedLocal: 'Sun 1 Feb 2026 20:00', freedBytes: 100 * GB, cumulativeFreedBytes: 100 * GB, voters: 10, eligible: 16, turnoutPercent: 62.5, removed: 3, outcome: 'DeleteViaArr' }, { id: 's2', title: 'Spring', resolvedLocal: 'Sun 1 Mar 2026 20:00', freedBytes: 200 * GB, cumulativeFreedBytes: 300 * GB, voters: 12, eligible: 16, turnoutPercent: 75, removed: 4, outcome: 'DeleteViaArr' }],
                mostVoted: [{ name: 'Old Film', removeVotes: 9, votes: 10, sessions: 2 }], participation: [{ name: 'alex', votesCast: 6, sessionsVoted: 2, sessionsEligible: 2, participationPercent: 100 }] });
        }
        if (route.request().method() === 'POST') { posted[p] = JSON.parse(route.request().postData() || 'null'); }
        if (p === 'Overview') {
            return json({ nowLocal: 'Sat 1 Jun 2026 12:00', defaultDeadlineInput: '2026-06-08T20:00', timeZone: 'Europe/London', timeZoneFound: true,
                customTabSnippet: '<div data-mediavote-mount="tab" class="mediavote-tab-host">Media Vote is loading. If this stays empty, reload the page.</div>',
                customTabFindings: [{ index: 1, title: 'Vote', kind: 'LeftoverV100', message: 'Tab 2 ("Vote") was written by Media Vote 1.0.0.0 and contains an inline script. Delete this tab.' }],
                killSwitch: { active: state.kill, path: '/config/data/mediavote/DISABLE' }, previewOnly: state.preview,
                entryPoints: { banner: false, tab: false, page: false, section: false, standalone: true },
                selfCheck: [{ name: 'Users', ok: true, detail: 'OK, 16 found (2 disabled, 1 admin)' }, { name: 'Play statistics (one user)', ok: false, detail: 'Jellyfin could not read play statistics: boom' }],
                selfCheckUtc: '2026-06-01T12:00:00Z', pluginVersion: '1.0.1.0', builtForJellyfin: '10.11.10',
                injection: { mode: 'NotNeeded', message: 'Not needed: the home banner and the Vote tab are both off.', selfTestPassed: false, leftoverDirectInjection: false },
                integrations: [{ name: 'Custom Tabs', detected: true, version: '0.2.10.0', enabled: false, registered: false, message: 'ACTION NEEDED' }],
                processUser: 'uid 1000, gid 1000', dataDirectory: '/config/data/mediavote', openSessionId: null, maxRemovalsPerVote: 5, arrInstanceCount: 1,
                awaitingApproval: state.withSession ? [{ id: 's10', title: 'Autumn', count: 2, bytes: 12 * GB, dueUtc: null, dueLocal: null }] : [],
                pendingDrafts: state.withSession ? [{ id: 't1', name: 'Monthly clean-up', sinceLocal: 'Sun 1 Nov 2026 09:00' }] : [],
                sessions: state.withSession ? [{ id: 's9', title: 'Autumn', status: 'Resolved', deadlineLocal: 'Sun 25 Oct 2026 20:00', voters: 5, needsAttention: true }] : [] });
        }
        if (p === 'Sessions/s10') {
            return json({ deadlineLocal: 'Sun 25 Oct 2026 20:00', deadlineLocalInput: '2026-10-25T20:00', eligibleCount: 16, voterCount: 5, nonVoters: [], votes: [], ranking: [],
                session: { id: 's10', status: 'Resolved', candidates: [], votes: [], settings: { title: 'Autumn', outcome: 'DeleteViaArr', votesPerUser: 3, winnerCount: 2 },
                    result: { outcome: 'DeleteViaArr', voterCount: 5, eligibleVoterCount: 16, turnoutPercent: 31, totalBytesSelected: 12 * GB, totalBytesFreed: 0, execution: state.approved ? 'Completed' : 'AwaitingApproval', turnoutMet: true, tiedItemIds: [], notes: [], plannedItemIds: ['x1', 'x2'], approvalDueUtc: null,
                        items: [{ itemId: 'x1', name: 'Film A', kind: 'Movie', score: 3, sizeBytes: 6 * GB, paths: [], externalSteps: [], status: 'Reported', message: 'Waiting for your approval. Would be deleted with its files by Film A in Radarr (id 4), 6.0 GB.', arrInstance: 'Radarr', arrId: 4, jellyfinCleanup: 'None' }] } } });
        }
        if (p === 'Sessions/s10/Approve') { state.approved = true; return json({ ok: true }); }
        if (p === 'Sessions/s9') {
            const item = (o) => Object.assign({ kind: 'Movie', score: 3, sizeBytes: 2e9, paths: ['/LM1/Movies/' + o.name], externalSteps: [], needsAttention: false, jellyfinCleanup: 'None' }, o);
            return json({ deadlineLocal: 'Sun 25 Oct 2026 20:00', deadlineLocalInput: '2026-10-25T20:00', eligibleCount: 16, voterCount: 5, nonVoters: [], votes: [], ranking: [],
                session: { id: 's9', status: 'Resolved', candidates: [], votes: [], settings: { title: 'Autumn', outcome: 'DeleteViaArr', votesPerUser: 3, winnerCount: 3 },
                    result: { outcome: 'DeleteViaArr', voterCount: 5, eligibleVoterCount: 16, turnoutPercent: 31, totalBytesSelected: 6e9, totalBytesFreed: 2e9, execution: 'Completed', turnoutMet: true, tiedItemIds: [], notes: [],
                        items: [
                            item({ name: 'Gone Film', status: 'Removed', arrInstance: 'Radarr', arrId: 42, arrTitle: 'Gone Film (2001)', arrSizeOnDisk: 2e9, jellyfinCleanup: 'FilesStillOnDisk', cleanupNote: 'files still on disk', needsAttention: true, message: 'Deleted with its files by Radarr and confirmed gone.' }),
                            item({ name: 'Home Video', status: 'NotMatched', needsAttention: true, message: 'Not managed by Radarr (no entry with its TMDb id 5). Nothing was deleted; remove it by hand if it should go.' }),
                            item({ name: 'Locked Film', status: 'DeleteRefused', needsAttention: true, arrInstance: 'Radarr', arrId: 7, message: "Radarr answered HTTP 500: Access to the path '/movies/Locked Film' is denied." })
                        ] } } });
        }
        if (p === 'ArrStatus') {
            state.arrStatusCalls = (state.arrStatusCalls || 0) + 1;
            return json({ instances: [{ name: 'Radarr', kind: 'Radarr', enabled: true, ok: true, message: 'Connected: Radarr 5.14.0', rootFolders: [{ path: '/movies', freeSpace: 7e11, totalSpace: 8e12 }] }, { name: 'Sonarr Anime', kind: 'Sonarr', enabled: true, ok: false, message: 'Could not reach Sonarr Anime: Connection refused', rootFolders: [] }] });
        }
        if (p === 'Settings' && route.request().method() === 'GET') { return json(settings); }
        if (p === 'Libraries') { return json([{ id: 'lib1', name: 'Movies', collectionType: 'movies', protected: false }, { id: 'lib2', name: 'Kids', collectionType: 'movies', protected: true }]); }
        if (p === 'Users') {
            if (state.usersFail) { return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ error: 'jellyfin_version_mismatch', message: "Media Vote error: Method not found: 'IUserManager.get_Users()'" }) }); }
            return json([{ id: 'u1', name: 'alex', disabled: false, admin: true }, { id: 'u2', name: 'sam', disabled: false, admin: false }, { id: 'u3', name: 'old friend', disabled: true, admin: false }]);
        }
        if (p === 'Visibility') { state.preview = posted.Visibility.previewOnly; return json({ ok: true, previewOnly: state.preview }); }
        if (p === 'Preview') {
            const del = posted.Preview && posted.Preview.settings.outcome === 'DeleteViaArr';
            return json({ count: del ? 11 : 12, movies: 10, series: 2, totalBytes: 5e11, maxFreedBytes: 6e10, notRemovable: 1, excludedNotRemovable: del ? 1 : 0, maxRemovals: 5, arrErrors: [],
                items: [{ name: 'Big', year: 2001, kind: 'Movie', sizeBytes: 6e10, playCount: 0, removable: true }, { name: 'Home video', year: 2010, kind: 'Movie', sizeBytes: 1e9, playCount: 0, removable: false, removalNote: 'Not managed by Radarr' }] });
        }
        if (p === 'Sessions') { return json({ ok: true, id: 'new-id' }); }
        if (p === 'Settings') { return json({ ok: true }); }
        if (p.indexOf('Test/') === 0) { return json({ ok: true, message: 'Connected: Radarr 5.14' }); }
        return route.fulfill({ status: 404, body: '' });
    });
    await page.goto(ORIGIN + '/admin');
    await page.waitForSelector('#mvHistory tr');
    check(await page.evaluate(() => /No votes yet/.test(document.getElementById('mvHistory').textContent)), 'votes tab shows empty history');

    // Users fail with HTTP 500 (the 1.0.0.0 bug): only the user picker shows an error; settings and libraries still load.
    await page.click('[data-tab="new"]');
    await page.check('input[name="mvVoters"][value="some"]');
    await page.waitForSelector('#mvUserError .mv-warn');
    check(await page.evaluate(() => /Could not load the user list: Media Vote error: Method not found/.test(document.getElementById('mvUserError').textContent)), 'users failing: readable error shown in the user picker');
    check(await page.evaluate(() => Array.prototype.some.call(document.querySelectorAll('#sArr input'), i => i.value === 'http://radarr:7878')), 'users failing: settings still loaded');
    check(await page.evaluate(() => document.querySelectorAll('#mvLibList input').length === 2 && document.querySelectorAll('#sProtLibs input').length === 2), 'users failing: libraries still loaded');
    state.usersFail = false;
    await page.click('#mvUserError button');
    await page.waitForSelector('#mvUserList input');
    check(await page.evaluate(() => document.querySelectorAll('#mvUserList input').length === 3 && /disabled account/.test(document.getElementById('mvUserList').textContent)), 'Retry: all users listed, disabled ones marked');
    await page.click('#mvUserAll');
    check(await page.evaluate(() => document.getElementById('mvUserCount').textContent === '2 of 3 selected'), 'select all selects the enabled users; count shown');
    await page.fill('#mvUserSearch', 'sam');
    check(await page.evaluate(() => Array.prototype.filter.call(document.querySelectorAll('#mvUserList label'), l => !l.hidden).length === 1), 'user search filters the list');
    await page.fill('#mvUserSearch', '');
    await page.click('#mvUserNone');
    check(await page.evaluate(() => document.getElementById('mvUserCount').textContent === '0 of 3 selected'), 'select none clears the selection');
    await page.check('input[name="mvVoters"][value="all"]');

    await page.click('[data-tab="status"]');
    await page.waitForSelector('#mvStatus .mv-card');
    const statusText = await page.evaluate(() => document.getElementById('mvStatus').textContent);
    check(/Preview mode is ON/.test(statusText) && /16 found/.test(statusText) && /could not read play statistics/.test(statusText), 'status tab: preview state and self-check lines with the exact error');
    check(/written by Media Vote 1.0.0.0/.test(statusText) && /delete the tab named above/i.test(statusText), 'status tab: 1.0.0.0 Custom Tabs leftover warning with instructions');
    await page.waitForFunction(() => /Radarr 5\.14\.0/.test(document.getElementById('mvStatus').textContent));
    const arrText = await page.evaluate(() => document.getElementById('mvStatus').textContent);
    check(/651\.9 GB free of 7\.3 TB/.test(arrText) && /Could not reach Sonarr Anime/.test(arrText), 'status tab: Sonarr/Radarr state, version, root-folder free space and errors (read-only)');
    check(/expected and fine/.test(arrText), 'status tab: says Jellyfin not writing to media drives is expected');
    await page.click('#mvStatus button.button-submit');
    await page.waitForSelector('#mvStatus .mv-warn button');
    await page.click('#mvStatus .mv-warn button.button-submit');
    await page.waitForFunction(() => /Live: visible to all users/.test(document.getElementById('mvStatus').textContent));
    check(posted.Visibility && posted.Visibility.previewOnly === false, 'Go live: in-page confirmation then POST Visibility');

    await page.click('[data-tab="new"]');
    await page.waitForFunction(() => /Closes Monday 8 Jun 2026 at 20:00 BST \(in 7 days\)/.test(document.querySelector('#mvDeadlinePicker .mv-deadline-result').textContent));
    check(JSON.stringify(state.deadlineCalls[0]) === JSON.stringify({ kind: 'Relative', amount: 7, unit: 'days', time: '20:00' }), 'deadline picker defaults to "in 7 days at 20:00" and shows the server\'s plain-words result');
    await page.click('#mvDeadlinePicker [data-preset="tonight"]');
    await page.waitForFunction(() => /already past 20:00/.test(document.querySelector('#mvDeadlinePicker .mv-deadline-result').textContent));
    check(true, 'a rejected choice shows the reason (for example "Tonight" after 20:00)');
    await page.click('#mvDeadlinePicker [data-preset="tomorrow"]');
    await page.waitForFunction(() => /Closes/.test(document.querySelector('#mvDeadlinePicker .mv-deadline-result').textContent));
    await page.selectOption('#mvDeadlinePicker select', 'hours');
    check(await page.evaluate(() => document.querySelector('#mvDeadlinePicker input[type=time]').hidden), 'the time field is hidden when the unit is hours');
    await page.selectOption('#mvDeadlinePicker select', 'days');
    await page.fill('#mvDeadlinePicker input[type=number]', '3');
    await page.waitForTimeout(400);
    check(JSON.stringify(state.deadlineCalls[state.deadlineCalls.length - 1]) === JSON.stringify({ kind: 'Relative', amount: 3, unit: 'days', time: '20:00' }), 'flexible row: "In 3 days at 20:00" is sent to the server');
    check(await page.evaluate(() => /"amount":3/.test(localStorage.getItem('mediavote-deadline-choice') || '')), 'the last deadline choice is remembered for this viewer');

    // Hand-picked titles: the shared media browser in picking mode.
    await page.selectOption('#mvScope', 'HandPicked');
    await page.waitForSelector('#mvBrowser .mediavote-card', { timeout: 8000 });
    check(state.libLoaded === '?lib=1', 'admin page loads client.js in library mode for the shared browser');
    check(await page.evaluate(() => document.querySelectorAll('#mvBrowser .mediavote-card').length === 30), 'browser shows the first page of 30 titles (paged on the server)');
    const cardText = await page.evaluate(() => document.querySelector('#mvBrowser .mediavote-card[data-id="b1"]').textContent);
    check(/8TB 1/.test(cardText) && /90% full/.test(cardText) && /Last played by sam/.test(cardText) && /3 plays, 2 viewers/.test(cardText) && /added/.test(cardText) && /Removable via Sonarr\/Radarr/.test(cardText), 'title card: drive with % full, last played by whom, plays and viewers, date added, removable');
    check(await page.evaluate(() => /Never watched/.test(document.querySelector('#mvBrowser .mediavote-card[data-id="b0"]').textContent) && /4 of 10 episodes watched/.test(document.querySelector('#mvBrowser .mediavote-card[data-id="b0"]').textContent)), 'never-watched badge and episodes watched for a series');
    await page.click('#mvBrowser .mediavote-card[data-id="b4"] .mediavote-pick');
    await page.waitForFunction(() => /1 selected · 5.0 GB/.test(document.querySelector('#mvBrowser .mediavote-tray').textContent));
    check(/8TB 1: 5.0 GB \(90% → 90% full/.test(await page.evaluate(() => document.querySelector('#mvBrowser .mediavote-tray').textContent)), 'selection tray: count, total size and per-drive projection');
    await page.evaluate(() => Array.prototype.find.call(document.querySelectorAll('#mvBrowser .mediavote-tray button'), b => /Select all/.test(b.textContent)).click());
    await page.waitForFunction(() => /4 selected/.test(document.querySelector('#mvBrowser .mediavote-tray').textContent));
    check(true, '"select all in current filter" adds every matching title');
    await page.evaluate(() => Array.prototype.find.call(document.querySelectorAll('#mvBrowser .mediavote-presets button'), b => b.textContent === 'Never watched').click());
    await page.waitForFunction(() => document.querySelectorAll('#mvBrowser .mediavote-card').length === 10);
    check(state.browseQueries[state.browseQueries.length - 1].watched === 'never', 'preset "Never watched" filters on the server');
    await page.evaluate(() => Array.prototype.find.call(document.querySelectorAll('#mvBrowser .mediavote-presets button'), b => b.textContent === 'Fullest drive only').click());
    await page.waitForTimeout(300);
    const fullest = state.browseQueries[state.browseQueries.length - 1];
    check(fullest.drive === '/LM1' && fullest.sort === 'size' && fullest.descending === 'true', 'preset "Fullest drive only" picks the fullest drive, largest first');
    const listBefore = await page.evaluate(() => document.querySelector('#mvBrowser .mediavote-grid').classList.contains('mediavote-list'));
    await page.evaluate(() => Array.prototype.find.call(document.querySelectorAll('#mvBrowser .mediavote-toolbar button'), b => /List view|Grid view/.test(b.textContent)).click());
    check(await page.evaluate(() => document.querySelector('#mvBrowser .mediavote-grid').classList.contains('mediavote-list')) === !listBefore, 'grid/list toggle switches the layout');
    check(listBefore === true, 'list view is the default on a phone-sized screen');

    // Free-space target fields.
    await page.selectOption('#mvTargetMode', 'FreeSpace');
    check(await page.isVisible('#mvTargetDrive') && /Maximum number of titles/.test(await page.textContent('#mvWinnersLabel')), 'free-space target shows the drive and turns N into a maximum');
    check(await page.evaluate(() => document.querySelectorAll('#mvTargetDrive option').length === 2), 'target drive list comes from the detected drives');
    await page.selectOption('#mvTargetMode', 'FixedCount');
    check(await page.evaluate(() => document.querySelector('input[name="mvOutcome"]:checked').value === 'NotifyOnly'), 'outcome defaults to Notify only');
    await page.fill('#mvTitle', 'Spring clean');
    await page.click('#mvPreviewBtn');
    await page.waitForSelector('#mvPreview .mv-ok');
    check(await page.evaluate(() => /12 titles/.test(document.getElementById('mvPreview').textContent) && /freed/.test(document.getElementById('mvPreview').textContent)), 'preview shows count and space that would be freed');
    check(await page.evaluate(() => /not removable automatically/.test(document.getElementById('mvPreview').textContent)), 'preview marks titles Sonarr/Radarr do not manage');
    await page.check('#mvPreview input[type="checkbox"]');
    check(await page.evaluate(() => document.querySelectorAll('#mvPreview tbody tr').length === 1), 'preview filter: removable only');

    await page.check('input[name="mvOutcome"][value="DeleteViaArr"]');
    check(await page.isVisible('#mvDeleteBox'), 'delete confirmation checkbox appears for "Delete via Sonarr/Radarr"');
    check(await page.evaluate(() => /at most 5 title/.test(document.getElementById('mvMaxNote').textContent)), 'safety maximum shown');
    await page.click('#mvPreviewBtn');
    await page.waitForFunction(() => /were left out/.test(document.getElementById('mvPreview').textContent));
    check(true, 'preview for a delete vote says unmanaged titles are left out');
    await page.click('#mvCreateBtn');
    check(!posted.Sessions, 'create is blocked until the deletion confirmation is ticked');
    await page.check('#mvDeleteConfirm');
    await page.click('#mvCreateBtn');
    await page.waitForFunction(() => document.querySelector('[data-panel="votes"]').hidden === false);
    check(posted.Sessions && posted.Sessions.settings.title === 'Spring clean' && posted.Sessions.settings.outcome === 'DeleteViaArr' && posted.Sessions.settings.deleteConfirmed === true && posted.Sessions.settings.includeUnmanagedTitles === false && posted.Sessions.deadline.kind === 'Relative' && posted.Sessions.deadline.amount === 3, 'create sends the expected settings and the deadline choice');
    check(posted.Sessions.settings.scope === 'HandPicked' && posted.Sessions.settings.handPickedItemIds.length === 4, 'create sends the titles picked in the browser');

    await page.click('[data-tab="settings"]');
    check(await page.evaluate(() => document.querySelector('#sArr input[type="password"]').value === '••••••1234'), 'API key is shown masked');
    await page.click('#sArr .mv-card button');
    await page.waitForFunction(() => /Connected/.test(document.getElementById('sArr').textContent));
    check(posted['Test/Radarr'] && posted['Test/Radarr'].url === 'http://radarr:7878' && posted['Test/Radarr'].id === 'r1', 'test connection posts the URL and the instance id (to use the saved key)');
    await page.evaluate(() => Array.prototype.find.call(document.querySelectorAll('#sArr button'), b => b.textContent === 'Add Sonarr').click());
    await page.evaluate(() => { const cards = document.querySelectorAll('#sArr .mv-card'); const last = cards[cards.length - 1]; const inputs = last.querySelectorAll('input'); inputs[0].value = 'Sonarr Anime'; inputs[1].value = 'http://anime:8989'; inputs[2].value = 'newkey'; });
    check(await page.evaluate(() => /data-mediavote-mount="tab"/.test(document.getElementById('sSnippet').value) && !/script/i.test(document.getElementById('sSnippet').value)), 'Vote tab card shows the minimal snippet (no script)');
    await page.click('#sCopySnippet');
    check(await page.evaluate(() => document.getElementById('sCopyResult').textContent.length > 0), 'copy button gives in-page feedback');
    await page.uncheck('#sBanner'); await page.uncheck('#sTab'); await page.uncheck('#sPage'); await page.uncheck('#sStandalone');
    await page.click('#mvSettingsForm button[type="submit"]');
    await page.waitForFunction(() => /Settings saved/.test(document.getElementById('mvMsg').textContent));
    check(posted.Settings && posted.Settings.enableBanner === false && posted.Settings.enableStandalonePage === false && posted.Settings.arrInstances[0].apiKey.indexOf('•') === 0, 'every entry point can be off (staged rollout); masked key sent back unchanged');
    check(posted.Settings.arrInstances.length === 2 && posted.Settings.arrInstances[1].kind === 'Sonarr' && posted.Settings.arrInstances[1].name === 'Sonarr Anime' && posted.Settings.arrInstances[1].id === null && posted.Settings.maxRemovalsPerVote === 5, 'a second instance (Anime Sonarr) can be added');
    // Result of a "Delete via Sonarr/Radarr" vote.
    state.withSession = true;
    await page.click('[data-tab="votes"]');
    await page.waitForSelector('#mvHistory button');
    await page.click('#mvHistory button');
    await page.waitForFunction(() => /Removed by Sonarr\/Radarr/.test(document.getElementById('mvDetail').textContent));
    const res = await page.evaluate(() => document.getElementById('mvDetail').textContent);
    check(/DELETE VIA SONARR\/RADARR/.test(res) && /To remove by hand/.test(res) && /Home Video/.test(res), 'result: unmanaged winners listed for removal by hand');
    check(/Access to the path '\/movies\/Locked Film' is denied\./.test(res) && /refused the delete/.test(res), 'result: Sonarr/Radarr error shown verbatim');
    check(/FILES STILL ON DISK/.test(res) && /needs attention/.test(res), 'result: files left on disk after removal are flagged');
    check(/Retry titles not removed/.test(res) && !/Preflight/i.test(res), 'result: Retry offered; no pre-flight wording');

    // Approval needed (banner on the Votes tab) → review → approve with in-page confirmation.
    check(await page.evaluate(() => /“Autumn” is waiting for your approval: 2 title\(s\), 12.0 GB/.test(document.getElementById('mvAlerts').textContent)), 'votes tab: approval-needed banner');
    check(await page.evaluate(() => /Recurring vote “Monthly clean-up” is ready to review/.test(document.getElementById('mvAlerts').textContent)), 'votes tab: recurring draft waiting for review (never opens by itself)');
    await page.evaluate(() => Array.prototype.find.call(document.querySelectorAll('#mvAlerts button'), b => b.textContent === 'Review').click());
    await page.waitForFunction(() => /Waiting for your approval/.test(document.getElementById('mvDetail').textContent));
    await page.evaluate(() => Array.prototype.find.call(document.querySelectorAll('#mvDetail button'), b => b.textContent === 'Approve removal').click());
    await page.evaluate(() => Array.prototype.find.call(document.querySelectorAll('#mvDetail .mv-warn button'), b => b.textContent === 'Approve').click());
    await page.waitForFunction(() => /Approved/.test(document.getElementById('mvMsg').textContent));
    check(state.approved === true, 'approve: in-page confirmation, then POST Approve');

    // Templates: save the current settings as a monthly template.
    await page.click('[data-tab="new"]');
    await page.fill('#mvTitle', 'Monthly clean-up');
    await page.evaluate(() => { document.getElementById('mvSaveTemplate').open = true; });
    await page.fill('#mvTplName', 'Monthly clean-up');
    await page.selectOption('#mvTplRecur', 'Monthly');
    check(await page.isVisible('#mvTplFirst'), 'recurring templates ask when the first draft is due');
    await page.fill('#mvTplFirst', '2026-11-01T09:00');
    await page.click('#mvTplSave');
    await page.waitForFunction(() => /Template saved/.test(document.getElementById('mvMsg').textContent));
    check(posted.Templates && posted.Templates.recurrence === 'Monthly' && posted.Templates.firstRunLocal === '2026-11-01T09:00' && posted.Templates.settings.title === 'Monthly clean-up' && posted.Templates.deadline.kind, 'save as template sends settings, deadline choice and schedule');
    await page.waitForFunction(() => document.querySelectorAll('#mvTemplateSelect option').length === 2);
    await page.fill('#mvTitle', '');
    await page.selectOption('#mvTemplateSelect', 't1');
    await page.click('#mvTemplateUse');
    check(await page.inputValue('#mvTitle') === 'Monthly clean-up', 'start from a template fills the form in one click');

    // Storage overview and history.
    await page.click('[data-tab="storage"]');
    await page.waitForSelector('#mvStorage .mv-card');
    const storage = await page.textContent('#mvStorage');
    check(/8TB 1/.test(storage) && /90% full/.test(storage) && /Largest libraries here/.test(storage) && /frees 134.0 GB on 8TB 1 \(90% to 89%\)/.test(storage), 'storage tab: drive bars, largest libraries, open vote projection');
    await page.click('[data-tab="history"]');
    await page.waitForSelector('#mvStats .mv-card');
    const stats = await page.textContent('#mvStats');
    check(/Space freed: 300.0 GB in 2 resolved vote/.test(stats) && /Old Film/.test(stats) && /alex/.test(stats) && /62.5%/.test(stats), 'history tab: space freed over time, most-voted titles, turnout, participation');

    // Settings: drive names, thresholds, approval, watch details.
    await page.click('[data-tab="settings"]');
    await page.waitForSelector('#sDrives input[data-mount="/LM2"]');
    await page.fill('#sDrives input[data-mount="/LM2"]', 'Big Seagate');
    await page.selectOption('#sWatch', 'CountsOnly');
    await page.fill('#sAutoApprove', '48');
    await page.click('#mvSettingsForm button[type="submit"]');
    await page.waitForFunction(() => /Settings saved/.test(document.getElementById('mvMsg').textContent));
    check(posted.Settings.driveNames.some(d => d.mountPoint === '/LM2' && d.name === 'Big Seagate') && posted.Settings.watchDetailsForVoters === 'CountsOnly' && posted.Settings.autoApproveHours === 48 && posted.Settings.requireApprovalForDeletes === true, 'settings save drive names, watch details and approval options');

    const overflow = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
    check(overflow, 'admin page has no horizontal overflow on a 375px phone');
    check(errors.length === 0, 'no console errors: ' + errors.join(' | '));
    await browser.close();
    console.log(failures ? failures + ' check(s) failed' : 'All admin smoke checks passed');
    process.exit(failures ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
