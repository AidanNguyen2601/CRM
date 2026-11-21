using System.Globalization;
using System.Text;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using static CRM.Web.Infrastructure.I18n;

namespace CRM.Web.Infrastructure;

/// <summary>Cấu hình mẫu in PDF (lưu AppSettings "pdf"); logo lưu riêng khóa "pdf.logo" (base64)</summary>
public class PdfSettings
{
    public const string Key = "pdf", LogoKey = "pdf.logo";
    /// <summary>Màu chủ đạo của tiêu đề / bảng (#RRGGBB)</summary>
    public string Accent { get; set; } = "#1F4E79";
    public bool ShowLogo { get; set; } = true;
    /// <summary>Dòng chân trang mọi chứng từ (vd. website, hotline)</summary>
    public string? Footer { get; set; }
    /// <summary>Lời kết cuối báo giá</summary>
    public string? QuoteNote { get; set; } = "Rất mong nhận được sự hợp tác của Quý khách. Mọi thắc mắc xin liên hệ người phụ trách ở trên.";
    /// <summary>Điều khoản chung in cuối hợp đồng (mỗi dòng một điều)</summary>
    public string? ContractClauses { get; set; } =
        "Hai bên cam kết thực hiện đúng các điều khoản đã thoả thuận trong hợp đồng.\n" +
        "Mọi sửa đổi, bổ sung phải được lập thành phụ lục và có chữ ký của đại diện hai bên.\n" +
        "Tranh chấp phát sinh (nếu có) được giải quyết trên tinh thần thương lượng; trường hợp không thương lượng được sẽ đưa ra Toà án có thẩm quyền.\n" +
        "Hợp đồng được lập thành 02 bản có giá trị pháp lý như nhau, mỗi bên giữ 01 bản.";
    /// <summary>Hướng dẫn thanh toán in trên đề nghị thanh toán</summary>
    public string? PaymentNote { get; set; } = "Quý khách vui lòng chuyển khoản và ghi rõ số hoá đơn trong nội dung thanh toán.";
}

/// <summary>Chọn font có đủ dấu tiếng Việt: cấu hình Pdf:FontRegular/FontBold → Arial (Windows) → Liberation Sans / DejaVu Sans (Linux)</summary>
public class CrmFontResolver : IFontResolver
{
    public const string Family = "CrmSans";
    private readonly Dictionary<string, string> _files = new();

    public CrmFontResolver(IConfiguration config)
    {
        var candidates = new List<(string R, string B, string I, string BI)>();
        if (!string.IsNullOrWhiteSpace(config["Pdf:FontRegular"]))
            candidates.Add((config["Pdf:FontRegular"]!, config["Pdf:FontBold"] ?? config["Pdf:FontRegular"]!, config["Pdf:FontItalic"] ?? "", config["Pdf:FontBoldItalic"] ?? ""));
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!string.IsNullOrEmpty(win))
            candidates.Add((Path.Combine(win, "arial.ttf"), Path.Combine(win, "arialbd.ttf"), Path.Combine(win, "ariali.ttf"), Path.Combine(win, "arialbi.ttf")));
        foreach (var dir in new[] { "/usr/share/fonts/truetype/liberation2", "/usr/share/fonts/truetype/liberation" })
            candidates.Add((Path.Combine(dir, "LiberationSans-Regular.ttf"), Path.Combine(dir, "LiberationSans-Bold.ttf"), Path.Combine(dir, "LiberationSans-Italic.ttf"), Path.Combine(dir, "LiberationSans-BoldItalic.ttf")));
        const string dv = "/usr/share/fonts/truetype/dejavu";
        candidates.Add((Path.Combine(dv, "DejaVuSans.ttf"), Path.Combine(dv, "DejaVuSans-Bold.ttf"), Path.Combine(dv, "DejaVuSans-Oblique.ttf"), Path.Combine(dv, "DejaVuSans-BoldOblique.ttf")));
        var set = candidates.FirstOrDefault(c => File.Exists(c.R));
        if (set.R == null) return;
        _files["R"] = set.R;
        _files["B"] = File.Exists(set.B) ? set.B : set.R;
        if (File.Exists(set.I)) _files["I"] = set.I;
        if (File.Exists(set.BI)) _files["BI"] = set.BI;
    }

    public bool Available => _files.ContainsKey("R");
    public string? RegularPath => _files.GetValueOrDefault("R");

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        if (!Available) return null;
        if (bold && italic) return _files.ContainsKey("BI") ? new FontResolverInfo("BI") : new FontResolverInfo("B", false, true);
        if (bold) return new FontResolverInfo("B", _files["B"] == _files["R"], false);
        if (italic) return _files.ContainsKey("I") ? new FontResolverInfo("I") : new FontResolverInfo("R", false, true);
        return new FontResolverInfo("R");
    }

    public byte[]? GetFont(string faceName) => _files.TryGetValue(faceName, out var f) ? File.ReadAllBytes(f) : null;
}

