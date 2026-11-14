using System.Text;
using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>
/// Tiếp thị khách hàng: chiến dịch theo kênh (email, SMS, Zalo, sự kiện, telesales...), danh sách đối tượng lập từ
/// phân khúc khách hàng / lead, theo dõi tiếp cận – phản hồi, và đo lead / cơ hội / doanh số / ROI mang lại.
/// </summary>
public class CampaignsController : CrmControllerBase
{
    private const int PreviewRows = 100;
    private readonly CampaignService _svc;

    public CampaignsController(CrmDbContext db, CampaignService svc) : base(db) => _svc = svc;

    public async Task<IActionResult> Index([FromQuery] CampaignFilter f)
    {
        var q = Db.Campaigns.VisibleTo(User).Include(c => c.Owner).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(f.Keyword))
        {
            var k = f.Keyword.Trim();
            q = q.Where(c => c.Name.Contains(k) || c.Code.Contains(k));
        }
        if (f.Channel.HasValue) q = q.Where(c => c.Channel == f.Channel);
        if (f.OwnerId.HasValue) q = q.Where(c => c.OwnerId == f.OwnerId);
        if (f.Year.HasValue) q = q.Where(c => c.StartDate.Year == f.Year);

        // Số đếm theo trạng thái (trước khi lọc trạng thái) cho các tab
        ViewBag.Counts = await q.GroupBy(c => c.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        if (f.Status.HasValue) q = q.Where(c => c.Status == f.Status);

        // Tổng hợp hiệu quả cho toàn bộ kết quả lọc
        var allIds = await q.Select(c => c.Id).ToListAsync();
        var stats = await _svc.ComputeAsync(allIds);
        ViewBag.Total = CampaignService.Sum(stats.Values);
        ViewBag.Stats = stats;

        var total = allIds.Count;
        var items = await q.OrderBy(c => c.Status == CampaignStatus.Active ? 0 : c.Status == CampaignStatus.Planned ? 1 : 2)
            .ThenByDescending(c => c.StartDate).ThenByDescending(c => c.Id)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();

        await LoadOwnersAsync(f.OwnerId);
        ViewBag.Years = await Db.Campaigns.VisibleTo(User).Select(c => c.StartDate.Year).Distinct().OrderByDescending(y => y).ToListAsync();
        ViewBag.Filter = f;
        return View(new PagedList<Campaign>(items, total, f.Page, f.PageSize));
    }

