// ---------- Hộp thoại của ứng dụng (thay confirm / alert / prompt của trình duyệt) ----------
// CrmDialog.confirm(msg, opts) → Promise<boolean>; CrmDialog.alert(msg, opts) → Promise; CrmDialog.prompt(msg, opts) → Promise<string|null>
// opts: { title, ok, cancel, danger, variant: 'info'|'warning'|'danger'|'success', placeholder, value, required, multiline }
window.CrmDialog = (function () {
    const en = () => document.documentElement.lang === 'en';
    const L = {
        confirm: () => en() ? 'Confirm' : 'Xác nhận', notice: () => en() ? 'Notice' : 'Thông báo', input: () => en() ? 'Enter information' : 'Nhập thông tin',
        ok: () => en() ? 'OK' : 'Đồng ý', cancel: () => en() ? 'Cancel' : 'Hủy', close: () => en() ? 'Close' : 'Đóng', required: () => en() ? 'Please enter a value.' : 'Vui lòng nhập nội dung.'
    };
    const icons = {
        confirm: '<circle cx="12" cy="12" r="10"/><path d="M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3M12 17h.01"/>',
        danger: '<path d="M21.73 18l-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3"/><path d="M12 9v4M12 17h.01"/>',
        info: '<circle cx="12" cy="12" r="10"/><path d="M12 16v-4M12 8h.01"/>',
        success: '<circle cx="12" cy="12" r="10"/><path d="m9 12 2 2 4-4"/>'
    };
    let el, modal, state = null;
    function build() {
        if (el) return;
        el = document.createElement('div');
        el.className = 'modal fade crm-dialog';
        el.tabIndex = -1;
        el.setAttribute('aria-hidden', 'true');
        el.innerHTML = '<div class="modal-dialog modal-dialog-centered"><div class="modal-content">'
            + '<div class="modal-body d-flex gap-3"><div class="crm-dialog-ico"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"></svg></div>'
            + '<div class="flex-grow-1 min-w-0"><h5 class="crm-dialog-title mb-1"></h5><div class="crm-dialog-msg"></div>'
            + '<div class="crm-dialog-field mt-2"></div><div class="crm-dialog-err text-danger small mt-1 d-none"></div></div></div>'
            + '<div class="modal-footer"><button type="button" class="btn btn-light crm-dialog-cancel"></button><button type="button" class="btn crm-dialog-ok"></button></div>'
            + '</div></div>';
        document.body.appendChild(el);
        modal = new bootstrap.Modal(el, { backdrop: 'static' });
        el.querySelector('.crm-dialog-ok').addEventListener('click', () => finish(true));
        el.querySelector('.crm-dialog-cancel').addEventListener('click', () => finish(false));
        el.addEventListener('keydown', e => {
            if (e.key === 'Escape') { e.preventDefault(); finish(false); }
            else if (e.key === 'Enter' && !(e.target.tagName === 'TEXTAREA' && !e.ctrlKey) && !e.target.classList.contains('crm-dialog-cancel')) { e.preventDefault(); finish(true); }
        });
        el.addEventListener('shown.bs.modal', () => {
            const f = el.querySelector('.crm-dialog-field input, .crm-dialog-field textarea');
            (f || el.querySelector('.crm-dialog-ok')).focus();
        });
        el.addEventListener('hidden.bs.modal', () => {
            const s = state; state = null;
            if (s) s.resolve(s.result);
            if (queue.length) open(queue.shift());
        });
    }
    function finish(ok) {
        if (!state || state.done) return;
        const input = el.querySelector('.crm-dialog-field input, .crm-dialog-field textarea');
        if (ok && input && state.required && !input.value.trim()) {
            const err = el.querySelector('.crm-dialog-err'); err.textContent = L.required(); err.classList.remove('d-none'); input.focus(); return;
        }
        state.done = true;
        state.result = state.kind === 'prompt' ? (ok ? input.value.trim() : null) : state.kind === 'alert' ? undefined : ok;
        modal.hide();
    }
    const queue = [];
    function open(req) {
        build();
        if (state) { queue.push(req); return; }
        const o = req.opts || {};
        const variant = o.danger ? 'danger' : o.variant || (req.kind === 'confirm' ? 'confirm' : req.kind === 'prompt' ? 'confirm' : 'info');
        state = { kind: req.kind, resolve: req.resolve, required: !!o.required, done: false };
        el.dataset.variant = variant;
        el.querySelector('svg').innerHTML = icons[variant === 'warning' ? 'danger' : variant] || icons.info;
        el.querySelector('.crm-dialog-title').textContent = o.title || (req.kind === 'alert' ? L.notice() : req.kind === 'prompt' ? L.input() : L.confirm());
        el.querySelector('.crm-dialog-msg').textContent = req.msg || '';
        const field = el.querySelector('.crm-dialog-field');
        field.innerHTML = '';
        if (req.kind === 'prompt') {
            const inp = document.createElement(o.multiline ? 'textarea' : 'input');
            inp.className = 'form-control'; if (o.multiline) inp.rows = 3;
            inp.placeholder = o.placeholder || ''; inp.value = o.value || ''; inp.maxLength = o.maxLength || 500;
            field.appendChild(inp);
        }
        el.querySelector('.crm-dialog-err').classList.add('d-none');
        const okBtn = el.querySelector('.crm-dialog-ok'), cancelBtn = el.querySelector('.crm-dialog-cancel');
        okBtn.textContent = o.ok || (req.kind === 'alert' ? L.close() : L.ok());
        okBtn.className = 'btn crm-dialog-ok ' + (o.danger ? 'btn-danger' : 'btn-primary');
        cancelBtn.textContent = o.cancel || L.cancel();
        cancelBtn.classList.toggle('d-none', req.kind === 'alert');
        modal.show();
    }
    const ask = kind => (msg, opts) => new Promise(resolve => open({ kind, msg, opts, resolve }));
    return { confirm: ask('confirm'), alert: ask('alert'), prompt: ask('prompt') };
})();

