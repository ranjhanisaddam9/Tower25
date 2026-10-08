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

    public static IServiceCollection AddLoginRateLimit(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(PolicyName, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ClientIp.Of(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = PermitLimit,
                        Window = Window,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));

            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(SecurityLog.Category);
                SecurityLog.LoginRateLimited(logger, ClientIp.Of(context.HttpContext));

                // No body here: the status-code page middleware renders the friendly /error/429 page.
                return ValueTask.CompletedTask;
            };
        });

        return services;
    }
}

public static class ClientIp
{
    /// <summary>The connecting client's IP (behind a reverse proxy, forwarded headers must be configured first; see M10).</summary>
    public static string Of(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
