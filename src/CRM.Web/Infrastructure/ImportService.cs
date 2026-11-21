using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

public enum ImportFieldKind { Text, Int, Decimal, Date, Bool, Enum, Owner, Customer }

/// <summary>Một trường có thể nhập: khóa = tên thuộc tính, nhãn, các tên cột tương đương để tự khớp</summary>
public record ImportField(string Key, string Label, ImportFieldKind Kind = ImportFieldKind.Text, bool Required = false, int Max = 0, Type? EnumType = null, params string[] Aliases);

public record ImportDefinition(string Type, string Label, string Icon, ImportField[] Fields, string DuplicateHint);

/// <summary>Kết quả phân tích một dòng</summary>
public class ImportRow
{
    public int RowNo { get; init; }
    /// <summary>Create / Update / Skip / Error</summary>
    public string Action { get; set; } = "Create";
    public List<string> Messages { get; } = new();
    public int? MatchId { get; set; }
    public string? MatchTitle { get; set; }
    public Dictionary<string, object?> Values { get; } = new();
    public string Display { get; set; } = "";
    /// <summary>Bản ghi đã tạo / cập nhật (sau khi nhập)</summary>
    public object? Entity { get; set; }
    /// <summary>Không ghi người phụ trách trong file (có thể tự phân bổ)</summary>
    public bool NoOwner { get; set; }
}

public class ImportPlan
{
    public string Type { get; init; } = "";
    public string Mode { get; init; } = "skip";
    public List<ImportRow> Rows { get; } = new();
    public int Count(string action) => Rows.Count(r => r.Action == action);
}

/// <summary>
/// Nhập dữ liệu từ Excel / CSV: khách hàng, người liên hệ, lead, hàng hoá. Phân tích trước (chạy thử) rồi mới ghi.
/// Trùng lặp: khách hàng theo mã / mã số thuế / email / điện thoại; liên hệ & lead theo email / điện thoại; hàng hoá theo mã.
/// Chế độ khi trùng: bỏ qua (mặc định) / cập nhật các ô có giá trị / vẫn tạo mới.
/// </summary>
public class ImportService
{
    public const int MaxRows = 5000;

