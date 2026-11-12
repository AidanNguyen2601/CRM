using CRM.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace CRM.Web.Controllers;

/// <summary>Đổi ngôn ngữ giao diện (Việt / Anh), lưu vào cookie 1 năm.</summary>
[AllowAnonymous]
public class LanguageController : Controller
{
    [HttpGet]
    public IActionResult Set(string lang, string? returnUrl = null)
    {
        var uiCulture = lang == "en" ? I18n.English : I18n.Vietnamese;
        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(System.Globalization.CultureInfo.CurrentCulture.Name, uiCulture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax });

        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : RedirectToAction("Index", "Home");
    }
}
