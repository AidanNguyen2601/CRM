using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>Chỉ tiêu / thực tế một tháng của kế hoạch</summary>
public class PlanMonthRow
{
    public int Month { get; init; }
    public double Elapsed { get; init; }
    public Dictionary<KpiMetric, decimal> Target { get; } = SalesPlanProgress.Metrics.ToDictionary(m => m, _ => 0m);
    public Dictionary<KpiMetric, decimal> Actual { get; } = SalesPlanProgress.Metrics.ToDictionary(m => m, _ => 0m);
    /// <summary>Doanh số lũy kế đến hết tháng</summary>
    public decimal CumTarget { get; set; }
    public decimal CumActual { get; set; }
    public decimal Percent(KpiMetric m) => Target[m] == 0 ? 0 : Math.Round(Actual[m] * 100m / Target[m], 1);
}

public class PlanCategoryRow
{
    public string Category { get; init; } = "";
    public decimal Target { get; set; }
    public decimal Actual { get; set; }
    public string? Note { get; set; }
    public decimal Percent => Target == 0 ? 0 : Math.Round(Actual * 100m / Target, 1);
}

public class PlanMemberRow
{
    public AppUser User { get; init; } = null!;
    public decimal Share { get; init; }
    public decimal Target { get; init; }
    public decimal Actual { get; set; }
    public decimal Forecast { get; set; }
    public decimal Percent => Target == 0 ? 0 : Math.Round(Actual * 100m / Target, 1);
}

/// <summary>Tiến độ thực hiện kế hoạch bán hàng</summary>
public class SalesPlanProgress
{
    /// <summary>Các chỉ tiêu của kế hoạch (dùng chung định nghĩa với KPI, trừ "Hoạt động")</summary>
    public static readonly KpiMetric[] Metrics = { KpiMetric.Revenue, KpiMetric.Collection, KpiMetric.Contracts, KpiMetric.NewCustomers };

    public double Elapsed { get; init; }
    public Dictionary<KpiMetric, decimal> Target { get; } = Metrics.ToDictionary(m => m, _ => 0m);
    public Dictionary<KpiMetric, decimal> Actual { get; } = Metrics.ToDictionary(m => m, _ => 0m);
    public List<PlanMonthRow> Months { get; } = new();
    public List<PlanCategoryRow> Categories { get; } = new();
    public List<PlanMemberRow> Members { get; } = new();
    /// <summary>Cơ hội đang mở dự kiến chốt từ hôm nay đến hết kỳ: tổng giá trị / có trọng số theo xác suất</summary>
    public decimal PipelineOpen { get; set; }
    public decimal PipelineWeighted { get; set; }
    public int PipelineCount { get; set; }

    public decimal Percent(KpiMetric m) => Target[m] == 0 ? 0 : Math.Round(Actual[m] * 100m / Target[m], 1);
    /// <summary>Dự báo doanh số cuối kỳ = thực tế + pipeline có trọng số</summary>
    public decimal Forecast => Actual[KpiMetric.Revenue] + PipelineWeighted;
    /// <summary>Doanh số còn thiếu để đạt kế hoạch</summary>
    public decimal Gap => Math.Max(0, Target[KpiMetric.Revenue] - Actual[KpiMetric.Revenue]);
    /// <summary>Độ phủ pipeline: tổng pipeline mở / doanh số còn thiếu (lần). ≥ 3 lần thường được coi là an toàn.</summary>
    public decimal? Coverage => Gap == 0 ? null : Math.Round(PipelineOpen / Gap, 1);
}

/// <summary>
/// Tính thực tế cho kế hoạch bán hàng – cùng định nghĩa với KPI, phạm vi là đơn vị lập kế hoạch và các đơn vị trực thuộc
/// (không phụ thuộc bộ chọn đơn vị của người đang xem):
/// doanh số = cơ hội thắng trong kỳ; thu tiền = phiếu thu; hợp đồng = hợp đồng ký (trừ đã hủy); khách hàng mới = khách hàng tạo trong kỳ.
/// Doanh số theo nhóm sản phẩm lấy từ dòng hàng của hợp đồng ký trong kỳ. Thực tế của nhân viên tính theo người phụ trách.
/// </summary>
public class SalesPlanService
{
    private readonly CrmDbContext _db;
    private readonly BranchDirectory _dir;
    private readonly NotificationService _notify;

