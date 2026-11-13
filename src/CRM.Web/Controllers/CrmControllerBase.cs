using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Các hàm dùng chung: danh sách chọn (dropdown), người phụ trách mặc định.</summary>
public abstract class CrmControllerBase : Controller
{
    protected readonly CrmDbContext Db;
    protected CrmControllerBase(CrmDbContext db) => Db = db;

    protected int CurrentUserId => User.GetUserId();

    /// <summary>
    /// Số dòng mỗi trang cho các danh sách (ListFilter): lấy từ URL (?PageSize=) và ghi nhớ vào cookie
    /// theo từng danh sách; không có trên URL thì dùng giá trị đã nhớ.
    /// </summary>
    public override void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
    {
        var filter = context.ActionArguments.Values.OfType<ViewModels.ListFilter>().FirstOrDefault();
        if (filter != null)
        {
            var cookie = $"crm.ps.{ControllerContext.ActionDescriptor.ControllerName}";
            if (Request.Query.ContainsKey(nameof(filter.PageSize)))
            {
                filter.PageSize = Paging.Clamp(filter.PageSize);
                Response.Cookies.Append(cookie, filter.PageSize.ToString(),
                    new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax, HttpOnly = true });
            }
            else if (int.TryParse(Request.Cookies[cookie], out var saved))
                filter.PageSize = Paging.Clamp(saved);
            else
                filter.PageSize = Paging.Clamp(filter.PageSize);
            if (filter.Page < 1) filter.Page = 1;
        }
        base.OnActionExecuting(context);
    }

    /// <summary>Sales luôn là người phụ trách bản ghi họ tạo; Manager/Admin được chọn.</summary>
    protected int? ResolveOwner(int? requested) => User.SeesAll() ? (requested ?? CurrentUserId) : CurrentUserId;

    protected async Task LoadOwnersAsync(int? selected)
    {
        ViewBag.Owners = new SelectList(await Db.Users.Where(u => u.IsActive).OrderBy(u => u.FullName).ToListAsync(),
            "Id", "FullName", selected);
    }

    protected async Task LoadCustomersAsync(int? selected)
    {
        ViewBag.Customers = new SelectList(await Db.Customers.VisibleTo(User).OrderBy(c => c.Name)
            .Select(c => new { c.Id, Name = c.Code + " - " + c.Name }).ToListAsync(), "Id", "Name", selected);
    }

    protected async Task LoadContactsAsync(int? customerId, int? selected)
    {
        var q = Db.Contacts.VisibleTo(User);
        if (customerId.HasValue) q = q.Where(c => c.CustomerId == customerId);
        ViewBag.Contacts = new SelectList(await q.OrderBy(c => c.FullName)
            .Select(c => new { c.Id, Name = c.FullName + (c.JobTitle != null ? " (" + c.JobTitle + ")" : "") }).ToListAsync(),
            "Id", "Name", selected);
    }

    protected async Task LoadOpportunitiesAsync(int? customerId, int? selected)
    {
        var q = Db.Opportunities.VisibleTo(User);
        if (customerId.HasValue) q = q.Where(o => o.CustomerId == customerId);
        ViewBag.Opportunities = new SelectList(await q.OrderByDescending(o => o.CreatedAt)
            .Select(o => new { o.Id, o.Name }).ToListAsync(), "Id", "Name", selected);
    }

    protected IActionResult NotAllowed()
    {
        TempData["Error"] = I18n.T("Bạn không có quyền với bản ghi này.");
        return RedirectToAction("Index", "Home");
    }
}
