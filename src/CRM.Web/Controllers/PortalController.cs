using System.Security.Claims;
using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>
/// Cổng khách hàng: người liên hệ được mời đăng nhập bằng email + mật khẩu (cookie riêng), xem / gửi phiếu hỗ trợ của công ty mình,
/// trao đổi và đánh giá mức hài lòng, xem hoá đơn – công nợ, hợp đồng, đọc bài viết công khai của kho kiến thức.
/// </summary>
[AllowAnonymous]
public class PortalController : Controller
{
    public const string Scheme = "Portal";
    private const string ContactClaim = "crm:contact";
    private readonly CrmDbContext _db;
    private readonly ITimeLimitedDataProtector _tokens;
    private readonly PasswordHasher<Contact> _hasher = new();

    public PortalController(CrmDbContext db, IDataProtectionProvider dp)
    {
        _db = db;
        _tokens = dp.CreateProtector("CRM.Portal.Password").ToTimeLimitedDataProtector();
    }

    /// <summary>Liên kết đặt mật khẩu (mời / quên mật khẩu): hết hiệu lực khi hết hạn hoặc mật khẩu đã đổi</summary>
    public static string MakeToken(ITimeLimitedDataProtector p, Contact c, TimeSpan life) =>
        p.Protect($"{c.Id}|{Stamp(c)}", life);

    private static string Stamp(Contact c) => SecurityText.Hash(c.PortalPasswordHash ?? "new")[..16];

