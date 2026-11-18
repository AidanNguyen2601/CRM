using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using static CRM.Web.Infrastructure.I18n;
using K = CRM.Web.Infrastructure.Reports.ReportColumnKind;

namespace CRM.Web.Infrastructure.Reports;

/// <summary>
/// Danh mục báo cáo chuẩn của CRM. Mọi báo cáo áp quyền xem: nhân viên kinh doanh chỉ thấy dữ liệu mình phụ trách.
/// Dữ liệu được lọc trên SQL rồi tổng hợp trong bộ nhớ (đơn giản, chính xác, đủ nhanh với quy mô CRM).
/// </summary>
public static class StandardReports
{
    public static string GroupSales => T("Bán hàng");
    public static string GroupMarketing => T("Khách hàng & tiếp thị");
    public static string GroupFinance => T("Tài chính & công nợ");
    public static string GroupActivity => T("Hoạt động");

    public static IReadOnlyList<ReportDefinition> All => new ReportDefinition[]
    {
        new("sales-by-rep", GroupSales, T("Doanh số theo nhân viên"), T("Cơ hội thắng, doanh số, giá trị trung bình, tỷ lệ thắng và pipeline đang mở của từng nhân viên trong kỳ."), "users",
            ReportFilters.Period, SalesByRep),
        new("sales-by-month", GroupSales, T("Doanh số theo tháng"), T("Doanh số thắng, hợp đồng ký và tiền thu được theo từng tháng trong năm."), "trending",
            ReportFilters.Year | ReportFilters.Owner, SalesByMonth),
        new("sales-by-customer", GroupSales, T("Top khách hàng theo doanh số"), T("Xếp hạng khách hàng theo doanh số các cơ hội thắng trong kỳ."), "customer",
            ReportFilters.Period | ReportFilters.Owner | ReportFilters.Top, SalesByCustomer),
        new("sales-by-product", GroupSales, T("Doanh số theo hàng hoá"), T("Số lượng và doanh số (trước VAT) từng hàng hoá / dịch vụ trên các hợp đồng ký trong kỳ."), "product",
            ReportFilters.Period | ReportFilters.Owner | ReportFilters.Top, SalesByProduct),
        new("pipeline-by-stage", GroupSales, T("Phễu bán hàng theo giai đoạn"), T("Số cơ hội đang mở, giá trị, giá trị có trọng số và số cơ hội đã quá ngày dự kiến chốt ở từng giai đoạn."), "pipeline",
            ReportFilters.Owner, PipelineByStage),
        new("lost-reasons", GroupSales, T("Phân tích lý do thua"), T("Các lý do mất cơ hội trong kỳ: số lượng, giá trị và tỷ trọng."), "loss",
            ReportFilters.Period | ReportFilters.Owner, LostReasons),

        new("leads-by-source", GroupMarketing, T("Lead theo nguồn và tỷ lệ chuyển đổi"), T("Số lead tạo trong kỳ theo nguồn, số đã chuyển đổi, không phù hợp và tỷ lệ chuyển đổi."), "lead",
            ReportFilters.Period | ReportFilters.Owner, LeadsBySource),
        new("customers-by-industry", GroupMarketing, T("Khách hàng theo ngành"), T("Số khách hàng, khách hàng mới trong kỳ và doanh số thắng trong kỳ theo ngành nghề."), "industry",
            ReportFilters.Period | ReportFilters.Owner, CustomersByIndustry),
        new("campaign-performance", GroupMarketing, T("Hiệu quả chiến dịch tiếp thị"), T("Đối tượng tiếp cận, phản hồi, lead / cơ hội phát sinh, doanh số thắng, chi phí và ROI của các chiến dịch bắt đầu trong kỳ."), "campaign",
            ReportFilters.Period | ReportFilters.Owner, CampaignPerformance),

        new("ar-aging", GroupFinance, T("Tuổi nợ phải thu"), T("Công nợ còn phải thu của từng khách hàng chia theo số ngày quá hạn (tính đến hôm nay)."), "receivable",
            ReportFilters.Owner, ArAging),
        new("collections-by-month", GroupFinance, T("Thu tiền theo tháng và phương thức"), T("Tổng tiền thu được mỗi tháng trong năm, chia theo phương thức thanh toán."), "wallet",
            ReportFilters.Year | ReportFilters.Owner, CollectionsByMonth),
        new("contracts-expiring", GroupFinance, T("Hợp đồng sắp hết hạn"), T("Hợp đồng đang thực hiện sẽ hết hạn trong N ngày tới, kèm tình hình thanh toán."), "contract",
            ReportFilters.Owner | ReportFilters.Days, ContractsExpiring),

        new("activities-by-rep", GroupActivity, T("Hoạt động theo nhân viên"), T("Số cuộc gọi, cuộc họp, email, công việc hoàn thành trong kỳ và số việc đang quá hạn của từng nhân viên."), "calendar",
            ReportFilters.Period, ActivitiesByRep),
    };

