using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>
/// Chăm sóc khách hàng: phiếu yêu cầu hỗ trợ (ticket).
/// Mới → Đang xử lý ⇄ Chờ khách phản hồi → Đã giải quyết (bắt buộc ghi cách giải quyết) → Đã đóng (ghi nhận mức hài lòng); mở lại được.
/// SLA theo độ ưu tiên: hạn phản hồi lần đầu + hạn giải quyết, tính từ lúc tiếp nhận.
/// Quyền: Admin / Manager / Nhân viên CSKH thấy và xử lý mọi phiếu; Sales thấy phiếu được giao hoặc của khách hàng mình phụ trách.
/// </summary>
public class TicketsController : CrmControllerBase
{
    public TicketsController(CrmDbContext db) : base(db) { }

    public async Task<IActionResult> Index(TicketFilter f)
    {
        var now = DateTime.Now;
        var me = CurrentUserId;
        var baseQ = Db.Tickets.VisibleTickets(User);
        if (User.IsSupportDesk() && f.OwnerId.HasValue) baseQ = baseQ.Where(x => x.OwnerId == f.OwnerId);
        if (f.Priority.HasValue) baseQ = baseQ.Where(x => x.Priority == f.Priority);
        if (!string.IsNullOrWhiteSpace(f.Category)) baseQ = baseQ.Where(x => x.Category == f.Category);
        if (f.CustomerId.HasValue) baseQ = baseQ.Where(x => x.CustomerId == f.CustomerId);
        if (!string.IsNullOrWhiteSpace(f.Keyword))
            baseQ = baseQ.Where(x => x.Number.Contains(f.Keyword) || x.Subject.Contains(f.Keyword)
                || x.Customer!.Name.Contains(f.Keyword) || x.Customer.Code.Contains(f.Keyword)
                || (x.Contact != null && x.Contact.FullName.Contains(f.Keyword)));

        var open = baseQ.Where(x => x.Status == TicketStatus.New || x.Status == TicketStatus.InProgress || x.Status == TicketStatus.WaitingCustomer);
        var breach = open.Where(x => x.ResolveDueAt < now || (x.FirstResponseAt == null && x.ResponseDueAt < now));
        ViewBag.Counts = new Dictionary<string, int>
        {
            ["open"] = await open.CountAsync(),
            ["mine"] = await open.CountAsync(x => x.OwnerId == me),
            ["unassigned"] = await open.CountAsync(x => x.OwnerId == null),
            ["breach"] = await breach.CountAsync(),
            ["closed"] = await baseQ.CountAsync(x => x.Status == TicketStatus.Resolved || x.Status == TicketStatus.Closed),
            ["all"] = await baseQ.CountAsync(),
        };

        // Chỉ số 30 ngày gần nhất (trên cùng phạm vi lọc)
        var from = now.AddDays(-30);
        var recentResolved = baseQ.Where(x => x.ResolvedAt != null && x.ResolvedAt >= from);
        var resolvedCount = await recentResolved.CountAsync();
        ViewBag.Resolved30 = resolvedCount;
        ViewBag.OnTime30 = resolvedCount == 0 ? (int?)null
            : (int)Math.Round(100.0 * await recentResolved.CountAsync(x => x.ResolvedAt <= x.ResolveDueAt) / resolvedCount);
        ViewBag.AvgResolveHours = await recentResolved
            .Select(x => (double?)EF.Functions.DateDiffMinute(x.CreatedAt, x.ResolvedAt!.Value)).AverageAsync() / 60.0;
        ViewBag.Csat = await baseQ.Where(x => x.SatisfactionScore != null && x.ClosedAt >= from)
            .Select(x => (double?)x.SatisfactionScore).AverageAsync();
        ViewBag.New30 = await baseQ.CountAsync(x => x.CreatedAt >= from);

        var scope = f.Scope ?? "open";
        var q = scope switch
        {
            "mine" => open.Where(x => x.OwnerId == me),
            "unassigned" => open.Where(x => x.OwnerId == null),
            "breach" => breach,
            "closed" => baseQ.Where(x => x.Status == TicketStatus.Resolved || x.Status == TicketStatus.Closed),
            "all" => baseQ,
            _ => open
        };
        var total = await q.CountAsync();
        // Phiếu đang mở lên trước, ưu tiên cao trước, hạn gần trước; phiếu đã xong theo ngày mới nhất
        var items = await q.Include(x => x.Customer).Include(x => x.Contact).Include(x => x.Owner).AsNoTracking()
            .OrderBy(x => x.Status == TicketStatus.Resolved || x.Status == TicketStatus.Closed)
            .ThenByDescending(x => x.Status == TicketStatus.Resolved || x.Status == TicketStatus.Closed ? x.CreatedAt : DateTime.MinValue)
            .ThenByDescending(x => x.Priority)
            .ThenBy(x => x.FirstResponseAt == null ? x.ResponseDueAt : x.ResolveDueAt)
            .ThenByDescending(x => x.Id)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();

        // Việc của tôi trên các phiếu hỗ trợ (việc chính + việc con)
        var myTasks = Db.Activities.Where(a => a.TicketId != null && a.OwnerId == me && a.Status == ActivityStatus.Planned);
        ViewBag.MyTasks = await myTasks.CountAsync();
        ViewBag.MyTasksOverdue = await myTasks.CountAsync(a => a.DueDate < now);
        ViewBag.MyTasksToday = await myTasks.CountAsync(a => a.DueDate >= DateTime.Today && a.DueDate < DateTime.Today.AddDays(1));
        f.Scope = scope;
        ViewBag.Filter = f;
        await LoadOwnersAsync(f.OwnerId);
        if (f.CustomerId.HasValue) ViewBag.CustomerName = await Db.Customers.Where(c => c.Id == f.CustomerId).Select(c => c.Name).FirstOrDefaultAsync();
        return View(new PagedList<Ticket>(items, total, f.Page, f.PageSize));
    }

