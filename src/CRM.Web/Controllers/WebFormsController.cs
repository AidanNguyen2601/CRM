using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Quản lý form thu lead trên website: cấu hình, mã nhúng, thống kê lead theo form và theo nguồn quảng cáo (UTM)</summary>
[Authorize(Roles = AppRoles.ManagerGroup)]
public class WebFormsController : CrmControllerBase
{
    public WebFormsController(CrmDbContext db) : base(db) { }

    public record FormStat(int Leads, int Converted, int Qualified);
    public record UtmRow(string Source, string Medium, string Campaign, int Leads, int Converted, decimal Won);

    public async Task<IActionResult> Index(int days = 90)
    {
        days = days is 30 or 90 or 365 ? days : 90;
        var since = DateTime.Today.AddDays(-days);
        ViewBag.Days = days;
        var forms = await Db.WebForms.AsNoTracking().Include(f => f.Owner).OrderByDescending(f => f.IsActive).ThenBy(f => f.Name).ToListAsync();
        var leads = Db.Leads.IgnoreQueryFilters().Where(l => l.WebFormId != null);
        ViewBag.Stats = (await leads.GroupBy(l => l.WebFormId!.Value)
            .Select(g => new { g.Key, N = g.Count(), C = g.Count(l => l.Status == LeadStatus.Converted), Q = g.Count(l => l.Status == LeadStatus.Qualified) }).ToListAsync())
            .ToDictionary(x => x.Key, x => new FormStat(x.N, x.C, x.Q));

        // Hiệu quả theo nguồn quảng cáo (mọi lead có UTM, kể cả nhập tay / Excel), doanh số từ cơ hội thắng sau chuyển đổi
        var utm = await Db.Leads.IgnoreQueryFilters().Where(l => l.CreatedAt >= since && (l.UtmSource != null || l.WebFormId != null))
            .Select(l => new { Source = l.UtmSource ?? "(direct)", Medium = l.UtmMedium ?? "", Campaign = l.UtmCampaign ?? "", l.Status, l.ConvertedOpportunityId }).ToListAsync();
        var oppIds = utm.Where(x => x.ConvertedOpportunityId.HasValue).Select(x => x.ConvertedOpportunityId!.Value).Distinct().ToList();
        var won = oppIds.Count == 0 ? new Dictionary<int, decimal>()
            : await Db.Opportunities.IgnoreQueryFilters().Where(o => oppIds.Contains(o.Id) && o.Stage == OpportunityStage.ClosedWon).ToDictionaryAsync(o => o.Id, o => o.Amount);
        ViewBag.Utm = utm.GroupBy(x => (x.Source, x.Medium, x.Campaign))
            .Select(g => new UtmRow(g.Key.Source, g.Key.Medium, g.Key.Campaign, g.Count(), g.Count(x => x.Status == LeadStatus.Converted),
                g.Where(x => x.ConvertedOpportunityId.HasValue).Sum(x => won.GetValueOrDefault(x.ConvertedOpportunityId!.Value))))
            .OrderByDescending(r => r.Leads).Take(30).ToList();
        return View(forms);
    }

