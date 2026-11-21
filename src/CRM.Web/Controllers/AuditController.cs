using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewComponents;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

public class AuditFilter : ListFilter
{
    public string? Type { get; set; }
    public int? EntityId { get; set; }
    public int? UserId { get; set; }
    public string? Act { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    /// <summary>Lịch sử đăng nhập: chỉ lần thất bại</summary>
    public bool FailedOnly { get; set; }
}

/// <summary>Nhật ký hệ thống: thay đổi dữ liệu + lịch sử đăng nhập (quản trị / quản lý)</summary>
[Authorize(Roles = AppRoles.ManagerGroup)]
public class AuditController : CrmControllerBase
{
    public AuditController(CrmDbContext db) : base(db) { }

    public async Task<IActionResult> Index(AuditFilter f)
    {
        var q = Db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrEmpty(f.Type)) q = q.Where(a => a.EntityType == f.Type);
        if (f.EntityId.HasValue) q = q.Where(a => a.EntityId == f.EntityId);
        if (f.UserId.HasValue) q = q.Where(a => a.UserId == f.UserId);
        if (!string.IsNullOrEmpty(f.Act)) q = q.Where(a => a.Action == f.Act);
        if (f.From.HasValue) q = q.Where(a => a.At >= f.From.Value.Date);
        if (f.To.HasValue) q = q.Where(a => a.At < f.To.Value.Date.AddDays(1));
        if (!string.IsNullOrWhiteSpace(f.Keyword)) q = q.Where(a => a.Title!.Contains(f.Keyword) || a.Changes!.Contains(f.Keyword));
        // Quản lý: chỉ thấy thao tác của người dùng thuộc phạm vi đơn vị của mình
        if (!User.IsInRole(AppRoles.Admin))
        {
            var ids = await Db.UsersInScope.Select(u => u.Id).ToListAsync();
            q = q.Where(a => a.UserId == null || ids.Contains(a.UserId.Value));
        }
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(a => a.Id).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();
        ViewBag.Filter = f;
        ViewBag.Lookups = await AuditLookups.LoadAsync(Db, items);
        ViewBag.Users = new SelectList(await Db.UsersInScope.OrderBy(u => u.FullName).Select(u => new { u.Id, u.FullName }).ToListAsync(), "Id", "FullName", f.UserId);
        return View(new PagedList<AuditLog>(items, total, f.Page, f.PageSize));
    }

    public async Task<IActionResult> Logins(AuditFilter f)
    {
        var q = Db.LoginLogs.AsNoTracking();
        if (f.UserId.HasValue) q = q.Where(a => a.UserId == f.UserId);
        if (f.FailedOnly) q = q.Where(a => !a.Success);
        if (f.From.HasValue) q = q.Where(a => a.At >= f.From.Value.Date);
        if (f.To.HasValue) q = q.Where(a => a.At < f.To.Value.Date.AddDays(1));
        if (!string.IsNullOrWhiteSpace(f.Keyword)) q = q.Where(a => a.Username.Contains(f.Keyword) || a.Ip!.Contains(f.Keyword));
        if (!User.IsInRole(AppRoles.Admin))
        {
            var ids = await Db.UsersInScope.Select(u => u.Id).ToListAsync();
            q = q.Where(a => a.UserId != null && ids.Contains(a.UserId.Value));
        }
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(a => a.Id).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();
        ViewBag.Filter = f;
        ViewBag.Users = new SelectList(await Db.UsersInScope.OrderBy(u => u.FullName).Select(u => new { u.Id, u.FullName }).ToListAsync(), "Id", "FullName", f.UserId);
        ViewBag.Failed24h = await Db.LoginLogs.CountAsync(a => !a.Success && a.At >= DateTime.Now.AddDays(-1));
        return View(new PagedList<LoginLog>(items, total, f.Page, f.PageSize));
    }

    /// <summary>Liên kết tới bản ghi gốc (nếu có trang chi tiết)</summary>
    public static string? LinkOf(IUrlHelper url, string type, int id) => type switch
    {
        nameof(Customer) => url.Action("Details", "Customers", new { id }),
        nameof(Lead) => url.Action("Details", "Leads", new { id }),
        nameof(Opportunity) => url.Action("Details", "Opportunities", new { id }),
        nameof(Quote) => url.Action("Details", "Quotes", new { id }),
        nameof(BusinessPlan) => url.Action("Details", "BusinessPlans", new { id }),
        nameof(Contract) => url.Action("Details", "Contracts", new { id }),
        nameof(Invoice) => url.Action("Details", "Invoices", new { id }),
        nameof(Payment) => url.Action("Details", "Payments", new { id }),
        nameof(Campaign) => url.Action("Details", "Campaigns", new { id }),
        nameof(Ticket) => url.Action("Details", "Tickets", new { id }),
        nameof(SalesPlan) => url.Action("Details", "SalesPlans", new { id }),
        nameof(Activity) => url.Action("Details", "Tasks", new { id }),
        nameof(Product) => url.Action("Edit", "Products", new { id }),
        nameof(Contact) => url.Action("Edit", "Contacts", new { id }),
        _ => null
    };
}
