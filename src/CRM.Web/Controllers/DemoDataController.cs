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
            (I18n.L("Hợp đồng", "Contracts"), "contract", await Db.Contracts.CountAsync()),
            (I18n.L("Hoá đơn", "Invoices"), "invoice", await Db.Invoices.CountAsync()),
            (I18n.T("Thanh toán"), "wallet", await Db.Payments.CountAsync()),
        };
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(int customers)
    {
        if (!Sizes.Contains(customers)) return BadRequest();
        var r = await DemoDataGenerator.GenerateAsync(Db, customers);
        _catalogs.Invalidate();
        TempData["Success"] = I18n.T("Đã nạp thêm: {0} khách hàng, {1} liên hệ, {2} lead, {3} cơ hội, {4} hoạt động, {5} báo giá, {6} PAKD, {7} hợp đồng, {8} hoá đơn, {9} phiếu thu.",
            r.Customers, r.Contacts, r.Leads, r.Opportunities, r.Activities, r.Quotes, r.BusinessPlans, r.Contracts, r.Invoices, r.Payments);
        return RedirectToAction(nameof(Index));
    }
}
