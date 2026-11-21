using System.Net;
using System.Security.Claims;
using System.Text;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>Ngữ cảnh soạn email từ một bản ghi</summary>
public class EmailContext
{
    public string Type { get; init; } = "";
    public int Id { get; init; }
    public string Title { get; set; } = "";
    public string? To { get; set; }
    public int? CustomerId { get; set; }
    public int? ContactId { get; set; }
    public int? OpportunityId { get; set; }
    public AttachmentTarget? AttachmentTarget { get; set; }
    public Dictionary<string, string?> Vars { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Bảng chứng từ (báo giá / hợp đồng / hoá đơn) dạng HTML để chèn vào email</summary>
    public string? DocumentHtml { get; set; }
    public string? BackUrl { get; set; }
    /// <summary>Gợi ý mẫu theo phạm vi</summary>
    public string Scope => Type;
    /// <summary>Danh sách email gợi ý (người liên hệ của khách hàng)</summary>
    public List<(string Name, string Email)> Suggestions { get; } = new();
}

/// <summary>Dựng ngữ cảnh email cho từng loại bản ghi (kiểm tra quyền xem bản ghi)</summary>
public class EmailComposer
{
    /// <summary>Các biến trộn dùng được trong mẫu email</summary>
    public static readonly (string Name, string Desc)[] Variables =
    {
        ("KhachHang", "Tên khách hàng"), ("NguoiLienHe", "Tên người liên hệ"), ("NhanVien", "Tên nhân viên gửi"),
        ("EmailNhanVien", "Email nhân viên gửi"), ("CongTy", "Tên công ty bán"), ("Ngay", "Ngày hôm nay"),
        ("SoChungTu", "Số báo giá / hợp đồng / hoá đơn / phiếu"), ("TongTien", "Tổng giá trị"), ("HanThanhToan", "Hạn thanh toán / hiệu lực"),
        ("ConPhaiThu", "Số tiền còn phải thu (hoá đơn)"), ("TieuDe", "Tiêu đề hợp đồng / phiếu hỗ trợ"), ("TrangThai", "Trạng thái"),
        ("Ten", "Tên đối tượng (chiến dịch)"), ("LinkHuyDangKy", "Liên kết từ chối nhận tin (chiến dịch)")
    };

    private readonly CrmDbContext _db;
    private readonly BranchDirectory _dir;
    public EmailComposer(CrmDbContext db, BranchDirectory dir) { _db = db; _dir = dir; }

    public static readonly string[] Types = { nameof(Customer), nameof(Contact), nameof(Quote), nameof(Contract), nameof(Invoice), nameof(Ticket), nameof(Opportunity) };