    public async Task<IActionResult> Details(int id)
    {
        var t = await Db.Tickets.Include(x => x.Customer).Include(x => x.Contact).Include(x => x.Contract).Include(x => x.Product)
            .Include(x => x.Owner).Include(x => x.CreatedBy)
            .Include(x => x.Comments).ThenInclude(c => c.Author)
            .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (t == null) return NotFound();
        if (!await CanAccessAsync(t)) return NotAllowed();
        t.Comments = t.Comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToList();

        ViewBag.OtherTickets = await Db.Tickets.Where(x => x.CustomerId == t.CustomerId && x.Id != id)
            .OrderByDescending(x => x.CreatedAt).Take(6).AsNoTracking().ToListAsync();
        ViewBag.CustomerOpenTickets = await Db.Tickets.CountAsync(x => x.CustomerId == t.CustomerId && x.Id != id
            && (x.Status == TicketStatus.New || x.Status == TicketStatus.InProgress || x.Status == TicketStatus.WaitingCustomer));
        ViewBag.Tasks = await Db.Activities.Include(a => a.Owner).AsNoTracking().Where(a => a.TicketId == id)
            .OrderBy(a => a.Status == ActivityStatus.Planned ? 0 : 1).ThenByDescending(a => a.IsTicketMain).ThenBy(a => a.DueDate).ToListAsync();
        await LoadAgentsAsync(t.OwnerId);
        return View(t);
    }

    // ================== CÔNG VIỆC CỦA PHIẾU ==================

