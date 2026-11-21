using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Chỉ số NPS của một tập câu trả lời</summary>
public record NpsStats(int Sent, int Responded, int Promoters, int Passives, int Detractors)
{
    public int Score => Responded == 0 ? 0 : (int)Math.Round((Promoters - Detractors) * 100.0 / Responded);
    public double ResponseRate => Sent == 0 ? 0 : Math.Round(Responded * 100.0 / Sent, 1);
    public static NpsStats Of(IEnumerable<NpsResponse> rs)
    {
        var list = rs.ToList();
        var done = list.Where(r => r.Score.HasValue).Select(r => r.Score!.Value).ToList();
        return new NpsStats(list.Count(r => r.SentAt.HasValue), done.Count, done.Count(s => s >= 9), done.Count(s => s is 7 or 8), done.Count(s => s <= 6));
    }
    public static string Css(int nps) => nps >= 50 ? "bg-success" : nps >= 0 ? "bg-warning text-dark" : "bg-danger";
}

/// <summary>
/// Khảo sát NPS: chọn đối tượng (người liên hệ của khách hàng đang giao dịch, có email), gửi email có liên kết trả lời riêng,
/// khách chấm 0–10 không cần đăng nhập; NPS = % ủng hộ (9–10) − % phản đối (0–6). Phản đối → báo người phụ trách khách hàng.
/// </summary>
public class NpsController : CrmControllerBase
{
    private readonly EmailService _mail;
    public NpsController(CrmDbContext db, EmailService mail) : base(db) => _mail = mail;

    [Authorize(Roles = AppRoles.SupportGroup)]
    public async Task<IActionResult> Index()
    {
        var surveys = await Db.NpsSurveys.Include(s => s.Responses).Include(s => s.CreatedBy).AsNoTracking().OrderByDescending(s => s.Id).ToListAsync();
        ViewBag.Overall = NpsStats.Of(surveys.SelectMany(s => s.Responses).Where(r => r.RespondedAt >= DateTime.Today.AddMonths(-12)));
        return View(surveys);
    }

    /// <summary>Người nhận: người liên hệ có email của khách hàng trong phạm vi (lọc loại khách hàng / nhãn / chỉ liên hệ chính)</summary>
    private IQueryable<Contact> Audience(CustomerType? type, int? tagId, bool primaryOnly)
    {
        var q = Db.Contacts.Where(c => c.Email != null && c.Email != "" && c.CustomerId != null);
        if (type.HasValue) q = q.Where(c => c.Customer!.Type == type);
        if (primaryOnly) q = q.Where(c => c.IsPrimary);
        if (tagId.HasValue) q = q.Where(c => Db.EntityTags.Any(t => t.EntityType == nameof(Customer) && t.TagId == tagId && t.EntityId == c.CustomerId));
        return q;
    }

    [Authorize(Roles = AppRoles.SupportGroup)]
    public async Task<IActionResult> Create(CustomerType? type = CustomerType.Customer, int? tagId = null, bool primaryOnly = true)
    {
        ViewBag.Count = await Audience(type, tagId, primaryOnly).Select(c => c.Email).Distinct().CountAsync();
        ViewBag.Type = type; ViewBag.TagId = tagId; ViewBag.PrimaryOnly = primaryOnly;
        ViewBag.Tags = await Db.Tags.OrderBy(t => t.Name).ToListAsync();
        ViewBag.Ready = await _mail.IsReadyAsync();
        return View(new NpsSurvey { Name = I18n.T("Khảo sát mức độ hài lòng {0}", DateTime.Today.ToString("MM/yyyy")) });
    }

