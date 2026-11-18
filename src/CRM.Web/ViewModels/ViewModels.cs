using System.ComponentModel.DataAnnotations;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;

namespace CRM.Web.ViewModels;

public class LoginViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập tên đăng nhập")]
    [Display(Name = "Tên đăng nhập")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập mật khẩu")]
    [DataType(DataType.Password)]
    [Display(Name = "Mật khẩu")]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Ghi nhớ đăng nhập")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

public class ChangePasswordViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập mật khẩu hiện tại")]
    [DataType(DataType.Password)]
    [Display(Name = "Mật khẩu hiện tại")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập mật khẩu mới")]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "Mật khẩu tối thiểu 6 ký tự")]
    [DataType(DataType.Password)]
    [Display(Name = "Mật khẩu mới")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng xác nhận mật khẩu mới")]
    [Compare(nameof(NewPassword), ErrorMessage = "Xác nhận mật khẩu không khớp")]
    [DataType(DataType.Password)]
    [Display(Name = "Xác nhận mật khẩu mới")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class StageSummary
{
    public OpportunityStage Stage { get; set; }
    public int Count { get; set; }
    public decimal Amount { get; set; }
}

public class DashboardViewModel
{
    public decimal OpenPipeline { get; set; }
    public decimal WeightedPipeline { get; set; }
    public int OpenOpportunities { get; set; }
    public decimal WonThisMonth { get; set; }
    public int WonCountThisMonth { get; set; }
    public decimal WinRate { get; set; }
    public int NewLeads30d { get; set; }
    public decimal LeadConversionRate { get; set; }
    public int OverdueActivities { get; set; }
    public List<StageSummary> Pipeline { get; set; } = new();
    public List<(string Month, decimal Amount)> WonByMonth { get; set; } = new();
    public List<(string Source, int Count)> LeadsBySource { get; set; } = new();
    public List<Opportunity> ClosingSoon { get; set; } = new();
    public List<Activity> UpcomingActivities { get; set; } = new();
}

public class ListFilter
{
    public string? Keyword { get; set; }
    public int? OwnerId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 15;
}

public class LeadFilter : ListFilter
{
    public LeadStatus? Status { get; set; }
    public LeadSource? Source { get; set; }
}

public class CustomerFilter : ListFilter
{
    public CustomerType? Type { get; set; }
}

public class OpportunityFilter : ListFilter
{
    public OpportunityStage? Stage { get; set; }
    /// <summary>open | won | lost | all</summary>
    public string? Scope { get; set; } = "open";
}

public class ActivityFilter
{
    /// <summary>today | overdue | upcoming | done | all</summary>
    public string View { get; set; } = "upcoming";
    public ActivityType? Type { get; set; }
    public int? OwnerId { get; set; }
}

public class LeadConvertViewModel
{
    public int LeadId { get; set; }
    public Lead? Lead { get; set; }

    [Display(Name = "Gắn vào khách hàng đã có")]
    public int? ExistingCustomerId { get; set; }

    [Display(Name = "Tên khách hàng mới")]
    [StringLength(200)]
    public string? NewCustomerName { get; set; }

    [Display(Name = "Tạo cơ hội bán hàng")]
    public bool CreateOpportunity { get; set; } = true;

    [Display(Name = "Tên cơ hội")]
    [StringLength(200)]
    public string? OpportunityName { get; set; }

    [Display(Name = "Giá trị cơ hội")]
    [Range(0, 1_000_000_000_000)]
    public decimal OpportunityAmount { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "Dự kiến chốt")]
    public DateTime? ExpectedCloseDate { get; set; } = DateTime.Today.AddDays(30);
}

public class OpportunityDetailViewModel
{
    public Opportunity Opportunity { get; set; } = null!;
    public List<Activity> Activities { get; set; } = new();
    public List<Quote> Quotes { get; set; } = new();
    public List<BusinessPlan> Plans { get; set; } = new();
}

public class QuoteLineInput
{
    public int? ProductId { get; set; }

    [Required(ErrorMessage = "Nhập diễn giải")]
    [StringLength(300)]
    public string Description { get; set; } = string.Empty;

    [StringLength(30)]
    public string? Unit { get; set; }

    [Range(0.01, 1_000_000, ErrorMessage = "Số lượng > 0")]
    public decimal Quantity { get; set; } = 1;

    [Range(0, 100_000_000_000)]
    public decimal UnitPrice { get; set; }

    [Range(0, 100)]
    public decimal DiscountPercent { get; set; }
}

public class QuoteFormViewModel
{
    public int Id { get; set; }

    [Display(Name = "Số báo giá")]
    public string Number { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng chọn khách hàng")]
    [Display(Name = "Khách hàng")]
    public int? CustomerId { get; set; }

    [Display(Name = "Người nhận")]
    public int? ContactId { get; set; }

    [Display(Name = "Cơ hội")]
    public int? OpportunityId { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "Ngày báo giá")]
    public DateTime QuoteDate { get; set; } = DateTime.Today;

    [DataType(DataType.Date)]
    [Display(Name = "Hiệu lực đến")]
    public DateTime ValidUntil { get; set; } = DateTime.Today.AddDays(30);

    [Range(0, 100)]
    [Display(Name = "Chiết khấu chung (%)")]
    public decimal DiscountPercent { get; set; }

    [Range(0, 100)]
    [Display(Name = "Thuế GTGT (%)")]
    public decimal VatPercent { get; set; } = 10;

    [StringLength(2000)]
    [Display(Name = "Điều khoản / ghi chú")]
    public string? Terms { get; set; } = "Thanh toán 50% khi ký hợp đồng, 50% sau nghiệm thu. Báo giá có hiệu lực 30 ngày.";

    public List<QuoteLineInput> Lines { get; set; } = new();
}

public class UserFormViewModel
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập tên đăng nhập")]
    [StringLength(50, MinimumLength = 3)]
    [RegularExpression(@"^[a-zA-Z0-9_.]+$", ErrorMessage = "Chỉ dùng chữ không dấu, số, dấu chấm, gạch dưới")]
    [Display(Name = "Tên đăng nhập")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập họ tên")]
    [StringLength(150)]
    [Display(Name = "Họ và tên")]
    public string FullName { get; set; } = string.Empty;

    [EmailAddress, StringLength(150)]
    [Display(Name = "Email")]
    public string? Email { get; set; }

    [StringLength(100, MinimumLength = 6, ErrorMessage = "Mật khẩu tối thiểu 6 ký tự")]
    [DataType(DataType.Password)]
    [Display(Name = "Mật khẩu")]
    public string? Password { get; set; }

    [Required]
    [Display(Name = "Vai trò")]
    public string Role { get; set; } = AppRoles.Sales;

    [Display(Name = "Kích hoạt")]
    public bool IsActive { get; set; } = true;
}

// ---------- Phương án kinh doanh ----------
public class BusinessPlanLineInput
{
    public int? ProductId { get; set; }

    [Required(ErrorMessage = "Nhập diễn giải")]
    [StringLength(300)]
    public string Description { get; set; } = string.Empty;

    [StringLength(30)]
    public string? Unit { get; set; }

    [Range(0.01, 1_000_000, ErrorMessage = "Số lượng > 0")]
    public decimal Quantity { get; set; } = 1;

    [Range(0, 100_000_000_000)]
    public decimal CostPrice { get; set; }

    [Range(0, 100_000_000_000)]
    public decimal SalePrice { get; set; }
}

public class BusinessPlanCostInput
{
    [Required(ErrorMessage = "Chọn loại chi phí")]
    [StringLength(200)]
    public string Category { get; set; } = string.Empty;

    [StringLength(300)]
    public string? Description { get; set; }

    [Range(0, 100_000_000_000)]
    public decimal Amount { get; set; }
}

public class BusinessPlanFormViewModel
{
    public int Id { get; set; }

    [Display(Name = "Số PAKD")]
    public string Number { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập tên phương án"), StringLength(200)]
    [Display(Name = "Tên phương án")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng chọn khách hàng")]
    [Display(Name = "Khách hàng")]
    public int? CustomerId { get; set; }

    [Display(Name = "Cơ hội")]
    public int? OpportunityId { get; set; }

    public int? QuoteId { get; set; }
    public string? QuoteNumber { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "Ngày lập")]
    public DateTime PlanDate { get; set; } = DateTime.Today;

    [StringLength(2000)]
    [Display(Name = "Thuyết minh / ghi chú")]
    public string? Notes { get; set; }

    public List<BusinessPlanLineInput> Lines { get; set; } = new();
    public List<BusinessPlanCostInput> Costs { get; set; } = new();
}

public class BusinessPlanFilter : ListFilter
{
    public BusinessPlanStatus? Status { get; set; }
}

// ---------- Hợp đồng / Hoá đơn ----------
public class SalesLineInput
{
    public int? ProductId { get; set; }

    [Required(ErrorMessage = "Nhập diễn giải")]
    [StringLength(300)]
    public string Description { get; set; } = string.Empty;

    [StringLength(30)]
    public string? Unit { get; set; }

    [Range(0.01, 1_000_000, ErrorMessage = "Số lượng > 0")]
    public decimal Quantity { get; set; } = 1;

    [Range(0, 100_000_000_000)]
    public decimal UnitPrice { get; set; }
}

public class MilestoneInput
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Nhập tên đợt thanh toán")]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Range(0.01, 100, ErrorMessage = "Tỷ lệ đợt thanh toán phải từ 0,01 đến 100%")]
    public decimal Percent { get; set; }

    [DataType(DataType.Date)]
    public DateTime? DueDate { get; set; }

    [StringLength(300)]
    public string? Condition { get; set; }
}

public class ContractFormViewModel
{
    public int Id { get; set; }

    [Display(Name = "Số hợp đồng")]
    public string Number { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập tên hợp đồng"), StringLength(200)]
    [Display(Name = "Tên hợp đồng")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng chọn khách hàng")]
    [Display(Name = "Khách hàng")]
    public int? CustomerId { get; set; }

    [Display(Name = "Người đại diện")]
    public int? ContactId { get; set; }

    [Display(Name = "Cơ hội")]
    public int? OpportunityId { get; set; }

    public int? QuoteId { get; set; }
    public int? BusinessPlanId { get; set; }
    public string? SourceText { get; set; }

    [DataType(DataType.Date)][Display(Name = "Ngày hiệu lực")] public DateTime? StartDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Ngày kết thúc")] public DateTime? EndDate { get; set; } = DateTime.Today.AddMonths(12);

    [Range(0, 100)]
    [Display(Name = "Thuế GTGT (%)")]
    public decimal VatPercent { get; set; } = 10;

    [StringLength(2000)]
    [Display(Name = "Điều khoản thanh toán")]
    public string? PaymentTerms { get; set; }

    [StringLength(2000)]
    [Display(Name = "Ghi chú")]
    public string? Notes { get; set; }

    public List<SalesLineInput> Lines { get; set; } = new();
    public List<MilestoneInput> Milestones { get; set; } = new();
}

public class ContractFilter : ListFilter
{
    public ContractStatus? Status { get; set; }
}

public class ContractDetailViewModel
{
    public Contract Contract { get; set; } = null!;
    public List<Invoice> Invoices { get; set; } = new();
    public List<Payment> Payments { get; set; } = new();
    public decimal Invoiced => Invoices.Where(i => i.IsBillable).Sum(i => i.Total);
    public decimal Paid => Payments.Sum(p => p.Amount);
    public decimal Receivable => Contract.Total - Paid;
    public decimal InvoicedFor(int milestoneId) => Invoices.Where(i => i.MilestoneId == milestoneId && i.Status != InvoiceStatus.Cancelled).Sum(i => i.Total);
}

public class InvoiceFormViewModel
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập ký hiệu hoá đơn"), StringLength(20)]
    [Display(Name = "Ký hiệu")]
    public string Symbol { get; set; } = string.Empty;

    [Display(Name = "Số hoá đơn")]
    public string Number { get; set; } = string.Empty;

    [DataType(DataType.Date)][Display(Name = "Ngày hoá đơn")] public DateTime InvoiceDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Hạn thanh toán")] public DateTime? DueDate { get; set; } = DateTime.Today.AddDays(30);

    [Required(ErrorMessage = "Vui lòng chọn khách hàng")]
    [Display(Name = "Khách hàng")]
    public int? CustomerId { get; set; }

    [Display(Name = "Hợp đồng")]
    public int? ContractId { get; set; }

    public int? MilestoneId { get; set; }
    public string? MilestoneName { get; set; }

    [Range(0, 100)]
    [Display(Name = "Thuế GTGT (%)")]
    public decimal VatPercent { get; set; } = 10;

    [StringLength(1000)]
    [Display(Name = "Ghi chú")]
    public string? Notes { get; set; }

    public List<SalesLineInput> Lines { get; set; } = new();
}

public class InvoiceFilter : ListFilter
{
    /// <summary>open (còn nợ) | overdue | paid | draft | all</summary>
    public string View { get; set; } = "open";
    public int? CustomerId { get; set; }
}

public class PaymentFilter : ListFilter
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? Method { get; set; }
    public int? CustomerId { get; set; }
}

// ==================== Tiếp thị khách hàng ====================

public class CampaignFilter : ListFilter
{
    public CampaignStatus? Status { get; set; }
    public CampaignChannel? Channel { get; set; }
    public int? Year { get; set; }
}

public class CampaignMemberFilter : ListFilter
{
    public CampaignMemberStatus? MemberStatus { get; set; }
}

/// <summary>Chỉ số hiệu quả của một chiến dịch (hoặc cộng dồn nhiều chiến dịch)</summary>
public class CampaignStats
{
    public int Members { get; set; }
    public int Reached { get; set; }
    public int Positive { get; set; }
    public int Unsubscribed { get; set; }
    public int Leads { get; set; }
    public int ConvertedLeads { get; set; }
    public int Opportunities { get; set; }
    public decimal Pipeline { get; set; }
    public int WonCount { get; set; }
    public decimal Won { get; set; }
    public decimal Cost { get; set; }
    public decimal Budget { get; set; }

    /// <summary>Tỷ lệ tiếp cận: đã tiếp cận / tổng đối tượng (%)</summary>
    public decimal ReachRate => Members == 0 ? 0 : Math.Round(Reached * 100m / Members, 1);
    /// <summary>Tỷ lệ phản hồi tích cực trên số đã tiếp cận (%)</summary>
    public decimal ResponseRate => Reached == 0 ? 0 : Math.Round(Positive * 100m / Reached, 1);
    public decimal? CostPerLead => Leads == 0 || Cost == 0 ? null : Math.Round(Cost / Leads, 0);
    /// <summary>ROI = (doanh số thắng − chi phí) / chi phí (%); không có chi phí → không tính</summary>
    public decimal? Roi => Cost == 0 ? null : Math.Round((Won - Cost) * 100m / Cost, 0);
    public decimal BudgetUsed => Budget == 0 ? 0 : Math.Round(Cost * 100m / Budget, 0);

    public static string RoiCss(decimal? roi) => roi == null ? "text-muted" : roi < 0 ? "text-danger" : roi >= 100 ? "text-success" : "";
}

public class CampaignDetailViewModel
{
    public Campaign Campaign { get; set; } = null!;
    public CampaignStats Stats { get; set; } = new();
    public Dictionary<CampaignMemberStatus, int> StatusCounts { get; set; } = new();
    public PagedList<CampaignMember> Members { get; set; } = null!;
    public CampaignMemberFilter Filter { get; set; } = new();
    public List<Lead> Leads { get; set; } = new();
    public List<Opportunity> Opportunities { get; set; } = new();
    public bool CanEdit { get; set; }
}

public enum CampaignAudience
{
    [Display(Name = "Khách hàng (người liên hệ)")] Customers = 1,
    [Display(Name = "Lead (KH tiềm năng)")] Leads = 2
}

/// <summary>Bộ lọc phân khúc để thêm đối tượng vào chiến dịch</summary>
public class CampaignSegmentViewModel
{
    public int CampaignId { get; set; }
    public CampaignAudience Audience { get; set; } = CampaignAudience.Customers;

    // Khách hàng
    public CustomerType? CustomerType { get; set; }
    public string? Industry { get; set; }
    public string? City { get; set; }
    /// <summary>true: mọi người liên hệ; false: chỉ liên hệ chính (không có liên hệ → dùng thông tin khách hàng)</summary>
    public bool AllContacts { get; set; }

    // Lead
    public LeadStatus? LeadStatus { get; set; }
    public LeadRating? LeadRating { get; set; }
    public LeadSource? LeadSource { get; set; }

    public int? OwnerId { get; set; }
    public string? Keyword { get; set; }
    /// <summary>Bỏ qua đối tượng thiếu thông tin liên lạc theo kênh (email cho Email, SĐT cho SMS/Zalo/Telesales)</summary>
    public bool RequireChannelInfo { get; set; } = true;
    /// <summary>Bỏ qua người đã từ chối nhận tin ở bất kỳ chiến dịch nào</summary>
    public bool ExcludeUnsubscribed { get; set; } = true;

    // Kết quả xem trước
    public Campaign? Campaign { get; set; }
    public bool Previewed { get; set; }
    public List<CampaignMember> Preview { get; set; } = new();
    public int ToAdd { get; set; }
    public int SkippedDuplicate { get; set; }
    public int SkippedNoInfo { get; set; }
    public int SkippedUnsubscribed { get; set; }
}

public class TicketFilter : ListFilter
{
    /// <summary>open | mine | unassigned | breach | closed | all</summary>
    public string? Scope { get; set; } = "open";
    public TicketPriority? Priority { get; set; }
    public string? Category { get; set; }
    public int? CustomerId { get; set; }
}
