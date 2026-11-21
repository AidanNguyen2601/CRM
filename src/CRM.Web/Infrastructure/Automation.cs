using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>Ngữ cảnh một bản ghi khi chạy quy tắc</summary>
public class AutomationTarget
{
    public string Type { get; init; } = "";
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public int? OwnerId { get; init; }
    public int BranchId { get; init; }
    public int? CustomerId { get; init; }
    public string? CustomerName { get; set; }
    public int? ContactId { get; init; }
    public string? Email { get; set; }
    public string Url { get; init; } = "/";
    public int? Days { get; set; }
}

/// <summary>
/// Chạy quy tắc tự động: sự kiện tức thời (lead / phiếu hỗ trợ mới, cơ hội chuyển giai đoạn, hợp đồng được ký) được thu thập
/// trong SaveChanges và xử lý sau khi lưu; sự kiện theo thời gian (lead bỏ quên, cơ hội không hoạt động, hoá đơn quá hạn,
/// sinh nhật liên hệ) được quét định kỳ – mỗi bản ghi chỉ chạy một lần cho mỗi mốc (nhật ký AutomationLogs).
/// </summary>
public class AutomationService
{
    public static readonly AutomationTrigger[] TimeTriggers =
        { AutomationTrigger.LeadIdle, AutomationTrigger.OpportunityIdle, AutomationTrigger.InvoiceOverdue, AutomationTrigger.ContactBirthday };

    private readonly CrmDbContext _db;
    private readonly NotificationService _notify;
    private readonly EmailService _mail;
    private readonly BranchDirectory _dir;

    public AutomationService(CrmDbContext db, NotificationService notify, EmailService mail, BranchDirectory dir)
    {
        _db = db; _notify = notify; _mail = mail; _dir = dir;
    }

    /// <summary>Thu thập sự kiện trước khi lưu (gọi trong SaveChanges)</summary>
    public static List<(AutomationTrigger, object)> Collect(DbContext db)
    {
        var list = new List<(AutomationTrigger, object)>();
        foreach (var e in db.ChangeTracker.Entries())
        {
            switch (e.Entity)
            {
                case Lead when e.State == EntityState.Added: list.Add((AutomationTrigger.LeadCreated, e.Entity)); break;
                case Ticket when e.State == EntityState.Added: list.Add((AutomationTrigger.TicketCreated, e.Entity)); break;
                case Opportunity o when e.State == EntityState.Modified && e.Property(nameof(Opportunity.Stage)).IsModified
                        && !Equals(e.Property(nameof(Opportunity.Stage)).OriginalValue, o.Stage):
                    list.Add((AutomationTrigger.OpportunityStageChanged, o)); break;
                case Contract c when c.Status == ContractStatus.Active && (e.State == EntityState.Added
                        || e.State == EntityState.Modified && e.Property(nameof(Contract.Status)).IsModified && !Equals(e.Property(nameof(Contract.Status)).OriginalValue, ContractStatus.Active)):
                    list.Add((AutomationTrigger.ContractSigned, c)); break;
            }
        }
        return list;
    }

    /// <summary>Xử lý các sự kiện tức thời đã thu thập (sau khi lưu)</summary>
    public async Task<int> ProcessPendingAsync()
    {
        if (_db.PendingEvents.Count == 0) return 0;
        var events = _db.PendingEvents.ToList();
        _db.PendingEvents.Clear();
        var triggers = events.Select(e => e.Trigger).Distinct().ToList();
        var rules = await _db.AutomationRules.Where(r => r.IsActive && triggers.Contains(r.Trigger)).ToListAsync();
        if (rules.Count == 0) return 0;
        var n = 0;
        foreach (var (trigger, entity) in events)
        {
            var target = await TargetOfAsync(entity);
            if (target == null) continue;
            foreach (var rule in rules.Where(r => r.Trigger == trigger && Matches(r, entity)))
            {
                await ExecuteAsync(rule, target, $"{trigger}:{DateTime.Now:yyyyMMddHHmmssfff}");
                n++;
            }
        }
        if (n > 0) await _db.SaveChangesAsync();
        _db.PendingEvents.Clear();   // sự kiện phát sinh do chính quy tắc (vd. công việc mới) không chạy tiếp
        return n;
    }

    private static bool Matches(AutomationRule r, object entity) => r.Condition is not int c || entity switch
    {
        Opportunity o => (int)o.Stage == c,
        Ticket t => (int)t.Priority == c,
        Lead l => (int)l.Source == c,
        _ => true
    };

