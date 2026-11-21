using System.Security.Claims;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using static CRM.Web.Infrastructure.I18n;

namespace CRM.Web.Infrastructure.Reports;

/// <summary>Quyền hiệu lực của một người dùng với một báo cáo</summary>
public record ReportGrant(bool CanView, bool CanExport, ReportDataScope Scope, int[] BranchIds, string Source, string? DenyReason = null, bool IsDefault = false)
{
    /// <summary>Lọc người phụ trách = người xem: true / false theo vùng được gán; null = mặc định theo vai trò (giữ logic sẵn có của từng báo cáo)</summary>
    public bool? OwnOnly => IsDefault ? null : Scope == ReportDataScope.Own;
}

/// <summary>Người được kiểm tra quyền (người đang đăng nhập hoặc người dùng chọn trên trang phân quyền)</summary>
public record ReportSubject(int UserId, string Role, int BranchId);

/// <summary>
/// Phân quyền báo cáo theo vai trò / người dùng kèm vùng dữ liệu, kết hợp với:
///  • quyền chức năng "Báo cáo" (Xem / Xuất Excel) trong ma trận phân quyền,
///  • vai trò được chạy của báo cáo động (AllowedRoles),
///  • phạm vi đơn vị: vùng dữ liệu của báo cáo thay cho phạm vi mặc định trong lúc chạy báo cáo (BranchScope.Override).
/// Không có quy tắc: quản trị = toàn hệ thống; quản lý = đơn vị mình và cấp dưới; nhân viên = dữ liệu mình phụ trách.
/// </summary>
public class ReportAccessService
{
    private readonly CrmDbContext _db;
    private readonly PermissionService _perms;
    private readonly BranchScope _scope;
    private List<ReportAccess>? _rules;

    public ReportAccessService(CrmDbContext db, PermissionService perms, BranchScope scope) { _db = db; _perms = perms; _scope = scope; }

    public static string StdKey(string key) => "std:" + key;
    public static string CustomKey(int id) => "custom:" + id;

    public static ReportSubject SubjectOf(ClaimsPrincipal user, BranchScope scope) =>
        new(user.GetUserId(), user.IsInRole(AppRoles.Admin) ? AppRoles.Admin : user.IsInRole(AppRoles.Manager) ? AppRoles.Manager
            : user.IsInRole(AppRoles.Support) ? AppRoles.Support : AppRoles.Sales, scope.UserBranchId ?? 0);

    private async Task<List<ReportAccess>> RulesAsync() => _rules ??= await _db.ReportAccesses.AsNoTracking().ToListAsync();

    public static ReportDataScope DefaultScope(string role) =>
        role == AppRoles.Admin ? ReportDataScope.All : role == AppRoles.Manager ? ReportDataScope.UnitTree : ReportDataScope.Own;

    /// <summary>Quyền hiệu lực của người dùng với báo cáo (chưa xét vai trò của báo cáo động – xem ResolveCustomAsync)</summary>
    public async Task<ReportGrant> ResolveAsync(string reportKey, ReportSubject s)
    {
        var moduleView = _perms.RoleCan(s.Role, "Reports.View");
        var moduleExport = _perms.RoleCan(s.Role, "Reports.Export");
        if (s.Role == AppRoles.Admin) return new(true, true, ReportDataScope.All, Array.Empty<int>(), T("Quản trị hệ thống"));
        if (!moduleView) return new(false, false, ReportDataScope.Own, Array.Empty<int>(), T("Phân quyền chức năng"), T("Vai trò không có quyền xem chức năng Báo cáo."));

        var rules = await RulesAsync();
        var uid = s.UserId.ToString();
        bool Is(ReportAccess r, string key, ReportAccessSubject t) =>
            r.ReportKey == key && r.SubjectType == t && (t == ReportAccessSubject.User ? r.Subject == uid : r.Subject == s.Role);
        // Quy tắc riêng của người dùng luôn ưu tiên hơn quy tắc theo vai trò; trong cùng loại, báo cáo cụ thể ưu tiên hơn "mọi báo cáo"
        var rule = rules.FirstOrDefault(r => Is(r, reportKey, ReportAccessSubject.User))
                   ?? rules.FirstOrDefault(r => Is(r, ReportAccess.AllReports, ReportAccessSubject.User))
                   ?? rules.FirstOrDefault(r => Is(r, reportKey, ReportAccessSubject.Role))
                   ?? rules.FirstOrDefault(r => Is(r, ReportAccess.AllReports, ReportAccessSubject.Role));

        // Không có quy tắc khớp: quyền mặc định theo vai trò (quy tắc của người khác không ngầm khoá báo cáo – muốn chặn thì thêm quy tắc "không được xem")
        if (rule == null)
            return new(true, moduleExport, DefaultScope(s.Role), Array.Empty<int>(), T("Mặc định theo vai trò"), IsDefault: true);
        var source = (rule.ReportKey == ReportAccess.AllReports ? T("Mọi báo cáo") : T("Báo cáo này")) + " · "
                     + (rule.SubjectType == ReportAccessSubject.User ? T("riêng người dùng") : T("vai trò {0}", RoleLabel(rule.Subject)));
        if (!rule.CanView) return new(false, false, rule.Scope, Array.Empty<int>(), source, T("Không được phân quyền xem báo cáo này."));
        var ids = ParseIds(rule.BranchIds);
        return new(true, rule.CanExport && moduleExport, rule.Scope, ids, source);
    }

