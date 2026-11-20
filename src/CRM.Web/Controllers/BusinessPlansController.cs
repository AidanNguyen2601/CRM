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
/// Phương án kinh doanh (PAKD): lập giá vốn – giá bán – chi phí – lợi nhuận cho thương vụ và trình duyệt.
/// Quy trình: Nháp → Chờ duyệt → Đã duyệt / Bị từ chối (sửa lại rồi gửi duyệt lại). PAKD đã duyệt bị khóa.
/// </summary>
public class BusinessPlansController : CrmControllerBase
{
    public BusinessPlansController(CrmDbContext db) : base(db) { }

    public async Task<IActionResult> Index(BusinessPlanFilter f)
    {
        var baseQ = Db.BusinessPlans.VisibleTo(User);
        if (User.SeesAll() && f.OwnerId.HasValue) baseQ = baseQ.Where(x => x.OwnerId == f.OwnerId);
        if (!string.IsNullOrWhiteSpace(f.Keyword))
            baseQ = baseQ.Where(x => x.Number.Contains(f.Keyword) || x.Title.Contains(f.Keyword)
                || (x.Customer != null && x.Customer.Name.Contains(f.Keyword)));

        ViewBag.Counts = await baseQ.GroupBy(x => x.Status).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        var q = baseQ;
        if (f.Status.HasValue) q = q.Where(x => x.Status == f.Status);
        var total = await q.CountAsync();
        ViewBag.SumRevenue = await q.SumAsync(x => x.Revenue);
        ViewBag.SumProfit = await q.SumAsync(x => x.Profit);
        var items = await q.Include(x => x.Customer).Include(x => x.Owner).AsNoTracking()
            .OrderByDescending(x => x.PlanDate).ThenByDescending(x => x.Id)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();

        ViewBag.Filter = f;
        await LoadOwnersAsync(f.OwnerId);
        return View(new PagedList<BusinessPlan>(items, total, f.Page, f.PageSize));
    }

    public async Task<IActionResult> Details(int id)
    {
        var p = await Db.BusinessPlans.Include(x => x.Customer).Include(x => x.Opportunity).Include(x => x.Quote)
            .Include(x => x.Owner).Include(x => x.ApprovedBy).Include(x => x.Lines).Include(x => x.Costs)
            .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (!User.CanAccess(p)) return NotAllowed();
        p.Lines = p.Lines.OrderBy(l => l.SortOrder).ToList();
        p.Costs = p.Costs.OrderBy(c => c.SortOrder).ToList();
        return View(p);
    }

