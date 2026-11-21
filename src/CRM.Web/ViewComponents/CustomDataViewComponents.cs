using CRM.Web.Controllers;
using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.ViewComponents;

public class TagsModel
{
    public string Type { get; set; } = "";
    public int Id { get; set; }
    public bool CanEdit { get; set; }
    public List<Tag> Items { get; set; } = new();
    public List<string> AllNames { get; set; } = new();
}

/// <summary>Nhãn của bản ghi: @await Component.InvokeAsync("Tags", new { type = "Customer", id = c.Id })</summary>
public class TagsViewComponent : ViewComponent
{
    private readonly CrmDbContext _db;
    public TagsViewComponent(CrmDbContext db) => _db = db;

    public async Task<IViewComponentResult> InvokeAsync(string type, int id)
    {
        var (item, _, _, canManage) = await SharesController.ResolveAsync(_db, UserClaimsPrincipal, type, id);
        if (item == null) return Content(string.Empty);
        var me = UserClaimsPrincipal.GetUserId();
        var canEdit = canManage || await _db.RecordShares.AnyAsync(s => s.EntityType == type && s.EntityId == id && s.UserId == me && s.CanEdit);
        return View(new TagsModel
        {
            Type = type, Id = id, CanEdit = canEdit,
            Items = (await CustomData.TagsOfAsync(_db, type, new[] { id })).GetValueOrDefault(id) ?? new(),
            AllNames = canEdit ? await _db.Tags.OrderBy(t => t.Name).Select(t => t.Name).ToListAsync() : new()
        });
    }
}

public class CustomFieldsModel
{
    public bool Edit { get; set; }
    public List<CustomFieldDef> Fields { get; set; } = new();
    public Dictionary<int, string?> Values { get; set; } = new();
}

/// <summary>
/// Trường tùy biến: mode "edit" (ô nhập trên form – giữ giá trị vừa gửi khi form báo lỗi) hoặc "view" (hiển thị trên trang chi tiết).
/// @await Component.InvokeAsync("CustomFields", new { type = "Customer", id = Model.Id, edit = true })
/// </summary>
public class CustomFieldsViewComponent : ViewComponent
{
    private readonly CrmDbContext _db;
    public CustomFieldsViewComponent(CrmDbContext db) => _db = db;

    public async Task<IViewComponentResult> InvokeAsync(string type, int id, bool edit = false)
    {
        var fields = await CustomData.FieldsAsync(_db, type);
        if (fields.Count == 0) return Content(string.Empty);
        Dictionary<int, string?> values;
        if (edit && HttpContext.Request.HasFormContentType && HttpContext.Request.Method == "POST")
            values = fields.ToDictionary(f => f.Id, f => (string?)HttpContext.Request.Form[CustomData.InputName(f)].ToString());
        else
            values = id == 0 ? new() : (await CustomData.ValuesAsync(_db, type, new[] { id })).GetValueOrDefault(id) ?? new();
        if (!edit && values.Values.All(string.IsNullOrEmpty)) return Content(string.Empty);
        return View(new CustomFieldsModel { Edit = edit, Fields = fields, Values = values });
    }
}
