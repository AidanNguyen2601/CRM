using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>Chỉ số doanh thu định kỳ</summary>
public class RecurringStats
{
    public decimal Mrr { get; set; }
    public decimal Arr => Mrr * 12;
    public int RecurringContracts { get; set; }
    public int ExpiringIn90 { get; set; }
    public decimal ExpiringValue { get; set; }
    /// <summary>Hợp đồng hết hạn trong 12 tháng qua: đã gia hạn / không gia hạn</summary>
    public int EndedLast12 { get; set; }
    public int RenewedLast12 { get; set; }
    public decimal RenewalRate => EndedLast12 == 0 ? 0 : Math.Round(RenewedLast12 * 100m / EndedLast12, 1);
}

/// <summary>
/// Hợp đồng định kỳ và gia hạn:
/// - Trước ngày kết thúc N ngày (Nhắc gia hạn trước) tự tạo cơ hội gia hạn cho người phụ trách hợp đồng;
/// - Hợp đồng có chu kỳ thanh toán + số tiền mỗi kỳ: đến kỳ tự lập hoá đơn nháp và dời kỳ tiếp theo;
/// - Gia hạn thủ công: tạo hợp đồng mới (nháp) từ hợp đồng cũ, thời hạn nối tiếp.
/// </summary>
public class RenewalService
{
    private readonly CrmDbContext _db;
    private readonly NotificationService _notify;
    private readonly BranchDirectory _dir;
    private readonly IConfiguration _config;

    public RenewalService(CrmDbContext db, NotificationService notify, BranchDirectory dir, IConfiguration config)
    {
        _db = db; _notify = notify; _dir = dir; _config = config;
    }

    public async Task<(int Renewals, int Invoices)> RunAsync(CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var renewals = 0;
        // (1) Cơ hội gia hạn
        var expiring = await _db.Contracts.IgnoreQueryFilters()
            .Where(c => c.Status == ContractStatus.Active && c.EndDate != null && c.RenewalNoticeDays > 0 && c.RenewalOpportunityId == null
                && c.EndDate >= today.AddDays(-30) && c.EndDate <= today.AddDays(365))
            .ToListAsync(ct);
        foreach (var c in expiring.Where(c => c.EndDate!.Value.AddDays(-c.RenewalNoticeDays) <= today))
        {
            var amount = c.BillingCycle != BillingCycle.None && c.RecurringAmount > 0 ? c.RecurringAmount * (12 / (int)c.BillingCycle) : c.SubTotal;
            var o = new Opportunity
            {
                Name = Cut(I18n.T("Gia hạn: {0}", c.Title), 200), CustomerId = c.CustomerId, ContactId = c.ContactId, OwnerId = c.OwnerId,
                Stage = OpportunityStage.Proposal, Probability = 60, Amount = amount, ExpectedCloseDate = c.EndDate,
                Description = I18n.T("Tự tạo từ hợp đồng {0} hết hạn ngày {1}.", c.Number, c.EndDate.ToDate()), CreatedAt = DateTime.Now
            };
            _db.Opportunities.Add(o);
            await _db.SaveChangesAsync(ct);
            c.RenewalOpportunityId = o.Id;
            _notify.Add(c.OwnerId, "renewal", I18n.T("Hợp đồng {0} sắp hết hạn ({1}) – đã tạo cơ hội gia hạn", c.Number, c.EndDate.ToDate()), $"/Opportunities/Details/{o.Id}");
            renewals++;
        }
        if (renewals > 0) await _db.SaveChangesAsync(ct);

        // (2) Hoá đơn định kỳ (mỗi lượt lập tối đa một kỳ / hợp đồng)
        var invoices = 0;
        var billing = await _db.Contracts.IgnoreQueryFilters()
            .Where(c => c.Status == ContractStatus.Active && c.BillingCycle != BillingCycle.None && c.RecurringAmount > 0
                && c.NextBillingDate != null && c.NextBillingDate <= today && (c.EndDate == null || c.NextBillingDate <= c.EndDate))
            .ToListAsync(ct);
        foreach (var c in billing)
        {
            var from = c.NextBillingDate!.Value.Date;
            var to = from.AddMonths((int)c.BillingCycle).AddDays(-1);
            if (c.EndDate.HasValue && to > c.EndDate) to = c.EndDate.Value.Date;
            var symbol = Symbol(c.BranchId);
            var inv = new Invoice
            {
                Symbol = symbol, Number = await NextInvoiceNumberAsync(symbol), InvoiceDate = from, DueDate = from.AddDays(15),
                CustomerId = c.CustomerId, ContractId = c.Id, OwnerId = c.OwnerId, VatPercent = c.VatPercent, Status = InvoiceStatus.Draft,
                Notes = I18n.T("Hoá đơn định kỳ tự lập theo hợp đồng {0}.", c.Number), CreatedAt = DateTime.Now
            };
            inv.Lines.Add(new InvoiceLine
            {
                Description = Cut(I18n.T("Phí dịch vụ kỳ {0} – {1} (HĐ {2})", from.ToDate(), to.ToDate(), c.Number), 300),
                Unit = I18n.T("Kỳ"), Quantity = 1, UnitPrice = c.RecurringAmount, SortOrder = 1
            });
            inv.Recalculate();
            _db.Invoices.Add(inv);
            c.NextBillingDate = from.AddMonths((int)c.BillingCycle);
            await _db.SaveChangesAsync(ct);
            _notify.Add(c.OwnerId, "renewal", I18n.T("Đã lập hoá đơn nháp định kỳ {0} cho hợp đồng {1}", inv.Number, c.Number), $"/Invoices/Details/{inv.Id}");
            invoices++;
        }
        if (invoices > 0) await _db.SaveChangesAsync(ct);
        return (renewals, invoices);
    }

