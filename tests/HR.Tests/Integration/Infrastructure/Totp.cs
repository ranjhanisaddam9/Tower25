using System.Security.Cryptography;

namespace HR.Tests.Integration.Infrastructure;

/// <summary>RFC 6238 TOTP (SHA-1, 30 s, 6 digits): what an authenticator app computes from the Base32 key.</summary>
public static class Totp
{
    public static string Code(string base32Key, DateTimeOffset? at = null)
    {
        var key = Base32Decode(base32Key);
        var counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        var message = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(message);
        }

        var hash = HMACSHA1.HashData(key, message);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>A code that is certainly wrong now (and in the neighbouring time steps the server also accepts).</summary>
    public static string WrongCode(string base32Key)
    {
        var valid = Enumerable.Range(-3, 7).Select(step => Code(base32Key, DateTimeOffset.UtcNow.AddSeconds(step * 30))).ToHashSet();
        for (var candidate = 0; ; candidate++)
        {
            var code = candidate.ToString("D6", CultureInfo.InvariantCulture);
            if (!valid.Contains(code))
            {
                return code;
            }
        }
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = input.Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
