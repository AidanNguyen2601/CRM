// Xác nhận trước các thao tác quan trọng: <form data-confirm="...">
document.addEventListener('submit', function (e) {
    const msg = e.target.getAttribute('data-confirm');
    if (msg && !confirm(msg)) e.preventDefault();
});

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
    nav.addEventListener('click', e => {
        const btn = e.target.closest('.nav-toggle');
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
            table.style.width = cols().reduce((s, th) => s + parseFloat(th.style.width || th.offsetWidth), 0) + 'px';
        }

        // Nút khôi phục bố cục (hiện khi đã tùy chỉnh)
        const host = table.closest('.card') || table.parentElement;
        host.classList.add('grid-host');
        const reset = document.createElement('button');
        reset.type = 'button';
        reset.className = 'grid-reset no-print';
        reset.title = TXT.reset;
        reset.setAttribute('aria-label', TXT.reset);
        reset.innerHTML = '<svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"/><path d="M3 3v5h5"/></svg>';
        reset.addEventListener('click', () => { store.del(key); location.reload(); });
        host.appendChild(reset);

        function save() {
            const state = { order: cols().map(th => +th.dataset.col) };
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
