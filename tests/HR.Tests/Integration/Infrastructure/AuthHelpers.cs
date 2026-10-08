using System.Net;
using System.Text.RegularExpressions;
using HR.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HR.Tests.Integration.Infrastructure;

public sealed record TestUser(string Id, string Email, string Password);

/// <summary>Creates users directly and drives the real cookie login flow through HTTP.</summary>
public static partial class AuthHelpers
{
    public const string DefaultPassword = "Correct-Horse-42";

    public static async Task<TestUser> CreateUserAsync(
        this HrWebApplicationFactory factory,
        string role,
        string? email = null,
        string password = DefaultPassword,
        bool mustChangePassword = false,
        bool isActive = true,
        string? fullName = null)
    {
        email ??= $"{role.ToLowerInvariant()}.{Guid.NewGuid():N}@example.test";
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = fullName ?? $"Test {role}",
            IsActive = isActive,
            MustChangePassword = mustChangePassword,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        AssertSucceeded(await users.CreateAsync(user, password));
        AssertSucceeded(await users.AddToRoleAsync(user, role));
        return new TestUser(user.Id, email, password);
    }

    public static async Task<ApplicationUser> GetUserAsync(this HrWebApplicationFactory factory, string id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await users.FindByIdAsync(id) ?? throw new InvalidOperationException($"User {id} not found.");
    }

    /// <summary>The antiforgery token from the first form on the page (the page must return 200).</summary>
    public static async Task<string> GetAntiforgeryTokenAsync(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url} returned {(int)response.StatusCode}, expected 200.");
        var html = await response.Content.ReadAsStringAsync();
        var match = TokenRegex().Match(html);
        Assert.True(match.Success, $"No antiforgery token found on {url}.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public static async Task<HttpResponseMessage> PostLoginAsync(this HttpClient client, string email, string password, string? returnUrl = null)
    {
        var token = await client.GetAntiforgeryTokenAsync("/account/login");
        var form = new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = token,
        };
        if (returnUrl is not null)
        {
            form["ReturnUrl"] = returnUrl;
        }

        return await client.PostAsync("/account/login", new FormUrlEncodedContent(form));
    }

    /// <summary>Posts a form, taking a fresh antiforgery token from <paramref name="tokenPage"/> first.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(this HttpClient client, string url, string tokenPage, IDictionary<string, string>? fields = null)
    {
        var token = await client.GetAntiforgeryTokenAsync(tokenPage);
        var form = new Dictionary<string, string>(fields ?? new Dictionary<string, string>())
        {
            ["__RequestVerificationToken"] = token,
        };
        return await client.PostAsync(url, new FormUrlEncodedContent(form));
    }

    public static async Task<HttpClient> CreateSignedInClientAsync(this HrWebApplicationFactory factory, TestUser user)
    {
        var client = factory.CreateHttpsClient();
        var response = await client.PostLoginAsync(user.Email, user.Password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    public static async Task<(HttpClient Client, TestUser User)> SignInAsAsync(this HrWebApplicationFactory factory, string role)
    {
        var user = await factory.CreateUserAsync(role);
        return (await factory.CreateSignedInClientAsync(user), user);
    }

    public static string? LoginError(string html)
    {
        var match = LoginErrorRegex().Match(html);
        return match.Success ? WebUtility.HtmlDecode(Regex.Replace(match.Groups[1].Value, "<[^>]+>", string.Empty)).Trim() : null;
    }

    private static void AssertSucceeded(IdentityResult result) =>
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    [GeneratedRegex("data-testid=\"login-error\">(.*?)</div>\\s*</div>", RegexOptions.Singleline)]
    private static partial Regex LoginErrorRegex();
}