    public static ReportDefinition? Find(string key) => All.FirstOrDefault(r => r.Key == key);

    // ================== BÁN HÀNG ==================

    private static async Task<ReportResult> SalesByRep(ReportContext x)
    {
        var f = x.Filter;
        var users = await SalesUsers(x);
        var ids = users.Select(u => u.Id).ToList();
        var opps = await x.Db.Opportunities.Where(o => o.OwnerId != null && ids.Contains(o.OwnerId.Value))
            .Select(o => new { o.OwnerId, o.Stage, o.Amount, o.ClosedAt, o.Probability }).ToListAsync();
        var r = new ReportResult()
            .Col(T("Nhân viên")).Col(T("Cơ hội thắng"), K.Number).Col(T("Doanh số"), K.Money).Col(T("Giá trị TB / cơ hội"), K.Money, false)
            .Col(T("Cơ hội thua"), K.Number).Col(T("Tỷ lệ thắng"), K.Percent).Col(T("Pipeline đang mở"), K.Money).Col(T("Pipeline có trọng số"), K.Money);
        foreach (var u in users)
        {
            var mine = opps.Where(o => o.OwnerId == u.Id).ToList();
            var won = mine.Where(o => o.Stage == OpportunityStage.ClosedWon && o.ClosedAt >= f.FromDate && o.ClosedAt < f.ToExclusive).ToList();
            var lost = mine.Count(o => o.Stage == OpportunityStage.ClosedLost && o.ClosedAt >= f.FromDate && o.ClosedAt < f.ToExclusive);
            var open = mine.Where(o => o.Stage is not (OpportunityStage.ClosedWon or OpportunityStage.ClosedLost)).ToList();
            var revenue = won.Sum(o => o.Amount);
            r.Add(u.FullName, won.Count, revenue, won.Count == 0 ? 0 : Math.Round(revenue / won.Count), lost,
                won.Count + lost == 0 ? 0 : Math.Round(won.Count * 100m / (won.Count + lost), 1),
                open.Sum(o => o.Amount), Math.Round(open.Sum(o => o.Amount * o.Probability / 100m)));
        }
        r.Rows.Sort((a, b) => ((decimal)b[2]!).CompareTo((decimal)a[2]!));
        r.AutoTotals(T("Tổng cộng")).Chart(0, 2);
        var tw = (decimal)r.Totals![1]!; var tl = (decimal)r.Totals[4]!;
        r.Totals[3] = tw == 0 ? 0 : Math.Round((decimal)r.Totals[2]! / tw);
        r.Totals[5] = tw + tl == 0 ? 0 : Math.Round(tw * 100m / (tw + tl), 1);
        return r;
    }

    private static async Task<ReportResult> SalesByMonth(ReportContext x)
    {
        var y = x.Filter.YearValue; var from = new DateTime(y, 1, 1); var to = from.AddYears(1); var owner = x.Owner;
        var won = await x.Db.Opportunities.Where(o => o.Stage == OpportunityStage.ClosedWon && o.ClosedAt >= from && o.ClosedAt < to && (owner == null || o.OwnerId == owner))
            .Select(o => new { o.ClosedAt, o.Amount }).ToListAsync();
        var contracts = await x.Db.Contracts.Where(c => c.SignDate >= from && c.SignDate < to && c.Status != ContractStatus.Cancelled && (owner == null || c.OwnerId == owner))
            .Select(c => new { c.SignDate, c.Total }).ToListAsync();
        var pays = await PaymentsQuery(x, from, to).Select(p => new { p.PaymentDate, p.Amount }).ToListAsync();
        var r = new ReportResult()
            .Col(T("Tháng")).Col(T("Cơ hội thắng"), K.Number).Col(T("Doanh số"), K.Money)
            .Col(T("Hợp đồng ký"), K.Number).Col(T("Giá trị hợp đồng (gồm VAT)"), K.Money).Col(T("Thu tiền"), K.Money);
        for (var m = 1; m <= 12; m++)
        {
            var w = won.Where(o => o.ClosedAt!.Value.Month == m).ToList();
            var c = contracts.Where(o => o.SignDate!.Value.Month == m).ToList();
            r.Add($"{m:00}/{y}", w.Count, w.Sum(o => o.Amount), c.Count, c.Sum(o => o.Total), pays.Where(p => p.PaymentDate.Month == m).Sum(p => p.Amount));
        }
        return r.AutoTotals(T("Cả năm")).Chart(0, 2);
    }

