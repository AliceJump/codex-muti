// Install test-only DOM support in the ignored primary checkout:
// npm install --prefix .research/dashboard-dom --no-audit --no-fund jsdom@26.1.0
// NODE_PATH=.research/dashboard-dom/node_modules node --test scripts/tests/dashboard-updates.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { JSDOM } = require('jsdom');
const html = readFileSync('src/CodexPool.Daemon/Dashboard/index.html', 'utf8');
const script = readFileSync('src/CodexPool.Daemon/Dashboard/dashboard.js', 'utf8');
const now = new Date().toISOString();
const totals = { requests: 1, reportedRequests: 1, totalTokens: 30, inputTokens: 20, outputTokens: 10,
    cachedInputTokens: 0, reasoningOutputTokens: 0, cachedReportedRequests: 1, reasoningReportedRequests: 1, unreportedRequests: 0 };
const stats = () => ({ startedAt: now, updatedAt: now, currentDay: now.slice(0, 10), persistence: 'ready', timeZone: 'UTC', daily: [],
    selected: { totals, accounts: [{ accountId: 'a', totals }], models: [{ model: 'model', totals }] },
    today: { accounts: [{ accountId: 'a', totals }] }, allTime: { totals } });
const state = () => ({ accounts: ['a', 'b'].map(id => ({ id, name: id, active: id === 'a', enabled: true, paused: false, drain: false, state: 'standby', auth: 'ready' })),
    activeAccountId: 'a', automaticSwitching: true, boundaryProbe: true, settings: { autoSwitch: true, windowThreshold: 95 }, inFlightRequests: [],
    usage: ['a', 'b'].map(accountId => ({ accountId, updatedAt: now, windows: [{ kind: 'primary_window', limitId: 'codex', windowSeconds: 18000,
        remainingPercent: 60, resetAt: new Date(Date.now() + 3600000).toISOString() }] })), recentSwitches: [], nativeLoginAttempts: [] });
const record = sequence => ({ sequence, timestamp: new Date(Date.parse(now) + sequence).toISOString(), account: 'a', model: 'model', durationMs: 10,
    status: 200, outcome: 'completed', attempts: 1, tokenUsage: null });
