using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CRM.Web.Models.Entities;

// Hợp đồng → Hoá đơn → Thanh toán (công nợ phải thu)

/// <summary>Dòng hàng dùng chung cho hợp đồng và hoá đơn (giá trước thuế)</summary>
public abstract class SalesLineBase
{
    public int Id { get; set; }

    public int? ProductId { get; set; }
    public Product? Product { get; set; }

    [Required, StringLength(300)]
    public string Description { get; set; } = string.Empty;

    [StringLength(30)]
    public string? Unit { get; set; }

    [Column(TypeName = "decimal(18,2)")] public decimal Quantity { get; set; } = 1;
    [Column(TypeName = "decimal(18,0)")] public decimal UnitPrice { get; set; }
    [Column(TypeName = "decimal(18,0)")] public decimal LineTotal { get; set; }

    public int SortOrder { get; set; }

    public void Recalculate() => LineTotal = Math.Round(Quantity * UnitPrice, 0);
}

/// <summary>Hợp đồng bán hàng</summary>
public class Contract : IOwned
{
    public int Id { get; set; }

    [Required, StringLength(30)]
    [Display(Name = "Số hợp đồng")]
    public string Number { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập tên hợp đồng"), StringLength(200)]
    [Display(Name = "Tên hợp đồng")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng chọn khách hàng")]
    [Display(Name = "Khách hàng")]
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    [Display(Name = "Người đại diện")]
    public int? ContactId { get; set; }
    public Contact? Contact { get; set; }

    [Display(Name = "Cơ hội")]
    public int? OpportunityId { get; set; }
    public Opportunity? Opportunity { get; set; }

    [Display(Name = "Báo giá")]
    public int? QuoteId { get; set; }
    public Quote? Quote { get; set; }

    [Display(Name = "Phương án kinh doanh")]
    public int? BusinessPlanId { get; set; }
    public BusinessPlan? BusinessPlan { get; set; }

    [DataType(DataType.Date)][Display(Name = "Ngày ký")] public DateTime? SignDate { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày hiệu lực")] public DateTime? StartDate { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày kết thúc")] public DateTime? EndDate { get; set; }

    [Display(Name = "Trạng thái")]
    public ContractStatus Status { get; set; } = ContractStatus.Draft;

    [Column(TypeName = "decimal(5,2)")]
    [Range(0, 100)]
    [Display(Name = "Thuế GTGT (%)")]
    public decimal VatPercent { get; set; } = 10;

    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá trị trước thuế")] public decimal SubTotal { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Thuế GTGT")] public decimal VatAmount { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Tổng giá trị")] public decimal Total { get; set; }

    [StringLength(2000)]
    [Display(Name = "Điều khoản thanh toán")]
    public string? PaymentTerms { get; set; }

    [StringLength(2000)]
    [Display(Name = "Ghi chú")]
    public string? Notes { get; set; }

    [Display(Name = "Phụ trách")]
    public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public ICollection<ContractLine> Lines { get; set; } = new List<ContractLine>();
    public ICollection<ContractMilestone> Milestones { get; set; } = new List<ContractMilestone>();

    [NotMapped] public bool IsEditable => Status == ContractStatus.Draft;
    /// <summary>Đã ký và chưa hủy → được xuất hoá đơn / thu tiền</summary>
    [NotMapped] public bool IsSigned => Status is ContractStatus.Active or ContractStatus.Completed or ContractStatus.Liquidated;

    public void Recalculate()
    {
        foreach (var l in Lines) l.Recalculate();
        SubTotal = Lines.Sum(l => l.LineTotal);
        VatAmount = Math.Round(SubTotal * VatPercent / 100m, 0);
        Total = SubTotal + VatAmount;
        foreach (var m in Milestones) m.Amount = Math.Round(Total * m.Percent / 100m, 0);
    }
}

public class ContractLine : SalesLineBase
{
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
}

