using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using HR.Infrastructure.Data;
using HR.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HR.Web.Security;

/// <summary>M10 hosting hardening: data protection keys, request limits, request rate limits and the health endpoint.</summary>
public static class Hardening
{
    public const string KeysPathKey = "DataProtection:KeysPath";
    public const string ApplicationName = "HRPayroll";
    public const long MaxRequestBodyBytes = 1_048_576; // 1 MB: forms only, no uploads

    /// <summary>
    /// Keys persist to a configurable folder so cookies (auth, antiforgery, TempData) survive app restarts and app-pool
    /// recycles. On Windows the key files are encrypted with DPAPI (machine scope), so they are never plaintext on disk.
    /// </summary>
    public static IServiceCollection AddAppDataProtection(this IServiceCollection services)
    {
        services.AddDataProtection().SetApplicationName(ApplicationName);

        // Resolved from the final configuration when the key ring is first used (environment variables, test hosts).
        services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
            .Configure<IConfiguration, ILoggerFactory>((options, configuration, loggers) =>
            {
                var path = configuration[KeysPathKey];
                if (string.IsNullOrWhiteSpace(path))
                {
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ApplicationName, "DataProtection-Keys");
                }

                options.XmlRepository = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(Directory.CreateDirectory(path), loggers);
                if (OperatingSystem.IsWindows())
                {
                    options.XmlEncryptor = new Microsoft.AspNetCore.DataProtection.XmlEncryption.DpapiXmlEncryptor(protectToLocalMachine: true, loggers);
                }
            });

        return services;
    }

    /// <summary>Body, form and model-binding limits (the IIS limit is also set in web.config).</summary>
    public static WebApplicationBuilder AddRequestLimits(this WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
            options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            options.Limits.MaxRequestLineSize = 8 * 1024;
        });
        builder.Services.Configure<IISServerOptions>(options => options.MaxRequestBodySize = MaxRequestBodyBytes);
        builder.Services.Configure<FormOptions>(options =>
        {
            options.ValueCountLimit = 2048;
            options.KeyLengthLimit = 512;
            options.ValueLengthLimit = 64 * 1024;
            options.MultipartBodyLengthLimit = MaxRequestBodyBytes;
        });
        builder.Services.Configure<MvcOptions>(options =>
        {
            options.MaxModelBindingCollectionSize = 1024;
            options.MaxModelValidationErrors = 100;
        });
        return builder;
    }

    // ===================== Request rate limits =====================

    public const string PostsPerMinuteKey = "RateLimits:PostsPerMinute";
    public const string ExportsPerMinuteKey = "RateLimits:ExportsPerMinute";
    public const string HealthPolicy = "health";

    /// <summary>Partition: the signed-in user, else the client IP.</summary>
    public static string Partition(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } userId ? "user:" + userId : "ip:" + ClientIp.Of(context);

    public static bool IsExport(HttpRequest request) =>
        HttpMethods.IsGet(request.Method)
        && (request.Path.Value?.EndsWith("/export", StringComparison.OrdinalIgnoreCase) == true || request.Path.Value?.EndsWith("/pdf", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>
    /// Every state-changing request: at most 120 per user (or IP) per minute. Exports and PDFs: at most 30 per minute.
    /// Configurable; chained with the per-endpoint login and change-password policies.
    /// </summary>
    public static PartitionedRateLimiter<HttpContext> GlobalLimiter(IConfiguration configuration)
    {
        var posts = configuration.GetValue<int?>(PostsPerMinuteKey) ?? 120;
        var exports = configuration.GetValue<int?>(ExportsPerMinuteKey) ?? 30;
        var postLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
                ? RateLimitPartition.GetNoLimiter("read")
                : RateLimitPartition.GetFixedWindowLimiter("post:" + Partition(context), _ => Window(posts)));
        var exportLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            IsExport(context.Request)
                ? RateLimitPartition.GetFixedWindowLimiter("export:" + Partition(context), _ => Window(exports))
                : RateLimitPartition.GetNoLimiter("other"));
        return PartitionedRateLimiter.CreateChained(postLimiter, exportLimiter);
    }

    public static FixedWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    /// <summary>
    /// Writes a rate-limit audit row at most once per partition per minute, so a flood of rejected requests can't turn
    /// into a flood of database writes.
    /// </summary>
    public static async Task AuditRejectionAsync(HttpContext context, AuditEvent auditEvent, string key, string summary)
    {
        var cache = context.RequestServices.GetRequiredService<IMemoryCache>();
        if (cache.TryGetValue("ratelimit-audit:" + key, out _))
        {
            return;
        }

        cache.Set("ratelimit-audit:" + key, true, TimeSpan.FromMinutes(1));
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        await context.RequestServices.GetRequiredService<AuditWriter>().WriteAsync(auditEvent, actor, null, null, summary, context.RequestAborted);
    }

    // ===================== Health =====================

    public static IServiceCollection AddAppHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");
        return services;
    }

    /// <summary>Anonymous, rate-limited, uncached; the body is only "Healthy" or "Unhealthy" (503): no details.</summary>
    public static void MapAppHealth(this WebApplication app) =>
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = (context, report) =>
            {
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.Headers.CacheControl = "no-store";
                return context.Response.WriteAsync(report.Status == HealthStatus.Healthy ? "Healthy" : "Unhealthy");
            },
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        }).AllowAnonymous().RequireRateLimiting(HealthPolicy);

    // ===================== Database availability =====================

    private static readonly HashSet<int> ConnectivityErrors = [-2, 2, 53, 121, 233, 4060, 18456, 10053, 10054, 10060, 10061, 11001, 40613];

    /// <summary>True when an exception means "the database can't be reached" (shown as 503, not 500).</summary>
    public static bool IsDatabaseUnavailable(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is SqlException sql && (ConnectivityErrors.Contains(sql.Number) || sql.Class >= 20
                || sql.Message.Contains("network-related", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (e is TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    public static string CorrelationId(HttpContext context) =>
        System.Diagnostics.Activity.Current?.TraceId.ToString() is { Length: > 0 } trace && trace != "00000000000000000000000000000000"
            ? trace
            : context.TraceIdentifier;

    public static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The request's client IP for audit rows (via the HTTP context accessor; null outside a request).</summary>
public sealed class HttpAuditContext(IHttpContextAccessor accessor) : IAuditContext
{
    public string? ClientIp => accessor.HttpContext is { } context ? Security.ClientIp.Of(context) : null;
}
