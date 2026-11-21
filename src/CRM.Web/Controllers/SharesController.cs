using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Chia sẻ bản ghi (khách hàng, liên hệ, lead, cơ hội) cho đồng nghiệp: xem hoặc xem + sửa. Người phụ trách hoặc quản lý mới được chia sẻ.</summary>
public class SharesController : CrmControllerBase
{
    public static readonly string[] Types = { nameof(Customer), nameof(Contact), nameof(Lead), nameof(Opportunity) };
    private readonly NotificationService _notify;
    public SharesController(CrmDbContext db, NotificationService notify) : base(db) => _notify = notify;

    /// <summary>Bản ghi + quyền quản lý chia sẻ (người phụ trách / quản lý)</summary>
    public static async Task<(IOwned? Item, string Title, string Url, bool CanManage)> ResolveAsync(CrmDbContext db, System.Security.Claims.ClaimsPrincipal user, string type, int id)
    {
        IOwned? item; string title, url;
        switch (type)
        {
            case nameof(Customer): { var x = await db.Customers.FindAsync(id); item = x; title = x?.Name ?? ""; url = $"/Customers/Details/{id}"; break; }
            case nameof(Contact): { var x = await db.Contacts.FindAsync(id); item = x; title = x?.FullName ?? ""; url = $"/Contacts/Edit/{id}"; break; }
            case nameof(Lead): { var x = await db.Leads.FindAsync(id); item = x; title = x?.FullName ?? ""; url = $"/Leads/Details/{id}"; break; }
            case nameof(Opportunity): { var x = await db.Opportunities.FindAsync(id); item = x; title = x?.Name ?? ""; url = $"/Opportunities/Details/{id}"; break; }
            default: return (null, "", "/", false);
        }
        return (item, title, url, item != null && user.CanAccess(item));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(string type, int id, int userId, bool canEdit)
    {
        var (item, title, url, canManage) = await ResolveAsync(Db, User, type, id);
        if (item == null) return NotFound();
        if (!canManage) return NotAllowed();
        var target = await Db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive);
        if (target == null || target.Id == item.OwnerId) { TempData["Error"] = I18n.T("Người nhận chia sẻ không hợp lệ."); return Redirect(url + "#shares"); }
        // Dữ liệu giới hạn theo đơn vị: chỉ chia sẻ cho người cùng đơn vị hoặc đơn vị cấp trên
        var branches = HttpContext.RequestServices.GetRequiredService<BranchDirectory>().Get().Ancestors(((IBranchScoped)item).BranchId);
        if (target.Role != AppRoles.Admin && !branches.Contains(target.BranchId))
        {
            TempData["Error"] = I18n.T("Người nhận thuộc đơn vị khác, không xem được dữ liệu của đơn vị này.");
            return Redirect(url + "#shares");
        }
        var s = await Db.RecordShares.FirstOrDefaultAsync(x => x.EntityType == type && x.EntityId == id && x.UserId == userId);
        if (s == null) Db.RecordShares.Add(s = new RecordShare { EntityType = type, EntityId = id, UserId = userId, SharedById = CurrentUserId });
        s.CanEdit = canEdit;
        AuditTrail.Write(Db, type, id, "Update", title, new[] { new AuditChange("SharedWith", null, target.FullName + (canEdit ? " (" + I18n.T("được sửa") + ")" : " (" + I18n.T("chỉ xem") + ")")) });
        _notify.Add(userId, "share", I18n.T("{0} chia sẻ với bạn: {1}", User.GetFullName(), title), url, CurrentUserId, canEdit ? I18n.T("Quyền: xem và sửa") : I18n.T("Quyền: chỉ xem"));
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã chia sẻ với {0}.", target.FullName);
        return Redirect(url + "#shares");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(int shareId)
    {
        var s = await Db.RecordShares.Include(x => x.User).FirstOrDefaultAsync(x => x.Id == shareId);
        if (s == null) return NotFound();
        var (item, title, url, canManage) = await ResolveAsync(Db, User, s.EntityType, s.EntityId);
        if (item == null || !canManage) return NotAllowed();
        Db.RecordShares.Remove(s);
        AuditTrail.Write(Db, s.EntityType, s.EntityId, "Update", title, new[] { new AuditChange("SharedWith", s.User?.FullName, null) });
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã bỏ chia sẻ với {0}.", s.User?.FullName ?? "");
        return Redirect(url + "#shares");
    }
}
