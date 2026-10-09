using System.Net;
using System.Text.RegularExpressions;
using HR.Infrastructure.Identity;
using HR.Infrastructure.People;
using HR.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace HR.Tests.Integration;

public partial class EmploymentPeriodTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    private HrWebApplicationFactory App => Fixture.Development;

    [Fact]
    public async Task Backfill_gives_every_existing_person_exactly_one_matching_period()
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
        var addPeople = (await db.Database.GetAppliedMigrationsAsync()).Single(m => m.EndsWith("_AddPeople", StringComparison.Ordinal));

        try
        {
            // Back to the schema before employment periods existed, with people stored the M3 way.
            await migrator.MigrateAsync(addPeople);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO [People] ([CodeNumber], [Code], [FullName], [Type], [Designation], [Phone], [JoiningDate], [LeavingDate], [IsActive],
                                      [CreatedAt], [CreatedByUserId], [UpdatedAt], [UpdatedByUserId])
                VALUES (900001, 'HR-900001', 'Backfill Active', 'Employee', 'Engineer', '+923451111111', '2025-02-03', NULL, 1, SYSDATETIMEOFFSET(), 'm3', SYSDATETIMEOFFSET(), 'm3'),
                       (900002, 'HR-900002', 'Backfill Left',   'Internee', 'Intern',   '+923452222222', '2024-07-01', '2025-06-30', 0, SYSDATETIMEOFFSET(), 'm3', SYSDATETIMEOFFSET(), 'm3'),
                       (900003, 'HR-900003', 'Backfill Rejoined', 'Employee', 'Analyst', '+923453333333', '2026-08-03', NULL, 1, SYSDATETIMEOFFSET(), 'm3', SYSDATETIMEOFFSET(), 'm3');
                """);

            await migrator.MigrateAsync(); // AddEmploymentPeriods (with its backfill) and everything after it
        }
        finally
        {
            await migrator.MigrateAsync(); // never leave the shared test database half-migrated
        }

        var people = await db.People.AsNoTracking().Include(p => p.EmploymentPeriods).Where(p => p.CodeNumber >= 900001).ToListAsync();
        Assert.Equal(3, people.Count);
        foreach (var person in people)
        {
            var period = Assert.Single(person.EmploymentPeriods);
            Assert.Equal(person.JoiningDate, period.StartDate);
            Assert.Equal(person.LeavingDate, period.EndDate);
        }

        var leftId = people.Single(x => x.FullName == "Backfill Left").Id;
        Assert.Equal(1, await db.EmploymentPeriods.CountAsync(p => p.EndDate != null && p.PersonId == leftId));
    }

    [Fact]
    public async Task Deactivate_then_reactivate_shows_two_periods_and_caches_the_latest()
    {
        var id = await App.CreatePersonAsync("Two Stints", joined: new DateOnly(2026, 1, 5));
        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);

        await client.PostFormAsync($"/people/{id}/deactivate", $"/people/{id}", new Dictionary<string, string> { ["leavingDate"] = "2026-03-31" });
        await client.PostFormAsync($"/people/{id}/reactivate", $"/people/{id}", new Dictionary<string, string> { ["rejoiningDate"] = "2026-06-01" });

        var html = await client.GetStringAsync($"/people/{id}");
        Assert.Contains("data-testid=\"employment-history\"", html);
        Assert.Equal(2, PeriodRegex().Matches(html).Count);
        Assert.Contains("01 Jun 2026", html);
        Assert.Contains("05 Jan 2026", html);
        Assert.Contains("31 Mar 2026", html);
        Assert.Contains("pill pill-success\">Current<", html);
        Assert.Contains("62 working days<", html); // 05 Jan – 31 Mar 2026

        await using var db = TestDatabaseFixture.CreateDbContext();
        var person = await db.People.Include(p => p.EmploymentPeriods).SingleAsync(p => p.Id == id);
        var latest = person.EmploymentPeriods.MaxBy(p => p.StartDate)!;
        Assert.Equal(2, person.EmploymentPeriods.Count);
        Assert.Equal(latest.StartDate, person.JoiningDate);
        Assert.Equal(latest.EndDate, person.LeavingDate);
        Assert.True(person.IsActive);

        // Deactivating again closes the new period and updates the cache.
        await client.PostFormAsync($"/people/{id}/deactivate", $"/people/{id}", new Dictionary<string, string> { ["leavingDate"] = "2026-09-30" });
        await using var check = TestDatabaseFixture.CreateDbContext();
        var after = await check.People.Include(p => p.EmploymentPeriods).SingleAsync(p => p.Id == id);
        Assert.All(after.EmploymentPeriods, p => Assert.NotNull(p.EndDate));
        Assert.Equal(new DateOnly(2026, 9, 30), after.LeavingDate);
        Assert.Equal(new DateOnly(2026, 6, 1), after.JoiningDate);
    }

    [Fact]
    public async Task Editing_the_joining_date_before_the_previous_period_end_gives_a_friendly_error()
    {
        var id = await App.CreatePersonAsync("Moved Start", joined: new DateOnly(2026, 1, 5));
        var (client, _) = await App.SignInAsAsync(AppRoles.Admin);
        await client.PostFormAsync($"/people/{id}/deactivate", $"/people/{id}", new Dictionary<string, string> { ["leavingDate"] = "2026-03-31" });
        await client.PostFormAsync($"/people/{id}/reactivate", $"/people/{id}", new Dictionary<string, string> { ["rejoiningDate"] = "2026-06-01" });

        var form = PeopleHelpers.Form("Moved Start", joined: "2026-03-15");
        form["RowVersion"] = PeopleHelpers.RowVersion(await client.GetStringAsync($"/people/{id}/edit"));
        var response = await client.PostFormAsync($"/people/{id}/edit", $"/people/{id}/edit", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The joining date must be after the previous employment period, which ended on 31 Mar 2026.", await response.Content.ReadAsStringAsync());

        // A valid move is applied to the current period and the cache.
        form["JoiningDate"] = "2026-04-06";
        form["RowVersion"] = PeopleHelpers.RowVersion(await client.GetStringAsync($"/people/{id}/edit"));
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostFormAsync($"/people/{id}/edit", $"/people/{id}/edit", form)).StatusCode);

        await using var db = TestDatabaseFixture.CreateDbContext();
        var person = await db.People.Include(p => p.EmploymentPeriods).SingleAsync(p => p.Id == id);
        Assert.Equal(new DateOnly(2026, 4, 6), person.JoiningDate);
        Assert.Equal(new DateOnly(2026, 4, 6), person.EmploymentPeriods.Single(p => p.EndDate == null).StartDate);
        Assert.Equal(new DateOnly(2026, 1, 5), person.EmploymentPeriods.Single(p => p.EndDate != null).StartDate);
    }

    [Fact]
    public async Task The_database_rejects_overlapping_or_second_open_periods()
    {
        var id = await App.CreatePersonAsync("Guarded Periods", joined: new DateOnly(2026, 1, 5));
        await using var db = TestDatabaseFixture.CreateDbContext();

        // Overlaps the open period (trigger).
        var overlap = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO [EmploymentPeriods] ([PersonId],[StartDate],[EndDate],[CreatedAt],[CreatedByUserId],[UpdatedAt],[UpdatedByUserId]) VALUES ({id}, '2025-06-01', '2026-02-01', SYSDATETIMEOFFSET(), 't', SYSDATETIMEOFFSET(), 't')"));
        Assert.Equal(PersonService.OverlapErrorNumber, overlap.Number);

        // A second open period (filtered unique index).
        var secondOpen = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO [EmploymentPeriods] ([PersonId],[StartDate],[EndDate],[CreatedAt],[CreatedByUserId],[UpdatedAt],[UpdatedByUserId]) VALUES ({id}, '2027-01-04', NULL, SYSDATETIMEOFFSET(), 't', SYSDATETIMEOFFSET(), 't')"));
        Assert.Contains(secondOpen.Number, new[] { 2601, 2627, PersonService.OverlapErrorNumber });

        // End before start (check constraint).
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO [EmploymentPeriods] ([PersonId],[StartDate],[EndDate],[CreatedAt],[CreatedByUserId],[UpdatedAt],[UpdatedByUserId]) VALUES ({id}, '2024-05-01', '2024-04-01', SYSDATETIMEOFFSET(), 't', SYSDATETIMEOFFSET(), 't')"));

        Assert.Equal(1, await db.EmploymentPeriods.CountAsync(p => p.PersonId == id));
    }

    [GeneratedRegex("data-testid=\"employment-period\"")]
    private static partial Regex PeriodRegex();
}
