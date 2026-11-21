using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CRM.Web.Infrastructure;

/// <summary>Cấu hình tra cứu mã số thuế (AppSettings "taxlookup")</summary>
public class TaxLookupSettings
{
    public const string Key = "taxlookup";
    public bool Enabled { get; set; } = true;
    /// <summary>Địa chỉ dịch vụ tra cứu, {0} = mã số thuế. Mặc định: VietQR (dữ liệu tổng hợp từ Tổng cục Thuế)</summary>
    public string Endpoint { get; set; } = "https://api.vietqr.io/v2/business/{0}";
}

public record TaxInfo(string TaxCode, string Name, string DisplayName, string? ShortName, string? InternationalName, string? Address, string? Status, bool Active, string? CityCode, string? CityName);

/// <summary>
/// Tra cứu thông tin doanh nghiệp theo mã số thuế để tự điền tên, địa chỉ, tỉnh / thành.
/// Chỉ gửi mã số thuế (thông tin công khai) tới dịch vụ tra cứu; kết quả được nhớ 1 ngày.
/// </summary>
public class TaxLookupService
{
    private readonly IHttpClientFactory _http;
    private readonly IMemoryCache _cache;
    private readonly SettingsStore _settings;
    private readonly CatalogService _catalogs;
    private readonly ILogger<TaxLookupService> _log;

    public TaxLookupService(IHttpClientFactory http, IMemoryCache cache, SettingsStore settings, CatalogService catalogs, ILogger<TaxLookupService> log)
    {
        _http = http; _cache = cache; _settings = settings; _catalogs = catalogs; _log = log;
    }

    /// <summary>MST hợp lệ về hình thức: 10 chữ số, hoặc 10 chữ số + "-" + 3 chữ số (đơn vị phụ thuộc)</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = Regex.Replace(code, @"[\s.]", "");
        var m = Regex.Match(c, @"^(\d{10})(?:-?(\d{3}))?$");
        return m.Success ? m.Groups[1].Value + (m.Groups[2].Success ? "-" + m.Groups[2].Value : "") : null;
    }

    public async Task<(TaxInfo? Info, string? Error)> LookupAsync(string? code, CancellationToken ct = default)
    {
        var mst = Normalize(code);
        if (mst == null) return (null, I18n.T("Mã số thuế gồm 10 chữ số (đơn vị phụ thuộc: 10 chữ số - 3 chữ số)."));
        var s = await _settings.GetAsync<TaxLookupSettings>(TaxLookupSettings.Key);
        if (!s.Enabled || string.IsNullOrWhiteSpace(s.Endpoint)) return (null, I18n.T("Chức năng tra cứu mã số thuế đang tắt."));
        if (_cache.TryGetValue("tax:" + mst, out TaxInfo? cached)) return (cached, null);
        try
        {
            var client = _http.CreateClient("taxlookup");
            using var resp = await client.GetAsync(string.Format(s.Endpoint, Uri.EscapeDataString(mst)), ct);
            if (!resp.IsSuccessStatusCode) return (null, I18n.T("Dịch vụ tra cứu không phản hồi (mã {0}). Vui lòng nhập tay.", (int)resp.StatusCode));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object)
                return (null, I18n.T("Không tìm thấy doanh nghiệp có mã số thuế {0}.", mst));
            string? S(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
            var name = S("name");
            if (string.IsNullOrEmpty(name)) return (null, I18n.T("Không tìm thấy doanh nghiệp có mã số thuế {0}.", mst));
            var address = S("address");
            var status = S("status");
            var (cityCode, cityName) = MatchCity(address);
            var info = new TaxInfo(S("id") ?? mst, name, ProperName(name), S("shortName"), S("internationalName"), address, status,
                status == null || !Regex.IsMatch(ImportService.Norm(status), @"ngung|tam ngung|cham dut|khong hoat dong|bo dia chi"), cityCode, cityName);
            _cache.Set("tax:" + mst, info, TimeSpan.FromDays(1));
            return (info, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning(ex, "Tra cứu MST {Mst} lỗi", mst);
            return (null, I18n.T("Không kết nối được dịch vụ tra cứu mã số thuế. Vui lòng nhập tay."));
        }
    }

    private static readonly HashSet<string> KeepUpper = new(StringComparer.OrdinalIgnoreCase)
        { "TNHH", "MTV", "CP", "XNK", "TM", "DV", "SX", "XD", "KD", "TMDV", "JSC", "LTD", "CO.", "VN", "HTX", "DNTN", "TP", "TP.", "II", "III", "IV" };

    /// <summary>"CÔNG TY TNHH ABC" → "Công Ty TNHH Abc"; giữ nguyên viết hoa các từ viết tắt thường gặp</summary>
    public static string ProperName(string name)
    {
        if (name.Any(char.IsLower)) return name.Trim();
        var ti = new CultureInfo("vi-VN").TextInfo;
        var words = Regex.Split(name.Trim(), @"(\s+|-|\(|\))");
        return string.Concat(words.Select(w => w.Length == 0 || !char.IsLetter(w[0]) || KeepUpper.Contains(w) ? w : ti.ToTitleCase(w.ToLower(new CultureInfo("vi-VN")))));
    }

    /// <summary>Khớp tỉnh / thành trong danh mục theo phần cuối địa chỉ</summary>
    private (string? Code, string? Name) MatchCity(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return (null, null);
        var a = ImportService.Norm(address);
        static string Core(string n) => Regex.Replace(n, @"^(thanh pho|tp\.?|tinh)\s+", "").Trim();
        foreach (var c in _catalogs.Items(CatalogGroup.City).OrderByDescending(c => c.Name.Length))
        {
            var key = Core(ImportService.Norm(c.Name));
            if (key.Length >= 2 && Regex.IsMatch(a, @"(^|[\s,.-])" + Regex.Escape(key) + @"\s*$|(tp\.?|thanh pho|tinh)\s+" + Regex.Escape(key) + @"\b"))
                return (c.Code, c.Name);
        }
        return (null, null);
    }
}