// Xác nhận trước các thao tác quan trọng: <form data-confirm="..."> hoặc nút submit <button data-confirm-click="...">.
// Bắt ở pha capture để chặn trước các xử lý submit khác của form; người dùng đồng ý → gửi lại form với đúng nút đã bấm.
document.addEventListener('submit', function (e) {
    const form = e.target;
    if (form.dataset.confirmed === '1') { delete form.dataset.confirmed; return; }
    const btn = e.submitter;
    const msg = (btn && btn.getAttribute('data-confirm-click')) || form.getAttribute('data-confirm');
    if (!msg) return;
    e.preventDefault();
    e.stopPropagation();
    const danger = !!((btn && /\bbtn-(outline-)?danger\b/.test(btn.className)) || form.querySelector('.btn-danger, .btn-outline-danger') && !btn);
    CrmDialog.confirm(msg, { danger }).then(ok => {
        if (!ok) return;
        form.dataset.confirmed = '1';
        if (btn && form.requestSubmit) form.requestSubmit(btn);
        else if (form.requestSubmit) form.requestSubmit();
        else form.submit();
    });
}, true);

// Dropdown phụ thuộc: chọn khách hàng → nạp lại danh sách người liên hệ
// <select data-contacts-for="#ContactId"> trên ô khách hàng
document.addEventListener('change', async function (e) {
    const target = e.target.getAttribute && e.target.getAttribute('data-contacts-for');
    if (!target) return;
    const sel = document.querySelector(target);
    if (!sel) return;
    sel.innerHTML = '<option value="">' + (document.documentElement.lang === 'en' ? '-- Select --' : '-- Chọn --') + '</option>';
    if (!e.target.value) return;
    const res = await fetch('/Contacts/ByCustomer?customerId=' + encodeURIComponent(e.target.value));
    const items = await res.json();
    items.forEach(function (c) { const o = document.createElement('option'); o.value = c.id; o.textContent = c.name; sel.appendChild(o); });
    if (items.length > 0) sel.value = items[0].id;
});

