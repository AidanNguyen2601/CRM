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
        ["plus"] = "<path d='M5 12h14M12 5v14'/>",
        ["trending"] = "<path d='M22 7l-8.5 8.5-5-5L2 17'/><path d='M16 7h6v6'/>",
        ["trophy"] = "<path d='M6 9H4.5a2.5 2.5 0 0 1 0-5H6M18 9h1.5a2.5 2.5 0 0 0 0-5H18M4 22h16M10 14.66V17c0 .55-.47.98-.97 1.21C7.85 18.75 7 20.24 7 22M14 14.66V17c0 .55.47.98.97 1.21C16.15 18.75 17 20.24 17 22'/><path d='M18 2H6v7a6 6 0 0 0 12 0z'/>",
        ["alert"] = "<path d='M21.73 18l-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3'/><path d='M12 9v4M12 17h.01'/>",
        ["call"] = "<path d='M22 16.92v3a2 2 0 0 1-2.18 2 19.79 19.79 0 0 1-8.63-3.07 19.5 19.5 0 0 1-6-6 19.79 19.79 0 0 1-3.07-8.67A2 2 0 0 1 4.11 2h3a2 2 0 0 1 2 1.72c.13.96.36 1.9.7 2.81a2 2 0 0 1-.45 2.11L8.09 9.91a16 16 0 0 0 6 6l1.27-1.27a2 2 0 0 1 2.11-.45c.91.34 1.85.57 2.81.7A2 2 0 0 1 22 16.92z'/>",
        ["meeting"] = "<path d='M18 21a8 8 0 0 0-16 0'/><circle cx='10' cy='8' r='5'/><path d='M22 20c0-3.37-2-6.5-4-8a5 5 0 0 0-.45-8.3'/>",
        ["email"] = "<rect x='2' y='4' width='20' height='16' rx='2'/><path d='M22 7l-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7'/>",
        ["task"] = "<rect x='3' y='3' width='18' height='18' rx='2'/><path d='M9 12l2 2 4-4'/>",
        ["catalog"] = "<path d='M4 19.5v-15A2.5 2.5 0 0 1 6.5 2H20v20H6.5a2.5 2.5 0 0 1 0-5H20'/>",
        ["menu"] = "<path d='M4 6h16M4 12h16M4 18h16'/>",
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
        _ => "catalog"
    };

    public static string ForActivity(ActivityType t) => t switch
    {
        ActivityType.Call => "call",
        ActivityType.Meeting => "meeting",
        ActivityType.Email => "email",
        _ => "task"
    };

    /// <summary>Icon tiêu đề trang theo controller / action.</summary>
    public static string? ForPage(string? controller, string? action) => controller switch
    {
        "Home" => "dashboard",
        "Activities" => "calendar",
        "Leads" => "lead",
        "Opportunities" => action == "Pipeline" ? "pipeline" : "opportunity",
        "Quotes" => "quote",
        "Customers" => "customer",
        "Contacts" => "contact",
        "Products" => "product",
        "Catalogs" => "catalog",
        "Users" => "users",
        "Account" => "key",
        _ => null
    };
}
