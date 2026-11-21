using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Nhãn (gắn / gỡ trên bản ghi; quản lý danh sách nhãn) và trường tùy biến (quản lý định nghĩa)</summary>
public class TagsController : CrmControllerBase
{
    public TagsController(CrmDbContext db) : base(db) { }

    public static readonly string[] Colors = { "#64748b", "#0f9d8a", "#2563eb", "#7c3aed", "#db2777", "#dc2626", "#ea580c", "#ca8a04", "#16a34a", "#0891b2" };

    /// <summary>Được chia sẻ quyền sửa bản ghi</summary>
    private Task<bool> SharedEditAsync(string type, int id) { var me = CurrentUserId; return Db.RecordShares.AnyAsync(s => s.EntityType == type && s.EntityId == id && s.UserId == me && s.CanEdit); }

    private string Back(string url) => Request.Headers.Referer.ToString() is { Length: > 0 } r && Uri.TryCreate(r, UriKind.Absolute, out var u) && u.Host == Request.Host.Host ? u.PathAndQuery : url;

    /// <summary>Gắn nhãn (chọn có sẵn hoặc gõ tên mới) cho bản ghi</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(string type, int id, string? name)
    {
        var (item, _, url, canManage) = await SharesController.ResolveAsync(Db, User, type, id);
        if (item == null) return NotFound();
        if (!canManage && !await SharedEditAsync(type, id)) return NotAllowed();
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 50) return Redirect(Back(url));
        var tag = await Db.Tags.FirstOrDefaultAsync(t => t.Name == name);
        if (tag == null)
        {
            tag = new Tag { Name = name, Color = Colors[Math.Abs(name.GetHashCode()) % Colors.Length] };
            Db.Tags.Add(tag);
            await Db.SaveChangesAsync();
        }
        if (!await Db.EntityTags.AnyAsync(t => t.TagId == tag.Id && t.EntityType == type && t.EntityId == id))
        {
            Db.EntityTags.Add(new EntityTag { TagId = tag.Id, EntityType = type, EntityId = id });
            await Db.SaveChangesAsync();
        }
        return Redirect(Back(url));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(string type, int id, int tagId)
    {
        var (item, _, url, canManage) = await SharesController.ResolveAsync(Db, User, type, id);
        if (item == null) return NotFound();
        if (!canManage && !await SharedEditAsync(type, id)) return NotAllowed();
        var et = await Db.EntityTags.FirstOrDefaultAsync(t => t.TagId == tagId && t.EntityType == type && t.EntityId == id);
        if (et != null) { Db.EntityTags.Remove(et); await Db.SaveChangesAsync(); }
        return Redirect(Back(url));
    }

    // ======================= Quản lý nhãn + trường tùy biến (quản lý / quản trị) =======================

    [Authorize(Roles = AppRoles.ManagerGroup)]
    public async Task<IActionResult> Index(string tab = "tags")
    {
        ViewBag.Tab = tab == "fields" ? "fields" : "tags";
        ViewBag.Usage = await Db.EntityTags.GroupBy(t => t.TagId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        ViewBag.Fields = await Db.CustomFieldDefs.AsNoTracking().OrderBy(f => f.EntityType).ThenBy(f => f.SortOrder).ThenBy(f => f.Id).ToListAsync();
        return View(await Db.Tags.AsNoTracking().OrderBy(t => t.Name).ToListAsync());
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTag(int id, string name, string color)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 50 || await Db.Tags.AnyAsync(t => t.Name == name && t.Id != id))
        {
            TempData["Error"] = I18n.T("Tên nhãn trống, quá dài hoặc đã tồn tại.");
            return RedirectToAction(nameof(Index));
        }
        var t = id == 0 ? null : await Db.Tags.FindAsync(id);
        if (t == null) { t = new Tag(); Db.Tags.Add(t); }
        t.Name = name;
        t.Color = System.Text.RegularExpressions.Regex.IsMatch(color ?? "", "^#[0-9a-fA-F]{6}$") ? color! : Colors[0];
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu nhãn \"{0}\".", t.Name);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTag(int id)
    {
        var t = await Db.Tags.FindAsync(id);
        if (t == null) return NotFound();
        Db.EntityTags.RemoveRange(await Db.EntityTags.Where(x => x.TagId == id).ToListAsync());
        Db.Tags.Remove(t);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa nhãn \"{0}\".", t.Name);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    public async Task<IActionResult> Field(int? id, string? type)
    {
        var f = id.HasValue ? await Db.CustomFieldDefs.FindAsync(id.Value) : new CustomFieldDef { EntityType = CustomData.Types.Contains(type) ? type! : nameof(Customer) };
        if (f == null) return NotFound();
        return View(f);
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Field(CustomFieldDef model)
    {
        if (!CustomData.Types.Contains(model.EntityType)) ModelState.AddModelError(nameof(model.EntityType), I18n.T("Loại dữ liệu không hợp lệ."));
        if (model.Type == CustomFieldType.Select && model.OptionList.Length == 0) ModelState.AddModelError(nameof(model.Options), I18n.T("Nhập ít nhất một lựa chọn (mỗi dòng một giá trị)."));
        if (!ModelState.IsValid) return View(model);
        CustomFieldDef f;
        if (model.Id == 0) { f = new CustomFieldDef { EntityType = model.EntityType }; Db.CustomFieldDefs.Add(f); }
        else f = await Db.CustomFieldDefs.FindAsync(model.Id) ?? throw new InvalidOperationException();
        f.Label = model.Label.Trim(); f.Type = model.Type; f.Options = model.Type == CustomFieldType.Select ? string.Join("\n", model.OptionList) : null;
        f.Required = model.Required; f.ShowInList = model.ShowInList; f.SortOrder = model.SortOrder; f.IsActive = model.IsActive;
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu trường \"{0}\".", f.Label);
        return RedirectToAction(nameof(Index), new { tab = "fields" });
    }

    [Authorize(Roles = AppRoles.ManagerGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteField(int id)
    {
        var f = await Db.CustomFieldDefs.FindAsync(id);
        if (f == null) return NotFound();
        Db.CustomFieldValues.RemoveRange(await Db.CustomFieldValues.Where(v => v.FieldId == id).ToListAsync());
        Db.CustomFieldDefs.Remove(f);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa trường \"{0}\" và dữ liệu đã nhập.", f.Label);
        return RedirectToAction(nameof(Index), new { tab = "fields" });
    }
}
