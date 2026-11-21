using System.Security.Cryptography;
using System.Text;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CRM.Web.Infrastructure;

/// <summary>Chính sách bảo mật đăng nhập (lưu AppSettings "security")</summary>
public class SecuritySettings
{
    public const string Key = "security";
    public int MinLength { get; set; } = 8;
    /// <summary>Bắt buộc có cả chữ và số</summary>
    public bool RequireLetterAndDigit { get; set; } = true;
    /// <summary>Bắt buộc có chữ hoa và chữ thường</summary>
    public bool RequireUpperLower { get; set; }
    /// <summary>Bắt buộc có ký tự đặc biệt</summary>
    public bool RequireSpecial { get; set; }
    /// <summary>Số lần đăng nhập sai liên tiếp trước khi khóa tạm (0 = không khóa)</summary>
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    /// <summary>Số ngày phải đổi mật khẩu (0 = không bắt buộc)</summary>
    public int PasswordExpiryDays { get; set; }
    /// <summary>Vai trò bắt buộc dùng xác thực 2 lớp</summary>
    public List<string> RequireTwoFactorRoles { get; set; } = new();

    /// <summary>Kiểm tra mật khẩu mới theo chính sách; trả về thông báo lỗi hoặc null</summary>
    public string? Validate(string? password, string? username = null)
    {
        var p = password ?? "";
        var errors = new List<string>();
        if (p.Length < Math.Max(6, MinLength)) errors.Add(I18n.T("tối thiểu {0} ký tự", Math.Max(6, MinLength)));
        if (RequireLetterAndDigit && (!p.Any(char.IsLetter) || !p.Any(char.IsDigit))) errors.Add(I18n.T("có cả chữ và số"));
        if (RequireUpperLower && (!p.Any(char.IsUpper) || !p.Any(char.IsLower))) errors.Add(I18n.T("có chữ hoa và chữ thường"));
        if (RequireSpecial && p.All(char.IsLetterOrDigit)) errors.Add(I18n.T("có ký tự đặc biệt"));
        if (!string.IsNullOrEmpty(username) && p.Contains(username, StringComparison.OrdinalIgnoreCase)) errors.Add(I18n.T("không chứa tên đăng nhập"));
        return errors.Count == 0 ? null : I18n.T("Mật khẩu phải {0}.", string.Join(", ", errors));
    }

    public string Describe() => Validate("") ?? "";
}

/// <summary>Mã xác thực một lần theo thời gian (TOTP – RFC 6238, tương thích Google / Microsoft Authenticator)</summary>
public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret() => Base32(RandomNumberGenerator.GetBytes(20));

    public static string Base32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] FromBase32(string s)
    {
        s = s.Replace(" ", "").TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in s)
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException();
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return bytes.ToArray();
    }

    public static string Code(string secret, long step)
    {
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        using var h = new HMACSHA1(FromBase32(secret));
        var hash = h.ComputeHash(counter);
        var o = hash[^1] & 0x0F;
        var bin = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    public static long CurrentStep => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

    /// <summary>Kiểm tra mã (chấp nhận lệch ±1 bước 30 giây do đồng hồ điện thoại)</summary>
    public static bool Verify(string? secret, string? code)
    {
        if (string.IsNullOrEmpty(secret)) return false;
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (code.Length != 6) return false;
        var now = CurrentStep;
        for (var d = -1; d <= 1; d++)
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, now + d)), Encoding.ASCII.GetBytes(code))) return true;
        return false;
    }

    /// <summary>Liên kết otpauth:// (mở trực tiếp ứng dụng Authenticator trên điện thoại)</summary>
    public static string Uri(string issuer, string account, string secret) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}?secret={secret}&issuer={System.Uri.EscapeDataString(issuer)}&digits=6&period=30";

    /// <summary>Hiển thị khóa theo nhóm 4 ký tự cho dễ nhập tay</summary>
    public static string Pretty(string secret) => string.Join(" ", Enumerable.Range(0, (secret.Length + 3) / 4).Select(i => secret.Substring(i * 4, Math.Min(4, secret.Length - i * 4))));
}

/// <summary>
/// Buộc hoàn tất yêu cầu bảo mật sau khi đăng nhập: thiết lập xác thực 2 lớp (vai trò bắt buộc) hoặc đổi mật khẩu hết hạn.
/// Trong lúc chưa hoàn tất chỉ dùng được các trang của Account (và đổi ngôn ngữ).
/// </summary>
public class SecurityRequirementFilter : IActionFilter
{
    public const string MfaSetupClaim = "crm:mfa-setup";
    public const string PwdExpiredClaim = "crm:pwd-expired";

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true) return;
        var controller = context.RouteData.Values["controller"]?.ToString();
        if (controller is "Account" or "Language") return;
        if (user.HasClaim(c => c.Type == MfaSetupClaim))
            context.Result = new RedirectToActionResult("TwoFactor", "Account", new { required = true });
        else if (user.HasClaim(c => c.Type == PwdExpiredClaim))
            context.Result = new RedirectToActionResult("ChangePassword", "Account", new { expired = true });
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}

public static class SecurityText
{
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
