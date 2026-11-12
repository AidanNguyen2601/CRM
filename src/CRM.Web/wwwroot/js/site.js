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
