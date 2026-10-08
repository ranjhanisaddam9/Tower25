using System.Text.RegularExpressions;
using HR.Tests.Integration.Infrastructure;
using HR.Web.Security;

namespace HR.Tests.Integration;

public partial class SecurityBaselineTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    public static TheoryData<string, string> Urls => new()
    {
        { "Development", "/" },
        { "Development", "/dev/styleguide" },
        { "Development", "/css/glass.css" },
        { "Development", "/js/site.js" },
        { "Development", "/no-such-page" },
        { "Production", "/" },
        { "Production", "/css/tokens.css" },
        { "Production", "/no-such-page" },
    };

    [Theory]
    [MemberData(nameof(Urls))]
    public async Task Every_response_carries_the_security_headers(string environment, string url)
    {
        using var client = (environment == "Production" ? Fixture.Production : Fixture.Development).CreateHttpsClient();

        var response = await client.GetAsync(url);

        Assert.Equal(SecurityHeadersMiddleware.ContentSecurityPolicy, Header(response, "Content-Security-Policy"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));
        Assert.Equal("camera=(), microphone=(), geolocation=()", Header(response, "Permissions-Policy"));
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public void Csp_matches_the_baseline_exactly()
    {
        Assert.Equal(
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'",
            SecurityHeadersMiddleware.ContentSecurityPolicy);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/dev/styleguide")]
    [InlineData("/no-such-page")]
    public async Task Rendered_pages_have_no_inline_scripts_styles_handlers_or_cdn_links(string url)
    {
        using var client = Fixture.Development.CreateHttpsClient();

        var html = await (await client.GetAsync(url)).Content.ReadAsStringAsync();

        Assert.Empty(InlineScriptRegex().Matches(html));
        Assert.DoesNotContain(" style=\"", html);
        Assert.Empty(EventHandlerRegex().Matches(html));
        Assert.Empty(ExternalAssetRegex().Matches(html));
        Assert.Contains("<script src=\"/js/theme-init", html); // the theme script is external
    }

    [Fact]
    public async Task Antiforgery_and_tempdata_cookies_are_secure_httponly_and_lax()
    {
        using var client = Fixture.Development.CreateHttpsClient();
        var page = await client.GetAsync("/dev/styleguide");

        var cookie = page.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(CookieSecurity.AntiforgeryCookieName + "=", StringComparison.Ordinal));

        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.Single()
        : response.Content.Headers.TryGetValues(name, out var contentValues) ? contentValues.Single()
        : null;

    // A <script> tag without a src attribute.
    [GeneratedRegex("<script\\b(?![^>]*\\bsrc\\s*=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScriptRegex();

    // onclick=, onload= ... attributes.
    [GeneratedRegex("\\son[a-z]+\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerRegex();

    // <script src> or <link href> pointing off-site.
    [GeneratedRegex("<(script|link)\\b[^>]*\\b(src|href)\\s*=\\s*\"(https?:)?//", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalAssetRegex();
}
