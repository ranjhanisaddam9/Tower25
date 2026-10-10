namespace HR.Web.ViewModels;

/// <param name="Tone">Icon chip colour: indigo, violet, teal, success, warning, danger or info.</param>
/// <param name="DeltaTone">success, danger or neutral.</param>
public sealed record StatTileViewModel(
    string Label,
    string Value,
    string Icon,
    string Tone,
    string? Caption = null,
    string? Delta = null,
    string DeltaTone = "neutral",
    string? Href = null,
    string? TestId = null);

public sealed record PeriodSummaryViewModel(DateOnly Start, DateOnly End, int WorkingDays);

/// <param name="HireSourceNotAssigned">Admin only; null for Managers (never loaded for them).</param>
public sealed record DashboardViewModel(
    DateOnly Today,
    PeriodSummaryViewModel CurrentPeriod,
    PeriodSummaryViewModel NextPeriod,
    PeopleTileViewModel People,
    RateTileViewModel Rate,
    int NoPaySetup,
    int? PendingBillingReviews,
    int? HireSourceNotAssigned,
    HR.Infrastructure.Absences.AbsenceDashboard Absences,
    HR.Infrastructure.Payroll.PayrollDashboard Payroll,
    HR.Infrastructure.Payroll.PayrollDashboardAdmin? PayrollAdmin,
    HR.Infrastructure.Invoices.InvoiceDashboard? Invoices,
    PayrollTrendViewModel Trend,
    HR.Infrastructure.Security.SecurityAlerts? Alerts = null);

/// <param name="CorrelationId">Shown on 5xx pages so a user can quote it; the same id is in the server log.</param>
public sealed record ErrorViewModel(int StatusCode, string Title, string Message, string Icon, string? CorrelationId = null);

public sealed record StyleguideMoneyRow(string Name, string Designation, string Status, int Days, decimal PayUsd, decimal PayPkr);

public sealed record StyleguideViewModel(IReadOnlyList<StyleguideMoneyRow> Rows);

/// <summary>Dashboard USD/PKR tile (same for both roles). <see cref="UsdToPkr"/> is null when no rate is set yet.</summary>
public sealed record RateTileViewModel(decimal? UsdToPkr, DateOnly? Since, RateChangeViewModel? Change);
