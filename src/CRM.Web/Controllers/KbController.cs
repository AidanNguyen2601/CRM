using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Kho kiến thức: bài viết hướng dẫn / xử lý sự cố, mẫu trả lời nhanh cho CSKH, bài công khai trên cổng khách hàng</summary>
public class KbController : CrmControllerBase
{
    public KbController(CrmDbContext db) : base(db) { }

    private bool CanEdit => User.IsSupportDesk();

    public async Task<IActionResult> Index(string? q, string? category, bool templates = false)
    {
        var query = Db.KbArticles.AsNoTracking().Include(a => a.Author).AsQueryable();
        if (!CanEdit) query = query.Where(a => a.IsActive);
        if (templates) query = query.Where(a => a.IsTemplate);
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(a => a.Category == category);
        var list = await query.OrderByDescending(a => a.UpdatedAt).ToListAsync();
        if (!string.IsNullOrWhiteSpace(q)) list = KbSearch.Rank(list, q).ToList();
        ViewBag.Q = q; ViewBag.Category = category; ViewBag.Templates = templates; ViewBag.CanEdit = CanEdit;
        ViewBag.Categories = await Db.KbArticles.Where(a => a.Category != null).Select(a => a.Category!).Distinct().OrderBy(c => c).ToListAsync();
        return View(list);
    }

    public async Task<IActionResult> Details(int id)
    {
        var a = await Db.KbArticles.Include(x => x.Author).FirstOrDefaultAsync(x => x.Id == id);
        if (a == null || !a.IsActive && !CanEdit) return NotFound();
        a.Views++;
        Db.Entry(a).Property(x => x.Views).IsModified = true;
        var suppress = Db.SuppressAudit; Db.SuppressAudit = true; await Db.SaveChangesAsync(); Db.SuppressAudit = suppress;   // lượt xem không ghi nhật ký
        ViewBag.CanEdit = CanEdit;
        ViewBag.Related = KbSearch.Rank(await Db.KbArticles.AsNoTracking().Where(x => x.IsActive && x.Id != id).ToListAsync(), a.Title + " " + a.Keywords).Take(5).ToList();
        return View(a);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Vote(int id, bool helpful)
    {
        var a = await Db.KbArticles.FindAsync(id);
        if (a == null) return NotFound();
        if (helpful) a.Helpful++; else a.NotHelpful++;
        Db.SuppressAudit = true;
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Cảm ơn bạn đã đánh giá bài viết.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.SupportGroup)]
    public async Task<IActionResult> Edit(int? id)
    {
        var a = id.HasValue ? await Db.KbArticles.FindAsync(id.Value) : new KbArticle();
        if (a == null) return NotFound();
        return View(a);
    }

    [Authorize(Roles = AppRoles.SupportGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(KbArticle model)
    {
        ModelState.Remove(nameof(model.Author));
        if (!ModelState.IsValid) return View(model);
        KbArticle a;
        if (model.Id == 0) { a = new KbArticle { AuthorId = CurrentUserId, CreatedAt = DateTime.Now }; Db.KbArticles.Add(a); }
        else a = await Db.KbArticles.FindAsync(model.Id) ?? throw new InvalidOperationException();
        a.Title = model.Title.Trim(); a.Category = string.IsNullOrWhiteSpace(model.Category) ? null : model.Category; a.Body = model.Body.Trim();
        a.Keywords = model.Keywords?.Trim(); a.IsPublic = model.IsPublic; a.IsTemplate = model.IsTemplate; a.IsActive = model.IsActive; a.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu bài viết \"{0}\".", a.Title);
        return RedirectToAction(nameof(Details), new { id = a.Id });
    }

    [Authorize(Roles = AppRoles.SupportGroup)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var a = await Db.KbArticles.FindAsync(id);
        if (a == null) return NotFound();
        Db.KbArticles.Remove(a);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa bài viết \"{0}\".", a.Title);
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>Tìm / gợi ý bài viết theo từ khóa (không dấu, đếm số từ trùng ở tiêu đề – từ khóa – nội dung)</summary>
public static class KbSearch
{
    private static readonly HashSet<string> Stop = new() { "va", "cua", "cho", "voi", "khong", "duoc", "the", "nao", "lam", "bi", "loi", "khi", "tai", "trong", "mot", "cac", "nhung", "can", "ho", "tro", "yeu", "cau" };

    public static string[] Words(string? s) =>
        ImportService.Norm(s).Split(new[] { ' ', ',', '.', '/', '-', '(', ')', ':', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !Stop.Contains(w)).Distinct().ToArray();

    public static IEnumerable<KbArticle> Rank(IEnumerable<KbArticle> list, string? text, string? category = null)
    {
        var words = Words(text);
        if (words.Length == 0) return Enumerable.Empty<KbArticle>();
        return list.Select(a =>
            {
                var title = ImportService.Norm(a.Title + " " + a.Keywords);
                var body = ImportService.Norm(a.Body);
                var score = words.Sum(w => (title.Contains(w) ? 3 : 0) + (body.Contains(w) ? 1 : 0)) + (category != null && a.Category == category ? 2 : 0);
                return (a, score);
            })
            .Where(x => x.score >= 3).OrderByDescending(x => x.score).ThenByDescending(x => x.a.Helpful).Select(x => x.a);
    }
}