    private static async Task<ReportResult> SalesByCustomer(ReportContext x)
    {
        var f = x.Filter; var owner = x.Owner; var top = Math.Clamp(f.Top ?? 20, 1, 500);
        var won = await x.Db.Opportunities.Where(o => o.Stage == OpportunityStage.ClosedWon && o.ClosedAt >= f.FromDate && o.ClosedAt < f.ToExclusive && (owner == null || o.OwnerId == owner))
            .Select(o => new { o.CustomerId, Name = o.Customer!.Name, Code = o.Customer.Code, o.Customer.Industry, o.Customer.City, o.Amount }).ToListAsync();
        var total = won.Sum(o => o.Amount);
        var r = new ReportResult()
            .Col("#", K.Number, false).Col(T("Mã KH")).Col(T("Khách hàng")).Col(T("Ngành")).Col(T("Tỉnh/TP"))
            .Col(T("Cơ hội thắng"), K.Number).Col(T("Doanh số"), K.Money).Col(T("Tỷ trọng"), K.Percent);
        var rank = 0;
        foreach (var g in won.GroupBy(o => o.CustomerId).OrderByDescending(g => g.Sum(o => o.Amount)).Take(top))
        {
            var a = g.First(); var rev = g.Sum(o => o.Amount);
            r.Add(++rank, a.Code, a.Name, a.Industry, a.City, g.Count(), rev, total == 0 ? 0 : Math.Round(rev * 100m / total, 1));
        }
        r.AutoTotals(T("Tổng (top {0})", top)).Chart(2, 6);
        r.Totals![0] = null; r.Totals[1] = T("Tổng (top {0})", top);
        r.Note = T("Tổng doanh số tất cả khách hàng trong kỳ: {0}", total.ToMoney());
        return r;
    }

    private static async Task<ReportResult> SalesByProduct(ReportContext x)
    {
        var f = x.Filter; var owner = x.Owner; var top = Math.Clamp(f.Top ?? 50, 1, 500);
        var lines = await x.Db.ContractLines
            .Where(l => l.Contract!.SignDate >= f.FromDate && l.Contract.SignDate < f.ToExclusive && l.Contract.Status != ContractStatus.Cancelled
                && (owner == null || l.Contract.OwnerId == owner))
            .Select(l => new { l.ProductId, Code = l.Product != null ? l.Product.Code : "", Name = l.Product != null ? l.Product.Name : l.Description,
                Category = l.Product != null ? l.Product.Category : null, l.Unit, l.Quantity, l.LineTotal, l.ContractId }).ToListAsync();
        var total = lines.Sum(l => l.LineTotal);
        var r = new ReportResult()
            .Col(T("Mã")).Col(T("Hàng hoá / dịch vụ")).Col(T("Nhóm")).Col(T("ĐVT"))
            .Col(T("Số lượng"), K.Number).Col(T("Số hợp đồng"), K.Number, false).Col(T("Doanh số (trước VAT)"), K.Money).Col(T("Tỷ trọng"), K.Percent);
        foreach (var g in lines.GroupBy(l => (object?)l.ProductId ?? l.Name).OrderByDescending(g => g.Sum(l => l.LineTotal)).Take(top))
        {
            var a = g.First(); var rev = g.Sum(l => l.LineTotal);
            r.Add(a.Code, a.Name, a.Category, a.Unit, g.Sum(l => l.Quantity), g.Select(l => l.ContractId).Distinct().Count(), rev,
                total == 0 ? 0 : Math.Round(rev * 100m / total, 1));
        }
        return r.AutoTotals(T("Tổng cộng")).Chart(1, 6);
    }

