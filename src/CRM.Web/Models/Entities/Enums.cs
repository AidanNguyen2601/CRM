using System.ComponentModel.DataAnnotations;

namespace CRM.Web.Models.Entities;

public enum CustomerType
{
    [Display(Name = "Tiềm năng")] Prospect = 1,
    [Display(Name = "Khách hàng")] Customer = 2,
    [Display(Name = "Đối tác")] Partner = 3,
    [Display(Name = "Ngừng giao dịch")] Inactive = 4
}

public enum LeadSource
{
    [Display(Name = "Website")] Website = 1,
    [Display(Name = "Giới thiệu")] Referral = 2,
    [Display(Name = "Sự kiện / Hội thảo")] Event = 3,
    [Display(Name = "Gọi điện chủ động")] ColdCall = 4,
    [Display(Name = "Mạng xã hội")] Social = 5,
    [Display(Name = "Email marketing")] EmailCampaign = 6,
    [Display(Name = "Khác")] Other = 9
}

public enum LeadStatus
{
    [Display(Name = "Mới")] New = 1,
    [Display(Name = "Đã liên hệ")] Contacted = 2,
    [Display(Name = "Đủ điều kiện")] Qualified = 3,
    [Display(Name = "Không phù hợp")] Unqualified = 4,
    [Display(Name = "Đã chuyển đổi")] Converted = 5
}

public enum LeadRating
{
    [Display(Name = "Nóng")] Hot = 1,
    [Display(Name = "Ấm")] Warm = 2,
    [Display(Name = "Lạnh")] Cold = 3
}

/// <summary>Giai đoạn cơ hội bán hàng. Giá trị số = xác suất thắng mặc định (%).</summary>
public enum OpportunityStage
{
    [Display(Name = "Tìm hiểu nhu cầu")] Prospecting = 10,
    [Display(Name = "Đánh giá")] Qualification = 25,
    [Display(Name = "Gửi đề xuất / báo giá")] Proposal = 50,
    [Display(Name = "Đàm phán")] Negotiation = 75,
    [Display(Name = "Thắng")] ClosedWon = 100,
    [Display(Name = "Thua")] ClosedLost = 0
}

public enum ActivityType
{
    [Display(Name = "Cuộc gọi")] Call = 1,
    [Display(Name = "Cuộc họp")] Meeting = 2,
    [Display(Name = "Email")] Email = 3,
    [Display(Name = "Công việc")] Task = 4
}

public enum ActivityStatus
{
    [Display(Name = "Kế hoạch")] Planned = 1,
    [Display(Name = "Hoàn thành")] Completed = 2,
    [Display(Name = "Đã hủy")] Cancelled = 3
}

public enum Priority
{
    [Display(Name = "Thấp")] Low = 1,
    [Display(Name = "Trung bình")] Normal = 2,
    [Display(Name = "Cao")] High = 3
}

public enum QuoteStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Đã gửi")] Sent = 2,
    [Display(Name = "Khách chấp nhận")] Accepted = 3,
    [Display(Name = "Khách từ chối")] Rejected = 4
}
