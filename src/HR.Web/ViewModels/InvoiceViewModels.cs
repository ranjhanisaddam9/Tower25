using System.ComponentModel.DataAnnotations;
using HR.Domain.Invoices;
using HR.Infrastructure.Invoices;

namespace HR.Web.ViewModels;

public static class InvoiceDisplay
{
    /// <summary>Overdue is shown instead of Issued when unpaid past the due date.</summary>
    public static string Label(InvoiceStatus status, bool overdue) => status switch
    {
        InvoiceStatus.Paid => "Paid",
        InvoiceStatus.Void => "Void",
        _ => overdue ? "Overdue" : "Issued",
    };

    public static string Pill(InvoiceStatus status, bool overdue) => status switch
    {
        InvoiceStatus.Paid => "pill-success",
        InvoiceStatus.Void => "pill-neutral",
        _ => overdue ? "pill-danger" : "pill-info",
    };

    public static string Lines(string? text) => text ?? string.Empty;
}

public sealed record InvoiceListViewModel(InvoiceList List, InvoiceStatusFilter Filter, int? Year, bool CanIssueInvoices)
{
    public bool HasFilter => Filter != InvoiceStatusFilter.All || Year is not null;
}

public sealed class MarkPaidForm
{
    [DataType(DataType.Date)]
    public DateOnly? PaidDate { get; set; }

    public decimal? AmountReceivedUsd { get; set; }

    [StringLength(Invoice.NoteMaxLength, ErrorMessage = "The note can be at most 300 characters.")]
    public string? PaymentNote { get; set; }

    public string? RowVersion { get; set; }
}

public sealed record InvoiceViewModel(InvoiceDetails Details, MarkPaidForm PaidForm, bool ShowPayment = false)
{
    public Invoice Invoice => Details.Invoice;
}
