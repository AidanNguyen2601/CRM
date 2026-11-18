using System.ComponentModel.DataAnnotations;

namespace CRM.Web.Models.Entities;

public enum CustomReportSource
{
    [Display(Name = "Câu lệnh SQL")] Sql = 1,
    [Display(Name = "Chọn từ View")] View = 2
}

/// <summary>
/// Báo cáo động do quản trị định nghĩa: câu lệnh SELECT tự viết, hoặc chọn cột từ một View.
/// Luôn chạy dưới người dùng database chỉ có quyền đọc (crm_report_reader).
/// </summary>
public class CustomReport
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập tên báo cáo"), StringLength(200)]
    [Display(Name = "Tên báo cáo")]
    public string Name { get; set; } = string.Empty;

    [StringLength(100)]
    [Display(Name = "Nhóm")]
    public string? Category { get; set; }

    [StringLength(1000)]
    [Display(Name = "Mô tả")]
    public string? Description { get; set; }

    [Display(Name = "Nguồn dữ liệu")]
    public CustomReportSource SourceType { get; set; } = CustomReportSource.Sql;

    /// <summary>Câu lệnh SELECT / WITH (khi SourceType = Sql)</summary>
    [Display(Name = "Câu lệnh SQL")]
    public string? SqlText { get; set; }

    // ----- Khi SourceType = View -----
    [StringLength(128)][Display(Name = "View")] public string? ViewName { get; set; }
    /// <summary>Danh sách cột, cách nhau dấu phẩy (trống = tất cả)</summary>
    [Display(Name = "Cột hiển thị")] public string? ViewColumns { get; set; }
    [StringLength(128)][Display(Name = "Cột ngày để lọc theo kỳ")] public string? DateColumn { get; set; }
    [StringLength(128)][Display(Name = "Cột người phụ trách (Id)")] public string? OwnerColumn { get; set; }
    [StringLength(128)][Display(Name = "Sắp xếp theo")] public string? OrderBy { get; set; }
    [Display(Name = "Giảm dần")] public bool OrderDesc { get; set; }
    [Range(1, 100000)][Display(Name = "Giới hạn số dòng")] public int? TopRows { get; set; }

    // ----- Biểu đồ -----
    [StringLength(128)][Display(Name = "Cột nhãn biểu đồ")] public string? ChartLabelColumn { get; set; }
    [StringLength(128)][Display(Name = "Cột giá trị biểu đồ")] public string? ChartValueColumn { get; set; }

    /// <summary>Vai trò được chạy báo cáo, cách nhau dấu phẩy (Admin luôn được)</summary>
    [StringLength(100)][Display(Name = "Vai trò được xem")] public string AllowedRoles { get; set; } = "Admin,Manager";

    [Display(Name = "Đang sử dụng")] public bool IsActive { get; set; } = true;

    public int? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? UpdatedAt { get; set; }

    public bool CanRun(System.Security.Claims.ClaimsPrincipal user) =>
        IsActive && (user.IsInRole(AppRoles.Admin) || AllowedRoles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(user.IsInRole));
}
