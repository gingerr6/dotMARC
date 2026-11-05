// Mobile browsers freeze a background tab and drop its WebSocket. Blazor only notices once the tab
// runs JavaScript again, then shows #components-reconnect-modal and retries on a backoff that
// stretches to 30 seconds. It reloads only after the whole retry budget (about six minutes) or when
// the server explicitly rejects the circuit. A phone that has just been unlocked is usually sitting
// in that gap: the retry was started while the radio was still asleep and never settles, or the next
// attempt is still a long way off. visibilitychange skips a wait that has not started yet; it does
// not interrupt one that is already in flight, and a bfcache restore fires pageshow instead.
//
// The server is not the cause. A frozen tab stops sending keepalives, the circuit is dropped after
// the 30s client timeout, and it is kept for the default three minutes. A typical app switch is
// inside that window, so the circuit is still there — the page just never finishes asking for it.
// Lengthening DisconnectedCircuitRetentionPeriod would not dismiss the modal.
//
// When the page is shown again, try once to resume the live circuit. If the server still has it,
// Blazor.reconnect() returns and the modal closes with no reload. If the server answers and refuses,
// reload once. If the server cannot be reached, keep the deploy behaviour: poll until it responds,
// then reload. A stamp in sessionStorage stops one reload from immediately causing another.
(function (root, factory) {
    const api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    }
    if (typeof document !== 'undefined' && typeof window !== 'undefined') {
        window.dotmarcReconnect = api;
        api.start({ document: document, window: window });
    } else {
        root.dotmarcReconnect = api;
    }
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
    // Any of these classes means Blazor is showing the modal. "hide" is the connected state.
    const downClasses = [
        'components-reconnect-show',
        'components-reconnect-retrying',
        'components-reconnect-failed',
        'components-reconnect-resume-failed',
        'components-reconnect-rejected',
        'components-reconnect-paused',
    ];

    // Let Blazor's own visibility fast-path try before we start a second attempt.
    const frameworkGraceMs = 500;
    // One reconnect on a waking radio. Long enough for a slow handshake, short of "a few seconds".
    const attemptTimeoutMs = 2000;
    // A timeout can land in the same moment the circuit comes back. Look once more before reloading.
    const postTimeoutRecheckMs = 400;
    // SignalR's client timeout is 30s and does not advance while the phone is suspended, so the
    // modal can appear a little after the tab is already visible. Treat that as the same resume.
    const resumeWindowMs = 45000;
    const pollIntervalMs = 3000;
    const headTimeoutMs = 3000;
    const reloadGuardMs = 15000;
    const storageKey = 'dotmarc-reconnect-reload';

    function includesClass(classNames, name) {
        if (!classNames) {
            return false;
        }
        if (typeof classNames.contains === 'function') {
            return classNames.contains(name);
        }
        if (typeof classNames.has === 'function') {
            return classNames.has(name);
        }
        if (typeof classNames.includes === 'function') {
            return classNames.includes(name);
        }
        return false;
    }

    function isConnectionDown(classNames) {
        return downClasses.some(name => includesClass(classNames, name));
    }

    function isRecentReload(now, reloadedAt) {
        return typeof reloadedAt === 'number' && reloadedAt > 0 && now - reloadedAt < reloadGuardMs;
    }

    // reconnect/resume: 'up' the call resolved true, 'down' the server answered and refused,
    // 'error' the call threw, 'timeout' it never settled, 'skipped' it was not called.
    // 'none' keeps the page, 'reload' loads it now, 'poll' loads it once the server answers.
    function decideResume(state) {
        if (!state.down || state.reconnect === 'up' || state.resume === 'up' || state.recentlyReloaded) {
            return 'none';
        }
        // The server answered. The old circuit is gone, and resume did not rebuild it.
        if (state.resume === 'down' || (state.reconnect === 'down' && state.resume === 'skipped')) {
            return 'reload';
        }
        return 'poll';
    }

    function classifyAttempt(attempt, timeoutMs, setTimer, clearTimer) {
        return new Promise(resolve => {
            let settled = false;
            let timer;
            const finish = value => {
                if (settled) {
                    return;
                }
                settled = true;
                if (timer !== undefined) {
                    clearTimer(timer);
                }
                resolve(value);
            };
            timer = setTimer(timeoutMs, () => finish('timeout'));
            Promise.resolve()
                .then(attempt)
                .then(ok => finish(ok ? 'up' : 'down'))
                .catch(() => finish('error'));
        });
    }

    function finish(effects, action) {
        if (action === 'reload') {
            effects.reload();
        } else if (action === 'poll') {
            effects.poll();
        }
        return action;
    }

    async function recoverFromResume(effects) {
        const delay = effects.delay || (ms => new Promise(resolve => setTimeout(resolve, ms)));
        const classify = effects.classify || ((attempt, ms) => classifyAttempt(attempt, ms, setTimeout, clearTimeout));
        const attemptMs = effects.attemptMs ?? attemptTimeoutMs;
        const graceMs = effects.graceMs ?? frameworkGraceMs;

        if (!effects.isDown()) {
            return 'none';
        }
        await delay(graceMs);
        if (!effects.isDown()) {
            return 'none';
        }

        // A paused circuit is not in the retry loop. Resume is the call that restores it.
        if (typeof effects.isPaused === 'function' && effects.isPaused()) {
            const resumeResult = await classify(effects.resume, attemptMs);
            if (resumeResult === 'up' || !effects.isDown()) {
                return 'none';
            }
            return finish(effects, decideResume({
                down: effects.isDown(),
                reconnect: 'skipped',
                resume: resumeResult,
                recentlyReloaded: effects.recentlyReloaded(),
            }));
        }

        let reconnectResult = await classify(effects.reconnect, attemptMs);
        if (reconnectResult === 'up' || !effects.isDown()) {
            return 'none';
        }

        if (reconnectResult === 'error') {
            // Blazor refuses to start a second reconnect while one is already running. That attempt
            // may be the one that brings the circuit back, so wait for it before reloading.
            await delay(attemptMs);
            if (!effects.isDown()) {
                return 'none';
            }
            reconnectResult = await classify(effects.reconnect, attemptMs);
            if (reconnectResult === 'up' || !effects.isDown()) {
                return 'none';
            }
        } else if (reconnectResult === 'timeout') {
            await delay(effects.postTimeoutRecheckMs ?? postTimeoutRecheckMs);
            if (!effects.isDown()) {
                return 'none';
            }
        }

        let resumeResult = 'skipped';
        if (reconnectResult === 'down') {
            resumeResult = await classify(effects.resume, attemptMs);
            if (resumeResult === 'up' || !effects.isDown()) {
                return 'none';
            }
        }

        return finish(effects, decideResume({
            down: effects.isDown(),
            reconnect: reconnectResult,
            resume: resumeResult,
            recentlyReloaded: effects.recentlyReloaded(),
        }));
    }

    function start(env) {
        const doc = env.document;
        const win = env.window;
        const modal = doc.getElementById('components-reconnect-modal');
        if (!modal) {
            return;
        }

        const delay = env.delay || (ms => new Promise(resolve => win.setTimeout(resolve, ms)));
        const classify = env.classify || ((attempt, ms) => classifyAttempt(
            attempt,
            ms,
            (timeout, callback) => win.setTimeout(callback, timeout),
            id => win.clearTimeout(id)));

        let waiting = false;
        let recovering = false;
        let resumeHandled = false;
        let resumeUntil = 0;
        // Same-document guard. sessionStorage covers the reload that follows, including private mode
        // where it usually works; if it throws, this still stops a second reload before navigation.
        let lastReloadAt = 0;

        const isDown = () => isConnectionDown(modal.classList);
        const isPaused = () => includesClass(modal.classList, 'components-reconnect-paused');

        function storedReloadAt() {
            try {
                return Number(win.sessionStorage.getItem(storageKey) || 0);
            } catch {
                return 0;
            }
        }

        function reloadMark() {
            return lastReloadAt || storedReloadAt();
        }

        function recentlyReloaded() {
            return isRecentReload(Date.now(), reloadMark());
        }

        function reload() {
            const now = Date.now();
            if (isRecentReload(now, reloadMark())) {
                return false;
            }
            lastReloadAt = now;
            try {
                win.sessionStorage.setItem(storageKey, String(now));
            } catch {
                // The in-memory stamp still covers this document.
            }
            win.location.reload();
            return true;
        }

        function poll() {
            if (waiting) {
                return;
            }
            waiting = true;
            const tryOnce = () => {
                if (!isDown()) {
                    waiting = false;
                    return;
                }
                const Abort = win.AbortController || AbortController;
                const controller = new Abort();
                const timer = win.setTimeout(() => controller.abort(), headTimeoutMs);
                win.fetch(win.location.href, { method: 'HEAD', cache: 'no-store', signal: controller.signal })
                    .then(response => {
                        win.clearTimeout(timer);
                        if (!isDown()) {
                            waiting = false;
                            return;
                        }
                        if (response.status < 500) {
                            // Refused by the guard: try again once the window has passed, not in a loop.
                            if (!reload()) {
                                win.setTimeout(tryOnce, reloadGuardMs);
                            }
                            return;
                        }
                        win.setTimeout(tryOnce, pollIntervalMs);
                    })
                    .catch(() => {
                        win.clearTimeout(timer);
                        if (!isDown()) {
                            waiting = false;
                            return;
                        }
                        win.setTimeout(tryOnce, pollIntervalMs);
                    });
            };
            tryOnce();
        }

        function blazor() {
            return win.Blazor || {};
        }

        async function kick() {
            if (recovering || resumeHandled || !isDown()) {
                return 'none';
            }
            recovering = true;
            try {
                const action = await recoverFromResume({
                    isDown,
                    isPaused,
                    recentlyReloaded,
                    reconnect: () => {
                        const current = blazor();
                        if (typeof current.reconnect !== 'function') {
                            return Promise.reject(new Error('Blazor reconnect is unavailable'));
                        }
                        return current.reconnect();
                    },
                    resume: () => {
                        const current = blazor();
                        if (typeof current.resumeCircuit !== 'function') {
                            return Promise.resolve(false);
                        }
                        return current.resumeCircuit();
                    },
                    reload,
                    poll,
                    delay,
                    classify,
                    graceMs: env.graceMs,
                    attemptMs: env.attemptMs,
                });
                if (action === 'reload' || action === 'poll') {
                    resumeHandled = true;
                }
                return action;
            } finally {
                recovering = false;
            }
        }

        // The server is back and this circuit is gone. If a reload was refused a moment ago, ask
        // again once the guard has passed instead of leaving the rejected page up for good.
        function reloadRejected() {
            if (!reload()) {
                win.setTimeout(() => {
                    if (isDown()) {
                        reloadRejected();
                    }
                }, reloadGuardMs);
            }
            return Promise.resolve('reload');
        }

        function onResume() {
            resumeUntil = Date.now() + resumeWindowMs;
            if (!recovering && !waiting) {
                resumeHandled = false;
            }
            if (!isDown()) {
                return Promise.resolve('none');
            }
            // Rejected means the server already answered and dropped this circuit: reload.
            // Failed can also mean the retries ran out while the phone was asleep, with the
            // circuit still alive, so try it before falling through to a reload.
            if (includesClass(modal.classList, 'components-reconnect-rejected')) {
                return reloadRejected();
            }
            return kick();
        }

        doc.addEventListener('visibilitychange', () => {
            if (doc.visibilityState === 'visible') {
                return onResume();
            }
        });
        // Fired when the browser resumes a frozen page. visibilitychange usually follows; the
        // in-flight guard makes the second one a no-op.
        doc.addEventListener('resume', () => onResume());
        // bfcache restores the frozen page, listeners included, and does not change visibility.
        win.addEventListener('pageshow', event => {
            if (event.persisted) {
                return onResume();
            }
        });

        const Observer = env.MutationObserver || win.MutationObserver;
        new Observer(() => {
            if (includesClass(modal.classList, 'components-reconnect-rejected')) {
                reloadRejected();
                return;
            }
            // A resume is still in progress: try the circuit, including when the retry budget
            // has just run out. Outside a resume, failed/resume-failed is the deploy path and
            // only reloads once the server answers.
            if (Date.now() < resumeUntil && isDown()) {
                kick();
                return;
            }
            if (includesClass(modal.classList, 'components-reconnect-failed')
                || includesClass(modal.classList, 'components-reconnect-resume-failed')) {
                poll();
            }
        }).observe(modal, { attributes: true, attributeFilter: ['class'] });
    }

    return {
        downClasses,
        frameworkGraceMs,
        attemptTimeoutMs,
        postTimeoutRecheckMs,
        resumeWindowMs,
        pollIntervalMs,
        headTimeoutMs,
        reloadGuardMs,
        storageKey,
        isConnectionDown,
        isRecentReload,
        decideResume,
        classifyAttempt,
        recoverFromResume,
        start,
    };
});
