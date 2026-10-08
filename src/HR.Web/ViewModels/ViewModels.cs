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
    string DeltaTone = "neutral");

public sealed record PeriodSummaryViewModel(DateOnly Start, DateOnly End, int WorkingDays);

public sealed record DashboardViewModel(DateOnly Today, PeriodSummaryViewModel CurrentPeriod, PeriodSummaryViewModel NextPeriod);

public sealed record ErrorViewModel(int StatusCode, string Title, string Message, string Icon);

public sealed record StyleguideMoneyRow(string Name, string Designation, string Status, int Days, decimal PayUsd, decimal PayPkr);

public sealed record StyleguideViewModel(IReadOnlyList<StyleguideMoneyRow> Rows);
