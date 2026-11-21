/* Nhúng form thu lead của CRM vào website:
 *   <script src="https://<crm>/js/crm-form.js" data-form="<mã form>" async></script>
 * Tạo iframe ngay sau thẻ script, tự chuyển UTM (utm_source/medium/campaign) và địa chỉ trang hiện tại vào form,
 * tự co giãn chiều cao và chuyển trang sau khi gửi (nếu form có cấu hình "Chuyển tới trang"). */
(function () {
    var s = document.currentScript;
    if (!s || !s.getAttribute('data-form')) return;
    var base = new URL(s.src, location.href).origin;
    var p = new URLSearchParams(location.search), q = new URLSearchParams({ embed: '1' });
    ['utm_source', 'utm_medium', 'utm_campaign'].forEach(function (k) { if (p.get(k)) q.set(k, p.get(k).slice(0, 150)); });
    q.set('page', location.href.slice(0, 500));
    if (document.referrer) q.set('ref', document.referrer.slice(0, 500));
    var f = document.createElement('iframe');
    f.src = base + '/f/' + encodeURIComponent(s.getAttribute('data-form')) + '?' + q.toString();
    f.title = s.getAttribute('data-title') || 'Form';
    f.loading = 'lazy';
    f.style.cssText = 'width:100%;border:0;min-height:460px;display:block;color-scheme:normal';
    s.parentNode.insertBefore(f, s.nextSibling);
    window.addEventListener('message', function (e) {
        if (e.origin !== base || e.source !== f.contentWindow || !e.data) return;
        if (e.data.type === 'crm-form-height' && e.data.height > 0) f.style.height = Math.min(e.data.height + 4, 3000) + 'px';
        if (e.data.type === 'crm-form-redirect' && /^https?:\/\//i.test(e.data.url)) location.href = e.data.url;
    });
})();
