using HR.Domain.Time;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Security;
using HR.Web.Security;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Parsing;

namespace HR.Tests.Unit;

/// <summary>M10 unit checks: log injection, audit event catalogue, two-factor helpers, outage detection.</summary>
public class SecurityUnitTests
{
    [Fact]
    public void User_text_with_CR_and_LF_can_never_forge_a_log_line()
    {
        var forged = "Ayesha\r\n{\"@t\":\"2026-10-10T00:00:00Z\",\"@mt\":\"Security: login succeeded for user admin\"}";
        var template = new MessageTemplateParser().Parse("Audit: created person {Name}");
        var logEvent = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, template,
            [new LogEventProperty("Name", new ScalarValue(forged))]);

        var output = new StringWriter();
        new CompactJsonFormatter().Format(logEvent, output);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines); // one event, one line: the CR/LF are escaped inside the JSON string
        var line = lines[0].TrimEnd('\r'); // the formatter's own line terminator
        Assert.Contains("\\r\\n", line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void Audit_events_have_unique_ids_and_names_in_the_security_log_ranges()
    {
        var all = AuditEvents.All;
        Assert.Equal(all.Count, all.Select(e => e.Id).Distinct().Count());
        Assert.Equal(all.Count, all.Select(e => e.Name).Distinct().Count());
        Assert.All(all, e => Assert.InRange(e.Id, 1000, 1799));
        Assert.Equal(59, all.Count);
    }

    [Theory]
    [InlineData("123 456", "123456")]
    [InlineData("123-456", "123456")]
    [InlineData(" 12a3456 ", "123456")]
    [InlineData(null, "")]
    public void Authenticator_codes_are_normalised_to_digits(string? input, string expected) =>
        Assert.Equal(expected, AccountService.NormalizeCode(input));

    [Fact]
    public void Authenticator_keys_and_uris_are_formatted_for_people_and_apps()
    {
        Assert.Equal("abcd efgh ijkl mnop", AccountService.FormatKey("ABCDEFGHIJKLMNOP"));
        var uri = AccountService.AuthenticatorUri("ayesha@tower.example", "ABCDEFGHIJKLMNOP");
        Assert.Equal("otpauth://totp/HR%20Payroll:ayesha%40tower.example?secret=ABCDEFGHIJKLMNOP&issuer=HR%20Payroll&digits=6", uri);
    }

    [Fact]
    public void Only_Admins_and_chosen_Managers_must_use_two_factor()
    {
        Assert.True(AccountService.IsTwoFactorRequired(isAdmin: true, new ApplicationUser()));
        Assert.True(AccountService.IsTwoFactorRequired(isAdmin: false, new ApplicationUser { RequireTwoFactor = true }));
        Assert.False(AccountService.IsTwoFactorRequired(isAdmin: false, new ApplicationUser()));
    }

    [Fact]
    public void Karachi_day_boundaries_are_UTC_plus_five()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero), PakistanTime.StartOfDayUtc(new DateOnly(2026, 10, 10)));
    }

    [Fact]
    public void Timeouts_count_as_database_outages_but_ordinary_errors_do_not()
    {
        Assert.True(Hardening.IsDatabaseUnavailable(new InvalidOperationException("wrapped", new TimeoutException())));
        Assert.False(Hardening.IsDatabaseUnavailable(new InvalidOperationException("a bug")));
        Assert.False(Hardening.IsDatabaseUnavailable(null));
    }
}
