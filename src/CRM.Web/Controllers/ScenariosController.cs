using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;

namespace CRM.Web.Controllers;

public class ScenariosViewModel
{
    public List<ScenarioFlow> Flows { get; init; } = new();
    public List<ScenarioAccount> Accounts { get; init; } = new();
    /// <summary>Tài khoản của từng vai trò (để hiện cạnh tên bước)</summary>
    public Dictionary<string, List<string>> ByRole { get; init; } = new();
    public bool ShowAccounts { get; init; }
}

/// <summary>Kịch bản nghiệp vụ: các luồng xuyên suốt của CRM, tài khoản nên dùng ở từng bước và chứng từ mẫu đang ở đúng bước</summary>
public class ScenariosController : CrmControllerBase
{
    private readonly ScenarioService _svc;
    public ScenariosController(CrmDbContext db, ScenarioService svc) : base(db) => _svc = svc;

    public async Task<IActionResult> Index()
    {
        var admin = User.IsInRole(AppRoles.Admin);
        var accounts = await _svc.AccountsAsync(admin);
        return View(new ScenariosViewModel
        {
            Flows = await _svc.FlowsAsync(User),
            Accounts = admin ? accounts : new(),
            ShowAccounts = admin,
            // Tên đăng nhập cạnh mỗi bước (chỉ quản trị thấy): tối đa 3 tài khoản cho mỗi vai trò
            ByRole = admin ? accounts.GroupBy(a => a.Role).ToDictionary(g => g.Key, g => g.Select(a => a.Username).Take(3).ToList()) : new()
        });
    }
}
