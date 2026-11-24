using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Infrastructure.Reports;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Lịch gửi báo cáo định kỳ qua email: quản trị thấy mọi lịch, quản lý thấy lịch do mình tạo</summary>
[Authorize(Roles = AppRoles.ManagerGroup)]
public class ReportSchedulesController : CrmControllerBase
{
    private readonly ReportAccessService _access;
    private readonly ReportScheduleService _svc;
    private readonly EmailService _mail;
    public ReportSchedulesController(CrmDbContext db, ReportAccessService access, ReportScheduleService svc, EmailService mail) : base(db) { _access = access; _svc = svc; _mail = mail; }

    public record ReportChoice(string Key, string Title, string Group);

    private ReportSubject Me => ReportAccessService.SubjectOf(User, HttpContext.RequestServices.GetRequiredService<BranchScope>());
    private IQueryable<ReportSchedule> Mine => User.IsInRole(AppRoles.Admin) ? Db.ReportSchedules : Db.ReportSchedules.Where(s => s.CreatedById == CurrentUserId);

    /// <summary>Báo cáo người dùng hiện tại được xem (chuẩn + động)</summary>
    private async Task<List<ReportChoice>> ChoicesAsync()
    {
        var me = Me; var list = new List<ReportChoice>();
        foreach (var d in StandardReports.All)
            if ((await _access.ResolveAsync(ReportAccessService.StdKey(d.Key), me)).CanView) list.Add(new(ReportAccessService.StdKey(d.Key), d.Title, d.Group));
        foreach (var r in await Db.CustomReports.AsNoTracking().OrderBy(r => r.Name).ToListAsync())
            if ((await _access.ResolveCustomAsync(r, me)).CanView) list.Add(new(ReportAccessService.CustomKey(r.Id), r.Name, I18n.T("Báo cáo động")));
        return list;
    }

    private async Task<string> TitleOfAsync(string key)
    {
        if (key.StartsWith("std:")) return StandardReports.Find(key[4..])?.Title ?? key;
        if (key.StartsWith("custom:") && int.TryParse(key[7..], out var id)) return await Db.CustomReports.Where(r => r.Id == id).Select(r => r.Name).FirstOrDefaultAsync() ?? key;
        return key;
    }

    public async Task<IActionResult> Index()
    {
        var list = await Mine.AsNoTracking().OrderByDescending(s => s.IsActive).ThenBy(s => s.Name).ToListAsync();
        var users = await Db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.FullName);
        ViewBag.Titles = new Dictionary<int, string>();
        foreach (var s in list) ((Dictionary<int, string>)ViewBag.Titles)[s.Id] = await TitleOfAsync(s.ReportKey);
        ViewBag.Users = users;
        ViewBag.MailReady = await _mail.IsReadyAsync();
        return View(list);
    }

    public async Task<IActionResult> Create(string? key)
    {
        var choices = await ChoicesAsync();
        var s = new ReportSchedule { ReportKey = choices.Any(c => c.Key == key) ? key! : choices.FirstOrDefault()?.Key ?? "", RecipientIds = CurrentUserId.ToString(), CreatedById = CurrentUserId };
        s.Name = choices.FirstOrDefault(c => c.Key == s.ReportKey)?.Title ?? "";
        await LoadAsync(s, choices);
        return View("Edit", s);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var s = await Mine.FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return NotFound();
        await LoadAsync(s, await ChoicesAsync());
        return View(s);
    }

    private async Task LoadAsync(ReportSchedule s, List<ReportChoice> choices)
    {
        ViewBag.Choices = choices;
        // Người nhận chọn được: người dùng đang hoạt động trong phạm vi của người tạo
        ViewBag.Recipients = await Db.UsersInScope.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.FullName).Select(u => new { u.Id, u.FullName, u.Email, u.Role }).ToListAsync();
        ViewBag.Selected = ReportScheduleService.ParseIds(s.RecipientIds);
        ViewBag.MailReady = await _mail.IsReadyAsync();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(ReportSchedule model, int[]? recipients)
    {
        var choices = await ChoicesAsync();
        var allowed = (await Db.UsersInScope.Where(u => u.IsActive).Select(u => u.Id).ToListAsync()).ToHashSet();
        var ids = (recipients ?? Array.Empty<int>()).Where(allowed.Contains).Distinct().ToArray();
        model.Name = model.Name?.Trim() ?? "";
        if (!choices.Any(c => c.Key == model.ReportKey)) ModelState.AddModelError(nameof(model.ReportKey), I18n.T("Chọn báo cáo bạn được xem."));
        if (ids.Length == 0) ModelState.AddModelError(string.Empty, I18n.T("Chọn ít nhất một người nhận."));
        if (!Enum.IsDefined(model.Frequency) || !Enum.IsDefined(model.Period)) return BadRequest();
        ModelState.Remove(nameof(model.RecipientIds));
        if (!ModelState.IsValid)
        {
            model.RecipientIds = string.Join(",", ids);
            await LoadAsync(model, choices);
            return View("Edit", model);
        }
        ReportSchedule s;
        if (model.Id == 0) { s = new ReportSchedule { CreatedById = CurrentUserId, CreatedAt = DateTime.Now }; Db.ReportSchedules.Add(s); }
        else s = await Mine.FirstOrDefaultAsync(x => x.Id == model.Id) ?? throw new InvalidOperationException();
        s.Name = model.Name; s.ReportKey = model.ReportKey; s.Frequency = model.Frequency; s.DayOfWeek = model.DayOfWeek; s.DayOfMonth = model.DayOfMonth;
        s.Hour = model.Hour; s.Period = model.Period; s.RecipientIds = string.Join(",", ids); s.IncludeExcel = model.IncludeExcel;
        s.PreviewRows = model.PreviewRows; s.IsActive = model.IsActive;
        s.NextRunAt = s.IsActive ? ReportScheduleService.NextRun(s, DateTime.Now) : null;
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu lịch gửi \"{0}\". Lần gửi tới: {1}.", s.Name, s.NextRunAt?.ToDateTime() ?? "–");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Gửi ngay (không chờ lịch) – email vào hàng đợi, không đổi lịch kế tiếp</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RunNow(int id)
    {
        var s = await Mine.FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return NotFound();
        var keepNext = s.NextRunAt;
        var r = await _svc.RunAsync(id);
        var after = await Db.ReportSchedules.FindAsync(id);
        if (after != null) { after.NextRunAt = keepNext; await Db.SaveChangesAsync(); }
        TempData[r.Sent > 0 ? "Success" : "Error"] = r.Message;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id)
    {
        var s = await Mine.FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return NotFound();
        s.IsActive = !s.IsActive;
        s.NextRunAt = s.IsActive ? ReportScheduleService.NextRun(s, DateTime.Now) : null;
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var s = await Mine.FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return NotFound();
        var files = HttpContext.RequestServices.GetRequiredService<AttachmentService>();
        foreach (var a in await Db.Attachments.Where(a => a.TargetType == AttachmentTarget.ReportSchedule && a.TargetId == id).ToListAsync()) await files.DeleteAsync(a);
        Db.ReportSchedules.Remove(s);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa lịch gửi \"{0}\".", s.Name);
        return RedirectToAction(nameof(Index));
    }
}
