using System.Globalization;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>Nhãn và trường tùy biến cho khách hàng / người liên hệ / lead / cơ hội</summary>
public static class CustomData
{
    public static readonly string[] Types = { nameof(Customer), nameof(Contact), nameof(Lead), nameof(Opportunity) };

    // ---------- Nhãn ----------

    /// <summary>Nhãn của nhiều bản ghi cùng loại: entityId → danh sách nhãn</summary>
    public static async Task<Dictionary<int, List<Tag>>> TagsOfAsync(CrmDbContext db, string type, IEnumerable<int> ids)
    {
        var list = ids.ToList();
        if (list.Count == 0) return new();
        return (await db.EntityTags.Include(t => t.Tag).AsNoTracking().Where(t => t.EntityType == type && list.Contains(t.EntityId)).ToListAsync())
            .GroupBy(t => t.EntityId).ToDictionary(g => g.Key, g => g.Select(t => t.Tag!).OrderBy(t => t.Name).ToList());
    }

    /// <summary>Lọc danh sách theo nhãn</summary>
    public static IQueryable<T> WithTag<T>(this IQueryable<T> q, CrmDbContext db, int? tagId) where T : class
    {
        if (tagId is not int tid) return q;
        var type = typeof(T).Name;
        return q.Where(x => db.EntityTags.Any(t => t.EntityType == type && t.TagId == tid && t.EntityId == EF.Property<int>(x, "Id")));
    }

    // ---------- Trường tùy biến ----------

    public static Task<List<CustomFieldDef>> FieldsAsync(CrmDbContext db, string type, bool listOnly = false) =>
        db.CustomFieldDefs.AsNoTracking().Where(f => f.EntityType == type && f.IsActive && (!listOnly || f.ShowInList))
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Id).ToListAsync();

    public static async Task<Dictionary<int, Dictionary<int, string?>>> ValuesAsync(CrmDbContext db, string type, IEnumerable<int> ids)
    {
        var list = ids.ToList();
        if (list.Count == 0) return new();
        return (await db.CustomFieldValues.AsNoTracking().Where(v => v.Field!.EntityType == type && list.Contains(v.EntityId)).ToListAsync())
            .GroupBy(v => v.EntityId).ToDictionary(g => g.Key, g => g.ToDictionary(v => v.FieldId, v => v.Value));
    }

    /// <summary>Tên ô nhập trên form</summary>
    public static string InputName(CustomFieldDef f) => $"cf_{f.Id}";

    /// <summary>Đọc và kiểm tra giá trị trường tùy biến từ form; trả về lỗi (tên trường → thông báo)</summary>
    public static async Task<(Dictionary<int, string?> Values, List<string> Errors)> ReadAsync(CrmDbContext db, string type, IFormCollection form)
    {
        var values = new Dictionary<int, string?>();
        var errors = new List<string>();
        foreach (var f in await FieldsAsync(db, type))
        {
            var raw = form[InputName(f)].ToString().Trim();
            if (f.Type == CustomFieldType.Checkbox) raw = raw is "true" or "on" ? "true" : "";
            if (raw.Length == 0)
            {
                if (f.Required && f.Type != CustomFieldType.Checkbox) errors.Add(I18n.T("Vui lòng nhập \"{0}\"", f.Label));
                values[f.Id] = null;
                continue;
            }
            switch (f.Type)
            {
                case CustomFieldType.Number when !decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out _):
                    errors.Add(I18n.T("\"{0}\" phải là số", f.Label)); break;
                case CustomFieldType.Date when !DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _):
                    errors.Add(I18n.T("\"{0}\" không phải ngày hợp lệ", f.Label)); break;
                case CustomFieldType.Select when !f.OptionList.Contains(raw):
                    errors.Add(I18n.T("\"{0}\" có giá trị không nằm trong danh sách", f.Label)); break;
            }
            values[f.Id] = raw.Length > 1000 ? raw[..1000] : raw;
        }
        return (values, errors);
    }

    /// <summary>Lưu giá trị trường tùy biến của một bản ghi (gọi sau khi bản ghi đã có Id)</summary>
    public static async Task SaveAsync(CrmDbContext db, int entityId, Dictionary<int, string?> values)
    {
        if (values.Count == 0) return;
        var ids = values.Keys.ToList();
        var existing = await db.CustomFieldValues.Where(v => v.EntityId == entityId && ids.Contains(v.FieldId)).ToListAsync();
        foreach (var (fid, val) in values)
        {
            var row = existing.FirstOrDefault(v => v.FieldId == fid);
            if (string.IsNullOrEmpty(val)) { if (row != null) db.CustomFieldValues.Remove(row); continue; }
            if (row == null) db.CustomFieldValues.Add(new CustomFieldValue { FieldId = fid, EntityId = entityId, Value = val });
            else row.Value = val;
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Hiển thị giá trị theo kiểu</summary>
    public static string Display(CustomFieldDef f, string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        return f.Type switch
        {
            CustomFieldType.Checkbox => v == "true" ? I18n.T("Có") : "",
            CustomFieldType.Date => DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToDate() : v,
            CustomFieldType.Number => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n.ToString("#,##0.##", CultureInfo.GetCultureInfo("vi-VN")) : v,
            _ => v
        };
    }
}
