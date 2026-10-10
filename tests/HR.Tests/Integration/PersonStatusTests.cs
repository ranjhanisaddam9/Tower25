using System.Net;
using HR.Domain.Pay;
using HR.Domain.Payroll;
using HR.Domain.People;
using HR.Domain.Time;
using HR.Infrastructure.Identity;
using HR.Infrastructure.People;
using HR.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HR.Tests.Integration;

/// <summary>M7 Part A: a person is active until their leaving date has passed; "Cancel leaving" reopens the same period.</summary>
public class PersonStatusTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    private HrWebApplicationFactory App => Fixture.Development;

    private sealed class FixedClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.ToDateTime(new TimeOnly(5, 0)), TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }

    private sealed class LockEverythingBefore(DateOnly lockedBefore) : IPayrollLock
    {
        public Task<bool> IsLockedAsync(DateOnly periodStart, CancellationToken cancellationToken = default) => Task.FromResult(periodStart < lockedBefore);

        public Task<DateOnly?> LatestLockedPeriodStartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DateOnly?>(PayPeriod.For(lockedBefore.AddDays(-1)).Start);
    }

    private WebApplicationFactory<Program> AppWith(DateOnly? today = null, DateOnly? lockedBefore = null) =>
        App.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            if (today is { } t)
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FixedClock(t));
            }

            if (lockedBefore is { } l)
            {
                services.RemoveAll<IPayrollLock>();
                services.AddSingleton<IPayrollLock>(new LockEverythingBefore(l));
            }
        }));

    private async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> factory, string role)
    {
        var user = await App.CreateUserAsync(role);
        var client = factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostLoginAsync(user.Email, user.Password)).StatusCode);
        return client;
    }

    [Fact]
    public async Task A_future_leaving_date_keeps_the_person_active_until_it_has_passed()
    {
        var leaver = await App.CreatePersonAsync("Future Leaver", joined: new DateOnly(2025, 1, 6), left: new DateOnly(2026, 10, 21));
        await App.CreatePersonAsync("Steady Stayer", joined: new DateOnly(2025, 1, 6));

        // Before the date (Oct 9) and on the day itself (Oct 21): active, with the "Leaving" pill, counted on the dashboard.
        foreach (var (day, pill) in new[] { (new DateOnly(2026, 10, 9), "Leaving 21 Oct 2026"), (new DateOnly(2026, 10, 21), "Leaving today") })
        {
            await using var factory = AppWith(today: day);
            var manager = await SignInAsync(factory, AppRoles.Manager);

            var active = await manager.GetStringAsync("/people");
            Assert.Contains("Future Leaver", active);
            Assert.Contains($"data-testid=\"status-leaving\">{pill}<", active);
            Assert.DoesNotContain("Future Leaver", await manager.GetStringAsync("/people?status=Inactive"));
            Assert.Matches("data-testid=\"active-people-tile\"[\\s\\S]*?stat-value\">2<", await manager.GetStringAsync("/"));

            var details = await manager.GetStringAsync($"/people/{leaver}");
            Assert.Contains("data-testid=\"cancel-leaving\"", details);
            Assert.DoesNotContain("data-bs-target=\"#reactivateModal\"", details);
        }

        // The day after: inactive, no pill, not counted.
        await using (var factory = AppWith(today: new DateOnly(2026, 10, 22)))
        {
            var manager = await SignInAsync(factory, AppRoles.Manager);
            Assert.DoesNotContain("Future Leaver", await manager.GetStringAsync("/people"));
            var inactive = await manager.GetStringAsync("/people?status=Inactive");
            Assert.Contains("Future Leaver", inactive);
            Assert.Contains("data-testid=\"status-inactive\"", inactive);
            Assert.Matches("data-testid=\"active-people-tile\"[\\s\\S]*?stat-value\">1<", await manager.GetStringAsync("/"));
            var details = await manager.GetStringAsync($"/people/{leaver}");
            Assert.DoesNotContain("data-testid=\"cancel-leaving\"", details);
            Assert.Contains("data-bs-target=\"#reactivateModal\"", details);

            // The absences status filter follows the same rule.
            Assert.Contains("value=\"Active\" selected", await manager.GetStringAsync("/absences"));
        }
    }

    [Fact]
    public async Task Cancel_leaving_reopens_the_same_employment_period()
    {
        var id = await App.CreatePersonAsync("Changed Mind", joined: new DateOnly(2025, 1, 6), left: new DateOnly(2026, 10, 21));
        await using var factory = AppWith(today: new DateOnly(2026, 10, 9));
        var manager = await SignInAsync(factory, AppRoles.Manager);

        int periodId;
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            periodId = (await db.EmploymentPeriods.SingleAsync(e => e.PersonId == id)).Id;
        }

        var response = await manager.PostFormAsync($"/people/{id}/cancel-leaving", $"/people/{id}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("Changed Mind is no longer leaving", WebUtility.HtmlDecode(await manager.FollowAsync(response)));

        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            var period = await db.EmploymentPeriods.SingleAsync(e => e.PersonId == id); // still exactly one period
            Assert.Equal((periodId, (DateOnly?)null), (period.Id, period.EndDate));
            Assert.Null((await db.People.SingleAsync(p => p.Id == id)).LeavingDate);
        }

        Assert.Contains(App.Logs.Entries, e => e.EventId.Id == 1105 && e.Values.Contains($"PersonId={id}"));

        // GET does nothing; a POST without a token is rejected.
        Assert.True((await manager.GetAsync($"/people/{id}/cancel-leaving")).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsync($"/people/{id}/cancel-leaving", new FormUrlEncodedContent([]))).StatusCode);
    }

    [Fact]
    public async Task Cancel_leaving_is_refused_after_the_date_or_when_a_finalized_period_would_change()
    {
        var passed = await App.CreatePersonAsync("Already Gone", joined: new DateOnly(2025, 1, 6), left: new DateOnly(2026, 10, 2));
        var locked = await App.CreatePersonAsync("Locked Leaver", joined: new DateOnly(2025, 1, 6), left: new DateOnly(2026, 10, 12));

        await using var factory = AppWith(today: new DateOnly(2026, 10, 9), lockedBefore: new DateOnly(2026, 10, 16));
        var admin = await SignInAsync(factory, AppRoles.Admin);

        var tooLate = await admin.PostFormAsync($"/people/{passed}/cancel-leaving", $"/people/{passed}");
        Assert.Contains("The leaving date has already passed.", await admin.FollowAsync(tooLate));

        var refused = await admin.PostFormAsync($"/people/{locked}/cancel-leaving", $"/people/{locked}");
        Assert.Contains("This change would alter the finalized payroll for 01 Oct–15 Oct 2026.", WebUtility.HtmlDecode(await admin.FollowAsync(refused)));

        await using var db = TestDatabaseFixture.CreateDbContext();
        Assert.Equal(new DateOnly(2026, 10, 12), (await db.People.SingleAsync(p => p.Id == locked)).LeavingDate);
    }

    [Fact]
    public async Task The_Owner_rule_counts_people_until_their_leaving_date_has_passed()
    {
        var today = PeopleHelpers.Today;
        var leavingOwner = await App.CreatePersonAsync("Leaving Owner", joined: new DateOnly(2025, 1, 6), source: HireSource.Owner);
        var successor = await App.CreatePersonAsync("Next Owner", joined: new DateOnly(2025, 1, 6));

        await using var scope = App.Services.CreateAsyncScope();
        var people = scope.ServiceProvider.GetRequiredService<PersonService>();

        // A future leaving date: the first Owner is still active, so a second can't be set.
        Assert.True((await people.DeactivateAsync(leavingOwner, today.AddDays(10), "t")).Succeeded);
        var blocked = await people.SetHireSourceAsync(successor, HireSource.Owner, "t");
        Assert.Contains("is already the active Owner", blocked.Errors![0].Message);

        // Once the leaving date has passed (moved into the past here), the successor may become the Owner.
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            await db.EmploymentPeriods.Where(e => e.PersonId == leavingOwner).ExecuteUpdateAsync(s => s.SetProperty(e => e.EndDate, today.AddDays(-1)));
            await db.People.Where(p => p.Id == leavingOwner).ExecuteUpdateAsync(s => s.SetProperty(p => p.LeavingDate, today.AddDays(-1)));
        }

        Assert.True((await people.SetHireSourceAsync(successor, HireSource.Owner, "t")).Succeeded);

        // The database trigger enforces the same rule as a safety net.
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
                db.People.Where(p => p.Id == leavingOwner).ExecuteUpdateAsync(s => s.SetProperty(p => p.LeavingDate, (DateOnly?)null)));
            Assert.Equal(PersonService.ActiveOwnerErrorNumber, ex.Number);
        }
    }
}
