using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Quản lý danh mục dùng chung: ngành nghề, tỉnh/thành, nhóm sản phẩm, đơn vị tính, lý do thua.</summary>
[Authorize(Roles = AppRoles.ManagerGroup)]
public class CatalogsController : CrmControllerBase
{
    private readonly CatalogService _catalogs;

    public CatalogsController(CrmDbContext db, CatalogService catalogs) : base(db) => _catalogs = catalogs;

    public async Task<IActionResult> Index(CatalogGroup group = CatalogGroup.Industry, string? keyword = null, bool showInactive = true)
    {
        var q = Db.CatalogItems.AsNoTracking().Where(x => x.Group == group);
        if (!showInactive) q = q.Where(x => x.IsActive);
        if (!string.IsNullOrWhiteSpace(keyword)) q = q.Where(x => x.Name.Contains(keyword) || (x.NameEn != null && x.NameEn.Contains(keyword)));

        ViewBag.Group = group;
        ViewBag.Keyword = keyword;
        ViewBag.ShowInactive = showInactive;
        ViewBag.Usage = await UsageAsync(group);
        ViewBag.Counts = await Db.CatalogItems.GroupBy(x => x.Group).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        return View(await q.OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync());
    }

    public async Task<IActionResult> Create(CatalogGroup group = CatalogGroup.Industry)
    {
        var next = await Db.CatalogItems.Where(x => x.Group == group).MaxAsync(x => (int?)x.SortOrder) ?? 0;
        return View("Form", new CatalogItem { Group = group, SortOrder = next + 10 });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var item = await Db.CatalogItems.FindAsync(id);
        return item == null ? NotFound() : View("Form", item);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CatalogItem model)
    {
        model.Name = model.Name?.Trim() ?? "";
        model.NameEn = string.IsNullOrWhiteSpace(model.NameEn) ? null : model.NameEn.Trim();
        if (!Enum.IsDefined(model.Group)) ModelState.AddModelError(nameof(model.Group), I18n.T("Nhóm danh mục không hợp lệ"));
        if (await Db.CatalogItems.AnyAsync(x => x.Group == model.Group && x.Name == model.Name && x.Id != model.Id))
            ModelState.AddModelError(nameof(model.Name), I18n.T("Tên đã tồn tại trong danh mục này"));
        if (!ModelState.IsValid) return View("Form", model);

        // Đổi tên + cập nhật dữ liệu đang dùng phải thành công / thất bại cùng nhau
        await using var tx = await Db.Database.BeginTransactionAsync();
        var renamed = 0;
        if (model.Id == 0) Db.CatalogItems.Add(model);
        else
        {
            var item = await Db.CatalogItems.FindAsync(model.Id);
            if (item == null) return NotFound();
            if (item.Group != model.Group) return BadRequest();

            // Đổi tên → cập nhật luôn các bản ghi nghiệp vụ đang dùng tên cũ
            if (!string.Equals(item.Name, model.Name, StringComparison.Ordinal))
                renamed = await RenameUsagesAsync(item.Group, item.Name, model.Name);

            item.Name = model.Name; item.NameEn = model.NameEn; item.SortOrder = model.SortOrder; item.IsActive = model.IsActive;
        }
        await Db.SaveChangesAsync();
        await tx.CommitAsync();
        _catalogs.Invalidate();

        TempData["Success"] = renamed > 0
            ? I18n.T("Đã lưu danh mục và cập nhật {0} bản ghi đang dùng.", renamed)
            : I18n.T("Đã lưu danh mục.");
        return RedirectToAction(nameof(Index), new { group = model.Group });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await Db.CatalogItems.FindAsync(id);
        if (item == null) return NotFound();

        var usage = await UsageAsync(item.Group);
        if (usage.TryGetValue(item.Name, out var n) && n > 0)
        {
            TempData["Error"] = I18n.T("Mục \"{0}\" đang được dùng ở {1} bản ghi. Hãy chuyển sang ngừng sử dụng thay vì xóa.", item.Name, n);
            return RedirectToAction(nameof(Index), new { group = item.Group });
        }

        Db.CatalogItems.Remove(item);
        await Db.SaveChangesAsync();
        _catalogs.Invalidate();
        TempData["Success"] = I18n.T("Đã xóa mục danh mục.");
        return RedirectToAction(nameof(Index), new { group = item.Group });
    }

    /// <summary>Số bản ghi nghiệp vụ đang dùng từng giá trị của nhóm danh mục.</summary>
    private async Task<Dictionary<string, int>> UsageAsync(CatalogGroup group)
    {
        var rows = group switch
        {
            CatalogGroup.Industry => await Db.Customers.Where(x => x.Industry != null)
                .GroupBy(x => x.Industry!).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(),
            CatalogGroup.City => await Db.Customers.Where(x => x.City != null)
                .GroupBy(x => x.City!).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(),
            CatalogGroup.ProductCategory => await Db.Products.Where(x => x.Category != null)
                .GroupBy(x => x.Category!).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(),
            CatalogGroup.Unit => (await Db.Products.GroupBy(x => x.Unit).Select(g => new { g.Key, Count = g.Count() }).ToListAsync())
                .Concat(await Db.QuoteLines.Where(x => x.Unit != null).GroupBy(x => x.Unit!).Select(g => new { g.Key, Count = g.Count() }).ToListAsync())
                .ToList(),
            CatalogGroup.LossReason => await Db.Opportunities.Where(x => x.LostReason != null)
                .GroupBy(x => x.LostReason!).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(),
            _ => new()
        };
        return rows.GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Count), StringComparer.OrdinalIgnoreCase);
    }

    private async Task<int> RenameUsagesAsync(CatalogGroup group, string oldName, string newName) => group switch
    {
        CatalogGroup.Industry => await Db.Customers.Where(x => x.Industry == oldName).ExecuteUpdateAsync(s => s.SetProperty(x => x.Industry, newName)),
        CatalogGroup.City => await Db.Customers.Where(x => x.City == oldName).ExecuteUpdateAsync(s => s.SetProperty(x => x.City, newName)),
        CatalogGroup.ProductCategory => await Db.Products.Where(x => x.Category == oldName).ExecuteUpdateAsync(s => s.SetProperty(x => x.Category, newName)),
        CatalogGroup.Unit => await Db.Products.Where(x => x.Unit == oldName).ExecuteUpdateAsync(s => s.SetProperty(x => x.Unit, newName)),
        // Đơn vị trên dòng báo giá và lý do thua của cơ hội là dữ liệu lịch sử → giữ nguyên chữ đã ghi
        _ => 0
    };
}
