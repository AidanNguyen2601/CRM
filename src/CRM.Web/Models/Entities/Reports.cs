using System.ComponentModel.DataAnnotations;

namespace CRM.Web.Models.Entities;

public enum CustomReportSource
{
    [Display(Name = "Câu lệnh SQL")] Sql = 1,
    [Display(Name = "Chọn từ View")] View = 2,
    [Display(Name = "Trình dựng (không cần SQL)")] Builder = 3
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
    /// <summary>Kiểu biểu đồ: bar (cột ngang) | line (đường) | pie (tròn) | none; trống = cột ngang khi có chọn cột biểu đồ</summary>
    [StringLength(10)][Display(Name = "Kiểu biểu đồ")] public string? ChartKind { get; set; }

    /// <summary>Định nghĩa báo cáo dạng trình dựng (JSON: nguồn, cột, nhóm / phép tính, điều kiện lọc) – xem ReportBuilderDef</summary>
    public string? BuilderJson { get; set; }

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

public enum ScheduleFrequency
{
    [Display(Name = "Hằng ngày")] Daily = 1,
    [Display(Name = "Hằng tuần")] Weekly = 2,
    [Display(Name = "Hằng tháng")] Monthly = 3
}

/// <summary>Kỳ dữ liệu của báo cáo gửi định kỳ (tính lùi từ ngày chạy)</summary>
public enum SchedulePeriod
{
    [Display(Name = "Hôm qua")] Yesterday = 1,
    [Display(Name = "7 ngày qua")] Last7Days = 2,
    [Display(Name = "30 ngày qua")] Last30Days = 9,
    [Display(Name = "Tháng này (đến hôm nay)")] ThisMonth = 3,
    [Display(Name = "Tháng trước")] LastMonth = 4,
    [Display(Name = "Quý này (đến hôm nay)")] ThisQuarter = 5,
    [Display(Name = "Quý trước")] LastQuarter = 6,
    [Display(Name = "Năm nay (đến hôm nay)")] ThisYear = 7,
    [Display(Name = "Năm trước")] LastYear = 8
}

/// <summary>
/// Lịch gửi báo cáo qua email: mỗi người nhận nhận báo cáo theo ĐÚNG quyền và vùng dữ liệu của chính họ (phân quyền báo cáo),
/// kèm file Excel. ReportKey = "std:{key}" hoặc "custom:{id}".
/// </summary>
public class ReportSchedule
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Vui lòng nhập tên lịch gửi"), StringLength(150)][Display(Name = "Tên lịch gửi")] public string Name { get; set; } = string.Empty;
    [Required, StringLength(60)][Display(Name = "Báo cáo")] public string ReportKey { get; set; } = string.Empty;
    [Display(Name = "Tần suất")] public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Weekly;
    /// <summary>Thứ trong tuần (0 = Chủ nhật … 6 = Thứ bảy) khi gửi hằng tuần</summary>
    [Range(0, 6)][Display(Name = "Thứ")] public int DayOfWeek { get; set; } = 1;
    /// <summary>Ngày trong tháng (1–31; tháng ngắn hơn thì gửi ngày cuối tháng) khi gửi hằng tháng</summary>
    [Range(1, 31)][Display(Name = "Ngày trong tháng")] public int DayOfMonth { get; set; } = 1;
    [Range(0, 23)][Display(Name = "Giờ gửi")] public int Hour { get; set; } = 8;
    [Display(Name = "Kỳ dữ liệu")] public SchedulePeriod Period { get; set; } = SchedulePeriod.LastMonth;
    /// <summary>Id người dùng nhận, cách nhau dấu phẩy</summary>
    [StringLength(1000)] public string RecipientIds { get; set; } = string.Empty;
    [Display(Name = "Đính kèm file Excel")] public bool IncludeExcel { get; set; } = true;
    [Range(0, 100)][Display(Name = "Số dòng xem trước trong email")] public int PreviewRows { get; set; } = 10;
    [Display(Name = "Đang gửi")] public bool IsActive { get; set; } = true;
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    [StringLength(500)] public string? LastStatus { get; set; }
    public int? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
