const assert = require('node:assert/strict');
const test = require('node:test');
const path = require('path');

const api = require(path.join(__dirname, '../../../src/DotMarc/wwwroot/reconnect.js'));

const flush = () => new Promise(resolve => setImmediate(resolve));

test('budgets stay short enough for a resumed phone, and long enough for a frozen timeout', () => {
    assert.ok(api.frameworkGraceMs <= 1000);
    assert.ok(api.attemptTimeoutMs <= 2500);
    assert.ok(api.frameworkGraceMs + api.attemptTimeoutMs * 2 + api.postTimeoutRecheckMs <= 8000);
    // SignalR notices a dead socket only after its 30s client timeout, which does not run while
    // the phone is suspended. The resume window has to still be open when that fires.
    assert.ok(api.resumeWindowMs >= 30000);
    assert.equal(api.pollIntervalMs, 3000);
    assert.equal(api.reloadGuardMs, 15000);
});

test('isConnectionDown is the modal Blazor is showing', () => {
    for (const name of api.downClasses) {
        assert.equal(api.isConnectionDown([name]), true, name);
        assert.equal(api.isConnectionDown(new Set([name])), true, name);
    }
    assert.equal(api.isConnectionDown(['components-reconnect-show', 'components-reconnect-retrying']), true);
    assert.equal(api.isConnectionDown([]), false);
    assert.equal(api.isConnectionDown(['components-reconnect-hide']), false);
    assert.equal(api.isConnectionDown(null), false);
});

test('isRecentReload blocks a second reload inside the guard and allows one after it', () => {
    assert.equal(api.isRecentReload(20_000, 10_000), true);
    assert.equal(api.isRecentReload(25_000, 10_000), false);
    assert.equal(api.isRecentReload(10_000 + api.reloadGuardMs, 10_000), false);
    assert.equal(api.isRecentReload(10_000, 0), false);
    assert.equal(api.isRecentReload(10_000, NaN), false);
});

test('decideResume keeps a live circuit, reloads a refused one, and polls when the server never answered', () => {
    const cases = [
        [{ down: false, reconnect: 'down', resume: 'down', recentlyReloaded: false }, 'none'],
        [{ down: true, reconnect: 'up', resume: 'skipped', recentlyReloaded: false }, 'none'],
        [{ down: true, reconnect: 'down', resume: 'up', recentlyReloaded: false }, 'none'],
        [{ down: true, reconnect: 'down', resume: 'down', recentlyReloaded: true }, 'none'],
        [{ down: true, reconnect: 'down', resume: 'down', recentlyReloaded: false }, 'reload'],
        [{ down: true, reconnect: 'down', resume: 'skipped', recentlyReloaded: false }, 'reload'],
        [{ down: true, reconnect: 'skipped', resume: 'down', recentlyReloaded: false }, 'reload'],
        [{ down: true, reconnect: 'error', resume: 'skipped', recentlyReloaded: false }, 'poll'],
        [{ down: true, reconnect: 'timeout', resume: 'skipped', recentlyReloaded: false }, 'poll'],
        [{ down: true, reconnect: 'down', resume: 'error', recentlyReloaded: false }, 'poll'],
        [{ down: true, reconnect: 'down', resume: 'timeout', recentlyReloaded: false }, 'poll'],
    ];
    for (const [state, expected] of cases) {
        assert.equal(api.decideResume(state), expected, JSON.stringify(state));
    }
});

test('classifyAttempt reports the circuit and cancels the timer', async () => {
    let cleared = false;
    const up = await api.classifyAttempt(async () => true, 2000, () => 1, () => { cleared = true; });
    assert.equal(up, 'up');
    assert.equal(cleared, true);

    const down = await api.classifyAttempt(async () => false, 2000, () => 1, () => {});
    assert.equal(down, 'down');

    const error = await api.classifyAttempt(() => Promise.reject(new Error('down')), 2000, () => 1, () => {});
    assert.equal(error, 'error');
});

test('classifyAttempt times out an attempt that never settles', async () => {
    let fire;
    const pending = api.classifyAttempt(() => new Promise(() => {}), 2000, (_ms, callback) => {
        fire = callback;
        return 1;
    }, () => { fire = null; });
    fire();
    assert.equal(await pending, 'timeout');
});

function effects(overrides) {
    const calls = [];
    const base = {
        isDown: () => true,
        isPaused: () => false,
        recentlyReloaded: () => false,
        reconnect: async () => true,
        resume: async () => false,
        reload() { calls.push('reload'); return true; },
        poll() { calls.push('poll'); },
        delay: async () => {},
        classify: async attempt => {
            calls.push(attempt === base.resume ? 'resume' : 'reconnect');
            try {
                return await attempt() ? 'up' : 'down';
            } catch {
                return 'error';
            }
        },
        calls,
    };
    return Object.assign(base, overrides);
}

