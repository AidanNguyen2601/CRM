using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

public class CustomersController : CrmControllerBase
{
    public CustomersController(CrmDbContext db) : base(db) { }

    public async Task<IActionResult> Index([FromQuery] CustomerFilter f)
    {
        var q = Db.Customers.VisibleTo(User, Db).Include(c => c.Owner).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(f.Keyword))
        {
            var k = f.Keyword.Trim();
            q = q.Where(c => c.Name.Contains(k) || c.Code.Contains(k) || (c.TaxCode != null && c.TaxCode.Contains(k))
                             || (c.Phone != null && c.Phone.Contains(k)) || (c.Email != null && c.Email.Contains(k)));
        }
        if (f.Type.HasValue) q = q.Where(c => c.Type == f.Type);
        if (f.Health == "good") q = q.Where(c => c.HealthScore >= CustomerHealth.GoodFrom);
        else if (f.Health == "warn") q = q.Where(c => c.HealthScore >= CustomerHealth.WarnFrom && c.HealthScore < CustomerHealth.GoodFrom);
        else if (f.Health == "risk") q = q.Where(c => c.HealthScore < CustomerHealth.WarnFrom);
        if (f.OwnerId.HasValue) q = q.Where(c => c.OwnerId == f.OwnerId);

        f.Page = Math.Max(1, f.Page);
        q = q.WithTag(Db, f.TagId);
        var total = await q.CountAsync();
        var items = await q.OrderBy(c => c.Name).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();

