using System.ComponentModel.DataAnnotations;

namespace CRM.Web.Models.Entities;

// Chăm sóc khách hàng nâng cao: kho kiến thức, email tạo phiếu, khảo sát NPS.

/// <summary>Bài viết kho kiến thức / mẫu trả lời cho bộ phận chăm sóc khách hàng</summary>
public class KbArticle
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập tiêu đề"), StringLength(200)]
    [Display(Name = "Tiêu đề")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Nhóm (dùng danh mục "Loại yêu cầu hỗ trợ")</summary>
    [StringLength(100)][Display(Name = "Nhóm")] public string? Category { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập nội dung")]
    [Display(Name = "Nội dung")]
    public string Body { get; set; } = string.Empty;

    /// <summary>Từ khóa để gợi ý bài viết theo tiêu đề phiếu hỗ trợ</summary>
    [StringLength(300)][Display(Name = "Từ khóa")] public string? Keywords { get; set; }

    /// <summary>Hiện cho khách hàng trên cổng khách hàng</summary>
    [Display(Name = "Công khai cho khách hàng")] public bool IsPublic { get; set; }
    /// <summary>Mẫu trả lời nhanh (chèn vào ô trao đổi trên phiếu hỗ trợ)</summary>
    [Display(Name = "Dùng làm mẫu trả lời")] public bool IsTemplate { get; set; }
    [Display(Name = "Đang dùng")] public bool IsActive { get; set; } = true;

    public int Views { get; set; }
    public int Helpful { get; set; }
    public int NotHelpful { get; set; }

    public int? AuthorId { get; set; }
    public AppUser? Author { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>Email đã nhận vào hộp thư hỗ trợ (chống xử lý trùng)</summary>
public class InboundMessage
{
    public int Id { get; set; }
    /// <summary>Message-ID của email (hoặc UIDL của POP3)</summary>
    [Required, StringLength(300)] public string MessageKey { get; set; } = string.Empty;
    [StringLength(200)] public string? From { get; set; }
    [StringLength(300)] public string? Subject { get; set; }
    public int? TicketId { get; set; }
    /// <summary>created / replied / ignored</summary>
    [StringLength(20)] public string? Result { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.Now;
}

public enum NpsStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Đã gửi")] Sent = 2,
    [Display(Name = "Đã đóng")] Closed = 3
}

/// <summary>Đợt khảo sát NPS ("Bạn sẵn sàng giới thiệu chúng tôi tới bạn bè / đối tác ở mức nào, 0–10?")</summary>
public class NpsSurvey
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Vui lòng nhập tên đợt khảo sát"), StringLength(150)]
    [Display(Name = "Tên đợt khảo sát")]
    public string Name { get; set; } = string.Empty;
    [StringLength(300)]
    [Display(Name = "Câu hỏi")]
    public string Question { get; set; } = "Anh/Chị sẵn sàng giới thiệu chúng tôi tới bạn bè, đối tác ở mức nào?";
    public NpsStatus Status { get; set; } = NpsStatus.Draft;
    public int? CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? SentAt { get; set; }
    public ICollection<NpsResponse> Responses { get; set; } = new List<NpsResponse>();
}

/// <summary>Một người nhận khảo sát NPS (gửi qua email, trả lời qua liên kết riêng không cần đăng nhập)</summary>
public class NpsResponse
{
    public int Id { get; set; }
    public int SurveyId { get; set; }
    public NpsSurvey? Survey { get; set; }
    public int? ContactId { get; set; }
    public Contact? Contact { get; set; }
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    [Required, StringLength(150)] public string Email { get; set; } = string.Empty;
    [Required, StringLength(64)] public string Token { get; set; } = string.Empty;
    [Range(0, 10)] public int? Score { get; set; }
    [StringLength(2000)] public string? Comment { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? RespondedAt { get; set; }

    /// <summary>9–10: ủng hộ; 7–8: trung lập; 0–6: phản đối</summary>
    public static string Group(int score) => score >= 9 ? "promoter" : score >= 7 ? "passive" : "detractor";
}