// ---------- Menu bên trái: rút gọn (chỉ icon) + mở rộng / thu gọn từng nhóm ----------
(function () {
    const root = document.documentElement;
    const nav = document.getElementById('sideNav');
    if (!nav) return;
    const store = {
        get(k) { try { return localStorage.getItem(k); } catch (e) { return null; } },
        set(k, v) { try { localStorage.setItem(k, v); } catch (e) { } }
    };
    const mobile = () => window.matchMedia('(max-width: 900px)').matches;

    // Tooltip tên chức năng khi menu rút gọn
    nav.querySelectorAll('a').forEach(a => { const s = a.querySelector('span'); if (s && !a.title) a.title = s.textContent.trim(); });

    function saveCollapsed() {
        store.set('crm.nav.collapsed', JSON.stringify(
            Array.from(nav.querySelectorAll('.nav-block.collapsed')).map(b => b.dataset.nav)));
    }
    function setBlock(block, collapsed) {
        block.classList.toggle('collapsed', collapsed);
        block.querySelector(':scope > .nav-toggle').setAttribute('aria-expanded', String(!collapsed));
    }

    // (2) Mở rộng / thu gọn từng nhóm
    // Menu nằm ngang (trên / dưới): mỗi nhóm là menu thả xuống, bấm để mở / đóng (rê chuột cũng mở)
    const horizontal = () => !mobile() && (root.dataset.menu === 'top' || root.dataset.menu === 'bottom');
    document.addEventListener('click', e => {
        if (horizontal() && !e.target.closest('.hnav-item')) nav.querySelectorAll('.hnav-item.open').forEach(x => x.classList.remove('open'));
    });

    nav.addEventListener('click', e => {
        const btn = e.target.closest('.nav-toggle');
        if (btn && horizontal()) {
            const item = btn.closest('.hnav-item');
            if (item) {
                const wasOpen = item.classList.contains('open');
                nav.querySelectorAll('.hnav-item.open').forEach(x => x.classList.remove('open'));
                if (!wasOpen) item.classList.add('open');
            }
            return;
        }
        if (!btn || root.classList.contains('sidebar-mini') && !mobile()) return;
        const block = btn.parentElement;
        setBlock(block, !block.classList.contains('collapsed'));
        saveCollapsed();
    });
    document.querySelectorAll('[data-nav-all]').forEach(b => b.addEventListener('click', () => {
        const collapse = b.dataset.navAll === 'collapse';
        nav.querySelectorAll('.nav-block').forEach(block => {
            // Khi thu gọn tất cả vẫn giữ mở nhóm chứa trang đang xem
            setBlock(block, collapse && !block.querySelector('a.active'));
        });
        saveCollapsed();
    }));

    // (1) Rút gọn menu (desktop) / trượt menu ra (mobile)
    const miniBtn = document.querySelector('.side-mini-btn');
    function syncMiniLabel() {
        if (!miniBtn) return;
        const label = root.classList.contains('sidebar-mini') ? miniBtn.dataset.labelExpand : miniBtn.dataset.labelCollapse;
        miniBtn.title = label;
        miniBtn.querySelector('span').textContent = label;
    }
    document.querySelectorAll('[data-sidebar-toggle]').forEach(b => b.addEventListener('click', () => {
        if (mobile() && !b.classList.contains('side-mini-btn')) {
            root.classList.toggle('sidebar-open');
            return;
        }
        const mini = root.classList.toggle('sidebar-mini');
        store.set('crm.nav.mini', mini ? '1' : '0');
        syncMiniLabel();
    }));
    document.querySelectorAll('[data-sidebar-close]').forEach(b => b.addEventListener('click', () => root.classList.remove('sidebar-open')));
    document.addEventListener('keydown', e => { if (e.key === 'Escape') root.classList.remove('sidebar-open'); });
    syncMiniLabel();
})();

