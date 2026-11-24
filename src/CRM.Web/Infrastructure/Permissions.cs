using System.Security.Claims;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>Một chức năng trong bảng phân quyền</summary>
public record PermModule(string Key, string Label, string[] Controllers, string[] Ops);

/// <summary>
/// Phân quyền chi tiết theo vai trò: Chức năng × Thao tác (Xem / Thêm / Sửa / Xóa / Xuất / Duyệt) + quyền theo trường.
/// Mặc định được phép (giữ hành vi cũ) – quản trị chỉ cần tắt những quyền muốn chặn. Quản trị hệ thống luôn đủ quyền.
/// Kết hợp với giới hạn sẵn có (nhân viên chỉ thấy bản ghi của mình / được chia sẻ, phạm vi đơn vị).
/// </summary>
public class PermissionService
{
    public const string View = "View", Create = "Create", Edit = "Edit", Delete = "Delete", Export = "Export", Approve = "Approve";
    public static readonly string[] AllOps = { View, Create, Edit, Delete, Export, Approve };
    public static string OpLabel(string op) => I18n.T(op switch
    {
        View => "Xem", Create => "Thêm", Edit => "Sửa", Delete => "Xóa", Export => "Xuất Excel", Approve => "Duyệt", _ => op
    });

    public static readonly PermModule[] Modules =
    {
        new("Customers", "Khách hàng & liên hệ", new[] { "Customers", "Contacts", "Health" }, new[] { View, Create, Edit, Delete }),
        new("Leads", "Lead (KH tiềm năng)", new[] { "Leads" }, new[] { View, Create, Edit, Delete }),
        new("Opportunities", "Cơ hội bán hàng & dự báo", new[] { "Opportunities", "Forecast" }, new[] { View, Create, Edit, Delete }),
        new("Quotes", "Báo giá", new[] { "Quotes" }, new[] { View, Create, Edit, Delete }),
        new("BusinessPlans", "Phương án kinh doanh", new[] { "BusinessPlans" }, new[] { View, Create, Edit, Delete }),
        new("Contracts", "Hợp đồng", new[] { "Contracts", "Renewals" }, new[] { View, Create, Edit, Delete }),
        new("Invoices", "Hoá đơn", new[] { "Invoices" }, new[] { View, Create, Edit, Delete }),
        new("Payments", "Thanh toán", new[] { "Payments" }, new[] { View, Create, Edit, Delete }),
        new("Activities", "Lịch làm việc & công việc", new[] { "Activities", "Tasks" }, new[] { View, Create, Edit, Delete }),
        new("Campaigns", "Chiến dịch tiếp thị & form thu lead", new[] { "Campaigns", "WebForms" }, new[] { View, Create, Edit, Delete, Export }),
        new("Tickets", "Phiếu hỗ trợ", new[] { "Tickets" }, new[] { View, Create, Edit, Delete }),
        new("Products", "Hàng hoá / dịch vụ & bảng giá", new[] { "Products", "PriceLists" }, new[] { View, Create, Edit, Delete }),
        new("Kpi", "KPI & hoa hồng", new[] { "Kpi", "Commissions" }, new[] { View }),
        new("SalesPlans", "Kế hoạch bán hàng", new[] { "SalesPlans" }, new[] { View }),
        new("Reports", "Báo cáo", new[] { "Reports", "ReportSchedules" }, new[] { View, Export }),
        new("Emails", "Gửi email", new[] { "Emails" }, new[] { View, Create }),
        new("Import", "Nhập Excel & gộp trùng", new[] { "Import", "Duplicates" }, new[] { View })
    };

    /// <summary>Quyền theo trường dữ liệu</summary>
    public static readonly (string Key, string Label)[] Fields =
    {
        ("Field.Cost", "Xem giá vốn, lãi gộp, lợi nhuận")
    };

    /// <summary>Vai trò được cấu hình (Admin luôn đủ quyền)</summary>
    public static readonly string[] Roles = { AppRoles.Manager, AppRoles.Sales, AppRoles.Support };

    private static readonly Dictionary<string, PermModule> ByController =
        Modules.SelectMany(m => m.Controllers.Select(c => (c, m))).ToDictionary(x => x.c, x => x.m, StringComparer.OrdinalIgnoreCase);

