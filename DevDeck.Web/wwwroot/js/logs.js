(function () {
    const panel = document.getElementById('logpanel');
    if (!panel) return;
    const serviceId = panel.getAttribute('data-service-id');
    const runState = document.getElementById('runState');
    const auto = document.getElementById('autoscroll');
    const countEl = document.getElementById('lineCount');
    let seen = 0;

    function classFor(line) {
        if (line.includes('[ERR]')) return 'ERR';
        if (line.includes('[SYS]')) return 'SYS';
        return 'OUT';
    }

    async function tick() {
        try {
            const res = await fetch(`/Manage/Services/${serviceId}/LogsSnapshot?sinceCount=${seen}`);
            if (!res.ok) return;
            const data = await res.json();

            if (runState) {
                runState.textContent = data.isRunning ? 'Running' : 'Stopped';
                runState.classList.toggle('pill-running', data.isRunning);
                runState.classList.toggle('pill-stopped', !data.isRunning);
            }

            if (Array.isArray(data.lines) && data.lines.length > 0) {
                const frag = document.createDocumentFragment();
                for (const line of data.lines) {
                    const div = document.createElement('div');
                    div.className = 'log-line ' + classFor(line);
                    div.textContent = line;
                    frag.appendChild(div);
                }
                panel.appendChild(frag);
                seen = data.totalCount;
                if (countEl) countEl.textContent = `${seen} lines`;
                if (auto && auto.checked) panel.scrollTop = panel.scrollHeight;
            }
        } catch (_) { /* swallow */ }
    }

    tick();
    setInterval(tick, 1200);
})();
