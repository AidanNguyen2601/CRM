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
    public DbSet<ReportAccess> ReportAccesses => Set<ReportAccess>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketComment> TicketComments => Set<TicketComment>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignMember> CampaignMembers => Set<CampaignMember>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<LoginLog> LoginLogs => Set<LoginLog>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();
    public DbSet<EmailTemplate> EmailTemplates => Set<EmailTemplate>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RecordShare> RecordShares => Set<RecordShare>();

    public DbSet<AutomationRule> AutomationRules => Set<AutomationRule>();
    public DbSet<AutomationLog> AutomationLogs => Set<AutomationLog>();
    public DbSet<PriceList> PriceLists => Set<PriceList>();
    public DbSet<PriceListItem> PriceListItems => Set<PriceListItem>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<EntityTag> EntityTags => Set<EntityTag>();
    public DbSet<CustomFieldDef> CustomFieldDefs => Set<CustomFieldDef>();
    public DbSet<CustomFieldValue> CustomFieldValues => Set<CustomFieldValue>();
    public DbSet<KbArticle> KbArticles => Set<KbArticle>();
    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();
    public DbSet<WebForm> WebForms => Set<WebForm>();
    public DbSet<InboundMessage> InboundMessages => Set<InboundMessage>();
    public DbSet<NpsSurvey> NpsSurveys => Set<NpsSurvey>();
    public DbSet<NpsResponse> NpsResponses => Set<NpsResponse>();

    /// <summary>Sự kiện nghiệp vụ phát sinh trong lần lưu (để chạy quy tắc tự động sau khi lưu)</summary>
    public List<(AutomationTrigger Trigger, object Entity)> PendingEvents { get; } = new();

    /// <summary>Tắt ghi nhật ký thay đổi (nạp dữ liệu mẫu số lượng lớn)</summary>
    public bool SuppressAudit { get; set; }
    /// <summary>Người đang thao tác (null ngoài request)</summary>
    public (int? Id, string? Name, string? Ip) Actor => _scope?.Actor ?? (null, null, null);

    public DbSet<SalesPlan> SalesPlans => Set<SalesPlan>();
    public DbSet<SalesPlanMonth> SalesPlanMonths => Set<SalesPlanMonth>();
    public DbSet<SalesPlanProduct> SalesPlanProducts => Set<SalesPlanProduct>();
    public DbSet<SalesPlanMember> SalesPlanMembers => Set<SalesPlanMember>();
    public DbSet<SalesPlanAction> SalesPlanActions => Set<SalesPlanAction>();

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

        // ---- Nền tảng: nhật ký, bảo mật, email, phân quyền, chia sẻ ----
        modelBuilder.Entity<AuditLog>().HasIndex(x => new { x.EntityType, x.EntityId });
        modelBuilder.Entity<AuditLog>().HasIndex(x => x.At);
        modelBuilder.Entity<LoginLog>().HasIndex(x => x.At);
        modelBuilder.Entity<LoginLog>().HasIndex(x => x.UserId);
        modelBuilder.Entity<PasswordResetToken>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        modelBuilder.Entity<PasswordResetToken>().HasIndex(x => x.TokenHash).IsUnique();
        modelBuilder.Entity<EmailMessage>().HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById);
        modelBuilder.Entity<EmailMessage>().HasIndex(x => new { x.Status, x.CreatedAt });
        modelBuilder.Entity<EmailMessage>().HasIndex(x => new { x.RelatedType, x.RelatedId });
        modelBuilder.Entity<EmailMessage>().HasIndex(x => x.CustomerId);
        modelBuilder.Entity<EmailTemplate>().HasIndex(x => x.Code);
        modelBuilder.Entity<RolePermission>().HasIndex(x => new { x.Role, x.Permission }).IsUnique();
        modelBuilder.Entity<RecordShare>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        modelBuilder.Entity<RecordShare>().HasIndex(x => new { x.EntityType, x.EntityId, x.UserId }).IsUnique();

        // ---- Vận hành bán hàng: tự động hoá, bảng giá, nhãn, trường tùy biến ----
        modelBuilder.Entity<AutomationRule>().HasOne(x => x.AssigneeUser).WithMany().HasForeignKey(x => x.AssigneeUserId);
        modelBuilder.Entity<AutomationRule>().HasIndex(x => new { x.Trigger, x.IsActive });
        modelBuilder.Entity<AutomationLog>().HasIndex(x => new { x.RuleId, x.EntityType, x.EntityId, x.Key }).IsUnique();
        modelBuilder.Entity<PriceListItem>().HasOne(x => x.PriceList).WithMany(p => p.Items).HasForeignKey(x => x.PriceListId);
        modelBuilder.Entity<PriceListItem>().HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
        modelBuilder.Entity<PriceListItem>().HasIndex(x => new { x.PriceListId, x.ProductId }).IsUnique();
        modelBuilder.Entity<Customer>().HasOne(x => x.PriceList).WithMany().HasForeignKey(x => x.PriceListId);
        modelBuilder.Entity<Quote>().HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId);
        modelBuilder.Entity<Quote>().HasOne(x => x.DiscountApprovedBy).WithMany().HasForeignKey(x => x.DiscountApprovedById);
        modelBuilder.Entity<Tag>().HasIndex(x => x.Name).IsUnique();
        modelBuilder.Entity<EntityTag>().HasOne(x => x.Tag).WithMany().HasForeignKey(x => x.TagId);
        modelBuilder.Entity<EntityTag>().HasIndex(x => new { x.TagId, x.EntityType, x.EntityId }).IsUnique();
        modelBuilder.Entity<EntityTag>().HasIndex(x => new { x.EntityType, x.EntityId });
        modelBuilder.Entity<CustomFieldDef>().HasIndex(x => x.EntityType);
        modelBuilder.Entity<CustomFieldValue>().HasOne(x => x.Field).WithMany().HasForeignKey(x => x.FieldId);
        modelBuilder.Entity<CustomFieldValue>().HasIndex(x => new { x.FieldId, x.EntityId }).IsUnique();

        // ---- Chăm sóc khách hàng nâng cao: kho kiến thức, email đến, NPS ----
        modelBuilder.Entity<KbArticle>().HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorId);
        modelBuilder.Entity<KbArticle>().HasIndex(x => x.Category);
        modelBuilder.Entity<InboundMessage>().HasIndex(x => x.MessageKey).IsUnique();
        modelBuilder.Entity<NpsResponse>().HasOne(x => x.Survey).WithMany(s => s.Responses).HasForeignKey(x => x.SurveyId);
        modelBuilder.Entity<NpsResponse>().HasOne(x => x.Contact).WithMany().HasForeignKey(x => x.ContactId);
        modelBuilder.Entity<NpsResponse>().HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        modelBuilder.Entity<NpsResponse>().HasIndex(x => x.Token).IsUnique();
        modelBuilder.Entity<NpsSurvey>().HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById);

        // ---- SLA: chính sách áp cho khách hàng / hợp đồng / phiếu ----
        modelBuilder.Entity<Customer>().HasOne(x => x.SlaPolicy).WithMany().HasForeignKey(x => x.SlaPolicyId);
        modelBuilder.Entity<Contract>().HasOne(x => x.SlaPolicy).WithMany().HasForeignKey(x => x.SlaPolicyId);
        modelBuilder.Entity<Ticket>().HasOne(x => x.SlaPolicy).WithMany().HasForeignKey(x => x.SlaPolicyId);
        modelBuilder.Entity<Ticket>().HasIndex(x => x.SlaPausedAt);

        // ---- Form thu lead trên website ----
        modelBuilder.Entity<WebForm>().HasIndex(x => x.Token).IsUnique();
        modelBuilder.Entity<WebForm>().HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId);
        modelBuilder.Entity<WebForm>().HasOne(x => x.Campaign).WithMany().HasForeignKey(x => x.CampaignId);
        modelBuilder.Entity<WebForm>().HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId);
        modelBuilder.Entity<Lead>().HasOne(x => x.WebForm).WithMany().HasForeignKey(x => x.WebFormId);

        modelBuilder.Entity<ReportAccess>().HasIndex(x => new { x.ReportKey, x.SubjectType, x.Subject }).IsUnique();

        // ---- Kế hoạch bán hàng ----
        modelBuilder.Entity<SalesPlan>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<SalesPlan>().HasIndex(x => new { x.Year, x.Status });
        modelBuilder.Entity<SalesPlan>().HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById);
        modelBuilder.Entity<SalesPlan>().HasOne(x => x.ApprovedBy).WithMany().HasForeignKey(x => x.ApprovedById);
        modelBuilder.Entity<SalesPlanMonth>().HasOne(x => x.SalesPlan).WithMany(p => p.Months).HasForeignKey(x => x.SalesPlanId);
        modelBuilder.Entity<SalesPlanMonth>().HasIndex(x => new { x.SalesPlanId, x.Month }).IsUnique();
        modelBuilder.Entity<SalesPlanProduct>().HasOne(x => x.SalesPlan).WithMany(p => p.Products).HasForeignKey(x => x.SalesPlanId);
        modelBuilder.Entity<SalesPlanMember>().HasOne(x => x.SalesPlan).WithMany(p => p.Members).HasForeignKey(x => x.SalesPlanId);
        modelBuilder.Entity<SalesPlanMember>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        modelBuilder.Entity<SalesPlanMember>().HasIndex(x => new { x.SalesPlanId, x.UserId }).IsUnique();
        modelBuilder.Entity<SalesPlanAction>().HasOne(x => x.SalesPlan).WithMany(p => p.Actions).HasForeignKey(x => x.SalesPlanId);
        modelBuilder.Entity<SalesPlanAction>().HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId);

        // ---- Cơ cấu chuỗi công ty / chi nhánh ----
        modelBuilder.Entity<Branch>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Branch>().HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId);
        modelBuilder.Entity<AppUser>().HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId);
        BranchScoped<Customer>(modelBuilder); BranchScoped<Contact>(modelBuilder); BranchScoped<Lead>(modelBuilder);
        BranchScoped<Opportunity>(modelBuilder); BranchScoped<Activity>(modelBuilder); BranchScoped<Quote>(modelBuilder);
        BranchScoped<BusinessPlan>(modelBuilder); BranchScoped<Contract>(modelBuilder); BranchScoped<Invoice>(modelBuilder);
        BranchScoped<Payment>(modelBuilder); BranchScoped<Campaign>(modelBuilder); BranchScoped<Ticket>(modelBuilder);
        BranchScoped<SalesPlan>(modelBuilder);

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
        SlaEngine.ApplyAsync(this).GetAwaiter().GetResult();
        var customerMoved = CustomerBranchChanged();
        var events = SuppressAudit ? null : AutomationService.Collect(this);
        var audit = SuppressAudit ? null : AuditTrail.Collect(this);
        var n = base.SaveChanges(acceptAllChangesOnSuccess);
        if (events is { Count: > 0 }) PendingEvents.AddRange(events);
        if (audit is { Count: > 0 }) { AuditLogs.AddRange(AuditTrail.Build(audit, Actor)); base.SaveChanges(acceptAllChangesOnSuccess); }
        if (customerMoved) SyncTicketBranchesAsync().GetAwaiter().GetResult();
        return n;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        await AssignBranchesAsync(ct);
        await SlaEngine.ApplyAsync(this, ct);
        var customerMoved = CustomerBranchChanged();
        // Nhật ký: thu thập trước khi lưu (giá trị cũ), ghi sau khi lưu (đã có Id của bản ghi mới)
        var events = SuppressAudit ? null : AutomationService.Collect(this);
        var audit = SuppressAudit ? null : AuditTrail.Collect(this);
        var n = await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        if (events is { Count: > 0 }) PendingEvents.AddRange(events);
        if (audit is { Count: > 0 }) { AuditLogs.AddRange(AuditTrail.Build(audit, Actor)); await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct); }
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
