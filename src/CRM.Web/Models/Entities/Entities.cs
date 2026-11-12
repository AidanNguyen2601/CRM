using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CRM.Web.Models.Entities;

/// <summary>Bản ghi có người phụ trách – dùng để phân quyền "Sales chỉ thấy dữ liệu của mình".</summary>
public interface IOwned
{
    int? OwnerId { get; set; }
}

/// <summary>Tài khoản người dùng hệ thống (nhân viên kinh doanh, quản lý...)</summary>
public class AppUser
{
    public int Id { get; set; }

    [Required, StringLength(50)]
    [Display(Name = "Tên đăng nhập")]
    public string Username { get; set; } = string.Empty;

    [Required, StringLength(150)]
    [Display(Name = "Họ và tên")]
    public string FullName { get; set; } = string.Empty;

    [StringLength(150)]
    [Display(Name = "Email")]
    public string? Email { get; set; }

    [Required, StringLength(500)]
    public string PasswordHash { get; set; } = string.Empty;

    [Required, StringLength(20)]
    [Display(Name = "Vai trò")]
    public string Role { get; set; } = AppRoles.Sales;

    [Display(Name = "Kích hoạt")]
    public bool IsActive { get; set; } = true;

    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string Sales = "Sales";

    public const string ManagerGroup = Admin + "," + Manager;
    public static readonly string[] All = { Admin, Manager, Sales };

    public static string DisplayName(string role) => role switch
    {
        Admin => "Quản trị hệ thống",
        Manager => "Quản lý kinh doanh",
        _ => "Nhân viên kinh doanh"
    };
}

/// <summary>Khách hàng (doanh nghiệp / tổ chức)</summary>
public class Customer : IOwned
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập mã khách hàng"), StringLength(20)]
    [Display(Name = "Mã KH")]
    public string Code { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập tên khách hàng"), StringLength(200)]
    [Display(Name = "Tên khách hàng")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Phân loại")]
    public CustomerType Type { get; set; } = CustomerType.Prospect;

    [StringLength(100)]
    [Display(Name = "Ngành nghề")]
    public string? Industry { get; set; }

    [StringLength(20)]
    [Display(Name = "Mã số thuế")]
    public string? TaxCode { get; set; }

    [StringLength(20)]
    [Display(Name = "Điện thoại")]
    public string? Phone { get; set; }

    [EmailAddress(ErrorMessage = "Email không hợp lệ"), StringLength(150)]
    [Display(Name = "Email")]
    public string? Email { get; set; }

    [StringLength(200)]
    [Display(Name = "Website")]
    public string? Website { get; set; }

    [StringLength(300)]
    [Display(Name = "Địa chỉ")]
    public string? Address { get; set; }

    [StringLength(100)]
    [Display(Name = "Tỉnh / Thành phố")]
    public string? City { get; set; }

    [Range(0, 1_000_000)]
    [Display(Name = "Số nhân sự")]
    public int? EmployeeCount { get; set; }

    [Display(Name = "Phụ trách")]
    public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }

    [StringLength(2000)]
    [Display(Name = "Ghi chú")]
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<Contact> Contacts { get; set; } = new List<Contact>();
}

/// <summary>Người liên hệ thuộc khách hàng</summary>
public class Contact : IOwned
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập họ tên"), StringLength(150)]
    [Display(Name = "Họ và tên")]
    public string FullName { get; set; } = string.Empty;

    [StringLength(100)]
    [Display(Name = "Chức vụ")]
    public string? JobTitle { get; set; }

    [Display(Name = "Khách hàng")]
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    [EmailAddress(ErrorMessage = "Email không hợp lệ"), StringLength(150)]
    [Display(Name = "Email")]
    public string? Email { get; set; }

    [StringLength(20)]
    [Display(Name = "Điện thoại")]
    public string? Phone { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "Ngày sinh")]
    public DateTime? BirthDate { get; set; }

    [Display(Name = "Liên hệ chính")]
    public bool IsPrimary { get; set; }

    [Display(Name = "Phụ trách")]
    public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }

    [StringLength(2000)]
    [Display(Name = "Ghi chú")]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>Khách hàng tiềm năng chưa xác thực</summary>
