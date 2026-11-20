using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.Text.RegularExpressions;
using CRM.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CRM.Web.Infrastructure;

/// <summary>Cấu hình giao diện: chủ đề, màu chủ đạo, font, cỡ chữ, vị trí và kiểu menu</summary>
public class UiPrefs
{
    public static readonly string[] Themes = { "light", "dark", "auto" };
    public static readonly string[] Menus = { "left", "right", "top", "bottom" };
    public static readonly string[] Sidebars = { "dark", "light", "accent" };
    public static readonly int[] FontSizes = { 13, 14, 15, 16 };
    public static readonly string[] Swatches = { "#0f9d8a", "#2563eb", "#7c3aed", "#db2777", "#dc2626", "#ea580c", "#ca8a04", "#16a34a", "#0891b2", "#475569" };

    /// <summary>Font: khóa → danh sách font CSS (chỉ dùng font có sẵn trên máy, không tải từ Internet – chạy được offline)</summary>
    public static readonly Dictionary<string, (string Label, string Css)> Fonts = new()
    {
        ["system"] = ("Segoe UI / hệ thống", "\"Segoe UI\", Roboto, \"Helvetica Neue\", Arial, sans-serif"),
        ["arial"] = ("Arial", "Arial, Helvetica, sans-serif"),
        ["roboto"] = ("Roboto", "Roboto, \"Segoe UI\", Arial, sans-serif"),
        ["tahoma"] = ("Tahoma", "Tahoma, Verdana, sans-serif"),
        ["verdana"] = ("Verdana", "Verdana, Geneva, sans-serif"),
        ["georgia"] = ("Georgia (có chân)", "Georgia, \"Times New Roman\", serif"),
    };

    public string Theme { get; set; } = "light";
    public string Accent { get; set; } = "#0f9d8a";
    public string Font { get; set; } = "system";
    public int FontSize { get; set; } = 14;
    public string Menu { get; set; } = "left";
    public string Sidebar { get; set; } = "dark";

    /// <summary>Chuẩn hoá: giá trị lạ → mặc định</summary>
    public UiPrefs Normalize()
    {
        if (!Themes.Contains(Theme)) Theme = "light";
        if (!Menus.Contains(Menu)) Menu = "left";
        if (!Sidebars.Contains(Sidebar)) Sidebar = "dark";
        if (!Fonts.ContainsKey(Font ?? "")) Font = "system";
        if (!FontSizes.Contains(FontSize)) FontSize = 14;
        if (string.IsNullOrEmpty(Accent) || !Regex.IsMatch(Accent, "^#[0-9a-fA-F]{6}$")) Accent = "#0f9d8a";
        Accent = Accent.ToLowerInvariant();
        return this;
    }

    public UiPrefs Clone() => (UiPrefs)MemberwiseClone();

    public string ToJson() => JsonSerializer.Serialize(this);
    public static UiPrefs? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<UiPrefs>(json)?.Normalize(); } catch { return null; }
    }

    // ----- Biến CSS sinh từ cấu hình -----
    // Giá trị chưa hợp lệ (trước khi Normalize) → dùng màu mặc định, không ném lỗi
    private (int R, int G, int B) Rgb => Accent != null && Regex.IsMatch(Accent, "^#[0-9a-fA-F]{6}$")
        ? (Convert.ToInt32(Accent[1..3], 16), Convert.ToInt32(Accent[3..5], 16), Convert.ToInt32(Accent[5..7], 16))
        : (15, 157, 138);
    private static string Hex(double r, double g, double b) =>
        $"#{(int)Math.Clamp(r, 0, 255):x2}{(int)Math.Clamp(g, 0, 255):x2}{(int)Math.Clamp(b, 0, 255):x2}";
    [JsonIgnore, ValidateNever] public string AccentDark { get { var (r, g, b) = Rgb; return Hex(r * .8, g * .8, b * .8); } }
    [JsonIgnore, ValidateNever] public string AccentLight { get { var (r, g, b) = Rgb; return Hex(r + (255 - r) * .55, g + (255 - g) * .55, b + (255 - b) * .55); } }
    [JsonIgnore, ValidateNever] public string AccentRgb { get { var (r, g, b) = Rgb; return $"{r},{g},{b}"; } }

    /// <summary>Chuỗi style gắn vào thẻ html</summary>
    public string CssVars() => string.Create(CultureInfo.InvariantCulture,
        $"--accent:{Accent};--accent-dark:{AccentDark};--accent-light:{AccentLight};--accent-rgb:{AccentRgb};--app-font:{Fonts[Font].Css.Replace("\"", "'")};font-size:{FontSize}px");
}

/// <summary>
/// Đọc / lưu cấu hình giao diện: của người dùng (cột Users.UiPrefs) → mặc định hệ thống (bảng AppSettings) → mặc định có sẵn.
/// Có cache, xoá cache khi lưu.
/// </summary>
public class UiSettingsService
{
    public const string DefaultKey = "ui.default";
    private readonly CrmDbContext _db;
    private readonly IMemoryCache _cache;

    public UiSettingsService(CrmDbContext db, IMemoryCache cache) { _db = db; _cache = cache; }

    public async Task<UiPrefs> SystemDefaultAsync() =>
        (await _cache.GetOrCreateAsync(DefaultKey, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            var json = await _db.AppSettings.Where(s => s.Key == DefaultKey).Select(s => s.Value).FirstOrDefaultAsync();
            return UiPrefs.FromJson(json) ?? new UiPrefs();
        }))!.Clone();

    /// <summary>Cấu hình riêng của người dùng (null = đang dùng mặc định hệ thống)</summary>
    public async Task<UiPrefs?> UserPrefsAsync(int userId)
    {
        var json = await _cache.GetOrCreateAsync($"ui.u.{userId}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            return await _db.Users.Where(u => u.Id == userId).Select(u => u.UiPrefs).FirstOrDefaultAsync();
        });
        return UiPrefs.FromJson(json);
    }

    /// <summary>Cấu hình áp dụng cho người dùng hiện tại</summary>
    public async Task<UiPrefs> CurrentAsync(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated == true && await UserPrefsAsync(user.GetUserId()) is { } mine) return mine;
        return await SystemDefaultAsync();
    }

    public async Task SaveUserAsync(int userId, UiPrefs? prefs)
    {
        var u = await _db.Users.FindAsync(userId);
        if (u == null) return;
        u.UiPrefs = prefs?.Normalize().ToJson();
        await _db.SaveChangesAsync();
        _cache.Remove($"ui.u.{userId}");
    }

    public async Task SaveDefaultAsync(UiPrefs prefs)
    {
        var s = await _db.AppSettings.FindAsync(DefaultKey);
        if (s == null) _db.AppSettings.Add(s = new Models.Entities.AppSetting { Key = DefaultKey });
        s.Value = prefs.Normalize().ToJson();
        s.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        _cache.Remove(DefaultKey);
    }
}
