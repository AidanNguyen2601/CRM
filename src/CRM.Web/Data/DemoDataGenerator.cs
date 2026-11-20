using System.Globalization;
using System.Text;
using CRM.Web.Models.Entities;
using CRM.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Data;

public record DemoDataResult(int Products, int Customers, int Contacts, int Leads, int Opportunities, int Activities,
    int Quotes, int BusinessPlans, int Contracts, int Invoices, int Payments, int Campaigns = 0, int Tickets = 0);

/// <summary>
/// Sinh thêm dữ liệu mẫu nhất quán theo cả chuỗi nghiệp vụ, trải trong 12 tháng gần nhất:
/// khách hàng → liên hệ → cơ hội → hoạt động → báo giá → PAKD → hợp đồng → hoá đơn → thanh toán, cùng lead.
/// Mỗi lần chạy là NẠP THÊM (không xóa dữ liệu cũ). Số chứng từ nối tiếp số đã có. Chạy trong một transaction.
/// </summary>
public static class DemoDataGenerator
{
    public static async Task<DemoDataResult> GenerateAsync(CrmDbContext db, int customerCount, int? seed = null)
    {
        customerCount = Math.Clamp(customerCount, 1, 1000);
        var g = new Gen(db, new Random(seed ?? Environment.TickCount));
        await using var tx = await db.Database.BeginTransactionAsync();
        var result = await g.RunAsync(customerCount);
        await tx.CommitAsync();
        return result;
    }

    /// <summary>Nạp thêm chiến dịch tiếp thị mẫu dựa trên dữ liệu khách hàng / lead đang có</summary>
    public static async Task<(int Campaigns, int Members, int Leads, int Opportunities)> GenerateMarketingAsync(CrmDbContext db, int campaignCount, int? seed = null)
    {
        campaignCount = Math.Clamp(campaignCount, 1, 50);
        var g = new Gen(db, new Random(seed ?? Environment.TickCount));
        await using var tx = await db.Database.BeginTransactionAsync();
        var result = await g.RunMarketingAsync(campaignCount);
        await tx.CommitAsync();
        return result;
    }

    /// <summary>Chỉ nạp phiếu hỗ trợ mẫu trên khách hàng / hợp đồng đang có</summary>
    public static async Task<(int Tickets, int Open, int Closed, int Comments)> GenerateSupportAsync(CrmDbContext db, int ticketCount, int? seed = null)
    {
        ticketCount = Math.Clamp(ticketCount, 1, 1000);
        var g = new Gen(db, new Random(seed ?? Environment.TickCount));
        await using var tx = await db.Database.BeginTransactionAsync();
        var result = await g.RunSupportAsync(ticketCount);
        await tx.CommitAsync();
        return result;
    }

    private sealed class Gen
    {
        private readonly CrmDbContext _db;
        private readonly Random _r;
        private readonly DateTime _today = DateTime.Today;

        private List<AppUser> _owners = new();
        private List<AppUser> _approvers = new();
        private List<Product> _products = new();
        private List<string> _industries = new(), _cities = new(), _lossReasons = new(), _costTypes = new(), _payMethods = new();
        private readonly HashSet<string> _usedNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _numbers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _invoiceSeq = new();

        public Gen(CrmDbContext db, Random r) { _db = db; _r = r; }

