using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Infrastructure.Reports;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

public class ReportPageModel
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Icon { get; set; } = "trending";
    public ReportFilters Filters { get; set; }
    public ReportFilter Filter { get; set; } = new();
    public ReportResult? Result { get; set; }
    public string? Error { get; set; }
    public string ExportAction { get; set; } = "Export";
    public object RouteValues { get; set; } = new { };
    public CustomReport? Custom { get; set; }
    public string? Sql { get; set; }
}

/// <summary>Báo cáo chuẩn (viết sẵn) và báo cáo động (SQL / View do quản trị định nghĩa)</summary>
public class ReportsController : CrmControllerBase
{
    private readonly CustomReportService _custom;
    public ReportsController(CrmDbContext db, CustomReportService custom) : base(db) => _custom = custom;

    public async Task<IActionResult> Index()
    {
        ViewBag.Standard = StandardReports.All;
        var custom = await Db.CustomReports.AsNoTracking().OrderBy(r => r.Category).ThenBy(r => r.Name).ToListAsync();
        ViewBag.Custom = User.IsInRole(AppRoles.Admin) ? custom : custom.Where(r => r.CanRun(User)).ToList();
        return View();
    }

    // ================== BÁO CÁO CHUẨN ==================

    public async Task<IActionResult> Run(string id, ReportFilter f)
    {
        var def = StandardReports.Find(id);
        if (def == null) return NotFound();
        Defaults(f, def.Filters);
        var m = new ReportPageModel
        {
            Title = def.Title, Description = def.Description, Icon = def.Icon, Filters = def.Filters, Filter = f,
            RouteValues = new { id }, ExportAction = nameof(Export),
            Result = await def.Run(new ReportContext { Db = Db, User = User, Filter = f })
        };
        await LoadOwnersAsync(f.OwnerId);
        return View("Result", m);
    }

    public async Task<IActionResult> Export(string id, ReportFilter f)
    {
        var def = StandardReports.Find(id);
        if (def == null) return NotFound();
        Defaults(f, def.Filters);
        var r = await def.Run(new ReportContext { Db = Db, User = User, Filter = f });
        return Xlsx(def.Title, Subtitle(def.Filters, f), r);
    }

    // ================== BÁO CÁO ĐỘNG ==================

    public async Task<IActionResult> Custom(int id, ReportFilter f)
    {
        var rep = await Db.CustomReports.FindAsync(id);
        if (rep == null) return NotFound();
        if (!rep.CanRun(User) && !User.IsInRole(AppRoles.Admin)) return NotAllowed();
        var m = new ReportPageModel { Title = rep.Name, Description = rep.Description, Icon = "receivable", Custom = rep, RouteValues = new { id }, ExportAction = nameof(CustomExport) };
        var (sql, err) = await _custom.SqlForAsync(rep);
        if (sql != null)
        {
            m.Filters = FiltersFor(rep, sql);
            Defaults(f, m.Filters);
            m.Sql = sql;
            (m.Result, err) = await _custom.RunAsync(sql, f, User, CustomReportService.DisplayRows, rep.ChartLabelColumn, rep.ChartValueColumn);
        }
        m.Filter = f;
        m.Error = err;
        await LoadOwnersAsync(f.OwnerId);
        return View("Result", m);
    }

