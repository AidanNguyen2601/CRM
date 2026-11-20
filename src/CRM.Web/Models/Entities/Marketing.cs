using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CRM.Web.Models.Entities;

/// <summary>Kênh tiếp thị của chiến dịch</summary>
public enum CampaignChannel
{
    [Display(Name = "Email")] Email = 1,
    [Display(Name = "SMS")] Sms = 2,
    [Display(Name = "Zalo / OTT")] Zalo = 3,
    [Display(Name = "Mạng xã hội")] Social = 4,
    [Display(Name = "Sự kiện / Hội thảo")] Event = 5,
    [Display(Name = "Telesales")] Telesales = 6,
    [Display(Name = "Webinar")] Webinar = 7,
    [Display(Name = "Khác")] Other = 9
}

public enum CampaignStatus
{
    [Display(Name = "Lên kế hoạch")] Planned = 1,
    [Display(Name = "Đang triển khai")] Active = 2,
    [Display(Name = "Đã kết thúc")] Completed = 3,
    [Display(Name = "Đã hủy")] Cancelled = 4
}

/// <summary>Trạng thái tiếp cận của từng đối tượng trong chiến dịch</summary>
public enum CampaignMemberStatus
{
    [Display(Name = "Chưa tiếp cận")] Pending = 1,
    [Display(Name = "Đã tiếp cận")] Sent = 2,
    [Display(Name = "Quan tâm / phản hồi")] Responded = 3,
    [Display(Name = "Chuyển đổi")] Converted = 4,
    [Display(Name = "Không quan tâm")] Declined = 5,
    [Display(Name = "Từ chối nhận tin")] Unsubscribed = 6,
    [Display(Name = "Không liên lạc được")] Failed = 7
}

/// <summary>Chiến dịch tiếp thị khách hàng: danh sách đối tượng, chi phí, lead / cơ hội / doanh số mang lại.</summary>
public class Campaign : IOwned, IBranchScoped
{
    public int Id { get; set; }

    /// <summary>Đơn vị (công ty / chi nhánh) – tự gán theo người phụ trách khi lưu</summary>
    [Display(Name = "Đơn vị")]
    public int BranchId { get; set; }

    [Required, StringLength(30)]
    [Display(Name = "Mã chiến dịch")]
    public string Code { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập tên chiến dịch"), StringLength(200)]
    [Display(Name = "Tên chiến dịch")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Kênh")]
    public CampaignChannel Channel { get; set; } = CampaignChannel.Email;

    [Display(Name = "Trạng thái")]
    public CampaignStatus Status { get; set; } = CampaignStatus.Planned;

    [DataType(DataType.Date)]
    [Display(Name = "Ngày bắt đầu")]
    public DateTime StartDate { get; set; } = DateTime.Today;

    [DataType(DataType.Date)]
    [Display(Name = "Ngày kết thúc")]
    public DateTime? EndDate { get; set; }

    [Column(TypeName = "decimal(18,0)")]
    [Range(0, 1_000_000_000_000, ErrorMessage = "Giá trị không hợp lệ")]
    [Display(Name = "Ngân sách")]
    public decimal Budget { get; set; }

    [Column(TypeName = "decimal(18,0)")]
    [Range(0, 1_000_000_000_000, ErrorMessage = "Giá trị không hợp lệ")]
    [Display(Name = "Chi phí thực tế")]
    public decimal ActualCost { get; set; }

    [Column(TypeName = "decimal(18,0)")]
    [Range(0, 1_000_000_000_000, ErrorMessage = "Giá trị không hợp lệ")]
    [Display(Name = "Doanh số kỳ vọng")]
    public decimal ExpectedRevenue { get; set; }

    [Range(0, 1_000_000)]
    [Display(Name = "Mục tiêu số lead")]
    public int? TargetLeads { get; set; }

    [StringLength(1000)]
    [Display(Name = "Mục tiêu / đối tượng")]
    public string? Objective { get; set; }

    [StringLength(4000)]
    [Display(Name = "Nội dung thông điệp")]
    public string? Message { get; set; }

    [Display(Name = "Phụ trách")]
    public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<CampaignMember> Members { get; set; } = new List<CampaignMember>();

    [NotMapped] public bool IsOpen => Status is CampaignStatus.Planned or CampaignStatus.Active;

    /// <summary>Nguồn lead tương ứng với kênh (gợi ý khi tạo lead từ chiến dịch)</summary>
    public static LeadSource LeadSourceFor(CampaignChannel ch) => ch switch
    {
        CampaignChannel.Email => LeadSource.EmailCampaign,
        CampaignChannel.Social or CampaignChannel.Zalo => LeadSource.Social,
        CampaignChannel.Event or CampaignChannel.Webinar => LeadSource.Event,
        CampaignChannel.Telesales => LeadSource.ColdCall,
        _ => LeadSource.Other
    };

    /// <summary>Loại hoạt động ghi vào lịch sử chăm sóc khi tiếp cận đối tượng qua kênh này</summary>
    public static ActivityType ActivityTypeFor(CampaignChannel ch) => ch switch
    {
        CampaignChannel.Email => ActivityType.Email,
        CampaignChannel.Telesales => ActivityType.Call,
        CampaignChannel.Event or CampaignChannel.Webinar => ActivityType.Meeting,
        _ => ActivityType.Task
    };

    /// <summary>Kênh cần email (true), số điện thoại (false) hay không bắt buộc (null)</summary>
    public static bool? NeedsEmail(CampaignChannel ch) => ch switch
    {
        CampaignChannel.Email => true,
        CampaignChannel.Sms or CampaignChannel.Zalo or CampaignChannel.Telesales => false,
        _ => null
    };
}

/// <summary>
/// Đối tượng nhận chiến dịch: người liên hệ của khách hàng hoặc lead. Họ tên / email / SĐT được lưu lại
/// tại thời điểm thêm để danh sách gửi vẫn giữ nguyên khi hồ sơ gốc thay đổi hoặc bị xóa.
/// </summary>
public class CampaignMember
{
    public int Id { get; set; }
    public int CampaignId { get; set; }
    public Campaign? Campaign { get; set; }

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? ContactId { get; set; }
    public Contact? Contact { get; set; }
    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }

    [Required, StringLength(150)]
    public string FullName { get; set; } = string.Empty;

    [StringLength(200)]
    public string? Company { get; set; }

    [StringLength(150)]
    public string? Email { get; set; }

    [StringLength(20)]
    public string? Phone { get; set; }

    public CampaignMemberStatus Status { get; set; } = CampaignMemberStatus.Pending;

    public DateTime? LastContactAt { get; set; }

    [StringLength(500)]
    public string? Note { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.Now;

    [NotMapped] public bool IsReached => Status is not (CampaignMemberStatus.Pending or CampaignMemberStatus.Failed);
    [NotMapped] public bool IsPositive => Status is CampaignMemberStatus.Responded or CampaignMemberStatus.Converted;
}
