using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Nạp thêm dữ liệu mẫu (chỉ quản trị hệ thống) – dùng cho demo / đào tạo / kiểm thử.</summary>
[Authorize(Roles = AppRoles.Admin)]
public class DemoDataController : CrmControllerBase
{
    public static readonly int[] Sizes = { 20, 50, 100, 200 };
    private readonly CatalogService _catalogs;

    public DemoDataController(CrmDbContext db, CatalogService catalogs) : base(db) => _catalogs = catalogs;

    /// <summary>Thao tác trên toàn chuỗi: bỏ giới hạn đơn vị đang chọn (tránh sinh trùng số chứng từ giữa các đơn vị)</summary>
    public override void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
    {
        HttpContext.RequestServices.GetRequiredService<BranchScope>().Bypass();
        base.OnActionExecuting(context);
    }

    /// <summary>
    /// Tạo cơ cấu chuỗi mẫu khi hệ thống mới có 1 đơn vị: đơn vị gốc thành công ty mẹ, thêm 2 chi nhánh (TP.HCM, Hà Nội),
    /// chia nhân viên kinh doanh xen kẽ vào 2 chi nhánh và chuyển dữ liệu họ phụ trách theo.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateChain([FromServices] BranchDirectory dir)
    {
        if (await Db.Branches.CountAsync() > 1)
        {
            TempData["Error"] = I18n.T("Hệ thống đã có nhiều đơn vị – hãy chỉnh cơ cấu trong mục Công ty & chi nhánh.");
            return RedirectToAction(nameof(Index));
        }
        var root = await Db.Branches.FirstAsync();
        root.Type = BranchType.Company;
        if (string.IsNullOrEmpty(root.ShortName)) root.ShortName = I18n.T("Trụ sở chính");
        var hcm = new Branch { Code = "HCM", Name = I18n.T("Chi nhánh TP. Hồ Chí Minh"), ShortName = "CN TP.HCM", Type = BranchType.Branch, Parent = root, SortOrder = 10 };
        var hn = new Branch { Code = "HN", Name = I18n.T("Chi nhánh Hà Nội"), ShortName = "CN Hà Nội", Type = BranchType.Branch, Parent = root, SortOrder = 20 };
        Db.Branches.AddRange(hcm, hn);
        await Db.SaveChangesAsync();

        var sales = await Db.Users.Where(u => u.Role == AppRoles.Sales).OrderBy(u => u.Id).ToListAsync();
        var moved = 0;
        for (var i = 0; i < sales.Count; i++)
        {
            sales[i].BranchId = i % 2 == 0 ? hcm.Id : hn.Id;
            await Db.SaveChangesAsync();
            moved += await BranchOps.MoveUserDataAsync(Db, sales[i].Id, sales[i].BranchId);
        }
        dir.Invalidate();
        TempData["Success"] = I18n.T("Đã tạo 2 chi nhánh, chia {0} nhân viên kinh doanh và chuyển {1} bản ghi theo người phụ trách.", sales.Count, moved);
        return RedirectToAction("Index", "Branches");
    }

    public async Task<IActionResult> Index()
    {
        ViewBag.Counts = new (string Label, string Icon, int Count)[]
        {
            (I18n.T("Hàng hoá / dịch vụ"), "product", await Db.Products.CountAsync()),
            (I18n.L("Khách hàng", "Customers"), "customer", await Db.Customers.CountAsync()),
            (I18n.L("Người liên hệ", "Contacts"), "contact", await Db.Contacts.CountAsync()),
            ("Lead", "lead", await Db.Leads.CountAsync()),
            (I18n.T("Cơ hội bán hàng"), "opportunity", await Db.Opportunities.CountAsync()),
            (I18n.T("Hoạt động"), "calendar", await Db.Activities.CountAsync()),
            (I18n.L("Báo giá", "Quotes"), "quote", await Db.Quotes.CountAsync()),
            (I18n.T("Phương án kinh doanh"), "plan", await Db.BusinessPlans.CountAsync()),
            (I18n.T("Kế hoạch bán hàng"), "target", await Db.SalesPlans.CountAsync()),
            (I18n.L("Hợp đồng", "Contracts"), "contract", await Db.Contracts.CountAsync()),
            (I18n.L("Hoá đơn", "Invoices"), "invoice", await Db.Invoices.CountAsync()),
            (I18n.T("Thanh toán"), "wallet", await Db.Payments.CountAsync()),
            (I18n.T("Chiến dịch tiếp thị"), "campaign", await Db.Campaigns.CountAsync()),
            (I18n.T("Phiếu hỗ trợ"), "support", await Db.Tickets.CountAsync()),
            (I18n.T("Bảng giá"), "coins", await Db.PriceLists.CountAsync()),
            (I18n.T("Nhãn đã gắn"), "category", await Db.EntityTags.CountAsync()),
            (I18n.T("Chính sách SLA"), "clock", await Db.SlaPolicies.CountAsync()),
            (I18n.T("Câu trả lời NPS"), "star", await Db.NpsResponses.CountAsync(x => x.Score != null)),
            (I18n.T("Form thu lead website"), "globe", await Db.WebForms.CountAsync()),
            (I18n.T("Bài viết kho kiến thức"), "catalog", await Db.KbArticles.CountAsync()),
            (I18n.T("Công ty & chi nhánh"), "branch", await Db.Branches.CountAsync()),
        };
        ViewBag.BranchCount = await Db.Branches.CountAsync();
        ViewBag.HasSupportAgent = await Db.Users.AnyAsync(u => u.IsActive && u.Role == AppRoles.Support);
        return View();
    }

