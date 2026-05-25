// Stop all — progressive-enhancement power-down cascade.
// Without JS the #stop-all-form posts to Services/StopAll and the page reloads.
// With JS we intercept the submit, ask the server to run the canonical StopAll
// sweep (including orphaned runs), and animate visible running cards as they dim.
(function () {
    const form = document.getElementById('stop-all-form');
    const btn = document.getElementById('stop-all-btn');
    const root = document.getElementById('cards');
    if (!form || !btn || !root) return;

    const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const STAGGER = reduce ? 0 : 240; // ms between power-downs (snappier than ignite)
    const token = form.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    const label = btn.querySelector('.sa-label');
    const idleLabel = label ? label.innerHTML : '■&nbsp;Stop all';

    const STOPPABLE = new Set(['running', 'starting', 'stopping']);

    function stoppable() {
        return Array.from(root.querySelectorAll('.service-card'))
            .filter(card => {
                const pill = card.querySelector('[data-field="runtime"]');
                return pill && STOPPABLE.has(pill.textContent.trim().toLowerCase());
            });
    }

    function setPill(card, value) {
        const pill = card.querySelector('[data-field="runtime"]');
        if (!pill) return;
        pill.textContent = value;
        for (const c of Array.from(pill.classList)) {
            if (c.startsWith('pill-')) pill.classList.remove(c);
        }
        pill.classList.add('pill-' + value.toLowerCase());
    }

    function flash(kind, text) {
        const content = document.querySelector('.content') || document.body;
        const el = document.createElement('div');
        el.className = 'flash ' + kind + ' flash-transient';
        el.textContent = text;
        content.insertBefore(el, content.firstChild);
        setTimeout(() => {
            el.classList.add('flash-leaving');
            setTimeout(() => el.remove(), 400);
        }, 4200);
    }

    function renderHud(stopped, processed, total) {
        if (!label) return;
        const filled = '▰'.repeat(processed);
        const empty = '▱'.repeat(Math.max(0, total - processed));
        label.innerHTML = `◆&nbsp;Halting&nbsp;<b>${stopped}</b>/${total} <span class="sa-bar">${filled}${empty}</span>`;
    }

    function renderWaitingHud() {
        if (!label) return;
        label.innerHTML = '◆&nbsp;Halting…';
    }

    function markQuashing(card) {
        card.classList.remove('stop-failed');
        card.classList.add('quashing');
        setPill(card, 'Stopping');
    }

    function markOutcome(outcome) {
        const card = root.querySelector(`.service-card[data-service-id="${outcome.serviceId}"]`);
        if (!card) return;
        if (outcome.success) {
            // The StopAll call has completed this service; polling will also confirm it.
            setPill(card, 'Stopped');
            card.classList.remove('quashing', 'stop-failed');
            const pill = card.querySelector('[data-field="runtime"]');
            if (pill) {
                pill.classList.remove('offline-pop');
                void pill.offsetWidth;
                pill.classList.add('offline-pop');
                pill.addEventListener('animationend', () => pill.classList.remove('offline-pop'), { once: true });
            }
        } else {
            card.classList.remove('quashing');
            card.classList.add('stop-failed');
        }
    }

    async function runStopAll() {
        const res = await fetch('/Manage/Services/StopAll', {
            method: 'POST',
            headers: {
                'Accept': 'application/json',
                'X-Requested-With': 'XMLHttpRequest',
                'RequestVerificationToken': token
            }
        });
        if (!res.ok) return null;
        return await res.json();
    }

    async function animateTargets(targets) {
        if (targets.length === 0) {
            renderWaitingHud();
            return;
        }

        let processed = 0;
        renderHud(0, 0, targets.length);
        for (const card of targets) {
            markQuashing(card);
            processed++;
            renderHud(0, processed, targets.length);
            if (STAGGER) await new Promise(r => setTimeout(r, STAGGER));
        }
    }

    async function powerDown() {
        const targets = stoppable();

        btn.disabled = true;
        btn.classList.add('stopping');
        let data = null;
        try {
            const [stopAllResult] = await Promise.all([runStopAll(), animateTargets(targets)]);
            data = stopAllResult;
        } catch (_) {
            data = null;
        } finally {
            btn.disabled = false;
            btn.classList.remove('stopping');
            if (label) label.innerHTML = idleLabel;
        }

        if (!data) {
            for (const card of targets) {
                card.classList.remove('quashing');
                card.classList.add('stop-failed');
            }
            flash('error', 'Stop all failed.');
            return;
        }

        const outcomes = Array.isArray(data.outcomes) ? data.outcomes : [];
        for (const outcome of outcomes) {
            markOutcome(outcome);
        }

        const stopped = Number(data.stopped) || 0;
        const failed = outcomes.filter(o => !o.success).length;
        if (stopped === 0 && failed === 0) {
            flash('info', 'Nothing to stop — no services are running.');
            return;
        }
        if (failed === 0) {
            flash('info', `✦ Deck powered down — ${stopped} ${stopped === 1 ? 'service' : 'services'} halted.`);
        } else {
            flash('error', `${stopped} stopped, ${failed} failed to stop.`);
        }
    }

    form.addEventListener('submit', (e) => {
        e.preventDefault();
        powerDown();
    });
})();
