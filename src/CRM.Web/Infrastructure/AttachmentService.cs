using System.Security.Claims;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>
/// Quản lý file đính kèm: lưu trên đĩa (Attachments:Path, mặc định App_Data/uploads – ngoài wwwroot),
/// tên file lưu trữ là GUID; quyền xem/sửa kế thừa từ bản ghi gốc.
/// </summary>
public class AttachmentService
{
    /// <summary>Định dạng cho phép tải lên</summary>
    public static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".txt", ".csv", ".xml",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".zip", ".rar", ".7z", ".msg", ".eml"
    };

    /// <summary>Định dạng được xem trực tiếp trên trình duyệt (còn lại luôn tải về)</summary>
    private static readonly HashSet<string> InlineExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".txt" };

    public const int MaxFilesPerUpload = 10;

    private readonly CrmDbContext _db;
    private readonly string _root;
    public long MaxFileSize { get; }

    public AttachmentService(CrmDbContext db, IConfiguration config, IWebHostEnvironment env)
    {
        _db = db;
        var path = config["Attachments:Path"];
        _root = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? Path.Combine(env.ContentRootPath, "App_Data", "uploads")
            : Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path));
        MaxFileSize = config.GetValue("Attachments:MaxFileSizeMB", 20) * 1024L * 1024L;
    }

    public static bool CanPreview(string fileName) => InlineExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>Kiểm tra bản ghi gốc tồn tại và quyền xem / thêm-xóa file của người dùng</summary>
    public async Task<(bool Exists, bool CanView, bool CanEdit)> CheckAsync(AttachmentTarget type, int id, ClaimsPrincipal user)
    {
        IOwned? owned = type switch
        {
            AttachmentTarget.Customer => await _db.Customers.FindAsync(id),
            AttachmentTarget.Lead => await _db.Leads.FindAsync(id),
            AttachmentTarget.Opportunity => await _db.Opportunities.FindAsync(id),
            AttachmentTarget.Quote => await _db.Quotes.FindAsync(id),
            AttachmentTarget.BusinessPlan => await _db.BusinessPlans.FindAsync(id),
            AttachmentTarget.Contract => await _db.Contracts.FindAsync(id),
            AttachmentTarget.Invoice => await _db.Invoices.FindAsync(id),
            AttachmentTarget.Payment => await _db.Payments.FindAsync(id),
            AttachmentTarget.Activity => await _db.Activities.FindAsync(id),
            _ => null
        };
        if (type == AttachmentTarget.Product)
        {
            var exists = await _db.Products.AnyAsync(p => p.Id == id);
            return (exists, exists, exists && user.SeesAll());   // hàng hoá: ai cũng xem, quản lý mới thêm/xóa
        }
        if (owned == null) return (false, false, false);
        var can = user.CanAccess(owned);
        return (true, can, can);
    }

    public Task<List<Attachment>> ListAsync(AttachmentTarget type, int id) =>
        _db.Attachments.Include(a => a.UploadedBy).Where(a => a.TargetType == type && a.TargetId == id)
            .OrderByDescending(a => a.UploadedAt).ThenByDescending(a => a.Id).AsNoTracking().ToListAsync();

    /// <summary>Kiểm tra file hợp lệ; trả về thông báo lỗi (null nếu hợp lệ)</summary>
    public string? Validate(IFormFile file)
    {
        if (file.Length == 0) return I18n.T("File {0} rỗng.", file.FileName);
        if (file.Length > MaxFileSize) return I18n.T("File {0} vượt quá dung lượng cho phép ({1} MB).", file.FileName, MaxFileSize / 1024 / 1024);
        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext)) return I18n.T("Định dạng file {0} không được phép.", file.FileName);
        return null;
    }

    public async Task<Attachment> SaveAsync(AttachmentTarget type, int id, IFormFile file, string? note, int userId)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var relative = Path.Combine(DateTime.Today.ToString("yyyy"), DateTime.Today.ToString("MM"), $"{Guid.NewGuid():N}{ext}");
        var full = FullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using (var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write))
            await file.CopyToAsync(fs);

        // Loại nội dung xác định theo phần mở rộng (không tin giá trị trình duyệt gửi lên)
        if (!new FileExtensionContentTypeProvider().TryGetContentType(file.FileName, out var contentType)) contentType = "application/octet-stream";
        var name = Path.GetFileName(file.FileName);
        if (name.Length > 255) name = name[..(255 - ext.Length)] + ext;
        var a = new Attachment
        {
            TargetType = type, TargetId = id, FileName = name, ContentType = contentType, Size = file.Length,
            StoragePath = relative.Replace('\\', '/'), Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            UploadedById = userId, UploadedAt = DateTime.Now
        };
        _db.Attachments.Add(a);
        await _db.SaveChangesAsync();
        return a;
    }

    public FileStream? OpenRead(Attachment a)
    {
        var full = FullPath(a.StoragePath);
        return File.Exists(full) ? new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    public async Task DeleteAsync(Attachment a)
    {
        _db.Attachments.Remove(a);
        await _db.SaveChangesAsync();
        TryDeleteFile(a.StoragePath);
    }

    /// <summary>Xóa toàn bộ file của bản ghi (gọi khi xóa bản ghi gốc)</summary>
    public async Task DeleteAllAsync(AttachmentTarget type, int id)
    {
        var list = await _db.Attachments.Where(a => a.TargetType == type && a.TargetId == id).ToListAsync();
        if (list.Count == 0) return;
        _db.Attachments.RemoveRange(list);
        await _db.SaveChangesAsync();
        foreach (var a in list) TryDeleteFile(a.StoragePath);
    }

    private void TryDeleteFile(string relative)
    {
        try { var f = FullPath(relative); if (File.Exists(f)) File.Delete(f); } catch (IOException) { /* bỏ qua: file đang bị khóa */ }
    }

    /// <summary>Ghép đường dẫn và đảm bảo không thoát ra ngoài thư mục lưu trữ</summary>
    private string FullPath(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Đường dẫn file không hợp lệ.");
        return full;
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.#} MB" : bytes >= 1024 ? $"{bytes / 1024d:0} KB" : $"{bytes} B";

    public static string IconFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "file-pdf",
        ".doc" or ".docx" or ".odt" or ".txt" => "file-doc",
        ".xls" or ".xlsx" or ".ods" or ".csv" => "file-sheet",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" => "file-image",
        ".zip" or ".rar" or ".7z" => "file-zip",
        _ => "file"
    };
}
