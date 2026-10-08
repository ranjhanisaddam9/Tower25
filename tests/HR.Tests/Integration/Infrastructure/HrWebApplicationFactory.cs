using System.Net;
using HR.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace HR.Tests.Integration.Infrastructure;

/// <summary>
/// Hosts HR.Web in-memory against the HRPayroll_Test database, in the given environment.
/// Adds three test seams: a per-client fake IP (header <see cref="ClientIpHeader"/>), captured logs,
/// and an adjustable clock for cookie/security-stamp validation.
/// </summary>
public sealed class HrWebApplicationFactory(string environment) : WebApplicationFactory<Program>
{
    public const string ClientIpHeader = "X-Test-Client-IP";

    private static int _nextClientIp;

    public CapturedLogs Logs { get; } = new();

    public AdjustableTimeProvider Time { get; } = new();

    /// <summary>A unique fake client IP, so the per-IP login rate limit never couples unrelated tests.</summary>
    public static string NextClientIp()
    {
        var n = Interlocked.Increment(ref _nextClientIp);
        return $"10.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}";
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = TestDatabaseFixture.ConnectionString,
                ["Database:MigrateOnStartup"] = "false", // the fixture migrates the test database
                ["https_port"] = "443",
                // Never seed the real Admin (from the developer's user secrets) into the test database.
                [AdminSeeder.PasswordKey] = string.Empty,
            });
        });

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new FakeClientIpStartupFilter());

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.PostConfigure<SecurityStampValidatorOptions>(options => options.TimeProvider = Time);
        });
    }

    private sealed class FakeClientIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(ClientIpHeader, out var value) && IPAddress.TryParse(value, out var ip))
                {
                    context.Connection.RemoteIpAddress = ip;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}

/// <summary>A clock that runs with real time plus an offset tests can move forward.</summary>
public sealed class AdjustableTimeProvider : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
}

public static class FactoryExtensions
{
    /// <summary>An HTTPS client that keeps cookies, does not follow redirects, and has its own fake client IP.</summary>
    public static HttpClient CreateHttpsClient<TEntryPoint>(this WebApplicationFactory<TEntryPoint> factory, string? clientIp = null)
        where TEntryPoint : class
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        client.DefaultRequestHeaders.Add(HrWebApplicationFactory.ClientIpHeader, clientIp ?? HrWebApplicationFactory.NextClientIp());
        return client;
    }
}
