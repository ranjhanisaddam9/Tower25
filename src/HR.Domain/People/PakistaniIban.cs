using System.Text;

namespace HR.Domain.People;

/// <summary>
/// Pakistani IBAN: PK + 2 check digits + 4-letter bank code + 16 alphanumerics (24 characters),
/// validated with the ISO 13616 mod-97 check. Stored uppercase without spaces; displayed in groups of 4.
/// </summary>
public static class PakistaniIban
{
    public const int Length = 24;

    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var compact = new StringBuilder();
        foreach (var c in input)
        {
            if (c is ' ' or '-')
            {
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }

            compact.Append(char.ToUpperInvariant(c));
        }

        var iban = compact.ToString();
        if (iban.Length != Length
            || !iban.StartsWith("PK", StringComparison.Ordinal)
            || !char.IsAsciiDigit(iban[2]) || !char.IsAsciiDigit(iban[3])
            || !iban[4..8].All(char.IsAsciiLetterUpper)
            || Mod97(iban[4..] + iban[..4]) != 1)
        {
            return false;
        }

        normalized = iban;
        return true;
    }

    /// <summary>"PK36SCBL0000001123456702" → "PK36 SCBL 0000 0011 2345 6702". Also works on a masked value.</summary>
    public static string Format(string value)
    {
        var groups = new List<string>();
        for (var i = 0; i < value.Length; i += 4)
        {
            groups.Add(value.Substring(i, Math.Min(4, value.Length - i)));
        }

        return string.Join(' ', groups);
    }

    /// <summary>Builds a valid PK IBAN for a 4-letter bank code and a 16-character account part (used for fake demo data).</summary>
    public static string Create(string bankCode, string account)
    {
        var body = bankCode.ToUpperInvariant() + account.ToUpperInvariant();
        var check = 98 - Mod97(body + "PK00");
        return $"PK{check:00}{body}";
    }

    private static int Mod97(string rearranged)
    {
        var remainder = 0;
        foreach (var c in rearranged)
        {
            remainder = char.IsAsciiDigit(c)
                ? (remainder * 10 + (c - '0')) % 97
                : (remainder * 100 + (c - 'A' + 10)) % 97;
        }

        return remainder;
    }
}