    public static readonly ImportDefinition[] Definitions =
    {
        new(nameof(Customer), "Khách hàng", "customer", new ImportField[]
        {
            new("Code", "Mã khách hàng", Max: 20, Aliases: new[] { "ma", "ma kh", "ma khach hang", "code", "customer code" }),
            new("Name", "Tên khách hàng", Required: true, Max: 200, Aliases: new[] { "ten", "ten kh", "ten khach hang", "ten cong ty", "cong ty", "name", "company", "customer" }),
            new("Type", "Phân loại", ImportFieldKind.Enum, EnumType: typeof(CustomerType), Aliases: new[] { "loai", "phan loai", "loai khach hang", "type" }),
            new("TaxCode", "Mã số thuế", Max: 20, Aliases: new[] { "mst", "ma so thue", "tax", "tax code", "tax id" }),
            new("Phone", "Điện thoại", Max: 20, Aliases: new[] { "dien thoai", "sdt", "so dien thoai", "phone", "tel", "mobile" }),
            new("Email", "Email", Max: 150, Aliases: new[] { "email", "e-mail", "thu dien tu" }),
            new("Website", "Website", Max: 200, Aliases: new[] { "website", "web", "trang web" }),
            new("Address", "Địa chỉ", Max: 300, Aliases: new[] { "dia chi", "address" }),
            new("City", "Tỉnh / Thành phố", Max: 100, Aliases: new[] { "tinh", "thanh pho", "tinh thanh", "tinh / thanh pho", "city", "province" }),
            new("Industry", "Ngành nghề", Max: 100, Aliases: new[] { "nganh", "nganh nghe", "linh vuc", "industry" }),
            new("EmployeeCount", "Số nhân viên", ImportFieldKind.Int, Aliases: new[] { "so nhan vien", "quy mo", "nhan su", "employees" }),
            new("OwnerId", "Người phụ trách", ImportFieldKind.Owner, Aliases: new[] { "phu trach", "nguoi phu trach", "nhan vien", "sales", "owner" }),
            new("Description", "Ghi chú", Max: 2000, Aliases: new[] { "ghi chu", "mo ta", "note", "notes", "description" })
        }, "Mã khách hàng, mã số thuế, email hoặc điện thoại"),
        new(nameof(Contact), "Người liên hệ", "contact", new ImportField[]
        {
            new("FullName", "Họ và tên", Required: true, Max: 150, Aliases: new[] { "ho ten", "ho va ten", "ten", "ten nguoi lien he", "nguoi lien he", "name", "full name", "contact" }),
            new("JobTitle", "Chức vụ", Max: 100, Aliases: new[] { "chuc vu", "chuc danh", "title", "job title", "position" }),
            new("CustomerId", "Khách hàng (mã, MST hoặc tên)", ImportFieldKind.Customer, Aliases: new[] { "khach hang", "cong ty", "ma kh", "ma khach hang", "customer", "company" }),
            new("Email", "Email", Max: 150, Aliases: new[] { "email", "e-mail" }),
            new("Phone", "Điện thoại", Max: 20, Aliases: new[] { "dien thoai", "sdt", "so dien thoai", "di dong", "phone", "mobile" }),
            new("BirthDate", "Ngày sinh", ImportFieldKind.Date, Aliases: new[] { "ngay sinh", "sinh nhat", "birthday", "birth date", "dob" }),
            new("IsPrimary", "Liên hệ chính", ImportFieldKind.Bool, Aliases: new[] { "lien he chinh", "chinh", "primary" }),
            new("OwnerId", "Người phụ trách", ImportFieldKind.Owner, Aliases: new[] { "phu trach", "nguoi phu trach", "owner" }),
            new("Notes", "Ghi chú", Max: 1000, Aliases: new[] { "ghi chu", "note", "notes" })
        }, "Email hoặc điện thoại"),
        new(nameof(Lead), "Lead (KH tiềm năng)", "lead", new ImportField[]
        {
            new("FullName", "Họ và tên", Required: true, Max: 150, Aliases: new[] { "ho ten", "ho va ten", "ten", "name", "full name" }),
            new("Company", "Công ty", Max: 200, Aliases: new[] { "cong ty", "doanh nghiep", "company" }),
            new("JobTitle", "Chức vụ", Max: 100, Aliases: new[] { "chuc vu", "title", "job title" }),
            new("Email", "Email", Max: 150, Aliases: new[] { "email", "e-mail" }),
            new("Phone", "Điện thoại", Max: 20, Aliases: new[] { "dien thoai", "sdt", "phone", "mobile" }),
            new("Source", "Nguồn", ImportFieldKind.Enum, EnumType: typeof(LeadSource), Aliases: new[] { "nguon", "nguon lead", "source" }),
            new("Status", "Trạng thái", ImportFieldKind.Enum, EnumType: typeof(LeadStatus), Aliases: new[] { "trang thai", "status" }),
            new("Rating", "Mức độ", ImportFieldKind.Enum, EnumType: typeof(LeadRating), Aliases: new[] { "muc do", "danh gia", "rating" }),
            new("EstimatedValue", "Giá trị dự kiến", ImportFieldKind.Decimal, Aliases: new[] { "gia tri", "gia tri du kien", "value", "estimated value" }),
            new("OwnerId", "Người phụ trách", ImportFieldKind.Owner, Aliases: new[] { "phu trach", "nguoi phu trach", "owner" }),
            new("Description", "Ghi chú", Max: 2000, Aliases: new[] { "ghi chu", "mo ta", "nhu cau", "note", "description" })
        }, "Email hoặc điện thoại"),
        new(nameof(Product), "Hàng hoá / dịch vụ", "product", new ImportField[]
        {
            new("Code", "Mã sản phẩm", Required: true, Max: 30, Aliases: new[] { "ma", "ma sp", "ma san pham", "ma hang", "code", "sku" }),
            new("Name", "Tên sản phẩm / dịch vụ", Required: true, Max: 200, Aliases: new[] { "ten", "ten sp", "ten san pham", "ten hang", "name", "product" }),
            new("Type", "Loại", ImportFieldKind.Enum, EnumType: typeof(ProductType), Aliases: new[] { "loai", "type" }),
            new("Category", "Nhóm", Max: 100, Aliases: new[] { "nhom", "nhom hang", "nhom san pham", "category" }),
            new("Unit", "Đơn vị tính", Max: 30, Aliases: new[] { "dvt", "don vi", "don vi tinh", "unit" }),
            new("UnitPrice", "Giá bán", ImportFieldKind.Decimal, Aliases: new[] { "gia", "gia ban", "don gia", "price", "unit price" }),
            new("CostPrice", "Giá vốn", ImportFieldKind.Decimal, Aliases: new[] { "gia von", "gia nhap", "cost", "cost price" }),
            new("VatPercent", "Thuế GTGT (%)", ImportFieldKind.Decimal, Aliases: new[] { "vat", "thue", "thue gtgt", "thue suat" }),
            new("Brand", "Hãng sản xuất", Max: 100, Aliases: new[] { "hang", "hang san xuat", "thuong hieu", "brand" }),
            new("Origin", "Xuất xứ", Max: 100, Aliases: new[] { "xuat xu", "origin" }),
            new("WarrantyMonths", "Bảo hành (tháng)", ImportFieldKind.Int, Aliases: new[] { "bao hanh", "bao hanh (thang)", "warranty" }),
            new("Specification", "Thông số kỹ thuật", Max: 2000, Aliases: new[] { "thong so", "thong so ky thuat", "specification" })
        }, "Mã sản phẩm")
    };

