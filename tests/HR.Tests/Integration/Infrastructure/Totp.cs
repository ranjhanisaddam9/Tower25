using System.Security.Cryptography;

namespace HR.Tests.Integration.Infrastructure;

/// <summary>RFC 6238 TOTP (SHA-1, 30 s, 6 digits): what an authenticator app computes from the Base32 key.</summary>
public static class Totp
{
    // The server accepts each time step once per user (M10 replay guard), so tests never reuse one.
    private static readonly Dictionary<string, long> LastStep = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    // Test hosts run on an adjustable clock (tests move it forward); the "authenticator app" must use the same time.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TimeProvider> Clocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Computes this key's codes from <paramref name="clock"/> (the test host's time) instead of real time.</summary>
    public static void UseClock(string base32Key, TimeProvider clock) => Clocks[base32Key] = clock;

    public static DateTimeOffset Now(string base32Key) => Clocks.TryGetValue(base32Key, out var clock) ? clock.GetUtcNow() : DateTimeOffset.UtcNow;

    /// <summary>
    /// A code for a time step this key has not used yet: the current step, or a later one inside the server's ±2-step drift
    /// window. Waits for the clock when the window is used up.
    /// </summary>
    public static async Task<string> NextCodeAsync(string base32Key)
    {
        while (true)
        {
            var now = Now(base32Key).ToUnixTimeSeconds() / 30;
            lock (Gate)
            {
                var step = LastStep.TryGetValue(base32Key, out var last) ? Math.Max(now, last + 1) : now;
                if (step <= now + 1) // stay well inside the server's window
                {
                    LastStep[base32Key] = step;
                    return Code(base32Key, DateTimeOffset.FromUnixTimeSeconds(step * 30));
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(31 - (Now(base32Key).ToUnixTimeSeconds() % 30)));
        }
    }

    public static string Code(string base32Key, DateTimeOffset? at = null)
    {
        var key = Base32Decode(base32Key);
        var counter = (at ?? Now(base32Key)).ToUnixTimeSeconds() / 30;
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
        var valid = Enumerable.Range(-3, 7).Select(step => Code(base32Key, Now(base32Key).AddSeconds(step * 30))).ToHashSet();
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
