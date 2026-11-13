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