    private static async Task<ReportResult> PipelineByStage(ReportContext x)
    {
        var owner = x.Owner; var today = DateTime.Today;
        var open = await x.Db.Opportunities.Where(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost && (owner == null || o.OwnerId == owner))
            .Select(o => new { o.Stage, o.Amount, o.Probability, o.ExpectedCloseDate }).ToListAsync();
        var r = new ReportResult()
            .Col(T("Giai đoạn")).Col(T("Số cơ hội"), K.Number).Col(T("Giá trị"), K.Money).Col(T("Giá trị có trọng số"), K.Money)
            .Col(T("Giá trị TB"), K.Money, false).Col(T("Quá ngày dự kiến chốt"), K.Number).Col(T("Tỷ trọng giá trị"), K.Percent);
        var total = open.Sum(o => o.Amount);
        foreach (var s in new[] { OpportunityStage.Prospecting, OpportunityStage.Qualification, OpportunityStage.Proposal, OpportunityStage.Negotiation })
        {
            var g = open.Where(o => o.Stage == s).ToList(); var v = g.Sum(o => o.Amount);
            r.Add(s.GetDisplayName(), g.Count, v, Math.Round(g.Sum(o => o.Amount * o.Probability / 100m)), g.Count == 0 ? 0 : Math.Round(v / g.Count),
                g.Count(o => o.ExpectedCloseDate < today), total == 0 ? 0 : Math.Round(v * 100m / total, 1));
        }
        return r.AutoTotals(T("Tổng pipeline")).Chart(0, 2);
    }

    private static async Task<ReportResult> LostReasons(ReportContext x)
    {
        var f = x.Filter; var owner = x.Owner;
        var lost = await x.Db.Opportunities.Where(o => o.Stage == OpportunityStage.ClosedLost && o.ClosedAt >= f.FromDate && o.ClosedAt < f.ToExclusive && (owner == null || o.OwnerId == owner))
            .Select(o => new { o.LostReason, o.Amount }).ToListAsync();
        var r = new ReportResult().Col(T("Lý do thua")).Col(T("Số cơ hội"), K.Number).Col(T("Giá trị"), K.Money).Col(T("Tỷ lệ theo số lượng"), K.Percent);
        foreach (var g in lost.GroupBy(o => string.IsNullOrWhiteSpace(o.LostReason) ? T("(Không ghi lý do)") : o.LostReason!.Trim()).OrderByDescending(g => g.Count()))
            r.Add(g.Key, g.Count(), g.Sum(o => o.Amount), Math.Round(g.Count() * 100m / lost.Count, 1));
        return r.AutoTotals(T("Tổng cộng")).Chart(0, 1);
    }

    // ================== KHÁCH HÀNG & TIẾP THỊ ==================

    private static async Task<ReportResult> LeadsBySource(ReportContext x)
    {
        var f = x.Filter; var owner = x.Owner;
        var leads = await x.Db.Leads.Where(l => l.CreatedAt >= f.FromDate && l.CreatedAt < f.ToExclusive && (owner == null || l.OwnerId == owner))
            .Select(l => new { l.Source, l.Status, l.EstimatedValue }).ToListAsync();
        var r = new ReportResult()
            .Col(T("Nguồn")).Col(T("Số lead"), K.Number).Col(T("Đang xử lý"), K.Number).Col(T("Đã chuyển đổi"), K.Number)
            .Col(T("Không phù hợp"), K.Number).Col(T("Tỷ lệ chuyển đổi"), K.Percent).Col(T("Giá trị dự kiến"), K.Money);
        foreach (var g in leads.GroupBy(l => l.Source).OrderByDescending(g => g.Count()))
        {
            var conv = g.Count(l => l.Status == LeadStatus.Converted);
            r.Add(g.Key.GetDisplayName(), g.Count(), g.Count(l => l.Status is LeadStatus.New or LeadStatus.Contacted or LeadStatus.Qualified), conv,
                g.Count(l => l.Status == LeadStatus.Unqualified), Math.Round(conv * 100m / g.Count(), 1), g.Sum(l => l.EstimatedValue));
        }
        r.AutoTotals(T("Tổng cộng")).Chart(0, 1);
        var tl = (decimal)r.Totals![1]!; r.Totals[5] = tl == 0 ? 0 : Math.Round((decimal)r.Totals[3]! * 100m / tl, 1);
        return r;
    }

