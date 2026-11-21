using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.ViewComponents;

public class AuditTrailModel
{
    public string Type { get; set; } = "";
    public int Id { get; set; }
    public List<AuditLog> Items { get; set; } = new();
    public int Total { get; set; }
    /// <summary>Tên hiển thị cho giá trị khóa ngoại (OwnerId → tên người dùng, CustomerId → tên khách hàng)</summary>
    public Dictionary<string, Dictionary<string, string>> Lookups { get; set; } = new();

    public string Value(string field, string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "–";
        var key = AuditLookups.KeyOf(field);
        return key != null && Lookups.TryGetValue(key, out var map) && map.TryGetValue(raw, out var name) ? name : raw;
    }
}

/// <summary>Tra tên cho các trường khóa ngoại thường gặp trong nhật ký</summary>
public static class AuditLookups
{
    private static readonly string[] UserFields = { "OwnerId", "UserId", "AssignedById", "ApprovedById", "CreatedById", "SharedById" };
    public static string? KeyOf(string field) =>
        UserFields.Contains(field) ? "user" : field == "CustomerId" ? "customer" : field == "ContactId" ? "contact" : null;

    public static async Task<Dictionary<string, Dictionary<string, string>>> LoadAsync(CrmDbContext db, IEnumerable<AuditLog> logs)
    {
        var ids = new Dictionary<string, HashSet<int>> { ["user"] = new(), ["customer"] = new(), ["contact"] = new() };
        foreach (var c in logs.SelectMany(l => AuditTrail.Parse(l.Changes)))
        {
            var k = KeyOf(c.f);
            if (k == null) continue;
            foreach (var v in new[] { c.o, c.n }) if (int.TryParse(v, out var i)) ids[k].Add(i);
        }
        var r = new Dictionary<string, Dictionary<string, string>>();
        if (ids["user"].Count > 0)
            r["user"] = await db.Users.Where(u => ids["user"].Contains(u.Id)).ToDictionaryAsync(u => u.Id.ToString(), u => u.FullName);
        if (ids["customer"].Count > 0)
            r["customer"] = await db.Customers.IgnoreQueryFilters().Where(c => ids["customer"].Contains(c.Id)).ToDictionaryAsync(c => c.Id.ToString(), c => c.Name);
        if (ids["contact"].Count > 0)
            r["contact"] = await db.Contacts.IgnoreQueryFilters().Where(c => ids["contact"].Contains(c.Id)).ToDictionaryAsync(c => c.Id.ToString(), c => c.FullName);
        return r;
    }
}

/// <summary>Khối "Lịch sử thay đổi": @await Component.InvokeAsync("AuditTrail", new { type = "Customer", id = c.Id })</summary>
public class AuditTrailViewComponent : ViewComponent
{
    private readonly CrmDbContext _db;
    public AuditTrailViewComponent(CrmDbContext db) => _db = db;

    public async Task<IViewComponentResult> InvokeAsync(string type, int id, int take = 30)
    {
        var q = _db.AuditLogs.AsNoTracking().Where(a => a.EntityType == type && a.EntityId == id);
        var items = await q.OrderByDescending(a => a.Id).Take(take).ToListAsync();
        return View(new AuditTrailModel
        {
            Type = type, Id = id, Items = items, Total = items.Count < take ? items.Count : await q.CountAsync(),
            Lookups = await AuditLookups.LoadAsync(_db, items)
        });
    }
}
