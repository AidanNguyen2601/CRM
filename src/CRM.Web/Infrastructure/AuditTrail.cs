using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CRM.Web.Infrastructure;

/// <summary>Một thay đổi của một trường: f = tên thuộc tính, o = giá trị cũ, n = giá trị mới</summary>
public record AuditChange(string f, string? o, string? n);

/// <summary>
/// Nhật ký thay đổi dữ liệu: thu thập thêm / sửa / xóa của các loại bản ghi nghiệp vụ trong SaveChanges
/// (chỉ các trường thực sự đổi giá trị), ghi kèm người thực hiện, IP, thời điểm.
/// </summary>
public static class AuditTrail
{
    /// <summary>Loại bản ghi được ghi nhật ký → tên hiển thị</summary>
    public static readonly Dictionary<string, string> Types = new()
    {
        [nameof(Customer)] = "Khách hàng", [nameof(Contact)] = "Người liên hệ", [nameof(Lead)] = "Lead",
        [nameof(Opportunity)] = "Cơ hội", [nameof(Activity)] = "Hoạt động / công việc", [nameof(Quote)] = "Báo giá",
        [nameof(BusinessPlan)] = "Phương án kinh doanh", [nameof(Contract)] = "Hợp đồng", [nameof(Invoice)] = "Hoá đơn",
        [nameof(Payment)] = "Phiếu thu", [nameof(Product)] = "Hàng hoá / dịch vụ", [nameof(Campaign)] = "Chiến dịch tiếp thị",
        [nameof(Ticket)] = "Phiếu hỗ trợ", [nameof(SalesPlan)] = "Kế hoạch bán hàng", [nameof(AppUser)] = "Người dùng",
        [nameof(Branch)] = "Công ty & chi nhánh", [nameof(CatalogItem)] = "Danh mục", [nameof(EmailTemplate)] = "Mẫu email",
        [nameof(SalesTarget)] = "Chỉ tiêu KPI", ["Security"] = "Bảo mật", ["Permission"] = "Phân quyền", ["Settings"] = "Cấu hình"
    };

    /// <summary>Trường không ghi nhật ký (kỹ thuật / nhạy cảm)</summary>
    private static readonly HashSet<string> Ignored = new()
    {
        "Id", "CreatedAt", "UpdatedAt", "PasswordHash", "UiPrefs", "LastLoginAt", "FailedLoginCount", "LockoutUntil",
        "TwoFactorSecret", "PasswordChangedAt", "BranchId", "PortalPasswordHash", "PortalLastLoginAt", "Score",
        "SlaPausedAt", "SlaPausedMinutes", "HealthScore", "HealthUpdatedAt", "Submissions", "LastSubmissionAt"
    };

    public static string TypeLabel(string type) => Types.TryGetValue(type, out var t) ? I18n.T(t) : type;

    /// <summary>Tên trường theo [Display(Name)] của thuộc tính (đã dịch theo ngôn ngữ giao diện)</summary>
    public static string FieldLabel(string type, string field)
    {
        if (field == "SharedWith") return I18n.T("Chia sẻ với");
        if (field == "MergedFrom") return I18n.T("Gộp từ");
        var t = typeof(Customer).Assembly.GetType("CRM.Web.Models.Entities." + type);
        var name = t?.GetProperty(field)?.GetCustomAttribute<DisplayAttribute>()?.Name;
        return name != null ? I18n.T(name) : field;
    }

    public sealed class Pending
    {
        public EntityEntry Entry = null!;
        public string Type = "";
        public string Action = "";
        public string? Title;
        public List<AuditChange> Changes = new();
    }