    private static async Task<ReportResult> CustomersByIndustry(ReportContext x)
    {
        var f = x.Filter; var owner = x.Owner;
        var customers = await x.Db.Customers.Where(c => owner == null || c.OwnerId == owner).Select(c => new { c.Id, c.Industry, c.Type, c.CreatedAt }).ToListAsync();
        var won = await x.Db.Opportunities.Where(o => o.Stage == OpportunityStage.ClosedWon && o.ClosedAt >= f.FromDate && o.ClosedAt < f.ToExclusive && (owner == null || o.OwnerId == owner))
            .Select(o => new { o.CustomerId, o.Amount }).ToListAsync();
        var industryOf = customers.ToDictionary(c => c.Id, c => c.Industry);
        var r = new ReportResult()
            .Col(T("Ngành nghề")).Col(T("Số khách hàng"), K.Number).Col(T("Đang là khách hàng"), K.Number).Col(T("Khách hàng mới trong kỳ"), K.Number)
            .Col(T("Doanh số thắng trong kỳ"), K.Money).Col(T("Tỷ trọng doanh số"), K.Percent);
        var total = won.Sum(o => o.Amount);
        foreach (var g in customers.GroupBy(c => string.IsNullOrWhiteSpace(c.Industry) ? T("(Chưa phân loại)") : c.Industry!).OrderBy(g => g.Key))
        {
            var ids = g.Select(c => c.Id).ToHashSet();
            var rev = won.Where(o => o.CustomerId.HasValue && ids.Contains(o.CustomerId.Value)).Sum(o => o.Amount);
            r.Add(g.Key, g.Count(), g.Count(c => c.Type == CustomerType.Customer), g.Count(c => c.CreatedAt >= f.FromDate && c.CreatedAt < f.ToExclusive),
                rev, total == 0 ? 0 : Math.Round(rev * 100m / total, 1));
        }
        r.Rows.Sort((a, b) => ((decimal)b[4]!).CompareTo((decimal)a[4]!));
        return r.AutoTotals(T("Tổng cộng")).Chart(0, 4);
    }

    private static async Task<ReportResult> CampaignPerformance(ReportContext x)
    {
        var f = x.Filter; var owner = x.Owner;
        var campaigns = await x.Db.Campaigns.Where(c => c.StartDate >= f.FromDate && c.StartDate < f.ToExclusive && c.Status != CampaignStatus.Cancelled && (owner == null || c.OwnerId == owner))
            .OrderBy(c => c.StartDate).Select(c => new { c.Id, c.Code, c.Name, c.Channel, c.ActualCost, c.Budget }).ToListAsync();
        var ids = campaigns.Select(c => c.Id).ToList();
        var members = await x.Db.CampaignMembers.Where(m => ids.Contains(m.CampaignId)).Select(m => new { m.CampaignId, m.Status }).ToListAsync();
        var leads = await x.Db.Leads.Where(l => l.CampaignId != null && ids.Contains(l.CampaignId.Value)).Select(l => new { l.CampaignId }).ToListAsync();
        var opps = await x.Db.Opportunities.Where(o => o.CampaignId != null && ids.Contains(o.CampaignId.Value)).Select(o => new { o.CampaignId, o.Stage, o.Amount }).ToListAsync();
        var r = new ReportResult()
            .Col(T("Mã")).Col(T("Chiến dịch")).Col(T("Kênh")).Col(T("Đối tượng"), K.Number).Col(T("Đã tiếp cận"), K.Number)
            .Col(T("Phản hồi / chuyển đổi"), K.Number).Col("Lead", K.Number).Col(T("Cơ hội"), K.Number).Col(T("Doanh số thắng"), K.Money)
            .Col(T("Chi phí"), K.Money).Col("ROI", K.Percent);
        foreach (var c in campaigns)
        {
            var ms = members.Where(m => m.CampaignId == c.Id).ToList();
            var os = opps.Where(o => o.CampaignId == c.Id).ToList();
            var rev = os.Where(o => o.Stage == OpportunityStage.ClosedWon).Sum(o => o.Amount);
            var cost = c.ActualCost > 0 ? c.ActualCost : c.Budget;
            r.Add(c.Code, c.Name, c.Channel.GetDisplayName(), ms.Count,
                ms.Count(m => m.Status is not (CampaignMemberStatus.Pending or CampaignMemberStatus.Failed)),
                ms.Count(m => m.Status is CampaignMemberStatus.Responded or CampaignMemberStatus.Converted),
                leads.Count(l => l.CampaignId == c.Id), os.Count, rev, cost, cost == 0 ? null : Math.Round((rev - cost) * 100m / cost, 1));
        }
        r.AutoTotals(T("Tổng cộng")).Chart(1, 8);
        var tc = (decimal)r.Totals![9]!; r.Totals[10] = tc == 0 ? null : Math.Round(((decimal)r.Totals[8]! - tc) * 100m / tc, 1);
        r.Note = T("ROI = (doanh số thắng − chi phí) / chi phí. Chi phí lấy chi phí thực tế, chưa có thì lấy ngân sách.");
        return r;
    }

