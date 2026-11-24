using System.Globalization;
using System.Text;
using System.Text.Json;
using static CRM.Web.Infrastructure.I18n;

namespace CRM.Web.Infrastructure.Reports;

public class BuilderColumn { public string Field { get; set; } = ""; public string? Label { get; set; } }

public class BuilderGroup
{
    public string Field { get; set; } = "";
    /// <summary>none | day | month | quarter | year (chỉ cột ngày)</summary>
    public string Bucket { get; set; } = "none";
    public string? Label { get; set; }
}

public class BuilderMeasure
{
    /// <summary>count | countd | sum | avg | min | max</summary>
    public string Agg { get; set; } = "count";
    public string Field { get; set; } = "*";
    public string? Label { get; set; }
}

public class BuilderFilter
{
    public string Field { get; set; } = "";
    public string Op { get; set; } = "eq";
    public string? Value { get; set; }
    public string? Value2 { get; set; }
    /// <summary>Hỏi người xem khi chạy báo cáo (Value là giá trị mặc định; để trống = không lọc)</summary>
    public bool Ask { get; set; }
    public string? Label { get; set; }
}

/// <summary>Định nghĩa báo cáo dựng bằng giao diện (lưu JSON trong CustomReport.BuilderJson)</summary>
public class ReportBuilderDef
{
    public string? Source { get; set; }
    /// <summary>list = danh sách chi tiết; summary = nhóm & cộng dồn</summary>
    public string Mode { get; set; } = "list";
    public List<BuilderColumn> Columns { get; set; } = new();
    public List<BuilderGroup> Groups { get; set; } = new();
    public List<BuilderMeasure> Measures { get; set; } = new();
    public List<BuilderFilter> Filters { get; set; } = new();
    /// <summary>Cột ngày để lọc theo kỳ báo cáo (Từ ngày – Đến ngày)</summary>
    public string? DateColumn { get; set; }
    public string? SortField { get; set; }
    public bool SortDesc { get; set; }
    public int? Top { get; set; }
    /// <summary>Chỉ số cột kết quả làm nhãn / giá trị của biểu đồ (-1 = tự chọn)</summary>
    public int ChartLabelIdx { get; set; } = -1;
    public int ChartValueIdx { get; set; } = -1;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static ReportBuilderDef? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ReportBuilderDef>(json, Json); } catch (JsonException) { return null; }
    }
}

/// <summary>Ô hỏi người xem khi chạy báo cáo (từ điều kiện "Hỏi khi chạy")</summary>
public record BuilderAsk(string Name, string Name2, string Label, string Type, string Op, string? Value, string? Value2)
{
    /// <summary>Điều kiện khoảng (hai ô nhập)</summary>
    public bool Range => Op == "between";
}

public record BuilderCompiled(string Sql, Dictionary<string, object?> Params, List<BuilderAsk> Asks, List<string> Titles, string? ChartLabel, string? ChartValue);

public record BuilderField(string Name, string Label, string Type);
public record BuilderDataset(string Name, string Label, List<BuilderField> Fields);

/// <summary>Nhãn tiếng Việt của View / cột và biên dịch định nghĩa trình dựng thành câu SELECT</summary>
public static class ReportBuilder
{
    // ======================= Nhãn =======================

    public static string DatasetLabel(string view) => view switch
    {
        "vw_Opportunities" => T("Cơ hội bán hàng"), "vw_Leads" => "Lead", "vw_Customers" => T("Khách hàng"), "vw_Activities" => T("Hoạt động"),
        "vw_Contracts" => T("Hợp đồng"), "vw_Invoices" => T("Hoá đơn"), "vw_Payments" => T("Thanh toán"), "vw_Tickets" => T("Phiếu hỗ trợ"),
        "vw_Quotes" => T("Báo giá"), "vw_Campaigns" => T("Chiến dịch tiếp thị"), "vw_NpsResponses" => T("Phản hồi khảo sát NPS"),
        _ => view
    };

