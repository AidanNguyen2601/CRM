using System.ComponentModel.DataAnnotations;
using CRM.Web.Models.Entities;

namespace CRM.Web.ViewModels;

public class SalesPlanFilter : ListFilter
{
    public SalesPlanStatus? Status { get; set; }
    public int? Year { get; set; }
}

/// <summary>Form kế hoạch bán hàng. Tiền nhập theo đơn vị TRIỆU ĐỒNG (máy chủ nhân 1.000.000 khi lưu).</summary>
public class SalesPlanFormViewModel
{
    public int Id { get; set; }

    [Display(Name = "Số kế hoạch")]
    public string? Number { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập tên kế hoạch"), StringLength(200)]
    [Display(Name = "Tên kế hoạch")]
    public string Title { get; set; } = string.Empty;

    [Display(Name = "Đơn vị")]
    public int BranchId { get; set; }

    [Range(2000, 2100)][Display(Name = "Năm")] public int Year { get; set; } = DateTime.Today.Year;
    [Range(1, 12)][Display(Name = "Từ tháng")] public int FromMonth { get; set; } = 1;
    [Range(1, 12)][Display(Name = "Đến tháng")] public int ToMonth { get; set; } = 12;

    [StringLength(4000)]
    [Display(Name = "Mục tiêu & chiến lược")]
    public string? Objectives { get; set; }

    /// <summary>Luôn đủ 12 tháng; chỉ các tháng trong kỳ được lưu</summary>
    public List<SalesPlanMonthInput> Months { get; set; } = new();
    public List<SalesPlanProductInput> Products { get; set; } = new();
    public List<SalesPlanMemberInput> Members { get; set; } = new();
}

public class SalesPlanMonthInput
{
    public int Month { get; set; }
    [Range(0, 1_000_000_000)] public decimal Revenue { get; set; }
    [Range(0, 1_000_000_000)] public decimal Collection { get; set; }
    [Range(0, 1_000_000)] public int Contracts { get; set; }
    [Range(0, 1_000_000)] public int NewCustomers { get; set; }
}

public class SalesPlanProductInput
{
    [StringLength(100)] public string? Category { get; set; }
    [Range(0, 1_000_000_000)] public decimal Revenue { get; set; }
    [StringLength(300)] public string? Note { get; set; }
}

public class SalesPlanMemberInput
{
    public int UserId { get; set; }
    [Range(0, 100)] public decimal SharePercent { get; set; }
}

public class SalesPlanActionInput
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Vui lòng nhập tên hành động"), StringLength(200)] public string Title { get; set; } = string.Empty;
    [StringLength(1000)] public string? Description { get; set; }
    public int? OwnerId { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? DueDate { get; set; }
    /// <summary>Ngân sách – đơn vị triệu đồng</summary>
    [Range(0, 1_000_000)] public decimal Budget { get; set; }
}