    /// <summary>Giao việc con trên phiếu (gọi lại khách, đi hiện trường, chuyển kỹ thuật...). CSKH giao cho bất kỳ ai; Sales chỉ giao cho mình.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTask(int id, string? subject, ActivityType type, int? ownerId, DateTime? dueDate, Priority priority, string? note)
    {
        var t = await LoadAsync(id);
        if (t == null) return NotFound();
        if (!await CanAccessAsync(t)) return NotAllowed();
        subject = subject?.Trim();
        if (string.IsNullOrEmpty(subject) || !dueDate.HasValue)
        {
            TempData["Error"] = I18n.T("Vui lòng nhập tên công việc và thời hạn.");
            return RedirectToAction(nameof(Details), null, new { id }, "tasks");
        }
        var owner = User.IsSupportDesk() ? ownerId ?? CurrentUserId : CurrentUserId;
        var ownerName = await Db.Users.Where(u => u.Id == owner && u.IsActive).Select(u => u.FullName).FirstOrDefaultAsync();
        if (ownerName == null) return BadRequest();
        if (!Enum.IsDefined(type)) type = ActivityType.Task;
        if (!Enum.IsDefined(priority)) priority = Priority.Normal;
        if (subject.Length > 200) subject = subject[..200];
        note = note?.Trim();
        Db.Activities.Add(new Activity
        {
            TicketId = t.Id, Type = type, Subject = subject, OwnerId = owner, DueDate = dueDate.Value, Priority = priority,
            CustomerId = t.CustomerId, ContactId = t.ContactId, Description = string.IsNullOrEmpty(note) ? null : note.Length > 2000 ? note[..2000] : note,
            CreatedAt = DateTime.Now
        });
        Log(t, I18n.T("Giao việc \"{0}\" cho {1}, hạn {2}.", subject, ownerName, dueDate.Value.ToDateTime()));
        await SaveAsync(t);
        TempData["Success"] = I18n.T("Đã giao việc cho {0}.", ownerName);
        return RedirectToAction(nameof(Details), null, new { id }, "tasks");
    }

    /// <summary>Hoàn thành / hủy việc con (người được giao hoặc bộ phận CSKH). Việc chính tự hoàn thành khi phiếu được giải quyết.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTask(int id, int taskId, bool cancel, string? result)
    {
        var t = await LoadAsync(id);
        if (t == null) return NotFound();
        var a = await Db.Activities.FirstOrDefaultAsync(x => x.Id == taskId && x.TicketId == id);
        if (a == null) return NotFound();
        if (!(User.IsSupportDesk() || a.OwnerId == CurrentUserId) || !await CanAccessAsync(t)) return NotAllowed();
        if (a.IsTicketMain || a.Status != ActivityStatus.Planned) return BadRequest();
        result = result?.Trim();
        if (cancel)
        {
            a.Status = ActivityStatus.Cancelled;
            Log(t, I18n.T("{0} hủy việc \"{1}\".", User.GetFullName(), a.Subject) + (string.IsNullOrEmpty(result) ? "" : " " + I18n.T("Lý do: {0}", result)));
        }
        else
        {
            a.Status = ActivityStatus.Completed;
            a.CompletedAt = DateTime.Now;
            if (!string.IsNullOrEmpty(result))
                a.Description = string.IsNullOrWhiteSpace(a.Description) ? result : a.Description + "\n" + result;
            if (a.Description?.Length > 2000) a.Description = a.Description[..2000];
            Log(t, I18n.T("{0} hoàn thành việc \"{1}\".", User.GetFullName(), a.Subject) + (string.IsNullOrEmpty(result) ? "" : " " + I18n.T("Kết quả: {0}", result)));
        }
        await SaveAsync(t);
        return RedirectToAction(nameof(Details), null, new { id }, "tasks");
    }

