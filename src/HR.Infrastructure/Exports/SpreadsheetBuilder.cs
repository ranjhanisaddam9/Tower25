using System.Globalization;
using ClosedXML.Excel;
using HR.Domain.Exports;
using HR.Domain.Time;

namespace HR.Infrastructure.Exports;

/// <summary>Upper bound on rows a list export reads (the lists are small; this only guards against runaway queries).</summary>
public static class ExportLimits
{
    public const int MaxRows = 100_000;
}

/// <summary>How a column's values are written: always as a typed cell, never as a formula.</summary>
public enum CellKind
{
    Text,
    Integer,
    Days,
    Usd,
    Pkr,
    Rate,
    Percent,
    Date,
}

/// <summary>One column of an exported sheet. <see cref="Value"/> returns the raw value (string, decimal, int, DateOnly) or null.</summary>
public sealed record ExportColumn<T>(string Header, CellKind Kind, Func<T, object?> Value);

/// <summary>A filter as the user applied it, shown on the About sheet and in the audit log.</summary>
public sealed record ExportFilter(string Name, string Value);

/// <summary>Who generated an export, when, and with which filters.</summary>
public sealed record ExportContext(string ReportName, string GeneratedBy, DateTimeOffset GeneratedAtUtc, IReadOnlyList<ExportFilter> Filters);

/// <summary>
/// Writes .xlsx exports (M9) with ClosedXML. Every value is a typed cell: numbers are numbers with a format, dates are
/// dates, and text is always a string value (ClosedXML never turns a string into a formula). Text that starts with
/// = + - @ tab or CR also gets Excel's quote prefix, so editing the cell keeps it as text. Each sheet has a bold, frozen
/// header row with an auto-filter; the workbook ends with an "About" sheet (generated at/by, filters, row count).
/// </summary>
public sealed class SpreadsheetBuilder(ExportContext context)
{
    public const string UsdFormat = "$#,##0.00";
    public const string PkrFormat = "\"Rs \"#,##0";
    public const string DateFormat = "dd mmm yyyy";
    public const string DateTimeFormat = "dd mmm yyyy hh:mm";
    public const string DaysFormat = "0.0";
    public const string RateFormat = "0.0000";
    public const string PercentFormat = "0.00\"%\"";
    public const string IntegerFormat = "0";
    public const string AboutSheetName = "About";

    private const double MinWidth = 8;
    private const double MaxWidth = 60;

    private readonly List<Action<XLWorkbook>> _sheets = [];

    /// <summary>Data rows on the first sheet (what the audit log records).</summary>
    public int RowCount { get; private set; } = -1;

    /// <param name="totals">Optional totals row, one value per column (the first is usually the label "Total").</param>
    public SpreadsheetBuilder AddSheet<T>(string name, IReadOnlyList<ExportColumn<T>> columns, IEnumerable<T> rows, IReadOnlyList<object?>? totals = null)
    {
        var data = rows.ToList();
        if (RowCount < 0)
        {
            RowCount = data.Count;
        }

        _sheets.Add(workbook =>
        {
            var sheet = workbook.Worksheets.Add(SheetName(name));
            var widths = columns.Select(c => (double)c.Header.Length + 4).ToArray(); // room for the filter button

            for (var c = 0; c < columns.Count; c++)
            {
                var header = sheet.Cell(1, c + 1);
                WriteText(header, columns[c].Header);
                header.Style.Font.Bold = true;
                header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E0E7FF");
                if (IsNumeric(columns[c].Kind))
                {
                    header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                }
            }

            for (var r = 0; r < data.Count; r++)
            {
                for (var c = 0; c < columns.Count; c++)
                {
                    var value = columns[c].Value(data[r]);
                    Write(sheet.Cell(r + 2, c + 1), columns[c].Kind, value);
                    widths[c] = Math.Max(widths[c], DisplayWidth(columns[c].Kind, value));
                }
            }

            var lastRow = data.Count + 1;
            if (totals is not null)
            {
                lastRow++;
                for (var c = 0; c < columns.Count && c < totals.Count; c++)
                {
                    var cell = sheet.Cell(lastRow, c + 1);
                    var kind = totals[c] is string ? CellKind.Text : columns[c].Kind;
                    Write(cell, kind, totals[c]);
                    cell.Style.Font.Bold = true;
                    cell.Style.Border.TopBorder = XLBorderStyleValues.Thin;
                    widths[c] = Math.Max(widths[c], DisplayWidth(kind, totals[c]));
                }
            }

            sheet.SheetView.FreezeRows(1);
            sheet.Range(1, 1, Math.Max(1, data.Count + 1), Math.Max(1, columns.Count)).SetAutoFilter();
            for (var c = 0; c < columns.Count; c++)
            {
                sheet.Column(c + 1).Width = Math.Clamp(widths[c], MinWidth, MaxWidth);
            }
        });

        return this;
    }

