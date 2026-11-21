using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

public class OpportunitiesController : CrmControllerBase
{
    public OpportunitiesController(CrmDbContext db) : base(db) { }

    /// <summary>Danh sách cơ hội dạng bảng</summary>
    public async Task<IActionResult> Index([FromQuery] OpportunityFilter f)
    {
        var q = Db.Opportunities.VisibleTo(User, Db).Include(o => o.Customer).Include(o => o.Owner).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(f.Keyword))
        {
            var k = f.Keyword.Trim();
            q = q.Where(o => o.Name.Contains(k) || (o.Customer != null && o.Customer.Name.Contains(k)));
        }
        q = f.Scope switch
        {
            "won" => q.Where(o => o.Stage == OpportunityStage.ClosedWon),
            "lost" => q.Where(o => o.Stage == OpportunityStage.ClosedLost),
            "all" => q,
            _ => q.Where(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost)
        };
        if (f.Stage.HasValue) q = q.Where(o => o.Stage == f.Stage);
        if (f.OwnerId.HasValue) q = q.Where(o => o.OwnerId == f.OwnerId);

        f.Page = Math.Max(1, f.Page);
        q = q.WithTag(Db, f.TagId);
        var total = await q.CountAsync();
        ViewBag.TotalAmount = await q.SumAsync(o => o.Amount);
        var items = await q.OrderBy(o => o.ExpectedCloseDate).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();
        await LoadOwnersAsync(f.OwnerId);
        ViewBag.Filter = f;
        await LoadRowExtrasAsync("Opportunity", items.Select(x => x.Id));
        return View(new PagedList<Opportunity>(items, total, f.Page, f.PageSize));
    }

    /// <summary>Pipeline dạng Kanban theo giai đoạn</summary>
    public async Task<IActionResult> Pipeline(int? ownerId)
    {
        var q = Db.Opportunities.VisibleTo(User, Db).Include(o => o.Customer).Include(o => o.Owner)
            .Where(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost);
        if (ownerId.HasValue) q = q.Where(o => o.OwnerId == ownerId);
        await LoadOwnersAsync(ownerId);
        ViewBag.OwnerId = ownerId;
        return View(await q.OrderBy(o => o.ExpectedCloseDate).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var o = await Db.Opportunities.Include(x => x.Customer).Include(x => x.Contact).Include(x => x.Owner).Include(x => x.Campaign)
            .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (o == null) return NotFound();
        if (!await CanAccessAsync(o, edit: false)) return NotAllowed();
        return View(new OpportunityDetailViewModel
        {
            Opportunity = o,
            Activities = await Db.Activities.Include(a => a.Owner).Where(a => a.OpportunityId == id).OrderByDescending(a => a.DueDate).ToListAsync(),
            Quotes = await Db.Quotes.Where(q => q.OpportunityId == id).OrderByDescending(q => q.QuoteDate).ToListAsync(),
            Plans = await Db.BusinessPlans.Where(p => p.OpportunityId == id).OrderByDescending(p => p.PlanDate).ThenByDescending(p => p.Id).ToListAsync()
        });
    }

    public async Task<IActionResult> Create(int? customerId, int? campaignId)
    {
        var model = new Opportunity
        {
            CustomerId = customerId, OwnerId = CurrentUserId, ExpectedCloseDate = DateTime.Today.AddDays(30),
            Probability = (int)OpportunityStage.Prospecting
        };
        if (campaignId.HasValue && await Db.Campaigns.FindAsync(campaignId) is { } camp)
        {
            model.CampaignId = camp.Id;
            model.Source = Campaign.LeadSourceFor(camp.Channel);
            model.Name = camp.Name;
        }
        await LoadLookups(model);
        return View("Form", model);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var o = await Db.Opportunities.FindAsync(id);
        if (o == null) return NotFound();
        if (!await CanAccessAsync(o)) return NotAllowed();
        await LoadLookups(o);
        return View("Form", o);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Opportunity model)
    {
        if (model.Stage == OpportunityStage.ClosedLost && string.IsNullOrWhiteSpace(model.LostReason))
            ModelState.AddModelError(nameof(model.LostReason), I18n.T("Vui lòng nhập lý do thua"));
        var (cfValues, cfErrors) = await CustomData.ReadAsync(Db, "Opportunity", Request.Form);
        foreach (var cfErr in cfErrors) ModelState.AddModelError(string.Empty, cfErr);
        if (!ModelState.IsValid)
        {
            await LoadLookups(model);
            return View("Form", model);
        }

        Opportunity entity;
        if (model.Id == 0)
        {
            model.OwnerId = ResolveOwner(model.OwnerId);
            model.CreatedAt = DateTime.Now;
            Db.Opportunities.Add(model);
            entity = model;
            // Cơ hội phát sinh từ chiến dịch → đối tượng tương ứng của khách hàng được ghi nhận "Chuyển đổi"
            if (model.CampaignId.HasValue && model.CustomerId.HasValue)
            {
                var members = await Db.CampaignMembers.Where(m => m.CampaignId == model.CampaignId && m.CustomerId == model.CustomerId
                    && m.Status != CampaignMemberStatus.Unsubscribed && m.Status != CampaignMemberStatus.Converted).ToListAsync();
                members.ForEach(m => { m.Status = CampaignMemberStatus.Converted; m.LastContactAt = DateTime.Now; });
            }
        }
        else
        {
            var o = await Db.Opportunities.FindAsync(model.Id);
            if (o == null) return NotFound();
            if (!await CanAccessAsync(o)) return NotAllowed();
            o.Name = model.Name; o.CustomerId = model.CustomerId; o.ContactId = model.ContactId; o.Stage = model.Stage;
            o.Amount = model.Amount; o.Probability = model.Probability; o.ExpectedCloseDate = model.ExpectedCloseDate;
            o.Source = model.Source; o.CampaignId = model.CampaignId; o.LostReason = model.LostReason; o.Description = model.Description;
            o.OwnerId = KeepOwner(o.OwnerId, model.OwnerId);
            entity = o;
        }
        ApplyStageRules(entity);
        await Db.SaveChangesAsync();
        await CustomData.SaveAsync(Db, model.Id, cfValues);
        TempData["Success"] = I18n.T("Đã lưu cơ hội.");
        return RedirectToAction(nameof(Details), new { id = entity.Id });
    }

    /// <summary>Đổi giai đoạn nhanh (từ Kanban hoặc trang chi tiết)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeStage(int id, OpportunityStage stage, string? lostReason, string? returnTo)
    {
        var o = await Db.Opportunities.Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == id);
        if (o == null) return NotFound();
        if (!await CanAccessAsync(o)) return NotAllowed();

        if (stage == OpportunityStage.ClosedLost && string.IsNullOrWhiteSpace(lostReason))
        {
            TempData["Error"] = I18n.T("Vui lòng nhập lý do thua.");
        }
        else
        {
            o.Stage = stage;
            o.Probability = (int)stage;
            if (stage == OpportunityStage.ClosedLost) o.LostReason = lostReason;
            ApplyStageRules(o);
            await Db.SaveChangesAsync();
            TempData["Success"] = I18n.T("Cơ hội \"{0}\" chuyển sang: {1}.", o.Name, stage.GetDisplayName());
        }
        return returnTo == "pipeline" ? RedirectToAction(nameof(Pipeline)) : RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var o = await Db.Opportunities.FindAsync(id);
        if (o == null) return NotFound();
        if (!User.CanAccess(o)) return NotAllowed();
        if (await Db.Quotes.AnyAsync(q => q.OpportunityId == id) || await Db.Activities.AnyAsync(a => a.OpportunityId == id))
        {
            TempData["Error"] = I18n.T("Cơ hội đã có báo giá hoặc hoạt động. Hãy đóng cơ hội ở trạng thái 'Thua' thay vì xóa.");
            return RedirectToAction(nameof(Details), new { id });
        }
        Db.Opportunities.Remove(o);
        await Db.SaveChangesAsync();
        await DeleteAttachmentsAsync(AttachmentTarget.Opportunity, o.Id);
        TempData["Success"] = I18n.T("Đã xóa cơ hội.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Quy tắc khi đổi giai đoạn: ngày chốt, xác suất, nâng khách tiềm năng thành khách hàng.</summary>
    private void ApplyStageRules(Opportunity o)
    {
        if (o.IsClosed)
        {
            o.ClosedAt ??= DateTime.Now;
            o.Probability = (int)o.Stage;
            if (o.Stage == OpportunityStage.ClosedWon && o.CustomerId.HasValue)
            {
                var c = o.Customer ?? Db.Customers.Find(o.CustomerId);
                if (c is { Type: CustomerType.Prospect }) c.Type = CustomerType.Customer;
            }
        }
        else
        {
            o.ClosedAt = null;
            o.LostReason = null;
        }
    }

    private async Task LoadLookups(Opportunity o)
    {
        await LoadCustomersAsync(o.CustomerId);
        await LoadContactsAsync(o.CustomerId, o.ContactId);
        await LoadOwnersAsync(o.OwnerId);
        ViewBag.Campaigns = await CampaignsController.CampaignOptionsAsync(Db, o.CampaignId);
    }
}
