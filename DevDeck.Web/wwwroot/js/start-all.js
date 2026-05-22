// Start all — progressive-enhancement launch cascade.
// Without JS the #start-all-form posts to Services/StartAll and the page reloads.
// With JS we intercept the submit and ignite enabled+stopped services one-by-one,
// lighting each card as it comes online. dashboard.js polling confirms Running.
(function () {
    const form = document.getElementById('start-all-form');
    const btn = document.getElementById('start-all-btn');
    const root = document.getElementById('cards');
    if (!form || !btn || !root) return;

    const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const STAGGER = reduce ? 0 : 380; // ms between ignitions
    const token = form.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    const label = btn.querySelector('.sa-label');
    const idleLabel = label ? label.innerHTML : '▶&nbsp;Start all';

    const NOT_STARTABLE = new Set(['running', 'starting', 'stopping']);

    function startable() {
        return Array.from(root.querySelectorAll('.service-card'))
            .filter(card => card.getAttribute('data-enabled') === 'true')
            .filter(card => {
                const pill = card.querySelector('[data-field="runtime"]');
                return pill && !NOT_STARTABLE.has(pill.textContent.trim().toLowerCase());
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

    function renderHud(successes, processed, total) {
        if (!label) return;
        const filled = '▰'.repeat(processed);
        const empty = '▱'.repeat(Math.max(0, total - processed));
        label.innerHTML = `◇&nbsp;Igniting&nbsp;<b>${successes}</b>/${total} <span class="sa-bar">${filled}${empty}</span>`;
    }

    async function ignite(card) {
        const id = card.getAttribute('data-service-id');
        card.classList.remove('launch-failed');
        card.classList.add('igniting');
        try {
            const res = await fetch('/Manage/Services/' + id + '/Start', {
                method: 'POST',
                headers: {
                    'Accept': 'application/json',
                    'X-Requested-With': 'XMLHttpRequest',
                    'RequestVerificationToken': token
                }
            });
            const data = res.ok ? await res.json() : null;
            if (data && data.success) {
                setPill(card, 'Starting'); // polling will flip it to Running
                return true;
            }
        } catch (_) { /* fall through to failure */ }
        card.classList.remove('igniting');
        card.classList.add('launch-failed');
        return false;
    }

    async function launch() {
        const targets = startable();
        if (targets.length === 0) {
            flash('info', 'Nothing to start — every enabled service is already running.');
            return;
        }

        btn.disabled = true;
        btn.classList.add('launching');
        let started = 0, failed = 0, processed = 0;
        renderHud(0, 0, targets.length);

        for (const card of targets) {
            const ok = await ignite(card);
            if (ok) started++; else failed++;
            processed++;
            renderHud(started, processed, targets.length);
            if (STAGGER) await new Promise(r => setTimeout(r, STAGGER));
        }

        btn.disabled = false;
        btn.classList.remove('launching');
        if (label) label.innerHTML = idleLabel;

        if (failed === 0) {
            flash('info', `✦ All systems go — ${started} ${started === 1 ? 'service' : 'services'} igniting.`);
        } else {
            flash('error', `${started} started, ${failed} failed to launch.`);
        }
    }

    form.addEventListener('submit', (e) => {
        e.preventDefault();
        launch();
    });
})();
