using System.Text;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Html;

namespace CRM.Web.Infrastructure;

/// <summary>
/// Bộ icon SVG nội tuyến (nét outline 24×24, màu theo currentColor) — không phụ thuộc CDN.
/// Dùng trong view: @Icon("customers") hoặc @Icon("customers", "ico-lg").
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["dashboard"] = "<rect x='3' y='3' width='7' height='9' rx='1'/><rect x='14' y='3' width='7' height='5' rx='1'/><rect x='14' y='12' width='7' height='9' rx='1'/><rect x='3' y='16' width='7' height='5' rx='1'/>",
        ["calendar"] = "<path d='M8 2v4M16 2v4'/><rect x='3' y='4' width='18' height='18' rx='2'/><path d='M3 10h18M9 16l2 2 4-4'/>",
        ["lead"] = "<circle cx='12' cy='12' r='10'/><circle cx='12' cy='12' r='6'/><circle cx='12' cy='12' r='2'/>",
        ["pipeline"] = "<rect x='3' y='3' width='18' height='18' rx='2'/><path d='M8 7v7M12 7v4M16 7v9'/>",
        ["opportunity"] = "<path d='M16 20V4a2 2 0 0 0-2-2h-4a2 2 0 0 0-2 2v16'/><rect x='2' y='6' width='20' height='14' rx='2'/>",
        ["quote"] = "<path d='M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7z'/><path d='M14 2v4a2 2 0 0 0 2 2h4M10 9H8M16 13H8M16 17H8'/>",
        ["customer"] = "<path d='M6 22V4a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v18z'/><path d='M6 12H4a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2h2M18 9h2a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2h-2M10 6h4M10 10h4M10 14h4M10 18h4'/>",
        ["contact"] = "<path d='M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2'/><circle cx='9' cy='7' r='4'/><path d='M22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75'/>",
        ["product"] = "<path d='M11 21.73a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73z'/><path d='M12 22V12M3.3 7l8.7 5 8.7-5M7.5 4.27l9 5.15'/>",
        ["industry"] = "<path d='M2 20a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V8l-7 5V8l-7 5V4a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2z'/><path d='M17 18h1M12 18h1M7 18h1'/>",
        ["city"] = "<path d='M20 10c0 5-5.54 10.19-7.4 11.8a1 1 0 0 1-1.2 0C9.54 20.19 4 15 4 10a8 8 0 0 1 16 0'/><circle cx='12' cy='10' r='3'/>",
        ["category"] = "<path d='M12.59 2.59A2 2 0 0 0 11.17 2H4a2 2 0 0 0-2 2v7.17a2 2 0 0 0 .59 1.42l8.7 8.7a2.43 2.43 0 0 0 3.42 0l6.58-6.58a2.43 2.43 0 0 0 0-3.42z'/><circle cx='7.5' cy='7.5' r='1.2'/>",
        ["unit"] = "<path d='M21.3 15.3a2.4 2.4 0 0 1 0 3.4l-2.6 2.6a2.4 2.4 0 0 1-3.4 0L2.7 8.7a2.41 2.41 0 0 1 0-3.4l2.6-2.6a2.41 2.41 0 0 1 3.4 0z'/><path d='M14.5 12.5l2-2M11.5 9.5l2-2M8.5 6.5l2-2M17.5 15.5l2-2'/>",
        ["loss"] = "<path d='M17 14V2'/><path d='M9 18.12L10 14H4.17a2 2 0 0 1-1.92-2.56l2.33-8A2 2 0 0 1 6.5 2H20a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-2.76a2 2 0 0 0-1.79 1.11L12 22a3.13 3.13 0 0 1-3-3.88z'/>",
        ["users"] = "<path d='M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z'/><path d='M9 12l2 2 4-4'/>",
        ["key"] = "<path d='M2.59 17.41A2 2 0 0 0 2 18.83V21a1 1 0 0 0 1 1h3a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h1a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h.17a2 2 0 0 0 1.42-.59l.81-.81a6.5 6.5 0 1 0-4-4z'/><circle cx='16.5' cy='7.5' r='1.2'/>",
        ["logout"] = "<path d='M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9'/>",
        ["report"] = "<path d='M3 3v18h18'/><path d='M18 17V9M13 17V5M8 17v-3'/>",
        ["palette"] = "<circle cx='13.5' cy='6.5' r='.5' fill='currentColor'/><circle cx='17.5' cy='10.5' r='.5' fill='currentColor'/><circle cx='8.5' cy='7.5' r='.5' fill='currentColor'/><circle cx='6.5' cy='12.5' r='.5' fill='currentColor'/><path d='M12 2C6.5 2 2 6.5 2 12s4.5 10 10 10c.93 0 1.65-.75 1.65-1.69 0-.44-.18-.84-.44-1.13-.29-.29-.44-.65-.44-1.13a1.64 1.64 0 0 1 1.67-1.67h2c3.05 0 5.55-2.5 5.55-5.55C21.97 6.01 17.46 2 12 2z'/>",
        ["bell"] = "<path d='M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9'/><path d='M10.3 21a1.94 1.94 0 0 0 3.4 0'/>",
        ["kanban"] = "<rect x='3' y='3' width='18' height='18' rx='2'/><path d='M8 7v7'/><path d='M12 7v4'/><path d='M16 7v9'/>",
        ["branch"] = "<path d='M6 22V4a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v18Z'/><path d='M6 12H4a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2h2'/><path d='M18 9h2a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2h-2'/><path d='M10 6h4'/><path d='M10 10h4'/><path d='M10 14h4'/><path d='M10 18h4'/>",
        ["support"] = "<path d='M3 11h3a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-5Zm0 0a9 9 0 1 1 18 0m0 0v5a2 2 0 0 1-2 2h-1a2 2 0 0 1-2-2v-3a2 2 0 0 1 2-2h3Z'/><path d='M21 16v2a4 4 0 0 1-4 4h-5'/>",
        ["star"] = "<path d='M11.52 2.3a.53.53 0 0 1 .96 0l2.31 4.68a2.12 2.12 0 0 0 1.6 1.16l5.16.75a.53.53 0 0 1 .3.91l-3.74 3.64a2.12 2.12 0 0 0-.61 1.88l.88 5.14a.53.53 0 0 1-.77.56l-4.62-2.43a2.12 2.12 0 0 0-1.97 0L6.4 21.01a.53.53 0 0 1-.77-.56l.88-5.14a2.12 2.12 0 0 0-.61-1.88L2.16 9.8a.53.53 0 0 1 .3-.91l5.16-.75a2.12 2.12 0 0 0 1.6-1.16z'/>",
        ["clock"] = "<circle cx='12' cy='12' r='10'/><path d='M12 6v6l4 2'/>",
        ["plus"] = "<path d='M5 12h14M12 5v14'/>",
        ["trending"] = "<path d='M22 7l-8.5 8.5-5-5L2 17'/><path d='M16 7h6v6'/>",
        ["trophy"] = "<path d='M6 9H4.5a2.5 2.5 0 0 1 0-5H6M18 9h1.5a2.5 2.5 0 0 0 0-5H18M4 22h16M10 14.66V17c0 .55-.47.98-.97 1.21C7.85 18.75 7 20.24 7 22M14 14.66V17c0 .55.47.98.97 1.21C16.15 18.75 17 20.24 17 22'/><path d='M18 2H6v7a6 6 0 0 0 12 0z'/>",
        ["alert"] = "<path d='M21.73 18l-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3'/><path d='M12 9v4M12 17h.01'/>",
        ["call"] = "<path d='M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72c.13.96.36 1.9.7 2.81a2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45c.91.34 1.85.57 2.81.7A2 2 0 0 1 22 16.92z'/>",
        ["meeting"] = "<path d='M18 21a8 8 0 0 0-16 0'/><circle cx='10' cy='8' r='5'/><path d='M22 20c0-3.37-2-6.5-4-8a5 5 0 0 0-.45-8.3'/>",
        ["email"] = "<rect x='2' y='4' width='20' height='16' rx='2'/><path d='M22 7l-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7'/>",
        ["task"] = "<rect x='3' y='3' width='18' height='18' rx='2'/><path d='M9 12l2 2 4-4'/>",
        ["catalog"] = "<path d='M4 19.5v-15A2.5 2.5 0 0 1 6.5 2H20v20H6.5a2.5 2.5 0 0 1 0-5H20'/>",
        ["plan"] = "<rect x='4' y='2' width='16' height='20' rx='2'/><path d='M8 6h8M16 14v4M16 10h.01M12 10h.01M8 10h.01M12 14h.01M8 14h.01M12 18h.01M8 18h.01'/>",
        ["coins"] = "<circle cx='8' cy='8' r='6'/><path d='M18.09 10.37A6 6 0 1 1 10.34 18M7 6h1v4M16.71 13.88l.7.71-2.82 2.82'/>",
        ["send"] = "<path d='M14.54 21.69a.5.5 0 0 0 .94-.03l6.5-19a.5.5 0 0 0-.64-.64l-19 6.5a.5.5 0 0 0-.03.94l7.93 3.18a2 2 0 0 1 1.11 1.11zM21.85 2.15 10.91 13.09'/>",
        ["check"] = "<path d='M20 6 9 17l-5-5'/>",
        ["x"] = "<path d='M18 6 6 18M6 6l12 12'/>",
        ["undo"] = "<path d='M9 14 4 9l5-5'/><path d='M4 9h10.5a5.5 5.5 0 0 1 0 11H11'/>",
        ["badge"] = "<path d='M3.85 8.62a4 4 0 0 1 4.78-4.77 4 4 0 0 1 6.74 0 4 4 0 0 1 4.78 4.78 4 4 0 0 1 0 6.74 4 4 0 0 1-4.77 4.78 4 4 0 0 1-6.75 0 4 4 0 0 1-4.78-4.77 4 4 0 0 1 0-6.76Z'/><path d='m9 12 2 2 4-4'/>",
        ["globe"] = "<circle cx='12' cy='12' r='10'/><path d='M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20M2 12h20'/>",
        ["card"] = "<rect x='2' y='5' width='20' height='14' rx='2'/><path d='M2 10h20'/>",
        ["contract"] = "<path d='M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7z'/><path d='M14 2v4a2 2 0 0 0 2 2h4'/><path d='M8 18c1.5-2 2.5-2 3.5 0s2 2 3.5 0'/><path d='M8 10h4M8 13h8'/>",
        ["invoice"] = "<path d='M4 2v20l2-1 2 1 2-1 2 1 2-1 2 1 2-1 2 1V2l-2 1-2-1-2 1-2-1-2 1-2-1-2 1Z'/><path d='M16 8h-6a2 2 0 1 0 0 4h4a2 2 0 1 1 0 4H8M12 17.5v-11'/>",
        ["wallet"] = "<path d='M19 7V4a1 1 0 0 0-1-1H5a2 2 0 0 0 0 4h15a1 1 0 0 1 1 1v4h-3a2 2 0 0 0 0 4h3a1 1 0 0 0 1-1v-2a1 1 0 0 0-1-1'/><path d='M3 5v14a2 2 0 0 0 2 2h15a1 1 0 0 0 1-1v-4'/>",
        ["receivable"] = "<path d='M12 2v20M17 5H9.5a3.5 3.5 0 0 0 0 7h5a3.5 3.5 0 0 1 0 7H6'/>",
        ["paperclip"] = "<path d='M13.234 20.252 21 12.3'/><path d='m16 6-8.414 8.586a2 2 0 0 0 0 2.828 2 2 0 0 0 2.828 0l8.414-8.586a4 4 0 0 0 0-5.656 4 4 0 0 0-5.656 0l-8.415 8.585a6 6 0 1 0 8.486 8.486'/>",
        ["download"] = "<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M7 10l5 5 5-5M12 15V3'/>",
        ["upload"] = "<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M17 8l-5-5-5 5M12 3v12'/>",
        ["file"] = "<path d='M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7z'/><path d='M14 2v4a2 2 0 0 0 2 2h4'/>",
        ["file-pdf"] = "<path d='M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7z' stroke='#dc2626'/><path d='M14 2v4a2 2 0 0 0 2 2h4' stroke='#dc2626'/><path d='M8 13h1.5a1.5 1.5 0 0 1 0 3H8v-5M13 11v5h1a2 2 0 0 0 0-4h-1' stroke='#dc2626'/>",
        ["file-doc"] = "<path d='M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7z' stroke='#2563eb'/><path d='M14 2v4a2 2 0 0 0 2 2h4M8 13h8M8 17h8M8 9h2' stroke='#2563eb'/>",
        ["file-sheet"] = "<path d='M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7z' stroke='#16a34a'/><path d='M14 2v4a2 2 0 0 0 2 2h4M8 13h8M8 17h8M12 11v8' stroke='#16a34a'/>",
        ["file-image"] = "<rect x='3' y='3' width='18' height='18' rx='2' stroke='#9333ea'/><circle cx='9' cy='9' r='2' stroke='#9333ea'/><path d='m21 15-3.09-3.09a2 2 0 0 0-2.82 0L6 21' stroke='#9333ea'/>",
        ["file-zip"] = "<path d='M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7z' stroke='#b45309'/><path d='M10 2v2M10 6v2M10 10v2M10 14a2 2 0 1 0 0 4 2 2 0 0 0 0-4z' stroke='#b45309'/>",
        ["campaign"] = "<path d='m3 11 18-5v12L3 14v-3z'/><path d='M11.6 16.8a3 3 0 1 1-5.8-1.6'/>",
        ["target"] = "<circle cx='12' cy='12' r='10'/><circle cx='12' cy='12' r='6'/><path d='M22 2 12 12M16 2h6v6'/>",
        ["menu"] ="<path d='M4 6h16M4 12h16M4 18h16'/>",
        ["chevron"] = "<path d='M6 9l6 6 6-6'/>",
        ["panel"] = "<rect x='3' y='3' width='18' height='18' rx='2'/><path d='M9 3v18M16 15l-3-3 3-3'/>",
        ["expand-all"] = "<path d='M7 15l5 5 5-5M7 9l5-5 5 5'/>",
        ["collapse-all"] = "<path d='M7 20l5-5 5 5M7 4l5 5 5-5'/>",
    };

    public static IHtmlContent Icon(string name, string? cssClass = null)
    {
        if (!Paths.TryGetValue(name, out var inner)) return HtmlString.Empty;
        var sb = new StringBuilder("<svg class=\"ico");
        if (!string.IsNullOrEmpty(cssClass)) sb.Append(' ').Append(cssClass);
        sb.Append("\" viewBox=\"0 0 24 24\" aria-hidden=\"true\" focusable=\"false\">").Append(inner).Append("</svg>");
        return new HtmlString(sb.ToString());
    }

    public static string ForCatalog(CatalogGroup g) => g switch
    {
        CatalogGroup.Industry => "industry",
        CatalogGroup.City => "city",
        CatalogGroup.ProductCategory => "category",
        CatalogGroup.Unit => "unit",
        CatalogGroup.LossReason => "loss",
        CatalogGroup.CostType => "coins",
        CatalogGroup.Brand => "badge",
        CatalogGroup.Origin => "globe",
        CatalogGroup.PaymentMethod => "card",
        CatalogGroup.TicketCategory => "support",
        _ => "catalog"
    };

    public static string ForActivity(ActivityType t) => t switch
    {
        ActivityType.Call => "call",
        ActivityType.Meeting => "meeting",
        ActivityType.Email => "email",
        _ => "task"
    };

    public static string ForChannel(CampaignChannel c) => c switch
    {
        CampaignChannel.Email => "email",
        CampaignChannel.Sms or CampaignChannel.Telesales => "call",
        CampaignChannel.Zalo => "send",
        CampaignChannel.Social or CampaignChannel.Webinar => "globe",
        CampaignChannel.Event => "meeting",
        _ => "campaign"
    };

    /// <summary>Icon tiêu đề trang theo controller / action.</summary>
    public static string? ForPage(string? controller, string? action) => controller switch
    {
        "Home" => "dashboard",
        "Activities" => "calendar",
        "Leads" => "lead",
        "Opportunities" => action == "Pipeline" ? "pipeline" : "opportunity",
        "Quotes" => "quote",
        "BusinessPlans" => "plan",
        "Contracts" => "contract",
        "Invoices" => "invoice",
        "Payments" => "wallet",
        "Customers" => "customer",
        "Contacts" => "contact",
        "Products" => "product",
        "Catalogs" => "catalog",
        "Users" => "users",
        "DemoData" => "receivable",
        "Kpi" => "trending",
        "Reports" => "report",
        "Tickets" => "support",
        "Branches" => "branch",
        "Appearance" => "palette",
        "Tasks" => "kanban",
        "Notifications" => "bell",
        "Campaigns" => action == "Segment" ? "target" : "campaign",
        "Account" => "key",
        _ => null
    };
}
