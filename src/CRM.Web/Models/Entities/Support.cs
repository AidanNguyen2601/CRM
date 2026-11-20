using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CRM.Web.Models.Entities;

// Chăm sóc khách hàng (CSKH): phiếu yêu cầu hỗ trợ (ticket), trao đổi trên phiếu, cam kết thời gian xử lý (SLA), đánh giá hài lòng (CSAT)

public enum TicketStatus
{
    [Display(Name = "Mới")] New = 1,
    [Display(Name = "Đang xử lý")] InProgress = 2,
    [Display(Name = "Chờ khách phản hồi")] WaitingCustomer = 3,
    [Display(Name = "Đã giải quyết")] Resolved = 4,
    [Display(Name = "Đã đóng")] Closed = 5
}

public enum TicketPriority
{
    [Display(Name = "Thấp")] Low = 1,
    [Display(Name = "Trung bình")] Normal = 2,
    [Display(Name = "Cao")] High = 3,
    [Display(Name = "Khẩn cấp")] Urgent = 4
}

public enum TicketChannel
{
    [Display(Name = "Điện thoại / Hotline")] Phone = 1,
    [Display(Name = "Email")] Email = 2,
    [Display(Name = "Zalo / OTT")] Zalo = 3,
    [Display(Name = "Website / Cổng hỗ trợ")] Web = 4,
    [Display(Name = "Trực tiếp")] InPerson = 5,
    [Display(Name = "Khác")] Other = 9
}

/// <summary>
/// Cam kết thời gian xử lý theo độ ưu tiên (giờ, tính theo giờ lịch – 24/7).
/// Phản hồi = lần trả lời khách đầu tiên; Giải quyết = chuyển sang "Đã giải quyết".
/// </summary>
public static class TicketSla
{
    public static (int ResponseHours, int ResolveHours) For(TicketPriority p) => p switch
    {
        TicketPriority.Urgent => (1, 4),
        TicketPriority.High => (4, 24),
        TicketPriority.Normal => (8, 72),
        _ => (24, 120)
    };

    /// <summary>Còn dưới tỷ lệ này của thời hạn thì cảnh báo "sắp đến hạn"</summary>
    public const double WarnRatio = 0.25;
}

/// <summary>Phiếu yêu cầu hỗ trợ của khách hàng. OwnerId = người xử lý (null = chưa phân công).</summary>
public class Ticket : IOwned, IBranchScoped
{
    public int Id { get; set; }

    /// <summary>Đơn vị (công ty / chi nhánh) – tự gán theo người phụ trách khi lưu</summary>
    [Display(Name = "Đơn vị")]
    public int BranchId { get; set; }

    [Required, StringLength(30)]
    [Display(Name = "Mã phiếu")]
    public string Number { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập tiêu đề"), StringLength(200)]
    [Display(Name = "Tiêu đề")]
    public string Subject { get; set; } = string.Empty;

    [StringLength(4000)]
    [Display(Name = "Nội dung yêu cầu")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn khách hàng")]
    [Display(Name = "Khách hàng")]
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    [Display(Name = "Người liên hệ")]
    public int? ContactId { get; set; }
    public Contact? Contact { get; set; }

    [Display(Name = "Hợp đồng")]
    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }

    [Display(Name = "Hàng hoá / dịch vụ")]
    public int? ProductId { get; set; }
    public Product? Product { get; set; }

    [Display(Name = "Kênh tiếp nhận")]
    public TicketChannel Channel { get; set; } = TicketChannel.Phone;

    [StringLength(200)]
    [Display(Name = "Loại yêu cầu")]
    public string? Category { get; set; }

    [Display(Name = "Độ ưu tiên")]
    public TicketPriority Priority { get; set; } = TicketPriority.Normal;

    [Display(Name = "Trạng thái")]
    public TicketStatus Status { get; set; } = TicketStatus.New;

    [Display(Name = "Người xử lý")]
    public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }

