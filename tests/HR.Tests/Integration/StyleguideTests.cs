using System.Net;
using System.Text.RegularExpressions;
using HR.Tests.Integration.Infrastructure;

namespace HR.Tests.Integration;

public partial class StyleguideTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Styleguide_returns_200_in_Development_with_every_component()
    {
        using var client = Fixture.Development.CreateHttpsClient();

        var response = await client.GetAsync("/dev/styleguide");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var marker in new[]
                 {
                     "glass-card", "stat-tile", "glass-table", "data-label=\"Pay (PKR)\"", "pill pill-success",
                     "empty-state", "btn-primary-gradient", "btn-ghost", "btn-icon", "form-floating",
                     "is-invalid", "is-valid", "data-confirm=", "id=\"confirmModal\"",
                 })
        {
            Assert.Contains(marker, html);
        }

        Assert.Contains("$150.00", html);
        Assert.Contains("Rs 42,000", html);
    }

    [Fact]
    public async Task Styleguide_returns_404_in_Production()
    {
        using var client = Fixture.Production.CreateHttpsClient();

        var response = await client.GetAsync("/dev/styleguide");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Page not found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Toast_post_without_antiforgery_token_is_rejected()
    {
        using var client = Fixture.Development.CreateHttpsClient();

        var response = await client.PostAsync("/dev/styleguide/toast",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["kind"] = "success" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Toast_post_with_token_redirects_and_shows_toast()
    {
        using var client = Fixture.Development.CreateHttpsClient();
        var page = await client.GetStringAsync("/dev/styleguide");
        var token = TokenRegex().Match(page).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));

        var response = await client.PostAsync("/dev/styleguide/toast", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["kind"] = "error",
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dev/styleguide", response.Headers.Location?.OriginalString);

        var html = await client.GetStringAsync("/dev/styleguide");
        Assert.Contains("toast-error", html);
        Assert.Contains("sample error toast", html);

        // TempData is read once: the toast does not reappear.
        Assert.DoesNotContain("sample error toast", await client.GetStringAsync("/dev/styleguide"));
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();
}