const page = (items, nextBefore = null) => ({ items: items.map(record), nextBefore, persistence: 'ready', startedAt: now });
const tick = () => new Promise(resolve => setImmediate(resolve));
async function setup() {
    const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'http://localhost/' });
    const w = dom.window, calls = [];
    let current = state(), history = page([3, 2], 2), statistics = stats();
    let intercept = null;
    w.setInterval = () => 0;
    w.EventSource = class { addEventListener() {} };
    w.fetch = async (url, options) => {
        calls.push({ url, options });
        if (intercept) { const reply = intercept(url, options); if (reply) return reply; }
        const data = url === '/browser-session' ? { csrf: 'fixture' } : url === '/status' ? current
            : url.startsWith('/statistics?') ? statistics : url.startsWith('/requests?') ? history : url === '/model-keys' ? [] : {};
        return { ok: true, json: async () => structuredClone(data) };
    };
    w.eval(script + '\nwindow.testApi={render,refreshHistory,renderStatistics,patchChildren,patchElement,getItems:()=>historyItems,getCursor:()=>nextBefore};');
    await tick();
    return { dom, w, calls, api: w.testApi, setState(value) { current = value; }, setPage(value) { history = value; },
        intercept(value) { intercept = value; }, setStats(value) { statistics = value; } };
}
test('model key lifecycle requires CSRF, renders names as text, and clears the one-time secret', async () => {
    const h = await setup(), { w } = h;
    try {
        let keys = [], copied = '';
        Object.defineProperty(w.navigator, 'clipboard', { value: { writeText: async value => { copied = value; } } });
        h.intercept((url) => {
            const data = url === '/model-keys/create' ? { id: 'fixture-key', key: 'SYNTHETIC-KEY-CANARY' }
                : url === '/model-keys' ? keys : url === '/model-keys/fixture-key/revoke' ? { revoked: true } : undefined;
            if (data === undefined) return null;
            if (url === '/model-keys/create') keys = [{ id: 'fixture-key', name: '<img src=x onerror=alert(1)>' }];
            if (url.endsWith('/revoke')) keys = [];
            return { ok: true, json: async () => structuredClone(data) };
        });
        const form = w.document.getElementById('model-key-create');
        w.document.getElementById('model-key-name').value = 'AstrBot';
        form.dispatchEvent(new w.SubmitEvent('submit', { cancelable: true, submitter: form.querySelector('button') }));
        await tick();
        const creation = h.calls.find(x => x.url === '/model-keys/create');
        assert.equal(creation.options.headers['X-Codex-Pool-CSRF'], 'fixture');
        assert.equal(JSON.parse(creation.options.body).name, 'AstrBot');
        assert.equal(w.document.getElementById('model-key-secret').value, 'SYNTHETIC-KEY-CANARY');
        assert.equal(w.document.querySelector('#model-keys img'), null);
        assert.match(w.document.getElementById('model-keys').textContent, /<img/);
        w.document.getElementById('model-key-copy').click(); await tick();
        assert.equal(copied, 'SYNTHETIC-KEY-CANARY');
        w.document.getElementById('model-keys').querySelector('button').click(); await tick();
        assert.equal(w.document.getElementById('model-key-secret').value, '');
        assert.equal(w.document.getElementById('model-key-issued').hidden, true);
        assert.equal(w.document.getElementById('model-keys').children.length, 0);
        assert.equal(w.localStorage.length, 0);
        assert.equal(w.sessionStorage.length, 0);
    } finally { h.dom.window.close(); }
});
test('identical state causes no account DOM mutations; changes preserve card, button focus, and latest actions', async () => {
    const h = await setup(), { w, api } = h;
    try {
        const s = state(); api.render(s);
        const cards = [...w.document.querySelectorAll('#accounts article')], button = cards[0].querySelector('button');
        button.focus();
        const observer = new w.MutationObserver(() => {}); observer.observe(w.document.getElementById('accounts'), { subtree: true, attributes: true, childList: true, characterData: true });
        api.render(s); assert.equal(observer.takeRecords().length, 0);
        s.accounts[0].name = 'renamed'; s.accounts[0].paused = true; s.usage[0].windows[0].remainingPercent = 22;
        api.render(s);
        assert.equal(w.document.querySelector('#accounts article'), cards[0]);
        assert.equal(w.document.activeElement, button); assert.equal(button.textContent, '恢复');
        assert.equal(cards[0].querySelector('progress').value, 22); assert.equal(cards[1], w.document.querySelectorAll('#accounts article')[1]);
        h.setState(s); button.click(); await tick();
        assert.ok(h.calls.some(x => x.url === '/accounts/a/resume' && x.options.method === 'POST'));
        s.accounts.reverse(); api.render(s);
        assert.equal(w.document.querySelectorAll('#accounts article')[1], cards[0]);
        s.accounts = s.accounts.filter(x => x.id !== 'b'); api.render(s); assert.equal(w.document.querySelectorAll('#accounts article').length, 1);
        observer.disconnect();
    } finally { h.dom.window.close(); }
});
test('subscription cutoff follows browser time zone including DST and unknown metadata stays unknown', async () => {
    const h = await setup(), { w, api } = h;
    try {
        const s = state();
        // Override the browser's local formatter zone to differ from the daemon/host.
        const original = w.Date.prototype.toLocaleString;
        let zone = 'America/Los_Angeles';
        w.Date.prototype.toLocaleString = function(locale, options) { return original.call(this, locale, { ...options, timeZone: zone }); };
        s.accounts[0].subscriptionActiveUntil = '2030-01-02T03:04:05Z';
        api.render(s);
        const deadline = w.document.querySelector('#accounts [data-key="subscription"]');
        assert.match(deadline.textContent, /2030\/01\/01/); assert.match(deadline.textContent, /19:04:05/); assert.match(deadline.textContent, /GMT-08:00/);
        s.accounts[0].subscriptionActiveUntil = '2030-07-02T03:04:05Z'; api.render(s);
        assert.equal(w.document.querySelector('#accounts [data-key="subscription"]'), deadline);
        assert.match(deadline.textContent, /2030\/07\/01/); assert.match(deadline.textContent, /20:04:05/); assert.match(deadline.textContent, /GMT-07:00/);
        zone = 'Asia/Shanghai'; api.render(s);
        assert.match(deadline.textContent, /2030\/07\/02/); assert.match(deadline.textContent, /11:04:05/); assert.match(deadline.textContent, /GMT\+08:00/);
        for (const value of [undefined, null, '', 'bad', 0]) {
            s.accounts[0].subscriptionActiveUntil = value; api.render(s);
            assert.equal(deadline.textContent, '订阅截止时间未知');
        }
    } finally { h.dom.window.close(); }
});

