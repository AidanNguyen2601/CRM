using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace CRM.Web.Controllers;

/// <summary>Tải lên / xem / tải về / xóa file đính kèm. Quyền kế thừa từ bản ghi gốc.</summary>
public class AttachmentsController : CrmControllerBase
{
    private readonly AttachmentService _files;
    public AttachmentsController(CrmDbContext db, AttachmentService files) : base(db) => _files = files;

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(220L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 220L * 1024 * 1024)]
    public async Task<IActionResult> Upload(AttachmentTarget type, int id, List<IFormFile> files, string? note, string? returnUrl)
    {
        var back = Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action("Index", "Home")!;
        var (exists, _, canEdit) = await _files.CheckAsync(type, id, User);
        if (!exists) return NotFound();
        if (!canEdit) return NotAllowed();

        files = files.Where(f => f != null && f.Length > 0).ToList();
        if (files.Count == 0) { TempData["Error"] = I18n.T("Chưa chọn file."); return LocalRedirect(back + "#attachments"); }
        if (files.Count > AttachmentService.MaxFilesPerUpload)
        {
            TempData["Error"] = I18n.T("Mỗi lần tải lên tối đa {0} file.", AttachmentService.MaxFilesPerUpload);
            return LocalRedirect(back + "#attachments");
        }
        var errors = files.Select(_files.Validate).Where(e => e != null).ToList();
        if (errors.Count > 0) { TempData["Error"] = string.Join(" ", errors); return LocalRedirect(back + "#attachments"); }

        foreach (var f in files) await _files.SaveAsync(type, id, f, note, CurrentUserId);
        TempData["Success"] = I18n.T("Đã đính kèm {0} file.", files.Count);
        return LocalRedirect(back + "#attachments");
    }

    /// <summary>Xem trực tiếp (PDF, ảnh, txt) hoặc tải về (các định dạng khác / ?download=true)</summary>
    [HttpGet]
    public async Task<IActionResult> Get(int id, bool download = false)
    {
        var a = await Db.Attachments.FindAsync(id);
        if (a == null) return NotFound();
        var (exists, canView, _) = await _files.CheckAsync(a.TargetType, a.TargetId, User);
        if (!exists || !canView) return NotAllowed();
        var stream = _files.OpenRead(a);
        if (stream == null)
        {
            TempData["Error"] = I18n.T("Không tìm thấy nội dung file {0} trên máy chủ.", a.FileName);
            var back = Uri.TryCreate(Request.Headers.Referer.ToString(), UriKind.Absolute, out var u) ? u.PathAndQuery : "/";
            return LocalRedirect(Url.IsLocalUrl(back) ? back : "/");
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!download && AttachmentService.CanPreview(a.FileName))
        {
            var cd = new ContentDispositionHeaderValue("inline");
            cd.SetHttpFileName(a.FileName);
            Response.Headers[HeaderNames.ContentDisposition] = cd.ToString();
            return File(stream, a.ContentType);
        }
        return File(stream, a.ContentType, a.FileName);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, string? returnUrl)
    {
        var back = Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action("Index", "Home")!;
        var a = await Db.Attachments.FindAsync(id);
        if (a == null) return NotFound();
        var (exists, _, canEdit) = await _files.CheckAsync(a.TargetType, a.TargetId, User);
        // Người tải lên hoặc quản lý mới được xóa
        if (!exists || !canEdit || (a.UploadedById != CurrentUserId && !User.SeesAll())) return NotAllowed();
        await _files.DeleteAsync(a);
        TempData["Success"] = I18n.T("Đã xóa file {0}.", a.FileName);
        return LocalRedirect(back + "#attachments");
    }
}