    private async Task<Contact?> CurrentAsync()
    {
        var r = await HttpContext.AuthenticateAsync(Scheme);
        if (!r.Succeeded || !int.TryParse(r.Principal!.FindFirstValue(ContactClaim), out var id)) return null;
        var c = await _db.Contacts.IgnoreQueryFilters().Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == id && x.PortalEnabled);
        if (c?.CustomerId == null) return null;
        ViewBag.PortalContact = c;
        return c;
    }

    private IActionResult ToLogin() => RedirectToAction(nameof(Login), new { returnUrl = Request.Path + Request.QueryString });

    // ======================= Đăng nhập =======================

    [HttpGet]
    public async Task<IActionResult> Login(string? returnUrl)
    {
        if (await CurrentAsync() != null) return RedirectToAction(nameof(Index));
        ViewBag.ReturnUrl = returnUrl;
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string? email, string? password, string? returnUrl)
    {
        email = email?.Trim().ToLowerInvariant();
        var c = string.IsNullOrEmpty(email) ? null : await _db.Contacts.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Email == email && x.PortalEnabled && x.PortalPasswordHash != null);
        // Chống dò mật khẩu: tối đa 10 lần sai / 15 phút / email
        var since = DateTime.Now.AddMinutes(-15);
        var tooMany = email != null && await _db.LoginLogs.CountAsync(l => l.Username == "portal:" + email && !l.Success && l.At > since) >= 10;
        var ok = !tooMany && c != null && _hasher.VerifyHashedPassword(c, c.PortalPasswordHash!, password ?? "") != PasswordVerificationResult.Failed;
        _db.LoginLogs.Add(new LoginLog
        {
            Username = ("portal:" + (email ?? "")).Length > 50 ? ("portal:" + email)[..50] : "portal:" + email, Success = ok,
            Reason = ok ? "Cổng khách hàng" : tooMany ? "Đang bị khóa tạm" : "Sai mật khẩu", Ip = HttpContext.Connection.RemoteIpAddress?.ToString(), At = DateTime.Now
        });
        if (!ok)
        {
            await _db.SaveChangesAsync();
            ModelState.AddModelError(string.Empty, tooMany ? I18n.T("Đăng nhập sai quá nhiều lần, vui lòng thử lại sau 15 phút.") : I18n.T("Email hoặc mật khẩu không đúng."));
            ViewBag.ReturnUrl = returnUrl;
            return View();
        }
        c!.PortalLastLoginAt = DateTime.Now;
        await _db.SaveChangesAsync();
        await SignInAsync(c);
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) && returnUrl.StartsWith("/Portal") ? Redirect(returnUrl) : RedirectToAction(nameof(Index));
    }

    private Task SignInAsync(Contact c) => HttpContext.SignInAsync(Scheme, new ClaimsPrincipal(new ClaimsIdentity(new[]
    {
        new Claim(ContactClaim, c.Id.ToString()), new Claim(ClaimTypes.Name, c.FullName)
    }, Scheme)));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(Scheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public async Task<IActionResult> SetPassword(string? t)
    {
        ViewBag.Valid = await FromTokenAsync(t) != null;
        ViewBag.Token = t;
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPassword(string? t, string? password, string? confirm)
    {
        var c = await FromTokenAsync(t);
        if (c == null) { ViewBag.Valid = false; return View(); }
        var policy = await HttpContext.RequestServices.GetRequiredService<SettingsStore>().GetAsync<SecuritySettings>(SecuritySettings.Key);
        var err = password != confirm ? I18n.T("Xác nhận mật khẩu không khớp") : policy.Validate(password);
        if (err != null) { ModelState.AddModelError(string.Empty, err); ViewBag.Valid = true; ViewBag.Token = t; return View(); }
        c.PortalPasswordHash = _hasher.HashPassword(c, password!);
        c.PortalEnabled = true;
        await _db.SaveChangesAsync();
        await SignInAsync(c);
        TempData["Success"] = I18n.T("Đã đặt mật khẩu. Chào mừng bạn đến cổng khách hàng!");
        return RedirectToAction(nameof(Index));
    }

    private async Task<Contact?> FromTokenAsync(string? t)
    {
        try
        {
            var parts = _tokens.Unprotect(t ?? "").Split('|');
            var c = await _db.Contacts.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == int.Parse(parts[0]) && x.PortalEnabled);
            return c != null && Stamp(c) == parts[1] ? c : null;
        }
        catch { return null; }
    }

    [HttpGet]
    public IActionResult Forgot() => View();

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Forgot(string? email)
    {
        email = email?.Trim().ToLowerInvariant();
        var c = string.IsNullOrEmpty(email) ? null : await _db.Contacts.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Email == email && x.PortalEnabled);
        var mail = HttpContext.RequestServices.GetRequiredService<EmailService>();
        if (c != null && await mail.IsReadyAsync())
        {
            var link = $"{Request.Scheme}://{Request.Host}{Url.Action(nameof(SetPassword), new { t = MakeToken(_tokens, c, TimeSpan.FromHours(1)) })}";
            mail.Queue(c.Email!, I18n.T("Đặt lại mật khẩu cổng khách hàng"),
                EmailService.ToHtml(I18n.T("Kính gửi {0},", c.FullName) + "\n\n" + I18n.T("Bấm vào liên kết sau để đặt mật khẩu mới (hiệu lực 1 giờ):") + "\n" + link), "Portal", c.Id, c.CustomerId);
            await _db.SaveChangesAsync();
        }
        TempData["Success"] = I18n.T("Nếu email đã được cấp quyền truy cập, liên kết đặt lại mật khẩu đã được gửi tới email đó.");
        return RedirectToAction(nameof(Login));
    }

    // ======================= Trang của khách hàng =======================

    public async Task<IActionResult> Index()
    {
        var c = await CurrentAsync();
        if (c == null) return ToLogin();
        var cid = c.CustomerId!.Value;
        ViewBag.OpenTickets = await _db.Tickets.IgnoreQueryFilters().AsNoTracking().Where(t => t.CustomerId == cid && (t.Status == TicketStatus.New || t.Status == TicketStatus.InProgress || t.Status == TicketStatus.WaitingCustomer || t.Status == TicketStatus.Resolved))
            .OrderByDescending(t => t.CreatedAt).Take(10).ToListAsync();
        ViewBag.Outstanding = await _db.Invoices.IgnoreQueryFilters().Where(i => i.CustomerId == cid && (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid))
            .SumAsync(i => (decimal?)(i.Total - i.PaidAmount)) ?? 0;
        ViewBag.ActiveContracts = await _db.Contracts.IgnoreQueryFilters().CountAsync(x => x.CustomerId == cid && x.Status == ContractStatus.Active);
        return View(c);
    }

    public async Task<IActionResult> Tickets()
    {
        var c = await CurrentAsync();
        if (c == null) return ToLogin();
        return View(await _db.Tickets.IgnoreQueryFilters().AsNoTracking().Where(t => t.CustomerId == c.CustomerId).OrderByDescending(t => t.CreatedAt).Take(200).ToListAsync());
    }

    public async Task<IActionResult> Ticket(int id)
    {
        var c = await CurrentAsync();
        if (c == null) return ToLogin();
        var t = await _db.Tickets.IgnoreQueryFilters().Include(x => x.Comments).ThenInclude(x => x.Author).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == c.CustomerId);
        if (t == null) return NotFound();
        t.Comments = t.Comments.Where(x => !x.IsInternal && !x.IsSystem).OrderBy(x => x.CreatedAt).ToList();
        return View(t);
    }

    [HttpGet]
    public async Task<IActionResult> NewTicket()
    {
        var c = await CurrentAsync();
        if (c == null) return ToLogin();
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> NewTicket(string? subject, string? description, string? category)
    {
        var c = await CurrentAsync();
        if (c == null) return ToLogin();
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(description))
        {
            ModelState.AddModelError(string.Empty, I18n.T("Vui lòng nhập tiêu đề và mô tả yêu cầu."));
            return View();
        }
        var prefix = $"YC-{DateTime.Today:yyyy}-";
        var n = await _db.Tickets.IgnoreQueryFilters().CountAsync(x => x.Number.StartsWith(prefix)) + 1;
        string no;
        do no = $"{prefix}{n++:0000}"; while (await _db.Tickets.IgnoreQueryFilters().AnyAsync(x => x.Number == no));
        var t = new Ticket
        {
            Number = no, Subject = Cut(subject.Trim(), 200), Description = Cut(description.Trim(), 4000), CustomerId = c.CustomerId, ContactId = c.Id,
            Channel = TicketChannel.Web, Priority = TicketPriority.Normal, Status = TicketStatus.New, CreatedAt = DateTime.Now,
            Category = string.IsNullOrWhiteSpace(category) ? null : Cut(category, 100)
        };
        t.ApplySla();
        t.Comments.Add(new TicketComment { Body = I18n.T("Khách hàng gửi yêu cầu qua cổng khách hàng ({0}).", c.FullName), IsSystem = true, IsInternal = true, CreatedAt = DateTime.Now });
        _db.Tickets.Add(t);
        await _db.SaveChangesAsync();
        var notify = HttpContext.RequestServices.GetRequiredService<NotificationService>();
        var owner = c.Customer?.OwnerId;
        foreach (var uid in await _db.Users.Where(u => u.IsActive && (u.Role == AppRoles.Support || u.Id == owner)).Select(u => u.Id).ToListAsync())
            notify.Add(uid, "ticket", I18n.T("Phiếu hỗ trợ mới từ cổng khách hàng: {0} – {1}", t.Number, t.Subject), $"/Tickets/Details/{t.Id}", null, c.Customer?.Name);
        await _db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã gửi yêu cầu {0}. Chúng tôi sẽ phản hồi sớm nhất.", t.Number);
        return RedirectToAction(nameof(Ticket), new { id = t.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reply(int id, string? body)
    {
        var c = await CurrentAsync();
        if (c == null) return ToLogin();
        var t = await _db.Tickets.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == c.CustomerId);
        if (t == null) return NotFound();
        if (t.Status == TicketStatus.Closed) { TempData["Error"] = I18n.T("Yêu cầu đã đóng. Vui lòng tạo yêu cầu mới."); return RedirectToAction(nameof(Ticket), new { id }); }
        if (string.IsNullOrWhiteSpace(body)) return RedirectToAction(nameof(Ticket), new { id });
        t.Comments.Add(new TicketComment { Body = Cut(body.Trim(), 4000), FromCustomer = true, AuthorName = Cut(c.FullName, 150), CreatedAt = DateTime.Now });
        if (t.Status is TicketStatus.WaitingCustomer or TicketStatus.Resolved) { t.Status = TicketStatus.InProgress; t.ResolvedAt = null; }
        t.UpdatedAt = DateTime.Now;
        HttpContext.RequestServices.GetRequiredService<NotificationService>().Add(t.OwnerId, "ticket", I18n.T("Khách hàng trả lời phiếu {0}: {1}", t.Number, t.Subject), $"/Tickets/Details/{t.Id}", null, Cut(body, 200));
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Ticket), new { id });
    }

    /// <summary>Khách đánh giá và đóng yêu cầu đã được giải quyết</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Rate(int id, int score, string? comment)
    {
        var c = await CurrentAsync();
        if (c == null) return ToLogin();
        var t = await _db.Tickets.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == c.CustomerId);
        if (t == null || t.Status != TicketStatus.Resolved || score is < 1 or > 5) return BadRequest();
        t.SatisfactionScore = score; t.SatisfactionComment = string.IsNullOrWhiteSpace(comment) ? null : Cut(comment.Trim(), 1000);
        t.Status = TicketStatus.Closed; t.ClosedAt = DateTime.Now;
        t.Comments.Add(new TicketComment { Body = I18n.T("Khách hàng đánh giá {0}/5 và đóng yêu cầu qua cổng khách hàng.", score), IsSystem = true, IsInternal = true, CreatedAt = DateTime.Now });
        await _db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Cảm ơn Quý khách đã đánh giá!");
        return RedirectToAction(nameof(Ticket), new { id });
    }

    public async Task<IActionResult> Invoices()
    {
        var c = await CurrentAsync();
        if (c == null) return ToLogin();
        ViewBag.Contracts = await _db.Contracts.IgnoreQueryFilters().AsNoTracking().Where(x => x.CustomerId == c.CustomerId && x.Status != ContractStatus.Draft && x.Status != ContractStatus.Cancelled)
            .OrderByDescending(x => x.SignDate).ToListAsync();
        return View(await _db.Invoices.IgnoreQueryFilters().AsNoTracking().Where(i => i.CustomerId == c.CustomerId && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Cancelled)
            .OrderByDescending(i => i.InvoiceDate).ToListAsync());
    }

    public async Task<IActionResult> Kb(string? q, int? id)
    {
        var c = await CurrentAsync();
        if (c == null) return ToLogin();
        var list = await _db.KbArticles.AsNoTracking().Where(a => a.IsActive && a.IsPublic).OrderByDescending(a => a.UpdatedAt).ToListAsync();
        ViewBag.Article = id.HasValue ? list.FirstOrDefault(a => a.Id == id) : null;
        ViewBag.Q = q;
        return View(string.IsNullOrWhiteSpace(q) ? list : KbSearch.Rank(list, q).ToList());
    }

    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
}

