using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CRM.Web.Models.Entities;

/// <summary>Các chỉ tiêu KPI bán hàng</summary>
public enum KpiMetric
{
    [Display(Name = "Doanh số")] Revenue = 1,
    [Display(Name = "Thu tiền")] Collection = 2,
    [Display(Name = "Hợp đồng ký mới")] Contracts = 3,
    [Display(Name = "Khách hàng mới")] NewCustomers = 4,
    [Display(Name = "Hoạt động hoàn thành")] Activities = 5
}

/// <summary>Chỉ tiêu KPI theo tháng của một nhân viên. Quý / năm = tổng các tháng.</summary>
public class SalesTarget
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public AppUser? User { get; set; }

    public int Year { get; set; }

    [Range(1, 12)]
    public int Month { get; set; }

    [Column(TypeName = "decimal(18,0)")] public decimal Revenue { get; set; }
    [Column(TypeName = "decimal(18,0)")] public decimal Collection { get; set; }
    public int Contracts { get; set; }
    public int NewCustomers { get; set; }
    public int Activities { get; set; }

    public int? UpdatedById { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public decimal Get(KpiMetric m) => m switch
    {
        KpiMetric.Revenue => Revenue,
        KpiMetric.Collection => Collection,
        KpiMetric.Contracts => Contracts,
        KpiMetric.NewCustomers => NewCustomers,
        _ => Activities
    };

    public void Set(KpiMetric m, decimal v)
    {
        switch (m)
        {
            case KpiMetric.Revenue: Revenue = Math.Round(v, 0); break;
            case KpiMetric.Collection: Collection = Math.Round(v, 0); break;
            case KpiMetric.Contracts: Contracts = (int)Math.Round(v); break;
            case KpiMetric.NewCustomers: NewCustomers = (int)Math.Round(v); break;
            default: Activities = (int)Math.Round(v); break;
        }
    }

    /// <summary>Chỉ tiêu tính bằng tiền (còn lại là số lượng)</summary>
    public static bool IsMoney(KpiMetric m) => m is KpiMetric.Revenue or KpiMetric.Collection;
}
