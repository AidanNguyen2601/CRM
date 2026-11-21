using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CRM.Web.Models.Entities;

// Vận hành bán hàng: quy tắc tự động, bảng giá, nhãn, trường tùy biến.

/// <summary>Sự kiện kích hoạt quy tắc tự động</summary>
public enum AutomationTrigger
{
    [Display(Name = "Có lead mới")] LeadCreated = 1,
    [Display(Name = "Lead mới chưa được liên hệ sau N ngày")] LeadIdle = 2,
    [Display(Name = "Cơ hội chuyển giai đoạn")] OpportunityStageChanged = 3,
    [Display(Name = "Cơ hội đang mở không có hoạt động N ngày")] OpportunityIdle = 4,
    [Display(Name = "Hợp đồng được ký (đang thực hiện)")] ContractSigned = 5,
    [Display(Name = "Hoá đơn quá hạn thanh toán N ngày")] InvoiceOverdue = 6,
    [Display(Name = "Có phiếu hỗ trợ mới")] TicketCreated = 7,
    [Display(Name = "Sinh nhật người liên hệ (hôm nay)")] ContactBirthday = 8
}

public enum AutomationAction
{
    [Display(Name = "Tạo công việc")] CreateTask = 1,
    [Display(Name = "Gửi thông báo")] Notify = 2,
    [Display(Name = "Gửi email cho khách hàng")] EmailCustomer = 3
}

/// <summary>Người nhận việc / thông báo của quy tắc</summary>
public enum AutomationAssignee
{
    [Display(Name = "Người phụ trách bản ghi")] Owner = 1,
    [Display(Name = "Quản lý của người phụ trách")] Manager = 2,
    [Display(Name = "Người dùng chỉ định")] User = 3
}

