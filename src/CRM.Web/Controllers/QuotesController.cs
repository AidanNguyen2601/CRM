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
        var policy = await HttpContext.RequestServices.GetRequiredService<PricingService>().PolicyAsync();
        ViewBag.CanApproveDiscount = q.ApprovalStatus == QuoteApproval.Pending && PricingService.CanApprove(User, policy, q.EffectiveDiscount)
            && (q.OwnerId != CurrentUserId || User.IsInRole(AppRoles.Admin));
        ViewBag.ApprovedBy = q.DiscountApprovedById.HasValue ? await Db.Users.Where(u => u.Id == q.DiscountApprovedById).Select(u => u.FullName).FirstOrDefaultAsync() : null;
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
            TempData["Error"] = I18n.T("Chỉ sửa được báo giá ở trạng thái Nháp.");
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
        if (vm.Lines.Count == 0) ModelState.AddModelError(string.Empty, I18n.T("Báo giá cần ít nhất một dòng sản phẩm / dịch vụ."));
        if (vm.ValidUntil < vm.QuoteDate) ModelState.AddModelError(nameof(vm.ValidUntil), I18n.T("Ngày hiệu lực phải sau ngày báo giá"));
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

        // Chiết khấu vượt hạn mức của vai trò người lập → chờ duyệt (mọi lần sửa đều xét lại)
        var pricing = HttpContext.RequestServices.GetRequiredService<PricingService>();
        var policy = await pricing.PolicyAsync();
        q.EffectiveDiscount = await pricing.EffectiveDiscountAsync(q);
        var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? AppRoles.Sales;
        var needApproval = policy.Enabled && q.EffectiveDiscount > policy.LimitFor(role);
        q.ApprovalStatus = needApproval ? QuoteApproval.Pending : QuoteApproval.None;
        q.DiscountApprovedById = null; q.DiscountApprovedAt = null;
        await Db.SaveChangesAsync();
        if (needApproval)
        {
            await NotifyApproversAsync(q, policy);
            await Db.SaveChangesAsync();
            TempData["Success"] = I18n.T("Đã lưu báo giá {0}. Chiết khấu {1}% vượt hạn mức {2}% – báo giá đang chờ duyệt.", q.Number, q.EffectiveDiscount.ToString("0.##"), policy.LimitFor(role).ToString("0.##"));
        }
        else TempData["Success"] = I18n.T("Đã lưu báo giá {0}.", q.Number);
        return RedirectToAction(nameof(Details), new { id = q.Id });
    }

    /// <summary>Báo người duyệt: quản lý (cùng đơn vị hoặc cấp trên) có hạn mức đủ, nếu không có thì quản trị</summary>
    private async Task NotifyApproversAsync(Quote q, DiscountPolicy policy)
    {
        var chain = HttpContext.RequestServices.GetRequiredService<BranchDirectory>().Get().Ancestors(q.BranchId);
        var users = await Db.Users.Where(u => u.IsActive && (u.Role == AppRoles.Admin || u.Role == AppRoles.Manager && chain.Contains(u.BranchId)))
            .Select(u => new { u.Id, u.Role }).ToListAsync();
        var approvers = policy.ManagerLimit >= q.EffectiveDiscount ? users.Where(u => u.Role == AppRoles.Manager).ToList() : new();
        if (approvers.Count == 0) approvers = users.Where(u => u.Role == AppRoles.Admin).ToList();
        var notify = HttpContext.RequestServices.GetRequiredService<NotificationService>();
        foreach (var u in approvers)
            notify.Add(u.Id, "approval", I18n.T("{0} đề nghị duyệt chiết khấu {1}% cho báo giá {2}", User.GetFullName(), q.EffectiveDiscount.ToString("0.##"), q.Number),
                Url.Action(nameof(Details), new { id = q.Id }), CurrentUserId, I18n.T("Tổng thanh toán {0} đ", q.Total.ToMoney()));
    }

    /// <summary>Duyệt / từ chối chiết khấu vượt hạn mức</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DecideDiscount(int id, bool approve, string? note)
    {
        var q = await Db.Quotes.FindAsync(id);
        if (q == null) return NotFound();
        if (q.ApprovalStatus != QuoteApproval.Pending) return BadRequest();
        var policy = await HttpContext.RequestServices.GetRequiredService<PricingService>().PolicyAsync();
        if (!PricingService.CanApprove(User, policy, q.EffectiveDiscount) || q.OwnerId == CurrentUserId && !User.IsInRole(AppRoles.Admin))
        {
            TempData["Error"] = I18n.T("Bạn không đủ hạn mức để duyệt chiết khấu {0}% (hoặc không thể tự duyệt báo giá của mình).", q.EffectiveDiscount.ToString("0.##"));
            return RedirectToAction(nameof(Details), new { id });
        }
        if (!approve && string.IsNullOrWhiteSpace(note))
        {
            TempData["Error"] = I18n.T("Vui lòng nhập lý do từ chối.");
            return RedirectToAction(nameof(Details), new { id });
        }
        q.ApprovalStatus = approve ? QuoteApproval.Approved : QuoteApproval.Rejected;
        q.DiscountApprovedById = CurrentUserId; q.DiscountApprovedAt = DateTime.Now;
        q.ApprovalNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        HttpContext.RequestServices.GetRequiredService<NotificationService>().Add(q.OwnerId, "approval",
            approve ? I18n.T("{0} đã duyệt chiết khấu báo giá {1}", User.GetFullName(), q.Number) : I18n.T("{0} từ chối chiết khấu báo giá {1}", User.GetFullName(), q.Number),
            Url.Action(nameof(Details), new { id }), CurrentUserId, q.ApprovalNote);
        await Db.SaveChangesAsync();
        TempData["Success"] = approve ? I18n.T("Đã duyệt chiết khấu báo giá {0}.", q.Number) : I18n.T("Đã từ chối chiết khấu báo giá {0}.", q.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Chuyển trạng thái báo giá; khách chấp nhận → cơ hội chuyển "Đàm phán" và cập nhật giá trị.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(int id, QuoteStatus status)
    {
        var q = await Db.Quotes.FindAsync(id);
        if (q == null) return NotFound();
        if (!User.CanAccess(q)) return NotAllowed();
        if (status is QuoteStatus.Sent or QuoteStatus.Accepted && q.ApprovalStatus is QuoteApproval.Pending or QuoteApproval.Rejected)
        {
            TempData["Error"] = I18n.T("Báo giá có chiết khấu vượt hạn mức chưa được duyệt – chưa thể gửi / chốt với khách hàng.");
            return RedirectToAction(nameof(Details), new { id });
        }
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
        TempData["Success"] = I18n.T("Báo giá {0}: {1}.", q.Number, status.GetDisplayName());
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
            EffectiveDiscount = src.EffectiveDiscount, ApprovalStatus = src.ApprovalStatus == QuoteApproval.None ? QuoteApproval.None : QuoteApproval.Pending,
            Lines = src.Lines.Select(l => new QuoteLine
            {
                ProductId = l.ProductId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity,
                UnitPrice = l.UnitPrice, DiscountPercent = l.DiscountPercent, SortOrder = l.SortOrder
            }).ToList()
        };
        q.Recalculate();
        Db.Quotes.Add(q);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã tạo bản sao {0}.", q.Number);
        return RedirectToAction(nameof(Edit), new { id = q.Id });
    }

    private async Task<string> NextNumberAsync()
    {
        var prefix = $"BG-{DateTime.Today:yyyy}-";
        var n = await Db.Quotes.IgnoreQueryFilters().CountAsync(q => q.Number.StartsWith(prefix)) + 1;
        string no;
        do no = $"{prefix}{n++:0000}"; while (await Db.Quotes.IgnoreQueryFilters().AnyAsync(q => q.Number == no));
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