    public static string ColumnLabel(string col) => col switch
    {
        "Id" => "Id", "Name" => T("Tên"), "Number" => T("Số chứng từ"), "Title" => T("Tiêu đề"), "Subject" => T("Tiêu đề"), "Code" => T("Mã"),
        "CustomerCode" => T("Mã khách hàng"), "CustomerName" => T("Khách hàng"), "ContactName" => T("Người liên hệ"), "Industry" => T("Ngành"), "City" => T("Tỉnh / Thành phố"),
        "Stage" => T("Giai đoạn (mã)"), "StageName" => T("Giai đoạn"), "IsOpen" => T("Đang mở"), "Amount" => T("Giá trị"), "Probability" => T("Xác suất (%)"),
        "WeightedAmount" => T("Giá trị có trọng số"), "ExpectedCloseDate" => T("Dự kiến chốt"), "ClosedAt" => T("Ngày đóng"), "LostReason" => T("Lý do thua"),
        "SourceName" => T("Nguồn"), "CampaignId" => T("Id chiến dịch"), "OwnerId" => T("Id người phụ trách"), "OwnerName" => T("Người phụ trách"),
        "CreatedAt" => T("Ngày tạo"), "BranchId" => T("Id đơn vị"), "BranchCode" => T("Mã đơn vị"), "BranchName" => T("Đơn vị"),
        "FullName" => T("Họ tên"), "Company" => T("Công ty"), "JobTitle" => T("Chức vụ"), "Email" => "Email", "Phone" => T("Điện thoại"),
        "StatusName" => T("Trạng thái"), "RatingName" => T("Mức độ"), "EstimatedValue" => T("Giá trị dự kiến"), "ConvertedAt" => T("Ngày chuyển đổi"),
        "TypeName" => T("Phân loại"), "TaxCode" => T("Mã số thuế"), "EmployeeCount" => T("Số nhân sự"), "WonRevenue" => T("Doanh số thắng"),
        "OpenPipeline" => T("Pipeline đang mở"), "Outstanding" => T("Còn phải thu"), "DueDate" => T("Hạn"), "CompletedAt" => T("Ngày hoàn thành"),
        "IsOverdue" => T("Quá hạn"), "LeadName" => T("Lead"), "OpportunityName" => T("Cơ hội"), "SignDate" => T("Ngày ký"), "StartDate" => T("Ngày bắt đầu"),
        "EndDate" => T("Ngày kết thúc"), "SubTotal" => T("Cộng tiền hàng"), "VatAmount" => T("Thuế GTGT"), "Total" => T("Tổng tiền"), "Invoiced" => T("Đã xuất hoá đơn"),
        "Paid" => T("Đã thu"), "Receivable" => T("Còn phải thu"), "Symbol" => T("Ký hiệu"), "InvoiceDate" => T("Ngày hoá đơn"), "ContractNumber" => T("Số hợp đồng"),
        "PaidAmount" => T("Đã thu"), "DaysOverdue" => T("Số ngày quá hạn"), "PaymentDate" => T("Ngày thu"), "InvoiceNo" => T("Hoá đơn"), "Method" => T("Phương thức"),
        "Reference" => T("Số chứng từ / UNC"), "ChannelName" => T("Kênh"), "Category" => T("Loại"), "PriorityName" => T("Độ ưu tiên"),
        "ResponseDueAt" => T("Hạn phản hồi"), "FirstResponseAt" => T("Phản hồi lần đầu"), "ResolveDueAt" => T("Hạn giải quyết"), "ResolvedAt" => T("Ngày giải quyết"),
        "ResponseHours" => T("Phản hồi (giờ)"), "ResolveHours" => T("Giải quyết (giờ)"), "ResponseBreached" => T("Trễ hạn phản hồi"), "ResolveBreached" => T("Trễ hạn giải quyết"),
        "SatisfactionScore" => T("Điểm hài lòng"), "AccountOwnerId" => T("Id phụ trách khách hàng"),
        "Score" => T("Điểm"), "UtmSource" => "UTM source", "UtmMedium" => "UTM medium", "UtmCampaign" => "UTM campaign", "WebFormName" => T("Form website"),
        "HealthScore" => T("Điểm sức khoẻ"), "HealthName" => T("Mức sức khoẻ"), "BillingCycleName" => T("Chu kỳ thanh toán"), "RecurringAmount" => T("Số tiền mỗi kỳ"),
        "NextBillingDate" => T("Kỳ lập hoá đơn tiếp theo"), "MonthlyRecurring" => "MRR", "SlaPolicyName" => T("Chính sách SLA"), "SlaPausedMinutes" => T("Phút SLA tạm dừng"),
        "QuoteDate" => T("Ngày báo giá"), "ValidUntil" => T("Hiệu lực đến"), "ApprovalName" => T("Trạng thái duyệt chiết khấu"), "DiscountPercent" => T("Chiết khấu (%)"),
        "EffectiveDiscount" => T("Chiết khấu thực tế (%)"), "DiscountAmount" => T("Tiền chiết khấu"), "ApprovedByName" => T("Người duyệt chiết khấu"),
        "DiscountApprovedAt" => T("Ngày duyệt chiết khấu"), "Budget" => T("Ngân sách"), "ActualCost" => T("Chi phí thực tế"), "MemberCount" => T("Số đối tượng"),
        "SurveyName" => T("Đợt khảo sát"), "NpsGroup" => T("Nhóm NPS"), "Comment" => T("Góp ý"), "SentAt" => T("Ngày gửi"), "RespondedAt" => T("Ngày trả lời"),
        _ => col
    };

