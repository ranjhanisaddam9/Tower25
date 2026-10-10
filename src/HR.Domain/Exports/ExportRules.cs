using System.Text;

namespace HR.Domain.Exports;

/// <summary>
/// Spreadsheet (formula) injection (OWASP "CSV injection"): text that starts with one of these characters can be read as
/// a formula by Excel or LibreOffice when a user edits the cell. Exports always write text as a string value, never a
/// formula, and additionally mark these cells with Excel's quote prefix so editing them keeps them as text.
/// </summary>
public static class SpreadsheetText
{
    private static readonly char[] Triggers = ['=', '+', '-', '@', '\t', '\r'];

    public static bool IsFormulaLike(string? value) => !string.IsNullOrEmpty(value) && Triggers.Contains(value[0]);
}

/// <summary>Builds safe ASCII download names such as "payroll-2026-10-16.xlsx".</summary>
public static class ExportFileName
{
    public const int MaxStemLength = 80;

    /// <summary>
    /// Joins the parts with "-", keeps only ASCII letters, digits and "-" (everything else becomes "-"), collapses repeats and
    /// appends the extension. Never empty, never contains quotes, slashes or non-ASCII characters.
    /// </summary>
    public static string Build(string extension, params string?[] parts)
    {
        var stem = new StringBuilder();
        foreach (var part in parts.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (stem.Length > 0)
            {
                stem.Append('-');
            }

            foreach (var c in part!.Trim())
            {
                stem.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
            }
        }

        var cleaned = Collapse(stem.ToString());
        if (cleaned.Length > MaxStemLength)
        {
            cleaned = cleaned[..MaxStemLength].TrimEnd('-');
        }

        var ext = new string(extension.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return (cleaned.Length == 0 ? "export" : cleaned) + (ext.Length == 0 ? string.Empty : "." + ext);
    }

    private static string Collapse(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == '-' && (result.Length == 0 || result[^1] == '-'))
            {
                continue;
            }

            result.Append(c);
        }

        return result.ToString().TrimEnd('-');
    }
}