    /// <summary>Quét các quy tắc theo thời gian (tác vụ nền)</summary>
    public async Task<int> RunScheduledAsync(CancellationToken ct = default)
    {
        var rules = await _db.AutomationRules.Where(r => r.IsActive && TimeTriggers.Contains(r.Trigger)).ToListAsync(ct);
        var n = 0;
        var now = DateTime.Now;
        foreach (var rule in rules)
        {
            var days = Math.Max(0, rule.Days ?? 0);
            var since = now.AddDays(-days);
            List<(object Entity, string Key)> due = new();
            switch (rule.Trigger)
            {
                case AutomationTrigger.LeadIdle:
                    due = (await _db.Leads.IgnoreQueryFilters().Where(l => l.Status == LeadStatus.New && l.CreatedAt <= since).Take(500).ToListAsync(ct))
                        .Select(l => ((object)l, "idle")).ToList();
                    break;
                case AutomationTrigger.OpportunityIdle:
                {
                    var open = await _db.Opportunities.IgnoreQueryFilters()
                        .Where(o => o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost)
                        .Select(o => new { O = o, Last = _db.Activities.IgnoreQueryFilters().Where(a => a.OpportunityId == o.Id).Max(a => (DateTime?)(a.CompletedAt ?? a.CreatedAt)) })
                        .Where(x => (x.Last ?? x.O.CreatedAt) <= since).Take(500).ToListAsync(ct);
                    due = open.Select(x => ((object)x.O, "idle:" + (x.Last ?? x.O.CreatedAt).ToString("yyyyMMdd"))).ToList();
                    break;
                }
                case AutomationTrigger.InvoiceOverdue:
                    due = (await _db.Invoices.IgnoreQueryFilters()
                            .Where(v => (v.Status == InvoiceStatus.Issued || v.Status == InvoiceStatus.PartiallyPaid) && v.DueDate != null && v.DueDate <= since.Date && v.Total > v.PaidAmount)
                            .Take(500).ToListAsync(ct))
                        .Select(v => ((object)v, "overdue")).ToList();
                    break;
                case AutomationTrigger.ContactBirthday:
                    due = (await _db.Contacts.IgnoreQueryFilters().Where(c => c.BirthDate != null && c.BirthDate.Value.Month == now.Month && c.BirthDate.Value.Day == now.Day).ToListAsync(ct))
                        .Select(c => ((object)c, "bday:" + now.Year)).ToList();
                    break;
            }
            if (due.Count == 0) continue;
            var type = due[0].Entity.GetType().Name;
            var ids = due.Select(d => IdOf(d.Entity)).ToList();
            var done = (await _db.AutomationLogs.Where(l => l.RuleId == rule.Id && l.EntityType == type && ids.Contains(l.EntityId)).Select(l => new { l.EntityId, l.Key }).ToListAsync(ct))
                .Select(x => (x.EntityId, x.Key)).ToHashSet();
            foreach (var (entity, key) in due.Where(d => !done.Contains((IdOf(d.Entity), d.Key))))
            {
                var target = await TargetOfAsync(entity);
                if (target == null) continue;
                target.Days = days;
                await ExecuteAsync(rule, target, key);
                n++;
            }
            await _db.SaveChangesAsync(ct);
        }
        return n;
    }

    private static int IdOf(object e) => (int)e.GetType().GetProperty("Id")!.GetValue(e)!;

