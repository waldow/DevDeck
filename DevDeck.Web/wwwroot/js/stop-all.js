// Stop all — progressive-enhancement power-down cascade.
// Without JS the #stop-all-form posts to Services/StopAll and the page reloads.
// With JS we intercept the submit and quash running services one-by-one, dimming
// each card as it powers down. dashboard.js polling confirms Stopped (offline pop).
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

    async function quash(card) {
        const id = card.getAttribute('data-service-id');
        card.classList.remove('stop-failed');
        card.classList.add('quashing');
        setPill(card, 'Stopping');
        try {
            const res = await fetch('/Manage/Services/' + id + '/Stop', {
                method: 'POST',
                headers: {
                    'Accept': 'application/json',
                    'X-Requested-With': 'XMLHttpRequest',
                    'RequestVerificationToken': token
                }
            });
            const data = res.ok ? await res.json() : null;
            if (data && data.success) {
                // polling will flip it to Stopped and fire the offline pop
                return true;
            }
        } catch (_) { /* fall through to failure */ }
        card.classList.remove('quashing');
        card.classList.add('stop-failed');
        return false;
    }

    async function powerDown() {
        const targets = stoppable();
        if (targets.length === 0) {
            flash('info', 'Nothing to stop — no services are running.');
            return;
        }

        btn.disabled = true;
        btn.classList.add('stopping');
        let stopped = 0, failed = 0, processed = 0;
        renderHud(0, 0, targets.length);

        for (const card of targets) {
            const ok = await quash(card);
            if (ok) stopped++; else failed++;
            processed++;
            renderHud(stopped, processed, targets.length);
            if (STAGGER) await new Promise(r => setTimeout(r, STAGGER));
        }

        btn.disabled = false;
        btn.classList.remove('stopping');
        if (label) label.innerHTML = idleLabel;

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
