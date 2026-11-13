using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>
/// KPI bán hàng: chỉ tiêu (giao theo tháng) so với thực tế (tính tự động từ dữ liệu).
/// Nhân viên kinh doanh chỉ xem KPI của mình; quản lý / quản trị xem cả đội và giao chỉ tiêu.
/// </summary>
public class KpiController : CrmControllerBase
{
    private readonly KpiService _kpi;
    public KpiController(CrmDbContext db, KpiService kpi) : base(db) => _kpi = kpi;

    /// <summary>Bảng KPI theo kỳ. period = month | quarter | year</summary>
    public async Task<IActionResult> Index(string period = "month", int? year = null, int? month = null, int? quarter = null)
    {
        var today = DateTime.Today;
        var y = year ?? today.Year;
        var p = period switch
        {
            "year" => KpiPeriod.FullYear(y),
            "quarter" => KpiPeriod.Quarter(y, Math.Clamp(quarter ?? (today.Month - 1) / 3 + 1, 1, 4)),
            _ => KpiPeriod.Month(y, Math.Clamp(month ?? today.Month, 1, 12))
        };
        var users = await VisibleUsersAsync();
        var rows = (await _kpi.ComputeAsync(p, users))
            .OrderByDescending(r => r.Percent(KpiMetric.Revenue)).ThenByDescending(r => r.Actual[KpiMetric.Revenue]).ToList();

        ViewBag.Period = period is "year" or "quarter" ? period : "month";
        ViewBag.P = p;
        ViewBag.Total = KpiService.Total(rows, new AppUser { FullName = I18n.T("Toàn đội") });
        ViewBag.Years = YearOptions();
        return View(rows);
    }

    /// <summary>KPI 12 tháng của một nhân viên</summary>
    public async Task<IActionResult> Member(int id, int? year = null)
    {
        if (!CanViewUser(id)) return NotAllowed();
        var u = await Db.Users.FindAsync(id);
        if (u == null) return NotFound();
        var y = year ?? DateTime.Today.Year;
        ViewBag.Year = y;
        ViewBag.Years = YearOptions();
        ViewBag.YearRow = (await _kpi.ComputeAsync(KpiPeriod.FullYear(y), new[] { u }))[0];
        ViewBag.Months = await _kpi.MonthlyAsync(y, u);
        return View(u);
    }

    /// <summary>Giao chỉ tiêu: lưới nhân viên × 12 tháng cho một chỉ tiêu</summary>
    [Authorize(Roles = AppRoles.ManagerGroup)]
    public async Task<IActionResult> Targets(int? year = null, KpiMetric metric = KpiMetric.Revenue)
    {
        var y = year ?? DateTime.Today.Year;
        var users = await _kpi.SalesUsersAsync();
        var ids = users.Select(u => u.Id).ToList();
        ViewBag.Year = y;
        ViewBag.Metric = metric;
        ViewBag.Years = YearOptions();
        ViewBag.Targets = await Db.SalesTargets.Where(t => t.Year == y && ids.Contains(t.UserId)).ToListAsync();
        return View(users);
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTargets(int year, KpiMetric metric)
    {
        if (year < 2000 || year > 2100 || !Enum.IsDefined(metric)) return BadRequest();
        var userIds = (await _kpi.SalesUsersAsync()).Select(u => u.Id).ToHashSet();
        var existing = await Db.SalesTargets.Where(t => t.Year == year).ToListAsync();
        var changed = 0;
        // Ô nhập có tên v_{userId}_{month}
        foreach (var (key, raw) in Request.Form)
        {
            var parts = key.Split('_');
            if (parts.Length != 3 || parts[0] != "v" || !int.TryParse(parts[1], out var uid) || !int.TryParse(parts[2], out var m)) continue;
            if (!userIds.Contains(uid) || m < 1 || m > 12) continue;
            var text = raw.ToString().Trim();   // ô type=number luôn gửi số dạng chuẩn (dấu chấm thập phân)
            var value = decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v) ? Math.Max(0, v) : 0;
            if (SalesTarget.IsMoney(metric)) value *= 1_000_000m;   // ô nhập theo triệu đồng
            var t = existing.FirstOrDefault(x => x.UserId == uid && x.Month == m);
            if (t == null)
            {
                if (value == 0) continue;
                t = new SalesTarget { UserId = uid, Year = year, Month = m };
                Db.SalesTargets.Add(t);
                existing.Add(t);
            }
            if (t.Get(metric) == value) continue;
            t.Set(metric, value);
            t.UpdatedById = CurrentUserId; t.UpdatedAt = DateTime.Now;
            changed++;
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu chỉ tiêu {0} năm {1} ({2} ô thay đổi).", metric.GetDisplayName(), year, changed);
        return RedirectToAction(nameof(Targets), new { year, metric });
    }

    /// <summary>Sao chép toàn bộ chỉ tiêu năm trước sang năm này, điều chỉnh theo % tăng trưởng (ghi đè)</summary>
    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CopyFromPreviousYear(int year, decimal growth, KpiMetric metric = KpiMetric.Revenue)
    {
        if (year < 2001 || year > 2100 || growth < -90 || growth > 500) return BadRequest();
        var prev = await Db.SalesTargets.Where(t => t.Year == year - 1).AsNoTracking().ToListAsync();
        if (prev.Count == 0)
        {
            TempData["Error"] = I18n.T("Năm {0} chưa có chỉ tiêu để sao chép.", year - 1);
            return RedirectToAction(nameof(Targets), new { year, metric });
        }
        var cur = await Db.SalesTargets.Where(t => t.Year == year).ToListAsync();
        var f = 1 + growth / 100m;
        foreach (var src in prev)
        {
            var t = cur.FirstOrDefault(x => x.UserId == src.UserId && x.Month == src.Month);
            if (t == null) { t = new SalesTarget { UserId = src.UserId, Year = year, Month = src.Month }; Db.SalesTargets.Add(t); }
            foreach (var m in Enum.GetValues<KpiMetric>())
                t.Set(m, SalesTarget.IsMoney(m) ? Math.Round(src.Get(m) * f / 1_000_000m) * 1_000_000m : Math.Ceiling(src.Get(m) * f));
            t.UpdatedById = CurrentUserId; t.UpdatedAt = DateTime.Now;
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã sao chép chỉ tiêu năm {0} sang năm {1} (tăng trưởng {2}%).", year - 1, year, growth.ToString("0.##"));
        return RedirectToAction(nameof(Targets), new { year, metric });
    }

    // Nhân viên chỉ xem chính mình; quản lý xem tất cả
    private bool CanViewUser(int userId) => User.SeesAll() || userId == CurrentUserId;

    private async Task<List<AppUser>> VisibleUsersAsync()
    {
        var all = await _kpi.SalesUsersAsync();
        return User.SeesAll() ? all : all.Where(u => u.Id == CurrentUserId).ToList();
    }

    private static int[] YearOptions()
    {
        var y = DateTime.Today.Year;
        return Enumerable.Range(y - 3, 5).ToArray();
    }
}
