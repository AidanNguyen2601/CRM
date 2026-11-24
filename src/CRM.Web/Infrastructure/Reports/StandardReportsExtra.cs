using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using static CRM.Web.Infrastructure.I18n;
using K = CRM.Web.Infrastructure.Reports.ReportColumnKind;

namespace CRM.Web.Infrastructure.Reports;

/// <summary>
/// Báo cáo chuẩn cho các nghiệp vụ mở rộng: doanh thu định kỳ & gia hạn, tuân thủ SLA, NPS, duyệt chiết khấu, tỷ lệ chốt báo giá,
/// lead theo form website / UTM, lead theo điểm, sức khoẻ khách hàng. Quy tắc phạm vi giống các báo cáo khác (x.Owner, bộ lọc đơn vị của DbContext).
/// </summary>
public static partial class StandardReports
{
    private static IReadOnlyList<ReportDefinition> Extra => new ReportDefinition[]
    {
        new("quote-win-rate", GroupSales, T("Tỷ lệ chốt báo giá theo nhân viên"), T("Báo giá lập trong kỳ: đã gửi, chấp nhận, từ chối, tỷ lệ chấp nhận, giá trị, chiết khấu thực tế trung bình và tỷ lệ phải duyệt chiết khấu."), "quote",
            ReportFilters.Period, QuoteWinRate),
        new("discount-approvals", GroupSales, T("Duyệt chiết khấu báo giá"), T("Báo giá có chiết khấu vượt hạn mức trong kỳ: chờ duyệt, đã duyệt, bị từ chối, người duyệt và thời gian xử lý."), "check",
            ReportFilters.Period | ReportFilters.Owner, DiscountApprovals),

        new("web-leads-utm", GroupMarketing, T("Lead theo form website & nguồn quảng cáo (UTM)"), T("Lead từ form website hoặc có UTM tạo trong kỳ: số lead, đủ điều kiện, chuyển đổi, điểm trung bình và doanh số thắng theo nguồn / kênh / chiến dịch UTM."), "globe",
            ReportFilters.Period | ReportFilters.Owner, WebLeadsUtm),
        new("lead-score-bands", GroupMarketing, T("Lead theo mức điểm"), T("Lead đang theo dõi tạo trong kỳ chia theo dải điểm 0–100: trạng thái, tỷ lệ đủ điều kiện và giá trị dự kiến – để ưu tiên chăm sóc lead điểm cao."), "lead",
            ReportFilters.Period | ReportFilters.Owner, LeadScoreBands),
        new("customer-health", GroupMarketing, T("Sức khoẻ khách hàng"), T("Khách hàng xếp theo điểm sức khoẻ thấp nhất (nguy cơ rời bỏ): điểm, mức, doanh thu 12 tháng và người phụ trách."), "star",
            ReportFilters.Owner | ReportFilters.Top, CustomerHealthReport),

        new("renewals-mrr", GroupFinance, T("Doanh thu định kỳ (MRR / ARR)"), T("Hợp đồng đang thực hiện có thanh toán định kỳ: số tiền mỗi kỳ quy ra tháng (MRR) và năm (ARR), kỳ lập hoá đơn tiếp theo, ngày kết thúc."), "contract",
            ReportFilters.Owner, RenewalsMrr),
        new("renewal-rate", GroupFinance, T("Tỷ lệ gia hạn hợp đồng theo tháng"), T("Hợp đồng kết thúc mỗi tháng trong năm: đã có cơ hội gia hạn, đã gia hạn, giá trị và tỷ lệ gia hạn."), "trending",
            ReportFilters.Year | ReportFilters.Owner, RenewalRate),

        new("sla-compliance", GroupSupport, T("Tuân thủ SLA theo chính sách và độ ưu tiên"), T("Phiếu tiếp nhận trong kỳ theo chính sách SLA và độ ưu tiên: tỷ lệ phản hồi / giải quyết đúng hạn, thời gian trung bình, phiếu đang trễ và thời gian đồng hồ SLA tạm dừng."), "clock",
            ReportFilters.Period | ReportFilters.Owner, SlaCompliance),
        new("nps-by-survey", GroupSupport, T("NPS theo đợt khảo sát"), T("Mỗi đợt khảo sát NPS: đã gửi, đã trả lời, tỷ lệ trả lời, số khách ủng hộ / trung lập / phản đối và điểm NPS."), "star",
            ReportFilters.Period | ReportFilters.Owner, NpsBySurvey),
        new("nps-detractors", GroupSupport, T("Khách phản đối NPS cần chăm sóc"), T("Khách chấm 6 điểm trở xuống trong kỳ kèm góp ý và người phụ trách – danh sách cần liên hệ lại."), "alert",
            ReportFilters.Period | ReportFilters.Owner, NpsDetractors),
    };

