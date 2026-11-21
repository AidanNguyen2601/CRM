using System.Text.RegularExpressions;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CRM.Web.Infrastructure;

/// <summary>Dữ liệu khách gửi từ form website</summary>
public class WebFormSubmission
{
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Company { get; set; }
    public string? JobTitle { get; set; }
    public string? Message { get; set; }
    public bool Consent { get; set; }
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? Page { get; set; }
    public string? Ref { get; set; }
}

/// <summary>
/// Nhận lead từ form website: kiểm tra dữ liệu, chống spam (ô bẫy, thời gian điền, giới hạn lượt theo IP),
/// gộp vào lead đang mở nếu trùng email / điện thoại, ngược lại tạo lead mới → chấm điểm, phân bổ, báo người phụ trách.
/// </summary>
public class WebFormService
{
    public const int MaxPerIpPer10Min = 5;
    private readonly CrmDbContext _db;
    private readonly LeadAssignService _assign;
    private readonly NotificationService _notify;
    private readonly IMemoryCache _cache;
    private readonly IDataProtector _ts;

    public WebFormService(CrmDbContext db, LeadAssignService assign, NotificationService notify, IMemoryCache cache, IDataProtectionProvider dp)
    {
        _db = db; _assign = assign; _notify = notify; _cache = cache; _ts = dp.CreateProtector("CRM.WebForm.Time");
    }

    public static string NewToken() => SecurityText.NewToken()[..20].Replace('-', 'x').Replace('_', 'y');

    /// <summary>Mốc thời gian mở form (đã mã hoá) – gửi quá nhanh sau khi mở là máy tự động</summary>
    public string TimeStamp() => _ts.Protect(DateTime.UtcNow.Ticks.ToString());

    public bool TooFast(string? stamp)
    {
        if (string.IsNullOrEmpty(stamp)) return false;   // form HTML tự dựng (không có mốc) – không áp dụng
        try { return DateTime.UtcNow - new DateTime(long.Parse(_ts.Unprotect(stamp)), DateTimeKind.Utc) < TimeSpan.FromSeconds(2); }
        catch { return true; }   // mốc bị sửa
    }

