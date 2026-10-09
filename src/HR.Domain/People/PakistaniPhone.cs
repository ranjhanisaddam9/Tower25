using System.Text;

namespace HR.Domain.People;

/// <summary>
/// Pakistani phone numbers, normalised to E.164 (<c>+923001234567</c>).
/// Accepts 0300-1234567, 03001234567, +92 300 1234567, 0092 300 1234567, 92-300-1234567, 3001234567,
/// and landlines such as 042-35761234. Spaces, dashes, dots and parentheses are ignored.
/// </summary>
public static class PakistaniPhone
{
    public const int MaxLength = 13; // +92 and up to 10 national digits

    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();
        var plus = text.StartsWith('+');
        var digits = new StringBuilder();
        foreach (var c in plus ? text[1..] : text)
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
            else if (c is not (' ' or '-' or '.' or '(' or ')'))
            {
                return false;
            }
        }

        var all = digits.ToString();
        string national;
        if (plus)
        {
            if (!all.StartsWith("92", StringComparison.Ordinal))
            {
                return false; // a foreign number
            }

            national = all[2..];
        }
        else if (all.StartsWith("0092", StringComparison.Ordinal))
        {
            national = all[4..];
        }
        else if (all.StartsWith('0'))
        {
            national = all[1..];
        }
        else if (all.StartsWith("92", StringComparison.Ordinal) && all.Length >= 11)
        {
            national = all[2..];
        }
        else
        {
            national = all;
        }

        if (!IsNationalNumber(national))
        {
            return false;
        }

        normalized = "+92" + national;
        return true;
    }

    /// <summary>"+923001234567" → "+92 300 1234567"; landlines → "+92 42 35761234".</summary>
    public static string Format(string normalized)
    {
        if (normalized.Length < 6 || !normalized.StartsWith("+92", StringComparison.Ordinal))
        {
            return normalized;
        }

        var national = normalized[3..];
        return national.StartsWith('3') && national.Length == 10
            ? $"+92 {national[..3]} {national[3..]}"
            : $"+92 {national[..2]} {national[2..]}";
    }

    // Mobile: 3XX + 7 digits. Landline: area code (not starting with 0, 1 or 3) + subscriber, 9–10 digits in all.
    private static bool IsNationalNumber(string national) =>
        national.All(char.IsAsciiDigit)
        && ((national.Length == 10 && national[0] == '3')
            || (national.Length is 9 or 10 && national[0] is >= '2' and <= '9' && national[0] != '3'));
}