    public SalesPlanService(CrmDbContext db, BranchDirectory dir, NotificationService notify) { _db = db; _dir = dir; _notify = notify; }

    public static string OtherCategory => I18n.T("Khác / chưa phân nhóm");

    /// <param name="detail">false: chỉ tính tổng theo tháng (dùng cho danh sách)</param>
    public async Task<SalesPlanProgress> ComputeAsync(SalesPlan p, bool detail = true)
    {
        var branches = _dir.Get().Subtree(p.BranchId);
        DateTime from = p.Start, to = p.End;
        var r = new SalesPlanProgress { Elapsed = new KpiPeriod(p.Year, p.FromMonth, p.ToMonth).Elapsed };
        var rows = p.MonthRange.ToDictionary(m => m, m => new PlanMonthRow { Month = m, Elapsed = KpiPeriod.Month(p.Year, m).Elapsed });
        foreach (var pm in p.Months.Where(x => rows.ContainsKey(x.Month)))
        {
            var row = rows[pm.Month];
            row.Target[KpiMetric.Revenue] = pm.Revenue; row.Target[KpiMetric.Collection] = pm.Collection;
            row.Target[KpiMetric.Contracts] = pm.Contracts; row.Target[KpiMetric.NewCustomers] = pm.NewCustomers;
        }

        void Put(KpiMetric m, IEnumerable<(int Month, decimal V)> data)
        {
            foreach (var (month, v) in data) if (rows.TryGetValue(month, out var row)) row.Actual[m] += v;
        }

        var opps = _db.Opportunities.IgnoreQueryFilters().Where(o => branches.Contains(o.BranchId));
        Put(KpiMetric.Revenue, (await opps.Where(o => o.Stage == OpportunityStage.ClosedWon && o.ClosedAt >= from && o.ClosedAt < to)
            .GroupBy(o => o.ClosedAt!.Value.Month).Select(g => new { g.Key, V = g.Sum(o => o.Amount) }).ToListAsync()).Select(x => (x.Key, x.V)));
        Put(KpiMetric.Collection, (await _db.Payments.IgnoreQueryFilters().Where(x => branches.Contains(x.BranchId) && x.PaymentDate >= from && x.PaymentDate < to)
            .GroupBy(x => x.PaymentDate.Month).Select(g => new { g.Key, V = g.Sum(x => x.Amount) }).ToListAsync()).Select(x => (x.Key, x.V)));
        Put(KpiMetric.Contracts, (await _db.Contracts.IgnoreQueryFilters()
            .Where(c => branches.Contains(c.BranchId) && c.SignDate >= from && c.SignDate < to && c.Status != ContractStatus.Cancelled)
            .GroupBy(c => c.SignDate!.Value.Month).Select(g => new { g.Key, V = g.Count() }).ToListAsync()).Select(x => (x.Key, (decimal)x.V)));
        Put(KpiMetric.NewCustomers, (await _db.Customers.IgnoreQueryFilters().Where(c => branches.Contains(c.BranchId) && c.CreatedAt >= from && c.CreatedAt < to)
            .GroupBy(c => c.CreatedAt.Month).Select(g => new { g.Key, V = g.Count() }).ToListAsync()).Select(x => (x.Key, (decimal)x.V)));

        decimal cumT = 0, cumA = 0;
        foreach (var row in rows.Values.OrderBy(x => x.Month))
        {
            foreach (var m in SalesPlanProgress.Metrics) { r.Target[m] += row.Target[m]; r.Actual[m] += row.Actual[m]; }
            row.CumTarget = cumT += row.Target[KpiMetric.Revenue];
            row.CumActual = cumA += row.Actual[KpiMetric.Revenue];
            r.Months.Add(row);
        }

        // Pipeline còn lại của kỳ
        var fcFrom = DateTime.Today > from ? DateTime.Today : from;
        if (fcFrom < to)
        {
            var open = opps.Where(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost
                    && o.ExpectedCloseDate >= fcFrom && o.ExpectedCloseDate < to);
            r.PipelineCount = await open.CountAsync();
            if (r.PipelineCount > 0)
            {
                r.PipelineOpen = await open.SumAsync(o => o.Amount);
                r.PipelineWeighted = Math.Round(await open.SumAsync(o => o.Amount * o.Probability / 100m), 0);
            }
        }
        if (!detail) return r;

        // ----- Theo nhóm sản phẩm: dòng hàng hợp đồng ký trong kỳ -----
        var cats = await _db.ContractLines.IgnoreQueryFilters()
            .Where(l => branches.Contains(l.Contract!.BranchId) && l.Contract.SignDate >= from && l.Contract.SignDate < to && l.Contract.Status != ContractStatus.Cancelled)
            .GroupBy(l => l.Product != null ? l.Product.Category : null)
            .Select(g => new { g.Key, V = g.Sum(l => l.LineTotal) }).ToListAsync();
        var byCat = new Dictionary<string, PlanCategoryRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var pp in p.Products.OrderBy(x => x.SortOrder))
        {
            if (!byCat.TryGetValue(pp.Category, out var row)) byCat[pp.Category] = row = new PlanCategoryRow { Category = pp.Category, Note = pp.Note };
            row.Target += pp.Revenue;
        }
        foreach (var c in cats)
        {
            var key = string.IsNullOrWhiteSpace(c.Key) ? OtherCategory : c.Key;
            if (!byCat.TryGetValue(key, out var row)) byCat[key] = row = new PlanCategoryRow { Category = key };
            row.Actual += c.V;
        }
        r.Categories.AddRange(byCat.Values.OrderByDescending(x => x.Target).ThenByDescending(x => x.Actual));

