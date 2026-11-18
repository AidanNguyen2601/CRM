using System.IO.Compression;
using System.Security;
using System.Text;

namespace CRM.Web.Infrastructure.Reports;

/// <summary>
/// Xuất báo cáo ra file Excel .xlsx (Office Open XML tối giản, không cần thư viện ngoài):
/// số lưu dạng số (cộng / lọc được trong Excel), ngày dạng ngày, tiêu đề in đậm, cố định dòng tiêu đề.
/// </summary>
public static class XlsxWriter
{
    public static byte[] Write(string title, string? subtitle, ReportResult r)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Entry(string path, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(content);
            }

            Entry("[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                "</Types>");
            Entry("_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");
            Entry("xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                $"<sheets><sheet name=\"{Esc(SheetName(title))}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Entry("xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                "</Relationships>");
            // Kiểu ô: 0 thường, 1 tiêu đề đậm có nền, 2 số nguyên #,##0, 3 số thập phân #,##0.00, 4 ngày, 5 phần trăm 0.0, 6 tiêu đề báo cáo (to, đậm), 7 dòng tổng đậm số
            Entry("xl/styles.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<numFmts count=\"2\"><numFmt numFmtId=\"164\" formatCode=\"dd/mm/yyyy\"/><numFmt numFmtId=\"165\" formatCode=\"0.0&quot;%&quot;\"/></numFmts>" +
                "<fonts count=\"3\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"14\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE2F3F0\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
                "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
                "<border><left/><right/><top/><bottom style=\"thin\"><color rgb=\"FF94A3B8\"/></bottom><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"8\">" +
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>" +
                "<xf numFmtId=\"3\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                "<xf numFmtId=\"4\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
                "<xf numFmtId=\"3\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyNumberFormat=\"1\"/>" +
                "</cellXfs></styleSheet>");

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            var headerRow = string.IsNullOrEmpty(subtitle) ? 3 : 4;
            sb.Append($"<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"{headerRow}\" topLeftCell=\"A{headerRow + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            sb.Append("<cols>");
            for (var i = 0; i < r.Columns.Count; i++)
            {
                var maxLen = Math.Max(r.Columns[i].Title.Length,
                    r.Rows.Take(500).Select(row => ReportResult.Format(row[i], r.Columns[i].Kind).Length).DefaultIfEmpty(0).Max());
                sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{Math.Clamp(maxLen + 3, 8, 60)}\" customWidth=\"1\"/>");
            }
            sb.Append("</cols><sheetData>");

            var rowNo = 1;
            sb.Append($"<row r=\"{rowNo}\">{Str("A1", title, 6)}</row>");
            rowNo++;
            if (!string.IsNullOrEmpty(subtitle)) { sb.Append($"<row r=\"{rowNo}\">{Str($"A{rowNo}", subtitle, 0)}</row>"); rowNo++; }
            rowNo++;   // dòng trống
            sb.Append($"<row r=\"{rowNo}\">");
            for (var i = 0; i < r.Columns.Count; i++) sb.Append(Str(Ref(i, rowNo), r.Columns[i].Title, 1));
            sb.Append("</row>");

            foreach (var row in r.Rows)
            {
                rowNo++;
                sb.Append($"<row r=\"{rowNo}\">");
                for (var i = 0; i < r.Columns.Count; i++) sb.Append(Cell(Ref(i, rowNo), row[i], r.Columns[i].Kind, false));
                sb.Append("</row>");
            }
            if (r.Totals != null)
            {
                rowNo++;
                sb.Append($"<row r=\"{rowNo}\">");
                for (var i = 0; i < r.Columns.Count; i++) sb.Append(Cell(Ref(i, rowNo), r.Totals[i], r.Columns[i].Kind, true));
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            Entry("xl/worksheets/sheet1.xml", sb.ToString());
        }
        return ms.ToArray();
    }

    private static string Cell(string reference, object? v, ReportColumnKind kind, bool bold)
    {
        if (v is null or DBNull) return "";
        if (v is DateTime d) return $"<c r=\"{reference}\" s=\"4\"><v>{d.ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c>";
        if (v is bool b) return $"<c r=\"{reference}\" t=\"b\"><v>{(b ? 1 : 0)}</v></c>";
        var n = ReportResult.ToDecimal(v);
        if (n.HasValue && (kind != ReportColumnKind.Text || v is not string))
        {
            var style = bold ? 7 : kind == ReportColumnKind.Percent ? 5 : n.Value == Math.Round(n.Value) ? 2 : 3;
            return $"<c r=\"{reference}\" s=\"{style}\"><v>{n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c>";
        }
        return Str(reference, v.ToString() ?? "", bold ? 1 : 0);
    }

    private static string Str(string reference, string text, int style) =>
        $"<c r=\"{reference}\" t=\"inlineStr\" s=\"{style}\"><is><t xml:space=\"preserve\">{Esc(Clean(text))}</t></is></c>";

    private static string Ref(int col, int row)
    {
        var name = "";
        for (var c = col + 1; c > 0; c = (c - 1) / 26) name = (char)('A' + (c - 1) % 26) + name;
        return name + row;
    }

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    /// <summary>Loại ký tự điều khiển không hợp lệ trong XML</summary>
    private static string Clean(string s) => new(s.Where(ch => ch == '\t' || ch == '\n' || ch == '\r' || ch >= ' ').ToArray());

    /// <summary>Tên sheet Excel: tối đa 31 ký tự, không chứa : \ / ? * [ ]</summary>
    private static string SheetName(string title)
    {
        var s = new string(title.Where(ch => !":\\/?*[]".Contains(ch)).ToArray()).Trim();
        return string.IsNullOrEmpty(s) ? "Report" : s.Length > 31 ? s[..31] : s;
    }
}
