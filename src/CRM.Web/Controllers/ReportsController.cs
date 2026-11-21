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
    /// <summary>Nhóm báo cáo chuẩn (null với báo cáo động)</summary>
    public string? Group { get; set; }
    /// <summary>Quyền hiệu lực và mô tả vùng dữ liệu đang áp</summary>
    public ReportGrant? Grant { get; set; }
    public string? ScopeText { get; set; }
    /// <summary>Hiện ô lọc người phụ trách (vùng dữ liệu không giới hạn "của mình")</summary>
    public bool ShowOwnerFilter { get; set; }
}

/// <summary>Báo cáo chuẩn (viết sẵn) và báo cáo động (SQL / View do quản trị định nghĩa); phân quyền từng báo cáo + vùng dữ liệu</summary>
public class ReportsController : CrmControllerBase
{
    private readonly CustomReportService _custom;
    private readonly ReportAccessService _access;
    public ReportsController(CrmDbContext db, CustomReportService custom, ReportAccessService access) : base(db) { _custom = custom; _access = access; }

    private ReportSubject Me => ReportAccessService.SubjectOf(User, HttpContext.RequestServices.GetRequiredService<BranchScope>());

    public async Task<IActionResult> Index()
    {
        var me = Me;
        var grants = new Dictionary<string, ReportGrant>();
        foreach (var d in StandardReports.All) grants[ReportAccessService.StdKey(d.Key)] = await _access.ResolveAsync(ReportAccessService.StdKey(d.Key), me);
        var custom = await Db.CustomReports.AsNoTracking().OrderBy(r => r.Category).ThenBy(r => r.Name).ToListAsync();
        foreach (var r in custom) grants[ReportAccessService.CustomKey(r.Id)] = await _access.ResolveCustomAsync(r, me);
        ViewBag.Standard = StandardReports.All.Where(d => grants[ReportAccessService.StdKey(d.Key)].CanView).ToList();
        ViewBag.Custom = User.IsInRole(AppRoles.Admin) ? custom : custom.Where(r => grants[ReportAccessService.CustomKey(r.Id)].CanView).ToList();
        ViewBag.Grants = grants;
        return View();
    }

    /// <summary>Chặn khi không có quyền; ngược lại áp vùng dữ liệu cho request và trả về mô tả</summary>
    private IActionResult? Denied(ReportGrant g, bool export)
    {
        if (g.CanView && (!export || g.CanExport)) return null;
        TempData["Error"] = export && g.CanView ? I18n.T("Bạn không được phân quyền xuất Excel báo cáo này.") : g.DenyReason ?? I18n.T("Bạn không có quyền xem báo cáo này.");
        return RedirectToAction(nameof(Index));
    }

    private static ReportFilters ApplyOwnerFlag(ReportFilters flags, ReportGrant g) => g.OwnOnly == true ? flags & ~ReportFilters.Owner : flags;

    // ================== BÁO CÁO CHUẨN ==================

    public async Task<IActionResult> Run(string id, ReportFilter f)
    {
        var def = StandardReports.Find(id);
        if (def == null) return NotFound();
        var g = await _access.ResolveAsync(ReportAccessService.StdKey(id), Me);
        if (Denied(g, false) is { } deny) return deny;
        var scopeText = _access.Apply(g, User);
        var flags = ApplyOwnerFlag(def.Filters, g);
        Defaults(f, flags);
        if (g.OwnOnly == true) f.OwnerId = null;
        var m = new ReportPageModel
        {
            Title = def.Title, Description = def.Description, Icon = def.Icon, Filters = flags, Filter = f, Group = def.Group,
            RouteValues = new { id }, ExportAction = nameof(Export), Grant = g, ScopeText = scopeText,
            ShowOwnerFilter = g.OwnOnly == false || (g.OwnOnly == null && (User.SeesAll() || (User.IsSupportDesk() && def.Group == StandardReports.GroupSupport))),
            Result = await def.Run(new ReportContext { Db = Db, User = User, Filter = f, OwnOnly = g.OwnOnly, Scope = HttpContext.RequestServices.GetRequiredService<BranchScope>() })
        };
        await LoadOwnersAsync(f.OwnerId);
        return View("Result", m);
    }

