using System.Data;
using HR.Domain.Invoices;
using HR.Domain.Payroll;
using HR.Domain.Settings;
using HR.Domain.Time;
using HR.Infrastructure.Data;
using HR.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HR.Infrastructure.Invoices;

public enum InvoiceStatusFilter
{
    All,
    Issued,
    Overdue,
    Paid,
    Void,
}

public sealed record InvoiceRow(
    int Id,
    string Number,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly IssueDate,
    DateOnly DueDate,
    decimal TotalUsd,
    InvoiceStatus Status,
    bool IsOverdue,
    DateOnly? PaidDate,
    decimal? AmountReceivedUsd)
{
    /// <summary>Paid invoices: received − total (negative = short, positive = over); 0 otherwise.</summary>
    public decimal PaymentDifferenceUsd => InvoiceMath.PaymentDifference(Status, TotalUsd, AmountReceivedUsd);
}

/// <summary>
/// Invoiced = all invoices that aren't void; received = amounts on paid ones; outstanding = unpaid totals plus the
/// shortfalls on invoices paid short (M9); amounts received over the total are shown separately as "received in excess".
/// </summary>
public sealed record InvoiceTotals(decimal InvoicedUsd, decimal ReceivedUsd, decimal OutstandingUsd, int OverdueCount, decimal ReceivedInExcessUsd = 0m, int ShortPaidCount = 0);

public sealed record InvoiceList(IReadOnlyList<InvoiceRow> Rows, InvoiceTotals Totals, IReadOnlyList<int> Years);

/// <summary>An invoice with its lines, plus the invoice that replaced it (when void) and the one it replaces.</summary>
public sealed record InvoiceDetails(Invoice Invoice, bool IsOverdue, int? ReplacedById, string? ReplacedByNumber, string? ReplacesNumber);

/// <summary>What the run page shows about the run's invoice.</summary>
public sealed record RunInvoice(int? InvoiceId, string? Number, InvoiceStatus? Status, bool IsOverdue, bool SettingsComplete);

/// <param name="OutstandingUsd">Unpaid totals plus shortfalls on invoices paid short.</param>
public sealed record InvoiceDashboard(decimal OutstandingUsd, int OutstandingCount, int OverdueCount, int ShortPaidCount = 0, decimal ReceivedInExcessUsd = 0m);

public enum InvoiceResultStatus
{
    Success,
    NotFound,
    Invalid,
    Refused,
    Conflict,
}

public sealed record InvoiceResult(InvoiceResultStatus Status, int? Id = null, string? Message = null, string? Field = null)
{
    public bool Succeeded => Status == InvoiceResultStatus.Success;
}

/// <summary>
/// The company invoice (SPEC §7, M8). Issued with the payroll's finalize (same transaction), or later from the run page
/// once Settings are complete. Void when the payroll is reopened; the next finalize issues a replacement. Admin only.
/// </summary>
public sealed class InvoiceService(AppDbContext db, IClock clock, ILoggerFactory loggerFactory)
{
    public const string SettingsIncompleteMessage = "Invoice pending: complete Settings (business name and client company name) to issue it.";
    public const string PaidReopenMessage = "Mark the invoice unpaid first.";
    public const string ConflictMessage = "Someone else changed this invoice at the same time. Reload and try again.";

    private readonly ILogger _log = loggerFactory.CreateLogger(SecurityLog.Category);

    // ===================== Issuing (inside the caller's transaction) =====================

