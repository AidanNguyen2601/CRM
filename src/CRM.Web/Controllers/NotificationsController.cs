using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Thông báo trong ứng dụng của người dùng đang đăng nhập</summary>
[Authorize]
public class NotificationsController : Controller
{
    private readonly CrmDbContext _db;
    public NotificationsController(CrmDbContext db) => _db = db;
    private int Me => User.GetUserId();

    public async Task<IActionResult> Index(bool unread = false, int page = 1, int pageSize = 30)
    {
        var size = Paging.Clamp(pageSize);
        var q = _db.Notifications.Include(n => n.Actor).AsNoTracking().Where(n => n.UserId == Me);
        if (unread) q = q.Where(n => !n.IsRead);
        var total = await q.CountAsync();
        page = Math.Max(1, page);
        ViewBag.Unread = unread;
        ViewBag.UnreadCount = await _db.Notifications.CountAsync(n => n.UserId == Me && !n.IsRead);
        return View(new PagedList<Notification>(await q.OrderByDescending(n => n.Id).Skip((page - 1) * size).Take(size).ToListAsync(), total, page, size));
    }

    /// <summary>Mở thông báo: đánh dấu đã đọc rồi chuyển tới trang liên quan</summary>
    public async Task<IActionResult> Open(int id)
    {
        var n = await _db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == Me);
        if (n == null) return RedirectToAction(nameof(Index));
        if (!n.IsRead) { n.IsRead = true; n.ReadAt = DateTime.Now; await _db.SaveChangesAsync(); }
        return !string.IsNullOrEmpty(n.Url) && Url.IsLocalUrl(n.Url) ? LocalRedirect(n.Url) : RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead(string? returnUrl)
    {
        await _db.Notifications.Where(n => n.UserId == Me && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true).SetProperty(n => n.ReadAt, DateTime.Now));
        if (Request.Headers.Accept.ToString().Contains("application/json")) return Json(new { ok = true });
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRead()
    {
        var n = await _db.Notifications.Where(x => x.UserId == Me && x.IsRead).ExecuteDeleteAsync();
        TempData["Success"] = I18n.T("Đã xóa {0} thông báo đã đọc.", n);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Kiểm tra thông báo mới (gọi định kỳ từ trình duyệt): số chưa đọc + các thông báo có Id lớn hơn after</summary>
    [HttpGet]
    public async Task<IActionResult> Poll(int after = 0)
    {
        var count = await _db.Notifications.CountAsync(n => n.UserId == Me && !n.IsRead);
        var items = after <= 0 ? new List<object>() : (await _db.Notifications.AsNoTracking()
                .Where(n => n.UserId == Me && n.Id > after && !n.IsRead).OrderByDescending(n => n.Id).Take(5).ToListAsync())
            .Select(n => (object)new { id = n.Id, title = n.Title, body = n.Body, url = Url.Action(nameof(Open), new { id = n.Id }), type = n.Type })
            .ToList();
        var last = await _db.Notifications.Where(n => n.UserId == Me).MaxAsync(n => (int?)n.Id) ?? 0;
        return Json(new { count, last, items });
    }
}
