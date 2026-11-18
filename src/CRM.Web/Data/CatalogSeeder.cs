using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Data;

/// <summary>
/// Nạp danh mục mặc định + các giá trị đang được dùng trong dữ liệu nghiệp vụ.
/// Chạy lại nhiều lần an toàn.
/// </summary>
public static class CatalogSeeder
{
    public static async Task SeedAsync(CrmDbContext db)
    {
        var existing = await db.CatalogItems.Select(x => new { x.Group, x.Name, x.Code }).ToListAsync();
        var codes = existing.GroupBy(x => x.Group).ToDictionary(x => x.Key, x => (ICollection<string>)x.Select(i => i.Code ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase));
        var known = existing.Select(x => (x.Group, x.Name.ToLowerInvariant())).ToHashSet();
        var groupsWithData = existing.Select(x => x.Group).ToHashSet();

        void Add(CatalogGroup g, string name, string? en, int order)
        {
            name = name.Trim();
            if (name.Length == 0 || name.Length > 200 || !known.Add((g, name.ToLowerInvariant()))) return;
            if (!codes.TryGetValue(g, out var used)) codes[g] = used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var code = CRM.Web.Infrastructure.CatalogCode.Suggest(name, used);
            used.Add(code);
            db.CatalogItems.Add(new CatalogItem { Group = g, Code = code, Name = name, NameEn = en, SortOrder = order });
        }

        // Danh mục mặc định: chỉ nạp cho nhóm chưa có mục nào (không ghi đè chỉnh sửa của người dùng)
        foreach (var (group, items) in Defaults)
        {
            if (groupsWithData.Contains(group)) continue;
            var order = 10;
            foreach (var (vi, en) in items) { Add(group, vi, en, order); order += 10; }
        }

        // Giá trị đang dùng trong dữ liệu nhưng chưa có trong danh mục → thêm vào để chọn lại được
        var used = new (CatalogGroup Group, List<string> Values)[]
        {
            (CatalogGroup.Industry, await db.Customers.Where(x => x.Industry != null).Select(x => x.Industry!).Distinct().ToListAsync()),
            (CatalogGroup.City, await db.Customers.Where(x => x.City != null).Select(x => x.City!).Distinct().ToListAsync()),
            (CatalogGroup.ProductCategory, await db.Products.Where(x => x.Category != null).Select(x => x.Category!).Distinct().ToListAsync()),
            (CatalogGroup.Unit, await db.Products.Select(x => x.Unit).Distinct().ToListAsync()),
        };
        foreach (var (group, values) in used)
            foreach (var v in values) Add(group, v, null, 900);

        await db.SaveChangesAsync();
    }

