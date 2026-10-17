/*
 * Headless smoke test for the Media Vote client script against a MOCK jellyfin-web page.
 * It is not a substitute for testing on a real server and real devices (see README checklist),
 * but it verifies banner placement, re-render resilience, the overlay, voting calls, the
 * keyboard guard and the "no active vote" state.
 *
 * Run: NODE_PATH=$(npm root -g) node tests/client/smoke.js
 * (needs the "playwright" package and a Chromium build; PLAYWRIGHT_BROWSERS_PATH is honoured)
 */
'use strict';
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const assets = path.join(__dirname, '../../src/Jellyfin.Plugin.MediaVote/Assets');
const ORIGIN = 'http://jf.test';

const items = Array.from({ length: 45 }, (_, i) => ({
    id: 'item-' + i, name: 'Title ' + i, year: 2000 + i, kind: i % 2 ? 'Series' : 'Movie', library: 'Lib',
    sizeBytes: (i + 1) * 1e9, dateAddedUtc: '2024-01-01T00:00:00Z', lastPlayedUtc: null, playCount: 0, genres: ['Drama'],
    imageTag: null, removeVotes: 0, keepVotes: null, removeVoters: [], keepVoters: [], myRemoveVotes: 0, myKeepVotes: 0,
    lastPlayedBy: i === 1 ? 'AlexG' : null, viewerCount: i === 1 ? 3 : 0, neverWatched: i !== 1,
    drives: i === 1 ? [{ key: '/LM1', name: '8TB 1', bytes: 2e9, known: true, usedBytes: 6.6e12, totalBytes: 7.3e12, usedPercent: 96.2, level: 'Critical' }] : []
}));
items[1].lastPlayedUtc = new Date(Date.now() - 95 * 86400000).toISOString();
items[1].playCount = 12;

function statusFor(state) {
    if (state.hidden || state.disabled) {
        return { serverTimeUtc: new Date().toISOString(), isAdmin: false, disabled: !!state.disabled, preview: !!state.hidden, hidden: true,
            entryPoints: { banner: false, tab: false, page: false, standalone: true }, closingSoonHours: 24, active: null, lastResult: null };
    }
    return {
        preview: !!state.adminPreview,
        hidden: false,
        disabled: false,
        serverTimeUtc: new Date().toISOString(),
        isAdmin: false,
        entryPoints: { banner: true, tab: true, page: true, standalone: true },
        closingSoonHours: 24,
        active: state.active ? {
            id: 's1', title: 'Spring clean', description: 'Pick what goes', deadlineUtc: new Date(Date.now() + 86400000).toISOString(),
            deadlineLocal: 'Saturday 1 June 2026, 20:00', timeZone: 'Europe/London', eligible: state.eligible,
            votesPerUser: 3, votesUsed: 3 - state.remaining, votesRemaining: state.remaining, allowKeepVotes: false, keepVotesPerUser: 0, keepVotesRemaining: 0,
            allowMultipleVotesPerItem: false, showLiveTallies: true, showVoterNames: true, winnerCount: 2, outcome: 'NotifyOnly',
            hasVoted: state.remaining < 3, closingSoon: false, candidateCount: items.length
        } : null,
        lastResult: state.active ? null : { id: 's0', title: 'Winter clean', resolvedUtc: '2026-01-01T00:00:00Z', outcome: 'NotifyOnly', turnoutPercent: 80, turnoutMet: true, showBanner: !!state.resultBanner && !state.dismissed, totalFreedBytes: 4e10, items: [{ id: 'x', name: 'Old Film', kind: 'Movie', score: 3, status: 'flagged' }, { id: 'y', name: 'Gone Film', kind: 'Movie', score: 5, status: 'removed', sizeBytes: 4e10, requestUrl: 'https://requests.example/movie/603' }] }
    };
}

