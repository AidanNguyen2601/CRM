using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>Kỳ báo cáo KPI: tháng / quý / năm</summary>
public record KpiPeriod(int Year, int FromMonth, int ToMonth)
{
    public DateTime Start => new(Year, FromMonth, 1);
    public DateTime End => new DateTime(Year, ToMonth, 1).AddMonths(1);   // không bao gồm

    public static KpiPeriod Month(int year, int month) => new(year, month, month);
    public static KpiPeriod Quarter(int year, int q) => new(year, (q - 1) * 3 + 1, q * 3);
    public static KpiPeriod FullYear(int year) => new(year, 1, 12);

    /// <summary>Tỷ lệ thời gian đã trôi qua của kỳ (để so tiến độ): 0..1</summary>
    public double Elapsed
    {
        get
        {
            var now = DateTime.Now;
            if (now >= End) return 1;
            if (now <= Start) return 0;
            return (now - Start).TotalDays / (End - Start).TotalDays;
        }
    }
}

/// <summary>Chỉ tiêu + thực tế của một nhân viên trong kỳ</summary>
public class KpiRow
{
    public AppUser User { get; init; } = null!;
    public Dictionary<KpiMetric, decimal> Target { get; } = Enum.GetValues<KpiMetric>().ToDictionary(m => m, _ => 0m);
    public Dictionary<KpiMetric, decimal> Actual { get; } = Enum.GetValues<KpiMetric>().ToDictionary(m => m, _ => 0m);
    /// <summary>Pipeline có trọng số của các cơ hội đang mở dự kiến chốt trong phần còn lại của kỳ</summary>
    public decimal RevenueForecastExtra { get; set; }

    public decimal Percent(KpiMetric m) => Target[m] == 0 ? 0 : Math.Round(Actual[m] * 100m / Target[m], 1);
    public decimal RevenueForecast => Actual[KpiMetric.Revenue] + RevenueForecastExtra;
    public bool HasTarget => Target.Values.Any(v => v > 0);

    /// <summary>Điểm KPI tổng hợp: trung bình % hoàn thành các chỉ tiêu có giao (mỗi chỉ tiêu tối đa 150%)</summary>
    public decimal Score
    {
        get
        {
            var ms = Enum.GetValues<KpiMetric>().Where(m => Target[m] > 0).ToList();
            return ms.Count == 0 ? 0 : Math.Round(ms.Average(m => Math.Min(150m, Percent(m))), 1);
        }
    }
}

/// <summary>
/// Tính KPI bán hàng. Thực tế lấy tự động từ dữ liệu nghiệp vụ, gán cho NGƯỜI PHỤ TRÁCH:
/// - Doanh số: cơ hội Thắng có ngày chốt trong kỳ (giá trị trước VAT)
/// - Thu tiền: phiếu thu trong kỳ, tính cho người phụ trách hợp đồng / hoá đơn (không có thì người thu)
/// - Hợp đồng ký mới: hợp đồng ký trong kỳ (trừ hợp đồng đã hủy)
/// - Khách hàng mới: khách hàng tạo trong kỳ
/// - Hoạt động hoàn thành: hoạt động hoàn thành trong kỳ
/// </summary>
public class KpiService
{
    private readonly CrmDbContext _db;
    public KpiService(CrmDbContext db) => _db = db;

    /// <summary>Nhân viên được giao KPI: nhân viên kinh doanh + quản lý kinh doanh đang hoạt động</summary>
    public Task<List<AppUser>> SalesUsersAsync() =>
        _db.Users.Where(u => u.IsActive && (u.Role == AppRoles.Sales || u.Role == AppRoles.Manager))
            .OrderBy(u => u.Role == AppRoles.Manager).ThenBy(u => u.FullName).ToListAsync();