/// <summary>Một bản ghi có thể trùng với dữ liệu đang nhập</summary>
public record DupHit(string Type, int Id, string Title, string? Detail, string Reason, string? Owner, string? Url, bool Strong);

/// <summary>
/// Cảnh báo trùng ngay khi nhập: khách hàng (MST, email, điện thoại, tên công ty), người liên hệ (email, điện thoại),
/// lead (email, điện thoại, đã là khách hàng / người liên hệ). Tìm trên toàn hệ thống; chỉ trả liên kết khi người dùng xem được bản ghi.
/// </summary>
public class DuplicateCheckService
{
    private readonly CrmDbContext _db;
    public DuplicateCheckService(CrmDbContext db) => _db = db;

    private static readonly Regex CommonWords = new(@"\b(công|ty|cty|tnhh|cổ|phần|cp|mtv|một|thành|viên|tập|đoàn|chi|nhánh|và|the|company|co|ltd|jsc|corp|group|tm|dv|sx|xnk)\b", RegexOptions.IgnoreCase);

    /// <summary>Từ đặc trưng nhất (dài nhất) trong tên để thu hẹp tìm kiếm bằng LIKE</summary>
    private static string? Probe(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var words = CommonWords.Replace(name, " ").Split(new[] { ' ', ',', '.', '-', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
        var w = words.OrderByDescending(x => x.Length).FirstOrDefault();
        return w is { Length: >= 3 } ? w : null;
    }

    /// <summary>3 số cuối – nhóm cuối của số điện thoại VN luôn ≥ 3 chữ số liền nhau (0912 345 678, 028 3388 5608)</summary>
    private static string? PhoneProbe(string? phone) { var k = ImportService.PhoneKey(phone); return k.Length == 9 ? k[^3..] : null; }

    public async Task<List<DupHit>> CheckAsync(string entity, int id, string? name, string? taxCode, string? phone, string? email, string? company, ClaimsPrincipal user)
    {
        var hits = new List<DupHit>();
        var emailKey = ImportService.EmailKey(email);
        if (emailKey.Length > 0 && !emailKey.Contains('@')) emailKey = "";
        var phoneKey = ImportService.PhoneKey(phone);
        var phoneProbe = PhoneProbe(phone);
        var taxDigits = ImportService.Digits(taxCode);

        // ---------- Khách hàng ----------
        if (entity is nameof(Customer) or nameof(Lead))
        {
            var custName = entity == nameof(Customer) ? name : company;
            var nameKey = MergeService.NameKey(custName);
            var probe = Probe(custName);
            var q = _db.Customers.IgnoreQueryFilters().AsNoTracking().Where(c => !(entity == nameof(Customer) && c.Id == id));
            var cands = await q.Where(c =>
                    (taxDigits.Length >= 10 && c.TaxCode != null && c.TaxCode.Contains(taxDigits.Substring(0, 10))) ||
                    (emailKey != "" && c.Email == emailKey) ||
                    (phoneProbe != null && c.Phone != null && c.Phone.Contains(phoneProbe)) ||
                    (probe != null && c.Name.Contains(probe)))
                .Select(c => new { c.Id, c.Name, c.Code, c.TaxCode, c.Email, c.Phone, c.OwnerId, c.BranchId, Owner = c.Owner != null ? c.Owner.FullName : null })
                .Take(200).ToListAsync();
            foreach (var c in cands)
            {
                var reasons = new List<string>(); var strong = false;
                if (entity == nameof(Customer) && taxDigits.Length >= 10 && ImportService.Digits(c.TaxCode) == taxDigits) { reasons.Add(I18n.T("trùng mã số thuế")); strong = true; }
                if (emailKey != "" && ImportService.EmailKey(c.Email) == emailKey) { reasons.Add(I18n.T("trùng email")); strong = true; }
                if (phoneKey.Length == 9 && ImportService.PhoneKey(c.Phone) == phoneKey) { reasons.Add(I18n.T("trùng điện thoại")); strong = true; }
                if (nameKey.Length >= 4 && MergeService.NameKey(c.Name) == nameKey) { reasons.Add(I18n.T("trùng tên công ty")); strong |= entity == nameof(Customer); }
                if (reasons.Count == 0) continue;
                hits.Add(new DupHit(nameof(Customer), c.Id, c.Name, string.Join(" · ", new[] { c.Code, c.TaxCode }.Where(x => !string.IsNullOrEmpty(x))),
                    string.Join(", ", reasons), c.Owner, $"/Customers/Details/{c.Id}", strong));
            }
        }

        // ---------- Người liên hệ ----------
        if ((entity is nameof(Contact) or nameof(Lead)) && (emailKey != "" || phoneProbe != null))
        {
            var cands = await _db.Contacts.IgnoreQueryFilters().AsNoTracking().Where(c => !(entity == nameof(Contact) && c.Id == id))
                .Where(c => (emailKey != "" && c.Email == emailKey) || (phoneProbe != null && c.Phone != null && c.Phone.Contains(phoneProbe)))
                .Select(c => new { c.Id, c.FullName, c.Email, c.Phone, c.CustomerId, Customer = c.Customer != null ? c.Customer.Name : null, Owner = c.Owner != null ? c.Owner.FullName : null })
                .Take(100).ToListAsync();
            foreach (var c in cands)
            {
                var reasons = new List<string>();
                if (emailKey != "" && ImportService.EmailKey(c.Email) == emailKey) reasons.Add(I18n.T("trùng email"));
                if (phoneKey.Length == 9 && ImportService.PhoneKey(c.Phone) == phoneKey) reasons.Add(I18n.T("trùng điện thoại"));
                if (reasons.Count == 0) continue;
                hits.Add(new DupHit(nameof(Contact), c.Id, c.FullName, c.Customer, string.Join(", ", reasons), c.Owner,
                    c.CustomerId.HasValue ? $"/Customers/Details/{c.CustomerId}" : $"/Contacts/Edit/{c.Id}", true));
            }
        }

        // ---------- Lead ----------
        if (entity is nameof(Lead) or nameof(Contact) && (emailKey != "" || phoneProbe != null))
        {
            var cands = await _db.Leads.IgnoreQueryFilters().AsNoTracking().Where(l => !(entity == nameof(Lead) && l.Id == id))
                .Where(l => (emailKey != "" && l.Email == emailKey) || (phoneProbe != null && l.Phone != null && l.Phone.Contains(phoneProbe)))
                .Select(l => new { l.Id, l.FullName, l.Company, l.Email, l.Phone, l.Status, Owner = l.Owner != null ? l.Owner.FullName : null })
                .Take(100).ToListAsync();
            foreach (var l in cands)
            {
                var reasons = new List<string>();
                if (emailKey != "" && ImportService.EmailKey(l.Email) == emailKey) reasons.Add(I18n.T("trùng email"));
                if (phoneKey.Length == 9 && ImportService.PhoneKey(l.Phone) == phoneKey) reasons.Add(I18n.T("trùng điện thoại"));
                if (reasons.Count == 0) continue;
                // Lead đã chuyển đổi / không đạt vẫn cảnh báo nhưng không chặn
                hits.Add(new DupHit(nameof(Lead), l.Id, l.FullName, string.Join(" · ", new[] { l.Company, l.Status.GetDisplayName() }.Where(x => !string.IsNullOrEmpty(x))),
                    string.Join(", ", reasons), l.Owner, $"/Leads/Details/{l.Id}", entity == nameof(Lead) && l.Status != LeadStatus.Converted));
            }
        }

        // Chỉ trả liên kết tới bản ghi người dùng được xem (theo phạm vi đơn vị + quyền sở hữu / chia sẻ)
        var visible = new HashSet<(string, int)>();
        foreach (var g in hits.GroupBy(h => h.Type))
        {
            var ids = g.Select(h => h.Id).ToList();
            var items = g.Key switch
            {
                nameof(Customer) => (await _db.Customers.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync()).Where(x => user.CanAccess(x)).Select(x => x.Id),
                nameof(Contact) => (await _db.Contacts.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync()).Where(x => user.CanAccess(x)).Select(x => x.Id),
                _ => (await _db.Leads.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync()).Where(x => user.CanAccess(x)).Select(x => x.Id)
            };
            foreach (var i in items) visible.Add((g.Key, i));
        }
        return hits.Select(h => visible.Contains((h.Type, h.Id)) ? h : h with { Url = null })
            .OrderByDescending(h => h.Strong).ThenBy(h => h.Type).Take(10).ToList();
    }

    public static string TypeLabel(string type) => I18n.T(type switch { nameof(Customer) => "Khách hàng", nameof(Contact) => "Người liên hệ", _ => "Lead" });
}
