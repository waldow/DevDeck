// Service create/edit form interactions.
// - Add env-var / health-check rows.
// - Remove a row by flagging it for deletion + hiding it (rows are never
//   removed from the DOM, so the [i] indices the model binder relies on stay
//   contiguous; the server skips rows whose Delete flag is set or whose
//   key/url is blank).
// - Reveal/mask a secret value.
// - Live "resolves to" preview for the URL field.
(function () {
    function addRow(tableId, columns) {
        const tbody = document.querySelector('#' + tableId + ' tbody');
        if (!tbody) return;
        const idx = tbody.children.length;
        const tr = document.createElement('tr');
        tr.className = 'repeat-row';
        tr.innerHTML = columns(idx);
        tbody.appendChild(tr);
    }

    document.getElementById('addEnv')?.addEventListener('click', () => {
        addRow('envvars', i => `
            <td><input name="EnvironmentVariables[${i}].Id" type="hidden" value="0" /><input name="EnvironmentVariables[${i}].Key" class="mono" /></td>
            <td class="cell-value">
                <input name="EnvironmentVariables[${i}].Value" class="mono js-value" type="text" autocomplete="off" />
                <button type="button" class="icon-btn reveal js-reveal" title="Show / hide value" aria-label="Show value">&#128065;</button>
            </td>
            <td><input name="EnvironmentVariables[${i}].IsSecret" type="checkbox" value="true" /><input name="EnvironmentVariables[${i}].IsSecret" type="hidden" value="false" /></td>
            <td class="cell-actions">
                <input name="EnvironmentVariables[${i}].Delete" type="hidden" value="false" class="js-delete-flag" />
                <button type="button" class="icon-btn danger js-remove" title="Remove variable" aria-label="Remove variable">&times;</button>
            </td>`);
    });

    document.getElementById('addHc')?.addEventListener('click', () => {
        addRow('healthchecks', i => `
            <td><input name="HealthChecks[${i}].Id" type="hidden" value="0" /><input name="HealthChecks[${i}].Url" class="mono" /></td>
            <td><input name="HealthChecks[${i}].ExpectedStatusCode" type="number" value="200" /></td>
            <td><input name="HealthChecks[${i}].IntervalSeconds" type="number" value="10" /></td>
            <td><input name="HealthChecks[${i}].Enabled" type="checkbox" value="true" checked /><input name="HealthChecks[${i}].Enabled" type="hidden" value="false" /></td>
            <td class="cell-actions">
                <input name="HealthChecks[${i}].Delete" type="hidden" value="false" class="js-delete-flag" />
                <button type="button" class="icon-btn danger js-remove" title="Remove health check" aria-label="Remove health check">&times;</button>
            </td>`);
    });

    // Delegated handlers for remove ✕ and reveal 👁 across both tables.
    document.addEventListener('click', (e) => {
        const remove = e.target.closest('.js-remove');
        if (remove) {
            const row = remove.closest('tr');
            if (!row) return;
            const flag = row.querySelector('.js-delete-flag');
            if (flag) flag.value = 'true';
            row.classList.add('row-removed');
            return;
        }
        const reveal = e.target.closest('.js-reveal');
        if (reveal) {
            const input = reveal.closest('.cell-value')?.querySelector('.js-value');
            if (!input) return;
            const show = input.type === 'password';
            input.type = show ? 'text' : 'password';
            reveal.classList.toggle('is-on', show);
            reveal.setAttribute('aria-label', show ? 'Hide value' : 'Show value');
        }
    });

    // Live URL preview: substitute {port} with the current Port value.
    const urlInput = document.getElementById('Url');
    const portInput = document.getElementById('Port');
    const preview = document.getElementById('urlPreview');
    function renderPreview() {
        if (!urlInput || !preview) return;
        const raw = (urlInput.value || '').trim();
        if (!raw) { preview.hidden = true; return; }
        const port = (portInput && portInput.value) ? portInput.value : '';
        const resolved = raw.replace(/\{port\}/g, port || '{port}');
        preview.hidden = false;
        preview.innerHTML = 'Resolves to: <b></b>';
        preview.querySelector('b').textContent = resolved;
    }
    urlInput?.addEventListener('input', renderPreview);
    portInput?.addEventListener('input', renderPreview);
    renderPreview();

    // Passthru mode: when "Use external instance" is on, DevDeck won't launch the process,
    // so dim the launch-only fields and surface the external port.
    const external = document.getElementById('UseExternalInstance');
    const externalPortRow = document.getElementById('ExternalPort')?.closest('.form-row');
    const externalPortLabel = document.querySelector('label[for="ExternalPort"]');
    const launchFieldIds = ['WorkingDirectory', 'StartCommand', 'StartArguments'];
    function reconcileExternal() {
        if (!external) return;
        const on = external.checked;
        if (externalPortRow) externalPortRow.style.display = on ? '' : 'none';
        if (externalPortLabel) externalPortLabel.style.display = on ? '' : 'none';
        for (const id of launchFieldIds) {
            const row = document.getElementById(id)?.closest('.form-row');
            if (row) row.classList.toggle('field-dimmed', on);
        }
    }
    external?.addEventListener('change', reconcileExternal);
    reconcileExternal();
})();
