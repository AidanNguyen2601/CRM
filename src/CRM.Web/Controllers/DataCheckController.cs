using CRM.Web.Data;
using CRM.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CRM.Web.Controllers;

/// <summary>API cho form nhập liệu: tra cứu mã số thuế, cảnh báo trùng ngay khi nhập</summary>
public class DataCheckController : CrmControllerBase
{
    private readonly TaxLookupService _tax;
    private readonly DuplicateCheckService _dups;
    public DataCheckController(CrmDbContext db, TaxLookupService tax, DuplicateCheckService dups) : base(db) { _tax = tax; _dups = dups; }

    /// <summary>Thông tin doanh nghiệp theo mã số thuế (JSON)</summary>
    [HttpGet]
    public async Task<IActionResult> Tax(string? code, CancellationToken ct)
    {
        var (info, error) = await _tax.LookupAsync(code, ct);
        if (info == null) return Json(new { ok = false, error });
        return Json(new
        {
            ok = true, info.TaxCode, name = info.DisplayName, rawName = info.Name, info.ShortName, info.InternationalName, info.Address, info.Status, info.Active,
            info.CityCode, info.CityName
        });
    }

    /// <summary>Bản ghi có thể trùng với dữ liệu đang nhập (JSON)</summary>
    [HttpGet]
    public async Task<IActionResult> Duplicates(string entity, int id, string? name, string? taxCode, string? phone, string? email, string? company)
    {
        if (entity is not ("Customer" or "Contact" or "Lead")) return BadRequest();
        var hits = await _dups.CheckAsync(entity, id, name, taxCode, phone, email, company, User);
        return Json(hits.Select(h => new { h.Type, typeLabel = DuplicateCheckService.TypeLabel(h.Type), h.Id, h.Title, h.Detail, h.Reason, h.Owner, h.Url, h.Strong }));
    }
}
