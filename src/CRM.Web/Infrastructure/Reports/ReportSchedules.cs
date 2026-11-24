using System.Net;
using System.Security.Claims;
using System.Text;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using static CRM.Web.Infrastructure.I18n;

namespace CRM.Web.Infrastructure.Reports;

public record ScheduleRunResult(int Sent, int Skipped, string Message);

/// <summary>
/// Gửi báo cáo định kỳ: với từng người nhận, chạy báo cáo DƯỚI QUYỀN CỦA CHÍNH HỌ (quyền xem / xuất / vùng dữ liệu theo phân quyền báo cáo,
/// giới hạn đơn vị) rồi đưa email kèm file Excel vào hàng đợi gửi. Người không còn quyền xem báo cáo được bỏ qua (ghi trong trạng thái).
/// </summary>
public class ReportScheduleService
{
    private readonly CrmDbContext _db;
    private readonly IServiceScopeFactory _scopes;
    private readonly EmailService _mail;
    private readonly AttachmentService _files;
    private readonly SettingsStore _settings;
    public ReportScheduleService(CrmDbContext db, IServiceScopeFactory scopes, EmailService mail, AttachmentService files, SettingsStore settings)
    { _db = db; _scopes = scopes; _mail = mail; _files = files; _settings = settings; }

    // ======================= Lịch & kỳ =======================

    /// <summary>Lần chạy kế tiếp sau thời điểm from (giờ địa phương)</summary>
    public static DateTime NextRun(ReportSchedule s, DateTime from)
    {
        var day = from.Date;
        DateTime At(DateTime d) => d.AddHours(s.Hour);
        switch (s.Frequency)
        {
            case ScheduleFrequency.Daily:
                return At(day) > from ? At(day) : At(day.AddDays(1));
            case ScheduleFrequency.Weekly:
                for (var i = 0; i < 8; i++)
                {
                    var d = day.AddDays(i);
                    if ((int)d.DayOfWeek == s.DayOfWeek && At(d) > from) return At(d);
                }
                return At(day.AddDays(7));
            default:
                for (var m = 0; m < 3; m++)
                {
                    var first = new DateTime(day.Year, day.Month, 1).AddMonths(m);
                    var d = first.AddDays(Math.Min(s.DayOfMonth, DateTime.DaysInMonth(first.Year, first.Month)) - 1);
                    if (At(d) > from) return At(d);
                }
                return At(day.AddMonths(1));
        }
    }

    /// <summary>Khoảng ngày [from, to] theo kỳ đã chọn, tính từ ngày chạy</summary>
    public static (DateTime From, DateTime To) PeriodRange(SchedulePeriod p, DateTime today)
    {
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var quarterStart = new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
        return p switch
        {
            SchedulePeriod.Yesterday => (today.AddDays(-1), today.AddDays(-1)),
            SchedulePeriod.Last7Days => (today.AddDays(-7), today.AddDays(-1)),
            SchedulePeriod.Last30Days => (today.AddDays(-30), today.AddDays(-1)),
            SchedulePeriod.ThisMonth => (monthStart, today),
            SchedulePeriod.LastMonth => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            SchedulePeriod.ThisQuarter => (quarterStart, today),
            SchedulePeriod.LastQuarter => (quarterStart.AddMonths(-3), quarterStart.AddDays(-1)),
            SchedulePeriod.ThisYear => (new DateTime(today.Year, 1, 1), today),
            _ => (new DateTime(today.Year - 1, 1, 1), new DateTime(today.Year - 1, 12, 31))
        };
    }

    public static string Describe(ReportSchedule s)
    {
        var at = $"{s.Hour:00}:00";
        return s.Frequency switch
        {
            ScheduleFrequency.Daily => T("Hằng ngày lúc {0}", at),
            ScheduleFrequency.Weekly => T("Hằng tuần – {0} lúc {1}", DayName(s.DayOfWeek), at),
            _ => T("Hằng tháng – ngày {0} lúc {1}", s.DayOfMonth, at)
        };
    }

    public static string DayName(int d) => d switch
    {
        1 => T("Thứ 2"), 2 => T("Thứ 3"), 3 => T("Thứ 4"), 4 => T("Thứ 5"), 5 => T("Thứ 6"), 6 => T("Thứ 7"), _ => T("Chủ nhật")
    };

