using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Infrastructure.Reports;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace CRM.Web.Controllers;

/// <summary>Phiên nhập dữ liệu (giữ trong bộ nhớ 1 giờ giữa các bước)</summary>
public class ImportSession
{
    public string Key { get; init; } = Guid.NewGuid().ToString("N");
    public int UserId { get; init; }
    public string Type { get; init; } = "";
    public string FileName { get; init; } = "";
    public List<string[]> Data { get; init; } = new();
    public string?[] Mapping { get; set; } = Array.Empty<string?>();
    public string Mode { get; set; } = "skip";
    public ImportPlan? Plan { get; set; }
}

/// <summary>Nhập dữ liệu từ Excel (.xlsx) / CSV: khách hàng, người liên hệ, lead, hàng hoá – có bước khớp cột, kiểm tra trùng và chạy thử.</summary>
public class ImportController : CrmControllerBase
{
    private readonly IMemoryCache _cache;
    private readonly ImportService _import;
    private readonly PermissionService _perms;

    public ImportController(CrmDbContext db, IMemoryCache cache, ImportService import, PermissionService perms) : base(db)
    {
        _cache = cache; _import = import; _perms = perms;
    }

    /// <summary>Loại dữ liệu người dùng được nhập (cần quyền thêm mới của chức năng tương ứng)</summary>
    private bool CanImport(string type) => type switch
    {
        nameof(Customer) or nameof(Contact) => _perms.Can(User, "Customers.Create"),
        nameof(Lead) => _perms.Can(User, "Leads.Create"),
        nameof(Product) => User.SeesAll() && _perms.Can(User, "Products.Create"),
        _ => false
    };

    private ImportSession? Session(string key) =>
        _cache.TryGetValue("import:" + key, out ImportSession? s) && s!.UserId == CurrentUserId ? s : null;

    private IActionResult Expired()
    {
        TempData["Error"] = I18n.T("Phiên nhập dữ liệu đã hết hạn, vui lòng tải file lại.");
        return RedirectToAction(nameof(Index));
    }

    public IActionResult Index(string? type)
    {
        ViewBag.Types = ImportService.Definitions.Where(d => CanImport(d.Type)).ToList();
        ViewBag.Type = type;
        return View();
    }

    /// <summary>Tải file mẫu .xlsx</summary>
    public IActionResult Template(string type)
    {
        var def = ImportService.Def(type);
        if (def == null || !CanImport(type)) return NotFound();
        return File(XlsxWriter.Write(I18n.T("Mẫu nhập {0}", I18n.T(def.Label)), null, ImportService.Template(def)),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"mau-nhap-{type.ToLowerInvariant()}.xlsx");
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(10_000_000)]
    public IActionResult Upload(string type, IFormFile? file)
    {
        var def = ImportService.Def(type);
        if (def == null || !CanImport(type)) return NotAllowed();
        if (file == null || file.Length == 0) { TempData["Error"] = I18n.T("Vui lòng chọn file Excel (.xlsx) hoặc CSV."); return RedirectToAction(nameof(Index), new { type }); }
        if (file.Length > 5_000_000) { TempData["Error"] = I18n.T("File quá lớn (tối đa 5 MB)."); return RedirectToAction(nameof(Index), new { type }); }
        List<string[]> data;
        try
        {
            using var s = file.OpenReadStream();
            data = XlsxReader.Read(s, file.FileName, ImportService.MaxRows + 1);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or IOException)
        {
            TempData["Error"] = I18n.T("Không đọc được file: {0}", ex.Message);
            return RedirectToAction(nameof(Index), new { type });
        }
        // Bỏ các dòng tiêu đề phụ của file mẫu (dòng tiêu đề báo cáo) – lấy dòng đầu tiên có từ 2 ô trở lên làm tiêu đề cột
        var headerAt = data.FindIndex(r => r.Count(c => !string.IsNullOrWhiteSpace(c)) >= 2);
        if (headerAt < 0 || data.Count - headerAt < 2) { TempData["Error"] = I18n.T("File không có dữ liệu (cần dòng tiêu đề và ít nhất một dòng dữ liệu)."); return RedirectToAction(nameof(Index), new { type }); }
        data = data.Skip(headerAt).ToList();
        if (data.Count - 1 > ImportService.MaxRows) { TempData["Error"] = I18n.T("File có quá {0} dòng – vui lòng chia nhỏ.", ImportService.MaxRows); return RedirectToAction(nameof(Index), new { type }); }
        var headers = data[0].Select(h => (h ?? "").Replace(" *", "").Trim()).ToArray();
        data[0] = headers;
        var session = new ImportSession { UserId = CurrentUserId, Type = type, FileName = file.FileName, Data = data, Mapping = ImportService.AutoMap(def, headers) };
        _cache.Set("import:" + session.Key, session, TimeSpan.FromHours(1));
        return RedirectToAction(nameof(Map), new { key = session.Key });
    }