        public async Task<DemoDataResult> RunAsync(int n)
        {
            await LoadContextAsync();
            var newProducts = await EnsureProductsAsync();

            // ---------- Khách hàng ----------
            var codeMax = (await _db.Customers.Select(c => c.Code).ToListAsync())
                .Select(c => int.TryParse(new string(c.Where(char.IsDigit).ToArray()), out var v) ? v : 0).DefaultIfEmpty(0).Max();
            var customers = new List<Customer>();
            for (var i = 0; i < n; i++)
            {
                var industry = Pick(_industries);
                var (name, core) = CompanyName(industry);
                var created = _today.AddDays(-_r.Next(30, 380));
                customers.Add(new Customer
                {
                    Code = $"KH{++codeMax:0000}", Name = name, Industry = industry, City = Pick(_cities), Type = CustomerType.Prospect,
                    TaxCode = "03" + _r.Next(10000000, 99999999), Phone = $"028 3{_r.Next(1000000, 9999999)}",
                    Email = $"info@{Slug(core)}.vn", Website = $"www.{Slug(core)}.vn", EmployeeCount = _r.Next(20, 3000),
                    Address = $"{_r.Next(1, 450)} {Pick(Streets)}", OwnerId = Pick(_owners).Id, CreatedAt = created
                });
            }
            _db.Customers.AddRange(customers);
            await _db.SaveChangesAsync();

            // ---------- Người liên hệ ----------
            var contacts = new List<Contact>();
            foreach (var c in customers)
            {
                var k = _r.Next(1, 4);
                for (var j = 0; j < k; j++)
                {
                    var (full, ascii) = PersonName();
                    contacts.Add(new Contact
                    {
                        FullName = full, JobTitle = Pick(JobTitles), CustomerId = c.Id, IsPrimary = j == 0, OwnerId = c.OwnerId,
                        Phone = "09" + _r.Next(10000000, 99999999), Email = $"{ascii}@{Slug(CoreOf(c))}.vn",
                        BirthDate = new DateTime(_r.Next(1970, 1996), _r.Next(1, 13), _r.Next(1, 28)), CreatedAt = c.CreatedAt.AddDays(_r.Next(0, 10))
                    });
                }
            }
            _db.Contacts.AddRange(contacts);
            await _db.SaveChangesAsync();
            var contactsByCustomer = contacts.GroupBy(x => x.CustomerId!.Value).ToDictionary(x => x.Key, x => x.ToList());

            // ---------- Cơ hội ----------
            var opps = new List<Opportunity>();
            foreach (var c in customers)
            {
                var k = _r.Next(1, 4);
                for (var j = 0; j < k; j++)
                {
                    var created = c.CreatedAt.AddDays(_r.Next(0, 60));
                    if (created > _today) created = _today.AddDays(-_r.Next(0, 10));
                    var age = (_today - created).Days;
                    var stage = age > 60
                        ? Weighted((OpportunityStage.ClosedWon, 55), (OpportunityStage.ClosedLost, 25), (OpportunityStage.Negotiation, 8), (OpportunityStage.Proposal, 7), (OpportunityStage.Qualification, 5))
                        : Weighted((OpportunityStage.Prospecting, 22), (OpportunityStage.Qualification, 22), (OpportunityStage.Proposal, 20), (OpportunityStage.Negotiation, 14), (OpportunityStage.ClosedWon, 14), (OpportunityStage.ClosedLost, 8));
                    var o = new Opportunity
                    {
                        Name = $"{CoreOf(c)} – {Pick(Needs)}", CustomerId = c.Id, ContactId = Pick(contactsByCustomer[c.Id]).Id,
                        Stage = stage, Probability = (int)stage, Amount = RoundTo(_r.Next(8, 300) * 5_000_000m, 5_000_000),
                        Source = Pick(Enum.GetValues<LeadSource>()), OwnerId = c.OwnerId, CreatedAt = created,
                        Description = Pick(OppDescriptions)
                    };
                    if (o.IsClosed)
                    {
                        var closed = created.AddDays(_r.Next(15, 90));
                        o.ClosedAt = closed > _today ? _today.AddDays(-_r.Next(0, 5)) : closed;
                        o.ExpectedCloseDate = o.ClosedAt.Value.Date;
                        if (stage == OpportunityStage.ClosedLost) o.LostReason = Pick(_lossReasons);
                    }
                    else o.ExpectedCloseDate = _today.AddDays(_r.Next(-12, 100));
                    opps.Add(o);
                }
            }
            _db.Opportunities.AddRange(opps);
            await _db.SaveChangesAsync();

            // Phân loại khách hàng theo kết quả cơ hội
            foreach (var c in customers)
            {
                var mine = opps.Where(o => o.CustomerId == c.Id).ToList();
                c.Type = mine.Any(o => o.Stage == OpportunityStage.ClosedWon) ? CustomerType.Customer
                    : _r.Next(100) < 6 ? CustomerType.Partner
                    : mine.All(o => o.Stage == OpportunityStage.ClosedLost) && _r.Next(100) < 40 ? CustomerType.Inactive
                    : CustomerType.Prospect;
            }

            // ---------- Báo giá ----------
            var quotes = new Dictionary<Opportunity, Quote>();
            foreach (var o in opps.Where(o => o.Stage >= OpportunityStage.Proposal || o.Stage == OpportunityStage.ClosedLost && _r.Next(100) < 60))
            {
                var limit = o.ClosedAt ?? _today;
                var qDate = Min(o.CreatedAt.Date.AddDays(_r.Next(5, 25)), limit.Date);
                var q = new Quote
                {
                    Number = NextNumber("BG", qDate), CustomerId = o.CustomerId, ContactId = o.ContactId, OpportunityId = o.Id, OwnerId = o.OwnerId,
                    QuoteDate = qDate, ValidUntil = qDate.AddDays(30), DiscountPercent = Pick(new[] { 0m, 0m, 0m, 3m, 5m, 10m }), VatPercent = 10,
                    Terms = "Thanh toán 50% khi ký hợp đồng, 50% sau nghiệm thu. Báo giá có hiệu lực 30 ngày.", CreatedAt = qDate,
                    Status = o.Stage switch
                    {
                        OpportunityStage.ClosedWon => QuoteStatus.Accepted,
                        OpportunityStage.ClosedLost => QuoteStatus.Rejected,
                        OpportunityStage.Proposal when _r.Next(100) < 25 => QuoteStatus.Draft,
                        _ => QuoteStatus.Sent
                    }
                };
                var sort = 1;
                foreach (var p in PickProducts())
                    q.Lines.Add(new QuoteLine { ProductId = p.Id, Description = p.Name, Unit = p.Unit, Quantity = Qty(p), UnitPrice = p.UnitPrice,
                        DiscountPercent = _r.Next(100) < 15 ? 5 : 0, SortOrder = sort++ });
                q.Recalculate();
                o.Amount = q.SubTotal - q.DiscountAmount;   // giá trị cơ hội = doanh thu trước thuế của báo giá
                quotes[o] = q;
                _db.Quotes.Add(q);
            }
            await _db.SaveChangesAsync();

            // ---------- Phương án kinh doanh ----------
            var plans = new Dictionary<Opportunity, BusinessPlan>();
            foreach (var (o, q) in quotes)
            {
                var want = o.Stage switch { OpportunityStage.ClosedWon => 75, OpportunityStage.Negotiation => 70, OpportunityStage.Proposal => 30, _ => 0 };
                if (_r.Next(100) >= want) continue;
                var factor = 1 - q.DiscountPercent / 100m;
                var plan = new BusinessPlan
                {
                    Number = NextNumber("PAKD", q.QuoteDate), Title = $"PAKD – {o.Name}", CustomerId = o.CustomerId, OpportunityId = o.Id, QuoteId = q.Id,
                    PlanDate = q.QuoteDate.AddDays(1), OwnerId = o.OwnerId, CreatedAt = q.QuoteDate.AddDays(1),
                    Notes = "Giá vốn theo báo giá nhà cung cấp tháng gần nhất. Thanh toán theo tiến độ hợp đồng."
                };
                var sort = 1;
                foreach (var l in q.Lines.OrderBy(x => x.SortOrder))
                {
                    var p = _products.First(x => x.Id == l.ProductId);
                    var sale = Math.Round(l.UnitPrice * (1 - l.DiscountPercent / 100m) * factor, 0);
                    var baseCost = p.CostPrice > 0 ? p.CostPrice : p.UnitPrice * 0.6m;
                    plan.Lines.Add(new BusinessPlanLine { ProductId = p.Id, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity,
                        SalePrice = sale, CostPrice = RoundTo(baseCost * (decimal)(0.9 + _r.NextDouble() * 0.2), 1000), SortOrder = sort++ });
                }
                plan.Recalculate();
                sort = 1;
                foreach (var ct in _costTypes.OrderBy(_ => _r.Next()).Take(_r.Next(1, 4)))
                    plan.Costs.Add(new BusinessPlanCost { Category = ct, Amount = RoundTo(plan.Revenue * (decimal)(0.01 + _r.NextDouble() * 0.06), 100_000), SortOrder = sort++ });
                plan.Recalculate();

                var approver = Pick(_approvers);
                if (o.Stage == OpportunityStage.ClosedWon || _r.Next(100) < 50)
                {
                    plan.Status = BusinessPlanStatus.Approved;
                    plan.SubmittedAt = plan.PlanDate.AddHours(10);
                    plan.ApprovedById = approver.Id; plan.ApprovedAt = plan.PlanDate.AddDays(1).AddHours(15);
                    plan.ApprovalNote = plan.MarginPercent < BusinessPlan.LowMargin ? "Đồng ý, lưu ý kiểm soát chi phí triển khai." : "Đồng ý.";
                }
                else if (_r.Next(100) < 50)
                {
                    plan.Status = BusinessPlanStatus.Pending; plan.SubmittedAt = plan.PlanDate.AddHours(10);
                }
                else if (_r.Next(100) < 40)
                {
                    plan.Status = BusinessPlanStatus.Rejected; plan.SubmittedAt = plan.PlanDate.AddHours(10);
                    plan.ApprovedById = approver.Id; plan.ApprovedAt = plan.PlanDate.AddDays(1);
                    plan.ApprovalNote = "Tỷ suất lợi nhuận thấp, đề nghị đàm phán lại giá vốn với nhà cung cấp.";
                }
                plans[o] = plan;
                _db.BusinessPlans.Add(plan);
            }
            await _db.SaveChangesAsync();

            // ---------- Hợp đồng ----------
            var contracts = new List<Contract>();
            foreach (var (o, q) in quotes.Where(x => x.Key.Stage == OpportunityStage.ClosedWon))
            {
                var sign = o.ClosedAt!.Value.Date;
                var factor = 1 - q.DiscountPercent / 100m;
                var c = new Contract
                {
                    Number = NextNumber("HD", sign), Title = o.Name, CustomerId = o.CustomerId, ContactId = o.ContactId, OpportunityId = o.Id, QuoteId = q.Id,
                    BusinessPlanId = plans.TryGetValue(o, out var bp) && bp.Status == BusinessPlanStatus.Approved ? bp.Id : null,
                    SignDate = sign, StartDate = sign, EndDate = sign.AddMonths(12), Status = ContractStatus.Active, VatPercent = q.VatPercent,
                    PaymentTerms = "Thanh toán bằng chuyển khoản theo các đợt nêu trên, trong vòng 15 ngày kể từ ngày nhận hoá đơn hợp lệ.",
                    OwnerId = o.OwnerId, CreatedAt = sign.AddDays(-2)
                };
                var sort = 1;
                foreach (var l in q.Lines.OrderBy(x => x.SortOrder))
                    c.Lines.Add(new ContractLine { ProductId = l.ProductId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity,
                        UnitPrice = Math.Round(l.UnitPrice * (1 - l.DiscountPercent / 100m) * factor, 0), SortOrder = sort++ });
                var plan = Pick(MilestonePlans);
                var due = sign.AddDays(7);
                for (var i = 0; i < plan.Length; i++)
                {
                    c.Milestones.Add(new ContractMilestone
                    {
                        Name = plan.Length == 1 ? "Thanh toán một lần" : i == 0 ? $"Đợt {i + 1} – Tạm ứng" : i == plan.Length - 1 ? $"Đợt {i + 1} – Thanh toán còn lại" : $"Đợt {i + 1} – Theo tiến độ",
                        Percent = plan[i], DueDate = due, SortOrder = i + 1,
                        Condition = i == 0 ? "Trong 7 ngày kể từ ngày ký hợp đồng" : i == plan.Length - 1 ? "Sau khi nghiệm thu, bàn giao" : "Sau khi hoàn thành giai đoạn triển khai"
                    });
                    due = due.AddDays(_r.Next(30, 100));
                }
                c.Recalculate();
                contracts.Add(c);
                _db.Contracts.Add(c);
            }
            await _db.SaveChangesAsync();

            // ---------- Hoá đơn + thanh toán ----------
            var invoices = new List<Invoice>();
            var payments = new List<Payment>();
            foreach (var c in contracts)
            {
                foreach (var m in c.Milestones.OrderBy(x => x.SortOrder))
                {
                    if (m.DueDate == null || m.DueDate.Value.AddDays(-7) > _today) continue;   // chưa tới đợt
                    var invDate = Min(Max(m.DueDate.Value.AddDays(-_r.Next(0, 8)), c.SignDate!.Value), _today);   // không xuất hoá đơn ngày tương lai
                    var inv = new Invoice
                    {
                        Symbol = $"1C{invDate:yy}TCR", InvoiceDate = invDate, DueDate = invDate.AddDays(Pick(new[] { 15, 15, 30 })),
                        CustomerId = c.CustomerId, ContractId = c.Id, MilestoneId = m.Id, VatPercent = c.VatPercent, OwnerId = c.OwnerId,
                        Status = InvoiceStatus.Issued, IssuedAt = invDate.AddHours(16), CreatedAt = invDate, Notes = m.Condition
                    };
                    inv.Number = NextInvoiceNumber(inv.Symbol);
                    inv.Lines.Add(new InvoiceLine { Description = $"{m.Name} theo hợp đồng số {c.Number}", Quantity = 1,
                        UnitPrice = Math.Round(c.SubTotal * m.Percent / 100m, 0), SortOrder = 1 });
                    inv.Recalculate();

                    // Khả năng đã thu tiền: hoá đơn càng cũ càng nhiều khả năng đã thu đủ
                    var overdueDays = (_today - inv.DueDate!.Value).Days;
                    var roll = _r.Next(100);
                    var paid = overdueDays > 30 ? (roll < 85 ? 1m : roll < 95 ? 0.5m : 0m)
                             : overdueDays > 0 ? (roll < 55 ? 1m : roll < 75 ? 0.5m : 0m)
                             : (roll < 35 ? 1m : roll < 50 ? 0.4m : 0m);
                    var collected = 0m;
                    if (paid > 0)
                    {
                        var parts = paid >= 1 && _r.Next(100) < 20 ? 2 : 1;   // đôi khi thu làm 2 lần
                        var target = paid >= 1 ? inv.Total : RoundTo(inv.Total * paid, 100_000);
                        for (var p = 0; p < parts; p++)
                        {
                            var amount = p == parts - 1 ? target - collected : RoundTo(target * 0.5m, 100_000);
                            var payDate = Min(inv.InvoiceDate.AddDays(_r.Next(3, 45) + p * 10), _today);
                            payments.Add(new Payment
                            {
                                Number = NextNumber("PT", payDate), PaymentDate = payDate, CustomerId = c.CustomerId, ContractId = c.Id, Invoice = inv,
                                Amount = amount, Method = Weighted((_payMethods.ElementAtOrDefault(0) ?? "Chuyển khoản", 85), (Pick(_payMethods), 15)),
                                Reference = "UNC" + _r.Next(100000, 999999), Notes = $"Thanh toán hoá đơn {inv.Symbol} – {inv.Number}",
                                OwnerId = c.OwnerId, CreatedAt = payDate
                            });
                            collected += amount;
                        }
                    }
                    inv.ApplyPayments(collected);
                    invoices.Add(inv);
                    _db.Invoices.Add(inv);
                }
                // Hợp đồng đã xuất & thu đủ mọi đợt → Hoàn thành; ký đã lâu → Thanh lý
                var allBilled = c.Milestones.All(m => invoices.Any(i => i.MilestoneId == m.Id && i.Status == InvoiceStatus.Paid));
                if (allBilled) c.Status = (_today - c.SignDate!.Value).Days > 240 ? ContractStatus.Liquidated : ContractStatus.Completed;
            }
            _db.Payments.AddRange(payments);
            await _db.SaveChangesAsync();

            // ---------- Lead ----------
            var leads = new List<Lead>();
            for (var i = 0; i < (int)Math.Ceiling(n * 1.2); i++)
            {
                var (full, ascii) = PersonName();
                var (company, core) = CompanyName(Pick(_industries));
                var status = Weighted((LeadStatus.New, 30), (LeadStatus.Contacted, 30), (LeadStatus.Qualified, 20), (LeadStatus.Unqualified, 20));
                leads.Add(new Lead
                {
                    FullName = full, Company = company, JobTitle = Pick(JobTitles), Phone = "09" + _r.Next(10000000, 99999999),
                    Email = $"{ascii}@{Slug(core)}.vn", Source = Pick(Enum.GetValues<LeadSource>()), Status = status,
                    Rating = Weighted((LeadRating.Hot, 25), (LeadRating.Warm, 45), (LeadRating.Cold, 30)),
                    EstimatedValue = status == LeadStatus.Unqualified ? 0 : RoundTo(_r.Next(6, 120) * 5_000_000m, 5_000_000),
                    OwnerId = Pick(_owners).Id, Description = Pick(LeadNeeds), CreatedAt = _today.AddDays(-_r.Next(0, 150)).AddHours(_r.Next(8, 18))
                });
            }
            // Một phần khách hàng mới đến từ lead đã chuyển đổi (để có tỷ lệ chuyển đổi lead)
            foreach (var c in customers.Where(_ => _r.Next(100) < 15))
            {
                var o = opps.Where(x => x.CustomerId == c.Id).OrderBy(x => x.CreatedAt).FirstOrDefault();
                var primary = contactsByCustomer[c.Id][0];
                leads.Add(new Lead
                {
                    FullName = primary.FullName, Company = c.Name, JobTitle = primary.JobTitle, Phone = primary.Phone, Email = primary.Email,
                    Source = o?.Source ?? Pick(Enum.GetValues<LeadSource>()), Status = LeadStatus.Converted, Rating = LeadRating.Hot,
                    EstimatedValue = o?.Amount ?? 0, OwnerId = c.OwnerId, Description = Pick(LeadNeeds),
                    ConvertedCustomerId = c.Id, ConvertedOpportunityId = o?.Id, ConvertedAt = o?.CreatedAt ?? c.CreatedAt,
                    CreatedAt = (o?.CreatedAt ?? c.CreatedAt).AddDays(-_r.Next(5, 30))
                });
            }
            _db.Leads.AddRange(leads);
            await _db.SaveChangesAsync();

            // ---------- Hoạt động ----------
            var acts = new List<Activity>();
            foreach (var o in opps)
            {
                var end = o.ClosedAt ?? _today.AddDays(30);
                for (var j = _r.Next(2, 6); j > 0; j--)
                    acts.Add(MakeActivity(o.CreatedAt, end, o.OwnerId, o.CustomerId, o.ContactId, null, o.Id));
            }
            foreach (var l in leads.Where(l => l.Status is LeadStatus.Contacted or LeadStatus.Qualified))
                for (var j = _r.Next(1, 3); j > 0; j--)
                    acts.Add(MakeActivity(l.CreatedAt, _today.AddDays(14), l.OwnerId, null, null, l.Id, null));
            // Quản lý công việc: một phần việc do quản lý giao, việc đang chờ có thể đã bắt đầu làm với tiến độ
            foreach (var a in acts)
            {
                if (a.Type == ActivityType.Task && _approvers.Count > 0 && _r.Next(100) < 50) a.AssignedById = Pick(_approvers).Id;
                else a.AssignedById = a.OwnerId;
                if (a.Status == ActivityStatus.Planned && _r.Next(100) < 35) { a.StartedAt = Min(a.CreatedAt.AddDays(1), _today); a.Progress = _r.Next(1, 9) * 10; }
                if (a.Status == ActivityStatus.Completed) { a.Progress = 100; a.StartedAt = a.CompletedAt; }
            }
            _db.Activities.AddRange(acts);
            await _db.SaveChangesAsync();

            await EnsureTargetsAsync();
            var campaigns = await GenerateCampaignsAsync(Math.Clamp(n / 12, 3, 12), customers, contactsByCustomer, leads, opps);
            var tickets = (await GenerateTicketsAsync(SourcesOf(contracts), contactsByCustomer)).Count;

            return new DemoDataResult(newProducts, customers.Count, contacts.Count, leads.Count, opps.Count, acts.Count,
                quotes.Count, plans.Count, contracts.Count, invoices.Count, payments.Count, campaigns, tickets);
        }

