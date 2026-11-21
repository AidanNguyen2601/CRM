using System.Security.Claims;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>Hạn mức chiết khấu theo vai trò (AppSettings "discount"). Vượt hạn mức → báo giá phải được duyệt.</summary>
public class DiscountPolicy
{
    public const string Key = "discount";
    public bool Enabled { get; set; }
    public decimal SalesLimit { get; set; } = 10;
    public decimal SupportLimit { get; set; } = 5;
    public decimal ManagerLimit { get; set; } = 20;

    /// <summary>Hạn mức của vai trò (%); quản trị không giới hạn</summary>
    public decimal LimitFor(string role) => role switch
    {
        AppRoles.Admin => 100, AppRoles.Manager => ManagerLimit, AppRoles.Support => SupportLimit, _ => SalesLimit
    };
}

/// <summary>Giá theo bảng giá của khách hàng và tính chiết khấu thực tế của báo giá</summary>
public class PricingService
{
    private readonly CrmDbContext _db;
    private readonly SettingsStore _settings;
    public PricingService(CrmDbContext db, SettingsStore settings) { _db = db; _settings = settings; }

    public Task<DiscountPolicy> PolicyAsync() => _settings.GetAsync<DiscountPolicy>(DiscountPolicy.Key);

    /// <summary>Giá theo bảng giá đang hiệu lực của khách hàng: productId → giá (rỗng nếu khách hàng không gắn bảng giá)</summary>
    public async Task<(PriceList? List, Dictionary<int, decimal> Prices)> CustomerPricesAsync(int? customerId)
    {
        if (customerId == null) return (null, new());
        var listId = await _db.Customers.IgnoreQueryFilters().Where(c => c.Id == customerId).Select(c => c.PriceListId).FirstOrDefaultAsync();
        if (listId == null) return (null, new());
        var list = await _db.PriceLists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == listId);
        if (list == null || !list.IsEffective) return (null, new());
        var prices = await _db.PriceListItems.Where(i => i.PriceListId == listId).ToDictionaryAsync(i => i.ProductId, i => i.Price);
        return (list, prices);
    }

    /// <summary>
    /// Chiết khấu thực tế (%) = 1 − giá trị sau chiết khấu / giá tham chiếu. Giá tham chiếu của dòng có sản phẩm là giá theo bảng giá
    /// của khách hàng (không có thì giá bán chuẩn) – nên hạ đơn giá cũng được tính là chiết khấu; dòng tự nhập dùng đơn giá đã nhập.
    /// </summary>
    public async Task<decimal> EffectiveDiscountAsync(Quote q)
    {
        var (_, prices) = await CustomerPricesAsync(q.CustomerId);
        var ids = q.Lines.Where(l => l.ProductId.HasValue).Select(l => l.ProductId!.Value).Distinct().ToList();
        var std = await _db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.UnitPrice);
        decimal gross = 0;
        foreach (var l in q.Lines)
        {
            var refPrice = l.ProductId is int pid ? prices.TryGetValue(pid, out var lp) ? lp : std.GetValueOrDefault(pid, l.UnitPrice) : l.UnitPrice;
            gross += l.Quantity * Math.Max(refPrice, 0);
        }
        var net = q.SubTotal - q.DiscountAmount;
        return gross <= 0 ? 0 : Math.Clamp(Math.Round((1 - net / gross) * 100m, 2), 0, 100);
    }

    public static bool CanApprove(ClaimsPrincipal user, DiscountPolicy p, decimal discount) =>
        user.IsInRole(AppRoles.Admin) || user.IsInRole(AppRoles.Manager) && p.ManagerLimit >= discount;
}