    public int? CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }

    [Display(Name = "Ngày tiếp nhận")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? UpdatedAt { get; set; }

    [Display(Name = "Hạn phản hồi")] public DateTime ResponseDueAt { get; set; }
    [Display(Name = "Hạn giải quyết")] public DateTime ResolveDueAt { get; set; }
    [Display(Name = "Phản hồi lần đầu")] public DateTime? FirstResponseAt { get; set; }
    [Display(Name = "Ngày giải quyết")] public DateTime? ResolvedAt { get; set; }
    [Display(Name = "Ngày đóng")] public DateTime? ClosedAt { get; set; }

    [StringLength(2000)]
    [Display(Name = "Cách giải quyết")]
    public string? Resolution { get; set; }

    [Range(1, 5)]
    [Display(Name = "Mức hài lòng")]
    public int? SatisfactionScore { get; set; }

    [StringLength(500)]
    [Display(Name = "Nhận xét của khách")]
    public string? SatisfactionComment { get; set; }

    public ICollection<TicketComment> Comments { get; set; } = new List<TicketComment>();
    /// <summary>Công việc của phiếu (bảng Hoạt động): việc chính + việc con</summary>
    public ICollection<Activity> Tasks { get; set; } = new List<Activity>();

    [NotMapped] public bool IsOpen => Status is TicketStatus.New or TicketStatus.InProgress or TicketStatus.WaitingCustomer;

    /// <summary>Đặt hạn phản hồi / giải quyết theo độ ưu tiên, tính từ lúc tiếp nhận</summary>
    public void ApplySla()
    {
        var (resp, res) = TicketSla.For(Priority);
        ResponseDueAt = CreatedAt.AddHours(resp);
        ResolveDueAt = CreatedAt.AddHours(res);
    }

    /// <summary>Đã / đang trễ hạn phản hồi</summary>
    [NotMapped] public bool ResponseBreached => (FirstResponseAt ?? (IsOpen ? DateTime.Now : ResolvedAt ?? ClosedAt ?? DateTime.Now)) > ResponseDueAt;
    /// <summary>Đã / đang trễ hạn giải quyết</summary>
    [NotMapped] public bool ResolveBreached => (ResolvedAt ?? (IsOpen ? DateTime.Now : ClosedAt ?? DateTime.Now)) > ResolveDueAt;

    /// <summary>Trạng thái SLA để hiển thị: ok / warn / breach / done (đã giải quyết đúng hạn)</summary>
    [NotMapped]
    public string SlaState
    {
        get
        {
            if (!IsOpen) return ResolveBreached ? "breach" : "done";
            if (ResolveBreached || (FirstResponseAt == null && ResponseBreached)) return "breach";
            var due = FirstResponseAt == null ? ResponseDueAt : ResolveDueAt;
            var total = (due - CreatedAt).TotalMinutes;
            return total > 0 && (due - DateTime.Now).TotalMinutes < total * TicketSla.WarnRatio ? "warn" : "ok";
        }
    }

    /// <summary>Hạn đang tính (phản hồi nếu chưa phản hồi, ngược lại là giải quyết)</summary>
    [NotMapped] public DateTime CurrentDue => FirstResponseAt == null && IsOpen ? ResponseDueAt : ResolveDueAt;
}

/// <summary>Trao đổi trên phiếu: phản hồi khách, ghi chú nội bộ hoặc ghi nhận hệ thống (đổi trạng thái, phân công...)</summary>
public class TicketComment
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    [Required, StringLength(4000)]
    public string Body { get; set; } = string.Empty;

    /// <summary>Ghi chú nội bộ (không gửi khách)</summary>
    public bool IsInternal { get; set; }
    /// <summary>Do hệ thống ghi (thay đổi trạng thái, phân công...)</summary>
    public bool IsSystem { get; set; }

    public int? AuthorId { get; set; }
    public AppUser? Author { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