    public static ImportDefinition? Def(string type) => Definitions.FirstOrDefault(d => d.Type == type);

    private readonly CrmDbContext _db;
    public ImportService(CrmDbContext db) => _db = db;

    // ---------- Chuẩn hoá ----------

    /// <summary>Bỏ dấu tiếng Việt, chữ thường, gọn khoảng trắng – dùng so khớp tên cột / giá trị</summary>
    public static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.Trim().ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in d) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return Regex.Replace(sb.ToString().Normalize(NormalizationForm.FormC), @"[\s_]+", " ").Trim(' ', '*', ':');
    }

    public static string Digits(string? s) => new string((s ?? "").Where(char.IsDigit).ToArray());
    /// <summary>Số điện thoại chuẩn để so trùng: 9 số cuối (bỏ +84 / 0 đầu)</summary>
    public static string PhoneKey(string? s) { var d = Digits(s); return d.Length >= 9 ? d[^9..] : ""; }
    public static string EmailKey(string? s) => (s ?? "").Trim().ToLowerInvariant();

    /// <summary>Tự khớp cột của file với trường (theo tên tiêu đề)</summary>
    public static string?[] AutoMap(ImportDefinition def, string[] headers)
    {
        var used = new HashSet<string>();
        return headers.Select(h =>
        {
            var n = Norm(h);
            var f = def.Fields.FirstOrDefault(x => !used.Contains(x.Key) && (Norm(x.Label) == n || Norm(x.Key) == n || x.Aliases.Contains(n)))
                 ?? def.Fields.FirstOrDefault(x => !used.Contains(x.Key) && n.Length >= 3 && x.Aliases.Any(a => a.Length >= 4 && n.Contains(a)));
            if (f != null) used.Add(f.Key);
            return f?.Key;
        }).ToArray();
    }

    // ---------- Phân tích ----------

    public async Task<ImportPlan> AnalyzeAsync(string type, List<string[]> data, string?[] mapping, string mode, ClaimsPrincipal user)
    {
        var def = Def(type) ?? throw new ArgumentException(type);
        var plan = new ImportPlan { Type = type, Mode = mode };
        var me = user.GetUserId();
        var seesAll = user.SeesAll();

        // Danh bạ tra cứu
        var users = await _db.UsersInScope.Where(u => u.IsActive).Select(u => new { u.Id, u.Username, u.FullName, u.Email }).ToListAsync();
        int? FindUser(string v)
        {
            var n = Norm(v);
            return users.FirstOrDefault(u => Norm(u.Username) == n || Norm(u.FullName) == n || EmailKey(u.Email) == EmailKey(v))?.Id;
        }
        var customerQuery = _db.Customers.VisibleTo(user, _db).Select(c => new { c.Id, c.Code, c.Name, c.TaxCode });
        var customers = type == nameof(Contact) ? await customerQuery.ToListAsync() : await customerQuery.Take(0).ToListAsync();
        int? FindCustomer(string v)
        {
            var n = Norm(v); var tax = Digits(v);
            return customers.FirstOrDefault(c => Norm(c.Code) == n)?.Id
                ?? (tax.Length >= 10 ? customers.FirstOrDefault(c => Digits(c.TaxCode) == tax)?.Id : null)
                ?? customers.FirstOrDefault(c => Norm(c.Name) == n)?.Id;
        }

        // Khóa trùng của dữ liệu đang có (phạm vi đơn vị đang xem; mã khách hàng / mã hàng: toàn hệ thống vì là duy nhất)
        var existing = await ExistingKeysAsync(type);
        var seen = new Dictionary<string, int>();   // khóa → số dòng đầu tiên trong file

        for (var i = 1; i < data.Count; i++)
        {
            var src = data[i];
            var row = new ImportRow { RowNo = i + 1 };
            for (var c = 0; c < mapping.Length; c++)
            {
                var key = mapping[c];
                if (key == null) continue;
                var f = def.Fields.First(x => x.Key == key);
                var raw = c < src.Length ? src[c]?.Trim() ?? "" : "";
                if (raw.Length == 0) continue;
                var (val, err) = Parse(f, raw, FindUser, FindCustomer);
                if (err != null) row.Messages.Add($"{I18n.T(f.Label)}: {err}");
                else row.Values[key] = val;
            }
            foreach (var f in def.Fields.Where(f => f.Required && !row.Values.ContainsKey(f.Key)))
                row.Messages.Add(I18n.T("Thiếu {0}", I18n.T(f.Label)));
            if (row.Values.TryGetValue("Email", out var em) && em is string es && !System.Net.Mail.MailAddress.TryCreate(es, out _))
                row.Messages.Add(I18n.T("Email không hợp lệ: {0}", es));
            // Nhân viên kinh doanh chỉ nhập dữ liệu cho chính mình
            row.NoOwner = !row.Values.ContainsKey("OwnerId");
            if (def.Fields.Any(f => f.Kind == ImportFieldKind.Owner))
                row.Values["OwnerId"] = seesAll && row.Values.TryGetValue("OwnerId", out var o) && o is int oid ? oid : me;
            row.Display = string.Join(" · ", new[] { "Code", "Name", "FullName", "Company", "TaxCode", "Email", "Phone" }
                .Where(row.Values.ContainsKey).Select(k => row.Values[k]?.ToString()).Where(s => !string.IsNullOrEmpty(s)).Take(4));
            if (row.Messages.Count > 0) { row.Action = "Error"; plan.Rows.Add(row); continue; }

            // Trùng với dữ liệu đang có / trùng trong file
            var keys = KeysOf(type, row.Values).ToList();
            var dupInFile = keys.FirstOrDefault(seen.ContainsKey);
            var match = keys.Select(k => existing.TryGetValue(k, out var m) ? m : default).FirstOrDefault(m => m.Id > 0);
            if (dupInFile != null && mode != "create")
            {
                row.Action = "Skip"; row.Messages.Add(I18n.T("Trùng với dòng {0} trong file", seen[dupInFile]));
            }
            else if (match.Id > 0)
            {
                row.MatchId = match.Id; row.MatchTitle = match.Title;
                if (mode == "update") row.Action = "Update";
                else if (mode == "create" && type != nameof(Product)) row.Action = "Create";   // khách hàng trùng mã sẽ được cấp mã mới
                else { row.Action = "Skip"; row.Messages.Add(I18n.T("Đã có: {0}", match.Title)); }
            }
            foreach (var k in keys) seen.TryAdd(k, row.RowNo);
            plan.Rows.Add(row);
        }
        return plan;
    }

    private static (object? Value, string? Error) Parse(ImportField f, string raw, Func<string, int?> findUser, Func<string, int?> findCustomer)
    {
        switch (f.Kind)
        {
            case ImportFieldKind.Text:
                return f.Max > 0 && raw.Length > f.Max ? (null, I18n.T("dài quá {0} ký tự", f.Max)) : (raw, null);
            case ImportFieldKind.Int:
            {
                // 1.200 / 1,200 = một nghìn hai trăm; 12.0 = 12
                var s = raw.Replace(" ", "");
                if (Regex.IsMatch(s, @"^\d{1,3}([.,]\d{3})+$")) s = s.Replace(".", "").Replace(",", "");
                else s = s.Split('.', ',')[0];
                return Regex.IsMatch(s, @"^\d+$") && int.TryParse(s, out var iv) ? (iv, null) : (null, I18n.T("không phải số nguyên"));
            }
            case ImportFieldKind.Decimal:
            {
                var s = raw.Replace(" ", "").Replace("đ", "").Replace("VND", "", StringComparison.OrdinalIgnoreCase).Replace("%", "");
                // 1.200.000 / 1,200,000 (phân cách nghìn) – 12.5 / 12,5 (thập phân)
                if (Regex.IsMatch(s, @"^-?\d{1,3}([.,]\d{3})+$")) s = s.Replace(".", "").Replace(",", "");
                s = s.Replace(',', '.');
                return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv) && dv >= 0 ? (dv, null) : (null, I18n.T("không phải số"));
            }
            case ImportFieldKind.Date:
            {
                var formats = new[] { "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy" };
                return DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? (dt.Date, null) : (null, I18n.T("ngày không hợp lệ (dd/MM/yyyy)"));
            }
            case ImportFieldKind.Bool:
            {
                var n = Norm(raw);
                return (n is "1" or "x" or "co" or "yes" or "true" or "dung" or "v", null);
            }
            case ImportFieldKind.Enum:
            {
                var n = Norm(raw);
                foreach (var v in Enum.GetValues(f.EnumType!))
                {
                    var e = (Enum)v;
                    if (Norm(e.ToString()) == n || Norm(e.GetDisplayName()) == n || Norm(I18n.T(e.GetDisplayName())) == n || Convert.ToInt32(e).ToString() == raw) return (e, null);
                }
                var options = string.Join(", ", Enum.GetValues(f.EnumType!).Cast<Enum>().Select(e => e.GetDisplayName()));
                return (null, I18n.T("\"{0}\" không có trong danh sách ({1})", raw, options));
            }
            case ImportFieldKind.Owner:
                return findUser(raw) is int uid ? (uid, null) : (null, I18n.T("không tìm thấy người dùng \"{0}\"", raw));
            case ImportFieldKind.Customer:
                return findCustomer(raw) is int cid ? (cid, null) : (null, I18n.T("không tìm thấy khách hàng \"{0}\"", raw));
        }
        return (raw, null);
    }

    /// <summary>Các khóa so trùng của một dòng</summary>
    private static IEnumerable<string> KeysOf(string type, Dictionary<string, object?> v)
    {
        string? S(string k) => v.TryGetValue(k, out var x) ? x as string : null;
        switch (type)
        {
            case nameof(Customer):
                if (!string.IsNullOrEmpty(S("Code"))) yield return "code:" + Norm(S("Code"));
                if (Digits(S("TaxCode")).Length >= 10) yield return "tax:" + Digits(S("TaxCode"));
                if (!string.IsNullOrEmpty(S("Email"))) yield return "email:" + EmailKey(S("Email"));
                if (PhoneKey(S("Phone")).Length > 0) yield return "phone:" + PhoneKey(S("Phone"));
                break;
            case nameof(Contact):
            case nameof(Lead):
                if (!string.IsNullOrEmpty(S("Email"))) yield return "email:" + EmailKey(S("Email"));
                if (PhoneKey(S("Phone")).Length > 0) yield return "phone:" + PhoneKey(S("Phone"));
                break;
            case nameof(Product):
                if (!string.IsNullOrEmpty(S("Code"))) yield return "code:" + Norm(S("Code"));
                break;
        }
    }

    private async Task<Dictionary<string, (int Id, string Title)>> ExistingKeysAsync(string type)
    {
        var map = new Dictionary<string, (int, string)>();
        void Put(string? k, int id, string title) { if (!string.IsNullOrEmpty(k) && !k.EndsWith(":")) map.TryAdd(k, (id, title)); }
        switch (type)
        {
            case nameof(Customer):
                foreach (var c in await _db.Customers.IgnoreQueryFilters().Select(c => new { c.Id, c.Code, c.Name, c.TaxCode, c.Email, c.Phone }).ToListAsync())
                {
                    var t = c.Code + " – " + c.Name;
                    Put("code:" + Norm(c.Code), c.Id, t);
                    if (Digits(c.TaxCode).Length >= 10) Put("tax:" + Digits(c.TaxCode), c.Id, t);
                    Put("email:" + EmailKey(c.Email), c.Id, t);
                    if (PhoneKey(c.Phone).Length > 0) Put("phone:" + PhoneKey(c.Phone), c.Id, t);
                }
                break;
            case nameof(Contact):
                foreach (var c in await _db.Contacts.Select(c => new { c.Id, c.FullName, c.Email, c.Phone }).ToListAsync())
                {
                    Put("email:" + EmailKey(c.Email), c.Id, c.FullName);
                    if (PhoneKey(c.Phone).Length > 0) Put("phone:" + PhoneKey(c.Phone), c.Id, c.FullName);
                }
                break;
            case nameof(Lead):
                foreach (var c in await _db.Leads.Select(c => new { c.Id, c.FullName, c.Email, c.Phone }).ToListAsync())
                {
                    Put("email:" + EmailKey(c.Email), c.Id, c.FullName);
                    if (PhoneKey(c.Phone).Length > 0) Put("phone:" + PhoneKey(c.Phone), c.Id, c.FullName);
                }
                break;
            case nameof(Product):
                foreach (var p in await _db.Products.Select(p => new { p.Id, p.Code, p.Name }).ToListAsync())
                    Put("code:" + Norm(p.Code), p.Id, p.Code + " – " + p.Name);
                break;
        }
        return map;
    }

    // ---------- Ghi dữ liệu ----------

    public async Task<(int Created, int Updated)> ExecuteAsync(ImportPlan plan, ClaimsPrincipal user, bool canSeeCost)
    {
        int created = 0, updated = 0;
        var nextCustomerNo = plan.Type == nameof(Customer) ? await _db.Customers.IgnoreQueryFilters().CountAsync() + 1 : 0;
        var usedCodes = plan.Type == nameof(Customer) ? (await _db.Customers.IgnoreQueryFilters().Select(c => c.Code).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase) : new();
        var batch = 0;
        foreach (var row in plan.Rows.Where(r => r.Action is "Create" or "Update"))
        {
            object entity;
            if (row.Action == "Update")
            {
                entity = plan.Type switch
                {
                    nameof(Customer) => (object?)await _db.Customers.FindAsync(row.MatchId),
                    nameof(Contact) => await _db.Contacts.FindAsync(row.MatchId),
                    nameof(Lead) => await _db.Leads.FindAsync(row.MatchId),
                    _ => await _db.Products.FindAsync(row.MatchId)
                } ?? throw new InvalidOperationException();
                // Nhân viên chỉ cập nhật bản ghi mình phụ trách
                if (entity is IOwned owned && !user.CanAccess(owned)) { row.Action = "Skip"; row.Messages.Add(I18n.T("Không có quyền sửa bản ghi đang có")); continue; }
                if (!user.SeesAll()) row.Values.Remove("OwnerId");
            }
            else
            {
                entity = plan.Type switch
                {
                    nameof(Customer) => new Customer(),
                    nameof(Contact) => new Contact(),
                    nameof(Lead) => new Lead(),
                    _ => new Product()
                };
                if (entity is Customer nc)
                {
                    var code = row.Values.GetValueOrDefault("Code") as string;
                    if (string.IsNullOrEmpty(code) || usedCodes.Contains(code))
                    {
                        do code = $"KH{nextCustomerNo++:0000}"; while (usedCodes.Contains(code));
                        row.Values["Code"] = code;
                    }
                    usedCodes.Add(code);
                }
            }
            if (!canSeeCost) row.Values.Remove("CostPrice");
            foreach (var (key, val) in row.Values)
            {
                var prop = entity.GetType().GetProperty(key);
                if (prop == null || val == null) continue;
                var target = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                prop.SetValue(entity, target.IsEnum ? Enum.ToObject(target, val) : Convert.ChangeType(val, target, CultureInfo.InvariantCulture));
            }
            if (entity is Product pr && string.IsNullOrEmpty(pr.Unit)) pr.Unit = "Cái";
            if (entity is Customer cu && row.Action == "Update") cu.UpdatedAt = DateTime.Now;
            row.Entity = entity;
            if (row.Action == "Create")
            {
                _db.Add(entity);
                created++;
            }
            else updated++;
            // Lưu theo lô để không giữ quá nhiều bản ghi trong bộ nhớ
            if (++batch % 200 == 0) await _db.SaveChangesAsync();
        }
        AuditTrail.Write(_db, plan.Type, 0, "Import", I18n.T("Nhập Excel: {0} tạo mới, {1} cập nhật, {2} bỏ qua, {3} lỗi",
            created, updated, plan.Count("Skip"), plan.Count("Error")));
        await _db.SaveChangesAsync();
        return (created, updated);
    }

    /// <summary>File mẫu: dòng tiêu đề + một dòng ví dụ</summary>
    public static Reports.ReportResult Template(ImportDefinition def)
    {
        var r = new Reports.ReportResult();
        foreach (var f in def.Fields) r.Col(I18n.T(f.Label) + (f.Required ? " *" : ""), Reports.ReportColumnKind.Text, false);
        r.Rows.Add(def.Fields.Select(f => (object?)Sample(def.Type, f)).ToArray());
        return r;
    }

    private static string Sample(string type, ImportField f) => (type, f.Key) switch
    {
        (_, "OwnerId") => "sales1",
        (nameof(Customer), "Code") => "",
        (nameof(Customer), "Name") => "Công ty TNHH Thương mại Minh Phát",
        (nameof(Customer), "Type") => "Tiềm năng",
        (nameof(Customer), "TaxCode") => "0312345678",
        (nameof(Customer), "Phone") => "028 3812 3456",
        (nameof(Customer), "Email") => "info@minhphat.vn",
        (nameof(Customer), "City") => "TP. Hồ Chí Minh",
        (nameof(Customer), "Industry") => "Bán lẻ",
        (nameof(Customer), "EmployeeCount") => "120",
        (nameof(Contact), "FullName") => "Nguyễn Văn An",
        (nameof(Contact), "JobTitle") => "Giám đốc",
        (nameof(Contact), "CustomerId") => "KH0001",
        (nameof(Contact), "Email") => "an.nguyen@minhphat.vn",
        (nameof(Contact), "Phone") => "0903 123 456",
        (nameof(Contact), "BirthDate") => "15/08/1985",
        (nameof(Contact), "IsPrimary") => "x",
        (nameof(Lead), "FullName") => "Trần Thị Bình",
        (nameof(Lead), "Company") => "Công ty CP Thực phẩm Sao Mai",
        (nameof(Lead), "Email") => "binh.tran@saomai.vn",
        (nameof(Lead), "Phone") => "0912 345 678",
        (nameof(Lead), "Source") => "Website",
        (nameof(Lead), "Rating") => "Nóng",
        (nameof(Lead), "EstimatedValue") => "150000000",
        (nameof(Product), "Code") => "SP-001",
        (nameof(Product), "Name") => "Phần mềm quản lý bán hàng – gói 1 năm",
        (nameof(Product), "Type") => "Dịch vụ",
        (nameof(Product), "Category") => "Phần mềm",
        (nameof(Product), "Unit") => "Gói",
        (nameof(Product), "UnitPrice") => "12000000",
        (nameof(Product), "CostPrice") => "6000000",
        (nameof(Product), "VatPercent") => "10",
        _ => ""
    };
}