        /// <summary>
        /// Giao chỉ tiêu KPI mẫu (năm trước + năm nay) cho người chưa có: dựa trên thực tế bình quân tháng
        /// trong 12 tháng gần nhất của từng người, nhân hệ số tăng trưởng 1,0–1,3.
        /// </summary>
        private async Task EnsureTargetsAsync()
        {
            var since = _today.AddMonths(-12);
            var ids = _owners.Select(u => u.Id).Distinct().ToList();
            var won = await _db.Opportunities.Where(o => o.Stage == OpportunityStage.ClosedWon && o.ClosedAt >= since && o.OwnerId != null)
                .GroupBy(o => o.OwnerId!.Value).Select(g => new { g.Key, V = g.Sum(o => o.Amount), N = g.Count() }).ToDictionaryAsync(x => x.Key);
            var cust = await _db.Customers.Where(c => c.CreatedAt >= since && c.OwnerId != null)
                .GroupBy(c => c.OwnerId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
            var acts = await _db.Activities.Where(a => a.Status == ActivityStatus.Completed && a.CompletedAt >= since && a.OwnerId != null)
                .GroupBy(a => a.OwnerId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
            var existing = (await _db.SalesTargets.Select(t => new { t.UserId, t.Year, t.Month }).ToListAsync())
                .Select(t => (t.UserId, t.Year, t.Month)).ToHashSet();

            foreach (var uid in ids)
            {
                var rev = won.TryGetValue(uid, out var w) ? w.V / 12m : 300_000_000m;
                var deals = won.TryGetValue(uid, out var w2) ? w2.N / 12m : 1m;
                var newCust = cust.GetValueOrDefault(uid) / 12m;
                var done = acts.GetValueOrDefault(uid) / 12m;
                foreach (var year in new[] { _today.Year - 1, _today.Year })
                {
                    var growth = (decimal)(1.0 + _r.NextDouble() * 0.3);
                    for (var m = 1; m <= 12; m++)
                    {
                        if (existing.Contains((uid, year, m))) continue;
                        var season = m is 12 or 6 ? 1.2m : m is 1 or 2 ? 0.8m : 1m;   // mùa vụ: cuối quý II / cuối năm cao, Tết thấp
                        _db.SalesTargets.Add(new SalesTarget
                        {
                            UserId = uid, Year = year, Month = m,
                            Revenue = Math.Max(50_000_000m, Math.Round(rev * growth * season / 10_000_000m) * 10_000_000m),
                            Collection = Math.Max(50_000_000m, Math.Round(rev * growth * season * 1.05m / 10_000_000m) * 10_000_000m),
                            Contracts = Math.Max(1, (int)Math.Round(deals * growth * season)),
                            NewCustomers = Math.Max(1, (int)Math.Round(newCust * growth)),
                            Activities = Math.Max(5, (int)Math.Round(done * growth / 5m) * 5),
                            UpdatedAt = DateTime.Now
                        });
                    }
                }
            }
            await _db.SaveChangesAsync();
        }

        // ================= Ngữ cảnh / danh mục =================
        private async Task LoadContextAsync()
        {
            var users = await _db.Users.Where(u => u.IsActive).ToListAsync();
            _owners = users.Where(u => u.Role == AppRoles.Sales).ToList();
            _owners.AddRange(users.Where(u => u.Role == AppRoles.Manager));   // quản lý cũng phụ trách một phần
            if (_owners.Count == 0) _owners = users;
            if (_owners.Count == 0) throw new InvalidOperationException("Chưa có người dùng nào để gán phụ trách.");
            _approvers = users.Where(u => u.Role is AppRoles.Manager or AppRoles.Admin).ToList();
            if (_approvers.Count == 0) _approvers = users;

            var cat = await _db.CatalogItems.Where(x => x.IsActive).OrderBy(x => x.SortOrder).ToListAsync();
            List<string> Of(CatalogGroup gr, params string[] fallback)
            {
                var l = cat.Where(x => x.Group == gr).Select(x => x.Name).ToList();
                return l.Count > 0 ? l : fallback.ToList();
            }
            _industries = Of(CatalogGroup.Industry, "Sản xuất", "Bán lẻ", "Y tế").Where(x => x != "Khác").ToList();
            _cities = Of(CatalogGroup.City, "Hà Nội", "TP. Hồ Chí Minh", "Đà Nẵng");
            // Ưu tiên các tỉnh/thành lớn để dữ liệu giống thực tế
            var big = new[] { "TP. Hồ Chí Minh", "Hà Nội", "Đà Nẵng", "Hải Phòng", "Cần Thơ", "Đồng Nai", "Bắc Ninh" }.Where(_cities.Contains).ToList();
            for (var i = 0; i < 4; i++) _cities.AddRange(big);
            _lossReasons = Of(CatalogGroup.LossReason, "Khách chọn nhà cung cấp khác giá thấp hơn");
            _costTypes = Of(CatalogGroup.CostType, "Vận chuyển", "Lắp đặt – triển khai", "Khác");
            _payMethods = Of(CatalogGroup.PaymentMethod, "Chuyển khoản", "Tiền mặt");

            foreach (var x in await _db.Quotes.Select(x => x.Number).ToListAsync()) _numbers.Add(x);
            foreach (var x in await _db.BusinessPlans.Select(x => x.Number).ToListAsync()) _numbers.Add(x);
            foreach (var x in await _db.Contracts.Select(x => x.Number).ToListAsync()) _numbers.Add(x);
            foreach (var x in await _db.Payments.Select(x => x.Number).ToListAsync()) _numbers.Add(x);
            foreach (var x in await _db.Customers.Select(x => x.Name).ToListAsync()) _usedNames.Add(x);
            foreach (var x in await _db.Invoices.Select(x => new { x.Symbol, x.Number }).ToListAsync())
                if (long.TryParse(x.Number, out var v)) _invoiceSeq[x.Symbol] = Math.Max(_invoiceSeq.GetValueOrDefault(x.Symbol), v);
        }

        /// <summary>Bổ sung hàng hoá mẫu (bỏ qua mã đã có)</summary>
        private async Task<int> EnsureProductsAsync()
        {
            var codes = (await _db.Products.Select(p => p.Code).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var (code, name, cat, unit, price, type, brand, origin, warranty, costRatio) in ExtraProducts)
            {
                if (codes.Contains(code)) continue;
                _db.Products.Add(new Product
                {
                    Code = code, Name = name, Category = cat, Unit = unit, UnitPrice = price, Type = type, Brand = brand, Origin = origin,
                    WarrantyMonths = warranty, CostPrice = RoundTo(price * costRatio, 1000), VatPercent = 10, IsActive = true
                });
                added++;
            }
            await _db.SaveChangesAsync();
            _products = await _db.Products.Where(p => p.IsActive).ToListAsync();
            if (_products.Count == 0) throw new InvalidOperationException("Chưa có hàng hoá.");
            return added;
        }

        // ================= Tiện ích sinh dữ liệu =================
        /// <summary>
        /// Chiến dịch tiếp thị trải trong 12 tháng: danh sách đối tượng (liên hệ chính + lead), trạng thái tiếp cận,
        /// và gắn một phần lead / cơ hội phát sinh trong thời gian chạy vào chiến dịch để có số liệu ROI.
        /// </summary>
        /// <summary>Chỉ sinh dữ liệu tiếp thị trên khách hàng / liên hệ / lead / cơ hội đang có (không tạo khách hàng mới)</summary>
        public async Task<(int Campaigns, int Members, int Leads, int Opportunities)> RunMarketingAsync(int count)
        {
            await LoadContextAsync();
            var customers = await _db.Customers.Where(c => c.Type != CustomerType.Inactive).ToListAsync();
            if (customers.Count == 0) throw new InvalidOperationException("Chưa có khách hàng để lập danh sách tiếp thị.");
            var contactsByCustomer = (await _db.Contacts.Where(x => x.CustomerId != null)
                    .OrderByDescending(x => x.IsPrimary).ThenBy(x => x.Id).ToListAsync())
                .GroupBy(x => x.CustomerId!.Value).ToDictionary(g => g.Key, g => g.ToList());
            var leads = await _db.Leads.ToListAsync();
            var opps = await _db.Opportunities.ToListAsync();
            var membersBefore = await _db.CampaignMembers.CountAsync();
            var leadsBefore = leads.Count(l => l.CampaignId != null);
            var oppsBefore = opps.Count(o => o.CampaignId != null);
            var n = await GenerateCampaignsAsync(count, customers, contactsByCustomer, leads, opps);
            return (n, await _db.CampaignMembers.CountAsync() - membersBefore,
                leads.Count(l => l.CampaignId != null) - leadsBefore, opps.Count(o => o.CampaignId != null) - oppsBefore);
        }

        private async Task<int> GenerateCampaignsAsync(int count, List<Customer> customers, Dictionary<int, List<Contact>> contactsByCustomer,
            List<Lead> leads, List<Opportunity> opps)
        {
            var seq = new Dictionary<int, int>();
            var campaigns = new List<Campaign>();
            for (var i = 0; i < count; i++)
            {
                // Chiến dịch cuối cùng ở tương lai gần (đang lên kế hoạch), còn lại rải trong 11 tháng qua
                var start = i == count - 1 ? _today.AddDays(_r.Next(5, 25)) : _today.AddDays(-_r.Next(0, 330));
                var end = start.AddDays(_r.Next(14, 61));
                var year = start.Year;
                if (!seq.ContainsKey(year))
                {
                    var prefix = $"CD{year}-";
                    var codes = await _db.Campaigns.Where(c => c.Code.StartsWith(prefix)).Select(c => c.Code).ToListAsync();
                    seq[year] = codes.Select(c => int.TryParse(c[prefix.Length..], out var x) ? x : 0).DefaultIfEmpty(0).Max();
                }
                var (name, channel) = Pick(CampaignIdeas);
                var status = start > _today ? CampaignStatus.Planned
                    : end >= _today ? CampaignStatus.Active
                    : _r.Next(100) < 10 ? CampaignStatus.Cancelled : CampaignStatus.Completed;
                var budget = RoundTo(_r.Next(2, 30) * 5_000_000m, 1_000_000);
                var spent = status switch
                {
                    CampaignStatus.Planned => 0m,
                    CampaignStatus.Active => RoundTo(budget * _r.Next(20, 70) / 100m, 500_000),
                    CampaignStatus.Cancelled => RoundTo(budget * _r.Next(5, 40) / 100m, 500_000),
                    _ => RoundTo(budget * _r.Next(60, 115) / 100m, 500_000)
                };
                campaigns.Add(new Campaign
                {
                    Code = $"CD{year}-{++seq[year]:000}", Name = $"{name} Q{(start.Month - 1) / 3 + 1}/{year}", Channel = channel, Status = status,
                    StartDate = start, EndDate = end, Budget = budget, ActualCost = spent, ExpectedRevenue = budget * _r.Next(4, 12),
                    TargetLeads = _r.Next(2, 8) * 5, OwnerId = Pick(_approvers.Count > 0 ? _approvers : _owners).Id,
                    Objective = Pick(CampaignObjectives), Message = CampaignMessage, CreatedAt = start.AddDays(-_r.Next(7, 21))
                });
            }
            _db.Campaigns.AddRange(campaigns);
            await _db.SaveChangesAsync();

            var openLeads = leads.Where(l => l.Status != LeadStatus.Converted).ToList();
            var members = new List<CampaignMember>();
            foreach (var c in campaigns)
            {
                var started = c.Status != CampaignStatus.Planned;
                var done = c.Status is CampaignStatus.Completed or CampaignStatus.Cancelled;
                var lastDay = c.EndDate!.Value < _today ? c.EndDate.Value : _today;
                CampaignMemberStatus MemberStatus() => !started ? CampaignMemberStatus.Pending : Weighted(
                    (CampaignMemberStatus.Pending, done ? 3 : 30), (CampaignMemberStatus.Sent, 40), (CampaignMemberStatus.Responded, 14),
                    (CampaignMemberStatus.Declined, 8), (CampaignMemberStatus.Unsubscribed, 2), (CampaignMemberStatus.Failed, 6));
                DateTime? Touched(CampaignMemberStatus s) => s == CampaignMemberStatus.Pending ? null
                    : c.StartDate.AddDays(_r.Next(0, Math.Max(1, (lastDay - c.StartDate).Days + 1))).AddHours(_r.Next(8, 18));

                foreach (var cu in customers.Where(_ => _r.Next(100) < 35))
                {
                    var ct = contactsByCustomer.TryGetValue(cu.Id, out var cs) && cs.Count > 0 ? cs[0] : null;
                    var s = MemberStatus();
                    members.Add(new CampaignMember
                    {
                        CampaignId = c.Id, CustomerId = cu.Id, ContactId = ct?.Id, FullName = ct?.FullName ?? cu.Name, Company = cu.Name,
                        Email = ct?.Email ?? cu.Email, Phone = ct?.Phone ?? cu.Phone, Status = s, LastContactAt = Touched(s), AddedAt = c.CreatedAt
                    });
                }
                foreach (var l in openLeads.Where(_ => _r.Next(100) < 25))
                {
                    var s = MemberStatus();
                    members.Add(new CampaignMember
                    {
                        CampaignId = c.Id, LeadId = l.Id, FullName = l.FullName, Company = l.Company, Email = l.Email, Phone = l.Phone,
                        Status = s, LastContactAt = Touched(s), AddedAt = c.CreatedAt
                    });
                }

                if (!started) continue;
                // Lead phát sinh trong thời gian chiến dịch, cùng nguồn với kênh → quy về chiến dịch
                var src = Campaign.LeadSourceFor(c.Channel);
                var window = c.EndDate.Value.AddDays(14);
                foreach (var l in leads.Where(l => l.CampaignId == null && l.CreatedAt >= c.StartDate && l.CreatedAt <= window
                                                   && (l.Source == src || _r.Next(100) < 15)))
                    l.CampaignId = c.Id;
                // Cơ hội của khách hàng là đối tượng chiến dịch, tạo trong thời gian chạy → quy về chiến dịch
                var memberCustomers = members.Where(m => m.CampaignId == c.Id && m.CustomerId != null && m.Status != CampaignMemberStatus.Unsubscribed)
                    .Select(m => m.CustomerId!.Value).ToHashSet();
                foreach (var o in opps.Where(o => o.CampaignId == null && o.CustomerId != null && memberCustomers.Contains(o.CustomerId.Value)
                                                  && o.CreatedAt >= c.StartDate && o.CreatedAt <= window && _r.Next(100) < 60))
                {
                    o.CampaignId = c.Id;
                    var m = members.FirstOrDefault(x => x.CampaignId == c.Id && x.CustomerId == o.CustomerId);
                    if (m != null) { m.Status = CampaignMemberStatus.Converted; m.LastContactAt ??= o.CreatedAt; }
                }
            }
            _db.CampaignMembers.AddRange(members);
            await _db.SaveChangesAsync();
            return campaigns.Count;
        }

        private static readonly (string Name, CampaignChannel Channel)[] CampaignIdeas =
        {
            ("Email giới thiệu giải pháp mới", CampaignChannel.Email),
            ("Ưu đãi gia hạn hợp đồng", CampaignChannel.Email),
            ("Bản tin khách hàng", CampaignChannel.Email),
            ("SMS chúc mừng & khuyến mãi", CampaignChannel.Sms),
            ("Chăm sóc qua Zalo OA", CampaignChannel.Zalo),
            ("Quảng cáo Facebook / LinkedIn", CampaignChannel.Social),
            ("Hội thảo chuyển đổi số", CampaignChannel.Event),
            ("Gọi điện khách hàng cũ", CampaignChannel.Telesales),
            ("Webinar demo sản phẩm", CampaignChannel.Webinar),
        };

        private static readonly string[] CampaignObjectives =
        {
            "Khách hàng đang giao dịch – bán thêm / bán chéo dịch vụ, mục tiêu tăng 10% doanh số nhóm khách hàng cũ.",
            "Doanh nghiệp vừa và nhỏ khu vực phía Bắc – thu thập lead mới cho đội kinh doanh.",
            "Khách hàng tiềm năng chưa phát sinh đơn hàng – kích hoạt lại nhu cầu, đặt lịch demo.",
            "Khách hàng sắp hết hạn hợp đồng – gia hạn sớm với ưu đãi.",
        };

        private const string CampaignMessage =
            "Kính gửi Quý khách,\n\nNhân dịp ra mắt gói dịch vụ mới, chúng tôi trân trọng gửi tới Quý khách ưu đãi giảm 10% cho đơn hàng ký trong tháng.\n" +
            "Vui lòng phản hồi email này hoặc liên hệ nhân viên phụ trách để được tư vấn chi tiết.\n\nTrân trọng.";

        /// <summary>Nơi phát sinh phiếu hỗ trợ: khách hàng (+ hợp đồng, hàng hoá nếu có), phiếu chỉ phát sinh từ ngày Since</summary>
        private record TicketSource(int CustomerId, int? ContractId, int? ProductId, DateTime Since);

        private static List<TicketSource> SourcesOf(IEnumerable<Contract> contracts) => contracts
            .Where(c => c.SignDate.HasValue && c.CustomerId.HasValue)
            .Select(c => new TicketSource(c.CustomerId!.Value, c.Id, c.Lines.Where(l => l.ProductId != null).Select(l => l.ProductId).FirstOrDefault(), c.SignDate!.Value.AddDays(5)))
            .ToList();

        /// <summary>
        /// Chỉ nạp phiếu hỗ trợ mẫu trên dữ liệu đang có: ưu tiên khách hàng có hợp đồng đã ký (phiếu gắn hợp đồng + hàng hoá),
        /// sau đó đến khách hàng đang giao dịch chưa có hợp đồng. Không tạo khách hàng mới.
        /// </summary>
        public async Task<(int Tickets, int Open, int Closed, int Comments)> RunSupportAsync(int count)
        {
            await LoadContextAsync();
            var contracts = await _db.Contracts.Include(c => c.Lines).AsNoTracking()
                .Where(c => c.SignDate != null && (c.Status == ContractStatus.Active || c.Status == ContractStatus.Completed || c.Status == ContractStatus.Liquidated))
                .ToListAsync();
            var sources = SourcesOf(contracts);
            var withContract = sources.Select(s => s.CustomerId).ToHashSet();
            var since = _today.AddMonths(-12);
            sources.AddRange((await _db.Customers.AsNoTracking().Where(c => c.Type == CustomerType.Customer || c.Type == CustomerType.Partner).ToListAsync())
                .Where(c => !withContract.Contains(c.Id))
                .Select(c => new TicketSource(c.Id, null, null, c.CreatedAt > since ? c.CreatedAt : since)));
            if (sources.Count == 0)
                sources = (await _db.Customers.AsNoTracking().Where(c => c.Type != CustomerType.Inactive).ToListAsync())
                    .Select(c => new TicketSource(c.Id, null, null, c.CreatedAt > since ? c.CreatedAt : since)).ToList();
            if (sources.Count == 0) throw new InvalidOperationException("Chưa có khách hàng để tạo phiếu hỗ trợ.");

            var contactsByCustomer = (await _db.Contacts.Where(x => x.CustomerId != null)
                    .OrderByDescending(x => x.IsPrimary).ThenBy(x => x.Id).ToListAsync())
                .GroupBy(x => x.CustomerId!.Value).ToDictionary(g => g.Key, g => g.ToList());
            var tickets = await GenerateTicketsAsync(sources, contactsByCustomer, count);
            return (tickets.Count, tickets.Count(t => t.IsOpen), tickets.Count(t => t.Status == TicketStatus.Closed), tickets.Sum(t => t.Comments.Count));
        }

        /// <summary>
        /// Phiếu hỗ trợ mẫu: phát sinh sau ngày ký hợp đồng (hoặc từ ngày Since của nguồn), thời gian phản hồi / giải quyết
        /// dao động quanh hạn SLA (khoảng 15% trễ hạn), phiếu cũ phần lớn đã đóng kèm điểm hài lòng.
        /// <paramref name="target"/> = null: mỗi nguồn 0–4 phiếu; có giá trị: sinh đúng số phiếu đó, rải ngẫu nhiên trên các nguồn.
        /// </summary>
        private async Task<List<Ticket>> GenerateTicketsAsync(List<TicketSource> sources, Dictionary<int, List<Contact>> contactsByCustomer, int? target = null)
        {
            sources = sources.Where(s => s.Since < _today).ToList();
            if (sources.Count == 0) return new();
            var users = await _db.Users.Where(u => u.IsActive).ToListAsync();
            var agents = users.Where(u => u.Role == AppRoles.Support).ToList();
            // Người xử lý: nhân viên CSKH (2 phần) + quản lý (1 phần); chưa có CSKH thì dùng quản lý / admin
            var managers = users.Where(u => u.Role == AppRoles.Manager).ToList();
            agents = agents.Count == 0 ? _approvers.ToList() : agents.Concat(agents).Concat(managers).ToList();
            var categories = await _db.CatalogItems.Where(x => x.Group == CatalogGroup.TicketCategory && x.IsActive)
                .OrderBy(x => x.SortOrder).Select(x => x.Name).ToListAsync();
            if (categories.Count == 0) categories = new() { "Hỗ trợ kỹ thuật" };
            foreach (var x in await _db.Tickets.Select(x => x.Number).ToListAsync()) _numbers.Add(x);

            var now = DateTime.Now;
            var tickets = new List<Ticket>();
            // Danh sách lượt sinh: mỗi phần tử là một nguồn → một phiếu
            var plan = target.HasValue
                ? Enumerable.Range(0, Math.Max(0, target.Value)).Select(_ => Pick(sources)).ToList()
                : sources.SelectMany(s => Enumerable.Repeat(s, Weighted((0, 25), (1, 35), (2, 25), (3, 10), (4, 5)))).ToList();
            foreach (var c in plan)
            {
                var since = c.Since;
                {
                    // Khoảng 1/3 phiếu phát sinh trong 10 ngày gần đây để luôn có phiếu đang mở / sắp đến hạn
                    var spanDays = Math.Max(1, (_today - since).Days + 1);
                    var created = since.AddDays(_r.Next(100) < 35 ? Math.Max(0, spanDays - 1 - (_r.Next(100) < 40 ? _r.Next(0, 3) : _r.Next(0, 10))) : _r.Next(0, spanDays)).AddHours(_r.Next(8, 18)).AddMinutes(_r.Next(60));
                    if (created > now) created = now.AddMinutes(-_r.Next(10, 300));
                    var category = Pick(categories);
                    var subjects = TicketSubjects.TryGetValue(category, out var s) ? s : TicketSubjects["Hỗ trợ kỹ thuật"];
                    var contacts = contactsByCustomer.GetValueOrDefault(c.CustomerId) ?? new List<Contact>();
                    var agent = Pick(agents);
                    var t = new Ticket
                    {
                        Number = NextNumber("YC", created), Subject = Pick(subjects), CustomerId = c.CustomerId, ContractId = c.ContractId, ProductId = c.ProductId,
                        ContactId = contacts.Count > 0 ? Pick(contacts).Id : null, Category = category, CreatedAt = created,
                        Channel = Weighted((TicketChannel.Phone, 40), (TicketChannel.Email, 30), (TicketChannel.Zalo, 20), (TicketChannel.Web, 8), (TicketChannel.InPerson, 2)),
                        Priority = Weighted((TicketPriority.Low, 20), (TicketPriority.Normal, 50), (TicketPriority.High, 22), (TicketPriority.Urgent, 8)),
                        CreatedById = agent.Id, Status = TicketStatus.New
                    };
                    t.Description = $"Khách hàng báo: {t.Subject.ToLowerInvariant()}. Mức ảnh hưởng: {Pick(TicketImpacts)}.";
                    t.ApplySla();
                    var (respH, resH) = TicketSla.For(t.Priority);
                    void Log(DateTime at, string body, bool system = true, bool internalNote = true, int? author = null) =>
                        t.Comments.Add(new TicketComment { Body = body, IsSystem = system, IsInternal = internalNote, AuthorId = author ?? agent.Id, CreatedAt = at });
                    Log(created, $"Tiếp nhận yêu cầu qua kênh {t.Channel.GetDisplayName()}.");

                    // Thời gian phản hồi / giải quyết: phần lớn trong hạn, một phần trễ
                    var firstResp = created.AddMinutes(respH * 60 * (_r.Next(100) < 85 ? 0.1 + _r.NextDouble() * 0.85 : 1.1 + _r.NextDouble()));
                    var resolved = created.AddMinutes(resH * 60 * (_r.Next(100) < 85 ? 0.15 + _r.NextDouble() * 0.8 : 1.05 + _r.NextDouble() * 1.2));
                    if (resolved < firstResp) resolved = firstResp.AddMinutes(30);
                    if (firstResp <= now)
                    {
                        t.OwnerId = agent.Id;
                        t.Status = TicketStatus.InProgress;
                        t.FirstResponseAt = firstResp;
                        Log(firstResp.AddMinutes(-5), $"Phân công cho {agent.FullName}.");
                        t.Comments.Add(new TicketComment { Body = Pick(TicketReplies), AuthorId = agent.Id, CreatedAt = firstResp });
                        if (_r.Next(100) < 40)
                            Log(firstResp.AddMinutes(_r.Next(20, 240)), Pick(TicketNotes), system: false);
                    }
                    else if (_r.Next(100) < 50) { t.OwnerId = agent.Id; t.Status = TicketStatus.InProgress; }

                    if (resolved <= now && t.FirstResponseAt != null)
                    {
                        t.Status = TicketStatus.Resolved;
                        t.ResolvedAt = resolved;
                        t.Resolution = Pick(TicketResolutions);
                        Log(resolved, "Đã giải quyết." + (resolved > t.ResolveDueAt ? " (trễ hạn SLA)" : ""));
                        var closeAt = resolved.AddDays(_r.Next(1, 5)).AddHours(_r.Next(0, 6));
                        if (closeAt <= now && _r.Next(100) < 85)
                        {
                            t.Status = TicketStatus.Closed;
                            t.ClosedAt = closeAt;
                            if (_r.Next(100) < 75)
                                t.SatisfactionScore = resolved > t.ResolveDueAt
                                    ? Weighted((5, 15), (4, 30), (3, 30), (2, 17), (1, 8))
                                    : Weighted((5, 50), (4, 32), (3, 12), (2, 4), (1, 2));
                            if (t.SatisfactionScore.HasValue && _r.Next(100) < 50)
                                t.SatisfactionComment = t.SatisfactionScore >= 4 ? Pick(TicketPraise) : Pick(TicketComplaints);
                            Log(closeAt, "Đóng phiếu." + (t.SatisfactionScore.HasValue ? $" Khách đánh giá {t.SatisfactionScore}/5." : ""));
                        }
                    }
                    else if (t.FirstResponseAt != null && _r.Next(100) < 35)
                    {
                        t.Status = TicketStatus.WaitingCustomer;
                        Log(t.FirstResponseAt.Value.AddMinutes(10), "Chuyển trạng thái: Đang xử lý → Chờ khách phản hồi.");
                    }
                    // ---- Công việc: việc chính theo người xử lý + đôi khi giao thêm việc con ----
                    if (t.OwnerId.HasValue)
                    {
                        var done = !t.IsOpen;
                        _db.Activities.Add(new Activity
                        {
                            Ticket = t, IsTicketMain = true, Type = ActivityType.Task, Subject = TicketWork.MainSubject(t), OwnerId = t.OwnerId,
                            CustomerId = t.CustomerId, ContactId = t.ContactId, Priority = TicketWork.PriorityOf(t.Priority), DueDate = t.ResolveDueAt,
                            CreatedAt = t.FirstResponseAt ?? created, Status = done ? ActivityStatus.Completed : ActivityStatus.Planned,
                            CompletedAt = done ? t.ResolvedAt : null, Description = done ? t.Resolution : null
                        });
                        if (_r.Next(100) < 35)
                        {
                            var (subject, type) = Pick(TicketSubTasks);
                            var assignee = _r.Next(100) < 60 ? agent : Pick(agents);
                            var start = t.FirstResponseAt ?? created;
                            var end = t.ResolvedAt ?? Max(t.ResolveDueAt, now.AddHours(4));
                            var due = start.AddMinutes(_r.Next(30, Math.Max(31, (int)(end - start).TotalMinutes)));
                            var subDone = t.ResolvedAt.HasValue || (due < now && _r.Next(100) < 70);
                            var sub = new Activity
                            {
                                Ticket = t, Type = type, Subject = subject, OwnerId = assignee.Id, CustomerId = t.CustomerId, ContactId = t.ContactId,
                                Priority = TicketWork.PriorityOf(t.Priority), DueDate = due, CreatedAt = start,
                                Status = subDone ? ActivityStatus.Completed : ActivityStatus.Planned,
                                CompletedAt = subDone ? Min(due, t.ResolvedAt ?? now) : null, Description = subDone ? Pick(TicketSubTaskResults) : null
                            };
                            _db.Activities.Add(sub);
                            Log(start.AddMinutes(1), $"Giao việc \"{subject}\" cho {assignee.FullName}, hạn {due:dd/MM/yyyy HH:mm}.");
                            if (subDone) Log(sub.CompletedAt!.Value, $"{assignee.FullName} hoàn thành việc \"{subject}\". Kết quả: {sub.Description}", system: true, author: assignee.Id);
                        }
                    }
                    t.UpdatedAt = t.Comments.Max(x => x.CreatedAt);
                    tickets.Add(t);
                }
            }
            _db.Tickets.AddRange(tickets);
            await _db.SaveChangesAsync();
            return tickets;
        }

        private static readonly Dictionary<string, string[]> TicketSubjects = new()
        {
            ["Hỗ trợ kỹ thuật"] = new[] { "Không đăng nhập được hệ thống", "Báo cáo chạy chậm vào cuối tháng", "Lỗi khi xuất file Excel", "Đồng bộ dữ liệu giữa chi nhánh bị lỗi", "Máy chủ báo đầy dung lượng", "Lỗi kết nối máy in hoá đơn" },
            ["Bảo hành / sửa chữa"] = new[] { "Thiết bị không lên nguồn", "Màn hình máy trạm bị sọc", "Ổ cứng máy chủ báo lỗi", "Yêu cầu bảo hành thiết bị mạng" },
            ["Khiếu nại"] = new[] { "Thời gian xử lý sự cố quá lâu", "Nhân viên triển khai không đến đúng hẹn", "Chất lượng đào tạo chưa đạt yêu cầu" },
            ["Yêu cầu thay đổi / nâng cấp"] = new[] { "Bổ sung trường thông tin trên phiếu nhập", "Nâng cấp thêm 20 người dùng", "Thêm mẫu báo cáo quản trị theo yêu cầu", "Tích hợp với phần mềm kế toán" },
            ["Hướng dẫn sử dụng"] = new[] { "Hướng dẫn phân quyền người dùng mới", "Cách khoá sổ cuối kỳ", "Hướng dẫn nhập dữ liệu hàng loạt từ Excel" },
            ["Hoá đơn / thanh toán"] = new[] { "Đề nghị xuất lại hoá đơn sai thông tin", "Đối chiếu công nợ quý", "Xác nhận đã chuyển khoản đợt 2" },
            ["Góp ý / đề xuất"] = new[] { "Đề xuất cải tiến giao diện màn hình bán hàng", "Góp ý về tài liệu hướng dẫn" },
            ["Khác"] = new[] { "Yêu cầu cung cấp hồ sơ năng lực", "Cập nhật thông tin người liên hệ" },
        };
        private static readonly (string, ActivityType)[] TicketSubTasks =
        {
            ("Gọi lại khách xác nhận kết quả xử lý", ActivityType.Call), ("Kết nối từ xa kiểm tra hệ thống", ActivityType.Task),
            ("Đến hiện trường kiểm tra thiết bị", ActivityType.Meeting), ("Chuyển đội phát triển phân tích lỗi", ActivityType.Task),
            ("Gửi hướng dẫn chi tiết qua email", ActivityType.Email), ("Đặt linh kiện thay thế", ActivityType.Task),
        };
        private static readonly string[] TicketSubTaskResults =
        {
            "Đã thực hiện, khách xác nhận ổn.", "Đã kiểm tra, không phát hiện lỗi thêm.", "Hoàn thành, đã cập nhật vào phiếu.", "Đã gửi, khách đã nhận."
        };
        private static readonly string[] TicketImpacts = { "một người dùng", "một phòng ban", "toàn công ty", "không ảnh hưởng vận hành" };
        private static readonly string[] TicketReplies =
        {
            "Chào anh/chị, bộ phận hỗ trợ đã tiếp nhận yêu cầu và đang kiểm tra. Chúng tôi sẽ cập nhật tiến độ sớm nhất.",
            "Cảm ơn anh/chị đã thông báo. Nhờ anh/chị gửi giúp ảnh chụp màn hình lỗi và thời điểm phát sinh để chúng tôi xử lý nhanh hơn.",
            "Chúng tôi đã kết nối từ xa và xác định được nguyên nhân, đang tiến hành khắc phục.",
            "Đã ghi nhận yêu cầu, kỹ thuật viên sẽ liên hệ lại trong hôm nay để hẹn lịch xử lý."
        };
        private static readonly string[] TicketNotes =
        {
            "Đã chuyển đội phát triển kiểm tra log máy chủ.", "Khách hàng VIP – ưu tiên xử lý.", "Liên quan phiên bản cập nhật tuần trước, cần kiểm tra lại bản vá.",
            "Đã đặt linh kiện thay thế, dự kiến về trong 2 ngày."
        };
        private static readonly string[] TicketResolutions =
        {
            "Đã cấu hình lại tài khoản và hướng dẫn khách đổi mật khẩu.", "Áp dụng bản vá mới nhất, khách đã kiểm tra hoạt động bình thường.",
            "Tối ưu chỉ mục cơ sở dữ liệu, thời gian chạy báo cáo giảm còn dưới 10 giây.", "Thay thế linh kiện theo chế độ bảo hành.",
            "Gửi tài liệu hướng dẫn và hướng dẫn trực tiếp qua điện thoại.", "Xuất hoá đơn điều chỉnh và gửi lại cho khách.",
            "Bổ sung chức năng theo yêu cầu trong bản phát hành tháng này."
        };
        private static readonly string[] TicketPraise = { "Hỗ trợ nhanh, nhiệt tình.", "Kỹ thuật viên chuyên nghiệp.", "Rất hài lòng." };
        private static readonly string[] TicketComplaints = { "Xử lý hơi chậm.", "Phải gọi nhiều lần mới có người hỗ trợ.", "Lỗi tái diễn, cần theo dõi thêm." };

        private T Pick<T>(IReadOnlyList<T> list) => list[_r.Next(list.Count)];
        private T Pick<T>(T[] arr) => arr[_r.Next(arr.Length)];

        private T Weighted<T>(params (T Value, int Weight)[] items)
        {
            var roll = _r.Next(items.Sum(x => x.Weight));
            foreach (var (v, w) in items) { if (roll < w) return v; roll -= w; }
            return items[^1].Value;
        }

        private static decimal RoundTo(decimal v, decimal step) => Math.Max(step, Math.Round(v / step) * step);
        private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
        private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

        private List<Product> PickProducts()
        {
            // Một gói giải pháp: 1–2 phần mềm/phần cứng + 1–2 dịch vụ
            var goods = _products.Where(p => p.Type == ProductType.Goods).OrderBy(_ => _r.Next()).Take(_r.Next(1, 3));
            var services = _products.Where(p => p.Type == ProductType.Service).OrderBy(_ => _r.Next()).Take(_r.Next(1, 3));
            var list = goods.Concat(services).ToList();
            return list.Count > 0 ? list : new List<Product> { Pick(_products) };
        }

        private decimal Qty(Product p) => p.Unit switch
        {
            "Ngày công" => _r.Next(5, 60),
            "Giờ" => _r.Next(8, 80),
            "Buổi" => _r.Next(2, 12),
            "User/năm" => _r.Next(10, 150),
            "Cái" => _r.Next(2, 40),
            _ => _r.Next(1, 4)
        };

        private string NextNumber(string prefix, DateTime date)
        {
            var n = 1;
            string no;
            do no = $"{prefix}-{date:yyyy}-{n++:0000}"; while (!_numbers.Add(no));
            return no;
        }

        private string NextInvoiceNumber(string symbol)
        {
            var v = _invoiceSeq.GetValueOrDefault(symbol) + 1;
            _invoiceSeq[symbol] = v;
            return v.ToString("00000000");
        }

        private (string Name, string Core) CompanyName(string industry)
        {
            for (var attempt = 0; ; attempt++)
            {
                var core = Pick(CoreNames);
                var prefixes = IndustryPrefixes.TryGetValue(industry, out var p) ? p : DefaultPrefixes;
                var name = $"{Pick(prefixes)} {core}";
                if (attempt > 5) name += $" {Pick(new[] { "Miền Nam", "Miền Bắc", "Sài Gòn", "Thủ Đô", "Toàn Cầu", "Quốc tế" })}";
                if (attempt > 20) name += $" {_r.Next(2, 99)}";
                if (_usedNames.Add(name)) return (name, core);
            }
        }

        private static string CoreOf(Customer c) => CoreNames.FirstOrDefault(x => c.Name.Contains(x)) ?? c.Name;

        private (string Full, string Ascii) PersonName()
        {
            var female = _r.Next(2) == 0;
            var family = Pick(Families);
            var middle = Pick(female ? FemaleMiddles : MaleMiddles);
            var given = Pick(female ? FemaleNames : MaleNames);
            return ($"{family} {middle} {given}", $"{Slug(given)}.{Slug(family)}{_r.Next(10, 99)}");
        }

        /// <summary>Bỏ dấu tiếng Việt, chữ thường, bỏ khoảng trắng</summary>
        private static string Slug(string s)
        {
            var norm = s.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var ch in norm)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        private Activity MakeActivity(DateTime from, DateTime to, int? owner, int? customerId, int? contactId, int? leadId, int? oppId)
        {
            var type = Weighted((ActivityType.Call, 35), (ActivityType.Meeting, 25), (ActivityType.Email, 25), (ActivityType.Task, 15));
            var span = Math.Max(1, (int)(to - from).TotalDays);
            var due = from.Date.AddDays(_r.Next(0, span)).AddHours(_r.Next(8, 17)).AddMinutes(_r.Next(0, 4) * 15);
            var a = new Activity
            {
                Type = type, Subject = Pick(ActivitySubjects[type]), DueDate = due, OwnerId = owner, CustomerId = customerId, ContactId = contactId,
                LeadId = leadId, OpportunityId = oppId, Priority = Weighted((Priority.Low, 20), (Priority.Normal, 60), (Priority.High, 20)),
                CreatedAt = Min(due.AddDays(-_r.Next(1, 7)), DateTime.Now)
            };
            if (due < DateTime.Now)
            {
                // Việc quá hạn lâu thường đã xử lý xong / hủy; chỉ việc gần đây (≤ 30 ngày) mới còn treo quá hạn
                var recent = (DateTime.Now - due).TotalDays <= 30;
                var roll = _r.Next(100);
                if (roll < (recent ? 80 : 93)) { a.Status = ActivityStatus.Completed; a.CompletedAt = due.AddHours(1); a.Description = Pick(ActivityResults); }
                else if (!recent || roll < 86) a.Status = ActivityStatus.Cancelled;
                else a.Status = ActivityStatus.Planned;   // quá hạn chưa làm
            }
            return a;
        }

        // ================= Dữ liệu nguồn =================
        private static readonly string[] CoreNames =
        {
            "Phú Thành", "Hưng Thịnh", "Minh Long", "Đại Phát", "Thiên Ân", "Hoàng Gia", "Tân Á", "Việt Hưng", "Sao Mai", "Bình Minh",
            "Kim Ngân", "Phương Đông", "Trường Sơn", "Hải Âu", "Lạc Hồng", "An Phú", "Nam Việt", "Đông Á", "Thành Công", "Gia Khang",
            "Phúc Lộc", "Tín Nghĩa", "Bảo Châu", "Quang Minh", "Vạn Xuân", "Ngọc Lan", "Đức Tài", "Hồng Hà", "Thái Bình Dương", "Cửu Long",
            "Hòa Phát Đạt", "Tiên Phong", "Ánh Sao", "Trí Việt", "Nhật Quang", "Song Long", "Hạ Long Xanh", "Phước An", "Lộc Thiên", "Đất Việt",
            "Hoàn Cầu", "Tâm An", "Kim Long", "Thịnh Vượng", "Hồng Phát", "Bến Thành", "Sông Hàn", "Trường An", "Mai Linh Xanh", "Hà Thành"
        };

        private static readonly string[] DefaultPrefixes = { "Công ty TNHH", "Công ty CP", "Công ty TNHH MTV" };

        private static readonly Dictionary<string, string[]> IndustryPrefixes = new()
        {
            ["Y tế"] = new[] { "Bệnh viện Đa khoa", "Phòng khám Đa khoa", "Hệ thống Y tế" },
            ["Dược phẩm"] = new[] { "Công ty CP Dược phẩm", "Công ty TNHH Dược", "Chuỗi Nhà thuốc" },
            ["Giáo dục"] = new[] { "Trường Quốc tế", "Hệ thống Giáo dục", "Trường Cao đẳng" },
            ["Sản xuất"] = new[] { "Công ty TNHH Cơ khí", "Nhà máy", "Công ty CP Sản xuất" },
            ["Bán lẻ"] = new[] { "Chuỗi cửa hàng", "Siêu thị", "Công ty CP Thương mại" },
            ["Tài chính – Ngân hàng"] = new[] { "Ngân hàng TMCP", "Công ty Chứng khoán", "Công ty Tài chính" },
            ["Vận tải – Logistics"] = new[] { "Công ty CP Vận tải", "Công ty TNHH Logistics", "Công ty CP Giao nhận" },
            ["Thực phẩm – Đồ uống"] = new[] { "Công ty CP Thực phẩm", "Công ty TNHH Nước giải khát", "Công ty CP Chế biến" },
            ["Công nghệ thông tin"] = new[] { "Công ty CP Công nghệ", "Công ty TNHH Phần mềm", "Công ty CP Giải pháp số" },
            ["Xây dựng – Bất động sản"] = new[] { "Công ty CP Xây dựng", "Công ty CP Địa ốc", "Tập đoàn Đầu tư" }
        };

        private static readonly string[] Streets =
        {
            "Nguyễn Huệ", "Lê Lợi", "Trần Hưng Đạo", "Hai Bà Trưng", "Điện Biên Phủ", "Nguyễn Văn Linh", "Võ Văn Kiệt", "Phạm Văn Đồng",
            "Cách Mạng Tháng Tám", "Lý Thường Kiệt", "Hoàng Quốc Việt", "Láng Hạ", "Nguyễn Trãi", "Trường Chinh", "Bạch Đằng"
        };

        private static readonly string[] Families = { "Nguyễn", "Trần", "Lê", "Phạm", "Hoàng", "Huỳnh", "Phan", "Vũ", "Võ", "Đặng", "Bùi", "Đỗ", "Hồ", "Ngô", "Dương", "Lý" };
        private static readonly string[] MaleMiddles = { "Văn", "Đức", "Minh", "Quốc", "Hữu", "Thành", "Hoàng", "Gia", "Tuấn" };
        private static readonly string[] FemaleMiddles = { "Thị", "Ngọc", "Thu", "Thanh", "Mỹ", "Phương", "Bảo", "Khánh" };
        private static readonly string[] MaleNames = { "Hùng", "Dũng", "Tuấn", "Minh", "Khang", "Phúc", "Long", "Nam", "Sơn", "Quân", "Hải", "Bình", "Tài", "Huy", "Đạt", "Thắng" };
        private static readonly string[] FemaleNames = { "Lan", "Hương", "Linh", "Trang", "Mai", "Ngọc", "Hà", "Thảo", "Vy", "Nhung", "Yến", "Hạnh", "Châu", "Anh", "Quyên", "Uyên" };

        private static readonly string[] JobTitles =
        {
            "Giám đốc", "Phó Giám đốc", "Giám đốc Tài chính", "Kế toán trưởng", "Trưởng phòng Nhân sự", "Trưởng phòng CNTT",
            "Giám đốc Vận hành", "Trưởng phòng Mua hàng", "Chuyên viên IT", "Trưởng phòng Hành chính", "Giám đốc Chuyển đổi số"
        };

        private static readonly string[] Needs =
        {
            "Triển khai HRM", "ERP Tài chính kế toán", "CRM bán hàng", "Số hoá quy trình phê duyệt", "Chấm công & tính lương",
            "Gia hạn hỗ trợ vận hành", "Tích hợp hệ thống", "Đào tạo người dùng", "Nâng cấp hạ tầng máy chủ", "Phần mềm quản lý kho",
            "Hệ thống bán hàng POS", "Bản quyền Microsoft 365", "Trang bị máy tính văn phòng", "Hạ tầng mạng chi nhánh"
        };

        private static readonly string[] OppDescriptions =
        {
            "Khách đang dùng Excel, cần chuẩn hoá quy trình và báo cáo quản trị.",
            "Mở rộng thêm chi nhánh, cần hệ thống dùng chung toàn công ty.",
            "Hệ thống cũ hết hỗ trợ, cần thay thế trong năm nay.",
            "Ban giám đốc yêu cầu báo cáo theo thời gian thực.",
            "Đã có ngân sách được duyệt cho quý tới."
        };

        private static readonly string[] LeadNeeds =
        {
            "Quan tâm giải pháp quản trị nhân sự, chấm công và tính lương.",
            "Cần phần mềm quản lý bán hàng cho chuỗi cửa hàng.",
            "Tìm hiểu ERP cho khối tài chính kế toán.",
            "Muốn số hoá quy trình phê duyệt nội bộ.",
            "Cần tư vấn nâng cấp hạ tầng CNTT."
        };

        private static readonly Dictionary<ActivityType, string[]> ActivitySubjects = new()
        {
            [ActivityType.Call] = new[] { "Gọi tìm hiểu nhu cầu", "Gọi follow-up sau demo", "Gọi nhắc lịch thanh toán", "Gọi xác nhận phạm vi triển khai" },
            [ActivityType.Meeting] = new[] { "Họp demo giải pháp", "Họp khảo sát hiện trạng", "Họp đàm phán điều khoản hợp đồng", "Họp nghiệm thu giai đoạn 1" },
            [ActivityType.Email] = new[] { "Gửi tài liệu giới thiệu giải pháp", "Gửi báo giá điều chỉnh", "Gửi dự thảo hợp đồng", "Gửi biên bản nghiệm thu" },
            [ActivityType.Task] = new[] { "Chuẩn bị báo giá", "Lập phương án kinh doanh", "Chuẩn bị hồ sơ thầu", "Chuẩn bị dữ liệu demo" }
        };

        private static readonly string[] ActivityResults =
        {
            "Khách phản hồi tích cực, hẹn trao đổi thêm.", "Đã gửi tài liệu, khách đang xem xét.", "Khách yêu cầu điều chỉnh giá.",
            "Thống nhất phạm vi, chuẩn bị báo giá.", "Đã chốt lịch họp tiếp theo."
        };

        private static readonly decimal[][] MilestonePlans =
        {
            new[] { 50m, 50m }, new[] { 30m, 70m }, new[] { 40m, 40m, 20m }, new[] { 100m }, new[] { 30m, 40m, 30m }
        };

        private static readonly (string Code, string Name, string Category, string Unit, decimal Price, ProductType Type, string? Brand, string? Origin, int? Warranty, decimal CostRatio)[] ExtraProducts =
        {
            ("HW-SRV", "Máy chủ Dell PowerEdge R760", "Phần cứng", "Bộ", 185_000_000, ProductType.Goods, "Dell", "Mỹ", 36, 0.82m),
            ("HW-LAP", "Laptop Lenovo ThinkPad T14", "Phần cứng", "Cái", 28_500_000, ProductType.Goods, "Lenovo", "Trung Quốc", 24, 0.85m),
            ("HW-PC", "Máy tính để bàn HP ProDesk 400", "Phần cứng", "Cái", 17_900_000, ProductType.Goods, "HP", "Trung Quốc", 12, 0.85m),
            ("HW-SW24", "Switch Cisco Catalyst 24 cổng", "Phần cứng", "Cái", 42_000_000, ProductType.Goods, "Cisco", "Mỹ", 36, 0.8m),
            ("HW-FW", "Thiết bị tường lửa Cisco Firepower 1120", "Phần cứng", "Bộ", 96_000_000, ProductType.Goods, "Cisco", "Mỹ", 36, 0.8m),
            ("HW-TC", "Máy chấm công nhận diện khuôn mặt", "Phần cứng", "Cái", 8_500_000, ProductType.Goods, null, "Hàn Quốc", 12, 0.7m),
            ("SW-M365", "Microsoft 365 Business Standard", "Phần mềm", "User/năm", 3_200_000, ProductType.Goods, "Microsoft", "Mỹ", null, 0.88m),
            ("SW-WINSRV", "Windows Server 2022 Standard", "Phần mềm", "Gói", 24_000_000, ProductType.Goods, "Microsoft", "Mỹ", null, 0.88m),
            ("SW-SQL", "SQL Server 2022 Standard (2 core)", "Phần mềm", "Gói", 98_000_000, ProductType.Goods, "Microsoft", "Mỹ", null, 0.88m),
            ("SW-ORA", "Oracle Database Standard Edition 2", "Phần mềm", "Gói", 420_000_000, ProductType.Goods, "Oracle", "Mỹ", null, 0.85m),
            ("WMS-STD", "Phần mềm quản lý kho WMS", "Phần mềm", "Gói/năm", 85_000_000, ProductType.Goods, "Tự phát triển", "Việt Nam", 12, 0.4m),
            ("POS-STD", "Phần mềm bán hàng POS (mỗi cửa hàng)", "Phần mềm", "Gói/năm", 6_000_000, ProductType.Goods, "Tự phát triển", "Việt Nam", 12, 0.35m),
            ("SV-CONS", "Tư vấn chuyển đổi số", "Dịch vụ", "Ngày công", 6_500_000, ProductType.Service, null, null, null, 0.45m),
            ("SV-INT", "Tích hợp hệ thống / API", "Dịch vụ", "Ngày công", 5_000_000, ProductType.Service, null, null, null, 0.45m),
            ("SV-HWI", "Lắp đặt, cấu hình thiết bị", "Dịch vụ", "Giờ", 450_000, ProductType.Service, null, null, null, 0.5m),
            ("SV-AMC", "Bảo trì hệ thống hằng năm", "Dịch vụ", "Năm", 48_000_000, ProductType.Service, null, null, null, 0.4m),
        };
    }
}
