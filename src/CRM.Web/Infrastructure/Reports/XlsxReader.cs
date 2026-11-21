using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace CRM.Web.Infrastructure.Reports;

/// <summary>
/// Đọc sheet đầu tiên của file Excel .xlsx (Office Open XML, không cần thư viện ngoài) hoặc file .csv thành bảng chuỗi.
/// Ô ngày (định dạng ngày trong Excel) trả về dạng yyyy-MM-dd; số trả về dạng chuẩn (dấu chấm thập phân).
/// </summary>
public static class XlsxReader
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pr = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static List<string[]> Read(Stream stream, string fileName, int maxRows = 10000)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext == ".csv" || ext == ".txt") return ReadCsv(stream, maxRows);
        if (ext != ".xlsx") throw new InvalidDataException(I18n.T("Chỉ hỗ trợ file .xlsx hoặc .csv."));
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        XDocument? Load(string path) { var e = zip.GetEntry(path); if (e == null) return null; using var s = e.Open(); return XDocument.Load(s); }

        // Sheet đầu tiên theo workbook.xml
        var sheetPath = "xl/worksheets/sheet1.xml";
        var wb = Load("xl/workbook.xml");
        var rid = wb?.Root?.Element(S + "sheets")?.Elements(S + "sheet").FirstOrDefault()?.Attribute(R + "id")?.Value;
        var rels = Load("xl/_rels/workbook.xml.rels");
        var target = rels?.Root?.Elements(Pr + "Relationship").FirstOrDefault(x => x.Attribute("Id")?.Value == rid)?.Attribute("Target")?.Value;
        if (!string.IsNullOrEmpty(target)) sheetPath = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;

        var shared = Load("xl/sharedStrings.xml")?.Root?.Elements(S + "si")
            .Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList() ?? new List<string>();
        var dateStyles = DateStyles(Load("xl/styles.xml"));
        var sheet = Load(sheetPath) ?? throw new InvalidDataException(I18n.T("Không đọc được sheet dữ liệu trong file Excel."));

        var rows = new List<string[]>();
        foreach (var row in sheet.Descendants(S + "row"))
        {
            var cells = new SortedDictionary<int, string>();
            var auto = 0;
            foreach (var c in row.Elements(S + "c"))
            {
                var col = ColumnIndex(c.Attribute("r")?.Value) ?? auto;
                auto = col + 1;
                var t = c.Attribute("t")?.Value;
                var v = c.Element(S + "v")?.Value;
                string text;
                if (t == "s") text = int.TryParse(v, out var si) && si < shared.Count ? shared[si] : "";
                else if (t == "inlineStr") text = string.Concat(c.Descendants(S + "t").Select(x => x.Value));
                else if (t == "b") text = v == "1" ? "TRUE" : "FALSE";
                else if (t is "str" or "e") text = v ?? "";
                else
                {
                    text = v ?? "";
                    var style = int.TryParse(c.Attribute("s")?.Value, out var st) ? st : -1;
                    if (style >= 0 && dateStyles.Contains(style) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa) && oa > 0 && oa < 2958466)
                        text = DateTime.FromOADate(oa).ToString(oa % 1 == 0 ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                        text = ((decimal)num).ToString(CultureInfo.InvariantCulture);
                }
                cells[col] = text.Trim();
            }
            if (cells.Count == 0) continue;
            var arr = new string[cells.Keys.Max() + 1];
            for (var i = 0; i < arr.Length; i++) arr[i] = cells.TryGetValue(i, out var x) ? x : "";
            if (arr.All(string.IsNullOrWhiteSpace)) continue;
            rows.Add(arr);
            if (rows.Count > maxRows) break;
        }
        return rows;
    }

    /// <summary>Chỉ số các kiểu ô (cellXfs) có định dạng ngày</summary>
    private static HashSet<int> DateStyles(XDocument? styles)
    {
        var result = new HashSet<int>();
        if (styles?.Root == null) return result;
        var custom = styles.Root.Element(S + "numFmts")?.Elements(S + "numFmt")
            .ToDictionary(x => int.Parse(x.Attribute("numFmtId")!.Value), x => x.Attribute("formatCode")?.Value ?? "") ?? new Dictionary<int, string>();
        bool IsDate(int id)
        {
            if (id is >= 14 and <= 22 or >= 45 and <= 47) return true;
            if (!custom.TryGetValue(id, out var f)) return false;
            f = System.Text.RegularExpressions.Regex.Replace(f, "\"[^\"]*\"|\\[[^\\]]*\\]", "").ToLowerInvariant();
            return f.Contains('y') || (f.Contains('d') && f.Contains('m'));
        }
        var i = 0;
        foreach (var xf in styles.Root.Element(S + "cellXfs")?.Elements(S + "xf") ?? Enumerable.Empty<XElement>())
        {
            if (int.TryParse(xf.Attribute("numFmtId")?.Value, out var id) && IsDate(id)) result.Add(i);
            i++;
        }
        return result;
    }

    private static int? ColumnIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return null;
        var n = 0; var any = false;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') { n = n * 26 + (ch - 'A' + 1); any = true; }
            else break;
        }
        return any ? n - 1 : null;
    }

    /// <summary>CSV: tự nhận dấu phân cách (, ; tab), hỗ trợ ô trong ngoặc kép, UTF-8 (có / không BOM)</summary>
    public static List<string[]> ReadCsv(Stream stream, int maxRows)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var firstLine = text.Split('\n').FirstOrDefault() ?? "";
        var delim = new[] { ';', '\t', ',' }.OrderByDescending(d => firstLine.Count(c => c == d)).First();
        var rows = new List<string[]>();
        var cur = new List<string>(); var sb = new StringBuilder(); var inQ = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQ)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') inQ = false;
                else sb.Append(ch);
            }
            else if (ch == '"') inQ = true;
            else if (ch == delim) { cur.Add(sb.ToString().Trim()); sb.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                cur.Add(sb.ToString().Trim()); sb.Clear();
                if (cur.Any(x => x.Length > 0)) rows.Add(cur.ToArray());
                cur = new List<string>();
                if (rows.Count > maxRows) return rows;
            }
            else sb.Append(ch);
        }
        cur.Add(sb.ToString().Trim());
        if (cur.Any(x => x.Length > 0)) rows.Add(cur.ToArray());
        return rows;
    }
}
