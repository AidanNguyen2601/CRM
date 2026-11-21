using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Gia hạn & doanh thu định kỳ: hợp đồng sắp hết hạn, cơ hội gia hạn, hợp đồng thanh toán định kỳ, MRR / ARR, tỷ lệ gia hạn</summary>
public class RenewalsController : CrmControllerBase
{
    private readonly RenewalService _renewal;
    public RenewalsController(CrmDbContext db, RenewalService renewal) : base(db) => _renewal = renewal;

    public async Task<IActionResult> Index(int days = 90)
    {
        days = Math.Clamp(days, 7, 365);
        var today = DateTime.Today;
        var visible = Db.Contracts.VisibleTo(User, Db);
        ViewBag.Stats = await _renewal.StatsAsync(visible);
        ViewBag.Days = days;
        ViewBag.Expiring = await visible.Include(c => c.Customer).Include(c => c.Owner).AsNoTracking()
            .Where(c => c.Status == ContractStatus.Active && c.EndDate >= today.AddDays(-30) && c.EndDate <= today.AddDays(days))
            .OrderBy(c => c.EndDate).ToListAsync();
        var oppIds = ((List<Contract>)ViewBag.Expiring).Where(c => c.RenewalOpportunityId.HasValue).Select(c => c.RenewalOpportunityId!.Value).ToList();
        ViewBag.RenewalOpps = await Db.Opportunities.IgnoreQueryFilters().Where(o => oppIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id);
        var renewedIds = await Db.Contracts.IgnoreQueryFilters().Where(c => c.RenewedFromId != null && c.Status != ContractStatus.Cancelled).Select(c => c.RenewedFromId!.Value).ToListAsync();
        ViewBag.Renewed = renewedIds.ToHashSet();
        ViewBag.Recurring = await visible.Include(c => c.Customer).AsNoTracking()
            .Where(c => c.Status == ContractStatus.Active && c.BillingCycle != BillingCycle.None && c.RecurringAmount > 0)
            .OrderBy(c => c.NextBillingDate).ToListAsync();
        return View();
    }

    /// <summary>Tạo hợp đồng gia hạn (nháp) từ hợp đồng đã ký</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Renew(int id)
    {
        var src = await Db.Contracts.Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id);
        if (src == null) return NotFound();
        if (!await CanAccessAsync(src)) return NotAllowed();
        if (!src.IsSigned) return BadRequest();
        var c = await _renewal.RenewAsync(src, CurrentUserId);
        TempData["Success"] = I18n.T("Đã tạo hợp đồng gia hạn {0} (bản nháp) – kiểm tra giá, thời hạn rồi chuyển sang đang thực hiện khi ký.", c.Number);
        return RedirectToAction("Details", "Contracts", new { id = c.Id });
    }

    /// <summary>Chạy ngay việc tạo cơ hội gia hạn và hoá đơn định kỳ đến hạn</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RunNow()
    {
        if (!User.SeesAll()) return NotAllowed();
        var (r, i) = await _renewal.RunAsync();
        TempData["Success"] = I18n.T("Đã tạo {0} cơ hội gia hạn và {1} hoá đơn nháp định kỳ.", r, i);
        return RedirectToAction(nameof(Index));
    }
}
