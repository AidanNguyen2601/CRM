using CRM.Web.Data;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.EntityFrameworkCore;
using static CRM.Web.Infrastructure.I18n;

namespace CRM.Web.Infrastructure;

/// <summary>
/// Khách hàng 360°: gom mọi dữ liệu liên quan tới một khách hàng (liên hệ, lead gốc, chiến dịch, cơ hội,
/// báo giá, phương án kinh doanh, hợp đồng, hoá đơn, thanh toán, hoạt động) thành một bức tranh duy nhất:
/// chỉ số tổng hợp, điểm sức khỏe, cảnh báo cần chú ý, dòng thời gian hợp nhất, doanh thu theo tháng, sản phẩm đã mua.
/// Quyền: ai xem được khách hàng thì xem được toàn bộ dữ liệu liên quan (giống trang chi tiết trước đây).
/// </summary>
public class Customer360Service
{
    private static readonly InvoiceStatus[] Billable = { InvoiceStatus.Issued, InvoiceStatus.PartiallyPaid, InvoiceStatus.Paid };

    /// <summary>Số ngày không tương tác thì cảnh báo</summary>
    public const int InactiveDays = 60;

    private readonly CrmDbContext _db;
    private readonly CatalogService _catalogs;
    public Customer360Service(CrmDbContext db, CatalogService catalogs) { _db = db; _catalogs = catalogs; }

