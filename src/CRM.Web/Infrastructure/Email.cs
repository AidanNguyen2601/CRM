using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CRM.Web.Infrastructure;

/// <summary>Cấu hình lưu dạng JSON trong bảng AppSettings (có cache)</summary>
public class SettingsStore
{
    private readonly CrmDbContext _db;
    private readonly IMemoryCache _cache;
    public SettingsStore(CrmDbContext db, IMemoryCache cache) { _db = db; _cache = cache; }

    public async Task<T> GetAsync<T>(string key) where T : new() =>
        await _cache.GetOrCreateAsync("setting." + key, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            var json = await _db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync();
            if (string.IsNullOrEmpty(json)) return new T();
            try { return JsonSerializer.Deserialize<T>(json) ?? new T(); } catch { return new T(); }
        }) ?? new T();

    public async Task SaveAsync<T>(string key, T value)
    {
        var s = await _db.AppSettings.FindAsync(key);
        if (s == null) _db.AppSettings.Add(s = new AppSetting { Key = key });
        s.Value = JsonSerializer.Serialize(value);
        s.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        _cache.Remove("setting." + key);
    }
}

/// <summary>Cấu hình máy chủ gửi thư (SMTP). Mật khẩu lưu đã mã hóa (Data Protection).</summary>
public class SmtpSettings
{
    public const string Key = "smtp";
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    /// <summary>Bật mã hóa TLS (STARTTLS cổng 587 / SSL cổng 465)</summary>
    public bool EnableSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? PasswordProtected { get; set; }
    public string FromEmail { get; set; } = "";
    public string? FromName { get; set; }
    /// <summary>Địa chỉ trang web (để tạo liên kết trong email: đặt lại mật khẩu, thông báo...) vd. https://crm.congty.vn</summary>
    public string? SiteUrl { get; set; }
    /// <summary>Chân email thêm cuối mọi email (chữ ký công ty)</summary>
    public string? Footer { get; set; }
    public bool IsReady => Enabled && !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromEmail);
}

/// <summary>Soạn / xếp hàng / gửi email. Email được xếp hàng và tác vụ nền gửi đi (thử lại tối đa 3 lần).</summary>
public class EmailService
{
    public const int MaxAttempts = 3;
    private readonly CrmDbContext _db;
    private readonly SettingsStore _settings;
    private readonly IDataProtector _protector;

    public EmailService(CrmDbContext db, SettingsStore settings, IDataProtectionProvider dp)
    {
        _db = db; _settings = settings; _protector = dp.CreateProtector("CRM.Smtp");
    }

    public Task<SmtpSettings> SettingsAsync() => _settings.GetAsync<SmtpSettings>(SmtpSettings.Key);
    public async Task<bool> IsReadyAsync() => (await SettingsAsync()).IsReady;

