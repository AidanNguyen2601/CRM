using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>
/// Quản lý công việc (dựa trên bảng Hoạt động – dùng chung với Lịch làm việc và việc của phiếu hỗ trợ):
/// giao việc, bảng Kanban Cần làm / Đang làm / Hoàn thành / Đã hủy, tiến độ, đầu việc, bình luận, thông báo.
/// Xem: người thực hiện, người giao việc; quản lý / quản trị xem cả nhóm (trong phạm vi đơn vị); CSKH xem việc của phiếu hỗ trợ.
/// </summary>
public class TasksController : CrmControllerBase
{
    private readonly NotificationService _notify;
    public TasksController(CrmDbContext db, NotificationService notify) : base(db) => _notify = notify;

    public static readonly string[] Columns = { "todo", "doing", "done", "cancelled" };

    private IQueryable<Activity> Visible()
    {
        if (User.SeesAll()) return Db.Activities;
        var me = CurrentUserId;
        var desk = User.IsSupportDesk();
        return Db.Activities.Where(a => a.OwnerId == me || a.AssignedById == me || (desk && a.TicketId != null));
    }

    private bool CanManage(Activity a) => User.SeesAll() || a.AssignedById == CurrentUserId || a.OwnerId == CurrentUserId;

    public async Task<IActionResult> Index(TaskFilter f)
    {
        var me = CurrentUserId;
        var now = DateTime.Now; var today = DateTime.Today;
        f.Scope ??= "mine"; f.View ??= "board";
        var q = Visible();
        q = f.Scope switch
        {
            "assigned" => q.Where(a => a.AssignedById == me && a.OwnerId != me),
            "all" => q,
            _ => q.Where(a => a.OwnerId == me)
        };
        if (f.OwnerId.HasValue && f.Scope != "mine") q = q.Where(a => a.OwnerId == f.OwnerId);
        if (f.Type.HasValue) q = q.Where(a => a.Type == f.Type);
        if (f.Priority.HasValue) q = q.Where(a => a.Priority == f.Priority);
        if (!string.IsNullOrWhiteSpace(f.Keyword))
            q = q.Where(a => a.Subject.Contains(f.Keyword) || (a.Description != null && a.Description.Contains(f.Keyword))
                || (a.Customer != null && a.Customer.Name.Contains(f.Keyword)));
        q = f.Due switch
        {
            "overdue" => q.Where(a => a.Status == ActivityStatus.Planned && a.DueDate < now),
            "today" => q.Where(a => a.Status == ActivityStatus.Planned && a.DueDate >= today && a.DueDate < today.AddDays(1)),
            "week" => q.Where(a => a.Status == ActivityStatus.Planned && a.DueDate < today.AddDays(7)),
            _ => q
        };

        // Chỉ số nhanh (việc của tôi + tôi giao)
        var mine = Db.Activities.Where(a => a.OwnerId == me && a.Status == ActivityStatus.Planned);
        ViewBag.Stats = new Dictionary<string, int>
        {
            ["mine"] = await mine.CountAsync(),
            ["doing"] = await mine.CountAsync(a => a.StartedAt != null),
            ["overdue"] = await mine.CountAsync(a => a.DueDate < now),
            ["today"] = await mine.CountAsync(a => a.DueDate >= today && a.DueDate < today.AddDays(1)),
            ["assigned"] = await Db.Activities.CountAsync(a => a.AssignedById == me && a.OwnerId != me && a.Status == ActivityStatus.Planned),
        };

        var include = q.Include(a => a.Owner).Include(a => a.AssignedBy).Include(a => a.Customer).Include(a => a.Ticket).AsNoTracking();
        if (f.View == "list")
        {
            var total = await q.CountAsync();
            var items = await include.OrderBy(a => a.Status).ThenBy(a => a.DueDate)
                .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();
            ViewBag.Paged = new PagedList<Activity>(items, total, f.Page, f.PageSize);
        }
        else
        {
            // Bảng: việc đang chờ + việc xong / hủy trong 30 ngày gần nhất
            var since = today.AddDays(-30);
            var list = await include.Where(a => a.Status == ActivityStatus.Planned
                    || (a.Status == ActivityStatus.Completed && a.CompletedAt >= since)
                    || (a.Status == ActivityStatus.Cancelled && a.DueDate >= since))
                .OrderByDescending(a => a.Priority).ThenBy(a => a.DueDate).Take(600).ToListAsync();
            ViewBag.Board = Columns.ToDictionary(c => c, c => list.Where(a => a.Board == c).ToList());
        }
        var ids = (ViewBag.Board as Dictionary<string, List<Activity>>)?.Values.SelectMany(x => x).Select(a => a.Id).ToList()
                  ?? ((PagedList<Activity>)ViewBag.Paged).Items.Select(a => a.Id).ToList();
        ViewBag.Checklist = await Db.TaskChecklistItems.Where(c => ids.Contains(c.ActivityId))
            .GroupBy(c => c.ActivityId).Select(g => new { g.Key, Done = g.Count(c => c.IsDone), Total = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => (x.Done, x.Total));
        ViewBag.Comments = await Db.TaskComments.Where(c => ids.Contains(c.ActivityId) && !c.IsSystem)
            .GroupBy(c => c.ActivityId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);

        ViewBag.Filter = f;
        await LoadOwnersAsync(f.OwnerId);
        return View();
    }

    public async Task<IActionResult> Details(int id)
    {
        var a = await Visible().Include(x => x.Owner).Include(x => x.AssignedBy).Include(x => x.Customer).Include(x => x.Contact)
            .Include(x => x.Opportunity).Include(x => x.Lead).Include(x => x.Ticket)
            .Include(x => x.Checklist).Include(x => x.Comments).ThenInclude(c => c.Author)
            .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return await Db.Activities.AnyAsync(x => x.Id == id) ? NotAllowed() : NotFound();
        a.Checklist = a.Checklist.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToList();
        a.Comments = a.Comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToList();
        ViewBag.CanManage = CanManage(a);
        return View(a);
    }

    public async Task<IActionResult> Create(int? customerId, int? opportunityId, int? ownerId)
    {
        var vm = new TaskFormViewModel { CustomerId = customerId, OpportunityId = opportunityId, OwnerId = ownerId ?? CurrentUserId };
        await LoadFormAsync(vm);
        return View("Form", vm);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var a = await Visible().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        if (!CanManage(a)) return NotAllowed();
        var vm = new TaskFormViewModel
        {
            Id = a.Id, Subject = a.Subject, Description = a.Description, Type = a.Type, Priority = a.Priority, OwnerId = a.OwnerId,
            DueDate = a.DueDate, CustomerId = a.CustomerId, OpportunityId = a.OpportunityId, IsTicketMain = a.IsTicketMain, TicketId = a.TicketId
        };
        await LoadFormAsync(vm);
        return View("Form", vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(TaskFormViewModel vm)
    {
        vm.Subject = vm.Subject?.Trim() ?? "";
        Activity? a = null;
        if (vm.Id != 0)
        {
            a = await Visible().FirstOrDefaultAsync(x => x.Id == vm.Id);
            if (a == null) return NotFound();
            if (!CanManage(a)) return NotAllowed();
            if (a.IsTicketMain) vm.OwnerId = a.OwnerId;   // việc chính của phiếu: người thực hiện theo phiếu
            if (a.IsTicketMain) ModelState.Remove(nameof(vm.OwnerId));
        }
        var ownerName = vm.OwnerId.HasValue ? await Db.UsersInScope.Where(u => u.Id == vm.OwnerId && u.IsActive).Select(u => u.FullName).FirstOrDefaultAsync() : null;
        if (ownerName == null && !(a?.IsTicketMain ?? false)) ModelState.AddModelError(nameof(vm.OwnerId), I18n.T("Vui lòng chọn người thực hiện"));
        if (vm.CustomerId.HasValue && !await Db.Customers.AnyAsync(c => c.Id == vm.CustomerId)) vm.CustomerId = null;
        if (vm.OpportunityId.HasValue && !await Db.Opportunities.AnyAsync(o => o.Id == vm.OpportunityId)) vm.OpportunityId = null;
        if (!ModelState.IsValid)
        {
            await LoadFormAsync(vm);
            return View("Form", vm);
        }

        var me = CurrentUserId;
        var url = "";
        if (a == null)
        {
            a = new Activity { AssignedById = me, CreatedAt = DateTime.Now, Status = ActivityStatus.Planned };
            Db.Activities.Add(a);
            Fill(a, vm);
            a.OwnerId = vm.OwnerId;
            var order = 0;
            foreach (var line in (vm.ChecklistText ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(50))
                a.Checklist.Add(new TaskChecklistItem { Title = line.Length > 300 ? line[..300] : line, SortOrder = order += 10 });
            Log(a, a.OwnerId == me ? I18n.T("Tạo công việc.") : I18n.T("Giao việc cho {0}, hạn {1}.", ownerName!, a.DueDate.ToDateTime()));
            await Db.SaveChangesAsync();
            url = Url.Action(nameof(Details), new { id = a.Id })!;
            _notify.Add(a.OwnerId, "task", I18n.T("{0} giao cho bạn: {1}", User.GetFullName(), a.Subject), url, me, I18n.T("Hạn {0}", a.DueDate.ToDateTime()));
        }
        else
        {
            var oldOwner = a.OwnerId; var oldDue = a.DueDate;
            Fill(a, vm);
            if (!a.IsTicketMain) a.OwnerId = vm.OwnerId;
            url = Url.Action(nameof(Details), new { id = a.Id })!;
            if (oldOwner != a.OwnerId)
            {
                Log(a, I18n.T("Chuyển việc cho {0}.", ownerName!));
                _notify.Add(a.OwnerId, "task", I18n.T("{0} giao cho bạn: {1}", User.GetFullName(), a.Subject), url, me, I18n.T("Hạn {0}", a.DueDate.ToDateTime()));
            }
            else if (oldDue != a.DueDate)
            {
                Log(a, I18n.T("Đổi thời hạn: {0} → {1}.", oldDue.ToDateTime(), a.DueDate.ToDateTime()));
                _notify.Add(a.OwnerId, "task", I18n.T("Đổi thời hạn công việc: {0}", a.Subject), url, me, I18n.T("Hạn mới {0}", a.DueDate.ToDateTime()));
            }
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu công việc.");
        return Redirect(url);
    }

    /// <summary>Đổi cột trên bảng: todo | doing | done | cancelled (dùng cho kéo thả và các nút)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Move(int id, string board, string? note)
    {
        var a = await Visible().FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return Fail(I18n.T("Không tìm thấy công việc."));
        if (!Columns.Contains(board)) return Fail("bad");
        if (a.IsTicketMain && board is "done" or "cancelled")
            return Fail(I18n.T("Việc chính của phiếu hỗ trợ tự hoàn thành khi phiếu được giải quyết."));
        if (a.Board == board) return Done(a);

        var me = CurrentUserId; var now = DateTime.Now;
        var url = Url.Action(nameof(Details), new { id = a.Id })!;
        var from = a.Board;
        switch (board)
        {
            case "todo": a.Status = ActivityStatus.Planned; a.StartedAt = null; a.CompletedAt = null; break;
            case "doing": a.Status = ActivityStatus.Planned; a.StartedAt ??= now; a.CompletedAt = null; break;
            case "done": a.Status = ActivityStatus.Completed; a.StartedAt ??= now; a.CompletedAt = now; a.Progress = 100; break;
            case "cancelled": a.Status = ActivityStatus.Cancelled; a.CompletedAt = null; break;
        }
        note = note?.Trim();
        if (board == "done" && !string.IsNullOrEmpty(note))
            a.Description = string.IsNullOrWhiteSpace(a.Description) ? note : a.Description + "\n" + note;
        Log(a, I18n.T("Chuyển trạng thái: {0} → {1}.", BoardName(from), BoardName(board)) + (string.IsNullOrEmpty(note) ? "" : " " + note));
        if (board == "done")
            _notify.Add(a.AssignedById, "task-done", I18n.T("{0} đã hoàn thành: {1}", User.GetFullName(), a.Subject), url, me, note);
        else if (board == "cancelled")
        {
            _notify.Add(a.OwnerId, "task", I18n.T("{0} đã hủy công việc: {1}", User.GetFullName(), a.Subject), url, me, note);
            if (a.AssignedById != a.OwnerId) _notify.Add(a.AssignedById, "task", I18n.T("{0} đã hủy công việc: {1}", User.GetFullName(), a.Subject), url, me, note);
        }
        else if (from is "done" or "cancelled")
            _notify.Add(a.OwnerId, "task", I18n.T("{0} đã mở lại công việc: {1}", User.GetFullName(), a.Subject), url, me);
        await Db.SaveChangesAsync();
        return Done(a);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetProgress(int id, int progress)
    {
        var a = await Visible().FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        a.Progress = Math.Clamp(progress, 0, 100);
        if (a.Progress > 0 && a.Status == ActivityStatus.Planned) a.StartedAt ??= DateTime.Now;
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---------- Đầu việc ----------

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddItem(int id, string? title)
    {
        var a = await Visible().Include(x => x.Checklist).FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        title = title?.Trim();
        if (!string.IsNullOrEmpty(title))
        {
            a.Checklist.Add(new TaskChecklistItem { Title = title.Length > 300 ? title[..300] : title, SortOrder = (a.Checklist.Max(c => (int?)c.SortOrder) ?? 0) + 10 });
            Recalc(a);
            await Db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Details), null, new { id }, "checklist");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleItem(int id, int itemId)
    {
        var a = await Visible().Include(x => x.Checklist).FirstOrDefaultAsync(x => x.Id == id);
        var item = a?.Checklist.FirstOrDefault(c => c.Id == itemId);
        if (a == null || item == null) return NotFound();
        item.IsDone = !item.IsDone;
        item.DoneAt = item.IsDone ? DateTime.Now : null;
        Recalc(a);
        if (a.Status == ActivityStatus.Planned) a.StartedAt ??= DateTime.Now;
        await Db.SaveChangesAsync();
        return Request.Headers.Accept.ToString().Contains("application/json")
            ? Json(new { ok = true, progress = a.Progress, done = item.IsDone })
            : RedirectToAction(nameof(Details), null, new { id }, "checklist");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteItem(int id, int itemId)
    {
        var a = await Visible().Include(x => x.Checklist).FirstOrDefaultAsync(x => x.Id == id);
        var item = a?.Checklist.FirstOrDefault(c => c.Id == itemId);
        if (a == null || item == null) return NotFound();
        Db.TaskChecklistItems.Remove(item);
        a.Checklist.Remove(item);
        Recalc(a);
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), null, new { id }, "checklist");
    }

    // ---------- Bình luận ----------

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Comment(int id, string? body)
    {
        var a = await Visible().FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        body = body?.Trim();
        if (string.IsNullOrEmpty(body)) return RedirectToAction(nameof(Details), new { id });
        if (body.Length > 2000) body = body[..2000];
        var me = CurrentUserId;
        a.Comments.Add(new TaskComment { Body = body, AuthorId = me, CreatedAt = DateTime.Now });
        var url = Url.Action(nameof(Details), null, new { id }, null, null, "comments")!;
        var title = I18n.T("{0} bình luận: {1}", User.GetFullName(), a.Subject);
        _notify.Add(a.OwnerId, "task-comment", title, url, me, body);
        if (a.AssignedById != a.OwnerId) _notify.Add(a.AssignedById, "task-comment", title, url, me, body);
        await Db.SaveChangesAsync();
        return Redirect(url);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var a = await Visible().Include(x => x.Checklist).Include(x => x.Comments).FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        if (!CanManage(a) || a.IsTicketMain) return NotAllowed();
        Db.TaskChecklistItems.RemoveRange(a.Checklist);
        Db.TaskComments.RemoveRange(a.Comments);
        Db.Activities.Remove(a);
        await Db.SaveChangesAsync();
        await DeleteAttachmentsAsync(AttachmentTarget.Activity, id);
        TempData["Success"] = I18n.T("Đã xóa công việc.");
        return RedirectToAction(nameof(Index));
    }

    // ---------- Hàm phụ ----------

    public static string BoardName(string b) => b switch
    {
        "todo" => I18n.T("Cần làm"), "doing" => I18n.T("Đang làm"), "done" => I18n.T("Hoàn thành"), _ => I18n.T("Đã hủy")
    };

    private static void Fill(Activity a, TaskFormViewModel vm)
    {
        a.Subject = vm.Subject; a.Description = string.IsNullOrWhiteSpace(vm.Description) ? null : vm.Description.Trim();
        a.Type = Enum.IsDefined(vm.Type) ? vm.Type : ActivityType.Task;
        a.Priority = Enum.IsDefined(vm.Priority) ? vm.Priority : Priority.Normal;
        if (!a.IsTicketMain) a.DueDate = vm.DueDate;
        if (a.TicketId == null) { a.CustomerId = vm.CustomerId; a.OpportunityId = vm.OpportunityId; }
    }

    /// <summary>Tiến độ theo đầu việc (khi có đầu việc)</summary>
    private static void Recalc(Activity a)
    {
        if (a.Checklist.Count > 0 && a.Status != ActivityStatus.Completed)
            a.Progress = (int)Math.Round(100.0 * a.Checklist.Count(c => c.IsDone) / a.Checklist.Count);
    }

    private void Log(Activity a, string text) =>
        a.Comments.Add(new TaskComment { Body = text.Length > 2000 ? text[..2000] : text, IsSystem = true, AuthorId = CurrentUserId, CreatedAt = DateTime.Now });

    private bool WantsJson => Request.Headers.Accept.ToString().Contains("application/json");
    private IActionResult Fail(string message)
    {
        if (WantsJson) return BadRequest(new { ok = false, message });
        TempData["Error"] = message;
        return RedirectToAction(nameof(Index));
    }
    private IActionResult Done(Activity a) => WantsJson
        ? Json(new { ok = true, board = a.Board, progress = a.Progress })
        : RedirectToAction(nameof(Details), new { id = a.Id });

    private async Task LoadFormAsync(TaskFormViewModel vm)
    {
        // Giao cho người trong phạm vi đơn vị đang xem
        ViewBag.Owners = new SelectList(await Db.UsersInScope.Where(u => u.IsActive).OrderBy(u => u.FullName)
            .Select(u => new { u.Id, u.FullName }).ToListAsync(), "Id", "FullName", vm.OwnerId);
        await LoadCustomersAsync(vm.CustomerId);
        await LoadOpportunitiesAsync(vm.CustomerId, vm.OpportunityId);
    }
}
