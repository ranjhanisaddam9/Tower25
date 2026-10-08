using System.Net;
using HR.Infrastructure.Identity;
using HR.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HR.Tests.Integration;

public class ErrorPageTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Unknown_url_returns_the_custom_404_page_when_signed_in(string environment)
    {
        var (client, _) = await Factory(environment).SignInAsAsync(AppRoles.Manager);

        var response = await client.GetAsync("/this/route/does-not-exist");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Page not found", html);
        Assert.Contains("Error 404", html);
        Assert.Contains("id=\"appSidebar\"", html); // rendered inside the app layout
    }

    [Fact]
    public async Task Unknown_url_sends_anonymous_visitors_to_sign_in()
    {
        using var client = Fixture.Production.CreateHttpsClient();

        var response = await client.GetAsync("/this/route/does-not-exist");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/login", response.Headers.Location?.PathAndQueryOrOriginal());
    }

    [Fact]
    public async Task Unhandled_exception_in_Production_shows_a_generic_500_page_that_leaks_nothing()
    {
        await using var factory = Fixture.Production.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddControllersWithViews().AddApplicationPart(typeof(ErrorPageTests).Assembly)));
        using var client = factory.CreateHttpsClient();

        var response = await client.GetAsync("/__test/throw");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("Something went wrong", html);
        Assert.DoesNotContain(ThrowingController.SecretDetail, html);
        Assert.DoesNotContain("InvalidOperationException", html);
        Assert.DoesNotContain("StackTrace", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".cs:line", html);
    }

    [Fact]
    public async Task Production_redirects_http_to_https_and_sends_hsts()
    {
        using var client = Fixture.Production.CreateHttpsClient();

        var redirect = await client.GetAsync(new Uri("http://localhost/account/login"));
        Assert.Equal(HttpStatusCode.TemporaryRedirect, redirect.StatusCode);
        Assert.Equal("https://localhost/account/login", redirect.Headers.Location?.ToString());

        // HSTS is never sent for localhost, so use a real-looking host name.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://payroll.example.test/account/login");
        var response = await client.SendAsync(request);
        Assert.True(response.Headers.Contains("Strict-Transport-Security"));
        Assert.Contains("max-age=31536000", response.Headers.GetValues("Strict-Transport-Security").Single());
    }

    private HrWebApplicationFactory Factory(string environment) =>
        environment == "Production" ? Fixture.Production : Fixture.Development;
}

/// <summary>Test-only controller, added as an application part by one test.</summary>
[AllowAnonymous]
[Route("__test")]
public class ThrowingController : Controller
{
    public const string SecretDetail = "secret-connection-detail-42";

    [HttpGet("throw")]
    public IActionResult Throw() => throw new InvalidOperationException(SecretDetail);
}
