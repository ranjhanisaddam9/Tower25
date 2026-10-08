using System.Security.Cryptography;

namespace HR.Infrastructure.Identity;

public interface ITemporaryPasswordGenerator
{
    string Generate();
}

/// <summary>
/// 14-character temporary passwords from a cryptographic RNG. Always contains an uppercase letter,
/// a lowercase letter and a digit (the password policy), and skips look-alike characters (0/O, 1/l/I)
/// so the password can be read out or typed without mistakes.
/// </summary>
public sealed class TemporaryPasswordGenerator : ITemporaryPasswordGenerator
{
    public const int Length = 14;

    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string All = Upper + Lower + Digits;

    public string Generate()
    {
        var chars = new char[Length];
        chars[0] = Pick(Upper);
        chars[1] = Pick(Lower);
        chars[2] = Pick(Digits);
        for (var i = 3; i < Length; i++)
        {
            chars[i] = Pick(All);
        }

        // Fisher–Yates shuffle so the guaranteed characters are not always first.
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