// ---------- Bảng nhiều dòng dùng chung (hợp đồng, hoá đơn) ----------
// <tbody data-rows="Lines"> chứa các <tr class="row-item">, ô nhập có data-f="TênTrường".
// Nút thêm: <button data-add-row="#idTbody">. Nút xóa trong dòng: .remove.
// Chọn hàng hoá: <select class="product"> (danh sách từ window.crmProducts) → tự điền Description/Unit/UnitPrice.
// Khi lưu: đặt name = Lines[i].Field liên tục. Mỗi thay đổi phát sự kiện 'rows:changed' để trang tự tính lại.
(function () {
    const fire = () => document.dispatchEvent(new CustomEvent('rows:changed'));
    const fillProducts = sel => {
        const list = window.crmProducts || [];
        const cur = sel.getAttribute('data-value') || sel.value;
        const blank = sel.getAttribute('data-blank') || '--';
        sel.innerHTML = '<option value="">' + blank + '</option>' +
            list.map(p => '<option value="' + p.id + '">' + p.code + ' – ' + p.name.replace(/</g, '&lt;') + '</option>').join('');
        if (cur) sel.value = cur;
    };
    const renumber = tbody => tbody.querySelectorAll('tr.row-item').forEach((tr, i) => { const c = tr.querySelector('.idx'); if (c) c.textContent = i + 1; });

    document.querySelectorAll('tbody[data-rows] select.product').forEach(fillProducts);

    document.addEventListener('click', e => {
        const add = e.target.closest('[data-add-row]');
        if (add) {
            const tbody = document.querySelector(add.getAttribute('data-add-row'));
            const tpl = tbody && tbody.querySelector('tr.row-item');
            if (!tpl) return;
            const tr = tpl.cloneNode(true);
            tr.querySelectorAll('input, textarea').forEach(i => i.value = i.hasAttribute('data-default') ? i.getAttribute('data-default') : '');
            tr.querySelectorAll('select').forEach(s => { s.removeAttribute('data-value'); if (s.classList.contains('product')) fillProducts(s); s.value = ''; });
            tbody.appendChild(tr);
            renumber(tbody); fire();
            const first = tr.querySelector('select, input'); if (first) first.focus();
            return;
        }
        const rm = e.target.closest('tbody[data-rows] .remove');
        if (rm) {
            const tbody = rm.closest('tbody'), tr = rm.closest('tr');
            if (tbody.querySelectorAll('tr.row-item').length > 1) tr.remove();
            else tr.querySelectorAll('input, select, textarea').forEach(i => i.value = i.hasAttribute('data-default') ? i.getAttribute('data-default') : '');
            renumber(tbody); fire();
        }
    });

    document.addEventListener('change', e => {
        if (!e.target.matches('tbody[data-rows] select.product')) return;
        const p = (window.crmProducts || []).find(x => String(x.id) === e.target.value);
        if (!p) return;
        const tr = e.target.closest('tr'), set = (f, v) => { const el = tr.querySelector('[data-f=' + f + ']'); if (el && v != null) el.value = v; };
        set('Description', p.name); set('Unit', p.unit); set('UnitPrice', p.price);
        fire();
    });
    document.addEventListener('input', e => { if (e.target.closest('tbody[data-rows]')) fire(); });

    document.addEventListener('submit', e => {
        e.target.querySelectorAll('tbody[data-rows]').forEach(tbody => {
            const name = tbody.getAttribute('data-rows');
            tbody.querySelectorAll('tr.row-item').forEach((tr, i) =>
                tr.querySelectorAll('[data-f]').forEach(el => el.name = name + '[' + i + '].' + el.getAttribute('data-f')));
        });
    }, true);

    window.crmFmt = n => (Math.round(n) || 0).toLocaleString('vi-VN');
    window.crmNum = el => parseFloat(el && el.value ? el.value : '0') || 0;
})();

