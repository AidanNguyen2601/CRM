using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Dự báo doanh số theo kỳ: so chỉ tiêu, theo tháng, theo nhân viên, danh sách cơ hội theo nhóm dự báo và rủi ro dự báo</summary>
public class ForecastController : CrmControllerBase
{
    private readonly ForecastService _forecast;
    private readonly KpiService _kpi;
    public ForecastController(CrmDbContext db, ForecastService forecast, KpiService kpi) : base(db) { _forecast = forecast; _kpi = kpi; }

    /// <summary>period = quarter (mặc định) | month | year; userId = xem riêng một nhân viên (quản lý)</summary>
    public async Task<IActionResult> Index(string period = "quarter", int? year = null, int? month = null, int? quarter = null, int? userId = null)
    {
        var today = DateTime.Today;
        var y = year is >= 2000 and <= 2100 ? year.Value : today.Year;
        var p = period switch
        {
            "year" => KpiPeriod.FullYear(y),
            "month" => KpiPeriod.Month(y, Math.Clamp(month ?? today.Month, 1, 12)),
            _ => KpiPeriod.Quarter(y, Math.Clamp(quarter ?? (today.Month - 1) / 3 + 1, 1, 4))
        };
        var all = await _kpi.SalesUsersAsync();
        var team = User.SeesAll() ? all : all.Where(u => u.Id == CurrentUserId).ToList();
        var users = userId.HasValue ? team.Where(u => u.Id == userId).ToList() : team;
        if (users.Count == 0) users = team;

        ViewBag.Period = period is "year" or "month" ? period : "quarter";
        ViewBag.Team = team;
        ViewBag.UserId = userId.HasValue && users.Count == 1 ? userId : null;
        ViewBag.Years = Enumerable.Range(today.Year - 3, 5).ToArray();
        return View(await _forecast.ComputeAsync(p, users));
    }

    /// <summary>Người phụ trách / quản lý đổi nhóm dự báo của cơ hội (category trống = tự xếp theo xác suất)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetCategory(int id, ForecastCategory? category)
    {
        var o = await Db.Opportunities.FindAsync(id);
        if (o == null) return NotFound();
        if (!await CanAccessAsync(o)) return Request.Headers.Accept.ToString().Contains("json") ? Forbid() : NotAllowed();
        if (category.HasValue && !Enum.IsDefined(category.Value)) return BadRequest();
        o.ForecastCategory = category;
        await Db.SaveChangesAsync();
        var effective = ForecastService.CategoryOf(o);
        if (Request.Headers.Accept.ToString().Contains("json")) return Json(new { ok = true, category = (int)effective, label = effective.GetDisplayName(), manual = category.HasValue });
        TempData["Success"] = I18n.T("Đã chuyển \"{0}\" sang nhóm {1}.", o.Name, effective.GetDisplayName());
        return Redirect(Request.Headers.Referer.ToString() is { Length: > 0 } r && Url.IsLocalUrl(new Uri(r).PathAndQuery) ? new Uri(r).PathAndQuery : Url.Action(nameof(Index))!);
    }
}
