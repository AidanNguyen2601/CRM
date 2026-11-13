using CRM.Web.Data;
using CRM.Web.Infrastructure;
using CRM.Web.Models;
using CRM.Web.Models.Entities;
using CRM.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Controllers;

public class HomeController : CrmControllerBase
{
    public HomeController(CrmDbContext db) : base(db) { }

    /// <summary>Dashboard kinh doanh (Sales: số liệu của mình; Manager/Admin: toàn công ty)</summary>
    public async Task<IActionResult> Index()
    {
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var vm = new DashboardViewModel();

        var opps = await Db.Opportunities.VisibleTo(User).Include(o => o.Customer).AsNoTracking().ToListAsync();
        var open = opps.Where(o => !o.IsClosed).ToList();
        vm.OpenOpportunities = open.Count;
        vm.OpenPipeline = open.Sum(o => o.Amount);
        vm.WeightedPipeline = open.Sum(o => o.WeightedAmount);

        var won = opps.Where(o => o.Stage == OpportunityStage.ClosedWon).ToList();
        var lost = opps.Count(o => o.Stage == OpportunityStage.ClosedLost);
        vm.WonThisMonth = won.Where(o => o.ClosedAt >= monthStart).Sum(o => o.Amount);
        vm.WonCountThisMonth = won.Count(o => o.ClosedAt >= monthStart);
        vm.WinRate = won.Count + lost == 0 ? 0 : Math.Round(won.Count * 100m / (won.Count + lost), 0);

        vm.Pipeline = Enum.GetValues<OpportunityStage>().Where(s => s is not (OpportunityStage.ClosedWon or OpportunityStage.ClosedLost))
            .Select(s => new StageSummary { Stage = s, Count = open.Count(o => o.Stage == s), Amount = open.Where(o => o.Stage == s).Sum(o => o.Amount) })
            .ToList();

        for (var i = 5; i >= 0; i--)
        {
            var m = monthStart.AddMonths(-i);
            vm.WonByMonth.Add((I18n.IsEnglish ? m.ToString("MMM yy", System.Globalization.CultureInfo.GetCultureInfo("en-US")) : $"T{m.Month}/{m:yy}", won.Where(o => o.ClosedAt >= m && o.ClosedAt < m.AddMonths(1)).Sum(o => o.Amount)));
        }

        var leads = await Db.Leads.VisibleTo(User).AsNoTracking().ToListAsync();
        vm.NewLeads30d = leads.Count(l => l.CreatedAt >= today.AddDays(-30));
        vm.LeadConversionRate = leads.Count == 0 ? 0 : Math.Round(leads.Count(l => l.Status == LeadStatus.Converted) * 100m / leads.Count, 0);
        vm.LeadsBySource = leads.GroupBy(l => l.Source).Select(g => (g.Key.GetDisplayName(), g.Count()))
            .OrderByDescending(x => x.Item2).ToList();

        vm.ClosingSoon = open.Where(o => o.ExpectedCloseDate <= today.AddDays(30))
            .OrderBy(o => o.ExpectedCloseDate).Take(6).ToList();

        var acts = Db.Activities.VisibleTo(User).Where(a => a.Status == ActivityStatus.Planned);
        vm.OverdueActivities = await acts.CountAsync(a => a.DueDate < DateTime.Now);
        vm.UpcomingActivities = await acts.Include(a => a.Customer).Include(a => a.Lead)
            .OrderBy(a => a.DueDate).Take(8).ToListAsync();

        // KPI tháng này: nhân viên kinh doanh xem của mình, quản lý xem tổng đội
        var kpi = HttpContext.RequestServices.GetRequiredService<KpiService>();
        var kpiUsers = await kpi.SalesUsersAsync();
        if (!User.SeesAll()) kpiUsers = kpiUsers.Where(u => u.Id == CurrentUserId).ToList();
        if (kpiUsers.Count > 0)
        {
            var period = KpiPeriod.Month(today.Year, today.Month);
            ViewBag.KpiPeriod = period;
            ViewBag.Kpi = KpiService.Total(await kpi.ComputeAsync(period, kpiUsers), new AppUser());
        }

        return View(vm);
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
        => View(new ErrorViewModel { RequestId = System.Diagnostics.Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