// ---------- Lưới dữ liệu: kéo thả đổi thứ tự cột + kéo mép để đổi độ rộng ----------
// Tự áp dụng cho các bảng danh sách (table.table-hover hoặc table[data-grid]); bỏ qua bảng có data-no-grid.
// Bố cục lưu theo trình duyệt (localStorage), khóa = trang + vị trí bảng + số cột.
(function () {
    const en = document.documentElement.lang === 'en';
    const TXT = {
        reset: en ? 'Reset column layout' : 'Khôi phục bố cục cột',
        columns: en ? 'Show / hide columns' : 'Ẩn / hiện cột',
        all: en ? 'Show all' : 'Hiện tất cả',
        keepOne: en ? 'At least one column must stay visible' : 'Phải giữ lại ít nhất một cột',
        hint: en ? 'Drag to move the column · drag the right edge to resize' : 'Kéo để đổi vị trí cột · kéo mép phải để đổi độ rộng'
    };
    const store = {
        get(k) { try { return JSON.parse(localStorage.getItem(k) || 'null'); } catch (e) { return null; } },
        set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch (e) { } },
        del(k) { try { localStorage.removeItem(k); } catch (e) { } }
    };
    const page = document.body.dataset.page || location.pathname;
    const MIN_W = 48;

    document.querySelectorAll('table.table-hover, table[data-grid]').forEach((table, gi) => {
        const head = table.tHead && table.tHead.rows[0];
        if (!head || table.hasAttribute('data-no-grid')) return;
        const ths = Array.from(head.cells);
        const n = ths.length;
        if (n < 2 || ths.some(th => th.colSpan > 1)) return;
        // data-grid="khóa riêng" khi nhiều nội dung khác nhau dùng chung một trang (vd. các báo cáo)
        const key = 'crm.grid.' + (table.dataset.grid || page) + '.' + gi + '.' + n;
        ths.forEach((th, i) => { th.dataset.col = i; });
        table.classList.add('grid');
        if (table.parentElement) table.parentElement.classList.add('grid-scroll');

        const cols = () => Array.from(head.cells);
        // Chỉ di chuyển các dòng đủ ô (bỏ qua dòng thông báo "không có dữ liệu" dùng colspan)
        const rows = () => Array.from(table.rows).filter(r => r.cells.length === n && !Array.from(r.cells).some(c => c.colSpan > 1));

        function move(from, to) {
            if (from === to) return;
            rows().forEach(r => {
                const cell = r.cells[from], ref = r.cells[to];
                if (from < to) ref.after(cell); else ref.before(cell);
            });
        }
        // Cố định độ rộng hiện tại của các cột (chuyển sang table-layout: fixed)
        function freeze() {
            if (table.classList.contains('grid-fixed')) return;
            const ws = cols().map(th => th.getBoundingClientRect().width);
            cols().forEach((th, i) => th.style.width = Math.max(MIN_W, Math.round(ws[i])) + 'px');
            table.classList.add('grid-fixed');
            syncWidth();
        }
        function syncWidth() {
            table.style.width = cols().filter(th => !hidden.has(+th.dataset.col)).reduce((s, th) => s + parseFloat(th.style.width || th.offsetWidth), 0) + 'px';
        }

        // Nút khôi phục bố cục (hiện khi đã tùy chỉnh)
        const host = table.closest('.card') || table.parentElement;
        host.classList.add('grid-host');

        // Cột đang ẩn (theo số thứ tự cột gốc)
        const hidden = new Set();
        function applyHidden() {
            const list = cols(), rs = rows();
            list.forEach((th, idx) => {
                const h = hidden.has(+th.dataset.col);
                rs.forEach(r => { const c = r.cells[idx]; if (c) c.classList.toggle('grid-col-hidden', h); });
            });
            if (table.classList.contains('grid-fixed')) syncWidth();
            host.classList.toggle('grid-has-hidden', hidden.size > 0);
        }
        const reset = document.createElement('button');
        reset.type = 'button';
        reset.className = 'grid-reset no-print';
        reset.title = TXT.reset;
        reset.setAttribute('aria-label', TXT.reset);
        reset.innerHTML = '<svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"/><path d="M3 3v5h5"/></svg>';
        reset.addEventListener('click', () => { store.del(key); location.reload(); });
        host.appendChild(reset);

        // Nút chọn cột hiển thị
        const colBtn = document.createElement('button');
        colBtn.type = 'button';
        colBtn.className = 'grid-cols-btn no-print';
        colBtn.title = TXT.columns;
        colBtn.setAttribute('aria-label', TXT.columns);
        colBtn.innerHTML = '<svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="4" width="18" height="16" rx="2"/><path d="M9 4v16"/><path d="M15 4v16"/></svg>';
        const panel = document.createElement('div');
        panel.className = 'grid-cols-panel no-print';
        panel.hidden = true;
        function buildPanel() {
            panel.innerHTML = '';
            const title = document.createElement('div');
            title.className = 'gcp-title';
            title.textContent = TXT.columns;
            panel.appendChild(title);
            ths.slice().sort((a, b) => +a.dataset.col - +b.dataset.col).forEach(th => {
                const id = +th.dataset.col;
                const row = document.createElement('label');
                const cb = document.createElement('input');
                cb.type = 'checkbox'; cb.className = 'form-check-input'; cb.checked = !hidden.has(id);
                const span = document.createElement('span');
                span.textContent = (th.innerText || '').trim() || ('#' + (id + 1));
                row.append(cb, span);
                cb.addEventListener('change', () => {
                    if (!cb.checked && hidden.size >= n - 1) { cb.checked = true; CrmDialog.alert(TXT.keepOne, { variant: "warning" }); return; }
                    if (cb.checked) hidden.delete(id); else hidden.add(id);
                    applyHidden(); save();
                });
                panel.appendChild(row);
            });
            const all = document.createElement('button');
            all.type = 'button'; all.className = 'btn btn-sm btn-link p-0 mt-1'; all.textContent = TXT.all;
            all.addEventListener('click', () => { hidden.clear(); applyHidden(); save(); buildPanel(); });
            panel.appendChild(all);
        }
        colBtn.addEventListener('click', e => {
            e.stopPropagation();
            if (panel.hidden) buildPanel();
            panel.hidden = !panel.hidden;
        });
        panel.addEventListener('click', e => e.stopPropagation());
        document.addEventListener('click', () => { panel.hidden = true; });
        host.appendChild(colBtn);
        host.appendChild(panel);

        function save() {
            const state = { order: cols().map(th => +th.dataset.col), hidden: Array.from(hidden) };
            if (table.classList.contains('grid-fixed')) {
                state.widths = {};
                cols().forEach(th => state.widths[th.dataset.col] = parseFloat(th.style.width));
            }
            store.set(key, state);
            host.classList.add('grid-custom');
        }

        // Khôi phục bố cục đã lưu
        const saved = store.get(key);
        if (saved && Array.isArray(saved.order) && saved.order.length === n) {
            saved.order.forEach((id, pos) => {
                const cur = cols().findIndex(th => +th.dataset.col === id);
                if (cur >= 0) move(cur, pos);
            });
            if (saved.widths) {
                cols().forEach(th => { const w = saved.widths[th.dataset.col]; if (w) th.style.width = Math.max(MIN_W, w) + 'px'; });
                table.classList.add('grid-fixed');
                syncWidth();
            }
            if (Array.isArray(saved.hidden)) saved.hidden.filter(id => id >= 0 && id < n).slice(0, n - 1).forEach(id => hidden.add(id));
            applyHidden();
            host.classList.add('grid-custom');
        }

        // Ô bị cắt (…) → hiện nội dung đầy đủ khi rê chuột
        table.addEventListener('mouseover', e => {
            const td = e.target.closest('td');
            if (td && table.classList.contains('grid-fixed') && !td.title && td.scrollWidth > td.clientWidth) td.title = td.innerText.trim();
        });

        let resizing = false, dragId = null;
        ths.forEach(th => {
            if (!th.title) th.title = TXT.hint;
            th.draggable = true;

            // --- Đổi độ rộng: kéo tay nắm ở mép phải tiêu đề ---
            const grip = document.createElement('span');
            grip.className = 'col-resizer';
            grip.draggable = false;
            th.appendChild(grip);
            grip.addEventListener('pointerdown', e => {
                e.preventDefault(); e.stopPropagation();
                freeze();
                resizing = true;
                const startX = e.clientX, startW = parseFloat(th.style.width);
                try { grip.setPointerCapture(e.pointerId); } catch (err) { /* thiết bị không hỗ trợ capture */ }
                table.classList.add('grid-resizing');
                const onMove = ev => { th.style.width = Math.max(MIN_W, startW + ev.clientX - startX) + 'px'; syncWidth(); };
                const onUp = () => {
                    grip.removeEventListener('pointermove', onMove);
                    grip.removeEventListener('pointerup', onUp);
                    grip.removeEventListener('pointercancel', onUp);
                    table.classList.remove('grid-resizing');
                    setTimeout(() => { resizing = false; });
                    save();
                };
                grip.addEventListener('pointermove', onMove);
                grip.addEventListener('pointerup', onUp);
                grip.addEventListener('pointercancel', onUp);
            });
            grip.addEventListener('click', e => e.stopPropagation());

            // --- Đổi thứ tự: kéo thả tiêu đề cột ---
            th.addEventListener('dragstart', e => {
                if (resizing) { e.preventDefault(); return; }
                dragId = th.dataset.col;
                e.dataTransfer.effectAllowed = 'move';
                e.dataTransfer.setData('text/plain', dragId);
                th.classList.add('grid-dragging');
            });
            th.addEventListener('dragend', () => {
                dragId = null;
                cols().forEach(c => c.classList.remove('grid-dragging', 'drop-before', 'drop-after'));
            });
            th.addEventListener('dragover', e => {
                if (dragId === null || th.dataset.col === dragId) return;
                e.preventDefault();
                e.dataTransfer.dropEffect = 'move';
                const r = th.getBoundingClientRect(), after = e.clientX > r.left + r.width / 2;
                th.classList.toggle('drop-after', after);
                th.classList.toggle('drop-before', !after);
            });
            th.addEventListener('dragleave', () => th.classList.remove('drop-before', 'drop-after'));
            th.addEventListener('drop', e => {
                if (dragId === null) return;
                e.preventDefault();
                const after = th.classList.contains('drop-after');
                th.classList.remove('drop-before', 'drop-after');
                const list = cols();
                const from = list.findIndex(c => c.dataset.col === dragId);
                let to = list.indexOf(th);
                if (from < 0 || to < 0 || from === to) return;
                // Vị trí đích sau khi rút cột ra khỏi vị trí cũ
                if (after && from > to) to++;
                if (!after && from < to) to--;
                move(from, to);
                save();
            });
        });
    });
})();

