/*
 * Jellyboxed web client: loaded into Jellyfin's web UI by the Jellyboxed server plugin.
 *
 *  - film page panel: your rating, watchlist button, friends' activity
 *  - rating badges on library posters
 *  - "log it on Letterboxd?" prompt after you finish a movie
 *  - connect / manage your Letterboxd account
 *
 * Plain JavaScript, no dependencies, no innerHTML: everything coming from Letterboxd is inserted as text.
 * Every feature is switched on/off by the server (see /Jellyboxed/me), so this file contains no settings of its own.
 */
(function () {
    'use strict';

    if (window.__jellyboxed) {
        return;
    }

    var VERSION = (function () {
        try {
            var m = /[?&]v=([\d.]+)/.exec((document.currentScript && document.currentScript.src) || '');
            return m ? m[1] : 'dev';
        } catch (e) { return 'dev'; }
    }());
    var J = (window.__jellyboxed = { version: VERSION });
    var NS_SVG = 'http://www.w3.org/2000/svg';
    var HEART_PATH = 'M12 21s-6.7-4.35-9.3-8.2C1 10.1 1.6 6.6 4.6 5.2 6.9 4.1 9.4 5 12 8c2.6-3 5.1-3.9 7.4-2.8 3 1.4 3.6 4.9 1.9 7.6C18.7 16.65 12 21 12 21z';
    var STAR_PATH = 'M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01L12 2z';
    var EYE_PATH = 'M12 4.5C7 4.5 2.73 7.61 1 12c1.73 4.39 6 7.5 11 7.5s9.27-3.11 11-7.5c-1.73-4.39-6-7.5-11-7.5zM12 17c-2.76 0-5-2.24-5-5s2.24-5 5-5 5 2.24 5 5-2.24 5-5 5zm0-8c-1.66 0-3 1.34-3 3s1.34 3 3 3 3-1.34 3-3-1.34-3-3-3z';
    var BOOKMARK_PATH = 'M6 3h12a1 1 0 0 1 1 1v17l-7-4-7 4V4a1 1 0 0 1 1-1z';

    // ------------------------------------------------------------------ state

    var me = null; // GET /Jellyboxd/me
    var meUserId = null;
    var loadingMe = false;
    var badges = {}; // itemId (32 hex) -> {r, l, w}
    var badgesEtag = null;
    var badgeStats = null;
    var badgesVersion = 0;
    var badgesLoadedAt = 0;
    var mountedFor = null; // item id the panel is currently shown for
    var promptShownFor = null;

    // ------------------------------------------------------------------ tiny helpers

    function log() {
        if (me && me.ui && me.ui.debug && window.console) {
            var args = Array.prototype.slice.call(arguments);
            args.unshift('[Jellyboxed]');
            console.log.apply(console, args);
        }
    }

    function h(tag, attrs) {
        var el = document.createElement(tag);
        if (attrs) {
            Object.keys(attrs).forEach(function (k) {
                var v = attrs[k];
                if (v === null || v === undefined || v === false) {
                    return;
                }
                if (k === 'class') {
                    el.className = v;
                } else if (k === 'text') {
                    el.textContent = v;
                } else if (k.indexOf('on') === 0 && typeof v === 'function') {
                    el.addEventListener(k.slice(2), v);
                } else {
                    el.setAttribute(k, v === true ? '' : String(v));
                }
            });
        }
        for (var i = 2; i < arguments.length; i++) {
            appendChild(el, arguments[i]);
        }
        return el;
    }

    function appendChild(el, child) {
        if (child === null || child === undefined || child === false) {
            return;
        }
        if (Array.isArray(child)) {
            child.forEach(function (c) { appendChild(el, c); });
        } else if (typeof child === 'string' || typeof child === 'number') {
            el.appendChild(document.createTextNode(String(child)));
        } else {
            el.appendChild(child);
        }
    }

    function svg(path, size, fill, cls) {
        var s = document.createElementNS(NS_SVG, 'svg');
        s.setAttribute('viewBox', '0 0 24 24');
        s.setAttribute('width', String(size));
        s.setAttribute('height', String(size));
        s.setAttribute('aria-hidden', 'true');
        if (cls) {
            s.setAttribute('class', cls);
        }
        var p = document.createElementNS(NS_SVG, 'path');
        p.setAttribute('d', path);
        p.setAttribute('fill', fill);
        s.appendChild(p);
        return s;
    }

    function stars(rating) {
        if (!rating || rating <= 0) {
            return '';
        }
        var whole = Math.floor(rating);
        var out = '';
        for (var i = 0; i < whole; i++) {
            out += '★';
        }
        return out + (rating - whole >= 0.5 ? '½' : '');
    }

    function normId(id) {
        return String(id || '').replace(/-/g, '').toLowerCase();
    }

    function debounce(fn, ms) {
        var t = null;
        return function () {
            if (t) {
                clearTimeout(t);
            }
            t = setTimeout(function () { t = null; fn(); }, ms);
        };
    }

    function throttleFrame(fn, ms) {
        var scheduled = false;
        return function () {
            if (scheduled) {
                return;
            }
            scheduled = true;
            setTimeout(function () {
                scheduled = false;
                try { fn(); } catch (e) { log('error', e); }
            }, ms);
        };
    }

    // ------------------------------------------------------------------ talking to the server

    function apiClient() {
        return window.ApiClient;
    }

    function signedIn() {
        var c = apiClient();
        try {
            return !!(c && c.accessToken && c.accessToken() && c.getCurrentUserId && c.getCurrentUserId());
        } catch (e) {
            return false;
        }
    }

    function authHeader() {
        var c = apiClient();
        function q(v) { return String(v === undefined || v === null ? '' : v).replace(/"/g, ''); }
        var parts = [];
        try { parts.push('Client="' + q(c.appName && c.appName()) + '"'); } catch (e) { /* optional */ }
        try { parts.push('Device="' + q(c.deviceName && c.deviceName()) + '"'); } catch (e) { /* optional */ }
        try { parts.push('DeviceId="' + q(c.deviceId && c.deviceId()) + '"'); } catch (e) { /* optional */ }
        try { parts.push('Version="' + q(c.appVersion && c.appVersion()) + '"'); } catch (e) { /* optional */ }
        parts.push('Token="' + q(c.accessToken()) + '"');
        return 'MediaBrowser ' + parts.join(', ');
    }

    function apiUrl(path) {
        var c = apiClient();
        return c && c.getUrl ? c.getUrl('Jellyboxed/' + path) : '/Jellyboxed/' + path;
    }

    function ApiError(status, code, message) {
        this.status = status;
        this.code = code;
        this.message = message;
    }

    /** Calls the plugin API. Resolves with parsed JSON (or null for 304); rejects with ApiError. */
    function api(path, opts) {
        opts = opts || {};
        var ctrl = typeof AbortController !== 'undefined' ? new AbortController() : null;
        var timer = ctrl ? setTimeout(function () { ctrl.abort(); }, opts.timeout || 20000) : null;
        var headers = { Authorization: authHeader(), Accept: 'application/json' };
        if (opts.body !== undefined) {
            headers['Content-Type'] = 'application/json';
        }
        if (opts.etag) {
            headers['If-None-Match'] = opts.etag;
        }
        return fetch(apiUrl(path), {
            method: opts.method || 'GET',
            headers: headers,
            body: opts.body !== undefined ? JSON.stringify(opts.body) : undefined,
            signal: ctrl ? ctrl.signal : undefined
        }).then(function (res) {
            if (timer) { clearTimeout(timer); }
            if (res.status === 304) {
                return { notModified: true, etag: res.headers.get('ETag') };
            }
            return res.text().then(function (text) {
                var data = null;
                try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
                if (!res.ok) {
                    throw new ApiError(res.status, data && data.error, (data && data.message) || ('Request failed (' + res.status + ')'));
                }
                if (data && opts.etag !== undefined) {
                    data.__etag = res.headers.get('ETag');
                }
                return data;
            });
        }, function (err) {
            if (timer) { clearTimeout(timer); }
            throw new ApiError(0, 'network', err && err.name === 'AbortError' ? 'The server took too long to answer.' : 'Could not reach the Jellyfin server.');
        });
    }

    function loadMe() {
        if (loadingMe || !signedIn()) {
            return Promise.resolve(me);
        }
        loadingMe = true;
        var uid = apiClient().getCurrentUserId();
        return api('me').then(function (data) {
            me = data;
            meUserId = uid;
            log('me', data);
            return me;
        }).catch(function (e) {
            log('could not load /me', e);
            return me;
        }).then(function (r) {
            loadingMe = false;
            return r;
        });
    }

    // ------------------------------------------------------------------ toasts & modals

    function toast(text, kind) {
        var host = document.getElementById('jbx-toasts');
        if (!host) {
            host = h('div', { id: 'jbx-toasts', 'aria-live': 'polite' });
            document.body.appendChild(host);
        }
        var el = h('div', { class: 'jbx-toast' + (kind ? ' jbx-' + kind : ''), text: text });
        host.appendChild(el);
        setTimeout(function () { el.classList.add('jbx-out'); }, 3800);
        setTimeout(function () { if (el.parentNode) { el.parentNode.removeChild(el); } }, 4300);
    }

    var openModal = null;

    function closeModal() {
        if (openModal) {
            var m = openModal;
            openModal = null;
            document.removeEventListener('keydown', m.onKey, true);
            if (m.el.parentNode) { m.el.parentNode.removeChild(m.el); }
            if (m.returnFocus && m.returnFocus.focus) { try { m.returnFocus.focus(); } catch (e) { /* ignore */ } }
            if (m.onClose) { m.onClose(); }
        }
    }

    function modal(title, body, opts) {
        closeModal();
        opts = opts || {};
        var returnFocus = document.activeElement;
        var titleId = 'jbx-modal-title';
        var closeBtn = h('button', { class: 'jbx-x', type: 'button', 'aria-label': 'Close', text: '×', onclick: closeModal });
        var dialog = h('div', { class: 'jbx-dialog' + (opts.wide ? ' jbx-wide' : ''), role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': titleId },
            h('div', { class: 'jbx-dialog-head' }, h('h2', { id: titleId, text: title }), closeBtn),
            body);
        var overlay = h('div', { class: 'jbx-overlay', onmousedown: function (e) { if (e.target === overlay) { closeModal(); } } }, dialog);
        function onKey(e) {
            if (e.key === 'Escape') {
                e.stopPropagation();
                closeModal();
            } else if (e.key === 'Tab') {
                var f = dialog.querySelectorAll('button, input, textarea, select, [tabindex="0"]');
                var list = Array.prototype.filter.call(f, function (n) { return !n.disabled && n.offsetParent !== null; });
                if (!list.length) { return; }
                var first = list[0];
                var last = list[list.length - 1];
                if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
                else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
            }
        }
        document.addEventListener('keydown', onKey, true);
        document.body.appendChild(overlay);
        openModal = { el: overlay, onKey: onKey, returnFocus: returnFocus, onClose: opts.onClose };
        var focusEl = dialog.querySelector('[data-autofocus]') || closeBtn;
        setTimeout(function () { try { focusEl.focus(); } catch (e) { /* ignore */ } }, 30);
        return dialog;
    }

    function errorBox() {
        return h('div', { class: 'jbx-error', role: 'alert', hidden: true });
    }

    function showError(box, msg) {
        box.textContent = msg;
        box.hidden = !msg;
    }

    // ------------------------------------------------------------------ connect / manage account

    function openConnectModal(onDone) {
        var f = me && me.features ? me.features : {};
        var tab = 'username';
        var err = errorBox();
        var form = h('div', { class: 'jbx-form' });
        var tabs = h('div', { class: 'jbx-tabs', role: 'tablist' });
        var busy = false;

        var username = h('input', { type: 'text', class: 'jbx-input', placeholder: 'Letterboxd username', autocomplete: 'username', 'data-autofocus': true, maxlength: '100' });
        var password = h('input', { type: 'password', class: 'jbx-input', placeholder: 'Password', autocomplete: 'current-password', maxlength: '200' });
        var cookies = h('textarea', { class: 'jbx-input', rows: '4', placeholder: 'Paste your letterboxd.com cookies here', spellcheck: 'false' });

        var submit = h('button', { class: 'jbx-btn jbx-primary', type: 'button', text: 'Connect' });

        var explain = {
            username: 'Quickest. Uses your public Letterboxd profile to show your ratings, likes and watchlist on posters and film pages. Read-only: you cannot log films from Jellyfin in this mode.',
            login: 'Sign in with your Letterboxd account to also log films, edit your watchlist and see what your friends thought. Your password is used once to sign in and is never stored; only the resulting session is kept on the server.',
            cookies: 'Use this if sign-in asks for a captcha. Log in to letterboxd.com in your browser, then copy your cookies (browser dev tools → Application/Storage → Cookies, or a cookie-export extension) and paste them here. Treat them like a password.'
        };

        function render() {
            tabs.textContent = '';
            [['username', 'Username only'], ['login', 'Sign in'], ['cookies', 'Cookies']].forEach(function (t) {
                if (t[0] === 'login' && f.passwordLogin === false) { return; }
                if (t[0] === 'cookies' && f.cookieLogin === false) { return; }
                tabs.appendChild(h('button', {
                    type: 'button', role: 'tab', 'aria-selected': tab === t[0] ? 'true' : 'false',
                    class: 'jbx-tab' + (tab === t[0] ? ' jbx-active' : ''), text: t[1],
                    onclick: function () { tab = t[0]; showError(err, ''); render(); }
                }));
            });
            form.textContent = '';
            form.appendChild(h('p', { class: 'jbx-muted', text: explain[tab] }));
            form.appendChild(h('label', { class: 'jbx-label', text: tab === 'login' ? 'Letterboxd username or email' : 'Letterboxd username' }));
            form.appendChild(username);
            if (tab === 'login') {
                form.appendChild(h('label', { class: 'jbx-label', text: 'Password' }));
                form.appendChild(password);
            }
            if (tab === 'cookies') {
                form.appendChild(h('label', { class: 'jbx-label', text: 'Cookies' }));
                form.appendChild(cookies);
            }
        }

        submit.addEventListener('click', function () {
            if (busy) { return; }
            var body = { username: username.value.trim() };
            if (tab === 'login') { body.password = password.value; }
            if (tab === 'cookies') { body.cookies = cookies.value; }
            if (!body.username && tab !== 'cookies') { showError(err, 'Enter your Letterboxd username.'); return; }
            if (tab === 'login' && !body.password) { showError(err, 'Enter your password.'); return; }
            if (tab === 'cookies' && !body.cookies.trim()) { showError(err, 'Paste your cookies.'); return; }
            busy = true;
            submit.disabled = true;
            submit.textContent = 'Connecting…';
            showError(err, '');
            api('me/connect', { method: 'POST', body: body, timeout: 60000 }).then(function () {
                password.value = '';
                closeModal();
                toast('Letterboxd connected. Syncing your films in the background…', 'ok');
                return loadMe().then(function () { badgesEtag = null; loadBadges(true); if (onDone) { onDone(); } });
            }).catch(function (e) {
                showError(err, e.message);
            }).then(function () {
                busy = false;
                submit.disabled = false;
                submit.textContent = 'Connect';
            });
        });

        render();
        modal('Connect Letterboxd', h('div', null, tabs, form, err,
            h('div', { class: 'jbx-actions' },
                h('button', { class: 'jbx-btn', type: 'button', text: 'Cancel', onclick: closeModal }), submit)));
    }

    function openAccountModal() {
        var a = me && me.account ? me.account : {};
        var f = me && me.features ? me.features : {};
        var err = errorBox();

        function toggle(label, key, help) {
            var input = h('input', { type: 'checkbox', class: 'jbx-check' });
            input.checked = !!(a.settings && a.settings[key]);
            input.addEventListener('change', function () {
                var body = {};
                body[key] = input.checked;
                api('me/settings', { method: 'PUT', body: body }).then(function (acc) {
                    me.account = Object.assign({}, me.account, acc);
                    showError(err, '');
                }).catch(function (e) { input.checked = !input.checked; showError(err, e.message); });
            });
            return h('label', { class: 'jbx-toggle' }, input, h('span', null, h('strong', { text: label }), help ? h('small', { text: help }) : null));
        }

        var syncLine = h('p', { class: 'jbx-muted' });
        function renderSync(s) {
            var parts = [];
            if (s && s.running) {
                parts.push('Syncing: ' + (s.phase || 'working') + (s.total ? ' (' + s.done + '/' + s.total + ')' : '') + '…');
            } else if (a.lastFullSync) {
                parts.push('Last full sync: ' + new Date(a.lastFullSync).toLocaleString());
            } else {
                parts.push('Not synced yet.');
            }
            if (a.counts && (a.counts.rated || a.counts.liked || a.counts.watchlist)) {
                parts.push(a.counts.rated + ' rated, ' + a.counts.liked + ' liked, ' + a.counts.watchlist + ' on watchlist.');
            }
            if (s && s.lastError) { parts.push('Last problem: ' + s.lastError); }
            syncLine.textContent = parts.join(' ');
        }
        renderSync(a.sync);

        var level = a.level === 'session' ? 'Signed in (full access)' : (a.level === 'public' ? 'Username only (read-only)' : 'Not connected');
        var syncBtn = h('button', { class: 'jbx-btn', type: 'button', text: 'Sync now', onclick: function () {
            api('me/sync', { method: 'POST' }).then(function (s) { renderSync(s); toast('Sync started.'); }).catch(function (e) { showError(err, e.message); });
        } });

        var body = h('div', null,
            h('p', null, h('strong', { text: a.username || '' }), ' – ', level),
            a.lastError ? h('p', { class: 'jbx-error', text: a.lastError }) : null,
            syncLine,
            h('div', { class: 'jbx-toggles' },
                a.level === 'session' && f.watchlistToFavorites ? toggle('Mirror my watchlist to Jellyfin favourites', 'watchlistToFavorites', 'Adds a favourite for watchlisted films in this library. Never removes any.') : null,
                a.level === 'session' && f.logging ? toggle('Love on Letterboxd = favourite in Jellyfin', 'lovedToFavorite', 'Applied when you log a film from here.') : null,
                a.level === 'session' && f.logging ? toggle('Mark films as watched when I log them', 'markPlayedOnLog') : null,
                a.level === 'session' && f.prompt ? toggle('Ask me to log a film after I watch it', 'promptAfterPlayback') : null),
            err,
            h('div', { class: 'jbx-actions' },
                h('button', { class: 'jbx-btn jbx-danger', type: 'button', text: 'Disconnect', onclick: function () {
                    if (!window.confirm('Disconnect Letterboxd and forget the cached data?')) { return; }
                    api('me/disconnect', { method: 'POST' }).then(function () {
                        closeModal(); toast('Letterboxd disconnected.'); badges = {}; badgesVersion++;
                        return loadMe().then(function () { remount(); });
                    }).catch(function (e) { showError(err, e.message); });
                } }),
                a.level !== 'session' && (f.passwordLogin !== false || f.cookieLogin !== false)
                    ? h('button', { class: 'jbx-btn', type: 'button', text: 'Sign in for full access', onclick: function () { closeModal(); openConnectModal(remount); } }) : null,
                syncBtn,
                h('button', { class: 'jbx-btn jbx-primary', type: 'button', text: 'Done', onclick: closeModal })));

        var poll = setInterval(function () {
            api('me/sync').then(function (s) { renderSync(s); }).catch(function () { /* ignore */ });
        }, 4000);
        modal('Letterboxd account', body, { onClose: function () { clearInterval(poll); loadMe(); } });
    }

    // ------------------------------------------------------------------ log / edit modal

    function starPicker(initial, onChange) {
        var value = initial || 0;
        var hover = null;
        var host = h('div', { class: 'jbx-stars', role: 'slider', tabindex: '0', 'aria-label': 'Rating', 'aria-valuemin': '0', 'aria-valuemax': '5', 'aria-valuenow': String(value) });
        var label = h('div', { class: 'jbx-muted jbx-star-label' });

        function ensureGradient() {
            if (document.getElementById('jbx-gradient')) { return; }
            var s = document.createElementNS(NS_SVG, 'svg');
            s.setAttribute('width', '0'); s.setAttribute('height', '0'); s.setAttribute('style', 'position:absolute'); s.setAttribute('aria-hidden', 'true'); s.id = 'jbx-gradient';
            var defs = document.createElementNS(NS_SVG, 'defs');
            var g = document.createElementNS(NS_SVG, 'linearGradient');
            g.id = 'jbx-half'; g.setAttribute('x1', '0'); g.setAttribute('x2', '1'); g.setAttribute('y1', '0'); g.setAttribute('y2', '0');
            var s1 = document.createElementNS(NS_SVG, 'stop'); s1.setAttribute('offset', '50%'); s1.setAttribute('class', 'jbx-stop-on');
            var s2 = document.createElementNS(NS_SVG, 'stop'); s2.setAttribute('offset', '50%'); s2.setAttribute('class', 'jbx-stop-off');
            g.appendChild(s1); g.appendChild(s2); defs.appendChild(g); s.appendChild(defs);
            document.body.appendChild(s);
        }

        function draw() {
            var shown = hover !== null ? hover : value;
            host.textContent = '';
            for (var i = 1; i <= 5; i++) {
                var cls = shown >= i ? 'jbx-star jbx-on' : (shown === i - 0.5 ? 'jbx-star jbx-half' : 'jbx-star');
                var s = svg(STAR_PATH, 34, 'currentColor', cls);
                s.setAttribute('data-i', String(i));
                host.appendChild(s);
            }
            label.textContent = value > 0 ? value + ' / 5' : 'No rating';
            host.setAttribute('aria-valuenow', String(value));
        }

        function valueAt(e) {
            var star = e.target.closest ? e.target.closest('svg.jbx-star') : null;
            if (!star) { return null; }
            var rect = star.getBoundingClientRect();
            var i = parseInt(star.getAttribute('data-i'), 10);
            return (e.clientX - rect.left) < rect.width / 2 ? i - 0.5 : i;
        }

        host.addEventListener('mousemove', function (e) { var v = valueAt(e); if (v !== null && v !== hover) { hover = v; draw(); } });
        host.addEventListener('mouseleave', function () { hover = null; draw(); });
        host.addEventListener('click', function (e) {
            var v = valueAt(e);
            if (v === null) { return; }
            value = v === value ? 0 : v; // click the same value again to clear
            hover = null;
            draw();
            onChange(value);
        });
        host.addEventListener('keydown', function (e) {
            var next = value;
            if (e.key === 'ArrowRight' || e.key === 'ArrowUp') { next = Math.min(5, value + 0.5); }
            else if (e.key === 'ArrowLeft' || e.key === 'ArrowDown') { next = Math.max(0, value - 0.5); }
            else if (e.key === 'Home') { next = 0; }
            else if (e.key === 'End') { next = 5; }
            else { return; }
            e.preventDefault();
            value = next;
            draw();
            onChange(value);
        });

        ensureGradient();
        draw();
        return { el: h('div', null, host, label), get: function () { return value; } };
    }

    function todayIso() {
        var d = new Date();
        var m = String(d.getMonth() + 1).padStart(2, '0');
        var day = String(d.getDate()).padStart(2, '0');
        return d.getFullYear() + '-' + m + '-' + day;
    }

    /** Opens the "log / update this film" dialog. initial: {rating, loved, review, rated} */
    function openLogModal(itemId, name, initial, onSaved) {
        initial = initial || {};
        var isEdit = !!initial.rated;
        var loved = !!initial.loved;
        var err = errorBox();
        var picker = starPicker(initial.rating || 0, function () { /* value read on save */ });

        var heartPath = svg(HEART_PATH, 28, 'currentColor', 'jbx-heart' + (loved ? ' jbx-on' : ''));
        var heartLabel = h('span', { class: 'jbx-muted', text: loved ? 'Loved it' : 'Not loved' });
        var heartBtn = h('button', { type: 'button', class: 'jbx-heart-btn', 'aria-pressed': loved ? 'true' : 'false', 'aria-label': 'Love this film' }, heartPath, heartLabel);
        heartBtn.addEventListener('click', function () {
            loved = !loved;
            heartBtn.setAttribute('aria-pressed', loved ? 'true' : 'false');
            heartPath.setAttribute('class', 'jbx-heart' + (loved ? ' jbx-on' : ''));
            heartLabel.textContent = loved ? 'Loved it' : 'Not loved';
        });

        var review = h('textarea', { class: 'jbx-input', rows: '5', placeholder: 'Write a review (optional)…', maxlength: '20000' });
        review.value = initial.review || '';
        var spoilers = h('input', { type: 'checkbox', class: 'jbx-check' });
        var rewatch = h('input', { type: 'checkbox', class: 'jbx-check' });
        var date = h('input', { type: 'date', class: 'jbx-input jbx-date', max: todayIso() });
        date.value = todayIso();
        var save = h('button', { class: 'jbx-btn jbx-primary', type: 'button', text: isEdit ? 'Update' : 'Save to Letterboxd' });

        save.addEventListener('click', function () {
            save.disabled = true;
            var label = save.textContent;
            save.textContent = 'Saving…';
            showError(err, '');
            api('item/' + normId(itemId) + '/log', {
                method: 'POST', timeout: 45000,
                body: { rating: picker.get(), review: review.value, loved: loved, rewatch: rewatch.checked, spoilers: spoilers.checked, date: date.value }
            }).then(function (res) {
                closeModal();
                toast(isEdit ? 'Letterboxd entry updated.' : 'Logged to Letterboxd.', 'ok');
                badgesEtag = null;
                loadBadges(true);
                if (onSaved) { onSaved(res && res.mine); }
            }).catch(function (e) {
                showError(err, e.message);
                save.disabled = false;
                save.textContent = label;
            });
        });

        modal(isEdit ? 'Update Letterboxd entry' : 'Log to Letterboxd', h('div', null,
            h('h3', { class: 'jbx-film', text: name || 'This film' }),
            h('div', { class: 'jbx-row' }, picker.el, heartBtn),
            h('label', { class: 'jbx-label', text: 'Review' }),
            review,
            h('div', { class: 'jbx-row jbx-wrap' },
                h('label', { class: 'jbx-inline' }, rewatch, ' I’ve watched this before'),
                h('label', { class: 'jbx-inline' }, spoilers, ' Contains spoilers')),
            h('label', { class: 'jbx-label', text: 'Watched on' }),
            date,
            err,
            h('div', { class: 'jbx-actions' }, h('button', { class: 'jbx-btn', type: 'button', text: 'Cancel', onclick: closeModal }), save)));
    }

    function openFriendModal(friend) {
        var body = h('div', null,
            h('div', { class: 'jbx-friend-head' },
                friend.avatar ? h('img', { class: 'jbx-avatar-lg', src: friend.avatar, alt: '' }) : null,
                h('div', null, h('strong', { text: friend.name }), friend.rating ? h('span', { class: 'jbx-accent jbx-gap', text: friend.rating }) : null)),
            friend.review ? h('div', { class: 'jbx-review', text: friend.review }) : h('p', { class: 'jbx-muted', text: 'No written review.' }),
            h('div', { class: 'jbx-actions' },
                friend.url ? h('a', { class: 'jbx-btn', href: friend.url, target: '_blank', rel: 'noopener noreferrer', text: 'Open profile' }) : null,
                h('button', { class: 'jbx-btn jbx-primary', type: 'button', text: 'Close', onclick: closeModal, 'data-autofocus': true })));
        modal('Review by ' + friend.name, body);
    }

    // ------------------------------------------------------------------ film page panel

    function itemIdFromLocation() {
        var href = window.location.href;
        if (href.indexOf('details') === -1) {
            return null;
        }
        var m = href.match(/[?&]id=([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f-]{27})/i);
        return m ? normId(m[1]) : null;
    }

    function findAnchor() {
        var list = [];
        if (me && me.ui && me.ui.detailSelector) {
            list.push(me.ui.detailSelector);
        }
        list.push('.detailSectionContent', '.detailPagePrimaryContainer', '.itemDetailPage .detailPageContent', '#itemDetailPage', '[data-testid="item-details"]', '[class*="details" i] [class*="content" i]');
        for (var i = 0; i < list.length; i++) {
            var found;
            try { found = document.querySelectorAll(list[i]); } catch (e) { continue; }
            for (var j = 0; j < found.length; j++) {
                var el = found[j];
                // Jellyfin keeps previously visited pages in the DOM, hidden. Only use the visible one.
                if (el.getClientRects().length > 0 && !el.closest('.hide, [hidden]')) {
                    return el;
                }
            }
        }
        return null;
    }

    function removePanel() {
        var old = document.querySelectorAll('.jbx-panel:not(.jbx-in-dialog)');
        for (var i = 0; i < old.length; i++) {
            old[i].parentNode.removeChild(old[i]);
        }
        mountedFor = null;
    }

    // ------------------------------------------------------------------ Jellyfin's own buttons -> Letterboxd

    var BUTTONS = 'button.watchlist-icon, button.watchlist-button, .watchlist-icon, .watchlist-button, [is="emby-ratingbutton"], button.btnUserRating';

    function buttonKind(btn) {
        if (btn.matches('.watchlist-icon, .watchlist-button')) { return { kind: 'watchlist', attr: 'data-active' }; }
        if (btn.hasAttribute('data-isfavorite')) { return { kind: 'favorite', attr: 'data-isfavorite' }; }
        return null;
    }

    function onJellyfinButton(ev) {
        var target = ev.target && ev.target.closest ? ev.target : null;
        if (!target || !signedIn() || !me || !me.account || me.account.level !== 'session' || !me.features) { return; }
        var btn = target.closest(BUTTONS);
        if (!btn) { return; }
        var info = buttonKind(btn);
        if (!info) { return; }
        if (info.kind === 'watchlist' && !me.features.pushWatchlist) { return; }
        if (info.kind === 'favorite' && !me.features.pushFavorite) { return; }
        var id = normId(btn.getAttribute('data-id') || btn.getAttribute('data-item-id') || btn.getAttribute('data-itemid') || itemIdFromLocation());
        if (!id) { return; }
        var before = btn.getAttribute(info.attr);
        // The other plugin or Jellyfin flips the attribute shortly after the click; wait for it.
        var tries = 0;
        var timer = setInterval(function () {
            tries++;
            var now = btn.getAttribute(info.attr);
            if (now !== before && now !== null) {
                clearInterval(timer);
                pushButton(id, info.kind, now === 'true');
            } else if (tries > 14) {
                clearInterval(timer);
            }
        }, 150);
    }

    function pushButton(id, kind, value) {
        api('item/' + id + '/jellyfin-button', { method: 'POST', body: { kind: kind, value: value }, timeout: 30000 }).then(function (res) {
            if (!res || !res.ok) { return; }
            toast(res.target === 'like' ? (value ? 'Liked on Letterboxd.' : 'Removed your like on Letterboxd.') : (value ? 'Added to your Letterboxd watchlist.' : 'Removed from your Letterboxd watchlist.'), 'ok');
            badgesEtag = null;
            loadBadges(true);
            if (itemIdFromLocation() === id) { remount(); }
        }).catch(function (e) {
            if (e && (e.code === 'notmovie' || e.code === 'noaccount')) { return; }
            toast((e && e.message) || 'Could not update Letterboxd.', 'err');
        });
    }

    function remount() {
        removePanel();
        scheduleScan();
    }

    var mountSeq = 0;

    function mountPanel(itemId) {
        if (!me) {
            return;
        }
        var f = me.features || {};
        if (!f.myRating && !f.friends && !f.watchlist && !f.logging) {
            mountedFor = itemId;
            return;
        }
        var anchor = findAnchor();
        if (!anchor) {
            return; // the page is still being built; the DOM observer will call us again
        }

        // A newer mount supersedes older ones, so a slow answer can never leave a "Loading…" panel behind.
        removePanel();
        var token = ++mountSeq;
        mountedFor = itemId;
        var panel = h('section', { class: 'jbx-panel', 'data-item': itemId, 'aria-label': 'Letterboxd' });
        anchor.appendChild(panel);
        panel.appendChild(h('div', { class: 'jbx-skeleton', text: 'Loading Letterboxd…' }));

        function stale() {
            if (token === mountSeq && panel.isConnected) { return false; }
            if (panel.parentNode) { panel.parentNode.removeChild(panel); }
            return true;
        }

        api('item/' + itemId).then(function (info) {
            if (stale()) { return; }
            if (!info || !info.isMovie || !info.hasTmdb) {
                panel.parentNode.removeChild(panel);
                return;
            }
            renderPanel(panel, itemId, info);
        }).catch(function (e) {
            if (stale()) { return; }
            renderPanelError(panel, itemId, e);
        });
    }

    function renderPanelError(panel, itemId, e) {
        panel.textContent = '';
        panel.appendChild(h('div', { class: 'jbx-head' }, h('span', { class: 'jbx-title', text: 'Letterboxd' })));
        if (e && e.code === 'notfound') {
            panel.appendChild(h('p', { class: 'jbx-muted', text: 'This film could not be matched on Letterboxd.' }));
            return;
        }
        panel.appendChild(h('p', { class: 'jbx-muted', text: (e && e.message) || 'Something went wrong.' }));
        panel.appendChild(h('button', { class: 'jbx-btn jbx-small', type: 'button', text: 'Try again', onclick: function () { remount(); } }));
    }

    function renderPanel(panel, itemId, info) {
        var f = me.features || {};
        var acct = me.account || {};
        var mine = info.mine;
        panel.textContent = '';
        var card = h('div', { class: 'jbx-card' });
        panel.appendChild(card);

        var head = h('div', { class: 'jbx-head' }, h('span', { class: 'jbx-title', text: 'Letterboxd' }));
        var tools = h('span', { class: 'jbx-tools' });
        if (mine && mine.url) {
            tools.appendChild(h('a', { class: 'jbx-tool', href: mine.url, target: '_blank', rel: 'noopener noreferrer', title: 'Open on Letterboxd', text: 'Open ↗' }));
        }
        tools.appendChild(h('button', { class: 'jbx-tool', type: 'button', text: acct.connected ? 'Account' : 'Connect', onclick: function () {
            if (acct.connected) { openAccountModal(); } else { openConnectModal(remount); }
        } }));
        head.appendChild(tools);
        card.appendChild(head);

        if (!acct.connected || !mine) {
            var empty = h('div', { class: 'jbx-empty' });
            if (f.selfConnect !== false) {
                empty.appendChild(h('p', { class: 'jbx-empty-text', text: 'Connect your Letterboxd account to see your rating, your watchlist and what your friends thought of this film.' }));
                empty.appendChild(h('button', { class: 'jbx-btn jbx-primary', type: 'button', text: 'Connect Letterboxd', onclick: function () { openConnectModal(remount); } }));
            } else {
                empty.appendChild(h('p', { class: 'jbx-empty-text', text: 'Ask your server administrator to connect your Letterboxd account.' }));
            }
            card.appendChild(empty);
            return;
        }

        var canWrite = mine.level === 'session';

        if (acct.lastError && !canWrite && acct.authMode !== 'none') {
            card.appendChild(h('p', { class: 'jbx-warn', text: 'Your Letterboxd session expired. Sign in again to log films and see friends.' }));
        }

        var body = h('div', { class: 'jbx-body' });
        card.appendChild(body);

        // --- my rating
        if (f.myRating !== false) {
            var rating = h('div', { class: 'jbx-block' }, h('div', { class: 'jbx-label-s', text: 'Your rating' }));
            var line = h('div', { class: 'jbx-rating-line' });
            var starsText = stars(mine.rating);
            line.appendChild(h('span', { class: 'jbx-stars-text' + (starsText ? ' jbx-accent' : ' jbx-none'), text: starsText || (mine.synced === false ? 'Syncing…' : 'Not rated') }));
            if (mine.loved) { line.appendChild(svg(HEART_PATH, 20, '#ff3b5c', 'jbx-heart-inline')); }
            if (mine.stale) { line.appendChild(h('span', { class: 'jbx-muted jbx-s', title: 'Refreshing from Letterboxd', text: '↻' })); }
            rating.appendChild(line);
            body.appendChild(rating);
        }

        // --- watched on Letterboxd (with date and rewatches when the diary knows them)
        if (mine.watched) {
            var seen = h('div', { class: 'jbx-watched' }, svg(EYE_PATH, 15, 'currentColor', 'jbx-watched-icon'));
            var text = 'Watched';
            var day = formatDay(mine.watchedOn, false);
            if (day) { text += ' on ' + day; }
            if (mine.watches > 1) { text += ' · ' + mine.watches + ' times'; } else if (mine.rewatch) { text += ' · rewatch'; }
            seen.appendChild(h('span', { text: text }));
            body.appendChild(seen);
        }

        // --- actions
        var actions = h('div', { class: 'jbx-actions-row' });
        if (canWrite && f.logging !== false && f.myRating !== false) {
            actions.appendChild(h('button', { class: 'jbx-btn jbx-primary', type: 'button', text: mine.rated ? 'Edit rating' : 'Rate & log', onclick: function () {
                openLogModal(itemId, info.name, mine, function () { remount(); });
            } }));
        } else if (canWrite && f.logging !== false) {
            actions.appendChild(h('button', { class: 'jbx-btn jbx-primary', type: 'button', text: 'Log film', onclick: function () {
                openLogModal(itemId, info.name, mine, function () { remount(); });
            } }));
        }

        if (f.watchlist !== false && (canWrite || mine.inWatchlist)) {
            var on = !!mine.inWatchlist;
            var btn = h('button', { type: 'button', class: 'jbx-chip' + (on ? ' jbx-on' : ''), 'aria-pressed': on ? 'true' : 'false' });
            var setLabel = function () {
                btn.textContent = '';
                btn.appendChild(svg(BOOKMARK_PATH, 14, 'currentColor'));
                btn.appendChild(document.createTextNode(' ' + (on ? 'On watchlist' : 'Add to watchlist')));
                btn.className = 'jbx-chip' + (on ? ' jbx-on' : '');
                btn.setAttribute('aria-pressed', on ? 'true' : 'false');
            };
            setLabel();
            if (canWrite) {
                btn.addEventListener('click', function () {
                    var next = !on;
                    btn.disabled = true;
                    api('item/' + itemId + '/watchlist', { method: 'POST', body: { add: next }, timeout: 30000 }).then(function () {
                        on = next;
                        setLabel();
                        badgesEtag = null; loadBadges(true);
                    }).catch(function (e) { toast(e.message, 'err'); }).then(function () { btn.disabled = false; });
                });
            } else {
                btn.disabled = true;
            }
            actions.appendChild(btn);
        }
        if (actions.childNodes.length) { body.appendChild(actions); }

        // --- friends
        if (f.friends !== false && canWrite) {
            var box = h('div', { class: 'jbx-friends' }, h('div', { class: 'jbx-muted jbx-s', text: 'Loading friends…' }));
            card.appendChild(box);
            loadFriends(box, itemId);
        } else if (f.friends !== false && !canWrite) {
            card.appendChild(h('p', { class: 'jbx-muted jbx-s jbx-note', text: 'Sign in to Letterboxd (Account → Sign in for full access) to log films and see your friends’ activity.' }));
        }
    }

    function loadFriends(box, itemId) {
        api('item/' + itemId + '/friends', { timeout: 45000 }).then(function (res) {
            if (!box.isConnected) { return; }
            box.textContent = '';
            var list = (res && res.friends) || [];
            if (!list.length) { box.parentNode.removeChild(box); return; }
            box.appendChild(h('div', { class: 'jbx-friends-head' },
                h('span', { class: 'jbx-label-s', text: 'Friends who watched' }),
                h('span', { class: 'jbx-count', text: String(list.length) })));
            var strip = h('div', { class: 'jbx-strip' });
            list.forEach(function (fr) {
                var hasReview = !!(fr.review && fr.review.trim());
                var avatarSrc = fr.avatar ? apiUrl(fr.avatar) : '';
                var item = h('button', { type: 'button', class: 'jbx-friend' + (hasReview ? ' jbx-has-review' : ''), title: fr.name + (fr.rating ? ' – ' + fr.rating : '') + (hasReview ? '' : ' (no review)'), onclick: function () {
                    openFriendModal({ name: fr.name, avatar: avatarSrc, rating: fr.rating, review: hasReview ? fr.review : '', url: fr.url });
                } },
                    h('span', { class: 'jbx-avatar-wrap' },
                        avatarSrc ? h('img', { class: 'jbx-avatar', src: avatarSrc, alt: '', loading: 'lazy' }) : h('span', { class: 'jbx-avatar jbx-avatar-ph', text: (fr.name || '?').charAt(0).toUpperCase() }),
                        hasReview ? h('span', { class: 'jbx-badge-dot', title: 'Has a review' }) : null),
                    h('span', { class: 'jbx-friend-name', text: fr.name || '' }),
                    h('span', { class: 'jbx-accent jbx-friend-stars', text: fr.rating || '' }));
                strip.appendChild(item);
            });
            box.appendChild(strip);
        }).catch(function (e) {
            if (!box.isConnected) { return; }
            box.textContent = '';
            if (e.code === 'notsignedin') {
                box.appendChild(h('p', { class: 'jbx-warn', text: e.message }));
            } else if (e.code === 'blocked') {
                box.appendChild(h('p', { class: 'jbx-muted jbx-s', text: e.message }));
            } else {
                box.appendChild(h('span', { class: 'jbx-muted jbx-s', text: 'Friends’ activity is unavailable right now. ' }));
                box.appendChild(h('button', { class: 'jbx-link jbx-linkbtn', type: 'button', text: 'Retry', onclick: function () {
                    box.textContent = ''; box.appendChild(h('div', { class: 'jbx-muted jbx-s', text: 'Loading friends…' })); loadFriends(box, itemId);
                } }));
            }
        });
    }

    // ------------------------------------------------------------------ poster badges

    function loadBadges(force) {
        if (!me || !me.features || !me.features.badges || !me.account || !me.account.connected) {
            return Promise.resolve();
        }
        if (!force && Date.now() - badgesLoadedAt < 60 * 1000) {
            return Promise.resolve();
        }
        badgesLoadedAt = Date.now();
        return api('badges', { etag: badgesEtag || '', timeout: 60000 }).then(function (res) {
            if (res && res.notModified) { return; }
            badges = (res && res.items) || {};
            badgeStats = (res && res.stats) || null;
            badgesEtag = res && res.__etag ? res.__etag : null;
            badgesVersion++;
            log('badges', Object.keys(badges).length);
            scheduleScan();
        }).catch(function (e) { log('badges failed', e); });
    }

    /** "2026-10-26" -> a short local date. Returns '' when it is not a date. */
    function formatDay(iso, short) {
        var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(iso || '');
        if (!m) { return ''; }
        var d = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]));
        if (isNaN(d.getTime())) { return ''; }
        try {
            return d.toLocaleDateString(undefined, short ? { day: 'numeric', month: 'short', year: '2-digit' } : { day: 'numeric', month: 'short', year: 'numeric' });
        } catch (e) { return iso; }
    }

    function badgeNode(entry) {
        var ui = (me && me.ui) || {};
        var pos = /^(top|bottom)-(left|center|right)$|^middle-(left|right)$/.test(ui.badgePosition || '') ? ui.badgePosition : 'top-left';
        var node = h('div', { class: 'jbx-badge jbx-pos-' + pos + (ui.badgeHideOnHover ? ' jbx-hide-hover' : ''), 'aria-hidden': 'true' });
        var any = false;
        if (entry.r && ui.badgeRating !== false) { node.appendChild(svg(STAR_PATH, 11, 'currentColor', 'jbx-badge-star')); node.appendChild(h('span', { text: String(entry.r) })); any = true; }
        if (entry.l && ui.badgeLoved !== false) { node.appendChild(svg(HEART_PATH, 12, '#ff3b5c')); any = true; }
        if (entry.w && ui.badgeWatchlist !== false) { node.appendChild(svg(BOOKMARK_PATH, 11, '#e0a030')); any = true; }
        if (entry.s && ui.badgeWatched) { node.appendChild(svg(EYE_PATH, 12, '#7ec8ff')); any = true; }
        if (entry.d && ui.badgeDate) {
            var when = formatDay(entry.d, true);
            if (when) { node.appendChild(h('span', { class: 'jbx-badge-date', text: when + (entry.n > 1 ? ' ↻' : '') })); any = true; }
        }
        return any ? node : null;
    }

    /** The poster image box of a card. Tries the most specific element first so the badge sits on the picture, not on the title text below it. */
    function poster(card) {
        var picks = ['.cardImageContainer', '.cardScalable', '[class*="cardImage" i]', '.cardBox'];
        for (var i = 0; i < picks.length; i++) {
            var el = card.querySelector(picks[i]);
            if (el) { return el; }
        }
        return card;
    }

    /** Poster cards: elements with an item id that are the card itself (not the buttons and links inside it). */
    function findCards() {
        var sel = (me && me.ui && me.ui.cardSelector) || '[data-id]';
        var all;
        try { all = document.querySelectorAll(sel); } catch (e) { all = document.querySelectorAll('[data-id]'); }
        var out = [];
        for (var i = 0; i < all.length; i++) {
            var el = all[i];
            var outer = el.closest('.card');
            if (outer && outer !== el) { continue; } // a button or link inside a card
            // Only real poster cards: anything else with an item id (detail-page buttons, links) is not a poster.
            if (!el.classList.contains('card') && !el.querySelector('.cardImageContainer, .cardScalable, .cardBox')) { continue; }
            out.push(el);
        }
        return out;
    }

    function paintBadges() {
        if (!me || !me.features || !me.features.badges || !me.account || !me.account.connected) {
            return;
        }
        var cards = findCards();
        var stamp = badgesVersion + '';
        for (var i = 0; i < cards.length; i++) {
            var card = cards[i];
            var id = normId(card.getAttribute('data-id') || card.getAttribute('data-itemid') || card.getAttribute('data-item-id'));
            if (!id) { continue; }
            // Letterboxd only knows films: skip cards that say they are shows, seasons, episodes, people, etc.
            var cardType = card.getAttribute('data-type') || card.getAttribute('data-itemtype');
            if (cardType && cardType !== 'Movie') { continue; }
            var container = poster(card);
            // Jellyfin recycles card elements: repaint when the item or the data changed.
            var key = id + ':' + stamp;
            if (card.getAttribute('data-jbx') === key) { continue; }
            card.setAttribute('data-jbx', key);
            var old = container.querySelector(':scope > .jbx-badge');
            if (old) { old.parentNode.removeChild(old); }
            var entry = badges[id];
            if (!entry) { continue; }
            var node = badgeNode(entry);
            if (!node) { continue; }
            if (window.getComputedStyle(container).position === 'static') { container.style.position = 'relative'; }
            container.appendChild(node);
            if (me.ui && me.ui.badgeHideOnHover) { card.classList.add('jbx-hover-host'); }
        }
    }

    // ------------------------------------------------------------------ "log it?" prompt

    function checkPending() {
        if (!me || !me.features || !me.features.prompt || !me.account || me.account.level !== 'session' || document.hidden) {
            return;
        }
        if (document.getElementById('jbx-prompt') || openModal) {
            return;
        }
        api('pending').then(function (res) {
            var list = (res && res.prompts) || [];
            if (!list.length || document.getElementById('jbx-prompt') || openModal) { return; }
            var p = list[0];
            if (promptShownFor === p.itemId) { return; }
            showPrompt(p);
        }).catch(function () { /* ignore */ });
    }

    function showPrompt(p) {
        promptShownFor = p.itemId;
        function dismiss(forever) {
            var el = document.getElementById('jbx-prompt');
            if (el) { el.parentNode.removeChild(el); }
            api('pending/' + p.itemId + '/dismiss', { method: 'POST', body: { forever: !!forever } }).catch(function () { /* ignore */ });
        }
        var card = h('div', { id: 'jbx-prompt', class: 'jbx-prompt', role: 'dialog', 'aria-label': 'Log to Letterboxd' },
            h('div', { class: 'jbx-prompt-title', text: 'Watched ' + p.name + (p.year ? ' (' + p.year + ')' : '') + '?' }),
            h('div', { class: 'jbx-muted jbx-s', text: 'Rate it and add it to your Letterboxd diary.' }),
            h('div', { class: 'jbx-actions' },
                h('button', { class: 'jbx-btn jbx-small', type: 'button', text: 'Not now', onclick: function () { dismiss(false); } }),
                h('button', { class: 'jbx-btn jbx-small', type: 'button', text: 'Never for this film', onclick: function () { dismiss(true); } }),
                h('button', { class: 'jbx-btn jbx-primary jbx-small', type: 'button', text: 'Rate & log', onclick: function () {
                    var el = document.getElementById('jbx-prompt');
                    if (el) { el.parentNode.removeChild(el); }
                    api('item/' + p.itemId).then(function (info) {
                        openLogModal(p.itemId, p.name, (info && info.mine) || {}, function () { remount(); });
                    }).catch(function () { openLogModal(p.itemId, p.name, {}, null); });
                } })));
        document.body.appendChild(card);
    }

    // Peek at (never alter) Jellyfin's own "playback stopped" report so the prompt shows up right away.
    function watchPlaybackStops() {
        if (typeof window.fetch !== 'function' || window.fetch.__jbx) { return; }
        var original = window.fetch;
        var wrapped = function (resource) {
            try {
                var url = typeof resource === 'string' ? resource : (resource && resource.url) || '';
                if (url.indexOf('/Sessions/Playing/Stopped') !== -1) {
                    setTimeout(checkPending, 2000);
                    setTimeout(checkPending, 6000);
                }
            } catch (e) { /* never interfere */ }
            return original.apply(this, arguments);
        };
        wrapped.__jbx = true;
        window.fetch = wrapped;
    }

    // ------------------------------------------------------------------ fallback button
    // Some themes / layouts hide or restyle the place where the panel is mounted. If no panel is
    // visible a little while after opening a film page, offer a floating button that opens the same
    // panel in a dialog, so the plugin is always reachable.

    var fabTimer = null;

    function panelVisible() {
        var list = document.querySelectorAll('.jbx-panel:not(.jbx-in-dialog)');
        for (var i = 0; i < list.length; i++) {
            if (list[i].isConnected && list[i].offsetHeight > 0 && list[i].getClientRects().length > 0 && !list[i].closest('.hide, [hidden]')) {
                return true;
            }
        }
        return false;
    }

    function removeFab() {
        var old = document.querySelectorAll('.jbx-fab');
        for (var i = 0; i < old.length; i++) { old[i].parentNode.removeChild(old[i]); }
    }

    function openPanelDialog(itemId) {
        var box = h('section', { class: 'jbx-panel jbx-in-dialog', 'data-item': itemId, 'aria-label': 'Letterboxd' },
            h('div', { class: 'jbx-skeleton', text: 'Loading Letterboxd…' }));
        modal('Letterboxd', box, { wide: true });
        api('item/' + itemId).then(function (info) {
            if (!info || !info.isMovie || !info.hasTmdb) {
                box.textContent = '';
                box.appendChild(h('p', { class: 'jbx-muted', text: 'Letterboxd is only available for movies that have a TMDB id in Jellyfin.' }));
                return;
            }
            renderPanel(box, itemId, info);
        }).catch(function (e) { renderPanelError(box, itemId, e); });
    }

    function scheduleFabCheck(itemId) {
        if (fabTimer) { clearTimeout(fabTimer); }
        fabTimer = setTimeout(function () {
            fabTimer = null;
            if (itemIdFromLocation() !== itemId || !me || panelVisible()) { return; }
            if (document.querySelector('.jbx-fab')) { return; }
            document.body.appendChild(h('button', {
                class: 'jbx-fab', type: 'button', title: 'Letterboxd', 'aria-label': 'Open Letterboxd',
                text: 'Letterboxd', onclick: function () { openPanelDialog(itemId); }
            }));
        }, 3000);
    }

    // ------------------------------------------------------------------ page watching

    var kindCache = {};
    var kindPending = {};

    function fetchKind(id) {
        if (kindPending[id]) { return; }
        kindPending[id] = true;
        var done = function (kind) { delete kindPending[id]; kindCache[id] = kind; if (kind === 'Unknown') { setTimeout(function () { delete kindCache[id]; }, 5000); } scheduleScan(); };
        try {
            var c = apiClient();
            c.getItem(c.getCurrentUserId(), id).then(function (item) { done((item && item.Type) || 'Unknown'); }, function () { done('Unknown'); });
        } catch (e) { done('Unknown'); }
    }

    var scan = throttleFrame(function () {
        if (!signedIn() || !me) { return; }
        paintBadges();
        var id = itemIdFromLocation();
        if (!id) {
            if (mountedFor) { mountedFor = null; }
            removeFab();
            return;
        }
        // Letterboxd only knows films: never show the panel or the button on shows, seasons, episodes, etc.
        var kind = kindCache[id];
        if (kind === undefined) { fetchKind(id); return; }
        if (kind !== 'Movie') { removePanel(); removeFab(); return; }
        if (panelVisible()) { removeFab(); } else { scheduleFabCheck(id); }
        var existing = document.querySelector('.jbx-panel:not(.jbx-in-dialog)');
        if (existing && existing.isConnected && existing.getAttribute('data-item') === id && mountedFor === id) {
            return;
        }
        if (existing && existing.getAttribute('data-item') !== id) {
            removePanel();
        }
        if (mountedFor !== id || !document.querySelector('.jbx-panel:not(.jbx-in-dialog)')) {
            mountedFor = null;
            mountPanel(id);
        }
    }, 200);

    function scheduleScan() { scan(); }

    function onNavigate() {
        mountedFor = null;
        loadBadges(false);
        scheduleScan();
        setTimeout(checkPending, 1500);
    }

    function hookHistory() {
        ['pushState', 'replaceState'].forEach(function (name) {
            var orig = history[name];
            if (!orig || orig.__jbx) { return; }
            var wrapped = function () {
                var r = orig.apply(this, arguments);
                setTimeout(onNavigate, 0);
                return r;
            };
            wrapped.__jbx = true;
            history[name] = wrapped;
        });
        window.addEventListener('popstate', onNavigate);
        window.addEventListener('hashchange', onNavigate);
        document.addEventListener('viewshow', onNavigate, true);
    }

    function applyTheme() {
        var accent = me && me.ui && /^#[0-9a-f]{3,8}$/i.test(me.ui.accent || '') ? me.ui.accent : '#00e054';
        document.documentElement.style.setProperty('--jbx-accent', accent);
    }

    function tick() {
        if (!signedIn()) {
            if (me) { me = null; meUserId = null; badges = {}; badgesEtag = null; removePanel(); }
            return;
        }
        var uid = apiClient().getCurrentUserId();
        if (!me || meUserId !== uid) {
            me = null;
            badges = {};
            badgesEtag = null;
            loadMe().then(function () {
                if (!me) { return; }
                applyTheme();
                loadBadges(true);
                scheduleScan();
                checkPending();
            });
        }
    }

    function start() {
        hookHistory();
        watchPlaybackStops();
        document.addEventListener('click', onJellyfinButton, true);
        new MutationObserver(scheduleScan).observe(document.body, { childList: true, subtree: true });
        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) { loadBadges(false); checkPending(); }
        });
        setInterval(tick, 2000);
        setInterval(function () { loadBadges(false); }, 5 * 60 * 1000);
        setInterval(checkPending, 90 * 1000);
        tick();
    }

    // Test / debugging hooks.
    J.reload = function () { me = null; badges = {}; badgesEtag = null; remount(); tick(); };
    J.state = function () { return { me: me, badges: badges }; };
    J.diagnose = function () {
        var cards = findCards();
        var withId = 0, painted = 0, inBadges = 0, sample = null;
        for (var i = 0; i < cards.length; i++) {
            var id = normId(cards[i].getAttribute('data-id'));
            if (id) { withId++; if (badges[id]) { inBadges++; } }
            if (cards[i].querySelector('.jbx-badge')) { painted++; }
            if (!sample && id) { sample = { id: id, inBadges: !!badges[id], type: cards[i].getAttribute('data-type') }; }
        }
        var ids = Object.keys(badges);
        return {
            version: VERSION, signedIn: signedIn(), connected: !!(me && me.account && me.account.connected),
            badgesEnabled: !!(me && me.features && me.features.badges), badgeItems: ids.length,
            badgeIdsSample: ids.slice(0, 3), serverCounts: badgeStats, cards: cards.length, cardsWithId: withId, cardsWithBadgeData: inBadges, cardsPainted: painted,
            sampleCard: sample, ui: me && me.ui
        };
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
}());
