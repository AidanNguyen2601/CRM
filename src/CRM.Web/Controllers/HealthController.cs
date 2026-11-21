using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Sức khoẻ khách hàng toàn danh mục: phân bố theo mức, doanh thu có nguy cơ, danh sách cần chăm sóc kèm nguyên nhân chính</summary>
public class HealthController : CrmControllerBase
{
    private readonly CustomerHealthService _health;
    public HealthController(CrmDbContext db, CustomerHealthService health) : base(db) => _health = health;

    public record Row(Customer Customer, HealthResult Result, decimal Revenue12m);

    public async Task<IActionResult> Index(string level = "risk", int? ownerId = null)
    {
        level = level is "good" or "warn" or "risk" or "all" ? level : "risk";
        var q = Db.Customers.VisibleTo(User, Db).Where(c => c.Type == CustomerType.Customer || c.Type == CustomerType.Partner);
        if (ownerId.HasValue && User.SeesAll()) q = q.Where(c => c.OwnerId == ownerId);

        // Phân bố theo mức + doanh thu 12 tháng (hoá đơn đã phát hành) của từng nhóm
        var since = DateTime.Today.AddMonths(-12);
        var all = await q.Select(c => new { c.Id, c.HealthScore }).ToListAsync();
        var custIds = q.Select(c => c.Id);
        var revAll = await Db.Invoices.Where(i => i.CustomerId != null && custIds.Contains(i.CustomerId.Value) && i.InvoiceDate >= since
                && (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid || i.Status == InvoiceStatus.Paid))
            .GroupBy(i => i.CustomerId!.Value).Select(g => new { g.Key, V = g.Sum(i => i.Total) }).ToDictionaryAsync(x => x.Key, x => x.V);
        string Lv(int? s) => s.HasValue ? CustomerHealth.LevelOf(s.Value) : "none";
        ViewBag.Dist = all.GroupBy(x => Lv(x.HealthScore)).ToDictionary(g => g.Key, g => (N: g.Count(), Rev: g.Sum(x => revAll.GetValueOrDefault(x.Id))));
        ViewBag.Total = all.Count;
        ViewBag.LastRun = await q.MaxAsync(c => c.HealthUpdatedAt);

        var list = level switch
        {
            "good" => q.Where(c => c.HealthScore >= CustomerHealth.GoodFrom),
            "warn" => q.Where(c => c.HealthScore >= CustomerHealth.WarnFrom && c.HealthScore < CustomerHealth.GoodFrom),
            "risk" => q.Where(c => c.HealthScore < CustomerHealth.WarnFrom),
            _ => q.Where(c => c.HealthScore != null)
        };
        var customers = await list.Include(c => c.Owner).OrderBy(c => c.HealthScore).ThenBy(c => c.Name).Take(100).ToListAsync();
        var ids = customers.Select(c => c.Id).ToList();
        var results = await _health.ComputeAsync(ids);
        var rev = await Db.Invoices.IgnoreQueryFilters().Where(i => i.CustomerId != null && ids.Contains(i.CustomerId.Value) && i.InvoiceDate >= since
                && (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid || i.Status == InvoiceStatus.Paid))
            .GroupBy(i => i.CustomerId!.Value).Select(g => new { g.Key, V = g.Sum(i => i.Total) }).ToDictionaryAsync(x => x.Key, x => x.V);
        // Ưu tiên khách nhiều doanh thu trong cùng mức điểm
        var rows = customers.Select(c => new Row(c, results[c.Id], rev.GetValueOrDefault(c.Id)))
            .OrderBy(r => CustomerHealth.LevelOf(r.Result.Score) switch { "risk" => 0, "warn" => 1, _ => 2 }).ThenByDescending(r => r.Revenue12m).ThenBy(r => r.Result.Score).ToList();

        ViewBag.Level = level;
        ViewBag.OwnerId = ownerId;
        if (User.SeesAll())
            ViewBag.Owners = new SelectList(await Db.UsersInScope.Where(u => u.IsActive).OrderBy(u => u.FullName).Select(u => new { u.Id, u.FullName }).ToListAsync(), "Id", "FullName", ownerId);
        return View(rows);
    }

    /// <summary>Tính lại điểm ngay (không chờ tác vụ hằng giờ)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Recalculate()
    {
        if (!User.SeesAll()) return NotAllowed();
        var n = await _health.RecalculateAsync();
        TempData["Success"] = I18n.T("Đã tính lại điểm sức khoẻ: {0} khách hàng thay đổi điểm.", n);
        return RedirectToAction(nameof(Index));
    }
}