    // ================== BÁN HÀNG ==================

    private static async Task<ReportResult> QuoteWinRate(ReportContext x)
    {
        var f = x.Filter;
        var users = await SalesUsers(x);
        var ids = users.Select(u => u.Id).ToList();
        var quotes = await x.Db.Quotes.Where(q => q.QuoteDate >= f.FromDate && q.QuoteDate < f.ToExclusive && q.OwnerId != null && ids.Contains(q.OwnerId.Value))
            .Select(q => new { q.OwnerId, q.Status, q.Total, q.EffectiveDiscount, q.ApprovalStatus }).ToListAsync();
        var r = new ReportResult()
            .Col(T("Nhân viên")).Col(T("Báo giá lập"), K.Number).Col(T("Đã gửi khách"), K.Number).Col(T("Chấp nhận"), K.Number).Col(T("Từ chối"), K.Number)
            .Col(T("Tỷ lệ chấp nhận"), K.Percent).Col(T("Giá trị đã gửi"), K.Money).Col(T("Giá trị chấp nhận"), K.Money)
            .Col(T("Chiết khấu TB (%)"), K.Percent).Col(T("Phải duyệt chiết khấu"), K.Number);
        foreach (var u in users)
        {
            var mine = quotes.Where(q => q.OwnerId == u.Id).ToList();
            if (mine.Count == 0) continue;
            var sent = mine.Where(q => q.Status != QuoteStatus.Draft).ToList();
            var acc = mine.Where(q => q.Status == QuoteStatus.Accepted).ToList();
            var rej = mine.Count(q => q.Status == QuoteStatus.Rejected);
            r.Add(u.FullName, mine.Count, sent.Count, acc.Count, rej, Pct(acc.Count, acc.Count + rej),
                sent.Sum(q => q.Total), acc.Sum(q => q.Total), mine.Count == 0 ? null : Math.Round(mine.Average(q => q.EffectiveDiscount), 1),
                mine.Count(q => q.ApprovalStatus != QuoteApproval.None));
        }
        r.Rows.Sort((a, b) => ((decimal)b[7]!).CompareTo((decimal)a[7]!));
        r.AutoTotals(T("Tổng cộng"));
        var ta = (int)(decimal)r.Totals![3]!; var tr = (int)(decimal)r.Totals![4]!;
        r.Totals[5] = Pct(ta, ta + tr);
        r.Totals[8] = quotes.Count == 0 ? null : Math.Round(quotes.Average(q => q.EffectiveDiscount), 1);
        r.Note = T("Tỷ lệ chấp nhận = chấp nhận / (chấp nhận + từ chối); báo giá đang chờ phản hồi chưa được tính.");
        return r.Chart(0, 7);
    }

