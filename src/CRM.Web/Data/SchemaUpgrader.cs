using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Data;

/// <summary>
/// Bổ sung các bảng mới cho database đã được tạo trước đó bằng EnsureCreated (EnsureCreated không thêm bảng
/// vào DB đã tồn tại). Mỗi bước kiểm tra tồn tại trước nên chạy lại nhiều lần an toàn.
/// Cấu trúc phải khớp với model EF trong CrmDbContext / Entities.
/// </summary>
public static class SchemaUpgrader
{
    public static async Task EnsureAsync(CrmDbContext db)
    {
        foreach (var sql in Steps) await db.Database.ExecuteSqlRawAsync(sql);
    }

    private static readonly string[] Steps =
    {
        // ---------- Danh mục dùng chung ----------
        """
        IF OBJECT_ID(N'[dbo].[CatalogItems]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[CatalogItems] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_CatalogItems] PRIMARY KEY,
                [Group] int NOT NULL,
                [Name] nvarchar(200) NOT NULL,
                [NameEn] nvarchar(200) NULL,
                [SortOrder] int NOT NULL,
                [IsActive] bit NOT NULL
            );
            CREATE UNIQUE INDEX [IX_CatalogItems_Group_Name] ON [dbo].[CatalogItems] ([Group], [Name]);
        END
        """,

        // ---------- Phương án kinh doanh ----------
        """
        IF OBJECT_ID(N'[dbo].[BusinessPlans]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[BusinessPlans] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_BusinessPlans] PRIMARY KEY,
                [Number] nvarchar(30) NOT NULL,
                [Title] nvarchar(200) NOT NULL,
                [CustomerId] int NOT NULL CONSTRAINT [FK_BusinessPlans_Customers_CustomerId] REFERENCES [dbo].[Customers] ([Id]),
                [OpportunityId] int NULL CONSTRAINT [FK_BusinessPlans_Opportunities_OpportunityId] REFERENCES [dbo].[Opportunities] ([Id]),
                [QuoteId] int NULL CONSTRAINT [FK_BusinessPlans_Quotes_QuoteId] REFERENCES [dbo].[Quotes] ([Id]),
                [PlanDate] datetime2 NOT NULL,
                [Status] int NOT NULL,
                [Revenue] decimal(18,0) NOT NULL,
                [CostOfGoods] decimal(18,0) NOT NULL,
                [OtherCosts] decimal(18,0) NOT NULL,
                [Profit] decimal(18,0) NOT NULL,
                [MarginPercent] decimal(9,2) NOT NULL,
                [Notes] nvarchar(2000) NULL,
                [OwnerId] int NULL CONSTRAINT [FK_BusinessPlans_Users_OwnerId] REFERENCES [dbo].[Users] ([Id]),
                [SubmittedAt] datetime2 NULL,
                [ApprovedById] int NULL CONSTRAINT [FK_BusinessPlans_Users_ApprovedById] REFERENCES [dbo].[Users] ([Id]),
                [ApprovedAt] datetime2 NULL,
                [ApprovalNote] nvarchar(1000) NULL,
                [CreatedAt] datetime2 NOT NULL,
                [UpdatedAt] datetime2 NULL
            );
            CREATE UNIQUE INDEX [IX_BusinessPlans_Number] ON [dbo].[BusinessPlans] ([Number]);
            CREATE INDEX [IX_BusinessPlans_Status] ON [dbo].[BusinessPlans] ([Status]);
            CREATE INDEX [IX_BusinessPlans_CustomerId] ON [dbo].[BusinessPlans] ([CustomerId]);
            CREATE INDEX [IX_BusinessPlans_OpportunityId] ON [dbo].[BusinessPlans] ([OpportunityId]);
            CREATE INDEX [IX_BusinessPlans_QuoteId] ON [dbo].[BusinessPlans] ([QuoteId]);
            CREATE INDEX [IX_BusinessPlans_OwnerId] ON [dbo].[BusinessPlans] ([OwnerId]);
            CREATE INDEX [IX_BusinessPlans_ApprovedById] ON [dbo].[BusinessPlans] ([ApprovedById]);
        END
        """,
        """
        IF OBJECT_ID(N'[dbo].[BusinessPlanLines]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[BusinessPlanLines] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_BusinessPlanLines] PRIMARY KEY,
                [BusinessPlanId] int NOT NULL CONSTRAINT [FK_BusinessPlanLines_BusinessPlans_BusinessPlanId] REFERENCES [dbo].[BusinessPlans] ([Id]),
                [ProductId] int NULL CONSTRAINT [FK_BusinessPlanLines_Products_ProductId] REFERENCES [dbo].[Products] ([Id]),
                [Description] nvarchar(300) NOT NULL,
                [Unit] nvarchar(30) NULL,
                [Quantity] decimal(18,2) NOT NULL,
                [CostPrice] decimal(18,0) NOT NULL,
                [SalePrice] decimal(18,0) NOT NULL,
                [CostTotal] decimal(18,0) NOT NULL,
                [SaleTotal] decimal(18,0) NOT NULL,
                [SortOrder] int NOT NULL
            );
            CREATE INDEX [IX_BusinessPlanLines_BusinessPlanId] ON [dbo].[BusinessPlanLines] ([BusinessPlanId]);
            CREATE INDEX [IX_BusinessPlanLines_ProductId] ON [dbo].[BusinessPlanLines] ([ProductId]);
        END
        """,
        """
        IF OBJECT_ID(N'[dbo].[BusinessPlanCosts]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[BusinessPlanCosts] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_BusinessPlanCosts] PRIMARY KEY,
                [BusinessPlanId] int NOT NULL CONSTRAINT [FK_BusinessPlanCosts_BusinessPlans_BusinessPlanId] REFERENCES [dbo].[BusinessPlans] ([Id]),
                [Category] nvarchar(200) NOT NULL,
                [Description] nvarchar(300) NULL,
                [Amount] decimal(18,0) NOT NULL,
                [SortOrder] int NOT NULL
            );
            CREATE INDEX [IX_BusinessPlanCosts_BusinessPlanId] ON [dbo].[BusinessPlanCosts] ([BusinessPlanId]);
        END
        """,
    };
}