    public async Task<IActionResult> CustomExport(int id, ReportFilter f)
    {
        var rep = await Db.CustomReports.FindAsync(id);
        if (rep == null) return NotFound();
        if (!rep.CanRun(User) && !User.IsInRole(AppRoles.Admin)) return NotAllowed();
        var (sql, err) = await _custom.SqlForAsync(rep);
        if (sql == null) { TempData["Error"] = err; return RedirectToAction(nameof(Custom), new { id }); }
        var filters = FiltersFor(rep, sql);
        Defaults(f, filters);
        var (r, runErr) = await _custom.RunAsync(sql, f, User, CustomReportService.ExportRows);
        if (r == null) { TempData["Error"] = runErr; return RedirectToAction(nameof(Custom), new { id }); }
        return Xlsx(rep.Name, Subtitle(filters, f), r);
    }

    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> CustomCreate(CustomReportSource source = CustomReportSource.Sql)
    {
        ViewBag.Schema = await _custom.SchemaAsync();
        return View("CustomForm", new CustomReport
        {
            SourceType = source, AllowedRoles = "Admin,Manager",
            SqlText = source == CustomReportSource.Sql ? "SELECT TOP (100) *\nFROM vw_Opportunities\nWHERE (@SeesAll = 1 OR OwnerId = @CurrentUserId)\nORDER BY Amount DESC" : null,
            ViewName = source == CustomReportSource.View ? "vw_Opportunities" : null
        });
    }

    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> CustomEdit(int id)
    {
        var rep = await Db.CustomReports.FindAsync(id);
        if (rep == null) return NotFound();
        ViewBag.Schema = await _custom.SchemaAsync();
        return View("CustomForm", rep);
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CustomSave(CustomReport model, string[]? roles)
    {
        model.AllowedRoles = string.Join(",", (roles ?? Array.Empty<string>()).Where(r => AppRoles.All.Contains(r)).Append(AppRoles.Admin).Distinct());
        Normalize(model);
        var (sql, err) = await _custom.SqlForAsync(model);
        if (err != null) ModelState.AddModelError(model.SourceType == CustomReportSource.Sql ? nameof(model.SqlText) : nameof(model.ViewName), err);
        if (!ModelState.IsValid)
        {
            ViewBag.Schema = await _custom.SchemaAsync();
            return View("CustomForm", model);
        }

        CustomReport rep;
        if (model.Id == 0) { rep = model; rep.CreatedById = CurrentUserId; rep.CreatedAt = DateTime.Now; Db.CustomReports.Add(rep); }
        else
        {
            rep = await Db.CustomReports.FindAsync(model.Id) ?? throw new InvalidOperationException();
            var (createdAt, createdBy) = (rep.CreatedAt, rep.CreatedById);   // không lấy từ form
            Db.Entry(rep).CurrentValues.SetValues(model);
            rep.CreatedAt = createdAt;
            rep.CreatedById = createdBy ?? CurrentUserId;
            rep.UpdatedAt = DateTime.Now;
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu báo cáo {0}.", rep.Name);
        return RedirectToAction(nameof(Custom), new { id = rep.Id });
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CustomDelete(int id)
    {
        var rep = await Db.CustomReports.FindAsync(id);
        if (rep == null) return NotFound();
        Db.CustomReports.Remove(rep);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa báo cáo {0}.", rep.Name);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Chạy thử khi soạn báo cáo: trả về bảng kết quả (tối đa 100 dòng) hoặc thông báo lỗi</summary>
    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CustomPreview(CustomReport model, ReportFilter f)
    {
        Normalize(model);
        var (sql, err) = await _custom.SqlForAsync(model);
        ReportResult? r = null;
        if (sql != null)
        {
            Defaults(f, FiltersFor(model, sql));
            (r, err) = await _custom.RunAsync(sql, f, User, 100, model.ChartLabelColumn, model.ChartValueColumn);
        }
        ViewBag.Error = err;
        ViewBag.Sql = sql;
        return PartialView("_Preview", r);
    }

    // ================== TIỆN ÍCH ==================

    private static void Normalize(CustomReport m)
    {
        if (m.SourceType == CustomReportSource.Sql) { m.ViewName = m.ViewColumns = m.DateColumn = m.OwnerColumn = m.OrderBy = null; m.TopRows = null; }
        else m.SqlText = null;
    }

    /// <summary>Ô lọc cần hiện cho báo cáo động: theo tham số câu lệnh có dùng (hoặc cột ngày / người phụ trách của View)</summary>
    private static ReportFilters FiltersFor(CustomReport rep, string sql)
    {
        var used = CustomReportService.UsedParameters(sql);
        var f = ReportFilters.None;
        if (used.Contains("@FromDate") || used.Contains("@ToDate")) f |= ReportFilters.Period;
        if (used.Contains("@Year")) f |= ReportFilters.Year;
        if (used.Contains("@OwnerId")) f |= ReportFilters.Owner;
        if (used.Contains("@Keyword")) f |= ReportFilters.Keyword;
        return f;
    }

    private static void Defaults(ReportFilter f, ReportFilters flags)
    {
        var today = DateTime.Today;
        f.From ??= new DateTime(today.Year, 1, 1);
        f.To ??= today;
        if (f.To < f.From) (f.From, f.To) = (f.To, f.From);
        f.Year ??= today.Year;
        if (flags.HasFlag(ReportFilters.Top)) f.Top ??= 20;
        if (flags.HasFlag(ReportFilters.Days)) f.Days ??= 90;
    }

    private string Subtitle(ReportFilters flags, ReportFilter f)
    {
        var parts = new List<string>();
        if (flags.HasFlag(ReportFilters.Period)) parts.Add(I18n.T("Kỳ: {0} – {1}", f.FromDate.ToDate(), f.ToExclusive.AddDays(-1).ToDate()));
        if (flags.HasFlag(ReportFilters.Year)) parts.Add(I18n.T("Năm {0}", f.YearValue));
        if (flags.HasFlag(ReportFilters.Days)) parts.Add(I18n.T("{0} ngày tới", f.Days ?? 90));
        if (flags.HasFlag(ReportFilters.Keyword) && !string.IsNullOrWhiteSpace(f.Keyword)) parts.Add(I18n.T("Tìm: {0}", f.Keyword!));
        parts.Add(I18n.T("Xuất lúc {0} bởi {1}", DateTime.Now.ToString("dd/MM/yyyy HH:mm"), User.GetFullName()));
        return string.Join(" · ", parts);
    }

    private FileContentResult Xlsx(string title, string subtitle, ReportResult r)
    {
        var name = new string(title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
        return File(XlsxWriter.Write(title, subtitle, r), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"{name} {DateTime.Now:yyyyMMdd-HHmm}.xlsx");
    }
}
