using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>
/// Hoá đơn bán hàng. Nháp → Phát hành → (thanh toán một phần) → Đã thanh toán; hoặc Hủy (khi chưa thu tiền).
/// Có thể xuất theo đợt thanh toán của hợp đồng hoặc theo toàn bộ hợp đồng.
/// </summary>
public class InvoicesController : CrmControllerBase
{
    private readonly IConfiguration _config;
    public InvoicesController(CrmDbContext db, IConfiguration config) : base(db) => _config = config;

    public async Task<IActionResult> Index(InvoiceFilter f)
    {
        var baseQ = Db.Invoices.VisibleTo(User);
        if (User.SeesAll() && f.OwnerId.HasValue) baseQ = baseQ.Where(x => x.OwnerId == f.OwnerId);
        if (f.CustomerId.HasValue) baseQ = baseQ.Where(x => x.CustomerId == f.CustomerId);
        if (!string.IsNullOrWhiteSpace(f.Keyword))
            baseQ = baseQ.Where(x => x.Number.Contains(f.Keyword) || x.Symbol.Contains(f.Keyword)
                || (x.Customer != null && x.Customer.Name.Contains(f.Keyword)) || (x.Contract != null && x.Contract.Number.Contains(f.Keyword)));

        var today = DateTime.Today;
        var openQ = baseQ.Where(x => x.Status == InvoiceStatus.Issued || x.Status == InvoiceStatus.PartiallyPaid);
        var overdueQ = openQ.Where(x => x.DueDate != null && x.DueDate < today);
        ViewBag.Counts = new Dictionary<string, int>
        {
            ["open"] = await openQ.CountAsync(),
            ["overdue"] = await overdueQ.CountAsync(),
            ["paid"] = await baseQ.CountAsync(x => x.Status == InvoiceStatus.Paid),
            ["draft"] = await baseQ.CountAsync(x => x.Status == InvoiceStatus.Draft),
            ["cancelled"] = await baseQ.CountAsync(x => x.Status == InvoiceStatus.Cancelled),
            ["all"] = await baseQ.CountAsync()
        };
        ViewBag.Outstanding = await openQ.SumAsync(x => x.Total - x.PaidAmount);
        ViewBag.OverdueAmount = await overdueQ.SumAsync(x => x.Total - x.PaidAmount);

        var q = f.View switch
        {
            "overdue" => overdueQ,
            "paid" => baseQ.Where(x => x.Status == InvoiceStatus.Paid),
            "draft" => baseQ.Where(x => x.Status == InvoiceStatus.Draft),
            "cancelled" => baseQ.Where(x => x.Status == InvoiceStatus.Cancelled),
            "all" => baseQ,
            _ => openQ
        };
        var total = await q.CountAsync();
        var items = await q.Include(x => x.Customer).Include(x => x.Contract).Include(x => x.Owner).AsNoTracking()
            .OrderBy(x => f.View == "open" || f.View == "overdue" ? x.DueDate : null).ThenByDescending(x => x.InvoiceDate).ThenByDescending(x => x.Id)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();

        ViewBag.Filter = f;
        await LoadOwnersAsync(f.OwnerId);
        await LoadCustomersAsync(f.CustomerId);
        return View(new PagedList<Invoice>(items, total, f.Page, f.PageSize));
    }

    public async Task<IActionResult> Details(int id)
    {
        var inv = await Db.Invoices.Include(x => x.Customer).Include(x => x.Contract).Include(x => x.Milestone).Include(x => x.Owner)
            .Include(x => x.Lines).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (inv == null) return NotFound();
        if (!User.CanAccess(inv)) return NotAllowed();
        inv.Lines = inv.Lines.OrderBy(l => l.SortOrder).ToList();
        ViewBag.Payments = await Db.Payments.Include(p => p.Owner).Where(p => p.InvoiceId == id).OrderBy(p => p.PaymentDate).ToListAsync();
        return View(inv);
    }

