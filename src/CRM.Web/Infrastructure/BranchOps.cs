using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Infrastructure;

/// <summary>Thao tác dùng chung với cơ cấu công ty / chi nhánh</summary>
public static class BranchOps
{
    /// <summary>Chuyển toàn bộ bản ghi do người dùng phụ trách sang đơn vị mới (khi điều chuyển nhân viên). Trả về số bản ghi đã chuyển.</summary>
    public static async Task<int> MoveUserDataAsync(CrmDbContext db, int userId, int branchId)
    {
        var n = 0;
        n += await db.Customers.IgnoreQueryFilters().Where(x => x.OwnerId == userId && x.BranchId != branchId).ExecuteUpdateAsync(s => s.SetProperty(x => x.BranchId, branchId));
        n += await db.Contacts.IgnoreQueryFilters().Where(x => x.OwnerId == userId && x.BranchId != branchId).ExecuteUpdateAsync(s => s.SetProperty(x => x.BranchId, branchId));
        n += await db.Leads.IgnoreQueryFilters().Where(x => x.OwnerId == userId && x.BranchId != branchId).ExecuteUpdateAsync(s => s.SetProperty(x => x.BranchId, branchId));
        n += await db.Opportunities.IgnoreQueryFilters().Where(x => x.OwnerId == userId && x.BranchId != branchId).ExecuteUpdateAsync(s => s.SetProperty(x => x.BranchId, branchId));
        n += await db.Activities.IgnoreQueryFilters().Where(x => x.OwnerId == userId && x.BranchId != branchId).ExecuteUpdateAsync(s => s.SetProperty(x => x.BranchId, branchId));
        n += await db.Quotes.IgnoreQueryFilters().Where(x => x.OwnerId == userId && x.BranchId != branchId).ExecuteUpdateAsync(s => s.SetProperty(x => x.BranchId, branchId));
        n += await db.BusinessPlans.IgnoreQueryFilters().Where(x => x.OwnerId == userId && x.BranchId != branchId).ExecuteUpdateAsync(s => s.SetProperty(x => x.BranchId, branchId));
        n += await db.Contracts.IgnoreQueryFilters().Where(x => x.OwnerId == userId && x.BranchId != branchId).ExecuteUpdateAsync(s => s.SetProperty(x => x.BranchId, branchId));
        n += await db.Invoices.IgnoreQueryFilters().Where(x => x.OwnerId == userId && x.BranchId != branchId).ExecuteUpdateAsync(s => s.SetProperty(x => x.BranchId, branchId));
        n += await db.Payments.IgnoreQueryFilters().Where(x => x.OwnerId == userId && x.BranchId != branchId).ExecuteUpdateAsync(s => s.SetProperty(x => x.BranchId, branchId));
        n += await db.Campaigns.IgnoreQueryFilters().Where(x => x.OwnerId == userId && x.BranchId != branchId).ExecuteUpdateAsync(s => s.SetProperty(x => x.BranchId, branchId));
        n += await db.SyncTicketBranchesAsync();   // phiếu hỗ trợ theo đơn vị của khách hàng
        return n;
    }

    /// <summary>Số bản ghi nghiệp vụ thuộc từng đơn vị (bỏ qua phạm vi xem)</summary>
    public static async Task<Dictionary<int, int>> RecordCountsAsync(CrmDbContext db)
    {
        var all = new List<(int BranchId, int N)>();
        async Task Add<T>(IQueryable<T> q) where T : class, IBranchScoped =>
            all.AddRange((await q.IgnoreQueryFilters().GroupBy(x => x.BranchId).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).Select(x => (x.Key, x.N)));
        await Add(db.Customers); await Add(db.Contacts); await Add(db.Leads); await Add(db.Opportunities); await Add(db.Activities); await Add(db.Quotes);
        await Add(db.BusinessPlans); await Add(db.Contracts); await Add(db.Invoices); await Add(db.Payments); await Add(db.Campaigns); await Add(db.Tickets);
        return all.GroupBy(x => x.BranchId).ToDictionary(g => g.Key, g => g.Sum(x => x.N));
    }

    /// <summary>Danh sách chọn đơn vị dạng cây (thụt lề theo cấp)</summary>
    public static List<SelectListItem> Options(BranchSnapshot snap, int? selected, IEnumerable<int>? only = null, bool activeOnly = true) =>
        snap.Tree(only).Where(x => !activeOnly || x.Branch.IsActive || x.Branch.Id == selected)
            .Select(x => new SelectListItem(new string(' ', x.Depth * 4) + (x.Depth > 0 ? "└ " : "") + $"{x.Branch.Code} – {x.Branch.Name}",
                x.Branch.Id.ToString(), x.Branch.Id == selected))
            .ToList();
}
