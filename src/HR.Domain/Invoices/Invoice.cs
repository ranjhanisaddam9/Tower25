using HR.Domain.Settings;

namespace HR.Domain.Invoices;

public enum InvoiceStatus
{
    Issued = 1,
    Paid = 2,
    Void = 3,
}

/// <summary>One invoice row's money: salary (BilledUsd), extras (Σ s·AmountUsd) and amount (InvoiceUsd) (SPEC §7).</summary>
public sealed record InvoiceLineAmounts(string DaysText, decimal SalaryUsd, decimal ExtrasUsd, decimal AmountUsd);

/// <summary>Pure invoice rules (M8).</summary>
public static class InvoiceMath
{
    /// <summary>"&lt;prefix&gt;-YYYY-NNNN", e.g. INV-2026-0007.</summary>
    public static string Number(string prefix, int year, int sequence) =>
        $"{prefix}-{year:0000}-{sequence.ToString("0000", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>Due date = issue date + payment terms (calendar days).</summary>
    public static DateOnly DueDate(DateOnly issueDate, int paymentTermsDays) => issueDate.AddDays(paymentTermsDays);

    /// <summary>Unpaid and past its due date (the due date itself is not overdue).</summary>
    public static bool IsOverdue(InvoiceStatus status, DateOnly dueDate, DateOnly today) => status == InvoiceStatus.Issued && today > dueDate;

    /// <summary>
    /// The invoice row for a finalized payroll line. The amount is the line's InvoiceUsd; salary is BilledUsd and extras are
    /// the pass-through adjustments, so Salary + Extras = Amount.
    /// </summary>
    public static InvoiceLineAmounts Line(decimal billedUsd, decimal adjustmentsUsd, decimal invoiceUsd, decimal payableDays, int workingDays)
    {
        if (billedUsd + adjustmentsUsd != invoiceUsd)
        {
            throw new InvalidOperationException("Salary plus extras must equal the invoiced amount.");
        }

        return new InvoiceLineAmounts($"{payableDays.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}/{workingDays}", billedUsd, adjustmentsUsd, invoiceUsd);
    }
}

/// <summary>Per-year invoice numbering: each year starts again at 1.</summary>
public sealed class InvoiceCounter
{
    private InvoiceCounter()
    {
    }

    public InvoiceCounter(int year)
    {
        Year = year;
    }

    public int Year { get; private set; }

    public int LastNumber { get; private set; }

    public int Next() => ++LastNumber;
}

/// <summary>Business or client details copied onto an invoice when it is issued.</summary>
public sealed record InvoiceParties(
    string BusinessName,
    string? BusinessAddress,
    string? BusinessEmail,
    string? BusinessPhone,
    string? BankName,
    string? BankAccountTitle,
    string? BankAccountNumber,
    string? BankSwift,
    string ClientName,
    string? ClientAddress,
    string? ClientContactPerson,
    string? ClientEmail,
    string? Footer)
{
    public static InvoiceParties From(AppSettings s) => new(
        s.BusinessName ?? throw new InvalidOperationException("The business name is not set."),
        s.BusinessAddress, s.BusinessEmail, s.BusinessPhone, s.BankName, s.BankAccountTitle, s.BankAccountNumber, s.BankSwift,
        s.ClientName ?? throw new InvalidOperationException("The client name is not set."),
        s.ClientAddress, s.ClientContactPerson, s.ClientEmail, s.InvoiceFooter);
}

/// <summary>
/// The company invoice for one finalized payroll (SPEC §7). Issued → Paid (and back), or Void when its payroll is
/// reopened; a re-finalized payroll gets a replacement that points at the voided one. Business and client details are a
/// snapshot taken at issue time.
/// </summary>
public sealed class Invoice
{
    public const int NoteMaxLength = 300;

    private readonly List<InvoiceLine> _lines = [];

    private Invoice()
    {
    }

    public int Id { get; private set; }

    public string Number { get; private set; } = string.Empty;

    public int RunId { get; private set; }

    public DateOnly PeriodStart { get; private set; }

    public DateOnly PeriodEnd { get; private set; }

    public InvoiceStatus Status { get; private set; }

    public DateOnly IssueDate { get; private set; }

    public DateOnly DueDate { get; private set; }

    public string BusinessName { get; private set; } = string.Empty;

    public string? BusinessAddress { get; private set; }

    public string? BusinessEmail { get; private set; }

    public string? BusinessPhone { get; private set; }

    public string? BankName { get; private set; }

    public string? BankAccountTitle { get; private set; }

    public string? BankAccountNumber { get; private set; }

    public string? BankSwift { get; private set; }

    public string ClientName { get; private set; } = string.Empty;

    public string? ClientAddress { get; private set; }

    public string? ClientContactPerson { get; private set; }

    public string? ClientEmail { get; private set; }

    public string? Footer { get; private set; }

    public decimal TotalUsd { get; private set; }

    public DateOnly? PaidDate { get; private set; }

