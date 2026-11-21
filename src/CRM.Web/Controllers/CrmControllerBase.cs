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

    /// <summary>Xóa toàn bộ file đính kèm của bản ghi (gọi sau khi xóa bản ghi gốc)</summary>
    protected Task DeleteAttachmentsAsync(AttachmentTarget type, int id) =>
        HttpContext.RequestServices.GetRequiredService<AttachmentService>().DeleteAllAsync(type, id);

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

    /// <summary>Sửa bản ghi: quản lý được đổi người phụ trách; nhân viên sửa bản ghi của mình giữ là mình; người được chia sẻ sửa thì giữ người phụ trách cũ.</summary>
    protected int? KeepOwner(int? existing, int? requested) =>
        User.SeesAll() ? (requested ?? CurrentUserId) : existing == null || existing == CurrentUserId ? CurrentUserId : existing;

    protected async Task LoadOwnersAsync(int? selected)
    {
        ViewBag.Owners = new SelectList(await Db.UsersInScope.Where(u => u.IsActive).OrderBy(u => u.FullName).ToListAsync(),
            "Id", "FullName", selected);
    }

    /// <summary>Danh sách: nhãn của từng dòng, trường tùy biến "hiện ở danh sách", danh sách nhãn để lọc</summary>
    protected async Task LoadRowExtrasAsync(string type, IEnumerable<int> ids)
    {
        var list = ids.ToList();
        ViewBag.RowTags = await CustomData.TagsOfAsync(Db, type, list);
        ViewBag.RowFields = await CustomData.FieldsAsync(Db, type, listOnly: true);
        ViewBag.RowValues = ((List<CustomFieldDef>)ViewBag.RowFields).Count > 0 ? await CustomData.ValuesAsync(Db, type, list) : new Dictionary<int, Dictionary<int, string?>>();
        ViewBag.TagOptions = await Db.Tags.OrderBy(t => t.Name).Select(t => new { t.Id, t.Name }).ToListAsync();
    }

    /// <summary>Danh sách bảng giá đang dùng (form khách hàng)</summary>
    protected async Task LoadPriceListsAsync(int? selected)
    {
        ViewBag.PriceLists = new SelectList(await Db.PriceLists.Where(p => p.IsActive || p.Id == selected).OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync(), "Id", "Name", selected);
    }

    /// <summary>Danh sách chính sách SLA đang dùng (form khách hàng / hợp đồng); rỗng = chưa cấu hình SLA</summary>
    protected async Task LoadSlaPoliciesAsync(int? selected)
    {
        ViewBag.SlaPolicies = new SelectList(await Db.SlaPolicies.Where(p => p.IsActive || p.Id == selected).OrderBy(p => p.Name)
            .Select(p => new { p.Id, Name = p.Name + (p.IsDefault ? " (" + I18n.T("mặc định") + ")" : "") }).ToListAsync(), "Id", "Name", selected);
    }

    protected async Task LoadCustomersAsync(int? selected)
    {
        ViewBag.Customers = new SelectList(await Db.Customers.VisibleTo(User, Db).OrderBy(c => c.Name)
            .Select(c => new { c.Id, Name = c.Code + " - " + c.Name }).ToListAsync(), "Id", "Name", selected);
    }

    protected async Task LoadContactsAsync(int? customerId, int? selected)
    {
        var q = Db.Contacts.VisibleTo(User, Db);
        if (customerId.HasValue) q = q.Where(c => c.CustomerId == customerId);
        ViewBag.Contacts = new SelectList(await q.OrderBy(c => c.FullName)
            .Select(c => new { c.Id, Name = c.FullName + (c.JobTitle != null ? " (" + c.JobTitle + ")" : "") }).ToListAsync(),
            "Id", "Name", selected);
    }

    protected async Task LoadOpportunitiesAsync(int? customerId, int? selected)
    {
        var q = Db.Opportunities.VisibleTo(User, Db);
        if (customerId.HasValue) q = q.Where(o => o.CustomerId == customerId);
        ViewBag.Opportunities = new SelectList(await q.OrderByDescending(o => o.CreatedAt)
            .Select(o => new { o.Id, o.Name }).ToListAsync(), "Id", "Name", selected);
    }

    /// <summary>
    /// Quyền với bản ghi có người phụ trách: quản lý / quản trị, người phụ trách, hoặc người được chia sẻ
    /// (edit = true: cần chia sẻ có quyền sửa).
    /// </summary>
    protected async Task<bool> CanAccessAsync<T>(T item, bool edit = true) where T : class, IOwned
    {
        if (User.CanAccess(item)) return true;
        var id = Db.Entry(item).Property<int>("Id").CurrentValue;
        var uid = CurrentUserId;
        var type = typeof(T).Name;
        return await Db.RecordShares.AnyAsync(s => s.EntityType == type && s.EntityId == id && s.UserId == uid && (!edit || s.CanEdit));
    }

    /// <summary>
    /// Chặn tạo bản ghi có thể trùng (cùng MST / email / điện thoại...) cho tới khi người dùng xác nhận "không phải trùng"
    /// (ô confirmDuplicate trong #dupBox). Trả false và đặt ViewBag.Duplicates khi cần xác nhận.
    /// </summary>
    protected async Task<bool> DuplicateGuardAsync(string entity, int id, string? name, string? taxCode, string? phone, string? email, string? company = null)
    {
        if (id != 0 || Request.HasFormContentType && Request.Form["confirmDuplicate"] == "true") return true;
        var hits = await HttpContext.RequestServices.GetRequiredService<DuplicateCheckService>().CheckAsync(entity, id, name, taxCode, phone, email, company, User);
        if (!hits.Any(h => h.Strong)) return true;
        ViewBag.Duplicates = hits;
        ModelState.AddModelError(string.Empty, I18n.T("Dữ liệu có thể trùng với bản ghi đã có. Hãy kiểm tra danh sách bên dưới; nếu không phải trùng, đánh dấu xác nhận rồi lưu lại."));
        return false;
    }

    /// <summary>Trả file PDF chứng từ (đã kiểm tra quyền ở action gọi). inline = mở xem trên trình duyệt thay vì tải về.</summary>
    protected async Task<IActionResult> PdfFileAsync(string type, int id, bool inline)
    {
        try
        {
            var pdf = await HttpContext.RequestServices.GetRequiredService<PdfService>().RenderAsync(type, id);
            if (pdf == null) return NotFound();
            if (inline)
            {
                Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { Inline = true, FileName = pdf.Value.FileName }.ToString();
                return File(pdf.Value.Data, "application/pdf");
            }
            return File(pdf.Value.Data, "application/pdf", pdf.Value.FileName);
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction("Details", new { id });
        }
    }

    protected IActionResult NotAllowed()
    {
        TempData["Error"] = I18n.T("Bạn không có quyền với bản ghi này.");
        return RedirectToAction("Index", "Home");
    }
}