    private static async Task<ReportResult> DiscountApprovals(ReportContext x)
    {
        var f = x.Filter; var owner = x.Owner;
        var list = await x.Db.Quotes.Where(q => q.ApprovalStatus != QuoteApproval.None && q.QuoteDate >= f.FromDate && q.QuoteDate < f.ToExclusive && (owner == null || q.OwnerId == owner))
            .OrderByDescending(q => q.QuoteDate)
            .Select(q => new
            {
                q.Id, q.Number, Customer = q.Customer!.Name, q.QuoteDate, q.EffectiveDiscount, q.Total, q.ApprovalStatus, q.ApprovalNote,
                Approver = q.DiscountApprovedBy != null ? q.DiscountApprovedBy.FullName : null, q.DiscountApprovedAt, q.CreatedAt, Owner = q.Owner != null ? q.Owner.FullName : null
            }).ToListAsync();
        var r = new ReportResult()
            .Col(T("Số báo giá")).Col(T("Khách hàng")).Col(T("Ngày"), K.Date).Col(T("Chiết khấu thực tế (%)"), K.Percent).Col(T("Giá trị"), K.Money)
            .Col(T("Trạng thái duyệt")).Col(T("Người duyệt")).Col(T("Thời gian xử lý (giờ)"), K.Number, false).Col(T("Ý kiến")).Col(T("Phụ trách"));
        foreach (var q in list)
            r.AddLink($"/Quotes/Details/{q.Id}", q.Number, q.Customer, q.QuoteDate, q.EffectiveDiscount, q.Total, q.ApprovalStatus.GetDisplayName(), q.Approver,
                q.DiscountApprovedAt.HasValue ? Math.Round((decimal)(q.DiscountApprovedAt.Value - q.CreatedAt).TotalHours, 1) : null, q.ApprovalNote, q.Owner);
        r.AutoTotals(T("Tổng cộng"));
        r.Totals![3] = list.Count == 0 ? null : Math.Round(list.Average(q => q.EffectiveDiscount), 1);
        r.Note = T("Chờ duyệt: {0} · Đã duyệt: {1} · Bị từ chối: {2}",
            list.Count(q => q.ApprovalStatus == QuoteApproval.Pending), list.Count(q => q.ApprovalStatus == QuoteApproval.Approved), list.Count(q => q.ApprovalStatus == QuoteApproval.Rejected));
        return r;
    }

    // ================== KHÁCH HÀNG & TIẾP THỊ ==================

    private static async Task<ReportResult> WebLeadsUtm(ReportContext x)
    {
        var f = x.Filter; var owner = x.Owner;
        var leads = await x.Db.Leads.Where(l => (l.WebFormId != null || l.UtmSource != null) && l.CreatedAt >= f.FromDate && l.CreatedAt < f.ToExclusive && (owner == null || l.OwnerId == owner))
            .Select(l => new { l.UtmSource, l.UtmMedium, l.UtmCampaign, Form = l.WebForm != null ? l.WebForm.Name : null, l.Status, l.Score, l.ConvertedOpportunityId }).ToListAsync();
        var oppIds = leads.Where(l => l.ConvertedOpportunityId.HasValue).Select(l => l.ConvertedOpportunityId!.Value).Distinct().ToList();
        var won = oppIds.Count == 0 ? new Dictionary<int, decimal>()
            : await x.Db.Opportunities.Where(o => oppIds.Contains(o.Id) && o.Stage == OpportunityStage.ClosedWon).ToDictionaryAsync(o => o.Id, o => o.Amount);
        var r = new ReportResult()
            .Col(T("Nguồn / kênh")).Col(T("Chiến dịch UTM")).Col(T("Form website")).Col("Lead", K.Number).Col(T("Đủ điều kiện"), K.Number)
            .Col(T("Đã chuyển đổi"), K.Number).Col(T("Tỷ lệ chuyển đổi"), K.Percent).Col(T("Điểm TB (lead đang theo dõi)"), K.Number, false).Col(T("Doanh số thắng"), K.Money);
        foreach (var g in leads.GroupBy(l => (Src: l.UtmSource ?? "(direct)", Med: l.UtmMedium ?? "", Camp: l.UtmCampaign ?? "")).OrderByDescending(g => g.Count()))
        {
            var conv = g.Count(l => l.Status == LeadStatus.Converted);
            r.Add(string.IsNullOrEmpty(g.Key.Med) ? g.Key.Src : $"{g.Key.Src} / {g.Key.Med}", g.Key.Camp, string.Join(", ", g.Select(l => l.Form).Where(n => n != null).Distinct()),
                g.Count(), g.Count(l => l.Status is LeadStatus.Qualified or LeadStatus.Converted), conv, Pct(conv, g.Count()),
                AvgOpenScore(g.Select(l => (l.Status, l.Score))), g.Where(l => l.ConvertedOpportunityId.HasValue).Sum(l => won.GetValueOrDefault(l.ConvertedOpportunityId!.Value)));
        }
        r.AutoTotals(T("Tổng cộng"));
        var tl = (int)(decimal)r.Totals![3]!; r.Totals[6] = Pct((int)(decimal)r.Totals![5]!, tl);
        r.Totals[7] = AvgOpenScore(leads.Select(l => (l.Status, l.Score)));
        r.Note = T("Doanh số thắng tính trên cơ hội do lead chuyển đổi ra. Gắn ?utm_source=…&utm_medium=…&utm_campaign=… vào đường dẫn quảng cáo để có số liệu theo kênh.");
        return r.Chart(0, 3, ReportChartKind.Pie);
    }

