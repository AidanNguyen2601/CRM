using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Chính sách SLA (thời hạn theo độ ưu tiên, giờ làm việc, dừng khi chờ khách, báo cấp trên) và lịch làm việc / ngày lễ</summary>
[Authorize(Roles = AppRoles.ManagerGroup)]
public class SlaController : CrmControllerBase
{
    private readonly SettingsStore _settings;
    public SlaController(CrmDbContext db, SettingsStore settings) : base(db) => _settings = settings;

    public async Task<IActionResult> Index()
    {
        ViewBag.Calendar = await _settings.GetAsync<SlaCalendarSettings>(SlaCalendarSettings.Key);
        ViewBag.Usage = new Dictionary<string, Dictionary<int, int>>
        {
            ["customers"] = await Db.Customers.IgnoreQueryFilters().Where(c => c.SlaPolicyId != null).GroupBy(c => c.SlaPolicyId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N),
            ["contracts"] = await Db.Contracts.IgnoreQueryFilters().Where(c => c.SlaPolicyId != null).GroupBy(c => c.SlaPolicyId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N),
            ["open"] = await Db.Tickets.IgnoreQueryFilters().Where(t => t.SlaPolicyId != null && (t.Status == TicketStatus.New || t.Status == TicketStatus.InProgress || t.Status == TicketStatus.WaitingCustomer))
                .GroupBy(t => t.SlaPolicyId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N)
        };
        return View(await Db.SlaPolicies.AsNoTracking().OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync());
    }

