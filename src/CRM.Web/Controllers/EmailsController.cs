using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

public class EmailFilter : ListFilter
{
    public EmailStatus? Status { get; set; }
}

public class ComposeEmailViewModel
{
    public string Type { get; set; } = "";
    public int Id { get; set; }
    public string? To { get; set; }
    public string? Cc { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public int? TemplateId { get; set; }
    public bool IncludeDocument { get; set; } = true;
    public int[] AttachmentIds { get; set; } = Array.Empty<int>();
}

public class CampaignEmailViewModel
{
    public int CampaignId { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public int? TemplateId { get; set; }
    /// <summary>Gửi cả đối tượng đã tiếp cận trước đó (mặc định chỉ đối tượng chưa tiếp cận)</summary>
    public bool IncludeContacted { get; set; }
}

/// <summary>
/// Email: soạn gửi từ khách hàng / báo giá / hợp đồng / hoá đơn / phiếu hỗ trợ (tự ghi vào lịch sử hoạt động),
/// hộp thư đi, mẫu email, cấu hình máy chủ gửi thư, gửi email hàng loạt cho chiến dịch, từ chối nhận tin.
/// </summary>
public class EmailsController : CrmControllerBase
{
    private readonly EmailService _mail;
    private readonly EmailComposer _composer;
    private readonly IDataProtector _unsub;

    public EmailsController(CrmDbContext db, EmailService mail, EmailComposer composer, IDataProtectionProvider dp) : base(db)
    {
        _mail = mail; _composer = composer; _unsub = dp.CreateProtector("CRM.Unsubscribe");
    }

    // ======================= Hộp thư đi =======================

