(function () {
    const panel = document.getElementById('logpanel');
    if (!panel) return;
    const serviceId = panel.getAttribute('data-service-id');
    const runState = document.getElementById('runState');
    const auto = document.getElementById('autoscroll');
    const countEl = document.getElementById('lineCount');
    let seen = 0;

    // One source of truth: each rule carries the boundaried pattern used to detect *and*
    // classify a token. A sticky clone of each (built below) drives a single left-to-right
    // scan in appendHighlightedText, so detection and styling can never drift apart, and
    // each rule keeps its own case-sensitivity (e.g. HTTP verbs stay uppercase-only, so
    // prose words like "delete"/"head" are left as plain text).
    const tokenRules = [
        { className: 'log-url', match: /https?:\/\/[^\s\]]+/i, link: true },
        { className: 'log-level log-error-token', match: /\b(?:error|errors|failed|failure|exception|critical|fatal)\b/i },
        { className: 'log-level log-warn-token', match: /\b(?:warning|warnings|warn)\b/i },
        { className: 'log-level log-info-token', match: /\b(?:info|debug|trace)\b/i },
        { className: 'log-method', match: /\b(?:GET|POST|PUT|PATCH|DELETE|OPTIONS|HEAD)\b/ },
        { className: 'log-code', match: /\b[A-Z]{2,}[0-9]{3,}\b/ },
        // Consume IPv4[:port] as one plain token (no class) so addresses like 127.0.0.1
        // aren't tinted as versions — it must precede log-version so the scan skips past
        // the whole address rather than re-matching a suffix (e.g. "0.0.1") as a version.
        { match: /\b\d{1,3}(?:\.\d{1,3}){3}\b(?::\d+)?/ },
        { className: 'log-version', match: /\b\d+(?:\.\d+){1,}(?:[-+][0-9A-Za-z.-]+)?\b/ },
        { className: 'log-duration', match: /\b\d{2}:\d{2}:\d{2}(?:\.\d+)?\b/ },
        { className: 'log-count', match: /\b\d+\s+(?:Warning|Error)\(s\)/i },
        { className: 'log-quoted', match: /'[^']*'|"[^"]*"/ },
        { className: 'log-path', match: /[A-Za-z]:\\[^\s:]+(?:\([0-9,]+\))?/ },
        { className: 'log-inner-ts', match: /\[\d{4}-\d{2}-\d{2}T[^\]]+\]/ }
    ];
    for (const rule of tokenRules) {
        rule.sticky = new RegExp(rule.match.source, rule.match.flags + 'y');
    }

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
        let pos = 0;
        let plainStart = 0;

        while (pos < text.length) {
            let hitRule = null;
            let hitLen = 0;
            for (const rule of tokenRules) {
                rule.sticky.lastIndex = pos;
                const m = rule.sticky.exec(text);
                if (m && m[0].length > 0) {
                    hitRule = rule;
                    hitLen = m[0].length;
                    break;
                }
            }

            if (hitRule) {
                if (pos > plainStart) {
                    appendText(parent, text.slice(plainStart, pos));
                }
                appendToken(parent, text.slice(pos, pos + hitLen), hitRule);
                pos += hitLen;
                plainStart = pos;
            } else {
                pos++;
            }
        }

        if (plainStart < text.length) {
            appendText(parent, text.slice(plainStart));
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
