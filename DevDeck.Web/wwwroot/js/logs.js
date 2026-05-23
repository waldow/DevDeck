(function () {
    const panel = document.getElementById('logpanel');
    if (!panel) return;
    const serviceId = panel.getAttribute('data-service-id');
    const runState = document.getElementById('runState');
    const auto = document.getElementById('autoscroll');
    const countEl = document.getElementById('lineCount');
    let seen = 0;

    const tokenRules = [
        { className: 'log-url', pattern: /^https?:\/\/[^\s\]]+/i, link: true },
        { className: 'log-level log-error-token', pattern: /^(?:error|errors|failed|failure|exception|critical|fatal)\b/i },
        { className: 'log-level log-warn-token', pattern: /^(?:warning|warnings|warn)\b/i },
        { className: 'log-level log-info-token', pattern: /^(?:info|debug|trace)\b/i },
        { className: 'log-method', pattern: /^(?:GET|POST|PUT|PATCH|DELETE|OPTIONS|HEAD)\b/ },
        { className: 'log-code', pattern: /^[A-Z]{2,}[0-9]{3,}\b/ },
        { className: 'log-version', pattern: /^\d+(?:\.\d+){1,}(?:[-+][0-9A-Za-z.-]+)?\b/ },
        { className: 'log-duration', pattern: /^\d{2}:\d{2}:\d{2}(?:\.\d+)?\b/ },
        { className: 'log-count', pattern: /^\d+\s+(?:Warning|Error)\(s\)\b/i },
        { className: 'log-quoted', pattern: /^'[^']*'|^"[^"]*"/ },
        { className: 'log-path', pattern: /^[A-Za-z]:\\[^\s:]+(?:\([0-9,]+\))?/ },
        { className: 'log-inner-ts', pattern: /^\[\d{4}-\d{2}-\d{2}T[^\]]+\]/ }
    ];
    const tokenPattern = /https?:\/\/[^\s\]]+|\b(?:error|errors|failed|failure|exception|critical|fatal)\b|\b(?:warning|warnings|warn)\b|\b(?:info|debug|trace)\b|\b(?:GET|POST|PUT|PATCH|DELETE|OPTIONS|HEAD)\b|\b[A-Z]{2,}[0-9]{3,}\b|\b\d+(?:\.\d+){1,}(?:[-+][0-9A-Za-z.-]+)?\b|\b\d{2}:\d{2}:\d{2}(?:\.\d+)?\b|\b\d+\s+(?:Warning|Error)\(s\)\b|'[^']*'|"[^"]*"|[A-Za-z]:\\[^\s:]+(?:\([0-9,]+\))?|\[\d{4}-\d{2}-\d{2}T[^\]]+\]/gi;

    function classFor(line) {
        if (line.includes('[ERR]')) return 'ERR';
        if (line.includes('[SYS]')) return 'SYS';
        return 'OUT';
    }

    function appendText(parent, value, className) {
        const span = document.createElement('span');
        if (className) span.className = className;
        span.textContent = value;
        parent.appendChild(span);
    }

    function appendToken(parent, value, rule) {
        if (!rule) {
            appendText(parent, value);
            return;
        }

        if (rule.link) {
            const link = document.createElement('a');
            link.className = rule.className;
            link.href = value;
            link.target = '_blank';
            link.rel = 'noopener noreferrer';
            link.textContent = value;
            parent.appendChild(link);
            return;
        }

        appendText(parent, value, rule.className);
    }

    function appendHighlightedText(parent, text) {
        let index = 0;
        tokenPattern.lastIndex = 0;
        let match;

        while ((match = tokenPattern.exec(text)) !== null) {
            if (match.index > index) {
                appendText(parent, text.slice(index, match.index));
            }

            const token = match[0];
            const rule = tokenRules.find(r => r.pattern.test(token));
            appendToken(parent, token, rule);
            index = match.index + token.length;
        }

        if (index < text.length) {
            appendText(parent, text.slice(index));
        }
    }

    function renderLine(line) {
        const div = document.createElement('div');
        div.className = 'log-line ' + classFor(line);

        const match = line.match(/^(\d{4}-\d{2}-\d{2}T\S+)\s+\[(OUT|ERR|SYS)\]\s?(.*)$/);
        if (!match) {
            appendHighlightedText(div, line);
            return div;
        }

        appendText(div, match[1], 'log-ts');
        appendText(div, ' ');
        appendText(div, `[${match[2]}]`, `log-stream log-stream-${match[2].toLowerCase()}`);
        if (match[3]) {
            appendText(div, ' ');
            appendHighlightedText(div, match[3]);
        }

        return div;
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
                    frag.appendChild(renderLine(line));
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