    public async Task<IActionResult> Export(string id, ReportFilter f)
    {
        var def = StandardReports.Find(id);
        if (def == null) return NotFound();
        var g = await _access.ResolveAsync(ReportAccessService.StdKey(id), Me);
        if (Denied(g, true) is { } deny) return deny;
        var scopeText = _access.Apply(g, User);
        var flags = ApplyOwnerFlag(def.Filters, g);
        Defaults(f, flags);
        if (g.OwnOnly == true) f.OwnerId = null;
        var r = await def.Run(new ReportContext { Db = Db, User = User, Filter = f, OwnOnly = g.OwnOnly, Scope = HttpContext.RequestServices.GetRequiredService<BranchScope>() });
        return Xlsx(def.Title, Subtitle(flags, f, scopeText), r);
    }

    // ================== BÁO CÁO ĐỘNG ==================

    public async Task<IActionResult> Custom(int id, ReportFilter f)
    {
        var rep = await Db.CustomReports.FindAsync(id);
        if (rep == null) return NotFound();
        var g = await _access.ResolveCustomAsync(rep, Me);
        if (Denied(g, false) is { } deny) return deny;
        var scopeText = _access.Apply(g, User);
        var m = new ReportPageModel
        {
            Title = rep.Name, Description = rep.Description, Icon = "receivable", Custom = rep, RouteValues = new { id }, ExportAction = nameof(CustomExport),
            Grant = g, ScopeText = scopeText, ShowOwnerFilter = g.OwnOnly == false || (g.OwnOnly == null && User.SeesAll())
        };
        var (sql, err) = await _custom.SqlForAsync(rep);
        if (sql != null)
        {
            m.Filters = ApplyOwnerFlag(FiltersFor(rep, sql), g);
            Defaults(f, m.Filters);
            if (g.OwnOnly == true) f.OwnerId = null;
            m.Sql = sql;
            (m.Result, err) = await _custom.RunAsync(sql, f, User, CustomReportService.DisplayRows, rep.ChartLabelColumn, rep.ChartValueColumn, g.OwnOnly);
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
        var g = await _access.ResolveCustomAsync(rep, Me);
        if (Denied(g, true) is { } deny) return deny;
        var scopeText = _access.Apply(g, User);
        var (sql, err) = await _custom.SqlForAsync(rep);
        if (sql == null) { TempData["Error"] = err; return RedirectToAction(nameof(Custom), new { id }); }
        var filters = ApplyOwnerFlag(FiltersFor(rep, sql), g);
        Defaults(f, filters);
        if (g.OwnOnly == true) f.OwnerId = null;
        var (r, runErr) = await _custom.RunAsync(sql, f, User, CustomReportService.ExportRows, ownOnly: g.OwnOnly);
        if (r == null) { TempData["Error"] = runErr; return RedirectToAction(nameof(Custom), new { id }); }
        return Xlsx(rep.Name, Subtitle(filters, f, scopeText), r);
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

    // ================== PHÂN QUYỀN BÁO CÁO ==================

    public record ReportItem(string Key, string Title, string Group);

    private async Task<List<ReportItem>> ReportItemsAsync()
    {
        var list = new List<ReportItem> { new(ReportAccess.AllReports, I18n.T("Mọi báo cáo (quy tắc chung)"), I18n.T("Chung")) };
        list.AddRange(StandardReports.All.Select(d => new ReportItem(ReportAccessService.StdKey(d.Key), d.Title, d.Group)));
        list.AddRange((await Db.CustomReports.AsNoTracking().OrderBy(r => r.Name).Select(r => new { r.Id, r.Name, r.Category }).ToListAsync())
            .Select(r => new ReportItem(ReportAccessService.CustomKey(r.Id), r.Name, I18n.T("Báo cáo động") + (string.IsNullOrEmpty(r.Category) ? "" : " · " + r.Category))));
        return list;
    }

    /// <summary>Phân quyền báo cáo: quy tắc theo báo cáo (key) + kiểm tra quyền hiệu lực của một người dùng (userId)</summary>
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Access(string? key, int? userId)
    {
        var items = await ReportItemsAsync();
        key = items.Any(i => i.Key == key) ? key : ReportAccess.AllReports;
        var users = await Db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.FullName).Select(u => new { u.Id, u.FullName, u.Username, u.Role, u.BranchId }).ToListAsync();
        var all = await Db.ReportAccesses.AsNoTracking().ToListAsync();
        ViewBag.Items = items;
        ViewBag.Key = key;
        ViewBag.Rules = all.Where(r => r.ReportKey == key).OrderBy(r => r.SubjectType).ThenBy(r => r.Subject).ToList();
        ViewBag.RuleCounts = all.GroupBy(r => r.ReportKey).ToDictionary(g => g.Key, g => g.Count());
        ViewBag.UserNames = users.ToDictionary(u => u.Id.ToString(), u => $"{u.FullName} ({u.Username})");
        ViewBag.Users = users.Select(u => (u.Id, Name: $"{u.FullName} ({u.Username}) – {ReportAccessService.RoleLabel(u.Role)}")).ToList();
        ViewBag.Snapshot = HttpContext.RequestServices.GetRequiredService<BranchScope>().Snapshot;

        // Kiểm tra quyền hiệu lực của một người dùng trên mọi báo cáo
        if (userId.HasValue && users.FirstOrDefault(u => u.Id == userId) is { } u)
        {
            var s = new ReportSubject(u.Id, u.Role, u.BranchId);
            var snap = (BranchSnapshot)ViewBag.Snapshot;
            var customs = await Db.CustomReports.AsNoTracking().ToDictionaryAsync(r => ReportAccessService.CustomKey(r.Id));
            var rows = new List<(ReportItem Item, ReportGrant Grant, string Scope)>();
            foreach (var it in items.Where(i => i.Key != ReportAccess.AllReports))
            {
                var g = customs.TryGetValue(it.Key, out var cr) ? await _access.ResolveCustomAsync(cr, s) : await _access.ResolveAsync(it.Key, s);
                rows.Add((it, g, ReportAccessService.Describe(g, s, snap)));
            }
            ViewBag.CheckUser = $"{u.FullName} ({u.Username}) – {ReportAccessService.RoleLabel(u.Role)} – {snap.NameOf(u.BranchId)}";
            ViewBag.CheckRows = rows;
            ViewBag.UserId = userId;
        }
        return View();
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AccessSave(string key, ReportAccessSubject subjectType, string? role, int? userId, bool canView, bool canExport,
        ReportDataScope scope, int[]? branchIds)
    {
        if (!(await ReportItemsAsync()).Any(i => i.Key == key)) return BadRequest();
        var subject = subjectType == ReportAccessSubject.Role ? role : userId?.ToString();
        if (subjectType == ReportAccessSubject.Role && (subject == null || !AppRoles.All.Contains(subject) || subject == AppRoles.Admin))
        {
            TempData["Error"] = I18n.T("Chọn vai trò cần phân quyền (quản trị hệ thống luôn được xem mọi báo cáo).");
            return RedirectToAction(nameof(Access), new { key });
        }
        if (subjectType == ReportAccessSubject.User && (userId == null || !await Db.Users.AnyAsync(u => u.Id == userId)))
        {
            TempData["Error"] = I18n.T("Chọn người dùng cần phân quyền.");
            return RedirectToAction(nameof(Access), new { key });
        }
        if (!Enum.IsDefined(scope)) return BadRequest();
        var snap = HttpContext.RequestServices.GetRequiredService<BranchScope>().Snapshot;
        var ids = (branchIds ?? Array.Empty<int>()).Where(snap.ById.ContainsKey).Distinct().ToArray();
        if (scope == ReportDataScope.Specific && ids.Length == 0)
        {
            TempData["Error"] = I18n.T("Vùng \"Các đơn vị chỉ định\" cần chọn ít nhất một đơn vị.");
            return RedirectToAction(nameof(Access), new { key });
        }
        var rule = await Db.ReportAccesses.FirstOrDefaultAsync(r => r.ReportKey == key && r.SubjectType == subjectType && r.Subject == subject);
        if (rule == null) { rule = new ReportAccess { ReportKey = key, SubjectType = subjectType, Subject = subject! }; Db.ReportAccesses.Add(rule); }
        rule.CanView = canView; rule.CanExport = canView && canExport; rule.Scope = scope;
        rule.BranchIds = scope == ReportDataScope.Specific ? string.Join(",", ids) : null;
        rule.UpdatedById = CurrentUserId; rule.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu phân quyền báo cáo.");
        return RedirectToAction(nameof(Access), new { key });
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AccessDelete(int id)
    {
        var rule = await Db.ReportAccesses.FindAsync(id);
        if (rule == null) return NotFound();
        Db.ReportAccesses.Remove(rule);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa quy tắc phân quyền.");
        return RedirectToAction(nameof(Access), new { key = rule.ReportKey });
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

    private string Subtitle(ReportFilters flags, ReportFilter f, string? scope = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(scope)) parts.Add(I18n.T("Vùng dữ liệu: {0}", scope));
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
