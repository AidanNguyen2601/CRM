using CRM.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CRM.Web.Controllers;

/// <summary>Bộ chọn đơn vị đang xem trên thanh trên cùng (mọi người dùng có phạm vi nhiều đơn vị)</summary>
[Authorize]
public class BranchScopeController : Controller
{
    /// <summary>id = null → xem tất cả các đơn vị trong phạm vi</summary>
    [HttpGet]
    public IActionResult Set(int? id, string? returnUrl, [FromServices] BranchScope scope)
    {
        if (id.HasValue && scope.Allowed.Contains(id.Value))
            Response.Cookies.Append(BranchScope.CookieName, id.Value.ToString(),
                new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax, HttpOnly = true });
        else Response.Cookies.Delete(BranchScope.CookieName);
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction("Index", "Home");
    }
}