    public static int[] ParseIds(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => int.TryParse(x, out var i) ? i : 0).Where(i => i > 0).Distinct().ToArray();

    // ======================= Chạy lịch =======================

    /// <summary>Chạy một lịch ngay: tạo email cho từng người nhận và cập nhật trạng thái / lần chạy kế tiếp</summary>
    public async Task<ScheduleRunResult> RunAsync(int scheduleId, CancellationToken ct = default)
    {
        var s = await _db.ReportSchedules.FirstOrDefaultAsync(x => x.Id == scheduleId, ct);
        if (s == null) return new(0, 0, T("Không tìm thấy lịch gửi."));
        var today = DateTime.Today;
        var (from, to) = PeriodRange(s.Period, today);
        var ids = ParseIds(s.RecipientIds);
        var users = await _db.Users.IgnoreQueryFilters().Where(u => ids.Contains(u.Id) && u.IsActive).ToListAsync(ct);
        var smtp = await _mail.SettingsAsync();
        int sent = 0, skipped = 0; var notes = new List<string>();

        foreach (var u in users)
        {
            if (string.IsNullOrWhiteSpace(u.Email)) { skipped++; notes.Add($"{u.FullName}: {T("chưa có email")}"); continue; }
            var run = await RunAsRecipientAsync(s, u, from, to, ct);
            if (run.Result == null) { skipped++; notes.Add($"{u.FullName}: {run.Error}"); continue; }
            var attachments = new List<int>();
            if (s.IncludeExcel)
            {
                var bytes = XlsxWriter.Write(run.Title, run.Subtitle, run.Result);
                var name = new string(run.Title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
                var att = await _files.SaveBytesAsync(AttachmentTarget.ReportSchedule, s.Id, $"{name} {to:yyyyMMdd}.xlsx", bytes, T("Gửi cho {0}", u.FullName), s.CreatedById ?? u.Id);
                attachments.Add(att.Id);
            }
            var subject = $"[CRM] {run.Title} – {from.ToDate()}{(from == to ? "" : " → " + to.ToDate())}";
            var link = EmailService.Absolute(smtp, run.Link);
            _mail.Queue(u.Email!, subject, EmailService.Wrap(BuildBody(run, s, u, from, to, link), smtp), "Report", s.Id, null, s.CreatedById, null, attachments);
            sent++;
        }
        if (users.Count < ids.Length) notes.Add(T("{0} người nhận không còn hoạt động", ids.Length - users.Count));

        s.LastRunAt = DateTime.Now;
        s.NextRunAt = s.IsActive ? NextRun(s, DateTime.Now) : null;
        var msg = T("Đã đưa {0} email vào hàng đợi", sent) + (skipped > 0 ? $"; {T("bỏ qua {0}", skipped)}" : "") + (notes.Count > 0 ? $" ({string.Join("; ", notes.Take(3))}{(notes.Count > 3 ? "…" : "")})" : "");
        s.LastStatus = msg.Length > 500 ? msg[..499] + "…" : msg;
        await _db.SaveChangesAsync(ct);
        return new(sent, skipped, msg);
    }

    private record RunOutput(ReportResult? Result, string? Error, string Title, string Subtitle, string ScopeText, string Link);

    /// <summary>Chạy báo cáo trong một phạm vi DI riêng, đóng vai người nhận (quyền, vùng dữ liệu, phạm vi đơn vị của họ)</summary>
    private async Task<RunOutput> RunAsRecipientAsync(ReportSchedule s, AppUser user, DateTime from, DateTime to, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.Role, user.Role),
            new Claim("FullName", user.FullName)
        }, "ReportSchedule"));
        var accessor = sp.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext { User = principal, RequestServices = sp };
        try
        {
            var db = sp.GetRequiredService<CrmDbContext>();
            var branch = sp.GetRequiredService<BranchScope>();
            var access = sp.GetRequiredService<ReportAccessService>();
            var subject = ReportAccessService.SubjectOf(principal, branch);
            var filter = new ReportFilter { From = from, To = to, Year = to.Year };
            string title, link; ReportResult? result; string? error = null;

            if (s.ReportKey.StartsWith("std:"))
            {
                var def = StandardReports.Find(s.ReportKey[4..]);
                if (def == null) return new(null, T("Báo cáo không còn tồn tại."), "", "", "", "");
                var g = await access.ResolveAsync(s.ReportKey, subject);
                if (!g.CanView) return new(null, g.DenyReason ?? T("Không có quyền xem báo cáo."), def.Title, "", "", "");
                var scopeText = access.Apply(g, principal);
                if (def.Filters.HasFlag(ReportFilters.Top)) filter.Top = 20;
                if (def.Filters.HasFlag(ReportFilters.Days)) filter.Days = 90;
                if (g.OwnOnly == true) filter.OwnerId = null;
                result = await def.Run(new ReportContext { Db = db, User = principal, Filter = filter, OwnOnly = g.OwnOnly, Scope = branch });
                title = def.Title; link = $"/Reports/Run/{def.Key}";
                return new(result, null, title, Subtitle(from, to, scopeText), scopeText, link);
            }
            if (s.ReportKey.StartsWith("custom:") && int.TryParse(s.ReportKey[7..], out var cid))
            {
                var rep = await db.CustomReports.FirstOrDefaultAsync(r => r.Id == cid, ct);
                if (rep == null) return new(null, T("Báo cáo không còn tồn tại."), "", "", "", "");
                var g = await access.ResolveCustomAsync(rep, subject);
                if (!g.CanView) return new(null, g.DenyReason ?? T("Không có quyền xem báo cáo."), rep.Name, "", "", "");
                var scopeText = access.Apply(g, principal);
                var custom = sp.GetRequiredService<CustomReportService>();
                string? sql; Dictionary<string, object?>? extra = null; string? cl = rep.ChartLabelColumn, cv = rep.ChartValueColumn;
                if (rep.SourceType == CustomReportSource.Builder)
                {
                    var (c, err) = await custom.CompileBuilderAsync(rep.BuilderJson, null);
                    if (c == null) return new(null, err, rep.Name, "", "", "");
                    sql = c.Sql; extra = c.Params;
                }
                else { (sql, error) = await custom.SqlForAsync(rep); if (sql == null) return new(null, error, rep.Name, "", "", ""); }
                if (g.OwnOnly == true) filter.OwnerId = null;
                (result, error) = await custom.RunAsync(sql!, filter, principal, CustomReportService.ExportRows, cl, cv, g.OwnOnly, rep.ChartKind, extra);
                if (result == null) return new(null, error, rep.Name, "", "", "");
                return new(result, null, rep.Name, Subtitle(from, to, scopeText), scopeText, $"/Reports/Custom/{rep.Id}");
            }
            return new(null, T("Mã báo cáo không hợp lệ."), "", "", "", "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(null, T("Lỗi chạy báo cáo: {0}", ex.Message), "", "", "", "");
        }
        finally { accessor.HttpContext = null; }
    }

    private static string Subtitle(DateTime from, DateTime to, string scope) =>
        T("Kỳ: {0} – {1}", from.ToDate(), to.ToDate()) + " · " + T("Vùng dữ liệu: {0}", scope) + " · " + T("Xuất lúc {0}", DateTime.Now.ToString("dd/MM/yyyy HH:mm"));

    // ======================= Nội dung email =======================

    private static string BuildBody(RunOutput run, ReportSchedule s, AppUser user, DateTime from, DateTime to, string link)
    {
        var r = run.Result!;
        var enc = WebWork.Enc;
        var sb = new StringBuilder();
        sb.Append($"<p>{enc(T("Kính gửi {0},", user.FullName))}</p>");
        sb.Append($"<p>{enc(T("Báo cáo định kỳ \"{0}\" cho kỳ {1} – {2}.", run.Title, from.ToDate(), to.ToDate()))}</p>");
        sb.Append($"<p style=\"color:#6b7280;font-size:12px\">{enc(T("Vùng dữ liệu: {0}", run.ScopeText))}</p>");
        var cols = r.Columns.Take(8).ToList();
        var rows = r.Rows.Take(s.PreviewRows).ToList();
        if (s.PreviewRows > 0 && cols.Count > 0)
        {
            sb.Append("<table style=\"border-collapse:collapse;font-size:12px;width:100%\"><thead><tr>");
            foreach (var c in cols) sb.Append($"<th style=\"background:#1f4e79;color:#fff;padding:5px 8px;text-align:{(c.IsNumeric ? "right" : "left")}\">{enc(c.Title)}</th>");
            sb.Append("</tr></thead><tbody>");
            foreach (var row in rows)
            {
                sb.Append("<tr>");
                for (var i = 0; i < cols.Count; i++)
                    sb.Append($"<td style=\"border-bottom:1px solid #e5e7eb;padding:4px 8px;text-align:{(cols[i].IsNumeric ? "right" : "left")}\">{enc(ReportResult.Format(row[i], cols[i].Kind))}</td>");
                sb.Append("</tr>");
            }
            if (r.Totals != null && r.Rows.Count > 0)
            {
                sb.Append("<tr>");
                for (var i = 0; i < cols.Count; i++)
                    sb.Append($"<td style=\"font-weight:bold;padding:5px 8px;background:#f3f4f6;text-align:{(cols[i].IsNumeric ? "right" : "left")}\">{enc(ReportResult.Format(r.Totals[i], cols[i].Kind))}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table>");
            var more = r.Rows.Count - rows.Count;
            sb.Append($"<p style=\"color:#6b7280;font-size:12px\">{enc(T("{0} dòng", r.Rows.Count.ToString("N0")))}"
                + (more > 0 ? $" · {enc(T("hiển thị {0} dòng đầu", rows.Count))}" : "") + (r.Columns.Count > cols.Count ? $" · {enc(T("hiển thị {0}/{1} cột", cols.Count, r.Columns.Count))}" : "")
                + (s.IncludeExcel && (more > 0 || r.Columns.Count > cols.Count) ? $" · {enc(T("xem đầy đủ trong file Excel đính kèm"))}" : "") + "</p>");
        }
        else if (s.IncludeExcel) sb.Append($"<p>{enc(T("Số liệu đầy đủ trong file Excel đính kèm."))}</p>");
        if (!string.IsNullOrEmpty(link)) sb.Append($"<p><a href=\"{WebUtility.HtmlEncode(link)}\">{enc(T("Mở báo cáo trong CRM"))}</a></p>");
        sb.Append($"<p style=\"color:#9ca3af;font-size:11px\">{enc(T("Email tự động theo lịch \"{0}\" ({1}).", s.Name, Describe(s)))}</p>");
        return sb.ToString();
    }
}

