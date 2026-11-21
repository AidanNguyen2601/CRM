using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Bảo mật & phân quyền (chỉ quản trị): chính sách mật khẩu / khóa tài khoản / xác thực 2 lớp và bảng quyền theo vai trò</summary>
[Authorize(Roles = AppRoles.Admin)]
public class SecurityController : CrmControllerBase
{
    private readonly SettingsStore _settings;
    private readonly PermissionService _perms;

    public SecurityController(CrmDbContext db, SettingsStore settings, PermissionService perms) : base(db) { _settings = settings; _perms = perms; }

    /// <summary>Tất cả mã quyền theo thứ tự hiển thị</summary>
    public static IEnumerable<string> AllKeys() =>
        PermissionService.Modules.SelectMany(m => m.Ops.Select(o => m.Key + "." + o)).Concat(PermissionService.Fields.Select(f => f.Key));

    public async Task<IActionResult> Index(string tab = "policy")
    {
        ViewBag.Tab = tab == "perms" ? "perms" : "policy";
        ViewBag.Locked = await Db.Users.Where(u => u.LockoutUntil > DateTime.Now).Select(u => u.Username).ToListAsync();
        ViewBag.TwoFactorCount = await Db.Users.CountAsync(u => u.IsActive && u.TwoFactorEnabled);
        ViewBag.UserCount = await Db.Users.CountAsync(u => u.IsActive);
        return View(await _settings.GetAsync<SecuritySettings>(SecuritySettings.Key));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePolicy(SecuritySettings model, string[]? twoFactorRoles)
    {
        model.MinLength = Math.Clamp(model.MinLength, 6, 64);
        model.MaxFailedAttempts = Math.Clamp(model.MaxFailedAttempts, 0, 50);
        model.LockoutMinutes = Math.Clamp(model.LockoutMinutes, 1, 1440);
        model.PasswordExpiryDays = Math.Clamp(model.PasswordExpiryDays, 0, 3650);
        model.RequireTwoFactorRoles = (twoFactorRoles ?? Array.Empty<string>()).Where(AppRoles.All.Contains).Distinct().ToList();
        var old = await _settings.GetAsync<SecuritySettings>(SecuritySettings.Key);
        await _settings.SaveAsync(SecuritySettings.Key, model);
        var changes = new List<AuditChange>();
        void Diff(string f, object? o, object? n) { if (!Equals(o?.ToString(), n?.ToString())) changes.Add(new AuditChange(f, o?.ToString(), n?.ToString())); }
        Diff("MinLength", old.MinLength, model.MinLength); Diff("RequireLetterAndDigit", old.RequireLetterAndDigit, model.RequireLetterAndDigit);
        Diff("RequireUpperLower", old.RequireUpperLower, model.RequireUpperLower); Diff("RequireSpecial", old.RequireSpecial, model.RequireSpecial);
        Diff("MaxFailedAttempts", old.MaxFailedAttempts, model.MaxFailedAttempts); Diff("LockoutMinutes", old.LockoutMinutes, model.LockoutMinutes);
        Diff("PasswordExpiryDays", old.PasswordExpiryDays, model.PasswordExpiryDays);
        Diff("RequireTwoFactorRoles", string.Join(",", old.RequireTwoFactorRoles), string.Join(",", model.RequireTwoFactorRoles));
        if (changes.Count > 0) { AuditTrail.Write(Db, "Security", 0, "Update", I18n.T("Chính sách bảo mật"), changes); await Db.SaveChangesAsync(); }
        TempData["Success"] = I18n.T("Đã lưu chính sách bảo mật. Yêu cầu xác thực 2 lớp / hạn mật khẩu áp dụng từ lần đăng nhập tiếp theo.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Lưu bảng quyền: ô được đánh dấu = cho phép. Chỉ lưu các quyền bị tắt.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePermissions(string[]? allow)
    {
        var allowed = (allow ?? Array.Empty<string>()).ToHashSet();
        var current = await Db.RolePermissions.ToListAsync();
        var changes = new List<AuditChange>();
        foreach (var role in PermissionService.Roles)
            foreach (var key in AllKeys())
            {
                var want = allowed.Contains(role + "|" + key);
                var row = current.FirstOrDefault(r => r.Role == role && r.Permission == key);
                var had = row?.Allowed ?? true;
                if (want == had) continue;
                changes.Add(new AuditChange(role + " · " + key, had ? I18n.T("Có") : I18n.T("Không"), want ? I18n.T("Có") : I18n.T("Không")));
                if (want) Db.RolePermissions.Remove(row!);
                else if (row == null) Db.RolePermissions.Add(new RolePermission { Role = role, Permission = key, Allowed = false });
                else row.Allowed = false;
            }
        if (changes.Count > 0) AuditTrail.Write(Db, "Permission", 0, "Update", I18n.T("Bảng phân quyền"), changes);
        await Db.SaveChangesAsync();
        _perms.Invalidate();
        TempData["Success"] = I18n.T("Đã lưu bảng phân quyền ({0} thay đổi).", changes.Count);
        return RedirectToAction(nameof(Index), new { tab = "perms" });
    }

    /// <summary>Khôi phục mặc định: mọi vai trò được phép tất cả (trong giới hạn vai trò sẵn có)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPermissions()
    {
        Db.RolePermissions.RemoveRange(await Db.RolePermissions.ToListAsync());
        AuditTrail.Write(Db, "Permission", 0, "Reset", I18n.T("Khôi phục bảng phân quyền mặc định"));
        await Db.SaveChangesAsync();
        _perms.Invalidate();
        TempData["Success"] = I18n.T("Đã khôi phục bảng phân quyền mặc định.");
        return RedirectToAction(nameof(Index), new { tab = "perms" });
    }
}
