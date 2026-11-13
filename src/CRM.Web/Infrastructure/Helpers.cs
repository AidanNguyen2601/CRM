using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using CRM.Web.Models.Entities;

namespace CRM.Web.Infrastructure;

public static class FormatExtensions
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    /// <summary>Định dạng tiền VNĐ: 15.000.000</summary>
    public static string ToMoney(this decimal value) => value.ToString("N0", Vi);

    /// <summary>Rút gọn: 1,2 tỷ / 350 tr</summary>
    public static string ToShortMoney(this decimal value) =>
        value >= 1_000_000_000 ? (value / 1_000_000_000m).ToString("0.##", Vi) + " tỷ"
        : value >= 1_000_000 ? (value / 1_000_000m).ToString("0.#", Vi) + " tr"
        : value.ToString("N0", Vi);

    public static string ToDate(this DateTime value) => value.ToString("dd/MM/yyyy");
    public static string ToDate(this DateTime? value) => value?.ToString("dd/MM/yyyy") ?? "";
    public static string ToDateTime(this DateTime value) => value.ToString("dd/MM/yyyy HH:mm");

    public static string GetDisplayName(this Enum value)
    {
        var member = value.GetType().GetMember(value.ToString()).FirstOrDefault();
        return I18n.T(member?.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? value.ToString());
    }
}

public static class ClaimsExtensions
{
    public const string FullNameClaim = "FullName";

    public static int GetUserId(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

    public static string GetFullName(this ClaimsPrincipal user) =>
        user.FindFirstValue(FullNameClaim) ?? user.Identity?.Name ?? "";

    /// <summary>Admin/Manager thấy toàn bộ dữ liệu; Sales chỉ thấy bản ghi mình phụ trách.</summary>
    public static bool SeesAll(this ClaimsPrincipal user) =>
        user.IsInRole(AppRoles.Admin) || user.IsInRole(AppRoles.Manager);
}

public static class QueryExtensions
{
    /// <summary>Lọc dữ liệu theo quyền: Sales chỉ thấy bản ghi mình phụ trách.</summary>
    public static IQueryable<T> VisibleTo<T>(this IQueryable<T> q, ClaimsPrincipal user) where T : class, IOwned
    {
        if (user.SeesAll()) return q;
        var uid = user.GetUserId();
        return q.Where(x => x.OwnerId == uid);
    }

    public static bool CanAccess(this ClaimsPrincipal user, IOwned item) =>
        user.SeesAll() || item.OwnerId == user.GetUserId();
}

public static class Paging
{
    /// <summary>Các lựa chọn số dòng mỗi trang</summary>
    public static readonly int[] PageSizes = { 10, 15, 25, 50, 100 };
    public const int DefaultPageSize = 15;

    /// <summary>Giới hạn số dòng mỗi trang trong khoảng cho phép</summary>
    public static int Clamp(int size) => size <= 0 ? DefaultPageSize : Math.Min(size, PageSizes[^1]);
}

/// <summary>Thông tin phân trang dùng cho partial _Pager</summary>
public interface IPagedList
{
    int Page { get; }
    int PageSize { get; }
    int TotalCount { get; }
    int TotalPages { get; }
}

public class PagedList<T> : IPagedList
{

    public IReadOnlyList<T> Items { get; }
    public int Page { get; }
    public int PageSize { get; }
    public int TotalCount { get; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);

    public PagedList(IReadOnlyList<T> items, int totalCount, int page, int pageSize)
    {
        Items = items; TotalCount = totalCount; Page = page; PageSize = pageSize;
    }
}
