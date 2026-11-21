using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>Số liệu dự báo của một tháng hoặc một nhân viên</summary>
public class ForecastFigures
{
    public decimal Target { get; set; }
    public decimal Won { get; set; }
    public decimal Commit { get; set; }
    public decimal BestCase { get; set; }
    public decimal Pipeline { get; set; }
    public decimal Weighted { get; set; }
    public int OpenDeals { get; set; }

    /// <summary>Dự báo chắc chắn = đã thắng + nhóm chắc chắn</summary>
    public decimal CommitForecast => Won + Commit;
    /// <summary>Dự báo khả quan = chắc chắn + nhóm có thể</summary>
    public decimal BestForecast => Won + Commit + BestCase;
    /// <summary>Dự báo theo trọng số = đã thắng + Σ(giá trị × xác suất)</summary>
    public decimal WeightedForecast => Won + Weighted;
    /// <summary>Còn thiếu so với chỉ tiêu (theo dự báo chắc chắn)</summary>
    public decimal Gap => Math.Max(0, Target - CommitForecast);
    /// <summary>Độ phủ pipeline: tổng cơ hội đang mở / phần chỉ tiêu còn lại (≥ 3 lần là an toàn)</summary>
    public decimal? Coverage => Target - Won > 0 ? Math.Round((Commit + BestCase + Pipeline) / (Target - Won), 1) : null;

    public void Add(ForecastFigures o)
    {
        Target += o.Target; Won += o.Won; Commit += o.Commit; BestCase += o.BestCase; Pipeline += o.Pipeline; Weighted += o.Weighted; OpenDeals += o.OpenDeals;
    }
}

public record ForecastDeal(int Id, string Name, string? Customer, int? CustomerId, string? Owner, decimal Amount, int Probability, DateTime? CloseDate,
    OpportunityStage Stage, ForecastCategory Category, bool Manual, DateTime? LastActivity);

public record ForecastRisk(string Kind, string Text, ForecastDeal Deal);

public class ForecastResult
{
    public KpiPeriod Period { get; init; } = null!;
    public List<(int Month, ForecastFigures F)> Months { get; } = new();
    public List<(AppUser User, ForecastFigures F)> Users { get; } = new();
    public ForecastFigures Total { get; } = new();
    public List<ForecastDeal> Deals { get; } = new();
    public List<ForecastRisk> Risks { get; } = new();
}

/// <summary>
/// Dự báo doanh số theo kỳ: đã thắng (ngày chốt trong kỳ) + cơ hội đang mở dự kiến chốt trong kỳ, xếp nhóm Chắc chắn / Có thể / Pipeline
/// (người phụ trách tự chọn, hoặc tự xếp theo xác suất ≥ 70% / ≥ 40%), so với chỉ tiêu doanh số KPI; kèm danh sách rủi ro dự báo.
/// </summary>
public class ForecastService
{
    public const int CommitFrom = 70, BestCaseFrom = 40, StaleDays = 30;
    private readonly CrmDbContext _db;
    public ForecastService(CrmDbContext db) => _db = db;

    public static ForecastCategory AutoCategory(int probability) =>
        probability >= CommitFrom ? ForecastCategory.Commit : probability >= BestCaseFrom ? ForecastCategory.BestCase : ForecastCategory.Pipeline;

    public static ForecastCategory CategoryOf(Opportunity o) => o.ForecastCategory ?? AutoCategory(o.Probability);