    /// <summary>Điểm trung bình của lead đang theo dõi (lead đã chuyển đổi / không phù hợp luôn 0 điểm nên bỏ qua)</summary>
    private static decimal? AvgOpenScore(IEnumerable<(LeadStatus Status, int Score)> xs)
    {
        var l = xs.Where(v => v.Status is LeadStatus.New or LeadStatus.Contacted or LeadStatus.Qualified).Select(v => v.Score).ToList();
        return l.Count == 0 ? null : Math.Round((decimal)l.Average(), 0);
    }

    private static async Task<ReportResult> LeadScoreBands(ReportContext x)
    {
        var f = x.Filter; var owner = x.Owner;
        // Lead đã chuyển đổi / không phù hợp luôn có điểm 0 (không còn được chấm) nên chỉ phân tích lead đang theo dõi
        var leads = await x.Db.Leads.Where(l => l.CreatedAt >= f.FromDate && l.CreatedAt < f.ToExclusive && (owner == null || l.OwnerId == owner)
                && (l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted || l.Status == LeadStatus.Qualified))
            .Select(l => new { l.Score, l.Status, l.EstimatedValue }).ToListAsync();
        var bands = new (string Label, int Min, int Max)[] { ("0–24", 0, 24), ("25–44", 25, 44), ("45–69", 45, 69), ("70–100", 70, 100) };
        var r = new ReportResult()
            .Col(T("Điểm lead")).Col(T("Lead đang theo dõi"), K.Number).Col(T("Mới"), K.Number).Col(T("Đã liên hệ"), K.Number).Col(T("Đủ điều kiện"), K.Number)
            .Col(T("Tỷ lệ đủ điều kiện"), K.Percent).Col(T("Giá trị dự kiến"), K.Money).Col(T("Giá trị TB / lead"), K.Money, false);
        foreach (var (label, min, max) in bands)
        {
            var b = leads.Where(l => l.Score >= min && l.Score <= max).ToList();
            r.Add(label, b.Count, b.Count(l => l.Status == LeadStatus.New), b.Count(l => l.Status == LeadStatus.Contacted), b.Count(l => l.Status == LeadStatus.Qualified),
                Pct(b.Count(l => l.Status == LeadStatus.Qualified), b.Count), b.Sum(l => l.EstimatedValue), b.Count == 0 ? null : Math.Round(b.Average(l => l.EstimatedValue)));
        }
        r.AutoTotals(T("Tổng cộng"));
        r.Totals![5] = Pct((int)(decimal)r.Totals![4]!, (int)(decimal)r.Totals![1]!);
        r.Totals[7] = leads.Count == 0 ? null : Math.Round(leads.Average(l => l.EstimatedValue));
        r.Note = T("Chỉ gồm lead đang theo dõi (mới, đã liên hệ, đủ điều kiện). Lead đã chuyển đổi hoặc không phù hợp không còn điểm. Ưu tiên gọi lead dải điểm cao.");
        return r.Chart(0, 1, ReportChartKind.Pie);
    }