    private readonly IServiceScopeFactory _scopes;
    private volatile Dictionary<string, bool>? _denied;   // khóa "Role|Permission" → Allowed

    public PermissionService(IServiceScopeFactory scopes) => _scopes = scopes;

    public void Invalidate() => _denied = null;

    private Dictionary<string, bool> Map()
    {
        var m = _denied;
        if (m != null) return m;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        m = db.RolePermissions.AsNoTracking().ToList().ToDictionary(r => r.Role + "|" + r.Permission, r => r.Allowed);
        _denied = m;
        return m;
    }

    public bool RoleCan(string role, string permission) =>
        role == AppRoles.Admin || !Map().TryGetValue(role + "|" + permission, out var allowed) || allowed;

    public bool Can(ClaimsPrincipal user, string permission)
    {
        if (user.Identity?.IsAuthenticated != true) return false;
        var role = user.FindFirstValue(ClaimTypes.Role) ?? "";
        return RoleCan(role, permission);
    }

    /// <summary>Xem được chức năng của controller (dùng ẩn / hiện menu)</summary>
    public bool CanController(ClaimsPrincipal user, string controller) =>
        !ByController.TryGetValue(controller, out var m) || Can(user, m.Key + "." + View);

    /// <summary>Quyền cần cho một action theo quy ước tên action; null = không kiểm soát</summary>
    public static string? Required(string controller, string action, string method, Func<string, string?> value)
    {
        if (!ByController.TryGetValue(controller, out var m)) return null;
        var a = action.ToLowerInvariant();
        string op;
        if (controller == "Emails") op = a is "compose" or "campaign" ? Create : View;
        else if (a.StartsWith("export")) op = Export;
        else if (a.StartsWith("delete") || a.StartsWith("remove")) op = Delete;
        else if (a is "decide" or "approve") op = Approve;
        else if (a == "create") op = Create;
        else if (a == "save" && method == "POST") op = int.TryParse(value("Id") ?? value("id"), out var id) && id > 0 ? Edit : Create;
        else if (a.StartsWith("edit")) op = Edit;
        else op = method == "POST" ? Edit : View;
        if (!m.Ops.Contains(op)) op = m.Ops.Contains(Edit) && op != View ? Edit : View;
        return m.Key + "." + op;
    }
}

/// <summary>Áp phân quyền chi tiết cho mọi action (theo PermissionService.Required)</summary>
public class PermissionFilter : IActionFilter
{
    private readonly PermissionService _perms;
    public PermissionFilter(PermissionService perms) => _perms = perms;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var http = context.HttpContext;
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true || user.IsInRole(AppRoles.Admin)) return;
        var controller = context.RouteData.Values["controller"]?.ToString() ?? "";
        var action = context.RouteData.Values["action"]?.ToString() ?? "";
        var req = http.Request;
        string? Value(string k) => req.HasFormContentType && req.Form.TryGetValue(k, out var f) ? f.ToString()
            : context.RouteData.Values.TryGetValue(k, out var r) ? r?.ToString() : req.Query[k].ToString() is { Length: > 0 } q ? q : null;
        var perm = PermissionService.Required(controller, action, req.Method, Value);
        if (perm == null || _perms.Can(user, perm)) return;

        var accept = req.Headers.Accept.ToString();
        if (accept.Contains("application/json") || req.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            context.Result = new ObjectResult(new { message = I18n.T("Bạn không có quyền thực hiện thao tác này.") }) { StatusCode = 403 };
            return;
        }
        var tempData = http.RequestServices.GetRequiredService<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory>().GetTempData(http);
        tempData["Error"] = I18n.T("Bạn không có quyền \"{0}\" với chức năng này. Liên hệ quản trị nếu cần.", PermissionService.OpLabel(perm.Split('.')[1]));
        var referer = req.Headers.Referer.ToString();
        context.Result = !string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var u) && u.Host == req.Host.Host && req.Method == "POST"
            ? new RedirectResult(u.PathAndQuery) : new RedirectToActionResult("Index", "Home", null);
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
