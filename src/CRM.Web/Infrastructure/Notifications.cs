using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>Tạo / đọc thông báo trong ứng dụng. Add() chỉ thêm vào DbContext – lưu cùng thao tác nghiệp vụ của người gọi.</summary>
public class NotificationService
{
    private readonly CrmDbContext _db;
    public NotificationService(CrmDbContext db) => _db = db;

    /// <summary>Thêm thông báo cho một người (bỏ qua khi người nhận chính là người thực hiện)</summary>
    public void Add(int? userId, string type, string title, string? url, int? actorId = null, string? body = null, string? key = null)
    {
        if (userId is not > 0 || userId == actorId) return;
        _db.Notifications.Add(new Notification
        {
            UserId = userId.Value, Type = type, Title = Cut(title, 300)!, Url = Cut(url, 300), ActorId = actorId,
            Body = Cut(body, 500), Key = Cut(key, 150), CreatedAt = DateTime.Now
        });
    }

    public Task<int> UnreadCountAsync(int userId) => _db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);

    public Task<List<Notification>> LatestAsync(int userId, int take = 8) =>
        _db.Notifications.AsNoTracking().Where(n => n.UserId == userId).OrderByDescending(n => n.Id).Take(take).ToListAsync();

    private static string? Cut(string? s, int max) => s == null ? null : s.Length > max ? s[..(max - 1)] + "…" : s;

    /// <summary>Biểu tượng theo loại thông báo</summary>
    public static string IconOf(string type) => type switch
    {
        "task" => "task",
        "task-done" => "check",
        "task-comment" => "email",
        "due" => "clock",
        "overdue" => "alert",
        "ticket" or "sla" => "support",
        "plan" => "target",
        "security" => "key",
        "share" => "users",
        "automation" => "kanban",
        "lead" => "lead",
        "renewal" => "contract",
        "approval" => "check",
        "nps" => "star",
        _ => "bell"
    };
}

/// <summary>
/// Tác vụ nền: mỗi 10 phút nhắc việc sắp đến hạn (trong 24 giờ) / vừa quá hạn (trong 3 ngày gần nhất)
/// và phiếu hỗ trợ sắp trễ / đã trễ SLA cho người phụ trách. Mỗi mốc hạn chỉ nhắc một lần (khóa chống trùng).
/// </summary>
public class NotificationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<NotificationWorker> _log;
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    public NotificationWorker(IServiceScopeFactory scopes, ILogger<NotificationWorker> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), ct);   // chờ khởi tạo DB xong
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var n = await RunOnceAsync(scope.ServiceProvider.GetRequiredService<CrmDbContext>(), ct);
                if (n > 0) _log.LogInformation("Đã tạo {Count} thông báo nhắc hạn", n);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Lỗi khi tạo thông báo nhắc hạn"); }
            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Quét một lần; trả về số thông báo đã tạo</summary>
    public static async Task<int> RunOnceAsync(CrmDbContext db, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var candidates = new List<(int UserId, string Type, string Title, string Url, string Key)>();

        // Công việc / hoạt động đang chờ của người phụ trách
        var acts = await db.Activities.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Status == ActivityStatus.Planned && a.OwnerId != null && a.DueDate >= now.AddDays(-3) && a.DueDate <= now.AddHours(24))
            .Select(a => new { a.Id, a.OwnerId, a.Subject, a.DueDate }).ToListAsync(ct);
        foreach (var a in acts)
        {
            var overdue = a.DueDate < now;
            candidates.Add((a.OwnerId!.Value, overdue ? "overdue" : "due",
                overdue ? $"Quá hạn: {a.Subject}" : $"Sắp đến hạn ({a.DueDate:dd/MM HH:mm}): {a.Subject}",
                $"/Tasks/Details/{a.Id}", $"{(overdue ? "overdue" : "due")}:{a.Id}:{a.DueDate:yyyyMMddHHmm}"));
        }

        // Phiếu hỗ trợ đang mở: sắp trễ (trong 2 giờ) / đã trễ SLA (trong 1 ngày gần nhất)
        var tickets = await db.Tickets.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.OwnerId != null && (t.Status == TicketStatus.New || t.Status == TicketStatus.InProgress || t.Status == TicketStatus.WaitingCustomer))
            .Select(t => new { t.Id, t.OwnerId, t.Number, t.Subject, Due = t.FirstResponseAt == null ? t.ResponseDueAt : t.ResolveDueAt, Response = t.FirstResponseAt == null })
            .Where(t => t.Due >= now.AddDays(-1) && t.Due <= now.AddHours(2)).ToListAsync(ct);
        foreach (var t in tickets)
        {
            var late = t.Due < now;
            var what = t.Response ? "phản hồi" : "giải quyết";
            candidates.Add((t.OwnerId!.Value, "sla",
                late ? $"Phiếu {t.Number} đã quá hạn {what} SLA: {t.Subject}" : $"Phiếu {t.Number} sắp đến hạn {what} ({t.Due:HH:mm}): {t.Subject}",
                $"/Tickets/Details/{t.Id}", $"sla{(late ? "-late" : "")}:{t.Id}:{t.Due:yyyyMMddHHmm}"));
        }
        if (candidates.Count == 0) return 0;

        var keys = candidates.Select(c => c.Key).ToList();
        var existing = (await db.Notifications.Where(n => n.Key != null && keys.Contains(n.Key)).Select(n => new { n.UserId, n.Key }).ToListAsync(ct))
            .Select(x => (x.UserId, x.Key!)).ToHashSet();
        var created = 0;
        foreach (var c in candidates.Where(c => !existing.Contains((c.UserId, c.Key))))
        {
            db.Notifications.Add(new Notification { UserId = c.UserId, Type = c.Type, Title = c.Title.Length > 300 ? c.Title[..299] + "…" : c.Title, Url = c.Url, Key = c.Key, CreatedAt = now });
            created++;
        }
        if (created > 0) await db.SaveChangesAsync(ct);
        return created;
    }
}
