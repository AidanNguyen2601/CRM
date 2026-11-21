using System.Security.Claims;
using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>
/// Đăng nhập / đăng xuất, khóa tạm khi sai mật khẩu nhiều lần, xác thực 2 lớp (TOTP), quên / đặt lại mật khẩu qua email,
/// đổi mật khẩu theo chính sách, trang "Tài khoản của tôi". Mọi lần đăng nhập được ghi vào lịch sử đăng nhập.
/// </summary>
public class AccountController : Controller
{
    private const string PendingCookie = "crm.2fa";
    private readonly CrmDbContext _db;
    private readonly IPasswordHasher<AppUser> _hasher;
    private readonly SettingsStore _settings;
    private readonly ITimeLimitedDataProtector _pending;
    private readonly IDataProtector _totp;

    public AccountController(CrmDbContext db, IPasswordHasher<AppUser> hasher, SettingsStore settings, IDataProtectionProvider dp)
    {
        _db = db; _hasher = hasher; _settings = settings;
        _pending = dp.CreateProtector("CRM.2fa.pending").ToTimeLimitedDataProtector();
        _totp = dp.CreateProtector("CRM.Totp");
    }

    private Task<SecuritySettings> PolicyAsync() => _settings.GetAsync<SecuritySettings>(SecuritySettings.Key);