    /// <summary>Bảng phân công & khối lượng việc của bộ phận CSKH</summary>
    public async Task<IActionResult> Workload()
    {
        if (!User.IsSupportDesk()) return NotAllowed();
        var now = DateTime.Now; var today = DateTime.Today; var week = today.AddDays(-7);
        var open = await OpenTickets(Db.Tickets).AsNoTracking()
            .Select(t => new { t.Id, t.OwnerId, t.Priority, t.Status, t.FirstResponseAt, t.ResponseDueAt, t.ResolveDueAt }).ToListAsync();
        var tasks = await Db.Activities.AsNoTracking().Where(a => a.TicketId != null && a.Status == ActivityStatus.Planned && a.OwnerId != null)
            .Select(a => new { a.OwnerId, a.DueDate }).ToListAsync();
        var resolved = await Db.Tickets.AsNoTracking().Where(t => t.ResolvedAt >= week && t.OwnerId != null)
            .Select(t => new { t.OwnerId, OnTime = t.ResolvedAt <= t.ResolveDueAt }).ToListAsync();

        var ids = open.Where(t => t.OwnerId != null).Select(t => t.OwnerId!.Value).Concat(tasks.Select(a => a.OwnerId!.Value)).Concat(resolved.Select(r => r.OwnerId!.Value)).ToHashSet();
        var users = await Db.Users.AsNoTracking().Where(u => u.IsActive && (u.Role == AppRoles.Support || ids.Contains(u.Id))).ToListAsync();
        ViewBag.Rows = users.Select(u =>
        {
            var o = open.Where(t => t.OwnerId == u.Id).ToList();
            var k = tasks.Where(a => a.OwnerId == u.Id).ToList();
            var r = resolved.Where(x => x.OwnerId == u.Id).ToList();
            return new WorkloadRow(u.Id, u.FullName, AppRoles.DisplayName(u.Role), o.Count,
                o.Count(t => t.Priority >= TicketPriority.High),
                o.Count(t => t.ResolveDueAt < now || (t.FirstResponseAt == null && t.ResponseDueAt < now)),
                o.Count(t => t.Status == TicketStatus.WaitingCustomer),
                k.Count, k.Count(a => a.DueDate < now), k.Count(a => a.DueDate >= now && a.DueDate < today.AddDays(1)),
                r.Count, r.Count == 0 ? null : (int)Math.Round(100.0 * r.Count(x => x.OnTime) / r.Count));
        }).OrderByDescending(x => x.Role == AppRoles.DisplayName(AppRoles.Support)).ThenByDescending(x => x.Open).ThenBy(x => x.Name).ToList();

        ViewBag.Unassigned = await OpenTickets(Db.Tickets).Where(t => t.OwnerId == null).Include(t => t.Customer).AsNoTracking()
            .OrderByDescending(t => t.Priority).ThenBy(t => t.ResponseDueAt).ToListAsync();
        await LoadAgentsAsync(null);
        return View();
    }

