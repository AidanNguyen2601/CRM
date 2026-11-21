using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>
/// Chấm điểm lead 0–100 để ưu tiên chăm sóc:
/// mức độ (nóng 30 / ấm 15), nguồn (giới thiệu 20, sự kiện 15, website 10, email 8, mạng xã hội 5, gọi chủ động 3),
/// đủ thông tin (email, điện thoại, công ty, chức vụ: mỗi mục 4), giá trị dự kiến (≥ 500 tr: 15, ≥ 100 tr: 10, &gt; 0: 5),
/// tương tác (hoạt động đã hoàn thành: 5 điểm / lần, tối đa 15), mới tương tác trong 7 ngày (+5), đủ điều kiện (+10).
/// Lead không phù hợp / đã chuyển đổi = 0.
/// </summary>
public static class LeadScoring
{
    public record Breakdown(string Label, int Points);

    public static List<Breakdown> Explain(Lead l, int doneActivities, DateTime? lastActivity)
    {
        var b = new List<Breakdown>();
        if (l.Status is LeadStatus.Unqualified or LeadStatus.Converted) return b;
        void Add(string label, int p) { if (p != 0) b.Add(new Breakdown(label, p)); }
        Add(I18n.T("Mức độ: {0}", l.Rating.GetDisplayName()), l.Rating switch { LeadRating.Hot => 30, LeadRating.Warm => 15, _ => 0 });
        Add(I18n.T("Nguồn: {0}", l.Source.GetDisplayName()), l.Source switch
        {
            LeadSource.Referral => 20, LeadSource.Event => 15, LeadSource.Website => 10, LeadSource.EmailCampaign => 8, LeadSource.Social => 5, LeadSource.ColdCall => 3, _ => 2
        });
        var info = new[] { l.Email, l.Phone, l.Company, l.JobTitle }.Count(x => !string.IsNullOrWhiteSpace(x)) * 4;
        Add(I18n.T("Đủ thông tin liên hệ"), info);
        Add(I18n.T("Giá trị dự kiến"), l.EstimatedValue >= 500_000_000 ? 15 : l.EstimatedValue >= 100_000_000 ? 10 : l.EstimatedValue > 0 ? 5 : 0);
        Add(I18n.T("Tương tác ({0} hoạt động)", doneActivities), Math.Min(15, doneActivities * 5));
        if (lastActivity >= DateTime.Now.AddDays(-7) || l.CreatedAt >= DateTime.Now.AddDays(-7)) Add(I18n.T("Mới tương tác trong 7 ngày"), 5);
        if (l.Status == LeadStatus.Qualified) Add(I18n.T("Đủ điều kiện"), 10);
        return b;
    }

    public static int Score(List<Breakdown> b) => Math.Clamp(b.Sum(x => x.Points), 0, 100);

    public static string Css(int score) => score >= 70 ? "bg-danger" : score >= 45 ? "bg-warning text-dark" : score >= 25 ? "bg-info text-dark" : "bg-light text-dark border";

    /// <summary>Tính lại điểm cho các lead (null = mọi lead đang mở). Trả về số lead đổi điểm.</summary>
    public static async Task<int> RecalculateAsync(CrmDbContext db, IReadOnlyCollection<int>? ids, CancellationToken ct = default)
    {
        var q = db.Leads.IgnoreQueryFilters().AsQueryable();
        q = ids != null ? q.Where(l => ids.Contains(l.Id)) : q.Where(l => l.Status != LeadStatus.Converted || l.Score != 0);
        var leads = await q.ToListAsync(ct);
        if (leads.Count == 0) return 0;
        var leadIds = leads.Select(l => l.Id).ToList();
        var stats = await db.Activities.IgnoreQueryFilters().Where(a => a.LeadId != null && leadIds.Contains(a.LeadId.Value))
            .GroupBy(a => a.LeadId!.Value)
            .Select(g => new { g.Key, Done = g.Count(a => a.Status == ActivityStatus.Completed), Last = g.Max(a => (DateTime?)(a.CompletedAt ?? a.CreatedAt)) })
            .ToDictionaryAsync(x => x.Key, ct);
        var changed = 0;
        foreach (var l in leads)
        {
            var s = stats.GetValueOrDefault(l.Id);
            var score = Score(Explain(l, s?.Done ?? 0, s?.Last));
            if (score == l.Score) continue;
            l.Score = score;
            changed++;
        }
        if (changed > 0)
        {
            var suppress = db.SuppressAudit;
            db.SuppressAudit = true;   // điểm tự tính không cần ghi nhật ký
            await db.SaveChangesAsync(ct);
            db.SuppressAudit = suppress;
        }
        return changed;
    }
}

