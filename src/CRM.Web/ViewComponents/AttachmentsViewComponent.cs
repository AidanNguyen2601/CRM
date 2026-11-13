using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;

namespace CRM.Web.ViewComponents;

public class AttachmentsModel
{
    public AttachmentTarget Type { get; set; }
    public int Id { get; set; }
    public List<Attachment> Items { get; set; } = new();
    public bool CanEdit { get; set; }
    public int CurrentUserId { get; set; }
    public bool SeesAll { get; set; }
    public string ReturnUrl { get; set; } = "/";
    public string? Hint { get; set; }
    public long MaxFileSize { get; set; }
}

/// <summary>Khối "File đính kèm" dùng chung: @await Component.InvokeAsync("Attachments", new { type = AttachmentTarget.Contract, id = Model.Id })</summary>
public class AttachmentsViewComponent : ViewComponent
{
    private readonly AttachmentService _files;
    public AttachmentsViewComponent(AttachmentService files) => _files = files;

    public async Task<IViewComponentResult> InvokeAsync(AttachmentTarget type, int id, string? hint = null)
    {
        var (exists, canView, canEdit) = await _files.CheckAsync(type, id, UserClaimsPrincipal);
        if (!exists || !canView) return Content(string.Empty);
        return View(new AttachmentsModel
        {
            Type = type, Id = id, Items = await _files.ListAsync(type, id), CanEdit = canEdit,
            CurrentUserId = UserClaimsPrincipal.GetUserId(), SeesAll = UserClaimsPrincipal.SeesAll(),
            ReturnUrl = HttpContext.Request.Path + HttpContext.Request.QueryString, Hint = hint, MaxFileSize = _files.MaxFileSize
        });
    }
}
