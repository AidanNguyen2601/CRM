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
        await EnsureCatalogCodesAsync(db);
    }

    private sealed class CatalogCodeRow
    {
        public int Id { get; set; }
        public int Group { get; set; }
        public string Name { get; set; } = "";
        public string? Code { get; set; }
    }

    /// <summary>
    /// Cột Mã cho danh mục: thêm cột (nếu chưa có), tự cấp mã từ tên cho các mục chưa có mã,
    /// rồi đặt NOT NULL + duy nhất theo (Group, Code).
    /// </summary>
    private static async Task EnsureCatalogCodesAsync(CrmDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            IF COL_LENGTH(N'dbo.CatalogItems', N'Code') IS NULL
                ALTER TABLE [dbo].[CatalogItems] ADD [Code] nvarchar(30) NULL;
            """);

        var rows = await db.Database.SqlQueryRaw<CatalogCodeRow>(
            "SELECT [Id], [Group], [Name], [Code] FROM [dbo].[CatalogItems] ORDER BY [Group], [SortOrder], [Id]").ToListAsync();
        foreach (var grp in rows.GroupBy(r => r.Group))
        {
            var used = grp.Where(r => !string.IsNullOrWhiteSpace(r.Code)).Select(r => r.Code!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var r in grp.Where(r => string.IsNullOrWhiteSpace(r.Code)))
            {
                var code = Infrastructure.CatalogCode.Suggest(r.Name, used);
                used.Add(code);
                await db.Database.ExecuteSqlRawAsync("UPDATE [dbo].[CatalogItems] SET [Code] = {0} WHERE [Id] = {1}", code, r.Id);
            }
        }

        await db.Database.ExecuteSqlRawAsync("""
            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.CatalogItems') AND name = N'Code' AND is_nullable = 1)
                ALTER TABLE [dbo].[CatalogItems] ALTER COLUMN [Code] nvarchar(30) NOT NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CatalogItems') AND name = N'IX_CatalogItems_Group_Code')
                CREATE UNIQUE INDEX [IX_CatalogItems_Group_Code] ON [dbo].[CatalogItems] ([Group], [Code]);
            """);
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
                [Code] nvarchar(30) NULL,
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

        // ---------- Danh mục hàng hoá: bổ sung cột cho Products ----------
        """
        IF COL_LENGTH(N'dbo.Products', N'Type') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Products] ADD
                [Type] int NOT NULL CONSTRAINT [DF_Products_Type] DEFAULT 1,
                [CostPrice] decimal(18,0) NOT NULL CONSTRAINT [DF_Products_CostPrice] DEFAULT 0,
                [VatPercent] decimal(5,2) NOT NULL CONSTRAINT [DF_Products_VatPercent] DEFAULT 10,
                [Brand] nvarchar(100) NULL,
                [Origin] nvarchar(100) NULL,
                [WarrantyMonths] int NULL,
                [Specification] nvarchar(1000) NULL;
            -- Hàng đang thuộc nhóm "Dịch vụ" → loại Dịch vụ
            EXEC(N'UPDATE [dbo].[Products] SET [Type] = 2 WHERE [Category] = N''Dịch vụ''');
        END
        """,

        // ---------- Hợp đồng ----------
        """
        IF OBJECT_ID(N'[dbo].[Contracts]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[Contracts] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Contracts] PRIMARY KEY,
                [Number] nvarchar(30) NOT NULL,
                [Title] nvarchar(200) NOT NULL,
                [CustomerId] int NOT NULL CONSTRAINT [FK_Contracts_Customers_CustomerId] REFERENCES [dbo].[Customers] ([Id]),
                [ContactId] int NULL CONSTRAINT [FK_Contracts_Contacts_ContactId] REFERENCES [dbo].[Contacts] ([Id]),
                [OpportunityId] int NULL CONSTRAINT [FK_Contracts_Opportunities_OpportunityId] REFERENCES [dbo].[Opportunities] ([Id]),
                [QuoteId] int NULL CONSTRAINT [FK_Contracts_Quotes_QuoteId] REFERENCES [dbo].[Quotes] ([Id]),
                [BusinessPlanId] int NULL CONSTRAINT [FK_Contracts_BusinessPlans_BusinessPlanId] REFERENCES [dbo].[BusinessPlans] ([Id]),
                [SignDate] datetime2 NULL,
                [StartDate] datetime2 NULL,
                [EndDate] datetime2 NULL,
                [Status] int NOT NULL,
                [VatPercent] decimal(5,2) NOT NULL,
                [SubTotal] decimal(18,0) NOT NULL,
                [VatAmount] decimal(18,0) NOT NULL,
                [Total] decimal(18,0) NOT NULL,
                [PaymentTerms] nvarchar(2000) NULL,
                [Notes] nvarchar(2000) NULL,
                [OwnerId] int NULL CONSTRAINT [FK_Contracts_Users_OwnerId] REFERENCES [dbo].[Users] ([Id]),
                [CreatedAt] datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX [IX_Contracts_Number] ON [dbo].[Contracts] ([Number]);
            CREATE INDEX [IX_Contracts_Status] ON [dbo].[Contracts] ([Status]);
            CREATE INDEX [IX_Contracts_CustomerId] ON [dbo].[Contracts] ([CustomerId]);
            CREATE INDEX [IX_Contracts_ContactId] ON [dbo].[Contracts] ([ContactId]);
            CREATE INDEX [IX_Contracts_OpportunityId] ON [dbo].[Contracts] ([OpportunityId]);
            CREATE INDEX [IX_Contracts_QuoteId] ON [dbo].[Contracts] ([QuoteId]);
            CREATE INDEX [IX_Contracts_BusinessPlanId] ON [dbo].[Contracts] ([BusinessPlanId]);
            CREATE INDEX [IX_Contracts_OwnerId] ON [dbo].[Contracts] ([OwnerId]);
        END
        """,
        """
        IF OBJECT_ID(N'[dbo].[ContractLines]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[ContractLines] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ContractLines] PRIMARY KEY,
                [ContractId] int NOT NULL CONSTRAINT [FK_ContractLines_Contracts_ContractId] REFERENCES [dbo].[Contracts] ([Id]),
                [ProductId] int NULL CONSTRAINT [FK_ContractLines_Products_ProductId] REFERENCES [dbo].[Products] ([Id]),
                [Description] nvarchar(300) NOT NULL,
                [Unit] nvarchar(30) NULL,
                [Quantity] decimal(18,2) NOT NULL,
                [UnitPrice] decimal(18,0) NOT NULL,
                [LineTotal] decimal(18,0) NOT NULL,
                [SortOrder] int NOT NULL
            );
            CREATE INDEX [IX_ContractLines_ContractId] ON [dbo].[ContractLines] ([ContractId]);
            CREATE INDEX [IX_ContractLines_ProductId] ON [dbo].[ContractLines] ([ProductId]);
        END
        """,
        """
        IF OBJECT_ID(N'[dbo].[ContractMilestones]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[ContractMilestones] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ContractMilestones] PRIMARY KEY,
                [ContractId] int NOT NULL CONSTRAINT [FK_ContractMilestones_Contracts_ContractId] REFERENCES [dbo].[Contracts] ([Id]),
                [Name] nvarchar(200) NOT NULL,
                [Percent] decimal(5,2) NOT NULL,
                [Amount] decimal(18,0) NOT NULL,
                [DueDate] datetime2 NULL,
                [Condition] nvarchar(300) NULL,
                [SortOrder] int NOT NULL
            );
            CREATE INDEX [IX_ContractMilestones_ContractId] ON [dbo].[ContractMilestones] ([ContractId]);
        END
        """,

        // ---------- Hoá đơn ----------
        """
        IF OBJECT_ID(N'[dbo].[Invoices]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[Invoices] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Invoices] PRIMARY KEY,
                [Symbol] nvarchar(20) NOT NULL,
                [Number] nvarchar(20) NOT NULL,
                [InvoiceDate] datetime2 NOT NULL,
                [DueDate] datetime2 NULL,
                [CustomerId] int NOT NULL CONSTRAINT [FK_Invoices_Customers_CustomerId] REFERENCES [dbo].[Customers] ([Id]),
                [ContractId] int NULL CONSTRAINT [FK_Invoices_Contracts_ContractId] REFERENCES [dbo].[Contracts] ([Id]),
                [MilestoneId] int NULL CONSTRAINT [FK_Invoices_ContractMilestones_MilestoneId] REFERENCES [dbo].[ContractMilestones] ([Id]),
                [Status] int NOT NULL,
                [VatPercent] decimal(5,2) NOT NULL,
                [SubTotal] decimal(18,0) NOT NULL,
                [VatAmount] decimal(18,0) NOT NULL,
                [Total] decimal(18,0) NOT NULL,
                [PaidAmount] decimal(18,0) NOT NULL,
                [Notes] nvarchar(1000) NULL,
                [OwnerId] int NULL CONSTRAINT [FK_Invoices_Users_OwnerId] REFERENCES [dbo].[Users] ([Id]),
                [CreatedAt] datetime2 NOT NULL,
                [IssuedAt] datetime2 NULL
            );
            CREATE UNIQUE INDEX [IX_Invoices_Symbol_Number] ON [dbo].[Invoices] ([Symbol], [Number]);
            CREATE INDEX [IX_Invoices_Status] ON [dbo].[Invoices] ([Status]);
            CREATE INDEX [IX_Invoices_CustomerId] ON [dbo].[Invoices] ([CustomerId]);
            CREATE INDEX [IX_Invoices_ContractId] ON [dbo].[Invoices] ([ContractId]);
            CREATE INDEX [IX_Invoices_MilestoneId] ON [dbo].[Invoices] ([MilestoneId]);
            CREATE INDEX [IX_Invoices_OwnerId] ON [dbo].[Invoices] ([OwnerId]);
        END
        """,
        """
        IF OBJECT_ID(N'[dbo].[InvoiceLines]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[InvoiceLines] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_InvoiceLines] PRIMARY KEY,
                [InvoiceId] int NOT NULL CONSTRAINT [FK_InvoiceLines_Invoices_InvoiceId] REFERENCES [dbo].[Invoices] ([Id]),
                [ProductId] int NULL CONSTRAINT [FK_InvoiceLines_Products_ProductId] REFERENCES [dbo].[Products] ([Id]),
                [Description] nvarchar(300) NOT NULL,
                [Unit] nvarchar(30) NULL,
                [Quantity] decimal(18,2) NOT NULL,
                [UnitPrice] decimal(18,0) NOT NULL,
                [LineTotal] decimal(18,0) NOT NULL,
                [SortOrder] int NOT NULL
            );
            CREATE INDEX [IX_InvoiceLines_InvoiceId] ON [dbo].[InvoiceLines] ([InvoiceId]);
            CREATE INDEX [IX_InvoiceLines_ProductId] ON [dbo].[InvoiceLines] ([ProductId]);
        END
        """,

        // ---------- Thanh toán ----------
        """
        IF OBJECT_ID(N'[dbo].[Payments]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[Payments] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Payments] PRIMARY KEY,
                [Number] nvarchar(30) NOT NULL,
                [PaymentDate] datetime2 NOT NULL,
                [CustomerId] int NOT NULL CONSTRAINT [FK_Payments_Customers_CustomerId] REFERENCES [dbo].[Customers] ([Id]),
                [ContractId] int NULL CONSTRAINT [FK_Payments_Contracts_ContractId] REFERENCES [dbo].[Contracts] ([Id]),
                [InvoiceId] int NULL CONSTRAINT [FK_Payments_Invoices_InvoiceId] REFERENCES [dbo].[Invoices] ([Id]),
                [Amount] decimal(18,0) NOT NULL,
                [Method] nvarchar(100) NOT NULL,
                [Reference] nvarchar(100) NULL,
                [Notes] nvarchar(500) NULL,
                [OwnerId] int NULL CONSTRAINT [FK_Payments_Users_OwnerId] REFERENCES [dbo].[Users] ([Id]),
                [CreatedAt] datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX [IX_Payments_Number] ON [dbo].[Payments] ([Number]);
            CREATE INDEX [IX_Payments_CustomerId] ON [dbo].[Payments] ([CustomerId]);
            CREATE INDEX [IX_Payments_ContractId] ON [dbo].[Payments] ([ContractId]);
            CREATE INDEX [IX_Payments_InvoiceId] ON [dbo].[Payments] ([InvoiceId]);
            CREATE INDEX [IX_Payments_OwnerId] ON [dbo].[Payments] ([OwnerId]);
        END
        """,

        // ---------- File đính kèm ----------
        """
        IF OBJECT_ID(N'[dbo].[Attachments]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[Attachments] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Attachments] PRIMARY KEY,
                [TargetType] int NOT NULL,
                [TargetId] int NOT NULL,
                [FileName] nvarchar(255) NOT NULL,
                [ContentType] nvarchar(100) NOT NULL,
                [Size] bigint NOT NULL,
                [StoragePath] nvarchar(300) NOT NULL,
                [Note] nvarchar(300) NULL,
                [UploadedById] int NULL CONSTRAINT [FK_Attachments_Users_UploadedById] REFERENCES [dbo].[Users] ([Id]),
                [UploadedAt] datetime2 NOT NULL
            );
            CREATE INDEX [IX_Attachments_TargetType_TargetId] ON [dbo].[Attachments] ([TargetType], [TargetId]);
            CREATE INDEX [IX_Attachments_UploadedById] ON [dbo].[Attachments] ([UploadedById]);
        END
        """,
    };
}