    private static readonly (CatalogGroup, (string Vi, string En)[])[] Defaults =
    {
        (CatalogGroup.Industry, new[]
        {
            ("Y tế", "Healthcare"), ("Dược phẩm", "Pharmaceuticals"), ("Giáo dục", "Education"),
            ("Sản xuất", "Manufacturing"), ("Bán lẻ", "Retail"), ("Tài chính – Ngân hàng", "Finance – Banking"),
            ("Vận tải – Logistics", "Transport – Logistics"), ("Thực phẩm – Đồ uống", "Food – Beverage"),
            ("Công nghệ thông tin", "Information technology"), ("Xây dựng – Bất động sản", "Construction – Real estate"),
            ("Khác", "Other"),
        }),
        (CatalogGroup.City, new[]
        {
            ("Hà Nội", "Hanoi"), ("TP. Hồ Chí Minh", "Ho Chi Minh City"), ("Hải Phòng", "Hai Phong"), ("Đà Nẵng", "Da Nang"),
            ("Cần Thơ", "Can Tho"), ("Huế", "Hue"), ("An Giang", "An Giang"), ("Bắc Ninh", "Bac Ninh"), ("Cà Mau", "Ca Mau"),
            ("Cao Bằng", "Cao Bang"), ("Đắk Lắk", "Dak Lak"), ("Điện Biên", "Dien Bien"), ("Đồng Nai", "Dong Nai"),
            ("Đồng Tháp", "Dong Thap"), ("Gia Lai", "Gia Lai"), ("Hà Tĩnh", "Ha Tinh"), ("Hưng Yên", "Hung Yen"),
            ("Khánh Hòa", "Khanh Hoa"), ("Lai Châu", "Lai Chau"), ("Lâm Đồng", "Lam Dong"), ("Lạng Sơn", "Lang Son"),
            ("Lào Cai", "Lao Cai"), ("Nghệ An", "Nghe An"), ("Ninh Bình", "Ninh Binh"), ("Phú Thọ", "Phu Tho"),
            ("Quảng Ngãi", "Quang Ngai"), ("Quảng Ninh", "Quang Ninh"), ("Quảng Trị", "Quang Tri"), ("Sơn La", "Son La"),
            ("Tây Ninh", "Tay Ninh"), ("Thái Nguyên", "Thai Nguyen"), ("Thanh Hóa", "Thanh Hoa"), ("Tuyên Quang", "Tuyen Quang"),
            ("Vĩnh Long", "Vinh Long"),
        }),
        (CatalogGroup.ProductCategory, new[]
        {
            ("Phần mềm", "Software"), ("Dịch vụ", "Services"), ("Phần cứng", "Hardware"),
        }),
        (CatalogGroup.Unit, new[]
        {
            ("Gói", "Package"), ("Gói/năm", "Package/year"), ("User/năm", "User/year"), ("Ngày công", "Man-day"),
            ("Buổi", "Session"), ("Giờ", "Hour"), ("Tháng", "Month"), ("Năm", "Year"), ("Cái", "Piece"), ("Bộ", "Set"),
        }),
        (CatalogGroup.LossReason, new[]
        {
            ("Khách chọn nhà cung cấp khác giá thấp hơn", "Customer chose a cheaper competitor"),
            ("Giá vượt ngân sách của khách", "Price exceeded the customer's budget"),
            ("Không đáp ứng yêu cầu kỹ thuật", "Did not meet technical requirements"),
            ("Khách hoãn / hủy dự án", "Customer postponed / cancelled the project"),
            ("Mất liên lạc với khách hàng", "Lost contact with the customer"),
            ("Khác", "Other"),
        }),
        (CatalogGroup.CostType, new[]
        {
            ("Vận chuyển", "Shipping"), ("Lắp đặt – triển khai", "Installation – deployment"), ("Đào tạo", "Training"),
            ("Hoa hồng – môi giới", "Commission – brokerage"), ("Bảo hành – bảo trì", "Warranty – maintenance"),
            ("Chi phí tài chính", "Financial costs"), ("Tiếp khách – công tác", "Entertainment – travel"),
            ("Thuê ngoài", "Outsourcing"), ("Khác", "Other"),
        }),
        (CatalogGroup.Brand, new[]
        {
            ("Microsoft", "Microsoft"), ("Oracle", "Oracle"), ("SAP", "SAP"), ("Dell", "Dell"), ("HP", "HP"), ("Lenovo", "Lenovo"),
            ("Cisco", "Cisco"), ("Tự phát triển", "In-house"),
        }),
        (CatalogGroup.Origin, new[]
        {
            ("Việt Nam", "Vietnam"), ("Mỹ", "United States"), ("Nhật Bản", "Japan"), ("Hàn Quốc", "South Korea"),
            ("Trung Quốc", "China"), ("Đức", "Germany"), ("Singapore", "Singapore"), ("Đài Loan", "Taiwan"),
        }),
        (CatalogGroup.PaymentMethod, new[]
        {
            ("Chuyển khoản", "Bank transfer"), ("Tiền mặt", "Cash"), ("Thẻ", "Card"), ("Bù trừ công nợ", "Debt offset"),
        }),
        (CatalogGroup.TicketCategory, new[]
        {
            ("Hỗ trợ kỹ thuật", "Technical support"), ("Bảo hành / sửa chữa", "Warranty / repair"), ("Khiếu nại", "Complaint"),
            ("Yêu cầu thay đổi / nâng cấp", "Change request"), ("Hướng dẫn sử dụng", "How-to question"), ("Hoá đơn / thanh toán", "Billing"),
            ("Góp ý / đề xuất", "Feedback"), ("Khác", "Other"),
        }),
    };
}