/// <summary>Đọc số tiền bằng chữ (tiếng Việt / tiếng Anh) cho chứng từ</summary>
public static class MoneyWords
{
    private static readonly string[] Digits = { "không", "một", "hai", "ba", "bốn", "năm", "sáu", "bảy", "tám", "chín" };
    private static readonly string[] Units = { "", " nghìn", " triệu", " tỷ", " nghìn tỷ", " triệu tỷ" };

    public static string Text(decimal amount) => IsEnglish ? English(amount) : Vietnamese(amount);

    public static string Vietnamese(decimal amount)
    {
        var n = (long)Math.Round(Math.Abs(amount), 0);
        if (n == 0) return "Không đồng";
        var groups = new List<int>();
        for (var x = n; x > 0; x /= 1000) groups.Add((int)(x % 1000));
        var parts = new List<string>();
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            if (groups[i] == 0) continue;
            parts.Add(Group(groups[i], i < groups.Count - 1) + Units[Math.Min(i, Units.Length - 1)]);
        }
        var s = string.Join(" ", parts) + " đồng";
        if (amount < 0) s = "âm " + s;
        return char.ToUpper(s[0]) + s[1..];
    }

    /// <summary>Đọc nhóm 3 chữ số; full = không phải nhóm đầu (phải đọc "không trăm", "linh")</summary>
    private static string Group(int g, bool full)
    {
        int h = g / 100, t = g / 10 % 10, u = g % 10;
        var sb = new StringBuilder();
        if (h > 0 || full) sb.Append(Digits[h]).Append(" trăm");
        if (t > 1) sb.Append(' ').Append(Digits[t]).Append(" mươi");
        else if (t == 1) sb.Append(" mười");
        else if (u > 0 && (h > 0 || full)) sb.Append(" linh");
        if (u > 0)
        {
            var w = u == 1 && t > 1 ? "mốt" : u == 5 && t > 0 ? "lăm" : u == 4 && t > 1 ? "tư" : Digits[u];
            sb.Append(' ').Append(w);
        }
        return sb.ToString().Trim();
    }

    private static readonly string[] Small = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
    private static readonly string[] Tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };
    private static readonly string[] Scales = { "", " thousand", " million", " billion", " trillion", " quadrillion" };

    public static string English(decimal amount)
    {
        var n = (long)Math.Round(Math.Abs(amount), 0);
        if (n == 0) return "Zero dong";
        var parts = new List<string>();
        var i = 0;
        for (var x = n; x > 0; x /= 1000, i++)
        {
            var g = (int)(x % 1000);
            if (g == 0) continue;
            var w = new List<string>();
            if (g >= 100) w.Add(Small[g / 100] + " hundred");
            var r = g % 100;
            if (r >= 20) w.Add(Tens[r / 10] + (r % 10 > 0 ? "-" + Small[r % 10] : ""));
            else if (r > 0) w.Add(Small[r]);
            parts.Insert(0, string.Join(" ", w) + Scales[Math.Min(i, Scales.Length - 1)]);
        }
        var s = string.Join(" ", parts) + " dong";
        if (amount < 0) s = "minus " + s;
        return char.ToUpper(s[0]) + s[1..];
    }
}

/// <summary>
/// Tạo file PDF chứng từ: báo giá, hợp đồng, đề nghị thanh toán (theo hoá đơn).
/// Thông tin bên bán lấy theo pháp nhân của đơn vị; logo, màu và điều khoản theo cấu hình "Mẫu in PDF".
/// Người gọi tự kiểm tra quyền truy cập bản ghi trước khi tạo.
/// </summary>
public class PdfService
{
    private static readonly object InitLock = new();
    private static CrmFontResolver? _fonts;

    private readonly CrmDbContext _db;
    private readonly BranchScope _scope;
    private readonly CatalogService _catalogs;
    private readonly SettingsStore _settings;

    public PdfService(CrmDbContext db, BranchScope scope, CatalogService catalogs, SettingsStore settings, IConfiguration config)
    {
        _db = db; _scope = scope; _catalogs = catalogs; _settings = settings;
        EnsureFonts(config);
    }