    [Authorize(Roles = AppRoles.SupportGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(NpsSurvey model, CustomerType? type, int? tagId, bool primaryOnly)
    {
        ModelState.Remove(nameof(model.CreatedBy)); ModelState.Remove(nameof(model.Responses));
        if (!ModelState.IsValid) return RedirectToAction(nameof(Create));
        var contacts = await Audience(type, tagId, primaryOnly).Select(c => new { c.Id, c.CustomerId, c.Email, c.FullName, Customer = c.Customer!.Name }).ToListAsync();
        contacts = contacts.GroupBy(c => c.Email!.Trim().ToLowerInvariant()).Select(g => g.First()).ToList();
        if (contacts.Count == 0)
        {
            TempData["Error"] = I18n.T("Không có người liên hệ nào phù hợp (cần có email).");
            return RedirectToAction(nameof(Create));
        }
        var s = new NpsSurvey { Name = model.Name.Trim(), Question = string.IsNullOrWhiteSpace(model.Question) ? new NpsSurvey().Question : model.Question.Trim(),
            Status = NpsStatus.Sent, CreatedById = CurrentUserId, CreatedAt = DateTime.Now, SentAt = DateTime.Now };
        Db.NpsSurveys.Add(s);
        var settings = await _mail.SettingsAsync();
        foreach (var c in contacts)
        {
            var r = new NpsResponse { ContactId = c.Id, CustomerId = c.CustomerId, Email = c.Email!.Trim(), Token = SecurityText.NewToken()[..40], SentAt = DateTime.Now };
            s.Responses.Add(r);
            var link = $"{(string.IsNullOrWhiteSpace(settings.SiteUrl) ? $"{Request.Scheme}://{Request.Host}" : settings.SiteUrl!.TrimEnd('/'))}{Url.Action(nameof(Answer), new { t = r.Token })}";
            var scale = string.Join(" ", Enumerable.Range(0, 11).Select(i =>
                $"<a href=\"{link}&amp;s={i}\" style=\"display:inline-block;width:30px;padding:6px 0;margin:2px;text-align:center;border-radius:4px;text-decoration:none;color:#fff;background:{(i <= 6 ? "#ef4444" : i <= 8 ? "#f59e0b" : "#16a34a")}\">{i}</a>"));
            _mail.Queue(r.Email, s.Name,
                EmailService.ToHtml(I18n.T("Kính gửi {0},", c.FullName) + "\n\n" + I18n.T("Cảm ơn {0} đã đồng hành cùng chúng tôi. Xin dành 30 giây cho câu hỏi sau:", c.Customer))
                + $"<p style=\"font-weight:600;margin:16px 0 8px\">{System.Net.WebUtility.HtmlEncode(s.Question)}</p><div>{scale}</div>"
                + $"<p style=\"color:#6b7280;font-size:12px\">0 = {I18n.T("Hoàn toàn không")} · 10 = {I18n.T("Chắc chắn sẽ giới thiệu")}</p>",
                "Nps", null, c.CustomerId, CurrentUserId);
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã gửi khảo sát tới {0} người liên hệ.", contacts.Count);
        return RedirectToAction(nameof(Details), new { id = s.Id });
    }

    [Authorize(Roles = AppRoles.SupportGroup)]
    public async Task<IActionResult> Details(int id)
    {
        var s = await Db.NpsSurveys.Include(x => x.Responses).ThenInclude(r => r.Customer).Include(x => x.Responses).ThenInclude(r => r.Contact)
            .AsNoTracking().AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return NotFound();
        ViewBag.Stats = NpsStats.Of(s.Responses);
        return View(s);
    }

    [Authorize(Roles = AppRoles.SupportGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int id)
    {
        var s = await Db.NpsSurveys.FindAsync(id);
        if (s == null) return NotFound();
        s.Status = s.Status == NpsStatus.Closed ? NpsStatus.Sent : NpsStatus.Closed;
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    // ======================= Trang trả lời (không cần đăng nhập) =======================

    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> Answer(string t, int? s)
    {
        var r = await Db.NpsResponses.Include(x => x.Survey).Include(x => x.Customer).FirstOrDefaultAsync(x => x.Token == t);
        if (r == null) return View("Answer", null);
        ViewBag.Preset = s is >= 0 and <= 10 ? s : r.Score;
        return View("Answer", r);
    }

    [AllowAnonymous]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Answer(string t, int score, string? comment)
    {
        var r = await Db.NpsResponses.Include(x => x.Survey).Include(x => x.Customer).FirstOrDefaultAsync(x => x.Token == t);
        if (r == null || r.Survey!.Status == NpsStatus.Closed || score is < 0 or > 10) return View("Answer", null);
        var first = r.Score == null;
        r.Score = score; r.Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(comment.Trim().Length, 2000)]; r.RespondedAt = DateTime.Now;
        // Khách phản đối (0–6) → báo người phụ trách khách hàng để chăm sóc lại
        if (first && score <= 6 && r.Customer?.OwnerId is int owner)
            HttpContext.RequestServices.GetRequiredService<NotificationService>().Add(owner, "nps",
                I18n.T("{0} chấm NPS {1}/10 – cần chăm sóc lại", r.Customer.Name, score), $"/Customers/Details/{r.CustomerId}", null, r.Comment);
        Db.SuppressAudit = true;
        await Db.SaveChangesAsync();
        ViewBag.Thanks = true;
        return View("Answer", r);
    }
}
