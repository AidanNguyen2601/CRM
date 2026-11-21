using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Infrastructure.Reports;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CRM.Web.Controllers;

/// <summary>Chính sách hoa hồng (AppSettings "commission"): cơ sở tính + bậc tỷ lệ theo % hoàn thành chỉ tiêu doanh số</summary>
public class CommissionSettings
{
    public const string Key = "commission";
    /// <summary>collection: tiền đã thu trong kỳ | revenue: doanh số (cơ hội thắng) trong kỳ</summary>
    public string Basis { get; set; } = "collection";
    public List<CommissionTier> Tiers { get; set; } = new()
    {
        new() { FromPercent = 0, Rate = 2 }, new() { FromPercent = 80, Rate = 3 }, new() { FromPercent = 100, Rate = 4 }, new() { FromPercent = 120, Rate = 5 }
    };

    /// <summary>Tỷ lệ áp dụng theo % hoàn thành chỉ tiêu (không có chỉ tiêu → bậc thấp nhất)</summary>
    public decimal RateFor(decimal attainment) =>
        Tiers.OrderBy(t => t.FromPercent).LastOrDefault(t => attainment >= t.FromPercent)?.Rate ?? Tiers.OrderBy(t => t.FromPercent).FirstOrDefault()?.Rate ?? 0;
}

public class CommissionTier
{
    public decimal FromPercent { get; set; }
    public decimal Rate { get; set; }
}

public class CommissionRow
{
    public AppUser User { get; init; } = null!;
    public decimal Basis { get; init; }
    public decimal Revenue { get; init; }
    public decimal Target { get; init; }
    public decimal Attainment => Target == 0 ? 0 : Math.Round(Revenue * 100m / Target, 1);
    public decimal Rate { get; init; }
    public decimal Amount => Math.Round(Basis * Rate / 100m, 0);
}

/// <summary>Hoa hồng kinh doanh theo kỳ: cơ sở tính (tiền thu / doanh số) × tỷ lệ theo bậc hoàn thành KPI doanh số</summary>
public class CommissionsController : CrmControllerBase
{
    private readonly KpiService _kpi;
    private readonly SettingsStore _settings;
    public CommissionsController(CrmDbContext db, KpiService kpi, SettingsStore settings) : base(db) { _kpi = kpi; _settings = settings; }

    private async Task<(KpiPeriod P, string Period, List<CommissionRow> Rows, CommissionSettings S)> BuildAsync(string period, int? year, int? month, int? quarter)
    {
        var today = DateTime.Today;
        var y = year ?? today.Year;
        var p = period switch
        {
            "year" => KpiPeriod.FullYear(y),
            "quarter" => KpiPeriod.Quarter(y, Math.Clamp(quarter ?? (today.Month - 1) / 3 + 1, 1, 4)),
            _ => KpiPeriod.Month(y, Math.Clamp(month ?? today.Month, 1, 12))
        };
        var s = await _settings.GetAsync<CommissionSettings>(CommissionSettings.Key);
        var users = await _kpi.SalesUsersAsync();
        if (!User.SeesAll()) users = users.Where(u => u.Id == CurrentUserId).ToList();
        var kpi = await _kpi.ComputeAsync(p, users);
        var rows = kpi.Select(r =>
        {
            var revenue = r.Actual[KpiMetric.Revenue];
            var target = r.Target[KpiMetric.Revenue];
            var att = target == 0 ? 0 : revenue * 100m / target;
            return new CommissionRow
            {
                User = r.User, Basis = s.Basis == "revenue" ? revenue : r.Actual[KpiMetric.Collection], Revenue = revenue, Target = target, Rate = s.RateFor(att)
            };
        }).OrderByDescending(r => r.Amount).ToList();
        return (p, period is "year" or "quarter" ? period : "month", rows, s);
    }

    public async Task<IActionResult> Index(string period = "month", int? year = null, int? month = null, int? quarter = null)
    {
        var (p, per, rows, s) = await BuildAsync(period, year, month, quarter);
        ViewBag.P = p; ViewBag.Period = per; ViewBag.Settings = s;
        ViewBag.Years = Enumerable.Range(DateTime.Today.Year - 3, 5).ToArray();
        return View(rows);
    }

    public async Task<IActionResult> Export(string period = "month", int? year = null, int? month = null, int? quarter = null)
    {
        var (p, _, rows, s) = await BuildAsync(period, year, month, quarter);
        var r = new ReportResult()
            .Col(I18n.T("Nhân viên"), ReportColumnKind.Text, false)
            .Col(s.Basis == "revenue" ? I18n.T("Doanh số (cơ sở tính)") : I18n.T("Tiền đã thu (cơ sở tính)"), ReportColumnKind.Money)
            .Col(I18n.T("Doanh số"), ReportColumnKind.Money).Col(I18n.T("Chỉ tiêu doanh số"), ReportColumnKind.Money)
            .Col(I18n.T("% hoàn thành"), ReportColumnKind.Percent, false).Col(I18n.T("Tỷ lệ hoa hồng (%)"), ReportColumnKind.Number, false)
            .Col(I18n.T("Hoa hồng"), ReportColumnKind.Money);
        foreach (var x in rows) r.Rows.Add(new object?[] { x.User.FullName, x.Basis, x.Revenue, x.Target, x.Attainment, x.Rate, x.Amount });
        r.AutoTotals(I18n.T("Tổng"));
        var title = I18n.T("Hoa hồng kinh doanh {0} – {1}", p.Start.ToDate(), p.End.AddDays(-1).ToDate());
        return File(XlsxWriter.Write(title, null, r), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"hoa-hong-{p.Start:yyyyMM}.xlsx");
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(string basis, decimal[] from, decimal[] rate)
    {
        var s = new CommissionSettings { Basis = basis == "revenue" ? "revenue" : "collection", Tiers = new() };
        for (var i = 0; i < Math.Min(from.Length, rate.Length); i++)
            if (from[i] >= 0 && rate[i] >= 0 && rate[i] <= 100) s.Tiers.Add(new CommissionTier { FromPercent = from[i], Rate = rate[i] });
        s.Tiers = s.Tiers.GroupBy(t => t.FromPercent).Select(g => g.Last()).OrderBy(t => t.FromPercent).ToList();
        if (s.Tiers.Count == 0) s.Tiers.Add(new CommissionTier { FromPercent = 0, Rate = 0 });
        await _settings.SaveAsync(CommissionSettings.Key, s);
        AuditTrail.Write(Db, "Settings", 0, "Update", I18n.T("Chính sách hoa hồng"),
            new[] { new AuditChange("Basis", null, s.Basis), new AuditChange("Tiers", null, string.Join("; ", s.Tiers.Select(t => $"≥{t.FromPercent}% → {t.Rate}%"))) });
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu chính sách hoa hồng.");
        return RedirectToAction(nameof(Index));
    }
}
