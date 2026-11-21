using System.Globalization;
using System.Text.Json;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CRM.Web.Infrastructure;

/// <summary>Lịch làm việc dùng tính hạn SLA (AppSettings "sla.calendar")</summary>
public class SlaCalendarSettings
{
    public const string Key = "sla.calendar";
    /// <summary>Ngày làm việc (DayOfWeek: 0 = Chủ nhật … 6 = Thứ bảy)</summary>
    public int[] WorkDays { get; set; } = { 1, 2, 3, 4, 5 };
    public string Start { get; set; } = "08:00";
    public string End { get; set; } = "17:30";
    /// <summary>Nghỉ trưa (để trống = không nghỉ)</summary>
    public string? BreakStart { get; set; } = "12:00";
    public string? BreakEnd { get; set; } = "13:30";
    /// <summary>Ngày lễ, mỗi dòng một ngày: "dd/MM" (lặp hằng năm) hoặc "dd/MM/yyyy"</summary>
    public string? Holidays { get; set; } = "01/01\n30/04\n01/05\n02/09";
}

/// <summary>Tính thời gian theo giờ làm việc: cộng phút làm việc, đếm phút làm việc giữa hai thời điểm</summary>
public class BusinessCalendar
{
    private readonly HashSet<DayOfWeek> _days;
    private readonly List<(TimeSpan From, TimeSpan To)> _windows = new();
    private readonly HashSet<(int D, int M)> _yearly = new();
    private readonly HashSet<DateTime> _dates = new();