    /// <summary>Giới hạn số lượt gửi theo IP (mỗi form)</summary>
    public bool RateLimited(int formId, string ip)
    {
        var key = $"wf:{formId}:{ip}";
        var n = _cache.GetOrCreate(key, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10); return 0; });
        _cache.Set(key, n + 1, TimeSpan.FromMinutes(10));
        return n >= MaxPerIpPer10Min;
    }

    /// <summary>Tên miền được nhúng / gửi form (trống = mọi trang)</summary>
    public static List<string> Domains(WebForm f) => (f.AllowedDomains ?? "").Split(new[] { '\n', ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(d => Regex.Replace(d.Trim().ToLowerInvariant(), @"^https?://|/.*$", "")).Where(d => d.Length > 0).Distinct().ToList();

    public static bool HostAllowed(WebForm f, string? url)
    {
        var domains = Domains(f);
        if (domains.Count == 0) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var host = u.Host.ToLowerInvariant();
        return domains.Any(d => host == d || host.EndsWith("." + d));
    }

    public static List<string> Validate(WebForm f, WebFormSubmission s)
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(s.FullName)) e.Add(I18n.T("Vui lòng nhập họ tên."));
        else if (s.FullName.Trim().Length > 150) e.Add(I18n.T("Họ tên quá dài."));
        var email = s.Email?.Trim();
        if (string.IsNullOrEmpty(email)) { if (f.RequireEmail) e.Add(I18n.T("Vui lòng nhập email.")); }
        else if (email.Length > 150 || !Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) e.Add(I18n.T("Email không hợp lệ."));
        var digits = ImportService.Digits(s.Phone);
        if (string.IsNullOrWhiteSpace(s.Phone)) { if (f.RequirePhone) e.Add(I18n.T("Vui lòng nhập số điện thoại.")); }
        else if (digits.Length is < 8 or > 15 || s.Phone!.Trim().Length > 20) e.Add(I18n.T("Số điện thoại không hợp lệ."));
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(s.Phone)) e.Add(I18n.T("Vui lòng để lại email hoặc số điện thoại để chúng tôi liên hệ."));
        if (f.AskConsent && !s.Consent) e.Add(I18n.T("Vui lòng đồng ý để chúng tôi xử lý thông tin liên hệ."));
        if ((s.Message?.Length ?? 0) > 2000) e.Add(I18n.T("Nội dung quá dài (tối đa 2000 ký tự)."));
        return e;
    }

    private static string? Cut(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }

    /// <summary>Lưu lead từ form (đã kiểm tra hợp lệ). Trả về lead và cho biết là lead mới hay gộp vào lead đang mở.</summary>
    public async Task<(Lead Lead, bool Created)> SubmitAsync(WebForm f, WebFormSubmission s)
    {
        var now = DateTime.Now;
        var email = Cut(s.Email, 150)?.ToLowerInvariant();
        var phoneKey = ImportService.PhoneKey(s.Phone);
        var probe = phoneKey.Length == 9 ? phoneKey[^3..] : null;   // 3 số cuối luôn liền nhau trong các cách viết số điện thoại

        // Lead đang mở trùng email / điện thoại → ghi thêm vào lead đó thay vì tạo trùng
        var open = await _db.Leads.IgnoreQueryFilters()
            .Where(l => (l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted || l.Status == LeadStatus.Qualified)
                        && ((email != null && l.Email == email) || (probe != null && l.Phone != null && l.Phone.Contains(probe))))
            .OrderByDescending(l => l.Id).ToListAsync();
        var existing = open.FirstOrDefault(l => (email != null && ImportService.EmailKey(l.Email) == email) || (phoneKey.Length == 9 && ImportService.PhoneKey(l.Phone) == phoneKey));
        var note = $"[{now:dd/MM/yyyy HH:mm}] " + I18n.T("Gửi form \"{0}\"", f.Name) + (string.IsNullOrWhiteSpace(s.Message) ? "" : ": " + s.Message!.Trim());
        if (existing != null)
        {
            existing.Description = Cut(string.IsNullOrEmpty(existing.Description) ? note : existing.Description + "\n" + note, 2000);
            existing.Company ??= Cut(s.Company, 200); existing.JobTitle ??= Cut(s.JobTitle, 100);
            existing.Email ??= email; existing.Phone ??= Cut(s.Phone, 20);
            existing.WebFormId ??= f.Id; existing.UtmSource ??= Cut(s.UtmSource, 100); existing.UtmMedium ??= Cut(s.UtmMedium, 100); existing.UtmCampaign ??= Cut(s.UtmCampaign, 150);
            if (f.AskConsent && s.Consent) existing.ConsentAt ??= now;
            f.Submissions++; f.LastSubmissionAt = now;
            if (existing.OwnerId.HasValue)
                _notify.Add(existing.OwnerId, "lead", I18n.T("Lead {0} vừa gửi lại form \"{1}\"", existing.FullName, f.Name), $"/Leads/Details/{existing.Id}");
            await _db.SaveChangesAsync();
            await LeadScoring.RecalculateAsync(_db, new[] { existing.Id });
            return (existing, false);
        }

        var lead = new Lead
        {
            FullName = Cut(s.FullName, 150)!, Email = email, Phone = Cut(s.Phone, 20), Company = Cut(s.Company, 200), JobTitle = Cut(s.JobTitle, 100),
            Description = Cut(s.Message, 2000), Source = f.Source, Status = LeadStatus.New, Rating = LeadRating.Warm, CampaignId = f.CampaignId,
            OwnerId = f.OwnerId, BranchId = f.BranchId ?? 0, WebFormId = f.Id, CreatedAt = now,
            UtmSource = Cut(s.UtmSource, 100), UtmMedium = Cut(s.UtmMedium, 100), UtmCampaign = Cut(s.UtmCampaign, 150),
            LandingPage = Cut(s.Page, 500), Referrer = Cut(s.Ref, 500), ConsentAt = f.AskConsent && s.Consent ? now : null
        };
        var autoAssigned = lead.OwnerId == null && await _assign.AssignAsync(new[] { lead }, null) > 0;
        _db.Leads.Add(lead);
        f.Submissions++; f.LastSubmissionAt = now;
        await _db.SaveChangesAsync();
        if (autoAssigned) _assign.NotifyAssigned(new[] { lead }, null);
        else if (lead.OwnerId.HasValue) _notify.Add(lead.OwnerId, "lead", I18n.T("Lead mới từ form \"{0}\": {1}", f.Name, lead.FullName), $"/Leads/Details/{lead.Id}");
        else
        {
            // Chưa có người nhận: báo quản lý / quản trị để phân công
            var managers = await _db.Users.Where(u => u.IsActive && (u.Role == AppRoles.Manager || u.Role == AppRoles.Admin)).Select(u => u.Id).ToListAsync();
            foreach (var m in managers) _notify.Add(m, "lead", I18n.T("Lead mới từ form \"{0}\" chưa có người phụ trách: {1}", f.Name, lead.FullName), $"/Leads/Details/{lead.Id}");
        }
        await _db.SaveChangesAsync();
        await LeadScoring.RecalculateAsync(_db, new[] { lead.Id });
        return (lead, true);
    }
}
