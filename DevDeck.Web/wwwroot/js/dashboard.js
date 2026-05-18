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

    function updatePill(el, value) {
        if (!el) return;
        if (el.textContent.trim() !== value) {
            el.textContent = value;
        }
        for (const c of Array.from(el.classList)) {
            if (c.startsWith('pill-')) el.classList.remove(c);
        }
        el.classList.add('pill-' + value.toLowerCase());
    }

    setInterval(tick, poll);
})();
