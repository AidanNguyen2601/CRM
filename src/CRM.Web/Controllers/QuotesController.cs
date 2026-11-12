using System.Text.Json;
using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

public class QuotesController : CrmControllerBase
{
    public QuotesController(CrmDbContext db) : base(db) { }

    public async Task<IActionResult> Index(string? keyword, QuoteStatus? status)
    {
        var q = Db.Quotes.VisibleTo(User).Include(x => x.Customer).Include(x => x.Owner).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
            q = q.Where(x => x.Number.Contains(keyword) || (x.Customer != null && x.Customer.Name.Contains(keyword)));
        if (status.HasValue) q = q.Where(x => x.Status == status);
        ViewBag.Keyword = keyword;
        ViewBag.Status = status;
        return View(await q.OrderByDescending(x => x.QuoteDate).ThenByDescending(x => x.Id).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var q = await Db.Quotes.Include(x => x.Customer).Include(x => x.Contact).Include(x => x.Opportunity).Include(x => x.Owner)
            .Include(x => x.Lines).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (q == null) return NotFound();
        if (!User.CanAccess(q)) return NotAllowed();
        q.Lines = q.Lines.OrderBy(l => l.SortOrder).ToList();
        return View(q);
    }

    public async Task<IActionResult> Create(int? opportunityId, int? customerId)
    {
        var vm = new QuoteFormViewModel { Number = await NextNumberAsync(), CustomerId = customerId, OpportunityId = opportunityId };
        if (opportunityId.HasValue)
        {
            var o = await Db.Opportunities.FindAsync(opportunityId);
            if (o != null) { vm.CustomerId = o.CustomerId; vm.ContactId = o.ContactId; }
        }
        vm.Lines.Add(new QuoteLineInput());
        await LoadLookups(vm);
        return View("Form", vm);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var q = await Db.Quotes.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (q == null) return NotFound();
        if (!User.CanAccess(q)) return NotAllowed();
        if (q.Status != QuoteStatus.Draft)
        {
            TempData["Error"] = "Chỉ sửa được báo giá ở trạng thái Nháp.";
            return RedirectToAction(nameof(Details), new { id });
        }
        var vm = new QuoteFormViewModel
        {
            Id = q.Id, Number = q.Number, CustomerId = q.CustomerId, ContactId = q.ContactId, OpportunityId = q.OpportunityId,
            QuoteDate = q.QuoteDate, ValidUntil = q.ValidUntil, DiscountPercent = q.DiscountPercent, VatPercent = Math.Round(q.VatPercent, 0), Terms = q.Terms,
            Lines = q.Lines.OrderBy(l => l.SortOrder).Select(l => new QuoteLineInput
            {
                ProductId = l.ProductId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity,
                UnitPrice = l.UnitPrice, DiscountPercent = l.DiscountPercent
            }).ToList()
        };
        await LoadLookups(vm);
        return View("Form", vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(QuoteFormViewModel vm)
    {
        vm.Lines = vm.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Description) || l.ProductId.HasValue).ToList();
        if (vm.Lines.Count == 0) ModelState.AddModelError(string.Empty, "Báo giá cần ít nhất một dòng sản phẩm / dịch vụ.");
        if (vm.ValidUntil < vm.QuoteDate) ModelState.AddModelError(nameof(vm.ValidUntil), "Ngày hiệu lực phải sau ngày báo giá");
        if (!ModelState.IsValid)
        {
            if (vm.Lines.Count == 0) vm.Lines.Add(new QuoteLineInput());
            await LoadLookups(vm);
            return View("Form", vm);
        }

        Quote q;
        if (vm.Id == 0)
        {
            q = new Quote { Number = await NextNumberAsync(), OwnerId = CurrentUserId, CreatedAt = DateTime.Now };
            if (vm.OpportunityId.HasValue)
            {
                var o = await Db.Opportunities.FindAsync(vm.OpportunityId);
                if (o != null) q.OwnerId = o.OwnerId;
            }
            Db.Quotes.Add(q);
        }
        else
        {
            q = await Db.Quotes.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == vm.Id) ?? throw new InvalidOperationException();
            if (!User.CanAccess(q)) return NotAllowed();
            if (q.Status != QuoteStatus.Draft) return BadRequest();
            Db.QuoteLines.RemoveRange(q.Lines);
            q.Lines.Clear();
        }

        q.CustomerId = vm.CustomerId; q.ContactId = vm.ContactId; q.OpportunityId = vm.OpportunityId;
        q.QuoteDate = vm.QuoteDate; q.ValidUntil = vm.ValidUntil; q.DiscountPercent = vm.DiscountPercent;
        q.VatPercent = vm.VatPercent; q.Terms = vm.Terms;
        var sort = 1;
        foreach (var l in vm.Lines)
        {
            q.Lines.Add(new QuoteLine
            {
                ProductId = l.ProductId, Description = l.Description.Trim(), Unit = l.Unit, Quantity = l.Quantity,
                UnitPrice = l.UnitPrice, DiscountPercent = l.DiscountPercent, SortOrder = sort++
            });
        }
        q.Recalculate();
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã lưu báo giá {q.Number}.";
        return RedirectToAction(nameof(Details), new { id = q.Id });
    }