    /// <summary>Đăng ký font một lần cho cả ứng dụng (PDFsharp dùng cấu hình toàn cục)</summary>
    private static void EnsureFonts(IConfiguration config)
    {
        if (_fonts != null) return;
        lock (InitLock)
        {
            if (_fonts != null) return;
            var r = new CrmFontResolver(config);
            if (GlobalFontSettings.FontResolver == null) GlobalFontSettings.FontResolver = r;
            _fonts = r;
        }
    }

    public static bool FontsAvailable => _fonts?.Available == true;
    public static string? FontPath => _fonts?.RegularPath;

    public static string SafeFileName(string s)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { ' ', '/', '\\' }).ToHashSet();
        return new string(s.Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim('-');
    }

    public async Task<PdfSettings> SettingsAsync() => await _settings.GetAsync<PdfSettings>(PdfSettings.Key);
    public async Task<string?> LogoAsync() => (await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == PdfSettings.LogoKey))?.Value;

    // ======================= Chứng từ =======================

    public async Task<(byte[] Data, string FileName)?> QuoteAsync(int id)
    {
        var q = await _db.Quotes.Include(x => x.Customer).Include(x => x.Contact).Include(x => x.Owner).Include(x => x.Lines)
            .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (q == null) return null;
        var s = await SettingsAsync();
        var b = await BuilderAsync(s, T("Báo giá {0}", q.Number));
        b.Header(q.BranchId, T("BÁO GIÁ"), new[] { T("Số: {0}", q.Number), T("Ngày: {0}", q.QuoteDate.ToDate()), T("Hiệu lực đến: {0}", q.ValidUntil.ToDate()) });
        b.Parties(T("Kính gửi"), CustomerLines(q.Customer, q.Contact, T("Người nhận: {0}")), T("Người phụ trách"),
            new[] { q.Owner?.FullName ?? "", q.Owner?.Email ?? "" });
        var hasDiscount = q.Lines.Any(l => l.DiscountPercent > 0);
        var rows = q.Lines.OrderBy(l => l.SortOrder).Select(l => new LineRow(l.Description, UnitText(l.Unit), l.Quantity, l.UnitPrice, hasDiscount ? l.DiscountPercent : null, l.LineTotal)).ToList();
        b.Lines(rows, hasDiscount);
        var totals = new List<(string, decimal, bool)> { (T("Cộng tiền hàng"), q.SubTotal, false) };
        if (q.DiscountAmount != 0) totals.Add((T("Chiết khấu ({0}%)", q.DiscountPercent.ToString("0.##")), -q.DiscountAmount, false));
        totals.Add((T("Thuế GTGT ({0}%)", q.VatPercent.ToString("0.##")), q.VatAmount, false));
        totals.Add((T("TỔNG THANH TOÁN"), q.Total, true));
        b.Totals(totals, q.Total);
        b.TextBlock(T("Điều khoản:"), q.Terms);
        b.Note(s.QuoteNote);
        b.Signatures(T("XÁC NHẬN CỦA KHÁCH HÀNG"), "", T("NGƯỜI LẬP BÁO GIÁ"), q.Owner?.FullName ?? "");
        return (b.Render(), SafeFileName($"BaoGia-{q.Number}") + ".pdf");
    }

    public async Task<(byte[] Data, string FileName)?> ContractAsync(int id)
    {
        var c = await _db.Contracts.Include(x => x.Customer).Include(x => x.Contact).Include(x => x.Owner).Include(x => x.Lines).Include(x => x.Milestones)
            .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return null;
        var s = await SettingsAsync();
        var b = await BuilderAsync(s, T("Hợp đồng {0}", c.Number));
        var info = new List<string> { T("Số: {0}", c.Number) };
        if (c.SignDate.HasValue) info.Add(T("Ngày ký: {0}", c.SignDate.ToDate()));
        info.Add(T("Hiệu lực: {0} – {1}", c.StartDate.ToDate(), c.EndDate.ToDate()));
        b.Header(c.BranchId, T("HỢP ĐỒNG"), info, c.Title);
        var legal = _scope.Snapshot.LegalEntityOf(c.BranchId);
        b.Parties(T("BÊN A (BÊN MUA)"), CustomerLines(c.Customer, c.Contact, T("Đại diện: {0}")),
            T("BÊN B (BÊN BÁN)"), SellerLines(c.BranchId));
        b.Heading(T("Điều 1. Hàng hoá / dịch vụ"));
        b.Lines(c.Lines.OrderBy(l => l.SortOrder).Select(l => new LineRow(l.Description, UnitText(l.Unit), l.Quantity, l.UnitPrice, null, l.LineTotal)).ToList(), false);
        b.Totals(new List<(string, decimal, bool)>
        {
            (T("Giá trị trước thuế"), c.SubTotal, false), (T("Thuế GTGT ({0}%)", c.VatPercent.ToString("0.##")), c.VatAmount, false), (T("TỔNG GIÁ TRỊ HỢP ĐỒNG"), c.Total, true)
        }, c.Total);
        var ms = c.Milestones.OrderBy(m => m.SortOrder).ToList();
        if (ms.Count > 0 || !string.IsNullOrWhiteSpace(c.PaymentTerms))
        {
            b.Heading(T("Điều 2. Lịch thanh toán"));
            if (ms.Count > 0) b.Milestones(ms);
            b.TextBlock(null, c.PaymentTerms);
        }
        if (c.BillingCycle != BillingCycle.None)
            b.Note(T("Định kỳ: {0} · {1} đ/kỳ · kỳ tới {2}", c.BillingCycle.GetDisplayName(), c.RecurringAmount.ToMoney(), c.NextBillingDate.ToDate()));
        var clauses = (s.ContractClauses ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (clauses.Count > 0 || !string.IsNullOrWhiteSpace(c.Notes))
        {
            b.Heading(T("Điều 3. Điều khoản chung"));
            b.Numbered(clauses);
            b.TextBlock(null, c.Notes);
        }
        b.Signatures(T("ĐẠI DIỆN BÊN A"), c.Contact?.FullName ?? "", T("ĐẠI DIỆN BÊN B"), legal?.Representative ?? "");
        return (b.Render(), SafeFileName($"HopDong-{c.Number}") + ".pdf");
    }

    /// <summary>Đề nghị thanh toán theo hoá đơn (không thay thế hoá đơn điện tử)</summary>
    public async Task<(byte[] Data, string FileName)?> InvoiceAsync(int id)
    {
        var inv = await _db.Invoices.Include(x => x.Customer).Include(x => x.Contract).Include(x => x.Milestone).Include(x => x.Owner).Include(x => x.Lines)
            .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (inv == null) return null;
        var s = await SettingsAsync();
        var b = await BuilderAsync(s, T("Đề nghị thanh toán {0}", inv.Number));
        var info = new List<string> { T("Hoá đơn: {0} – {1}", inv.Symbol, inv.Number), T("Ngày: {0}", inv.InvoiceDate.ToDate()) };
        if (inv.DueDate.HasValue) info.Add(T("Hạn thanh toán: {0}", inv.DueDate.ToDate()));
        b.Header(inv.BranchId, T("ĐỀ NGHỊ THANH TOÁN"), info);
        var right = new List<string>();
        if (inv.Contract != null) right.Add(T("Hợp đồng: {0}", inv.Contract.Number));
        if (inv.Milestone != null) right.Add(T("Đợt: {0}", inv.Milestone.Name));
        right.Add(T("Người lập: {0}", inv.Owner?.FullName ?? ""));
        b.Parties(T("Kính gửi"), CustomerLines(inv.Customer, null, ""), T("Thông tin"), right);
        b.Lines(inv.Lines.OrderBy(l => l.SortOrder).Select(l => new LineRow(l.Description, UnitText(l.Unit), l.Quantity, l.UnitPrice, null, l.LineTotal)).ToList(), false);
        var totals = new List<(string, decimal, bool)>
        {
            (T("Cộng tiền hàng"), inv.SubTotal, false), (T("Thuế GTGT ({0}%)", inv.VatPercent.ToString("0.##")), inv.VatAmount, false), (T("TỔNG THANH TOÁN"), inv.Total, true)
        };
        if (inv.PaidAmount > 0) { totals.Add((T("Đã thanh toán"), -inv.PaidAmount, false)); totals.Add((T("CÒN PHẢI THANH TOÁN"), inv.Outstanding, true)); }
        b.Totals(totals, inv.PaidAmount > 0 ? inv.Outstanding : inv.Total);
        var legal = _scope.Snapshot.LegalEntityOf(inv.BranchId);
        var pay = new List<string>();
        if (!string.IsNullOrWhiteSpace(legal?.BankAccount)) pay.Add(T("Tài khoản nhận: {0}", legal!.BankAccount!));
        pay.Add(T("Nội dung: {0}", $"{inv.Number} {inv.Customer?.Code}".Trim()));
        if (!string.IsNullOrWhiteSpace(s.PaymentNote)) pay.Add(s.PaymentNote!);
        b.TextBlock(T("Thông tin thanh toán:"), string.Join("\n", pay));
        b.TextBlock(T("Ghi chú:"), inv.Notes);
        b.Note(T("Chứng từ này dùng để đề nghị thanh toán, không thay thế hoá đơn điện tử."));
        b.Signatures(T("KẾ TOÁN"), "", T("NGƯỜI LẬP"), inv.Owner?.FullName ?? "");
        return (b.Render(), SafeFileName($"DeNghiThanhToan-{inv.Number}") + ".pdf");
    }

    public Task<(byte[] Data, string FileName)?> RenderAsync(string type, int id) => type switch
    {
        nameof(Quote) => QuoteAsync(id),
        nameof(Contract) => ContractAsync(id),
        nameof(Invoice) => InvoiceAsync(id),
        _ => Task.FromResult<(byte[], string)?>(null)
    };

    // ======================= Dữ liệu dùng chung =======================

    private string UnitText(string? u) => _catalogs.Text(CatalogGroup.Unit, u);

    private List<string> CustomerLines(Customer? c, Contact? contact, string contactFormat)
    {
        var l = new List<string>();
        if (c == null) return l;
        l.Add("**" + c.Name);
        var addr = string.Join(", ", new[] { c.Address, _catalogs.Text(CatalogGroup.City, c.City) }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (addr.Length > 0) l.Add(T("Địa chỉ: {0}", addr));
        if (!string.IsNullOrWhiteSpace(c.TaxCode)) l.Add(T("MST: {0}", c.TaxCode));
        if (!string.IsNullOrWhiteSpace(c.Phone)) l.Add(T("ĐT: {0}", c.Phone));
        if (contact != null && contactFormat.Length > 0) l.Add(string.Format(contactFormat, contact.FullName + (string.IsNullOrWhiteSpace(contact.JobTitle) ? "" : " – " + contact.JobTitle)));
        return l;
    }

    private List<string> SellerLines(int branchId)
    {
        var snap = _scope.Snapshot;
        var unit = snap.ById.GetValueOrDefault(branchId);
        var legal = snap.LegalEntityOf(branchId);
        var l = new List<string>();
        if (legal == null) return l;
        l.Add("**" + legal.Name + (unit != null && unit.Id != legal.Id ? " – " + unit.Name : ""));
        var addr = unit?.Address ?? legal.Address;
        if (!string.IsNullOrWhiteSpace(addr)) l.Add(T("Địa chỉ: {0}", addr));
        if (!string.IsNullOrWhiteSpace(legal.TaxCode)) l.Add(T("MST: {0}", legal.TaxCode));
        var phone = unit?.Phone ?? legal.Phone;
        if (!string.IsNullOrWhiteSpace(phone)) l.Add(T("ĐT: {0}", phone));
        if (!string.IsNullOrWhiteSpace(legal.Representative)) l.Add(T("Đại diện: {0}", legal.Representative));
        if (!string.IsNullOrWhiteSpace(legal.BankAccount)) l.Add(T("TK: {0}", legal.BankAccount));
        return l;
    }

    private async Task<DocBuilder> BuilderAsync(PdfSettings s, string title)
    {
        if (!FontsAvailable) throw new InvalidOperationException(T("Máy chủ chưa có font hỗ trợ tiếng Việt để tạo PDF (cần Arial, Liberation Sans hoặc DejaVu Sans)."));
        var logo = s.ShowLogo ? await LogoAsync() : null;
        return new DocBuilder(title, s, logo, this);
    }

    internal record LineRow(string Description, string Unit, decimal Qty, decimal Price, decimal? Discount, decimal Total);

    /// <summary>Dựng tài liệu MigraDoc theo bố cục chung của các chứng từ</summary>
    internal class DocBuilder
    {
        private readonly Document _doc = new();
        private readonly Section _sec;
        private readonly Color _accent, _light, _border = new(0xC8, 0xCE, 0xD6), _muted = new(0x60, 0x6A, 0x76);
        private readonly string? _logo;
        private readonly PdfService _svc;

        public DocBuilder(string title, PdfSettings s, string? logo, PdfService svc)
        {
            _svc = svc; _logo = logo;
            _accent = ParseColor(s.Accent) ?? new Color(0x1F, 0x4E, 0x79);
            _light = new Color((byte)(255 - (255 - _accent.R) / 10), (byte)(255 - (255 - _accent.G) / 10), (byte)(255 - (255 - _accent.B) / 10));
            _doc.Info.Title = title; _doc.Info.Author = "CRM";
            var normal = _doc.Styles[StyleNames.Normal]!;
            normal.Font.Name = CrmFontResolver.Family; normal.Font.Size = 9.5;
            normal.ParagraphFormat.SpaceAfter = 1;
            _sec = _doc.AddSection();
            var ps = _sec.PageSetup;
            ps.PageFormat = PageFormat.A4; ps.TopMargin = ps.BottomMargin = Unit.FromCentimeter(1.5);
            ps.LeftMargin = ps.RightMargin = Unit.FromCentimeter(1.6); ps.FooterDistance = Unit.FromCentimeter(0.7);
            var f = _sec.Footers.Primary.AddParagraph();
            f.Format.Font.Size = 7.5; f.Format.Font.Color = _muted; f.Format.Alignment = ParagraphAlignment.Center;
            f.Format.Borders.Top.Width = 0.5; f.Format.Borders.Top.Color = _border; f.Format.Borders.Distance = 3;
            if (!string.IsNullOrWhiteSpace(s.Footer)) f.AddText(s.Footer!.Trim() + "   ·   ");
            f.AddText(T("Trang") + " "); f.AddPageField(); f.AddText("/"); f.AddNumPagesField();
        }

        private static Color? ParseColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            var h = hex.Trim().TrimStart('#');
            return h.Length == 6 && int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
                ? new Color((byte)(v >> 16), (byte)(v >> 8 & 0xFF), (byte)(v & 0xFF)) : null;
        }

        private double Width => 21.0 - 3.2;

        /// <summary>Kích thước điểm ảnh của PNG / JPEG (0,0 nếu không đọc được)</summary>
        internal static (int W, int H) ImageSize(byte[] b)
        {
            if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50)
                return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
            if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
            {
                var p = 2;
                while (p + 9 < b.Length)
                {
                    if (b[p] != 0xFF) { p++; continue; }
                    var marker = b[p + 1];
                    var len = (b[p + 2] << 8) | b[p + 3];
                    // SOF0..SOF15 (trừ DHT C4, JPG C8, DAC CC) chứa kích thước ảnh
                    if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                        return ((b[p + 7] << 8) | b[p + 8], (b[p + 5] << 8) | b[p + 6]);
                    p += 2 + len;
                }
            }
            return (0, 0);
        }

        private static void AddLines(Cell cell, IEnumerable<string> lines, double size = 9)
        {
            foreach (var line in lines.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var p = cell.AddParagraph();
                p.Format.Font.Size = size;
                if (line.StartsWith("**")) { p.AddFormattedText(line[2..], TextFormat.Bold); p.Format.Font.Size = size + 1; }
                else p.AddText(line);
            }
        }

        public void Header(int branchId, string docTitle, IEnumerable<string> info, string? subtitle = null)
        {
            var t = _sec.AddTable();
            t.Borders.Visible = false;
            var hasLogo = !string.IsNullOrEmpty(_logo);
            if (hasLogo) t.AddColumn(Unit.FromCentimeter(3.2));
            t.AddColumn(Unit.FromCentimeter(Width - 6.4 - (hasLogo ? 3.2 : 0)));
            t.AddColumn(Unit.FromCentimeter(6.4));
            var r = t.AddRow();
            r.VerticalAlignment = VerticalAlignment.Top;
            var i = 0;
            if (hasLogo)
            {
                var img = r.Cells[i++].AddImage("base64:" + _logo);
                img.LockAspectRatio = true;
                // Co ảnh vừa khung 2,9 × 2,0 cm, giữ nguyên tỉ lệ
                var (w, h) = ImageSize(Convert.FromBase64String(_logo!));
                if (w > 0 && h > 0 && (double)w / h < 2.9 / 2.0) img.Height = Unit.FromCentimeter(2.0);
                else img.Width = Unit.FromCentimeter(2.9);
            }
            var seller = r.Cells[i++];
            AddLines(seller, _svc.SellerLines(branchId).Where(x => !x.StartsWith(T("Đại diện: {0}", "").TrimEnd())), 8.5);
            var right = r.Cells[i];
            right.Format.Alignment = ParagraphAlignment.Right;
            var tp = right.AddParagraph(docTitle);
            tp.Format.Font.Size = docTitle.Length > 12 ? 14 : 17; tp.Format.Font.Bold = true; tp.Format.Font.Color = _accent; tp.Format.SpaceAfter = 3;
            foreach (var x in info) { var p = right.AddParagraph(x); p.Format.Font.Size = 9; }
            var line = _sec.AddParagraph();
            line.Format.Borders.Bottom.Width = 1.5; line.Format.Borders.Bottom.Color = _accent; line.Format.SpaceAfter = 6; line.Format.Font.Size = 2;
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                var st = _sec.AddParagraph(subtitle!);
                st.Format.Alignment = ParagraphAlignment.Center; st.Format.Font.Size = 12; st.Format.Font.Bold = true; st.Format.SpaceAfter = 6;
            }
        }

        public void Parties(string leftTitle, IEnumerable<string> left, string rightTitle, IEnumerable<string> right)
        {
            var t = _sec.AddTable();
            t.Borders.Visible = false;
            t.TopPadding = t.BottomPadding = 4; t.LeftPadding = t.RightPadding = 6;
            t.AddColumn(Unit.FromCentimeter(Width / 2 + 0.6)); t.AddColumn(Unit.FromCentimeter(Width / 2 - 0.6));
            var r = t.AddRow();
            foreach (var (cell, title, lines) in new[] { (r.Cells[0], leftTitle, left), (r.Cells[1], rightTitle, right) })
            {
                cell.Shading.Color = _light;
                var h = cell.AddParagraph(title); h.Format.Font.Size = 8; h.Format.Font.Color = _muted; h.Format.Font.Bold = true; h.Format.SpaceAfter = 2;
                AddLines(cell, lines);
            }
            r.Cells[0].Borders.Right.Width = 4; r.Cells[0].Borders.Right.Color = Colors.White;
            _sec.AddParagraph().Format.SpaceAfter = 4;
        }

        public void Heading(string text)
        {
            var p = _sec.AddParagraph(text);
            p.Format.Font.Bold = true; p.Format.Font.Size = 10.5; p.Format.Font.Color = _accent; p.Format.SpaceBefore = 8; p.Format.SpaceAfter = 4;
        }

        private Table Grid(params (double Cm, ParagraphAlignment Align)[] cols)
        {
            var t = _sec.AddTable();
            t.Borders.Width = 0.5; t.Borders.Color = _border; t.TopPadding = t.BottomPadding = 2.5;
            t.Format.Font.Size = 9;
            foreach (var (cm, al) in cols) { var c = t.AddColumn(Unit.FromCentimeter(cm)); c.Format.Alignment = al; }
            return t;
        }

        private void HeaderRow(Table t, params string[] titles)
        {
            var r = t.AddRow();
            r.HeadingFormat = true; r.Shading.Color = _accent; r.Format.Font.Bold = true; r.Format.Font.Color = Colors.White;
            r.VerticalAlignment = VerticalAlignment.Center;
            for (var i = 0; i < titles.Length; i++) { r.Cells[i].AddParagraph(titles[i]); }
        }

        public void Lines(List<LineRow> rows, bool withDiscount)
        {
            var desc = Width - 1.0 - 2.1 - 1.4 - 2.6 - 2.9 - (withDiscount ? 1.3 : 0);
            var cols = new List<(double, ParagraphAlignment)> { (1.0, ParagraphAlignment.Center), (desc, ParagraphAlignment.Left), (2.1, ParagraphAlignment.Center),
                (1.4, ParagraphAlignment.Right), (2.6, ParagraphAlignment.Right) };
            if (withDiscount) cols.Add((1.3, ParagraphAlignment.Right));
            cols.Add((2.9, ParagraphAlignment.Right));
            var t = Grid(cols.ToArray());
            var titles = new List<string> { T("STT"), T("Hàng hoá / dịch vụ"), T("ĐVT"), T("SL"), T("Đơn giá") };
            if (withDiscount) titles.Add("CK %");
            titles.Add(T("Thành tiền"));
            HeaderRow(t, titles.ToArray());
            var n = 1;
            foreach (var l in rows)
            {
                var r = t.AddRow();
                if (n % 2 == 0) r.Shading.Color = _light;
                var vals = new List<string> { (n++).ToString(), l.Description, l.Unit, l.Qty.ToString("0.##"), l.Price.ToMoney() };
                if (withDiscount) vals.Add(l.Discount > 0 ? l.Discount.Value.ToString("0.##") : "");
                vals.Add(l.Total.ToMoney());
                for (var i = 0; i < vals.Count; i++) { r.Cells[i].AddParagraph(vals[i]); }
            }
            if (rows.Count == 0) { var r = t.AddRow(); r.Cells[0].MergeRight = cols.Count - 1; r.Cells[0].AddParagraph(T("Chưa có dòng hàng hoá.")); }
        }

        public void Totals(List<(string Label, decimal Amount, bool Strong)> lines, decimal inWords)
        {
            var t = _sec.AddTable();
            t.Borders.Visible = false; t.Format.Font.Size = 9.5;
            t.AddColumn(Unit.FromCentimeter(Width - 3.6)).Format.Alignment = ParagraphAlignment.Right;
            t.AddColumn(Unit.FromCentimeter(3.6)).Format.Alignment = ParagraphAlignment.Right;
            foreach (var (label, amount, strong) in lines)
            {
                var r = t.AddRow(); r.TopPadding = 2;
                r.Cells[0].AddParagraph(label); r.Cells[1].AddParagraph(amount.ToMoney());
                if (strong) { r.Format.Font.Bold = true; r.Format.Font.Size = 10.5; r.Cells[1].Format.Font.Color = _accent; }
            }
            var w = _sec.AddParagraph();
            w.Format.SpaceBefore = 4; w.Format.SpaceAfter = 4;
            w.AddFormattedText(T("Số tiền bằng chữ:") + " ", TextFormat.Bold);
            w.AddFormattedText(MoneyWords.Text(inWords) + ".", TextFormat.Italic);
        }

        public void Milestones(List<ContractMilestone> ms)
        {
            var t = Grid((1.0, ParagraphAlignment.Center), (Width - 1.0 - 1.5 - 2.9 - 2.4 - 4.0, ParagraphAlignment.Left), (1.5, ParagraphAlignment.Right),
                (2.9, ParagraphAlignment.Right), (2.4, ParagraphAlignment.Center), (4.0, ParagraphAlignment.Left));
            HeaderRow(t, T("STT"), T("Đợt thanh toán"), T("Tỷ lệ"), T("Số tiền"), T("Hạn thanh toán"), T("Điều kiện"));
            var n = 1;
            foreach (var m in ms)
            {
                var r = t.AddRow();
                var vals = new[] { (n++).ToString(), m.Name, m.Percent.ToString("0.##") + "%", m.Amount.ToMoney(), m.DueDate.ToDate(), m.Condition ?? "" };
                for (var i = 0; i < vals.Length; i++) { r.Cells[i].AddParagraph(vals[i]); }
            }
            _sec.AddParagraph().Format.SpaceAfter = 2;
        }

        public void Numbered(List<string> items)
        {
            var n = 1;
            foreach (var x in items)
            {
                var p = _sec.AddParagraph($"{n++}. {x}");
                p.Format.LeftIndent = Unit.FromCentimeter(0.5); p.Format.FirstLineIndent = Unit.FromCentimeter(-0.5); p.Format.SpaceAfter = 2;
            }
        }

        public void TextBlock(string? title, string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var p = _sec.AddParagraph();
            p.Format.SpaceBefore = 3; p.Format.SpaceAfter = 3;
            if (!string.IsNullOrEmpty(title)) { p.AddFormattedText(title, TextFormat.Bold); p.AddLineBreak(); }
            var lines = text!.Replace("\r", "").Split('\n');
            for (var i = 0; i < lines.Length; i++) { if (i > 0) p.AddLineBreak(); p.AddText(lines[i]); }
        }

        public void Note(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var p = _sec.AddParagraph(text!);
            p.Format.Font.Italic = true; p.Format.Font.Size = 8.5; p.Format.Font.Color = _muted; p.Format.SpaceBefore = 4;
        }

        public void Signatures(string leftTitle, string leftName, string rightTitle, string rightName)
        {
            _sec.AddParagraph().Format.SpaceAfter = 10;
            var t = _sec.AddTable();
            t.Borders.Visible = false; t.KeepTogether = true;
            t.AddColumn(Unit.FromCentimeter(Width / 2)).Format.Alignment = ParagraphAlignment.Center;
            t.AddColumn(Unit.FromCentimeter(Width / 2)).Format.Alignment = ParagraphAlignment.Center;
            var r = t.AddRow();
            foreach (var (cell, title, name) in new[] { (r.Cells[0], leftTitle, leftName), (r.Cells[1], rightTitle, rightName) })
            {
                var h = cell.AddParagraph(title); h.Format.Font.Bold = true;
                var hint = cell.AddParagraph(T("(Ký, ghi rõ họ tên)")); hint.Format.Font.Italic = true; hint.Format.Font.Size = 8; hint.Format.Font.Color = _muted;
                hint.Format.SpaceAfter = Unit.FromCentimeter(2.0);
                if (!string.IsNullOrWhiteSpace(name)) { var p = cell.AddParagraph(name); p.Format.Font.Bold = true; }
            }
        }

        public byte[] Render()
        {
            var renderer = new PdfDocumentRenderer { Document = _doc };
            renderer.RenderDocument();
            using var ms = new MemoryStream();
            renderer.PdfDocument.Save(ms, false);
            return ms.ToArray();
        }
    }
}
