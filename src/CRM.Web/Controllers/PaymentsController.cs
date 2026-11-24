using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>
/// Phiếu thu: ghi nhận khách hàng thanh toán, tự cập nhật số đã thu + trạng thái hoá đơn.
/// Phiếu thu không sửa (theo nguyên tắc chứng từ); ghi sai thì quản lý xóa và lập lại.
/// </summary>
public class PaymentsController : CrmControllerBase
{
    private readonly LoyaltyService _loyalty;
    public PaymentsController(CrmDbContext db, LoyaltyService loyalty) : base(db) => _loyalty = loyalty;

    public async Task<IActionResult> Index(PaymentFilter f)
    {
        var q = Db.Payments.VisibleTo(User);
        if (User.SeesAll() && f.OwnerId.HasValue) q = q.Where(x => x.OwnerId == f.OwnerId);
        if (f.CustomerId.HasValue) q = q.Where(x => x.CustomerId == f.CustomerId);
        if (f.From.HasValue) q = q.Where(x => x.PaymentDate >= f.From.Value.Date);
        if (f.To.HasValue) q = q.Where(x => x.PaymentDate < f.To.Value.Date.AddDays(1));
        if (!string.IsNullOrWhiteSpace(f.Method)) q = q.Where(x => x.Method == f.Method);
        if (!string.IsNullOrWhiteSpace(f.Keyword))
            q = q.Where(x => x.Number.Contains(f.Keyword) || (x.Reference != null && x.Reference.Contains(f.Keyword))
                || (x.Customer != null && x.Customer.Name.Contains(f.Keyword)) || (x.Invoice != null && x.Invoice.Number.Contains(f.Keyword)));

        var total = await q.CountAsync();
        ViewBag.SumAmount = await q.SumAsync(x => (decimal?)x.Amount) ?? 0;
        var items = await q.Include(x => x.Customer).Include(x => x.Invoice).Include(x => x.Contract).Include(x => x.Owner).AsNoTracking()
            .OrderByDescending(x => x.PaymentDate).ThenByDescending(x => x.Id)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();

        // Tổng quan công nợ phải thu (theo quyền xem)
        var open = Db.Invoices.VisibleTo(User).Where(i => i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid);
        ViewBag.Receivable = await open.SumAsync(i => (decimal?)(i.Total - i.PaidAmount)) ?? 0;
        ViewBag.Overdue = await open.Where(i => i.DueDate != null && i.DueDate < DateTime.Today).SumAsync(i => (decimal?)(i.Total - i.PaidAmount)) ?? 0;
        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        ViewBag.ThisMonth = await Db.Payments.VisibleTo(User).Where(p => p.PaymentDate >= monthStart).SumAsync(p => (decimal?)p.Amount) ?? 0;

        ViewBag.Filter = f;
        await LoadOwnersAsync(f.OwnerId);
        await LoadCustomersAsync(f.CustomerId);
        return View(new PagedList<Payment>(items, total, f.Page, f.PageSize));
    }

    /// <summary>Phiếu thu (in được)</summary>
    public async Task<IActionResult> Details(int id)
    {
        var p = await Db.Payments.Include(x => x.Customer).Include(x => x.Invoice).Include(x => x.Contract).Include(x => x.Owner)
            .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (!User.CanAccess(p)) return NotAllowed();
        return View(p);
    }

