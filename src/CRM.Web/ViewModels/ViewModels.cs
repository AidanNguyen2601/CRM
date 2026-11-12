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

public class CustomerDetailViewModel
{
    public Customer Customer { get; set; } = null!;
    public List<Contact> Contacts { get; set; } = new();
    public List<Opportunity> Opportunities { get; set; } = new();
    public List<Quote> Quotes { get; set; } = new();
    public List<Activity> Activities { get; set; } = new();
    public decimal WonTotal => Opportunities.Where(o => o.Stage == OpportunityStage.ClosedWon).Sum(o => o.Amount);
    public decimal OpenTotal => Opportunities.Where(o => !o.IsClosed).Sum(o => o.Amount);
}

public class OpportunityDetailViewModel
{
    public Opportunity Opportunity { get; set; } = null!;
    public List<Activity> Activities { get; set; } = new();
    public List<Quote> Quotes { get; set; } = new();
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
