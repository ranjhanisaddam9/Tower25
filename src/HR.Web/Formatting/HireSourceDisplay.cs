using HR.Domain.People;

namespace HR.Web.Formatting;

/// <summary>Admin-only labels and pill styles for hire sources. Never used when rendering for a Manager.</summary>
public static class HireSourceDisplay
{
    public const string NotAssigned = "Not assigned";

    public static readonly IReadOnlyList<HireSource> All = [HireSource.CompanyRecommended, HireSource.BudgetHire, HireSource.Owner];

    public static string Label(HireSource? source) => source switch
    {
        HireSource.CompanyRecommended => "Company recommended",
        HireSource.BudgetHire => "Budget hire",
        HireSource.Owner => "Owner",
        _ => NotAssigned,
    };

    public static string Description(HireSource source) => source switch
    {
        HireSource.CompanyRecommended => "The Company found this person and set the salary.",
        HireSource.BudgetHire => "The Company gave a budget and the owner hired within it.",
        HireSource.Owner => "The owner. Only one active person can have this source.",
        _ => string.Empty,
    };

    public static string PillClass(HireSource? source) => source switch
    {
        HireSource.CompanyRecommended => "pill-teal",
        HireSource.BudgetHire => "pill-violet",
        HireSource.Owner => "pill-solid-success",
        _ => "pill-warning",
    };
}