const harness = `<!DOCTYPE html><html><head><meta name="viewport" content="width=device-width, initial-scale=1"></head>
<body class="skinBody" style="background:#181818;color:#eee;margin:0">
<div class="skinHeader" style="height:50px">Header</div>
<div id="indexPage" class="page homePage libraryPage">
  <div class="tabContent pageTabContent is-active" id="homeTab" data-index="0"><div class="sections"><div class="verticalSection">Continue watching</div></div></div>
  <div class="tabContent pageTabContent" id="favoritesTab" data-index="1"><div class="sections"></div></div>
</div>
<div id="tabHost"></div>
<div id="customTabMount"><div data-mediavote-mount="tab" class="mediavote-tab-host">Media Vote is loading. If this stays empty, reload the page.</div></div>
<button id="outside">outside</button>
<script>
  window.ApiClient = { accessToken: function(){return 'tok';}, getCurrentUserId: function(){return 'u1';}, serverAddress: function(){return '${ORIGIN}';},
    getUrl: function(p){ return '${ORIGIN}/' + p.replace(/^\\//,''); }, serverId: function(){ return 'srv'; } };
  window.__jeFired = 0;
  // Simulates Jellyfin Enhanced: capture-phase listener on document that ignores INPUT/TEXTAREA only.
  document.addEventListener('keydown', function (e) { if (['INPUT','TEXTAREA'].indexOf(document.activeElement.tagName) >= 0) return; window.__jeFired++; }, true);
  // Simulates Media Bar: an absolutely positioned slideshow appended to <body>.
  var sc = document.createElement('div'); sc.id = 'slides-container'; sc.style.cssText = 'position:absolute;top:0;left:0;width:100%;height:300px;'; document.body.appendChild(sc);
</script>
<script src="/MediaVote/client.js" defer></script>
</body></html>`;

let failures = 0;
function check(cond, msg) {
    console.log((cond ? 'PASS ' : 'FAIL ') + msg);
    if (!cond) { failures++; }
}

