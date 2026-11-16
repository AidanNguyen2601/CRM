using CRM.Web.Models.Entities;

namespace CRM.Web.ViewModels;

/// <summary>Toàn bộ dữ liệu trang Khách hàng 360°</summary>
public class Customer360ViewModel
{
    public Customer Customer { get; set; } = null!;

    // ---- Quan hệ ----
    public List<Contact> Contacts { get; set; } = new();
    public List<Lead> SourceLeads { get; set; } = new();
    public List<CampaignMember> Campaigns { get; set; } = new();

    // ---- Bán hàng ----
    public List<Opportunity> Opportunities { get; set; } = new();
    public List<Quote> Quotes { get; set; } = new();
    public List<BusinessPlan> Plans { get; set; } = new();

    // ---- Tài chính ----
    public List<Contract> Contracts { get; set; } = new();
    public List<Invoice> Invoices { get; set; } = new();
    public List<Payment> Payments { get; set; } = new();
    public List<MilestoneDue> UpcomingMilestones { get; set; } = new();
    public List<MonthlyFinance> Monthly { get; set; } = new();
    public List<PurchasedProduct> Products { get; set; } = new();

    // ---- Chăm sóc ----
    public List<Activity> Activities { get; set; } = new();
    public List<Activity> Upcoming { get; set; } = new();
    public List<TimelineEvent> Timeline { get; set; } = new();

    // ---- Chỉ số ----
    public decimal Invoiced { get; set; }
    public decimal Invoiced12m { get; set; }
    public decimal Paid { get; set; }
    public decimal Receivable { get; set; }
    public decimal Overdue { get; set; }
    public int OverdueInvoices { get; set; }
    public decimal OpenPipeline { get; set; }
    public decimal WeightedPipeline { get; set; }
    public int OpenOpportunities { get; set; }
    public decimal WonTotal { get; set; }
    public int WinRate { get; set; }
    public int ClosedCount { get; set; }
    public decimal ContractValue { get; set; }
    public int ActiveContracts { get; set; }
    public DateTime? LastInteraction { get; set; }
    public int? DaysSinceInteraction { get; set; }
    public DateTime CustomerSince { get; set; }
    public decimal AvgPaymentDays { get; set; }

    public HealthResult Health { get; set; } = new();
    public List<Customer360Alert> Alerts { get; set; } = new();
}

public record MonthlyFinance(string Month, decimal Invoiced, decimal Collected);

public record PurchasedProduct(int? ProductId, string? Code, string Name, string? Unit,
    decimal Quantity, decimal Amount, int Times, DateTime LastDate);

public record MilestoneDue(int ContractId, string ContractNumber, string Name, decimal Amount, DateTime? DueDate);

/// <summary>Một sự kiện trên dòng thời gian hợp nhất</summary>
public record TimelineEvent(DateTime When, string Kind, string Icon, string Title, string? Detail,
    string? Controller, string? Action, int? Id, decimal? Amount, string? Css = null);

/// <summary>Cảnh báo cần chú ý. Level: danger / warning / info</summary>
public record Customer360Alert(string Level, string Icon, string Text, string? Tab = null);

public record HealthFactor(string Name, int Score, int Max, string Note);

public class HealthResult
{
    public int Score { get; set; }
    /// <summary>good / warn / risk</summary>
    public string Level { get; set; } = "good";
    public List<HealthFactor> Factors { get; set; } = new();
}