/// <summary>Nhân viên mời / thu hồi quyền vào cổng khách hàng của người liên hệ</summary>
public class PortalAccessController : CrmControllerBase
{
    public PortalAccessController(CrmDbContext db) : base(db) { }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Invite(int contactId)
    {
        var c = await Db.Contacts.Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == contactId);
        if (c == null) return NotFound();
        if (!await CanAccessAsync(c)) return NotAllowed();
        if (string.IsNullOrWhiteSpace(c.Email) || c.CustomerId == null)
        {
            TempData["Error"] = I18n.T("Người liên hệ cần có email và thuộc một khách hàng.");
            return RedirectToAction("Edit", "Contacts", new { id = contactId });
        }
        var mail = HttpContext.RequestServices.GetRequiredService<EmailService>();
        c.PortalEnabled = true;
        var dp = HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("CRM.Portal.Password").ToTimeLimitedDataProtector();
        var link = $"{Request.Scheme}://{Request.Host}{Url.Action("SetPassword", "Portal", new { t = PortalController.MakeToken(dp, c, TimeSpan.FromDays(3)) })}";
        var s = await mail.SettingsAsync();
        mail.Queue(c.Email!, I18n.T("Mời sử dụng cổng khách hàng – {0}", c.Customer?.Name ?? ""),
            EmailService.ToHtml(I18n.T("Kính gửi {0},", c.FullName) + "\n\n" + I18n.T("Quý khách được mời sử dụng cổng khách hàng để gửi và theo dõi yêu cầu hỗ trợ, xem hoá đơn và hợp đồng.")
                + "\n" + I18n.T("Bấm vào liên kết sau để đặt mật khẩu (hiệu lực 3 ngày):") + "\n" + link + "\n\n" + I18n.T("Trân trọng,") + "\n" + User.GetFullName()),
            "Portal", c.Id, c.CustomerId, CurrentUserId);
        AuditTrail.Write(Db, nameof(Contact), c.Id, "Update", c.FullName, new[] { new AuditChange("PortalEnabled", null, I18n.T("Mời vào cổng khách hàng")) });
        await Db.SaveChangesAsync();
        TempData["Success"] = s.IsReady ? I18n.T("Đã gửi lời mời tới {0}.", c.Email) : I18n.T("Đã tạo lời mời nhưng máy chủ gửi thư chưa được cấu hình – email sẽ được gửi khi cấu hình xong.");
        return RedirectToAction("Edit", "Contacts", new { id = contactId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(int contactId)
    {
        var c = await Db.Contacts.FirstOrDefaultAsync(x => x.Id == contactId);
        if (c == null) return NotFound();
        if (!await CanAccessAsync(c)) return NotAllowed();
        c.PortalEnabled = false; c.PortalPasswordHash = null;
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã thu hồi quyền vào cổng khách hàng của {0}.", c.FullName);
        return RedirectToAction("Edit", "Contacts", new { id = contactId });
    }
}
