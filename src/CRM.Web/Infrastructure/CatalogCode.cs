using System.Globalization;
using System.Text;

namespace CRM.Web.Infrastructure;

/// <summary>
/// Gợi ý mã danh mục từ tên: lấy chữ cái đầu mỗi từ (bỏ dấu, viết hoa).
/// VD: "Hà Nội" → HN, "TP. Hồ Chí Minh" → TPHCM, "Tài chính – Ngân hàng" → TCNH, "Microsoft" → MICROSOFT.
/// Trùng mã trong nhóm → thêm hậu tố số (HN2, HN3...).
/// </summary>
public static class CatalogCode
{
    public const int MaxLength = 30;

    public static string Suggest(string name, ICollection<string> existing)
    {
        var words = Words(name);
        string code;
        if (words.Count == 0) code = "MUC";
        else if (words.Count == 1) code = words[0].Length > 10 ? words[0][..10] : words[0];
        // Số và từ viết tắt viết hoa sẵn (TP, CNTT...) giữ nguyên; từ thường lấy chữ cái đầu
        else code = string.Concat(words.Select(w => char.IsDigit(w[0]) || (w.Length <= 4 && w.All(char.IsUpper)) ? w : w[..1]));
        code = code.ToUpperInvariant();
        if (code.Length > MaxLength - 3) code = code[..(MaxLength - 3)];

        var result = code;
        for (var i = 2; existing.Contains(result, StringComparer.OrdinalIgnoreCase); i++) result = code + i;
        return result;
    }

    /// <summary>Chuẩn hoá mã người dùng nhập: bỏ khoảng trắng hai đầu, viết hoa</summary>
    public static string Normalize(string? code) => (code ?? "").Trim().ToUpperInvariant();

    private static List<string> Words(string s)
    {
        var norm = (s ?? "").Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in norm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) && ch < 128 ? ch : ' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
