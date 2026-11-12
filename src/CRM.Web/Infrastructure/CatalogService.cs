using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CRM.Web.Infrastructure;

/// <summary>
/// Tra cứu danh mục dùng chung (có cache). Dùng trong form (dropdown) và khi hiển thị
/// giá trị đã lưu theo ngôn ngữ đang chọn.
/// </summary>
public class CatalogService
{
    private const string CacheKey = "catalog-items";
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopes;

    public CatalogService(IMemoryCache cache, IServiceScopeFactory scopes)
    {
        _cache = cache;
        _scopes = scopes;
    }

    private IReadOnlyList<CatalogItem> All() =>
        _cache.GetOrCreate(CacheKey, e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
            return (IReadOnlyList<CatalogItem>)db.CatalogItems.AsNoTracking()
                .OrderBy(x => x.Group).ThenBy(x => x.SortOrder).ThenBy(x => x.Name).ToList();
        })!;

    /// <summary>Gọi sau khi thêm / sửa / xóa danh mục.</summary>
    public void Invalidate() => _cache.Remove(CacheKey);

    public IEnumerable<CatalogItem> Items(CatalogGroup group, bool activeOnly = true) =>
        All().Where(x => x.Group == group && (!activeOnly || x.IsActive));

    /// <summary>Tên hiển thị theo ngôn ngữ cho giá trị đã lưu (giá trị lưu là tên tiếng Việt).</summary>
    public string Text(CatalogGroup group, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var item = All().FirstOrDefault(x => x.Group == group && string.Equals(x.Name, value, StringComparison.OrdinalIgnoreCase));
        return item?.DisplayName ?? value;
    }

    /// <summary>
    /// Danh sách chọn: value = tên tiếng Việt (giá trị lưu), text = tên theo ngôn ngữ.
    /// Giá trị hiện tại không còn trong danh mục (đã ngừng / dữ liệu cũ) vẫn được giữ để không mất dữ liệu khi lưu.
    /// </summary>
    public List<SelectListItem> Options(CatalogGroup group, string? current)
    {
        var list = Items(group)
            .Select(x => new SelectListItem(x.DisplayName, x.Name, string.Equals(x.Name, current, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (!string.IsNullOrWhiteSpace(current) && !list.Any(x => x.Selected))
            list.Insert(0, new SelectListItem(Text(group, current), current, true));
        return list;
    }
}