    public BusinessCalendar(SlaCalendarSettings s)
    {
        _days = (s.WorkDays ?? Array.Empty<int>()).Where(d => d is >= 0 and <= 6).Select(d => (DayOfWeek)d).ToHashSet();
        var start = Time(s.Start) ?? TimeSpan.FromHours(8);
        var end = Time(s.End) ?? TimeSpan.FromHours(17.5);
        if (end <= start) end = start + TimeSpan.FromHours(8);
        var bs = Time(s.BreakStart); var be = Time(s.BreakEnd);
        if (bs.HasValue && be.HasValue && bs > start && be < end && be > bs) { _windows.Add((start, bs.Value)); _windows.Add((be.Value, end)); }
        else _windows.Add((start, end));
        foreach (var line in (s.Holidays ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DateTime.TryParseExact(line, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) _dates.Add(d.Date);
            else if (DateTime.TryParseExact(line + "/2000", "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var y)) _yearly.Add((y.Day, y.Month));
        }
        if (_days.Count == 0) _days = new() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
    }

    public static TimeSpan? Time(string? s) =>
        TimeSpan.TryParseExact(s?.Trim(), new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t : null;

    public double MinutesPerDay => _windows.Sum(w => (w.To - w.From).TotalMinutes);

    public bool IsWorkDay(DateTime d) => _days.Contains(d.DayOfWeek) && !_dates.Contains(d.Date) && !_yearly.Contains((d.Day, d.Month));

    /// <summary>Cộng số phút làm việc kể từ thời điểm from</summary>
    public DateTime AddMinutes(DateTime from, double minutes)
    {
        if (minutes <= 0) return from;
        var day = from.Date;
        for (var guard = 0; guard < 3660; guard++, day = day.AddDays(1))
        {
            if (!IsWorkDay(day)) continue;
            foreach (var (f, t) in _windows)
            {
                var ws = day + f; var we = day + t;
                var s = from > ws ? from : ws;
                if (s >= we) continue;
                var avail = (we - s).TotalMinutes;
                if (minutes <= avail) return s.AddMinutes(minutes);
                minutes -= avail;
            }
        }
        return from.AddMinutes(minutes);   // lịch không có ngày làm việc nào trong 10 năm – tính liên tục
    }

    /// <summary>Số phút làm việc trong khoảng [a, b)</summary>
    public double MinutesBetween(DateTime a, DateTime b)
    {
        if (b <= a) return 0;
        double total = 0;
        for (var day = a.Date; day <= b.Date; day = day.AddDays(1))
        {
            if (!IsWorkDay(day)) continue;
            foreach (var (f, t) in _windows)
            {
                var s = day + f > a ? day + f : a;
                var e = day + t < b ? day + t : b;
                if (e > s) total += (e - s).TotalMinutes;
            }
        }
        return total;
    }
}

/// <summary>
/// Áp SLA cho phiếu hỗ trợ khi lưu (gọi trong CrmDbContext.SaveChanges):
///  • Phiếu mới / đổi độ ưu tiên, hợp đồng, khách hàng → chọn chính sách (hợp đồng → khách hàng → mặc định) và tính hạn.
///  • Chuyển sang "Chờ khách phản hồi" → dừng đồng hồ; rời trạng thái đó → cộng thời gian đã dừng vào hạn.
/// Chính sách + lịch làm việc được nhớ tạm (làm mới khi sửa cấu hình hoặc sau 5 phút).
/// </summary>
public static class SlaEngine
{
    public record Snapshot(Dictionary<int, SlaPolicy> Policies, int? DefaultId, BusinessCalendar Calendar, DateTime LoadedAt);
    private static Snapshot? _snap;
    public static void Invalidate() => _snap = null;

    public static async Task<Snapshot> LoadAsync(CrmDbContext db, CancellationToken ct = default)
    {
        var s = _snap;
        if (s != null && s.LoadedAt > DateTime.Now.AddMinutes(-5)) return s;
        var policies = await db.SlaPolicies.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
        var json = await db.AppSettings.AsNoTracking().Where(x => x.Key == SlaCalendarSettings.Key).Select(x => x.Value).FirstOrDefaultAsync(ct);
        SlaCalendarSettings cal;
        try { cal = string.IsNullOrEmpty(json) ? new() : JsonSerializer.Deserialize<SlaCalendarSettings>(json) ?? new(); } catch { cal = new(); }
        var def = policies.Values.Where(p => p.IsDefault && p.IsActive).Select(p => (int?)p.Id).FirstOrDefault();
        return _snap = new Snapshot(policies, def, new BusinessCalendar(cal), DateTime.Now);
    }

    /// <summary>Cộng phút theo chính sách (giờ làm việc hoặc 24/7)</summary>
    public static DateTime Add(Snapshot s, SlaPolicy? p, DateTime from, double minutes) =>
        p?.BusinessHours == true ? s.Calendar.AddMinutes(from, minutes) : from.AddMinutes(minutes);

    public static double Between(Snapshot s, SlaPolicy? p, DateTime a, DateTime b) =>
        p?.BusinessHours == true ? s.Calendar.MinutesBetween(a, b) : Math.Max(0, (b - a).TotalMinutes);

    /// <summary>Tính lại hạn phản hồi / giải quyết từ lúc tiếp nhận (+ thời gian đã dừng)</summary>
    public static void Compute(Snapshot s, Ticket t, SlaPolicy? p)
    {
        t.SlaPolicyId = p?.Id;
        double resp, res;
        if (p != null) { var (a, b) = p.For(t.Priority); resp = (double)a * 60; res = (double)b * 60; }
        else { var (a, b) = TicketSla.For(t.Priority); resp = a * 60; res = b * 60; }
        t.ResponseDueAt = Add(s, p, t.CreatedAt, resp + (t.FirstResponseAt == null ? t.SlaPausedMinutes : 0));
        t.ResolveDueAt = Add(s, p, t.CreatedAt, res + t.SlaPausedMinutes);
    }

    private static async Task<SlaPolicy?> ResolveAsync(CrmDbContext db, Snapshot s, Ticket t, CancellationToken ct)
    {
        int? id = null;
        if (t.ContractId.HasValue)
            id = await db.Contracts.IgnoreQueryFilters().Where(c => c.Id == t.ContractId).Select(c => c.SlaPolicyId).FirstOrDefaultAsync(ct);
        if (id == null && t.CustomerId.HasValue)
            id = await db.Customers.IgnoreQueryFilters().Where(c => c.Id == t.CustomerId).Select(c => c.SlaPolicyId).FirstOrDefaultAsync(ct);
        if (id.HasValue && s.Policies.TryGetValue(id.Value, out var p) && p.IsActive) return p;
        return s.DefaultId.HasValue ? s.Policies[s.DefaultId.Value] : null;
    }

    public static async Task ApplyAsync(CrmDbContext db, CancellationToken ct = default)
    {
        var entries = db.ChangeTracker.Entries<Ticket>().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList();
        if (entries.Count == 0) return;
        var s = await LoadAsync(db, ct);
        var now = DateTime.Now;
        foreach (var e in entries)
        {
            var t = e.Entity;
            if (e.State == EntityState.Added)
            {
                // Dữ liệu mẫu / nhập lịch sử tự đặt hạn → giữ nguyên
                if (db.SuppressAudit && t.ResponseDueAt != default) continue;
                Compute(s, t, await ResolveAsync(db, s, t, ct));
                if (t.Status == TicketStatus.WaitingCustomer && PolicyOf(s, t)?.PauseWhenWaiting == true) t.SlaPausedAt = now;
                continue;
            }
            var policy = PolicyOf(s, t);
            if (StatusChanged(e, out var from) )
            {
                if (t.Status == TicketStatus.WaitingCustomer && from != TicketStatus.WaitingCustomer && t.SlaPausedAt == null && policy?.PauseWhenWaiting == true)
                    t.SlaPausedAt = now;
                else if (from == TicketStatus.WaitingCustomer && t.Status != TicketStatus.WaitingCustomer && t.SlaPausedAt.HasValue)
                    Resume(s, t, policy, now);
            }
            if (t.IsOpen && (Changed(e, nameof(Ticket.Priority)) || Changed(e, nameof(Ticket.ContractId)) || Changed(e, nameof(Ticket.CustomerId))))
                Compute(s, t, await ResolveAsync(db, s, t, ct));
        }
    }

    /// <summary>Tiếp tục đồng hồ: cộng thời gian đã dừng vào các hạn chưa đạt</summary>
    public static void Resume(Snapshot s, Ticket t, SlaPolicy? p, DateTime now)
    {
        var paused = Between(s, p, t.SlaPausedAt!.Value, now);
        t.SlaPausedAt = null;
        if (paused <= 0) return;
        t.SlaPausedMinutes += (int)Math.Round(paused);
        t.ResolveDueAt = Add(s, p, t.ResolveDueAt, paused);
        if (t.FirstResponseAt == null) t.ResponseDueAt = Add(s, p, t.ResponseDueAt, paused);
    }

    private static SlaPolicy? PolicyOf(Snapshot s, Ticket t) => t.SlaPolicyId.HasValue ? s.Policies.GetValueOrDefault(t.SlaPolicyId.Value) : null;

    private static bool Changed(EntityEntry<Ticket> e, string prop)
    {
        var p = e.Property(prop);
        return p.IsModified && !Equals(p.OriginalValue, p.CurrentValue);
    }

    private static bool StatusChanged(EntityEntry<Ticket> e, out TicketStatus from)
    {
        var p = e.Property(x => x.Status);
        from = p.OriginalValue;
        return p.IsModified && p.OriginalValue != p.CurrentValue;
    }
}
