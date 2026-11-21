// Chất lượng dữ liệu trên form nhập liệu:
//  - Tra cứu mã số thuế: nút [data-tax-lookup] cạnh ô MST → điền tên, địa chỉ, tỉnh/thành (ô đã có dữ liệu thì hỏi trước khi ghi đè).
//  - Cảnh báo trùng ngay khi nhập: form có data-dupcheck="Customer|Contact|Lead", các ô mang data-dc="name|taxCode|phone|email|company",
//    kết quả hiện trong #dupBox (chuỗi hiển thị lấy từ data-* của #dupBox do máy chủ dịch sẵn).
(function () {
    const form = document.querySelector('form[data-dupcheck]');
    const box = document.getElementById('dupBox');
    const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

    // ---------- Cảnh báo trùng ----------
    let timer = null, last = '';
    function fields() {
        const v = {};
        form.querySelectorAll('[data-dc]').forEach(el => { v[el.dataset.dc] = el.value.trim(); });
        return v;
    }
    async function check() {
        const v = fields();
        const q = new URLSearchParams({ entity: form.dataset.dupcheck, id: form.querySelector('[name=Id]')?.value || '0', ...v }).toString();
        if (q === last) return;
        last = q;
        if (!Object.values(v).some(x => x.length >= 3)) { box.innerHTML = ''; return; }
        let hits = [];
        try { const r = await fetch('/DataCheck/Duplicates?' + q, { headers: { 'Accept': 'application/json' } }); if (r.ok) hits = await r.json(); } catch { return; }
        render(hits);
    }
    function render(hits) {
        if (!hits.length) { box.innerHTML = ''; return; }
        const strong = hits.some(h => h.strong);
        const d = box.dataset;
        const rows = hits.map(h => `<li class="mb-1"><span class="badge text-bg-light border me-1">${esc(h.typeLabel)}</span>` +
            (h.url ? `<a href="${esc(h.url)}" target="_blank" rel="noopener">${esc(h.title)}</a>` : `<b>${esc(h.title)}</b>`) +
            (h.detail ? ` <span class="text-muted">(${esc(h.detail)})</span>` : '') +
            ` – <span class="${h.strong ? 'fw-semibold' : ''}">${esc(h.reason)}</span>` +
            (h.owner ? ` · <span class="text-muted">${esc(d.owner)} ${esc(h.owner)}</span>` : '') +
            (!h.url ? ` <span class="text-muted small">${esc(d.noaccess)}</span>` : '') + '</li>').join('');
        const confirmed = form.querySelector('[name=confirmDuplicate]')?.checked;
        box.innerHTML = `<div class="alert alert-${strong ? 'warning' : 'info'} py-2 mb-3"><div class="fw-semibold mb-1">${esc(strong ? d.title : d.similar)}</div>` +
            `<ul class="mb-1 ps-3 small">${rows}</ul>` +
            (strong ? `<div class="form-check small"><input class="form-check-input" type="checkbox" name="confirmDuplicate" value="true" id="confirmDup" ${confirmed ? 'checked' : ''} />` +
                `<label class="form-check-label" for="confirmDup">${esc(d.confirm)}</label></div>` : '') + '</div>';
    }
    if (form && box) {
        form.querySelectorAll('[data-dc]').forEach(el => {
            el.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(check, 600); });
            el.addEventListener('change', () => { clearTimeout(timer); check(); });
        });
        // Máy chủ trả về danh sách trùng khi chặn lưu → hiện lại ngay
        if (box.dataset.initial) { try { render(JSON.parse(box.dataset.initial)); } catch { } }
    }

    // ---------- Tra cứu mã số thuế ----------
    document.querySelectorAll('[data-tax-lookup]').forEach(btn => {
        btn.addEventListener('click', async () => {
            const scope = btn.closest('form') || document;
            const input = scope.querySelector(btn.dataset.taxLookup);
            const status = scope.querySelector(btn.dataset.taxStatus || '#taxStatus');
            const code = (input?.value || '').trim();
            const t = btn.dataset;
            if (!code) { CrmDialog.alert(t.empty); return; }
            btn.disabled = true;
            const label = btn.innerHTML; btn.innerHTML = `<span class="spinner-border spinner-border-sm"></span>`;
            let res;
            try { const r = await fetch('/DataCheck/Tax?code=' + encodeURIComponent(code)); res = await r.json(); }
            catch { res = { ok: false, error: t.fail }; }
            finally { btn.disabled = false; btn.innerHTML = label; }
            if (!res.ok) { if (status) status.innerHTML = ''; CrmDialog.alert(res.error || t.fail); return; }
            input.value = res.taxCode;
            const targets = [
                [btn.dataset.fillName, res.name, t.lname],
                [btn.dataset.fillAddress, res.address, t.laddress]
            ];
            const conflicts = targets.filter(([sel, val]) => sel && val && scope.querySelector(sel)?.value.trim() && scope.querySelector(sel).value.trim() !== val);
            let overwrite = true;
            if (conflicts.length) {
                overwrite = await CrmDialog.confirm(t.overwrite + '\n' + conflicts.map(([, val, lbl]) => `${lbl}: ${val}`).join('\n'), { ok: t.yes, cancel: t.keep });
            }
            for (const [sel, val] of targets) {
                const el = sel && scope.querySelector(sel);
                if (!el || !val) continue;
                if (!el.value.trim() || overwrite) { el.value = val; el.dispatchEvent(new Event('change', { bubbles: true })); }
            }
            const city = btn.dataset.fillCity && scope.querySelector(btn.dataset.fillCity);
            if (city && res.cityCode && (!city.value || overwrite) && [...city.options].some(o => o.value === res.cityCode)) city.value = res.cityCode;
            if (status) {
                status.innerHTML = `<span class="badge ${res.active ? 'text-bg-success' : 'text-bg-danger'}">${esc(res.status || (res.active ? t.active : ''))}</span>` +
                    (res.shortName || res.internationalName ? ` <span class="text-muted">${esc(res.internationalName || res.shortName)}</span>` : '');
            }
            if (!res.active) CrmDialog.alert(t.inactive.replace('{0}', res.status || ''), { variant: 'warning' });
            input.dispatchEvent(new Event('change', { bubbles: true }));
        });
    });
})();
