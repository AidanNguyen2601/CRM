using System.Data;
using System.Security.Claims;
using System.Text.RegularExpressions;
using CRM.Web.Data;
using CRM.Web.Models.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static CRM.Web.Infrastructure.I18n;

namespace CRM.Web.Infrastructure.Reports;

/// <summary>Bảng / View và các cột – dùng cho trình soạn báo cáo động</summary>
public record DbObjectInfo(string Name, bool IsView, List<string> Columns, Dictionary<string, string>? Types = null);

/// <summary>
/// Chạy báo cáo động. Lớp bảo vệ:
///  1. Chỉ quản trị được tạo / sửa báo cáo.
///  2. Kiểm tra câu lệnh: một câu SELECT / WITH duy nhất, không chứa lệnh ghi / quản trị.
///  3. QUAN TRỌNG NHẤT: luôn chạy với EXECUTE AS USER = crm_report_reader (chỉ thuộc db_datareader, bị cấm đọc cột mật khẩu)
///     trên kết nối riêng không dùng pool → dù câu lệnh có lọt kiểm tra, SQL Server cũng từ chối mọi thao tác ghi.
///  4. Giới hạn thời gian chạy và số dòng trả về.
/// Tham số dùng được trong câu lệnh: @FromDate, @ToDate, @Year, @OwnerId, @Keyword, @CurrentUserId, @SeesAll,
/// @BranchIds (danh sách Id đơn vị trong phạm vi đang xem, phân cách dấu phẩy; rỗng = toàn chuỗi).
/// </summary>
public class CustomReportService
{
    public const int DisplayRows = 5000, ExportRows = 50000, TimeoutSeconds = 30;

    public static readonly string[] Parameters = { "@FromDate", "@ToDate", "@Year", "@OwnerId", "@Keyword", "@CurrentUserId", "@SeesAll", "@BranchIds" };