/// <summary>Quy tắc "khi ... thì ...": sự kiện + điều kiện → tạo công việc / thông báo / email</summary>
public class AutomationRule
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập tên quy tắc"), StringLength(150)]
    [Display(Name = "Tên quy tắc")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Đang bật")] public bool IsActive { get; set; } = true;
    [Display(Name = "Khi")] public AutomationTrigger Trigger { get; set; } = AutomationTrigger.LeadCreated;

    /// <summary>Số ngày (sự kiện theo thời gian)</summary>
    [Range(0, 3650)][Display(Name = "Số ngày")] public int? Days { get; set; }
    /// <summary>Điều kiện: giai đoạn cơ hội chuyển tới (OpportunityStage), độ ưu tiên phiếu (TicketPriority), nguồn lead (LeadSource)</summary>
    [Display(Name = "Điều kiện")] public int? Condition { get; set; }

    [Display(Name = "Thì")] public AutomationAction Action { get; set; } = AutomationAction.CreateTask;
    [Display(Name = "Giao cho / gửi tới")] public AutomationAssignee Assignee { get; set; } = AutomationAssignee.Owner;
    public int? AssigneeUserId { get; set; }
    public AppUser? AssigneeUser { get; set; }

    /// <summary>Tiêu đề công việc / thông báo (có biến {{Ten}}, {{KhachHang}}, {{SoNgay}})</summary>
    [StringLength(200)][Display(Name = "Tiêu đề")] public string? Title { get; set; }
    [StringLength(1000)][Display(Name = "Nội dung")] public string? Message { get; set; }
    /// <summary>Hạn công việc: sau N ngày kể từ lúc tạo</summary>
    [Range(0, 365)][Display(Name = "Hạn hoàn thành (ngày)")] public int TaskDueDays { get; set; } = 1;
    [Display(Name = "Ưu tiên")] public Priority TaskPriority { get; set; } = Priority.Normal;
    [Display(Name = "Mẫu email")] public int? EmailTemplateId { get; set; }

    public int RunCount { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>Đã chạy quy tắc cho bản ghi (chống chạy lặp với sự kiện theo thời gian)</summary>
public class AutomationLog
{
    public long Id { get; set; }
    public int RuleId { get; set; }
    [Required, StringLength(30)] public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    [Required, StringLength(60)] public string Key { get; set; } = string.Empty;
    [StringLength(300)] public string? Result { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
}

/// <summary>Bảng giá (theo nhóm khách hàng / kênh). Khách hàng gắn bảng giá → báo giá lấy giá theo bảng.</summary>
public class PriceList
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Vui lòng nhập tên bảng giá"), StringLength(150)]
    [Display(Name = "Tên bảng giá")]
    public string Name { get; set; } = string.Empty;
    [StringLength(500)][Display(Name = "Mô tả")] public string? Description { get; set; }
    [DataType(DataType.Date)][Display(Name = "Hiệu lực từ")] public DateTime? ValidFrom { get; set; }
    [DataType(DataType.Date)][Display(Name = "Hiệu lực đến")] public DateTime? ValidTo { get; set; }
    [Display(Name = "Đang dùng")] public bool IsActive { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public ICollection<PriceListItem> Items { get; set; } = new List<PriceListItem>();

    [NotMapped] public bool IsEffective => IsActive && (ValidFrom == null || ValidFrom <= DateTime.Today) && (ValidTo == null || ValidTo >= DateTime.Today);
}

public class PriceListItem
{
    public int Id { get; set; }
    public int PriceListId { get; set; }
    public PriceList? PriceList { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    [Column(TypeName = "decimal(18,0)")] public decimal Price { get; set; }
}

public enum QuoteApproval
{
    [Display(Name = "Không cần duyệt")] None = 0,
    [Display(Name = "Chờ duyệt chiết khấu")] Pending = 1,
    [Display(Name = "Đã duyệt chiết khấu")] Approved = 2,
    [Display(Name = "Từ chối chiết khấu")] Rejected = 3
}

/// <summary>Nhãn gắn cho khách hàng, liên hệ, lead, cơ hội</summary>
public class Tag
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Vui lòng nhập tên nhãn"), StringLength(50)]
    [Display(Name = "Tên nhãn")]
    public string Name { get; set; } = string.Empty;
    /// <summary>Màu dạng #rrggbb</summary>
    [StringLength(7)][Display(Name = "Màu")] public string Color { get; set; } = "#64748b";
}

public class EntityTag
{
    public int Id { get; set; }
    public int TagId { get; set; }
    public Tag? Tag { get; set; }
    [Required, StringLength(30)] public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
}

public enum CustomFieldType
{
    [Display(Name = "Văn bản")] Text = 1,
    [Display(Name = "Số")] Number = 2,
    [Display(Name = "Ngày")] Date = 3,
    [Display(Name = "Chọn một")] Select = 4,
    [Display(Name = "Có / không")] Checkbox = 5
}

/// <summary>Trường tùy biến do doanh nghiệp tự thêm cho khách hàng / liên hệ / lead / cơ hội</summary>
public class CustomFieldDef
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Dùng cho")] public string EntityType { get; set; } = nameof(Customer);
    [Required(ErrorMessage = "Vui lòng nhập tên trường"), StringLength(100)]
    [Display(Name = "Tên trường")]
    public string Label { get; set; } = string.Empty;
    [Display(Name = "Kiểu dữ liệu")] public CustomFieldType Type { get; set; } = CustomFieldType.Text;
    /// <summary>Các lựa chọn (kiểu Chọn một), mỗi dòng một giá trị</summary>
    [StringLength(2000)][Display(Name = "Các lựa chọn")] public string? Options { get; set; }
    [Display(Name = "Bắt buộc nhập")] public bool Required { get; set; }
    [Display(Name = "Hiện ở danh sách")] public bool ShowInList { get; set; }
    [Display(Name = "Thứ tự")] public int SortOrder { get; set; }
    [Display(Name = "Đang dùng")] public bool IsActive { get; set; } = true;

    [NotMapped] public string[] OptionList => (Options ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public class CustomFieldValue
{
    public int Id { get; set; }
    public int FieldId { get; set; }
    public CustomFieldDef? Field { get; set; }
    public int EntityId { get; set; }
    [StringLength(1000)] public string? Value { get; set; }
}

public enum BillingCycle
{
    [Display(Name = "Không định kỳ")] None = 0,
    [Display(Name = "Hằng tháng")] Monthly = 1,
    [Display(Name = "Hằng quý")] Quarterly = 3,
    [Display(Name = "6 tháng")] SemiAnnual = 6,
    [Display(Name = "Hằng năm")] Yearly = 12
}
