using System.Security.Claims;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>
/// Nghiệp vụ tiếp thị: tính hiệu quả chiến dịch (tiếp cận, phản hồi, lead, cơ hội, doanh số, ROI)
/// và lập danh sách đối tượng từ phân khúc khách hàng / lead.
/// </summary>
public class CampaignService
{
    private readonly CrmDbContext _db;
    public CampaignService(CrmDbContext db) => _db = db;

    /// <summary>Chỉ số hiệu quả theo từng chiến dịch</summary>
    public async Task<Dictionary<int, CampaignStats>> ComputeAsync(IReadOnlyCollection<int> ids)
    {
        var result = ids.Distinct().ToDictionary(id => id, _ => new CampaignStats());
        if (result.Count == 0) return result;
        var list = result.Keys.ToList();

        var costs = await _db.Campaigns.Where(c => list.Contains(c.Id))
            .Select(c => new { c.Id, c.ActualCost, c.Budget }).ToListAsync();
        foreach (var c in costs) { result[c.Id].Cost = c.ActualCost; result[c.Id].Budget = c.Budget; }

        var members = await _db.CampaignMembers.Where(m => list.Contains(m.CampaignId))
            .GroupBy(m => new { m.CampaignId, m.Status }).Select(g => new { g.Key.CampaignId, g.Key.Status, N = g.Count() }).ToListAsync();
        foreach (var m in members)
        {
            var s = result[m.CampaignId];
            s.Members += m.N;
            if (m.Status is not (CampaignMemberStatus.Pending or CampaignMemberStatus.Failed)) s.Reached += m.N;
            if (m.Status is CampaignMemberStatus.Responded or CampaignMemberStatus.Converted) s.Positive += m.N;
            if (m.Status == CampaignMemberStatus.Unsubscribed) s.Unsubscribed += m.N;
        }

        var leads = await _db.Leads.Where(l => l.CampaignId != null && list.Contains(l.CampaignId.Value))
            .GroupBy(l => l.CampaignId!.Value)
            .Select(g => new { Id = g.Key, N = g.Count(), Converted = g.Count(l => l.Status == LeadStatus.Converted) }).ToListAsync();
        foreach (var l in leads) { result[l.Id].Leads = l.N; result[l.Id].ConvertedLeads = l.Converted; }

        var opps = await _db.Opportunities.Where(o => o.CampaignId != null && list.Contains(o.CampaignId.Value))
            .GroupBy(o => new { Id = o.CampaignId!.Value, o.Stage })
            .Select(g => new { g.Key.Id, g.Key.Stage, N = g.Count(), Amount = g.Sum(o => o.Amount) }).ToListAsync();
        foreach (var o in opps)
        {
            var s = result[o.Id];
            s.Opportunities += o.N;
            if (o.Stage == OpportunityStage.ClosedWon) { s.WonCount += o.N; s.Won += o.Amount; }
            else if (o.Stage != OpportunityStage.ClosedLost) s.Pipeline += o.Amount;
        }
        return result;
    }

    public static CampaignStats Sum(IEnumerable<CampaignStats> items)
    {
        var t = new CampaignStats();
        foreach (var s in items)
        {
            t.Members += s.Members; t.Reached += s.Reached; t.Positive += s.Positive; t.Unsubscribed += s.Unsubscribed;
            t.Leads += s.Leads; t.ConvertedLeads += s.ConvertedLeads; t.Opportunities += s.Opportunities;
            t.Pipeline += s.Pipeline; t.WonCount += s.WonCount; t.Won += s.Won; t.Cost += s.Cost; t.Budget += s.Budget;
        }
        return t;
    }