    public IActionResult Map(string key)
    {
        var s = Session(key);
        if (s == null) return Expired();
        ViewBag.Def = ImportService.Def(s.Type);
        return View(s);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Map(string key, string?[] mapping, string mode)
    {
        var s = Session(key);
        if (s == null) return Expired();
        var def = ImportService.Def(s.Type)!;
        var cols = s.Data[0].Length;
        var map = Enumerable.Range(0, cols).Select(i => i < mapping.Length && def.Fields.Any(f => f.Key == mapping[i]) ? mapping[i] : null).ToArray();
        // Một trường chỉ gắn với một cột
        var dupField = map.Where(m => m != null).GroupBy(m => m).FirstOrDefault(g => g.Count() > 1);
        var missing = def.Fields.Where(f => f.Required && !map.Contains(f.Key)).Select(f => I18n.T(f.Label)).ToList();
        s.Mapping = map;
        s.Mode = mode is "update" or "create" ? mode : "skip";
        if (dupField != null || missing.Any())
        {
            TempData["Error"] = dupField != null
                ? I18n.T("Trường \"{0}\" đang gắn với nhiều cột.", I18n.T(def.Fields.First(f => f.Key == dupField.Key).Label))
                : I18n.T("Chưa chọn cột cho trường bắt buộc: {0}.", string.Join(", ", missing));
            return RedirectToAction(nameof(Map), new { key });
        }
        s.Plan = await _import.AnalyzeAsync(s.Type, s.Data, map, s.Mode, User);
        return RedirectToAction(nameof(Preview), new { key });
    }

    public IActionResult Preview(string key)
    {
        var s = Session(key);
        if (s?.Plan == null) return Expired();
        ViewBag.Def = ImportService.Def(s.Type);
        return View(s);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Execute(string key)
    {
        var s = Session(key);
        if (s?.Plan == null) return Expired();
        if (!CanImport(s.Type)) return NotAllowed();
        var (created, updated) = await _import.ExecuteAsync(s.Plan, User, _perms.Can(User, "Field.Cost"));
        _cache.Remove("import:" + key);
        // Lead mới không ghi người phụ trách → tự phân bổ theo cấu hình (quản lý nhập)
        if (s.Type == nameof(Lead) && User.SeesAll())
        {
            var assign = HttpContext.RequestServices.GetRequiredService<LeadAssignService>();
            var leads = s.Plan.Rows.Where(r => r.Action == "Create" && r.NoOwner && r.Entity is Lead).Select(r => (Lead)r.Entity!).ToList();
            if (leads.Count > 0 && await assign.AssignAsync(leads, CurrentUserId) > 0)
            {
                await Db.SaveChangesAsync();
                assign.NotifyAssigned(leads, CurrentUserId);
                await Db.SaveChangesAsync();
            }
            await LeadScoring.RecalculateAsync(Db, s.Plan.Rows.Where(r => r.Entity is Lead).Select(r => ((Lead)r.Entity!).Id).ToList());
        }
        TempData["Success"] = I18n.T("Đã nhập {0}: {1} tạo mới, {2} cập nhật, {3} bỏ qua, {4} dòng lỗi.",
            I18n.T(ImportService.Def(s.Type)!.Label).ToLowerInvariant(), created, updated, s.Plan.Count("Skip"), s.Plan.Count("Error"));
        return s.Type switch
        {
            nameof(Customer) => RedirectToAction("Index", "Customers"),
            nameof(Contact) => RedirectToAction("Index", "Contacts"),
            nameof(Lead) => RedirectToAction("Index", "Leads"),
            _ => RedirectToAction("Index", "Products")
        };
    }
}
