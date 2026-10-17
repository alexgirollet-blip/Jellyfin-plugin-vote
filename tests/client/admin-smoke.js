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
            return json({ nowLocal: 'Sat 1 Jun 2026 12:00', defaultDeadlineInput: '2026-06-08T20:00', timeZone: 'Europe/London', timeZoneFound: true, customTabHtml: '<div data-mediavote-mount="tab"></div>',
                injection: { mode: 'Failed', message: 'index.html is not writable', dockerFix: 'Install File Transformation…' }, integrations: [{ name: 'Custom Tabs', detected: true, version: '0.2.10.0', enabled: true, registered: true, message: 'ok' }],
                processUser: 'uid 1000, gid 1000', dataDirectory: '/config/data/mediavote', openSessionId: null, sessions: [] });
        }
        if (p === 'Settings' && route.request().method() === 'GET') { return json(settings); }
        if (p === 'Libraries') { return json([{ id: 'lib1', name: 'Movies', collectionType: 'movies', protected: false }, { id: 'lib2', name: 'Kids', collectionType: 'movies', protected: true }]); }
        if (p === 'Users') { return json([{ id: 'u1', name: 'alex', disabled: false, admin: true }, { id: 'u2', name: 'sam', disabled: false, admin: false }]); }
        if (p === 'Preview') { return json({ count: 12, movies: 10, series: 2, totalBytes: 5e11, maxFreedBytes: 6e10, items: [{ name: 'Big', year: 2001, kind: 'Movie', sizeBytes: 6e10, playCount: 0 }] }); }
        if (p === 'Sessions') { return json({ ok: true, id: 'new-id' }); }
        if (p === 'Settings') { return json({ ok: true }); }
        if (p.indexOf('Test/') === 0) { return json({ ok: true, message: 'Connected: Radarr 5.14' }); }
        return route.fulfill({ status: 404, body: '' });
    });
    await page.goto(ORIGIN + '/admin');
    await page.waitForSelector('#mvHistory tr');
    check(await page.evaluate(() => /No votes yet/.test(document.getElementById('mvHistory').textContent)), 'votes tab shows empty history');

    await page.click('[data-tab="status"]');
    await page.waitForSelector('#mvStatus .mv-warn');
    check(await page.evaluate(() => /Install File Transformation/.test(document.getElementById('mvStatus').textContent)), 'status tab shows the Docker / File Transformation fix when injection failed');

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
    await page.uncheck('#sBanner'); await page.uncheck('#sTab'); await page.uncheck('#sPage'); await page.uncheck('#sStandalone');
    await page.click('#mvSettingsForm button[type="submit"]');
    check(!posted.Settings && await page.isChecked('#sBanner'), 'banner cannot be disabled when no other entry point is on');
    await page.uncheck('#sBanner'); await page.check('#sPage');
    await page.click('#mvSettingsForm button[type="submit"]');
    await page.waitForFunction(() => /Settings saved/.test(document.getElementById('mvMsg').textContent));
    check(posted.Settings && posted.Settings.enableBanner === false && posted.Settings.radarrApiKey.indexOf('•') === 0, 'settings save keeps the masked key (unchanged) and the new toggles');
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
    check(overflow, 'admin page has no horizontal overflow on a 375px phone');
    check(errors.length === 0, 'no console errors: ' + errors.join(' | '));
    await browser.close();
    console.log(failures ? failures + ' check(s) failed' : 'All admin smoke checks passed');
    process.exit(failures ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