    /// <summary>
    /// Lập danh sách đối tượng mới theo phân khúc: chỉ lấy dữ liệu người dùng được xem, bỏ trùng với đối tượng đã có
    /// (theo liên hệ / lead / email), bỏ người thiếu thông tin liên lạc theo kênh và người đã từ chối nhận tin.
    /// Cập nhật các số đếm trong <paramref name="vm"/> và trả về các bản ghi chưa lưu.
    /// </summary>
    public async Task<List<CampaignMember>> ResolveSegmentAsync(CampaignSegmentViewModel vm, Campaign campaign, ClaimsPrincipal user)
    {
        var candidates = vm.Audience == CampaignAudience.Leads
            ? await LeadCandidatesAsync(vm, user)
            : await CustomerCandidatesAsync(vm, user);

        var existing = await _db.CampaignMembers.AsNoTracking().Where(m => m.CampaignId == campaign.Id)
            .Select(m => new { m.CustomerId, m.ContactId, m.LeadId, m.Email }).ToListAsync();
        var exContacts = existing.Where(m => m.ContactId != null).Select(m => m.ContactId!.Value).ToHashSet();
        var exLeads = existing.Where(m => m.LeadId != null).Select(m => m.LeadId!.Value).ToHashSet();
        var exCustomers = existing.Where(m => m.ContactId == null && m.LeadId == null && m.CustomerId != null).Select(m => m.CustomerId!.Value).ToHashSet();
        var emails = existing.Where(m => !string.IsNullOrWhiteSpace(m.Email)).Select(m => m.Email!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<int> unsubContacts = new(), unsubLeads = new();
        HashSet<string> unsubEmails = new(StringComparer.OrdinalIgnoreCase);
        if (vm.ExcludeUnsubscribed)
        {
            var unsub = await _db.CampaignMembers.AsNoTracking().Where(m => m.Status == CampaignMemberStatus.Unsubscribed)
                .Select(m => new { m.ContactId, m.LeadId, m.Email }).ToListAsync();
            unsubContacts = unsub.Where(u => u.ContactId != null).Select(u => u.ContactId!.Value).ToHashSet();
            unsubLeads = unsub.Where(u => u.LeadId != null).Select(u => u.LeadId!.Value).ToHashSet();
            unsubEmails = unsub.Where(u => !string.IsNullOrWhiteSpace(u.Email)).Select(u => u.Email!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var needsEmail = vm.RequireChannelInfo ? Campaign.NeedsEmail(campaign.Channel) : null;
        var result = new List<CampaignMember>();
        vm.SkippedDuplicate = vm.SkippedNoInfo = vm.SkippedUnsubscribed = 0;
        foreach (var m in candidates)
        {
            var email = m.Email?.Trim();
            var hasEmail = !string.IsNullOrWhiteSpace(email);
            var dup = (m.ContactId != null && exContacts.Contains(m.ContactId.Value))
                      || (m.LeadId != null && exLeads.Contains(m.LeadId.Value))
                      || (m.ContactId == null && m.LeadId == null && m.CustomerId != null && exCustomers.Contains(m.CustomerId.Value))
                      || (hasEmail && emails.Contains(email!));
            if (dup) { vm.SkippedDuplicate++; continue; }

            if ((m.ContactId != null && unsubContacts.Contains(m.ContactId.Value))
                || (m.LeadId != null && unsubLeads.Contains(m.LeadId.Value))
                || (hasEmail && unsubEmails.Contains(email!)))
            { vm.SkippedUnsubscribed++; continue; }

            if ((needsEmail == true && !hasEmail) || (needsEmail == false && string.IsNullOrWhiteSpace(m.Phone)))
            { vm.SkippedNoInfo++; continue; }

            m.CampaignId = campaign.Id;
            m.Email = hasEmail ? email : null;
            result.Add(m);
            // Bỏ trùng ngay trong lô đang thêm
            if (m.ContactId != null) exContacts.Add(m.ContactId.Value);
            if (m.LeadId != null) exLeads.Add(m.LeadId.Value);
            if (m.ContactId == null && m.LeadId == null && m.CustomerId != null) exCustomers.Add(m.CustomerId.Value);
            if (hasEmail) emails.Add(email!);
        }
        vm.ToAdd = result.Count;
        return result;
    }

    private async Task<List<CampaignMember>> CustomerCandidatesAsync(CampaignSegmentViewModel vm, ClaimsPrincipal user)
    {
        var cq = _db.Customers.VisibleTo(user).AsNoTracking();
        if (vm.CustomerType.HasValue) cq = cq.Where(c => c.Type == vm.CustomerType);
        else cq = cq.Where(c => c.Type != CustomerType.Inactive);
        if (!string.IsNullOrWhiteSpace(vm.Industry)) cq = cq.Where(c => c.Industry == vm.Industry);
        if (!string.IsNullOrWhiteSpace(vm.City)) cq = cq.Where(c => c.City == vm.City);
        if (vm.OwnerId.HasValue && user.SeesAll()) cq = cq.Where(c => c.OwnerId == vm.OwnerId);
        if (!string.IsNullOrWhiteSpace(vm.Keyword))
        {
            var k = vm.Keyword.Trim();
            cq = cq.Where(c => c.Name.Contains(k) || c.Code.Contains(k));
        }

        var customers = await cq.OrderBy(c => c.Name).Select(c => new { c.Id, c.Name, c.Email, c.Phone }).ToListAsync();
        var contacts = (await _db.Contacts.AsNoTracking()
                .Where(x => x.CustomerId != null && cq.Any(c => c.Id == x.CustomerId))
                .OrderByDescending(x => x.IsPrimary).ThenBy(x => x.FullName)
                .Select(x => new { x.Id, CustomerId = x.CustomerId!.Value, x.FullName, x.Email, x.Phone }).ToListAsync())
            .ToLookup(x => x.CustomerId);

        var list = new List<CampaignMember>();
        foreach (var c in customers)
        {
            var cs = contacts[c.Id].ToList();
            if (cs.Count == 0)
            {
                list.Add(new CampaignMember { CustomerId = c.Id, FullName = Cut(c.Name, 150), Company = c.Name, Email = c.Email, Phone = c.Phone });
                continue;
            }
            foreach (var x in vm.AllContacts ? cs : cs.Take(1))
                list.Add(new CampaignMember
                {
                    CustomerId = c.Id, ContactId = x.Id, FullName = x.FullName, Company = c.Name,
                    Email = string.IsNullOrWhiteSpace(x.Email) ? c.Email : x.Email,
                    Phone = string.IsNullOrWhiteSpace(x.Phone) ? c.Phone : x.Phone
                });
        }
        return list;
    }

    private async Task<List<CampaignMember>> LeadCandidatesAsync(CampaignSegmentViewModel vm, ClaimsPrincipal user)
    {
        var q = _db.Leads.VisibleTo(user).AsNoTracking();
        if (vm.LeadStatus.HasValue) q = q.Where(l => l.Status == vm.LeadStatus);
        else q = q.Where(l => l.Status != LeadStatus.Converted && l.Status != LeadStatus.Unqualified);
        if (vm.LeadRating.HasValue) q = q.Where(l => l.Rating == vm.LeadRating);
        if (vm.LeadSource.HasValue) q = q.Where(l => l.Source == vm.LeadSource);
        if (vm.OwnerId.HasValue && user.SeesAll()) q = q.Where(l => l.OwnerId == vm.OwnerId);
        if (!string.IsNullOrWhiteSpace(vm.Keyword))
        {
            var k = vm.Keyword.Trim();
            q = q.Where(l => l.FullName.Contains(k) || (l.Company != null && l.Company.Contains(k)));
        }
        return await q.OrderBy(l => l.Rating).ThenBy(l => l.FullName)
            .Select(l => new CampaignMember
            {
                LeadId = l.Id, CustomerId = l.ConvertedCustomerId, FullName = l.FullName, Company = l.Company, Email = l.Email, Phone = l.Phone
            }).ToListAsync();
    }

    /// <summary>Sinh mã chiến dịch dạng CD2026-001 (nối tiếp theo năm)</summary>
    public async Task<string> NextCodeAsync(int year)
    {
        var prefix = $"CD{year}-";
        var codes = await _db.Campaigns.Where(c => c.Code.StartsWith(prefix)).Select(c => c.Code).ToListAsync();
        var max = codes.Select(c => int.TryParse(c[prefix.Length..], out var n) ? n : 0).DefaultIfEmpty(0).Max();
        return $"{prefix}{max + 1:000}";
    }

    /// <summary>Lead đã chuyển đổi → cập nhật đối tượng chiến dịch tương ứng sang khách hàng / liên hệ mới, trạng thái Chuyển đổi</summary>
    public async Task OnLeadConvertedAsync(int leadId, int customerId, int contactId)
    {
        var members = await _db.CampaignMembers.Where(m => m.LeadId == leadId).ToListAsync();
        foreach (var m in members)
        {
            m.CustomerId = customerId;
            m.ContactId = contactId;
            if (m.Status != CampaignMemberStatus.Unsubscribed) { m.Status = CampaignMemberStatus.Converted; m.LastContactAt ??= DateTime.Now; }
        }
    }

    /// <summary>Trước khi xóa hồ sơ gốc: bỏ liên kết, giữ lại thông tin đã lưu trên danh sách đối tượng</summary>
    public async Task DetachAsync(int? customerId = null, int? contactId = null, int? leadId = null)
    {
        if (customerId.HasValue)
            await _db.CampaignMembers.Where(m => m.CustomerId == customerId).ExecuteUpdateAsync(s => s.SetProperty(m => m.CustomerId, (int?)null));
        if (contactId.HasValue)
            await _db.CampaignMembers.Where(m => m.ContactId == contactId).ExecuteUpdateAsync(s => s.SetProperty(m => m.ContactId, (int?)null));
        if (leadId.HasValue)
            await _db.CampaignMembers.Where(m => m.LeadId == leadId).ExecuteUpdateAsync(s => s.SetProperty(m => m.LeadId, (int?)null));
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n];
}