// ---------- File đính kèm: kéo thả / chọn file, hiện danh sách đã chọn, kiểm tra dung lượng trước khi gửi ----------
(function () {
    const en = document.documentElement.lang === 'en';
    const fmt = b => b >= 1048576 ? (b / 1048576).toFixed(1) + ' MB' : Math.round(b / 1024) + ' KB';
    document.querySelectorAll('form.att-upload').forEach(form => {
        const input = form.querySelector('input[type=file]'), drop = form.querySelector('.att-drop'),
              text = form.querySelector('.att-drop-text'), btn = form.querySelector('button'), original = text.textContent;
        const max = parseInt(input.dataset.max || '0', 10);
        function refresh() {
            const files = Array.from(input.files || []);
            const big = files.filter(f => max && f.size > max);
            if (big.length) {
                text.textContent = (en ? 'Too large: ' : 'Vượt dung lượng: ') + big.map(f => f.name + ' (' + fmt(f.size) + ')').join(', ');
                drop.classList.add('att-error'); btn.disabled = true; return;
            }
            drop.classList.remove('att-error');
            text.textContent = files.length ? files.map(f => f.name + ' (' + fmt(f.size) + ')').join(', ') : original;
            btn.disabled = files.length === 0;
        }
        input.addEventListener('change', refresh);
        ['dragenter', 'dragover'].forEach(ev => drop.addEventListener(ev, e => { e.preventDefault(); drop.classList.add('att-over'); }));
        ['dragleave', 'drop'].forEach(ev => drop.addEventListener(ev, () => drop.classList.remove('att-over')));
        drop.addEventListener('drop', e => {
            e.preventDefault();
            if (e.dataTransfer && e.dataTransfer.files.length) { input.files = e.dataTransfer.files; refresh(); }
        });
        form.addEventListener('submit', () => { btn.disabled = true; btn.classList.add('disabled'); });
    });
})();

