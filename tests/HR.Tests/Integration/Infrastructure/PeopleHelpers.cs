using System.Net;
using System.Text.RegularExpressions;
using HR.Domain.People;
using HR.Infrastructure.People;
using Microsoft.Extensions.DependencyInjection;

namespace HR.Tests.Integration.Infrastructure;

/// <summary>Creates people straight through <see cref="PersonService"/> and builds people forms for HTTP posts.</summary>
public static partial class PeopleHelpers
{
    private static int _phoneCounter;

    /// <summary>A unique valid mobile number in the 0345 range (test data only).</summary>
    public static string NextPhone() => $"0345{Interlocked.Increment(ref _phoneCounter) % 10_000_000:0000000}";

    public static async Task<int> CreatePersonAsync(
        this HrWebApplicationFactory factory,
        string fullName,
        PersonType type = PersonType.Employee,
        string? email = null,
        string? phone = null,
        string? cnic = null,
        string? iban = null,
        DateOnly? joined = null,
        HireSource? source = null,
        DateOnly? left = null,
        string designation = "Engineer")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var people = scope.ServiceProvider.GetRequiredService<PersonService>();

        var result = await people.CreateAsync(
            new PersonInput(fullName, type, designation, email, phone ?? NextPhone(), cnic, null, iban, joined ?? new DateOnly(2025, 1, 6), null),
            "test-setup");
        Assert.True(result.Succeeded, string.Join("; ", result.Errors?.Select(e => $"{e.Field}: {e.Message}") ?? []));
        var id = result.Id!.Value;

        if (source is not null)
        {
            Assert.True((await people.SetHireSourceAsync(id, source, "test-setup")).Succeeded);
        }

        if (left is not null)
        {
            Assert.True((await people.DeactivateAsync(id, left, "test-setup")).Succeeded);
        }

        return id;
    }

    public static Dictionary<string, string> Form(
        string fullName = "Test Person",
        string type = "Employee",
        string designation = "Engineer",
        string? email = null,
        string? phone = null,
        string? cnic = null,
        string? iban = null,
        string joined = "2026-01-05",
        string? notes = null)
    {
        var form = new Dictionary<string, string>
        {
            ["FullName"] = fullName,
            ["Type"] = type,
            ["Designation"] = designation,
            ["Phone"] = phone ?? NextPhone(),
            ["JoiningDate"] = joined,
        };
        if (email is not null) form["Email"] = email;
        if (cnic is not null) form["Cnic"] = cnic;
        if (iban is not null) form["Iban"] = iban;
        if (notes is not null) form["Notes"] = notes;
        return form;
    }

    /// <summary>The RowVersion hidden field from an edit page.</summary>
    public static string RowVersion(string html)
    {
        var match = RowVersionRegex().Match(html);
        Assert.True(match.Success, "No RowVersion field on the page.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>Follows one redirect (POST-redirect-GET) and returns the resulting page.</summary>
    public static async Task<string> FollowAsync(this HttpClient client, HttpResponseMessage redirect)
    {
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        return await client.GetStringAsync(redirect.Headers.Location);
    }

    public static int IdFromLocation(HttpResponseMessage redirect)
    {
        var match = IdRegex().Match(redirect.Headers.Location?.OriginalString ?? string.Empty);
        Assert.True(match.Success, $"Unexpected redirect: {redirect.Headers.Location}");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("name=\"RowVersion\" value=\"([^\"]*)\"")]
    private static partial Regex RowVersionRegex();

    [GeneratedRegex("^/people/(\\d+)$")]
    private static partial Regex IdRegex();
}
