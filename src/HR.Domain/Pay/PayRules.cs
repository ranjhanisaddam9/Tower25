using HR.Domain.Payroll;
using HR.Domain.People;

namespace HR.Domain.Pay;

public sealed record PayError(string Field, string Message);

/// <summary>
/// What an Admin posts for a rate record. Which fields count depends on the hire source; the rest are ignored.
/// <list type="bullet">
/// <item>CompanyRecommended: <see cref="Salary"/> (billed = paid, USD) and <see cref="Commission"/>.</item>
/// <item>BudgetHire: <see cref="Budget"/> (billed), <see cref="Pay"/> and <see cref="Currency"/> (default PKR). Commission is 0.</item>
/// <item>Owner: <see cref="Salary"/> (billed = paid, USD). Commission is 0.</item>
/// </list>
/// </summary>
public sealed record AdminPayInput(
    DateOnly? EffectiveFrom,
    decimal? Salary,
    decimal? Commission,
    decimal? Budget,
    decimal? Pay,
    PayCurrency? Currency);

/// <summary>Field names used in errors (they match the form properties).</summary>
public static class PayFields
{
    public const string EffectiveFrom = "EffectiveFrom";
    public const string Salary = "Salary";
    public const string Commission = "Commission";
    public const string Budget = "Budget";
    public const string Pay = "Pay";
    public const string Currency = "Currency";
}

/// <summary>Rate-record rules (SPEC §2). The server always derives billed, commission and currency; posted values are never trusted.</summary>
public static class PayRules
{
    public const decimal MinUsdMonthly = 1m;
    public const decimal MaxUsdMonthly = 100_000m;
    public const decimal MinPkrMonthly = 1_000m;
    public const decimal MaxPkrMonthly = 50_000_000m;
    public const decimal MaxCommission = 10_000m;
    public const decimal DefaultCommission = 25m;

    public const string NoHireSourceMessage = "Pay setup for this person hasn't been completed yet.";