public class Lead : IOwned
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập họ tên"), StringLength(150)]
    [Display(Name = "Họ và tên")]
    public string FullName { get; set; } = string.Empty;

    [StringLength(200)]
    [Display(Name = "Công ty")]
    public string? Company { get; set; }

    [StringLength(100)]
    [Display(Name = "Chức vụ")]
    public string? JobTitle { get; set; }

    [EmailAddress(ErrorMessage = "Email không hợp lệ"), StringLength(150)]
    [Display(Name = "Email")]
    public string? Email { get; set; }

    [StringLength(20)]
    [Display(Name = "Điện thoại")]
    public string? Phone { get; set; }

    [Display(Name = "Nguồn")]
    public LeadSource Source { get; set; } = LeadSource.Website;

    [Display(Name = "Trạng thái")]
    public LeadStatus Status { get; set; } = LeadStatus.New;

    [Display(Name = "Mức độ")]
    public LeadRating Rating { get; set; } = LeadRating.Warm;

    [Column(TypeName = "decimal(18,0)")]
    [Range(0, 1_000_000_000_000)]
    [Display(Name = "Giá trị dự kiến")]
    public decimal EstimatedValue { get; set; }

    [Display(Name = "Phụ trách")]
    public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }

    [StringLength(2000)]
    [Display(Name = "Nhu cầu / ghi chú")]
    public string? Description { get; set; }

    public int? ConvertedCustomerId { get; set; }
    public Customer? ConvertedCustomer { get; set; }
    public int? ConvertedOpportunityId { get; set; }
    public DateTime? ConvertedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>Cơ hội bán hàng</summary>
public class Opportunity : IOwned
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập tên cơ hội"), StringLength(200)]
    [Display(Name = "Tên cơ hội")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng chọn khách hàng")]
    [Display(Name = "Khách hàng")]
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    [Display(Name = "Người liên hệ")]
    public int? ContactId { get; set; }
    public Contact? Contact { get; set; }

    [Display(Name = "Giai đoạn")]
    public OpportunityStage Stage { get; set; } = OpportunityStage.Prospecting;

    [Column(TypeName = "decimal(18,0)")]
    [Range(0, 1_000_000_000_000, ErrorMessage = "Giá trị không hợp lệ")]
    [Display(Name = "Giá trị")]
    public decimal Amount { get; set; }

    [Range(0, 100)]
    [Display(Name = "Xác suất (%)")]
    public int Probability { get; set; } = 10;

    [DataType(DataType.Date)]
    [Display(Name = "Dự kiến chốt")]
    public DateTime? ExpectedCloseDate { get; set; }

    [Display(Name = "Nguồn")]
    public LeadSource? Source { get; set; }

    [Display(Name = "Phụ trách")]
    public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }

    [StringLength(500)]
    [Display(Name = "Lý do thua")]
    public string? LostReason { get; set; }

    [StringLength(2000)]
    [Display(Name = "Mô tả")]
    public string? Description { get; set; }

    [Display(Name = "Ngày chốt")]
    public DateTime? ClosedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [NotMapped] public bool IsClosed => Stage is OpportunityStage.ClosedWon or OpportunityStage.ClosedLost;
    [NotMapped] public decimal WeightedAmount => Amount * Probability / 100m;
}

/// <summary>Hoạt động chăm sóc: cuộc gọi, cuộc họp, email, công việc</summary>
public class Activity : IOwned
{
    public int Id { get; set; }

    [Display(Name = "Loại")]
    public ActivityType Type { get; set; } = ActivityType.Call;

    [Required(ErrorMessage = "Vui lòng nhập tiêu đề"), StringLength(200)]
    [Display(Name = "Tiêu đề")]
    public string Subject { get; set; } = string.Empty;

    [Display(Name = "Thời hạn")]
    public DateTime DueDate { get; set; } = DateTime.Today.AddHours(9);

    [Display(Name = "Trạng thái")]
    public ActivityStatus Status { get; set; } = ActivityStatus.Planned;

    [Display(Name = "Ưu tiên")]
    public Priority Priority { get; set; } = Priority.Normal;

    [Display(Name = "Phụ trách")]
    public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }

    [Display(Name = "Khách hàng")] public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    [Display(Name = "Người liên hệ")] public int? ContactId { get; set; }
    public Contact? Contact { get; set; }
    [Display(Name = "Lead")] public int? LeadId { get; set; }
    public Lead? Lead { get; set; }
    [Display(Name = "Cơ hội")] public int? OpportunityId { get; set; }
    public Opportunity? Opportunity { get; set; }

    [StringLength(2000)]
    [Display(Name = "Nội dung / kết quả")]
    public string? Description { get; set; }

    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [NotMapped] public bool IsOverdue => Status == ActivityStatus.Planned && DueDate < DateTime.Now;
}