    public byte[] Build()
    {
        using var workbook = new XLWorkbook();
        workbook.Properties.Title = context.ReportName;
        workbook.Properties.Author = "HR Payroll";
        foreach (var sheet in _sheets)
        {
            sheet(workbook);
        }

        AddAbout(workbook);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>A typed cell. Exposed for the unit tests of the injection and format rules.</summary>
    public static void Write(IXLCell cell, CellKind kind, object? value)
    {
        if (value is null)
        {
            return;
        }

        switch (kind)
        {
            case CellKind.Text:
                WriteText(cell, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                return;
            case CellKind.Date:
                switch (value)
                {
                    case DateOnly date:
                        cell.Value = date.ToDateTime(TimeOnly.MinValue);
                        cell.Style.NumberFormat.Format = DateFormat;
                        return;
                    case DateTimeOffset instant:
                        cell.Value = TimeZoneInfo.ConvertTime(instant, PakistanTime.Zone).DateTime;
                        cell.Style.NumberFormat.Format = DateTimeFormat;
                        return;
                    default:
                        throw new ArgumentException($"A date column got a {value.GetType().Name}.", nameof(value));
                }

            default:
                cell.Value = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                cell.Style.NumberFormat.Format = kind switch
                {
                    CellKind.Integer => IntegerFormat,
                    CellKind.Days => DaysFormat,
                    CellKind.Usd => UsdFormat,
                    CellKind.Pkr => PkrFormat,
                    CellKind.Rate => RateFormat,
                    _ => PercentFormat,
                };
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                return;
        }
    }

    /// <summary>Text is a string value, never a formula; formula-like text is also quote-prefixed.</summary>
    public static void WriteText(IXLCell cell, string text)
    {
        cell.Value = text;
        if (SpreadsheetText.IsFormulaLike(text))
        {
            cell.Style.IncludeQuotePrefix = true;
        }
    }

    /// <summary>Excel sheet names: at most 31 characters, none of : \ / ? * [ ].</summary>
    public static string SheetName(string name)
    {
        var cleaned = new string(name.Select(c => c is ':' or '\\' or '/' or '?' or '*' or '[' or ']' ? '-' : c).ToArray()).Trim('\'', ' ');
        cleaned = cleaned.Length > 31 ? cleaned[..31] : cleaned;
        return cleaned.Length == 0 ? "Sheet" : cleaned;
    }

    private void AddAbout(XLWorkbook workbook)
    {
        var sheet = workbook.Worksheets.Add(AboutSheetName);
        var rows = new List<(string Item, CellKind Kind, object Value)>
        {
            ("Report", CellKind.Text, context.ReportName),
            ("Generated at (Asia/Karachi)", CellKind.Date, context.GeneratedAtUtc),
            ("Generated by", CellKind.Text, context.GeneratedBy),
            ("Rows", CellKind.Integer, Math.Max(0, RowCount)),
        };
        rows.AddRange(context.Filters.Select(f => ("Filter: " + f.Name, CellKind.Text, (object)f.Value)));

        WriteText(sheet.Cell(1, 1), "Item");
        WriteText(sheet.Cell(1, 2), "Value");
        sheet.Row(1).Style.Font.Bold = true;
        for (var i = 0; i < rows.Count; i++)
        {
            WriteText(sheet.Cell(i + 2, 1), rows[i].Item);
            Write(sheet.Cell(i + 2, 2), rows[i].Kind, rows[i].Value);
            sheet.Cell(i + 2, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        }

        sheet.SheetView.FreezeRows(1);
        sheet.Column(1).Width = 30;
        sheet.Column(2).Width = Math.Clamp(rows.Max(r => (double)(Convert.ToString(r.Value, CultureInfo.InvariantCulture)?.Length ?? 0)) + 4, 24, MaxWidth);
    }

    private static bool IsNumeric(CellKind kind) => kind is not (CellKind.Text or CellKind.Date);

    private static double DisplayWidth(CellKind kind, object? value) => value switch
    {
        null => MinWidth,
        _ when kind == CellKind.Date => 14,
        _ when kind == CellKind.Text => (Convert.ToString(value, CultureInfo.InvariantCulture)?.Length ?? 0) + 2,
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("#,##0.00", CultureInfo.InvariantCulture).Length + 5,
    };
}
