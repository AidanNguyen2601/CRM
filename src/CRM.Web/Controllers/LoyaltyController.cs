using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Khách hàng thân thiết: tổng quan hạng thành viên, sổ điểm từng khách, đổi / điều chỉnh điểm, cấu hình chương trình</summary>
public class LoyaltyController : CrmControllerBase
{
    private readonly LoyaltyService _loyalty;
    private readonly SettingsStore _settings;
    public LoyaltyController(CrmDbContext db, LoyaltyService loyalty, SettingsStore settings) : base(db) { _loyalty = loyalty; _settings = settings; }

    private IQueryable<Customer> Members() =>
        Db.Customers.VisibleTo(User, Db).Where(c => c.Type == CustomerType.Customer || c.Type == CustomerType.Partner);

    public async Task<IActionResult> Index(int? tier, string? q, int? ownerId, int page = 1, int pageSize = 30)
    {
        var s = await _loyalty.SettingsAsync();
        var members = Members();
        if (ownerId.HasValue && User.SeesAll()) members = members.Where(c => c.OwnerId == ownerId);

        var perTier = await members.GroupBy(c => c.LoyaltyTierId).Select(g => new { g.Key, N = g.Count(), Pts = g.Sum(c => c.LoyaltyPoints) }).ToListAsync();
        ViewBag.PerTier = perTier.ToDictionary(x => x.Key ?? 0, x => (x.N, x.Pts));
        ViewBag.TotalMembers = perTier.Sum(x => x.N);
        ViewBag.TotalPoints = perTier.Sum(x => x.Pts);

        var ids = members.Select(c => c.Id);
        var since = DateTime.Today.AddDays(-30);
        var recent = await Db.LoyaltyTransactions.Where(t => ids.Contains(t.CustomerId) && t.CreatedAt >= since)
            .GroupBy(t => t.Type).Select(g => new { g.Key, Pts = g.Sum(t => t.Points) }).ToListAsync();
        ViewBag.Earned30 = recent.Where(r => r.Key is LoyaltyTxType.Earn or LoyaltyTxType.Adjust && r.Pts > 0).Sum(r => r.Pts);
        ViewBag.Redeemed30 = -recent.Where(r => r.Key == LoyaltyTxType.Redeem).Sum(r => r.Pts);
        ViewBag.Expired30 = -recent.Where(r => r.Key == LoyaltyTxType.Expire).Sum(r => r.Pts);
        var soon = DateTime.Today.AddDays(30);
        ViewBag.ExpiringSoon = await Db.LoyaltyTransactions.Where(t => ids.Contains(t.CustomerId) && t.Remaining > 0 && t.ExpiresAt != null && t.ExpiresAt >= DateTime.Today && t.ExpiresAt <= soon)
            .SumAsync(t => (int?)t.Remaining) ?? 0;

        var list = members;
        if (tier.HasValue) list = list.Where(c => c.LoyaltyTierId == tier);
        if (!string.IsNullOrWhiteSpace(q)) list = list.Where(c => c.Name.Contains(q) || c.Code.Contains(q));
        var size = pageSize is 15 or 30 or 50 or 100 ? pageSize : 30;
        var total = await list.CountAsync();
        var rows = await list.Include(c => c.Owner).AsNoTracking().OrderByDescending(c => c.LoyaltyLifetime).ThenBy(c => c.Name)
            .Skip((Math.Max(1, page) - 1) * size).Take(size).ToListAsync();

        ViewBag.Settings = s; ViewBag.Tier = tier; ViewBag.Q = q; ViewBag.OwnerId = ownerId; ViewBag.Total = total;
        if (User.SeesAll())
            ViewBag.Owners = new SelectList(await Db.UsersInScope.Where(u => u.IsActive).OrderBy(u => u.FullName).Select(u => new { u.Id, u.FullName }).ToListAsync(), "Id", "FullName", ownerId);
        return View(new PagedList<Customer>(rows, total, Math.Max(1, page), size));
    }