test('recoverFromResume does nothing while the circuit is up, including when it returns during the grace', async () => {
    const idle = effects({ isDown: () => false, classify: async () => { throw new Error('should not run'); } });
    assert.equal(await api.recoverFromResume(idle), 'none');

    let checks = 0;
    const recovered = effects({
        isDown: () => ++checks === 1,
        classify: async () => { throw new Error('should not run'); },
    });
    assert.equal(await api.recoverFromResume(recovered), 'none');
});

test('recoverFromResume keeps the page when reconnect or resume restores the circuit', async () => {
    const reconnected = effects();
    assert.equal(await api.recoverFromResume(reconnected), 'none');
    assert.deepEqual(reconnected.calls, ['reconnect']);

    const resumed = effects({
        reconnect: async () => false,
        resume: async () => true,
    });
    assert.equal(await api.recoverFromResume(resumed), 'none');
    assert.deepEqual(resumed.calls, ['reconnect', 'resume']);
});

test('recoverFromResume reloads once when the server refuses the circuit', async () => {
    const refused = effects({ reconnect: async () => false, resume: async () => false });
    assert.equal(await api.recoverFromResume(refused), 'reload');
    assert.deepEqual(refused.calls, ['reconnect', 'resume', 'reload']);

    const guarded = effects({
        reconnect: async () => false,
        resume: async () => false,
        recentlyReloaded: () => true,
    });
    assert.equal(await api.recoverFromResume(guarded), 'none');
    assert.equal(guarded.calls.includes('reload'), false);
});

test('recoverFromResume waits out an in-flight attempt instead of reloading over it', async () => {
    let down = true;
    let attempts = 0;
    let delays = 0;
    const cameBack = effects({
        isDown: () => down,
        classify: async () => {
            attempts++;
            return 'error';
        },
        // The grace delay is the first. The circuit comes back while we wait for the attempt
        // that was already running, which is the second.
        delay: async () => {
            delays++;
            if (delays > 1) {
                down = false;
            }
        },
    });
    assert.equal(await api.recoverFromResume(cameBack), 'none');
    assert.equal(attempts, 1);
    assert.deepEqual(cameBack.calls, []);
});

test('recoverFromResume polls when the attempt times out and the circuit is still down', async () => {
    const results = ['timeout'];
    const timedOut = effects({
        classify: async () => results.shift(),
    });
    assert.equal(await api.recoverFromResume(timedOut), 'poll');
    assert.deepEqual(timedOut.calls, ['poll']);

    let down = true;
    const landedLate = effects({
        isDown: () => down,
        classify: async () => 'timeout',
        delay: async () => { down = false; },
    });
    // The first delay is the grace, while the circuit is still down. The recheck is the second.
    let delays = 0;
    landedLate.delay = async () => {
        delays++;
        if (delays > 1) {
            down = false;
        }
    };
    assert.equal(await api.recoverFromResume(landedLate), 'none');
    assert.equal(landedLate.calls.includes('poll'), false);
});

test('a paused circuit is resumed, not put through the reconnect retry', async () => {
    const paused = effects({
        isPaused: () => true,
        resume: async () => true,
        classify: async attempt => {
            paused.calls.push('classify');
            return await attempt() ? 'up' : 'down';
        },
    });
    assert.equal(await api.recoverFromResume(paused), 'none');
    assert.deepEqual(paused.calls, ['classify']);

    const gone = effects({ isPaused: () => true, resume: async () => false });
    assert.equal(await api.recoverFromResume(gone), 'reload');
});

