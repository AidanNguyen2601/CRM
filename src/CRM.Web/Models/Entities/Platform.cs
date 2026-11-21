using System.ComponentModel.DataAnnotations;

namespace CRM.Web.Models.Entities;

// Nền tảng vận hành: nhật ký thay đổi, bảo mật đăng nhập, email, phân quyền chi tiết, chia sẻ bản ghi.

/// <summary>Nhật ký thay đổi dữ liệu (tự ghi trong SaveChanges). Không ràng buộc khóa ngoại để giữ lịch sử khi bản ghi / người dùng bị xóa.</summary>
public class AuditLog
{
    public long Id { get; set; }
    [Required, StringLength(50)] public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    /// <summary>Create / Update / Delete / Merge / Import ...</summary>
    [Required, StringLength(20)] public string Action { get; set; } = string.Empty;
    /// <summary>Tên hiển thị của bản ghi tại thời điểm thay đổi</summary>
    [StringLength(300)] public string? Title { get; set; }
    /// <summary>JSON: [{"f":"Tên trường","o":"cũ","n":"mới"}]</summary>
    public string? Changes { get; set; }
    public int? UserId { get; set; }
    [StringLength(150)] public string? UserName { get; set; }
    [StringLength(50)] public string? Ip { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
}

/// <summary>Lịch sử đăng nhập (thành công / thất bại)</summary>
public class LoginLog
{
    public long Id { get; set; }
    public int? UserId { get; set; }
    [Required, StringLength(50)] public string Username { get; set; } = string.Empty;
    public bool Success { get; set; }
    [StringLength(100)] public string? Reason { get; set; }
    [StringLength(50)] public string? Ip { get; set; }
    [StringLength(300)] public string? UserAgent { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
}

/// <summary>Mã đặt lại mật khẩu (chỉ lưu băm SHA-256 của mã)</summary>
public class PasswordResetToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    [Required, StringLength(100)] public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public enum EmailStatus
{
    [Display(Name = "Chờ gửi")] Queued = 1,
    [Display(Name = "Đã gửi")] Sent = 2,
    [Display(Name = "Gửi lỗi")] Failed = 3
}

/// <summary>Email gửi đi (hàng đợi + lịch sử). Tác vụ nền gửi các email "Chờ gửi".</summary>
public class EmailMessage
{
    public int Id { get; set; }
    [Required, StringLength(500)] public string To { get; set; } = string.Empty;
    [StringLength(500)] public string? Cc { get; set; }
    [Required, StringLength(300)] public string Subject { get; set; } = string.Empty;
    /// <summary>Nội dung HTML</summary>
    public string Body { get; set; } = string.Empty;
    public EmailStatus Status { get; set; } = EmailStatus.Queued;
    public int Attempts { get; set; }
    [StringLength(1000)] public string? Error { get; set; }
    /// <summary>Đính kèm: danh sách Id file đính kèm (bảng Attachments), phân cách dấu phẩy</summary>
    [StringLength(500)] public string? AttachmentIds { get; set; }
    /// <summary>Bản ghi liên quan (Quote, Contract, Customer, Campaign, Notification, System ...)</summary>
    [StringLength(30)] public string? RelatedType { get; set; }
    public int? RelatedId { get; set; }
    public int? CustomerId { get; set; }
    public int? CampaignMemberId { get; set; }
    public int? CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? SentAt { get; set; }
}

/// <summary>Mẫu email có biến trộn {{Bien}}. Code: mẫu hệ thống (password-reset, notification) hoặc để trống.</summary>
public class EmailTemplate
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Vui lòng nhập tên mẫu"), StringLength(150)]
    [Display(Name = "Tên mẫu")]
    public string Name { get; set; } = string.Empty;
    [StringLength(50)] public string? Code { get; set; }
    /// <summary>Dùng cho: Customer, Contact, Quote, Contract, Invoice, Campaign, Ticket, System (trống = mọi nơi)</summary>
    [StringLength(30)]
    [Display(Name = "Dùng cho")]
    public string? Scope { get; set; }
    [Required(ErrorMessage = "Vui lòng nhập tiêu đề"), StringLength(300)]
    [Display(Name = "Tiêu đề")]
    public string Subject { get; set; } = string.Empty;
    [Required(ErrorMessage = "Vui lòng nhập nội dung")]
    [Display(Name = "Nội dung")]
    public string Body { get; set; } = string.Empty;
    [Display(Name = "Đang dùng")]
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>Quyền của vai trò theo chức năng (vd. "Customers.Delete"). Không có dòng = được phép (mặc định như trước).</summary>
public class RolePermission
{
    public int Id { get; set; }
    [Required, StringLength(20)] public string Role { get; set; } = string.Empty;
    [Required, StringLength(60)] public string Permission { get; set; } = string.Empty;
    public bool Allowed { get; set; }
}

/// <summary>Chia sẻ một bản ghi (khách hàng, cơ hội, lead, liên hệ) cho người dùng khác ngoài người phụ trách</summary>
public class RecordShare
{
    public int Id { get; set; }
    [Required, StringLength(30)] public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    /// <summary>Được sửa (không chỉ xem)</summary>
    public bool CanEdit { get; set; }
    public int? SharedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
