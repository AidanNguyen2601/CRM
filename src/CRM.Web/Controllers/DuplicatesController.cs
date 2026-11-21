using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Kiểm tra và gộp bản ghi trùng (khách hàng, người liên hệ, lead) – quản lý / quản trị</summary>
[Authorize(Roles = AppRoles.ManagerGroup)]
public class DuplicatesController : CrmControllerBase
{
    private readonly MergeService _merge;
    public DuplicatesController(CrmDbContext db, MergeService merge) : base(db) => _merge = merge;

    public async Task<IActionResult> Index(string type = nameof(Customer))
    {
        if (!MergeService.Types.Contains(type)) type = nameof(Customer);
        var groups = await _merge.FindAsync(type, User);
        ViewBag.Type = type;
        ViewBag.Total = groups.Count;
        return View(groups.Take(100).ToList());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Merge(string type, int masterId, int[] ids)
    {
        if (!MergeService.Types.Contains(type)) return BadRequest();
        var all = ids.Append(masterId).Distinct().ToList();
        if (all.Count < 2)
        {
            TempData["Error"] = I18n.T("Chọn ít nhất một bản ghi trùng để gộp vào bản ghi chính.");
            return RedirectToAction(nameof(Index), new { type });
        }
        // Tất cả phải nằm trong phạm vi được xem
        var visible = type switch
        {
            nameof(Customer) => await Db.Customers.VisibleTo(User, Db).CountAsync(x => all.Contains(x.Id)),
            nameof(Contact) => await Db.Contacts.VisibleTo(User, Db).CountAsync(x => all.Contains(x.Id)),
            _ => await Db.Leads.VisibleTo(User, Db).CountAsync(x => all.Contains(x.Id))
        };
        if (visible != all.Count) return NotAllowed();
        var moved = await _merge.MergeAsync(type, masterId, ids);
        TempData["Success"] = I18n.T("Đã gộp {0} bản ghi vào bản ghi chính, chuyển {1} dữ liệu liên quan.", all.Count - 1, moved);
        return RedirectToAction(nameof(Index), new { type });
    }
}