    /// <summary>Chuyển trạng thái báo giá; khách chấp nhận → cơ hội chuyển "Đàm phán" và cập nhật giá trị.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(int id, QuoteStatus status)
    {
        var q = await Db.Quotes.FindAsync(id);
        if (q == null) return NotFound();
        if (!User.CanAccess(q)) return NotAllowed();
        q.Status = status;
        if (status == QuoteStatus.Accepted && q.OpportunityId.HasValue)
        {
            var o = await Db.Opportunities.FindAsync(q.OpportunityId);
            if (o != null && !o.IsClosed)
            {
                o.Amount = q.Total - q.VatAmount; // doanh thu trước thuế
                if (o.Stage < OpportunityStage.Negotiation) { o.Stage = OpportunityStage.Negotiation; o.Probability = (int)o.Stage; }
            }
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Báo giá {q.Number}: {status.GetDisplayName()}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Nhân bản báo giá thành bản nháp mới</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Duplicate(int id)
    {
        var src = await Db.Quotes.Include(x => x.Lines).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (src == null) return NotFound();
        if (!User.CanAccess(src)) return NotAllowed();
        var q = new Quote
        {
            Number = await NextNumberAsync(), CustomerId = src.CustomerId, ContactId = src.ContactId, OpportunityId = src.OpportunityId,
            OwnerId = src.OwnerId, DiscountPercent = src.DiscountPercent, VatPercent = src.VatPercent, Terms = src.Terms,
            Lines = src.Lines.Select(l => new QuoteLine
            {
                ProductId = l.ProductId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity,
                UnitPrice = l.UnitPrice, DiscountPercent = l.DiscountPercent, SortOrder = l.SortOrder
            }).ToList()
        };
        q.Recalculate();
        Db.Quotes.Add(q);
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã tạo bản sao {q.Number}.";
        return RedirectToAction(nameof(Edit), new { id = q.Id });
    }

    private async Task<string> NextNumberAsync()
    {
        var prefix = $"BG-{DateTime.Today:yyyy}-";
        var n = await Db.Quotes.CountAsync(q => q.Number.StartsWith(prefix)) + 1;
        string no;
        do no = $"{prefix}{n++:0000}"; while (await Db.Quotes.AnyAsync(q => q.Number == no));
        return no;
    }

    private async Task LoadLookups(QuoteFormViewModel vm)
    {
        await LoadCustomersAsync(vm.CustomerId);
        await LoadContactsAsync(vm.CustomerId, vm.ContactId);
        await LoadOpportunitiesAsync(vm.CustomerId, vm.OpportunityId);
        ViewBag.ProductsJson = JsonSerializer.Serialize(await Db.Products.Where(p => p.IsActive).OrderBy(p => p.Code)
            .Select(p => new { id = p.Id, code = p.Code, name = p.Name, unit = p.Unit, price = p.UnitPrice }).ToListAsync());
    }
}
