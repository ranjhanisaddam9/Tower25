namespace HR.Domain.People;

/// <summary>
/// How a person came to be placed at the Company (SPEC §2). Admin-only: never loaded for, or shown to, a Manager.
/// A person's source is nullable; null means the Admin has not assigned one yet.
/// </summary>
public enum HireSource
{
    CompanyRecommended = 1,
    BudgetHire = 2,
    Owner = 3,
}