    // ======================= Đăng nhập =======================

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Home");
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [AllowAnonymous]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);
        var policy = await PolicyAsync();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == vm.Username);
        var generic = I18n.T("Tên đăng nhập hoặc mật khẩu không đúng, hoặc tài khoản đã bị khóa.");

        if (user == null) { await LogAsync(null, vm.Username, false, "Tài khoản không tồn tại"); return Fail(vm, generic); }
        if (!user.IsActive) { await LogAsync(user.Id, vm.Username, false, "Tài khoản ngưng hoạt động"); return Fail(vm, generic); }
        if (user.LockoutUntil > DateTime.Now)
        {
            await LogAsync(user.Id, vm.Username, false, "Đang bị khóa tạm");
            return Fail(vm, I18n.T("Tài khoản đang tạm khóa đến {0} do đăng nhập sai nhiều lần. Vui lòng thử lại sau hoặc liên hệ quản trị.", user.LockoutUntil.Value.ToString("HH:mm dd/MM")));
        }
        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, vm.Password) == PasswordVerificationResult.Failed)
        {
            var locked = await RegisterFailureAsync(user, policy);
            await LogAsync(user.Id, vm.Username, false, locked ? "Sai mật khẩu – khóa tạm" : "Sai mật khẩu");
            return Fail(vm, locked ? I18n.T("Đăng nhập sai quá {0} lần – tài khoản bị tạm khóa {1} phút.", policy.MaxFailedAttempts, policy.LockoutMinutes) : generic);
        }

        user.FailedLoginCount = 0; user.LockoutUntil = null;
        if (user.TwoFactorEnabled && !string.IsNullOrEmpty(user.TwoFactorSecret))
        {
            await _db.SaveChangesAsync();
            // Bước 2: lưu tạm trạng thái đã qua mật khẩu (mã hóa, hiệu lực 5 phút) rồi hỏi mã xác thực
            Response.Cookies.Append(PendingCookie, _pending.Protect($"{user.Id}|{(vm.RememberMe ? 1 : 0)}|0", TimeSpan.FromMinutes(5)),
                new CookieOptions { HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Strict, Secure = Request.IsHttps, MaxAge = TimeSpan.FromMinutes(5) });
            return RedirectToAction(nameof(LoginTwoFactor), new { returnUrl = vm.ReturnUrl });
        }
        return await CompleteLoginAsync(user, vm.RememberMe, vm.ReturnUrl, policy, "Đăng nhập");
    }

    private IActionResult Fail(LoginViewModel vm, string message)
    {
        ModelState.AddModelError(string.Empty, message);
        return View("Login", vm);
    }

    /// <summary>Tăng số lần sai; đạt ngưỡng → khóa tạm và báo quản trị. Trả về true nếu vừa bị khóa.</summary>
    private async Task<bool> RegisterFailureAsync(AppUser user, SecuritySettings policy)
    {
        user.FailedLoginCount++;
        var locked = policy.MaxFailedAttempts > 0 && user.FailedLoginCount >= policy.MaxFailedAttempts;
        if (locked)
        {
            user.LockoutUntil = DateTime.Now.AddMinutes(Math.Max(1, policy.LockoutMinutes));
            user.FailedLoginCount = 0;
            var notify = HttpContext.RequestServices.GetRequiredService<NotificationService>();
            foreach (var adminId in await _db.Users.Where(u => u.IsActive && u.Role == AppRoles.Admin).Select(u => u.Id).ToListAsync())
                notify.Add(adminId, "security", I18n.T("Tài khoản {0} bị tạm khóa do đăng nhập sai nhiều lần", user.Username), "/Audit/Logins?FailedOnly=true",
                    body: I18n.T("IP: {0}", HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?"));
        }
        await _db.SaveChangesAsync();
        return locked;
    }

    private async Task<IActionResult> CompleteLoginAsync(AppUser user, bool remember, string? returnUrl, SecuritySettings policy, string reason)
    {
        user.LastLoginAt = DateTime.Now;
        await _db.SaveChangesAsync();
        await SignInAsync(user, remember, policy);
        await LogAsync(user.Id, user.Username, true, reason);
        Response.Cookies.Delete(PendingCookie);
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction("Index", "Home");
    }

    /// <summary>Tạo cookie đăng nhập; kèm cờ "phải thiết lập 2 lớp" / "mật khẩu hết hạn" nếu cần</summary>
    private async Task SignInAsync(AppUser user, bool remember, SecuritySettings policy)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
            new(ClaimsExtensions.FullNameClaim, user.FullName)
        };
        if (!user.TwoFactorEnabled && policy.RequireTwoFactorRoles.Contains(user.Role)) claims.Add(new(SecurityRequirementFilter.MfaSetupClaim, "1"));
        if (policy.PasswordExpiryDays > 0 && (user.PasswordChangedAt ?? user.CreatedAt) < DateTime.Now.AddDays(-policy.PasswordExpiryDays))
            claims.Add(new(SecurityRequirementFilter.PwdExpiredClaim, "1"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { IsPersistent = remember });
    }

    private async Task LogAsync(int? userId, string username, bool success, string reason)
    {
        _db.LoginLogs.Add(new LoginLog
        {
            UserId = userId, Username = username.Length > 50 ? username[..50] : username, Success = success, Reason = reason,
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers.UserAgent.ToString() is { Length: > 300 } ua ? ua[..300] : Request.Headers.UserAgent.ToString(),
            At = DateTime.Now
        });
        await _db.SaveChangesAsync();
    }

    // ---------- Bước 2: mã xác thực ----------

    private (int UserId, bool Remember, int Tries)? ReadPending()
    {
        try
        {
            var parts = _pending.Unprotect(Request.Cookies[PendingCookie] ?? "").Split('|');
            return (int.Parse(parts[0]), parts[1] == "1", int.Parse(parts[2]));
        }
        catch { return null; }
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult LoginTwoFactor(string? returnUrl)
    {
        if (ReadPending() == null) return RedirectToAction(nameof(Login), new { returnUrl });
        ViewBag.ReturnUrl = returnUrl;
        return View();
    }

    [AllowAnonymous]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> LoginTwoFactor(string? code, string? returnUrl)
    {
        var p = ReadPending();
        if (p == null)
        {
            TempData["Error"] = I18n.T("Phiên xác thực đã hết hạn, vui lòng đăng nhập lại.");
            return RedirectToAction(nameof(Login), new { returnUrl });
        }
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == p.Value.UserId && u.IsActive);
        if (user == null || user.LockoutUntil > DateTime.Now) return RedirectToAction(nameof(Login), new { returnUrl });
        var policy = await PolicyAsync();
        if (Totp.Verify(Unprotect(user.TwoFactorSecret), code))
            return await CompleteLoginAsync(user, p.Value.Remember, returnUrl, policy, "Đăng nhập (xác thực 2 lớp)");

        var locked = await RegisterFailureAsync(user, policy);
        await LogAsync(user.Id, user.Username, false, "Sai mã xác thực 2 lớp");
        if (locked || p.Value.Tries >= 4)
        {
            Response.Cookies.Delete(PendingCookie);
            TempData["Error"] = locked ? I18n.T("Nhập sai quá nhiều lần – tài khoản bị tạm khóa {0} phút.", policy.LockoutMinutes) : I18n.T("Nhập sai mã quá nhiều lần, vui lòng đăng nhập lại.");
            return RedirectToAction(nameof(Login), new { returnUrl });
        }
        Response.Cookies.Append(PendingCookie, _pending.Protect($"{user.Id}|{(p.Value.Remember ? 1 : 0)}|{p.Value.Tries + 1}", TimeSpan.FromMinutes(5)),
            new CookieOptions { HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Strict, Secure = Request.IsHttps, MaxAge = TimeSpan.FromMinutes(5) });
        ModelState.AddModelError(string.Empty, I18n.T("Mã xác thực không đúng hoặc đã hết hạn."));
        ViewBag.ReturnUrl = returnUrl;
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    // ======================= Quên / đặt lại mật khẩu =======================

    [AllowAnonymous]
    [HttpGet]
    public IActionResult ForgotPassword() => View();

    /// <summary>Gửi liên kết đặt lại mật khẩu (hiệu lực 30 phút). Luôn trả cùng một thông báo để không lộ tài khoản có tồn tại hay không.</summary>
    [AllowAnonymous]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(string? login)
    {
        var mail = HttpContext.RequestServices.GetRequiredService<EmailService>();
        var s = await mail.SettingsAsync();
        if (!s.IsReady)
        {
            TempData["Error"] = I18n.T("Hệ thống chưa cấu hình gửi email. Vui lòng liên hệ quản trị để đặt lại mật khẩu.");
            return RedirectToAction(nameof(ForgotPassword));
        }
        login = login?.Trim();
        if (!string.IsNullOrEmpty(login))
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.IsActive && u.Email != null && u.Email != "" && (u.Username == login || u.Email == login));
            // Chống lạm dụng: tối đa 3 yêu cầu / giờ / tài khoản
            if (user != null && await _db.PasswordResetTokens.CountAsync(t => t.UserId == user.Id && t.CreatedAt > DateTime.Now.AddHours(-1)) < 3)
            {
                var token = SecurityText.NewToken();
                _db.PasswordResetTokens.Add(new PasswordResetToken { UserId = user.Id, TokenHash = SecurityText.Hash(token), ExpiresAt = DateTime.Now.AddMinutes(30) });
                var link = EmailService.Absolute(s, Url.Action(nameof(ResetPassword), new { token }));
                if (string.IsNullOrWhiteSpace(s.SiteUrl)) link = $"{Request.Scheme}://{Request.Host}{Url.Action(nameof(ResetPassword), new { token })}";
                var tpl = await _db.EmailTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Code == PlatformDefaults.ResetCode && t.IsActive);
                var vars = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["NhanVien"] = user.FullName, ["Link"] = link, ["Ngay"] = DateTime.Today.ToDate() };
                mail.Queue(user.Email!, tpl != null ? EmailService.Merge(tpl.Subject, vars) : I18n.T("[CRM] Đặt lại mật khẩu"),
                    EmailService.ToHtml(tpl != null ? EmailService.Merge(tpl.Body, vars) : link), "System", user.Id);
                await _db.SaveChangesAsync();
            }
        }
        TempData["Success"] = I18n.T("Nếu thông tin khớp với một tài khoản có email, liên kết đặt lại mật khẩu đã được gửi tới email đó (hiệu lực 30 phút).");
        return RedirectToAction(nameof(ForgotPassword));
    }

    private async Task<PasswordResetToken?> FindTokenAsync(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var hash = SecurityText.Hash(token);
        return await _db.PasswordResetTokens.Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > DateTime.Now && t.User!.IsActive);
    }

    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> ResetPassword(string? token)
    {
        if (await FindTokenAsync(token) == null) return View("ResetPassword", null);
        ViewBag.Policy = (await PolicyAsync()).Describe();
        return View("ResetPassword", token);
    }

    [AllowAnonymous]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(string? token, string? newPassword, string? confirmPassword)
    {
        var t = await FindTokenAsync(token);
        if (t == null) return View("ResetPassword", null);
        var policy = await PolicyAsync();
        var error = newPassword != confirmPassword ? I18n.T("Xác nhận mật khẩu không khớp") : policy.Validate(newPassword, t.User!.Username);
        if (error != null)
        {
            ModelState.AddModelError(string.Empty, error);
            ViewBag.Policy = policy.Describe();
            return View("ResetPassword", token);
        }
        var user = t.User!;
        user.PasswordHash = _hasher.HashPassword(user, newPassword!);
        user.PasswordChangedAt = DateTime.Now; user.FailedLoginCount = 0; user.LockoutUntil = null;
        // Vô hiệu tất cả mã đặt lại còn lại của tài khoản
        foreach (var other in await _db.PasswordResetTokens.Where(x => x.UserId == user.Id && x.UsedAt == null).ToListAsync()) other.UsedAt = DateTime.Now;
        AuditTrail.Write(_db, "Security", user.Id, "Reset", I18n.T("Đặt lại mật khẩu qua email: {0}", user.Username));
        await _db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã đặt mật khẩu mới. Vui lòng đăng nhập.");
        return RedirectToAction(nameof(Login));
    }

    // ======================= Đổi mật khẩu =======================

    [HttpGet]
    public async Task<IActionResult> ChangePassword(bool expired = false)
    {
        ViewBag.Expired = expired || User.HasClaim(c => c.Type == SecurityRequirementFilter.PwdExpiredClaim);
        ViewBag.Policy = (await PolicyAsync()).Describe();
        return View(new ChangePasswordViewModel());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel vm)
    {
        var policy = await PolicyAsync();
        ViewBag.Policy = policy.Describe();
        ViewBag.Expired = User.HasClaim(c => c.Type == SecurityRequirementFilter.PwdExpiredClaim);
        if (!ModelState.IsValid) return View(vm);
        var user = await _db.Users.FindAsync(User.GetUserId());
        if (user == null) return NotFound();
        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, vm.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            ModelState.AddModelError(nameof(vm.CurrentPassword), I18n.T("Mật khẩu hiện tại không đúng"));
            return View(vm);
        }
        if (policy.Validate(vm.NewPassword, user.Username) is { } err) { ModelState.AddModelError(nameof(vm.NewPassword), err); return View(vm); }
        if (vm.NewPassword == vm.CurrentPassword) { ModelState.AddModelError(nameof(vm.NewPassword), I18n.T("Mật khẩu mới phải khác mật khẩu hiện tại.")); return View(vm); }

        user.PasswordHash = _hasher.HashPassword(user, vm.NewPassword);
        user.PasswordChangedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        await SignInAsync(user, true, policy);   // cập nhật cookie (bỏ cờ mật khẩu hết hạn)
        TempData["Success"] = I18n.T("Đổi mật khẩu thành công.");
        return RedirectToAction("Index", "Home");
    }

    // ======================= Tài khoản của tôi =======================

    [HttpGet]
    public async Task<IActionResult> Profile()
    {
        var user = await _db.Users.Include(u => u.Branch).AsNoTracking().FirstOrDefaultAsync(u => u.Id == User.GetUserId());
        if (user == null) return NotFound();
        ViewBag.Logins = await _db.LoginLogs.AsNoTracking().Where(l => l.UserId == user.Id).OrderByDescending(l => l.Id).Take(8).ToListAsync();
        ViewBag.MailReady = await HttpContext.RequestServices.GetRequiredService<EmailService>().IsReadyAsync();
        ViewBag.TwoFactorRequired = (await PolicyAsync()).RequireTwoFactorRoles.Contains(user.Role);
        return View(user);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Profile(string? email, bool emailNotifications)
    {
        var user = await _db.Users.FindAsync(User.GetUserId());
        if (user == null) return NotFound();
        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (email != null && !EmailService.IsValidList(email))
        {
            TempData["Error"] = I18n.T("Email không hợp lệ.");
            return RedirectToAction(nameof(Profile));
        }
        user.Email = email;
        user.EmailNotifications = emailNotifications && email != null;
        await _db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu thông tin tài khoản.");
        return RedirectToAction(nameof(Profile));
    }

    // ======================= Xác thực 2 lớp =======================

    private string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try { return _totp.Unprotect(cipher); } catch { return null; }
    }

    [HttpGet]
    public async Task<IActionResult> TwoFactor(bool required = false)
    {
        var user = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == User.GetUserId());
        ViewBag.Required = required || User.HasClaim(c => c.Type == SecurityRequirementFilter.MfaSetupClaim);
        ViewBag.Enabled = user.TwoFactorEnabled;
        if (!user.TwoFactorEnabled)
        {
            var secret = Totp.NewSecret();
            ViewBag.Secret = secret;
            ViewBag.SecretProtected = _totp.Protect(secret);
            ViewBag.Uri = Totp.Uri("CRM", user.Username, secret);
        }
        ViewBag.Locked = (await PolicyAsync()).RequireTwoFactorRoles.Contains(user.Role);
        return View(user);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EnableTwoFactor(string secretProtected, string? code)
    {
        var secret = Unprotect(secretProtected);
        if (secret == null) return BadRequest();
        if (!Totp.Verify(secret, code))
        {
            TempData["Error"] = I18n.T("Mã xác thực không đúng. Kiểm tra lại giờ trên điện thoại và nhập mã đang hiển thị.");
            return RedirectToAction(nameof(TwoFactor));
        }
        var user = await _db.Users.FindAsync(User.GetUserId());
        if (user == null) return NotFound();
        user.TwoFactorEnabled = true;
        user.TwoFactorSecret = _totp.Protect(secret);
        AuditTrail.Write(_db, "Security", user.Id, "Update", I18n.T("Bật xác thực 2 lớp: {0}", user.Username));
        await _db.SaveChangesAsync();
        await SignInAsync(user, true, await PolicyAsync());
        TempData["Success"] = I18n.T("Đã bật xác thực 2 lớp. Từ lần đăng nhập sau cần nhập mã từ ứng dụng Authenticator.");
        return RedirectToAction(nameof(Profile));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DisableTwoFactor(string? password)
    {
        var user = await _db.Users.FindAsync(User.GetUserId());
        if (user == null) return NotFound();
        if ((await PolicyAsync()).RequireTwoFactorRoles.Contains(user.Role))
        {
            TempData["Error"] = I18n.T("Vai trò của bạn bắt buộc dùng xác thực 2 lớp.");
            return RedirectToAction(nameof(TwoFactor));
        }
        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? "") == PasswordVerificationResult.Failed)
        {
            TempData["Error"] = I18n.T("Mật khẩu không đúng.");
            return RedirectToAction(nameof(TwoFactor));
        }
        user.TwoFactorEnabled = false; user.TwoFactorSecret = null;
        AuditTrail.Write(_db, "Security", user.Id, "Update", I18n.T("Tắt xác thực 2 lớp: {0}", user.Username));
        await _db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã tắt xác thực 2 lớp.");
        return RedirectToAction(nameof(Profile));
    }

    [AllowAnonymous]
    public IActionResult AccessDenied() => View();
}
