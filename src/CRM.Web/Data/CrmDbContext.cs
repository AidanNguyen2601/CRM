using CRM.Web.Infrastructure;
using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Data;

public class CrmDbContext : DbContext
{
    private readonly BranchScope? _scope;

    public CrmDbContext(DbContextOptions<CrmDbContext> options, BranchScope? scope = null) : base(options) => _scope = scope;

    // Tham số cho bộ lọc theo đơn vị (EF đọc lại mỗi lần truy vấn)
    private bool BranchFilterOff => _scope == null || !_scope.Active;
    private int[] BranchFilterIds => _scope?.Ids ?? Array.Empty<int>();

    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<TaskChecklistItem> TaskChecklistItems => Set<TaskChecklistItem>();
    public DbSet<TaskComment> TaskComments => Set<TaskComment>();
    public DbSet<Notification> Notifications => Set<Notification>();
    /// <summary>Người dùng thuộc các đơn vị trong phạm vi đang xem (dùng cho danh sách chọn người phụ trách, KPI, báo cáo theo người)</summary>
    public IQueryable<AppUser> UsersInScope => _scope == null ? Users : _scope.Users(Users);

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<QuoteLine> QuoteLines => Set<QuoteLine>();
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<BusinessPlan> BusinessPlans => Set<BusinessPlan>();
    public DbSet<BusinessPlanLine> BusinessPlanLines => Set<BusinessPlanLine>();
    public DbSet<BusinessPlanCost> BusinessPlanCosts => Set<BusinessPlanCost>();
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<ContractLine> ContractLines => Set<ContractLine>();
    public DbSet<ContractMilestone> ContractMilestones => Set<ContractMilestone>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<SalesTarget> SalesTargets => Set<SalesTarget>();
    public DbSet<CustomReport> CustomReports => Set<CustomReport>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketComment> TicketComments => Set<TicketComment>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignMember> CampaignMembers => Set<CampaignMember>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUser>().HasIndex(x => x.Username).IsUnique();
        modelBuilder.Entity<Customer>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Product>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Quote>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<CatalogItem>().HasIndex(x => new { x.Group, x.Name }).IsUnique();
        modelBuilder.Entity<CatalogItem>().HasIndex(x => new { x.Group, x.Code }).IsUnique();
        modelBuilder.Entity<CatalogItem>().Property(x => x.Code).IsRequired();
        modelBuilder.Entity<BusinessPlan>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<BusinessPlan>().HasIndex(x => x.Status);
        modelBuilder.Entity<BusinessPlan>().HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId);
        modelBuilder.Entity<BusinessPlan>().HasOne(x => x.ApprovedBy).WithMany().HasForeignKey(x => x.ApprovedById);
        modelBuilder.Entity<Contract>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<Contract>().HasIndex(x => x.Status);
        modelBuilder.Entity<Invoice>().HasIndex(x => new { x.Symbol, x.Number }).IsUnique();
        modelBuilder.Entity<Invoice>().HasIndex(x => x.Status);
        modelBuilder.Entity<Payment>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<Attachment>().HasIndex(x => new { x.TargetType, x.TargetId });
        modelBuilder.Entity<SalesTarget>().HasIndex(x => new { x.UserId, x.Year, x.Month }).IsUnique();
        modelBuilder.Entity<Ticket>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<Ticket>().HasIndex(x => new { x.Status, x.OwnerId });
        modelBuilder.Entity<Ticket>().HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId);
        modelBuilder.Entity<Ticket>().HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById);
        modelBuilder.Entity<Activity>().HasOne(x => x.Ticket).WithMany(t => t.Tasks).HasForeignKey(x => x.TicketId);
        modelBuilder.Entity<Activity>().HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId);
        modelBuilder.Entity<Activity>().HasOne(x => x.AssignedBy).WithMany().HasForeignKey(x => x.AssignedById);
        modelBuilder.Entity<TaskChecklistItem>().HasOne(x => x.Activity).WithMany(a => a.Checklist).HasForeignKey(x => x.ActivityId);
        modelBuilder.Entity<TaskComment>().HasOne(x => x.Activity).WithMany(a => a.Comments).HasForeignKey(x => x.ActivityId);
        modelBuilder.Entity<Notification>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        modelBuilder.Entity<Notification>().HasOne(x => x.Actor).WithMany().HasForeignKey(x => x.ActorId);
        modelBuilder.Entity<Notification>().HasIndex(x => new { x.UserId, x.IsRead, x.CreatedAt });
        modelBuilder.Entity<Notification>().HasIndex(x => new { x.UserId, x.Key });
        modelBuilder.Entity<SalesTarget>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        modelBuilder.Entity<Opportunity>().HasIndex(x => x.Stage);
        modelBuilder.Entity<Activity>().HasIndex(x => new { x.OwnerId, x.Status, x.DueDate });
        modelBuilder.Entity<Campaign>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Campaign>().HasIndex(x => x.Status);
        modelBuilder.Entity<CampaignMember>().HasIndex(x => new { x.CampaignId, x.Status });
        modelBuilder.Entity<CampaignMember>().HasOne(x => x.Campaign).WithMany(c => c.Members).HasForeignKey(x => x.CampaignId);

        // ---- Cơ cấu chuỗi công ty / chi nhánh ----
        modelBuilder.Entity<Branch>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Branch>().HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId);
        modelBuilder.Entity<AppUser>().HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId);
        BranchScoped<Customer>(modelBuilder); BranchScoped<Contact>(modelBuilder); BranchScoped<Lead>(modelBuilder);
        BranchScoped<Opportunity>(modelBuilder); BranchScoped<Activity>(modelBuilder); BranchScoped<Quote>(modelBuilder);
        BranchScoped<BusinessPlan>(modelBuilder); BranchScoped<Contract>(modelBuilder); BranchScoped<Invoice>(modelBuilder);
        BranchScoped<Payment>(modelBuilder); BranchScoped<Campaign>(modelBuilder); BranchScoped<Ticket>(modelBuilder);

        // Không xóa dây chuyền: dữ liệu khách hàng phải xóa có chủ đích
        foreach (var fk in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;
    }

    /// <summary>Khóa ngoại tới đơn vị + bộ lọc toàn cục: chỉ thấy dữ liệu của các đơn vị trong phạm vi</summary>
    private void BranchScoped<T>(ModelBuilder mb) where T : class, IBranchScoped
    {
        mb.Entity<T>().HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId);
        mb.Entity<T>().HasQueryFilter(x => BranchFilterOff || BranchFilterIds.Contains(x.BranchId));
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AssignBranchesAsync(CancellationToken.None).GetAwaiter().GetResult();
        var customerMoved = CustomerBranchChanged();
        var n = base.SaveChanges(acceptAllChangesOnSuccess);
        if (customerMoved) SyncTicketBranchesAsync().GetAwaiter().GetResult();
        return n;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        await AssignBranchesAsync(ct);
        var customerMoved = CustomerBranchChanged();
        var n = await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        if (customerMoved) await SyncTicketBranchesAsync(ct);
        return n;
    }

    private bool CustomerBranchChanged() =>
        ChangeTracker.Entries<Customer>().Any(e => e.State == EntityState.Modified && e.Property(x => x.BranchId).IsModified);

    /// <summary>Phiếu hỗ trợ đi theo đơn vị của khách hàng (sau khi khách hàng chuyển đơn vị)</summary>
    public Task<int> SyncTicketBranchesAsync(CancellationToken ct = default) =>
        Database.ExecuteSqlRawAsync("UPDATE t SET t.[BranchId] = c.[BranchId] FROM [dbo].[Tickets] t JOIN [dbo].[Customers] c ON c.[Id] = t.[CustomerId] WHERE t.[BranchId] <> c.[BranchId]", ct);

    /// <summary>
    /// Gán đơn vị cho bản ghi nghiệp vụ: theo đơn vị của người phụ trách (cả khi đổi người phụ trách);
    /// phiếu hỗ trợ: theo khách hàng; không có người phụ trách → theo khách hàng → theo đơn vị đang làm việc của người dùng → đơn vị gốc.
    /// </summary>
    /// <summary>Phiếu hỗ trợ thuộc đơn vị của khách hàng (tổng đài CSKH xử lý cho cả chuỗi), không theo người xử lý</summary>
    private static bool FollowsCustomer(object entity) => entity is Ticket;

    private async Task AssignBranchesAsync(CancellationToken ct)
    {
        var entries = ChangeTracker.Entries<IBranchScoped>()
            .Where(e => e.State == EntityState.Added
                     || e.State == EntityState.Modified && (FollowsCustomer(e.Entity)
                         ? e.Property("CustomerId").IsModified
                         : e.Entity is IOwned && e.Property(nameof(IOwned.OwnerId)).IsModified))
            .ToList();
        if (entries.Count == 0) return;

        var ownerIds = entries.Select(e => (e.Entity as IOwned)?.OwnerId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var ownerBranch = ownerIds.Count == 0 ? new Dictionary<int, int>()
            : await Users.Where(u => ownerIds.Contains(u.Id)).Select(u => new { u.Id, u.BranchId }).ToDictionaryAsync(x => x.Id, x => x.BranchId, ct);
        int? root = null;
        foreach (var e in entries)
        {
            var byCustomer = FollowsCustomer(e.Entity);
            var owner = (e.Entity as IOwned)?.OwnerId;
            if (!byCustomer && owner.HasValue && ownerBranch.TryGetValue(owner.Value, out var ob) && ob != 0) { e.Entity.BranchId = ob; continue; }
            if (!byCustomer && e.State != EntityState.Added) continue;   // bỏ người phụ trách: giữ đơn vị cũ
            var custProp = e.Metadata.FindProperty("CustomerId");
            if (custProp != null && e.Property("CustomerId").CurrentValue is int cid && cid > 0)
            {
                var cb = Customers.Local.FirstOrDefault(c => c.Id == cid)?.BranchId
                         ?? await Customers.IgnoreQueryFilters().Where(c => c.Id == cid).Select(c => (int?)c.BranchId).FirstOrDefaultAsync(ct);
                if (cb is > 0) { e.Entity.BranchId = cb.Value; continue; }
            }
            if (e.State != EntityState.Added) continue;
            if (e.Entity.BranchId > 0 && await Branches.AnyAsync(b => b.Id == e.Entity.BranchId, ct)) continue;   // đã chỉ định sẵn (không người phụ trách / khách hàng)
            if (_scope?.WorkingBranchId is int wb && wb > 0) { e.Entity.BranchId = wb; continue; }
            root ??= await Branches.Where(b => b.ParentId == null).OrderBy(b => b.Id).Select(b => (int?)b.Id).FirstOrDefaultAsync(ct) ?? 0;
            e.Entity.BranchId = root.Value;
        }
    }
}
