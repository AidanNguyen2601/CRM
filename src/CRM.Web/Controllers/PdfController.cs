using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

/// <summary>Mẫu in PDF chứng từ: logo, màu chủ đạo, chân trang, lời kết báo giá, điều khoản hợp đồng, hướng dẫn thanh toán; xem thử.</summary>
[Authorize(Roles = AppRoles.Admin)]
public class PdfController : CrmControllerBase
{
    private const int MaxLogoBytes = 500 * 1024;
    private readonly PdfService _pdf;
    private readonly SettingsStore _settings;
    public PdfController(CrmDbContext db, PdfService pdf, SettingsStore settings) : base(db) { _pdf = pdf; _settings = settings; }

    public async Task<IActionResult> Index()
    {
        ViewBag.Logo = await _pdf.LogoAsync();
        ViewBag.FontPath = PdfService.FontPath;
        ViewBag.Samples = new Dictionary<string, int?>
        {
            [nameof(Quote)] = await Db.Quotes.OrderByDescending(q => q.Id).Select(q => (int?)q.Id).FirstOrDefaultAsync(),
            [nameof(Contract)] = await Db.Contracts.OrderByDescending(q => q.Id).Select(q => (int?)q.Id).FirstOrDefaultAsync(),
            [nameof(Invoice)] = await Db.Invoices.OrderByDescending(q => q.Id).Select(q => (int?)q.Id).FirstOrDefaultAsync()
        };
        return View(await _pdf.SettingsAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(PdfSettings model, IFormFile? logo, bool removeLogo)
    {
        static string? Clean(string? s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Replace("\r", "").Trim();
            return t.Length > max ? t[..max] : t;
        }
        var accent = (model.Accent ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(accent, "^#[0-9a-fA-F]{6}$")) accent = new PdfSettings().Accent;
        var s = new PdfSettings
        {
            Accent = accent, ShowLogo = model.ShowLogo, Footer = Clean(model.Footer, 300), QuoteNote = Clean(model.QuoteNote, 1000),
            ContractClauses = Clean(model.ContractClauses, 6000), PaymentNote = Clean(model.PaymentNote, 1000)
        };
        if (logo != null && logo.Length > 0)
        {
            var ext = Path.GetExtension(logo.FileName).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg") || logo.Length > MaxLogoBytes)
            {
                TempData["Error"] = I18n.T("Logo phải là ảnh PNG / JPG, tối đa 500 KB.");
                return RedirectToAction(nameof(Index));
            }
            using var ms = new MemoryStream();
            await logo.CopyToAsync(ms);
            var bytes = ms.ToArray();
            // Kiểm tra chữ ký file ảnh (không tin phần mở rộng)
            var isPng = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
            var isJpg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
            if (!isPng && !isJpg)
            {
                TempData["Error"] = I18n.T("Logo phải là ảnh PNG / JPG, tối đa 500 KB.");
                return RedirectToAction(nameof(Index));
            }
            await SaveLogoAsync(Convert.ToBase64String(bytes));
        }
        else if (removeLogo) await SaveLogoAsync(null);
        await _settings.SaveAsync(PdfSettings.Key, s);
        TempData["Success"] = I18n.T("Đã lưu mẫu in PDF.");
        return RedirectToAction(nameof(Index));
    }

    private async Task SaveLogoAsync(string? base64)
    {
        var row = await Db.AppSettings.FirstOrDefaultAsync(x => x.Key == PdfSettings.LogoKey);
        if (base64 == null) { if (row != null) Db.AppSettings.Remove(row); }
        else if (row == null) Db.AppSettings.Add(new AppSetting { Key = PdfSettings.LogoKey, Value = base64, UpdatedAt = DateTime.Now });
        else { row.Value = base64; row.UpdatedAt = DateTime.Now; }
        await Db.SaveChangesAsync();
    }

    /// <summary>Xem thử mẫu với chứng từ gần nhất</summary>
    public async Task<IActionResult> Preview(string type, int id)
    {
        if (!EmailsController.PdfTypes.Contains(type)) return NotFound();
        try
        {
            var pdf = await _pdf.RenderAsync(type, id);
            if (pdf == null) return NotFound();
            Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { Inline = true, FileName = pdf.Value.FileName }.ToString();
            return File(pdf.Value.Data, "application/pdf");
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index));
        }
    }
}
