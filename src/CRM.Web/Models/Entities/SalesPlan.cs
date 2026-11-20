using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CRM.Web.Models.Entities;

/// <summary>Trạng thái kế hoạch bán hàng</summary>
public enum SalesPlanStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Chờ duyệt")] Pending = 2,
    [Display(Name = "Đã duyệt")] Approved = 3,
    [Display(Name = "Bị từ chối")] Rejected = 4,
    [Display(Name = "Đã kết thúc")] Closed = 5
}

/// <summary>
/// Kế hoạch bán hàng theo kỳ (năm / quý / nhóm tháng) của một công ty / chi nhánh (gồm cả các đơn vị trực thuộc):
/// chỉ tiêu theo tháng, theo nhóm sản phẩm, phân bổ cho nhân viên (đẩy sang KPI) và chương trình hành động.
/// Thực tế được tính tự động từ dữ liệu nghiệp vụ của đơn vị.
/// </summary>
public class SalesPlan : IBranchScoped
{
    public int Id { get; set; }

    /// <summary>Đơn vị lập kế hoạch – kế hoạch tính cho đơn vị này và các đơn vị trực thuộc</summary>
    [Display(Name = "Đơn vị")]
    public int BranchId { get; set; }

    [Required, StringLength(30)]
    [Display(Name = "Số kế hoạch")]
    public string Number { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập tên kế hoạch"), StringLength(200)]
    [Display(Name = "Tên kế hoạch")]
    public string Title { get; set; } = string.Empty;

    [Range(2000, 2100)] public int Year { get; set; } = DateTime.Today.Year;
    [Range(1, 12)] public int FromMonth { get; set; } = 1;
    [Range(1, 12)] public int ToMonth { get; set; } = 12;

    [Display(Name = "Trạng thái")]
    public SalesPlanStatus Status { get; set; } = SalesPlanStatus.Draft;

    [StringLength(4000)]
    [Display(Name = "Mục tiêu & chiến lược")]
    public string? Objectives { get; set; }

    public int? CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? UpdatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }

    public int? ApprovedById { get; set; }
    public AppUser? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    [StringLength(1000)] public string? ApprovalNote { get; set; }

    /// <summary>Lần gần nhất đã đẩy phân bổ chỉ tiêu sang KPI nhân viên</summary>
    public DateTime? KpiAppliedAt { get; set; }

    public ICollection<SalesPlanMonth> Months { get; set; } = new List<SalesPlanMonth>();
    public ICollection<SalesPlanProduct> Products { get; set; } = new List<SalesPlanProduct>();
    public ICollection<SalesPlanMember> Members { get; set; } = new List<SalesPlanMember>();
    public ICollection<SalesPlanAction> Actions { get; set; } = new List<SalesPlanAction>();

    [NotMapped] public bool IsEditable => Status is SalesPlanStatus.Draft or SalesPlanStatus.Rejected;
    [NotMapped] public DateTime Start => new(Year, FromMonth, 1);
    /// <summary>Ngày kết thúc kỳ (không bao gồm)</summary>
    [NotMapped] public DateTime End => new DateTime(Year, ToMonth, 1).AddMonths(1);
    [NotMapped] public IEnumerable<int> MonthRange => Enumerable.Range(FromMonth, ToMonth - FromMonth + 1);

    [NotMapped] public decimal TargetRevenue => Months.Sum(m => m.Revenue);
    [NotMapped] public decimal TargetCollection => Months.Sum(m => m.Collection);
    [NotMapped] public int TargetContracts => Months.Sum(m => m.Contracts);
    [NotMapped] public int TargetNewCustomers => Months.Sum(m => m.NewCustomers);

    /// <summary>Tên kỳ: "Năm 2026", "Quý 2/2026", "Tháng 3/2026", "Tháng 1–6/2026"</summary>
    public string PeriodLabel()
    {
        if (FromMonth == 1 && ToMonth == 12) return Infrastructure.I18n.T("Năm {0}", Year);
        if (FromMonth == ToMonth) return Infrastructure.I18n.T("Tháng {0}/{1}", FromMonth, Year);
        if (ToMonth - FromMonth == 2 && FromMonth % 3 == 1) return Infrastructure.I18n.T("Quý {0}/{1}", (FromMonth - 1) / 3 + 1, Year);
        return Infrastructure.I18n.T("Tháng {0}–{1}/{2}", FromMonth, ToMonth, Year);
    }
}

/// <summary>Chỉ tiêu một tháng của kế hoạch (doanh số / thu tiền: trước VAT, đồng)</summary>
public class SalesPlanMonth
{
    public int Id { get; set; }
    public int SalesPlanId { get; set; }
    public SalesPlan? SalesPlan { get; set; }

    [Range(1, 12)] public int Month { get; set; }
    [Column(TypeName = "decimal(18,0)")] public decimal Revenue { get; set; }
    [Column(TypeName = "decimal(18,0)")] public decimal Collection { get; set; }
    public int Contracts { get; set; }
    public int NewCustomers { get; set; }
}

/// <summary>Chỉ tiêu doanh số theo nhóm sản phẩm trong cả kỳ</summary>
public class SalesPlanProduct
{
    public int Id { get; set; }
    public int SalesPlanId { get; set; }
    public SalesPlan? SalesPlan { get; set; }

    [Required, StringLength(100)] public string Category { get; set; } = string.Empty;
    [Column(TypeName = "decimal(18,0)")] public decimal Revenue { get; set; }
    [StringLength(300)] public string? Note { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Phân bổ chỉ tiêu cho nhân viên theo tỷ lệ % (áp cho từng tháng của kế hoạch)</summary>
public class SalesPlanMember
{
    public int Id { get; set; }
    public int SalesPlanId { get; set; }
    public SalesPlan? SalesPlan { get; set; }

    public int UserId { get; set; }
    public AppUser? User { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    [Range(0, 100)]
    public decimal SharePercent { get; set; }
}

/// <summary>
/// Hành động / chương trình để đạt kế hoạch. Có thể giao thành công việc (ActivityId) – trạng thái theo công việc;
/// không giao việc thì đánh dấu hoàn thành thủ công.
/// </summary>
public class SalesPlanAction
{
    public int Id { get; set; }
    public int SalesPlanId { get; set; }
    public SalesPlan? SalesPlan { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập tên hành động"), StringLength(200)]
    public string Title { get; set; } = string.Empty;
    [StringLength(1000)] public string? Description { get; set; }

    public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }

    public DateTime? StartDate { get; set; }
    public DateTime? DueDate { get; set; }
    [Column(TypeName = "decimal(18,0)")] public decimal Budget { get; set; }

    /// <summary>Công việc đã giao (không ràng buộc khóa ngoại: công việc bị xóa → coi như chưa giao)</summary>
    public int? ActivityId { get; set; }
    public DateTime? DoneAt { get; set; }
    public int SortOrder { get; set; }
}