    public async Task<Customer360ViewModel?> BuildAsync(int id)
    {
        var c = await _db.Customers.Include(x => x.Owner).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return null;

        var today = DateTime.Today;
        var m = new Customer360ViewModel { Customer = c };

        // ---------- Tải dữ liệu ----------
        m.Contacts = await _db.Contacts.AsNoTracking().Where(x => x.CustomerId == id)
            .OrderByDescending(x => x.IsPrimary).ThenBy(x => x.FullName).ToListAsync();
        m.SourceLeads = await _db.Leads.Include(l => l.Campaign).AsNoTracking()
            .Where(l => l.ConvertedCustomerId == id).OrderBy(l => l.CreatedAt).ToListAsync();
        m.Campaigns = await _db.CampaignMembers.Include(x => x.Campaign).AsNoTracking().Where(x => x.CustomerId == id)
            .OrderByDescending(x => x.Campaign!.StartDate).ToListAsync();
        m.Opportunities = await _db.Opportunities.Include(o => o.Owner).AsNoTracking().Where(o => o.CustomerId == id)
            .OrderByDescending(o => o.CreatedAt).ToListAsync();
        m.Quotes = await _db.Quotes.Include(q => q.Opportunity).AsNoTracking().Where(q => q.CustomerId == id)
            .OrderByDescending(q => q.QuoteDate).ToListAsync();
        m.Plans = await _db.BusinessPlans.AsNoTracking().Where(p => p.CustomerId == id)
            .OrderByDescending(p => p.PlanDate).ToListAsync();
        m.Contracts = await _db.Contracts.Include(k => k.Milestones).AsNoTracking().Where(k => k.CustomerId == id)
            .OrderByDescending(k => k.SignDate ?? k.CreatedAt).ToListAsync();
        m.Invoices = await _db.Invoices.Include(i => i.Contract).AsNoTracking().Where(i => i.CustomerId == id)
            .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Id).ToListAsync();
        m.Payments = await _db.Payments.Include(p => p.Invoice).Include(p => p.Contract).AsNoTracking().Where(p => p.CustomerId == id)
            .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.Id).ToListAsync();
        m.Activities = await _db.Activities.Include(a => a.Owner).Include(a => a.Contact).AsNoTracking()
            .Where(a => a.CustomerId == id).OrderByDescending(a => a.DueDate).ToListAsync();
        m.Tickets = await _db.Tickets.Include(t => t.Owner).Include(t => t.Contact).AsNoTracking()
            .Where(t => t.CustomerId == id).OrderByDescending(t => t.CreatedAt).ToListAsync();
        m.OpenTickets = m.Tickets.Count(t => t.IsOpen);
        m.BreachedTickets = m.Tickets.Count(t => t.IsOpen && t.SlaState == "breach");
        var rated = m.Tickets.Where(t => t.SatisfactionScore.HasValue).ToList();
        m.Csat = rated.Count == 0 ? null : rated.Average(t => t.SatisfactionScore!.Value);

        // ---------- Chỉ số tài chính ----------
        var billable = m.Invoices.Where(i => i.IsBillable).ToList();
        m.Invoiced = billable.Sum(i => i.Total);
        m.Invoiced12m = billable.Where(i => i.InvoiceDate >= today.AddMonths(-12)).Sum(i => i.Total);
        m.Paid = m.Payments.Sum(p => p.Amount);
        m.Receivable = billable.Sum(i => i.Outstanding);
        var overdue = billable.Where(i => i.IsOverdue).ToList();
        m.Overdue = overdue.Sum(i => i.Outstanding);
        m.OverdueInvoices = overdue.Count;
        var maxOverdueDays = overdue.Count == 0 ? 0 : overdue.Max(i => (today - i.DueDate!.Value.Date).Days);

        var paidInvoices = m.Payments.Where(p => p.Invoice != null).ToList();
        m.AvgPaymentDays = paidInvoices.Count == 0 ? 0
            : Math.Round((decimal)paidInvoices.Average(p => Math.Max(0, (p.PaymentDate.Date - p.Invoice!.InvoiceDate.Date).Days)), 0);

        var signed = m.Contracts.Where(k => k.IsSigned).ToList();
        m.ContractValue = signed.Sum(k => k.Total);
        m.ActiveContracts = m.Contracts.Count(k => k.Status == ContractStatus.Active);

        // ---------- Chỉ số bán hàng ----------
        var open = m.Opportunities.Where(o => !o.IsClosed).ToList();
        m.OpenOpportunities = open.Count;
        m.OpenPipeline = open.Sum(o => o.Amount);
        m.WeightedPipeline = open.Sum(o => o.Amount * o.Probability / 100m);
        var won = m.Opportunities.Where(o => o.Stage == OpportunityStage.ClosedWon).ToList();
        m.WonTotal = won.Sum(o => o.Amount);
        m.ClosedCount = m.Opportunities.Count(o => o.IsClosed);
        m.WinRate = m.ClosedCount == 0 ? 0 : (int)Math.Round(won.Count * 100.0 / m.ClosedCount);

        // ---------- Tương tác ----------
        m.Upcoming = m.Activities.Where(a => a.Status == ActivityStatus.Planned)
            .OrderBy(a => a.DueDate).Take(8).ToList();
        var touches = new List<DateTime>();
        touches.AddRange(m.Activities.Where(a => a.Status == ActivityStatus.Completed).Select(a => a.CompletedAt ?? a.DueDate));
        touches.AddRange(m.Payments.Select(p => p.PaymentDate));
        touches.AddRange(m.Tickets.Select(t => t.CreatedAt));
        touches.AddRange(m.Quotes.Where(q => q.Status != QuoteStatus.Draft).Select(q => q.QuoteDate));
        touches.AddRange(m.Contracts.Where(k => k.SignDate.HasValue).Select(k => k.SignDate!.Value));
        touches.AddRange(m.Campaigns.Where(x => x.Status is CampaignMemberStatus.Responded or CampaignMemberStatus.Converted
                                                 && x.LastContactAt.HasValue).Select(x => x.LastContactAt!.Value));
        touches = touches.Where(t => t <= DateTime.Now).ToList();
        m.LastInteraction = touches.Count == 0 ? null : touches.Max();
        m.DaysSinceInteraction = m.LastInteraction.HasValue ? (today - m.LastInteraction.Value.Date).Days : null;

        var firstDates = new List<DateTime> { c.CreatedAt };
        firstDates.AddRange(m.SourceLeads.Select(l => l.CreatedAt));
        firstDates.AddRange(signed.Where(k => k.SignDate.HasValue).Select(k => k.SignDate!.Value));
        m.CustomerSince = firstDates.Min();

        // ---------- Doanh thu 12 tháng ----------
        var start = new DateTime(today.Year, today.Month, 1).AddMonths(-11);
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var next = d.AddMonths(1);
            m.Monthly.Add(new MonthlyFinance(d.ToString("MM/yy"),
                billable.Where(i => i.InvoiceDate >= d && i.InvoiceDate < next).Sum(i => i.Total),
                m.Payments.Where(p => p.PaymentDate >= d && p.PaymentDate < next).Sum(p => p.Amount)));
        }

        // ---------- Sản phẩm đã mua (theo hoá đơn đã phát hành, giá trước thuế) ----------
        var lines = await _db.InvoiceLines.AsNoTracking()
            .Where(l => l.Invoice!.CustomerId == id && Billable.Contains(l.Invoice.Status))
            .Select(l => new
            {
                l.ProductId, Code = l.Product != null ? l.Product.Code : null,
                Name = l.Product != null ? l.Product.Name : l.Description,
                Unit = l.Unit ?? (l.Product != null ? l.Product.Unit : null),
                l.Quantity, l.LineTotal, l.InvoiceId, l.Invoice!.InvoiceDate
            }).ToListAsync();
        m.Products = lines.GroupBy(l => l.ProductId.HasValue ? "p" + l.ProductId : "d" + l.Name)
            .Select(g => new PurchasedProduct(g.First().ProductId, g.First().Code, g.First().Name, g.First().Unit,
                g.Sum(x => x.Quantity), g.Sum(x => x.LineTotal), g.Select(x => x.InvoiceId).Distinct().Count(), g.Max(x => x.InvoiceDate)))
            .OrderByDescending(p => p.Amount).ToList();

        // ---------- Đợt thanh toán sắp đến hạn (hợp đồng đã ký, chưa có hoá đơn hiệu lực) ----------
        var invoicedMilestones = m.Invoices.Where(i => i.MilestoneId.HasValue && i.Status != InvoiceStatus.Cancelled)
            .Select(i => i.MilestoneId!.Value).ToHashSet();
        m.UpcomingMilestones = m.Contracts.Where(k => k.Status == ContractStatus.Active)
            .SelectMany(k => k.Milestones.Where(ms => !invoicedMilestones.Contains(ms.Id))
                .Select(ms => new MilestoneDue(k.Id, k.Number, ms.Name, ms.Amount, ms.DueDate)))
            .OrderBy(x => x.DueDate ?? DateTime.MaxValue).ToList();

        // Doanh thu 12 tháng: lấy số lớn hơn giữa hoá đơn đã xuất và cơ hội đã thắng (khách chưa xuất hoá đơn vẫn được tính)
        var won12m = won.Where(o => (o.ClosedAt ?? o.CreatedAt) >= today.AddMonths(-12)).Sum(o => o.Amount);
        // Hỗ trợ (CSAT 6 tháng), NPS gần nhất, hợp đồng sắp hết hạn (60 ngày) chưa có cơ hội / hợp đồng gia hạn
        var csat = m.Tickets.Where(t => t.SatisfactionScore.HasValue && (t.ClosedAt ?? t.ResolvedAt ?? t.CreatedAt) >= today.AddDays(-180)).Select(t => t.SatisfactionScore!.Value).ToList();
        var nps = await _db.NpsResponses.Where(r => r.CustomerId == id && r.Score != null).OrderByDescending(r => r.RespondedAt).Select(r => r.Score).FirstOrDefaultAsync();
        var renewedIds = await _db.Contracts.IgnoreQueryFilters().Where(k => k.CustomerId == id && k.RenewedFromId != null).Select(k => k.RenewedFromId!.Value).ToListAsync();
        var expiring = m.Contracts.Count(k => k.Status == ContractStatus.Active && k.EndDate >= today && k.EndDate <= today.AddDays(60) && k.RenewalOpportunityId == null && !renewedIds.Contains(k.Id));
        m.Health = CustomerHealth.Evaluate(new HealthInput(
            m.DaysSinceInteraction, m.Receivable, m.Overdue, maxOverdueDays, m.OpenOpportunities,
            Math.Max(m.Invoiced12m, won12m), Math.Max(m.Invoiced, m.WonTotal), m.Activities.Count(a => a.IsOverdue),
            m.OpenTickets, m.BreachedTickets, csat.Count == 0 ? null : csat.Average(), nps, expiring));
        m.Alerts = BuildAlerts(m, today);
        m.Timeline = BuildTimeline(m, _catalogs);
        return m;
    }

    private static List<Customer360Alert> BuildAlerts(Customer360ViewModel m, DateTime today)
    {
        var list = new List<Customer360Alert>();
        if (m.OverdueInvoices > 0)
            list.Add(new("danger", "invoice", T("{0} hoá đơn quá hạn, còn nợ {1}", m.OverdueInvoices, m.Overdue.ToShortMoney()), "finance"));

        if (m.BreachedTickets > 0)
            list.Add(new("danger", "support", T("{0} phiếu hỗ trợ đã trễ hạn SLA", m.BreachedTickets), "support"));
        else if (m.OpenTickets > 0)
            list.Add(new("info", "support", T("Khách hàng đang có {0} phiếu hỗ trợ chưa xong", m.OpenTickets), "support"));
        var unhappy = m.Tickets.Where(t => t.SatisfactionScore <= 2 && t.ClosedAt >= today.AddDays(-90)).ToList();
        if (unhappy.Count > 0)
            list.Add(new("warning", "star", T("Khách không hài lòng ({0}/5) với phiếu {1} – nên liên hệ lại", unhappy[0].SatisfactionScore, unhappy[0].Number), "support"));
        var overdueTasks = m.Activities.Count(a => a.IsOverdue);
        if (overdueTasks > 0)
            list.Add(new("danger", "alert", T("{0} hoạt động chăm sóc đã quá hạn", overdueTasks), "timeline"));

        foreach (var o in m.Opportunities.Where(o => !o.IsClosed && o.ExpectedCloseDate.HasValue && o.ExpectedCloseDate.Value.Date < today))
            list.Add(new("warning", "opportunity", T("Cơ hội \"{0}\" đã quá ngày dự kiến chốt {1}", o.Name, o.ExpectedCloseDate.ToDate()), "sales"));

        foreach (var q in m.Quotes.Where(q => q.Status == QuoteStatus.Sent))
        {
            var days = (q.ValidUntil.Date - today).Days;
            if (days < 0) list.Add(new("warning", "quote", T("Báo giá {0} đã hết hiệu lực nhưng khách chưa phản hồi", q.Number), "sales"));
            else if (days <= 7) list.Add(new("info", "quote", T("Báo giá {0} hết hiệu lực sau {1} ngày", q.Number, days), "sales"));
        }

        foreach (var ms in m.UpcomingMilestones.Where(x => x.DueDate.HasValue && x.DueDate.Value.Date <= today.AddDays(14)))
            list.Add(new(ms.DueDate!.Value.Date < today ? "warning" : "info", "coins",
                T("Đợt \"{0}\" của hợp đồng {1} ({2}) đến hạn {3}, chưa xuất hoá đơn", ms.Name, ms.ContractNumber, ms.Amount.ToShortMoney(), ms.DueDate.ToDate()), "finance"));

        foreach (var k in m.Contracts.Where(k => k.Status == ContractStatus.Active && k.EndDate.HasValue
                                                 && k.EndDate.Value.Date >= today && k.EndDate.Value.Date <= today.AddDays(30)))
            list.Add(new("info", "contract", T("Hợp đồng {0} kết thúc ngày {1} – cân nhắc gia hạn", k.Number, k.EndDate.ToDate()), "finance"));

        foreach (var ct in m.Contacts.Where(x => x.BirthDate.HasValue))
        {
            var days = DaysUntilBirthday(ct.BirthDate!.Value, today);
            if (days <= 14)
                list.Add(new("info", "contact", days == 0 ? T("Hôm nay là sinh nhật {0}", ct.FullName)
                    : T("Sinh nhật {0} sau {1} ngày ({2})", ct.FullName, days, ct.BirthDate.Value.ToString("dd/MM")), "contacts"));
        }

        if (m.Customer.Type != CustomerType.Inactive)
        {
            if (m.DaysSinceInteraction is null)
                list.Add(new("warning", "call", T("Chưa ghi nhận tương tác nào với khách hàng"), "timeline"));
            else if (m.DaysSinceInteraction > InactiveDays)
                list.Add(new("warning", "call", T("Đã {0} ngày không tương tác với khách hàng", m.DaysSinceInteraction), "timeline"));
            if (!m.Upcoming.Any())
                list.Add(new("info", "calendar", T("Chưa có kế hoạch chăm sóc tiếp theo"), "timeline"));
        }
        return list.OrderBy(a => a.Level == "danger" ? 0 : a.Level == "warning" ? 1 : 2).ToList();
    }

    public static int DaysUntilBirthday(DateTime birth, DateTime today)
    {
        var day = Math.Min(birth.Day, DateTime.DaysInMonth(today.Year, birth.Month));
        var next = new DateTime(today.Year, birth.Month, day);
        if (next < today)
        {
            day = Math.Min(birth.Day, DateTime.DaysInMonth(today.Year + 1, birth.Month));
            next = new DateTime(today.Year + 1, birth.Month, day);
        }
        return (next - today).Days;
    }

    private static List<TimelineEvent> BuildTimeline(Customer360ViewModel m, CatalogService catalogs)
    {
        var t = new List<TimelineEvent>();

        foreach (var l in m.SourceLeads)
        {
            t.Add(new(l.CreatedAt, "marketing", "lead", T("Lead mới: {0}", l.FullName),
                $"{l.Source.GetDisplayName()}{(l.Campaign != null ? " · " + l.Campaign.Name : "")}", "Leads", "Details", l.Id, l.EstimatedValue > 0 ? l.EstimatedValue : null));
            if (l.ConvertedAt.HasValue)
                t.Add(new(l.ConvertedAt.Value, "marketing", "check", T("Chuyển đổi lead thành khách hàng"), l.FullName, "Leads", "Details", l.Id, null, "done"));
        }
        foreach (var x in m.Campaigns.Where(x => x.Campaign != null))
            t.Add(new(x.LastContactAt ?? x.AddedAt, "marketing", "campaign", T("Chiến dịch: {0}", x.Campaign!.Name),
                $"{x.Campaign.Channel.GetDisplayName()} · {x.Status.GetDisplayName()} · {x.FullName}", "Campaigns", "Details", x.CampaignId, null));

        foreach (var a in m.Activities)
            t.Add(new(a.CompletedAt ?? a.DueDate, "care", Icons.ForActivity(a.Type), $"{a.Type.GetDisplayName()}: {a.Subject}",
                string.Join(" · ", new[] { a.Status.GetDisplayName(), a.Contact?.FullName, a.Owner?.FullName }.Where(s => !string.IsNullOrEmpty(s))),
                "Activities", "Edit", a.Id, null, a.Status == ActivityStatus.Completed ? "done" : a.IsOverdue ? "overdue" : "planned"));

        foreach (var o in m.Opportunities)
        {
            t.Add(new(o.CreatedAt, "sales", "opportunity", T("Mở cơ hội: {0}", o.Name), o.Owner?.FullName, "Opportunities", "Details", o.Id, o.Amount));
            if (o.IsClosed && o.ClosedAt.HasValue)
                t.Add(new(o.ClosedAt.Value, "sales", o.Stage == OpportunityStage.ClosedWon ? "trophy" : "loss",
                    o.Stage == OpportunityStage.ClosedWon ? T("Thắng cơ hội: {0}", o.Name) : T("Thua cơ hội: {0}", o.Name),
                    o.LostReason, "Opportunities", "Details", o.Id, o.Amount, o.Stage == OpportunityStage.ClosedWon ? "done" : "overdue"));
        }
        foreach (var q in m.Quotes)
            t.Add(new(q.QuoteDate, "sales", "quote", T("Báo giá {0}", q.Number), q.Status.GetDisplayName(), "Quotes", "Details", q.Id, q.Total,
                q.Status == QuoteStatus.Accepted ? "done" : q.Status == QuoteStatus.Rejected ? "overdue" : null));
        foreach (var p in m.Plans)
            t.Add(new(p.PlanDate, "sales", "plan", T("Phương án kinh doanh {0}", p.Number), $"{p.Title} · {p.Status.GetDisplayName()}",
                "BusinessPlans", "Details", p.Id, p.Revenue));

        foreach (var k in m.Contracts)
            t.Add(new(k.SignDate ?? k.CreatedAt, "finance", "contract",
                k.SignDate.HasValue ? T("Ký hợp đồng {0}", k.Number) : T("Hợp đồng {0}", k.Number),
                $"{k.Title} · {k.Status.GetDisplayName()}", "Contracts", "Details", k.Id, k.Total, k.IsSigned ? "done" : null));
        foreach (var i in m.Invoices.Where(i => i.Status != InvoiceStatus.Draft))
            t.Add(new(i.IssuedAt ?? i.InvoiceDate, "finance", "invoice", T("Hoá đơn {0}", i.FullNumber),
                i.Status.GetDisplayName() + (i.IsOverdue ? " · " + T("Quá hạn") : ""), "Invoices", "Details", i.Id, i.Total,
                i.IsOverdue ? "overdue" : i.Status == InvoiceStatus.Paid ? "done" : null));
        foreach (var p in m.Payments)
            t.Add(new(p.PaymentDate, "finance", "wallet", T("Thu tiền {0}", p.Number),
                string.Join(" · ", new[] { catalogs.Text(CatalogGroup.PaymentMethod, p.Method), p.Invoice?.FullNumber, p.Contract?.Number }.Where(s => !string.IsNullOrEmpty(s))),
                "Payments", "Details", p.Id, p.Amount, "done"));
        foreach (var k in m.Tickets)
        {
            var detail = string.Join(" · ", new[] { k.Status.GetDisplayName(), k.Priority.GetDisplayName(), catalogs.Text(CatalogGroup.TicketCategory, k.Category), k.Owner?.FullName }
                .Where(s => !string.IsNullOrEmpty(s)));
            t.Add(new(k.CreatedAt, "support", "support", T("Yêu cầu hỗ trợ {0}: {1}", k.Number, k.Subject), detail,
                "Tickets", "Details", k.Id, null, k.IsOpen ? (k.SlaState == "breach" ? "overdue" : "planned") : "done"));
            if (k.SatisfactionScore.HasValue && k.ClosedAt.HasValue)
                t.Add(new(k.ClosedAt.Value, "support", "star", T("Khách đánh giá {0}/5 – phiếu {1}", k.SatisfactionScore, k.Number), k.SatisfactionComment,
                    "Tickets", "Details", k.Id, null, k.SatisfactionScore <= 2 ? "overdue" : "done"));
        }

        return t.OrderByDescending(e => e.When).ToList();
    }
}