    public async Task<IActionResult> Customer(int id)
    {
        var c = await Members().Include(x => x.Owner).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        ViewBag.Settings = await _loyalty.SettingsAsync();
        ViewBag.Ledger = await Db.LoyaltyTransactions.AsNoTracking().Where(t => t.CustomerId == id).OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(200).ToListAsync();
        var users = await Db.LoyaltyTransactions.Where(t => t.CustomerId == id && t.CreatedById != null).Select(t => t.CreatedById!.Value).Distinct().ToListAsync();
        ViewBag.Users = await Db.UsersInScope.Where(u => users.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName);
        var soon = DateTime.Today.AddDays(30);
        ViewBag.ExpiringSoon = await Db.LoyaltyTransactions.Where(t => t.CustomerId == id && t.Remaining > 0 && t.ExpiresAt != null && t.ExpiresAt <= soon).SumAsync(t => (int?)t.Remaining) ?? 0;
        return View(c);
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Redeem(int id, int points, string? description)
    {
        if (!await Members().AnyAsync(c => c.Id == id)) return NotFound();
        var r = await _loyalty.RedeemAsync(id, points, description, CurrentUserId);
        if (r.Ok) TempData["Success"] = I18n.T("Đã đổi {0} điểm, quy đổi {1}.", points, r.Value.ToMoney());
        else TempData["Error"] = r.Error;
        return RedirectToAction(nameof(Customer), new { id });
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Adjust(int id, int points, string? reason)
    {
        if (!await Members().AnyAsync(c => c.Id == id)) return NotFound();
        var r = await _loyalty.AdjustAsync(id, points, reason ?? "", CurrentUserId);
        if (r.Ok) TempData["Success"] = I18n.T("Đã điều chỉnh {0} điểm.", points.ToString("+#;-#"));
        else TempData["Error"] = r.Error;
        return RedirectToAction(nameof(Customer), new { id });
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    public async Task<IActionResult> Settings()
    {
        ViewBag.Usage = await Db.Customers.IgnoreQueryFilters().Where(c => c.LoyaltyTierId != null).GroupBy(c => c.LoyaltyTierId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        return View(await _loyalty.SettingsAsync());
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings(LoyaltySettings model, int[] tierId, string[] tierName, int[] tierMin, decimal[] tierMult, decimal[] tierDiscount, string[] tierColor, string[] tierBenefits)
    {
        var tiers = new List<LoyaltyTierDef>();
        for (var i = 0; i < tierName.Length; i++)
        {
            var name = tierName[i]?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            tiers.Add(new LoyaltyTierDef
            {
                Id = i < tierId.Length ? tierId[i] : 0, Name = name, MinPoints = Math.Max(0, i < tierMin.Length ? tierMin[i] : 0),
                EarnMultiplier = Math.Clamp(i < tierMult.Length ? tierMult[i] : 1, 0.1m, 10m), DiscountPercent = Math.Clamp(i < tierDiscount.Length ? tierDiscount[i] : 0, 0, 100),
                Color = i < tierColor.Length && System.Text.RegularExpressions.Regex.IsMatch(tierColor[i] ?? "", "^#[0-9a-fA-F]{6}$") ? tierColor[i] : "#6c757d",
                Benefits = i < tierBenefits.Length ? tierBenefits[i]?.Trim() : null
            });
        }
        if (tiers.Count == 0) ModelState.AddModelError("", I18n.T("Cần ít nhất một hạng thành viên."));
        else if (tiers.Select(t => t.MinPoints).Distinct().Count() != tiers.Count) ModelState.AddModelError("", I18n.T("Mỗi hạng phải có mức điểm tối thiểu khác nhau."));
        else if (!tiers.Any(t => t.MinPoints == 0)) ModelState.AddModelError("", I18n.T("Hạng thấp nhất phải bắt đầu từ 0 điểm."));
        if (model.SpendPerPoint < 1000) ModelState.AddModelError("", I18n.T("Số tiền để được 1 điểm tối thiểu 1.000."));
        if (model.PointValue < 0) ModelState.AddModelError("", I18n.T("Giá trị 1 điểm không được âm."));
        if (!ModelState.IsValid)
        {
            model.Tiers = tiers.Count > 0 ? tiers : model.Tiers;
            ViewBag.Usage = new Dictionary<int, int>();
            return View("Settings", model);
        }
        // Hạng mới nhận mã kế tiếp; mã cũ giữ nguyên để khách đang có hạng không bị mất liên kết
        var next = Math.Max(tiers.Count == 0 ? 0 : tiers.Max(t => t.Id), 0);
        foreach (var t in tiers.Where(t => t.Id <= 0)) t.Id = ++next;
        model.Tiers = tiers; model.ExpiryMonths = Math.Clamp(model.ExpiryMonths, 0, 120); model.MinRedeem = Math.Max(1, model.MinRedeem);
        await _settings.SaveAsync(LoyaltySettings.Key, model);

        // Xếp lại hạng theo cấu hình mới
        var n = 0;
        foreach (var c in await Db.Customers.IgnoreQueryFilters().Where(c => c.Type == CustomerType.Customer || c.Type == CustomerType.Partner).ToListAsync())
        {
            var want = model.TierFor(c.LoyaltyLifetime)?.Id;
            if (c.LoyaltyTierId != want) { c.LoyaltyTierId = want; n++; }
        }
        var suppress = Db.SuppressAudit; Db.SuppressAudit = true;
        await Db.SaveChangesAsync();
        Db.SuppressAudit = suppress;
        TempData["Success"] = I18n.T("Đã lưu cấu hình chương trình khách hàng thân thiết. {0} khách hàng được xếp lại hạng.", n);
        return RedirectToAction(nameof(Settings));
    }

    /// <summary>Tích điểm bù cho các phiếu thu đã có từ trước khi bật chương trình</summary>
    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Backfill()
    {
        var (n, pts) = await _loyalty.BackfillAsync();
        TempData["Success"] = I18n.T("Đã tích điểm bù cho {0} phiếu thu ({1} điểm).", n, pts);
        return RedirectToAction(nameof(Index));
    }
}