    /// <summary>Phân loại kiểu dữ liệu SQL thành text / number / date / bool</summary>
    public static string TypeGroup(string? sqlType) => (sqlType ?? "").ToLowerInvariant() switch
    {
        "int" or "bigint" or "smallint" or "tinyint" or "decimal" or "numeric" or "float" or "real" or "money" or "smallmoney" => "number",
        "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => "date",
        "bit" => "bool",
        _ => "text"
    };

    public static List<BuilderDataset> Datasets(IEnumerable<DbObjectInfo> schema) =>
        schema.Where(o => o.IsView).Select(o => new BuilderDataset(o.Name, DatasetLabel(o.Name),
            o.Columns.Select(c => new BuilderField(c, ColumnLabel(c), TypeGroup(o.Types != null && o.Types.TryGetValue(c, out var t) ? t : null))).ToList())).ToList();

    // ======================= Biên dịch =======================

    private static string Q(string id) => "[" + id.Replace("]", "]]") + "]";
    private static string CleanAlias(string? s) => new string((s ?? "").Where(ch => !char.IsControl(ch) && ch != '[' && ch != ']').ToArray()).Trim() is { Length: > 0 } t ? (t.Length > 60 ? t[..60] : t) : "";

    private static readonly Dictionary<string, string> RelativeOps = new()
    {
        ["thismonth"] = "DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)|DATEADD(month, 1, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1))",
        ["lastmonth"] = "DATEADD(month, -1, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1))|DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)",
        ["thisquarter"] = "DATEFROMPARTS(YEAR(GETDATE()), (DATEPART(quarter, GETDATE()) - 1) * 3 + 1, 1)|DATEADD(month, 3, DATEFROMPARTS(YEAR(GETDATE()), (DATEPART(quarter, GETDATE()) - 1) * 3 + 1, 1))",
        ["thisyear"] = "DATEFROMPARTS(YEAR(GETDATE()), 1, 1)|DATEFROMPARTS(YEAR(GETDATE()) + 1, 1, 1)",
        ["lastyear"] = "DATEFROMPARTS(YEAR(GETDATE()) - 1, 1, 1)|DATEFROMPARTS(YEAR(GETDATE()), 1, 1)",
        ["last7"] = "DATEADD(day, -7, CAST(GETDATE() AS date))|DATEADD(day, 1, CAST(GETDATE() AS date))",
        ["last30"] = "DATEADD(day, -30, CAST(GETDATE() AS date))|DATEADD(day, 1, CAST(GETDATE() AS date))",
        ["last90"] = "DATEADD(day, -90, CAST(GETDATE() AS date))|DATEADD(day, 1, CAST(GETDATE() AS date))",
    };