function harness(options = {}) {
    const classes = new Set(options.classes || []);
    const reloads = [];
    const fetches = [];
    const calls = [];
    const timers = [];
    const listeners = { document: new Map(), window: new Map() };
    const storage = options.storage || new Map();
    const add = (target, name, fn) => {
        const list = listeners[target].get(name) || [];
        list.push(fn);
        listeners[target].set(name, list);
    };
    const doc = {
        visibilityState: options.visibility || 'visible',
        getElementById(id) {
            if (options.modal === null || id !== 'components-reconnect-modal') {
                return null;
            }
            return { classList: { contains: name => classes.has(name) } };
        },
        addEventListener: (name, fn) => add('document', name, fn),
    };
    const win = {
        Blazor: options.blazor === undefined ? {
            async reconnect() {
                calls.push('reconnect');
                if (options.reconnect) {
                    return options.reconnect();
                }
                return true;
            },
            async resumeCircuit() {
                calls.push('resume');
                if (options.resume) {
                    return options.resume();
                }
                return false;
            },
        } : options.blazor,
        location: {
            href: 'https://dotmarc.example/manage',
            reload: () => reloads.push('reload'),
        },
        sessionStorage: options.sessionStorage || {
            getItem: key => (storage.has(key) ? storage.get(key) : null),
            setItem: (key, value) => storage.set(key, String(value)),
        },
        setTimeout(fn, ms) {
            const entry = { fn, ms, cancelled: false };
            timers.push(entry);
            return entry;
        },
        clearTimeout(entry) {
            if (entry) {
                entry.cancelled = true;
            }
        },
        fetch(url, init) {
            fetches.push({ url, init });
            if (options.fetch) {
                return options.fetch(url, init, fetches.length);
            }
            return Promise.resolve({ status: 200 });
        },
        addEventListener: (name, fn) => add('window', name, fn),
        AbortController,
    };
    let observer;
    class Observer {
        constructor(callback) {
            this.callback = callback;
            observer = this;
        }
        observe(target, config) {
            this.target = target;
            this.config = config;
        }
    }
    api.start({
        document: doc,
        window: win,
        MutationObserver: Observer,
        delay: async () => {},
    });
    return {
        classes,
        reloads,
        fetches,
        calls,
        timers,
        doc,
        observer,
        storage,
        async fire(target, name, event) {
            for (const fn of listeners[target].get(name) || []) {
                await fn(event);
            }
        },
        fireTimers(ms) {
            const due = timers.filter(entry => !entry.cancelled && entry.ms === ms);
            for (const entry of due) {
                entry.cancelled = true;
                entry.fn();
            }
        },
    };
}

test('a visible tab with a live circuit does not reconnect or reload', async () => {
    const page = harness();
    await page.fire('document', 'visibilitychange');
    assert.deepEqual(page.calls, []);
    assert.deepEqual(page.reloads, []);
});

test('switching back while the circuit is still alive reconnects without a reload', async () => {
    const page = harness({ classes: ['components-reconnect-show'] });
    await page.fire('document', 'visibilitychange');
    assert.deepEqual(page.calls, ['reconnect']);
    assert.deepEqual(page.reloads, []);
    assert.equal(page.fetches.length, 0);
});

test('a hidden tab and an ordinary pageshow do not count as a resume', async () => {
    const hidden = harness({ classes: ['components-reconnect-show'], visibility: 'hidden' });
    await hidden.fire('document', 'visibilitychange');
    assert.deepEqual(hidden.calls, []);

    const loaded = harness({ classes: ['components-reconnect-show'] });
    await loaded.fire('window', 'pageshow', { persisted: false });
    assert.deepEqual(loaded.calls, []);
});

test('a bfcache restore and a frozen-page resume reconnect', async () => {
    const restored = harness({
        classes: ['components-reconnect-retrying'],
        reconnect: async () => false,
        resume: async () => true,
    });
    await restored.fire('window', 'pageshow', { persisted: true });
    assert.deepEqual(restored.calls, ['reconnect', 'resume']);
    assert.deepEqual(restored.reloads, []);

    const frozen = harness({ classes: ['components-reconnect-show'] });
    await frozen.fire('document', 'resume');
    assert.deepEqual(frozen.calls, ['reconnect']);
});

test('a refused circuit reloads once, and a second resume inside the guard does not reload again', async () => {
    const storage = new Map();
    const first = harness({
        classes: ['components-reconnect-show'],
        storage,
        reconnect: async () => false,
        resume: async () => false,
    });
    await first.fire('document', 'visibilitychange');
    assert.deepEqual(first.reloads, ['reload']);
    assert.equal(storage.get(api.storageKey) > 0, true);

    const second = harness({
        classes: ['components-reconnect-show'],
        storage,
        reconnect: async () => false,
        resume: async () => false,
    });
    await second.fire('document', 'visibilitychange');
    assert.deepEqual(second.reloads, []);
    assert.deepEqual(second.calls, ['reconnect', 'resume']);
});

test('a paused circuit resumes without a reconnect attempt', async () => {
    const page = harness({
        classes: ['components-reconnect-paused'],
        resume: async () => true,
    });
    await page.fire('document', 'visibilitychange');
    assert.deepEqual(page.calls, ['resume']);
    assert.deepEqual(page.reloads, []);
});