    public async Task<ForecastResult> ComputeAsync(KpiPeriod p, IReadOnlyCollection<AppUser> users)
    {
        var r = new ForecastResult { Period = p };
        var ids = users.Select(u => u.Id).ToList();
        var from = p.Start; var to = p.End;
        var byMonth = Enumerable.Range(p.FromMonth, p.ToMonth - p.FromMonth + 1).ToDictionary(m => m, _ => new ForecastFigures());
        var byUser = users.ToDictionary(u => u.Id, _ => new ForecastFigures());

        // Chỉ tiêu doanh số (KPI) theo tháng / nhân viên
        foreach (var t in await _db.SalesTargets.Where(t => ids.Contains(t.UserId) && t.Year == p.Year && t.Month >= p.FromMonth && t.Month <= p.ToMonth)
                     .Select(t => new { t.UserId, t.Month, t.Revenue }).ToListAsync())
        {
            byMonth[t.Month].Target += t.Revenue; byUser[t.UserId].Target += t.Revenue;
        }

        // Đã thắng trong kỳ
        foreach (var w in await _db.Opportunities.Where(o => o.Stage == OpportunityStage.ClosedWon && o.ClosedAt >= from && o.ClosedAt < to && o.OwnerId != null && ids.Contains(o.OwnerId.Value))
                     .Select(o => new { o.OwnerId, o.ClosedAt, o.Amount }).ToListAsync())
        {
            byMonth[w.ClosedAt!.Value.Month].Won += w.Amount; byUser[w.OwnerId!.Value].Won += w.Amount;
        }

        // Cơ hội đang mở: dự kiến chốt trong kỳ (phần còn lại từ hôm nay) + cơ hội trễ hạn chốt / chưa có ngày chốt (để cảnh báo)
        var today = DateTime.Today;
        var open = await _db.Opportunities.AsNoTracking().Include(o => o.Customer).Include(o => o.Owner)
            .Where(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost && o.OwnerId != null && ids.Contains(o.OwnerId.Value)
                        && (o.ExpectedCloseDate == null || o.ExpectedCloseDate < to))
            .ToListAsync();
        var openIds = open.Select(o => o.Id).ToList();
        var lastAct = openIds.Count == 0 ? new Dictionary<int, DateTime>()
            : (await _db.Activities.Where(a => a.OpportunityId != null && openIds.Contains(a.OpportunityId.Value))
                .GroupBy(a => a.OpportunityId!.Value).Select(g => new { g.Key, D = g.Max(a => a.CompletedAt ?? a.CreatedAt) }).ToListAsync())
                .ToDictionary(x => x.Key, x => x.D);

        foreach (var o in open)
        {
            var cat = CategoryOf(o);
            var deal = new ForecastDeal(o.Id, o.Name, o.Customer?.Name, o.CustomerId, o.Owner?.FullName, o.Amount, o.Probability, o.ExpectedCloseDate,
                o.Stage, cat, o.ForecastCategory.HasValue, lastAct.TryGetValue(o.Id, out var la) ? la : null);
            if (o.ExpectedCloseDate == null) { r.Risks.Add(new("nodate", I18n.T("Chưa có ngày dự kiến chốt – không được tính vào dự báo"), deal)); continue; }
            if (o.ExpectedCloseDate < today)
            {
                // Quá ngày dự kiến chốt mà chưa thắng / thua: không tính vào dự báo cho tới khi cập nhật ngày chốt
                if (o.ExpectedCloseDate >= from.AddMonths(-3)) r.Risks.Add(new("slipped", I18n.T("Quá ngày dự kiến chốt {0} – cập nhật ngày chốt hoặc đóng cơ hội", o.ExpectedCloseDate.ToDate()), deal));
                continue;
            }
            if (o.ExpectedCloseDate < from) continue;
            r.Deals.Add(deal);
            if (cat == ForecastCategory.Omitted) continue;
            foreach (var f in new[] { byMonth[o.ExpectedCloseDate.Value.Month], byUser[o.OwnerId!.Value] })
            {
                if (cat == ForecastCategory.Commit) f.Commit += o.Amount;
                else if (cat == ForecastCategory.BestCase) f.BestCase += o.Amount;
                else f.Pipeline += o.Amount;
                f.Weighted += o.Amount * o.Probability / 100m;
                f.OpenDeals++;
            }
            var stale = deal.LastActivity == null ? (today - o.CreatedAt).TotalDays : (today - deal.LastActivity.Value).TotalDays;
            if (cat == ForecastCategory.Commit && stale > StaleDays)
                r.Risks.Add(new("stale", I18n.T("Nhóm chắc chắn nhưng {0} ngày chưa có hoạt động", (int)stale), deal));
            else if (cat == ForecastCategory.Commit && o.Probability < BestCaseFrom)
                r.Risks.Add(new("mismatch", I18n.T("Xếp chắc chắn nhưng xác suất chỉ {0}%", o.Probability), deal));
        }

        foreach (var (m, f) in byMonth.OrderBy(x => x.Key)) { r.Months.Add((m, f)); r.Total.Add(f); }
        foreach (var u in users) r.Users.Add((u, byUser[u.Id]));
        r.Deals.Sort((a, b) => a.Category != b.Category ? a.Category.CompareTo(b.Category) : b.Amount.CompareTo(a.Amount));
        r.Risks.Sort((a, b) => b.Deal.Amount.CompareTo(a.Deal.Amount));
        return r;
    }
}
