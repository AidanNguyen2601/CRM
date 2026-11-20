using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CRM.Web.Controllers;

/// <summary>Cấu hình giao diện: mỗi người tự chọn; quản trị đặt mặc định cho toàn hệ thống</summary>
[Authorize]
public class AppearanceController : Controller
{
    private readonly UiSettingsService _ui;
    public AppearanceController(UiSettingsService ui) => _ui = ui;

    public async Task<IActionResult> Index()
    {
        ViewBag.IsCustom = await _ui.UserPrefsAsync(User.GetUserId()) != null;
        ViewBag.Default = await _ui.SystemDefaultAsync();
        return View(await _ui.CurrentAsync(User));
    }

    /// <summary>scope = me (của tôi) | default (mặc định hệ thống – quản trị) | reset (về mặc định hệ thống)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(UiPrefs model, string scope = "me")
    {
        model.Normalize();
        switch (scope)
        {
            case "default" when User.IsInRole(AppRoles.Admin):
                await _ui.SaveDefaultAsync(model);
                TempData["Success"] = I18n.T("Đã lưu giao diện mặc định cho toàn hệ thống (áp dụng cho người chưa tự cấu hình).");
                break;
            case "reset":
                await _ui.SaveUserAsync(User.GetUserId(), null);
                TempData["Success"] = I18n.T("Đã chuyển về giao diện mặc định của hệ thống.");
                break;
            default:
                await _ui.SaveUserAsync(User.GetUserId(), model);
                TempData["Success"] = I18n.T("Đã lưu giao diện của bạn.");
                break;
        }
        return RedirectToAction(nameof(Index));
    }
}
