using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace HR.Tests.Integration.Infrastructure;

/// <summary>Hosts HR.Web in-memory against the HRPayroll_Test database, in the given environment.</summary>
public sealed class HrWebApplicationFactory(string environment) : WebApplicationFactory<Program>
{
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
            });
        });
    }
}

public static class FactoryExtensions
{
    /// <summary>An HTTPS client that does not follow redirects, so tests can assert on them.</summary>
    public static HttpClient CreateHttpsClient<TEntryPoint>(this WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
}