    public async Task<IActionResult> Create(int? invoiceId, int? contractId, int? customerId)
    {
        var p = new Payment { CustomerId = customerId, ContractId = contractId, InvoiceId = invoiceId, Method = "Chuyển khoản" };
        if (invoiceId.HasValue)
        {
            var inv = await Db.Invoices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == invoiceId);
            if (inv == null) return NotFound();
            if (!User.CanAccess(inv)) return NotAllowed();
            if (!inv.CanReceivePayment)
            {
                TempData["Error"] = I18n.T("Hoá đơn này không ở trạng thái chờ thanh toán.");
                return RedirectToAction("Details", "Invoices", new { id = inv.Id });
            }
            p.CustomerId = inv.CustomerId; p.ContractId = inv.ContractId; p.Amount = inv.Outstanding;
            p.Notes = I18n.T("Thanh toán hoá đơn {0}", inv.FullNumber);
        }
        else if (contractId.HasValue)
        {
            var c = await Db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId);
            if (c == null) return NotFound();
            if (!User.CanAccess(c)) return NotAllowed();
            p.CustomerId = c.CustomerId;
            p.Notes = I18n.T("Thanh toán theo hợp đồng {0}", c.Number);
        }
        await LoadLookups(p);
        return View("Form", p);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Payment model)
    {
        ModelState.Remove(nameof(model.Number));
        Invoice? inv = null;
        if (model.InvoiceId.HasValue)
        {
            inv = await Db.Invoices.FirstOrDefaultAsync(x => x.Id == model.InvoiceId);
            if (inv == null || !User.CanAccess(inv)) ModelState.AddModelError(nameof(model.InvoiceId), I18n.T("Hoá đơn không hợp lệ"));
            else
            {
                if (inv.CustomerId != model.CustomerId) ModelState.AddModelError(nameof(model.InvoiceId), I18n.T("Hoá đơn không thuộc khách hàng đã chọn"));
                if (!inv.CanReceivePayment) ModelState.AddModelError(nameof(model.InvoiceId), I18n.T("Hoá đơn này không ở trạng thái chờ thanh toán."));
                else if (model.Amount > inv.Outstanding)
                    ModelState.AddModelError(nameof(model.Amount), I18n.T("Số tiền vượt quá số còn phải thu của hoá đơn ({0}).", inv.Outstanding.ToMoney()));
                model.ContractId = inv.ContractId;
            }
        }
        if (model.ContractId.HasValue && inv == null)
        {
            var c = await Db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == model.ContractId);
            if (c == null || c.CustomerId != model.CustomerId || !User.CanAccess(c))
                ModelState.AddModelError(nameof(model.ContractId), I18n.T("Hợp đồng không thuộc khách hàng đã chọn"));
        }
        if (!ModelState.IsValid)
        {
            await LoadLookups(model);
            return View("Form", model);
        }

        var p = new Payment
        {
            Number = await NextNumberAsync(), PaymentDate = model.PaymentDate.Date, CustomerId = model.CustomerId, ContractId = model.ContractId,
            InvoiceId = model.InvoiceId, Amount = model.Amount, Method = model.Method.Trim(), Reference = model.Reference, Notes = model.Notes,
            OwnerId = CurrentUserId, CreatedAt = DateTime.Now
        };
        Db.Payments.Add(p);
        await Db.SaveChangesAsync();
        if (inv != null) await RefreshInvoiceAsync(inv.Id);
        var earned = await _loyalty.EarnForPaymentAsync(p);

        TempData["Success"] = I18n.T("Đã ghi nhận phiếu thu {0}: {1}.", p.Number, p.Amount.ToMoney()) + (earned > 0 ? " " + I18n.T("Tích {0} điểm thưởng cho khách hàng.", earned) : "");
        return inv != null ? RedirectToAction("Details", "Invoices", new { id = inv.Id }) : RedirectToAction(nameof(Details), new { id = p.Id });
    }

    /// <summary>Xóa phiếu thu (ghi nhận sai) – chỉ quản lý; số đã thu của hoá đơn được tính lại.</summary>
    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var p = await Db.Payments.FindAsync(id);
        if (p == null) return NotFound();
        var invoiceId = p.InvoiceId;
        Db.Payments.Remove(p);
        await Db.SaveChangesAsync();
        await DeleteAttachmentsAsync(AttachmentTarget.Payment, p.Id);
        await _loyalty.ReverseForPaymentAsync(p.Id);
        if (invoiceId.HasValue) await RefreshInvoiceAsync(invoiceId.Value);
        TempData["Success"] = I18n.T("Đã xóa phiếu thu {0}.", p.Number);
        return RedirectToAction(nameof(Index));
    }

    private async Task RefreshInvoiceAsync(int invoiceId)
    {
        var inv = await Db.Invoices.FindAsync(invoiceId);
        if (inv == null) return;
        inv.ApplyPayments(await Db.Payments.Where(x => x.InvoiceId == invoiceId).SumAsync(x => (decimal?)x.Amount) ?? 0);
        await Db.SaveChangesAsync();
    }

    private async Task<string> NextNumberAsync()
    {
        var prefix = $"PT-{DateTime.Today:yyyy}-";
        var n = await Db.Payments.IgnoreQueryFilters().CountAsync(x => x.Number.StartsWith(prefix)) + 1;
        string no;
        do no = $"{prefix}{n++:0000}"; while (await Db.Payments.IgnoreQueryFilters().AnyAsync(x => x.Number == no));
        return no;
    }

    private async Task LoadLookups(Payment p)
    {
        await LoadCustomersAsync(p.CustomerId);
        var contracts = Db.Contracts.VisibleTo(User).Where(c => c.Status != ContractStatus.Draft && c.Status != ContractStatus.Cancelled);
        if (p.CustomerId.HasValue) contracts = contracts.Where(c => c.CustomerId == p.CustomerId);
        ViewBag.Contracts = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(
            await contracts.OrderByDescending(c => c.SignDate).Select(c => new { c.Id, Name = c.Number + " – " + c.Title }).ToListAsync(), "Id", "Name", p.ContractId);
        var invoices = Db.Invoices.VisibleTo(User).Where(i => i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid || i.Id == p.InvoiceId);
        if (p.CustomerId.HasValue) invoices = invoices.Where(i => i.CustomerId == p.CustomerId);
        ViewBag.Invoices = (await invoices.OrderBy(i => i.DueDate).Select(i => new { i.Id, i.Symbol, i.Number, Outstanding = i.Total - i.PaidAmount }).ToListAsync())
            .Select(i => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem($"{i.Symbol} – {i.Number} ({I18n.T("còn {0}", i.Outstanding.ToMoney())})", i.Id.ToString(), i.Id == p.InvoiceId))
            .ToList();
    }
}