    private static async Task<ReportResult> CustomerHealthReport(ReportContext x)
    {
        var owner = x.Owner; var top = Math.Clamp(x.Filter.Top ?? 50, 1, 500); var since = DateTime.Today.AddMonths(-12);
        var q = x.Db.Customers.Where(c => c.HealthScore != null && (owner == null || c.OwnerId == owner));
        var all = await q.Select(c => c.HealthScore!.Value).ToListAsync();
        var list = await q.OrderBy(c => c.HealthScore).ThenBy(c => c.Name).Take(top)
            .Select(c => new { c.Id, c.Code, c.Name, Score = c.HealthScore!.Value, c.HealthUpdatedAt, Owner = c.Owner != null ? c.Owner.FullName : null }).ToListAsync();
        var ids = list.Select(c => c.Id).ToList();
        var rev = ids.Count == 0 ? new Dictionary<int, decimal>()
            : (await x.Db.Invoices.Where(i => i.CustomerId != null && ids.Contains(i.CustomerId.Value) && i.InvoiceDate >= since
                    && (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid || i.Status == InvoiceStatus.Paid))
                .GroupBy(i => i.CustomerId!.Value).Select(g => new { g.Key, V = g.Sum(i => i.Total) }).ToListAsync()).ToDictionary(g => g.Key, g => g.V);
        var r = new ReportResult()
            .Col(T("Mã KH")).Col(T("Khách hàng")).Col(T("Điểm sức khoẻ"), K.Number, false).Col(T("Mức")).Col(T("Doanh thu 12 tháng"), K.Money).Col(T("Phụ trách")).Col(T("Cập nhật"), K.Date);
        foreach (var c in list)
            r.AddLink($"/Customers/Details/{c.Id}", c.Code, c.Name, c.Score, CustomerHealth.LevelText(CustomerHealth.LevelOf(c.Score)), rev.GetValueOrDefault(c.Id), c.Owner, c.HealthUpdatedAt);
        r.LinkColumn = 1;
        r.AutoTotals(T("Tổng cộng"));
        r.Note = T("Toàn bộ khách đã chấm điểm: {0} · Tốt {1} · Cần chú ý {2} · Rủi ro {3}. Bảng liệt kê {4} khách điểm thấp nhất.",
            all.Count, all.Count(s => s >= CustomerHealth.GoodFrom), all.Count(s => s >= CustomerHealth.WarnFrom && s < CustomerHealth.GoodFrom), all.Count(s => s < CustomerHealth.WarnFrom), list.Count);
        return r;
    }

    // ================== TÀI CHÍNH & CÔNG NỢ ==================