    public static bool IsRelative(string op) => RelativeOps.ContainsKey(op);

    private static bool Truthy(string? v) => (v ?? "").Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes" or "có" or "co";

    private static bool TryParseNumber(string s, out decimal d)
    {
        s = s.Trim().Replace(" ", "");
        if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d)) return true;
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.GetCultureInfo("vi-VN"), out d);
    }

    private static bool TryParseDate(string s, out DateTime d) =>
        DateTime.TryParseExact(s.Trim(), new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    private static string BucketExpr(string col, string bucket) => bucket switch
    {
        "day" => $"CAST({Q(col)} AS date)",
        "month" => $"CONVERT(char(7), {Q(col)}, 120)",
        "quarter" => $"CONCAT(YEAR({Q(col)}), N'-Q', DATEPART(quarter, {Q(col)}))",
        "year" => $"CONVERT(char(4), {Q(col)}, 120)",
        _ => Q(col)
    };

    private static string BucketSuffix(string bucket) => bucket switch
    {
        "day" => " (" + T("ngày") + ")", "month" => " (" + T("tháng") + ")", "quarter" => " (" + T("quý") + ")", "year" => " (" + T("năm") + ")", _ => ""
    };

    private static string AggTitle(string agg, string fieldLabel) => agg switch
    {
        "count" => T("Số dòng"), "countd" => T("Số {0} khác nhau", fieldLabel), "sum" => T("Tổng {0}", fieldLabel), "avg" => T("Trung bình {0}", fieldLabel),
        "min" => T("Nhỏ nhất {0}", fieldLabel), "max" => T("Lớn nhất {0}", fieldLabel), _ => fieldLabel
    };

    /// <summary>
    /// Biên dịch định nghĩa thành câu SELECT. Mọi tên bảng / cột lấy từ danh sách thật của View (đối chiếu, bọc [ ]); mọi giá trị người dùng
    /// nhập đi qua tham số (@bf…) nên không thể chèn câu lệnh. Điều kiện phạm vi (người phụ trách, đơn vị, kỳ báo cáo) tự thêm khi View có cột tương ứng.
    /// ask: lấy giá trị người xem nhập cho điều kiện "Hỏi khi chạy" (null = dùng giá trị mặc định).
    /// </summary>
    public static (BuilderCompiled? Result, string? Error) Compile(ReportBuilderDef def, IReadOnlyList<DbObjectInfo> schema, Func<string, string?>? ask = null)
    {
        var view = schema.FirstOrDefault(o => o.IsView && string.Equals(o.Name, def.Source, StringComparison.OrdinalIgnoreCase));
        if (view == null) return (null, T("Chọn nguồn dữ liệu cho báo cáo."));
        string? Col(string? n) => string.IsNullOrWhiteSpace(n) ? null : view.Columns.FirstOrDefault(c => string.Equals(c, n.Trim(), StringComparison.OrdinalIgnoreCase));
        string TypeOf(string col) => TypeGroup(view.Types != null && view.Types.TryGetValue(col, out var t) ? t : null);
        var summary = def.Mode == "summary";

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Alias(string? label, string fallback)
        {
            var a = CleanAlias(label); if (a.Length == 0) a = CleanAlias(fallback); if (a.Length == 0) a = "Cột";
            var res = a; var n = 2;
            while (!used.Add(res)) res = $"{a} ({n++})";
            return res;
        }

        var select = new List<string>(); var titles = new List<string>(); var groupBy = new List<string>();
        var firstTimeGroup = false;
        if (summary)
        {
            foreach (var g in def.Groups)
            {
                var c = Col(g.Field);
                if (c == null) return (null, T("Cột nhóm \"{0}\" không có trong nguồn dữ liệu.", g.Field));
                var bucket = TypeOf(c) == "date" && g.Bucket is "day" or "month" or "quarter" or "year" ? g.Bucket : "none";
                if (titles.Count == 0 && bucket != "none") firstTimeGroup = true;
                var expr = BucketExpr(c, bucket);
                var alias = Alias(g.Label, ColumnLabel(c) + BucketSuffix(bucket));
                select.Add($"{expr} AS {Q(alias)}"); titles.Add(alias); groupBy.Add(expr);
            }
            var measures = def.Measures.Count == 0 ? new List<BuilderMeasure> { new() { Agg = "count" } } : def.Measures;
            foreach (var m in measures)
            {
                var agg = m.Agg is "count" or "countd" or "sum" or "avg" or "min" or "max" ? m.Agg : "count";
                string expr, fieldLabel = "";
                if (agg == "count") expr = "COUNT(*)";
                else
                {
                    var c = Col(m.Field);
                    if (c == null) return (null, T("Cột \"{0}\" không có trong nguồn dữ liệu.", m.Field));
                    var type = TypeOf(c); fieldLabel = ColumnLabel(c);
                    if ((agg is "sum" or "avg") && type != "number") return (null, T("Không thể {0} cột \"{1}\" vì không phải cột số.", agg == "sum" ? T("cộng") : T("tính trung bình"), fieldLabel));
                    if ((agg is "min" or "max") && type is not ("number" or "date")) return (null, T("Chỉ lấy nhỏ nhất / lớn nhất cho cột số hoặc ngày: \"{0}\".", fieldLabel));
                    expr = agg switch { "countd" => $"COUNT(DISTINCT {Q(c)})", "sum" => $"SUM({Q(c)})", "avg" => $"AVG(CAST({Q(c)} AS decimal(38,4)))", "min" => $"MIN({Q(c)})", _ => $"MAX({Q(c)})" };
                }
                var alias = Alias(m.Label, AggTitle(agg, fieldLabel));
                select.Add($"{expr} AS {Q(alias)}"); titles.Add(alias);
            }
        }
        else
        {
            var cols = def.Columns.Select(c => (Field: Col(c.Field), c.Label)).Where(c => c.Field != null).ToList();
            if (cols.Count == 0) cols = view.Columns.Take(40).Select(c => ((string?)c, (string?)null)).ToList();
            foreach (var (field, label) in cols)
            {
                var alias = Alias(label, ColumnLabel(field!));
                select.Add($"{Q(field!)} AS {Q(alias)}"); titles.Add(alias);
            }
        }

        // ---------- Điều kiện ----------
        var where = new List<string>(); var pars = new Dictionary<string, object?>(); var asks = new List<BuilderAsk>();
        for (var i = 0; i < def.Filters.Count; i++)
        {
            var f = def.Filters[i];
            var c = Col(f.Field);
            if (c == null) return (null, T("Cột lọc \"{0}\" không có trong nguồn dữ liệu.", f.Field));
            var type = TypeOf(c); var label = string.IsNullOrWhiteSpace(f.Label) ? ColumnLabel(c) : f.Label!.Trim();
            var op = f.Op ?? "eq"; var name = $"bf{i}"; var name2 = $"bf{i}_2";
            string? v1 = f.Value, v2 = f.Value2;
            if (f.Ask)
            {
                if (ask != null) { v1 = ask(name) ?? f.Value; v2 = ask(name2) ?? f.Value2; }   // khoá có trong URL (kể cả rỗng) → dùng giá trị người xem nhập
                asks.Add(new BuilderAsk(name, name2, label, type, op, v1, v2));
                if (op is not ("isnull" or "notnull") && !IsRelative(op) && (string.IsNullOrWhiteSpace(v1) || (op == "between" && string.IsNullOrWhiteSpace(v2)))) continue;   // để trống = không lọc
            }
            var (cond, err) = FilterCondition(c, type, op, v1, v2, name, label, pars);
            if (err != null) return (null, err);
            if (cond != null) where.Add(cond);
        }

        var seesOwner = Col("OwnerId");
        var sb = new StringBuilder();
        var top = def.Top is > 0 ? $"TOP ({Math.Min(def.Top.Value, 100000)}) " : "";
        sb.Append($"SELECT {top}{string.Join(", ", select)}\nFROM [dbo].{Q(view.Name)}\nWHERE 1 = 1");
        if (Col(def.DateColumn) is { } dc && TypeOf(dc) == "date") sb.Append($"\n  AND {Q(dc)} >= @FromDate AND {Q(dc)} < DATEADD(day, 1, @ToDate)");
        if (seesOwner != null) sb.Append($"\n  AND (@SeesAll = 1 OR {Q(seesOwner)} = @CurrentUserId)\n  AND (@OwnerId IS NULL OR {Q(seesOwner)} = @OwnerId)");
        if (Col("BranchId") != null) sb.Append("\n  AND " + CustomReportService.BranchCondition);
        foreach (var w in where) sb.Append("\n  AND ").Append(w);
        if (summary && groupBy.Count > 0) sb.Append("\nGROUP BY ").Append(string.Join(", ", groupBy));

        // ---------- Sắp xếp ----------
        string? order = null;
        if (summary)
        {
            // SortField: tên cột kết quả hoặc "#chỉ số" (giao diện dùng chỉ số để không phụ thuộc cách đặt nhãn)
            var sortIdx = def.SortField is { Length: > 1 } sfx && sfx[0] == '#' && int.TryParse(sfx[1..], out var si) && si >= 0 && si < titles.Count
                ? si : titles.FindIndex(t => string.Equals(t, def.SortField, StringComparison.OrdinalIgnoreCase));
            if (sortIdx >= 0) order = $"{Q(titles[sortIdx])}{(def.SortDesc ? " DESC" : "")}";
            else if (firstTimeGroup) order = Q(titles[0]);
            else if (groupBy.Count < titles.Count) order = $"{Q(titles[groupBy.Count])} DESC";
        }
        else if (Col(def.SortField) is { } sf) order = $"{Q(sf)}{(def.SortDesc ? " DESC" : "")}";
        if (order != null) sb.Append("\nORDER BY ").Append(order);

        // ---------- Biểu đồ: chỉ số cột (mặc định: nhóm đầu + phép tính đầu) ----------
        string? chartLabel = null, chartValue = null;
        var li = def.ChartLabelIdx; var vi = def.ChartValueIdx;
        if (summary && li < 0 && vi < 0 && groupBy.Count > 0) { li = 0; vi = groupBy.Count; }
        if (li >= 0 && li < titles.Count) chartLabel = titles[li];
        if (vi >= 0 && vi < titles.Count) chartValue = titles[vi];
        return (new BuilderCompiled(sb.ToString(), pars, asks, titles, chartLabel, chartValue), null);
    }

    /// <summary>Điều kiện của một bộ lọc: (câu điều kiện, lỗi). Giá trị luôn đi qua tham số.</summary>
    private static (string? Cond, string? Error) FilterCondition(string col, string type, string op, string? v1, string? v2, string name, string label, Dictionary<string, object?> pars)
    {
        var q = Q(col);
        if (op == "isnull") return ($"{q} IS NULL", null);
        if (op == "notnull") return ($"{q} IS NOT NULL", null);
        if (type == "date" && RelativeOps.TryGetValue(op, out var rel)) { var p = rel.Split('|'); return ($"({q} >= {p[0]} AND {q} < {p[1]})", null); }

        (string?, string?) Fail(string msg) => (null, msg);
        if (string.IsNullOrWhiteSpace(v1)) return Fail(T("Điều kiện lọc \"{0}\" chưa có giá trị.", label));

        // Đọc một giá trị theo kiểu cột; lỗi trả về qua Error
        (object? Value, string? Error) Parse(string s)
        {
            switch (type)
            {
                case "number":
                    return TryParseNumber(s, out var d) ? (d, null) : (null, T("Giá trị \"{0}\" của điều kiện \"{1}\" không phải số.", s, label));
                case "date":
                    return TryParseDate(s, out var dt) ? (dt, null) : (null, T("Giá trị \"{0}\" của điều kiện \"{1}\" không phải ngày hợp lệ (dd/MM/yyyy).", s, label));
                case "bool": return (Truthy(s), null);
                default: return (s.Trim(), null);
            }
        }
        static string LikeEscape(string s) => s.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

        if (op == "in")
        {
            var items = v1.Split(new[] { '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(50).ToList();
            if (items.Count == 0) return Fail(T("Điều kiện lọc \"{0}\" chưa có giá trị.", label));
            var names = new List<string>();
            for (var k = 0; k < items.Count; k++)
            {
                var (val, err) = Parse(items[k]);
                if (err != null) return Fail(err);
                var pn = $"{name}_i{k}"; pars["@" + pn] = val; names.Add("@" + pn);
            }
            return ($"{q} IN ({string.Join(", ", names)})", null);
        }
        if (op is "contains" or "startswith")
        {
            if (type != "text") return Fail(T("Điều kiện \"chứa / bắt đầu bằng\" chỉ dùng cho cột văn bản: \"{0}\".", label));
            pars["@" + name] = op == "contains" ? $"%{LikeEscape(v1.Trim())}%" : $"{LikeEscape(v1.Trim())}%";
            return ($"{q} LIKE @{name}", null);
        }
        if (op == "between")
        {
            if (string.IsNullOrWhiteSpace(v2)) return Fail(T("Điều kiện \"{0}\" (khoảng) cần đủ hai giá trị.", label));
            var (a, ea) = Parse(v1); if (ea != null) return Fail(ea);
            var (b, eb) = Parse(v2!); if (eb != null) return Fail(eb);
            pars["@" + name] = a; pars["@" + name + "b"] = b;
            return (type == "date" ? $"({q} >= @{name} AND {q} < DATEADD(day, 1, @{name}b))" : $"({q} >= @{name} AND {q} <= @{name}b)", null);
        }
        var (val1, e1) = Parse(v1); if (e1 != null) return Fail(e1);
        pars["@" + name] = val1;
        string? cond = type == "date"
            ? op switch
            {
                "eq" => $"({q} >= @{name} AND {q} < DATEADD(day, 1, @{name}))",
                "ne" => $"({q} < @{name} OR {q} >= DATEADD(day, 1, @{name}))",
                "gt" => $"{q} >= DATEADD(day, 1, @{name})",
                "ge" => $"{q} >= @{name}",
                "lt" => $"{q} < @{name}",
                "le" => $"{q} < DATEADD(day, 1, @{name})",
                _ => null
            }
            : op switch
            {
                "eq" => $"{q} = @{name}", "ne" => $"({q} <> @{name} OR {q} IS NULL)", "gt" => $"{q} > @{name}", "ge" => $"{q} >= @{name}",
                "lt" => $"{q} < @{name}", "le" => $"{q} <= @{name}", _ => null
            };
        return cond == null ? Fail(T("Phép so sánh \"{0}\" không dùng được cho điều kiện \"{1}\".", op, label)) : (cond, null);
    }
}
