using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>
/// Cơ cấu chuỗi: công ty mẹ → công ty con → chi nhánh (quản trị hệ thống).
/// Bộ chọn đơn vị trên thanh trên cùng (Switch) dùng cho mọi người dùng có phạm vi nhiều đơn vị.
/// </summary>
[Authorize(Roles = AppRoles.Admin)]
public class BranchesController : CrmControllerBase
{
    private readonly BranchDirectory _dir;
    public BranchesController(CrmDbContext db, BranchDirectory dir) : base(db) => _dir = dir;

    public async Task<IActionResult> Index()
    {
        var snap = _dir.Get();
        ViewBag.Snapshot = snap;
        ViewBag.Users = await Db.Users.GroupBy(u => u.BranchId).Select(g => new { g.Key, N = g.Count(), Active = g.Count(u => u.IsActive) })
            .ToDictionaryAsync(x => x.Key, x => (x.N, x.Active));
        ViewBag.Customers = await Db.Customers.IgnoreQueryFilters().GroupBy(c => c.BranchId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var from = new DateTime(DateTime.Today.Year, 1, 1);
        ViewBag.Won = await Db.Opportunities.IgnoreQueryFilters().Where(o => o.Stage == OpportunityStage.ClosedWon && o.ClosedAt >= from)
            .GroupBy(o => o.BranchId).Select(g => new { g.Key, V = g.Sum(o => o.Amount) }).ToDictionaryAsync(x => x.Key, x => x.V);
        ViewBag.OpenTickets = await Db.Tickets.IgnoreQueryFilters()
            .Where(t => t.Status == TicketStatus.New || t.Status == TicketStatus.InProgress || t.Status == TicketStatus.WaitingCustomer)
            .GroupBy(t => t.BranchId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        return View(snap.Tree());
    }

    public IActionResult Create(int? parentId)
    {
        var snap = _dir.Get();
        var parent = parentId ?? snap.RootId;
        LoadParents(null, parent);
        return View("Form", new Branch { ParentId = parent, Type = BranchType.Branch, SortOrder = (snap.Branches.Where(b => b.ParentId == parent).Max(b => (int?)b.SortOrder) ?? 0) + 10 });
    }

    public async Task<IActionResult> Edit(int id)
    {
        var b = await Db.Branches.FindAsync(id);
        if (b == null) return NotFound();
        LoadParents(b.Id, b.ParentId);
        return View("Form", b);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Branch model)
    {
        model.Code = (model.Code ?? "").Trim().ToUpperInvariant();
        model.Name = model.Name?.Trim() ?? "";
        ModelState.Remove(nameof(Branch.Parent));
        var snap = _dir.Get();
        if (await Db.Branches.AnyAsync(b => b.Code == model.Code && b.Id != model.Id))
            ModelState.AddModelError(nameof(model.Code), I18n.T("Mã đơn vị đã tồn tại"));
        var isRoot = model.Id != 0 && snap.ById.TryGetValue(model.Id, out var cur) && cur.ParentId == null;
        if (isRoot) model.ParentId = null;   // đơn vị gốc không có cấp trên
        else if (model.ParentId == null || !snap.ById.ContainsKey(model.ParentId.Value))
            ModelState.AddModelError(nameof(model.ParentId), I18n.T("Vui lòng chọn đơn vị cấp trên"));
        else if (model.Id != 0 && snap.Subtree(model.Id).Contains(model.ParentId.Value))
            ModelState.AddModelError(nameof(model.ParentId), I18n.T("Không thể chọn chính đơn vị này hoặc đơn vị trực thuộc làm cấp trên"));
        if (isRoot && !model.IsActive)
            ModelState.AddModelError(nameof(model.IsActive), I18n.T("Không thể ngừng hoạt động đơn vị gốc"));
        if (!ModelState.IsValid)
        {
            LoadParents(model.Id == 0 ? null : model.Id, model.ParentId);
            return View("Form", model);
        }

        Branch b;
        if (model.Id == 0) { b = new Branch { CreatedAt = DateTime.Now }; Db.Branches.Add(b); }
        else b = await Db.Branches.FindAsync(model.Id) ?? throw new InvalidOperationException();
        b.Code = model.Code; b.Name = model.Name; b.ShortName = Clean(model.ShortName); b.Type = model.Type; b.ParentId = model.ParentId;
        b.TaxCode = Clean(model.TaxCode); b.Address = Clean(model.Address); b.Phone = Clean(model.Phone); b.Email = Clean(model.Email);
        b.Representative = Clean(model.Representative); b.BankAccount = Clean(model.BankAccount); b.InvoiceSymbol = Clean(model.InvoiceSymbol)?.ToUpperInvariant();
        b.SortOrder = model.SortOrder; b.IsActive = model.IsActive;
        await Db.SaveChangesAsync();
        _dir.Invalidate();
        TempData["Success"] = I18n.T("Đã lưu đơn vị {0}.", b.Name);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var b = await Db.Branches.FindAsync(id);
        if (b == null) return NotFound();
        var users = await Db.Users.CountAsync(u => u.BranchId == id);
        var children = await Db.Branches.CountAsync(x => x.ParentId == id);
        var records = (await BranchOps.RecordCountsAsync(Db)).GetValueOrDefault(id);
        if (b.ParentId == null || users + children + records > 0)
        {
            TempData["Error"] = b.ParentId == null ? I18n.T("Không thể xóa đơn vị gốc.")
                : I18n.T("Đơn vị \"{0}\" đang có {1} người dùng, {2} đơn vị trực thuộc và {3} bản ghi nghiệp vụ. Hãy chuyển đi hoặc ngừng hoạt động thay vì xóa.", b.Name, users, children, records);
            return RedirectToAction(nameof(Index));
        }
        Db.Branches.Remove(b);
        await Db.SaveChangesAsync();
        _dir.Invalidate();
        TempData["Success"] = I18n.T("Đã xóa đơn vị {0}.", b.Name);
        return RedirectToAction(nameof(Index));
    }

    private void LoadParents(int? selfId, int? selected)
    {
        var snap = _dir.Get();
        var exclude = selfId.HasValue ? snap.Subtree(selfId.Value).ToHashSet() : new HashSet<int>();
        ViewBag.Parents = BranchOps.Options(snap, selected, snap.Branches.Select(b => b.Id).Where(i => !exclude.Contains(i)));
        ViewBag.IsRoot = selfId.HasValue && snap.ById.TryGetValue(selfId.Value, out var s) && s.ParentId == null;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
