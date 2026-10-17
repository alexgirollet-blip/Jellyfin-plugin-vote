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

const harness = `<!DOCTYPE html><html><head><meta name="viewport" content="width=device-width, initial-scale=1"></head><body style="background:#fff;color:#111">
<script>
window.ApiClient = { accessToken: function(){return 'admintok';}, getUrl: function(p){ return '${ORIGIN}/' + p.replace(/^\\//,''); } };
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
    const state = { usersFail: true, kill: false, preview: true };
    const settings = {
        enableBanner: true, enableCustomTab: true, enablePluginPage: true, enableHomeSection: true, enableStandalonePage: true, tabTitle: 'Vote',
        timeZoneId: 'Europe/London', closingSoonHours: 24, resultBannerDays: 7, usePlaybackReporting: true, protectedItemIds: [], protectedItemNames: {}, protectedLibraryIds: [],
        radarrUrl: 'http://radarr:7878', radarrApiKey: '••••••1234', radarrAction: 'Unmonitor',
        sonarrUrl: '', sonarrApiKey: '', sonarrAction: 'Unmonitor', jellyseerrUrl: '', jellyseerrApiKey: '', jellyseerrAction: 'Nothing',
        webhookUrl: '', webhookKind: 'Discord', webhookOnOpened: true, webhookOnClosingSoon: true, webhookOnResults: true, publicServerUrl: ''
    };
    await page.route(ORIGIN + '/**', async route => {
        const url = new URL(route.request().url());
        const p = url.pathname.replace('/MediaVote/Admin/', '');
        const json = b => route.fulfill({ contentType: 'application/json', body: JSON.stringify(b) });
        if (url.pathname === '/admin') { return route.fulfill({ contentType: 'text/html', body: harness }); }
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
                processUser: 'uid 1000, gid 1000', dataDirectory: '/config/data/mediavote', openSessionId: null, sessions: [] });
        }
        if (p === 'Settings' && route.request().method() === 'GET') { return json(settings); }
        if (p === 'Libraries') { return json([{ id: 'lib1', name: 'Movies', collectionType: 'movies', protected: false }, { id: 'lib2', name: 'Kids', collectionType: 'movies', protected: true }]); }
        if (p === 'Users') {
            if (state.usersFail) { return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ error: 'jellyfin_version_mismatch', message: "Media Vote error: Method not found: 'IUserManager.get_Users()'" }) }); }
            return json([{ id: 'u1', name: 'alex', disabled: false, admin: true }, { id: 'u2', name: 'sam', disabled: false, admin: false }, { id: 'u3', name: 'old friend', disabled: true, admin: false }]);
        }
        if (p === 'Visibility') { state.preview = posted.Visibility.previewOnly; return json({ ok: true, previewOnly: state.preview }); }
        if (p === 'Preview') { return json({ count: 12, movies: 10, series: 2, totalBytes: 5e11, maxFreedBytes: 6e10, items: [{ name: 'Big', year: 2001, kind: 'Movie', sizeBytes: 6e10, playCount: 0 }] }); }
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
    check(await page.evaluate(() => document.getElementById('sRadarrUrl') && document.getElementById('sRadarrUrl').value === 'http://radarr:7878'), 'users failing: settings still loaded');
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
    await page.click('#mvStatus button.button-submit');
    await page.waitForSelector('#mvStatus .mv-warn button');
    await page.click('#mvStatus .mv-warn button.button-submit');
    await page.waitForFunction(() => /Live: visible to all users/.test(document.getElementById('mvStatus').textContent));
    check(posted.Visibility && posted.Visibility.previewOnly === false, 'Go live: in-page confirmation then POST Visibility');

    await page.click('[data-tab="new"]');
    check(await page.inputValue('#mvDeadline') === '2026-06-08T20:00', 'deadline defaults to next week 20:00 local');
    check(await page.evaluate(() => document.querySelector('input[name="mvOutcome"]:checked').value === 'NotifyOnly'), 'outcome defaults to Notify only');
    await page.fill('#mvTitle', 'Spring clean');
    await page.click('#mvPreviewBtn');
    await page.waitForSelector('#mvPreview .mv-ok');
    check(await page.evaluate(() => /12 titles/.test(document.getElementById('mvPreview').textContent) && /freed/.test(document.getElementById('mvPreview').textContent)), 'preview shows count and space that would be freed');

    await page.check('input[name="mvOutcome"][value="DeleteAutomatically"]');
    check(await page.isVisible('#mvDeleteBox'), 'delete confirmation checkbox appears for automatic deletion');
    await page.click('#mvCreateBtn');
    check(!posted.Sessions, 'create is blocked until the deletion confirmation is ticked');
    await page.check('#mvDeleteConfirm');
    await page.click('#mvCreateBtn');
    await page.waitForFunction(() => document.querySelector('[data-panel="votes"]').hidden === false);
    check(posted.Sessions && posted.Sessions.settings.title === 'Spring clean' && posted.Sessions.settings.outcome === 'DeleteAutomatically' && posted.Sessions.settings.deleteConfirmed === true && posted.Sessions.deadlineLocal === '2026-06-08T20:00', 'create sends the expected settings');

    await page.click('[data-tab="settings"]');
    check(await page.inputValue('#sRadarrKey') === '••••••1234', 'API key is shown masked');
    await page.click('#sArr button');
    await page.waitForFunction(() => /Connected/.test(document.getElementById('sArr').textContent));
    check(posted['Test/Radarr'] && posted['Test/Radarr'].url === 'http://radarr:7878', 'test connection posts the URL');
    check(await page.evaluate(() => /data-mediavote-mount="tab"/.test(document.getElementById('sSnippet').value) && !/script/i.test(document.getElementById('sSnippet').value)), 'Vote tab card shows the minimal snippet (no script)');
    await page.click('#sCopySnippet');
    check(await page.evaluate(() => document.getElementById('sCopyResult').textContent.length > 0), 'copy button gives in-page feedback');
    await page.uncheck('#sBanner'); await page.uncheck('#sTab'); await page.uncheck('#sPage'); await page.uncheck('#sStandalone');
    await page.click('#mvSettingsForm button[type="submit"]');
    await page.waitForFunction(() => /Settings saved/.test(document.getElementById('mvMsg').textContent));
    check(posted.Settings && posted.Settings.enableBanner === false && posted.Settings.enableStandalonePage === false && posted.Settings.radarrApiKey.indexOf('•') === 0, 'every entry point can be off (staged rollout); masked key sent back unchanged');
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
    check(overflow, 'admin page has no horizontal overflow on a 375px phone');
    check(errors.length === 0, 'no console errors: ' + errors.join(' | '));
    await browser.close();
    console.log(failures ? failures + ' check(s) failed' : 'All admin smoke checks passed');
    process.exit(failures ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
