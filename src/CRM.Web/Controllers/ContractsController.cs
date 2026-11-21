using System.Text.Json;
using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>
/// Hợp đồng bán hàng. Nháp → Ký (Đang thực hiện) → Hoàn thành → Thanh lý; hoặc Hủy.
/// Lịch thanh toán theo đợt (% tổng giá trị gồm thuế) là căn cứ xuất hoá đơn và theo dõi công nợ.
/// </summary>
public class ContractsController : CrmControllerBase
{
    public ContractsController(CrmDbContext db) : base(db) { }

    public async Task<IActionResult> Index(ContractFilter f)
    {
        var baseQ = Db.Contracts.VisibleTo(User);
        if (User.SeesAll() && f.OwnerId.HasValue) baseQ = baseQ.Where(x => x.OwnerId == f.OwnerId);
        if (!string.IsNullOrWhiteSpace(f.Keyword))
            baseQ = baseQ.Where(x => x.Number.Contains(f.Keyword) || x.Title.Contains(f.Keyword)
                || (x.Customer != null && x.Customer.Name.Contains(f.Keyword)));
        ViewBag.Counts = await baseQ.GroupBy(x => x.Status).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        var q = f.Status.HasValue ? baseQ.Where(x => x.Status == f.Status) : baseQ;
        var total = await q.CountAsync();
        var items = await q.Include(x => x.Customer).Include(x => x.Owner).AsNoTracking()
            .OrderByDescending(x => x.SignDate ?? x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();
        var ids = items.Select(x => x.Id).ToList();
        ViewBag.Paid = await Db.Payments.Where(p => p.ContractId != null && ids.Contains(p.ContractId.Value))
            .GroupBy(p => p.ContractId!.Value).Select(g => new { g.Key, Sum = g.Sum(p => p.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum);
        ViewBag.SumTotal = await q.Where(x => x.Status != ContractStatus.Cancelled).SumAsync(x => x.Total);

        ViewBag.Filter = f;
        await LoadOwnersAsync(f.OwnerId);
        return View(new PagedList<Contract>(items, total, f.Page, f.PageSize));
    }

    public async Task<IActionResult> Details(int id)
    {
        var c = await Db.Contracts.Include(x => x.Customer).Include(x => x.Contact).Include(x => x.Opportunity).Include(x => x.Quote)
            .Include(x => x.BusinessPlan).Include(x => x.Owner).Include(x => x.Lines).Include(x => x.Milestones)
            .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        c.Lines = c.Lines.OrderBy(l => l.SortOrder).ToList();
        c.Milestones = c.Milestones.OrderBy(m => m.SortOrder).ToList();
        return View(new ContractDetailViewModel
        {
            Contract = c,
            Invoices = await Db.Invoices.Where(i => i.ContractId == id).OrderBy(i => i.InvoiceDate).ThenBy(i => i.Id).ToListAsync(),
            Payments = await Db.Payments.Include(p => p.Invoice).Where(p => p.ContractId == id).OrderBy(p => p.PaymentDate).ToListAsync()
        });
    }

    /// <summary>Lập hợp đồng; có thể lập từ PAKD đã duyệt, từ báo giá hoặc từ cơ hội.</summary>
    /// <summary>Tải / xem file PDF hợp đồng</summary>
    public async Task<IActionResult> Pdf(int id, bool inline = false)
    {
        var c = await Db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        return await PdfFileAsync(nameof(Contract), id, inline);
    }

    public async Task<IActionResult> Create(int? businessPlanId, int? quoteId, int? opportunityId, int? customerId)
    {
        var vm = new ContractFormViewModel { Number = await NextNumberAsync(), CustomerId = customerId, OpportunityId = opportunityId };

        if (businessPlanId.HasValue)
        {
            var p = await Db.BusinessPlans.Include(x => x.Lines).Include(x => x.Opportunity).AsNoTracking().FirstOrDefaultAsync(x => x.Id == businessPlanId);
            if (p == null || !User.CanAccess(p)) return NotFound();
            if (p.Status != BusinessPlanStatus.Approved)
            {
                TempData["Error"] = I18n.T("Chỉ lập hợp đồng từ phương án kinh doanh đã được duyệt.");
                return RedirectToAction("Details", "BusinessPlans", new { id = p.Id });
            }
            vm.BusinessPlanId = p.Id; vm.QuoteId = p.QuoteId; vm.CustomerId = p.CustomerId; vm.OpportunityId = p.OpportunityId;
            vm.Title = p.Opportunity?.Name ?? p.Title;
            vm.SourceText = I18n.T("Lập từ phương án kinh doanh {0}", p.Number);
            vm.Lines = p.Lines.OrderBy(l => l.SortOrder).Select(l => new SalesLineInput
            {
                ProductId = l.ProductId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, UnitPrice = l.SalePrice
            }).ToList();
        }
        else if (quoteId.HasValue)
        {
            var q = await Db.Quotes.Include(x => x.Lines).Include(x => x.Opportunity).AsNoTracking().FirstOrDefaultAsync(x => x.Id == quoteId);
            if (q == null || !User.CanAccess(q)) return NotFound();
            vm.QuoteId = q.Id; vm.CustomerId = q.CustomerId; vm.ContactId = q.ContactId; vm.OpportunityId = q.OpportunityId;
            vm.VatPercent = Math.Round(q.VatPercent, 0);
            vm.Title = q.Opportunity?.Name ?? I18n.T("Hợp đồng theo báo giá {0}", q.Number);
            vm.SourceText = I18n.T("Lập từ báo giá {0}", q.Number);
            var factor = 1 - q.DiscountPercent / 100m;
            vm.Lines = q.Lines.OrderBy(l => l.SortOrder).Select(l => new SalesLineInput
            {
                ProductId = l.ProductId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity,
                UnitPrice = Math.Round(l.UnitPrice * (1 - l.DiscountPercent / 100m) * factor, 0)
            }).ToList();
        }
        if (vm.OpportunityId.HasValue && string.IsNullOrEmpty(vm.Title))
        {
            var o = await Db.Opportunities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == vm.OpportunityId);
            if (o != null && User.CanAccess(o)) { vm.CustomerId = o.CustomerId; vm.ContactId ??= o.ContactId; vm.Title = o.Name; }
        }
        if (vm.Lines.Count == 0) vm.Lines.Add(new SalesLineInput());
        // Lịch thanh toán mặc định: tạm ứng 50% sau ký, 50% còn lại sau nghiệm thu
        vm.Milestones.Add(new MilestoneInput { Name = I18n.T("Đợt 1 – Tạm ứng"), Percent = 50, DueDate = DateTime.Today.AddDays(7), Condition = I18n.T("Trong 7 ngày kể từ ngày ký hợp đồng") });
        vm.Milestones.Add(new MilestoneInput { Name = I18n.T("Đợt 2 – Thanh toán còn lại"), Percent = 50, DueDate = DateTime.Today.AddMonths(3), Condition = I18n.T("Sau khi nghiệm thu, bàn giao") });
        vm.PaymentTerms = I18n.T("Thanh toán bằng chuyển khoản theo các đợt nêu trên, trong vòng 15 ngày kể từ ngày nhận hoá đơn hợp lệ.");
        await LoadLookups(vm);
        return View("Form", vm);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var c = await Db.Contracts.Include(x => x.Lines).Include(x => x.Milestones).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        if (!c.IsEditable)
        {
            TempData["Error"] = I18n.T("Chỉ sửa được hợp đồng ở trạng thái Nháp.");
            return RedirectToAction(nameof(Details), new { id });
        }
        var vm = new ContractFormViewModel
        {
            Id = c.Id, Number = c.Number, Title = c.Title, CustomerId = c.CustomerId, ContactId = c.ContactId, OpportunityId = c.OpportunityId,
            QuoteId = c.QuoteId, BusinessPlanId = c.BusinessPlanId, StartDate = c.StartDate, EndDate = c.EndDate,
            BillingCycle = c.BillingCycle, RecurringAmount = c.RecurringAmount, NextBillingDate = c.NextBillingDate, RenewalNoticeDays = c.RenewalNoticeDays, SlaPolicyId = c.SlaPolicyId,
            VatPercent = Math.Round(c.VatPercent, 0), PaymentTerms = c.PaymentTerms, Notes = c.Notes,
            Lines = c.Lines.OrderBy(l => l.SortOrder).Select(l => new SalesLineInput
            {
                ProductId = l.ProductId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, UnitPrice = l.UnitPrice
            }).ToList(),
            Milestones = c.Milestones.OrderBy(m => m.SortOrder).Select(m => new MilestoneInput
            {
                Id = m.Id, Name = m.Name, Percent = m.Percent, DueDate = m.DueDate, Condition = m.Condition
            }).ToList()
        };
        if (vm.Lines.Count == 0) vm.Lines.Add(new SalesLineInput());
        await LoadLookups(vm);
        return View("Form", vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(ContractFormViewModel vm)
    {
        vm.Lines = vm.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Description) || l.ProductId.HasValue).ToList();
        vm.Milestones = vm.Milestones.Where(m => !string.IsNullOrWhiteSpace(m.Name) || m.Percent > 0).ToList();
        ModelState.Clear();
        TryValidateModel(vm);
        if (vm.Lines.Count == 0) ModelState.AddModelError(string.Empty, I18n.T("Hợp đồng cần ít nhất một dòng hàng hoá / dịch vụ."));
        if (vm.Milestones.Count > 0 && Math.Abs(vm.Milestones.Sum(m => m.Percent) - 100) > 0.001m)
            ModelState.AddModelError(string.Empty, I18n.T("Tổng tỷ lệ các đợt thanh toán phải bằng 100% (hiện tại {0}%).", vm.Milestones.Sum(m => m.Percent).ToString("0.##")));
        if (vm.StartDate.HasValue && vm.EndDate.HasValue && vm.EndDate < vm.StartDate)
            ModelState.AddModelError(nameof(vm.EndDate), I18n.T("Ngày kết thúc phải sau ngày hiệu lực"));
        if (!ModelState.IsValid)
        {
            if (vm.Lines.Count == 0) vm.Lines.Add(new SalesLineInput());
            await LoadLookups(vm);
            return View("Form", vm);
        }

        Contract c;
        if (vm.Id == 0)
        {
            c = new Contract { Number = await NextNumberAsync(), OwnerId = CurrentUserId, CreatedAt = DateTime.Now };
            if (vm.OpportunityId.HasValue)
            {
                var o = await Db.Opportunities.FindAsync(vm.OpportunityId);
                if (o?.OwnerId != null) c.OwnerId = o.OwnerId;
            }
            Db.Contracts.Add(c);
        }
        else
        {
            c = await Db.Contracts.Include(x => x.Lines).Include(x => x.Milestones).FirstOrDefaultAsync(x => x.Id == vm.Id)
                ?? throw new InvalidOperationException();
            if (!User.CanAccess(c)) return NotAllowed();
            if (!c.IsEditable) return BadRequest();
            // Hợp đồng nháp chưa có hoá đơn nên có thể tạo lại các dòng / đợt thanh toán
            Db.ContractLines.RemoveRange(c.Lines);
            Db.ContractMilestones.RemoveRange(c.Milestones);
            c.Lines.Clear();
            c.Milestones.Clear();
        }

        c.Title = vm.Title.Trim(); c.CustomerId = vm.CustomerId; c.ContactId = vm.ContactId; c.OpportunityId = vm.OpportunityId;
        c.QuoteId = vm.QuoteId; c.BusinessPlanId = vm.BusinessPlanId; c.StartDate = vm.StartDate; c.EndDate = vm.EndDate;
        c.BillingCycle = vm.BillingCycle; c.RecurringAmount = vm.BillingCycle == BillingCycle.None ? 0 : vm.RecurringAmount; c.RenewalNoticeDays = vm.RenewalNoticeDays; c.SlaPolicyId = vm.SlaPolicyId;
        c.NextBillingDate = vm.BillingCycle == BillingCycle.None ? null : vm.NextBillingDate ?? vm.StartDate;
        c.VatPercent = vm.VatPercent; c.PaymentTerms = vm.PaymentTerms; c.Notes = vm.Notes;
        var sort = 1;
        foreach (var l in vm.Lines)
            c.Lines.Add(new ContractLine { ProductId = l.ProductId, Description = l.Description.Trim(), Unit = l.Unit, Quantity = l.Quantity, UnitPrice = l.UnitPrice, SortOrder = sort++ });
        sort = 1;
        foreach (var m in vm.Milestones)
            c.Milestones.Add(new ContractMilestone { Name = m.Name.Trim(), Percent = m.Percent, DueDate = m.DueDate, Condition = m.Condition, SortOrder = sort++ });
        c.Recalculate();

        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu hợp đồng {0}.", c.Number);
        return RedirectToAction(nameof(Details), new { id = c.Id });
    }

    /// <summary>Ký hợp đồng: chuyển Đang thực hiện; cơ hội liên quan chuyển Thắng.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Sign(int id, DateTime? signDate)
    {
        var c = await Db.Contracts.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        if (c.Status != ContractStatus.Draft || c.Lines.Count == 0) return BadRequest();
        c.Status = ContractStatus.Active;
        c.SignDate = (signDate ?? DateTime.Today).Date;
        c.StartDate ??= c.SignDate;

        if (c.OpportunityId.HasValue)
        {
            var o = await Db.Opportunities.FindAsync(c.OpportunityId);
            if (o != null && !o.IsClosed)
            {
                o.Stage = OpportunityStage.ClosedWon; o.Probability = 100; o.Amount = c.SubTotal; o.ClosedAt = DateTime.Now;
            }
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã ký hợp đồng {0}.", c.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Hoàn thành / Thanh lý / Hủy</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(int id, ContractStatus status)
    {
        var c = await Db.Contracts.FindAsync(id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();

        var allowed = (c.Status, status) switch
        {
            (ContractStatus.Active, ContractStatus.Completed) => true,
            (ContractStatus.Active or ContractStatus.Completed, ContractStatus.Liquidated) => true,
            (ContractStatus.Draft or ContractStatus.Active, ContractStatus.Cancelled) => true,
            _ => false
        };
        if (!allowed) return BadRequest();
        if (status == ContractStatus.Cancelled && await Db.Invoices.AnyAsync(i => i.ContractId == id && i.Status != InvoiceStatus.Cancelled && i.Status != InvoiceStatus.Draft))
        {
            TempData["Error"] = I18n.T("Hợp đồng đã có hoá đơn phát hành, hãy hủy hoá đơn trước khi hủy hợp đồng.");
            return RedirectToAction(nameof(Details), new { id });
        }
        c.Status = status;
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Hợp đồng {0}: {1}.", c.Number, status.GetDisplayName());
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await Db.Contracts.Include(x => x.Lines).Include(x => x.Milestones).FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        if (!c.IsEditable || await Db.Invoices.AnyAsync(i => i.ContractId == id) || await Db.Payments.AnyAsync(p => p.ContractId == id) || await Db.Tickets.AnyAsync(t => t.ContractId == id))
        {
            TempData["Error"] = I18n.T("Chỉ xóa được hợp đồng nháp chưa có hoá đơn / thanh toán.");
            return RedirectToAction(nameof(Details), new { id });
        }
        Db.ContractLines.RemoveRange(c.Lines);
        Db.ContractMilestones.RemoveRange(c.Milestones);
        Db.Contracts.Remove(c);
        await Db.SaveChangesAsync();
        await DeleteAttachmentsAsync(AttachmentTarget.Contract, c.Id);
        TempData["Success"] = I18n.T("Đã xóa hợp đồng {0}.", c.Number);
        return RedirectToAction(nameof(Index));
    }

    private async Task<string> NextNumberAsync()
    {
        var prefix = $"HD-{DateTime.Today:yyyy}-";
        var n = await Db.Contracts.IgnoreQueryFilters().CountAsync(x => x.Number.StartsWith(prefix)) + 1;
        string no;
        do no = $"{prefix}{n++:0000}"; while (await Db.Contracts.IgnoreQueryFilters().AnyAsync(x => x.Number == no));
        return no;
    }

    private async Task LoadLookups(ContractFormViewModel vm)
    {
        await LoadCustomersAsync(vm.CustomerId);
        await LoadSlaPoliciesAsync(vm.SlaPolicyId);
        await LoadContactsAsync(vm.CustomerId, vm.ContactId);
        await LoadOpportunitiesAsync(vm.CustomerId, vm.OpportunityId);
        ViewBag.ProductsJson = await ProductsJsonAsync(Db);
    }

    /// <summary>Danh sách hàng hoá đang bán cho bảng dòng (JSON)</summary>
    internal static async Task<string> ProductsJsonAsync(CrmDbContext db) =>
        JsonSerializer.Serialize(await db.Products.Where(p => p.IsActive).OrderBy(p => p.Code)
            .Select(p => new { id = p.Id, code = p.Code, name = p.Name, unit = p.Unit, price = p.UnitPrice, vat = p.VatPercent }).ToListAsync());
}
