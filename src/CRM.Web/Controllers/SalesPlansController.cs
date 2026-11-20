using System.Text.Json;
using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Trạng thái một hành động của kế hoạch (theo công việc đã giao hoặc đánh dấu thủ công)</summary>
public record PlanActionState(string Key, string Label, string Css, int Progress, bool Overdue, int? TaskId);

/// <summary>
/// Kế hoạch bán hàng theo kỳ của công ty / chi nhánh.
/// Quy trình: Nháp → Chờ duyệt → Đã duyệt (thực hiện, theo dõi) → Đã kết thúc; bị từ chối thì sửa và gửi lại.
/// Quản lý / quản trị lập và duyệt (không tự duyệt kế hoạch mình lập, trừ quản trị); nhân viên xem kế hoạch của đơn vị
/// và kế hoạch mình được phân bổ.
/// </summary>
public class SalesPlansController : CrmControllerBase
{
    private const decimal Million = 1_000_000m;
    private readonly SalesPlanService _svc;
    private readonly BranchScope _scope;
    private readonly NotificationService _notify;

    public SalesPlansController(CrmDbContext db, SalesPlanService svc, BranchScope scope, NotificationService notify) : base(db)
    {
        _svc = svc; _scope = scope; _notify = notify;
    }

    /// <summary>Kế hoạch xem được: thuộc các đơn vị đang xem, hoặc có mình trong danh sách phân bổ</summary>
    private IQueryable<SalesPlan> Visible()
    {
        if (!_scope.Active) return Db.SalesPlans;
        var ids = _scope.Ids;
        var me = CurrentUserId;
        return Db.SalesPlans.IgnoreQueryFilters().Where(p => ids.Contains(p.BranchId) || p.Members.Any(m => m.UserId == me));
    }

    /// <summary>Quản lý / quản trị thuộc đơn vị cấp trên hoặc cùng đơn vị với kế hoạch</summary>
    private bool CanManage(SalesPlan p) => User.SeesAll() && _scope.Allowed.Contains(p.BranchId);

    private bool CanDecide(SalesPlan p) =>
        CanManage(p) && p.Status == SalesPlanStatus.Pending && (User.IsInRole(AppRoles.Admin) || p.CreatedById != CurrentUserId);

    // ======================= Danh sách =======================

    public async Task<IActionResult> Index(SalesPlanFilter f)
    {
        var baseQ = Visible();
        if (f.Year.HasValue) baseQ = baseQ.Where(x => x.Year == f.Year);
        if (!string.IsNullOrWhiteSpace(f.Keyword)) baseQ = baseQ.Where(x => x.Number.Contains(f.Keyword) || x.Title.Contains(f.Keyword));
        ViewBag.Counts = await baseQ.GroupBy(x => x.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);

        var q = f.Status.HasValue ? baseQ.Where(x => x.Status == f.Status) : baseQ;
        var total = await q.CountAsync();
        var items = await q.Include(x => x.Months).Include(x => x.CreatedBy).AsNoTracking()
            .OrderByDescending(x => x.Year).ThenBy(x => x.FromMonth).ThenByDescending(x => x.Id)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();
        var progress = new Dictionary<int, SalesPlanProgress>();
        foreach (var p in items) progress[p.Id] = await _svc.ComputeAsync(p, detail: false);

        ViewBag.Progress = progress;
        ViewBag.Filter = f;
        ViewBag.Years = await Visible().Select(x => x.Year).Distinct().OrderByDescending(y => y).ToListAsync();
        ViewBag.CanCreate = User.SeesAll();
        return View(new PagedList<SalesPlan>(items, total, f.Page, f.PageSize));
    }

    // ======================= Chi tiết =======================