public record HealthInput(int? DaysSinceInteraction, decimal Receivable, decimal Overdue, int MaxOverdueDays,
    int OpenOpportunities, decimal Revenue12m, decimal RevenueAllTime, int OverdueTasks,
    int OpenTickets = 0, int BreachedTickets = 0, double? Csat = null, int? LatestNps = null, int ExpiringNoRenewal = 0);

/// <summary>
/// Điểm sức khỏe khách hàng 0–100 = tổng 7 yếu tố. ≥ 70: Tốt, 40–69: Cần chú ý, &lt; 40: Rủi ro.
/// Dùng chung cho trang khách hàng 360° và tác vụ tính hằng giờ (CustomerHealthService) – logic thuần C#, không truy cập DB.
/// </summary>
public static class CustomerHealth
{
    public const int GoodFrom = 70, WarnFrom = 40;

    public static string LevelOf(int score) => score >= GoodFrom ? "good" : score >= WarnFrom ? "warn" : "risk";

    public static HealthResult Evaluate(HealthInput i)
    {
        var f = new List<HealthFactor>();

        // 1. Tương tác gần đây (25)
        var d = i.DaysSinceInteraction;
        f.Add(new(T("Tương tác gần đây"), d switch { null => 0, <= 30 => 25, <= 60 => 16, <= 90 => 8, _ => 0 }, 25,
            d is null ? T("Chưa có tương tác") : T("{0} ngày trước", d)));

        // 2. Công nợ (20)
        int debt = i.Overdue <= 0 ? 20 : i.MaxOverdueDays <= 30 ? 10 : 0;
        f.Add(new(T("Thanh toán đúng hạn"), debt, 20,
            i.Overdue <= 0 ? (i.Receivable > 0 ? T("Còn phải thu {0}, chưa quá hạn", i.Receivable.ToShortMoney()) : T("Không có nợ quá hạn"))
                           : T("Quá hạn {0}, lâu nhất {1} ngày", i.Overdue.ToShortMoney(), i.MaxOverdueDays)));

        // 3. Doanh thu (15)
        int rev = i.Revenue12m > 0 ? 15 : i.RevenueAllTime > 0 ? 7 : 0;
        f.Add(new(T("Doanh thu"), rev, 15,
            i.Revenue12m > 0 ? T("{0} trong 12 tháng", i.Revenue12m.ToShortMoney())
                : i.RevenueAllTime > 0 ? T("Không phát sinh trong 12 tháng") : T("Chưa có doanh thu")));

        // 4. Hỗ trợ (15): phiếu tồn, trễ SLA, mức hài lòng
        var sup = 15 - Math.Min(8, i.OpenTickets * 3) - (i.BreachedTickets > 0 ? 5 : 0) - (i.Csat is < 3 ? 5 : 0);
        var supNotes = new List<string>();
        if (i.OpenTickets > 0) supNotes.Add(T("{0} phiếu đang mở", i.OpenTickets));
        if (i.BreachedTickets > 0) supNotes.Add(T("{0} phiếu trễ SLA", i.BreachedTickets));
        if (i.Csat.HasValue) supNotes.Add(T("hài lòng TB {0}/5", i.Csat.Value.ToString("0.#")));
        f.Add(new(T("Hỗ trợ"), Math.Max(0, sup), 15, supNotes.Count == 0 ? T("Không có phiếu hỗ trợ tồn đọng") : string.Join(", ", supNotes)));

        // 5. Cơ hội & gia hạn (10)
        var opp = (i.OpenOpportunities > 0 ? 10 : 0) - (i.ExpiringNoRenewal > 0 ? 5 : 0);
        var oppNote = i.OpenOpportunities > 0 ? T("{0} cơ hội", i.OpenOpportunities) : T("Không có cơ hội mới");
        if (i.ExpiringNoRenewal > 0) oppNote += " · " + T("{0} hợp đồng sắp hết hạn chưa có kế hoạch gia hạn", i.ExpiringNoRenewal);
        f.Add(new(T("Cơ hội & gia hạn"), Math.Max(0, opp), 10, oppNote));

        // 6. NPS (10)
        f.Add(new("NPS", i.LatestNps switch { null => 5, >= 9 => 10, >= 7 => 6, _ => 0 }, 10,
            i.LatestNps is null ? T("Chưa có khảo sát NPS") : T("Lần gần nhất chấm {0}/10", i.LatestNps)));

        // 7. Kỷ luật chăm sóc (5)
        f.Add(new(T("Chăm sóc đúng hẹn"), i.OverdueTasks == 0 ? 5 : i.OverdueTasks <= 2 ? 2 : 0, 5,
            i.OverdueTasks == 0 ? T("Không có việc quá hạn") : T("{0} việc quá hạn", i.OverdueTasks)));

        var score = Math.Clamp(f.Sum(x => x.Score), 0, 100);
        return new HealthResult { Score = score, Factors = f, Level = LevelOf(score) };
    }

    /// <summary>Yếu tố kéo điểm xuống nhiều nhất</summary>
    public static HealthFactor? Weakest(HealthResult r) =>
        r.Factors.Where(x => x.Score < x.Max).OrderBy(x => (double)x.Score / x.Max).ThenByDescending(x => x.Max).FirstOrDefault();

    public static string LevelText(string level) => level switch
    {
        "good" => T("Tốt"),
        "warn" => T("Cần chú ý"),
        _ => T("Rủi ro")
    };

    public static string LevelCss(string level) => level switch { "good" => "success", "warn" => "warning", _ => "danger" };
}
