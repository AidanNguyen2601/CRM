using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CRM.Web.Models.Entities;

/// <summary>
/// Chính sách cam kết dịch vụ (SLA): thời hạn phản hồi / giải quyết theo độ ưu tiên.
/// Áp cho phiếu theo thứ tự: hợp đồng của phiếu → khách hàng → chính sách mặc định → mức chuẩn của hệ thống.
/// </summary>
public class SlaPolicy
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập tên chính sách"), StringLength(100)]
    [Display(Name = "Tên chính sách")]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    [Display(Name = "Mô tả")]
    public string? Description { get; set; }

    [Display(Name = "Mặc định")] public bool IsDefault { get; set; }
    [Display(Name = "Đang dùng")] public bool IsActive { get; set; } = true;

    /// <summary>Tính hạn theo giờ làm việc (bỏ ngoài giờ, ngày nghỉ, ngày lễ) thay vì 24/7</summary>
    [Display(Name = "Tính theo giờ làm việc")] public bool BusinessHours { get; set; } = true;
    /// <summary>Dừng đồng hồ SLA khi phiếu ở trạng thái "Chờ khách phản hồi"</summary>
    [Display(Name = "Dừng tính giờ khi chờ khách")] public bool PauseWhenWaiting { get; set; } = true;
    /// <summary>Báo quản lý của người xử lý khi phiếu trễ hạn (và khi phiếu chưa phân công sắp trễ)</summary>
    [Display(Name = "Báo quản lý khi trễ hạn")] public bool Escalate { get; set; } = true;

    [Column(TypeName = "decimal(6,2)")] public decimal UrgentResponse { get; set; } = 1;
    [Column(TypeName = "decimal(6,2)")] public decimal UrgentResolve { get; set; } = 4;
    [Column(TypeName = "decimal(6,2)")] public decimal HighResponse { get; set; } = 4;
    [Column(TypeName = "decimal(6,2)")] public decimal HighResolve { get; set; } = 16;
    [Column(TypeName = "decimal(6,2)")] public decimal NormalResponse { get; set; } = 8;
    [Column(TypeName = "decimal(6,2)")] public decimal NormalResolve { get; set; } = 40;
    [Column(TypeName = "decimal(6,2)")] public decimal LowResponse { get; set; } = 16;
    [Column(TypeName = "decimal(6,2)")] public decimal LowResolve { get; set; } = 80;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>Số giờ (phản hồi, giải quyết) theo độ ưu tiên</summary>
    public (decimal Response, decimal Resolve) For(TicketPriority p) => p switch
    {
        TicketPriority.Urgent => (UrgentResponse, UrgentResolve),
        TicketPriority.High => (HighResponse, HighResolve),
        TicketPriority.Normal => (NormalResponse, NormalResolve),
        _ => (LowResponse, LowResolve)
    };
}