/// <summary>Tiện ích HTML nhỏ dùng chung</summary>
internal static class WebWork { public static string Enc(string? s) => WebUtility.HtmlEncode(s ?? ""); }

/// <summary>Tác vụ nền: mỗi 5 phút chạy các lịch đến hạn; dọn file Excel của lần gửi cũ (&gt; 14 ngày)</summary>
public class ReportScheduleWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ReportScheduleWorker> _log;
    private DateTime _lastCleanup = DateTime.MinValue;
    public ReportScheduleWorker(IServiceScopeFactory scopes, ILogger<ReportScheduleWorker> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(50), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                List<int> due;
                using (var scope = _scopes.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
                    due = await db.ReportSchedules.IgnoreQueryFilters().Where(s => s.IsActive && s.NextRunAt != null && s.NextRunAt <= DateTime.Now).Select(s => s.Id).ToListAsync(ct);
                }
                foreach (var id in due)
                {
                    using var scope = _scopes.CreateScope();
                    var r = await scope.ServiceProvider.GetRequiredService<ReportScheduleService>().RunAsync(id, ct);
                    _log.LogInformation("Gửi báo cáo định kỳ #{Id}: {Msg}", id, r.Message);
                }
                if (DateTime.Now - _lastCleanup > TimeSpan.FromHours(12)) { _lastCleanup = DateTime.Now; await CleanupAsync(ct); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Lỗi tác vụ gửi báo cáo định kỳ"); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        var files = scope.ServiceProvider.GetRequiredService<AttachmentService>();
        var old = await db.Attachments.Where(a => a.TargetType == AttachmentTarget.ReportSchedule && a.UploadedAt < DateTime.Now.AddDays(-14)).ToListAsync(ct);
        foreach (var a in old) await files.DeleteAsync(a);
        if (old.Count > 0) _log.LogInformation("Đã dọn {Count} file báo cáo gửi định kỳ cũ", old.Count);
    }
}
