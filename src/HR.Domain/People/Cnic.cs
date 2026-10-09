namespace HR.Domain.People;

/// <summary>Pakistani CNIC: 13 digits, stored as 12345-1234567-1. Accepts input with or without the dashes.</summary>
public static class Cnic
{
    public const int Length = 15;

    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();
        string digits;
        if (text.Length == 15)
        {
            if (text[5] != '-' || text[13] != '-')
            {
                return false;
            }

            digits = text[..5] + text[6..13] + text[14..];
        }
        else if (text.Length == 13)
        {
            digits = text;
        }
        else
        {
            return false;
        }

        if (!digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        normalized = $"{digits[..5]}-{digits[5..12]}-{digits[12..]}";
        return true;
    }
}
