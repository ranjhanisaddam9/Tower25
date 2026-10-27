using HR.Domain.Payroll;
using HR.Domain.People;

namespace HR.Domain.Pay;

public sealed record PayError(string Field, string Message);

/// <summary>
/// What an Admin posts for a rate record (all monthly USD). Which fields count depends on the hire source; the rest,
/// including any posted derived value, are ignored (SPEC §2).
/// <list type="bullet">
/// <item>CompanyRecommended: <see cref="Budget"/> (billed) and <see cref="Commission"/>; salary = billed − commission.</item>
/// <item>BudgetHire: <see cref="Budget"/> (billed) and <see cref="Salary"/>; commission = budget − salary.</item>
/// <item>Owner: <see cref="Salary"/>; commission 0, billed = salary.</item>
/// </list>
/// </summary>
public sealed record AdminPayInput(
    DateOnly? EffectiveFrom,
    decimal? Salary,
    decimal? Commission,
    decimal? Budget);

/// <summary>Field names used in errors (they match the form properties).</summary>
public static class PayFields
{
    public const string EffectiveFrom = "EffectiveFrom";
    public const string Salary = "Salary";
    public const string Commission = "Commission";
    public const string Budget = "Budget";
}

/// <summary>Rate-record rules (SPEC §2). The server always derives the third figure; posted derived values are never trusted.</summary>
public static class PayRules
{
    public const decimal MinUsdMonthly = 1m;
    public const decimal MaxUsdMonthly = 100_000m;
    public const decimal MaxCommissionMonthly = 20_000m;

    /// <summary>Monthly commission suggested for a CompanyRecommended person's first record (later records suggest the previous one).</summary>
    public const decimal DefaultCommissionMonthly = 50m;

    public const string NoHireSourceMessage = "Pay setup for this person hasn't been completed yet.";

    /// <summary>
    /// Derives the stored terms from the Admin's input for the given hire source. Returns null with errors when invalid.
    /// <paramref name="earliestEffectiveFrom"/> is the start of the pay period containing the first employment period's start.
    /// </summary>
    public static PayTerms? DeriveAdmin(HireSource source, AdminPayInput input, DateOnly earliestEffectiveFrom, out IReadOnlyList<PayError> errors)
    {
        var list = new List<PayError>();
        ValidateEffectiveFrom(input.EffectiveFrom, earliestEffectiveFrom, list);

        decimal salary;
        decimal commission;
        decimal billed;
        switch (source)
        {
            case HireSource.CompanyRecommended:
                // Owner request (18 Oct 2026): the Admin enters what the Company is billed and the commission; the
                // salary paid to the person is the difference.
                billed = Required(input.Budget, PayFields.Budget, "Enter the monthly amount billed to the Company.", list);
                commission = Required(input.Commission, PayFields.Commission, "Enter the monthly commission (0 for none).", list);
                if (input.Commission is < 0m || input.Commission > MaxCommissionMonthly)
                {
                    list.Add(new PayError(PayFields.Commission, "The commission must be between $0 and $20,000 a month."));
                }
                else if (input.Budget is { } b && input.Commission is { } c && c >= b)
                {
                    list.Add(new PayError(PayFields.Commission, "The commission must be less than the billed amount, so the person is paid something."));
                }

                salary = billed - commission;
                break;
            case HireSource.BudgetHire:
                billed = Required(input.Budget, PayFields.Budget, "Enter the Company's monthly budget.", list);
                salary = Required(input.Salary, PayFields.Salary, "Enter the monthly salary paid to the person.", list);
                commission = billed - salary;
                break;
            case HireSource.Owner:
                salary = Required(input.Salary, PayFields.Salary, "Enter your monthly salary.", list);
                commission = 0m;
                billed = salary;
                break;
            default:
                errors = [new PayError(string.Empty, NoHireSourceMessage)];
                return null;
        }

        if (list.Count > 0)
        {
            errors = list;
            return null;
        }

        var terms = new PayTerms(input.EffectiveFrom!.Value, salary, commission, billed);
        // A CompanyRecommended salary is derived, so its limits are reported on the billed field the Admin typed.
        list.AddRange(TermErrors(terms, source == HireSource.Owner ? PayFields.Salary : PayFields.Budget)
            .Select(e => source == HireSource.CompanyRecommended && e.Field == PayFields.Salary ? e with { Field = PayFields.Budget } : e)
            .Distinct());
        errors = list;
        return list.Count > 0 ? null : terms;
    }