    // ================== TÀI CHÍNH & CÔNG NỢ ==================

    private static async Task<ReportResult> ArAging(ReportContext x)
    {
        var owner = x.Owner; var today = DateTime.Today;
        var inv = await x.Db.Invoices.Where(i => (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid) && (owner == null || i.OwnerId == owner))
            .Select(i => new { i.CustomerId, Name = i.Customer!.Name, Code = i.Customer.Code, Debt = i.Total - i.PaidAmount, i.DueDate }).ToListAsync();
        var r = new ReportResult()
            .Col(T("Mã KH")).Col(T("Khách hàng")).Col(T("Số hoá đơn"), K.Number).Col(T("Tổng còn nợ"), K.Money).Col(T("Chưa đến hạn"), K.Money)
            .Col(T("Quá hạn 1–30 ngày"), K.Money).Col(T("31–60 ngày"), K.Money).Col(T("61–90 ngày"), K.Money).Col(T("Trên 90 ngày"), K.Money);
        foreach (var g in inv.Where(i => i.Debt > 0).GroupBy(i => i.CustomerId).OrderByDescending(g => g.Sum(i => i.Debt)))
        {
            decimal Bucket(int min, int max) => g.Where(i => { var d = i.DueDate.HasValue ? (today - i.DueDate.Value.Date).Days : 0; return d >= min && d <= max; }).Sum(i => i.Debt);
            var a = g.First();
            r.Add(a.Code, a.Name, g.Count(), g.Sum(i => i.Debt), Bucket(int.MinValue, 0), Bucket(1, 30), Bucket(31, 60), Bucket(61, 90), Bucket(91, int.MaxValue));
        }
        return r.AutoTotals(T("Tổng cộng")).Chart(1, 3);
    }

    private static async Task<ReportResult> CollectionsByMonth(ReportContext x)
    {
        var y = x.Filter.YearValue; var from = new DateTime(y, 1, 1);
        var pays = await PaymentsQuery(x, from, from.AddYears(1)).Select(p => new { p.PaymentDate, p.Method, p.Amount }).ToListAsync();
        var methods = pays.GroupBy(p => p.Method).OrderByDescending(g => g.Sum(p => p.Amount)).Select(g => g.Key).ToList();
        var r = new ReportResult().Col(T("Tháng"));
        foreach (var m in methods) r.Col(m, K.Money);
        r.Col(T("Số phiếu thu"), K.Number).Col(T("Tổng thu"), K.Money);
        for (var mo = 1; mo <= 12; mo++)
        {
            var row = new List<object?> { $"{mo:00}/{y}" };
            var inMonth = pays.Where(p => p.PaymentDate.Month == mo).ToList();
            row.AddRange(methods.Select(m => (object?)inMonth.Where(p => p.Method == m).Sum(p => p.Amount)));
            row.Add(inMonth.Count); row.Add(inMonth.Sum(p => p.Amount));
            r.Rows.Add(row.ToArray());
        }
        return r.AutoTotals(T("Cả năm")).Chart(0, r.Columns.Count - 1);
    }