    private static async Task<ReportResult> RenewalsMrr(ReportContext x)
    {
        var owner = x.Owner; var today = DateTime.Today;
        var list = await x.Db.Contracts.Where(c => c.Status == ContractStatus.Active && c.BillingCycle != BillingCycle.None && (owner == null || c.OwnerId == owner))
            .OrderBy(c => c.EndDate)
            .Select(c => new { c.Id, c.Number, Customer = c.Customer!.Name, c.BillingCycle, c.RecurringAmount, c.NextBillingDate, c.EndDate, Owner = c.Owner != null ? c.Owner.FullName : null }).ToListAsync();
        var r = new ReportResult()
            .Col(T("Số hợp đồng")).Col(T("Khách hàng")).Col(T("Chu kỳ")).Col(T("Số tiền mỗi kỳ"), K.Money, false).Col("MRR", K.Money).Col("ARR", K.Money)
            .Col(T("Kỳ lập hoá đơn tiếp theo"), K.Date).Col(T("Ngày kết thúc"), K.Date).Col(T("Còn lại (ngày)"), K.Number, false).Col(T("Phụ trách"));
        foreach (var c in list)
        {
            var months = Math.Max(1, (int)c.BillingCycle);
            var mrr = Math.Round(c.RecurringAmount / months);
            r.AddLink($"/Contracts/Details/{c.Id}", c.Number, c.Customer, c.BillingCycle.GetDisplayName(), c.RecurringAmount, mrr, mrr * 12, c.NextBillingDate, c.EndDate,
                c.EndDate.HasValue ? (c.EndDate.Value.Date - today).Days : null, c.Owner);
        }
        r.AutoTotals(T("Tổng cộng")).Chart(1, 4);
        r.Note = T("MRR = số tiền mỗi kỳ chia số tháng của chu kỳ; ARR = MRR × 12. Chỉ tính hợp đồng đang thực hiện có thanh toán định kỳ.");
        return r;
    }

    private static async Task<ReportResult> RenewalRate(ReportContext x)
    {
        var y = x.Filter.YearValue; var from = new DateTime(y, 1, 1); var to = from.AddYears(1); var owner = x.Owner;
        var ending = await x.Db.Contracts.Where(c => c.EndDate != null && c.EndDate >= from && c.EndDate < to
                && (c.Status == ContractStatus.Active || c.Status == ContractStatus.Completed || c.Status == ContractStatus.Liquidated) && (owner == null || c.OwnerId == owner))
            .Select(c => new { c.Id, c.EndDate, c.Total, c.RenewalOpportunityId }).ToListAsync();
        var ids = ending.Select(c => c.Id).ToList();
        var renewed = ids.Count == 0 ? new Dictionary<int, decimal>()
            : (await x.Db.Contracts.Where(c => c.RenewedFromId != null && ids.Contains(c.RenewedFromId.Value) && c.Status != ContractStatus.Cancelled)
                .Select(c => new { Src = c.RenewedFromId!.Value, c.Total }).ToListAsync()).GroupBy(c => c.Src).ToDictionary(g => g.Key, g => g.Sum(c => c.Total));
        var r = new ReportResult()
            .Col(T("Tháng")).Col(T("Hợp đồng kết thúc"), K.Number).Col(T("Đã có cơ hội gia hạn"), K.Number).Col(T("Đã gia hạn"), K.Number)
            .Col(T("Tỷ lệ gia hạn"), K.Percent).Col(T("Giá trị kết thúc"), K.Money).Col(T("Giá trị gia hạn"), K.Money);
        for (var m = 1; m <= 12; m++)
        {
            var inMonth = ending.Where(c => c.EndDate!.Value.Month == m).ToList();
            var ren = inMonth.Where(c => renewed.ContainsKey(c.Id)).ToList();
            r.Add($"{m:00}/{y}", inMonth.Count, inMonth.Count(c => c.RenewalOpportunityId.HasValue), ren.Count, Pct(ren.Count, inMonth.Count),
                inMonth.Sum(c => c.Total), ren.Sum(c => renewed[c.Id]));
        }
        r.AutoTotals(T("Cả năm"));
        r.Totals![4] = Pct((int)(decimal)r.Totals![3]!, (int)(decimal)r.Totals![1]!);
        r.Note = T("Đã gia hạn = có hợp đồng nối tiếp được tạo từ hợp đồng này (nút Gia hạn). Tháng chưa tới có tỷ lệ thấp vì chưa đến hạn gia hạn.");
        return r.Chart(0, 4, ReportChartKind.Line);
    }

    // ================== CHĂM SÓC KHÁCH HÀNG ==================

