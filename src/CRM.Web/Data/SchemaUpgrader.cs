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
        foreach (var sql in BranchSteps()) await db.Database.ExecuteSqlRawAsync(sql);
        await EnsureCatalogCodesAsync(db);
        await ReportingSchema.EnsureAsync(db);
        await Infrastructure.PlatformDefaults.EnsureAsync(db);
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

    /// <summary>Bảng đơn vị cho tất cả các bảng thuộc đơn vị (người dùng + nghiệp vụ)</summary>
    public static readonly string[] BranchScopedTables =
        { "Users", "Customers", "Contacts", "Leads", "Opportunities", "Activities", "Quotes", "BusinessPlans", "Contracts", "Invoices", "Payments", "Campaigns", "Tickets" };

    /// <summary>
    /// Chuỗi công ty / chi nhánh: bảng Branches + đơn vị gốc; thêm BranchId (NOT NULL, mặc định = đơn vị gốc cho dữ liệu cũ)
    /// vào người dùng và các bảng nghiệp vụ.
    /// </summary>
    private static IEnumerable<string> BranchSteps()
    {
        yield return """
            IF OBJECT_ID(N'[dbo].[AppSettings]', N'U') IS NULL
                CREATE TABLE [dbo].[AppSettings] (
                    [Key] nvarchar(100) NOT NULL CONSTRAINT [PK_AppSettings] PRIMARY KEY,
                    [Value] nvarchar(max) NULL,
                    [UpdatedAt] datetime2 NOT NULL
                );
            IF COL_LENGTH(N'dbo.Users', N'UiPrefs') IS NULL
                ALTER TABLE [dbo].[Users] ADD [UiPrefs] nvarchar(1000) NULL;
            """;
        // Việc chính của phiếu hỗ trợ lỡ ghi tiêu đề tiếng Anh (tạo ngoài request trên máy cài tiếng Anh) → tiếng Việt
        yield return "IF COL_LENGTH(N'dbo.Activities', N'IsTicketMain') IS NOT NULL EXEC(N'UPDATE [dbo].[Activities] SET [Subject] = N''Xử lý phiếu '' + SUBSTRING([Subject], 15, 200) WHERE [IsTicketMain] = 1 AND [Subject] LIKE N''Handle ticket %''')";
        // ---------- Quản lý công việc + thông báo ----------
        yield return """
            IF COL_LENGTH(N'dbo.Activities', N'AssignedById') IS NULL
            BEGIN
                ALTER TABLE [dbo].[Activities] ADD
                    [AssignedById] int NULL CONSTRAINT [FK_Activities_Users_AssignedById] REFERENCES [dbo].[Users] ([Id]),
                    [StartedAt] datetime2 NULL,
                    [Progress] int NOT NULL CONSTRAINT [DF_Activities_Progress] DEFAULT 0;
                EXEC(N'CREATE INDEX [IX_Activities_AssignedById] ON [dbo].[Activities] ([AssignedById])');
            END
            """;
        yield return """
            IF OBJECT_ID(N'[dbo].[TaskChecklistItems]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TaskChecklistItems] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TaskChecklistItems] PRIMARY KEY,
                    [ActivityId] int NOT NULL CONSTRAINT [FK_TaskChecklistItems_Activities_ActivityId] REFERENCES [dbo].[Activities] ([Id]),
                    [Title] nvarchar(300) NOT NULL,
                    [IsDone] bit NOT NULL,
                    [DoneAt] datetime2 NULL,
                    [SortOrder] int NOT NULL
                );
                CREATE INDEX [IX_TaskChecklistItems_ActivityId] ON [dbo].[TaskChecklistItems] ([ActivityId]);
            END
            IF OBJECT_ID(N'[dbo].[TaskComments]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TaskComments] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TaskComments] PRIMARY KEY,
                    [ActivityId] int NOT NULL CONSTRAINT [FK_TaskComments_Activities_ActivityId] REFERENCES [dbo].[Activities] ([Id]),
                    [Body] nvarchar(2000) NOT NULL,
                    [IsSystem] bit NOT NULL,
                    [AuthorId] int NULL CONSTRAINT [FK_TaskComments_Users_AuthorId] REFERENCES [dbo].[Users] ([Id]),
                    [CreatedAt] datetime2 NOT NULL
                );
                CREATE INDEX [IX_TaskComments_ActivityId] ON [dbo].[TaskComments] ([ActivityId]);
                CREATE INDEX [IX_TaskComments_AuthorId] ON [dbo].[TaskComments] ([AuthorId]);
            END
            IF OBJECT_ID(N'[dbo].[Notifications]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[Notifications] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Notifications] PRIMARY KEY,
                    [UserId] int NOT NULL CONSTRAINT [FK_Notifications_Users_UserId] REFERENCES [dbo].[Users] ([Id]),
                    [Type] nvarchar(30) NOT NULL,
                    [Title] nvarchar(300) NOT NULL,
                    [Body] nvarchar(500) NULL,
                    [Url] nvarchar(300) NULL,
                    [Key] nvarchar(150) NULL,
                    [ActorId] int NULL CONSTRAINT [FK_Notifications_Users_ActorId] REFERENCES [dbo].[Users] ([Id]),
                    [IsRead] bit NOT NULL,
                    [ReadAt] datetime2 NULL,
                    [CreatedAt] datetime2 NOT NULL
                );
                CREATE INDEX [IX_Notifications_UserId_IsRead_CreatedAt] ON [dbo].[Notifications] ([UserId], [IsRead], [CreatedAt]);
                CREATE INDEX [IX_Notifications_UserId_Key] ON [dbo].[Notifications] ([UserId], [Key]);
                CREATE INDEX [IX_Notifications_ActorId] ON [dbo].[Notifications] ([ActorId]);
            END
            """;
        yield return """
            IF OBJECT_ID(N'[dbo].[Branches]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[Branches] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Branches] PRIMARY KEY,
                    [Code] nvarchar(20) NOT NULL,
                    [Name] nvarchar(200) NOT NULL,
                    [ShortName] nvarchar(100) NULL,
                    [Type] int NOT NULL,
                    [ParentId] int NULL CONSTRAINT [FK_Branches_Branches_ParentId] REFERENCES [dbo].[Branches] ([Id]),
                    [TaxCode] nvarchar(20) NULL,
                    [Address] nvarchar(300) NULL,
                    [Phone] nvarchar(50) NULL,
                    [Email] nvarchar(150) NULL,
                    [Representative] nvarchar(150) NULL,
                    [BankAccount] nvarchar(200) NULL,
                    [InvoiceSymbol] nvarchar(20) NULL,
                    [SortOrder] int NOT NULL,
                    [IsActive] bit NOT NULL,
                    [CreatedAt] datetime2 NOT NULL
                );
                CREATE UNIQUE INDEX [IX_Branches_Code] ON [dbo].[Branches] ([Code]);
                CREATE INDEX [IX_Branches_ParentId] ON [dbo].[Branches] ([ParentId]);
            END
            IF NOT EXISTS (SELECT 1 FROM [dbo].[Branches])
                INSERT INTO [dbo].[Branches] ([Code], [Name], [Type], [SortOrder], [IsActive], [CreatedAt])
                VALUES (N'HQ', N'Công ty chính', 1, 0, 1, SYSDATETIME());
            """;
        foreach (var t in BranchScopedTables)
            yield return $"""
                IF COL_LENGTH(N'dbo.{t}', N'BranchId') IS NULL
                BEGIN
                    DECLARE @root nvarchar(12) = CAST((SELECT MIN([Id]) FROM [dbo].[Branches] WHERE [ParentId] IS NULL) AS nvarchar(12));
                    DECLARE @sql nvarchar(max) = N'ALTER TABLE [dbo].[{t}] ADD [BranchId] int NOT NULL CONSTRAINT [DF_{t}_BranchId] DEFAULT ' + @root
                        + N' CONSTRAINT [FK_{t}_Branches_BranchId] REFERENCES [dbo].[Branches] ([Id])';
                    EXEC(@sql);
                    EXEC(N'CREATE INDEX [IX_{t}_BranchId] ON [dbo].[{t}] ([BranchId])');
                END
                """;
        // ---------- Nền tảng: nhật ký, bảo mật đăng nhập, email, phân quyền, chia sẻ ----------
        yield return """
            IF COL_LENGTH(N'dbo.Users', N'FailedLoginCount') IS NULL
                ALTER TABLE [dbo].[Users] ADD
                    [FailedLoginCount] int NOT NULL CONSTRAINT [DF_Users_FailedLoginCount] DEFAULT 0,
                    [LockoutUntil] datetime2 NULL,
                    [TwoFactorEnabled] bit NOT NULL CONSTRAINT [DF_Users_TwoFactorEnabled] DEFAULT 0,
                    [TwoFactorSecret] nvarchar(500) NULL,
                    [PasswordChangedAt] datetime2 NULL,
                    [EmailNotifications] bit NOT NULL CONSTRAINT [DF_Users_EmailNotifications] DEFAULT 0;
            IF OBJECT_ID(N'[dbo].[AuditLogs]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[AuditLogs] (
                    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_AuditLogs] PRIMARY KEY,
                    [EntityType] nvarchar(50) NOT NULL,
                    [EntityId] int NOT NULL,
                    [Action] nvarchar(20) NOT NULL,
                    [Title] nvarchar(300) NULL,
                    [Changes] nvarchar(max) NULL,
                    [UserId] int NULL,
                    [UserName] nvarchar(150) NULL,
                    [Ip] nvarchar(50) NULL,
                    [At] datetime2 NOT NULL
                );
                CREATE INDEX [IX_AuditLogs_EntityType_EntityId] ON [dbo].[AuditLogs] ([EntityType], [EntityId]);
                CREATE INDEX [IX_AuditLogs_At] ON [dbo].[AuditLogs] ([At]);
            END
            IF OBJECT_ID(N'[dbo].[LoginLogs]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[LoginLogs] (
                    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_LoginLogs] PRIMARY KEY,
                    [UserId] int NULL,
                    [Username] nvarchar(50) NOT NULL,
                    [Success] bit NOT NULL,
                    [Reason] nvarchar(100) NULL,
                    [Ip] nvarchar(50) NULL,
                    [UserAgent] nvarchar(300) NULL,
                    [At] datetime2 NOT NULL
                );
                CREATE INDEX [IX_LoginLogs_At] ON [dbo].[LoginLogs] ([At]);
                CREATE INDEX [IX_LoginLogs_UserId] ON [dbo].[LoginLogs] ([UserId]);
            END
            IF OBJECT_ID(N'[dbo].[PasswordResetTokens]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[PasswordResetTokens] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_PasswordResetTokens] PRIMARY KEY,
                    [UserId] int NOT NULL CONSTRAINT [FK_PasswordResetTokens_Users_UserId] REFERENCES [dbo].[Users] ([Id]),
                    [TokenHash] nvarchar(100) NOT NULL,
                    [ExpiresAt] datetime2 NOT NULL,
                    [UsedAt] datetime2 NULL,
                    [CreatedAt] datetime2 NOT NULL
                );
                CREATE UNIQUE INDEX [IX_PasswordResetTokens_TokenHash] ON [dbo].[PasswordResetTokens] ([TokenHash]);
                CREATE INDEX [IX_PasswordResetTokens_UserId] ON [dbo].[PasswordResetTokens] ([UserId]);
            END
            IF OBJECT_ID(N'[dbo].[EmailMessages]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[EmailMessages] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EmailMessages] PRIMARY KEY,
                    [To] nvarchar(500) NOT NULL,
                    [Cc] nvarchar(500) NULL,
                    [Subject] nvarchar(300) NOT NULL,
                    [Body] nvarchar(max) NOT NULL,
                    [Status] int NOT NULL,
                    [Attempts] int NOT NULL,
                    [Error] nvarchar(1000) NULL,
                    [AttachmentIds] nvarchar(500) NULL,
                    [RelatedType] nvarchar(30) NULL,
                    [RelatedId] int NULL,
                    [CustomerId] int NULL,
                    [CampaignMemberId] int NULL,
                    [CreatedById] int NULL CONSTRAINT [FK_EmailMessages_Users_CreatedById] REFERENCES [dbo].[Users] ([Id]),
                    [CreatedAt] datetime2 NOT NULL,
                    [SentAt] datetime2 NULL
                );
                CREATE INDEX [IX_EmailMessages_Status_CreatedAt] ON [dbo].[EmailMessages] ([Status], [CreatedAt]);
                CREATE INDEX [IX_EmailMessages_RelatedType_RelatedId] ON [dbo].[EmailMessages] ([RelatedType], [RelatedId]);
                CREATE INDEX [IX_EmailMessages_CustomerId] ON [dbo].[EmailMessages] ([CustomerId]);
                CREATE INDEX [IX_EmailMessages_CreatedById] ON [dbo].[EmailMessages] ([CreatedById]);
            END
            IF OBJECT_ID(N'[dbo].[EmailTemplates]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[EmailTemplates] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EmailTemplates] PRIMARY KEY,
                    [Name] nvarchar(150) NOT NULL,
                    [Code] nvarchar(50) NULL,
                    [Scope] nvarchar(30) NULL,
                    [Subject] nvarchar(300) NOT NULL,
                    [Body] nvarchar(max) NOT NULL,
                    [IsActive] bit NOT NULL,
                    [UpdatedAt] datetime2 NOT NULL
                );
                CREATE INDEX [IX_EmailTemplates_Code] ON [dbo].[EmailTemplates] ([Code]);
            END
            IF OBJECT_ID(N'[dbo].[RolePermissions]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[RolePermissions] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_RolePermissions] PRIMARY KEY,
                    [Role] nvarchar(20) NOT NULL,
                    [Permission] nvarchar(60) NOT NULL,
                    [Allowed] bit NOT NULL
                );
                CREATE UNIQUE INDEX [IX_RolePermissions_Role_Permission] ON [dbo].[RolePermissions] ([Role], [Permission]);
            END
            IF OBJECT_ID(N'[dbo].[RecordShares]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[RecordShares] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_RecordShares] PRIMARY KEY,
                    [EntityType] nvarchar(30) NOT NULL,
                    [EntityId] int NOT NULL,
                    [UserId] int NOT NULL CONSTRAINT [FK_RecordShares_Users_UserId] REFERENCES [dbo].[Users] ([Id]),
                    [CanEdit] bit NOT NULL,
                    [SharedById] int NULL,
                    [CreatedAt] datetime2 NOT NULL
                );
                CREATE UNIQUE INDEX [IX_RecordShares_EntityType_EntityId_UserId] ON [dbo].[RecordShares] ([EntityType], [EntityId], [UserId]);
                CREATE INDEX [IX_RecordShares_UserId] ON [dbo].[RecordShares] ([UserId]);
            END
            """;

        // ---------- Vận hành bán hàng: tự động hoá, bảng giá, nhãn, trường tùy biến, định kỳ ----------
        yield return """
            IF COL_LENGTH(N'dbo.Leads', N'Score') IS NULL
                ALTER TABLE [dbo].[Leads] ADD [Score] int NOT NULL CONSTRAINT [DF_Leads_Score] DEFAULT 0;
            IF COL_LENGTH(N'dbo.Quotes', N'ApprovalStatus') IS NULL
            BEGIN
                ALTER TABLE [dbo].[Quotes] ADD
                    [ApprovalStatus] int NOT NULL CONSTRAINT [DF_Quotes_ApprovalStatus] DEFAULT 0,
                    [EffectiveDiscount] decimal(5,2) NOT NULL CONSTRAINT [DF_Quotes_EffectiveDiscount] DEFAULT 0,
                    [DiscountApprovedById] int NULL CONSTRAINT [FK_Quotes_Users_DiscountApprovedById] REFERENCES [dbo].[Users] ([Id]),
                    [DiscountApprovedAt] datetime2 NULL,
                    [ApprovalNote] nvarchar(500) NULL;
                EXEC(N'CREATE INDEX [IX_Quotes_DiscountApprovedById] ON [dbo].[Quotes] ([DiscountApprovedById])');
            END
            IF COL_LENGTH(N'dbo.Contracts', N'BillingCycle') IS NULL
                ALTER TABLE [dbo].[Contracts] ADD
                    [BillingCycle] int NOT NULL CONSTRAINT [DF_Contracts_BillingCycle] DEFAULT 0,
                    [RecurringAmount] decimal(18,0) NOT NULL CONSTRAINT [DF_Contracts_RecurringAmount] DEFAULT 0,
                    [NextBillingDate] datetime2 NULL,
                    [RenewalNoticeDays] int NOT NULL CONSTRAINT [DF_Contracts_RenewalNoticeDays] DEFAULT 60,
                    [RenewalOpportunityId] int NULL,
                    [RenewedFromId] int NULL;
            IF OBJECT_ID(N'[dbo].[PriceLists]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[PriceLists] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_PriceLists] PRIMARY KEY,
                    [Name] nvarchar(150) NOT NULL,
                    [Description] nvarchar(500) NULL,
                    [ValidFrom] datetime2 NULL,
                    [ValidTo] datetime2 NULL,
                    [IsActive] bit NOT NULL,
                    [UpdatedAt] datetime2 NOT NULL
                );
                CREATE TABLE [dbo].[PriceListItems] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_PriceListItems] PRIMARY KEY,
                    [PriceListId] int NOT NULL CONSTRAINT [FK_PriceListItems_PriceLists_PriceListId] REFERENCES [dbo].[PriceLists] ([Id]),
                    [ProductId] int NOT NULL CONSTRAINT [FK_PriceListItems_Products_ProductId] REFERENCES [dbo].[Products] ([Id]),
                    [Price] decimal(18,0) NOT NULL
                );
                CREATE UNIQUE INDEX [IX_PriceListItems_PriceListId_ProductId] ON [dbo].[PriceListItems] ([PriceListId], [ProductId]);
                CREATE INDEX [IX_PriceListItems_ProductId] ON [dbo].[PriceListItems] ([ProductId]);
            END
            IF COL_LENGTH(N'dbo.Customers', N'PriceListId') IS NULL
            BEGIN
                ALTER TABLE [dbo].[Customers] ADD [PriceListId] int NULL CONSTRAINT [FK_Customers_PriceLists_PriceListId] REFERENCES [dbo].[PriceLists] ([Id]);
                EXEC(N'CREATE INDEX [IX_Customers_PriceListId] ON [dbo].[Customers] ([PriceListId])');
            END
            IF OBJECT_ID(N'[dbo].[AutomationRules]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[AutomationRules] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_AutomationRules] PRIMARY KEY,
                    [Name] nvarchar(150) NOT NULL,
                    [IsActive] bit NOT NULL,
                    [Trigger] int NOT NULL,
                    [Days] int NULL,
                    [Condition] int NULL,
                    [Action] int NOT NULL,
                    [Assignee] int NOT NULL,
                    [AssigneeUserId] int NULL CONSTRAINT [FK_AutomationRules_Users_AssigneeUserId] REFERENCES [dbo].[Users] ([Id]),
                    [Title] nvarchar(200) NULL,
                    [Message] nvarchar(1000) NULL,
                    [TaskDueDays] int NOT NULL,
                    [TaskPriority] int NOT NULL,
                    [EmailTemplateId] int NULL,
                    [RunCount] int NOT NULL,
                    [LastRunAt] datetime2 NULL,
                    [CreatedAt] datetime2 NOT NULL
                );
                CREATE INDEX [IX_AutomationRules_AssigneeUserId] ON [dbo].[AutomationRules] ([AssigneeUserId]);
                CREATE INDEX [IX_AutomationRules_Trigger_IsActive] ON [dbo].[AutomationRules] ([Trigger], [IsActive]);
                CREATE TABLE [dbo].[AutomationLogs] (
                    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_AutomationLogs] PRIMARY KEY,
                    [RuleId] int NOT NULL,
                    [EntityType] nvarchar(30) NOT NULL,
                    [EntityId] int NOT NULL,
                    [Key] nvarchar(60) NOT NULL,
                    [Result] nvarchar(300) NULL,
                    [At] datetime2 NOT NULL
                );
                CREATE UNIQUE INDEX [IX_AutomationLogs_RuleId_EntityType_EntityId_Key] ON [dbo].[AutomationLogs] ([RuleId], [EntityType], [EntityId], [Key]);
            END
            IF OBJECT_ID(N'[dbo].[Tags]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[Tags] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Tags] PRIMARY KEY,
                    [Name] nvarchar(50) NOT NULL,
                    [Color] nvarchar(7) NOT NULL
                );
                CREATE UNIQUE INDEX [IX_Tags_Name] ON [dbo].[Tags] ([Name]);
                CREATE TABLE [dbo].[EntityTags] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EntityTags] PRIMARY KEY,
                    [TagId] int NOT NULL CONSTRAINT [FK_EntityTags_Tags_TagId] REFERENCES [dbo].[Tags] ([Id]),
                    [EntityType] nvarchar(30) NOT NULL,
                    [EntityId] int NOT NULL
                );
                CREATE UNIQUE INDEX [IX_EntityTags_TagId_EntityType_EntityId] ON [dbo].[EntityTags] ([TagId], [EntityType], [EntityId]);
                CREATE INDEX [IX_EntityTags_EntityType_EntityId] ON [dbo].[EntityTags] ([EntityType], [EntityId]);
            END
            IF OBJECT_ID(N'[dbo].[CustomFieldDefs]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[CustomFieldDefs] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_CustomFieldDefs] PRIMARY KEY,
                    [EntityType] nvarchar(30) NOT NULL,
                    [Label] nvarchar(100) NOT NULL,
                    [Type] int NOT NULL,
                    [Options] nvarchar(2000) NULL,
                    [Required] bit NOT NULL,
                    [ShowInList] bit NOT NULL,
                    [SortOrder] int NOT NULL,
                    [IsActive] bit NOT NULL
                );
                CREATE INDEX [IX_CustomFieldDefs_EntityType] ON [dbo].[CustomFieldDefs] ([EntityType]);
                CREATE TABLE [dbo].[CustomFieldValues] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_CustomFieldValues] PRIMARY KEY,
                    [FieldId] int NOT NULL CONSTRAINT [FK_CustomFieldValues_CustomFieldDefs_FieldId] REFERENCES [dbo].[CustomFieldDefs] ([Id]),
                    [EntityId] int NOT NULL,
                    [Value] nvarchar(1000) NULL
                );
                CREATE UNIQUE INDEX [IX_CustomFieldValues_FieldId_EntityId] ON [dbo].[CustomFieldValues] ([FieldId], [EntityId]);
            END
            """;

        // ---------- Chăm sóc khách hàng nâng cao: kho kiến thức, email → phiếu, cổng khách hàng, NPS ----------
        yield return """
            IF COL_LENGTH(N'dbo.Contacts', N'PortalEnabled') IS NULL
                ALTER TABLE [dbo].[Contacts] ADD
                    [PortalEnabled] bit NOT NULL CONSTRAINT [DF_Contacts_PortalEnabled] DEFAULT 0,
                    [PortalPasswordHash] nvarchar(500) NULL,
                    [PortalLastLoginAt] datetime2 NULL;
            IF COL_LENGTH(N'dbo.Tickets', N'ReporterEmail') IS NULL
                ALTER TABLE [dbo].[Tickets] ADD [ReporterEmail] nvarchar(150) NULL;
            IF COL_LENGTH(N'dbo.TicketComments', N'FromCustomer') IS NULL
                ALTER TABLE [dbo].[TicketComments] ADD
                    [FromCustomer] bit NOT NULL CONSTRAINT [DF_TicketComments_FromCustomer] DEFAULT 0,
                    [AuthorName] nvarchar(150) NULL;
            IF OBJECT_ID(N'[dbo].[KbArticles]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[KbArticles] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_KbArticles] PRIMARY KEY,
                    [Title] nvarchar(200) NOT NULL,
                    [Category] nvarchar(100) NULL,
                    [Body] nvarchar(max) NOT NULL,
                    [Keywords] nvarchar(300) NULL,
                    [IsPublic] bit NOT NULL,
                    [IsTemplate] bit NOT NULL,
                    [IsActive] bit NOT NULL,
                    [Views] int NOT NULL,
                    [Helpful] int NOT NULL,
                    [NotHelpful] int NOT NULL,
                    [AuthorId] int NULL CONSTRAINT [FK_KbArticles_Users_AuthorId] REFERENCES [dbo].[Users] ([Id]),
                    [CreatedAt] datetime2 NOT NULL,
                    [UpdatedAt] datetime2 NOT NULL
                );
                CREATE INDEX [IX_KbArticles_AuthorId] ON [dbo].[KbArticles] ([AuthorId]);
                CREATE INDEX [IX_KbArticles_Category] ON [dbo].[KbArticles] ([Category]);
            END
            IF OBJECT_ID(N'[dbo].[InboundMessages]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[InboundMessages] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_InboundMessages] PRIMARY KEY,
                    [MessageKey] nvarchar(300) NOT NULL,
                    [From] nvarchar(200) NULL,
                    [Subject] nvarchar(300) NULL,
                    [TicketId] int NULL,
                    [Result] nvarchar(20) NULL,
                    [ReceivedAt] datetime2 NOT NULL
                );
                CREATE UNIQUE INDEX [IX_InboundMessages_MessageKey] ON [dbo].[InboundMessages] ([MessageKey]);
            END
            IF OBJECT_ID(N'[dbo].[NpsSurveys]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[NpsSurveys] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_NpsSurveys] PRIMARY KEY,
                    [Name] nvarchar(150) NOT NULL,
                    [Question] nvarchar(300) NOT NULL,
                    [Status] int NOT NULL,
                    [CreatedById] int NULL CONSTRAINT [FK_NpsSurveys_Users_CreatedById] REFERENCES [dbo].[Users] ([Id]),
                    [CreatedAt] datetime2 NOT NULL,
                    [SentAt] datetime2 NULL
                );
                CREATE INDEX [IX_NpsSurveys_CreatedById] ON [dbo].[NpsSurveys] ([CreatedById]);
                CREATE TABLE [dbo].[NpsResponses] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_NpsResponses] PRIMARY KEY,
                    [SurveyId] int NOT NULL CONSTRAINT [FK_NpsResponses_NpsSurveys_SurveyId] REFERENCES [dbo].[NpsSurveys] ([Id]),
                    [ContactId] int NULL CONSTRAINT [FK_NpsResponses_Contacts_ContactId] REFERENCES [dbo].[Contacts] ([Id]),
                    [CustomerId] int NULL CONSTRAINT [FK_NpsResponses_Customers_CustomerId] REFERENCES [dbo].[Customers] ([Id]),
                    [Email] nvarchar(150) NOT NULL,
                    [Token] nvarchar(64) NOT NULL,
                    [Score] int NULL,
                    [Comment] nvarchar(2000) NULL,
                    [SentAt] datetime2 NULL,
                    [RespondedAt] datetime2 NULL
                );
                CREATE UNIQUE INDEX [IX_NpsResponses_Token] ON [dbo].[NpsResponses] ([Token]);
                CREATE INDEX [IX_NpsResponses_SurveyId] ON [dbo].[NpsResponses] ([SurveyId]);
                CREATE INDEX [IX_NpsResponses_ContactId] ON [dbo].[NpsResponses] ([ContactId]);
                CREATE INDEX [IX_NpsResponses_CustomerId] ON [dbo].[NpsResponses] ([CustomerId]);
            END
            """;

        // ---------- Kế hoạch bán hàng ----------
        yield return """
            IF OBJECT_ID(N'[dbo].[SalesPlans]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[SalesPlans] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SalesPlans] PRIMARY KEY,
                    [BranchId] int NOT NULL CONSTRAINT [FK_SalesPlans_Branches_BranchId] REFERENCES [dbo].[Branches] ([Id]),
                    [Number] nvarchar(30) NOT NULL,
                    [Title] nvarchar(200) NOT NULL,
                    [Year] int NOT NULL,
                    [FromMonth] int NOT NULL,
                    [ToMonth] int NOT NULL,
                    [Status] int NOT NULL,
                    [Objectives] nvarchar(4000) NULL,
                    [CreatedById] int NULL CONSTRAINT [FK_SalesPlans_Users_CreatedById] REFERENCES [dbo].[Users] ([Id]),
                    [CreatedAt] datetime2 NOT NULL,
                    [UpdatedAt] datetime2 NULL,
                    [SubmittedAt] datetime2 NULL,
                    [ApprovedById] int NULL CONSTRAINT [FK_SalesPlans_Users_ApprovedById] REFERENCES [dbo].[Users] ([Id]),
                    [ApprovedAt] datetime2 NULL,
                    [ApprovalNote] nvarchar(1000) NULL,
                    [KpiAppliedAt] datetime2 NULL
                );
                CREATE UNIQUE INDEX [IX_SalesPlans_Number] ON [dbo].[SalesPlans] ([Number]);
                CREATE INDEX [IX_SalesPlans_Year_Status] ON [dbo].[SalesPlans] ([Year], [Status]);
                CREATE INDEX [IX_SalesPlans_BranchId] ON [dbo].[SalesPlans] ([BranchId]);
                CREATE INDEX [IX_SalesPlans_CreatedById] ON [dbo].[SalesPlans] ([CreatedById]);
                CREATE INDEX [IX_SalesPlans_ApprovedById] ON [dbo].[SalesPlans] ([ApprovedById]);
            END
            IF OBJECT_ID(N'[dbo].[SalesPlanMonths]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[SalesPlanMonths] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SalesPlanMonths] PRIMARY KEY,
                    [SalesPlanId] int NOT NULL CONSTRAINT [FK_SalesPlanMonths_SalesPlans_SalesPlanId] REFERENCES [dbo].[SalesPlans] ([Id]),
                    [Month] int NOT NULL,
                    [Revenue] decimal(18,0) NOT NULL,
                    [Collection] decimal(18,0) NOT NULL,
                    [Contracts] int NOT NULL,
                    [NewCustomers] int NOT NULL
                );
                CREATE UNIQUE INDEX [IX_SalesPlanMonths_SalesPlanId_Month] ON [dbo].[SalesPlanMonths] ([SalesPlanId], [Month]);
            END
            IF OBJECT_ID(N'[dbo].[SalesPlanProducts]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[SalesPlanProducts] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SalesPlanProducts] PRIMARY KEY,
                    [SalesPlanId] int NOT NULL CONSTRAINT [FK_SalesPlanProducts_SalesPlans_SalesPlanId] REFERENCES [dbo].[SalesPlans] ([Id]),
                    [Category] nvarchar(100) NOT NULL,
                    [Revenue] decimal(18,0) NOT NULL,
                    [Note] nvarchar(300) NULL,
                    [SortOrder] int NOT NULL
                );
                CREATE INDEX [IX_SalesPlanProducts_SalesPlanId] ON [dbo].[SalesPlanProducts] ([SalesPlanId]);
            END
            IF OBJECT_ID(N'[dbo].[SalesPlanMembers]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[SalesPlanMembers] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SalesPlanMembers] PRIMARY KEY,
                    [SalesPlanId] int NOT NULL CONSTRAINT [FK_SalesPlanMembers_SalesPlans_SalesPlanId] REFERENCES [dbo].[SalesPlans] ([Id]),
                    [UserId] int NOT NULL CONSTRAINT [FK_SalesPlanMembers_Users_UserId] REFERENCES [dbo].[Users] ([Id]),
                    [SharePercent] decimal(5,2) NOT NULL
                );
                CREATE UNIQUE INDEX [IX_SalesPlanMembers_SalesPlanId_UserId] ON [dbo].[SalesPlanMembers] ([SalesPlanId], [UserId]);
                CREATE INDEX [IX_SalesPlanMembers_UserId] ON [dbo].[SalesPlanMembers] ([UserId]);
            END
            IF OBJECT_ID(N'[dbo].[SalesPlanActions]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[SalesPlanActions] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SalesPlanActions] PRIMARY KEY,
                    [SalesPlanId] int NOT NULL CONSTRAINT [FK_SalesPlanActions_SalesPlans_SalesPlanId] REFERENCES [dbo].[SalesPlans] ([Id]),
                    [Title] nvarchar(200) NOT NULL,
                    [Description] nvarchar(1000) NULL,
                    [OwnerId] int NULL CONSTRAINT [FK_SalesPlanActions_Users_OwnerId] REFERENCES [dbo].[Users] ([Id]),
                    [StartDate] datetime2 NULL,
                    [DueDate] datetime2 NULL,
                    [Budget] decimal(18,0) NOT NULL,
                    [ActivityId] int NULL,
                    [DoneAt] datetime2 NULL,
                    [SortOrder] int NOT NULL
                );
                CREATE INDEX [IX_SalesPlanActions_SalesPlanId] ON [dbo].[SalesPlanActions] ([SalesPlanId]);
                CREATE INDEX [IX_SalesPlanActions_OwnerId] ON [dbo].[SalesPlanActions] ([OwnerId]);
            END
            """;
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

        // ---------- Chỉ tiêu KPI bán hàng ----------
        """
        IF OBJECT_ID(N'[dbo].[SalesTargets]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[SalesTargets] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SalesTargets] PRIMARY KEY,
                [UserId] int NOT NULL CONSTRAINT [FK_SalesTargets_Users_UserId] REFERENCES [dbo].[Users] ([Id]),
                [Year] int NOT NULL,
                [Month] int NOT NULL,
                [Revenue] decimal(18,0) NOT NULL,
                [Collection] decimal(18,0) NOT NULL,
                [Contracts] int NOT NULL,
                [NewCustomers] int NOT NULL,
                [Activities] int NOT NULL,
                [UpdatedById] int NULL,
                [UpdatedAt] datetime2 NOT NULL
            );
            CREATE UNIQUE INDEX [IX_SalesTargets_UserId_Year_Month] ON [dbo].[SalesTargets] ([UserId], [Year], [Month]);
        END
        """,

        // ---------- Tiếp thị khách hàng: chiến dịch + đối tượng ----------
        """
        IF OBJECT_ID(N'[dbo].[Campaigns]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[Campaigns] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Campaigns] PRIMARY KEY,
                [Code] nvarchar(30) NOT NULL,
                [Name] nvarchar(200) NOT NULL,
                [Channel] int NOT NULL,
                [Status] int NOT NULL,
                [StartDate] datetime2 NOT NULL,
                [EndDate] datetime2 NULL,
                [Budget] decimal(18,0) NOT NULL,
                [ActualCost] decimal(18,0) NOT NULL,
                [ExpectedRevenue] decimal(18,0) NOT NULL,
                [TargetLeads] int NULL,
                [Objective] nvarchar(1000) NULL,
                [Message] nvarchar(4000) NULL,
                [OwnerId] int NULL CONSTRAINT [FK_Campaigns_Users_OwnerId] REFERENCES [dbo].[Users] ([Id]),
                [CreatedAt] datetime2 NOT NULL,
                [UpdatedAt] datetime2 NULL
            );
            CREATE UNIQUE INDEX [IX_Campaigns_Code] ON [dbo].[Campaigns] ([Code]);
            CREATE INDEX [IX_Campaigns_Status] ON [dbo].[Campaigns] ([Status]);
            CREATE INDEX [IX_Campaigns_OwnerId] ON [dbo].[Campaigns] ([OwnerId]);
        END
        """,
        """
        IF OBJECT_ID(N'[dbo].[CampaignMembers]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[CampaignMembers] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_CampaignMembers] PRIMARY KEY,
                [CampaignId] int NOT NULL CONSTRAINT [FK_CampaignMembers_Campaigns_CampaignId] REFERENCES [dbo].[Campaigns] ([Id]),
                [CustomerId] int NULL CONSTRAINT [FK_CampaignMembers_Customers_CustomerId] REFERENCES [dbo].[Customers] ([Id]),
                [ContactId] int NULL CONSTRAINT [FK_CampaignMembers_Contacts_ContactId] REFERENCES [dbo].[Contacts] ([Id]),
                [LeadId] int NULL CONSTRAINT [FK_CampaignMembers_Leads_LeadId] REFERENCES [dbo].[Leads] ([Id]),
                [FullName] nvarchar(150) NOT NULL,
                [Company] nvarchar(200) NULL,
                [Email] nvarchar(150) NULL,
                [Phone] nvarchar(20) NULL,
                [Status] int NOT NULL,
                [LastContactAt] datetime2 NULL,
                [Note] nvarchar(500) NULL,
                [AddedAt] datetime2 NOT NULL
            );
            CREATE INDEX [IX_CampaignMembers_CampaignId_Status] ON [dbo].[CampaignMembers] ([CampaignId], [Status]);
            CREATE INDEX [IX_CampaignMembers_CustomerId] ON [dbo].[CampaignMembers] ([CustomerId]);
            CREATE INDEX [IX_CampaignMembers_ContactId] ON [dbo].[CampaignMembers] ([ContactId]);
            CREATE INDEX [IX_CampaignMembers_LeadId] ON [dbo].[CampaignMembers] ([LeadId]);
        END
        """,
        // Gắn lead / cơ hội với chiến dịch mang lại (đo hiệu quả, ROI)
        """
        IF COL_LENGTH(N'dbo.Leads', N'CampaignId') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Leads] ADD [CampaignId] int NULL
                CONSTRAINT [FK_Leads_Campaigns_CampaignId] REFERENCES [dbo].[Campaigns] ([Id]);
            EXEC(N'CREATE INDEX [IX_Leads_CampaignId] ON [dbo].[Leads] ([CampaignId])');
        END
        """,
        """
        IF COL_LENGTH(N'dbo.Opportunities', N'CampaignId') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Opportunities] ADD [CampaignId] int NULL
                CONSTRAINT [FK_Opportunities_Campaigns_CampaignId] REFERENCES [dbo].[Campaigns] ([Id]);
            EXEC(N'CREATE INDEX [IX_Opportunities_CampaignId] ON [dbo].[Opportunities] ([CampaignId])');
        END
        """,

        // ---------- Chăm sóc khách hàng: phiếu hỗ trợ ----------
        """
        IF OBJECT_ID(N'[dbo].[Tickets]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[Tickets] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Tickets] PRIMARY KEY,
                [Number] nvarchar(30) NOT NULL,
                [Subject] nvarchar(200) NOT NULL,
                [Description] nvarchar(4000) NULL,
                [CustomerId] int NOT NULL CONSTRAINT [FK_Tickets_Customers_CustomerId] REFERENCES [dbo].[Customers] ([Id]),
                [ContactId] int NULL CONSTRAINT [FK_Tickets_Contacts_ContactId] REFERENCES [dbo].[Contacts] ([Id]),
                [ContractId] int NULL CONSTRAINT [FK_Tickets_Contracts_ContractId] REFERENCES [dbo].[Contracts] ([Id]),
                [ProductId] int NULL CONSTRAINT [FK_Tickets_Products_ProductId] REFERENCES [dbo].[Products] ([Id]),
                [Channel] int NOT NULL,
                [Category] nvarchar(200) NULL,
                [Priority] int NOT NULL,
                [Status] int NOT NULL,
                [OwnerId] int NULL CONSTRAINT [FK_Tickets_Users_OwnerId] REFERENCES [dbo].[Users] ([Id]),
                [CreatedById] int NULL CONSTRAINT [FK_Tickets_Users_CreatedById] REFERENCES [dbo].[Users] ([Id]),
                [CreatedAt] datetime2 NOT NULL,
                [UpdatedAt] datetime2 NULL,
                [ResponseDueAt] datetime2 NOT NULL,
                [ResolveDueAt] datetime2 NOT NULL,
                [FirstResponseAt] datetime2 NULL,
                [ResolvedAt] datetime2 NULL,
                [ClosedAt] datetime2 NULL,
                [Resolution] nvarchar(2000) NULL,
                [SatisfactionScore] int NULL,
                [SatisfactionComment] nvarchar(500) NULL
            );
            CREATE UNIQUE INDEX [IX_Tickets_Number] ON [dbo].[Tickets] ([Number]);
            CREATE INDEX [IX_Tickets_Status_OwnerId] ON [dbo].[Tickets] ([Status], [OwnerId]);
            CREATE INDEX [IX_Tickets_CustomerId] ON [dbo].[Tickets] ([CustomerId]);
            CREATE INDEX [IX_Tickets_ContactId] ON [dbo].[Tickets] ([ContactId]);
            CREATE INDEX [IX_Tickets_ContractId] ON [dbo].[Tickets] ([ContractId]);
            CREATE INDEX [IX_Tickets_ProductId] ON [dbo].[Tickets] ([ProductId]);
            CREATE INDEX [IX_Tickets_OwnerId] ON [dbo].[Tickets] ([OwnerId]);
            CREATE INDEX [IX_Tickets_CreatedById] ON [dbo].[Tickets] ([CreatedById]);
        END
        """,
        """
        IF OBJECT_ID(N'[dbo].[TicketComments]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[TicketComments] (
                [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TicketComments] PRIMARY KEY,
                [TicketId] int NOT NULL CONSTRAINT [FK_TicketComments_Tickets_TicketId] REFERENCES [dbo].[Tickets] ([Id]),
                [Body] nvarchar(4000) NOT NULL,
                [IsInternal] bit NOT NULL,
                [IsSystem] bit NOT NULL,
                [AuthorId] int NULL CONSTRAINT [FK_TicketComments_Users_AuthorId] REFERENCES [dbo].[Users] ([Id]),
                [CreatedAt] datetime2 NOT NULL
            );
            CREATE INDEX [IX_TicketComments_TicketId] ON [dbo].[TicketComments] ([TicketId]);
            CREATE INDEX [IX_TicketComments_AuthorId] ON [dbo].[TicketComments] ([AuthorId]);
        END
        """,
        """
        IF COL_LENGTH(N'dbo.Activities', N'TicketId') IS NULL
        BEGIN
            ALTER TABLE [dbo].[Activities] ADD
                [TicketId] int NULL CONSTRAINT [FK_Activities_Tickets_TicketId] REFERENCES [dbo].[Tickets] ([Id]),
                [IsTicketMain] bit NOT NULL CONSTRAINT [DF_Activities_IsTicketMain] DEFAULT 0;
            EXEC(N'CREATE INDEX [IX_Activities_TicketId] ON [dbo].[Activities] ([TicketId])');
        END
        """,
    };
}