/// <summary>Sản phẩm / dịch vụ</summary>
public class Product
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập mã"), StringLength(30)]
    [Display(Name = "Mã sản phẩm")]
    public string Code { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập tên"), StringLength(200)]
    [Display(Name = "Tên sản phẩm / dịch vụ")]
    public string Name { get; set; } = string.Empty;

    [StringLength(100)]
    [Display(Name = "Nhóm")]
    public string? Category { get; set; }

    [Required, StringLength(30)]
    [Display(Name = "Đơn vị tính")]
    public string Unit { get; set; } = "Gói";

    [Column(TypeName = "decimal(18,0)")]
    [Range(0, 100_000_000_000)]
    [Display(Name = "Đơn giá")]
    public decimal UnitPrice { get; set; }

    [Display(Name = "Đang kinh doanh")]
    public bool IsActive { get; set; } = true;

    [StringLength(1000)]
    [Display(Name = "Mô tả")]
    public string? Description { get; set; }
}

/// <summary>Báo giá</summary>
public class Quote : IOwned
{
    public int Id { get; set; }

    [Required, StringLength(30)]
    [Display(Name = "Số báo giá")]
    public string Number { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng chọn khách hàng")]
    [Display(Name = "Khách hàng")]
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    [Display(Name = "Người nhận")]
    public int? ContactId { get; set; }
    public Contact? Contact { get; set; }

    [Display(Name = "Cơ hội")]
    public int? OpportunityId { get; set; }
    public Opportunity? Opportunity { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "Ngày báo giá")]
    public DateTime QuoteDate { get; set; } = DateTime.Today;

    [DataType(DataType.Date)]
    [Display(Name = "Hiệu lực đến")]
    public DateTime ValidUntil { get; set; } = DateTime.Today.AddDays(30);

    [Display(Name = "Trạng thái")]
    public QuoteStatus Status { get; set; } = QuoteStatus.Draft;

    [Column(TypeName = "decimal(5,2)")]
    [Range(0, 100)]
    [Display(Name = "Chiết khấu chung (%)")]
    public decimal DiscountPercent { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    [Range(0, 100)]
    [Display(Name = "Thuế GTGT (%)")]
    public decimal VatPercent { get; set; } = 10;

    [Column(TypeName = "decimal(18,0)")][Display(Name = "Cộng tiền hàng")] public decimal SubTotal { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Chiết khấu")] public decimal DiscountAmount { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Thuế GTGT")] public decimal VatAmount { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Tổng thanh toán")] public decimal Total { get; set; }

    [Display(Name = "Phụ trách")]
    public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }

    [StringLength(2000)]
    [Display(Name = "Điều khoản / ghi chú")]
    public string? Terms { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public ICollection<QuoteLine> Lines { get; set; } = new List<QuoteLine>();

    /// <summary>Tính lại tổng tiền từ các dòng</summary>
    public void Recalculate()
    {
        foreach (var l in Lines) l.Recalculate();
        SubTotal = Lines.Sum(l => l.LineTotal);
        DiscountAmount = Math.Round(SubTotal * DiscountPercent / 100m, 0);
        VatAmount = Math.Round((SubTotal - DiscountAmount) * VatPercent / 100m, 0);
        Total = SubTotal - DiscountAmount + VatAmount;
    }
}

public class QuoteLine
{
    public int Id { get; set; }
    public int QuoteId { get; set; }
    public Quote? Quote { get; set; }

    public int? ProductId { get; set; }
    public Product? Product { get; set; }

    [Required, StringLength(300)]
    public string Description { get; set; } = string.Empty;

    [StringLength(30)]
    public string? Unit { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Quantity { get; set; } = 1;

    [Column(TypeName = "decimal(18,0)")]
    public decimal UnitPrice { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal DiscountPercent { get; set; }

    [Column(TypeName = "decimal(18,0)")]
    public decimal LineTotal { get; set; }

    public int SortOrder { get; set; }

    public void Recalculate() =>
        LineTotal = Math.Round(Quantity * UnitPrice * (1 - DiscountPercent / 100m), 0);
}