    public decimal? AmountReceivedUsd { get; private set; }

    public string? PaymentNote { get; private set; }

    public DateTimeOffset? VoidedAt { get; private set; }

    public string? VoidedByUserId { get; private set; }

    public string? VoidReason { get; private set; }

    public int? ReplacesInvoiceId { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public string IssuedByUserId { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    public string UpdatedByUserId { get; private set; } = string.Empty;

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyCollection<InvoiceLine> Lines => _lines;

    public static Invoice Issue(
        string number,
        int runId,
        DateOnly periodStart,
        DateOnly periodEnd,
        DateOnly issueDate,
        int paymentTermsDays,
        InvoiceParties parties,
        IEnumerable<InvoiceLine> lines,
        int? replacesInvoiceId,
        string actorId,
        DateTimeOffset now)
    {
        var invoice = new Invoice
        {
            Number = number,
            RunId = runId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            Status = InvoiceStatus.Issued,
            IssueDate = issueDate,
            DueDate = InvoiceMath.DueDate(issueDate, paymentTermsDays),
            BusinessName = parties.BusinessName,
            BusinessAddress = parties.BusinessAddress,
            BusinessEmail = parties.BusinessEmail,
            BusinessPhone = parties.BusinessPhone,
            BankName = parties.BankName,
            BankAccountTitle = parties.BankAccountTitle,
            BankAccountNumber = parties.BankAccountNumber,
            BankSwift = parties.BankSwift,
            ClientName = parties.ClientName,
            ClientAddress = parties.ClientAddress,
            ClientContactPerson = parties.ClientContactPerson,
            ClientEmail = parties.ClientEmail,
            Footer = parties.Footer,
            ReplacesInvoiceId = replacesInvoiceId,
            IssuedAt = now,
            IssuedByUserId = actorId,
            UpdatedAt = now,
            UpdatedByUserId = actorId,
        };
        invoice._lines.AddRange(lines);
        invoice.TotalUsd = invoice._lines.Sum(l => l.AmountUsd);
        return invoice;
    }

    public void MarkPaid(DateOnly paidDate, decimal amountReceivedUsd, string? note, string actorId, DateTimeOffset now)
    {
        if (Status != InvoiceStatus.Issued)
        {
            throw new InvalidOperationException("Only an issued invoice can be marked paid.");
        }

        if (amountReceivedUsd <= 0m || amountReceivedUsd != Math.Round(amountReceivedUsd, 2))
        {
            throw new ArgumentOutOfRangeException(nameof(amountReceivedUsd), amountReceivedUsd, "Enter the amount received in USD (cents allowed).");
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > NoteMaxLength })
        {
            throw new ArgumentException("The note can be at most 300 characters.", nameof(note));
        }

        Status = InvoiceStatus.Paid;
        PaidDate = paidDate;
        AmountReceivedUsd = amountReceivedUsd;
        PaymentNote = trimmed;
        Touch(actorId, now);
    }

    public void MarkUnpaid(string actorId, DateTimeOffset now)
    {
        if (Status != InvoiceStatus.Paid)
        {
            throw new InvalidOperationException("Only a paid invoice can be marked unpaid.");
        }

        Status = InvoiceStatus.Issued;
        PaidDate = null;
        AmountReceivedUsd = null;
        PaymentNote = null;
        Touch(actorId, now);
    }

    public void Void(string reason, string actorId, DateTimeOffset now)
    {
        if (Status != InvoiceStatus.Issued)
        {
            throw new InvalidOperationException(Status == InvoiceStatus.Paid ? "Mark the invoice unpaid first." : "This invoice is already void.");
        }

        Status = InvoiceStatus.Void;
        VoidedAt = now;
        VoidedByUserId = actorId;
        VoidReason = reason.Length > 500 ? reason[..500] : reason;
        Touch(actorId, now);
    }

    private void Touch(string actorId, DateTimeOffset now)
    {
        UpdatedAt = now;
        UpdatedByUserId = actorId;
    }
}

/// <summary>One person's row on an invoice (ordered by name).</summary>
public sealed class InvoiceLine
{
    private InvoiceLine()
    {
    }

    public InvoiceLine(int position, int personId, string name, string designation, InvoiceLineAmounts amounts)
    {
        Position = position;
        PersonId = personId;
        Name = name;
        Designation = designation;
        DaysText = amounts.DaysText;
        SalaryUsd = amounts.SalaryUsd;
        ExtrasUsd = amounts.ExtrasUsd;
        AmountUsd = amounts.AmountUsd;
    }

    public int Id { get; private set; }

    public int InvoiceId { get; private set; }

    public int Position { get; private set; }

    public int PersonId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Designation { get; private set; } = string.Empty;

    public string DaysText { get; private set; } = string.Empty;

    public decimal SalaryUsd { get; private set; }

    public decimal ExtrasUsd { get; private set; }

    public decimal AmountUsd { get; private set; }
}