    private static async Task<ReportResult> SlaCompliance(ReportContext x)
    {
        var f = x.Filter; var now = DateTime.Now;
        var rows = await TicketsQuery(x).Where(t => t.CreatedAt >= f.FromDate && t.CreatedAt < f.ToExclusive)
            .Select(t => new
            {
                Policy = t.SlaPolicy != null ? t.SlaPolicy.Name : null, t.Priority, t.Status, t.CreatedAt, t.ResponseDueAt, t.ResolveDueAt, t.FirstResponseAt, t.ResolvedAt, t.SlaPausedAt, t.SlaPausedMinutes
            }).ToListAsync();
        bool Open(TicketStatus s) => s is TicketStatus.New or TicketStatus.InProgress or TicketStatus.WaitingCustomer;
        var r = new ReportResult()
            .Col(T("Chính sách SLA")).Col(T("Độ ưu tiên")).Col(T("Phiếu tiếp nhận"), K.Number).Col(T("Phản hồi đúng hạn"), K.Percent).Col(T("Giải quyết đúng hạn"), K.Percent)
            .Col(T("Phản hồi TB (giờ)"), K.Number, false).Col(T("Giải quyết TB (giờ)"), K.Number, false).Col(T("Đang mở"), K.Number).Col(T("Đang trễ hạn"), K.Number)
            .Col(T("Tạm dừng TB (giờ)"), K.Number, false);
        foreach (var g in rows.GroupBy(t => (Policy: t.Policy ?? T("Mức chuẩn (24/7)"), t.Priority)).OrderBy(g => g.Key.Policy).ThenByDescending(g => g.Key.Priority))
        {
            var responded = g.Where(t => t.FirstResponseAt != null).ToList();
            var resolved = g.Where(t => t.ResolvedAt != null).ToList();
            var open = g.Where(t => Open(t.Status)).ToList();
            var pausedAvg = g.Where(t => t.SlaPausedMinutes > 0).Select(t => t.SlaPausedMinutes).ToList();
            r.Add(g.Key.Policy, g.Key.Priority.GetDisplayName(), g.Count(),
                Pct(responded.Count(t => t.FirstResponseAt <= t.ResponseDueAt), responded.Count), Pct(resolved.Count(t => t.ResolvedAt <= t.ResolveDueAt), resolved.Count),
                AvgHours(g.Select(t => (t.CreatedAt, t.FirstResponseAt))), AvgHours(g.Select(t => (t.CreatedAt, t.ResolvedAt))),
                open.Count, open.Count(t => t.SlaPausedAt == null && (t.FirstResponseAt == null ? t.ResponseDueAt : t.ResolveDueAt) < now),
                pausedAvg.Count == 0 ? null : Math.Round((decimal)pausedAvg.Average() / 60m, 1));
        }
        r.AutoTotals(T("Tổng cộng"));
        var resAll = rows.Where(t => t.ResolvedAt != null).ToList(); var respAll = rows.Where(t => t.FirstResponseAt != null).ToList();
        r.Totals![3] = Pct(respAll.Count(t => t.FirstResponseAt <= t.ResponseDueAt), respAll.Count);
        r.Totals[4] = Pct(resAll.Count(t => t.ResolvedAt <= t.ResolveDueAt), resAll.Count);
        r.Totals[5] = AvgHours(rows.Select(t => (t.CreatedAt, t.FirstResponseAt)));
        r.Totals[6] = AvgHours(rows.Select(t => (t.CreatedAt, t.ResolvedAt)));
        r.Note = T("Đúng hạn tính trên phiếu đã phản hồi / đã giải quyết. Phiếu chờ khách phản hồi không bị tính trễ trong thời gian đồng hồ SLA tạm dừng.");
        return r;
    }