/// <summary>Cấu hình tự phân bổ lead (AppSettings "lead.assign")</summary>
public class LeadAssignSettings
{
    public const string Key = "lead.assign";
    public bool Enabled { get; set; }
    /// <summary>roundrobin: lần lượt | least: ít lead đang mở nhất | source: theo nguồn lead (nguồn chưa gán → lần lượt)</summary>| least: ít lead đang mở nhất | city: theo tỉnh/thành (không khớp → lần lượt)</summary>
    public string Method { get; set; } = "roundrobin";
    public List<int> UserIds { get; set; } = new();
    /// <summary>Nguồn lead (giá trị LeadSource) → người phụ trách</summary>
    public Dictionary<int, int> SourceRules { get; set; } = new();
    public int LastIndex { get; set; } = -1;
}

/// <summary>Tự phân bổ lead cho nhân viên theo cấu hình</summary>
public class LeadAssignService
{
    private readonly CrmDbContext _db;
    private readonly SettingsStore _settings;
    private readonly NotificationService _notify;
    public LeadAssignService(CrmDbContext db, SettingsStore settings, NotificationService notify) { _db = db; _settings = settings; _notify = notify; }

    public Task<LeadAssignSettings> SettingsAsync() => _settings.GetAsync<LeadAssignSettings>(LeadAssignSettings.Key);

    /// <summary>Chọn người phụ trách cho các lead (gán OwnerId, chưa lưu). Trả về số lead đã gán.</summary>
    public async Task<int> AssignAsync(IList<Lead> leads, int? actorId)
    {
        var s = await SettingsAsync();
        var pool = await _db.Users.Where(u => u.IsActive && s.UserIds.Contains(u.Id)).OrderBy(u => u.Id).Select(u => u.Id).ToListAsync();
        if (!s.Enabled || pool.Count == 0 || leads.Count == 0) return 0;
        var load = s.Method == "least"
            ? await _db.Leads.IgnoreQueryFilters().Where(l => l.OwnerId != null && pool.Contains(l.OwnerId.Value) && (l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted || l.Status == LeadStatus.Qualified))
                .GroupBy(l => l.OwnerId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N)
            : new Dictionary<int, int>();
        var n = 0;
        foreach (var lead in leads)
        {
            int owner;
            if (s.Method == "source" && s.SourceRules.TryGetValue((int)lead.Source, out var bySource) && pool.Contains(bySource)) owner = bySource;
            else if (s.Method == "least") owner = pool.OrderBy(id => load.GetValueOrDefault(id)).ThenBy(id => id).First();
            else { s.LastIndex = (s.LastIndex + 1) % pool.Count; owner = pool[s.LastIndex]; }
            load[owner] = load.GetValueOrDefault(owner) + 1;
            lead.OwnerId = owner;
            n++;
        }
        await _settings.SaveAsync(LeadAssignSettings.Key, s);   // lưu vị trí xoay vòng
        return n;
    }

    /// <summary>Báo cho người được phân bổ (gọi sau khi lưu lead)</summary>
    public void NotifyAssigned(IEnumerable<Lead> leads, int? actorId)
    {
        foreach (var g in leads.Where(l => l.OwnerId.HasValue).GroupBy(l => l.OwnerId!.Value))
        {
            var first = g.First();
            _notify.Add(g.Key, "lead", g.Count() == 1 ? I18n.T("Bạn được phân bổ lead: {0}", first.FullName) : I18n.T("Bạn được phân bổ {0} lead mới", g.Count()),
                g.Count() == 1 ? $"/Leads/Details/{first.Id}" : "/Leads", actorId);
        }
    }
}