    /// <summary>Giao nhiều phiếu chưa phân công cho một người</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkAssign(List<int> ids, int? ownerId)
    {
        if (!User.IsSupportDesk()) return NotAllowed();
        var name = ownerId.HasValue ? await Db.Users.Where(u => u.Id == ownerId && u.IsActive).Select(u => u.FullName).FirstOrDefaultAsync() : null;
        if (name == null || ids.Count == 0)
        {
            TempData["Error"] = I18n.T("Hãy chọn phiếu và người xử lý.");
            return RedirectToAction(nameof(Workload));
        }
        var list = await Db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync();
        foreach (var t in list.Where(t => t.IsOpen && t.OwnerId != ownerId))
        {
            t.OwnerId = ownerId;
            if (t.Status == TicketStatus.New) t.Status = TicketStatus.InProgress;
            Log(t, I18n.T("Phân công cho {0}.", name));
            t.UpdatedAt = DateTime.Now;
            await TicketWork.SyncAsync(Db, t);
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã giao {0} phiếu cho {1}.", list.Count, name);
        return RedirectToAction(nameof(Workload));
    }

    private static IQueryable<Ticket> OpenTickets(IQueryable<Ticket> q) =>
        q.Where(t => t.Status == TicketStatus.New || t.Status == TicketStatus.InProgress || t.Status == TicketStatus.WaitingCustomer);

    public async Task<IActionResult> Create(int? customerId, int? contractId, int? contactId)
    {
        var t = new Ticket
        {
            CustomerId = customerId, ContractId = contractId, ContactId = contactId,
            OwnerId = User.IsSupportDesk() ? CurrentUserId : null
        };
        if (contractId.HasValue && !customerId.HasValue)
            t.CustomerId = await Db.Contracts.Where(c => c.Id == contractId).Select(c => (int?)c.CustomerId).FirstOrDefaultAsync();
        await LoadFormAsync(t);
        return View("Form", t);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var t = await Db.Tickets.Include(x => x.Customer).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (t == null) return NotFound();
        if (!await CanAccessAsync(t)) return NotAllowed();
        await LoadFormAsync(t);
        return View("Form", t);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Ticket model)
    {
        foreach (var k in new[] { nameof(Ticket.Number), nameof(Ticket.Customer), nameof(Ticket.Owner), nameof(Ticket.CreatedBy) })
            ModelState.Remove(k);
        model.Subject = model.Subject?.Trim() ?? "";

        var customer = model.CustomerId.HasValue ? await Db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == model.CustomerId) : null;
        if (customer == null || !(User.IsSupportDesk() || customer.OwnerId == CurrentUserId))
            ModelState.AddModelError(nameof(model.CustomerId), I18n.T("Vui lòng chọn khách hàng"));
        if (model.ContactId.HasValue && !await Db.Contacts.AnyAsync(c => c.Id == model.ContactId && c.CustomerId == model.CustomerId))
            model.ContactId = null;
        if (model.ContractId.HasValue && !await Db.Contracts.AnyAsync(c => c.Id == model.ContractId && c.CustomerId == model.CustomerId))
            model.ContractId = null;
        if (model.OwnerId.HasValue && !await Db.Users.AnyAsync(u => u.Id == model.OwnerId && u.IsActive))
            ModelState.AddModelError(nameof(model.OwnerId), I18n.T("Người xử lý không hợp lệ"));
        if (!ModelState.IsValid)
        {
            await LoadFormAsync(model);
            return View("Form", model);
        }

        Ticket t;
        var resetDue = false;
        if (model.Id == 0)
        {
            t = new Ticket
            {
                Number = await NextNumberAsync(), CreatedById = CurrentUserId, CreatedAt = DateTime.Now,
                Status = model.OwnerId.HasValue ? TicketStatus.InProgress : TicketStatus.New
            };
            Copy(model, t);
            t.OwnerId = model.OwnerId;
            t.ApplySla();
            Db.Tickets.Add(t);
            Log(t, I18n.T("Tiếp nhận yêu cầu qua kênh {0}.", t.Channel.GetDisplayName())
                + (t.OwnerId.HasValue ? " " + I18n.T("Phân công cho {0}.", await UserNameAsync(t.OwnerId)) : ""));
        }
        else
        {
            t = await Db.Tickets.Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == model.Id) ?? throw new InvalidOperationException();
            if (!await CanAccessAsync(t)) return NotAllowed();
            var oldPriority = t.Priority;
            resetDue = false;
            var oldOwner = t.OwnerId;
            Copy(model, t);
            if (oldPriority != t.Priority)
            {
                resetDue = true;
                if (t.IsOpen) t.ApplySla();   // đổi độ ưu tiên → tính lại hạn SLA từ lúc tiếp nhận
                Log(t, I18n.T("Đổi độ ưu tiên: {0} → {1}.", oldPriority.GetDisplayName(), t.Priority.GetDisplayName()));
            }
            if (User.IsSupportDesk() && oldOwner != model.OwnerId)
            {
                t.OwnerId = model.OwnerId;
                Log(t, I18n.T("Phân công cho {0}.", await UserNameAsync(t.OwnerId)));
                if (t.Status == TicketStatus.New && t.OwnerId.HasValue) t.Status = TicketStatus.InProgress;
            }
            t.UpdatedAt = DateTime.Now;
        }
        await TicketWork.SyncAsync(Db, t, resetDue);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu phiếu hỗ trợ {0}.", t.Number);
        return RedirectToAction(nameof(Details), new { id = t.Id });
    }

    /// <summary>Nhận xử lý phiếu</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Take(int id)
    {
        var t = await LoadAsync(id);
        if (t == null) return NotFound();
        if (!User.IsSupportDesk() && t.OwnerId != CurrentUserId) return NotAllowed();
        if (!t.IsOpen) return BadRequest();
        t.OwnerId = CurrentUserId;
        if (t.Status == TicketStatus.New) t.Status = TicketStatus.InProgress;
        Log(t, I18n.T("{0} nhận xử lý phiếu.", User.GetFullName()));
        await SaveAsync(t);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Phân công người xử lý (bộ phận CSKH)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(int id, int? ownerId)
    {
        if (!User.IsSupportDesk()) return NotAllowed();
        var t = await LoadAsync(id);
        if (t == null) return NotFound();
        if (ownerId.HasValue && !await Db.Users.AnyAsync(u => u.Id == ownerId && u.IsActive)) return BadRequest();
        if (t.OwnerId != ownerId)
        {
            t.OwnerId = ownerId;
            if (t.Status == TicketStatus.New && ownerId.HasValue) t.Status = TicketStatus.InProgress;
            Log(t, ownerId.HasValue ? I18n.T("Phân công cho {0}.", await UserNameAsync(ownerId)) : I18n.T("Bỏ phân công."));
            await SaveAsync(t);
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Trao đổi: phản hồi khách (tính là lần phản hồi đầu tiên nếu chưa có) hoặc ghi chú nội bộ</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Comment(int id, string? body, bool isInternal, TicketStatus? then)
    {
        var t = await LoadAsync(id);
        if (t == null) return NotFound();
        if (!await CanAccessAsync(t)) return NotAllowed();
        body = body?.Trim();
        if (string.IsNullOrEmpty(body))
        {
            TempData["Error"] = I18n.T("Vui lòng nhập nội dung trao đổi.");
            return RedirectToAction(nameof(Details), new { id });
        }
        if (body.Length > 4000) body = body[..4000];
        var now = DateTime.Now;
        t.Comments.Add(new TicketComment { Body = body, IsInternal = isInternal, AuthorId = CurrentUserId, CreatedAt = now });
        if (!isInternal && t.IsOpen)
        {
            t.FirstResponseAt ??= now;
            if (t.Status == TicketStatus.New) t.Status = TicketStatus.InProgress;
            t.OwnerId ??= User.IsSupportDesk() ? CurrentUserId : null;
        }
        if (t.IsOpen && then is TicketStatus.InProgress or TicketStatus.WaitingCustomer && then != t.Status)
        {
            Log(t, I18n.T("Chuyển trạng thái: {0} → {1}.", t.Status.GetDisplayName(), then.Value.GetDisplayName()));
            t.Status = then.Value;
        }
        await SaveAsync(t);
        return RedirectToAction(nameof(Details), null, new { id }, "comments");
    }

    /// <summary>Chuyển giữa Đang xử lý / Chờ khách phản hồi</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(int id, TicketStatus status)
    {
        var t = await LoadAsync(id);
        if (t == null) return NotFound();
        if (!await CanAccessAsync(t)) return NotAllowed();
        if (!t.IsOpen || status is not (TicketStatus.InProgress or TicketStatus.WaitingCustomer)) return BadRequest();
        if (t.Status != status)
        {
            Log(t, I18n.T("Chuyển trạng thái: {0} → {1}.", t.Status.GetDisplayName(), status.GetDisplayName()));
            t.Status = status;
            await SaveAsync(t);
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Đã giải quyết: bắt buộc ghi cách giải quyết</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(int id, string? resolution)
    {
        var t = await LoadAsync(id);
        if (t == null) return NotFound();
        if (!await CanAccessAsync(t)) return NotAllowed();
        if (!t.IsOpen) return BadRequest();
        resolution = resolution?.Trim();
        if (string.IsNullOrEmpty(resolution))
        {
            TempData["Error"] = I18n.T("Vui lòng ghi cách giải quyết.");
            return RedirectToAction(nameof(Details), new { id });
        }
        var now = DateTime.Now;
        t.Resolution = resolution.Length > 2000 ? resolution[..2000] : resolution;
        t.Status = TicketStatus.Resolved;
        t.ResolvedAt = now;
        t.FirstResponseAt ??= now;
        t.OwnerId ??= CurrentUserId;
        Log(t, I18n.T("Đã giải quyết.") + (now > t.ResolveDueAt ? " " + I18n.T("(trễ hạn SLA)") : ""));
        await SaveAsync(t);
        TempData["Success"] = I18n.T("Phiếu {0} đã được giải quyết.", t.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Đóng phiếu, ghi nhận mức hài lòng của khách (nếu có)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int id, int? score, string? comment)
    {
        var t = await LoadAsync(id);
        if (t == null) return NotFound();
        if (!await CanAccessAsync(t)) return NotAllowed();
        if (t.Status != TicketStatus.Resolved) return BadRequest();
        t.Status = TicketStatus.Closed;
        t.ClosedAt = DateTime.Now;
        t.SatisfactionScore = score is >= 1 and <= 5 ? score : null;
        comment = comment?.Trim();
        t.SatisfactionComment = string.IsNullOrEmpty(comment) ? null : comment.Length > 500 ? comment[..500] : comment;
        Log(t, I18n.T("Đóng phiếu.") + (t.SatisfactionScore.HasValue ? " " + I18n.T("Khách đánh giá {0}/5.", t.SatisfactionScore) : ""));
        await SaveAsync(t);
        TempData["Success"] = I18n.T("Đã đóng phiếu {0}.", t.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Mở lại phiếu đã giải quyết / đã đóng (khách báo lỗi tái diễn...)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reopen(int id, string? reason)
    {
        var t = await LoadAsync(id);
        if (t == null) return NotFound();
        if (!await CanAccessAsync(t)) return NotAllowed();
        if (t.IsOpen) return BadRequest();
        t.Status = TicketStatus.InProgress;
        t.ResolvedAt = null; t.ClosedAt = null; t.SatisfactionScore = null; t.SatisfactionComment = null;
        reason = reason?.Trim();
        Log(t, I18n.T("Mở lại phiếu.") + (string.IsNullOrEmpty(reason) ? "" : " " + I18n.T("Lý do: {0}", reason.Length > 500 ? reason[..500] : reason)));
        await SaveAsync(t, resetDue: true);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (!User.SeesAll()) return NotAllowed();
        var t = await Db.Tickets.Include(x => x.Comments).FirstOrDefaultAsync(x => x.Id == id);
        if (t == null) return NotFound();
        var tasks = await Db.Activities.Where(a => a.TicketId == id).ToListAsync();
        Db.Activities.RemoveRange(tasks);
        Db.TicketComments.RemoveRange(t.Comments);
        Db.Tickets.Remove(t);
        await Db.SaveChangesAsync();
        await DeleteAttachmentsAsync(AttachmentTarget.Ticket, id);
        foreach (var a in tasks) await DeleteAttachmentsAsync(AttachmentTarget.Activity, a.Id);
        TempData["Success"] = I18n.T("Đã xóa phiếu hỗ trợ {0}.", t.Number);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Người liên hệ + hợp đồng + hàng hoá đã mua của khách hàng (cho form phiếu)</summary>
    [HttpGet]
    public async Task<IActionResult> CustomerRefs(int customerId)
    {
        var customer = await Db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId);
        if (customer == null || !(User.IsSupportDesk() || customer.OwnerId == CurrentUserId)) return Json(new { contacts = Array.Empty<object>(), contracts = Array.Empty<object>() });
        var contacts = await Db.Contacts.Where(c => c.CustomerId == customerId).OrderBy(c => c.FullName)
            .Select(c => new { id = c.Id, name = c.FullName + (c.JobTitle != null ? " (" + c.JobTitle + ")" : "") }).ToListAsync();
        var contracts = await Db.Contracts.Where(c => c.CustomerId == customerId && c.Status != ContractStatus.Cancelled && c.Status != ContractStatus.Draft)
            .OrderByDescending(c => c.SignDate)
            .Select(c => new { id = c.Id, name = c.Number + " – " + c.Title }).ToListAsync();
        var openTickets = await Db.Tickets.CountAsync(x => x.CustomerId == customerId
            && (x.Status == TicketStatus.New || x.Status == TicketStatus.InProgress || x.Status == TicketStatus.WaitingCustomer));
        return Json(new { contacts, contracts, openTickets });
    }

    // ---------- Hàm phụ ----------

    private static void Copy(Ticket from, Ticket to)
    {
        to.Subject = from.Subject; to.Description = from.Description?.Trim();
        to.CustomerId = from.CustomerId; to.ContactId = from.ContactId; to.ContractId = from.ContractId; to.ProductId = from.ProductId;
        to.Channel = from.Channel; to.Category = string.IsNullOrWhiteSpace(from.Category) ? null : from.Category.Trim();
        to.Priority = from.Priority;
    }

    /// <summary>Quyền với phiếu: CSKH, người xử lý, người phụ trách khách hàng, hoặc người được giao việc trên phiếu</summary>
    private async Task<bool> CanAccessAsync(Ticket t)
    {
        if (User.CanAccessTicket(t, t.Customer?.OwnerId)) return true;
        var me = CurrentUserId;
        return await Db.Activities.AnyAsync(a => a.TicketId == t.Id && a.OwnerId == me);
    }

    private Task<Ticket?> LoadAsync(int id) => Db.Tickets.Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == id);

    /// <summary>Lưu phiếu + đồng bộ việc chính (người xử lý / trạng thái / hạn)</summary>
    private async Task SaveAsync(Ticket t, bool resetDue = false)
    {
        t.UpdatedAt = DateTime.Now;
        await TicketWork.SyncAsync(Db, t, resetDue);
        await Db.SaveChangesAsync();
    }

    private void Log(Ticket t, string text) =>
        t.Comments.Add(new TicketComment { Body = text, IsSystem = true, IsInternal = true, AuthorId = CurrentUserId, CreatedAt = DateTime.Now });

    private async Task<string> UserNameAsync(int? id) =>
        id.HasValue ? await Db.Users.Where(u => u.Id == id).Select(u => u.FullName).FirstOrDefaultAsync() ?? "" : "";

    /// <summary>Danh sách người xử lý: nhân viên CSKH trước, sau đó quản lý / sales</summary>
    private async Task LoadAgentsAsync(int? selected)
    {
        var users = await Db.Users.Where(u => u.IsActive).OrderBy(u => u.FullName).ToListAsync();
        // Kèm số phiếu đang mở của từng người để phân công cân tải
        var load = await OpenTickets(Db.Tickets).Where(t => t.OwnerId != null)
            .GroupBy(t => t.OwnerId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var support = new SelectListGroup { Name = AppRoles.DisplayName(AppRoles.Support) };
        var others = new SelectListGroup { Name = I18n.T("Khác") };
        ViewBag.Agents = users.OrderBy(u => u.Role == AppRoles.Support ? 0 : 1).ThenBy(u => u.FullName)
            .Select(u => new SelectListItem($"{u.FullName} ({I18n.T("{0} phiếu mở", load.GetValueOrDefault(u.Id))})", u.Id.ToString(), u.Id == selected)
                { Group = u.Role == AppRoles.Support ? support : others })
            .ToList();
    }

    private async Task LoadFormAsync(Ticket t)
    {
        var customers = User.IsSupportDesk() ? Db.Customers.AsQueryable() : Db.Customers.VisibleTo(User);
        ViewBag.Customers = new SelectList(await customers.OrderBy(c => c.Name)
            .Select(c => new { c.Id, Name = c.Code + " - " + c.Name }).ToListAsync(), "Id", "Name", t.CustomerId);
        ViewBag.Contacts = new SelectList(t.CustomerId.HasValue
            ? await Db.Contacts.Where(c => c.CustomerId == t.CustomerId).OrderBy(c => c.FullName)
                .Select(c => new { c.Id, Name = c.FullName + (c.JobTitle != null ? " (" + c.JobTitle + ")" : "") }).ToListAsync()
            : new(), "Id", "Name", t.ContactId);
        ViewBag.Contracts = new SelectList(t.CustomerId.HasValue
            ? await Db.Contracts.Where(c => c.CustomerId == t.CustomerId && c.Status != ContractStatus.Cancelled && (c.Status != ContractStatus.Draft || c.Id == t.ContractId))
                .OrderByDescending(c => c.SignDate).Select(c => new { c.Id, Name = c.Number + " – " + c.Title }).ToListAsync()
            : new(), "Id", "Name", t.ContractId);
        ViewBag.Products = new SelectList(await Db.Products.Where(p => p.IsActive || p.Id == t.ProductId).OrderBy(p => p.Name)
            .Select(p => new { p.Id, Name = p.Code + " - " + p.Name }).ToListAsync(), "Id", "Name", t.ProductId);
        await LoadAgentsAsync(t.OwnerId);
    }

    private async Task<string> NextNumberAsync()
    {
        var prefix = $"YC-{DateTime.Today:yyyy}-";
        var n = await Db.Tickets.CountAsync(x => x.Number.StartsWith(prefix)) + 1;
        string no;
        do no = $"{prefix}{n++:0000}"; while (await Db.Tickets.AnyAsync(x => x.Number == no));
        return no;
    }
}
