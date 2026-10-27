using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace HR.Infrastructure.Identity;

/// <summary>
/// Owner request (18 Oct 2026, temporary): <c>Security:DisableTwoFactorInDevelopment = true</c> turns two-factor sign-in
/// off on the dev PC only. Sign-in then skips the authenticator step and the enrolment gate is off; email and password
/// are still required. Outside Development the setting is ignored, so a server can never run without two-factor.
/// </summary>
public sealed class TwoFactorSwitch(IConfiguration configuration, IHostEnvironment environment)
{
    public const string SettingKey = "Security:DisableTwoFactorInDevelopment";

    /// <summary>Read on every call, so a changed setting applies without a restart.</summary>
    public bool Disabled => environment.IsDevelopment() && configuration.GetValue<bool>(SettingKey);
}