    public static readonly int[] MarketingSizes = { 4, 8, 12 };
    public static readonly int[] SupportSizes = { 20, 50, 100, 200 };

    /// <summary>Chỉ nạp phiếu hỗ trợ mẫu trên khách hàng / hợp đồng đang có</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateSupport(int tickets)
    {
        if (!SupportSizes.Contains(tickets)) return BadRequest();
        if (!await Db.Customers.AnyAsync())
        {
            TempData["Error"] = I18n.T("Chưa có khách hàng để tạo phiếu hỗ trợ. Hãy nạp dữ liệu mẫu trước.");
            return RedirectToAction(nameof(Index));
        }
        var r = await DemoDataGenerator.GenerateSupportAsync(Db, tickets);
        TempData["Success"] = I18n.T("Đã nạp {0} phiếu hỗ trợ ({1} đang mở, {2} đã đóng) với {3} lượt trao đổi.", r.Tickets, r.Open, r.Closed, r.Comments);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Chỉ nạp chiến dịch tiếp thị mẫu trên khách hàng / lead đang có</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateMarketing(int campaigns)
    {
        if (!MarketingSizes.Contains(campaigns)) return BadRequest();
        if (!await Db.Customers.AnyAsync())
        {
            TempData["Error"] = I18n.T("Chưa có khách hàng để lập danh sách tiếp thị. Hãy nạp dữ liệu mẫu trước.");
            return RedirectToAction(nameof(Index));
        }
        var r = await DemoDataGenerator.GenerateMarketingAsync(Db, campaigns);
        TempData["Success"] = I18n.T("Đã nạp {0} chiến dịch tiếp thị với {1} đối tượng; gắn {2} lead và {3} cơ hội vào chiến dịch.",
            r.Campaigns, r.Members, r.Leads, r.Opportunities);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Dữ liệu mẫu cho nghiệp vụ mở rộng (bảng giá, nhãn, trường tùy biến, SLA, dự báo, định kỳ, duyệt chiết khấu, NPS, form website)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateExtended()
    {
        if (!await Db.Customers.AnyAsync())
        {
            TempData["Error"] = I18n.T("Chưa có khách hàng. Hãy nạp dữ liệu mẫu trước.");
            return RedirectToAction(nameof(Index));
        }
        var x = await DemoDataExtended.GenerateAsync(Db, HttpContext.RequestServices);
        TempData["Success"] = I18n.T("Đã nạp nghiệp vụ mở rộng: {0} bảng giá (gắn {1} khách hàng), gắn nhãn {2} bản ghi, {3} giá trị trường tùy biến, SLA VIP cho {4} khách hàng và {5} hợp đồng, {6} cơ hội có nhóm dự báo, {7} hợp đồng định kỳ, {8} báo giá duyệt chiết khấu, {9} đợt NPS ({10}/{11} khách trả lời), {12} form website với {13} lead có UTM; chấm lại sức khoẻ {14} khách hàng.",
            x.PriceLists, x.PricedCustomers, x.TaggedRecords, x.CustomValues, x.VipCustomers, x.VipContracts, x.ForecastSet, x.RecurringContracts, x.QuoteApprovals,
            x.NpsSurveys, x.NpsAnswered, x.NpsResponses, x.WebForms, x.WebLeads, x.HealthScored);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(int customers)
    {
        if (!Sizes.Contains(customers)) return BadRequest();
        var r = await DemoDataGenerator.GenerateAsync(Db, customers);
        _catalogs.Invalidate();
        TempData["Success"] = I18n.T("Đã nạp thêm: {0} khách hàng, {1} liên hệ, {2} lead, {3} cơ hội, {4} hoạt động, {5} báo giá, {6} PAKD, {7} hợp đồng, {8} hoá đơn, {9} phiếu thu, {10} chiến dịch tiếp thị, {11} phiếu hỗ trợ.",
            r.Customers, r.Contacts, r.Leads, r.Opportunities, r.Activities, r.Quotes, r.BusinessPlans, r.Contracts, r.Invoices, r.Payments, r.Campaigns, r.Tickets);
        return RedirectToAction(nameof(Index));
    }
}
