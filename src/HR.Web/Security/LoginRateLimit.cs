using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using HR.Infrastructure.Security;
using Microsoft.AspNetCore.RateLimiting;

namespace HR.Web.Security;

/// <summary>POST /account/login: at most 10 attempts per client IP per minute, then 429 with a friendly page.</summary>
public static class LoginRateLimit
{
    public const string PolicyName = "login";
    public const int PermitLimit = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Registers every rate-limit policy (login and change password) and the shared 429 handling.</summary>
    public static IServiceCollection AddLoginRateLimit(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(PolicyName, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ClientIp.Of(context),
                    _ => FixedWindow(PermitLimit, Window)));

            // Partitioned by the signed-in user (UseRateLimiter runs after UseAuthentication).
            options.AddPolicy(ChangePasswordRateLimit.PolicyName, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ChangePasswordRateLimit.PartitionKey(context),
                    _ => FixedWindow(ChangePasswordRateLimit.PermitLimit, ChangePasswordRateLimit.Window)));

            options.OnRejected = (context, _) =>
            {
                var http = context.HttpContext;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(SecurityLog.Category);
                var policy = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
                if (policy == ChangePasswordRateLimit.PolicyName)
                {
                    SecurityLog.ChangePasswordRateLimited(logger, ChangePasswordRateLimit.PartitionKey(http), ClientIp.Of(http));
                }
                else
                {
                    SecurityLog.LoginRateLimited(logger, ClientIp.Of(http));
                }

                // No body here: the status-code page middleware renders the friendly /error/429 page.
                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    private static FixedWindowRateLimiterOptions FixedWindow(int permits, TimeSpan window) => new()
    {
        PermitLimit = permits,
        Window = window,
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}

/// <summary>POST /account/change-password: at most 5 attempts per signed-in user per minute.</summary>
public static class ChangePasswordRateLimit
{
    public const string PolicyName = "change-password";
    public const int PermitLimit = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>The user id; falls back to the client IP (the action requires sign-in, so that is never expected).</summary>
    public static string PartitionKey(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } userId
            ? "user:" + userId
            : "ip:" + ClientIp.Of(context);
}

public static class ClientIp
{
    /// <summary>The connecting client's IP (behind a reverse proxy, forwarded headers must be configured first; see M10).</summary>
    public static string Of(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