(async () => {
    const browser = await chromium.launch();
    const state = { active: true, eligible: true, remaining: 3, votes: [] };
    async function newPage(viewport) {
        const context = await browser.newContext({ viewport });
        const page = await context.newPage();
        const errors = [];
        page.on('pageerror', e => errors.push(String(e)));
        page.on('console', m => { if (m.type() === 'error') { errors.push(m.text()); } });
        await page.route(ORIGIN + '/**', async route => {
            const url = new URL(route.request().url());
            const p = url.pathname;
            if (p === '/web/index.html') { return route.fulfill({ contentType: 'text/html; charset=utf-8', body: harness }); }
            if (p === '/MediaVote/client.js') {
                if (state.clientStatus) { return route.fulfill({ status: state.clientStatus, body: 'error' }); }
                return route.fulfill({ contentType: 'application/javascript; charset=utf-8', body: fs.readFileSync(path.join(assets, 'client.js'), 'utf8') });
            }
            if (p === '/MediaVote/client.css') { return route.fulfill({ contentType: 'text/css', body: fs.readFileSync(path.join(assets, 'client.css'), 'utf8') }); }
            if (p === '/MediaVote/Status') {
                state.statusCalls = (state.statusCalls || 0) + 1;
                if (state.statusError) { return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ error: 'server_error', message: 'Media Vote error: boom' }) }); }
                return route.fulfill({ contentType: 'application/json', body: JSON.stringify(statusFor(state)) });
            }
            if (p === '/MediaVote/Items') {
                state.itemsCalls = (state.itemsCalls || 0) + 1;
                const pageNo = Number(url.searchParams.get('page') || 0);
                const size = Number(url.searchParams.get('pageSize') || 30);
                return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ total: items.length, page: pageNo, pageSize: size, genres: ['Drama'], items: items.slice(pageNo * size, pageNo * size + size) }) });
            }
            if (p === '/MediaVote/Results/s0/Dismiss') {
                state.dismissCalls = (state.dismissCalls || 0) + 1;
                if (state.dismissFails) { return route.fulfill({ status: 500, contentType: 'application/json', body: '{"error":"server_error","message":"boom"}' }); }
                state.dismissed = true;
                return route.fulfill({ contentType: 'application/json', body: '{"ok":true}' });
            }
            if (p === '/MediaVote/Vote') {
                const auth = route.request().headers()['authorization'] || '';
                state.votes.push({ body: JSON.parse(route.request().postData()), auth });
                state.remaining--;
                return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ ok: true, votesRemaining: state.remaining, keepVotesRemaining: 0 }) });
            }
            return route.fulfill({ status: 404, body: '' });
        });
        await page.goto(ORIGIN + '/web/index.html#/home');
        return { page, errors, context };
    }

    // ---------------- desktop
    let { page, errors, context } = await newPage({ width: 1280, height: 800 });
    await page.waitForSelector('#mediavote-banner', { timeout: 5000 });
    check(await page.evaluate(() => document.querySelector('#homeTab').firstElementChild.id === 'mediavote-banner'), 'banner is the first element of the home tab');
    check(await page.evaluate(() => getComputedStyle(document.getElementById('mediavote-banner')).zIndex === '11'), 'banner stacks above Media Bar slides (z-index 11)');

    // Home Screen Sections style re-render: replace the sections content and the element itself.
    await page.evaluate(() => {
        const tab = document.querySelector('#homeTab');
        tab.querySelector('.sections').innerHTML = '<div class="homeSectionsContainer">HSS</div>';
        const fresh = document.createElement('div'); fresh.className = 'sections homeSectionsContainer'; fresh.textContent = 'rerendered';
        tab.replaceChild(fresh, tab.querySelector('.sections'));
    });
    await page.waitForTimeout(400);
    check(await page.evaluate(() => document.querySelectorAll('#mediavote-banner').length === 1 && document.querySelector('#homeTab').firstElementChild.id === 'mediavote-banner'), 'banner survives a home re-render exactly once');

    // Another plugin inserts something at the top: banner moves back above it.
    await page.evaluate(() => { const d = document.createElement('div'); d.id = 'other-plugin-top'; document.querySelector('#homeTab').prepend(d); });
    await page.waitForTimeout(400);
    check(await page.evaluate(() => document.querySelector('#homeTab').firstElementChild.id === 'mediavote-banner'), 'banner returns to the top after another element is inserted above it');

    // Navigate away: banner removed; back home: banner back, once.
    await page.evaluate(() => { document.getElementById('indexPage').classList.add('hide'); location.hash = '#/movies'; });
    await page.waitForTimeout(400);
    check(await page.evaluate(() => !document.getElementById('mediavote-banner')), 'banner removed when leaving the home page');
    await page.evaluate(() => { document.getElementById('indexPage').classList.remove('hide'); location.hash = '#/home'; });
    await page.waitForSelector('#mediavote-banner', { timeout: 3000 });
    check(await page.evaluate(() => document.querySelectorAll('#mediavote-banner').length === 1), 'banner re-added once on returning home');

    // Open the overlay.
    await page.click('.mediavote-banner-main');
    await page.waitForSelector('#mediavote-overlay .mediavote-card');
    check(await page.evaluate(() => document.body.style.position === 'fixed'), 'background scroll locked while overlay is open');
    check(await page.evaluate(() => document.querySelectorAll('#mediavote-overlay .mediavote-card').length === 30), 'first page of 30 items rendered (paginated)');
    check(await page.evaluate(() => document.getElementById('mediavote-overlay').getAttribute('aria-modal') === 'true'), 'overlay is an aria-modal dialog');

    // Vote.
    await page.click('#mediavote-overlay .mediavote-card [data-action="vote"]');
    await page.waitForFunction(() => /2 of 3 votes left/.test(document.querySelector('#mediavote-overlay .mediavote-footer').textContent));
    check(state.votes.length === 1 && state.votes[0].body.itemId === 'item-0' && state.votes[0].body.kind === 'Remove', 'vote POST sent with item id and kind');
    check(/MediaBrowser Token="tok"/.test(state.votes[0].auth), 'vote request carries the Jellyfin token');
    check(await page.evaluate(() => !!document.querySelector('#mediavote-overlay .mediavote-card[data-id="item-0"] [data-action="unvote"]')), 'card switches to "undo" after voting');

    // Keyboard guard: a key pressed on our <select> must not reach the Enhanced-style listener.
    const before = await page.evaluate(() => window.__jeFired);
    await page.focus('#mediavote-overlay .mediavote-toolbar select');
    await page.keyboard.press('k');
    check(await page.evaluate(() => window.__jeFired) === before, 'shortcut listener does not fire for keys inside our UI');

    // Focus trap: Tab from the last focusable wraps inside the overlay.
    await page.evaluate(() => { const f = document.querySelectorAll('#mediavote-overlay button'); f[f.length - 1].focus(); });
    await page.keyboard.press('Tab');
    check(await page.evaluate(() => document.getElementById('mediavote-overlay').contains(document.activeElement)), 'focus stays trapped in the overlay');

    // Escape closes and restores scroll.
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);
    check(await page.evaluate(() => !document.getElementById('mediavote-overlay') && document.body.style.position === ''), 'Escape closes the overlay and restores page scroll');
    await page.focus('#outside');
    await page.keyboard.press('k');
    check(await page.evaluate(() => window.__jeFired) > before, 'shortcuts still work outside our UI');

    // Mount point (Custom Tabs / Plugin Pages).
    await page.evaluate(() => { document.getElementById('tabHost').innerHTML = '<div data-mediavote-mount="tab"></div>'; });
    await page.waitForSelector('#tabHost [data-mediavote-mount] .mediavote-card', { timeout: 3000 });
    check(true, 'tab/page mount point renders the same voting UI');
    // Shared browser cards in the voting view: drive bar, watch details, filters.
    await page.click('.mediavote-banner-main');
    await page.waitForSelector('#mediavote-overlay .mediavote-card[data-id="item-1"]');
    const card1 = await page.evaluate(() => document.querySelector('#mediavote-overlay .mediavote-card[data-id="item-1"]').textContent);
    check(/8TB 1/.test(card1) && /96% full/.test(card1) && /Last played by AlexG, 3 months ago/.test(card1) && /12 plays, 3 viewers/.test(card1), 'vote card: drive with % full, last played by whom and when, plays and viewers');
    check(await page.evaluate(() => !!document.querySelector('#mediavote-overlay .mediavote-card[data-id="item-1"] .mediavote-level-critical')), 'drive bar is marked critical above the threshold');
    check(await page.evaluate(() => /Never watched/.test(document.querySelector('#mediavote-overlay .mediavote-card[data-id="item-2"]').textContent)), '"Never watched" badge');
    check(await page.evaluate(() => !!document.querySelector('#mediavote-overlay .mediavote-filters summary')), 'filters panel available to voters');
    check(await page.evaluate(() => Array.prototype.every.call(document.querySelectorAll('#mediavote-overlay .mediavote-toolbar button, #mediavote-overlay .mediavote-filters summary'), b => b.getBoundingClientRect().height >= 43)), 'toolbar controls are at least 44px tall');
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);
    check(errors.length === 0, 'no console errors (desktop): ' + errors.join(' | '));
    await context.close();

    // ---------------- phone, no active vote
    state.active = false;
    ({ page, errors, context } = await newPage({ width: 360, height: 740 }));
    await page.waitForTimeout(800);
    check(await page.evaluate(() => !document.getElementById('mediavote-banner')), 'no banner when no vote is active');
    await page.evaluate(() => { document.getElementById('tabHost').innerHTML = '<div data-mediavote-mount="page"></div>'; });
    await page.waitForSelector('#tabHost .mediavote-empty', { timeout: 3000 });
    check(await page.evaluate(() => /No active vote right now/.test(document.querySelector('#tabHost .mediavote-empty').textContent) && /Winter clean/.test(document.querySelector('#tabHost .mediavote-empty').textContent)), '"No active vote" message with last result');
    check(await page.evaluate(() => /Gone Film — Removed \(37.3 GB\)/.test(document.querySelector('#tabHost .mediavote-result').textContent) && /37.3 GB freed/.test(document.querySelector('#tabHost .mediavote-result').textContent)), 'result shows what was removed, with sizes');
    check(await page.evaluate(() => { const a = document.querySelector('#tabHost .mediavote-request-link'); return a && a.getAttribute('href') === 'https://requests.example/movie/603' && a.textContent === 'Request it again'; }), 'removed titles get a "Request it again" Jellyseerr link');
    check(errors.length === 0, 'no console errors (phone): ' + errors.join(' | '));
    await context.close();

    // ---------------- result banner: the close button must keep it closed (bug reported on 1.0.1.0)
    for (const dismissFails of [false, true]) {
        state.active = false; state.resultBanner = true; state.dismissed = false; state.dismissFails = dismissFails; state.dismissCalls = 0;
        ({ page, errors, context } = await newPage({ width: 1280, height: 800 }));
        await page.waitForSelector('#mediavote-banner .mediavote-icon-btn', { timeout: 5000 });
        await page.click('#mediavote-banner .mediavote-icon-btn');
        // Home re-render (HSS / Media Bar) right after closing: this used to put the banner straight back.
        await page.evaluate(() => { const tab = document.querySelector('#homeTab'); const d = document.createElement('div'); d.textContent = 'rerender'; tab.appendChild(d); });
        await page.waitForTimeout(600);
        check(await page.evaluate(() => !document.getElementById('mediavote-banner')), 'result banner stays closed after a home re-render' + (dismissFails ? ' (server error)' : ''));
        await page.evaluate(() => window.MediaVote && window.MediaVote.refresh && window.MediaVote.refresh());
        await page.waitForTimeout(600);
        check(state.dismissCalls === 1, 'closing the result banner tells the server once' + (dismissFails ? ' (server error)' : ''));
        check(await page.evaluate(() => !document.getElementById('mediavote-banner')), 'result banner stays closed after a re-render and a fresh status' + (dismissFails ? ' even when the server call fails' : ''));
        check(errors.filter(e => !/500/.test(e)).length === 0, 'no console errors (result banner): ' + errors.join(' | '));
        await context.close();
    }
    state.resultBanner = false; state.dismissFails = false;

    // ---------------- phone, active vote: overlay is full screen, no horizontal overflow
    state.active = true; state.remaining = 3;
    ({ page, errors, context } = await newPage({ width: 360, height: 740 }));
    await page.waitForSelector('#mediavote-banner');
    await page.click('.mediavote-banner-main');
    await page.waitForSelector('#mediavote-overlay .mediavote-card');
    const box = await page.evaluate(() => { const r = document.querySelector('.mediavote-mode-overlay').getBoundingClientRect(); const a = document.querySelector('.mediavote-mode-overlay'); return { w: r.width, h: r.height, sw: a.scrollWidth, cw: a.clientWidth }; });
    check(box.w === 360 && box.h === 740, 'overlay is full screen on a 360px phone (' + box.w + 'x' + box.h + ')');
    check(box.sw <= box.cw, 'no horizontal scrolling on a 360px phone');
    const tapOk = await page.evaluate(() => Array.prototype.every.call(document.querySelectorAll('.mediavote-app button'), b => b.offsetParent === null || (b.getBoundingClientRect().height >= 44)));
    check(tapOk, 'all visible buttons are at least 44px tall');
    check(errors.length === 0, 'no console errors (phone overlay): ' + errors.join(' | '));
    await context.close();

    // ---------------- ineligible user: no banner
    state.eligible = false;
    ({ page, errors, context } = await newPage({ width: 1024, height: 768 }));
    await page.waitForTimeout(800);
    check(await page.evaluate(() => !document.getElementById('mediavote-banner')), 'ineligible users see no banner');
    await context.close();


    // ---------------- 1.0.1.0 safety: preview mode for a non-admin renders nothing and stops
    Object.assign(state, { active: true, eligible: true, remaining: 3, hidden: true, disabled: false, statusCalls: 0, itemsCalls: 0 });
    ({ page, errors, context } = await newPage({ width: 1280, height: 800 }));
    await page.waitForFunction(() => window.MediaVote && window.MediaVote.stopped(), null, { timeout: 5000 });
    check(await page.evaluate(() => !document.getElementById('mediavote-banner')), 'preview mode (non-admin): no banner');
    check(await page.evaluate(() => !document.querySelector('[data-mediavote-mounted]') && /Media Vote is loading/.test(document.getElementById('customTabMount').textContent)), 'preview mode (non-admin): mount element left untouched');
    check(state.itemsCalls === 0, 'preview mode (non-admin): no item list requested');
    const callsAfterStop = state.statusCalls;
    await page.evaluate(() => { location.hash = '#/movies'; location.hash = '#/home'; document.getElementById('tabHost').innerHTML = '<div data-mediavote-mount="tab">x</div>'; });
    await page.waitForTimeout(600);
    check(state.statusCalls === callsAfterStop && await page.evaluate(() => !document.querySelector('[data-mediavote-mounted]')), 'preview mode (non-admin): no further work after stopping (no requests, no mounting)');
    check(errors.length === 0, 'no console errors (preview): ' + errors.join(' | '));
    await context.close();

    // ---------------- kill switch: server says disabled
    Object.assign(state, { hidden: false, disabled: true, statusCalls: 0, itemsCalls: 0 });
    ({ page, errors, context } = await newPage({ width: 1280, height: 800 }));
    await page.waitForFunction(() => window.MediaVote && window.MediaVote.stopped(), null, { timeout: 5000 });
    check(await page.evaluate(() => !document.getElementById('mediavote-banner') && !document.querySelector('[data-mediavote-mounted]') && !document.getElementById('mediavote-css')), 'disabled: nothing rendered, not even the stylesheet');
    await context.close();

    // ---------------- client.js missing (404): the page is unaffected
    Object.assign(state, { disabled: false, clientStatus: 404 });
    ({ page, errors, context } = await newPage({ width: 1280, height: 800 }));
    await page.waitForTimeout(800);
    check(await page.evaluate(() => !!document.getElementById('homeTab') && !window.MediaVote) && errors.filter(e => !/Failed to load resource/.test(e)).length === 0, 'client.js 404: harmless, no script errors');
    await context.close();
    state.clientStatus = 0;

    // ---------------- Status keeps failing (HTTP 500): stops after 3 attempts
    Object.assign(state, { statusError: true, statusCalls: 0 });
    ({ page, errors, context } = await newPage({ width: 1280, height: 800 }));
    for (let i = 0; i < 6; i++) { await page.evaluate(() => { location.hash = '#/movies'; location.hash = '#/home'; }); await page.waitForTimeout(150); }
    await page.waitForFunction(() => window.MediaVote && window.MediaVote.stopped(), null, { timeout: 5000 });
    const callsAtStop = state.statusCalls;
    await page.evaluate(() => { location.hash = '#/movies'; location.hash = '#/home'; });
    await page.waitForTimeout(400);
    check(callsAtStop <= 4 && state.statusCalls === callsAtStop, 'repeated server errors: script stops itself (' + callsAtStop + ' status calls) and stays quiet');
    await context.close();
    state.statusError = false;

    // ---------------- late mount: a Custom Tabs-style element that appears later gets the voting UI
    Object.assign(state, { active: true, eligible: true, remaining: 3, hidden: false, disabled: false, adminPreview: true });
    ({ page, errors, context } = await newPage({ width: 1280, height: 800 }));
    await page.waitForSelector('#customTabMount .mediavote-card', { timeout: 5000 });
    check(await page.evaluate(() => !/Media Vote is loading/.test(document.getElementById('customTabMount').textContent)), 'mount element in the page at load: fallback text replaced by the voting UI');
    await page.waitForTimeout(1500);
    await page.evaluate(() => { const d = document.createElement('div'); d.className = 'tabContent pageTabContent'; d.innerHTML = '<div data-mediavote-mount="tab">Media Vote is loading. If this stays empty, reload the page.</div>'; document.body.appendChild(d); });
    await page.waitForFunction(() => document.querySelectorAll('[data-mediavote-mounted] .mediavote-card').length > 30, null, { timeout: 5000 });
    check(true, 'mount element that appears late (after re-render) is filled with the voting UI');
    // Custom Tabs re-renders the tab: the filled element is replaced by a fresh copy of the snippet.
    await page.evaluate(() => { document.getElementById('customTabMount').innerHTML = '<div data-mediavote-mount="tab">Media Vote is loading. If this stays empty, reload the page.</div>'; });
    await page.waitForSelector('#customTabMount .mediavote-card', { timeout: 5000 });
    check(await page.evaluate(() => !/Media Vote is loading/.test(document.getElementById('customTabMount').textContent)), 'a re-rendered tab (fresh snippet) is filled again');
    check(errors.length === 0, 'no console errors (late mount): ' + errors.join(' | '));
    await context.close();
    state.adminPreview = false;

    // ---------------- standalone page with its own login
    state.eligible = true; state.active = true;
    {
        const ctx = await browser.newContext({ viewport: { width: 390, height: 844 } });
        const pg = await ctx.newPage();
        const errs = [];
        pg.on('pageerror', e => errs.push(String(e)));
        let authHeader = null;
        await pg.route(ORIGIN + '/**', async route => {
            const url = new URL(route.request().url());
            const p = url.pathname;
            if (p === '/jf/MediaVote/vote') { return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(assets, 'standalone.html'), 'utf8') }); }
            if (p === '/jf/MediaVote/client.js') { return route.fulfill({ contentType: 'application/javascript; charset=utf-8', body: fs.readFileSync(path.join(assets, 'client.js'), 'utf8') }); }
            if (p === '/jf/MediaVote/client.css') { return route.fulfill({ contentType: 'text/css', body: fs.readFileSync(path.join(assets, 'client.css'), 'utf8') }); }
            if (p === '/jf/Users/AuthenticateByName') { return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ AccessToken: 'standalone-token', User: { Id: 'u9', Name: 'friend' } }) }); }
            if (p === '/jf/MediaVote/Status') { authHeader = route.request().headers().authorization; return route.fulfill({ contentType: 'application/json', body: JSON.stringify(statusFor(state)) }); }
            if (p === '/jf/MediaVote/Items') { return route.fulfill({ contentType: 'application/json', body: JSON.stringify({ total: 3, page: 0, pageSize: 30, genres: [], items: items.slice(0, 3) }) }); }
            return route.fulfill({ status: 404, body: '' });
        });
        await pg.goto(ORIGIN + '/jf/MediaVote/vote');
        await pg.fill('#mvs-user', 'friend');
        await pg.fill('#mvs-pass', 'pw');
        await pg.click('#mvs-submit');
        await pg.waitForSelector('#mvs-app .mediavote-card', { timeout: 5000 });
        check(authHeader === 'MediaBrowser Token="standalone-token"', 'standalone page signs in and uses its own token (base URL /jf honoured)');
        check(await pg.evaluate(() => document.querySelector('.mediavote-card-title').getAttribute('href').indexOf('http://jf.test/jf/web/#/details?id=') === 0), 'standalone item links point into the Jellyfin web client');
        check(errs.length === 0, 'no console errors (standalone): ' + errs.join(' | '));
        await ctx.close();
    }

    await browser.close();
    console.log(failures ? failures + ' check(s) failed' : 'All client smoke checks passed');
    process.exit(failures ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