test('a server that never answers is polled, not reloaded, until it responds', async () => {
    let status = 502;
    const page = harness({
        classes: ['components-reconnect-show'],
        reconnect: async () => { throw new Error('network down'); },
        fetch: () => Promise.resolve({ status }),
    });
    await page.fire('document', 'visibilitychange');
    await flush();
    assert.deepEqual(page.reloads, []);
    assert.equal(page.fetches.length, 1);
    assert.equal(page.fetches[0].url, 'https://dotmarc.example/manage');
    assert.equal(page.fetches[0].init.method, 'HEAD');
    assert.equal(page.fetches[0].init.cache, 'no-store');
    assert.ok(page.fetches[0].init.signal);

    status = 200;
    page.fireTimers(api.pollIntervalMs);
    await flush();
    assert.deepEqual(page.reloads, ['reload']);
});

test('resuming onto a failed circuit tries the live one before reloading', async () => {
    const alive = harness({ classes: ['components-reconnect-failed'] });
    await alive.fire('document', 'visibilitychange');
    assert.deepEqual(alive.calls, ['reconnect']);
    assert.deepEqual(alive.reloads, []);
    assert.equal(alive.fetches.length, 0);

    // The same class with nobody resuming the tab is a deploy: reload only once the server answers.
    const deploy = harness({
        classes: ['components-reconnect-failed'],
        fetch: () => Promise.resolve({ status: 200 }),
    });
    deploy.observer.callback();
    await flush();
    assert.deepEqual(deploy.calls, []);
    assert.deepEqual(deploy.reloads, ['reload']);
});

test('a failed deploy keeps polling until the server answers, and does not reload if the circuit returns', async () => {
    const down = harness({
        classes: ['components-reconnect-failed'],
        fetch: (_url, _init, n) => Promise.resolve({ status: n === 1 ? 503 : 200 }),
    });
    down.observer.callback();
    await flush();
    assert.deepEqual(down.reloads, []);
    down.fireTimers(api.pollIntervalMs);
    await flush();
    assert.deepEqual(down.reloads, ['reload']);
    assert.deepEqual(down.observer.config, { attributes: true, attributeFilter: ['class'] });

    let resolveFetch;
    const recovered = harness({
        classes: ['components-reconnect-resume-failed'],
        fetch: () => new Promise(resolve => { resolveFetch = resolve; }),
    });
    recovered.observer.callback();
    await flush();
    recovered.classes.delete('components-reconnect-resume-failed');
    resolveFetch({ status: 200 });
    await flush();
    assert.deepEqual(recovered.reloads, []);
});

test('a rejected circuit reloads, and a hung health check is retried rather than followed', async () => {
    const rejected = harness({ classes: ['components-reconnect-rejected'] });
    rejected.observer.callback();
    rejected.observer.callback();
    assert.deepEqual(rejected.reloads, ['reload']);
    assert.equal(rejected.fetches.length, 0);

    const hung = harness({
        classes: ['components-reconnect-failed'],
        fetch: (_url, init) => new Promise((_resolve, reject) => {
            init.signal.addEventListener('abort', () => reject(new Error('aborted')));
        }),
    });
    hung.observer.callback();
    await flush();
    assert.equal(hung.fetches.length, 1);
    hung.fireTimers(api.headTimeoutMs);
    await flush();
    assert.deepEqual(hung.reloads, []);
    assert.equal(hung.timers.some(entry => !entry.cancelled && entry.ms === api.pollIntervalMs), true);
});

test('a reload the guard refuses is tried again later, not in a loop', async () => {
    const storage = new Map([[api.storageKey, String(Date.now())]]);
    const page = harness({ classes: ['components-reconnect-rejected'], storage });
    page.observer.callback();
    assert.deepEqual(page.reloads, []);
    const waiting = page.timers.filter(entry => !entry.cancelled && entry.ms === api.reloadGuardMs);
    assert.equal(waiting.length, 1);

    storage.clear();
    page.fireTimers(api.reloadGuardMs);
    assert.deepEqual(page.reloads, ['reload']);
});

test('sessionStorage being unavailable still stops a second reload in the same document', () => {
    const page = harness({
        classes: ['components-reconnect-rejected'],
        sessionStorage: {
            getItem() { throw new Error('private mode'); },
            setItem() { throw new Error('private mode'); },
        },
    });
    page.observer.callback();
    page.observer.callback();
    assert.deepEqual(page.reloads, ['reload']);
});

test('a page with no reconnect modal does not throw', () => {
    assert.doesNotThrow(() => harness({ modal: null }));
});
