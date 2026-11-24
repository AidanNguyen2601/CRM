using System.ComponentModel.DataAnnotations;

namespace CRM.Web.Models.Entities;

public enum LoyaltyTxType
{
    [Display(Name = "Tích điểm")] Earn = 1,
    [Display(Name = "Đổi điểm")] Redeem = 2,
    [Display(Name = "Điều chỉnh")] Adjust = 3,
    [Display(Name = "Hết hạn")] Expire = 4,
    [Display(Name = "Thu hồi")] Reverse = 5
}

/// <summary>
/// Sổ điểm thưởng của khách hàng. Điểm cộng (Earn / Adjust dương) là một "lô" có hạn dùng và số điểm còn lại (Remaining);
/// đổi điểm và hết hạn trừ dần theo lô cũ nhất trước (FIFO). Số dư khách hàng = tổng Remaining của các lô.
/// </summary>
public class LoyaltyTransaction
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public LoyaltyTxType Type { get; set; }

    /// <summary>Số điểm có dấu: cộng (+) khi tích / điều chỉnh tăng, trừ (-) khi đổi / hết hạn / thu hồi</summary>
    public int Points { get; set; }
    /// <summary>Số điểm còn dùng được của lô (chỉ lô cộng điểm)</summary>
    public int Remaining { get; set; }
    public DateTime? ExpiresAt { get; set; }
    /// <summary>Lô đã bị thu hồi (phiếu thu bị xóa) – không tính vào hạng thành viên</summary>
    public bool Reversed { get; set; }

    /// <summary>Phiếu thu sinh ra điểm (không ràng buộc khóa ngoại để vẫn giữ lịch sử khi xóa phiếu)</summary>
    public int? PaymentId { get; set; }

    [StringLength(500)] public string? Description { get; set; }
    public int? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