    public async Task<IActionResult> Edit(int? id)
    {
        var p = id.HasValue ? await Db.SlaPolicies.FindAsync(id) : new SlaPolicy();
        if (p == null) return NotFound();
        return View(p);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(SlaPolicy model)
    {
        model.Name = model.Name?.Trim() ?? "";
        if (await Db.SlaPolicies.AnyAsync(p => p.Name == model.Name && p.Id != model.Id))
            ModelState.AddModelError(nameof(model.Name), I18n.T("Tên chính sách đã tồn tại"));
        foreach (var pr in Enum.GetValues<TicketPriority>())
        {
            var (a, b) = model.For(pr);
            if (a <= 0 || b <= 0 || a > 2000 || b > 2000) ModelState.AddModelError(string.Empty, I18n.T("Thời hạn mức \"{0}\" phải lớn hơn 0 và không quá 2000 giờ.", pr.GetDisplayName()));
            else if (b < a) ModelState.AddModelError(string.Empty, I18n.T("Mức \"{0}\": hạn giải quyết phải lớn hơn hoặc bằng hạn phản hồi.", pr.GetDisplayName()));
        }
        if (model.IsDefault && !model.IsActive) ModelState.AddModelError(nameof(model.IsActive), I18n.T("Chính sách mặc định phải đang dùng."));
        if (!ModelState.IsValid) return View("Edit", model);

        SlaPolicy p;
        if (model.Id == 0) { p = new SlaPolicy { CreatedAt = DateTime.Now }; Db.SlaPolicies.Add(p); }
        else p = await Db.SlaPolicies.FindAsync(model.Id) ?? throw new InvalidOperationException();
        p.Name = model.Name; p.Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim();
        p.IsActive = model.IsActive; p.IsDefault = model.IsDefault; p.BusinessHours = model.BusinessHours; p.PauseWhenWaiting = model.PauseWhenWaiting; p.Escalate = model.Escalate;
        p.UrgentResponse = model.UrgentResponse; p.UrgentResolve = model.UrgentResolve; p.HighResponse = model.HighResponse; p.HighResolve = model.HighResolve;
        p.NormalResponse = model.NormalResponse; p.NormalResolve = model.NormalResolve; p.LowResponse = model.LowResponse; p.LowResolve = model.LowResolve;
        if (p.IsDefault)
            foreach (var other in await Db.SlaPolicies.Where(x => x.IsDefault && x.Id != model.Id).ToListAsync()) other.IsDefault = false;
        await Db.SaveChangesAsync();
        SlaEngine.Invalidate();
        TempData["Success"] = I18n.T("Đã lưu chính sách SLA \"{0}\". Áp dụng cho phiếu tạo mới hoặc đổi độ ưu tiên từ bây giờ.", p.Name);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var p = await Db.SlaPolicies.FindAsync(id);
        if (p == null) return NotFound();
        var used = await Db.Customers.IgnoreQueryFilters().CountAsync(c => c.SlaPolicyId == id) + await Db.Contracts.IgnoreQueryFilters().CountAsync(c => c.SlaPolicyId == id);
        if (used > 0 || p.IsDefault)
        {
            TempData["Error"] = p.IsDefault ? I18n.T("Không xóa được chính sách mặc định – hãy chọn chính sách mặc định khác trước.")
                : I18n.T("Chính sách đang gắn với {0} khách hàng / hợp đồng – hãy bỏ gắn hoặc chuyển sang ngừng dùng.", used);
            return RedirectToAction(nameof(Index));
        }
        // Phiếu đã áp chính sách này giữ nguyên hạn đã tính, chỉ bỏ liên kết
        await Db.Tickets.IgnoreQueryFilters().Where(t => t.SlaPolicyId == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.SlaPolicyId, (int?)null));
        Db.SlaPolicies.Remove(p);
        await Db.SaveChangesAsync();
        SlaEngine.Invalidate();
        TempData["Success"] = I18n.T("Đã xóa chính sách SLA \"{0}\".", p.Name);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Calendar(int[]? workDays, string? start, string? end, string? breakStart, string? breakEnd, string? holidays)
    {
        var s = new SlaCalendarSettings
        {
            WorkDays = (workDays ?? Array.Empty<int>()).Where(d => d is >= 0 and <= 6).Distinct().OrderBy(d => d).ToArray(),
            Start = start?.Trim() ?? "", End = end?.Trim() ?? "",
            BreakStart = string.IsNullOrWhiteSpace(breakStart) ? null : breakStart.Trim(), BreakEnd = string.IsNullOrWhiteSpace(breakEnd) ? null : breakEnd.Trim(),
            Holidays = string.Join("\n", (holidays ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct())
        };
        var st = BusinessCalendar.Time(s.Start); var en = BusinessCalendar.Time(s.End);
        var errors = new List<string>();
        if (s.WorkDays.Length == 0) errors.Add(I18n.T("Chọn ít nhất một ngày làm việc."));
        if (st == null || en == null || en <= st) errors.Add(I18n.T("Giờ làm việc không hợp lệ (định dạng HH:mm, giờ kết thúc sau giờ bắt đầu)."));
        if ((s.BreakStart != null || s.BreakEnd != null) && (BusinessCalendar.Time(s.BreakStart) is not { } bs || BusinessCalendar.Time(s.BreakEnd) is not { } be || bs <= st || be >= en || be <= bs))
            errors.Add(I18n.T("Giờ nghỉ trưa phải nằm trong giờ làm việc (để trống nếu không nghỉ)."));
        var badDates = (s.Holidays ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d{2}/\d{2}(/\d{4})?$") || !DateTime.TryParseExact(l.Length == 5 ? l + "/2000" : l, "dd/MM/yyyy", null, System.Globalization.DateTimeStyles.None, out _)).ToList();
        if (badDates.Count > 0) errors.Add(I18n.T("Ngày lễ không hợp lệ: {0} (dùng dd/MM hoặc dd/MM/yyyy).", string.Join(", ", badDates.Take(5))));
        if (errors.Count > 0) { TempData["Error"] = string.Join(" ", errors); return RedirectToAction(nameof(Index)); }
        await _settings.SaveAsync(SlaCalendarSettings.Key, s);
        SlaEngine.Invalidate();
        TempData["Success"] = I18n.T("Đã lưu lịch làm việc.");
        return RedirectToAction(nameof(Index));
    }
}
