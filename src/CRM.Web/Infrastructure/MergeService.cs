using System.Security.Claims;
using System.Text.RegularExpressions;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>Một bản ghi trong nhóm nghi trùng</summary>
public class DupItem
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public string? Code { get; init; }
    public string? TaxCode { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Extra { get; init; }
    public string? Owner { get; init; }
    public DateTime CreatedAt { get; init; }
    /// <summary>Số bản ghi liên quan (cơ hội, hợp đồng...) – gợi ý chọn bản ghi chính</summary>
    public int Related { get; set; }
}

public class DupGroup
{
    public string Reason { get; init; } = "";
    public string Value { get; init; } = "";
    public List<DupItem> Items { get; init; } = new();
}

/// <summary>
/// Tìm nhóm bản ghi nghi trùng (khách hàng, người liên hệ, lead) và gộp: chuyển toàn bộ dữ liệu liên quan
/// (mọi bảng có khóa ngoại tới bản ghi bị gộp, file đính kèm, chia sẻ, email) sang bản ghi chính, bổ sung các trường
/// bản ghi chính còn trống, rồi xóa bản ghi trùng. Chạy trong transaction, ghi nhật ký "Gộp trùng".
/// </summary>
public class MergeService
{
    public static readonly string[] Types = { nameof(Customer), nameof(Contact), nameof(Lead) };
    private readonly CrmDbContext _db;
    public MergeService(CrmDbContext db) => _db = db;

    /// <summary>Tên công ty chuẩn hoá để so trùng: bỏ dấu, bỏ loại hình doanh nghiệp, ký tự đặc biệt</summary>
    public static string NameKey(string? name)
    {
        var n = ImportService.Norm(name);
        n = Regex.Replace(n, @"\b(cong ty|cty|tnhh|co phan|cp|mtv|mot thanh vien|tap doan|chi nhanh|jsc|co ltd|ltd|company|corporation|corp)\b", " ");
        return Regex.Replace(n, @"[^a-z0-9]+", "");
    }