    public static List<Pending> Collect(DbContext db)
    {
        var list = new List<Pending>();
        foreach (var e in db.ChangeTracker.Entries())
        {
            if (e.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            var type = e.Metadata.ClrType.Name;
            if (!Types.ContainsKey(type)) continue;
            var p = new Pending { Entry = e, Type = type, Title = TitleOf(e.Entity) };
            foreach (var prop in e.Properties)
            {
                var name = prop.Metadata.Name;
                if (Ignored.Contains(name) || prop.Metadata.IsShadowProperty()) continue;
                switch (e.State)
                {
                    case EntityState.Added:
                        var nv = Format(prop.CurrentValue);
                        if (!string.IsNullOrEmpty(nv) && nv != "0" && nv != Format(false)) p.Changes.Add(new AuditChange(name, null, nv));
                        break;
                    case EntityState.Modified:
                        if (!prop.IsModified) continue;
                        var o = Format(prop.OriginalValue); var n = Format(prop.CurrentValue);
                        if (o != n) p.Changes.Add(new AuditChange(name, o, n));
                        break;
                }
            }
            if (e.State == EntityState.Modified && p.Changes.Count == 0) continue;
            p.Action = e.State switch { EntityState.Added => "Create", EntityState.Deleted => "Delete", _ => "Update" };
            if (e.State == EntityState.Deleted) p.Changes.Clear();
            list.Add(p);
        }
        return list;
    }

    public static IEnumerable<AuditLog> Build(List<Pending> pending, (int? Id, string? Name, string? Ip) actor) =>
        pending.Select(p => new AuditLog
        {
            EntityType = p.Type, EntityId = IdOf(p.Entry), Action = p.Action, Title = Cut(p.Title, 300),
            Changes = p.Changes.Count == 0 ? null : JsonSerializer.Serialize(p.Changes),
            UserId = actor.Id, UserName = Cut(actor.Name ?? I18n.T("Hệ thống"), 150), Ip = Cut(actor.Ip, 50), At = DateTime.Now
        });

    /// <summary>Ghi một dòng nhật ký tự do (gộp trùng, nhập Excel, đổi quyền...) – lưu cùng SaveChanges của người gọi</summary>
    public static void Write(Data.CrmDbContext db, string type, int id, string action, string? title, IEnumerable<AuditChange>? changes = null)
    {
        var actor = db.Actor;
        var ch = changes?.ToList();
        db.AuditLogs.Add(new AuditLog
        {
            EntityType = type, EntityId = id, Action = action, Title = Cut(title, 300),
            Changes = ch is { Count: > 0 } ? JsonSerializer.Serialize(ch) : null,
            UserId = actor.Id, UserName = Cut(actor.Name ?? I18n.T("Hệ thống"), 150), Ip = Cut(actor.Ip, 50), At = DateTime.Now
        });
    }

    public static List<AuditChange> Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new();
        try { return JsonSerializer.Deserialize<List<AuditChange>>(json) ?? new(); } catch { return new(); }
    }

    public static string ActionLabel(string action) => I18n.T(action switch
    {
        "Create" => "Tạo mới", "Update" => "Sửa", "Delete" => "Xóa", "Merge" => "Gộp trùng", "Import" => "Nhập Excel",
        "Login" => "Đăng nhập", "Unlock" => "Mở khóa", "Reset" => "Đặt lại", _ => action
    });

    public static string ActionCss(string action) => action switch
    {
        "Create" or "Import" => "bg-success", "Delete" => "bg-danger", "Merge" => "bg-warning text-dark", _ => "bg-primary"
    };

    private static int IdOf(EntityEntry e) =>
        e.Metadata.FindPrimaryKey()?.Properties.Count == 1 && e.Property(e.Metadata.FindPrimaryKey()!.Properties[0].Name).CurrentValue is int i ? i : 0;

    private static string? TitleOf(object entity)
    {
        string? P(string n) => entity.GetType().GetProperty(n)?.GetValue(entity)?.ToString();
        var number = P("Number"); var code = P("Code"); var name = P("Name") ?? P("Title") ?? P("Subject") ?? P("FullName") ?? P("Username");
        if (number != null) return name != null ? $"{number} – {name}" : number;
        if (code != null && name != null && entity is not CatalogItem) return $"{code} – {name}";
        return name ?? code;
    }

    private static string? Format(object? v) => v switch
    {
        null => "",
        string s => Cut(s, 500),
        Enum en => en.GetDisplayName(),
        bool b => b ? I18n.T("Có") : I18n.T("Không"),
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("dd/MM/yyyy") : d.ToString("dd/MM/yyyy HH:mm"),
        decimal m => m.ToString("#,##0.##", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Cut(v.ToString(), 500)
    };

    private static string? Cut(string? s, int max) => s == null ? null : s.Length > max ? s[..(max - 1)] + "…" : s;
}