    public async Task<IActionResult> Details(int id, [FromQuery] CampaignMemberFilter f)
    {
        var c = await Db.Campaigns.Include(x => x.Owner).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();

        var mq = Db.CampaignMembers.AsNoTracking().Where(m => m.CampaignId == id);
        var counts = await mq.GroupBy(m => m.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        if (f.MemberStatus.HasValue) mq = mq.Where(m => m.Status == f.MemberStatus);
        if (!string.IsNullOrWhiteSpace(f.Keyword))
        {
            var k = f.Keyword.Trim();
            mq = mq.Where(m => m.FullName.Contains(k) || (m.Company != null && m.Company.Contains(k))
                               || (m.Email != null && m.Email.Contains(k)) || (m.Phone != null && m.Phone.Contains(k)));
        }
        var total = await mq.CountAsync();
        var members = await mq.OrderBy(m => m.Status).ThenBy(m => m.Company).ThenBy(m => m.FullName)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync();

        return View(new CampaignDetailViewModel
        {
            Campaign = c,
            Stats = (await _svc.ComputeAsync(new[] { id }))[id],
            StatusCounts = counts,
            Members = new PagedList<CampaignMember>(members, total, f.Page, f.PageSize),
            Filter = f,
            Leads = await Db.Leads.Include(l => l.Owner).AsNoTracking().Where(l => l.CampaignId == id)
                .OrderByDescending(l => l.CreatedAt).Take(50).ToListAsync(),
            Opportunities = await Db.Opportunities.Include(o => o.Customer).Include(o => o.Owner).AsNoTracking()
                .Where(o => o.CampaignId == id).OrderByDescending(o => o.CreatedAt).Take(50).ToListAsync(),
            CanEdit = User.CanAccess(c)
        });
    }

    public async Task<IActionResult> Create()
    {
        var model = new Campaign
        {
            Code = await _svc.NextCodeAsync(DateTime.Today.Year), OwnerId = CurrentUserId,
            StartDate = DateTime.Today, EndDate = DateTime.Today.AddDays(30)
        };
        await LoadOwnersAsync(model.OwnerId);
        return View("Form", model);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var c = await Db.Campaigns.FindAsync(id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        await LoadOwnersAsync(c.OwnerId);
        return View("Form", c);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Campaign model)
    {
        if (model.Id == 0) ModelState.Remove(nameof(model.Code));   // mã do hệ thống cấp
        if (model.EndDate.HasValue && model.EndDate < model.StartDate)
            ModelState.AddModelError(nameof(model.EndDate), I18n.T("Ngày kết thúc phải sau ngày bắt đầu"));
        if (!ModelState.IsValid)
        {
            await LoadOwnersAsync(model.OwnerId);
            return View("Form", model);
        }

        Campaign entity;
        if (model.Id == 0)
        {
            if (string.IsNullOrWhiteSpace(model.Code) || await Db.Campaigns.AnyAsync(c => c.Code == model.Code))
                model.Code = await _svc.NextCodeAsync(model.StartDate.Year);
            model.Status = CampaignStatus.Planned;
            model.OwnerId = ResolveOwner(model.OwnerId);
            model.CreatedAt = DateTime.Now;
            Db.Campaigns.Add(model);
            entity = model;
        }
        else
        {
            var c = await Db.Campaigns.FindAsync(model.Id);
            if (c == null) return NotFound();
            if (!User.CanAccess(c)) return NotAllowed();
            c.Name = model.Name; c.Channel = model.Channel; c.StartDate = model.StartDate; c.EndDate = model.EndDate;
            c.Budget = model.Budget; c.ActualCost = model.ActualCost; c.ExpectedRevenue = model.ExpectedRevenue;
            c.TargetLeads = model.TargetLeads; c.Objective = model.Objective; c.Message = model.Message;
            c.OwnerId = ResolveOwner(model.OwnerId);
            c.UpdatedAt = DateTime.Now;
            entity = c;
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu chiến dịch {0}.", entity.Code);
        return RedirectToAction(nameof(Details), new { id = entity.Id });
    }

    /// <summary>Chuyển trạng thái: bắt đầu triển khai / kết thúc / hủy / mở lại</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(int id, CampaignStatus status)
    {
        var c = await Db.Campaigns.FindAsync(id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();

        var allowed = (c.Status, status) switch
        {
            (CampaignStatus.Planned, CampaignStatus.Active or CampaignStatus.Cancelled) => true,
            (CampaignStatus.Active, CampaignStatus.Completed or CampaignStatus.Cancelled) => true,
            (CampaignStatus.Completed or CampaignStatus.Cancelled, CampaignStatus.Active) => true,
            _ => false
        };
        if (!allowed)
        {
            TempData["Error"] = I18n.T("Không thể chuyển trạng thái chiến dịch.");
            return RedirectToAction(nameof(Details), new { id });
        }

        if (status == CampaignStatus.Active && c.Status == CampaignStatus.Planned && c.StartDate > DateTime.Today) c.StartDate = DateTime.Today;
        if (status == CampaignStatus.Active && c.EndDate.HasValue && c.EndDate < DateTime.Today) c.EndDate = null;
        if (status is CampaignStatus.Completed or CampaignStatus.Cancelled && (c.EndDate == null || c.EndDate > DateTime.Today))
            c.EndDate = DateTime.Today < c.StartDate ? c.StartDate : DateTime.Today;
        c.Status = status;
        c.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Chiến dịch chuyển sang trạng thái: {0}.", status.GetDisplayName());
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await Db.Campaigns.FindAsync(id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        if (await Db.Leads.AnyAsync(l => l.CampaignId == id) || await Db.Opportunities.AnyAsync(o => o.CampaignId == id))
        {
            TempData["Error"] = I18n.T("Chiến dịch đã mang lại lead hoặc cơ hội nên không thể xóa. Hãy chuyển sang 'Đã hủy' nếu không dùng nữa.");
            return RedirectToAction(nameof(Details), new { id });
        }
        await Db.CampaignMembers.Where(m => m.CampaignId == id).ExecuteDeleteAsync();
        Db.Campaigns.Remove(c);
        await Db.SaveChangesAsync();
        await DeleteAttachmentsAsync(AttachmentTarget.Campaign, id);
        TempData["Success"] = I18n.T("Đã xóa chiến dịch {0}.", c.Code);
        return RedirectToAction(nameof(Index));
    }

    // ==================== Đối tượng chiến dịch ====================

    /// <summary>Màn hình lập danh sách đối tượng theo phân khúc</summary>
    public async Task<IActionResult> Segment(int id, CampaignAudience audience = CampaignAudience.Customers)
    {
        var c = await Db.Campaigns.FindAsync(id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        if (!c.IsOpen) return ClosedCampaign(id);
        var vm = new CampaignSegmentViewModel { CampaignId = id, Campaign = c, Audience = audience };
        await LoadOwnersAsync(null);
        return View(vm);
    }

    /// <summary>mode = preview: xem trước số lượng; mode = add: thêm vào chiến dịch</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Segment(CampaignSegmentViewModel vm, string mode)
    {
        var c = await Db.Campaigns.FindAsync(vm.CampaignId);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        if (!c.IsOpen) return ClosedCampaign(c.Id);

        var members = await _svc.ResolveSegmentAsync(vm, c, User);
        if (mode == "add")
        {
            if (members.Count == 0)
            {
                TempData["Error"] = I18n.T("Không có đối tượng mới nào phù hợp để thêm.");
            }
            else
            {
                var now = DateTime.Now;
                members.ForEach(m => m.AddedAt = now);
                Db.CampaignMembers.AddRange(members);
                await Db.SaveChangesAsync();
                TempData["Success"] = I18n.T("Đã thêm {0} đối tượng vào chiến dịch (bỏ qua {1} trùng, {2} thiếu thông tin liên lạc, {3} từ chối nhận tin).",
                    members.Count, vm.SkippedDuplicate, vm.SkippedNoInfo, vm.SkippedUnsubscribed);
                return RedirectToAction(nameof(Details), new { id = c.Id });
            }
        }

        ModelState.Clear();
        vm.Campaign = c;
        vm.Previewed = true;
        vm.Preview = members.Take(PreviewRows).ToList();
        await LoadOwnersAsync(vm.OwnerId);
        return View(vm);
    }

    /// <summary>Cập nhật trạng thái tiếp cận hàng loạt; tùy chọn ghi vào lịch sử chăm sóc</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateMembers(int id, int[] memberIds, CampaignMemberStatus status, bool logActivity, string? note)
    {
        var c = await Db.Campaigns.FindAsync(id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        if (memberIds.Length == 0)
        {
            TempData["Error"] = I18n.T("Chưa chọn đối tượng nào.");
            return RedirectToAction(nameof(Details), new { id });
        }

        var members = await Db.CampaignMembers.Where(m => m.CampaignId == id && memberIds.Contains(m.Id)).ToListAsync();
        var now = DateTime.Now;
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note?.Length > 500) note = note[..500];
        var logged = 0;
        foreach (var m in members)
        {
            m.Status = status;
            if (status != CampaignMemberStatus.Pending) m.LastContactAt = now;
            if (note != null) m.Note = note;

            // Chỉ ghi hoạt động khi đã thực sự tiếp cận và có hồ sơ gốc để gắn vào
            if (logActivity && status is not (CampaignMemberStatus.Pending or CampaignMemberStatus.Failed)
                && (m.CustomerId != null || m.LeadId != null))
            {
                Db.Activities.Add(new Activity
                {
                    Type = Campaign.ActivityTypeFor(c.Channel),
                    Subject = Cut($"[{c.Code}] {c.Name}", 200),
                    DueDate = now, CompletedAt = now, Status = ActivityStatus.Completed, Priority = Priority.Normal,
                    OwnerId = CurrentUserId, CustomerId = m.CustomerId, ContactId = m.ContactId, LeadId = m.LeadId,
                    Description = Cut($"{I18n.T("Chiến dịch tiếp thị")} – {c.Channel.GetDisplayName()}: {status.GetDisplayName()}" + (note != null ? $"\n{note}" : ""), 2000)
                });
                logged++;
            }
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = logged > 0
            ? I18n.T("Đã cập nhật {0} đối tượng và ghi {1} hoạt động vào lịch sử chăm sóc.", members.Count, logged)
            : I18n.T("Đã cập nhật {0} đối tượng.", members.Count);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMembers(int id, int[] memberIds)
    {
        var c = await Db.Campaigns.FindAsync(id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        var n = memberIds.Length == 0 ? 0
            : await Db.CampaignMembers.Where(m => m.CampaignId == id && memberIds.Contains(m.Id)).ExecuteDeleteAsync();
        TempData[n > 0 ? "Success" : "Error"] = n > 0 ? I18n.T("Đã bỏ {0} đối tượng khỏi chiến dịch.", n) : I18n.T("Chưa chọn đối tượng nào.");
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Xuất danh sách đối tượng ra CSV (UTF-8) để nạp vào công cụ gửi email / SMS / Zalo</summary>
    public async Task<IActionResult> Export(int id, CampaignMemberStatus? memberStatus)
    {
        var c = await Db.Campaigns.FindAsync(id);
        if (c == null) return NotFound();
        if (!User.CanAccess(c)) return NotAllowed();
        var q = Db.CampaignMembers.AsNoTracking().Where(m => m.CampaignId == id);
        if (memberStatus.HasValue) q = q.Where(m => m.Status == memberStatus);
        var rows = await q.OrderBy(m => m.Company).ThenBy(m => m.FullName).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", new[] { I18n.T("Họ tên"), I18n.T("Công ty"), "Email", I18n.T("Điện thoại"), I18n.T("Trạng thái"), I18n.T("Lần tiếp cận gần nhất"), I18n.T("Ghi chú") }.Select(Csv)));
        foreach (var m in rows)
            sb.AppendLine(string.Join(",", new[]
            {
                m.FullName, m.Company, m.Email, m.Phone, m.Status.GetDisplayName(), m.LastContactAt?.ToString("dd/MM/yyyy HH:mm"), m.Note
            }.Select(Csv)));
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", $"{c.Code}_{DateTime.Now:yyyyMMdd}.csv");
    }

    /// <summary>Danh sách chiến dịch cho dropdown "Chiến dịch" trên lead / cơ hội</summary>
    public static async Task<SelectList> CampaignOptionsAsync(CrmDbContext db, int? selected)
    {
        var items = await db.Campaigns.AsNoTracking()
            .Where(c => c.Status == CampaignStatus.Planned || c.Status == CampaignStatus.Active || c.Id == selected)
            .OrderByDescending(c => c.StartDate).Select(c => new { c.Id, Name = c.Code + " - " + c.Name }).ToListAsync();
        return new SelectList(items, "Id", "Name", selected);
    }

    private IActionResult ClosedCampaign(int id)
    {
        TempData["Error"] = I18n.T("Chiến dịch đã kết thúc hoặc đã hủy, không thể thêm đối tượng.");
        return RedirectToAction(nameof(Details), new { id });
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>Ô CSV: bọc nháy kép, chặn công thức Excel (=, +, -, @) để tránh CSV injection</summary>
    private static string Csv(string? v)
    {
        v ??= "";
        var isPhone = System.Text.RegularExpressions.Regex.IsMatch(v, @"^\+?[0-9 .\-()]+$");
        if (v.Length > 0 && !isPhone && "=+-@\t\r".Contains(v[0])) v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }
}
