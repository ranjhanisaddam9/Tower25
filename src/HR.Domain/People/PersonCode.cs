using System.Globalization;

namespace HR.Domain.People;

/// <summary>Person codes: "HR-0001". The number comes from a database sequence and is never reused.</summary>
public static class PersonCode
{
    public const string Prefix = "HR-";
    public const int MaxLength = 16;

    public static string Format(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        return Prefix + number.ToString("0000", CultureInfo.InvariantCulture);
    }
}