    public async Task<List<DupGroup>> FindAsync(string type, ClaimsPrincipal user)
    {
        List<DupItem> items = type switch
        {
            nameof(Customer) => await _db.Customers.VisibleTo(user, _db).AsNoTracking().Select(c => new DupItem
            {
                Id = c.Id, Title = c.Name, Code = c.Code, TaxCode = c.TaxCode, Email = c.Email, Phone = c.Phone, Extra = c.City,
                Owner = c.Owner != null ? c.Owner.FullName : null, CreatedAt = c.CreatedAt
            }).ToListAsync(),
            nameof(Contact) => await _db.Contacts.VisibleTo(user, _db).AsNoTracking().Select(c => new DupItem
            {
                Id = c.Id, Title = c.FullName, Email = c.Email, Phone = c.Phone, Extra = c.Customer != null ? c.Customer.Name : null,
                Owner = c.Owner != null ? c.Owner.FullName : null, CreatedAt = c.CreatedAt
            }).ToListAsync(),
            _ => await _db.Leads.VisibleTo(user, _db).AsNoTracking().Where(l => l.Status != LeadStatus.Converted).Select(l => new DupItem
            {
                Id = l.Id, Title = l.FullName, Email = l.Email, Phone = l.Phone, Extra = l.Company,
                Owner = l.Owner != null ? l.Owner.FullName : null, CreatedAt = l.CreatedAt
            }).ToListAsync()
        };

        var groups = new List<DupGroup>();
        var grouped = new HashSet<string>();
        void Add(string reason, Func<DupItem, string> key)
        {
            foreach (var g in items.Select(i => (Key: key(i), Item: i)).Where(x => x.Key.Length > 0).GroupBy(x => x.Key).Where(g => g.Count() > 1))
            {
                var ids = string.Join(",", g.Select(x => x.Item.Id).OrderBy(i => i));
                if (!grouped.Add(ids)) continue;   // cùng nhóm đã liệt kê theo tiêu chí khác
                groups.Add(new DupGroup { Reason = reason, Value = g.Key, Items = g.Select(x => x.Item).OrderBy(x => x.CreatedAt).ToList() });
            }
        }
        if (type == nameof(Customer)) Add(I18n.T("Trùng mã số thuế"), i => ImportService.Digits(i.TaxCode).Length >= 10 ? ImportService.Digits(i.TaxCode) : "");
        Add(I18n.T("Trùng email"), i => ImportService.EmailKey(i.Email));
        Add(I18n.T("Trùng điện thoại"), i => ImportService.PhoneKey(i.Phone));
        if (type == nameof(Customer)) Add(I18n.T("Trùng tên công ty"), i => NameKey(i.Title) is { Length: >= 4 } k ? k : "");
        else Add(I18n.T("Trùng họ tên và công ty"), i => ImportService.Norm(i.Title) + "|" + ImportService.Norm(i.Extra) is var k && k.Length > 4 && !k.EndsWith("|") ? k : "");

        // Số dữ liệu liên quan của từng bản ghi (gợi ý giữ bản ghi có nhiều dữ liệu nhất)
        var ids = groups.SelectMany(g => g.Items).Select(i => i.Id).Distinct().ToList();
        if (ids.Count > 0)
        {
            var counts = type switch
            {
                nameof(Customer) => (await _db.Opportunities.IgnoreQueryFilters().Where(o => o.CustomerId != null && ids.Contains(o.CustomerId.Value)).GroupBy(o => o.CustomerId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync())
                    .Concat(await _db.Contracts.IgnoreQueryFilters().Where(o => o.CustomerId != null && ids.Contains(o.CustomerId.Value)).GroupBy(o => o.CustomerId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync())
                    .Concat(await _db.Contacts.IgnoreQueryFilters().Where(o => o.CustomerId != null && ids.Contains(o.CustomerId.Value)).GroupBy(o => o.CustomerId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync()),
                nameof(Contact) => await _db.Activities.IgnoreQueryFilters().Where(a => a.ContactId != null && ids.Contains(a.ContactId.Value)).GroupBy(a => a.ContactId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync(),
                _ => await _db.Activities.IgnoreQueryFilters().Where(a => a.LeadId != null && ids.Contains(a.LeadId.Value)).GroupBy(a => a.LeadId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync()
            };
            var map = counts.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Sum(x => x.N));
            foreach (var i in groups.SelectMany(g => g.Items)) i.Related = map.GetValueOrDefault(i.Id);
        }
        return groups.OrderBy(g => g.Reason).ThenBy(g => g.Value).ToList();
    }

    /// <summary>Gộp các bản ghi duplicateIds vào masterId. Trả về số bản ghi liên quan đã chuyển.</summary>
    public async Task<int> MergeAsync(string type, int masterId, IReadOnlyCollection<int> duplicateIds)
    {
        var clr = type switch { nameof(Customer) => typeof(Customer), nameof(Contact) => typeof(Contact), nameof(Lead) => typeof(Lead), _ => throw new ArgumentException(type) };
        var dupIds = duplicateIds.Where(i => i != masterId).Distinct().ToList();
        if (dupIds.Count == 0) return 0;
        var moved = 0;
        await using var tx = await _db.Database.BeginTransactionAsync();

        // 1) Bổ sung trường còn trống của bản ghi chính từ bản ghi trùng (theo thứ tự cũ → mới)
        var master = await _db.FindAsync(clr, masterId) ?? throw new InvalidOperationException();
        var dups = new List<object>();
        foreach (var id in dupIds) dups.Add(await _db.FindAsync(clr, id) ?? throw new InvalidOperationException());
        var fill = type switch
        {
            nameof(Customer) => new[] { "TaxCode", "Phone", "Email", "Website", "Address", "City", "Industry", "EmployeeCount", "Description" },
            nameof(Contact) => new[] { "JobTitle", "Email", "Phone", "BirthDate", "CustomerId", "Notes" },
            _ => new[] { "Company", "JobTitle", "Email", "Phone", "Description", "CampaignId" }
        };
        var changes = new List<AuditChange>();
        foreach (var f in fill)
        {
            var p = clr.GetProperty(f)!;
            var cur = p.GetValue(master);
            if (cur != null && !(cur is string s && string.IsNullOrWhiteSpace(s))) continue;
            var src = dups.Select(d => p.GetValue(d)).FirstOrDefault(v => v != null && !(v is string s2 && string.IsNullOrWhiteSpace(s2)));
            if (src == null) continue;
            p.SetValue(master, src);
        }
        changes.Add(new AuditChange("MergedFrom", null, string.Join("; ", dups.Select(d => Title(type, d)))));

        // 2) Chuyển mọi bảng có khóa ngoại tới bản ghi bị gộp
        var table = _db.Model.FindEntityType(clr)!;
        var idList = string.Join(",", dupIds);
        foreach (var fk in _db.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()).Where(fk => fk.PrincipalEntityType == table && fk.Properties.Count == 1))
        {
            var dep = fk.DeclaringEntityType;
            var tableName = dep.GetTableName();
            if (tableName == null || dep.ClrType == clr && fk.Properties[0].Name == "Id") continue;
            var col = fk.Properties[0].GetColumnName();
            moved += await _db.Database.ExecuteSqlRawAsync($"UPDATE [dbo].[{tableName}] SET [{col}] = {{0}} WHERE [{col}] IN ({idList})", masterId);
        }
        // Tham chiếu không có khóa ngoại: file đính kèm, chia sẻ, email đã gửi
        var attType = type switch { nameof(Customer) => (int)AttachmentTarget.Customer, nameof(Lead) => (int)AttachmentTarget.Lead, _ => -1 };
        if (attType >= 0)
            moved += await _db.Database.ExecuteSqlRawAsync($"UPDATE [dbo].[Attachments] SET [TargetId] = {{0}} WHERE [TargetType] = {{1}} AND [TargetId] IN ({idList})", masterId, attType);
        await _db.Database.ExecuteSqlRawAsync($"DELETE s FROM [dbo].[RecordShares] s WHERE s.[EntityType] = {{0}} AND s.[EntityId] IN ({idList}) AND EXISTS (SELECT 1 FROM [dbo].[RecordShares] m WHERE m.[EntityType] = s.[EntityType] AND m.[EntityId] = {{1}} AND m.[UserId] = s.[UserId])", type, masterId);
        await _db.Database.ExecuteSqlRawAsync($"UPDATE [dbo].[RecordShares] SET [EntityId] = {{0}} WHERE [EntityType] = {{1}} AND [EntityId] IN ({idList})", masterId, type);
        // Nhãn và trường tùy biến (bỏ phần trùng với bản ghi chính trước khi chuyển)
        await _db.Database.ExecuteSqlRawAsync($"DELETE e FROM [dbo].[EntityTags] e WHERE e.[EntityType] = {{0}} AND e.[EntityId] IN ({idList}) AND EXISTS (SELECT 1 FROM [dbo].[EntityTags] m WHERE m.[EntityType] = e.[EntityType] AND m.[EntityId] = {{1}} AND m.[TagId] = e.[TagId])", type, masterId);
        await _db.Database.ExecuteSqlRawAsync($"DELETE e FROM [dbo].[EntityTags] e WHERE e.[EntityType] = {{0}} AND e.[EntityId] IN ({idList}) AND e.[Id] NOT IN (SELECT MIN(x.[Id]) FROM [dbo].[EntityTags] x WHERE x.[EntityType] = {{0}} AND x.[EntityId] IN ({idList}) GROUP BY x.[TagId])", type);
        moved += await _db.Database.ExecuteSqlRawAsync($"UPDATE [dbo].[EntityTags] SET [EntityId] = {{0}} WHERE [EntityType] = {{1}} AND [EntityId] IN ({idList})", masterId, type);
        var fieldIds = await _db.CustomFieldDefs.Where(f => f.EntityType == type).Select(f => f.Id).ToListAsync();
        if (fieldIds.Count > 0)
        {
            var fl = string.Join(",", fieldIds);
            await _db.Database.ExecuteSqlRawAsync($"DELETE v FROM [dbo].[CustomFieldValues] v WHERE v.[FieldId] IN ({fl}) AND v.[EntityId] IN ({idList}) AND (EXISTS (SELECT 1 FROM [dbo].[CustomFieldValues] m WHERE m.[FieldId] = v.[FieldId] AND m.[EntityId] = {{0}}) OR v.[Id] NOT IN (SELECT MIN(x.[Id]) FROM [dbo].[CustomFieldValues] x WHERE x.[FieldId] IN ({fl}) AND x.[EntityId] IN ({idList}) GROUP BY x.[FieldId]))", masterId);
            moved += await _db.Database.ExecuteSqlRawAsync($"UPDATE [dbo].[CustomFieldValues] SET [EntityId] = {{0}} WHERE [FieldId] IN ({fl}) AND [EntityId] IN ({idList})", masterId);
        }
        if (type == nameof(Customer))
        {
            moved += await _db.Database.ExecuteSqlRawAsync($"UPDATE [dbo].[EmailMessages] SET [CustomerId] = {{0}} WHERE [CustomerId] IN ({idList})", masterId);
            await _db.SyncTicketBranchesAsync();
        }

        // 3) Xóa bản ghi trùng (dữ liệu liên quan đã chuyển hết)
        foreach (var d in dups) _db.Remove(d);
        AuditTrail.Write(_db, type, masterId, "Merge", Title(type, master), changes);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return moved;
    }

    private static string Title(string type, object e) => e switch
    {
        Customer c => $"{c.Code} – {c.Name}",
        Contact c => c.FullName,
        Lead l => l.FullName + (string.IsNullOrEmpty(l.Company) ? "" : $" ({l.Company})"),
        _ => e.ToString() ?? ""
    };
}
