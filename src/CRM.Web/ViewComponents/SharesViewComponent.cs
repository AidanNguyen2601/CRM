using CRM.Web.Controllers;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.ViewComponents;

public class SharesModel
{
    public string Type { get; set; } = "";
    public int Id { get; set; }
    public bool CanManage { get; set; }
    public List<RecordShare> Items { get; set; } = new();
    public List<AppUser> Candidates { get; set; } = new();
}

/// <summary>Khối "Chia sẻ": @await Component.InvokeAsync("Shares", new { type = "Customer", id = c.Id })</summary>
public class SharesViewComponent : ViewComponent
{
    private readonly CrmDbContext _db;
    public SharesViewComponent(CrmDbContext db) => _db = db;

    public async Task<IViewComponentResult> InvokeAsync(string type, int id)
    {
        var (item, _, _, canManage) = await SharesController.ResolveAsync(_db, UserClaimsPrincipal, type, id);
        if (item == null) return Content(string.Empty);
        var items = await _db.RecordShares.Include(s => s.User).AsNoTracking().Where(s => s.EntityType == type && s.EntityId == id).OrderBy(s => s.User!.FullName).ToListAsync();
        if (!canManage && items.Count == 0) return Content(string.Empty);
        var taken = items.Select(s => s.UserId).Append(item.OwnerId ?? 0).ToList();
        // Chỉ chia sẻ được cho người có phạm vi đơn vị bao gồm bản ghi (cùng đơn vị hoặc đơn vị cấp trên; quản trị xem cả chuỗi)
        var branches = HttpContext.RequestServices.GetRequiredService<Infrastructure.BranchDirectory>().Get().Ancestors(((IBranchScoped)item).BranchId);
        return View(new SharesModel
        {
            Type = type, Id = id, CanManage = canManage, Items = items,
            Candidates = canManage ? await _db.Users.Where(u => u.IsActive && !taken.Contains(u.Id) && (u.Role == AppRoles.Admin || branches.Contains(u.BranchId))).OrderBy(u => u.FullName).ToListAsync() : new()
        });
    }
}