    public async Task<IActionResult> Details(int id)
    {
        var p = await Visible().Include(x => x.Months).Include(x => x.Products).Include(x => x.Members).ThenInclude(m => m.User)
            .Include(x => x.Actions).ThenInclude(a => a.Owner).Include(x => x.CreatedBy).Include(x => x.ApprovedBy)
            .AsSplitQuery().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();

        ViewBag.Progress = await _svc.ComputeAsync(p);
        ViewBag.ActionStates = await ActionStatesAsync(p.Actions);
        ViewBag.CanManage = CanManage(p);
        ViewBag.CanDecide = CanDecide(p);
        ViewBag.BranchPath = _scope.Snapshot.PathOf(p.BranchId);
        if (CanManage(p)) ViewBag.ActionOwners = await OwnerOptionsAsync(p.BranchId);
        return View(p);
    }

    private async Task<Dictionary<int, PlanActionState>> ActionStatesAsync(IEnumerable<SalesPlanAction> actions)
    {
        var list = actions.ToList();
        var taskIds = list.Where(a => a.ActivityId.HasValue).Select(a => a.ActivityId!.Value).ToList();
        var tasks = await Db.Activities.IgnoreQueryFilters().Where(t => taskIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Status, t.StartedAt, t.Progress, t.DueDate }).ToDictionaryAsync(t => t.Id);
        var now = DateTime.Now;
        var result = new Dictionary<int, PlanActionState>();
        foreach (var a in list)
        {
            if (a.ActivityId is int tid && tasks.TryGetValue(tid, out var t))
                result[a.Id] = t.Status switch
                {
                    ActivityStatus.Completed => new("done", I18n.T("Hoàn thành"), "bg-success", 100, false, tid),
                    ActivityStatus.Cancelled => new("cancelled", I18n.T("Đã hủy"), "bg-danger", t.Progress, false, tid),
                    _ when t.StartedAt.HasValue => new("doing", I18n.T("Đang làm"), "bg-primary", t.Progress, t.DueDate < now, tid),
                    _ => new("todo", I18n.T("Đã giao, chưa làm"), "bg-info text-dark", t.Progress, t.DueDate < now, tid)
                };
            else if (a.DoneAt.HasValue)
                result[a.Id] = new("done", I18n.T("Hoàn thành"), "bg-success", 100, false, null);
            else
                result[a.Id] = new("none", I18n.T("Chưa giao việc"), "bg-light text-dark border", 0, a.DueDate.HasValue && a.DueDate.Value.Date.AddDays(1) <= now, null);
        }
        return result;
    }

    // ======================= Lập / sửa =======================

    [Authorize(Roles = AppRoles.ManagerGroup)]
    public async Task<IActionResult> Create(int? year)
    {
        var y = year ?? DateTime.Today.Year;
        var branch = _scope.WorkingBranchId is int wb && _scope.Allowed.Contains(wb) ? wb : _scope.Allowed.FirstOrDefault();
        var vm = new SalesPlanFormViewModel
        {
            Year = y, BranchId = branch,
            Title = I18n.T("Kế hoạch bán hàng năm {0} – {1}", y, _scope.Snapshot.NameOf(branch))
        };
        FillMonths(vm, null);
        await LoadLookupsAsync(vm);
        return View("Form", vm);
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    public async Task<IActionResult> Edit(int id)
    {
        var p = await Db.SalesPlans.IgnoreQueryFilters().Include(x => x.Months).Include(x => x.Products).Include(x => x.Members)
            .AsSplitQuery().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (!CanManage(p)) return NotAllowed();
        if (!p.IsEditable)
        {
            TempData["Error"] = I18n.T("Chỉ sửa được kế hoạch ở trạng thái Nháp hoặc Bị từ chối. Hãy nhân bản để điều chỉnh.");
            return RedirectToAction(nameof(Details), new { id });
        }
        var vm = new SalesPlanFormViewModel
        {
            Id = p.Id, Number = p.Number, Title = p.Title, BranchId = p.BranchId, Year = p.Year, FromMonth = p.FromMonth, ToMonth = p.ToMonth,
            Objectives = p.Objectives,
            Products = p.Products.OrderBy(x => x.SortOrder).Select(x => new SalesPlanProductInput { Category = x.Category, Revenue = x.Revenue / Million, Note = x.Note }).ToList(),
            Members = p.Members.Select(m => new SalesPlanMemberInput { UserId = m.UserId, SharePercent = m.SharePercent }).ToList()
        };
        FillMonths(vm, p.Months);
        await LoadLookupsAsync(vm);
        return View("Form", vm);
    }

    private static void FillMonths(SalesPlanFormViewModel vm, IEnumerable<SalesPlanMonth>? data)
    {
        var src = (data ?? Enumerable.Empty<SalesPlanMonth>()).ToDictionary(x => x.Month);
        var posted = vm.Months.Where(x => x.Month is >= 1 and <= 12).GroupBy(x => x.Month).ToDictionary(g => g.Key, g => g.First());
        vm.Months = Enumerable.Range(1, 12).Select(m => posted.TryGetValue(m, out var pm) ? pm
            : src.TryGetValue(m, out var s)
                ? new SalesPlanMonthInput { Month = m, Revenue = s.Revenue / Million, Collection = s.Collection / Million, Contracts = s.Contracts, NewCustomers = s.NewCustomers }
                : new SalesPlanMonthInput { Month = m }).ToList();
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(SalesPlanFormViewModel vm)
    {
        vm.Products = vm.Products.Where(x => !string.IsNullOrWhiteSpace(x.Category) || x.Revenue > 0).ToList();
        vm.Members = vm.Members.Where(x => x.SharePercent > 0).GroupBy(x => x.UserId).Select(g => g.First()).ToList();
        if (vm.FromMonth > vm.ToMonth) ModelState.AddModelError(nameof(vm.ToMonth), I18n.T("Tháng kết thúc phải sau hoặc bằng tháng bắt đầu."));
        if (!_scope.Allowed.Contains(vm.BranchId)) ModelState.AddModelError(nameof(vm.BranchId), I18n.T("Đơn vị không hợp lệ."));
        if (vm.Products.Any(x => string.IsNullOrWhiteSpace(x.Category))) ModelState.AddModelError(string.Empty, I18n.T("Vui lòng chọn nhóm sản phẩm cho mọi dòng chỉ tiêu theo nhóm."));
        if (vm.Products.GroupBy(x => x.Category!.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            ModelState.AddModelError(string.Empty, I18n.T("Mỗi nhóm sản phẩm chỉ nhập một dòng."));
        if (vm.Members.Sum(x => x.SharePercent) > 100.0001m) ModelState.AddModelError(string.Empty, I18n.T("Tổng tỷ lệ phân bổ cho nhân viên không được vượt quá 100%."));

        // Nhân viên phân bổ phải thuộc đơn vị lập kế hoạch (hoặc đơn vị trực thuộc)
        var subtree = _scope.Snapshot.Subtree(vm.BranchId);
        var memberIds = vm.Members.Select(m => m.UserId).ToList();
        var validMembers = await Db.Users.Where(u => memberIds.Contains(u.Id) && u.IsActive && subtree.Contains(u.BranchId)).Select(u => u.Id).ToListAsync();
        if (validMembers.Count != memberIds.Count) ModelState.AddModelError(string.Empty, I18n.T("Có nhân viên được phân bổ không thuộc đơn vị của kế hoạch."));

        SalesPlan? p = null;
        if (vm.Id != 0)
        {
            p = await Db.SalesPlans.IgnoreQueryFilters().Include(x => x.Months).Include(x => x.Products).Include(x => x.Members).AsSplitQuery().FirstOrDefaultAsync(x => x.Id == vm.Id);
            if (p == null) return NotFound();
            if (!CanManage(p)) return NotAllowed();
            if (!p.IsEditable) return BadRequest();
        }
        if (!ModelState.IsValid)
        {
            FillMonths(vm, null);
            await LoadLookupsAsync(vm);
            return View("Form", vm);
        }

        if (p == null)
        {
            p = new SalesPlan { Number = await NextNumberAsync(vm.Year), CreatedById = CurrentUserId, CreatedAt = DateTime.Now };
            Db.SalesPlans.Add(p);
        }
        else
        {
            Db.SalesPlanMonths.RemoveRange(p.Months);
            Db.SalesPlanProducts.RemoveRange(p.Products);
            Db.SalesPlanMembers.RemoveRange(p.Members);
            p.Months.Clear(); p.Products.Clear(); p.Members.Clear();
            p.Status = SalesPlanStatus.Draft;   // sửa kế hoạch bị từ chối → về Nháp chờ gửi lại
            p.UpdatedAt = DateTime.Now;
        }
        p.Title = vm.Title.Trim(); p.BranchId = vm.BranchId; p.Year = vm.Year; p.FromMonth = vm.FromMonth; p.ToMonth = vm.ToMonth;
        p.Objectives = string.IsNullOrWhiteSpace(vm.Objectives) ? null : vm.Objectives.Trim();
        foreach (var m in vm.Months.Where(m => m.Month >= vm.FromMonth && m.Month <= vm.ToMonth))
            p.Months.Add(new SalesPlanMonth
            {
                Month = m.Month, Revenue = Math.Round(m.Revenue * Million, 0), Collection = Math.Round(m.Collection * Million, 0),
                Contracts = m.Contracts, NewCustomers = m.NewCustomers
            });
        var sort = 1;
        foreach (var x in vm.Products)
            p.Products.Add(new SalesPlanProduct { Category = x.Category!.Trim(), Revenue = Math.Round(x.Revenue * Million, 0), Note = x.Note?.Trim(), SortOrder = sort++ });
        foreach (var m in vm.Members)
            p.Members.Add(new SalesPlanMember { UserId = m.UserId, SharePercent = Math.Round(m.SharePercent, 2) });

        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu kế hoạch {0}.", p.Number);
        return RedirectToAction(nameof(Details), new { id = p.Id });
    }

    /// <summary>Gợi ý chỉ tiêu từ thực tế cùng kỳ năm trước (JSON, tiền theo triệu đồng)</summary>
    [Authorize(Roles = AppRoles.ManagerGroup)]
    public async Task<IActionResult> Suggest(int branchId, int year, int fromMonth = 1, int toMonth = 12, decimal growth = 10, string basis = "auto")
    {
        if (!_scope.Allowed.Contains(branchId) || year is < 2001 or > 2100 || fromMonth < 1 || toMonth > 12 || fromMonth > toMonth || growth is < -90 or > 500)
            return BadRequest();
        return Json(await _svc.SuggestAsync(branchId, year, fromMonth, toMonth, growth, basis is "prev" or "recent" ? basis : "auto"));
    }

    private async Task LoadLookupsAsync(SalesPlanFormViewModel vm)
    {
        var snap = _scope.Snapshot;
        var allowed = _scope.Allowed;
        ViewBag.Branches = BranchOps.Options(snap, vm.BranchId, allowed);
        ViewBag.SubtreesJson = JsonSerializer.Serialize(allowed.ToDictionary(b => b.ToString(), b => snap.Subtree(b)));
        var memberIds = vm.Members.Select(m => m.UserId).ToList();
        var staff = await Db.Users.Where(u => allowed.Contains(u.BranchId)
                && (u.IsActive && (u.Role == AppRoles.Sales || u.Role == AppRoles.Manager) || memberIds.Contains(u.Id)))
            .OrderBy(u => u.Role == AppRoles.Manager).ThenBy(u => u.FullName)
            .Select(u => new { u.Id, u.FullName, u.Role, u.BranchId }).ToListAsync();
        ViewBag.Staff = staff.Select(u => (u.Id, u.FullName, u.Role, u.BranchId, snap.NameOf(u.BranchId))).ToList();
        var cats = Catalogs.Items(CatalogGroup.ProductCategory).Select(c => c.Name).ToList();
        foreach (var x in vm.Products.Where(x => !string.IsNullOrWhiteSpace(x.Category) && !cats.Contains(x.Category!, StringComparer.OrdinalIgnoreCase)))
            cats.Add(x.Category!);
        ViewBag.Categories = cats;
    }

    private CatalogService Catalogs => HttpContext.RequestServices.GetRequiredService<CatalogService>();

    /// <summary>Người phụ trách hành động: người dùng đang hoạt động thuộc đơn vị của kế hoạch</summary>
    private async Task<SelectList> OwnerOptionsAsync(int branchId, int? selected = null)
    {
        var subtree = _scope.Snapshot.Subtree(branchId);
        return new SelectList(await Db.Users.Where(u => u.IsActive && subtree.Contains(u.BranchId)).OrderBy(u => u.FullName)
            .Select(u => new { u.Id, u.FullName }).ToListAsync(), "Id", "FullName", selected);
    }

    private async Task<string> NextNumberAsync(int year)
    {
        var prefix = $"KHBH-{year}-";
        var n = await Db.SalesPlans.IgnoreQueryFilters().CountAsync(x => x.Number.StartsWith(prefix)) + 1;
        string no;
        do no = $"{prefix}{n++:000}"; while (await Db.SalesPlans.IgnoreQueryFilters().AnyAsync(x => x.Number == no));
        return no;
    }

    // ======================= Quy trình duyệt =======================

    private async Task<SalesPlan?> ManagedAsync(int id, bool full = false)
    {
        var q = Db.SalesPlans.IgnoreQueryFilters();   // quyền kiểm tra bằng CanManage (không phụ thuộc bộ chọn đơn vị)
        if (full) q = q.Include(x => x.Months).Include(x => x.Products).Include(x => x.Members).ThenInclude(m => m.User).Include(x => x.Actions).AsSplitQuery();
        var p = await q.FirstOrDefaultAsync(x => x.Id == id);
        return p != null && CanManage(p) ? p : null;
    }

    private string DetailsUrl(int id) => Url.Action(nameof(Details), new { id })!;

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id)
    {
        var p = await ManagedAsync(id, full: true);
        if (p == null) return NotFound();
        if (!p.IsEditable) return BadRequest();
        if (p.TargetRevenue <= 0)
        {
            TempData["Error"] = I18n.T("Kế hoạch chưa có chỉ tiêu doanh số.");
            return RedirectToAction(nameof(Details), new { id });
        }
        p.Status = SalesPlanStatus.Pending;
        p.SubmittedAt = DateTime.Now;
        p.ApprovedById = null; p.ApprovedAt = null;

        // Báo cho người duyệt: quản trị + quản lý của đơn vị kế hoạch hoặc đơn vị cấp trên
        var snap = _scope.Snapshot;
        var approvers = await Db.Users.Where(u => u.IsActive && (u.Role == AppRoles.Admin || u.Role == AppRoles.Manager))
            .Select(u => new { u.Id, u.Role, u.BranchId }).ToListAsync();
        foreach (var u in approvers.Where(u => u.Id != p.CreatedById && (u.Role == AppRoles.Admin || snap.Subtree(u.BranchId).Contains(p.BranchId))))
            _notify.Add(u.Id, "plan", I18n.T("{0} gửi duyệt kế hoạch bán hàng: {1}", User.GetFullName(), p.Title), DetailsUrl(id), CurrentUserId,
                I18n.T("{0} · Doanh số {1}", p.PeriodLabel(), p.TargetRevenue.ToShortMoney()));
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã gửi duyệt kế hoạch {0}.", p.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Recall(int id)
    {
        var p = await ManagedAsync(id);
        if (p == null) return NotFound();
        if (p.Status != SalesPlanStatus.Pending) return BadRequest();
        p.Status = SalesPlanStatus.Draft;
        p.SubmittedAt = null;
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã thu hồi kế hoạch {0} về Nháp.", p.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Duyệt / từ chối. Khi duyệt có thể giao việc cho các hành động và đẩy phân bổ sang KPI.</summary>
    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(int id, bool approve, string? note, bool assignTasks = false, bool applyKpi = false)
    {
        var p = await ManagedAsync(id, full: true);
        if (p == null) return NotFound();
        if (p.Status != SalesPlanStatus.Pending) return BadRequest();
        if (!CanDecide(p))
        {
            TempData["Error"] = I18n.T("Bạn không thể tự duyệt kế hoạch do mình lập.");
            return RedirectToAction(nameof(Details), new { id });
        }
        if (!approve && string.IsNullOrWhiteSpace(note))
        {
            TempData["Error"] = I18n.T("Vui lòng nhập lý do từ chối.");
            return RedirectToAction(nameof(Details), new { id });
        }
        var me = CurrentUserId;
        p.Status = approve ? SalesPlanStatus.Approved : SalesPlanStatus.Rejected;
        p.ApprovedById = me;
        p.ApprovedAt = DateTime.Now;
        p.ApprovalNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var url = DetailsUrl(id);
        _notify.Add(p.CreatedById, "plan", approve ? I18n.T("{0} đã duyệt kế hoạch: {1}", User.GetFullName(), p.Title)
            : I18n.T("{0} đã từ chối kế hoạch: {1}", User.GetFullName(), p.Title), url, me, p.ApprovalNote);
        if (approve)
            foreach (var m in p.Members.Where(m => m.SharePercent > 0))
                _notify.Add(m.UserId, "plan", I18n.T("Bạn được phân bổ {0}% chỉ tiêu kế hoạch {1}", m.SharePercent.ToString("0.##"), p.Title), url, me,
                    I18n.T("Doanh số {0} · {1}", Math.Round(p.TargetRevenue * m.SharePercent / 100m, 0).ToShortMoney(), p.PeriodLabel()));
        await Db.SaveChangesAsync();

        var msg = approve ? I18n.T("Đã duyệt kế hoạch {0}.", p.Number) : I18n.T("Đã từ chối kế hoạch {0}.", p.Number);
        if (approve && assignTasks)
        {
            var n = 0;
            foreach (var a in p.Actions.Where(a => a.OwnerId.HasValue && a.DoneAt == null).OrderBy(a => a.SortOrder))
                if (await _svc.AssignActionAsync(p, a, me, User.GetFullName(), TaskUrl) != null) n++;
            if (n > 0) msg += " " + I18n.T("Đã giao {0} công việc.", n);
        }
        if (approve && applyKpi && p.Members.Count > 0)
            msg += " " + I18n.T("Đã cập nhật {0} ô chỉ tiêu KPI.", await _svc.ApplyToKpiAsync(p, me));
        TempData["Success"] = msg;
        return RedirectToAction(nameof(Details), new { id });
    }

    private string TaskUrl(int taskId) => Url.Action("Details", "Tasks", new { id = taskId })!;

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int id, bool reopen = false)
    {
        var p = await ManagedAsync(id);
        if (p == null) return NotFound();
        if (p.Status != (reopen ? SalesPlanStatus.Closed : SalesPlanStatus.Approved)) return BadRequest();
        p.Status = reopen ? SalesPlanStatus.Approved : SalesPlanStatus.Closed;
        p.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        TempData["Success"] = reopen ? I18n.T("Đã mở lại kế hoạch {0}.", p.Number) : I18n.T("Đã kết thúc kế hoạch {0}.", p.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Đẩy phân bổ chỉ tiêu sang KPI tháng của nhân viên (ghi đè các chỉ tiêu tương ứng)</summary>
    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyKpi(int id)
    {
        var p = await ManagedAsync(id, full: true);
        if (p == null) return NotFound();
        if (p.Status != SalesPlanStatus.Approved || p.Members.Count == 0) return BadRequest();
        var n = await _svc.ApplyToKpiAsync(p, CurrentUserId);
        TempData["Success"] = I18n.T("Đã cập nhật {0} ô chỉ tiêu KPI cho {1} nhân viên.", n, p.Members.Count(m => m.SharePercent > 0));
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Nhân bản thành bản nháp mới; nextYear = chuyển sang năm sau (dời ngày các hành động 1 năm)</summary>
    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Duplicate(int id, bool nextYear = false)
    {
        var src = await ManagedAsync(id, full: true);
        if (src == null) return NotFound();
        var shift = nextYear ? 1 : 0;
        var year = src.Year + shift;
        var p = new SalesPlan
        {
            Number = await NextNumberAsync(year), BranchId = src.BranchId, Year = year, FromMonth = src.FromMonth, ToMonth = src.ToMonth,
            Title = nextYear ? src.Title.Replace(src.Year.ToString(), year.ToString()) : src.Title + " " + I18n.T("(bản sao)"),
            Objectives = src.Objectives, CreatedById = CurrentUserId, CreatedAt = DateTime.Now,
            Months = src.Months.Select(m => new SalesPlanMonth { Month = m.Month, Revenue = m.Revenue, Collection = m.Collection, Contracts = m.Contracts, NewCustomers = m.NewCustomers }).ToList(),
            Products = src.Products.Select(x => new SalesPlanProduct { Category = x.Category, Revenue = x.Revenue, Note = x.Note, SortOrder = x.SortOrder }).ToList(),
            Members = src.Members.Select(m => new SalesPlanMember { UserId = m.UserId, SharePercent = m.SharePercent }).ToList(),
            Actions = src.Actions.Select(a => new SalesPlanAction
            {
                Title = a.Title, Description = a.Description, OwnerId = a.OwnerId, Budget = a.Budget, SortOrder = a.SortOrder,
                StartDate = a.StartDate?.AddYears(shift), DueDate = a.DueDate?.AddYears(shift)
            }).ToList()
        };
        if (p.Title.Length > 200) p.Title = p.Title[..200];
        Db.SalesPlans.Add(p);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã tạo bản sao {0}.", p.Number);
        return RedirectToAction(nameof(Edit), new { id = p.Id });
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var p = await ManagedAsync(id, full: true);
        if (p == null) return NotFound();
        if (!p.IsEditable)
        {
            TempData["Error"] = I18n.T("Chỉ xóa được kế hoạch ở trạng thái Nháp hoặc Bị từ chối.");
            return RedirectToAction(nameof(Details), new { id });
        }
        Db.SalesPlanMonths.RemoveRange(p.Months);
        Db.SalesPlanProducts.RemoveRange(p.Products);
        Db.SalesPlanMembers.RemoveRange(p.Members);
        Db.SalesPlanActions.RemoveRange(p.Actions);
        Db.SalesPlans.Remove(p);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa kế hoạch {0}.", p.Number);
        return RedirectToAction(nameof(Index));
    }

    // ======================= Chương trình hành động =======================

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAction([FromRoute] int id, SalesPlanActionInput a)
    {
        var p = await ManagedAsync(id, full: true);
        if (p == null) return NotFound();
        if (p.Status == SalesPlanStatus.Closed) return BadRequest();
        if (string.IsNullOrWhiteSpace(a.Title) || a.Title.Length > 200 || a.Budget < 0 || (a.StartDate > a.DueDate))
        {
            TempData["Error"] = I18n.T("Hành động chưa hợp lệ: cần tên (tối đa 200 ký tự), ngày bắt đầu không sau hạn hoàn thành.");
            return RedirectToAction(nameof(Details), null, new { id }, "actions");
        }
        if (a.OwnerId.HasValue)
        {
            var subtree = _scope.Snapshot.Subtree(p.BranchId);
            if (!await Db.Users.AnyAsync(u => u.Id == a.OwnerId && u.IsActive && subtree.Contains(u.BranchId))) return BadRequest();
        }
        SalesPlanAction act;
        if (a.Id == 0)
        {
            act = new SalesPlanAction { SortOrder = p.Actions.Select(x => x.SortOrder).DefaultIfEmpty().Max() + 1 };
            p.Actions.Add(act);
        }
        else act = p.Actions.FirstOrDefault(x => x.Id == a.Id) ?? throw new InvalidOperationException();

        var ownerChanged = act.OwnerId != a.OwnerId;
        var dueChanged = act.DueDate != a.DueDate?.Date;
        act.Title = a.Title.Trim(); act.Description = string.IsNullOrWhiteSpace(a.Description) ? null : a.Description.Trim();
        act.OwnerId = a.OwnerId; act.StartDate = a.StartDate?.Date; act.DueDate = a.DueDate?.Date; act.Budget = Math.Round(a.Budget * Million, 0);

        // Đã giao việc và việc chưa xong → cập nhật người làm / hạn của công việc theo hành động
        if (act.ActivityId is int tid && (ownerChanged || dueChanged))
        {
            var task = await Db.Activities.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == tid && t.Status == ActivityStatus.Planned);
            if (task != null)
            {
                if (ownerChanged && act.OwnerId.HasValue)
                {
                    task.OwnerId = act.OwnerId;
                    _notify.Add(act.OwnerId, "task", I18n.T("{0} giao cho bạn: {1}", User.GetFullName(), task.Subject), TaskUrl(tid), CurrentUserId);
                }
                if (dueChanged && act.DueDate.HasValue) task.DueDate = act.DueDate.Value.AddHours(17);
            }
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu hành động \"{0}\".", act.Title);
        return RedirectToAction(nameof(Details), null, new { id }, "actions");
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAction(int id, int actionId)
    {
        var p = await ManagedAsync(id, full: true);
        var act = p?.Actions.FirstOrDefault(x => x.Id == actionId);
        if (p == null || act == null) return NotFound();
        if (p.Status == SalesPlanStatus.Closed) return BadRequest();
        Db.SalesPlanActions.Remove(act);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa hành động \"{0}\". Công việc đã giao (nếu có) vẫn được giữ.", act.Title);
        return RedirectToAction(nameof(Details), null, new { id }, "actions");
    }

    /// <summary>Giao một hành động (actionId) hoặc tất cả hành động chưa giao (actionId = null) thành công việc</summary>
    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignAction(int id, int? actionId)
    {
        var p = await ManagedAsync(id, full: true);
        if (p == null) return NotFound();
        if (p.Status != SalesPlanStatus.Approved)
        {
            TempData["Error"] = I18n.T("Chỉ giao việc khi kế hoạch đã được duyệt.");
            return RedirectToAction(nameof(Details), null, new { id }, "actions");
        }
        var targets = p.Actions.Where(a => (actionId == null || a.Id == actionId) && a.OwnerId.HasValue && a.DoneAt == null).OrderBy(a => a.SortOrder).ToList();
        var n = 0;
        foreach (var a in targets)
            if (await _svc.AssignActionAsync(p, a, CurrentUserId, User.GetFullName(), TaskUrl) != null) n++;
        TempData[n > 0 ? "Success" : "Error"] = n > 0 ? I18n.T("Đã giao {0} công việc.", n)
            : I18n.T("Không có hành động nào cần giao (cần có người phụ trách và chưa được giao việc).");
        return RedirectToAction(nameof(Details), null, new { id }, "actions");
    }

    /// <summary>Đánh dấu hoàn thành / mở lại hành động chưa giao việc (người phụ trách hành động hoặc quản lý)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleAction(int id, int actionId)
    {
        var p = await Visible().Include(x => x.Actions).FirstOrDefaultAsync(x => x.Id == id);
        var act = p?.Actions.FirstOrDefault(x => x.Id == actionId);
        if (p == null || act == null) return NotFound();
        if (!CanManage(p) && act.OwnerId != CurrentUserId) return NotAllowed();
        if (p.Status != SalesPlanStatus.Approved) return BadRequest();
        if (act.ActivityId.HasValue && await Db.Activities.IgnoreQueryFilters().AnyAsync(t => t.Id == act.ActivityId))
        {
            TempData["Error"] = I18n.T("Hành động đã giao việc – cập nhật trạng thái trên công việc.");
            return RedirectToAction(nameof(Details), null, new { id }, "actions");
        }
        act.DoneAt = act.DoneAt.HasValue ? null : DateTime.Now;
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), null, new { id }, "actions");
    }
}
