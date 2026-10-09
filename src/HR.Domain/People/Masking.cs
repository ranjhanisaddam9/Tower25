namespace HR.Domain.People;

/// <summary>Masks identifiers (CNIC, IBAN) on every page except a person's details page.</summary>
public static class Masking
{
    public const char MaskChar = '•';

    /// <summary>
    /// Replaces every letter and digit with • except the last <paramref name="visible"/> letters/digits.
    /// Separators stay, so the shape stays recognisable: "12345-1234567-1" → "•••••-••••567-1".
    /// </summary>
    public static string MaskAllButLast(string? value, int visible = 4)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var remaining = value.Count(char.IsAsciiLetterOrDigit);
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(chars[i]))
            {
                continue;
            }

            if (remaining > visible)
            {
                chars[i] = MaskChar;
            }

            remaining--;
        }

        return new string(chars);
    }

    public static string Cnic(string? cnic) => MaskAllButLast(cnic);

    /// <summary>Masked and grouped in 4s: "•••• •••• •••• •••• •••• 6702".</summary>
    public static string Iban(string? iban) => string.IsNullOrEmpty(iban) ? string.Empty : PakistaniIban.Format(MaskAllButLast(iban));
}
