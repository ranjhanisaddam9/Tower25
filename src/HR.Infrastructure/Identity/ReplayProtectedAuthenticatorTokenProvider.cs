using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace HR.Infrastructure.Identity;

/// <summary>
/// RFC 6238 authenticator codes (SHA-1, 30 s steps, 6 digits, ±2 steps of clock drift: the same as Identity's
/// <see cref="AuthenticatorTokenProvider{TUser}"/>) with one addition (M10): a code is single-use. The accepted time step
/// is stored on the user, and any code whose step is at or before the last accepted one is refused, both at sign-in and
/// when confirming enrolment. Two requests racing with the same code can't both win: the save is guarded by Identity's
/// concurrency stamp, so the second one fails and is refused.
/// </summary>
public sealed class ReplayProtectedAuthenticatorTokenProvider(TimeProvider time) : IUserTwoFactorTokenProvider<ApplicationUser>
{
    public const int StepSeconds = 30;
    public const int DriftSteps = 2;

    public async Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<ApplicationUser> manager, ApplicationUser user) =>
        !string.IsNullOrWhiteSpace(await manager.GetAuthenticatorKeyAsync(user));

    /// <summary>Codes come from the user's authenticator app, never from the server.</summary>
    public Task<string> GenerateAsync(string purpose, UserManager<ApplicationUser> manager, ApplicationUser user) =>
        Task.FromResult(string.Empty);

    public async Task<bool> ValidateAsync(string purpose, string token, UserManager<ApplicationUser> manager, ApplicationUser user)
    {
        var key = await manager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrWhiteSpace(key) || token.Length != 6 || !token.All(char.IsAsciiDigit))
        {
            return false;
        }

        var step = MatchingStep(Base32Decode(key), token, time.GetUtcNow());
        if (step is null || step <= user.LastTotpTimeStep)
        {
            return false; // wrong, or already used (or older than a code already used)
        }

        user.LastTotpTimeStep = step;
        return (await manager.UpdateAsync(user)).Succeeded;
    }

    /// <summary>The time step (within the drift window around <paramref name="now"/>) whose code equals <paramref name="code"/>.</summary>
    public static long? MatchingStep(byte[] key, string code, DateTimeOffset now)
    {
        var current = now.ToUnixTimeSeconds() / StepSeconds;
        for (var step = current - DriftSteps; step <= current + DriftSteps; step++)
        {
            if (CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(Code(key, step)), System.Text.Encoding.ASCII.GetBytes(code)))
            {
                return step;
            }
        }

        return null;
    }

    public static string Code(byte[] key, long step)
    {
        var message = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(message);
        }

        var hash = HMACSHA1.HashData(key, message);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    public static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>(input.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in input.Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('=').ToUpperInvariant())
        {
            var value = alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new FormatException("The authenticator key is not valid Base32.");
            }

            buffer = (buffer << 5) | value;
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
