using HR.Domain.Pay;

namespace HR.Web.Formatting;

/// <summary>Labels and pill styles for rate-record change types.</summary>
public static class PayDisplay
{
    /// <summary>Admin labels (BillingChange is spelled out).</summary>
    public static string AdminLabel(RateChangeType type) => type switch
    {
        RateChangeType.BillingChange => "Billing change",
        _ => type.ToString(),
    };

    /// <summary>Pill class for an Admin type or a Manager label ("Update" stands in for BillingChange for Managers).</summary>
    public static string PillClass(string label) => label switch
    {
        "Initial" => "pill-neutral",
        "Increment" => "pill-success",
        "Decrement" => "pill-danger",
        "Correction" => "pill-primary", // not pill-violet: that class marks a hire source on Admin pages
        _ => "pill-info", // "Update" (Managers) and "Billing change" (Admins)
    };

    public static string PillClass(RateChangeType type) => PillClass(type == RateChangeType.BillingChange ? "Update" : type.ToString());

    public static string CurrencyName(PayCurrency currency) => currency == PayCurrency.USD ? "USD" : "PKR";
}
