using System.ComponentModel.DataAnnotations;

namespace CRM.Web.Models.Entities;

// Quản lý công việc (dựa trên bảng Hoạt động: Activity) + thông báo trong ứng dụng

/// <summary>Đầu việc (checklist) của một công việc</summary>
public class TaskChecklistItem
{
    public int Id { get; set; }
    public int ActivityId { get; set; }
    public Activity? Activity { get; set; }

    [Required, StringLength(300)]
    public string Title { get; set; } = string.Empty;
    public bool IsDone { get; set; }
    public DateTime? DoneAt { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Bình luận / nhật ký trên công việc</summary>
public class TaskComment
{
    public int Id { get; set; }
    public int ActivityId { get; set; }
    public Activity? Activity { get; set; }

    [Required, StringLength(2000)]
    public string Body { get; set; } = string.Empty;
    /// <summary>Do hệ thống ghi (đổi trạng thái, giao việc...)</summary>
    public bool IsSystem { get; set; }
    public int? AuthorId { get; set; }
    public AppUser? Author { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>Thông báo gửi tới một người dùng</summary>
public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>Loại: task, task-done, task-comment, due, overdue, ticket...</summary>
    [Required, StringLength(30)]
    public string Type { get; set; } = "info";

    [Required, StringLength(300)]
    public string Title { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Body { get; set; }

    [StringLength(300)]
    public string? Url { get; set; }

    /// <summary>Khóa chống gửi trùng (vd. nhắc hạn cho cùng một việc + cùng hạn)</summary>
    [StringLength(150)]
    public string? Key { get; set; }

    public int? ActorId { get; set; }
    public AppUser? Actor { get; set; }

    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