    /// <summary>
    /// A Manager's increment (or edit of their own increment): only the new monthly salary and the date come from the form.
    /// CompanyRecommended: commission carried from <paramref name="basis"/>, billed re-derived. BudgetHire: billed (the
    /// budget) carried, commission re-derived (the Admin reviews it). Owner: billed = salary.
    /// </summary>
    public static PayTerms? DeriveIncrement(
        HireSource source,
        PayTerms basis,
        DateOnly? effectiveFrom,
        decimal? salary,
        DateOnly earliestEffectiveFrom,
        out IReadOnlyList<PayError> errors)
    {
        var list = new List<PayError>();
        ValidateEffectiveFrom(effectiveFrom, earliestEffectiveFrom, list);
        var amount = Required(salary, PayFields.Salary, "Enter the new monthly salary.", list);
        if (list.Count > 0)
        {
            errors = list;
            return null;
        }

        var date = effectiveFrom!.Value;
        var terms = source switch
        {
            HireSource.CompanyRecommended => new PayTerms(date, amount, basis.CommissionMonthlyUsd, amount + basis.CommissionMonthlyUsd),
            HireSource.BudgetHire => new PayTerms(date, amount, basis.BilledMonthlyUsd - amount, basis.BilledMonthlyUsd),
            HireSource.Owner => new PayTerms(date, amount, 0m, amount),
            _ => null,
        };

        if (terms is null)
        {
            errors = [new PayError(string.Empty, NoHireSourceMessage)];
            return null;
        }

        // Every limit is reported on the only field a Manager has.
        list.AddRange(TermErrors(terms, PayFields.Salary).Select(e => e with { Field = e.Field == PayFields.EffectiveFrom ? e.Field : PayFields.Salary }).Distinct());
        errors = list;
        return list.Count > 0 ? null : terms;
    }

    /// <summary>Limits and shape rules on stored terms (also the entity's invariant): Billed = Salary + Commission.</summary>
    public static IReadOnlyList<PayError> TermErrors(PayTerms terms, string billedField = PayFields.Budget)
    {
        var list = new List<PayError>();
        if (!IsPeriodStart(terms.EffectiveFrom))
        {
            list.Add(new PayError(PayFields.EffectiveFrom, "Pay changes take effect on the 1st or the 16th of a month."));
        }

        CheckUsd(terms.SalaryMonthlyUsd, PayFields.Salary, list);
        CheckUsd(terms.BilledMonthlyUsd, billedField, list);

        if (Math.Abs(terms.CommissionMonthlyUsd) > MaxUsdMonthly)
        {
            list.Add(new PayError(PayFields.Commission, "The commission is out of range."));
        }
        else if (decimal.Round(terms.CommissionMonthlyUsd, 2) != terms.CommissionMonthlyUsd)
        {
            list.Add(new PayError(PayFields.Commission, "Use at most 2 decimal places for USD."));
        }

        if (terms.BilledMonthlyUsd != terms.SalaryMonthlyUsd + terms.CommissionMonthlyUsd)
        {
            list.Add(new PayError(billedField, "Billed must equal salary plus commission."));
        }

        // Distinct (field, message) only: CompanyRecommended/Owner report billed limits on the salary field.
        return list.Distinct().ToList();
    }

    public static bool IsPeriodStart(DateOnly date) => date.Day is 1 or 16;

    /// <summary>The earliest allowed EffectiveFrom: the start of the pay period containing the first employment period's start.</summary>
    public static DateOnly EarliestEffectiveFrom(DateOnly firstEmploymentStart) => PayPeriod.For(firstEmploymentStart).Start;

    /// <summary>
    /// Initial for the first record; otherwise compares the salary with <paramref name="previous"/>: up → Increment,
    /// down → Decrement, same salary but billed or commission changed → BillingChange, nothing changed → Correction.
    /// </summary>
    public static RateChangeType DeriveChangeType(PayTerms? previous, PayTerms current)
    {
        if (previous is null)
        {
            return RateChangeType.Initial;
        }

        if (current.SalaryMonthlyUsd > previous.SalaryMonthlyUsd)
        {
            return RateChangeType.Increment;
        }

        if (current.SalaryMonthlyUsd < previous.SalaryMonthlyUsd)
        {
            return RateChangeType.Decrement;
        }

        return current.BilledMonthlyUsd != previous.BilledMonthlyUsd || current.CommissionMonthlyUsd != previous.CommissionMonthlyUsd
            ? RateChangeType.BillingChange
            : RateChangeType.Correction;
    }

    /// <summary>A BudgetHire loses money when the salary is above the budget (commission below zero). Needs the Admin's confirmation.</summary>
    public static bool LosesMoney(PayTerms terms) => terms.CommissionMonthlyUsd < 0m;

    private static void CheckUsd(decimal amount, string field, List<PayError> list)
    {
        if (amount < MinUsdMonthly || amount > MaxUsdMonthly)
        {
            list.Add(new PayError(field, "Monthly USD amounts must be between $1 and $100,000."));
        }
        else if (decimal.Round(amount, 2) != amount)
        {
            list.Add(new PayError(field, "Use at most 2 decimal places for USD."));
        }
    }

    private static void ValidateEffectiveFrom(DateOnly? effectiveFrom, DateOnly earliest, List<PayError> list)
    {
        if (effectiveFrom is not { } date)
        {
            list.Add(new PayError(PayFields.EffectiveFrom, "Choose the pay period the change takes effect from."));
        }
        else if (!IsPeriodStart(date))
        {
            list.Add(new PayError(PayFields.EffectiveFrom, "Pay changes take effect on the 1st or the 16th of a month."));
        }
        else if (date < earliest)
        {
            list.Add(new PayError(PayFields.EffectiveFrom,
                $"Pay can't start before {earliest:dd MMM yyyy}, the start of the pay period in which this person first joined."));
        }
    }

    private static decimal Required(decimal? value, string field, string message, List<PayError> list)
    {
        if (value is { } v)
        {
            return v;
        }

        list.Add(new PayError(field, message));
        return 0m;
    }
}