    public async Task<IActionResult> Edit(int? id)
    {
        var f = id.HasValue ? await Db.WebForms.FindAsync(id) : new WebForm
        {
            Title = I18n.T("Đăng ký tư vấn"), Intro = I18n.T("Để lại thông tin, chuyên viên của chúng tôi sẽ liên hệ trong thời gian sớm nhất."),
            SubmitText = I18n.T("Gửi thông tin")
        };
        if (f == null) return NotFound();
        await LoadAsync(f);
        return View(f);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(WebForm model)
    {
        model.Name = model.Name?.Trim() ?? "";
        if (await Db.WebForms.AnyAsync(f => f.Name == model.Name && f.Id != model.Id)) ModelState.AddModelError(nameof(model.Name), I18n.T("Tên form đã tồn tại"));
        if (!string.IsNullOrWhiteSpace(model.RedirectUrl) && !(Uri.TryCreate(model.RedirectUrl.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
            ModelState.AddModelError(nameof(model.RedirectUrl), I18n.T("Địa chỉ trang phải bắt đầu bằng http:// hoặc https://"));
        if (!string.IsNullOrWhiteSpace(model.Accent) && !System.Text.RegularExpressions.Regex.IsMatch(model.Accent.Trim(), "^#[0-9a-fA-F]{6}$")) model.Accent = null;
        if (model.OwnerId.HasValue && !await Db.Users.AnyAsync(x => x.Id == model.OwnerId && x.IsActive)) ModelState.AddModelError(nameof(model.OwnerId), I18n.T("Người phụ trách không hợp lệ"));
        ModelState.Remove(nameof(model.Token));
        if (!ModelState.IsValid) { await LoadAsync(model); return View("Edit", model); }

        WebForm f;
        if (model.Id == 0) { f = new WebForm { Token = WebFormService.NewToken(), CreatedAt = DateTime.Now }; Db.WebForms.Add(f); }
        else f = await Db.WebForms.FindAsync(model.Id) ?? throw new InvalidOperationException();
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        f.Name = model.Name; f.IsActive = model.IsActive; f.Title = Clean(model.Title); f.Intro = Clean(model.Intro); f.SubmitText = Clean(model.SubmitText);
        f.SuccessMessage = Clean(model.SuccessMessage); f.RedirectUrl = Clean(model.RedirectUrl); f.Accent = Clean(model.Accent);
        f.AskCompany = model.AskCompany; f.AskJobTitle = model.AskJobTitle; f.AskMessage = model.AskMessage; f.RequireEmail = model.RequireEmail; f.RequirePhone = model.RequirePhone;
        f.AskConsent = model.AskConsent; f.ConsentText = Clean(model.ConsentText); f.Source = model.Source; f.CampaignId = model.CampaignId; f.OwnerId = model.OwnerId;
        f.BranchId = model.BranchId; f.AllowedDomains = Clean(model.AllowedDomains);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu form \"{0}\".", f.Name);
        return RedirectToAction(nameof(Edit), new { id = f.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var f = await Db.WebForms.FindAsync(id);
        if (f == null) return NotFound();
        if (await Db.Leads.IgnoreQueryFilters().AnyAsync(l => l.WebFormId == id))
        {
            f.IsActive = false;
            await Db.SaveChangesAsync();
            TempData["Success"] = I18n.T("Form \"{0}\" đã có lead nên được chuyển sang ngừng nhận (giữ thống kê).", f.Name);
        }
        else
        {
            Db.WebForms.Remove(f);
            await Db.SaveChangesAsync();
            TempData["Success"] = I18n.T("Đã xóa form \"{0}\".", f.Name);
        }
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Đổi mã form (mã cũ ngừng hoạt động – dùng khi bị spam)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Regenerate(int id)
    {
        var f = await Db.WebForms.FindAsync(id);
        if (f == null) return NotFound();
        f.Token = WebFormService.NewToken();
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã đổi mã form. Hãy cập nhật mã nhúng trên website.");
        return RedirectToAction(nameof(Edit), new { id });
    }

    private async Task LoadAsync(WebForm f)
    {
        ViewBag.Owners = new SelectList(await Db.UsersInScope.Where(u => u.IsActive).OrderBy(u => u.FullName).Select(u => new { u.Id, u.FullName }).ToListAsync(), "Id", "FullName", f.OwnerId);
        ViewBag.Campaigns = new SelectList(await Db.Campaigns.OrderByDescending(c => c.Id).Take(100).Select(c => new { c.Id, c.Name }).ToListAsync(), "Id", "Name", f.CampaignId);
        ViewBag.Branches = new SelectList(await Db.Branches.Where(b => b.IsActive).OrderBy(b => b.SortOrder).ThenBy(b => b.Name).Select(b => new { b.Id, b.Name }).ToListAsync(), "Id", "Name", f.BranchId);
        ViewBag.AutoAssign = (await HttpContext.RequestServices.GetRequiredService<LeadAssignService>().SettingsAsync()).Enabled;
        ViewBag.Base = $"{Request.Scheme}://{Request.Host}";
    }
}
