namespace HR.Domain.Payroll;

/// <summary>
/// The one shared rounding helper (SPEC §5): always MidpointRounding.AwayFromZero,
/// USD to 2 decimals, PKR to whole rupees.
/// </summary>
public static class Money
{
    public static decimal RoundUsd(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static decimal RoundPkr(decimal amount) => Math.Round(amount, 0, MidpointRounding.AwayFromZero);
}
