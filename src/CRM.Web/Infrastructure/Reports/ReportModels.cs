using System.Security.Claims;
using CRM.Web.Data;

namespace CRM.Web.Infrastructure.Reports;

/// <summary>Kiểu dữ liệu cột báo cáo (quyết định định dạng hiển thị / xuất Excel)</summary>
public enum ReportColumnKind { Text, Money, Number, Percent, Date }

/// <summary>Kiểu biểu đồ của báo cáo: cột ngang (xếp hạng), đường (xu hướng theo thứ tự dòng), tròn (tỷ trọng)</summary>
public enum ReportChartKind { Bar, Line, Pie }

public record ReportColumn(string Title, ReportColumnKind Kind = ReportColumnKind.Text, bool Total = true)
{
    public bool IsNumeric => Kind is ReportColumnKind.Money or ReportColumnKind.Number or ReportColumnKind.Percent;
}

/// <summary>Kết quả báo cáo dạng bảng (dùng chung cho báo cáo chuẩn và báo cáo động)</summary>
public class ReportResult
{
    public List<ReportColumn> Columns { get; } = new();
    public List<object?[]> Rows { get; } = new();
    /// <summary>Dòng tổng (null = không hiện). Tự tính bằng <see cref="AutoTotals"/> hoặc gán tay.</summary>
    public object?[]? Totals { get; set; }
    /// <summary>Biểu đồ cột: chỉ số cột nhãn và cột giá trị (-1 = không vẽ)</summary>
    public int ChartLabel { get; set; } = -1;
    public int ChartValue { get; set; } = -1;
    public ReportChartKind ChartKind { get; set; } = ReportChartKind.Bar;
    /// <summary>Liên kết mở bản ghi chi tiết của từng dòng (song song với Rows; null = không có) – hiện ở cột LinkColumn</summary>
    public List<string?> Links { get; } = new();
    public int LinkColumn { get; set; }
    public string? Note { get; set; }
    public bool Truncated { get; set; }

    public ReportResult Col(string title, ReportColumnKind kind = ReportColumnKind.Text, bool total = true)
    {
        Columns.Add(new ReportColumn(title, kind, total));
        return this;
    }

    public void Add(params object?[] values) => Rows.Add(values);

    /// <summary>Cộng các cột số (bỏ qua cột phần trăm và cột đánh dấu không cộng)</summary>
    public ReportResult AutoTotals(string label)
    {
        var t = new object?[Columns.Count];
        t[0] = label;
        for (var i = 1; i < Columns.Count; i++)
        {
            var c = Columns[i];
            if (!c.IsNumeric || c.Kind == ReportColumnKind.Percent || !c.Total) continue;
            t[i] = Rows.Sum(r => ToDecimal(r[i]) ?? 0);
        }
        Totals = t;
        return this;
    }

    public ReportResult Chart(int label, int value, ReportChartKind kind = ReportChartKind.Bar) { ChartLabel = label; ChartValue = value; ChartKind = kind; return this; }

    /// <summary>Thêm dòng kèm liên kết chi tiết (bấm vào cột LinkColumn để mở bản ghi)</summary>
    public void AddLink(string? url, params object?[] values)
    {
        while (Links.Count < Rows.Count) Links.Add(null);
        Rows.Add(values); Links.Add(url);
    }

    public static decimal? ToDecimal(object? v) => v switch
    {
        null or DBNull => null,
        decimal d => d,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        double db => (decimal)db,
        float f => (decimal)f,
        _ => decimal.TryParse(v.ToString(), out var x) ? x : null
    };

    public static string Format(object? v, ReportColumnKind kind) => v switch
    {
        null or DBNull => "",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("dd/MM/yyyy") : d.ToString("dd/MM/yyyy HH:mm"),
        bool b => b ? "✓" : "",
        _ when kind == ReportColumnKind.Percent && ToDecimal(v) is { } p => p.ToString("0.#", Vi) + "%",
        _ when kind is ReportColumnKind.Money or ReportColumnKind.Number && ToDecimal(v) is { } n =>
            n == Math.Round(n) ? n.ToString("N0", Vi) : n.ToString("N2", Vi),
        _ => v.ToString() ?? ""
    };

    private static readonly System.Globalization.CultureInfo Vi = System.Globalization.CultureInfo.GetCultureInfo("vi-VN");
}

/// <summary>Bộ lọc báo cáo</summary>
public class ReportFilter
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int? Year { get; set; }
    public int? OwnerId { get; set; }
    public int? Top { get; set; }
    public int? Days { get; set; }
    public string? Keyword { get; set; }

    public DateTime FromDate => (From ?? new DateTime(DateTime.Today.Year, 1, 1)).Date;
    /// <summary>Hết ngày "đến" (không bao gồm) để so sánh &lt;</summary>
    public DateTime ToExclusive => (To ?? DateTime.Today).Date.AddDays(1);
    public int YearValue => Year ?? DateTime.Today.Year;
}

[Flags]
public enum ReportFilters { None = 0, Period = 1, Year = 2, Owner = 4, Top = 8, Days = 16, Keyword = 32 }

/// <summary>Ngữ cảnh chạy báo cáo: DB, người dùng và bộ lọc (đã áp quyền: nhân viên kinh doanh chỉ thấy dữ liệu của mình)</summary>
public class ReportContext
{
    public required CrmDbContext Db { get; init; }
    public required ClaimsPrincipal User { get; init; }
    public required ReportFilter Filter { get; init; }
    /// <summary>Phạm vi đơn vị (báo cáo theo công ty / chi nhánh)</summary>
    public BranchScope? Scope { get; init; }

    /// <summary>Vùng dữ liệu "chỉ dữ liệu mình phụ trách" (theo phân quyền báo cáo). null = mặc định theo vai trò (nhân viên chỉ thấy của mình)</summary>
    public bool? OwnOnly { get; init; }

    /// <summary>Người phụ trách cần lọc: vùng "của mình" → luôn = chính mình; vùng đơn vị / toàn hệ thống → theo bộ lọc (null = tất cả)</summary>
    public int? Owner => (OwnOnly ?? !User.SeesAll()) ? User.GetUserId() : Filter.OwnerId;
}

/// <summary>Định nghĩa một báo cáo chuẩn</summary>
public record ReportDefinition(string Key, string Group, string Title, string Description, string Icon,
    ReportFilters Filters, Func<ReportContext, Task<ReportResult>> Run);
