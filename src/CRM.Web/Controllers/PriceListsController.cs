using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Bảng giá theo nhóm khách hàng / kênh và chính sách chiết khấu (hạn mức theo vai trò, vượt mức phải duyệt)</summary>
public class PriceListsController : CrmControllerBase
{
    private readonly PricingService _pricing;
    private readonly SettingsStore _settings;
    public PriceListsController(CrmDbContext db, PricingService pricing, SettingsStore settings) : base(db) { _pricing = pricing; _settings = settings; }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    public async Task<IActionResult> Index()
    {
        ViewBag.Policy = await _pricing.PolicyAsync();
        ViewBag.CustomerCounts = await Db.Customers.IgnoreQueryFilters().Where(c => c.PriceListId != null).GroupBy(c => c.PriceListId!.Value)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        return View(await Db.PriceLists.Include(p => p.Items).AsNoTracking().OrderByDescending(p => p.IsActive).ThenBy(p => p.Name).ToListAsync());
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    public async Task<IActionResult> Edit(int? id)
    {
        var p = id.HasValue ? await Db.PriceLists.Include(x => x.Items).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id) : new PriceList();
        if (p == null) return NotFound();
        ViewBag.Products = await Db.Products.Where(x => x.IsActive || p.Items.Select(i => i.ProductId).Contains(x.Id)).OrderBy(x => x.Category).ThenBy(x => x.Code).AsNoTracking().ToListAsync();
        return View(p);
    }

    /// <summary>Lưu bảng giá. Ô giá theo sản phẩm tên price_{productId}; để trống = không thuộc bảng giá (dùng giá chuẩn).</summary>
    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(PriceList model)
    {
        ModelState.Remove(nameof(model.Items));
        if (model.ValidFrom.HasValue && model.ValidTo.HasValue && model.ValidTo < model.ValidFrom)
            ModelState.AddModelError(nameof(model.ValidTo), I18n.T("Ngày hết hiệu lực phải sau ngày bắt đầu."));
        if (!ModelState.IsValid)
        {
            ViewBag.Products = await Db.Products.Where(x => x.IsActive).OrderBy(x => x.Category).ThenBy(x => x.Code).AsNoTracking().ToListAsync();
            return View(model);
        }
        PriceList p;
        if (model.Id == 0) { p = new PriceList(); Db.PriceLists.Add(p); }
        else p = await Db.PriceLists.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == model.Id) ?? throw new InvalidOperationException();
        p.Name = model.Name.Trim(); p.Description = model.Description; p.ValidFrom = model.ValidFrom; p.ValidTo = model.ValidTo; p.IsActive = model.IsActive; p.UpdatedAt = DateTime.Now;
        var productIds = await Db.Products.Select(x => x.Id).ToListAsync();
        foreach (var pid in productIds)
        {
            var raw = Request.Form[$"price_{pid}"].ToString().Trim();
            var item = p.Items.FirstOrDefault(i => i.ProductId == pid);
            if (string.IsNullOrEmpty(raw)) { if (item != null) Db.PriceListItems.Remove(item); continue; }
            if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var price) || price < 0) continue;
            if (item == null) p.Items.Add(item = new PriceListItem { ProductId = pid });
            item.Price = Math.Round(price, 0);
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu bảng giá \"{0}\" ({1} sản phẩm).", p.Name, p.Items.Count);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var p = await Db.PriceLists.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        var used = await Db.Customers.IgnoreQueryFilters().CountAsync(c => c.PriceListId == id);
        if (used > 0)
        {
            TempData["Error"] = I18n.T("Bảng giá đang áp dụng cho {0} khách hàng – hãy bỏ gắn hoặc chuyển sang ngừng dùng.", used);
            return RedirectToAction(nameof(Index));
        }
        Db.PriceListItems.RemoveRange(p.Items);
        Db.PriceLists.Remove(p);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa bảng giá \"{0}\".", p.Name);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePolicy(DiscountPolicy model)
    {
        model.SalesLimit = Math.Clamp(model.SalesLimit, 0, 100); model.SupportLimit = Math.Clamp(model.SupportLimit, 0, 100); model.ManagerLimit = Math.Clamp(model.ManagerLimit, 0, 100);
        await _settings.SaveAsync(DiscountPolicy.Key, model);
        AuditTrail.Write(Db, "Settings", 0, "Update", I18n.T("Chính sách chiết khấu"), new[]
        {
            new AuditChange("Enabled", null, model.Enabled.ToString()), new AuditChange("SalesLimit", null, model.SalesLimit + "%"),
            new AuditChange("ManagerLimit", null, model.ManagerLimit + "%")
        });
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu chính sách chiết khấu.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Giá theo bảng giá của khách hàng (dùng trên form báo giá)</summary>
    public async Task<IActionResult> ForCustomer(int customerId)
    {
        if (!await Db.Customers.VisibleTo(User, Db).AnyAsync(c => c.Id == customerId)) return Json(new { name = (string?)null, prices = new Dictionary<int, decimal>() });
        var (list, prices) = await _pricing.CustomerPricesAsync(customerId);
        return Json(new { name = list?.Name, prices });
    }
}
