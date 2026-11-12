using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

public class LeadsController : CrmControllerBase
{
    public LeadsController(CrmDbContext db) : base(db) { }

    public async Task<IActionResult> Index([FromQuery] LeadFilter f)
    {
        var q = Db.Leads.VisibleTo(User).Include(l => l.Owner).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(f.Keyword))
        {
            var k = f.Keyword.Trim();
            q = q.Where(l => l.FullName.Contains(k) || (l.Company != null && l.Company.Contains(k))
                             || (l.Email != null && l.Email.Contains(k)) || (l.Phone != null && l.Phone.Contains(k)));
        }
        if (f.Status.HasValue) q = q.Where(l => l.Status == f.Status);
        else q = q.Where(l => l.Status != LeadStatus.Converted && l.Status != LeadStatus.Unqualified);
        if (f.Source.HasValue) q = q.Where(l => l.Source == f.Source);
        if (f.OwnerId.HasValue) q = q.Where(l => l.OwnerId == f.OwnerId);

        f.Page = Math.Max(1, f.Page);
        var total = await q.CountAsync();
        var items = await q.OrderBy(l => l.Rating).ThenByDescending(l => l.CreatedAt)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();
        await LoadOwnersAsync(f.OwnerId);
        ViewBag.Filter = f;
        return View(new PagedList<Lead>(items, total, f.Page, f.PageSize));
    }

    public async Task<IActionResult> Details(int id)
    {
        var l = await Db.Leads.Include(x => x.Owner).Include(x => x.ConvertedCustomer).FirstOrDefaultAsync(x => x.Id == id);
        if (l == null) return NotFound();
        if (!User.CanAccess(l)) return NotAllowed();
        ViewBag.Activities = await Db.Activities.Include(a => a.Owner).Where(a => a.LeadId == id)
            .OrderByDescending(a => a.DueDate).ToListAsync();
        return View(l);
    }

    public async Task<IActionResult> Create()
    {
        await LoadOwnersAsync(CurrentUserId);
        return View("Form", new Lead { OwnerId = CurrentUserId });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var l = await Db.Leads.FindAsync(id);
        if (l == null) return NotFound();
        if (!User.CanAccess(l)) return NotAllowed();
        await LoadOwnersAsync(l.OwnerId);
        return View("Form", l);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Lead model)
    {
        if (string.IsNullOrWhiteSpace(model.Phone) && string.IsNullOrWhiteSpace(model.Email))
            ModelState.AddModelError(nameof(model.Phone), I18n.T("Cần ít nhất số điện thoại hoặc email"));
        if (!ModelState.IsValid)
        {
            await LoadOwnersAsync(model.OwnerId);
            return View("Form", model);
        }

        if (model.Id == 0)
        {
            model.OwnerId = ResolveOwner(model.OwnerId);
            model.CreatedAt = DateTime.Now;
            if (model.Status == LeadStatus.Converted) model.Status = LeadStatus.Qualified;
            Db.Leads.Add(model);
        }
        else
        {
            var l = await Db.Leads.FindAsync(model.Id);
            if (l == null) return NotFound();
            if (!User.CanAccess(l)) return NotAllowed();
            if (l.Status == LeadStatus.Converted)
            {
                TempData["Error"] = I18n.T("Lead đã chuyển đổi, không thể sửa.");
                return RedirectToAction(nameof(Details), new { id = l.Id });
            }
            l.FullName = model.FullName; l.Company = model.Company; l.JobTitle = model.JobTitle;
            l.Email = model.Email; l.Phone = model.Phone; l.Source = model.Source; l.Rating = model.Rating;
            l.Status = model.Status == LeadStatus.Converted ? l.Status : model.Status;
            l.EstimatedValue = model.EstimatedValue; l.Description = model.Description;
            l.OwnerId = ResolveOwner(model.OwnerId);
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu lead.");
        return RedirectToAction(nameof(Details), new { id = model.Id });
    }

    /// <summary>Chuyển đổi lead → khách hàng (+ người liên hệ) + cơ hội bán hàng</summary>
    public async Task<IActionResult> Convert(int id)
    {
        var l = await Db.Leads.FindAsync(id);
        if (l == null) return NotFound();
        if (!User.CanAccess(l)) return NotAllowed();
        if (l.Status == LeadStatus.Converted) return RedirectToAction(nameof(Details), new { id });

        // Gợi ý khách hàng trùng tên công ty
        var match = string.IsNullOrWhiteSpace(l.Company) ? null
            : await Db.Customers.FirstOrDefaultAsync(c => c.Name == l.Company);
        await LoadCustomersAsync(match?.Id);
        return View(new LeadConvertViewModel
        {
            LeadId = id, Lead = l, ExistingCustomerId = match?.Id,
            NewCustomerName = match == null ? (l.Company ?? l.FullName) : null,
            OpportunityName = $"{l.Company ?? l.FullName} – {l.Description?.Split('.')[0] ?? I18n.T("Cơ hội mới")}",
            OpportunityAmount = l.EstimatedValue
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Convert(LeadConvertViewModel vm)
    {
        var l = await Db.Leads.FindAsync(vm.LeadId);
        if (l == null) return NotFound();
        if (!User.CanAccess(l)) return NotAllowed();
        if (l.Status == LeadStatus.Converted) return RedirectToAction(nameof(Details), new { id = l.Id });

        if (vm.ExistingCustomerId == null && string.IsNullOrWhiteSpace(vm.NewCustomerName))
            ModelState.AddModelError(nameof(vm.NewCustomerName), I18n.T("Chọn khách hàng có sẵn hoặc nhập tên khách hàng mới"));
        if (vm.CreateOpportunity && string.IsNullOrWhiteSpace(vm.OpportunityName))
            ModelState.AddModelError(nameof(vm.OpportunityName), I18n.T("Nhập tên cơ hội"));
        if (!ModelState.IsValid)
        {
            vm.Lead = l;
            await LoadCustomersAsync(vm.ExistingCustomerId);
            return View(vm);
        }

        // 1. Khách hàng
        Customer customer;
        if (vm.ExistingCustomerId.HasValue)
        {
            customer = await Db.Customers.FindAsync(vm.ExistingCustomerId) ?? throw new InvalidOperationException();
        }
        else
        {
            customer = new Customer
            {
                Code = await NextCustomerCodeAsync(), Name = vm.NewCustomerName!.Trim(), Type = CustomerType.Prospect,
                Phone = l.Phone, Email = l.Email, OwnerId = l.OwnerId, Description = l.Description
            };
            Db.Customers.Add(customer);
            await Db.SaveChangesAsync();
        }

        // 2. Người liên hệ
        var contact = new Contact
        {
            FullName = l.FullName, JobTitle = l.JobTitle, Email = l.Email, Phone = l.Phone, CustomerId = customer.Id,
            OwnerId = l.OwnerId, IsPrimary = !await Db.Contacts.AnyAsync(c => c.CustomerId == customer.Id)
        };
        Db.Contacts.Add(contact);
        await Db.SaveChangesAsync();

        // 3. Cơ hội
        Opportunity? opp = null;
        if (vm.CreateOpportunity)
        {
            opp = new Opportunity
            {
                Name = vm.OpportunityName!.Trim(), CustomerId = customer.Id, ContactId = contact.Id, Amount = vm.OpportunityAmount,
                Stage = OpportunityStage.Qualification, Probability = (int)OpportunityStage.Qualification,
                ExpectedCloseDate = vm.ExpectedCloseDate, OwnerId = l.OwnerId, Source = l.Source, Description = l.Description
            };
            Db.Opportunities.Add(opp);
            await Db.SaveChangesAsync();
        }

        // 4. Cập nhật lead + chuyển các hoạt động của lead sang khách hàng
        l.Status = LeadStatus.Converted;
        l.ConvertedAt = DateTime.Now;
        l.ConvertedCustomerId = customer.Id;
        l.ConvertedOpportunityId = opp?.Id;
        var acts = await Db.Activities.Where(a => a.LeadId == l.Id).ToListAsync();
        foreach (var a in acts) { a.CustomerId = customer.Id; a.ContactId = contact.Id; a.OpportunityId = opp?.Id; }
        await Db.SaveChangesAsync();

        TempData["Success"] = opp != null ? I18n.T("Đã chuyển đổi lead thành khách hàng {0} và tạo cơ hội bán hàng.", customer.Name) : I18n.T("Đã chuyển đổi lead thành khách hàng {0}.", customer.Name);
        return opp != null
            ? RedirectToAction("Details", "Opportunities", new { id = opp.Id })
            : RedirectToAction("Details", "Customers", new { id = customer.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var l = await Db.Leads.FindAsync(id);
        if (l == null) return NotFound();
        if (!User.CanAccess(l)) return NotAllowed();
        if (await Db.Activities.AnyAsync(a => a.LeadId == id))
        {
            TempData["Error"] = I18n.T("Lead đã có hoạt động chăm sóc. Hãy chuyển trạng thái 'Không phù hợp' thay vì xóa.");
            return RedirectToAction(nameof(Details), new { id });
        }
        Db.Leads.Remove(l);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa lead.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<string> NextCustomerCodeAsync()
    {
        var n = await Db.Customers.CountAsync() + 1;
        string code;
        do code = $"KH{n++:0000}"; while (await Db.Customers.AnyAsync(c => c.Code == code));
        return code;
    }
}
