using System.Text.RegularExpressions;
using HR.Infrastructure.Identity;
using HR.Tests.Integration.Infrastructure;
using HR.Web.Security;

namespace HR.Tests.Integration;

public partial class SecurityBaselineTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    public static TheoryData<string, string> Urls => new()
    {
        { "Development", "/" },
        { "Development", "/account/login" },
        { "Development", "/dev/styleguide" },
        { "Development", "/css/glass.css" },
        { "Development", "/js/site.js" },
        { "Development", "/no-such-page" },
        { "Production", "/" },
        { "Production", "/account/login" },
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

    [Fact]
    public async Task Static_files_are_served_to_anonymous_visitors()
    {
        using var client = Fixture.Production.CreateHttpsClient();

        foreach (var url in new[] { "/css/glass.css", "/js/theme-init.js", "/lib/bootstrap/dist/css/bootstrap.min.css" })
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task Rendered_pages_have_no_inline_scripts_styles_handlers_or_cdn_links()
    {
        using var anonymous = Fixture.Development.CreateHttpsClient();
        var pages = new List<string> { await anonymous.GetStringAsync("/account/login") };

        var (admin, _) = await Fixture.Development.SignInAsAsync(AppRoles.Admin);
        foreach (var url in new[] { "/", "/dev/styleguide", "/no-such-page", "/admin/managers", "/admin/managers/create", "/account/change-password" })
        {
            pages.Add(await (await admin.GetAsync(url)).Content.ReadAsStringAsync());
        }

        foreach (var html in pages)
        {
            Assert.Empty(InlineScriptRegex().Matches(html));
            Assert.DoesNotContain(" style=\"", html);
            Assert.Empty(EventHandlerRegex().Matches(html));
            Assert.Empty(ExternalAssetRegex().Matches(html));
            Assert.Contains("<script src=\"/js/theme-init", html); // the theme script is external
        }
    }

    [Fact]
    public async Task Antiforgery_cookie_is_secure_httponly_and_lax()
    {
        using var client = Fixture.Development.CreateHttpsClient();
        var page = await client.GetAsync("/account/login");

        var cookie = page.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(CookieSecurity.AntiforgeryCookieName + "=", StringComparison.Ordinal));

        AssertSecureCookie(cookie);
    }

    [Fact]
    public async Task Auth_cookie_is_named_hr_auth_secure_httponly_lax_and_session_only()
    {
        var user = await Fixture.Development.CreateUserAsync(AppRoles.Manager);
        using var client = Fixture.Development.CreateHttpsClient();

        var response = await client.PostLoginAsync(user.Email, user.Password);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AuthCookie.Name + "=", StringComparison.Ordinal));

        AssertSecureCookie(cookie);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase); // no "remember me": session cookie
    }

    private static void AssertSecureCookie(string cookie)
    {
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
