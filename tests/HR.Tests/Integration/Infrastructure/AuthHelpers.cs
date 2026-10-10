using System.Net;
using System.Text.RegularExpressions;
using HR.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HR.Tests.Integration.Infrastructure;

public sealed record TestUser(string Id, string Email, string Password, string? AuthenticatorKey = null);

/// <summary>
/// Creates users directly and drives the real cookie login flow through HTTP. Admins need two-factor sign-in (M10), so
/// Admin test users are enrolled with an authenticator key and <see cref="PostLoginAsync"/> completes the second step
/// with a real TOTP code, exactly as a person with an authenticator app would.
/// </summary>
public static partial class AuthHelpers
{
    public const string DefaultPassword = "Correct-Horse-42";

    /// <summary>Authenticator keys of enrolled test users, by email (the login helper answers the code step with them).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<TestUser> CreateUserAsync(
        this HrWebApplicationFactory factory,
        string role,
        string? email = null,
        string password = DefaultPassword,
        bool mustChangePassword = false,
        bool isActive = true,
        string? fullName = null,
        bool? enrolTwoFactor = null)
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

        if (enrolTwoFactor ?? role == AppRoles.Admin)
        {
            var key = await EnrolAsync(users, user);
            return new TestUser(user.Id, email, password, key);
        }

        return new TestUser(user.Id, email, password);
    }

    /// <summary>Enrols an existing user in two-factor sign-in (as the setup page would) and remembers the key.</summary>
    public static async Task<string> EnrolTwoFactorAsync(this HrWebApplicationFactory factory, string userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await EnrolAsync(users, (await users.FindByIdAsync(userId))!);
    }

    private static async Task<string> EnrolAsync(UserManager<ApplicationUser> users, ApplicationUser user)
    {
        AssertSucceeded(await users.ResetAuthenticatorKeyAsync(user));
        var key = (await users.GetAuthenticatorKeyAsync(user))!;
        AssertSucceeded(await users.SetTwoFactorEnabledAsync(user, true));
        Keys[user.Email!] = key;
        return key;
    }

    /// <summary>Forgets a user's key (e.g. after an Admin reset their two-factor), so login stops at the code step.</summary>
    public static void ForgetAuthenticator(string email) => Keys.TryRemove(email, out _);

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

        var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(form));

        // Two-factor accounts: answer the code step like an authenticator app would.
        if (response.StatusCode == HttpStatusCode.Redirect
            && response.Headers.Location?.OriginalString.StartsWith("/account/login-2fa", StringComparison.Ordinal) == true
            && Keys.TryGetValue(email, out var key))
        {
            return await client.PostTwoFactorCodeAsync(Totp.Code(key), response.Headers.Location.OriginalString);
        }

        return response;
    }

    /// <summary>Posts an authenticator code to the second sign-in step.</summary>
    public static async Task<HttpResponseMessage> PostTwoFactorCodeAsync(this HttpClient client, string code, string page = "/account/login-2fa")
    {
        var token = await client.GetAntiforgeryTokenAsync(page);
        var query = page.Contains('?', StringComparison.Ordinal) ? page[page.IndexOf('?', StringComparison.Ordinal)..] : string.Empty;
        return await client.PostAsync("/account/login-2fa" + query, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Code"] = code,
            ["ReturnUrl"] = System.Web.HttpUtility.ParseQueryString(query).Get("returnUrl") ?? string.Empty,
            ["__RequestVerificationToken"] = token,
        }));
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