/// <summary>Đợt thanh toán theo hợp đồng (tỷ lệ % trên tổng giá trị gồm thuế)</summary>
public class ContractMilestone
{
    public int Id { get; set; }
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }

    [Required, StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Column(TypeName = "decimal(5,2)")] public decimal Percent { get; set; }
    [Column(TypeName = "decimal(18,0)")] public decimal Amount { get; set; }

    public DateTime? DueDate { get; set; }

    [StringLength(300)]
    public string? Condition { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>Hoá đơn bán hàng (GTGT)</summary>
public class Invoice : IOwned
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập ký hiệu hoá đơn"), StringLength(20)]
    [Display(Name = "Ký hiệu")]
    public string Symbol { get; set; } = string.Empty;

    [Required, StringLength(20)]
    [Display(Name = "Số hoá đơn")]
    public string Number { get; set; } = string.Empty;

    [DataType(DataType.Date)]
    [Display(Name = "Ngày hoá đơn")]
    public DateTime InvoiceDate { get; set; } = DateTime.Today;

    [DataType(DataType.Date)]
    [Display(Name = "Hạn thanh toán")]
    public DateTime? DueDate { get; set; }

    [Required(ErrorMessage = "Vui lòng chọn khách hàng")]
    [Display(Name = "Khách hàng")]
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    [Display(Name = "Hợp đồng")]
    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }

    [Display(Name = "Đợt thanh toán")]
    public int? MilestoneId { get; set; }
    public ContractMilestone? Milestone { get; set; }

    [Display(Name = "Trạng thái")]
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    [Column(TypeName = "decimal(5,2)")]
    [Range(0, 100)]
    [Display(Name = "Thuế GTGT (%)")]
    public decimal VatPercent { get; set; } = 10;

    [Column(TypeName = "decimal(18,0)")][Display(Name = "Cộng tiền hàng")] public decimal SubTotal { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Thuế GTGT")] public decimal VatAmount { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Tổng thanh toán")] public decimal Total { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Đã thu")] public decimal PaidAmount { get; set; }

    [StringLength(1000)]
    [Display(Name = "Ghi chú")]
    public string? Notes { get; set; }

    [Display(Name = "Phụ trách")]
    public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? IssuedAt { get; set; }

    public ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();

    [NotMapped] public bool IsEditable => Status == InvoiceStatus.Draft;
    /// <summary>Hoá đơn có hiệu lực công nợ (đã phát hành, chưa hủy)</summary>
    [NotMapped] public bool IsBillable => Status is InvoiceStatus.Issued or InvoiceStatus.PartiallyPaid or InvoiceStatus.Paid;
    [NotMapped] public decimal Outstanding => IsBillable ? Math.Max(0, Total - PaidAmount) : 0;
    [NotMapped] public bool IsOverdue => Outstanding > 0 && DueDate.HasValue && DueDate.Value.Date < DateTime.Today;
    [NotMapped] public bool CanReceivePayment => Status is InvoiceStatus.Issued or InvoiceStatus.PartiallyPaid;
    [NotMapped] public string FullNumber => $"{Symbol} – {Number}";

    public void Recalculate()
    {
        foreach (var l in Lines) l.Recalculate();
        SubTotal = Lines.Sum(l => l.LineTotal);
        VatAmount = Math.Round(SubTotal * VatPercent / 100m, 0);
        Total = SubTotal + VatAmount;
    }

    /// <summary>Cập nhật số đã thu và trạng thái (chỉ áp dụng với hoá đơn đã phát hành)</summary>
    public void ApplyPayments(decimal paid)
    {
        PaidAmount = paid;
        if (!IsBillable) return;
        Status = paid <= 0 ? InvoiceStatus.Issued : paid >= Total ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
    }
}

public class InvoiceLine : SalesLineBase
{
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
}

/// <summary>Phiếu thu – ghi nhận khách hàng thanh toán</summary>
public class Payment : IOwned
{
    public int Id { get; set; }

    [Required, StringLength(30)]
    [Display(Name = "Số phiếu thu")]
    public string Number { get; set; } = string.Empty;

    [DataType(DataType.Date)]
    [Display(Name = "Ngày thu")]
    public DateTime PaymentDate { get; set; } = DateTime.Today;

    [Required(ErrorMessage = "Vui lòng chọn khách hàng")]
    [Display(Name = "Khách hàng")]
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    [Display(Name = "Hợp đồng")]
    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }

    [Display(Name = "Hoá đơn")]
    public int? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    [Column(TypeName = "decimal(18,0)")]
    [Range(1, 1_000_000_000_000, ErrorMessage = "Số tiền phải lớn hơn 0")]
    [Display(Name = "Số tiền")]
    public decimal Amount { get; set; }

    [Required(ErrorMessage = "Chọn phương thức thanh toán"), StringLength(100)]
    [Display(Name = "Phương thức")]
    public string Method { get; set; } = string.Empty;

    [StringLength(100)]
    [Display(Name = "Số chứng từ / UNC")]
    public string? Reference { get; set; }

    [StringLength(500)]
    [Display(Name = "Nội dung")]
    public string? Notes { get; set; }

    [Display(Name = "Người thu")]
    public int? OwnerId { get; set; }
    public AppUser? Owner { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>File đính kèm của một bản ghi nghiệp vụ. Nội dung file lưu trên đĩa (thư mục cấu hình Attachments:Path).</summary>
public class Attachment
{
    public int Id { get; set; }
    public AttachmentTarget TargetType { get; set; }
    public int TargetId { get; set; }

    /// <summary>Tên file gốc người dùng tải lên (chỉ để hiển thị / tải về)</summary>
    [Required, StringLength(255)] public string FileName { get; set; } = string.Empty;
    [Required, StringLength(100)] public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }

    /// <summary>Đường dẫn tương đối trong thư mục lưu trữ (yyyy/MM/guid.ext) – không bao giờ lấy từ tên file người dùng</summary>
    [Required, StringLength(300)] public string StoragePath { get; set; } = string.Empty;

    [StringLength(300)] public string? Note { get; set; }

    public int? UploadedById { get; set; }
    public AppUser? UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.Now;
}