    public async Task<IActionResult> Index(EmailFilter f)
    {
        var q = Db.EmailMessages.AsNoTracking();
        if (!User.SeesAll()) { var me = CurrentUserId; q = q.Where(m => m.CreatedById == me); }
        ViewBag.Counts = await q.GroupBy(m => m.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        if (f.Status.HasValue) q = q.Where(m => m.Status == f.Status);
        if (!string.IsNullOrWhiteSpace(f.Keyword)) q = q.Where(m => m.To.Contains(f.Keyword) || m.Subject.Contains(f.Keyword));
        var total = await q.CountAsync();
        var items = await q.Include(m => m.CreatedBy).OrderByDescending(m => m.Id).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();
        ViewBag.Filter = f;
        ViewBag.Ready = await _mail.IsReadyAsync();
        return View(new PagedList<EmailMessage>(items, total, f.Page, f.PageSize));
    }

    public async Task<IActionResult> Show(int id)
    {
        var m = await Db.EmailMessages.Include(x => x.CreatedBy).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (m == null) return NotFound();
        if (!User.SeesAll() && m.CreatedById != CurrentUserId) return NotAllowed();
        ViewBag.Settings = await _mail.SettingsAsync();
        return View(m);
    }

    /// <summary>Gửi lại email lỗi</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Retry(int id)
    {
        var m = await Db.EmailMessages.FindAsync(id);
        if (m == null) return NotFound();
        if (!User.SeesAll() && m.CreatedById != CurrentUserId) return NotAllowed();
        if (m.Status != EmailStatus.Failed) return BadRequest();
        m.Status = EmailStatus.Queued; m.Attempts = 0; m.Error = null;
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã đưa email vào hàng đợi gửi lại.");
        return RedirectToAction(nameof(Show), new { id });
    }

    // ======================= Soạn email từ bản ghi =======================

    /// <summary>Báo giá có chiết khấu vượt hạn mức chưa duyệt thì không được gửi cho khách</summary>
    private async Task<bool> QuoteBlockedAsync(string type, int id) =>
        type == nameof(Quote) && await Db.Quotes.AnyAsync(q => q.Id == id && (q.ApprovalStatus == QuoteApproval.Pending || q.ApprovalStatus == QuoteApproval.Rejected));

    public async Task<IActionResult> Compose(string type, int id, int? templateId)
    {
        var ctx = await _composer.BuildAsync(type, id, User);
        if (ctx == null) return NotAllowed();
        if (await QuoteBlockedAsync(type, id))
        {
            TempData["Error"] = I18n.T("Báo giá có chiết khấu vượt hạn mức chưa được duyệt – chưa thể gửi / chốt với khách hàng.");
            return Redirect(ctx.BackUrl ?? "/");
        }
        var templates = await TemplatesFor(type);
        var tpl = templateId.HasValue ? templates.FirstOrDefault(t => t.Id == templateId) : templates.FirstOrDefault(t => t.Scope == type);
        var vm = new ComposeEmailViewModel
        {
            Type = type, Id = id, To = ctx.To, TemplateId = tpl?.Id,
            Subject = tpl != null ? EmailService.Merge(tpl.Subject, ctx.Vars) : ctx.Title,
            Body = tpl != null ? EmailService.Merge(tpl.Body, ctx.Vars) : I18n.T("Kính gửi {0},", ctx.Vars.GetValueOrDefault("NguoiLienHe") ?? "") + "\n\n\n\n" + I18n.T("Trân trọng,") + "\n" + ctx.Vars["NhanVien"]
        };
        await LoadComposeAsync(ctx, templates);
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Compose(ComposeEmailViewModel vm)
    {
        var ctx = await _composer.BuildAsync(vm.Type, vm.Id, User);
        if (ctx == null) return NotAllowed();
        if (await QuoteBlockedAsync(vm.Type, vm.Id)) return BadRequest();
        if (!EmailService.IsValidList(vm.To)) ModelState.AddModelError(nameof(vm.To), I18n.T("Địa chỉ người nhận không hợp lệ (nhiều địa chỉ cách nhau bằng dấu phẩy)."));
        if (!string.IsNullOrWhiteSpace(vm.Cc) && !EmailService.IsValidList(vm.Cc)) ModelState.AddModelError(nameof(vm.Cc), I18n.T("Địa chỉ CC không hợp lệ."));
        if (string.IsNullOrWhiteSpace(vm.Subject)) ModelState.AddModelError(nameof(vm.Subject), I18n.T("Vui lòng nhập tiêu đề."));
        if (string.IsNullOrWhiteSpace(vm.Body)) ModelState.AddModelError(nameof(vm.Body), I18n.T("Vui lòng nhập nội dung."));
        // Chỉ đính kèm file của chính bản ghi này
        var allowed = ctx.AttachmentTarget.HasValue
            ? await Db.Attachments.Where(a => a.TargetType == ctx.AttachmentTarget && a.TargetId == ctx.Id).Select(a => a.Id).ToListAsync() : new List<int>();
        var attIds = vm.AttachmentIds.Where(allowed.Contains).ToList();
        if (!ModelState.IsValid)
        {
            await LoadComposeAsync(ctx, await TemplatesFor(vm.Type));
            return View(vm);
        }
        var html = EmailService.ToHtml(vm.Body!.Trim());
        if (vm.IncludeDocument && ctx.DocumentHtml != null) html += ctx.DocumentHtml;
        var m = _mail.Queue(vm.To!.Trim(), vm.Subject!.Trim(), html, vm.Type, vm.Id, ctx.CustomerId, CurrentUserId, vm.Cc?.Trim(), attIds);

        // Ghi vào lịch sử chăm sóc khách hàng (hoạt động Email đã hoàn thành)
        if (ctx.CustomerId.HasValue || ctx.ContactId.HasValue || ctx.OpportunityId.HasValue)
        {
            var plain = vm.Body!.Trim();
            Db.Activities.Add(new Activity
            {
                Type = ActivityType.Email, Subject = Cut(I18n.T("Gửi email: {0}", vm.Subject!.Trim()), 200), Status = ActivityStatus.Completed,
                DueDate = DateTime.Now, CompletedAt = DateTime.Now, OwnerId = CurrentUserId, AssignedById = CurrentUserId, Progress = 100,
                CustomerId = ctx.CustomerId, ContactId = ctx.ContactId, OpportunityId = ctx.OpportunityId,
                Description = Cut(I18n.T("Đến: {0}", vm.To!.Trim()) + "\n" + plain, 2000)
            });
        }
        // Báo giá nháp gửi cho khách → chuyển "Đã gửi"
        if (vm.Type == nameof(Quote))
        {
            var q = await Db.Quotes.FindAsync(vm.Id);
            if (q != null && q.Status == QuoteStatus.Draft) q.Status = QuoteStatus.Sent;
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = await _mail.IsReadyAsync()
            ? I18n.T("Đã đưa email tới {0} vào hàng đợi gửi.", vm.To!.Trim())
            : I18n.T("Đã lưu email vào hàng đợi nhưng máy chủ gửi thư chưa được cấu hình – email sẽ được gửi khi quản trị cấu hình xong.");
        return Redirect(ctx.BackUrl ?? Url.Action(nameof(Show), new { id = m.Id })!);
    }

    /// <summary>Xem trước nội dung mẫu đã trộn biến (JSON) khi đổi mẫu trên form soạn</summary>
    public async Task<IActionResult> Render(string type, int id, int templateId)
    {
        var ctx = await _composer.BuildAsync(type, id, User);
        var t = await Db.EmailTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == templateId && x.IsActive);
        if (ctx == null || t == null) return NotFound();
        return Json(new { subject = EmailService.Merge(t.Subject, ctx.Vars), body = EmailService.Merge(t.Body, ctx.Vars) });
    }

    private async Task LoadComposeAsync(EmailContext ctx, List<EmailTemplate> templates)
    {
        ViewBag.Context = ctx;
        ViewBag.Templates = templates;
        ViewBag.Attachments = ctx.AttachmentTarget.HasValue
            ? await Db.Attachments.AsNoTracking().Where(a => a.TargetType == ctx.AttachmentTarget && a.TargetId == ctx.Id).OrderBy(a => a.FileName).ToListAsync()
            : new List<Attachment>();
        ViewBag.Ready = await _mail.IsReadyAsync();
    }

    private Task<List<EmailTemplate>> TemplatesFor(string scope) =>
        Db.EmailTemplates.AsNoTracking().Where(t => t.IsActive && t.Code == null && (t.Scope == null || t.Scope == scope))
            .OrderBy(t => t.Scope == null).ThenBy(t => t.Name).ToListAsync();

    // ======================= Gửi email cho chiến dịch =======================

    public async Task<IActionResult> Campaign(int id)
    {
        var c = await Db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        var vm = new CampaignEmailViewModel { CampaignId = id, Subject = c.Name, Body = c.Message };
        await LoadCampaignAsync(c);
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Campaign(CampaignEmailViewModel vm)
    {
        var c = await Db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == vm.CampaignId);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        if (string.IsNullOrWhiteSpace(vm.Subject) || string.IsNullOrWhiteSpace(vm.Body))
        {
            ModelState.AddModelError(string.Empty, I18n.T("Vui lòng nhập tiêu đề và nội dung."));
            await LoadCampaignAsync(c);
            return View(vm);
        }
        var members = await Recipients(vm.CampaignId, vm.IncludeContacted).ToListAsync();
        // Không gửi trùng: bỏ đối tượng đang có email chiến dịch chờ gửi
        var queued = await Db.EmailMessages.Where(m => m.RelatedType == nameof(Models.Entities.Campaign) && m.RelatedId == vm.CampaignId && m.Status == EmailStatus.Queued && m.CampaignMemberId != null)
            .Select(m => m.CampaignMemberId!.Value).ToListAsync();
        var me = await Db.Users.AsNoTracking().FirstAsync(u => u.Id == CurrentUserId);
        var s = await _mail.SettingsAsync();
        var n = 0;
        foreach (var mem in members.Where(m => !queued.Contains(m.Id)))
        {
            var unsub = EmailService.Absolute(s, Url.Action(nameof(Unsubscribe), new { t = _unsub.Protect(mem.Id.ToString()) }));
            var vars = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Ten"] = mem.FullName, ["NguoiLienHe"] = mem.FullName, ["KhachHang"] = mem.Company, ["NhanVien"] = me.FullName,
                ["EmailNhanVien"] = me.Email, ["Ngay"] = DateTime.Today.ToDate(), ["LinkHuyDangKy"] = unsub
            };
            var body = EmailService.ToHtml(EmailService.Merge(vm.Body!, vars))
                + $"<p style=\"color:#9ca3af;font-size:11px;margin-top:24px\">{I18n.T("Không muốn nhận thêm email từ chúng tôi?")} <a href=\"{unsub}\" style=\"color:#9ca3af\">{I18n.T("Từ chối nhận tin")}</a></p>";
            _mail.Queue(mem.Email!, EmailService.Merge(vm.Subject!, vars), body, nameof(Models.Entities.Campaign), vm.CampaignId, mem.CustomerId, CurrentUserId, campaignMemberId: mem.Id);
            n++;
        }
        await Db.SaveChangesAsync();
        TempData[n > 0 ? "Success" : "Error"] = n > 0 ? I18n.T("Đã đưa {0} email vào hàng đợi gửi.", n) : I18n.T("Không có đối tượng nào có email để gửi.");
        return RedirectToAction("Details", "Campaigns", new { id = vm.CampaignId });
    }

    private IQueryable<CampaignMember> Recipients(int campaignId, bool includeContacted) =>
        Db.CampaignMembers.Where(m => m.CampaignId == campaignId && m.Email != null && m.Email != ""
            && m.Status != CampaignMemberStatus.Unsubscribed && m.Status != CampaignMemberStatus.Declined
            && (includeContacted || m.Status == CampaignMemberStatus.Pending));

    private async Task LoadCampaignAsync(Campaign c)
    {
        ViewBag.Campaign = c;
        ViewBag.PendingCount = await Recipients(c.Id, false).CountAsync();
        ViewBag.AllCount = await Recipients(c.Id, true).CountAsync();
        ViewBag.NoEmail = await Db.CampaignMembers.CountAsync(m => m.CampaignId == c.Id && (m.Email == null || m.Email == ""));
        ViewBag.Templates = await TemplatesFor(nameof(Models.Entities.Campaign));
        ViewBag.Ready = await _mail.IsReadyAsync();
    }

    /// <summary>Người nhận bấm "Từ chối nhận tin" trong email chiến dịch (không cần đăng nhập)</summary>
    [AllowAnonymous]
    public async Task<IActionResult> Unsubscribe(string t)
    {
        int memberId;
        try { memberId = int.Parse(_unsub.Unprotect(t)); } catch { return View("Unsubscribe", false); }
        var mem = await Db.CampaignMembers.FindAsync(memberId);
        if (mem == null) return View("Unsubscribe", false);
        mem.Status = CampaignMemberStatus.Unsubscribed;
        mem.LastContactAt = DateTime.Now;
        mem.Note = Cut(((mem.Note ?? "") + " " + I18n.T("[Tự từ chối nhận tin qua email {0}]", DateTime.Now.ToDateTime())).Trim(), 500);
        await Db.SaveChangesAsync();
        return View("Unsubscribe", true);
    }

    // ======================= Mẫu email =======================

    [Authorize(Roles = AppRoles.ManagerGroup)]
    public async Task<IActionResult> Templates() =>
        View(await Db.EmailTemplates.AsNoTracking().OrderBy(t => t.Code != null).ThenBy(t => t.Scope).ThenBy(t => t.Name).ToListAsync());

    [Authorize(Roles = AppRoles.ManagerGroup)]
    public async Task<IActionResult> Template(int? id)
    {
        var t = id.HasValue ? await Db.EmailTemplates.FindAsync(id.Value) : new EmailTemplate();
        if (t == null) return NotFound();
        return View(t);
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Template(EmailTemplate model)
    {
        if (!ModelState.IsValid) return View(model);
        EmailTemplate t;
        if (model.Id == 0) { t = new EmailTemplate(); Db.EmailTemplates.Add(t); }
        else t = await Db.EmailTemplates.FindAsync(model.Id) ?? throw new InvalidOperationException();
        t.Name = model.Name.Trim(); t.Subject = model.Subject.Trim(); t.Body = model.Body; t.IsActive = model.IsActive; t.UpdatedAt = DateTime.Now;
        if (t.Code == null) t.Scope = string.IsNullOrWhiteSpace(model.Scope) ? null : model.Scope;   // mẫu hệ thống giữ nguyên phạm vi
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu mẫu email \"{0}\".", t.Name);
        return RedirectToAction(nameof(Templates));
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTemplate(int id)
    {
        var t = await Db.EmailTemplates.FindAsync(id);
        if (t == null) return NotFound();
        if (t.Code != null)
        {
            TempData["Error"] = I18n.T("Không xóa được mẫu hệ thống – có thể sửa nội dung hoặc tắt.");
            return RedirectToAction(nameof(Templates));
        }
        Db.EmailTemplates.Remove(t);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa mẫu email \"{0}\".", t.Name);
        return RedirectToAction(nameof(Templates));
    }

    // ======================= Cấu hình máy chủ gửi thư =======================

    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Settings()
    {
        var s = await _mail.SettingsAsync();
        if (string.IsNullOrWhiteSpace(s.SiteUrl)) s.SiteUrl = $"{Request.Scheme}://{Request.Host}";
        ViewBag.HasPassword = !string.IsNullOrEmpty(s.PasswordProtected);
        return View(s);
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(SmtpSettings model, string? password, bool clearPassword, string? testTo)
    {
        var cur = await _mail.SettingsAsync();
        model.PasswordProtected = clearPassword ? null : !string.IsNullOrEmpty(password) ? _mail.Protect(password) : cur.PasswordProtected;
        model.Host = model.Host?.Trim() ?? ""; model.FromEmail = model.FromEmail?.Trim() ?? "";
        if (model.Port is < 1 or > 65535) model.Port = 587;
        if (!string.IsNullOrWhiteSpace(model.FromEmail) && !EmailService.IsValidList(model.FromEmail))
        {
            TempData["Error"] = I18n.T("Email người gửi không hợp lệ.");
            return RedirectToAction(nameof(Settings));
        }
        await HttpContext.RequestServices.GetRequiredService<SettingsStore>().SaveAsync(SmtpSettings.Key, model);
        AuditTrail.Write(Db, "Settings", 0, "Update", I18n.T("Cấu hình máy chủ gửi thư"),
            new[] { new AuditChange("Host", cur.Host, model.Host), new AuditChange("FromEmail", cur.FromEmail, model.FromEmail), new AuditChange("Enabled", cur.Enabled.ToString(), model.Enabled.ToString()) }
                .Where(c => c.o != c.n));
        await Db.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(testTo))
        {
            if (!EmailService.IsValidList(testTo)) { TempData["Error"] = I18n.T("Địa chỉ nhận thư thử không hợp lệ."); return RedirectToAction(nameof(Settings)); }
            try
            {
                var test = new EmailMessage { To = testTo.Trim(), Subject = I18n.T("[CRM] Email thử nghiệm"), Body = EmailService.ToHtml(I18n.T("Máy chủ gửi thư đã được cấu hình đúng. Thời gian gửi: {0}.", DateTime.Now.ToDateTime())) };
                var temp = new SmtpSettings
                {
                    Enabled = true, Host = model.Host, Port = model.Port, EnableSsl = model.EnableSsl, Username = model.Username, PasswordProtected = model.PasswordProtected,
                    FromEmail = model.FromEmail, FromName = model.FromName, Footer = model.Footer
                };
                await _mail.SendAsync(test, temp);
                TempData["Success"] = I18n.T("Đã lưu cấu hình và gửi thư thử tới {0} thành công.", testTo.Trim());
            }
            catch (Exception ex)
            {
                TempData["Error"] = I18n.T("Đã lưu cấu hình nhưng gửi thư thử thất bại: {0}", ex.InnerException?.Message ?? ex.Message);
            }
        }
        else TempData["Success"] = I18n.T("Đã lưu cấu hình máy chủ gửi thư.");
        return RedirectToAction(nameof(Settings));
    }

    private static string Cut(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;
}
