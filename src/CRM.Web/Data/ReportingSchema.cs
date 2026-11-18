using Microsoft.EntityFrameworkCore;

namespace CRM.Web.Data;

/// <summary>
/// Hạ tầng báo cáo động trong database (chạy mỗi lần khởi động, an toàn khi chạy lại):
///  - Bảng CustomReports (định nghĩa báo cáo động).
///  - Người dùng database CHỈ ĐỌC crm_report_reader (không có login): báo cáo động chạy dưới quyền người dùng này
///    nên không thể sửa / xóa dữ liệu, không đọc được cột mật khẩu, không truy cập database khác.
///  - Các View nghiệp vụ (vw_*) với tên trạng thái tiếng Việt, giúp viết báo cáo dễ hơn.
/// </summary>
public static class ReportingSchema
{
    public const string ReaderUser = "crm_report_reader";

    public static async Task EnsureAsync(CrmDbContext db, ILogger? log = null)
    {
        await db.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID(N'[dbo].[CustomReports]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[CustomReports] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_CustomReports] PRIMARY KEY,
                    [Name] nvarchar(200) NOT NULL,
                    [Category] nvarchar(100) NULL,
                    [Description] nvarchar(1000) NULL,
                    [SourceType] int NOT NULL,
                    [SqlText] nvarchar(max) NULL,
                    [ViewName] nvarchar(128) NULL,
                    [ViewColumns] nvarchar(max) NULL,
                    [DateColumn] nvarchar(128) NULL,
                    [OwnerColumn] nvarchar(128) NULL,
                    [OrderBy] nvarchar(128) NULL,
                    [OrderDesc] bit NOT NULL,
                    [TopRows] int NULL,
                    [ChartLabelColumn] nvarchar(128) NULL,
                    [ChartValueColumn] nvarchar(128) NULL,
                    [AllowedRoles] nvarchar(100) NOT NULL,
                    [IsActive] bit NOT NULL,
                    [CreatedById] int NULL,
                    [CreatedAt] datetime2 NOT NULL,
                    [UpdatedAt] datetime2 NULL
                );
            END
            """);

        // Người dùng chỉ đọc. Cần quyền db_owner trên CRM_DB (có ở cả 2 phương án triển khai); thiếu quyền thì bỏ qua và ghi log.
        try
        {
            await db.Database.ExecuteSqlRawAsync($"""
                IF USER_ID(N'{ReaderUser}') IS NULL CREATE USER [{ReaderUser}] WITHOUT LOGIN;
                IF IS_ROLEMEMBER(N'db_datareader', N'{ReaderUser}') = 0 ALTER ROLE [db_datareader] ADD MEMBER [{ReaderUser}];
                DENY SELECT ON [dbo].[Users] ([PasswordHash]) TO [{ReaderUser}];
                """);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Không tạo được người dùng chỉ đọc {User} cho báo cáo động – báo cáo động sẽ không chạy được.", ReaderUser);
        }

        foreach (var sql in Views) await db.Database.ExecuteSqlRawAsync(sql);
    }

    // CREATE OR ALTER VIEW phải là câu lệnh duy nhất trong batch → mỗi view một lệnh riêng
    private static readonly string[] Views =
    {
        """
        CREATE OR ALTER VIEW [dbo].[vw_Opportunities] AS
        SELECT o.Id, o.Name, c.Code AS CustomerCode, c.Name AS CustomerName, c.Industry, c.City,
               o.Stage, CASE o.Stage WHEN 10 THEN N'Tìm hiểu nhu cầu' WHEN 25 THEN N'Đánh giá' WHEN 50 THEN N'Gửi đề xuất / báo giá'
                                     WHEN 75 THEN N'Đàm phán' WHEN 100 THEN N'Thắng' WHEN 0 THEN N'Thua' END AS StageName,
               CAST(CASE WHEN o.Stage IN (0, 100) THEN 0 ELSE 1 END AS bit) AS IsOpen,
               o.Amount, o.Probability, CAST(o.Amount * o.Probability / 100 AS decimal(18,0)) AS WeightedAmount,
               o.ExpectedCloseDate, o.ClosedAt, o.LostReason,
               CASE o.Source WHEN 1 THEN N'Website' WHEN 2 THEN N'Giới thiệu' WHEN 3 THEN N'Sự kiện / Hội thảo' WHEN 4 THEN N'Gọi điện chủ động'
                             WHEN 5 THEN N'Mạng xã hội' WHEN 6 THEN N'Email marketing' WHEN 9 THEN N'Khác' END AS SourceName,
               o.CampaignId, o.OwnerId, u.FullName AS OwnerName, o.CreatedAt
        FROM dbo.Opportunities o
        LEFT JOIN dbo.Customers c ON c.Id = o.CustomerId
        LEFT JOIN dbo.Users u ON u.Id = o.OwnerId
        """,
        """
        CREATE OR ALTER VIEW [dbo].[vw_Leads] AS
        SELECT l.Id, l.FullName, l.Company, l.JobTitle, l.Email, l.Phone,
               CASE l.Source WHEN 1 THEN N'Website' WHEN 2 THEN N'Giới thiệu' WHEN 3 THEN N'Sự kiện / Hội thảo' WHEN 4 THEN N'Gọi điện chủ động'
                             WHEN 5 THEN N'Mạng xã hội' WHEN 6 THEN N'Email marketing' WHEN 9 THEN N'Khác' END AS SourceName,
               CASE l.Status WHEN 1 THEN N'Mới' WHEN 2 THEN N'Đã liên hệ' WHEN 3 THEN N'Đủ điều kiện' WHEN 4 THEN N'Không phù hợp' WHEN 5 THEN N'Đã chuyển đổi' END AS StatusName,
               CASE l.Rating WHEN 1 THEN N'Nóng' WHEN 2 THEN N'Ấm' WHEN 3 THEN N'Lạnh' END AS RatingName,
               l.EstimatedValue, l.ConvertedAt, l.CampaignId, l.OwnerId, u.FullName AS OwnerName, l.CreatedAt
        FROM dbo.Leads l
        LEFT JOIN dbo.Users u ON u.Id = l.OwnerId
        """,
        """
        CREATE OR ALTER VIEW [dbo].[vw_Customers] AS
        SELECT c.Id, c.Code, c.Name,
               CASE c.Type WHEN 1 THEN N'Tiềm năng' WHEN 2 THEN N'Khách hàng' WHEN 3 THEN N'Đối tác' WHEN 4 THEN N'Ngừng giao dịch' END AS TypeName,
               c.Industry, c.City, c.TaxCode, c.Phone, c.Email, c.EmployeeCount,
               ISNULL((SELECT SUM(o.Amount) FROM dbo.Opportunities o WHERE o.CustomerId = c.Id AND o.Stage = 100), 0) AS WonRevenue,
               ISNULL((SELECT SUM(o.Amount) FROM dbo.Opportunities o WHERE o.CustomerId = c.Id AND o.Stage NOT IN (0, 100)), 0) AS OpenPipeline,
               ISNULL((SELECT SUM(i.Total - i.PaidAmount) FROM dbo.Invoices i WHERE i.CustomerId = c.Id AND i.Status IN (2, 3)), 0) AS Outstanding,
               c.OwnerId, u.FullName AS OwnerName, c.CreatedAt
        FROM dbo.Customers c
        LEFT JOIN dbo.Users u ON u.Id = c.OwnerId
        """,
        """
        CREATE OR ALTER VIEW [dbo].[vw_Activities] AS
        SELECT a.Id, CASE a.Type WHEN 1 THEN N'Cuộc gọi' WHEN 2 THEN N'Cuộc họp' WHEN 3 THEN N'Email' WHEN 4 THEN N'Công việc' END AS TypeName,
               a.Subject, a.DueDate,
               CASE a.Status WHEN 1 THEN N'Kế hoạch' WHEN 2 THEN N'Hoàn thành' WHEN 3 THEN N'Đã hủy' END AS StatusName,
               CAST(CASE WHEN a.Status = 1 AND a.DueDate < GETDATE() THEN 1 ELSE 0 END AS bit) AS IsOverdue,
               a.CompletedAt, c.Name AS CustomerName, l.FullName AS LeadName, o.Name AS OpportunityName,
               a.OwnerId, u.FullName AS OwnerName, a.CreatedAt
        FROM dbo.Activities a
        LEFT JOIN dbo.Customers c ON c.Id = a.CustomerId
        LEFT JOIN dbo.Leads l ON l.Id = a.LeadId
        LEFT JOIN dbo.Opportunities o ON o.Id = a.OpportunityId
        LEFT JOIN dbo.Users u ON u.Id = a.OwnerId
        """,
        """
        CREATE OR ALTER VIEW [dbo].[vw_Contracts] AS
        SELECT k.Id, k.Number, k.Title, c.Name AS CustomerName, k.SignDate, k.StartDate, k.EndDate,
               CASE k.Status WHEN 1 THEN N'Nháp' WHEN 2 THEN N'Đang thực hiện' WHEN 3 THEN N'Hoàn thành' WHEN 4 THEN N'Đã thanh lý' WHEN 5 THEN N'Đã hủy' END AS StatusName,
               k.SubTotal, k.VatAmount, k.Total,
               ISNULL((SELECT SUM(i.Total) FROM dbo.Invoices i WHERE i.ContractId = k.Id AND i.Status IN (2, 3, 4)), 0) AS Invoiced,
               ISNULL((SELECT SUM(p.Amount) FROM dbo.Payments p WHERE p.ContractId = k.Id), 0) AS Paid,
               k.Total - ISNULL((SELECT SUM(p.Amount) FROM dbo.Payments p WHERE p.ContractId = k.Id), 0) AS Receivable,
               k.OwnerId, u.FullName AS OwnerName, k.CreatedAt
        FROM dbo.Contracts k
        LEFT JOIN dbo.Customers c ON c.Id = k.CustomerId
        LEFT JOIN dbo.Users u ON u.Id = k.OwnerId
        """,
        """
        CREATE OR ALTER VIEW [dbo].[vw_Invoices] AS
        SELECT i.Id, i.Symbol, i.Number, i.InvoiceDate, i.DueDate, c.Name AS CustomerName, k.Number AS ContractNumber,
               CASE i.Status WHEN 1 THEN N'Nháp' WHEN 2 THEN N'Đã phát hành' WHEN 3 THEN N'Thanh toán một phần' WHEN 4 THEN N'Đã thanh toán' WHEN 5 THEN N'Đã hủy' END AS StatusName,
               i.SubTotal, i.VatAmount, i.Total, i.PaidAmount,
               CASE WHEN i.Status IN (2, 3) THEN i.Total - i.PaidAmount ELSE 0 END AS Outstanding,
               CASE WHEN i.Status IN (2, 3) AND i.DueDate < CAST(GETDATE() AS date) THEN DATEDIFF(day, i.DueDate, CAST(GETDATE() AS date)) ELSE 0 END AS DaysOverdue,
               i.OwnerId, u.FullName AS OwnerName, i.CreatedAt
        FROM dbo.Invoices i
        LEFT JOIN dbo.Customers c ON c.Id = i.CustomerId
        LEFT JOIN dbo.Contracts k ON k.Id = i.ContractId
        LEFT JOIN dbo.Users u ON u.Id = i.OwnerId
        """,
        """
        CREATE OR ALTER VIEW [dbo].[vw_Payments] AS
        SELECT p.Id, p.Number, p.PaymentDate, c.Name AS CustomerName, i.Symbol + N'-' + i.Number AS InvoiceNo, k.Number AS ContractNumber,
               p.Amount, p.Method, p.Reference,
               COALESCE(k.OwnerId, i.OwnerId, p.OwnerId) AS OwnerId, u.FullName AS OwnerName, p.CreatedAt
        FROM dbo.Payments p
        LEFT JOIN dbo.Customers c ON c.Id = p.CustomerId
        LEFT JOIN dbo.Invoices i ON i.Id = p.InvoiceId
        LEFT JOIN dbo.Contracts k ON k.Id = p.ContractId
        LEFT JOIN dbo.Users u ON u.Id = COALESCE(k.OwnerId, i.OwnerId, p.OwnerId)
        """,
    };

    /// <summary>Báo cáo động mẫu (chỉ nạp khi bảng còn trống) – minh hoạ cả 2 cách: SQL và View</summary>
    public static async Task SeedSamplesAsync(CrmDbContext db)
    {
        if (await db.CustomReports.AnyAsync()) return;
        db.CustomReports.AddRange(
            new Models.Entities.CustomReport
            {
                Name = "Doanh số theo tỉnh / thành phố", Category = "Mẫu SQL", SourceType = Models.Entities.CustomReportSource.Sql,
                Description = "Ví dụ câu lệnh SQL có tham số kỳ báo cáo (@FromDate, @ToDate) và phân quyền (@SeesAll, @CurrentUserId).",
                SqlText = """
                    SELECT ISNULL(o.City, N'(Chưa rõ)')      AS [Tỉnh / Thành phố],
                           COUNT(*)                          AS [Số cơ hội thắng],
                           SUM(o.Amount)                     AS [Doanh số],
                           COUNT(DISTINCT o.CustomerName)    AS [Số khách hàng]
                    FROM vw_Opportunities o
                    WHERE o.Stage = 100
                      AND o.ClosedAt >= @FromDate AND o.ClosedAt < DATEADD(day, 1, @ToDate)
                      AND (@SeesAll = 1 OR o.OwnerId = @CurrentUserId)
                    GROUP BY o.City
                    ORDER BY [Doanh số] DESC
                    """,
                ChartLabelColumn = "Tỉnh / Thành phố", ChartValueColumn = "Doanh số", AllowedRoles = "Admin,Manager,Sales"
            },
            new Models.Entities.CustomReport
            {
                Name = "Hoá đơn quá hạn cần đòi", Category = "Mẫu SQL", SourceType = Models.Entities.CustomReportSource.Sql,
                Description = "Hoá đơn còn nợ đã quá hạn thanh toán, sắp xếp theo số ngày quá hạn. Có ô tìm kiếm @Keyword theo tên khách hàng.",
                SqlText = """
                    SELECT i.Symbol + N' - ' + i.Number AS [Hoá đơn], i.CustomerName AS [Khách hàng], i.InvoiceDate AS [Ngày HĐ],
                           i.DueDate AS [Hạn thanh toán], i.DaysOverdue AS [Số ngày quá hạn], i.Outstanding AS [Còn nợ], i.OwnerName AS [Phụ trách]
                    FROM vw_Invoices i
                    WHERE i.DaysOverdue > 0
                      AND (@Keyword IS NULL OR i.CustomerName LIKE N'%' + @Keyword + N'%')
                      AND (@SeesAll = 1 OR i.OwnerId = @CurrentUserId)
                    ORDER BY i.DaysOverdue DESC
                    """,
                ChartLabelColumn = "Khách hàng", ChartValueColumn = "Còn nợ", AllowedRoles = "Admin,Manager,Sales"
            },
            new Models.Entities.CustomReport
            {
                Name = "Cơ hội đang mở (từ View)", Category = "Mẫu View", SourceType = Models.Entities.CustomReportSource.View,
                Description = "Ví dụ báo cáo chọn cột từ View vw_Opportunities, không cần viết SQL; tự lọc theo người phụ trách.",
                ViewName = "vw_Opportunities", ViewColumns = "Name,CustomerName,StageName,Amount,WeightedAmount,ExpectedCloseDate,OwnerName,IsOpen",
                OwnerColumn = "OwnerId", OrderBy = "Amount", OrderDesc = true, TopRows = 200,
                ChartLabelColumn = "StageName", ChartValueColumn = "Amount", AllowedRoles = "Admin,Manager,Sales"
            });
        await db.SaveChangesAsync();
    }
}