    public string Protect(string plain) => _protector.Protect(plain);
    public string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try { return _protector.Unprotect(cipher); } catch { return null; }
    }

    /// <summary>Thêm email vào hàng đợi (lưu cùng SaveChanges của người gọi)</summary>
    public EmailMessage Queue(string to, string subject, string htmlBody, string? relatedType = null, int? relatedId = null,
        int? customerId = null, int? createdById = null, string? cc = null, IEnumerable<int>? attachmentIds = null, int? campaignMemberId = null)
    {
        var m = new EmailMessage
        {
            To = Cut(to, 500)!, Cc = Cut(cc, 500), Subject = Cut(subject, 300)!, Body = htmlBody, RelatedType = relatedType, RelatedId = relatedId,
            CustomerId = customerId, CreatedById = createdById, CampaignMemberId = campaignMemberId,
            AttachmentIds = attachmentIds == null ? null : Cut(string.Join(",", attachmentIds), 500), CreatedAt = DateTime.Now
        };
        _db.EmailMessages.Add(m);
        return m;
    }

    /// <summary>Trộn biến {{Bien}} (không phân biệt hoa thường; biến không có giữ nguyên chuỗi rỗng)</summary>
    public static string Merge(string text, IReadOnlyDictionary<string, string?> values) =>
        Regex.Replace(text ?? "", @"\{\{\s*([\w\.]+)\s*\}\}", m =>
            values.FirstOrDefault(kv => string.Equals(kv.Key, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)).Value ?? "");

    /// <summary>Văn bản thuần (người dùng soạn) → HTML: giữ xuống dòng, tự nhận liên kết. Nội dung đã là HTML thì giữ nguyên.</summary>
    public static string ToHtml(string text)
    {
        if (Regex.IsMatch(text ?? "", @"<(p|div|br|table|ul|h\d|b|strong|a)\b", RegexOptions.IgnoreCase)) return text!;
        var enc = WebUtility.HtmlEncode(text ?? "");
        enc = Regex.Replace(enc, @"(https?://[^\s<]+)", "<a href=\"$1\">$1</a>");
        return enc.Replace("\r\n", "\n").Replace("\n", "<br>");
    }

    /// <summary>Bọc nội dung vào khung email (font, chân email)</summary>
    public static string Wrap(string bodyHtml, SmtpSettings s) =>
        "<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;line-height:1.55;color:#1f2937\">" + bodyHtml
        + (string.IsNullOrWhiteSpace(s.Footer) ? "" : "<hr style=\"border:0;border-top:1px solid #e5e7eb;margin:20px 0 10px\"><div style=\"color:#6b7280;font-size:12px\">" + ToHtml(s.Footer) + "</div>")
        + "</div>";

    /// <summary>Liên kết tuyệt đối từ đường dẫn tương đối (dựa vào SiteUrl đã cấu hình)</summary>
    public static string Absolute(SmtpSettings s, string? relative) =>
        string.IsNullOrEmpty(relative) || relative.StartsWith("http", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(s.SiteUrl)
            ? relative ?? "" : s.SiteUrl!.TrimEnd('/') + "/" + relative.TrimStart('/');

    /// <summary>Gửi ngay một email (dùng bởi tác vụ nền và nút "Gửi thử")</summary>
    public async Task SendAsync(EmailMessage m, SmtpSettings s, AttachmentService? files = null, CancellationToken ct = default)
    {
        using var client = new SmtpClient(s.Host, s.Port) { EnableSsl = s.EnableSsl, DeliveryMethod = SmtpDeliveryMethod.Network, Timeout = 30000 };
        if (!string.IsNullOrWhiteSpace(s.Username)) client.Credentials = new NetworkCredential(s.Username, Unprotect(s.PasswordProtected) ?? "");
        using var msg = new MailMessage
        {
            From = new MailAddress(s.FromEmail, string.IsNullOrWhiteSpace(s.FromName) ? null : s.FromName, Encoding.UTF8),
            Subject = m.Subject, SubjectEncoding = Encoding.UTF8, Body = Wrap(m.Body, s), BodyEncoding = Encoding.UTF8, IsBodyHtml = true
        };
        foreach (var a in Split(m.To)) msg.To.Add(a);
        foreach (var a in Split(m.Cc)) msg.CC.Add(a);
        var streams = new List<Stream>();
        try
        {
            if (files != null && !string.IsNullOrEmpty(m.AttachmentIds))
            {
                var ids = m.AttachmentIds.Split(',').Select(x => int.TryParse(x, out var i) ? i : 0).Where(i => i > 0).ToList();
                foreach (var att in await _db.Attachments.Where(x => ids.Contains(x.Id)).ToListAsync(ct))
                {
                    var stream = files.OpenRead(att);
                    if (stream == null) continue;
                    streams.Add(stream);
                    msg.Attachments.Add(new System.Net.Mail.Attachment(stream, att.FileName, att.ContentType));
                }
            }
            await client.SendMailAsync(msg, ct);
        }
        finally { foreach (var st in streams) st.Dispose(); }
    }

    public static IEnumerable<string> Split(string? list) =>
        (list ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool IsValidList(string? list) =>
        Split(list).Any() && Split(list).All(a => MailAddress.TryCreate(a, out _));

    private static string? Cut(string? s, int max) => s == null ? null : s.Length > max ? s[..max] : s;
}

/// <summary>Tác vụ nền: mỗi 20 giây gửi các email đang chờ (tối đa 50 thư / lượt), cập nhật trạng thái thư và đối tượng chiến dịch</summary>
public class EmailWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EmailWorker> _log;
    public EmailWorker(IServiceScopeFactory scopes, ILogger<EmailWorker> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var n = await RunOnceAsync(scope.ServiceProvider, ct);
                if (n > 0) _log.LogInformation("Đã xử lý {Count} email", n);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogWarning(ex, "Lỗi gửi email"); }
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
        }
    }

    public static async Task<int> RunOnceAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var db = sp.GetRequiredService<CrmDbContext>();
        var mail = sp.GetRequiredService<EmailService>();
        await PlatformDefaults.QueueNotificationEmailsAsync(db, mail, sp.GetRequiredService<SettingsStore>(), ct);
        var s = await mail.SettingsAsync();
        if (!s.IsReady) return 0;
        var batch = await db.EmailMessages.Where(m => m.Status == EmailStatus.Queued).OrderBy(m => m.Id).Take(50).ToListAsync(ct);
        var files = sp.GetRequiredService<AttachmentService>();
        foreach (var m in batch)
        {
            m.Attempts++;
            try
            {
                await mail.SendAsync(m, s, files, ct);
                m.Status = EmailStatus.Sent; m.SentAt = DateTime.Now; m.Error = null;
                if (m.CampaignMemberId.HasValue)
                {
                    var mem = await db.CampaignMembers.FindAsync(new object[] { m.CampaignMemberId.Value }, ct);
                    if (mem != null && mem.Status == CampaignMemberStatus.Pending) { mem.Status = CampaignMemberStatus.Sent; mem.LastContactAt = DateTime.Now; }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var err = ex.InnerException != null ? ex.Message + " – " + ex.InnerException.Message : ex.Message;
                m.Error = err.Length > 1000 ? err[..1000] : err;
                if (m.Attempts >= EmailService.MaxAttempts)
                {
                    m.Status = EmailStatus.Failed;
                    if (m.CampaignMemberId.HasValue)
                    {
                        var mem = await db.CampaignMembers.FindAsync(new object[] { m.CampaignMemberId.Value }, ct);
                        if (mem != null && mem.Status == CampaignMemberStatus.Pending) mem.Status = CampaignMemberStatus.Failed;
                    }
                }
            }
            await db.SaveChangesAsync(ct);
        }
        return batch.Count;
    }
}