    /// <summary>
    /// Derives the stored terms from the Admin's input for the given hire source. Returns null with errors when invalid.
    /// <paramref name="earliestEffectiveFrom"/> is the start of the pay period containing the first employment period's start.
    /// </summary>
    public static PayTerms? DeriveAdmin(HireSource source, AdminPayInput input, DateOnly earliestEffectiveFrom, out IReadOnlyList<PayError> errors)
    {
        var list = new List<PayError>();
        ValidateEffectiveFrom(input.EffectiveFrom, earliestEffectiveFrom, list);

        decimal billed;
        decimal commission;
        decimal pay;
        PayCurrency currency;
        switch (source)
        {
            case HireSource.CompanyRecommended:
                billed = pay = Required(input.Salary, PayFields.Salary, "Enter the monthly salary.", list);
                commission = input.Commission ?? Missing(PayFields.Commission, "Enter the commission per pay period (0 for none).", list);
                currency = PayCurrency.USD;
                break;
            case HireSource.BudgetHire:
                billed = Required(input.Budget, PayFields.Budget, "Enter the Company's monthly budget.", list);
                pay = Required(input.Pay, PayFields.Pay, "Enter the monthly pay to the person.", list);
                commission = 0m;
                currency = input.Currency is { } c && Enum.IsDefined(c) ? c : PayCurrency.PKR;
                break;
            case HireSource.Owner:
                billed = pay = Required(input.Salary, PayFields.Salary, "Enter your monthly salary.", list);
                commission = 0m;
                currency = PayCurrency.USD;
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

        var terms = new PayTerms(input.EffectiveFrom!.Value, billed, commission, pay, currency);
        list.AddRange(TermErrors(terms, source == HireSource.BudgetHire ? PayFields.Pay : PayFields.Salary,
            source == HireSource.BudgetHire ? PayFields.Budget : PayFields.Salary));
        errors = list;
        return list.Count > 0 ? null : terms;
    }

    /// <summary>
    /// A Manager's increment (or edit of their own increment): only the pay amount and date come from the form.
    /// Currency and commission are copied from <paramref name="basis"/>; billed follows pay for CompanyRecommended
    /// and Owner, and stays as in <paramref name="basis"/> for BudgetHire.
    /// </summary>
    public static PayTerms? DeriveIncrement(
        HireSource source,
        PayTerms basis,
        DateOnly? effectiveFrom,
        decimal? pay,
        DateOnly earliestEffectiveFrom,
        out IReadOnlyList<PayError> errors)
    {
        var list = new List<PayError>();
        ValidateEffectiveFrom(effectiveFrom, earliestEffectiveFrom, list);
        var amount = Required(pay, PayFields.Pay, "Enter the new monthly pay.", list);
        if (list.Count > 0)
        {
            errors = list;
            return null;
        }

        var terms = source switch
        {
            HireSource.CompanyRecommended => new PayTerms(effectiveFrom!.Value, amount, basis.CommissionPerPeriodUsd, amount, PayCurrency.USD),
            HireSource.Owner => new PayTerms(effectiveFrom!.Value, amount, 0m, amount, PayCurrency.USD),
            HireSource.BudgetHire => new PayTerms(effectiveFrom!.Value, basis.BilledMonthlyUsd, 0m, amount, basis.PayCurrency),
            _ => null,
        };

        if (terms is null)
        {
            errors = [new PayError(string.Empty, NoHireSourceMessage)];
            return null;
        }

        list.AddRange(TermErrors(terms, PayFields.Pay, PayFields.Pay));
        errors = list;
        return list.Count > 0 ? null : terms;
    }

    /// <summary>Limits and shape rules on stored terms (also used as the entity's invariant).</summary>
    public static IReadOnlyList<PayError> TermErrors(PayTerms terms, string payField = PayFields.Pay, string billedField = PayFields.Budget)
    {
        var list = new List<PayError>();
        if (!IsPeriodStart(terms.EffectiveFrom))
        {
            list.Add(new PayError(PayFields.EffectiveFrom, "Pay changes take effect on the 1st or the 16th of a month."));
        }

        if (terms.BilledMonthlyUsd < MinUsdMonthly || terms.BilledMonthlyUsd > MaxUsdMonthly)
        {
            list.Add(new PayError(billedField, "Monthly USD amounts must be between $1 and $100,000."));
        }
        else if (decimal.Round(terms.BilledMonthlyUsd, 2) != terms.BilledMonthlyUsd)
        {
            list.Add(new PayError(billedField, "Use at most 2 decimal places for USD."));
        }

        if (terms.CommissionPerPeriodUsd < 0 || terms.CommissionPerPeriodUsd > MaxCommission)
        {
            list.Add(new PayError(PayFields.Commission, "The commission must be between $0 and $10,000 per pay period."));
        }
        else if (decimal.Round(terms.CommissionPerPeriodUsd, 2) != terms.CommissionPerPeriodUsd)
        {
            list.Add(new PayError(PayFields.Commission, "Use at most 2 decimal places for USD."));
        }

        if (terms.PayCurrency == PayCurrency.PKR)
        {
            if (terms.PayMonthlyAmount < MinPkrMonthly || terms.PayMonthlyAmount > MaxPkrMonthly)
            {
                list.Add(new PayError(payField, "Monthly PKR pay must be between Rs 1,000 and Rs 50,000,000."));
            }
            else if (decimal.Truncate(terms.PayMonthlyAmount) != terms.PayMonthlyAmount)
            {
                list.Add(new PayError(payField, "PKR pay must be in whole rupees."));
            }
        }
        else if (terms.PayMonthlyAmount < MinUsdMonthly || terms.PayMonthlyAmount > MaxUsdMonthly)
        {
            list.Add(new PayError(payField, "Monthly USD amounts must be between $1 and $100,000."));
        }
        else if (decimal.Round(terms.PayMonthlyAmount, 2) != terms.PayMonthlyAmount)
        {
            list.Add(new PayError(payField, "Use at most 2 decimal places for USD."));
        }

        // Distinct (field, message) only: CompanyRecommended/Owner use one field for billed and pay.
        return list.Distinct().ToList();
    }

    public static bool IsPeriodStart(DateOnly date) => date.Day is 1 or 16;

    /// <summary>The earliest allowed EffectiveFrom: the start of the pay period containing the first employment period's start.</summary>
    public static DateOnly EarliestEffectiveFrom(DateOnly firstEmploymentStart) => PayPeriod.For(firstEmploymentStart).Start;

    /// <summary>
    /// Initial for the first record; otherwise compares pay with <paramref name="previous"/>: up → Increment, down → Decrement,
    /// same pay but billed or commission changed → BillingChange. If the pay currency changed, pay is compared in USD at
    /// <paramref name="usdToPkr"/>; without a rate the change is labelled Correction.
    /// </summary>
    public static RateChangeType DeriveChangeType(PayTerms? previous, PayTerms current, decimal? usdToPkr)
    {
        if (previous is null)
        {
            return RateChangeType.Initial;
        }

        decimal previousPay = previous.PayMonthlyAmount;
        decimal currentPay = current.PayMonthlyAmount;
        if (previous.PayCurrency != current.PayCurrency)
        {
            if (usdToPkr is not { } rate || rate <= 0)
            {
                return RateChangeType.Correction;
            }

            previousPay = ToUsd(previous.PayMonthlyAmount, previous.PayCurrency, rate);
            currentPay = ToUsd(current.PayMonthlyAmount, current.PayCurrency, rate);
        }

        if (currentPay > previousPay)
        {
            return RateChangeType.Increment;
        }

        if (currentPay < previousPay)
        {
            return RateChangeType.Decrement;
        }

        return current.BilledMonthlyUsd != previous.BilledMonthlyUsd || current.CommissionPerPeriodUsd != previous.CommissionPerPeriodUsd
            ? RateChangeType.BillingChange
            : RateChangeType.Correction;
    }

    /// <summary>
    /// BudgetHire only: true when the monthly pay (converted to USD at <paramref name="usdToPkr"/> if PKR) is at least the budget.
    /// Null when PKR pay can't be compared because there is no rate.
    /// </summary>
    public static bool? LosesMoney(PayTerms terms, decimal? usdToPkr)
    {
        if (terms.PayCurrency == PayCurrency.USD)
        {
            return terms.PayMonthlyAmount >= terms.BilledMonthlyUsd;
        }

        return usdToPkr is { } rate && rate > 0 ? terms.PayMonthlyAmount / rate >= terms.BilledMonthlyUsd : null;
    }

    private static decimal ToUsd(decimal amount, PayCurrency currency, decimal rate) =>
        currency == PayCurrency.USD ? amount : amount / rate;

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

    private static decimal Missing(string field, string message, List<PayError> list)
    {
        list.Add(new PayError(field, message));
        return 0m;
    }
}