        // ----- Theo nhân viên được phân bổ -----
        if (p.Members.Count > 0)
        {
            var ids = p.Members.Select(m => m.UserId).ToList();
            var won = await _db.Opportunities.IgnoreQueryFilters()
                .Where(o => o.Stage == OpportunityStage.ClosedWon && o.ClosedAt >= from && o.ClosedAt < to && o.OwnerId != null && ids.Contains(o.OwnerId.Value))
                .GroupBy(o => o.OwnerId!.Value).Select(g => new { g.Key, V = g.Sum(o => o.Amount) }).ToDictionaryAsync(x => x.Key, x => x.V);
            var fc = fcFrom < to
                ? await _db.Opportunities.IgnoreQueryFilters()
                    .Where(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost && o.ExpectedCloseDate >= fcFrom && o.ExpectedCloseDate < to
                        && o.OwnerId != null && ids.Contains(o.OwnerId.Value))
                    .GroupBy(o => o.OwnerId!.Value).Select(g => new { g.Key, V = g.Sum(o => o.Amount * o.Probability / 100m) }).ToDictionaryAsync(x => x.Key, x => x.V)
                : new Dictionary<int, decimal>();
            foreach (var m in p.Members.Where(m => m.User != null).OrderByDescending(m => m.SharePercent).ThenBy(m => m.User!.FullName))
            {
                var a = won.GetValueOrDefault(m.UserId);
                r.Members.Add(new PlanMemberRow
                {
                    User = m.User!, Share = m.SharePercent, Target = Math.Round(r.Target[KpiMetric.Revenue] * m.SharePercent / 100m, 0),
                    Actual = a, Forecast = a + Math.Round(fc.GetValueOrDefault(m.UserId), 0)
                });
            }
        }
        return r;
    }

    /// <summary>
    /// Gợi ý chỉ tiêu: thực tế cùng kỳ năm trước của đơn vị × (1 + tăng trưởng %).
    /// Tiền làm tròn đến triệu đồng (trả về theo đơn vị triệu), số lượng làm tròn lên.
    /// </summary>
    public async Task<object> SuggestAsync(int branchId, int year, int fromMonth, int toMonth, decimal growth)
    {
        var prev = new SalesPlan { BranchId = branchId, Year = year - 1, FromMonth = fromMonth, ToMonth = toMonth };
        var r = await ComputeAsync(prev);
        var f = 1 + growth / 100m;
        decimal Mil(decimal v) => Math.Round(v * f / 1_000_000m, 0);
        decimal Qty(decimal v) => Math.Ceiling(v * f);
        return new
        {
            months = r.Months.Select(m => new
            {
                month = m.Month, revenue = Mil(m.Actual[KpiMetric.Revenue]), collection = Mil(m.Actual[KpiMetric.Collection]),
                contracts = Qty(m.Actual[KpiMetric.Contracts]), newCustomers = Qty(m.Actual[KpiMetric.NewCustomers])
            }),
            products = r.Categories.Where(c => c.Actual > 0).Select(c => new { category = c.Category, revenue = Mil(c.Actual) }),
            total = Mil(r.Actual[KpiMetric.Revenue])
        };
    }

    /// <summary>
    /// Đẩy phân bổ sang chỉ tiêu KPI tháng của từng nhân viên (ghi đè Doanh số, Thu tiền, Hợp đồng, Khách hàng mới;
    /// giữ nguyên chỉ tiêu Hoạt động). Làm tròn lũy kế để tổng các tháng đúng bằng tổng phân bổ.
    /// </summary>
    public async Task<int> ApplyToKpiAsync(SalesPlan p, int actorId)
    {
        var ids = p.Members.Where(m => m.SharePercent > 0).Select(m => m.UserId).ToList();
        var existing = await _db.SalesTargets.Where(t => t.Year == p.Year && ids.Contains(t.UserId) && t.Month >= p.FromMonth && t.Month <= p.ToMonth).ToListAsync();
        var months = p.MonthRange.Select(m => p.Months.FirstOrDefault(x => x.Month == m) ?? new SalesPlanMonth { Month = m }).ToList();
        var cells = 0;
        foreach (var mem in p.Members.Where(m => m.SharePercent > 0))
        {
            var share = mem.SharePercent / 100m;
            foreach (var metric in SalesPlanProgress.Metrics)
            {
                var money = SalesTarget.IsMoney(metric);
                var unit = money ? 1_000_000m : 1m;
                decimal raw = 0, done = 0;
                foreach (var pm in months)
                {
                    raw += metric switch
                    {
                        KpiMetric.Revenue => pm.Revenue, KpiMetric.Collection => pm.Collection,
                        KpiMetric.Contracts => pm.Contracts, _ => pm.NewCustomers
                    } * share;
                    var cum = Math.Round(raw / unit, 0, MidpointRounding.AwayFromZero) * unit;
                    var value = cum - done;
                    done = cum;
                    var t = existing.FirstOrDefault(x => x.UserId == mem.UserId && x.Month == pm.Month);
                    if (t == null)
                    {
                        if (value == 0) continue;
                        t = new SalesTarget { UserId = mem.UserId, Year = p.Year, Month = pm.Month };
                        _db.SalesTargets.Add(t);
                        existing.Add(t);
                    }
                    if (t.Get(metric) == value) continue;
                    t.Set(metric, value);
                    t.UpdatedById = actorId; t.UpdatedAt = DateTime.Now;
                    cells++;
                }
            }
        }
        p.KpiAppliedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return cells;
    }

    /// <summary>Giao một hành động của kế hoạch thành công việc cho người phụ trách (chưa giao hoặc công việc đã bị xóa)</summary>
    public async Task<Activity?> AssignActionAsync(SalesPlan p, SalesPlanAction a, int actorId, string actorName, Func<int, string> taskUrl)
    {
        if (a.OwnerId == null) return null;
        if (a.ActivityId.HasValue && await _db.Activities.IgnoreQueryFilters().AnyAsync(x => x.Id == a.ActivityId)) return null;
        var due = (a.DueDate ?? p.End.AddDays(-1)).Date.AddHours(17);
        var task = new Activity
        {
            Type = ActivityType.Task, Subject = Cut($"[{p.Number}] {a.Title}", 200), OwnerId = a.OwnerId, AssignedById = actorId,
            DueDate = due, Status = ActivityStatus.Planned, Priority = Priority.Normal,
            Description = Cut(I18n.T("Thuộc kế hoạch bán hàng {0} – {1}.", p.Number, p.Title)
                + (string.IsNullOrWhiteSpace(a.Description) ? "" : "\n" + a.Description)
                + (a.Budget > 0 ? "\n" + I18n.T("Ngân sách: {0} đ", a.Budget.ToMoney()) : ""), 2000),
            CreatedAt = DateTime.Now
        };
        _db.Activities.Add(task);
        await _db.SaveChangesAsync();
        a.ActivityId = task.Id;
        a.DoneAt = null;
        _notify.Add(a.OwnerId, "task", I18n.T("{0} giao cho bạn: {1}", actorName, task.Subject), taskUrl(task.Id), actorId, I18n.T("Hạn {0}", task.DueDate.ToDateTime()));
        await _db.SaveChangesAsync();
        return task;
    }

    private static string Cut(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;
}