    private static async Task<ReportResult> ContractsExpiring(ReportContext x)
    {
        var owner = x.Owner; var days = Math.Clamp(x.Filter.Days ?? 90, 1, 3650); var today = DateTime.Today; var until = today.AddDays(days);
        var list = await x.Db.Contracts.Where(c => c.Status == ContractStatus.Active && c.EndDate != null && c.EndDate <= until && (owner == null || c.OwnerId == owner))
            .OrderBy(c => c.EndDate)
            .Select(c => new { c.Id, c.Number, c.Title, Customer = c.Customer!.Name, c.EndDate, c.Total, Owner = c.Owner!.FullName }).ToListAsync();
        var ids = list.Select(c => c.Id).ToList();
        var paid = (await x.Db.Payments.Where(p => p.ContractId != null && ids.Contains(p.ContractId.Value)).Select(p => new { p.ContractId, p.Amount }).ToListAsync())
            .GroupBy(p => p.ContractId!.Value).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
        var r = new ReportResult()
            .Col(T("Số hợp đồng")).Col(T("Tên hợp đồng")).Col(T("Khách hàng")).Col(T("Ngày kết thúc"), K.Date).Col(T("Còn lại (ngày)"), K.Number, false)
            .Col(T("Giá trị"), K.Money).Col(T("Đã thu"), K.Money).Col(T("Còn phải thu"), K.Money).Col(T("Phụ trách"));
        foreach (var c in list)
        {
            var p = paid.GetValueOrDefault(c.Id);
            r.Add(c.Number, c.Title, c.Customer, c.EndDate, (c.EndDate!.Value.Date - today).Days, c.Total, p, c.Total - p, c.Owner);
        }
        r.Note = T("Bao gồm cả hợp đồng đang thực hiện đã quá ngày kết thúc (số ngày âm).");
        return r.AutoTotals(T("Tổng cộng"));
    }

    // ================== HOẠT ĐỘNG ==================

    private static async Task<ReportResult> ActivitiesByRep(ReportContext x)
    {
        var f = x.Filter; var users = await SalesUsers(x); var ids = users.Select(u => u.Id).ToList(); var now = DateTime.Now;
        var done = await x.Db.Activities.Where(a => a.Status == ActivityStatus.Completed && a.CompletedAt >= f.FromDate && a.CompletedAt < f.ToExclusive
                && a.OwnerId != null && ids.Contains(a.OwnerId.Value))
            .Select(a => new { a.OwnerId, a.Type }).ToListAsync();
        var overdue = (await x.Db.Activities.Where(a => a.Status == ActivityStatus.Planned && a.DueDate < now && a.OwnerId != null && ids.Contains(a.OwnerId.Value))
            .Select(a => a.OwnerId!.Value).ToListAsync()).GroupBy(i => i).ToDictionary(g => g.Key, g => g.Count());
        var r = new ReportResult().Col(T("Nhân viên"));
        var types = Enum.GetValues<ActivityType>();
        foreach (var t in types) r.Col(t.GetDisplayName(), K.Number);
        r.Col(T("Tổng hoàn thành"), K.Number).Col(T("Đang quá hạn"), K.Number);
        foreach (var u in users)
        {
            var mine = done.Where(a => a.OwnerId == u.Id).ToList();
            var row = new List<object?> { u.FullName };
            row.AddRange(types.Select(t => (object?)mine.Count(a => a.Type == t)));
            row.Add(mine.Count); row.Add(overdue.GetValueOrDefault(u.Id));
            r.Rows.Add(row.ToArray());
        }
        r.Rows.Sort((a, b) => ((int)b[^2]!).CompareTo((int)a[^2]!));
        return r.AutoTotals(T("Tổng cộng")).Chart(0, r.Columns.Count - 2);
    }

    // ================== TIỆN ÍCH ==================

    /// <summary>Nhân viên hiển thị trong báo cáo theo người: quản lý thấy cả đội (hoặc 1 người theo bộ lọc), nhân viên chỉ thấy mình</summary>
    private static async Task<List<AppUser>> SalesUsers(ReportContext x)
    {
        var owner = x.Owner;
        return await x.Db.Users.Where(u => (owner == null ? (u.IsActive && (u.Role == AppRoles.Sales || u.Role == AppRoles.Manager)) : u.Id == owner))
            .OrderBy(u => u.FullName).ToListAsync();
    }

    /// <summary>Phiếu thu trong khoảng thời gian, gán cho người phụ trách hợp đồng / hoá đơn (giống KPI)</summary>
    private static IQueryable<Payment> PaymentsQuery(ReportContext x, DateTime from, DateTime to)
    {
        var owner = x.Owner;
        var q = x.Db.Payments.Where(p => p.PaymentDate >= from && p.PaymentDate < to);
        if (owner != null)
            q = q.Where(p => (p.Contract != null ? p.Contract.OwnerId : p.Invoice != null ? p.Invoice.OwnerId : p.OwnerId) == owner);
        return q;
    }
}