    public async Task<EmailContext?> BuildAsync(string type, int id, ClaimsPrincipal user)
    {
        var me = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == user.GetUserId());
        var ctx = new EmailContext { Type = type, Id = id };
        ctx.Vars["NhanVien"] = me.FullName; ctx.Vars["EmailNhanVien"] = me.Email; ctx.Vars["Ngay"] = DateTime.Today.ToDate();
        int branchId;
        switch (type)
        {
            case nameof(Customer):
            {
                var c = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                if (c == null || !user.CanAccess(c)) return null;
                ctx.Title = c.Name; ctx.CustomerId = c.Id; ctx.To = c.Email; ctx.AttachmentTarget = Models.Entities.AttachmentTarget.Customer;
                ctx.BackUrl = $"/Customers/Details/{id}"; branchId = c.BranchId;
                break;
            }
            case nameof(Contact):
            {
                var c = await _db.Contacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                if (c == null || !user.CanAccess(c)) return null;
                ctx.Title = c.FullName; ctx.ContactId = c.Id; ctx.CustomerId = c.CustomerId; ctx.To = c.Email;
                ctx.Vars["NguoiLienHe"] = c.FullName; ctx.BackUrl = c.CustomerId.HasValue ? $"/Customers/Details/{c.CustomerId}" : "/Contacts"; branchId = c.BranchId;
                break;
            }
            case nameof(Opportunity):
            {
                var o = await _db.Opportunities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                if (o == null || !user.CanAccess(o)) return null;
                ctx.Title = o.Name; ctx.CustomerId = o.CustomerId; ctx.ContactId = o.ContactId; ctx.OpportunityId = o.Id;
                ctx.AttachmentTarget = Models.Entities.AttachmentTarget.Opportunity; ctx.BackUrl = $"/Opportunities/Details/{id}"; branchId = o.BranchId;
                ctx.Vars["TieuDe"] = o.Name; ctx.Vars["TongTien"] = o.Amount.ToMoney() + " đ";
                break;
            }
            case nameof(Quote):
            {
                var q = await _db.Quotes.Include(x => x.Lines).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                if (q == null || !user.CanAccess(q)) return null;
                ctx.Title = q.Number; ctx.CustomerId = q.CustomerId; ctx.ContactId = q.ContactId; ctx.OpportunityId = q.OpportunityId;
                ctx.AttachmentTarget = Models.Entities.AttachmentTarget.Quote; ctx.BackUrl = $"/Quotes/Details/{id}"; branchId = q.BranchId;
                ctx.Vars["SoChungTu"] = q.Number; ctx.Vars["TongTien"] = q.Total.ToMoney() + " đ"; ctx.Vars["HanThanhToan"] = q.ValidUntil.ToDate();
                ctx.Vars["TrangThai"] = q.Status.GetDisplayName();
                ctx.DocumentHtml = Table(I18n.T("BÁO GIÁ {0}", q.Number), q.Lines.OrderBy(l => l.SortOrder)
                        .Select(l => (l.Description, l.Unit, l.Quantity, Math.Round(l.UnitPrice * (1 - l.DiscountPercent / 100m), 0), l.LineTotal)),
                    new[] { (I18n.T("Cộng tiền hàng"), q.SubTotal), (I18n.T("Chiết khấu"), -q.DiscountAmount), (I18n.T("Thuế GTGT"), q.VatAmount), (I18n.T("Tổng thanh toán"), q.Total) },
                    I18n.T("Báo giá có hiệu lực đến ngày {0}.", q.ValidUntil.ToDate()));
                break;
            }
            case nameof(Contract):
            {
                var c = await _db.Contracts.Include(x => x.Lines).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                if (c == null || !user.CanAccess(c)) return null;
                ctx.Title = c.Number; ctx.CustomerId = c.CustomerId; ctx.ContactId = c.ContactId; ctx.OpportunityId = c.OpportunityId;
                ctx.AttachmentTarget = Models.Entities.AttachmentTarget.Contract; ctx.BackUrl = $"/Contracts/Details/{id}"; branchId = c.BranchId;
                ctx.Vars["SoChungTu"] = c.Number; ctx.Vars["TieuDe"] = c.Title; ctx.Vars["TongTien"] = c.Total.ToMoney() + " đ";
                ctx.Vars["HanThanhToan"] = c.EndDate.ToDate(); ctx.Vars["TrangThai"] = c.Status.GetDisplayName();
                ctx.DocumentHtml = Table(I18n.T("HỢP ĐỒNG {0} – {1}", c.Number, c.Title), c.Lines.OrderBy(l => l.SortOrder)
                        .Select(l => (l.Description, l.Unit, l.Quantity, l.UnitPrice, l.LineTotal)),
                    new[] { (I18n.T("Giá trị trước thuế"), c.SubTotal), (I18n.T("Thuế GTGT"), c.VatAmount), (I18n.T("Tổng giá trị"), c.Total) }, null);
                break;
            }
            case nameof(Invoice):
            {
                var v = await _db.Invoices.Include(x => x.Lines).Include(x => x.Contract).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                if (v == null || !user.CanAccess(v)) return null;
                ctx.Title = v.Symbol + " " + v.Number; ctx.CustomerId = v.CustomerId; ctx.ContactId = v.Contract?.ContactId;
                ctx.AttachmentTarget = Models.Entities.AttachmentTarget.Invoice; ctx.BackUrl = $"/Invoices/Details/{id}"; branchId = v.BranchId;
                ctx.Vars["SoChungTu"] = v.Symbol + " " + v.Number; ctx.Vars["TongTien"] = v.Total.ToMoney() + " đ";
                ctx.Vars["ConPhaiThu"] = (v.Total - v.PaidAmount).ToMoney() + " đ"; ctx.Vars["HanThanhToan"] = v.DueDate.ToDate();
                ctx.Vars["TrangThai"] = v.Status.GetDisplayName();
                ctx.DocumentHtml = Table(I18n.T("HOÁ ĐƠN {0}", v.Symbol + " " + v.Number), v.Lines.OrderBy(l => l.SortOrder)
                        .Select(l => (l.Description, l.Unit, l.Quantity, l.UnitPrice, l.LineTotal)),
                    new[] { (I18n.T("Cộng tiền hàng"), v.SubTotal), (I18n.T("Thuế GTGT"), v.VatAmount), (I18n.T("Tổng thanh toán"), v.Total), (I18n.T("Đã thu"), v.PaidAmount), (I18n.T("Còn phải thu"), v.Total - v.PaidAmount) },
                    v.DueDate.HasValue ? I18n.T("Hạn thanh toán: {0}.", v.DueDate.ToDate()) : null);
                break;
            }
            case nameof(Ticket):
            {
                var t = await _db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
                if (t == null || !(user.IsSupportDesk() || t.OwnerId == user.GetUserId())) return null;
                ctx.Title = t.Number; ctx.CustomerId = t.CustomerId; ctx.ContactId = t.ContactId;
                ctx.AttachmentTarget = Models.Entities.AttachmentTarget.Ticket; ctx.BackUrl = $"/Tickets/Details/{id}"; branchId = t.BranchId;
                ctx.Vars["SoChungTu"] = t.Number; ctx.Vars["TieuDe"] = t.Subject; ctx.Vars["TrangThai"] = t.Status.GetDisplayName();
                break;
            }
            default: return null;
        }