    /// <summary>Lập hoá đơn: theo đợt thanh toán (milestoneId), theo toàn bộ hợp đồng (contractId) hoặc hoá đơn lẻ.</summary>
    public async Task<IActionResult> Create(int? contractId, int? milestoneId, int? customerId)
    {
        var vm = new InvoiceFormViewModel { Symbol = DefaultSymbol(), CustomerId = customerId };

        if (milestoneId.HasValue)
        {
            var m = await Db.ContractMilestones.Include(x => x.Contract).AsNoTracking().FirstOrDefaultAsync(x => x.Id == milestoneId);
            if (m?.Contract == null) return NotFound();
            contractId = m.ContractId;
            vm.MilestoneId = m.Id; vm.MilestoneName = m.Name;
            vm.DueDate = m.DueDate.HasValue && m.DueDate.Value > DateTime.Today ? m.DueDate : DateTime.Today.AddDays(15);
        }
        if (contractId.HasValue)
        {
            var c = await Db.Contracts.Include(x => x.Lines).Include(x => x.Milestones).AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId);
            if (c == null) return NotFound();
            if (!User.CanAccess(c)) return NotAllowed();
            if (!c.IsSigned || c.Status == ContractStatus.Liquidated)
            {
                TempData["Error"] = I18n.T("Chỉ xuất hoá đơn cho hợp đồng đã ký và chưa thanh lý.");
                return RedirectToAction("Details", "Contracts", new { id = c.Id });
            }
            vm.ContractId = c.Id; vm.CustomerId = c.CustomerId; vm.VatPercent = Math.Round(c.VatPercent, 0);
            vm.Symbol = DefaultSymbol(c.BranchId);   // ký hiệu hoá đơn của pháp nhân ký hợp đồng
            var m = c.Milestones.FirstOrDefault(x => x.Id == vm.MilestoneId);
            if (m != null)
            {
                // Một dòng cho đợt thanh toán: giá trị trước thuế = tỷ lệ đợt × giá trị hợp đồng trước thuế
                vm.Lines.Add(new SalesLineInput
                {
                    Description = I18n.T("{0} theo hợp đồng số {1}", m.Name, c.Number),
                    Quantity = 1, UnitPrice = Math.Round(c.SubTotal * m.Percent / 100m, 0)
                });
                vm.Notes = m.Condition;
            }
            else
            {
                vm.Lines = c.Lines.OrderBy(l => l.SortOrder).Select(l => new SalesLineInput
                {
                    ProductId = l.ProductId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, UnitPrice = l.UnitPrice
                }).ToList();
            }
            var already = await Db.Invoices.Where(i => i.ContractId == c.Id && i.Status != InvoiceStatus.Cancelled).SumAsync(i => (decimal?)i.Total) ?? 0;
            if (already > 0) ViewBag.Warning = I18n.T("Hợp đồng đã xuất hoá đơn {0} / {1}.", already.ToMoney(), c.Total.ToMoney());
        }
        if (vm.Lines.Count == 0) vm.Lines.Add(new SalesLineInput());
        await LoadLookups(vm);
        return View("Form", vm);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var inv = await Db.Invoices.Include(x => x.Lines).Include(x => x.Milestone).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (inv == null) return NotFound();
        if (!User.CanAccess(inv)) return NotAllowed();
        if (!inv.IsEditable)
        {
            TempData["Error"] = I18n.T("Chỉ sửa được hoá đơn nháp.");
            return RedirectToAction(nameof(Details), new { id });
        }
        var vm = new InvoiceFormViewModel
        {
            Id = inv.Id, Symbol = inv.Symbol, Number = inv.Number, InvoiceDate = inv.InvoiceDate, DueDate = inv.DueDate,
            CustomerId = inv.CustomerId, ContractId = inv.ContractId, MilestoneId = inv.MilestoneId, MilestoneName = inv.Milestone?.Name,
            VatPercent = Math.Round(inv.VatPercent, 0), Notes = inv.Notes,
            Lines = inv.Lines.OrderBy(l => l.SortOrder).Select(l => new SalesLineInput
            {
                ProductId = l.ProductId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, UnitPrice = l.UnitPrice
            }).ToList()
        };
        if (vm.Lines.Count == 0) vm.Lines.Add(new SalesLineInput());
        await LoadLookups(vm);
        return View("Form", vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(InvoiceFormViewModel vm)
    {
        vm.Lines = vm.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Description) || l.ProductId.HasValue).ToList();
        vm.Symbol = (vm.Symbol ?? "").Trim().ToUpperInvariant();
        ModelState.Clear();
        TryValidateModel(vm);
        if (vm.Lines.Count == 0) ModelState.AddModelError(string.Empty, I18n.T("Hoá đơn cần ít nhất một dòng hàng hoá / dịch vụ."));
        if (vm.DueDate.HasValue && vm.DueDate < vm.InvoiceDate) ModelState.AddModelError(nameof(vm.DueDate), I18n.T("Hạn thanh toán phải sau ngày hoá đơn"));
        if (vm.ContractId.HasValue)
        {
            var c = await Db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == vm.ContractId);
            if (c == null || c.CustomerId != vm.CustomerId) ModelState.AddModelError(nameof(vm.ContractId), I18n.T("Hợp đồng không thuộc khách hàng đã chọn"));
        }
        if (!ModelState.IsValid)
        {
            if (vm.Lines.Count == 0) vm.Lines.Add(new SalesLineInput());
            await LoadLookups(vm);
            return View("Form", vm);
        }

        Invoice inv;
        if (vm.Id == 0)
        {
            inv = new Invoice { Symbol = vm.Symbol, Number = await NextNumberAsync(vm.Symbol), OwnerId = CurrentUserId, CreatedAt = DateTime.Now, MilestoneId = vm.MilestoneId };
            Db.Invoices.Add(inv);
        }
        else
        {
            inv = await Db.Invoices.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == vm.Id) ?? throw new InvalidOperationException();
            if (!User.CanAccess(inv)) return NotAllowed();
            if (!inv.IsEditable) return BadRequest();
            if (inv.Symbol != vm.Symbol) { inv.Symbol = vm.Symbol; inv.Number = await NextNumberAsync(vm.Symbol); }
            Db.InvoiceLines.RemoveRange(inv.Lines);
            inv.Lines.Clear();
        }
        inv.InvoiceDate = vm.InvoiceDate; inv.DueDate = vm.DueDate; inv.CustomerId = vm.CustomerId; inv.ContractId = vm.ContractId;
        if (vm.ContractId == null) inv.MilestoneId = null;
        inv.VatPercent = vm.VatPercent; inv.Notes = vm.Notes;
        var sort = 1;
        foreach (var l in vm.Lines)
            inv.Lines.Add(new InvoiceLine { ProductId = l.ProductId, Description = l.Description.Trim(), Unit = l.Unit, Quantity = l.Quantity, UnitPrice = l.UnitPrice, SortOrder = sort++ });
        inv.Recalculate();

        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu hoá đơn {0}.", inv.FullNumber);
        return RedirectToAction(nameof(Details), new { id = inv.Id });
    }

    /// <summary>Phát hành hoá đơn → phát sinh công nợ phải thu</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Issue(int id)
    {
        var inv = await Db.Invoices.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (inv == null) return NotFound();
        if (!User.CanAccess(inv)) return NotAllowed();
        if (!inv.IsEditable || inv.Lines.Count == 0) return BadRequest();
        inv.Status = InvoiceStatus.Issued;
        inv.IssuedAt = DateTime.Now;
        inv.ApplyPayments(await Db.Payments.Where(p => p.InvoiceId == id).SumAsync(p => (decimal?)p.Amount) ?? 0);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã phát hành hoá đơn {0}.", inv.FullNumber);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Hủy hoá đơn đã phát hành (chưa có thanh toán) – chỉ quản lý</summary>
    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        var inv = await Db.Invoices.FindAsync(id);
        if (inv == null) return NotFound();
        if (inv.Status == InvoiceStatus.Cancelled) return BadRequest();
        if (await Db.Payments.AnyAsync(p => p.InvoiceId == id))
        {
            TempData["Error"] = I18n.T("Hoá đơn đã có thanh toán, hãy xóa phiếu thu trước khi hủy.");
            return RedirectToAction(nameof(Details), new { id });
        }
        inv.Status = InvoiceStatus.Cancelled;
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã hủy hoá đơn {0}.", inv.FullNumber);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var inv = await Db.Invoices.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (inv == null) return NotFound();
        if (!User.CanAccess(inv)) return NotAllowed();
        if (!inv.IsEditable)
        {
            TempData["Error"] = I18n.T("Chỉ xóa được hoá đơn nháp.");
            return RedirectToAction(nameof(Details), new { id });
        }
        Db.InvoiceLines.RemoveRange(inv.Lines);
        Db.Invoices.Remove(inv);
        await Db.SaveChangesAsync();
        await DeleteAttachmentsAsync(AttachmentTarget.Invoice, inv.Id);
        TempData["Success"] = I18n.T("Đã xóa hoá đơn {0}.", inv.FullNumber);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Danh sách hoá đơn còn nợ của khách hàng (cho form phiếu thu)</summary>
    [HttpGet]
    public async Task<IActionResult> Open(int customerId)
        => Json(await Db.Invoices.VisibleTo(User)
            .Where(i => i.CustomerId == customerId && (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid))
            .OrderBy(i => i.DueDate).ThenBy(i => i.InvoiceDate)
            .Select(i => new { id = i.Id, name = i.Symbol + " – " + i.Number, outstanding = i.Total - i.PaidAmount, contractId = i.ContractId })
            .ToListAsync());

    /// <summary>Ký hiệu hoá đơn mặc định: của pháp nhân (công ty gần nhất) thuộc đơn vị; không có thì theo cấu hình</summary>
    private string DefaultSymbol(int? branchId = null)
    {
        var scope = HttpContext.RequestServices.GetRequiredService<BranchScope>();
        var id = branchId ?? scope.WorkingBranchId;
        if (id.HasValue)
        {
            var snap = scope.Snapshot;
            var seen = new HashSet<int>();
            for (var b = snap.ById.GetValueOrDefault(id.Value); b != null && seen.Add(b.Id); b = b.ParentId.HasValue ? snap.ById.GetValueOrDefault(b.ParentId.Value) : null)
                if (!string.IsNullOrWhiteSpace(b.InvoiceSymbol)) return b.InvoiceSymbol!;
        }
        return _config["Invoice:Symbol"] is { Length: > 0 } s ? s : $"1C{DateTime.Today:yy}TCR";
    }

    /// <summary>Số hoá đơn 8 chữ số, tăng dần theo ký hiệu</summary>
    private async Task<string> NextNumberAsync(string symbol)
    {
        var numbers = await Db.Invoices.IgnoreQueryFilters().Where(i => i.Symbol == symbol).Select(i => i.Number).ToListAsync();
        var max = numbers.Select(n => long.TryParse(n, out var v) ? v : 0).DefaultIfEmpty(0).Max();
        return (max + 1).ToString("00000000");
    }

    private async Task LoadLookups(InvoiceFormViewModel vm)
    {
        await LoadCustomersAsync(vm.CustomerId);
        var contracts = Db.Contracts.VisibleTo(User).Where(c => c.Status == ContractStatus.Active || c.Status == ContractStatus.Completed || c.Id == vm.ContractId);
        if (vm.CustomerId.HasValue) contracts = contracts.Where(c => c.CustomerId == vm.CustomerId);
        ViewBag.Contracts = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(
            await contracts.OrderByDescending(c => c.SignDate).Select(c => new { c.Id, Name = c.Number + " – " + c.Title }).ToListAsync(), "Id", "Name", vm.ContractId);
        ViewBag.ProductsJson = await ContractsController.ProductsJsonAsync(Db);
    }
}
