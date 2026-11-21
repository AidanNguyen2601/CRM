using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>
/// Email → phiếu hỗ trợ: cấu hình hộp thư hỗ trợ (POP3), đọc thử, và địa chỉ webhook cho dịch vụ chuyển tiếp email
/// (SendGrid Inbound Parse, Mailgun Routes, Postmark Inbound… hoặc JSON {from, subject, text, messageId}).
/// </summary>
public class InboundController : CrmControllerBase
{
    private readonly InboundService _inbound;
    private readonly SettingsStore _settings;
    public InboundController(CrmDbContext db, InboundService inbound, SettingsStore settings) : base(db) { _inbound = inbound; _settings = settings; }

    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Index()
    {
        var s = await _inbound.SettingsAsync();
        // Lần đầu: lưu mã webhook ngẫu nhiên để địa chỉ không đổi giữa các lần mở trang
        if (!await Db.AppSettings.AnyAsync(x => x.Key == InboundSettings.Key)) await _settings.SaveAsync(InboundSettings.Key, s);
        ViewBag.WebhookUrl = $"{Request.Scheme}://{Request.Host}{Url.Action(nameof(Email), new { token = s.WebhookToken })}";
        ViewBag.HasPassword = !string.IsNullOrEmpty(s.PasswordProtected);
        ViewBag.Recent = await Db.InboundMessages.AsNoTracking().OrderByDescending(m => m.Id).Take(20).ToListAsync();
        return View(s);
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(InboundSettings model, string? password, bool clearPassword, bool regenerate, bool checkNow)
    {
        var cur = await _inbound.SettingsAsync();
        var mail = HttpContext.RequestServices.GetRequiredService<EmailService>();
        cur.Pop3Enabled = model.Pop3Enabled; cur.Host = model.Host?.Trim() ?? ""; cur.Port = model.Port is > 0 and < 65536 ? model.Port : 995;
        cur.UseSsl = model.UseSsl; cur.Username = model.Username?.Trim(); cur.DeleteAfterRead = model.DeleteAfterRead;
        cur.AutoReply = model.AutoReply; cur.DefaultPriority = model.DefaultPriority;
        if (clearPassword) cur.PasswordProtected = null; else if (!string.IsNullOrEmpty(password)) cur.PasswordProtected = mail.Protect(password);
        if (regenerate) cur.WebhookToken = SecurityText.NewToken()[..24];
        await _settings.SaveAsync(InboundSettings.Key, cur);
        if (checkNow)
        {
            var (read, created, replied) = await _inbound.CheckMailboxAsync();
            var after = await _inbound.SettingsAsync();
            TempData[after.LastResult?.StartsWith(I18n.T("Lỗi: {0}", "").TrimEnd()) == true ? "Error" : "Success"] = after.LastResult ?? I18n.T("Đã đọc hộp thư.");
        }
        else TempData["Success"] = I18n.T("Đã lưu cấu hình hộp thư hỗ trợ.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Webhook nhận email (không cần đăng nhập, xác thực bằng mã bí mật trên địa chỉ)</summary>
    [AllowAnonymous, IgnoreAntiforgeryToken]
    [HttpPost]
    [RequestSizeLimit(5_000_000)]
    public async Task<IActionResult> Email(string token)
    {
        var s = await _inbound.SettingsAsync();
        if (string.IsNullOrEmpty(token) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(s.WebhookToken)))
            return Unauthorized();
        string? from = null, subject = null, text = null, html = null, msgId = null;
        if (Request.HasFormContentType)
        {
            var f = Request.Form;
            string? F(params string[] keys) => keys.Select(k => f[k].ToString()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
            from = F("from", "From", "sender"); subject = F("subject", "Subject"); text = F("text", "body-plain", "TextBody", "plain");
            html = F("html", "body-html", "HtmlBody"); msgId = F("message-id", "Message-Id", "MessageID", "messageId");
            if (string.IsNullOrEmpty(msgId) && F("headers") is { } hdr) msgId = System.Text.RegularExpressions.Regex.Match(hdr, @"Message-ID:\s*(\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;
        }
        else
        {
            JsonDocument doc;
            try { doc = await JsonDocument.ParseAsync(Request.Body); }
            catch (JsonException) { return BadRequest("json"); }
            using var _ = doc;
            var r = doc.RootElement;
            string? J(params string[] keys) => keys.Select(k => r.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.Object && v.TryGetProperty("Email", out var e) ? e.GetString() : v.ValueKind == JsonValueKind.String ? v.GetString() : null : null).FirstOrDefault(v => !string.IsNullOrEmpty(v));
            from = J("from", "From", "FromFull"); subject = J("subject", "Subject"); text = J("text", "TextBody", "plain"); html = J("html", "HtmlBody"); msgId = J("messageId", "MessageID", "message-id");
        }
        if (string.IsNullOrWhiteSpace(from)) return BadRequest("from");
        var (email, name) = MimeLite.Address(from);
        var body = !string.IsNullOrWhiteSpace(text) ? text! : System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(html ?? "", @"<(br|/p|/div)\b[^>]*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "<[^>]+>", ""));
        var key = !string.IsNullOrWhiteSpace(msgId) ? "mid:" + msgId!.Trim() : "hook:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email + subject + body)))[..32];
        var (result, ticket) = await _inbound.ProcessAsync(new InboundMail(key, email, name, subject ?? "", body));
        return Json(new { result, ticket = ticket?.Number });
    }
}