    public async Task<List<KpiRow>> ComputeAsync(KpiPeriod p, IReadOnlyCollection<AppUser> users)
    {
        var ids = users.Select(u => u.Id).ToList();
        var rows = users.ToDictionary(u => u.Id, u => new KpiRow { User = u });
        DateTime from = p.Start, to = p.End;

        // ----- Chỉ tiêu (tổng các tháng trong kỳ) -----
        var targets = await _db.SalesTargets.Where(t => ids.Contains(t.UserId) && t.Year == p.Year && t.Month >= p.FromMonth && t.Month <= p.ToMonth)
            .GroupBy(t => t.UserId)
            .Select(g => new { g.Key, Revenue = g.Sum(x => x.Revenue), Collection = g.Sum(x => x.Collection), Contracts = g.Sum(x => x.Contracts),
                NewCustomers = g.Sum(x => x.NewCustomers), Activities = g.Sum(x => x.Activities) })
            .ToListAsync();
        foreach (var t in targets)
        {
            var r = rows[t.Key];
            r.Target[KpiMetric.Revenue] = t.Revenue; r.Target[KpiMetric.Collection] = t.Collection; r.Target[KpiMetric.Contracts] = t.Contracts;
            r.Target[KpiMetric.NewCustomers] = t.NewCustomers; r.Target[KpiMetric.Activities] = t.Activities;
        }

        // ----- Thực tế -----
        void Put(KpiMetric m, IEnumerable<(int? Owner, decimal Value)> data)
        {
            foreach (var (owner, value) in data)
                if (owner.HasValue && rows.TryGetValue(owner.Value, out var r)) r.Actual[m] += value;
        }

        Put(KpiMetric.Revenue, (await _db.Opportunities
            .Where(o => o.Stage == OpportunityStage.ClosedWon && o.ClosedAt >= from && o.ClosedAt < to && o.OwnerId != null && ids.Contains(o.OwnerId.Value))
            .GroupBy(o => o.OwnerId).Select(g => new { g.Key, V = g.Sum(o => o.Amount) }).ToListAsync()).Select(x => (x.Key, x.V)));

        // Thu tiền: ưu tiên người phụ trách hợp đồng, rồi hoá đơn, cuối cùng người lập phiếu thu
        Put(KpiMetric.Collection, (await _db.Payments
            .Where(x => x.PaymentDate >= from && x.PaymentDate < to)
            .Select(x => new { Owner = x.Contract != null ? x.Contract.OwnerId : x.Invoice != null ? x.Invoice.OwnerId : x.OwnerId, x.Amount })
            .GroupBy(x => x.Owner).Select(g => new { g.Key, V = g.Sum(x => x.Amount) }).ToListAsync()).Select(x => (x.Key, x.V)));

        Put(KpiMetric.Contracts, (await _db.Contracts
            .Where(c => c.SignDate >= from && c.SignDate < to && c.Status != ContractStatus.Cancelled && c.OwnerId != null && ids.Contains(c.OwnerId.Value))
            .GroupBy(c => c.OwnerId).Select(g => new { g.Key, V = g.Count() }).ToListAsync()).Select(x => (x.Key, (decimal)x.V)));

        Put(KpiMetric.NewCustomers, (await _db.Customers
            .Where(c => c.CreatedAt >= from && c.CreatedAt < to && c.OwnerId != null && ids.Contains(c.OwnerId.Value))
            .GroupBy(c => c.OwnerId).Select(g => new { g.Key, V = g.Count() }).ToListAsync()).Select(x => (x.Key, (decimal)x.V)));

        Put(KpiMetric.Activities, (await _db.Activities
            .Where(a => a.Status == ActivityStatus.Completed && a.CompletedAt >= from && a.CompletedAt < to && a.OwnerId != null && ids.Contains(a.OwnerId.Value))
            .GroupBy(a => a.OwnerId).Select(g => new { g.Key, V = g.Count() }).ToListAsync()).Select(x => (x.Key, (decimal)x.V)));

        // ----- Dự báo doanh số: pipeline có trọng số dự kiến chốt từ hôm nay đến hết kỳ -----
        var fcFrom = DateTime.Today > from ? DateTime.Today : from;
        if (fcFrom < to)
        {
            var fc = await _db.Opportunities
                .Where(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost
                    && o.ExpectedCloseDate >= fcFrom && o.ExpectedCloseDate < to && o.OwnerId != null && ids.Contains(o.OwnerId.Value))
                .GroupBy(o => o.OwnerId).Select(g => new { g.Key, V = g.Sum(o => o.Amount * o.Probability / 100m) }).ToListAsync();
            foreach (var x in fc) rows[x.Key!.Value].RevenueForecastExtra = Math.Round(x.V, 0);
        }

        return rows.Values.ToList();
    }

    /// <summary>KPI từng tháng trong năm của một nhân viên</summary>
    public async Task<List<(int Month, KpiRow Row)>> MonthlyAsync(int year, AppUser user)
    {
        var list = new List<(int, KpiRow)>();
        for (var m = 1; m <= 12; m++)
            list.Add((m, (await ComputeAsync(KpiPeriod.Month(year, m), new[] { user }))[0]));
        return list;
    }

    /// <summary>Cộng dồn các dòng thành dòng tổng của đội</summary>
    public static KpiRow Total(IEnumerable<KpiRow> rows, AppUser label)
    {
        var t = new KpiRow { User = label };
        foreach (var r in rows)
        {
            foreach (var m in Enum.GetValues<KpiMetric>()) { t.Target[m] += r.Target[m]; t.Actual[m] += r.Actual[m]; }
            t.RevenueForecastExtra += r.RevenueForecastExtra;
        }
        return t;
    }
}

/// <summary>Hiển thị KPI: định dạng giá trị và màu tiến độ</summary>
public static class KpiUi
{
    public static string Format(KpiMetric m, decimal v) => SalesTarget.IsMoney(m) ? v.ToShortMoney() : v.ToString("N0");

    public static string Icon(KpiMetric m) => m switch
    {
        KpiMetric.Revenue => "trophy",
        KpiMetric.Collection => "wallet",
        KpiMetric.Contracts => "contract",
        KpiMetric.NewCustomers => "customer",
        _ => "calendar"
    };

    /// <summary>
    /// Màu theo mức hoàn thành so với tiến độ thời gian của kỳ (elapsed 0..1):
    /// đạt ≥100% → xanh lá; ≥ tiến độ → xanh dương (đúng tiến độ); ≥ 70% tiến độ → vàng; thấp hơn → đỏ.
    /// </summary>
    public static string Css(decimal pct, double elapsed)
    {
        if (pct >= 100) return "success";
        var expected = (decimal)Math.Max(elapsed, 0.05) * 100m;
        return pct >= expected ? "primary" : pct >= expected * 0.7m ? "warning" : "danger";
    }
}