        // Doanh thu đã thắng theo khách hàng (trang hiện tại)
        var ids = items.Select(c => c.Id).ToList();
        ViewBag.Won = (await Db.Opportunities
                .Where(o => o.Stage == OpportunityStage.ClosedWon && o.CustomerId != null && ids.Contains(o.CustomerId.Value))
                .Select(o => new { o.CustomerId, o.Amount }).ToListAsync())
            .GroupBy(x => x.CustomerId!.Value).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        await LoadOwnersAsync(f.OwnerId);
        ViewBag.Filter = f;
        await LoadRowExtrasAsync("Customer", items.Select(x => x.Id));
        return View(new PagedList<Customer>(items, total, f.Page, f.PageSize));
    }

    /// <summary>Khách hàng 360°: toàn bộ quan hệ, bán hàng, tài chính, chăm sóc của một khách hàng</summary>
    public async Task<IActionResult> Details(int id, [FromServices] Customer360Service svc, [FromServices] LoyaltyService loyalty)
    {
        var c = await Db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        if (!await CanAccessAsync(c, edit: false)) return NotAllowed();
        ViewBag.Nps = await Db.NpsResponses.Where(r => r.CustomerId == id && r.Score != null).OrderByDescending(r => r.RespondedAt).Select(r => r.Score).FirstOrDefaultAsync();
        if (c.LoyaltyTierId != null || c.LoyaltyPoints > 0) { var ls = await loyalty.SettingsAsync(); ViewBag.Loyalty = ls; ViewBag.LoyaltyTier = ls.TierById(c.LoyaltyTierId); ViewBag.LoyaltyNext = ls.NextTier(c.LoyaltyLifetime); }
        ViewBag.LoyaltyPoints = c.LoyaltyPoints; ViewBag.LoyaltyLifetime = c.LoyaltyLifetime;
        return View(await svc.BuildAsync(id));
    }

    public async Task<IActionResult> Create()
    {
        var n = await Db.Customers.IgnoreQueryFilters().CountAsync() + 1;
        var model = new Customer { Code = $"KH{n:0000}", OwnerId = CurrentUserId };
        while (await Db.Customers.IgnoreQueryFilters().AnyAsync(c => c.Code == model.Code)) model.Code = $"KH{++n:0000}";
        await LoadOwnersAsync(model.OwnerId); await LoadPriceListsAsync(model.PriceListId); await LoadSlaPoliciesAsync(model.SlaPolicyId);
        return View("Form", model);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var c = await Db.Customers.FindAsync(id);
        if (c == null) return NotFound();
        if (!await CanAccessAsync(c)) return NotAllowed();
        await LoadOwnersAsync(c.OwnerId); await LoadPriceListsAsync(c.PriceListId); await LoadSlaPoliciesAsync(c.SlaPolicyId);
        return View("Form", c);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Customer model)
    {
        if (await Db.Customers.IgnoreQueryFilters().AnyAsync(c => c.Code == model.Code && c.Id != model.Id))
            ModelState.AddModelError(nameof(model.Code), I18n.T("Mã khách hàng đã tồn tại"));
        // Chuẩn hoá MST (bỏ khoảng trắng / dấu chấm, đơn vị phụ thuộc dạng 0123456789-001) rồi so theo chữ số
        model.TaxCode = TaxLookupService.Normalize(model.TaxCode) ?? model.TaxCode?.Trim();
        var taxDigits = ImportService.Digits(model.TaxCode);
        if (taxDigits.Length >= 10)
        {
            var prefix = taxDigits[..10];
            var same = (await Db.Customers.IgnoreQueryFilters().Where(c => c.Id != model.Id && c.TaxCode != null && c.TaxCode.Contains(prefix))
                .Select(c => new { c.TaxCode, c.Name }).ToListAsync()).FirstOrDefault(c => ImportService.Digits(c.TaxCode) == taxDigits);
            if (same != null) ModelState.AddModelError(nameof(model.TaxCode), I18n.T("Mã số thuế đã thuộc về khách hàng khác") + $" ({same.Name})");
        }
        if (ModelState.IsValid) await DuplicateGuardAsync(nameof(Customer), model.Id, model.Name, model.TaxCode, model.Phone, model.Email);
        var (cfValues, cfErrors) = await CustomData.ReadAsync(Db, "Customer", Request.Form);
        foreach (var cfErr in cfErrors) ModelState.AddModelError(string.Empty, cfErr);
        if (!ModelState.IsValid)
        {
            await LoadOwnersAsync(model.OwnerId); await LoadPriceListsAsync(model.PriceListId); await LoadSlaPoliciesAsync(model.SlaPolicyId);
            return View("Form", model);
        }

        if (model.Id == 0)
        {
            model.OwnerId = ResolveOwner(model.OwnerId);
            model.CreatedAt = DateTime.Now;
            if (!User.SeesAll()) { model.PriceListId = null; model.SlaPolicyId = null; }
            Db.Customers.Add(model);
        }
        else
        {
            var c = await Db.Customers.FindAsync(model.Id);
            if (c == null) return NotFound();
            if (!await CanAccessAsync(c)) return NotAllowed();
            c.Code = model.Code; c.Name = model.Name; c.Type = model.Type; c.Industry = model.Industry; c.TaxCode = model.TaxCode;
            c.Phone = model.Phone; c.Email = model.Email; c.Website = model.Website; c.Address = model.Address; c.City = model.City;
            c.EmployeeCount = model.EmployeeCount; c.Description = model.Description; c.OwnerId = KeepOwner(c.OwnerId, model.OwnerId);
            if (User.SeesAll()) { c.PriceListId = model.PriceListId; c.SlaPolicyId = model.SlaPolicyId; }   // chỉ quản lý gắn bảng giá, SLA
            c.UpdatedAt = DateTime.Now;
        }
        await Db.SaveChangesAsync();
        await CustomData.SaveAsync(Db, model.Id, cfValues);
        TempData["Success"] = I18n.T("Đã lưu khách hàng.");
        return RedirectToAction(nameof(Details), new { id = model.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await Db.Customers.FindAsync(id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        var used = await Db.Contacts.AnyAsync(x => x.CustomerId == id) || await Db.Opportunities.AnyAsync(x => x.CustomerId == id)
                   || await Db.Quotes.AnyAsync(x => x.CustomerId == id) || await Db.Activities.AnyAsync(x => x.CustomerId == id) || await Db.Tickets.AnyAsync(x => x.CustomerId == id) || await Db.LoyaltyTransactions.AnyAsync(x => x.CustomerId == id);
        if (used)
        {
            TempData["Error"] = I18n.T("Khách hàng đã có dữ liệu liên quan (liên hệ, cơ hội, báo giá, hoạt động). Hãy chuyển phân loại sang 'Ngừng giao dịch'.");
            return RedirectToAction(nameof(Details), new { id });
        }
        await HttpContext.RequestServices.GetRequiredService<CampaignService>().DetachAsync(customerId: c.Id);
        Db.Customers.Remove(c);
        await Db.SaveChangesAsync();
        await DeleteAttachmentsAsync(AttachmentTarget.Customer, c.Id);
        TempData["Success"] = I18n.T("Đã xóa khách hàng.");
        return RedirectToAction(nameof(Index));
    }
}