test('statistics update values in existing rows and preserve edited form and scroll', async () => {
    const h = await setup(), { w, api } = h;
    try {
        const s = state(); s.tokenStatistics = stats(); h.setState(s); api.render(s); await tick();
        const row = w.document.querySelector('#token-accounts tr'), metric = w.document.querySelector('#token-summary strong');
        const field = w.document.querySelector('[name=windowThreshold]'); field.focus(); field.value = '71'; field.dispatchEvent(new w.Event('input', { bubbles: true }));
        const scroller = w.document.getElementById('token-daily'); scroller.scrollTop = 100;
        const next = stats(); next.selected.totals = { ...totals, totalTokens: 55 }; next.selected.accounts[0].totals = next.selected.totals;
        api.renderStatistics(next, new Map([['a', 'updated']])); assert.equal(metric.textContent, '55'); api.render(s);
        assert.equal(w.document.querySelector('#token-accounts tr'), row); assert.equal(w.document.querySelector('#token-summary strong'), metric);
        assert.equal(field.value, '71'); assert.equal(w.document.activeElement, field); assert.equal(scroller.scrollTop, 100);
        field.blur(); api.render(s); assert.equal(field.value, '71'); // Unsaved input also survives focus leaving the form.
    } finally { h.dom.window.close(); }
});
test('background history refresh never empties loaded pages and keeps the older cursor and rows', async () => {
    const h = await setup(), { w, api } = h;
    try {
        const s = state(); s.tokenStatistics = stats(); h.setState(s); api.render(s); await tick();
        h.setPage(page([1], null)); await api.refreshHistory(true, true);
        assert.equal(api.getItems().length, 3); assert.equal(api.getCursor(), null);
        const older = w.document.querySelector('#requests tr[data-key="1"]');
        const viewport = older.closest('.table-scroll'); viewport.scrollTop = 100;
        let finish;
        h.intercept(url => url.startsWith('/requests?') ? new Promise(resolve => { finish = () => resolve({ ok: true, json: async () => page([4, 3], 3) }); }) : null);
        s.tokenStatistics.updatedAt = new Date(Date.parse(now) + 10).toISOString(); api.render(s); await tick();
        assert.equal(w.document.querySelectorAll('#requests tr').length, 3); assert.equal(api.getItems().length, 3);
        finish(); await tick();
        assert.equal(api.getItems().length, 4); assert.equal(api.getCursor(), null);
        assert.equal(w.document.querySelector('#requests tr[data-key="1"]'), older);
        assert.equal(viewport.scrollTop, 100);
        h.intercept(null); h.setPage(page([4, 3], 3)); await api.refreshHistory(true);
        assert.equal(api.getItems().length, 4); // Overlap never duplicates a record.
    } finally { h.dom.window.close(); }
});
test('a changed date range discards stale in-flight results and resets pagination once', async () => {
    const h = await setup(), { w, api } = h;
    try {
        const s = state(); s.tokenStatistics = stats(); api.render(s); await tick();
        let finish;
        h.intercept(url => url.startsWith('/requests?') && !url.includes('from=') ? new Promise(resolve => { finish = () => resolve({ ok: true, json: async () => page([99]) }); }) : null);
        const pending = api.refreshHistory(true); await tick();
        w.document.getElementById('history-from').value = '2026-01-01'; h.setPage(page([1]));
        w.document.getElementById('history-range').dispatchEvent(new w.Event('submit', { cancelable: true })); await tick();
        finish(); await pending;
        assert.deepEqual(Array.from(api.getItems(), x => x.sequence), [1]);
    } finally { h.dom.window.close(); }
});
