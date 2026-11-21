using System.ComponentModel.DataAnnotations;

namespace CRM.Web.Models.Entities;

/// <summary>
/// Form thu lead nhúng vào website / landing page. Khách điền form → tạo lead (ghi nguồn, UTM, trang gửi),
/// tự chấm điểm, tự phân bổ (hoặc giao người phụ trách cố định) và kích hoạt quy tắc tự động "Có lead mới".
/// </summary>
public class WebForm
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập tên form"), StringLength(100)]
    [Display(Name = "Tên form")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Mã công khai trên địa chỉ form (/f/{Token})</summary>
    [Required, StringLength(32)]
    public string Token { get; set; } = string.Empty;

    [Display(Name = "Đang nhận")] public bool IsActive { get; set; } = true;

    [StringLength(150)][Display(Name = "Tiêu đề form")] public string? Title { get; set; }
    [StringLength(500)][Display(Name = "Lời giới thiệu")] public string? Intro { get; set; }
    [StringLength(50)][Display(Name = "Chữ trên nút gửi")] public string? SubmitText { get; set; }
    [StringLength(500)][Display(Name = "Lời cảm ơn")] public string? SuccessMessage { get; set; }
    /// <summary>Chuyển tới trang này sau khi gửi (để trống = hiện lời cảm ơn)</summary>
    [StringLength(300)][Display(Name = "Chuyển tới trang (sau khi gửi)")] public string? RedirectUrl { get; set; }
    /// <summary>Màu nút / tiêu đề (#RRGGBB)</summary>
    [StringLength(7)][Display(Name = "Màu chủ đạo")] public string? Accent { get; set; }

    [Display(Name = "Hỏi công ty")] public bool AskCompany { get; set; } = true;
    [Display(Name = "Hỏi chức vụ")] public bool AskJobTitle { get; set; }
    [Display(Name = "Hỏi nhu cầu")] public bool AskMessage { get; set; } = true;
    [Display(Name = "Bắt buộc email")] public bool RequireEmail { get; set; }
    [Display(Name = "Bắt buộc điện thoại")] public bool RequirePhone { get; set; } = true;
    /// <summary>Hiện ô đồng ý xử lý dữ liệu cá nhân (Nghị định 13/2023)</summary>
    [Display(Name = "Xin đồng ý xử lý dữ liệu")] public bool AskConsent { get; set; } = true;
    [StringLength(500)][Display(Name = "Nội dung đồng ý")] public string? ConsentText { get; set; }

    [Display(Name = "Nguồn lead")] public LeadSource Source { get; set; } = LeadSource.Website;
    [Display(Name = "Chiến dịch")] public int? CampaignId { get; set; }
    public Campaign? Campaign { get; set; }
    /// <summary>Người phụ trách cố định (trống = tự phân bổ theo cấu hình; không bật phân bổ thì để trống người phụ trách)</summary>
    [Display(Name = "Giao cho")] public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }
    /// <summary>Đơn vị nhận lead khi chưa có người phụ trách</summary>
    [Display(Name = "Đơn vị nhận")] public int? BranchId { get; set; }

    /// <summary>Tên miền được phép nhúng (mỗi dòng một tên miền; trống = mọi trang)</summary>
    [StringLength(500)][Display(Name = "Tên miền được nhúng")] public string? AllowedDomains { get; set; }

    public int Submissions { get; set; }
    public DateTime? LastSubmissionAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
