using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

public class ActivitiesController : CrmControllerBase
{
    public ActivitiesController(CrmDbContext db) : base(db) { }

    public async Task<IActionResult> Index([FromQuery] ActivityFilter f)
    {
        var now = DateTime.Now;
        var today = DateTime.Today;
        var q = Db.Activities.VisibleTo(User)
            .Include(a => a.Owner).Include(a => a.Customer).Include(a => a.Lead).Include(a => a.Opportunity).AsNoTracking();
        q = f.View switch
        {
            "today" => q.Where(a => a.Status == ActivityStatus.Planned && a.DueDate >= today && a.DueDate < today.AddDays(1)),
            "overdue" => q.Where(a => a.Status == ActivityStatus.Planned && a.DueDate < now),
            "done" => q.Where(a => a.Status == ActivityStatus.Completed),
            "all" => q,
            _ => q.Where(a => a.Status == ActivityStatus.Planned)
        };
        if (f.Type.HasValue) q = q.Where(a => a.Type == f.Type);
        if (f.OwnerId.HasValue) q = q.Where(a => a.OwnerId == f.OwnerId);

        var list = f.View == "done"
            ? await q.OrderByDescending(a => a.CompletedAt).Take(200).ToListAsync()
            : await q.OrderBy(a => a.DueDate).Take(200).ToListAsync();

        var mine = Db.Activities.VisibleTo(User).Where(a => a.Status == ActivityStatus.Planned);
        ViewBag.CountToday = await mine.CountAsync(a => a.DueDate >= today && a.DueDate < today.AddDays(1));
        ViewBag.CountOverdue = await mine.CountAsync(a => a.DueDate < now);
        ViewBag.CountPlanned = await mine.CountAsync();
        await LoadOwnersAsync(f.OwnerId);
        ViewBag.Filter = f;
        return View(list);
    }

    /// <summary>Tạo hoạt động, có thể gắn sẵn với khách hàng / lead / cơ hội</summary>
    public async Task<IActionResult> Create(int? customerId, int? leadId, int? opportunityId, ActivityType type = ActivityType.Call)
    {
        var a = new Activity
        {
            Type = type, CustomerId = customerId, LeadId = leadId, OpportunityId = opportunityId, OwnerId = CurrentUserId,
            DueDate = DateTime.Now.Date.AddHours(Math.Min(DateTime.Now.Hour + 1, 17))
        };
        if (opportunityId.HasValue)
        {
            var o = await Db.Opportunities.FindAsync(opportunityId);
            if (o != null) { a.CustomerId = o.CustomerId; a.ContactId = o.ContactId; }
        }
        await LoadLookups(a);
        return View("Form", a);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var a = await Db.Activities.FindAsync(id);
        if (a == null) return NotFound();
        if (!User.CanAccess(a)) return NotAllowed();
        await LoadLookups(a);
        return View("Form", a);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Activity model, string? returnUrl)
    {
        if (model.CustomerId == null && model.LeadId == null && model.OpportunityId == null)
            ModelState.AddModelError(string.Empty, I18n.T("Hoạt động cần gắn với ít nhất một khách hàng, lead hoặc cơ hội."));
        if (!ModelState.IsValid)
        {
            await LoadLookups(model);
            return View("Form", model);
        }

        if (model.Id == 0)
        {
            model.OwnerId = ResolveOwner(model.OwnerId);
            model.CreatedAt = DateTime.Now;
            if (model.Status == ActivityStatus.Completed) model.CompletedAt = DateTime.Now;
            Db.Activities.Add(model);
        }
        else
        {
            var a = await Db.Activities.FindAsync(model.Id);
            if (a == null) return NotFound();
            if (!User.CanAccess(a)) return NotAllowed();
            if (a.Status != ActivityStatus.Completed && model.Status == ActivityStatus.Completed) a.CompletedAt = DateTime.Now;
            if (model.Status != ActivityStatus.Completed) a.CompletedAt = null;
            a.Type = model.Type; a.Subject = model.Subject; a.DueDate = model.DueDate; a.Status = model.Status;
            a.Priority = model.Priority; a.CustomerId = model.CustomerId; a.ContactId = model.ContactId; a.LeadId = model.LeadId;
            a.OpportunityId = model.OpportunityId; a.Description = model.Description; a.OwnerId = ResolveOwner(model.OwnerId);
        }

        // Lead đang "Mới" có hoạt động → chuyển "Đã liên hệ"
        if (model.LeadId.HasValue)
        {
            var lead = await Db.Leads.FindAsync(model.LeadId);
            if (lead is { Status: LeadStatus.New }) lead.Status = LeadStatus.Contacted;
        }

        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu hoạt động.");
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Index));
    }

    /// <summary>Đánh dấu hoàn thành, kèm kết quả</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(int id, string? result, string? returnUrl)
    {
        var a = await Db.Activities.FindAsync(id);
        if (a == null) return NotFound();
        if (!User.CanAccess(a)) return NotAllowed();
        a.Status = ActivityStatus.Completed;
        a.CompletedAt = DateTime.Now;
        if (!string.IsNullOrWhiteSpace(result))
            a.Description = string.IsNullOrWhiteSpace(a.Description) ? result : a.Description + "\n" + result;
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã hoàn thành: {0}.", a.Subject);
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Index));
    }

    private async Task LoadLookups(Activity a)
    {
        await LoadCustomersAsync(a.CustomerId);
        await LoadContactsAsync(a.CustomerId, a.ContactId);
        await LoadOpportunitiesAsync(a.CustomerId, a.OpportunityId);
        await LoadOwnersAsync(a.OwnerId);
        ViewBag.Leads = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(
            await Db.Leads.VisibleTo(User).Where(l => l.Status != LeadStatus.Converted || l.Id == a.LeadId)
                .OrderBy(l => l.FullName).Select(l => new { l.Id, Name = l.FullName + (l.Company != null ? " – " + l.Company : "") }).ToListAsync(),
            "Id", "Name", a.LeadId);
    }
}
