/*
 * Media Vote client (framework-free, no build step).
 *
 * One shared voting component (VoteApp) is used by every entry point:
 *   - the home-page banner (opens VoteApp in an in-page overlay),
 *   - the Custom Tabs "Vote" tab and the Plugin Pages sidebar page ([data-mediavote-mount] elements),
 *   - the standalone /MediaVote/vote page (own login form).
 *
 * Compatibility rules followed here:
 *   - Everything is namespaced "mediavote-" (ids, classes, events); the only global is window.MediaVote.
 *   - Nothing of jellyfin-web or other plugins is patched; we only add our own elements and listeners.
 *   - No window.open, alert/confirm/prompt, clipboard or fullscreen APIs (in-app web views).
 *   - No state of importance in localStorage; all vote state lives on the server.
 *   - Key events inside our UI are stopped at window-capture so global shortcuts (Jellyfin Enhanced)
 *     never fire while the user types; we register no global shortcuts of our own.
 *   - Written in ES2017 (no optional chaining) for older iOS/Android web views.
 */
(function () {
    'use strict';

    if (window.MediaVote && window.MediaVote.__loaded) {
        if (window.MediaVote.scan) { window.MediaVote.scan(); }
        return;
    }

    var VERSION = '1.0.0';
    var PAGE_SIZE = 30;
    var STATUS_TTL = 20000;
    var UI_ATTR = 'data-mediavote-ui';

    // ------------------------------------------------------------------ failure guard
    // Media Vote must never break Jellyfin: every handler runs inside safe(); after MAX_FAILURES errors
    // (or MAX_STATUS_FAILURES failed status calls) everything stops: observers, timers and listeners are removed.
    var MAX_FAILURES = 5;
    var MAX_STATUS_FAILURES = 3;
    var guard = { failures: 0, statusFailures: 0, dead: false, observer: null, timers: [], listeners: [] };

    function shutdown(reason) {
        if (guard.dead) { return; }
        guard.dead = true;
        try { if (guard.observer) { guard.observer.disconnect(); } } catch (e) { /* ignore */ }
        guard.timers.forEach(function (id) { try { clearTimeout(id); } catch (e) { /* ignore */ } });
        guard.timers = [];
        guard.listeners.forEach(function (l) { try { l[0].removeEventListener(l[1], l[2], l[3]); } catch (e) { /* ignore */ } });
        guard.listeners = [];
        try { console.info('Media Vote: stopped (' + reason + '). Jellyfin itself is not affected.'); } catch (e) { /* ignore */ }
    }

    function fail(err) {
        guard.failures++;
        try { console.warn('Media Vote: error ' + guard.failures + '/' + MAX_FAILURES, err); } catch (e) { /* ignore */ }
        if (guard.failures >= MAX_FAILURES) { shutdown('too many errors'); }
    }

    function safe(fn) {
        return function () {
            if (guard.dead) { return undefined; }
            try { return fn.apply(this, arguments); } catch (e) { fail(e); return undefined; }
        };
    }

    function listen(target, type, fn, options) {
        var handler = safe(fn);
        target.addEventListener(type, handler, options);
        guard.listeners.push([target, type, handler, options]);
    }

    function later(fn, ms) {
        var id = setTimeout(safe(fn), ms);
        guard.timers.push(id);
        if (guard.timers.length > 64) { guard.timers.splice(0, 32); }
        return id;
    }

    // ------------------------------------------------------------------ small helpers

    function el(tag, attrs, children) {
        var e = document.createElement(tag);
        if (attrs) {
            Object.keys(attrs).forEach(function (k) {
                var v = attrs[k];
                if (v === null || v === undefined || v === false) { return; }
                if (k === 'text') { e.textContent = v; }
                else if (k === 'className') { e.className = v; }
                else if (k.indexOf('on') === 0 && typeof v === 'function') { e.addEventListener(k.substring(2), v); }
                else { e.setAttribute(k, v === true ? '' : String(v)); }
            });
        }
        (children || []).forEach(function (c) {
            if (c === null || c === undefined || c === false) { return; }
            e.appendChild(typeof c === 'string' ? document.createTextNode(c) : c);
        });
        return e;
    }

    function formatBytes(b) {
        if (!b || b <= 0) { return '—'; }
        var units = ['B', 'KB', 'MB', 'GB', 'TB'];
        var i = Math.min(units.length - 1, Math.floor(Math.log(b) / Math.log(1024)));
        return (b / Math.pow(1024, i)).toFixed(i >= 3 ? 1 : 0) + ' ' + units[i];
    }

    function formatDate(iso) {
        if (!iso) { return null; }
        var d = new Date(iso);
        if (isNaN(d.getTime()) || d.getFullYear() < 1900) { return null; }
        return d.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
    }

    function relativeTime(ms) {
        var abs = Math.abs(ms);
        var m = Math.round(abs / 60000);
        if (m < 60) { return m + ' min'; }
        var h = Math.round(m / 60);
        if (h < 48) { return h + ' h'; }
        return Math.round(h / 24) + ' days';
    }

    function debounce(fn, ms) {
        var t = null;
        return function () {
            var args = arguments;
            var self = this;
            clearTimeout(t);
            t = setTimeout(function () { fn.apply(self, args); }, ms);
        };
    }

    function isHidden(node) {
        return !node || !node.isConnected || !!(node.closest && node.closest('.hide'));
    }

    var ERRORS = {
        session_not_open: 'There is no active vote right now.',
        session_not_found: 'This vote no longer exists.',
        deadline_passed: 'Voting has closed.',
        not_eligible: 'You are not taking part in this vote.',
        item_not_votable: 'That title is not part of this vote.',
        no_votes_remaining: 'You have no votes left. Remove or move one of your votes first.',
        duplicate_not_allowed: 'You have already voted for this title.',
        keep_votes_disabled: 'Keep votes are not enabled for this vote.',
        no_such_vote: 'You have not voted for this title.',
        network: 'Could not reach the server. Check your connection and try again.'
    };

    // ------------------------------------------------------------------ connection / API

    /** Connection inside jellyfin-web: uses the logged-in ApiClient (token read on every call). */
    function webConnection() {
        return {
            kind: 'web',
            ready: function () {
                var c = window.ApiClient;
                return !!(c && typeof c.accessToken === 'function' && c.accessToken() && c.getCurrentUserId && c.getCurrentUserId());
            },
            url: function (path) { return window.ApiClient.getUrl(path); },
            token: function () { return window.ApiClient.accessToken(); },
            userKey: function () { return window.ApiClient.serverAddress() + '|' + window.ApiClient.getCurrentUserId(); },
            serverId: function () { return window.ApiClient.serverId ? window.ApiClient.serverId() : ''; },
            itemHref: function (id) {
                var sid = this.serverId();
                return '#/details?id=' + encodeURIComponent(id) + (sid ? '&serverId=' + encodeURIComponent(sid) : '');
            },
            inApp: true
        };
    }

    function request(conn, method, path, body) {
        var headers = { 'Accept': 'application/json', 'Authorization': 'MediaBrowser Token="' + conn.token() + '"' };
        if (body !== undefined) { headers['Content-Type'] = 'application/json'; }
        return fetch(conn.url(path), {
            method: method,
            headers: headers,
            body: body === undefined ? undefined : JSON.stringify(body),
            credentials: 'same-origin',
            cache: 'no-store'
        }).then(function (resp) {
            return resp.text().then(function (text) {
                var data = null;
                try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
                if (!resp.ok) {
                    var err = new Error((data && (ERRORS[data.error] || data.message)) || ('HTTP ' + resp.status));
                    err.code = data && data.error;
                    err.status = resp.status;
                    throw err;
                }
                return data;
            });
        }, function () {
            var err = new Error(ERRORS.network);
            err.code = 'network';
            throw err;
        });
    }

    function Api(conn) {
        return {
            conn: conn,
            status: function () { return request(conn, 'GET', 'MediaVote/Status'); },
            items: function (q) {
                var p = [];
                Object.keys(q).forEach(function (k) {
                    if (q[k] !== '' && q[k] !== null && q[k] !== undefined && q[k] !== false) { p.push(encodeURIComponent(k) + '=' + encodeURIComponent(q[k])); }
                });
                return request(conn, 'GET', 'MediaVote/Items?' + p.join('&'));
            },
            vote: function (itemId, kind) { return request(conn, 'POST', 'MediaVote/Vote', { itemId: itemId, kind: kind }); },
            unvote: function (itemId, kind) { return request(conn, 'POST', 'MediaVote/Unvote', { itemId: itemId, kind: kind }); },
            move: function (fromId, toId, kind) { return request(conn, 'POST', 'MediaVote/Move', { fromItemId: fromId, itemId: toId, kind: kind }); },
            dismiss: function (sessionId) { return request(conn, 'POST', 'MediaVote/Results/' + sessionId + '/Dismiss'); },
            imageUrl: function (id, tag) {
                return conn.url('Items/' + id + '/Images/Primary') + '?fillHeight=360&fillWidth=240&quality=80' + (tag ? '&tag=' + encodeURIComponent(tag) : '');
            }
        };
    }

    // Shared status cache so the banner, tab and page do not each poll the server.
    var statusCache = { key: null, at: 0, data: null, pending: null };

    function getStatus(api, force) {
        var key = api.conn.userKey();
        var now = Date.now();
        if (!force && statusCache.key === key && statusCache.data && now - statusCache.at < STATUS_TTL) {
            return Promise.resolve(statusCache.data);
        }
        if (!force && statusCache.pending && statusCache.key === key) { return statusCache.pending; }
        statusCache.key = key;
        statusCache.pending = api.status().then(function (data) {
            statusCache.data = data;
            statusCache.at = Date.now();
            statusCache.pending = null;
            data.__clockOffset = new Date(data.serverTimeUtc).getTime() - Date.now();
            document.dispatchEvent(new CustomEvent('mediavote-status', { detail: data }));
            return data;
        }, function (e) {
            statusCache.pending = null;
            guard.statusFailures++; // counted once per failed request, however many callers share it
            throw e;
        });
        return statusCache.pending;
    }

    // ------------------------------------------------------------------ theming

    function loadCss(conn) {
        if (document.getElementById('mediavote-css')) { return; }
        var link = el('link', { id: 'mediavote-css', rel: 'stylesheet', href: conn.url('MediaVote/client.css') + '?v=' + VERSION });
        document.head.appendChild(link);
    }

    function opaque(color) {
        return color && color !== 'transparent' && !/rgba\(.*,\s*0\)$/.test(color);
    }

    /** Reads the active theme's colours (works with any Jellyfin theme or custom CSS) into CSS variables on our root. */
    function applyTheme(root) {
        var bg = null;
        var candidates = [document.querySelector('.skinBody'), document.body, document.documentElement];
        for (var i = 0; i < candidates.length && !bg; i++) {
            if (candidates[i]) {
                var c = getComputedStyle(candidates[i]).backgroundColor;
                if (opaque(c)) { bg = c; }
            }
        }
        var fg = getComputedStyle(document.body).color;
        var probe = el('button', { className: 'raised button-submit emby-button', 'aria-hidden': 'true', tabindex: '-1', style: 'position:absolute;left:-9999px;top:0;visibility:hidden' }, ['x']);
        document.body.appendChild(probe);
        var accent = getComputedStyle(probe).backgroundColor;
        var accentFg = getComputedStyle(probe).color;
        probe.remove();
        if (bg) { root.style.setProperty('--mediavote-bg', bg); }
        if (fg) { root.style.setProperty('--mediavote-fg', fg); }
        if (opaque(accent)) {
            root.style.setProperty('--mediavote-accent', accent);
            root.style.setProperty('--mediavote-accent-fg', accentFg);
        }
    }

    // ------------------------------------------------------------------ key guard

    // Stops key events that originate inside our UI before any document-level listener
    // (e.g. Jellyfin Enhanced's capture-phase shortcuts) sees them. Default actions
    // (typing, Tab focus moves, Enter/Space activation) still happen.
    var keyHandlers = [];

    function keyGuard(e) {
        if (guard.dead) { return; }
        try { keyGuardCore(e); } catch (err) { fail(err); }
    }

    function keyGuardCore(e) {
        var t = e.target;
        if (!t || !t.closest || !t.closest('[' + UI_ATTR + ']')) { return; }
        e.stopPropagation();
        if (e.type === 'keydown') {
            for (var i = keyHandlers.length - 1; i >= 0; i--) {
                if (keyHandlers[i](e) === true) { break; }
            }
        }
    }

    ['keydown', 'keyup', 'keypress'].forEach(function (type) {
        window.addEventListener(type, keyGuard, true);
    });

    // ------------------------------------------------------------------ in-page dialog (replaces confirm/prompt)

    function focusables(root) {
        return Array.prototype.filter.call(
            root.querySelectorAll('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])'),
            function (n) { return !n.disabled && n.offsetParent !== null; });
    }

    function trapTab(root, e) {
        if (e.key !== 'Tab') { return false; }
        var f = focusables(root);
        if (!f.length) { return false; }
        var first = f[0];
        var last = f[f.length - 1];
        if (e.shiftKey && document.activeElement === first) { last.focus(); e.preventDefault(); return true; }
        if (!e.shiftKey && document.activeElement === last) { first.focus(); e.preventDefault(); return true; }
        return false;
    }

    /** Small modal sheet inside our UI. options: {title, body (node|string), actions: [{label, primary, value}]} → Promise<value|null>. */
    function sheet(host, options) {
        return new Promise(function (resolve) {
            var previous = document.activeElement;
            var titleId = 'mediavote-sheet-title-' + Math.random().toString(36).slice(2);
            var panel = el('div', { className: 'mediavote-sheet', role: 'alertdialog', 'aria-modal': 'true', 'aria-labelledby': titleId });
            var backdrop = el('div', { className: 'mediavote-sheet-backdrop' }, [panel]);
            backdrop.setAttribute(UI_ATTR, '');
            panel.appendChild(el('h3', { id: titleId, className: 'mediavote-sheet-title', text: options.title }));
            if (options.body) { panel.appendChild(typeof options.body === 'string' ? el('p', { text: options.body }) : options.body); }
            var actions = el('div', { className: 'mediavote-sheet-actions' });
            function done(v) {
                keyHandlers.splice(keyHandlers.indexOf(onKey), 1);
                backdrop.remove();
                if (previous && previous.focus) { previous.focus(); }
                resolve(v);
            }
            (options.actions || [{ label: 'OK', primary: true, value: true }]).forEach(function (a) {
                actions.appendChild(el('button', {
                    type: 'button',
                    className: 'mediavote-btn ' + (a.primary ? 'mediavote-btn-primary raised button-submit emby-button' : 'raised emby-button'),
                    text: a.label,
                    onclick: function () { done(a.value); }
                }));
            });
            panel.appendChild(actions);
            backdrop.addEventListener('click', function (e) { if (e.target === backdrop) { done(null); } });
            function onKey(e) {
                if (!backdrop.isConnected) { return false; }
                if (e.key === 'Escape') { e.preventDefault(); done(null); return true; }
                return trapTab(panel, e);
            }
            keyHandlers.push(onKey);
            (host || document.body).appendChild(backdrop);
            var f = focusables(panel);
            if (f.length) { f[f.length - 1].focus(); }
        });
    }

    // ------------------------------------------------------------------ VoteApp: the shared voting component

    /**
     * Mounts the voting UI into `container`.
     * opts: { mode: 'overlay'|'page', onClose: fn, navigate: fn(href) }
     */
    function VoteApp(container, api, opts) {
        opts = opts || {};
        var state = {
            status: null,
            q: { search: '', type: '', genre: '', sort: 'name', desc: false, mine: false },
            items: [],
            total: 0,
            page: 0,
            loading: false,
            genres: [],
            busy: {}
        };
        var root = el('div', { className: 'mediavote-app mediavote-mode-' + (opts.mode || 'page') });
        root.setAttribute(UI_ATTR, '');
        applyTheme(root);
        container.appendChild(root);

        var live = el('div', { className: 'mediavote-sr-only', 'aria-live': 'polite', role: 'status' });
        var header = el('div', { className: 'mediavote-header' });
        var body = el('div', { className: 'mediavote-body' });
        var footer = el('div', { className: 'mediavote-footer' });
        root.appendChild(header);
        root.appendChild(body);
        root.appendChild(footer);
        root.appendChild(live);

        var grid = null;
        var moreBtn = null;
        var sentinel = null;
        var observer = null;
        var countdownTimer = null;

        function announce(text) {
            live.textContent = '';
            setTimeout(function () { live.textContent = text; }, 30);
        }

        function toast(text, isError) {
            var t = el('div', { className: 'mediavote-toast' + (isError ? ' mediavote-toast-error' : ''), role: isError ? 'alert' : 'status', text: text });
            root.appendChild(t);
            setTimeout(function () { t.remove(); }, 4000);
        }

        function active() { return state.status && state.status.active; }

        function nowServer() { return Date.now() + ((state.status && state.status.__clockOffset) || 0); }

        // ---------------- header

        function renderHeader() {
            header.innerHTML = '';
            var a = active();
            var titleId = 'mediavote-title-' + Math.random().toString(36).slice(2);
            root.setAttribute('aria-labelledby', titleId);
            var title = el('h2', { id: titleId, className: 'mediavote-title', text: a ? a.title : 'Media Vote' });
            var top = el('div', { className: 'mediavote-header-top' }, [title]);
            if (opts.mode === 'overlay') {
                top.appendChild(el('button', {
                    type: 'button', className: 'mediavote-icon-btn', 'aria-label': 'Close voting', title: 'Close',
                    onclick: function () { if (opts.onClose) { opts.onClose(); } }
                }, [el('span', { className: 'material-icons', 'aria-hidden': 'true', text: 'close' })]));
            }
            header.appendChild(top);
            if (!a) { return; }
            if (a.description) { header.appendChild(el('p', { className: 'mediavote-desc', text: a.description })); }
            var meta = el('p', { className: 'mediavote-meta' });
            header.appendChild(meta);
            function tick() {
                var ms = new Date(a.deadlineUtc).getTime() - nowServer();
                meta.textContent = (ms > 0 ? 'Closes ' + a.deadlineLocal + ' (' + a.timeZone + ') · in ' + relativeTime(ms) : 'Voting has closed') +
                    ' · Top ' + a.winnerCount + ' ' + (a.outcome === 'DeleteAutomatically' ? 'will be removed' : 'will be reviewed by the admin');
            }
            tick();
            clearInterval(countdownTimer);
            countdownTimer = setInterval(function () { if (!root.isConnected) { clearInterval(countdownTimer); return; } tick(); }, 30000);
            if (a.eligible) {
                var chips = el('div', { className: 'mediavote-chips' });
                chips.appendChild(el('span', { className: 'mediavote-chip mediavote-chip-strong', text: a.votesRemaining + ' of ' + a.votesPerUser + ' votes left' }));
                if (a.allowKeepVotes) { chips.appendChild(el('span', { className: 'mediavote-chip', text: a.keepVotesRemaining + ' keep vote' + (a.keepVotesRemaining === 1 ? '' : 's') + ' left' })); }
                if (!a.hasVoted) { chips.appendChild(el('span', { className: 'mediavote-chip mediavote-chip-alert', text: 'You haven’t voted yet' })); }
                if (a.closingSoon) { chips.appendChild(el('span', { className: 'mediavote-chip mediavote-chip-alert', text: 'Closing soon' })); }
                header.appendChild(chips);
            }
            renderToolbar();
        }

        function renderToolbar() {
            var a = active();
            if (!a || (!a.eligible && !(state.status && state.status.isAdmin))) { return; }
            var bar = el('div', { className: 'mediavote-toolbar', role: 'search' });
            var search = el('input', {
                type: 'search', className: 'mediavote-input mediavote-search', placeholder: 'Search titles', 'aria-label': 'Search titles',
                value: state.q.search, autocomplete: 'off', enterkeyhint: 'search'
            });
            search.addEventListener('input', debounce(function () { state.q.search = search.value; reload(); }, 300));
            search.addEventListener('focus', function () {
                // Keep the search box visible above the on-screen keyboard.
                setTimeout(function () { if (search.scrollIntoView) { search.scrollIntoView({ block: 'nearest' }); } }, 300);
            });
            function select(label, key, options) {
                var s = el('select', { className: 'mediavote-input', 'aria-label': label });
                options.forEach(function (o) { s.appendChild(el('option', { value: o[0], text: o[1], selected: state.q[key] === o[0] })); });
                s.addEventListener('change', function () { state.q[key] = s.value; reload(); });
                return s;
            }
            var sorts = [['name', 'Title'], ['year', 'Year'], ['size', 'Size'], ['added', 'Date added'], ['lastplayed', 'Last played'], ['playcount', 'Play count']];
            if (a.showLiveTallies || (state.status && state.status.isAdmin)) { sorts.splice(1, 0, ['votes', 'Votes']); }
            bar.appendChild(search);
            var row = el('div', { className: 'mediavote-toolbar-row' });
            row.appendChild(select('Type', 'type', [['', 'Movies & series'], ['Movie', 'Movies'], ['Series', 'Series']]));
            var genreSel = select('Genre', 'genre', [['', 'All genres']].concat(state.genres.map(function (g) { return [g, g]; })));
            genreSel.className += ' mediavote-genre';
            row.appendChild(genreSel);
            row.appendChild(select('Sort by', 'sort', sorts));
            var order = el('button', {
                type: 'button', className: 'mediavote-btn raised emby-button', 'aria-pressed': state.q.desc ? 'true' : 'false',
                'aria-label': state.q.desc ? 'Sort descending' : 'Sort ascending', text: state.q.desc ? '↓' : '↑',
                onclick: function () { state.q.desc = !state.q.desc; order.textContent = state.q.desc ? '↓' : '↑'; order.setAttribute('aria-pressed', state.q.desc ? 'true' : 'false'); reload(); }
            });
            row.appendChild(order);
            var mine = el('button', {
                type: 'button', className: 'mediavote-btn raised emby-button', 'aria-pressed': state.q.mine ? 'true' : 'false', text: 'My votes',
                onclick: function () { state.q.mine = !state.q.mine; mine.setAttribute('aria-pressed', state.q.mine ? 'true' : 'false'); reload(); }
            });
            row.appendChild(mine);
            bar.appendChild(row);
            header.appendChild(bar);
        }

        function refreshGenres() {
            var g = header.querySelector('.mediavote-genre');
            if (!g || g.options.length > 1 || !state.genres.length) { return; }
            state.genres.forEach(function (x) { g.appendChild(el('option', { value: x, text: x })); });
        }

        // ---------------- empty / result states

        function renderNoVote() {
            body.innerHTML = '';
            footer.innerHTML = '';
            var s = state.status;
            var a = active();
            var box = el('div', { className: 'mediavote-empty' });
            if (a && !a.eligible) {
                box.appendChild(el('p', { className: 'mediavote-empty-title', text: 'You are not taking part in the current vote.' }));
                box.appendChild(el('p', { text: '“' + a.title + '” closes ' + a.deadlineLocal + '.' }));
            } else {
                box.appendChild(el('p', { className: 'mediavote-empty-title', text: 'No active vote right now.' }));
            }
            if (s && s.lastResult) { box.appendChild(renderResult(s.lastResult)); }
            body.appendChild(box);
        }

        function renderResult(r) {
            var wrap = el('div', { className: 'mediavote-result' });
            wrap.appendChild(el('h3', { text: 'Last result: ' + r.title }));
            var when = formatDate(r.resolvedUtc);
            if (when) { wrap.appendChild(el('p', { className: 'mediavote-meta', text: 'Closed ' + when + ' · turnout ' + r.turnoutPercent + '%' })); }
            if (!r.turnoutMet) { wrap.appendChild(el('p', { text: 'Not enough people voted, so nothing was removed.' })); }
            var list = el('ul', { className: 'mediavote-result-list' });
            var labels = { removed: 'Removed', flagged: 'Selected for admin review', tied: 'Tied: the admin will decide', kept: 'Kept', pending: 'Pending' };
            r.items.forEach(function (i) { list.appendChild(el('li', null, [el('strong', { text: i.name }), ' — ' + (labels[i.status] || i.status)])); });
            if (r.items.length) { wrap.appendChild(list); }
            else if (r.turnoutMet) { wrap.appendChild(el('p', { text: 'Nothing was selected.' })); }
            return wrap;
        }

        // ---------------- item grid

        function renderShell() {
            body.innerHTML = '';
            grid = el('ul', { className: 'mediavote-grid', 'aria-label': 'Votable titles' });
            grid.addEventListener('click', onGridClick);
            body.appendChild(grid);
            moreBtn = el('button', { type: 'button', className: 'mediavote-btn raised emby-button mediavote-more', text: 'Load more', onclick: loadMore });
            sentinel = el('div', { className: 'mediavote-sentinel', 'aria-hidden': 'true' });
            body.appendChild(sentinel);
            body.appendChild(moreBtn);
            if ('IntersectionObserver' in window) {
                if (observer) { observer.disconnect(); }
                observer = new IntersectionObserver(function (entries) {
                    if (entries[0] && entries[0].isIntersecting && !state.loading && state.items.length < state.total) { loadMore(); }
                }, { root: opts.mode === 'overlay' ? root : null, rootMargin: '600px' });
                observer.observe(sentinel);
            }
            renderFooter();
        }

        function renderFooter() {
            footer.innerHTML = '';
            var a = active();
            if (!a || !a.eligible) { return; }
            footer.appendChild(el('span', { className: 'mediavote-footer-text', text: a.votesRemaining + ' of ' + a.votesPerUser + ' votes left' }));
            if (opts.mode === 'overlay') {
                footer.appendChild(el('button', { type: 'button', className: 'mediavote-btn mediavote-btn-primary raised button-submit emby-button', text: 'Done', onclick: function () { if (opts.onClose) { opts.onClose(); } } }));
            }
        }

        function card(item) {
            var a = active();
            var li = el('li', { className: 'mediavote-card', 'data-id': item.id });
            var poster = el('div', { className: 'mediavote-poster' });
            var placeholder = el('span', { className: 'mediavote-poster-fallback', 'aria-hidden': 'true', text: (item.name || '?').charAt(0) });
            poster.appendChild(placeholder);
            if (item.imageTag) {
                var img = el('img', { alt: '', loading: 'lazy', decoding: 'async', src: api.imageUrl(item.id, item.imageTag) });
                img.addEventListener('error', function () { img.remove(); });
                poster.appendChild(img);
            }
            li.appendChild(poster);

            var info = el('div', { className: 'mediavote-info' });
            var titleText = item.name + (item.year ? ' (' + item.year + ')' : '');
            var href = api.conn.itemHref(item.id);
            info.appendChild(el('a', { className: 'mediavote-card-title', href: href, 'data-action': 'details', text: titleText }));
            var facts = [item.kind === 'Series' ? 'Series' : 'Movie', formatBytes(item.sizeBytes)];
            if (item.library) { facts.push(item.library); }
            info.appendChild(el('p', { className: 'mediavote-facts', text: facts.join(' · ') }));
            var stats = [];
            var added = formatDate(item.dateAddedUtc);
            if (added) { stats.push('Added ' + added); }
            var played = formatDate(item.lastPlayedUtc);
            stats.push(played ? 'Last played ' + played : 'Never played');
            if (item.playCount) { stats.push(item.playCount + ' play' + (item.playCount === 1 ? '' : 's')); }
            info.appendChild(el('p', { className: 'mediavote-facts', text: stats.join(' · ') }));

            if (item.removeVotes !== null && item.removeVotes !== undefined) {
                var tally = item.removeVotes + ' vote' + (item.removeVotes === 1 ? '' : 's') + ' to remove';
                if (item.keepVotes) { tally += ' · ' + item.keepVotes + ' to keep'; }
                info.appendChild(el('p', { className: 'mediavote-tally', text: tally }));
                if (item.removeVoters && item.removeVoters.length) { info.appendChild(el('p', { className: 'mediavote-voters', text: 'Remove: ' + item.removeVoters.join(', ') })); }
                if (item.keepVoters && item.keepVoters.length) { info.appendChild(el('p', { className: 'mediavote-voters', text: 'Keep: ' + item.keepVoters.join(', ') })); }
            }

            if (a && a.eligible) {
                var actions = el('div', { className: 'mediavote-actions' });
                var mineCount = item.myRemoveVotes || 0;
                if (mineCount > 0) {
                    actions.appendChild(el('button', {
                        type: 'button', className: 'mediavote-btn mediavote-btn-on raised button-submit emby-button', 'data-action': 'unvote', 'aria-pressed': 'true',
                        'aria-label': 'Remove my vote for ' + titleText, text: a.allowMultipleVotesPerItem ? 'Remove one vote (' + mineCount + ')' : 'Voted ✓ · undo'
                    }));
                }
                if (!item.myKeepVotes && (mineCount === 0 || a.allowMultipleVotesPerItem)) {
                    actions.appendChild(el('button', {
                        type: 'button', className: 'mediavote-btn raised emby-button', 'data-action': 'vote', 'aria-pressed': 'false',
                        'aria-label': 'Vote to remove ' + titleText, text: mineCount > 0 ? 'Add a vote' : 'Vote to remove'
                    }));
                }
                if (a.allowKeepVotes && mineCount === 0) {
                    actions.appendChild(el('button', {
                        type: 'button', className: 'mediavote-btn raised emby-button' + (item.myKeepVotes ? ' mediavote-btn-on' : ''), 'data-action': item.myKeepVotes ? 'unkeep' : 'keep',
                        'aria-pressed': item.myKeepVotes ? 'true' : 'false', 'aria-label': (item.myKeepVotes ? 'Withdraw keep vote for ' : 'Vote to keep ') + titleText,
                        text: item.myKeepVotes ? 'Keeping ✓ · undo' : 'Keep'
                    }));
                }
                info.appendChild(actions);
            }
            li.appendChild(info);
            return li;
        }

        function appendItems(items) {
            var frag = document.createDocumentFragment();
            items.forEach(function (i) { frag.appendChild(card(i)); });
            grid.appendChild(frag);
            moreBtn.hidden = state.items.length >= state.total;
            if (!state.total) {
                grid.appendChild(el('li', { className: 'mediavote-none', text: state.q.mine ? 'You have not voted for anything yet.' : 'No titles match.' }));
            }
        }

        function replaceCard(item) {
            var old = grid && grid.querySelector('[data-id="' + item.id + '"]');
            if (old) { old.replaceWith(card(item)); }
        }

        function fetchPage(page) {
            state.loading = true;
            var q = state.q;
            return api.items({ search: q.search, type: q.type, genre: q.genre, sort: q.sort, desc: q.desc, mine: q.mine, page: page, pageSize: PAGE_SIZE })
                .then(function (res) {
                    state.loading = false;
                    state.total = res.total;
                    state.page = page;
                    if (res.genres && !state.genres.length) { state.genres = res.genres; refreshGenres(); }
                    return res.items;
                }, function (e) { state.loading = false; throw e; });
        }

        function reload() {
            if (!grid) { return; }
            state.items = [];
            grid.innerHTML = '';
            grid.setAttribute('aria-busy', 'true');
            fetchPage(0).then(function (items) {
                state.items = items;
                grid.setAttribute('aria-busy', 'false');
                appendItems(items);
            }, function (e) {
                grid.setAttribute('aria-busy', 'false');
                toast(e.message, true);
            });
        }

        function loadMore() {
            if (state.loading || state.items.length >= state.total) { return; }
            fetchPage(state.page + 1).then(function (items) {
                state.items = state.items.concat(items);
                appendItems(items);
            }, function (e) { toast(e.message, true); });
        }

        // ---------------- actions

        function findItem(id) {
            for (var i = 0; i < state.items.length; i++) { if (state.items[i].id === id) { return state.items[i]; } }
            return null;
        }

        function afterChange(item, delta, res) {
            var a = active();
            if (res) {
                a.votesRemaining = res.votesRemaining;
                a.keepVotesRemaining = res.keepVotesRemaining;
                a.votesUsed = a.votesPerUser - res.votesRemaining;
                a.hasVoted = a.votesUsed > 0 || (a.allowKeepVotes && a.keepVotesRemaining < a.keepVotesPerUser);
            }
            if (item && delta) {
                if (delta.remove) {
                    item.myRemoveVotes = Math.max(0, (item.myRemoveVotes || 0) + delta.remove);
                    if (item.removeVotes !== null && item.removeVotes !== undefined) { item.removeVotes = Math.max(0, item.removeVotes + delta.remove); }
                }
                if (delta.keep) {
                    item.myKeepVotes = Math.max(0, (item.myKeepVotes || 0) + delta.keep);
                    if (item.keepVotes !== null && item.keepVotes !== undefined) { item.keepVotes = Math.max(0, item.keepVotes + delta.keep); }
                }
                replaceCard(item);
            }
            statusCache.at = 0; // banner and other views pick up the change
            renderHeaderKeepToolbar();
            renderFooter();
        }

        // Re-render only the chips/meta without losing the search box focus.
        function renderHeaderKeepToolbar() {
            var toolbar = header.querySelector('.mediavote-toolbar');
            var focused = document.activeElement;
            renderHeader();
            if (toolbar) {
                var fresh = header.querySelector('.mediavote-toolbar');
                if (fresh) { fresh.replaceWith(toolbar); }
                if (focused && toolbar.contains(focused)) { focused.focus(); }
            }
        }

        function offerMove(item) {
            var mineItems = state.items.filter(function (i) { return i.myRemoveVotes > 0 && i.id !== item.id; });
            var bodyNode = el('div');
            bodyNode.appendChild(el('p', { text: 'You have used all your votes. Move one of them to “' + item.name + '”?' }));
            var actionsList = mineItems.slice(0, 8).map(function (m) { return { label: 'Move from ' + m.name, value: m.id }; });
            actionsList.push({ label: 'Cancel', value: null, primary: true });
            if (!mineItems.length) {
                bodyNode.appendChild(el('p', { text: 'Tap “My votes” to see and undo your current votes.' }));
            }
            return sheet(root, { title: 'No votes left', body: bodyNode, actions: actionsList }).then(function (fromId) {
                if (!fromId) { return; }
                return api.move(fromId, item.id, 'Remove').then(function (res) {
                    var from = findItem(fromId);
                    if (from) { afterChange(from, { remove: -1 }, null); }
                    afterChange(item, { remove: 1 }, res);
                    announce('Vote moved to ' + item.name + '. ' + res.votesRemaining + ' votes left.');
                });
            });
        }

        function onGridClick(e) {
            var target = e.target.closest('[data-action]');
            if (!target || !grid.contains(target)) { return; }
            var li = target.closest('.mediavote-card');
            var item = li && findItem(li.getAttribute('data-id'));
            var action = target.getAttribute('data-action');
            if (action === 'details') {
                if (opts.navigate) { e.preventDefault(); opts.navigate(target.getAttribute('href')); }
                return;
            }
            if (!item || state.busy[item.id]) { return; }
            var a = active();
            if (action === 'vote' && a.votesRemaining <= 0) {
                offerMove(item).catch(function (err) { toast(err.message, true); });
                return;
            }
            state.busy[item.id] = true;
            target.disabled = true;
            var p;
            if (action === 'vote') {
                p = api.vote(item.id, 'Remove').then(function (res) { afterChange(item, { remove: 1 }, res); announce('Vote added for ' + item.name + '. ' + res.votesRemaining + ' votes left.'); });
            } else if (action === 'unvote') {
                p = api.unvote(item.id, 'Remove').then(function (res) { afterChange(item, { remove: -1 }, res); announce('Vote removed. ' + res.votesRemaining + ' votes left.'); });
            } else if (action === 'keep') {
                p = api.vote(item.id, 'Keep').then(function (res) { afterChange(item, { keep: 1 }, res); announce('Keep vote added for ' + item.name + '.'); });
            } else if (action === 'unkeep') {
                p = api.unvote(item.id, 'Keep').then(function (res) { afterChange(item, { keep: -1 }, res); announce('Keep vote withdrawn.'); });
            } else {
                p = Promise.resolve();
            }
            p.catch(function (err) {
                toast(err.message, true);
                if (err.code === 'deadline_passed' || err.code === 'session_not_open') { refresh(true); }
            }).then(function () {
                delete state.busy[item.id];
                if (target.isConnected) { target.disabled = false; }
            });
        }

        // ---------------- lifecycle

        function refresh(force) {
            return getStatus(api, force).then(function (status) {
                state.status = status;
                renderHeader();
                var a = active();
                if (a && (a.eligible || status.isAdmin)) {
                    renderShell();
                    reload();
                } else {
                    renderNoVote();
                }
            }, function (e) {
                if (e.status === 401 && api.conn.onUnauthorized) { api.conn.onUnauthorized(); return; }
                body.innerHTML = '';
                body.appendChild(el('div', { className: 'mediavote-empty' }, [el('p', { className: 'mediavote-empty-title', text: 'Media Vote could not load.' }), el('p', { text: e.message })]));
            });
        }

        root.addEventListener('mediavote-refresh', function () { refresh(true); });
        refresh(false);

        return {
            root: root,
            refresh: refresh,
            destroy: function () {
                clearInterval(countdownTimer);
                if (observer) { observer.disconnect(); }
                root.remove();
            }
        };
    }

    // ------------------------------------------------------------------ overlay (opened from the banner)

    var overlay = null;

    function lockScroll() {
        var de = document.documentElement;
        var b = document.body;
        var saved = { y: window.scrollY, deOverflow: de.style.overflow, bOverflow: b.style.overflow, bPosition: b.style.position, bTop: b.style.top, bWidth: b.style.width };
        de.style.overflow = 'hidden';
        b.style.overflow = 'hidden';
        // iOS Safari/WKWebView ignores overflow:hidden on body; fixing the body is the reliable lock.
        b.style.position = 'fixed';
        b.style.top = (-saved.y) + 'px';
        b.style.width = '100%';
        return function unlock() {
            de.style.overflow = saved.deOverflow;
            b.style.overflow = saved.bOverflow;
            b.style.position = saved.bPosition;
            b.style.top = saved.bTop;
            b.style.width = saved.bWidth;
            window.scrollTo(0, saved.y);
        };
    }

    function trackViewport(node) {
        var vv = window.visualViewport;
        if (!vv) { return function () {}; }
        function update() { node.style.setProperty('--mediavote-vh', vv.height + 'px'); }
        update();
        vv.addEventListener('resize', update);
        return function () { vv.removeEventListener('resize', update); };
    }

    function openOverlay(conn) {
        if (overlay) { return; }
        var api = Api(conn);
        var opener = document.activeElement;
        var wrap = el('div', { id: 'mediavote-overlay', className: 'mediavote-overlay', role: 'dialog', 'aria-modal': 'true' });
        wrap.setAttribute(UI_ATTR, '');
        document.body.appendChild(wrap);
        var unlock = lockScroll();
        var untrack = trackViewport(wrap);
        var app;
        function close(after) {
            if (!overlay) { return; }
            keyHandlers.splice(keyHandlers.indexOf(onKey), 1);
            window.removeEventListener('hashchange', onHash);
            untrack();
            app.destroy();
            wrap.remove();
            unlock();
            overlay = null;
            refreshBanner(true);
            if (after) { after(); }
            else if (opener && opener.isConnected && opener.focus) { opener.focus(); }
        }
        function onKey(e) {
            if (!wrap.isConnected) { return false; }
            if (e.key === 'Escape') {
                if (wrap.querySelector('.mediavote-sheet-backdrop')) { return false; }
                e.preventDefault();
                close();
                return true;
            }
            return trapTab(wrap, e);
        }
        function onHash() { close(function () {}); }
        keyHandlers.push(onKey);
        window.addEventListener('hashchange', onHash);
        app = VoteApp(wrap, api, {
            mode: 'overlay',
            onClose: function () { close(); },
            navigate: function (href) { close(function () { window.location.hash = href.replace(/^#/, ''); }); }
        });
        wrap.addEventListener('click', function (e) { if (e.target === wrap) { close(); } });
        overlay = { close: close };
        setTimeout(function () {
            var btn = wrap.querySelector('.mediavote-icon-btn');
            if (btn) { btn.focus(); }
        }, 50);
    }

    // ------------------------------------------------------------------ banner

    // Host adapters locate the home page's content container. Add a Jellyfin 12 adapter here
    // when its MUI layout is supported; no other code depends on the DOM structure.
    var hostAdapters = [
        {
            name: 'jellyfin-10.11',
            find: function () {
                var pages = document.querySelectorAll('#indexPage, .homePage');
                for (var i = 0; i < pages.length; i++) {
                    if (isHidden(pages[i])) { continue; }
                    var tab = pages[i].querySelector('#homeTab') || pages[i].querySelector('.pageTabContent[data-index="0"]');
                    if (tab) { return tab; }
                }
                return null;
            }
        },
        {
            // Generic fallback: the parent of a visible home sections container (also used by Home Screen Sections).
            name: 'generic',
            find: function () {
                var list = document.querySelectorAll('.homeSectionsContainer, .sections');
                for (var i = 0; i < list.length; i++) {
                    if (!isHidden(list[i]) && list[i].parentElement) { return list[i].parentElement; }
                }
                return null;
            }
        }
    ];

    function isHomeRoute() {
        var h = (window.location.hash || '').toLowerCase();
        return h === '' || h === '#' || h === '#/' || /^#!?\/home(\.html)?([?/]|$)/.test(h);
    }

    function findHomeContainer() {
        if (!isHomeRoute()) { return null; }
        for (var i = 0; i < hostAdapters.length; i++) {
            var c = hostAdapters[i].find();
            if (c) { return c; }
        }
        return null;
    }

    var webConn = webConnection();
    var bannerState = { status: null, repositions: 0, lastHash: null };

    // Result banners this viewer closed. Kept in memory (and in browser storage when available) so the banner
    // cannot be put back by the next DOM check before the server has recorded the dismissal.
    var dismissedResults = {};
    try {
        (JSON.parse(window.localStorage.getItem('mediavote-dismissed') || '[]') || []).forEach(function (id) { dismissedResults[id] = true; });
    } catch (e) { /* storage unavailable: memory only */ }

    function isDismissed(result) { return !!(result && dismissedResults[result.id]); }

    function rememberDismissed(id) {
        dismissedResults[id] = true;
        try { window.localStorage.setItem('mediavote-dismissed', JSON.stringify(Object.keys(dismissedResults).slice(-20))); } catch (e) { /* memory only */ }
        [bannerState.status, statusCache.data].forEach(function (st) {
            if (st && st.lastResult && st.lastResult.id === id) { st.lastResult.showBanner = false; }
        });
    }

    function wantsResultBanner(status) {
        return !!(status && status.lastResult && status.lastResult.showBanner && !isDismissed(status.lastResult));
    }

    function removeBanner() {
        var b = document.getElementById('mediavote-banner');
        if (b) { b.remove(); }
    }

    function buildBanner(status) {
        var a = status.active;
        var r = status.lastResult;
        var banner = el('div', { id: 'mediavote-banner', className: 'mediavote-banner' });
        banner.setAttribute(UI_ATTR, '');
        applyTheme(banner);
        if (a && a.eligible) {
            var line = a.votesRemaining > 0 ? 'You have ' + a.votesRemaining + ' vote' + (a.votesRemaining === 1 ? '' : 's') + ' left' : 'All your votes are cast';
            var main = el('button', { type: 'button', className: 'mediavote-banner-main', 'aria-haspopup': 'dialog', onclick: function () { openOverlay(webConn); } }, [
                el('span', { className: 'material-icons mediavote-banner-icon', 'aria-hidden': 'true', text: 'how_to_vote' }),
                el('span', { className: 'mediavote-banner-text' }, [
                    el('strong', { text: 'Vote: ' + a.title }),
                    el('span', { text: line + ' · closes ' + a.deadlineLocal })
                ]),
                !a.hasVoted ? el('span', { className: 'mediavote-chip mediavote-chip-alert', text: 'Not voted yet' }) : null,
                a.closingSoon ? el('span', { className: 'mediavote-chip mediavote-chip-alert', text: 'Closing soon' }) : null,
                el('span', { className: 'mediavote-banner-cta raised button-submit emby-button', text: 'Vote now' })
            ]);
            banner.appendChild(main);
            return banner;
        }
        if (wantsResultBanner(status)) {
            var removed = r.items.filter(function (i) { return i.status === 'removed'; }).map(function (i) { return i.name; });
            var flagged = r.items.filter(function (i) { return i.status === 'flagged' || i.status === 'tied'; }).map(function (i) { return i.name; });
            var summary = !r.turnoutMet ? 'Not enough people voted; nothing was removed.'
                : removed.length ? 'Removed: ' + removed.join(', ')
                    : flagged.length ? 'Selected for admin review: ' + flagged.join(', ') : 'Nothing was selected.';
            banner.appendChild(el('div', { className: 'mediavote-banner-main mediavote-banner-static' }, [
                el('span', { className: 'material-icons mediavote-banner-icon', 'aria-hidden': 'true', text: 'campaign' }),
                el('span', { className: 'mediavote-banner-text' }, [el('strong', { text: 'Vote results: ' + r.title }), el('span', { text: summary })])
            ]));
            banner.appendChild(el('button', {
                type: 'button', className: 'mediavote-icon-btn', 'aria-label': 'Dismiss vote results',
                onclick: safe(function () {
                    rememberDismissed(r.id);
                    banner.remove();
                    Api(webConn).dismiss(r.id).then(function () { statusCache.at = 0; }, function () {});
                })
            }, [el('span', { className: 'material-icons', 'aria-hidden': 'true', text: 'close' })]));
            return banner;
        }
        return null;
    }

    /** True when the server says Media Vote must show nothing to this user (kill switch, or preview mode for a non-admin). */
    function statusSaysHidden(status) {
        return !status || status.disabled === true || status.hidden === true;
    }

    /** Ensures the banner is present exactly once, as the first child of the home content, or absent. */
    function placeBanner() {
        var status = bannerState.status;
        var existing = document.getElementById('mediavote-banner');
        if (statusSaysHidden(status) || !status.entryPoints || !status.entryPoints.banner) {
            if (existing) { existing.remove(); }
            return;
        }
        var container = findHomeContainer();
        if (!container) {
            if (existing) { existing.remove(); }
            return;
        }
        var wanted = (status.active && status.active.eligible) || wantsResultBanner(status);
        if (!wanted) {
            if (existing) { existing.remove(); }
            return;
        }
        if (existing && existing.parentElement === container && container.firstElementChild === existing) { return; }
        if (existing && existing.parentElement === container) {
            // Something was inserted above us. Move back to the top, but never fight endlessly with another script.
            if (bannerState.repositions > 5) { return; }
            bannerState.repositions++;
        }
        var banner = existing || buildBanner(status);
        if (banner) { container.insertBefore(banner, container.firstChild); }
    }

    function refreshBanner(force) {
        if (guard.dead || !webConn.ready()) { return; }
        var api = Api(webConn);
        getStatus(api, force).then(safe(function (status) {
            guard.statusFailures = 0;
            if (statusSaysHidden(status)) {
                // Disabled on the server, or preview mode and this user is not an admin: render nothing, stop all work.
                removeBanner();
                shutdown(status && status.disabled ? 'disabled on the server' : 'preview mode: visible to administrators only');
                return;
            }
            var changed = JSON.stringify(status.active) + JSON.stringify(status.lastResult) !== JSON.stringify(bannerState.status && bannerState.status.active) + JSON.stringify(bannerState.status && bannerState.status.lastResult);
            bannerState.status = status;
            if (status.entryPoints && (status.entryPoints.banner || status.entryPoints.tab)) { loadCss(webConn); }
            if (!isHomeRoute()) { removeBanner(); } else {
                if (changed) { removeBanner(); }
                placeBanner();
            }
            scan();
        }), function () {
            if (guard.statusFailures >= MAX_STATUS_FAILURES) { shutdown('the server did not answer'); }
        });
    }

    var scheduled = false;

    function schedule() {
        if (scheduled || guard.dead) { return; }
        scheduled = true;
        later(function () {
            scheduled = false;
            if (!webConn.ready() || !bannerState.status) { return; }
            placeBanner();
            scan();
        }, 150);
    }

    function onNavigate() {
        var h = window.location.hash;
        if (h !== bannerState.lastHash) {
            bannerState.lastHash = h;
            bannerState.repositions = 0;
        }
        if (!isHomeRoute()) { removeBanner(); }
        refreshBanner(false);
        schedule();
    }

    // ------------------------------------------------------------------ mount points (Custom Tabs tab, Plugin Pages page)

    function scan() {
        if (guard.dead) { return; }
        var standalone = window.MediaVote && window.MediaVote.__standalone;
        var conn = standalone || webConn;
        if (!conn.ready()) { return; }
        // In jellyfin-web, mount only once the server has confirmed this user may see Media Vote.
        if (!standalone && statusSaysHidden(bannerState.status)) { return; }
        var nodes = document.querySelectorAll('[data-mediavote-mount]:not([data-mediavote-mounted])');
        if (!nodes.length) { return; }
        loadCss(conn);
        Array.prototype.forEach.call(nodes, function (node) {
            try {
                node.setAttribute('data-mediavote-mounted', '1');
                node.textContent = ''; // remove the snippet's "Media Vote is loading" fallback text
                VoteApp(node, Api(conn), {
                    mode: 'page',
                    navigate: conn.inApp ? function (href) { window.location.hash = href.replace(/^#/, ''); } : null
                });
            } catch (e) { fail(e); }
        });
    }

    // ------------------------------------------------------------------ boot

    function boot() {
        listen(document, 'viewshow', onNavigate);
        listen(window, 'hashchange', onNavigate);
        listen(window, 'popstate', onNavigate);
        listen(document, 'visibilitychange', function () { if (!document.hidden) { refreshBanner(false); } });
        // Re-check placement and late mount points after any re-render (Home Screen Sections, Media Bar, Custom Tabs).
        guard.observer = new MutationObserver(safe(function (records) {
            for (var i = 0; i < records.length; i++) {
                var t = records[i].target;
                if (t && t.closest && t.closest('[' + UI_ATTR + ']')) { continue; }
                schedule();
                return;
            }
        }));
        guard.observer.observe(document.body, { childList: true, subtree: true });
        var waited = 0;
        (function waitForLogin() {
            if (guard.dead) { return; }
            if (webConn.ready()) { onNavigate(); return; }
            waited += 1;
            later(waitForLogin, waited < 120 ? 500 : 5000);
        })();
    }

    window.MediaVote = {
        __loaded: true,
        version: VERSION,
        scan: safe(scan),
        refresh: safe(function () { statusCache.at = 0; refreshBanner(true); }),
        open: safe(function () { if (webConn.ready() && !statusSaysHidden(bannerState.status)) { openOverlay(webConn); } }),
        /** Used by the standalone page: mounts the app with a custom connection. */
        mountStandalone: function (container, conn) {
            window.MediaVote.__standalone = conn;
            loadCss(conn);
            return VoteApp(container, Api(conn), { mode: 'page' });
        },
        /** For diagnostics: whether the script stopped itself. */
        stopped: function () { return guard.dead; }
    };

    if (!window.__mediavoteStandalone) {
        try {
            if (document.body) { boot(); }
            else { document.addEventListener('DOMContentLoaded', safe(boot)); }
        } catch (e) {
            fail(e);
            shutdown('start-up failed');
        }
    }
})();