        if (ctx.CustomerId.HasValue)
        {
            var cust = await _db.Customers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == ctx.CustomerId);
            ctx.Vars["KhachHang"] = cust?.Name;
            var contacts = await _db.Contacts.IgnoreQueryFilters().AsNoTracking().Where(x => x.CustomerId == ctx.CustomerId && x.Email != null)
                .OrderByDescending(x => x.IsPrimary).ThenBy(x => x.FullName).Select(x => new { x.Id, x.FullName, x.Email }).ToListAsync();
            ctx.Suggestions.AddRange(contacts.Select(x => (x.FullName, x.Email!)));
            if (cust?.Email != null && !ctx.Suggestions.Any(s => s.Email == cust.Email)) ctx.Suggestions.Add((cust.Name, cust.Email));
            ctx.ContactId ??= contacts.FirstOrDefault()?.Id;
        }
        if (ctx.ContactId.HasValue)
        {
            var ct = await _db.Contacts.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == ctx.ContactId);
            if (ct != null) { ctx.Vars.TryAdd("NguoiLienHe", ct.FullName); if (!string.IsNullOrEmpty(ct.Email)) ctx.To ??= ct.Email; }
        }
        ctx.To ??= ctx.Suggestions.FirstOrDefault().Email;
        if (ctx.Vars.GetValueOrDefault("NguoiLienHe") == null) ctx.Vars["NguoiLienHe"] = ctx.Vars.GetValueOrDefault("KhachHang");
        ctx.Vars["CongTy"] = _dir.Get().LegalEntityOf(branchId)?.Name;
        return ctx;
    }

    /// <summary>Bảng hàng hoá (HTML nội tuyến, hiển thị được trong mọi trình đọc thư)</summary>
    private static string Table(string title, IEnumerable<(string Desc, string? Unit, decimal Qty, decimal Price, decimal Total)> lines,
        IEnumerable<(string Label, decimal Value)> totals, string? note)
    {
        const string th = "style=\"padding:6px 8px;border:1px solid #d1d5db;background:#f3f4f6;text-align:left\"";
        const string td = "style=\"padding:6px 8px;border:1px solid #e5e7eb\"";
        const string tdr = "style=\"padding:6px 8px;border:1px solid #e5e7eb;text-align:right;white-space:nowrap\"";
        var sb = new StringBuilder();
        sb.Append("<div style=\"margin:16px 0\"><div style=\"font-weight:600;margin-bottom:6px\">").Append(WebUtility.HtmlEncode(title)).Append("</div>");
        sb.Append("<table style=\"border-collapse:collapse;width:100%;font-size:13px\"><tr>")
          .Append($"<th {th}>#</th><th {th}>{I18n.T("Hàng hoá / dịch vụ")}</th><th {th}>{I18n.T("ĐVT")}</th><th {th}>{I18n.T("SL")}</th><th {th}>{I18n.T("Đơn giá")}</th><th {th}>{I18n.T("Thành tiền")}</th></tr>");
        var i = 1;
        foreach (var l in lines)
            sb.Append($"<tr><td {td}>{i++}</td><td {td}>{WebUtility.HtmlEncode(l.Desc)}</td><td {td}>{WebUtility.HtmlEncode(l.Unit ?? "")}</td>")
              .Append($"<td {tdr}>{l.Qty:0.##}</td><td {tdr}>{l.Price.ToMoney()}</td><td {tdr}>{l.Total.ToMoney()}</td></tr>");
        foreach (var (label, value) in totals)
            sb.Append($"<tr><td {td} colspan=\"5\" align=\"right\"><b>{WebUtility.HtmlEncode(label)}</b></td><td {tdr}><b>{value.ToMoney()}</b></td></tr>");
        sb.Append("</table>");
        if (note != null) sb.Append("<div style=\"color:#6b7280;font-size:12px;margin-top:4px\">").Append(WebUtility.HtmlEncode(note)).Append("</div>");
        return sb.Append("</div>").ToString();
    }
}