    /// <summary>Phản hồi NPS của khách trong phạm vi người xem (lọc theo khách hàng thuộc phạm vi đơn vị / người phụ trách)</summary>
    private static IQueryable<NpsResponse> NpsQuery(ReportContext x)
    {
        var owner = x.Owner;
        var customers = x.Db.Customers.Where(c => owner == null || c.OwnerId == owner).Select(c => c.Id);
        return x.Db.NpsResponses.Where(r => r.CustomerId != null && customers.Contains(r.CustomerId.Value));
    }

    private static async Task<ReportResult> NpsBySurvey(ReportContext x)
    {
        var f = x.Filter;
        var rows = await NpsQuery(x).Where(r => r.SentAt != null && r.SentAt >= f.FromDate && r.SentAt < f.ToExclusive)
            .Select(r => new { r.SurveyId, Survey = r.Survey!.Name, r.SentAt, r.Score }).ToListAsync();
        var r = new ReportResult()
            .Col(T("Đợt khảo sát")).Col(T("Ngày gửi"), K.Date).Col(T("Đã gửi"), K.Number).Col(T("Đã trả lời"), K.Number).Col(T("Tỷ lệ trả lời"), K.Percent)
            .Col(T("Ủng hộ (9–10)"), K.Number).Col(T("Trung lập (7–8)"), K.Number).Col(T("Phản đối (0–6)"), K.Number).Col("NPS", K.Number, false);
        decimal? Nps(int pro, int det, int n) => n == 0 ? null : Math.Round((pro - det) * 100m / n, 0);
        foreach (var g in rows.GroupBy(v => v.SurveyId).OrderBy(g => g.Min(v => v.SentAt)))
        {
            var ans = g.Where(v => v.Score != null).ToList();
            int pro = ans.Count(v => v.Score >= 9), det = ans.Count(v => v.Score <= 6);
            r.AddLink($"/Nps/Details/{g.Key}", g.First().Survey, g.Min(v => v.SentAt), g.Count(), ans.Count, Pct(ans.Count, g.Count()), pro, ans.Count(v => v.Score is 7 or 8), det, Nps(pro, det, ans.Count));
        }
        r.AutoTotals(T("Tổng cộng"));
        var allAns = rows.Where(v => v.Score != null).ToList();
        r.Totals![4] = Pct(allAns.Count, rows.Count);
        r.Totals[8] = Nps(allAns.Count(v => v.Score >= 9), allAns.Count(v => v.Score <= 6), allAns.Count);
        r.Note = T("NPS = % khách ủng hộ − % khách phản đối (−100 đến 100). Trên 0 là tốt, trên 50 là rất tốt.");
        return r.Chart(0, 8, ReportChartKind.Line);
    }

    private static async Task<ReportResult> NpsDetractors(ReportContext x)
    {
        var f = x.Filter;
        var list = await NpsQuery(x).Where(r => r.Score != null && r.Score <= 6 && r.RespondedAt >= f.FromDate && r.RespondedAt < f.ToExclusive)
            .OrderBy(r => r.Score).ThenByDescending(r => r.RespondedAt)
            .Select(r => new
            {
                r.CustomerId, Customer = r.Customer!.Name, Contact = r.Contact != null ? r.Contact.FullName : null, r.Score, r.Comment, r.RespondedAt,
                Survey = r.Survey!.Name, Owner = r.Customer.Owner != null ? r.Customer.Owner.FullName : null
            }).ToListAsync();
        var r = new ReportResult()
            .Col(T("Khách hàng")).Col(T("Người liên hệ")).Col(T("Điểm"), K.Number, false).Col(T("Góp ý")).Col(T("Ngày trả lời"), K.Date).Col(T("Đợt khảo sát")).Col(T("Phụ trách"));
        foreach (var v in list) r.AddLink($"/Customers/Details/{v.CustomerId}", v.Customer, v.Contact, v.Score, v.Comment, v.RespondedAt, v.Survey, v.Owner);
        r.Note = T("{0} khách phản đối trong kỳ – liên hệ lại và ghi hoạt động chăm sóc trên trang khách hàng.", list.Count);
        return r;
    }
}
