using HR.Domain.Absences;
using HR.Domain.Exports;
using HR.Domain.Invoices;
using HR.Domain.Pay;
using HR.Domain.Payroll;
using HR.Domain.People;
using HR.Infrastructure.Exports;
using HR.Infrastructure.Payroll;
using HR.Web.Formatting;
using HR.Web.ViewModels;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace HR.Web.Exports;

/// <summary>
/// A4 PDF downloads (M9 Part D), laid out like the print pages (no Aurora styling). The invoice is built from the
/// invoice's own snapshot only; payslips and the register from the payroll's frozen lines. Draft payrolls carry a
/// "DRAFT – NOT FINAL" watermark. Payslips never show billing, for any role.
/// </summary>
public static class PdfDocuments
{
    public const string DraftWatermark = "DRAFT – NOT FINAL";
    public const string VoidWatermark = "VOID";

    private static readonly string Ink = "#0F172A";
    private static readonly string Muted = "#475569";
    private static readonly string Rule = "#CBD5E1";
    private static readonly string HeaderFill = "#EEF2FF";

    // ===================== Invoice =====================

    public static ExportFile Invoice(Invoice inv, bool isOverdue, string? replacesNumber)
    {
        var lines = inv.Lines.OrderBy(l => l.Position).ToList();
        var bytes = Document.Create(document => document.Page(page =>
        {
            Setup(page, inv.Status == InvoiceStatus.Void ? VoidWatermark : null);
            // The parties appear once, at the top of the first page; later pages repeat only the table header.
            void Parties(ColumnDescriptor header)
            {
                header.Item().Row(row =>
                {
                    row.RelativeItem().Column(from =>
                    {
                        from.Item().Text("From").FontSize(8).FontColor(Muted);
                        from.Item().Text(inv.BusinessName).SemiBold().FontSize(12);
                        foreach (var line in LinesOf(inv.BusinessAddress).Concat(Optional(inv.BusinessEmail)).Concat(Optional(inv.BusinessPhone)))
                        {
                            from.Item().Text(line).FontColor(Muted);
                        }
                    });
                    row.ConstantItem(200).AlignRight().Column(meta =>
                    {
                        meta.Item().AlignRight().Text("Invoice").Bold().FontSize(20);
                        Fact(meta, "Number", inv.Number);
                        Fact(meta, "Issue date", DisplayFormat.Date(inv.IssueDate));
                        Fact(meta, "Due date", DisplayFormat.Date(inv.DueDate));
                        Fact(meta, "Period", DisplayFormat.DateRange(inv.PeriodStart, inv.PeriodEnd));
                        Fact(meta, "Status", InvoiceDisplay.Label(inv.Status, isOverdue));
                        if (replacesNumber is not null)
                        {
                            meta.Item().AlignRight().Text($"Replaces {replacesNumber} (void).").FontSize(8).FontColor(Muted);
                        }
                    });
                });
                header.Item().PaddingTop(14).Column(to =>
                {
                    to.Item().Text("Bill to").FontSize(8).FontColor(Muted);
                    to.Item().Text(inv.ClientName).SemiBold().FontSize(12);
                    if (inv.ClientContactPerson is { } contact)
                    {
                        to.Item().Text("Attn: " + contact).FontColor(Muted);
                    }

                    foreach (var line in LinesOf(inv.ClientAddress).Concat(Optional(inv.ClientEmail)))
                    {
                        to.Item().Text(line).FontColor(Muted);
                    }
                });
            }

            page.Content().Column(content =>
            {
                content.Item().Column(Parties);
                content.Item().PaddingTop(14).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(24);
                        c.RelativeColumn(3);
                        c.RelativeColumn(3);
                        c.ConstantColumn(44);
                        c.RelativeColumn(2);
                        c.RelativeColumn(2);
                        c.RelativeColumn(2);
                    });
                    table.Header(h =>
                    {
                        HeadCell(h.Cell(), "#", right: true);
                        HeadCell(h.Cell(), "Name");
                        HeadCell(h.Cell(), "Designation");
                        HeadCell(h.Cell(), "Days", right: true);
                        HeadCell(h.Cell(), "Salary (USD)", right: true);
                        HeadCell(h.Cell(), "Extras (USD)", right: true);
                        HeadCell(h.Cell(), "Amount (USD)", right: true);
                    });
                    foreach (var line in lines)
                    {
                        BodyCell(table.Cell(), line.Position.ToString(System.Globalization.CultureInfo.InvariantCulture), right: true);
                        BodyCell(table.Cell(), line.Name);
                        BodyCell(table.Cell(), line.Designation);
                        BodyCell(table.Cell(), line.DaysText, right: true);
                        BodyCell(table.Cell(), DisplayFormat.Usd(line.SalaryUsd), right: true);
                        BodyCell(table.Cell(), line.ExtrasUsd == 0m ? "-" : DisplayFormat.Usd(line.ExtrasUsd), right: true);
                        BodyCell(table.Cell(), DisplayFormat.Usd(line.AmountUsd), right: true);
                    }

                    table.Cell().ColumnSpan(6).BorderTop(1).BorderColor(Ink).PaddingVertical(5).AlignRight().Text("Total (USD)").Bold();
                    table.Cell().BorderTop(1).BorderColor(Ink).PaddingVertical(5).AlignRight().Text(DisplayFormat.Usd(inv.TotalUsd)).Bold();
                });

                if (inv.BankName is not null || inv.BankAccountNumber is not null)
                {
                    content.Item().PaddingTop(18).Column(pay =>
                    {
                        pay.Item().Text("Payment details").FontSize(8).FontColor(Muted);
                        if (inv.BankName is { } bank)
                        {
                            Pair(pay, "Bank", bank);
                        }

                        if (inv.BankAccountTitle is { } title)
                        {
                            Pair(pay, "Account title", title);
                        }

                        if (inv.BankAccountNumber is { } account)
                        {
                            Pair(pay, "Account / IBAN", account.StartsWith("PK", StringComparison.Ordinal) && account.Length == 24 ? PakistaniIban.Format(account) : account);
                        }

                        if (inv.BankSwift is { } swift)
                        {
                            Pair(pay, "SWIFT", swift);
                        }
                    });
                }

                if (inv.Footer is { } footer)
                {
                    content.Item().PaddingTop(18).Column(f =>
                    {
                        foreach (var line in LinesOf(footer))
                        {
                            f.Item().Text(line).FontColor(Muted);
                        }
                    });
                }
            });
            PageNumbers(page, "Invoice " + inv.Number);
        })).GeneratePdf();

        return new ExportFile(bytes, ExportFileName.Build("pdf", inv.Number), ExportContentTypes.Pdf, lines.Count);
    }

    // ===================== Payslips =====================

    /// <summary>One A4 page per line, in the given order.</summary>
    public static ExportFile Payslips(PayrollRunHeader run, IReadOnlyList<PayrollLineDetail> details, string issuer, string fileName)
    {
        var bytes = Document.Create(document =>
        {
            foreach (var detail in details)
            {
                document.Page(page =>
                {
                    Setup(page, run.IsDraft ? DraftWatermark : null);
                    page.Content().Element(c => Payslip(c, run, detail, issuer));
                    PageNumbers(page);
                });
            }
        }).GeneratePdf();
        return new ExportFile(bytes, fileName, ExportContentTypes.Pdf, details.Count);
    }

    private static void Payslip(IContainer container, PayrollRunHeader run, PayrollLineDetail detail, string issuer)
    {
        var l = detail.Line;
        var paid = detail.Absences.Sum(a => a.PaidDays);
        var unpaid = detail.Absences.Sum(a => a.UnpaidDays);
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(head =>
                {
                    head.Item().Text(issuer).FontColor(Muted);
                    head.Item().Text("Payslip").Bold().FontSize(20);
                    head.Item().Text(DisplayFormat.DateRange(run.Period.Start, run.Period.End));
                });
                if (run.IsDraft)
                {
                    row.ConstantItem(150).AlignRight().AlignMiddle().Text(DraftWatermark).Bold().FontColor(Colors.Red.Darken2);
                }
            });

            col.Item().PaddingTop(14).Table(t =>
            {
                t.ColumnsDefinition(c =>
                {
                    c.RelativeColumn();
                    c.RelativeColumn();
                    c.RelativeColumn();
                    c.RelativeColumn();
                });
                foreach (var label in new[] { "Name", "Code", "Designation", "Type" })
                {
                    t.Cell().PaddingBottom(2).Text(label).FontSize(8).FontColor(Muted);
                }

                foreach (var value in new[] { l.PersonName, l.PersonCode, l.Designation, l.PersonType.ToString() })
                {
                    t.Cell().Text(value).SemiBold();
                }
            });

            Section(col, "Days");
            col.Item().Table(t =>
            {
                TwoColumns(t);
                Row(t, "Working days in the period", l.WorkingDays.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Row(t, "Employed working days", l.EmployedWorkingDays.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Row(t, "Absences: paid leave", PayrollDisplay.Days(paid));
                Row(t, "Absences: unpaid", "- " + PayrollDisplay.Days(unpaid));
                Row(t, "Extra days worked", "+ " + PayrollDisplay.Days(l.ExtraDays));
                Row(t, "Payable days", $"{PayrollDisplay.Days(l.PayableDays)} of {l.WorkingDays}", total: true);
            });
            if (detail.Absences.Count > 0)
            {
                col.Item().PaddingTop(4).Text("Absences: " + string.Join("; ", detail.Absences.Select(a =>
                    $"{DisplayFormat.Date(a.Date)} {(a.Portion == AbsencePortion.Full ? "full" : "half")} ({AbsenceDisplay.Split(a.PaidDays, a.UnpaidDays)})"))).FontSize(8).FontColor(Muted);
            }

            Section(col, "Pay");
            col.Item().Table(t =>
            {
                TwoColumns(t);
                Row(t, "Monthly pay", l.PayMonthlyAmount is { } m && l.PayCurrency is { } c ? DisplayFormat.Money(m, c) : "-");
                Row(t, $"Base pay for the period ({PayrollDisplay.Fraction(l.PayableDays, l.WorkingDays)} of half a month)", PayrollDisplay.Pkr(l.PayPkr));
                foreach (var a in detail.Adjustments)
                {
                    var sign = a.Type == AdjustmentType.Deduction ? "- " : "+ ";
                    var label = PayrollDisplay.AdjustmentLabel(a.Type) + (a.Note is { } n ? ": " + n : "") + (a.Currency == PayCurrency.USD ? $" ({DisplayFormat.Usd(a.Amount)})" : "");
                    Row(t, label, sign + PayrollDisplay.Pkr(a.AmountPkr));
                }

                Row(t, "Net pay", PayrollDisplay.Pkr(l.NetPayPkr), total: true);
            });

            col.Item().PaddingTop(8).Text(
                $"Exchange rate used: {(run.ExchangeRate is { } r ? "1 USD = " + PayrollDisplay.Rate(r) : "not set")} · USD reference: {PayrollDisplay.Usd(l.NetPayUsd)}")
                .FontSize(8).FontColor(Muted);
        });
    }

    // ===================== Register =====================

    public static ExportFile Register(PayrollRunHeader run, IReadOnlyList<RegisterRow> rows, string issuer, string fileName)
    {
        var bytes = Document.Create(document => document.Page(page =>
        {
            Setup(page, run.IsDraft ? DraftWatermark : null);
            page.Header().Column(h =>
            {
                h.Item().Text(issuer).FontColor(Muted);
                h.Item().Text("Payroll register").Bold().FontSize(18);
                h.Item().Text($"{DisplayFormat.DateRange(run.Period.Start, run.Period.End)} · {(run.IsDraft ? DraftWatermark : "Finalized")}");
            });
            page.Content().PaddingVertical(12).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(58);
                    c.RelativeColumn(3);
                    c.RelativeColumn(2);
                    c.ConstantColumn(188); // a grouped IBAN fits on one line
                    c.ConstantColumn(78);
                });
                table.Header(h =>
                {
                    HeadCell(h.Cell(), "Code");
                    HeadCell(h.Cell(), "Name");
                    HeadCell(h.Cell(), "Bank");
                    HeadCell(h.Cell(), "IBAN");
                    HeadCell(h.Cell(), "Net pay (PKR)", right: true);
                });
                foreach (var r in rows)
                {
                    BodyCell(table.Cell(), r.PersonCode);
                    BodyCell(table.Cell(), r.PersonName);
                    BodyCell(table.Cell(), r.BankName ?? "-");
                    BodyCell(table.Cell(), r.Iban is { } iban ? PakistaniIban.Format(iban) : "-");
                    BodyCell(table.Cell(), PayrollDisplay.Pkr(r.NetPayPkr), right: true);
                }

                table.Cell().ColumnSpan(4).BorderTop(1).BorderColor(Ink).PaddingVertical(5).AlignRight().Text("Total").Bold();
                table.Cell().BorderTop(1).BorderColor(Ink).PaddingVertical(5).AlignRight().Text(DisplayFormat.Pkr(rows.Sum(r => r.NetPayPkr ?? 0m))).Bold();
            });
            PageNumbers(page);
        })).GeneratePdf();
        return new ExportFile(bytes, fileName, ExportContentTypes.Pdf, rows.Count);
    }

    // ===================== Shared layout =====================

    private static void Setup(PageDescriptor page, string? watermark)
    {
        page.Size(PageSizes.A4);
        page.Margin(40);
        page.PageColor(Colors.White);
        page.DefaultTextStyle(s => s.FontFamily(PdfSetup.FontFamily).FontSize(9.5f).FontColor(Ink));
        if (watermark is not null)
        {
            page.Foreground().AlignCenter().AlignMiddle().Text(watermark).FontSize(54).Bold().FontColor("#FECACA");
        }
    }

    /// <param name="label">Shown before the page number, e.g. the invoice number, so a loose later page is still identifiable.</param>
    private static void PageNumbers(PageDescriptor page, string? label = null) =>
        page.Footer().AlignCenter().Text(t =>
        {
            t.DefaultTextStyle(s => s.FontSize(8).FontColor(Muted));
            if (label is not null)
            {
                t.Span(label + " · ");
            }

            t.Span("Page ");
            t.CurrentPageNumber();
            t.Span(" of ");
            t.TotalPages();
        });

    private static void HeadCell(IContainer cell, string text, bool right = false)
    {
        var c = cell.Background(HeaderFill).BorderBottom(1).BorderColor(Rule).PaddingVertical(5).PaddingHorizontal(4);
        (right ? c.AlignRight() : c).Text(text).SemiBold().FontSize(8.5f);
    }

    private static void BodyCell(IContainer cell, string text, bool right = false)
    {
        // ShowEntire keeps a wrapped row together instead of splitting it across a page break.
        var c = cell.ShowEntire().BorderBottom(0.5f).BorderColor(Rule).PaddingVertical(4).PaddingHorizontal(4);
        (right ? c.AlignRight() : c).Text(text);
    }

    private static void Fact(ColumnDescriptor column, string label, string value) =>
        column.Item().AlignRight().Text(t =>
        {
            t.Span(label + "  ").FontSize(8).FontColor(Muted);
            t.Span(value).SemiBold();
        });

    private static void Pair(ColumnDescriptor column, string label, string value) =>
        column.Item().Text(t =>
        {
            t.Span(label + ": ").FontColor(Muted);
            t.Span(value);
        });

    private static void Section(ColumnDescriptor column, string title) =>
        column.Item().PaddingTop(14).PaddingBottom(4).Text(title).SemiBold().FontSize(11);

    private static void TwoColumns(TableDescriptor table) =>
        table.ColumnsDefinition(c =>
        {
            c.RelativeColumn(3);
            c.RelativeColumn(1);
        });

    private static void Row(TableDescriptor table, string label, string value, bool total = false)
    {
        var left = table.Cell().BorderBottom(total ? 0 : 0.5f).BorderTop(total ? 1 : 0).BorderColor(total ? Ink : Rule).PaddingVertical(4);
        var right = table.Cell().BorderBottom(total ? 0 : 0.5f).BorderTop(total ? 1 : 0).BorderColor(total ? Ink : Rule).PaddingVertical(4).AlignRight();
        if (total)
        {
            left.Text(label).Bold();
            right.Text(value).Bold();
        }
        else
        {
            left.Text(label);
            right.Text(value);
        }
    }

    private static IEnumerable<string> LinesOf(string? text) =>
        (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> Optional(string? value) => value is null ? [] : [value];
}