    /// <summary>
    /// Builds the invoice for a just-finalized run (tracked, lines loaded) and adds it to the context without saving.
    /// Returns null when Settings can't issue invoices yet (the run then shows "Invoice pending").
    /// </summary>
    public async Task<Invoice?> AddForRunAsync(PayrollRun run, string actorId, CancellationToken cancellationToken)
    {
        var settings = await db.Settings.SingleOrDefaultAsync(s => s.Id == AppSettings.SingletonId, cancellationToken);
        if (settings is not { CanIssueInvoices: true } || run.FinalizedAt is not { } finalizedAt)
        {
            return null;
        }

        var issueDate = PakistanTime.ToKarachiDate(finalizedAt); // the finalize date
        var counter = await db.Set<InvoiceCounter>().SingleOrDefaultAsync(c => c.Year == issueDate.Year, cancellationToken);
        if (counter is null)
        {
            counter = new InvoiceCounter(issueDate.Year);
            db.Add(counter);
        }

        var lines = run.Lines
            .Where(l => !l.IsOrphaned)
            .OrderBy(l => l.PersonName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.PersonCode, StringComparer.Ordinal)
            .Select((l, i) => new InvoiceLine(i + 1, l.PersonId, l.PersonName, l.Designation,
                InvoiceMath.Line(l.BilledUsd!.Value, l.AdjustmentsUsd!.Value, l.InvoiceUsd!.Value, l.PayableDays, l.WorkingDays)))
            .ToList();
        var replaces = await db.Invoices.AsNoTracking()
            .Where(i => i.RunId == run.Id && i.Status == InvoiceStatus.Void)
            .OrderByDescending(i => i.VoidedAt)
            .Select(i => (int?)i.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var invoice = Invoice.Issue(InvoiceMath.Number(settings.InvoicePrefix, issueDate.Year, counter.Next()), run.Id, run.PeriodStart, run.PeriodEnd,
            issueDate, settings.PaymentTermsDays, InvoiceParties.From(settings), lines, replaces, actorId, clock.UtcNow);
        db.Invoices.Add(invoice);
        return invoice;
    }

    public void AuditIssued(string actorId, Invoice invoice) =>
        SecurityLog.InvoiceIssued(_log, actorId, invoice.Id, invoice.Number, invoice.RunId, invoice.TotalUsd, invoice.ReplacesInvoiceId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");

    /// <summary>
    /// For a run being reopened: voids its invoice with the reopen reason (tracked, not saved). Returns a refusal when the
    /// invoice is paid. The void is audited by <see cref="AuditVoided"/> after the caller saves.
    /// </summary>
    public async Task<(string? Refusal, Invoice? Voided)> VoidForReopenAsync(int runId, string reason, string actorId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(i => i.RunId == runId && i.Status != InvoiceStatus.Void, cancellationToken);
        if (invoice is null)
        {
            return (null, null);
        }

        if (invoice.Status == InvoiceStatus.Paid)
        {
            return (PaidReopenMessage, null);
        }

        invoice.Void(reason, actorId, clock.UtcNow);
        return (null, invoice);
    }

    public void AuditVoided(string actorId, Invoice invoice) => SecurityLog.InvoiceVoided(_log, actorId, invoice.Id, invoice.Number, invoice.RunId);

    /// <summary>Issues the pending invoice of a finalized run once Settings are complete (the "Issue invoice" button).</summary>
    public async Task<InvoiceResult> IssuePendingAsync(int runId, string actorId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var run = await db.PayrollRuns.Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            return new InvoiceResult(InvoiceResultStatus.NotFound);
        }

        if (run.Status != PayrollStatus.Finalized)
        {
            return new InvoiceResult(InvoiceResultStatus.Refused, Message: "Only a finalized payroll gets an invoice.");
        }

        if (await db.Invoices.AnyAsync(i => i.RunId == runId && i.Status != InvoiceStatus.Void, cancellationToken))
        {
            return new InvoiceResult(InvoiceResultStatus.Refused, Message: "This payroll already has an invoice.");
        }

        var invoice = await AddForRunAsync(run, actorId, cancellationToken);
        if (invoice is null)
        {
            return new InvoiceResult(InvoiceResultStatus.Refused, Message: SettingsIncompleteMessage);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        AuditIssued(actorId, invoice);
        return new InvoiceResult(InvoiceResultStatus.Success, invoice.Id);
    }

    // ===================== Payment =====================

    public async Task<InvoiceResult> MarkPaidAsync(int id, DateOnly? paidDate, decimal? amountReceivedUsd, string? note, byte[] rowVersion, string actorId, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (invoice is null)
        {
            return new InvoiceResult(InvoiceResultStatus.NotFound);
        }

        if (invoice.Status != InvoiceStatus.Issued)
        {
            return new InvoiceResult(InvoiceResultStatus.Refused, id, invoice.Status == InvoiceStatus.Void ? "A void invoice can't be paid." : "This invoice is already marked paid.");
        }

        if (paidDate is not { } date)
        {
            return new InvoiceResult(InvoiceResultStatus.Invalid, id, "Enter the date the payment was received.", "PaidDate");
        }

        if (date < invoice.IssueDate || date > clock.Today)
        {
            return new InvoiceResult(InvoiceResultStatus.Invalid, id, "The payment date must be between the issue date and today.", "PaidDate");
        }

        if (amountReceivedUsd is not { } amount || amount <= 0m || amount > 10_000_000m || amount != Math.Round(amount, 2))
        {
            return new InvoiceResult(InvoiceResultStatus.Invalid, id, "Enter the amount received in USD (more than 0, cents allowed).", "AmountReceivedUsd");
        }

        if (note?.Trim() is { Length: > Invoice.NoteMaxLength })
        {
            return new InvoiceResult(InvoiceResultStatus.Invalid, id, "The note can be at most 300 characters.", "PaymentNote");
        }

        if (!invoice.RowVersion.AsSpan().SequenceEqual(rowVersion))
        {
            return new InvoiceResult(InvoiceResultStatus.Conflict, id, ConflictMessage);
        }

        db.Entry(invoice).Property(i => i.RowVersion).OriginalValue = rowVersion;
        invoice.MarkPaid(date, amount, note, actorId, clock.UtcNow);
        if (await SaveAsync(cancellationToken) is { } failure)
        {
            return failure with { Id = id };
        }

        SecurityLog.InvoicePaid(_log, actorId, invoice.Id, invoice.Number, amount, date);
        return new InvoiceResult(InvoiceResultStatus.Success, id);
    }

    public async Task<InvoiceResult> MarkUnpaidAsync(int id, byte[] rowVersion, string actorId, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (invoice is null)
        {
            return new InvoiceResult(InvoiceResultStatus.NotFound);
        }

        if (invoice.Status != InvoiceStatus.Paid)
        {
            return new InvoiceResult(InvoiceResultStatus.Refused, id, "Only a paid invoice can be marked unpaid.");
        }

        if (!invoice.RowVersion.AsSpan().SequenceEqual(rowVersion))
        {
            return new InvoiceResult(InvoiceResultStatus.Conflict, id, ConflictMessage);
        }

        db.Entry(invoice).Property(i => i.RowVersion).OriginalValue = rowVersion;
        invoice.MarkUnpaid(actorId, clock.UtcNow);
        if (await SaveAsync(cancellationToken) is { } failure)
        {
            return failure with { Id = id };
        }

        SecurityLog.InvoiceUnpaid(_log, actorId, invoice.Id, invoice.Number);
        return new InvoiceResult(InvoiceResultStatus.Success, id);
    }

    // ===================== Queries =====================

    public async Task<InvoiceList> ListAsync(InvoiceStatusFilter filter, int? year, CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var all = await db.Invoices.AsNoTracking()
            .OrderByDescending(i => i.IssueDate).ThenByDescending(i => i.Id)
            .Select(i => new { i.Id, i.Number, i.PeriodStart, i.PeriodEnd, i.IssueDate, i.DueDate, i.TotalUsd, i.Status, i.PaidDate, i.AmountReceivedUsd })
            .ToListAsync(cancellationToken);
        var rows = all
            .Select(i => new InvoiceRow(i.Id, i.Number, i.PeriodStart, i.PeriodEnd, i.IssueDate, i.DueDate, i.TotalUsd, i.Status,
                InvoiceMath.IsOverdue(i.Status, i.DueDate, today), i.PaidDate, i.AmountReceivedUsd))
            .ToList();
        var years = rows.Select(r => r.IssueDate.Year).Distinct().OrderDescending().ToList();
        var inYear = rows.Where(r => year is not { } y || r.IssueDate.Year == y).ToList();
        var shown = inYear.Where(r => filter switch
        {
            InvoiceStatusFilter.Issued => r.Status == InvoiceStatus.Issued,
            InvoiceStatusFilter.Overdue => r.IsOverdue,
            InvoiceStatusFilter.Paid => r.Status == InvoiceStatus.Paid,
            InvoiceStatusFilter.Void => r.Status == InvoiceStatus.Void,
            _ => true,
        }).ToList();
        return new InvoiceList(shown, Totals(inYear), years);
    }

    public static InvoiceTotals Totals(IEnumerable<InvoiceRow> rows)
    {
        var list = rows.ToList();
        return new InvoiceTotals(
            list.Where(r => r.Status != InvoiceStatus.Void).Sum(r => r.TotalUsd),
            list.Where(r => r.Status == InvoiceStatus.Paid).Sum(r => r.AmountReceivedUsd ?? 0m),
            list.Sum(r => InvoiceMath.OutstandingUsd(r.Status, r.TotalUsd, r.AmountReceivedUsd)),
            list.Count(r => r.IsOverdue),
            list.Sum(r => InvoiceMath.ExcessUsd(r.Status, r.TotalUsd, r.AmountReceivedUsd)),
            list.Count(r => r.PaymentDifferenceUsd < 0m));
    }

    public async Task<InvoiceDetails?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.Lines).SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (invoice is null)
        {
            return null;
        }

        var replacedBy = await db.Invoices.AsNoTracking().Where(i => i.ReplacesInvoiceId == id).Select(i => new { i.Id, i.Number }).FirstOrDefaultAsync(cancellationToken);
        var replaces = invoice.ReplacesInvoiceId is { } r
            ? await db.Invoices.AsNoTracking().Where(i => i.Id == r).Select(i => i.Number).SingleOrDefaultAsync(cancellationToken)
            : null;
        return new InvoiceDetails(invoice, InvoiceMath.IsOverdue(invoice.Status, invoice.DueDate, clock.Today), replacedBy?.Id, replacedBy?.Number, replaces);
    }

