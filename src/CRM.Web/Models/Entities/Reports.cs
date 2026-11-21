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

/// <summary>Vùng dữ liệu được xem trong báo cáo</summary>
public enum ReportDataScope
{
    [Display(Name = "Chỉ dữ liệu mình phụ trách")] Own = 1,
    [Display(Name = "Đơn vị của mình")] Unit = 2,
    [Display(Name = "Đơn vị của mình và cấp dưới")] UnitTree = 3,
    [Display(Name = "Các đơn vị chỉ định (kèm cấp dưới)")] Specific = 4,
    [Display(Name = "Toàn hệ thống")] All = 5
}

public enum ReportAccessSubject
{
    [Display(Name = "Vai trò")] Role = 1,
    [Display(Name = "Người dùng")] User = 2
}

/// <summary>
/// Phân quyền báo cáo: ai (vai trò / người dùng) được xem, xuất Excel báo cáo nào (ReportKey = "std:{key}", "custom:{id}" hoặc "*" = mọi báo cáo)
/// và trong vùng dữ liệu nào. Ưu tiên: người dùng + báo cáo cụ thể → người dùng + "*" → vai trò + báo cáo cụ thể → vai trò + "*".
/// Không có quy tắc khớp → quyền mặc định theo vai trò; muốn chặn ai xem báo cáo thì thêm quy tắc CanView = false cho vai trò / người đó.
/// </summary>
public class ReportAccess
{
    public const string AllReports = "*";
    public int Id { get; set; }
    [Required, StringLength(60)] public string ReportKey { get; set; } = AllReports;
    public ReportAccessSubject SubjectType { get; set; } = ReportAccessSubject.Role;
    /// <summary>Tên vai trò hoặc Id người dùng</summary>
    [Required, StringLength(50)] public string Subject { get; set; } = string.Empty;
    [Display(Name = "Được xem")] public bool CanView { get; set; } = true;
    [Display(Name = "Được xuất Excel")] public bool CanExport { get; set; } = true;
    [Display(Name = "Vùng dữ liệu")] public ReportDataScope Scope { get; set; } = ReportDataScope.UnitTree;
    /// <summary>Đơn vị chỉ định (Id, cách nhau dấu phẩy) khi Scope = Specific</summary>
    [StringLength(500)] public string? BranchIds { get; set; }
    public int? UpdatedById { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
