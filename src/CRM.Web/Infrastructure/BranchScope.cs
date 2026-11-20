using System.Security.Claims;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>Ảnh chụp cơ cấu tổ chức (đã cache): danh sách đơn vị, cây cha – con, đơn vị của từng người dùng.</summary>
public sealed class BranchSnapshot
{
    public IReadOnlyList<Branch> Branches { get; }
    public IReadOnlyDictionary<int, Branch> ById { get; }
    public IReadOnlyDictionary<int, int> UserBranch { get; }
    public int RootId { get; }
    private readonly Dictionary<int, List<int>> _children;

    public BranchSnapshot(List<Branch> branches, Dictionary<int, int> userBranch)
    {
        Branches = branches;
        ById = branches.ToDictionary(b => b.Id);
        UserBranch = userBranch;
        _children = branches.Where(b => b.ParentId.HasValue).GroupBy(b => b.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.SortOrder).ThenBy(b => b.Name).Select(b => b.Id).ToList());
        RootId = branches.Where(b => b.ParentId == null).OrderBy(b => b.Id).Select(b => b.Id).FirstOrDefault();
    }

    /// <summary>Đơn vị và toàn bộ đơn vị trực thuộc (đệ quy)</summary>
    public List<int> Subtree(int id)
    {
        var result = new List<int>();
        var stack = new Stack<int>();
        if (ById.ContainsKey(id)) stack.Push(id);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            if (result.Contains(x)) continue;   // phòng dữ liệu vòng lặp
            result.Add(x);
            if (_children.TryGetValue(x, out var ch)) foreach (var c in ch) stack.Push(c);
        }
        return result;
    }

    /// <summary>Danh sách theo cây (cha trước con) kèm độ sâu – dùng để hiển thị thụt lề</summary>
    public List<(Branch Branch, int Depth)> Tree(IEnumerable<int>? only = null)
    {
        var allow = only?.ToHashSet();
        var list = new List<(Branch, int)>();
        void Walk(int id, int depth)
        {
            if (allow == null || allow.Contains(id)) list.Add((ById[id], depth));
            if (_children.TryGetValue(id, out var ch)) foreach (var c in ch) Walk(c, depth + 1);
        }
        foreach (var root in Branches.Where(b => b.ParentId == null || !ById.ContainsKey(b.ParentId.Value)).OrderBy(b => b.SortOrder).ThenBy(b => b.Id))
            Walk(root.Id, 0);
        return list;
    }

    /// <summary>Đơn vị pháp nhân gần nhất (chính nó hoặc cấp trên có loại Công ty) – dùng cho thông tin bên bán, ký hiệu hoá đơn</summary>
    public Branch? LegalEntityOf(int id)
    {
        var seen = new HashSet<int>();
        for (var b = ById.GetValueOrDefault(id); b != null && seen.Add(b.Id); b = b.ParentId.HasValue ? ById.GetValueOrDefault(b.ParentId.Value) : null)
            if (b.Type == BranchType.Company) return b;
        return ById.GetValueOrDefault(id);
    }

    /// <summary>Đường dẫn đầy đủ: Công ty mẹ › Công ty con › Chi nhánh</summary>
    public string PathOf(int id)
    {
        var parts = new List<string>(); var seen = new HashSet<int>();
        for (var b = ById.GetValueOrDefault(id); b != null && seen.Add(b.Id); b = b.ParentId.HasValue ? ById.GetValueOrDefault(b.ParentId.Value) : null)
            parts.Insert(0, b.DisplayName);
        return string.Join(" › ", parts);
    }

    public string NameOf(int id) => ById.TryGetValue(id, out var b) ? b.DisplayName : "";
}

/// <summary>Cache cơ cấu tổ chức dùng chung toàn ứng dụng. Gọi <see cref="Invalidate"/> sau khi sửa đơn vị hoặc đổi đơn vị của người dùng.</summary>
public class BranchDirectory
{
    private readonly IServiceScopeFactory _scopes;
    private volatile BranchSnapshot? _snapshot;
    private readonly object _lock = new();