    private async Task<AutomationTarget?> TargetOfAsync(object entity)
    {
        AutomationTarget t;
        switch (entity)
        {
            case Lead l:
                t = new AutomationTarget { Type = nameof(Lead), Id = l.Id, Title = l.FullName + (string.IsNullOrEmpty(l.Company) ? "" : $" ({l.Company})"), OwnerId = l.OwnerId,
                    BranchId = l.BranchId, Email = l.Email, Url = $"/Leads/Details/{l.Id}" };
                t.CustomerName = l.Company;
                return t;
            case Opportunity o:
                t = new AutomationTarget { Type = nameof(Opportunity), Id = o.Id, Title = o.Name, OwnerId = o.OwnerId, BranchId = o.BranchId, CustomerId = o.CustomerId,
                    ContactId = o.ContactId, Url = $"/Opportunities/Details/{o.Id}" };
                break;
            case Contract c:
                t = new AutomationTarget { Type = nameof(Contract), Id = c.Id, Title = $"{c.Number} – {c.Title}", OwnerId = c.OwnerId, BranchId = c.BranchId, CustomerId = c.CustomerId,
                    ContactId = c.ContactId, Url = $"/Contracts/Details/{c.Id}" };
                break;
            case Invoice v:
                t = new AutomationTarget { Type = nameof(Invoice), Id = v.Id, Title = $"{v.Symbol} {v.Number}", OwnerId = v.OwnerId, BranchId = v.BranchId, CustomerId = v.CustomerId,
                    Url = $"/Invoices/Details/{v.Id}" };
                break;
            case Ticket k:
                t = new AutomationTarget { Type = nameof(Ticket), Id = k.Id, Title = $"{k.Number} – {k.Subject}", OwnerId = k.OwnerId, BranchId = k.BranchId, CustomerId = k.CustomerId,
                    ContactId = k.ContactId, Url = $"/Tickets/Details/{k.Id}" };
                break;
            case Contact ct:
                t = new AutomationTarget { Type = nameof(Contact), Id = ct.Id, Title = ct.FullName, OwnerId = ct.OwnerId, BranchId = ct.BranchId, CustomerId = ct.CustomerId,
                    ContactId = ct.Id, Email = ct.Email, Url = ct.CustomerId.HasValue ? $"/Customers/Details/{ct.CustomerId}" : $"/Contacts/Edit/{ct.Id}" };
                break;
            default: return null;
        }
        if (t.CustomerId.HasValue)
            t.CustomerName = await _db.Customers.IgnoreQueryFilters().Where(c => c.Id == t.CustomerId).Select(c => c.Name).FirstOrDefaultAsync();
        if (t.Email == null)
        {
            var q = _db.Contacts.IgnoreQueryFilters().Where(c => c.Email != null && c.Email != "");
            t.Email = t.ContactId.HasValue ? await q.Where(c => c.Id == t.ContactId).Select(c => c.Email).FirstOrDefaultAsync() : null;
            if (t.Email == null && t.CustomerId.HasValue)
                t.Email = await q.Where(c => c.CustomerId == t.CustomerId).OrderByDescending(c => c.IsPrimary).Select(c => c.Email).FirstOrDefaultAsync()
                       ?? await _db.Customers.IgnoreQueryFilters().Where(c => c.Id == t.CustomerId).Select(c => c.Email).FirstOrDefaultAsync();
        }
        return t;
    }

    /// <summary>Người nhận: người phụ trách / quản lý gần nhất của người phụ trách (cùng đơn vị hoặc cấp trên) / người chỉ định</summary>
    private async Task<int?> AssigneeAsync(AutomationRule r, AutomationTarget t)
    {
        if (r.Assignee == AutomationAssignee.User) return r.AssigneeUserId;
        if (r.Assignee == AutomationAssignee.Owner && t.OwnerId.HasValue) return t.OwnerId;
        var branch = t.OwnerId.HasValue ? await _db.Users.Where(u => u.Id == t.OwnerId).Select(u => (int?)u.BranchId).FirstOrDefaultAsync() ?? t.BranchId : t.BranchId;
        var chain = _dir.Get().Ancestors(branch);
        var managers = await _db.Users.Where(u => u.IsActive && u.Role == AppRoles.Manager && chain.Contains(u.BranchId)).Select(u => new { u.Id, u.BranchId }).ToListAsync();
        var nearest = managers.OrderBy(m => chain.IndexOf(m.BranchId)).FirstOrDefault()?.Id;
        return nearest ?? await _db.Users.Where(u => u.IsActive && u.Role == AppRoles.Admin).OrderBy(u => u.Id).Select(u => (int?)u.Id).FirstOrDefaultAsync();
    }

