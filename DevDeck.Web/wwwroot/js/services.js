// Services index — a living management table.
// Polls /Manage/Status/Snapshot to keep runtime + health pills honest, and
// turns the inline Start/Stop/Restart forms into AJAX so the row updates in
// place (with the same ignite / online-pop language as the Dashboard) rather
// than reloading the whole page. Progressive enhancement: without JS the forms
// still post normally.
(function () {
    const root = document.getElementById('svc-table');
    if (!root) return;

    const poll = parseInt(root.getAttribute('data-poll'), 10) || 1500;
    const RUNNING_STATES = new Set(['running', 'starting', 'stopping']);

    function rowFor(id) { return root.querySelector(`.svc-row[data-service-id="${id}"]`); }
    function token(row) { return row.querySelector('input[name="__RequestVerificationToken"]')?.value || ''; }

    function setPill(row, field, value) {
        const el = row.querySelector(`[data-field="${field}"]`);
        if (!el) return null;
        const next = 'pill-' + value.toLowerCase();
        const cameOnline = next === 'pill-running' && !el.classList.contains('pill-running');
        if (el.textContent.trim() !== value) el.textContent = value;
        for (const c of Array.from(el.classList)) {
            if (c.startsWith('pill-')) el.classList.remove(c);
        }
        el.classList.add(next);
        if (cameOnline) {
            el.classList.remove('online-pop');
            void el.offsetWidth; // restart the animation
            el.classList.add('online-pop');
            el.addEventListener('animationend', () => el.classList.remove('online-pop'), { once: true });
            row.classList.remove('igniting');
        }
        return el;
    }

    // Show the action group that matches the current runtime state.
    function reconcileActions(row, runtimeStatus) {
        const isUp = RUNNING_STATES.has(runtimeStatus.toLowerCase());
        const runGroup = row.querySelector('.row-actions[data-when="running"]');
        const stopGroup = row.querySelector('.row-actions[data-when="stopped"]');
        if (runGroup) runGroup.hidden = !isUp;
        if (stopGroup) stopGroup.hidden = isUp;
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

    async function tick() {
        try {
            const res = await fetch('/Manage/Status/Snapshot', { headers: { 'Accept': 'application/json' } });
            if (!res.ok) return;
            const data = await res.json();
            for (const svc of data.services) {
                const row = rowFor(svc.id);
                if (!row) continue;
                setPill(row, 'runtime', svc.runtimeStatus);
                setPill(row, 'health', svc.healthStatus);
                reconcileActions(row, svc.runtimeStatus);
            }
        } catch (_) { /* swallow — transient */ }
    }

    // Intercept Start / Stop / Restart submits within rows.
    function actionOf(form) {
        const a = (form.getAttribute('action') || '').toLowerCase();
        if (a.endsWith('/start')) return 'start';
        if (a.endsWith('/restart')) return 'restart';
        if (a.endsWith('/stop')) return 'stop';
        return null;
    }

    async function runAction(form) {
        const row = form.closest('.svc-row');
        const action = actionOf(form);
        if (!row || !action) return false; // let it post normally

        const id = row.getAttribute('data-service-id');
        row.classList.remove('launch-failed');
        if (action === 'start' || action === 'restart') {
            row.classList.add('igniting');
            setPill(row, 'runtime', 'Starting');
            reconcileActions(row, 'starting');
        } else {
            setPill(row, 'runtime', 'Stopping');
        }

        try {
            const res = await fetch(form.getAttribute('action'), {
                method: 'POST',
                headers: {
                    'Accept': 'application/json',
                    'X-Requested-With': 'XMLHttpRequest',
                    'RequestVerificationToken': token(row)
                }
            });
            const data = res.ok ? await res.json() : null;
            if (data && data.success) {
                // Polling promotes Starting→Running and flips Stopping→Stopped.
                return true;
            }
            row.classList.remove('igniting');
            if (action !== 'stop') row.classList.add('launch-failed');
            flash('error', (data && data.message) || `Could not ${action} service.`);
        } catch (_) {
            row.classList.remove('igniting');
            if (action !== 'stop') row.classList.add('launch-failed');
            flash('error', `Could not ${action} service — request failed.`);
        }
        // Re-sync from the truth on the next tick.
        tick();
        return true;
    }

    root.addEventListener('submit', (e) => {
        const form = e.target.closest('form');
        if (!form || !form.closest('.svc-row') || !actionOf(form)) return;
        e.preventDefault();
        runAction(form);
    });

    setInterval(tick, poll);
})();
