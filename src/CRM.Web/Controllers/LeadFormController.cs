using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>
/// Trang form công khai /f/{token}: mở trực tiếp, nhúng iframe (wwwroot/js/crm-form.js) hoặc nhận POST từ form HTML tự dựng trên website.
/// Không cần đăng nhập; chống spam bằng ô bẫy, thời gian điền tối thiểu, giới hạn lượt gửi theo IP và danh sách tên miền được phép.
/// </summary>
[AllowAnonymous]
[Route("f")]
public class LeadFormController : Controller
{
    private readonly CrmDbContext _db;
    private readonly WebFormService _forms;
    public LeadFormController(CrmDbContext db, WebFormService forms) { _db = db; _forms = forms; }

    public record PublicFormModel(WebForm Form, WebFormSubmission Values, List<string> Errors, string Stamp, bool Embed, bool Done);

    private Task<WebForm?> FindAsync(string token) => _db.WebForms.FirstOrDefaultAsync(f => f.Token == token && f.IsActive);

    /// <summary>Chỉ cho nhúng iframe trên các tên miền đã khai báo</summary>
    private void FrameHeaders(WebForm f)
    {
        var domains = WebFormService.Domains(f);
        Response.Headers.ContentSecurityPolicy = "frame-ancestors " + (domains.Count == 0 ? "*" : "'self' " + string.Join(" ", domains.SelectMany(d => new[] { "https://" + d, "https://*." + d, "http://" + d })));
        Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    }

    [HttpGet("{token}")]
    public async Task<IActionResult> Show(string token, bool embed, string? utm_source, string? utm_medium, string? utm_campaign, string? page, string? @ref)
    {
        var f = await FindAsync(token);
        if (f == null) return NotFound();
        FrameHeaders(f);
        var v = new WebFormSubmission { UtmSource = utm_source, UtmMedium = utm_medium, UtmCampaign = utm_campaign, Page = page ?? Request.Headers.Referer.ToString(), Ref = @ref };
        return View("~/Views/WebForms/Public.cshtml", new PublicFormModel(f, v, new(), _forms.TimeStamp(), embed, false));
    }

    [HttpPost("{token}")]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(64_000)]
    public async Task<IActionResult> Submit(string token, [FromForm] WebFormSubmission v, [FromForm(Name = "_ts")] string? stamp,
        [FromForm(Name = "website")] string? honeypot, [FromForm(Name = "_embed")] bool embed, [FromForm(Name = "consent")] string? consent)
    {
        var f = await FindAsync(token);
        if (f == null) return NotFound();
        FrameHeaders(f);
        v.Consent = consent is "true" or "on" or "1";
        // Gửi trực tiếp từ trang khác (form HTML tự dựng): trang gửi phải thuộc tên miền được phép
        var origin = Request.Headers.Origin.ToString();
        var self = $"{Request.Scheme}://{Request.Host}";
        if (!string.IsNullOrEmpty(origin) && !origin.Equals(self, StringComparison.OrdinalIgnoreCase) && !WebFormService.HostAllowed(f, origin))
            return StatusCode(403);
        if (string.IsNullOrEmpty(v.Page) && !string.IsNullOrEmpty(origin) && origin != self) v.Page = Request.Headers.Referer.ToString();

        // Máy tự động: ô bẫy có dữ liệu hoặc gửi quá nhanh → báo thành công nhưng không lưu
        if (!string.IsNullOrEmpty(honeypot) || _forms.TooFast(stamp)) return Done(f, embed);
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?";
        if (_forms.RateLimited(f.Id, ip))
            return View("~/Views/WebForms/Public.cshtml", new PublicFormModel(f, v, new() { I18n.T("Bạn đã gửi quá nhiều lần, vui lòng thử lại sau ít phút.") }, _forms.TimeStamp(), embed, false));

        var errors = WebFormService.Validate(f, v);
        if (errors.Count > 0) return View("~/Views/WebForms/Public.cshtml", new PublicFormModel(f, v, errors, _forms.TimeStamp(), embed, false));
        await _forms.SubmitAsync(f, v);
        return Done(f, embed);
    }

    private IActionResult Done(WebForm f, bool embed)
    {
        var redirect = Uri.TryCreate(f.RedirectUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? f.RedirectUrl : null;
        if (redirect != null && !embed) return Redirect(redirect);
        return View("~/Views/WebForms/Public.cshtml", new PublicFormModel(f, new(), new(), "", embed, true));
    }
}