    private static readonly string[] Forbidden =
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "DROP", "ALTER", "CREATE", "TRUNCATE", "EXEC", "EXECUTE", "GRANT", "REVOKE", "DENY",
        "BACKUP", "RESTORE", "SHUTDOWN", "DBCC", "KILL", "RECONFIGURE", "BULK", "OPENROWSET", "OPENQUERY", "OPENDATASOURCE",
        "WAITFOR", "USE", "REVERT", "SETUSER", "INTO", "DECLARE", "SET"
    };

    private readonly CrmDbContext _db;
    private readonly BranchScope _scope;
    public CustomReportService(CrmDbContext db, BranchScope scope) { _db = db; _scope = scope; }

    /// <summary>Điều kiện lọc theo đơn vị cho cột BranchId (dùng trong báo cáo dạng View)</summary>
    public const string BranchCondition = "(@BranchIds = N'' OR [BranchId] IN (SELECT TRY_CAST([value] AS int) FROM STRING_SPLIT(@BranchIds, N',')))";

    /// <summary>Kiểm tra câu lệnh SQL; trả về thông báo lỗi hoặc null nếu hợp lệ</summary>
    public static string? Validate(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return T("Chưa nhập câu lệnh SQL.");
        if (sql.Length > 20000) return T("Câu lệnh quá dài (tối đa 20.000 ký tự).");
        var code = StripLiteralsAndComments(sql).Trim().TrimEnd(';').Trim();
        if (!Regex.IsMatch(code, @"^(SELECT|WITH)\b", RegexOptions.IgnoreCase))
            return T("Báo cáo động chỉ cho phép câu lệnh đọc dữ liệu bắt đầu bằng SELECT hoặc WITH.");
        if (code.Contains(';')) return T("Chỉ được dùng MỘT câu lệnh (không dùng dấu chấm phẩy giữa câu).");
        foreach (var k in Forbidden)
            if (Regex.IsMatch(code, $@"(?<![\w@#\[]){k}(?![\w\]])", RegexOptions.IgnoreCase))
                return T("Câu lệnh chứa từ khoá không được phép: {0}. Báo cáo động chỉ được đọc dữ liệu.", k);
        if (Regex.IsMatch(code, @"\b(xp_|sp_)\w*", RegexOptions.IgnoreCase))
            return T("Không được gọi thủ tục hệ thống (xp_ / sp_).");
        // Tên 3 phần db.schema.bảng (hoặc db..bảng) = truy vấn sang database khác
        if (Regex.IsMatch(code, @"(?:\w+|\[x\])\s*\.\s*(?:\w+|\[x\])?\s*\.\s*(?:\w+|\[x\])", RegexOptions.IgnoreCase))
            return T("Báo cáo động chỉ được truy vấn dữ liệu của CRM (không dùng tên database.schema.bảng).");
        return null;
    }

    /// <summary>Bỏ chuỗi '...', [tên], chú thích -- và /* */ để kiểm tra từ khoá không bị nhầm</summary>
    private static string StripLiteralsAndComments(string sql)
    {
        var s = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"--[^\r\n]*", " ");
        s = Regex.Replace(s, @"N?'(?:[^']|'')*'", "''");
        s = Regex.Replace(s, @"\[(?:[^\]]|\]\])*\]", "[x]");
        return s;
    }

    /// <summary>Danh sách View và bảng (schema dbo) cùng các cột – ẩn cột mật khẩu</summary>
    public async Task<List<DbObjectInfo>> SchemaAsync()
    {
        var rows = await _db.Database.SqlQueryRaw<SchemaRow>("""
            SELECT t.TABLE_NAME AS ObjectName, CAST(CASE WHEN t.TABLE_TYPE = 'VIEW' THEN 1 ELSE 0 END AS bit) AS IsView,
                   c.COLUMN_NAME AS ColumnName, c.ORDINAL_POSITION AS Ordinal, c.DATA_TYPE AS DataType
            FROM INFORMATION_SCHEMA.TABLES t
            JOIN INFORMATION_SCHEMA.COLUMNS c ON c.TABLE_SCHEMA = t.TABLE_SCHEMA AND c.TABLE_NAME = t.TABLE_NAME
            WHERE t.TABLE_SCHEMA = 'dbo' AND t.TABLE_NAME NOT LIKE '[_][_]%'
            """).ToListAsync();
        return rows.Where(r => !(r.ObjectName == "Users" && r.ColumnName == "PasswordHash"))
            .GroupBy(r => (r.ObjectName, r.IsView))
            .Select(g => new DbObjectInfo(g.Key.ObjectName, g.Key.IsView, g.OrderBy(r => r.Ordinal).Select(r => r.ColumnName).ToList(),
                g.ToDictionary(r => r.ColumnName, r => r.DataType, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(o => o.IsView).ThenBy(o => o.Name).ToList();
    }

    private sealed class SchemaRow
    {
        public string ObjectName { get; set; } = "";
        public bool IsView { get; set; }
        public string ColumnName { get; set; } = "";
        public int Ordinal { get; set; }
        public string DataType { get; set; } = "";
    }

    /// <summary>Dựng câu lệnh SELECT cho báo cáo dạng View (tên View / cột được đối chiếu với danh sách thật, không ghép chuỗi tự do)</summary>
    public async Task<(string? Sql, string? Error)> BuildViewSqlAsync(CustomReport r)
    {
        var schema = await SchemaAsync();
        var view = schema.FirstOrDefault(o => o.IsView && string.Equals(o.Name, r.ViewName, StringComparison.OrdinalIgnoreCase));
        if (view == null) return (null, T("Không tìm thấy View {0}.", r.ViewName ?? ""));
        string? Col(string? name) => string.IsNullOrWhiteSpace(name) ? null : view.Columns.FirstOrDefault(c => string.Equals(c, name.Trim(), StringComparison.OrdinalIgnoreCase));
        static string Q(string id) => "[" + id.Replace("]", "]]") + "]";

        var cols = (r.ViewColumns ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var bad = cols.Where(c => Col(c) == null).ToList();
        if (bad.Count > 0) return (null, T("View {0} không có cột: {1}", view.Name, string.Join(", ", bad)));
        foreach (var (label, value) in new[] { (T("Cột ngày"), r.DateColumn), (T("Cột người phụ trách"), r.OwnerColumn), (T("Sắp xếp theo"), r.OrderBy) })
            if (!string.IsNullOrWhiteSpace(value) && Col(value) == null) return (null, T("{0}: View {1} không có cột {2}", label, view.Name, value!));

        var select = cols.Count == 0 ? "*" : string.Join(", ", cols.Select(c => Q(Col(c)!)));
        var top = r.TopRows is > 0 ? $"TOP ({r.TopRows.Value}) " : "";
        var sql = $"SELECT {top}{select}\nFROM [dbo].{Q(view.Name)}\nWHERE 1 = 1";
        if (Col(r.DateColumn) is { } dc) sql += $"\n  AND {Q(dc)} >= @FromDate AND {Q(dc)} < DATEADD(day, 1, @ToDate)";
        if (Col(r.OwnerColumn) is { } oc) sql += $"\n  AND (@SeesAll = 1 OR {Q(oc)} = @CurrentUserId)\n  AND (@OwnerId IS NULL OR {Q(oc)} = @OwnerId)";
        if (Col("BranchId") != null) sql += "\n  AND " + BranchCondition;   // chỉ dữ liệu các đơn vị trong phạm vi
        if (Col(r.OrderBy) is { } ob) sql += $"\nORDER BY {Q(ob)}{(r.OrderDesc ? " DESC" : "")}";
        return (sql, null);
    }

    /// <summary>Biên dịch báo cáo dạng trình dựng; ask = giá trị người xem nhập cho điều kiện "Hỏi khi chạy" (null = mặc định)</summary>
    public async Task<(BuilderCompiled? Compiled, string? Error)> CompileBuilderAsync(string? json, Func<string, string?>? ask)
    {
        var def = ReportBuilderDef.Parse(json);
        if (def == null) return (null, T("Chưa cấu hình báo cáo (chọn nguồn dữ liệu và cột)."));
        var (res, err) = ReportBuilder.Compile(def, await SchemaAsync(), ask);
        return (res, err);
    }

    /// <summary>Nguồn dữ liệu (View) và cột cho trình dựng</summary>
    public async Task<List<BuilderDataset>> BuilderDatasetsAsync() => ReportBuilder.Datasets(await SchemaAsync());

    /// <summary>Câu lệnh cuối cùng của báo cáo (SQL tự viết hoặc dựng từ View)</summary>
    public async Task<(string? Sql, string? Error)> SqlForAsync(CustomReport r)
    {
        if (r.SourceType == CustomReportSource.View) return await BuildViewSqlAsync(r);
        if (r.SourceType == CustomReportSource.Builder)
        {
            var c = await CompileBuilderAsync(r.BuilderJson, null);
            return c.Compiled == null ? (null, c.Error) : (c.Compiled.Sql, null);
        }
        var err = Validate(r.SqlText);
        return err != null ? (null, err) : (r.SqlText, null);
    }

    /// <summary>Tham số mà báo cáo thực sự dùng (để hiện ô lọc tương ứng)</summary>
    public static HashSet<string> UsedParameters(string sql) =>
        Parameters.Where(p => Regex.IsMatch(sql, Regex.Escape(p) + @"\b", RegexOptions.IgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Chạy câu lệnh dưới người dùng chỉ đọc; trả về kết quả hoặc thông báo lỗi thân thiện</summary>
    public async Task<(ReportResult? Result, string? Error)> RunAsync(string sql, ReportFilter f, ClaimsPrincipal user, int maxRows,
        string? chartLabel = null, string? chartValue = null, bool? ownOnly = null, string? chartKind = null, IReadOnlyDictionary<string, object?>? extra = null)
    {
        var err = Validate(sql);
        if (err != null) return (null, err);

        // Kết nối riêng, không dùng pool: ngữ cảnh EXECUTE AS kết thúc cùng phiên làm việc
        var csb = new SqlConnectionStringBuilder(_db.Database.GetConnectionString()) { Pooling = false, ApplicationName = "CRM custom report" };
        await using var conn = new SqlConnection(csb.ConnectionString);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = TimeoutSeconds;
        cmd.CommandText = $"EXECUTE AS USER = N'{ReportingSchema.ReaderUser}';\n{sql.Trim().TrimEnd(';')}\n;\nREVERT;";

        // @SeesAll = 1 khi vùng dữ liệu không giới hạn theo người phụ trách (đơn vị / toàn hệ thống)
        var seesAll = ownOnly.HasValue ? !ownOnly.Value : user.SeesAll();
        cmd.Parameters.Add(new SqlParameter("@FromDate", SqlDbType.Date) { Value = f.FromDate });
        cmd.Parameters.Add(new SqlParameter("@ToDate", SqlDbType.Date) { Value = f.ToExclusive.AddDays(-1) });
        cmd.Parameters.Add(new SqlParameter("@Year", SqlDbType.Int) { Value = f.YearValue });
        cmd.Parameters.Add(new SqlParameter("@OwnerId", SqlDbType.Int) { Value = seesAll ? (object?)f.OwnerId ?? DBNull.Value : user.GetUserId() });
        cmd.Parameters.Add(new SqlParameter("@Keyword", SqlDbType.NVarChar, 200) { Value = string.IsNullOrWhiteSpace(f.Keyword) ? DBNull.Value : f.Keyword.Trim() });
        cmd.Parameters.Add(new SqlParameter("@CurrentUserId", SqlDbType.Int) { Value = user.GetUserId() });
        cmd.Parameters.Add(new SqlParameter("@SeesAll", SqlDbType.Bit) { Value = seesAll });
        cmd.Parameters.Add(new SqlParameter("@BranchIds", SqlDbType.NVarChar, -1) { Value = _scope.Active ? string.Join(",", _scope.Ids) : "" });
        if (extra != null) foreach (var kv in extra) cmd.Parameters.Add(new SqlParameter(kv.Key, kv.Value ?? DBNull.Value));

        try
        {
            await conn.OpenAsync();
            await using var reader = await cmd.ExecuteReaderAsync();
            var r = new ReportResult();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var t = reader.GetFieldType(i);
                var kind = t == typeof(DateTime) || t == typeof(DateTimeOffset) ? ReportColumnKind.Date
                    : t == typeof(decimal) || t == typeof(double) || t == typeof(float) ? ReportColumnKind.Money
                    : t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ? ReportColumnKind.Number
                    : ReportColumnKind.Text;
                var name = string.IsNullOrEmpty(reader.GetName(i)) ? T("Cột {0}", i + 1) : reader.GetName(i);
                // Không cộng tổng các cột mã định danh (Id, OwnerId...)
                r.Col(name, kind, !name.EndsWith("Id", StringComparison.Ordinal));
            }
            while (await reader.ReadAsync())
            {
                if (r.Rows.Count >= maxRows) { r.Truncated = true; break; }
                var row = new object?[reader.FieldCount];
                reader.GetValues(row!);
                for (var i = 0; i < row.Length; i++) if (row[i] is DBNull) row[i] = null;
                r.Rows.Add(row);
            }
            if (r.Columns.Count > 1 && r.Columns.Skip(1).Any(c => c.IsNumeric && c.Total) && r.Rows.Count > 1) r.AutoTotals(T("Tổng"));
            var li = r.Columns.FindIndex(c => string.Equals(c.Title, chartLabel, StringComparison.OrdinalIgnoreCase));
            var vi = r.Columns.FindIndex(c => string.Equals(c.Title, chartValue, StringComparison.OrdinalIgnoreCase));
            if (li >= 0 && vi >= 0 && r.Columns[vi].IsNumeric && chartKind != "none")
                r.Chart(li, vi, chartKind == "line" ? ReportChartKind.Line : chartKind == "pie" ? ReportChartKind.Pie : ReportChartKind.Bar);
            if (r.Truncated) r.Note = T("Chỉ hiển thị {0} dòng đầu tiên. Hãy lọc thêm hoặc xuất Excel (tối đa {1} dòng).", maxRows.ToString("N0"), ExportRows.ToString("N0"));
            return (r, null);
        }
        catch (SqlException ex)
        {
            var msg = ex.Number switch
            {
                229 or 230 or 262 or 297 or 300 or 916 => T("Không có quyền: báo cáo động chỉ được ĐỌC dữ liệu của CRM (không ghi, không đọc mật khẩu, không truy cập database khác). Chi tiết: {0}", ex.Message),
                15517 or 15151 or 15406 => T("Database chưa có người dùng chỉ đọc {0} – khởi động lại ứng dụng bằng tài khoản database có quyền db_owner. Chi tiết: {1}", ReportingSchema.ReaderUser, ex.Message),
                -2 => T("Báo cáo chạy quá {0} giây và đã bị dừng. Hãy thu hẹp điều kiện lọc.", TimeoutSeconds),
                _ => T("Lỗi SQL: {0}", ex.Message)
            };
            return (null, msg);
        }
    }
}
