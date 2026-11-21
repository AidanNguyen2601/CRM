using CRM.Web.Data;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>
/// Tính điểm sức khoẻ khách hàng hàng loạt (tác vụ hằng giờ) – cùng công thức CustomerHealth.Evaluate với trang khách hàng 360°,
/// dữ liệu đọc theo lô. Lưu Customers.HealthScore để lọc / sắp xếp / theo dõi toàn danh mục; báo người phụ trách khi khách rơi xuống mức rủi ro.
/// Chỉ tính cho khách hàng / đối tác (khách tiềm năng, ngừng giao dịch để trống).
/// </summary>
public class CustomerHealthService
{
    private static readonly InvoiceStatus[] Billable = { InvoiceStatus.Issued, InvoiceStatus.PartiallyPaid, InvoiceStatus.Paid };
    private readonly CrmDbContext _db;
    private readonly NotificationService _notify;
    public CustomerHealthService(CrmDbContext db, NotificationService notify) { _db = db; _notify = notify; }

    public static bool Tracked(CustomerType t) => t is CustomerType.Customer or CustomerType.Partner;

    /// <summary>Tính điểm cho các khách hàng (bỏ qua phạm vi đơn vị)</summary>
    public async Task<Dictionary<int, HealthResult>> ComputeAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default)
    {
        var today = DateTime.Today; var now = DateTime.Now;
        var result = new Dictionary<int, HealthResult>();
        if (ids.Count == 0) return result;
        var idList = ids.ToList();
        static Dictionary<int, DateTime> MaxBy(IEnumerable<(int Id, DateTime D)> xs) =>
            xs.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Max(x => x.D));

        // ---- Lần tương tác gần nhất: hoạt động đã xong, thanh toán, phiếu hỗ trợ, báo giá đã gửi, hợp đồng ký, chiến dịch phản hồi ----
        var touches = new List<(int Id, DateTime D)>();
        touches.AddRange((await _db.Activities.IgnoreQueryFilters().Where(a => a.CustomerId != null && idList.Contains(a.CustomerId.Value) && a.Status == ActivityStatus.Completed)
            .GroupBy(a => a.CustomerId!.Value).Select(g => new { g.Key, D = g.Max(a => a.CompletedAt ?? a.DueDate) }).ToListAsync(ct)).Select(x => (x.Key, x.D)));
        touches.AddRange((await _db.Payments.IgnoreQueryFilters().Where(p => p.CustomerId != null && idList.Contains(p.CustomerId.Value))
            .GroupBy(p => p.CustomerId!.Value).Select(g => new { g.Key, D = g.Max(p => p.PaymentDate) }).ToListAsync(ct)).Select(x => (x.Key, x.D)));
        touches.AddRange((await _db.Tickets.IgnoreQueryFilters().Where(t => t.CustomerId != null && idList.Contains(t.CustomerId.Value))
            .GroupBy(t => t.CustomerId!.Value).Select(g => new { g.Key, D = g.Max(t => t.CreatedAt) }).ToListAsync(ct)).Select(x => (x.Key, x.D)));
        touches.AddRange((await _db.Quotes.IgnoreQueryFilters().Where(q => q.CustomerId != null && idList.Contains(q.CustomerId.Value) && q.Status != QuoteStatus.Draft)
            .GroupBy(q => q.CustomerId!.Value).Select(g => new { g.Key, D = g.Max(q => q.QuoteDate) }).ToListAsync(ct)).Select(x => (x.Key, x.D)));
        touches.AddRange((await _db.Contracts.IgnoreQueryFilters().Where(k => k.CustomerId != null && idList.Contains(k.CustomerId.Value) && k.SignDate != null)
            .GroupBy(k => k.CustomerId!.Value).Select(g => new { g.Key, D = g.Max(k => k.SignDate!.Value) }).ToListAsync(ct)).Select(x => (x.Key, x.D)));
        touches.AddRange((await _db.CampaignMembers.Where(x => x.CustomerId != null && idList.Contains(x.CustomerId.Value) && x.LastContactAt != null
                && (x.Status == CampaignMemberStatus.Responded || x.Status == CampaignMemberStatus.Converted))
            .GroupBy(x => x.CustomerId!.Value).Select(g => new { g.Key, D = g.Max(x => x.LastContactAt!.Value) }).ToListAsync(ct)).Select(x => (x.Key, x.D)));
        var lastTouch = MaxBy(touches.Where(t => t.D <= now));

        // ---- Công nợ & doanh thu ----
        var invoices = await _db.Invoices.IgnoreQueryFilters().Where(i => i.CustomerId != null && idList.Contains(i.CustomerId.Value) && Billable.Contains(i.Status))
            .Select(i => new { CustomerId = i.CustomerId!.Value, i.Total, i.PaidAmount, i.DueDate, i.InvoiceDate }).ToListAsync(ct);
        var invBy = invoices.ToLookup(i => i.CustomerId);
        var opps = await _db.Opportunities.IgnoreQueryFilters().Where(o => o.CustomerId != null && idList.Contains(o.CustomerId.Value))
            .Select(o => new { CustomerId = o.CustomerId!.Value, o.Stage, o.Amount, o.ClosedAt, o.CreatedAt }).ToListAsync(ct);
        var oppBy = opps.ToLookup(o => o.CustomerId);

        // ---- Hỗ trợ ----
        var tickets = await _db.Tickets.IgnoreQueryFilters().Where(t => t.CustomerId != null && idList.Contains(t.CustomerId.Value)
                && (t.Status == TicketStatus.New || t.Status == TicketStatus.InProgress || t.Status == TicketStatus.WaitingCustomer || t.SatisfactionScore != null))
            .Select(t => new { CustomerId = t.CustomerId!.Value, t.Status, t.SlaPausedAt, t.FirstResponseAt, t.ResponseDueAt, t.ResolveDueAt, t.SatisfactionScore, Done = t.ClosedAt ?? t.ResolvedAt ?? t.CreatedAt })
            .ToListAsync(ct);
        var tkBy = tickets.ToLookup(t => t.CustomerId);

        // ---- NPS, việc quá hạn, hợp đồng sắp hết hạn ----
        var nps = (await _db.NpsResponses.Where(r => r.CustomerId != null && idList.Contains(r.CustomerId.Value) && r.Score != null)
                .Select(r => new { CustomerId = r.CustomerId!.Value, r.Score, r.RespondedAt }).ToListAsync(ct))
            .GroupBy(r => r.CustomerId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.RespondedAt).First().Score);
        var overdueTasks = await _db.Activities.IgnoreQueryFilters().Where(a => a.CustomerId != null && idList.Contains(a.CustomerId.Value) && a.Status == ActivityStatus.Planned && a.DueDate < now)
            .GroupBy(a => a.CustomerId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var renewedIds = (await _db.Contracts.IgnoreQueryFilters().Where(k => k.RenewedFromId != null).Select(k => k.RenewedFromId!.Value).ToListAsync(ct)).ToHashSet();
        var expiring = (await _db.Contracts.IgnoreQueryFilters().Where(k => k.CustomerId != null && idList.Contains(k.CustomerId.Value) && k.Status == ContractStatus.Active
                && k.EndDate >= today && k.EndDate <= today.AddDays(60) && k.RenewalOpportunityId == null).Select(k => new { k.Id, CustomerId = k.CustomerId!.Value }).ToListAsync(ct))
            .Where(k => !renewedIds.Contains(k.Id)).GroupBy(k => k.CustomerId).ToDictionary(g => g.Key, g => g.Count());

        foreach (var id in idList)
        {
            var inv = invBy[id].ToList();
            var overdue = inv.Where(i => i.Total > i.PaidAmount && i.DueDate.HasValue && i.DueDate.Value.Date < today).ToList();
            var op = oppBy[id].ToList();
            var won = op.Where(o => o.Stage == OpportunityStage.ClosedWon).ToList();
            var tk = tkBy[id].ToList();
            var open = tk.Where(t => t.Status is TicketStatus.New or TicketStatus.InProgress or TicketStatus.WaitingCustomer).ToList();
            var csat = tk.Where(t => t.SatisfactionScore.HasValue && t.Done >= today.AddDays(-180)).Select(t => t.SatisfactionScore!.Value).ToList();
            var invoiced12 = inv.Where(i => i.InvoiceDate >= today.AddMonths(-12)).Sum(i => i.Total);
            var won12 = won.Where(o => (o.ClosedAt ?? o.CreatedAt) >= today.AddMonths(-12)).Sum(o => o.Amount);
            result[id] = CustomerHealth.Evaluate(new HealthInput(
                lastTouch.TryGetValue(id, out var lt) ? (today - lt.Date).Days : null,
                inv.Sum(i => Math.Max(0, i.Total - i.PaidAmount)), overdue.Sum(i => i.Total - i.PaidAmount),
                overdue.Count == 0 ? 0 : overdue.Max(i => (today - i.DueDate!.Value.Date).Days),
                op.Count(o => o.Stage is not (OpportunityStage.ClosedWon or OpportunityStage.ClosedLost)),
                Math.Max(invoiced12, won12), Math.Max(inv.Sum(i => i.Total), won.Sum(o => o.Amount)), overdueTasks.GetValueOrDefault(id),
                open.Count, open.Count(t => t.SlaPausedAt == null && (t.FirstResponseAt == null ? t.ResponseDueAt : t.ResolveDueAt) < now),
                csat.Count == 0 ? null : csat.Average(), nps.GetValueOrDefault(id), expiring.GetValueOrDefault(id)));
        }
        return result;
    }

    /// <summary>Tính lại (mọi khách hàng hoặc danh sách chỉ định); báo người phụ trách khi khách chuyển xuống mức rủi ro. Trả về số khách đổi điểm.</summary>
    public async Task<int> RecalculateAsync(IReadOnlyCollection<int>? onlyIds = null, CancellationToken ct = default)
    {
        var q = _db.Customers.IgnoreQueryFilters().AsQueryable();
        if (onlyIds != null) q = q.Where(c => onlyIds.Contains(c.Id));
        var customers = await q.ToListAsync(ct);
        var changed = 0;
        foreach (var chunk in customers.Where(c => Tracked(c.Type)).Chunk(500))
        {
            var scores = await ComputeAsync(chunk.Select(c => c.Id).ToList(), ct);
            foreach (var c in chunk)
            {
                var r = scores[c.Id];
                var before = c.HealthScore;
                c.HealthUpdatedAt = DateTime.Now;
                if (before == r.Score) continue;
                c.HealthScore = r.Score;
                changed++;
                if (before >= CustomerHealth.WarnFrom && r.Score < CustomerHealth.WarnFrom && c.OwnerId.HasValue)
                    _notify.Add(c.OwnerId, "health", I18n.T("{0} chuyển sang mức rủi ro (điểm sức khoẻ {1}) – {2}", c.Name, r.Score, CustomerHealth.Weakest(r) is { } w ? w.Name + ": " + w.Note : ""),
                        $"/Customers/Details/{c.Id}", key: $"health-risk:{c.Id}:{DateTime.Today:yyyyMMdd}");
            }
        }
        // Khách đổi sang tiềm năng / ngừng giao dịch: bỏ điểm cũ
        foreach (var c in customers.Where(c => !Tracked(c.Type) && c.HealthScore != null)) { c.HealthScore = null; c.HealthUpdatedAt = null; changed++; }
        var suppress = _db.SuppressAudit;
        _db.SuppressAudit = true;   // điểm tự tính không ghi nhật ký
        await _db.SaveChangesAsync(ct);
        _db.SuppressAudit = suppress;
        return changed;
    }
}
