using System.ComponentModel.DataAnnotations;

namespace CRM.Web.Models.Entities;

// Cơ cấu tổ chức dạng chuỗi: công ty mẹ → công ty con → chi nhánh. Mỗi người dùng thuộc một đơn vị;
// mỗi bản ghi nghiệp vụ thuộc đơn vị của người phụ trách (tự gán khi lưu) – xem CrmDbContext.

public enum BranchType
{
    [Display(Name = "Công ty")] Company = 1,
    [Display(Name = "Chi nhánh")] Branch = 2
}

/// <summary>Bản ghi nghiệp vụ thuộc một đơn vị (công ty / chi nhánh) – dùng để giới hạn phạm vi xem theo đơn vị.</summary>
public interface IBranchScoped
{
    int BranchId { get; set; }
}

/// <summary>Công ty / chi nhánh trong chuỗi</summary>
public class Branch
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập mã"), StringLength(20)]
    [Display(Name = "Mã đơn vị")]
    public string Code { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập tên"), StringLength(200)]
    [Display(Name = "Tên đơn vị")]
    public string Name { get; set; } = string.Empty;

    [StringLength(100)]
    [Display(Name = "Tên viết tắt")]
    public string? ShortName { get; set; }

    [Display(Name = "Loại đơn vị")]
    public BranchType Type { get; set; } = BranchType.Branch;

    [Display(Name = "Trực thuộc")]
    public int? ParentId { get; set; }
    public Branch? Parent { get; set; }

    [StringLength(20)]
    [Display(Name = "Mã số thuế")]
    public string? TaxCode { get; set; }

    [StringLength(300)]
    [Display(Name = "Địa chỉ")]
    public string? Address { get; set; }

    [StringLength(50)]
    [Display(Name = "Điện thoại")]
    public string? Phone { get; set; }

    [StringLength(150), EmailAddress]
    [Display(Name = "Email")]
    public string? Email { get; set; }

    [StringLength(150)]
    [Display(Name = "Người đại diện")]
    public string? Representative { get; set; }

    [StringLength(200)]
    [Display(Name = "Tài khoản ngân hàng")]
    public string? BankAccount { get; set; }

    [StringLength(20)]
    [Display(Name = "Ký hiệu hoá đơn")]
    public string? InvoiceSymbol { get; set; }

    [Display(Name = "Thứ tự")]
    public int SortOrder { get; set; }

    [Display(Name = "Đang hoạt động")]
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string DisplayName => string.IsNullOrWhiteSpace(ShortName) ? Name : ShortName!;
}