    public async Task<RunInvoice> RunInvoiceAsync(int runId, CancellationToken cancellationToken = default)
    {
        var settingsComplete = (await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == AppSettings.SingletonId, cancellationToken))?.CanIssueInvoices ?? false;
        var invoice = await db.Invoices.AsNoTracking()
            .Where(i => i.RunId == runId && i.Status != InvoiceStatus.Void)
            .Select(i => new { i.Id, i.Number, i.Status, i.DueDate })
            .SingleOrDefaultAsync(cancellationToken);
        return new RunInvoice(invoice?.Id, invoice?.Number, invoice?.Status,
            invoice is not null && InvoiceMath.IsOverdue(invoice.Status, invoice.DueDate, clock.Today), settingsComplete);
    }

    public async Task<IReadOnlyDictionary<int, (int Id, string Number, InvoiceStatus Status)>> ActiveByRunAsync(CancellationToken cancellationToken = default) =>
        (await db.Invoices.AsNoTracking().Where(i => i.Status != InvoiceStatus.Void)
            .Select(i => new { i.RunId, i.Id, i.Number, i.Status }).ToListAsync(cancellationToken))
        .ToDictionary(i => i.RunId, i => (i.Id, i.Number, i.Status));

    public async Task<InvoiceDashboard> DashboardAsync(CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var open = await db.Invoices.AsNoTracking().Where(i => i.Status != InvoiceStatus.Void)
            .Select(i => new { i.Status, i.TotalUsd, i.AmountReceivedUsd, i.DueDate }).ToListAsync(cancellationToken);
        var issued = open.Where(i => i.Status == InvoiceStatus.Issued).ToList();
        return new InvoiceDashboard(
            open.Sum(i => InvoiceMath.OutstandingUsd(i.Status, i.TotalUsd, i.AmountReceivedUsd)),
            issued.Count,
            issued.Count(i => InvoiceMath.IsOverdue(i.Status, i.DueDate, today)),
            open.Count(i => InvoiceMath.PaymentDifference(i.Status, i.TotalUsd, i.AmountReceivedUsd) < 0m),
            open.Sum(i => InvoiceMath.ExcessUsd(i.Status, i.TotalUsd, i.AmountReceivedUsd)));
    }

    private async Task<InvoiceResult?> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new InvoiceResult(InvoiceResultStatus.Conflict, Message: ConflictMessage);
        }
    }
}