    /// <summary>Lập PAKD mới; có thể lập từ cơ hội hoặc từ báo giá (lấy sẵn các dòng hàng và giá bán).</summary>
    public async Task<IActionResult> Create(int? opportunityId, int? customerId, int? quoteId)
    {
        var vm = new BusinessPlanFormViewModel { Number = await NextNumberAsync(), CustomerId = customerId, OpportunityId = opportunityId };

        if (quoteId.HasValue)
        {
            var quote = await Db.Quotes.Include(x => x.Lines).Include(x => x.Opportunity).Include(x => x.Customer)
                .AsNoTracking().FirstOrDefaultAsync(x => x.Id == quoteId);
            if (quote != null && User.CanAccess(quote))
            {
                vm.QuoteId = quote.Id; vm.QuoteNumber = quote.Number;
                vm.CustomerId = quote.CustomerId; vm.OpportunityId = quote.OpportunityId;
                vm.Title = I18n.T("PAKD – {0}", quote.Opportunity?.Name ?? quote.Customer?.Name ?? quote.Number);
                // Giá bán trên PAKD = đơn giá sau chiết khấu dòng và chiết khấu chung của báo giá (trước thuế)
                var factor = 1 - quote.DiscountPercent / 100m;
                vm.Lines = quote.Lines.OrderBy(l => l.SortOrder).Select(l => new BusinessPlanLineInput
                {
                    ProductId = l.ProductId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity,
                    SalePrice = Math.Round(l.UnitPrice * (1 - l.DiscountPercent / 100m) * factor, 0)
                }).ToList();
                // Giá vốn lấy từ danh mục hàng hoá
                var ids = vm.Lines.Where(l => l.ProductId.HasValue).Select(l => l.ProductId!.Value).ToList();
                var costs = await Db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.CostPrice);
                foreach (var l in vm.Lines)
                    if (l.ProductId.HasValue && costs.TryGetValue(l.ProductId.Value, out var c)) l.CostPrice = c;
            }
        }
        if (vm.OpportunityId.HasValue && string.IsNullOrEmpty(vm.Title))
        {
            var o = await Db.Opportunities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == vm.OpportunityId);
            if (o != null && User.CanAccess(o)) { vm.CustomerId = o.CustomerId; vm.Title = I18n.T("PAKD – {0}", o.Name); }
        }
        if (vm.Lines.Count == 0) vm.Lines.Add(new BusinessPlanLineInput());
        await LoadLookups(vm);
        return View("Form", vm);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var p = await Db.BusinessPlans.Include(x => x.Lines).Include(x => x.Costs).Include(x => x.Quote)
            .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (!User.CanAccess(p)) return NotAllowed();
        if (!p.IsEditable)
        {
            TempData["Error"] = I18n.T("Chỉ sửa được phương án ở trạng thái Nháp hoặc Bị từ chối.");
            return RedirectToAction(nameof(Details), new { id });
        }
        var vm = new BusinessPlanFormViewModel
        {
            Id = p.Id, Number = p.Number, Title = p.Title, CustomerId = p.CustomerId, OpportunityId = p.OpportunityId,
            QuoteId = p.QuoteId, QuoteNumber = p.Quote?.Number, PlanDate = p.PlanDate, Notes = p.Notes,
            Lines = p.Lines.OrderBy(l => l.SortOrder).Select(l => new BusinessPlanLineInput
            {
                ProductId = l.ProductId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity,
                CostPrice = l.CostPrice, SalePrice = l.SalePrice
            }).ToList(),
            Costs = p.Costs.OrderBy(c => c.SortOrder).Select(c => new BusinessPlanCostInput
            {
                Category = c.Category, Description = c.Description, Amount = c.Amount
            }).ToList()
        };
        if (vm.Lines.Count == 0) vm.Lines.Add(new BusinessPlanLineInput());
        await LoadLookups(vm);
        return View("Form", vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(BusinessPlanFormViewModel vm)
    {
        // Bỏ các dòng trống (người dùng thêm dòng nhưng không nhập)
        vm.Lines = vm.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Description) || l.ProductId.HasValue).ToList();
        vm.Costs = vm.Costs.Where(c => !string.IsNullOrWhiteSpace(c.Category) || c.Amount > 0).ToList();
        ModelState.Clear();
        TryValidateModel(vm);
        if (vm.Lines.Count == 0) ModelState.AddModelError(string.Empty, I18n.T("Phương án cần ít nhất một dòng hàng hóa / dịch vụ."));

        if (!ModelState.IsValid)
        {
            if (vm.Lines.Count == 0) vm.Lines.Add(new BusinessPlanLineInput());
            await LoadLookups(vm);
            return View("Form", vm);
        }

        BusinessPlan p;
        if (vm.Id == 0)
        {
            p = new BusinessPlan { Number = await NextNumberAsync(), OwnerId = CurrentUserId, CreatedAt = DateTime.Now };
            if (vm.OpportunityId.HasValue)
            {
                var o = await Db.Opportunities.FindAsync(vm.OpportunityId);
                if (o != null && o.OwnerId.HasValue) p.OwnerId = o.OwnerId;
            }
            Db.BusinessPlans.Add(p);
        }
        else
        {
            p = await Db.BusinessPlans.Include(x => x.Lines).Include(x => x.Costs).FirstOrDefaultAsync(x => x.Id == vm.Id)
                ?? throw new InvalidOperationException();
            if (!User.CanAccess(p)) return NotAllowed();
            if (!p.IsEditable) return BadRequest();
            Db.BusinessPlanLines.RemoveRange(p.Lines);
            Db.BusinessPlanCosts.RemoveRange(p.Costs);
            p.Lines.Clear();
            p.Costs.Clear();
            // Sửa phương án bị từ chối → quay về Nháp, chờ gửi duyệt lại (giữ ý kiến duyệt cũ để tham khảo)
            p.Status = BusinessPlanStatus.Draft;
            p.UpdatedAt = DateTime.Now;
        }

        p.Title = vm.Title.Trim(); p.CustomerId = vm.CustomerId; p.OpportunityId = vm.OpportunityId; p.QuoteId = vm.QuoteId;
        p.PlanDate = vm.PlanDate; p.Notes = vm.Notes;
        var sort = 1;
        foreach (var l in vm.Lines)
            p.Lines.Add(new BusinessPlanLine
            {
                ProductId = l.ProductId, Description = l.Description.Trim(), Unit = l.Unit, Quantity = l.Quantity,
                CostPrice = l.CostPrice, SalePrice = l.SalePrice, SortOrder = sort++
            });
        sort = 1;
        foreach (var c in vm.Costs)
            p.Costs.Add(new BusinessPlanCost { Category = c.Category.Trim(), Description = c.Description, Amount = c.Amount, SortOrder = sort++ });
        p.Recalculate();

        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu phương án {0}.", p.Number);
        return RedirectToAction(nameof(Details), new { id = p.Id });
    }

    /// <summary>Gửi duyệt (người lập / người phụ trách)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id)
    {
        var p = await Db.BusinessPlans.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (!User.CanAccess(p)) return NotAllowed();
        if (!p.IsEditable || p.Lines.Count == 0) return BadRequest();
        p.Status = BusinessPlanStatus.Pending;
        p.SubmittedAt = DateTime.Now;
        p.ApprovedById = null; p.ApprovedAt = null;
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã gửi duyệt phương án {0}.", p.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Thu hồi phương án đang chờ duyệt về Nháp để sửa</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Recall(int id)
    {
        var p = await Db.BusinessPlans.FindAsync(id);
        if (p == null) return NotFound();
        if (!User.CanAccess(p)) return NotAllowed();
        if (p.Status != BusinessPlanStatus.Pending) return BadRequest();
        p.Status = BusinessPlanStatus.Draft;
        p.SubmittedAt = null;
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã thu hồi phương án {0} về Nháp.", p.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Quản lý duyệt / từ chối. Từ chối bắt buộc nhập ý kiến.</summary>
    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(int id, bool approve, string? note)
    {
        var p = await Db.BusinessPlans.FindAsync(id);
        if (p == null) return NotFound();
        if (p.Status != BusinessPlanStatus.Pending) return BadRequest();
        if (!approve && string.IsNullOrWhiteSpace(note))
        {
            TempData["Error"] = I18n.T("Vui lòng nhập lý do từ chối.");
            return RedirectToAction(nameof(Details), new { id });
        }
        p.Status = approve ? BusinessPlanStatus.Approved : BusinessPlanStatus.Rejected;
        p.ApprovedById = CurrentUserId;
        p.ApprovedAt = DateTime.Now;
        p.ApprovalNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await Db.SaveChangesAsync();
        TempData["Success"] = approve ? I18n.T("Đã duyệt phương án {0}.", p.Number) : I18n.T("Đã từ chối phương án {0}.", p.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Nhân bản thành bản nháp mới (dùng khi cần điều chỉnh phương án đã duyệt)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Duplicate(int id)
    {
        var src = await Db.BusinessPlans.Include(x => x.Lines).Include(x => x.Costs).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (src == null) return NotFound();
        if (!User.CanAccess(src)) return NotAllowed();
        var p = new BusinessPlan
        {
            Number = await NextNumberAsync(), Title = src.Title, CustomerId = src.CustomerId, OpportunityId = src.OpportunityId,
            QuoteId = src.QuoteId, OwnerId = src.OwnerId, Notes = src.Notes,
            Lines = src.Lines.Select(l => new BusinessPlanLine
            {
                ProductId = l.ProductId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity,
                CostPrice = l.CostPrice, SalePrice = l.SalePrice, SortOrder = l.SortOrder
            }).ToList(),
            Costs = src.Costs.Select(c => new BusinessPlanCost { Category = c.Category, Description = c.Description, Amount = c.Amount, SortOrder = c.SortOrder }).ToList()
        };
        p.Recalculate();
        Db.BusinessPlans.Add(p);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã tạo bản sao {0}.", p.Number);
        return RedirectToAction(nameof(Edit), new { id = p.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var p = await Db.BusinessPlans.Include(x => x.Lines).Include(x => x.Costs).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (!User.CanAccess(p)) return NotAllowed();
        if (!p.IsEditable)
        {
            TempData["Error"] = I18n.T("Chỉ xóa được phương án ở trạng thái Nháp hoặc Bị từ chối.");
            return RedirectToAction(nameof(Details), new { id });
        }
        Db.BusinessPlanLines.RemoveRange(p.Lines);
        Db.BusinessPlanCosts.RemoveRange(p.Costs);
        Db.BusinessPlans.Remove(p);
        await Db.SaveChangesAsync();
        await DeleteAttachmentsAsync(AttachmentTarget.BusinessPlan, p.Id);
        TempData["Success"] = I18n.T("Đã xóa phương án {0}.", p.Number);
        return RedirectToAction(nameof(Index));
    }

    private async Task<string> NextNumberAsync()
    {
        var prefix = $"PAKD-{DateTime.Today:yyyy}-";
        var n = await Db.BusinessPlans.IgnoreQueryFilters().CountAsync(x => x.Number.StartsWith(prefix)) + 1;
        string no;
        do no = $"{prefix}{n++:0000}"; while (await Db.BusinessPlans.IgnoreQueryFilters().AnyAsync(x => x.Number == no));
        return no;
    }

    private async Task LoadLookups(BusinessPlanFormViewModel vm)
    {
        await LoadCustomersAsync(vm.CustomerId);
        await LoadOpportunitiesAsync(vm.CustomerId, vm.OpportunityId);
        ViewBag.ProductsJson = JsonSerializer.Serialize(await Db.Products.Where(p => p.IsActive).OrderBy(p => p.Code)
            .Select(p => new { id = p.Id, code = p.Code, name = p.Name, unit = p.Unit, price = p.UnitPrice, cost = p.CostPrice }).ToListAsync());
    }
}
