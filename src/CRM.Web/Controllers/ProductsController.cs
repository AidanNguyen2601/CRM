using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

public class ProductsController : CrmControllerBase
{
    public ProductsController(CrmDbContext db) : base(db) { }

    public async Task<IActionResult> Index(string? keyword, ProductType? type, string? category, bool showInactive = false)
    {
        var q = Db.Products.AsNoTracking();
        if (type.HasValue) q = q.Where(p => p.Type == type);
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(p => p.Category == category);
        ViewBag.Type = type;
        ViewBag.Category = category;
        if (!showInactive) q = q.Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(keyword)) q = q.Where(p => p.Name.Contains(keyword) || p.Code.Contains(keyword) || (p.Brand != null && p.Brand.Contains(keyword)));
        ViewBag.Keyword = keyword;
        ViewBag.ShowInactive = showInactive;
        return View(await q.OrderBy(p => p.Category).ThenBy(p => p.Code).ToListAsync());
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    public IActionResult Create() => View("Form", new Product());

    [Authorize(Roles = AppRoles.ManagerGroup)]
    public async Task<IActionResult> Edit(int id)
    {
        var p = await Db.Products.FindAsync(id);
        return p == null ? NotFound() : View("Form", p);
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Product model)
    {
        if (await Db.Products.AnyAsync(p => p.Code == model.Code && p.Id != model.Id))
            ModelState.AddModelError(nameof(model.Code), I18n.T("Mã sản phẩm đã tồn tại"));
        if (!ModelState.IsValid) return View("Form", model);

        if (model.Id == 0) Db.Products.Add(model);
        else
        {
            var p = await Db.Products.FindAsync(model.Id);
            if (p == null) return NotFound();
            p.Code = model.Code; p.Name = model.Name; p.Category = model.Category; p.Unit = model.Unit;
            p.UnitPrice = model.UnitPrice; p.IsActive = model.IsActive; p.Description = model.Description;
            p.Type = model.Type; p.CostPrice = model.CostPrice; p.VatPercent = model.VatPercent; p.Brand = model.Brand;
            p.Origin = model.Origin; p.WarrantyMonths = model.WarrantyMonths; p.Specification = model.Specification;
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu sản phẩm.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>API cho form báo giá</summary>
    [HttpGet]
    public async Task<IActionResult> Lookup()
        => Json(await Db.Products.Where(p => p.IsActive).OrderBy(p => p.Code)
            .Select(p => new { id = p.Id, code = p.Code, name = p.Name, unit = p.Unit, price = p.UnitPrice, cost = p.CostPrice, vat = p.VatPercent }).ToListAsync());
}