    public static string Merge(string? text, AutomationTarget t) => EmailService.Merge(text ?? "", new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["Ten"] = t.Title, ["KhachHang"] = t.CustomerName, ["SoNgay"] = t.Days?.ToString(), ["Ngay"] = DateTime.Today.ToDate()
    });

    private async Task ExecuteAsync(AutomationRule rule, AutomationTarget t, string key)
    {
        string result;
        var who = await AssigneeAsync(rule, t);
        var title = Merge(string.IsNullOrWhiteSpace(rule.Title) ? "{{Ten}}" : rule.Title, t);
        switch (rule.Action)
        {
            case AutomationAction.CreateTask:
            {
                if (who == null) { result = I18n.T("Không xác định được người nhận"); break; }
                var due = rule.TaskDueDays == 0 ? DateTime.Now.AddHours(2) : DateTime.Today.AddDays(rule.TaskDueDays).AddHours(17);
                var task = new Activity
                {
                    Type = ActivityType.Task, Subject = Cut(title, 200), OwnerId = who, Priority = rule.TaskPriority, Status = ActivityStatus.Planned,
                    DueDate = due, Description = Cut(Merge(rule.Message, t) + "\n" + I18n.T("[Tự động – quy tắc \"{0}\"]", rule.Name), 2000), CreatedAt = DateTime.Now,
                    LeadId = t.Type == nameof(Lead) ? t.Id : null, OpportunityId = t.Type == nameof(Opportunity) ? t.Id : null,
                    TicketId = t.Type == nameof(Ticket) ? t.Id : null, CustomerId = t.CustomerId, ContactId = t.ContactId
                };
                _db.Activities.Add(task);
                await _db.SaveChangesAsync();
                _notify.Add(who, "task", I18n.T("Việc tự động: {0}", task.Subject), $"/Tasks/Details/{task.Id}", null, I18n.T("Hạn {0}", task.DueDate.ToDateTime()));
                result = I18n.T("Tạo công việc #{0}", task.Id);
                break;
            }
            case AutomationAction.Notify:
                if (who == null) { result = I18n.T("Không xác định được người nhận"); break; }
                _notify.Add(who, "automation", title, t.Url, null, Merge(rule.Message, t));
                result = I18n.T("Thông báo cho người dùng #{0}", who);
                break;
            default:
            {
                if (string.IsNullOrWhiteSpace(t.Email) || !EmailService.IsValidList(t.Email)) { result = I18n.T("Không có email người nhận"); break; }
                var tpl = rule.EmailTemplateId.HasValue ? await _db.EmailTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == rule.EmailTemplateId && x.IsActive) : null;
                var vars = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Ten"] = t.Title, ["NguoiLienHe"] = t.Title, ["KhachHang"] = t.CustomerName, ["Ngay"] = DateTime.Today.ToDate(),
                    ["CongTy"] = _dir.Get().LegalEntityOf(t.BranchId)?.Name
                };
                if (t.OwnerId.HasValue)
                {
                    var owner = await _db.Users.AsNoTracking().Where(u => u.Id == t.OwnerId).Select(u => new { u.FullName, u.Email }).FirstOrDefaultAsync();
                    vars["NhanVien"] = owner?.FullName; vars["EmailNhanVien"] = owner?.Email;
                }
                var subject = tpl != null ? EmailService.Merge(tpl.Subject, vars) : title;
                var body = tpl != null ? EmailService.Merge(tpl.Body, vars) : Merge(rule.Message, t);
                _mail.Queue(t.Email!, subject, EmailService.ToHtml(body), t.Type, t.Id, t.CustomerId);
                result = I18n.T("Gửi email tới {0}", t.Email);
                break;
            }
        }
        rule.RunCount++;
        rule.LastRunAt = DateTime.Now;
        _db.AutomationLogs.Add(new AutomationLog { RuleId = rule.Id, EntityType = t.Type, EntityId = t.Id, Key = Cut(key, 60), Result = Cut(result, 300), At = DateTime.Now });
    }

    private static string Cut(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;
}

/// <summary>Sau mỗi action: chạy quy tắc cho các sự kiện phát sinh khi lưu dữ liệu trong request</summary>
public class AutomationFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        await next();
        var db = context.HttpContext.RequestServices.GetRequiredService<CrmDbContext>();
        if (db.PendingEvents.Count == 0) return;
        try { await context.HttpContext.RequestServices.GetRequiredService<AutomationService>().ProcessPendingAsync(); }
        catch (Exception ex)
        {
            context.HttpContext.RequestServices.GetRequiredService<ILogger<AutomationFilter>>().LogWarning(ex, "Lỗi chạy quy tắc tự động");
            db.PendingEvents.Clear();
        }
    }
}

/// <summary>Tác vụ nền: mỗi giờ quét quy tắc theo thời gian, chấm lại điểm lead, xử lý gia hạn / hoá đơn định kỳ</summary>
public class AutomationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AutomationWorker> _log;
    public AutomationWorker(IServiceScopeFactory scopes, ILogger<AutomationWorker> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var sp = scope.ServiceProvider;
                var n = await sp.GetRequiredService<AutomationService>().RunScheduledAsync(ct);
                var scored = await LeadScoring.RecalculateAsync(sp.GetRequiredService<CrmDbContext>(), null, ct);
                var (renew, bills) = await sp.GetRequiredService<RenewalService>().RunAsync(ct);
                if (n + renew + bills > 0) _log.LogInformation("Tự động hoá: {Rules} lượt quy tắc, {Renew} cơ hội gia hạn, {Bills} hoá đơn định kỳ; chấm điểm {Scored} lead", n, renew, bills, scored);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogWarning(ex, "Lỗi tác vụ tự động hoá"); }
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch (OperationCanceledException) { break; }
        }
    }
}