    /// <summary>Báo cáo động: thêm điều kiện "vai trò được xem" của báo cáo (trừ khi được cấp riêng cho người dùng)</summary>
    public async Task<ReportGrant> ResolveCustomAsync(CustomReport rep, ReportSubject s)
    {
        var g = await ResolveAsync(CustomKey(rep.Id), s);
        if (!g.CanView || s.Role == AppRoles.Admin) return g;
        if (!rep.IsActive) return g with { CanView = false, CanExport = false, DenyReason = T("Báo cáo đang ngừng sử dụng.") };
        var byUser = (await RulesAsync()).Any(r => r.ReportKey == CustomKey(rep.Id) && r.SubjectType == ReportAccessSubject.User && r.Subject == s.UserId.ToString() && r.CanView);
        var roleOk = rep.AllowedRoles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(s.Role);
        return byUser || roleOk ? g : g with { CanView = false, CanExport = false, DenyReason = T("Vai trò không nằm trong danh sách được xem của báo cáo.") };
    }

    public static int[] ParseIds(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => int.TryParse(x, out var i) ? i : 0).Where(i => i > 0).Distinct().ToArray();

    /// <summary>Danh sách đơn vị của vùng dữ liệu (null = toàn hệ thống)</summary>
    public static List<int>? BranchesOf(ReportGrant g, ReportSubject s, BranchSnapshot snap) => g.Scope switch
    {
        ReportDataScope.All => null,
        ReportDataScope.Unit => new List<int> { s.BranchId },
        ReportDataScope.Specific => g.BranchIds.SelectMany(snap.Subtree).Distinct().ToList(),
        ReportDataScope.UnitTree => snap.Subtree(s.BranchId),
        // Chỉ dữ liệu mình phụ trách: lọc theo người phụ trách, không giới hạn thêm theo đơn vị
        _ => s.Role == AppRoles.Admin ? null : snap.Subtree(snap.RootId)
    };

    /// <summary>Áp vùng dữ liệu cho request hiện tại; trả về mô tả để hiện trên báo cáo</summary>
    public string Apply(ReportGrant g, ClaimsPrincipal user)
    {
        var s = SubjectOf(user, _scope);
        var snap = _scope.Snapshot;
        if (!g.IsDefault) _scope.Override(BranchesOf(g, s, snap));   // mặc định: giữ phạm vi đơn vị sẵn có
        var text = Describe(g, s, snap);
        if (_scope.Selected is int sel && g.Scope != ReportDataScope.Own) text += " · " + T("đang lọc theo đơn vị chọn trên thanh trên cùng: {0}", snap.NameOf(sel));
        return text;
    }

    public static string Describe(ReportGrant g, ReportSubject s, BranchSnapshot snap)
    {
        var scope = g.Scope.GetDisplayName();
        var units = g.Scope switch
        {
            ReportDataScope.Unit or ReportDataScope.UnitTree => snap.NameOf(s.BranchId),
            ReportDataScope.Specific => g.BranchIds.Length == 0 ? T("(chưa chọn đơn vị)") : string.Join(", ", g.BranchIds.Select(snap.NameOf)),
            _ => null
        };
        return units == null ? scope : $"{scope}: {units}";
    }

    public static string RoleLabel(string role) => AppRoles.DisplayName(role);
}
