using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>
/// Quản lý công việc của phiếu hỗ trợ (dùng chung bảng Hoạt động → hiện trên Lịch làm việc của người được giao):
/// - Việc chính: tự tạo khi phiếu có người xử lý (hạn = hạn giải quyết SLA), chuyển theo người xử lý,
///   hoàn thành khi phiếu được giải quyết, hủy khi bỏ phân công, lên lịch lại khi mở lại phiếu.
/// - Việc con: giao thêm cho bất kỳ ai (gọi lại khách, đi hiện trường, chuyển kỹ thuật...).
/// </summary>
public static class TicketWork
{
    public static Priority PriorityOf(TicketPriority p) => p switch
    {
        TicketPriority.Urgent or TicketPriority.High => Priority.High,
        TicketPriority.Low => Priority.Low,
        _ => Priority.Normal
    };

    public static string MainSubject(Ticket t)
    {
        var s = $"{I18n.T("Xử lý phiếu")} {t.Number}: {t.Subject}";
        return s.Length > 200 ? s[..200] : s;
    }

    /// <summary>
    /// Đồng bộ việc chính với người xử lý / trạng thái / độ ưu tiên của phiếu (chưa SaveChanges).
    /// <paramref name="resetDue"/>: đặt lại hạn (đổi độ ưu tiên, mở lại phiếu).
    /// </summary>
    public static async Task SyncAsync(CrmDbContext db, Ticket t, bool resetDue = false)
    {
        var main = db.Activities.Local.FirstOrDefault(a => a.IsTicketMain && (a.Ticket == t || (t.Id != 0 && a.TicketId == t.Id)));
        if (main == null && t.Id != 0)
            main = await db.Activities.FirstOrDefaultAsync(a => a.TicketId == t.Id && a.IsTicketMain);
        var now = DateTime.Now;

        if (t.IsOpen && t.OwnerId.HasValue)
        {
            if (main == null)
            {
                main = new Activity { Type = ActivityType.Task, IsTicketMain = true, Ticket = t, CreatedAt = now };
                db.Activities.Add(main);
                resetDue = true;
            }
            if (main.Status != ActivityStatus.Planned)
            {
                main.Status = ActivityStatus.Planned;
                main.CompletedAt = null;
                resetDue = true;
            }
            if (resetDue)
                main.DueDate = t.ResolveDueAt > now ? t.ResolveDueAt : now.AddHours(TicketSla.For(t.Priority).ResolveHours);
            main.OwnerId = t.OwnerId;
            main.Subject = MainSubject(t);
            main.Priority = PriorityOf(t.Priority);
            main.CustomerId = t.CustomerId;
            main.ContactId = t.ContactId;
        }
        else if (main is { Status: ActivityStatus.Planned })
        {
            if (t.IsOpen)
                main.Status = ActivityStatus.Cancelled;   // bỏ phân công
            else
            {
                main.Status = ActivityStatus.Completed;
                main.CompletedAt = t.ResolvedAt ?? now;
                main.Description = t.Resolution;
            }
        }
    }

    /// <summary>Tạo việc chính cho các phiếu đã có người xử lý nhưng chưa có việc (dữ liệu trước khi có tính năng). Chạy lại an toàn.</summary>
    public static async Task<int> BackfillAsync(CrmDbContext db)
    {
        var tickets = await db.Tickets.Where(t => t.OwnerId != null && !db.Activities.Any(a => a.TicketId == t.Id && a.IsTicketMain)).ToListAsync();
        foreach (var t in tickets)
        {
            var done = !t.IsOpen;
            db.Activities.Add(new Activity
            {
                Type = ActivityType.Task, IsTicketMain = true, TicketId = t.Id, Subject = MainSubject(t),
                OwnerId = t.OwnerId, CustomerId = t.CustomerId, ContactId = t.ContactId, Priority = PriorityOf(t.Priority),
                DueDate = t.ResolveDueAt, CreatedAt = t.CreatedAt,
                Status = done ? ActivityStatus.Completed : ActivityStatus.Planned,
                CompletedAt = done ? t.ResolvedAt ?? t.ClosedAt ?? t.UpdatedAt : null,
                Description = done ? t.Resolution : null
            });
        }
        if (tickets.Count > 0) await db.SaveChangesAsync();
        return tickets.Count;
    }
}
