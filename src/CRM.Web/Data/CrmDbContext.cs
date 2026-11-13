using CRM.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Data;

public class CrmDbContext : DbContext
{
    public CrmDbContext(DbContextOptions<CrmDbContext> options) : base(options) { }

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUser>().HasIndex(x => x.Username).IsUnique();
        modelBuilder.Entity<Customer>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Product>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<Quote>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<CatalogItem>().HasIndex(x => new { x.Group, x.Name }).IsUnique();
        modelBuilder.Entity<BusinessPlan>().HasIndex(x => x.Number).IsUnique();
        modelBuilder.Entity<BusinessPlan>().HasIndex(x => x.Status);
        modelBuilder.Entity<BusinessPlan>().HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId);
        modelBuilder.Entity<BusinessPlan>().HasOne(x => x.ApprovedBy).WithMany().HasForeignKey(x => x.ApprovedById);
        modelBuilder.Entity<Opportunity>().HasIndex(x => x.Stage);
        modelBuilder.Entity<Activity>().HasIndex(x => new { x.OwnerId, x.Status, x.DueDate });

        // Không xóa dây chuyền: dữ liệu khách hàng phải xóa có chủ đích
        foreach (var fk in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;
    }
}
