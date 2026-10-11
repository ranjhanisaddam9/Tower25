using System.Text;
using HR.Infrastructure.Identity;

namespace HR.Tests.Unit;

/// <summary>M10: the replay-protected authenticator provider computes standard RFC 6238 codes.</summary>
public class TotpUnitTests
{
    // RFC 6238 appendix B, SHA-1 seed "12345678901234567890"; 6-digit codes are the last 6 digits of the 8-digit vectors.
    private static readonly byte[] RfcKey = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void Codes_match_the_RFC_6238_test_vectors(long unixSeconds, string expected) =>
        Assert.Equal(expected, ReplayProtectedAuthenticatorTokenProvider.Code(RfcKey, unixSeconds / 30));

    [Fact]
    public void The_matching_step_is_found_within_two_steps_of_drift_and_not_beyond()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var current = now.ToUnixTimeSeconds() / 30;
        for (var drift = -2; drift <= 2; drift++)
        {
            var code = ReplayProtectedAuthenticatorTokenProvider.Code(RfcKey, current + drift);
            Assert.Equal(current + drift, ReplayProtectedAuthenticatorTokenProvider.MatchingStep(RfcKey, code, now));
        }

        var tooOld = ReplayProtectedAuthenticatorTokenProvider.Code(RfcKey, current - 3);
        var tooNew = ReplayProtectedAuthenticatorTokenProvider.Code(RfcKey, current + 3);
        Assert.Null(ReplayProtectedAuthenticatorTokenProvider.MatchingStep(RfcKey, tooOld, now));
        Assert.Null(ReplayProtectedAuthenticatorTokenProvider.MatchingStep(RfcKey, tooNew, now));
    }

    [Fact]
    public void Base32_decoding_round_trips_the_RFC_key()
    {
        // "12345678901234567890" in Base32.
        Assert.Equal(RfcKey, ReplayProtectedAuthenticatorTokenProvider.Base32Decode("GEZD GNBV GY3T QOJQ GEZD GNBV GY3T QOJQ"));
        Assert.Throws<FormatException>(() => ReplayProtectedAuthenticatorTokenProvider.Base32Decode("not-base32!"));
    }
}
