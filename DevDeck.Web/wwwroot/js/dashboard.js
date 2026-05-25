(function () {
    const root = document.getElementById('cards');
    if (!root) return;
    const poll = parseInt(root.getAttribute('data-poll'), 10) || 1500;

    async function tick() {
        try {
            const res = await fetch('/Manage/Status/Snapshot', { headers: { 'Accept': 'application/json' } });
            if (!res.ok) return;
            const data = await res.json();
            for (const svc of data.services) {
                const card = root.querySelector(`[data-service-id="${svc.id}"]`);
                if (!card) continue;
                updatePill(card.querySelector('[data-field="runtime"]'), svc.runtimeStatus);
                updatePill(card.querySelector('[data-field="health"]'), svc.healthStatus);
            }
        } catch (_) { /* swallow */ }
    }

    const OFFLINE_STATES = new Set(['pill-stopped', 'pill-crashed', 'pill-killed', 'pill-failedtostart']);

    function updatePill(el, value) {
        if (!el) return;
        const next = 'pill-' + value.toLowerCase();
        const wasOffline = el.classList.contains('pill-stopped') || el.classList.contains('pill-crashed')
            || el.classList.contains('pill-killed') || el.classList.contains('pill-failedtostart');
        const cameOnline = next === 'pill-running' && !el.classList.contains('pill-running');
        const wentOffline = OFFLINE_STATES.has(next) && !wasOffline;
        if (el.textContent.trim() !== value) {
            el.textContent = value;
        }
        for (const c of Array.from(el.classList)) {
            if (c.startsWith('pill-')) el.classList.remove(c);
        }
        el.classList.add(next);
        if (cameOnline) {
            el.classList.remove('online-pop');
            void el.offsetWidth; // restart the animation
            el.classList.add('online-pop');
            el.addEventListener('animationend', () => el.classList.remove('online-pop'), { once: true });
            el.closest('.service-card')?.classList.remove('igniting');
        }
        if (wentOffline) {
            el.classList.remove('offline-pop');
            void el.offsetWidth; // restart the animation
            el.classList.add('offline-pop');
            el.addEventListener('animationend', () => el.classList.remove('offline-pop'), { once: true });
            el.closest('.service-card')?.classList.remove('quashing');
        }
    }

    setInterval(tick, poll);
})();
