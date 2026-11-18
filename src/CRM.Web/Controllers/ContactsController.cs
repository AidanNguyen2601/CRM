using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

public class ContactsController : CrmControllerBase
{
    public ContactsController(CrmDbContext db) : base(db) { }

    public async Task<IActionResult> Index([FromQuery] ListFilter f, int? customerId)
    {
        var q = Db.Contacts.VisibleTo(User).Include(c => c.Customer).Include(c => c.Owner).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(f.Keyword))
        {
            var k = f.Keyword.Trim();
            q = q.Where(c => c.FullName.Contains(k) || (c.Email != null && c.Email.Contains(k)) || (c.Phone != null && c.Phone.Contains(k))
                             || (c.Customer != null && c.Customer.Name.Contains(k)));
        }
        if (customerId.HasValue) q = q.Where(c => c.CustomerId == customerId);
        if (f.OwnerId.HasValue) q = q.Where(c => c.OwnerId == f.OwnerId);

        f.Page = Math.Max(1, f.Page);
        var total = await q.CountAsync();
        var items = await q.OrderBy(c => c.FullName).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();
        await LoadOwnersAsync(f.OwnerId);
        ViewBag.Filter = f;
        ViewBag.CustomerId = customerId;
        return View(new PagedList<Contact>(items, total, f.Page, f.PageSize));
    }

    public async Task<IActionResult> Create(int? customerId)
    {
        var model = new Contact { CustomerId = customerId, OwnerId = CurrentUserId };
        if (customerId.HasValue)
        {
            var c = await Db.Customers.FindAsync(customerId);
            if (c != null) model.OwnerId = c.OwnerId;
        }
        await LoadLookups(model);
        return View("Form", model);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var c = await Db.Contacts.FindAsync(id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        await LoadLookups(c);
        return View("Form", c);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Contact model, string? returnTo)
    {
        if (!ModelState.IsValid)
        {
            await LoadLookups(model);
            return View("Form", model);
        }

        Contact entity;
        if (model.Id == 0)
        {
            model.OwnerId = ResolveOwner(model.OwnerId);
            model.CreatedAt = DateTime.Now;
            Db.Contacts.Add(model);
            entity = model;
        }
        else
        {
            var c = await Db.Contacts.FindAsync(model.Id);
            if (c == null) return NotFound();
            if (!User.CanAccess(c)) return NotAllowed();
            c.FullName = model.FullName; c.JobTitle = model.JobTitle; c.CustomerId = model.CustomerId; c.Email = model.Email;
            c.Phone = model.Phone; c.BirthDate = model.BirthDate; c.IsPrimary = model.IsPrimary; c.Notes = model.Notes;
            c.OwnerId = ResolveOwner(model.OwnerId);
            entity = c;
        }

        // Mỗi khách hàng chỉ có 1 liên hệ chính
        if (entity.IsPrimary && entity.CustomerId.HasValue)
        {
            var others = await Db.Contacts.Where(x => x.CustomerId == entity.CustomerId && x.Id != entity.Id && x.IsPrimary).ToListAsync();
            others.ForEach(x => x.IsPrimary = false);
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu người liên hệ.");
        return entity.CustomerId.HasValue && returnTo == "customer"
            ? RedirectToAction("Details", "Customers", new { id = entity.CustomerId })
            : RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await Db.Contacts.FindAsync(id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        if (await Db.Opportunities.AnyAsync(o => o.ContactId == id) || await Db.Quotes.AnyAsync(q => q.ContactId == id)
            || await Db.Activities.AnyAsync(a => a.ContactId == id) || await Db.Tickets.AnyAsync(t => t.ContactId == id))
        {
            TempData["Error"] = I18n.T("Người liên hệ đang gắn với cơ hội, báo giá hoặc hoạt động nên không thể xóa.");
            return RedirectToAction(nameof(Index));
        }
        await HttpContext.RequestServices.GetRequiredService<CampaignService>().DetachAsync(contactId: c.Id);
        Db.Contacts.Remove(c);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa người liên hệ.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>API cho dropdown phụ thuộc: liên hệ theo khách hàng</summary>
    [HttpGet]
    public async Task<IActionResult> ByCustomer(int customerId)
        => Json(await Db.Contacts.VisibleTo(User).Where(c => c.CustomerId == customerId)
            .OrderByDescending(c => c.IsPrimary).ThenBy(c => c.FullName)
            .Select(c => new { id = c.Id, name = c.FullName }).ToListAsync());

    private async Task LoadLookups(Contact c)
    {
        await LoadCustomersAsync(c.CustomerId);
        await LoadOwnersAsync(c.OwnerId);
    }
}
