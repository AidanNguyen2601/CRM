using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Tự động hoá: quy tắc "khi … thì …" và tự phân bổ lead (quản lý / quản trị)</summary>
[Authorize(Roles = AppRoles.ManagerGroup)]
public class AutomationController : CrmControllerBase
{
    private readonly LeadAssignService _assign;
    private readonly SettingsStore _settings;
    public AutomationController(CrmDbContext db, LeadAssignService assign, SettingsStore settings) : base(db) { _assign = assign; _settings = settings; }

    public async Task<IActionResult> Index()
    {
        ViewBag.Logs = await Db.AutomationLogs.AsNoTracking().OrderByDescending(l => l.Id).Take(30).ToListAsync();
        ViewBag.RuleNames = await Db.AutomationRules.ToDictionaryAsync(r => r.Id, r => r.Name);
        return View(await Db.AutomationRules.Include(r => r.AssigneeUser).AsNoTracking().OrderByDescending(r => r.IsActive).ThenBy(r => r.Trigger).ThenBy(r => r.Name).ToListAsync());
    }

    public async Task<IActionResult> Edit(int? id)
    {
        var r = id.HasValue ? await Db.AutomationRules.FindAsync(id.Value) : new AutomationRule { Title = "{{Ten}}" };
        if (r == null) return NotFound();
        await LoadAsync(r);
        return View(r);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(AutomationRule model)
    {
        if (model.Assignee == AutomationAssignee.User && model.AssigneeUserId == null)
            ModelState.AddModelError(nameof(model.AssigneeUserId), I18n.T("Vui lòng chọn người nhận."));
        if (AutomationService.TimeTriggers.Contains(model.Trigger) && model.Trigger != AutomationTrigger.ContactBirthday && model.Days is null or < 1)
            ModelState.AddModelError(nameof(model.Days), I18n.T("Vui lòng nhập số ngày (≥ 1)."));
        if (model.Action == AutomationAction.EmailCustomer && model.EmailTemplateId == null && string.IsNullOrWhiteSpace(model.Message))
            ModelState.AddModelError(nameof(model.Message), I18n.T("Chọn mẫu email hoặc nhập nội dung email."));
        ModelState.Remove(nameof(model.AssigneeUser));
        if (!ModelState.IsValid) { await LoadAsync(model); return View(model); }
        AutomationRule r;
        if (model.Id == 0) { r = new AutomationRule(); Db.AutomationRules.Add(r); }
        else r = await Db.AutomationRules.FindAsync(model.Id) ?? throw new InvalidOperationException();
        r.Name = model.Name.Trim(); r.IsActive = model.IsActive; r.Trigger = model.Trigger;
        r.Days = AutomationService.TimeTriggers.Contains(model.Trigger) ? model.Days : null;
        r.Condition = model.Trigger is AutomationTrigger.OpportunityStageChanged or AutomationTrigger.TicketCreated or AutomationTrigger.LeadCreated ? model.Condition : null;
        r.Action = model.Action; r.Assignee = model.Assignee; r.AssigneeUserId = model.Assignee == AutomationAssignee.User ? model.AssigneeUserId : null;
        r.Title = model.Title?.Trim(); r.Message = model.Message?.Trim(); r.TaskDueDays = model.TaskDueDays; r.TaskPriority = model.TaskPriority;
        r.EmailTemplateId = model.Action == AutomationAction.EmailCustomer ? model.EmailTemplateId : null;
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu quy tắc \"{0}\".", r.Name);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id)
    {
        var r = await Db.AutomationRules.FindAsync(id);
        if (r == null) return NotFound();
        r.IsActive = !r.IsActive;
        await Db.SaveChangesAsync();
        TempData["Success"] = r.IsActive ? I18n.T("Đã bật quy tắc \"{0}\".", r.Name) : I18n.T("Đã tắt quy tắc \"{0}\".", r.Name);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await Db.AutomationRules.FindAsync(id);
        if (r == null) return NotFound();
        Db.AutomationLogs.RemoveRange(await Db.AutomationLogs.Where(l => l.RuleId == id).ToListAsync());
        Db.AutomationRules.Remove(r);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa quy tắc \"{0}\".", r.Name);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Chạy ngay các quy tắc theo thời gian (không chờ lượt quét hằng giờ)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RunNow()
    {
        var n = await HttpContext.RequestServices.GetRequiredService<AutomationService>().RunScheduledAsync();
        TempData["Success"] = I18n.T("Đã chạy quy tắc theo thời gian: {0} lượt thực hiện.", n);
        return RedirectToAction(nameof(Index));
    }

    private async Task LoadAsync(AutomationRule r)
    {
        ViewBag.Users = new SelectList(await Db.UsersInScope.Where(u => u.IsActive).OrderBy(u => u.FullName).Select(u => new { u.Id, u.FullName }).ToListAsync(), "Id", "FullName", r.AssigneeUserId);
        ViewBag.Templates = new SelectList(await Db.EmailTemplates.Where(t => t.IsActive && t.Code == null).OrderBy(t => t.Name).Select(t => new { t.Id, t.Name }).ToListAsync(), "Id", "Name", r.EmailTemplateId);
    }

    // ======================= Tự phân bổ lead =======================

    public async Task<IActionResult> LeadAssign()
    {
        ViewBag.Users = await Db.UsersInScope.Where(u => u.IsActive && (u.Role == AppRoles.Sales || u.Role == AppRoles.Manager)).OrderBy(u => u.FullName).ToListAsync();
        ViewBag.Unassigned = await Db.Leads.CountAsync(l => l.OwnerId == null && l.Status != LeadStatus.Converted);
        return View(await _assign.SettingsAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> LeadAssign(bool enabled, string method, int[] userIds)
    {
        var s = await _assign.SettingsAsync();
        var valid = await Db.UsersInScope.Where(u => u.IsActive && userIds.Contains(u.Id)).Select(u => u.Id).ToListAsync();
        s.Enabled = enabled; s.Method = method is "least" or "source" ? method : "roundrobin"; s.UserIds = valid;
        s.SourceRules = new();
        foreach (var src in Enum.GetValues<LeadSource>())
            if (int.TryParse(Request.Form[$"src_{(int)src}"], out var uid) && valid.Contains(uid)) s.SourceRules[(int)src] = uid;
        await _settings.SaveAsync(LeadAssignSettings.Key, s);
        TempData["Success"] = I18n.T("Đã lưu cấu hình phân bổ lead.");
        return RedirectToAction(nameof(LeadAssign));
    }

    /// <summary>Phân bổ các lead chưa có người phụ trách (hoặc các lead được chọn) theo cấu hình</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignLeads(int[]? ids)
    {
        var q = Db.Leads.Where(l => l.Status != LeadStatus.Converted);
        q = ids is { Length: > 0 } ? q.Where(l => ids.Contains(l.Id)) : q.Where(l => l.OwnerId == null);
        var leads = await q.ToListAsync();
        var n = await _assign.AssignAsync(leads, CurrentUserId);
        if (n == 0)
        {
            TempData["Error"] = I18n.T("Chưa bật tự phân bổ hoặc chưa chọn nhân viên nhận lead.");
            return RedirectToAction(nameof(LeadAssign));
        }
        await Db.SaveChangesAsync();
        _assign.NotifyAssigned(leads, CurrentUserId);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã phân bổ {0} lead.", n);
        return ids is { Length: > 0 } ? RedirectToAction("Index", "Leads") : RedirectToAction(nameof(LeadAssign));
    }
}