// ---------- Thông báo: kiểm tra định kỳ (60 giây) → cập nhật số chưa đọc, hiện thông báo nổi / thông báo màn hình ----------
(function () {
    const bell = document.getElementById('notifBell');
    if (!bell) return;
    const badge = bell.querySelector('.notif-count');
    let last = parseInt(bell.dataset.last || '0', 10);
    let box = null;
    function toast(n) {
        if (!box) { box = document.createElement('div'); box.className = 'notif-toasts'; document.body.appendChild(box); }
        const a = document.createElement('a');
        a.className = 'notif-toast';
        a.href = n.url;
        const t = document.createElement('div');
        const b = document.createElement('b'); b.textContent = n.title; t.appendChild(b);
        if (n.body) { const s = document.createElement('div'); s.className = 'small text-muted'; s.textContent = n.body; t.appendChild(s); }
        a.appendChild(t);
        box.appendChild(a);
        setTimeout(() => a.remove(), 8000);
        if ('Notification' in window && Notification.permission === 'granted' && document.hidden) {
            try { const dn = new Notification(n.title, { body: n.body || '' }); dn.onclick = () => { window.focus(); location.href = n.url; }; } catch (e) { }
        }
    }
    async function poll() {
        try {
            const r = await fetch(bell.dataset.poll + '?after=' + last, { headers: { 'Accept': 'application/json' } });
            if (!r.ok) return;
            const d = await r.json();
            badge.textContent = d.count > 99 ? '99+' : d.count;
            badge.classList.toggle('d-none', d.count === 0);
            document.title = document.title.replace(/^\(\d+\+?\) /, '');
            if (d.count > 0) document.title = '(' + (d.count > 99 ? '99+' : d.count) + ') ' + document.title;
            (d.items || []).slice().reverse().forEach(toast);
            if (d.last > last) last = d.last;
        } catch (e) { /* mất kết nối tạm thời */ }
    }
    setInterval(poll, 60000);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) poll(); });
})();
