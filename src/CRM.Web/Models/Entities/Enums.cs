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

/// <summary>Nhóm dự báo doanh số của cơ hội đang mở</summary>
public enum ForecastCategory
{
    [Display(Name = "Chắc chắn")] Commit = 1,
    [Display(Name = "Có thể")] BestCase = 2,
    [Display(Name = "Pipeline")] Pipeline = 3,
    [Display(Name = "Loại khỏi dự báo")] Omitted = 9
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

/// <summary>Các nhóm danh mục dùng chung (bảng CatalogItems)</summary>
public enum CatalogGroup
{
    [Display(Name = "Ngành nghề")] Industry = 1,
    [Display(Name = "Tỉnh / Thành phố")] City = 2,
    [Display(Name = "Nhóm sản phẩm")] ProductCategory = 3,
    [Display(Name = "Đơn vị tính")] Unit = 4,
    [Display(Name = "Lý do thua")] LossReason = 5,
    [Display(Name = "Loại chi phí")] CostType = 6,
    [Display(Name = "Hãng sản xuất")] Brand = 7,
    [Display(Name = "Xuất xứ")] Origin = 8,
    [Display(Name = "Phương thức thanh toán")] PaymentMethod = 9,
    [Display(Name = "Loại yêu cầu hỗ trợ")] TicketCategory = 10
}

/// <summary>Trạng thái phương án kinh doanh (quy trình duyệt)</summary>
public enum BusinessPlanStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Chờ duyệt")] Pending = 2,
    [Display(Name = "Đã duyệt")] Approved = 3,
    [Display(Name = "Bị từ chối")] Rejected = 4
}

public enum ProductType
{
    [Display(Name = "Hàng hoá")] Goods = 1,
    [Display(Name = "Dịch vụ")] Service = 2
}

public enum ContractStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Đang thực hiện")] Active = 2,
    [Display(Name = "Hoàn thành")] Completed = 3,
    [Display(Name = "Đã thanh lý")] Liquidated = 4,
    [Display(Name = "Đã hủy")] Cancelled = 5
}

public enum InvoiceStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Đã phát hành")] Issued = 2,
    [Display(Name = "Thanh toán một phần")] PartiallyPaid = 3,
    [Display(Name = "Đã thanh toán")] Paid = 4,
    [Display(Name = "Đã hủy")] Cancelled = 5
}

/// <summary>Loại bản ghi được đính kèm file</summary>
public enum AttachmentTarget
{
    Customer = 1,
    Lead = 2,
    Opportunity = 3,
    Quote = 4,
    BusinessPlan = 5,
    Contract = 6,
    Invoice = 7,
    Payment = 8,
    Activity = 9,
    Product = 10,
    Campaign = 11,
    Ticket = 12,
    /// <summary>File Excel đính kèm email báo cáo gửi định kỳ (chỉ quản trị tải được)</summary>
    ReportSchedule = 30
}