    /// <summary>Gia hạn thủ công: hợp đồng nháp mới nối tiếp thời hạn, sao chép dòng hàng và điều khoản</summary>
    public async Task<Contract> RenewAsync(Contract src, int actorId)
    {
        var term = src.StartDate.HasValue && src.EndDate.HasValue ? src.EndDate.Value - src.StartDate.Value : TimeSpan.FromDays(364);
        var start = (src.EndDate ?? DateTime.Today).Date.AddDays(1);
        var prefix = $"HD-{DateTime.Today:yyyy}-";
        var n = await _db.Contracts.IgnoreQueryFilters().CountAsync(x => x.Number.StartsWith(prefix)) + 1;
        string no;
        do no = $"{prefix}{n++:0000}"; while (await _db.Contracts.IgnoreQueryFilters().AnyAsync(x => x.Number == no));
        var c = new Contract
        {
            Number = no, Title = Cut(src.Title.Contains(I18n.T("(gia hạn)")) ? src.Title : src.Title + " " + I18n.T("(gia hạn)"), 200),
            CustomerId = src.CustomerId, ContactId = src.ContactId, OpportunityId = src.RenewalOpportunityId ?? src.OpportunityId,
            StartDate = start, EndDate = start.Add(term), VatPercent = src.VatPercent, PaymentTerms = src.PaymentTerms, Notes = src.Notes,
            OwnerId = src.OwnerId ?? actorId, Status = ContractStatus.Draft, BillingCycle = src.BillingCycle, RecurringAmount = src.RecurringAmount,
            NextBillingDate = src.BillingCycle != BillingCycle.None ? start : null, RenewalNoticeDays = src.RenewalNoticeDays, RenewedFromId = src.Id,
            CreatedAt = DateTime.Now,
            Lines = src.Lines.OrderBy(l => l.SortOrder).Select(l => new ContractLine
            {
                ProductId = l.ProductId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, UnitPrice = l.UnitPrice, SortOrder = l.SortOrder
            }).ToList()
        };
        c.Recalculate();
        _db.Contracts.Add(c);
        await _db.SaveChangesAsync();
        return c;
    }

    public async Task<RecurringStats> StatsAsync(IQueryable<Contract> visible)
    {
        var today = DateTime.Today;
        var active = await visible.Where(c => c.Status == ContractStatus.Active && c.BillingCycle != BillingCycle.None && c.RecurringAmount > 0)
            .Select(c => new { c.RecurringAmount, c.BillingCycle }).ToListAsync();
        var s = new RecurringStats
        {
            RecurringContracts = active.Count,
            Mrr = Math.Round(active.Sum(c => c.RecurringAmount / (int)c.BillingCycle), 0)
        };
        var exp = visible.Where(c => c.Status == ContractStatus.Active && c.EndDate >= today && c.EndDate <= today.AddDays(90));
        s.ExpiringIn90 = await exp.CountAsync();
        s.ExpiringValue = await exp.SumAsync(c => (decimal?)c.SubTotal) ?? 0;
        var ended = await visible.Where(c => c.EndDate >= today.AddMonths(-12) && c.EndDate < today && c.Status != ContractStatus.Cancelled && c.Status != ContractStatus.Draft)
            .Select(c => c.Id).ToListAsync();
        s.EndedLast12 = ended.Count;
        s.RenewedLast12 = ended.Count == 0 ? 0 : await _db.Contracts.IgnoreQueryFilters()
            .Where(c => c.RenewedFromId != null && ended.Contains(c.RenewedFromId.Value) && c.Status != ContractStatus.Cancelled)
            .Select(c => c.RenewedFromId).Distinct().CountAsync();
        return s;
    }

    private string Symbol(int branchId)
    {
        var snap = _dir.Get();
        foreach (var id in snap.Ancestors(branchId))
            if (snap.ById.TryGetValue(id, out var b) && !string.IsNullOrWhiteSpace(b.InvoiceSymbol)) return b.InvoiceSymbol!;
        return _config["Invoice:Symbol"] is { Length: > 0 } s ? s : $"1C{DateTime.Today:yy}TCR";
    }

    private async Task<string> NextInvoiceNumberAsync(string symbol)
    {
        var numbers = await _db.Invoices.IgnoreQueryFilters().Where(i => i.Symbol == symbol).Select(i => i.Number).ToListAsync();
        var max = numbers.Select(n => long.TryParse(n, out var v) ? v : 0).DefaultIfEmpty(0).Max();
        return (max + 1).ToString("00000000");
    }

    private static string Cut(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;
}