    public BranchDirectory(IServiceScopeFactory scopes) => _scopes = scopes;

    public BranchSnapshot Get()
    {
        var s = _snapshot;
        if (s != null) return s;
        lock (_lock)
        {
            if (_snapshot != null) return _snapshot;
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
            var branches = db.Branches.AsNoTracking().ToList();
            var users = db.Users.AsNoTracking().Select(u => new { u.Id, u.BranchId }).ToDictionary(x => x.Id, x => x.BranchId);
            return _snapshot = new BranchSnapshot(branches, users);
        }
    }

    public void Invalidate() => _snapshot = null;
}

/// <summary>
/// Phạm vi đơn vị của người dùng hiện tại (theo request):
/// Admin – cả chuỗi; người dùng khác – đơn vị mình và các đơn vị trực thuộc.
/// Có thể thu hẹp bằng bộ chọn đơn vị (cookie crm.branch). Ngoài request (seed, lệnh nạp dữ liệu) không giới hạn.
/// </summary>
public class BranchScope
{
    public const string CookieName = "crm.branch";
    private readonly IHttpContextAccessor _http;
    private readonly BranchDirectory _dir;
    private bool _ready;
    private bool _active;
    private int[] _ids = Array.Empty<int>();
    private List<int> _allowed = new();
    private int? _selected, _userBranch;

    public BranchScope(IHttpContextAccessor http, BranchDirectory dir) { _http = http; _dir = dir; }

    /// <summary>Có đang giới hạn dữ liệu theo đơn vị không</summary>
    public bool Active { get { Init(); return _active; } }
    /// <summary>Các đơn vị đang xem (khi Active)</summary>
    public int[] Ids { get { Init(); return _ids; } }
    /// <summary>Các đơn vị người dùng được phép xem</summary>
    public IReadOnlyList<int> Allowed { get { Init(); return _allowed; } }
    /// <summary>Đơn vị đang chọn trên bộ chọn (null = tất cả trong phạm vi)</summary>
    public int? Selected { get { Init(); return _selected; } }
    public int? UserBranchId { get { Init(); return _userBranch; } }
    /// <summary>Đơn vị mặc định cho bản ghi mới không có người phụ trách</summary>
    public int? WorkingBranchId => Selected ?? UserBranchId;
    public BranchSnapshot Snapshot => _dir.Get();

    /// <summary>Tắt giới hạn đơn vị trong request hiện tại (thao tác quản trị trên toàn chuỗi: nạp dữ liệu mẫu...)</summary>
    public void Bypass() { Init(); _active = false; }

    /// <summary>Lọc danh sách người dùng theo phạm vi đang xem</summary>
    public IQueryable<AppUser> Users(IQueryable<AppUser> q)
    {
        if (!Active) return q;
        var ids = Ids;
        return q.Where(u => ids.Contains(u.BranchId));
    }

    private void Init()
    {
        if (_ready) return;
        _ready = true;
        var user = _http.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return;
        var snap = _dir.Get();
        if (snap.Branches.Count == 0) return;
        var uid = user.GetUserId();
        if (!snap.UserBranch.TryGetValue(uid, out var ub))
        {
            _dir.Invalidate();   // người dùng vừa tạo
            snap = _dir.Get();
            ub = snap.UserBranch.GetValueOrDefault(uid, snap.RootId);
        }
        _userBranch = ub;
        _allowed = user.IsInRole(AppRoles.Admin) ? snap.Branches.Select(b => b.Id).ToList() : snap.Subtree(ub);

        var view = _allowed;
        if (int.TryParse(_http.HttpContext!.Request.Cookies[CookieName], out var sel) && _allowed.Contains(sel))
        {
            _selected = sel;
            view = snap.Subtree(sel).Where(_allowed.Contains).ToList();
        }
        _ids = view.ToArray();
        _active = _ids.Length < snap.Branches.Count;
    }
}
