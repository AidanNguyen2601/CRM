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
        var q = Db.Customers.VisibleTo(User).Include(c => c.Owner).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(f.Keyword))
        {
            var k = f.Keyword.Trim();
            q = q.Where(c => c.Name.Contains(k) || c.Code.Contains(k) || (c.TaxCode != null && c.TaxCode.Contains(k))
                             || (c.Phone != null && c.Phone.Contains(k)) || (c.Email != null && c.Email.Contains(k)));
        }
        if (f.Type.HasValue) q = q.Where(c => c.Type == f.Type);
        if (f.OwnerId.HasValue) q = q.Where(c => c.OwnerId == f.OwnerId);

        f.Page = Math.Max(1, f.Page);
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
        return View(new PagedList<Customer>(items, total, f.Page, f.PageSize));
    }

    public async Task<IActionResult> Details(int id)
    {
        var c = await Db.Customers.Include(x => x.Owner).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        var billable = Db.Invoices.Where(i => i.CustomerId == id && (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid || i.Status == InvoiceStatus.Paid));
        var open = billable.Where(i => i.Status != InvoiceStatus.Paid);
        return View(new CustomerDetailViewModel
        {
            Customer = c,
            Contracts = await Db.Contracts.Where(x => x.CustomerId == id).OrderByDescending(x => x.SignDate ?? x.CreatedAt).ToListAsync(),
            Invoiced = await billable.SumAsync(i => (decimal?)i.Total) ?? 0,
            Paid = await Db.Payments.Where(p => p.CustomerId == id).SumAsync(p => (decimal?)p.Amount) ?? 0,
            Receivable = await open.SumAsync(i => (decimal?)(i.Total - i.PaidAmount)) ?? 0,
            Overdue = await open.Where(i => i.DueDate != null && i.DueDate < DateTime.Today).SumAsync(i => (decimal?)(i.Total - i.PaidAmount)) ?? 0,
            Contacts = await Db.Contacts.Where(x => x.CustomerId == id).OrderByDescending(x => x.IsPrimary).ThenBy(x => x.FullName).ToListAsync(),
            Opportunities = await Db.Opportunities.Include(o => o.Owner).Where(o => o.CustomerId == id).OrderByDescending(o => o.CreatedAt).ToListAsync(),
            Quotes = await Db.Quotes.Where(q => q.CustomerId == id).OrderByDescending(q => q.QuoteDate).ToListAsync(),
            Activities = await Db.Activities.Include(a => a.Owner).Include(a => a.Contact).Where(a => a.CustomerId == id)
                .OrderByDescending(a => a.DueDate).Take(30).ToListAsync()
        });
    }

    public async Task<IActionResult> Create()
    {
        var n = await Db.Customers.CountAsync() + 1;
        var model = new Customer { Code = $"KH{n:0000}", OwnerId = CurrentUserId };
        while (await Db.Customers.AnyAsync(c => c.Code == model.Code)) model.Code = $"KH{++n:0000}";
        await LoadOwnersAsync(model.OwnerId);
        return View("Form", model);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var c = await Db.Customers.FindAsync(id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        await LoadOwnersAsync(c.OwnerId);
        return View("Form", c);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Customer model)
    {
        if (await Db.Customers.AnyAsync(c => c.Code == model.Code && c.Id != model.Id))
            ModelState.AddModelError(nameof(model.Code), I18n.T("Mã khách hàng đã tồn tại"));
        if (!string.IsNullOrWhiteSpace(model.TaxCode) && await Db.Customers.AnyAsync(c => c.TaxCode == model.TaxCode && c.Id != model.Id))
            ModelState.AddModelError(nameof(model.TaxCode), I18n.T("Mã số thuế đã thuộc về khách hàng khác"));
        if (!ModelState.IsValid)
        {
            await LoadOwnersAsync(model.OwnerId);
            return View("Form", model);
        }

        if (model.Id == 0)
        {
            model.OwnerId = ResolveOwner(model.OwnerId);
            model.CreatedAt = DateTime.Now;
            Db.Customers.Add(model);
        }
        else
        {
            var c = await Db.Customers.FindAsync(model.Id);
            if (c == null) return NotFound();
            if (!User.CanAccess(c)) return NotAllowed();
            c.Code = model.Code; c.Name = model.Name; c.Type = model.Type; c.Industry = model.Industry; c.TaxCode = model.TaxCode;
            c.Phone = model.Phone; c.Email = model.Email; c.Website = model.Website; c.Address = model.Address; c.City = model.City;
            c.EmployeeCount = model.EmployeeCount; c.Description = model.Description; c.OwnerId = ResolveOwner(model.OwnerId);
            c.UpdatedAt = DateTime.Now;
        }
        await Db.SaveChangesAsync();
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
                   || await Db.Quotes.AnyAsync(x => x.CustomerId == id) || await Db.Activities.AnyAsync(x => x.CustomerId == id);
        if (used)
        {
            TempData["Error"] = I18n.T("Khách hàng đã có dữ liệu liên quan (liên hệ, cơ hội, báo giá, hoạt động). Hãy chuyển phân loại sang 'Ngừng giao dịch'.");
            return RedirectToAction(nameof(Details), new { id });
        }
        Db.Customers.Remove(c);
        await Db.SaveChangesAsync();
        await DeleteAttachmentsAsync(AttachmentTarget.Customer, c.Id);
        TempData["Success"] = I18n.T("Đã xóa khách hàng.");
        return RedirectToAction(nameof(Index));
    }
}
