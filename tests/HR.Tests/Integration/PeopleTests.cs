using System.Net;
using System.Text.RegularExpressions;
using HR.Domain.People;
using HR.Infrastructure.Identity;
using HR.Infrastructure.People;
using HR.Tests.Integration.Infrastructure;
using HR.Web.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HR.Tests.Integration;

public partial class PeopleTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    private HrWebApplicationFactory App => Fixture.Development;

    // ---------- Access ----------

    [Fact]
    public async Task Anonymous_visitors_are_sent_to_login()
    {
        var id = await App.CreatePersonAsync("Anon Target");
        using var client = App.CreateHttpsClient();

        foreach (var url in new[] { "/people", "/people/create", $"/people/{id}", $"/people/{id}/edit" })
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/account/login", response.Headers.Location?.PathAndQueryOrOriginal());
        }
    }

    [Theory]
    [InlineData(AppRoles.Manager)]
    [InlineData(AppRoles.Admin)]
    public async Task Managers_and_Admins_can_list_create_edit_deactivate_and_reactivate(string role)
    {
        var (client, _) = await App.SignInAsAsync(role);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/people")).StatusCode);

        // Create (phone normalised on save).
        var created = await client.PostFormAsync("/people/create", "/people/create",
            PeopleHelpers.Form("Hamza Sheikh", designation: "DevOps Engineer", email: "Hamza@Example.com", phone: "0300-1234567", joined: "2025-03-03"));
        var id = PeopleHelpers.IdFromLocation(created);
        var details = await client.FollowAsync(created);
        Assert.Contains("Hamza Sheikh has been added.", details);
        Assert.Contains("92 300 1234567", details);

        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            var stored = await db.People.SingleAsync(p => p.Id == id);
            Assert.Equal("+923001234567", stored.Phone);
            Assert.Equal("hamza@example.com", stored.Email);
            Assert.Matches("^HR-\\d{4,}$", stored.Code);
            Assert.Null(stored.HireSource);
        }

        // Edit.
        var editPage = await client.GetStringAsync($"/people/{id}/edit");
        var form = PeopleHelpers.Form("Hamza Sheikh", designation: "Senior DevOps Engineer", phone: "+92 300 1234567", joined: "2025-03-03");
        form["RowVersion"] = PeopleHelpers.RowVersion(editPage);
        var edited = await client.PostFormAsync($"/people/{id}/edit", $"/people/{id}/edit", form);
        Assert.Contains("has been updated.", await client.FollowAsync(edited));

        // Deactivate, then reactivate.
        var deactivated = await client.PostFormAsync($"/people/{id}/deactivate", $"/people/{id}", new Dictionary<string, string> { ["leavingDate"] = "2026-06-30" });
        Assert.Contains("has been deactivated.", await client.FollowAsync(deactivated));
        var reactivated = await client.PostFormAsync($"/people/{id}/reactivate", $"/people/{id}", new Dictionary<string, string> { ["rejoiningDate"] = "2026-08-03" });
        Assert.Contains("has been reactivated.", await client.FollowAsync(reactivated));

        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            var stored = await db.People.SingleAsync(p => p.Id == id);
            Assert.Equal("Senior DevOps Engineer", stored.Designation);
            Assert.True(stored.IsActive);
            Assert.Null(stored.LeavingDate);
            Assert.Equal(new DateOnly(2026, 8, 3), stored.JoiningDate);
        }

        // The audit log records the previous dates.
        var audit = Assert.Single(App.Logs.Entries, e => e.EventId.Id == 1103 && e.Values.Contains($"PersonId={id}"));
        Assert.Contains($"PreviousJoiningDate={new DateOnly(2025, 3, 3)}", audit.Values);
        Assert.Contains($"PreviousLeavingDate={new DateOnly(2026, 6, 30)}", audit.Values);
    }

    // ---------- Codes ----------

    [Fact]
    public async Task Codes_are_sequential_unique_and_never_reused_after_a_failed_insert()
    {
        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);

        var first = PeopleHelpers.IdFromLocation(await client.PostFormAsync("/people/create", "/people/create", PeopleHelpers.Form("Code One", email: "code.one@example.test")));
        var second = PeopleHelpers.IdFromLocation(await client.PostFormAsync("/people/create", "/people/create", PeopleHelpers.Form("Code Two")));

        // A failed insert: the transaction takes a number and rolls back.
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.NextPersonCodeNumberAsync();
            await transaction.RollbackAsync();
        }

        // A rejected create (duplicate email) adds nothing.
        var duplicate = await client.PostFormAsync("/people/create", "/people/create", PeopleHelpers.Form("Code Dup", email: "code.one@example.test"));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);

        var third = PeopleHelpers.IdFromLocation(await client.PostFormAsync("/people/create", "/people/create", PeopleHelpers.Form("Code Three")));

        await using var check = TestDatabaseFixture.CreateDbContext();
        var people = await check.People.Where(p => p.Id == first || p.Id == second || p.Id == third).OrderBy(p => p.Id).ToListAsync();
        Assert.Equal(3, people.Count);
        Assert.Equal(people[0].CodeNumber + 1, people[1].CodeNumber);
        Assert.Equal(people[1].CodeNumber + 2, people[2].CodeNumber); // the rolled-back number is skipped, not reused
        Assert.All(people, p => Assert.Equal(PersonCode.Format(p.CodeNumber), p.Code));
        Assert.Equal(3, people.Select(p => p.Code).Distinct().Count());
    }

    // ---------- Validation ----------

    [Fact]
    public async Task Duplicate_email_and_cnic_get_friendly_errors()
    {
        await App.CreatePersonAsync("Existing", email: "taken@example.test", cnic: "11111-1111111-1");
        var otherId = await App.CreatePersonAsync("Other", email: "other@example.test", cnic: "22222-2222222-2");
        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);

        var created = await client.PostFormAsync("/people/create", "/people/create",
            PeopleHelpers.Form("Clash", email: "TAKEN@example.test", cnic: "1111111111111"));
        var html = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Contains(WebUtility.HtmlEncode(PersonService.DuplicateEmailMessage), html);
        Assert.Contains(WebUtility.HtmlEncode(PersonService.DuplicateCnicMessage), html);

        var editPage = await client.GetStringAsync($"/people/{otherId}/edit");
        var form = PeopleHelpers.Form("Other", email: "taken@example.test", cnic: "11111-1111111-1");
        form["RowVersion"] = PeopleHelpers.RowVersion(editPage);
        var edited = await client.PostFormAsync($"/people/{otherId}/edit", $"/people/{otherId}/edit", form);
        var editHtml = await edited.Content.ReadAsStringAsync();
        Assert.Contains(WebUtility.HtmlEncode(PersonService.DuplicateEmailMessage), editHtml);
        Assert.Contains(WebUtility.HtmlEncode(PersonService.DuplicateCnicMessage), editHtml);
    }

    [Fact]
    public async Task Invalid_fields_are_rejected_on_the_server()
    {
        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);

        var response = await client.PostFormAsync("/people/create", "/people/create",
            PeopleHelpers.Form("", designation: "", phone: "12345", cnic: "123", iban: "PK37SCBL0000001123456702"));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Enter the full name.", html);
        Assert.Contains("Enter the designation.", html);
        Assert.Contains("Enter a Pakistani phone number", html);
        Assert.Contains("Enter the CNIC as 12345-1234567-1", html);
        Assert.Contains("Enter a valid Pakistani IBAN", html);
        await using var db = TestDatabaseFixture.CreateDbContext();
        Assert.Equal(0, await db.People.CountAsync());
    }

    // ---------- Concurrency ----------

    [Fact]
    public async Task Second_edit_with_the_same_row_version_gets_the_conflict_message()
    {
        var id = await App.CreatePersonAsync("Mariam Javed", designation: "Product Designer");
        var (alice, _) = await App.SignInAsAsync(AppRoles.Manager);
        var (bob, _) = await App.SignInAsAsync(AppRoles.Admin);

        var rowVersion = PeopleHelpers.RowVersion(await alice.GetStringAsync($"/people/{id}/edit"));
        Assert.Equal(rowVersion, PeopleHelpers.RowVersion(await bob.GetStringAsync($"/people/{id}/edit")));

        var aliceForm = PeopleHelpers.Form("Mariam Javed", designation: "Lead Product Designer", joined: "2025-01-06");
        aliceForm["RowVersion"] = rowVersion;
        Assert.Equal(HttpStatusCode.Redirect, (await alice.PostFormAsync($"/people/{id}/edit", $"/people/{id}/edit", aliceForm)).StatusCode);

        var bobForm = PeopleHelpers.Form("Mariam Javed", designation: "Principal Designer", joined: "2025-01-06");
        bobForm["RowVersion"] = rowVersion;
        var conflict = await bob.PostFormAsync($"/people/{id}/edit", $"/people/{id}/edit", bobForm);
        var html = await conflict.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains(WebUtility.HtmlEncode(PeopleController.ConflictMessage), html);
        Assert.Contains("data-testid=\"conflict-changes\"", html);
        Assert.Contains("Lead Product Designer", html);   // what is saved now
        Assert.Contains("Principal Designer", html);      // what Bob entered
        Assert.NotEqual(rowVersion, PeopleHelpers.RowVersion(html)); // the form now carries the fresh version

        await using var db = TestDatabaseFixture.CreateDbContext();
        Assert.Equal("Lead Product Designer", (await db.People.SingleAsync(p => p.Id == id)).Designation);
    }

    // ---------- Hire source is Admin-only ----------

    private static readonly string[] HireSourceLeaks =
    [
        "Hire source", "hireSource", "CompanyRecommended", "Company recommended", "BudgetHire", "Budget hire", "Owner", "Not assigned",
        "pill-teal", "pill-violet",
    ];

    [Fact]
    public async Task Manager_responses_contain_no_hire_source_information()
    {
        await using (var scope = App.Services.CreateAsyncScope())
        {
            Assert.Equal(25, await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync());
        }

        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            // The demo data really does contain every source, and some without one.
            Assert.True(await db.People.AnyAsync(p => p.HireSource == HireSource.Owner));
            Assert.True(await db.People.AnyAsync(p => p.HireSource == HireSource.CompanyRecommended));
            Assert.True(await db.People.AnyAsync(p => p.HireSource == HireSource.BudgetHire));
            Assert.True(await db.People.AnyAsync(p => p.HireSource == null));
        }

        await using var check = TestDatabaseFixture.CreateDbContext();
        var ownerId = await check.People.Where(p => p.HireSource == HireSource.Owner).Select(p => p.Id).SingleAsync();
        var unassignedId = await check.People.Where(p => p.HireSource == null).Select(p => p.Id).FirstAsync();
        var inactiveId = await check.People.Where(p => !p.IsActive).Select(p => p.Id).FirstAsync();

        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        var urls = new[]
        {
            "/", "/people", "/people?status=All", "/people?status=Inactive", "/people?page=2&status=All",
            "/people?hireSource=Owner", "/people?hireSource=NotAssigned&status=All",
            $"/people/{ownerId}", $"/people/{unassignedId}", $"/people/{inactiveId}",
            "/people/create", $"/people/{ownerId}/edit", $"/people/{unassignedId}/edit",
        };

        foreach (var url in urls)
        {
            var response = await manager.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} returned {(int)response.StatusCode}");
            var html = await response.Content.ReadAsStringAsync();
            foreach (var leak in HireSourceLeaks)
            {
                Assert.False(html.Contains(leak, StringComparison.OrdinalIgnoreCase), $"{url} contains \"{leak}\"");
            }
        }

        // The Manager's hire-source query parameter is ignored: the active list is complete.
        Assert.Contains("21 people", await manager.GetStringAsync("/people?hireSource=Owner"));

        // The same pages do show it to an Admin.
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var adminList = await admin.GetStringAsync("/people?status=All");
        Assert.Contains("Hire source", adminList);
        Assert.Contains("Not assigned", adminList);
        Assert.Contains("data-testid=\"hire-source-card\"", await admin.GetStringAsync($"/people/{ownerId}"));
    }

    [Fact]
    public async Task Manager_post_to_hire_source_is_forbidden()
    {
        var id = await App.CreatePersonAsync("Target");
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);

        var response = await manager.PostFormAsync($"/people/{id}/hire-source", $"/people/{id}", new Dictionary<string, string> { ["hireSource"] = "Owner" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = TestDatabaseFixture.CreateDbContext();
        Assert.Null((await db.People.SingleAsync(p => p.Id == id)).HireSource);
    }

    [Fact]
    public async Task Manager_create_and_edit_ignore_a_posted_hire_source()
    {
        var existing = await App.CreatePersonAsync("Has Source", source: HireSource.BudgetHire);
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);

        var createForm = PeopleHelpers.Form("Sneaky Create");
        createForm["HireSource"] = "Owner";
        createForm["hireSource"] = "Owner";
        var created = PeopleHelpers.IdFromLocation(await manager.PostFormAsync("/people/create", "/people/create", createForm));

        var editForm = PeopleHelpers.Form("Has Source", joined: "2025-01-06");
        editForm["RowVersion"] = PeopleHelpers.RowVersion(await manager.GetStringAsync($"/people/{existing}/edit"));
        editForm["HireSource"] = "CompanyRecommended";
        Assert.Equal(HttpStatusCode.Redirect, (await manager.PostFormAsync($"/people/{existing}/edit", $"/people/{existing}/edit", editForm)).StatusCode);

        await using var db = TestDatabaseFixture.CreateDbContext();
        Assert.Null((await db.People.SingleAsync(p => p.Id == created)).HireSource);
        Assert.Equal(HireSource.BudgetHire, (await db.People.SingleAsync(p => p.Id == existing)).HireSource);
    }

    [Fact]
    public async Task Only_one_active_person_can_be_the_Owner()
    {
        var a = await App.CreatePersonAsync("Person A");
        var b = await App.CreatePersonAsync("Person B");
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        var setA = await admin.PostFormAsync($"/people/{a}/hire-source", $"/people/{a}", new Dictionary<string, string> { ["hireSource"] = "Owner" });
        Assert.Contains("Hire source set to Owner.", await admin.FollowAsync(setA));

        var setB = await admin.PostFormAsync($"/people/{b}/hire-source", $"/people/{b}", new Dictionary<string, string> { ["hireSource"] = "Owner" });
        Assert.Contains("is already the active Owner", await admin.FollowAsync(setB));

        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            Assert.Null((await db.People.SingleAsync(p => p.Id == b)).HireSource);
        }

        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostFormAsync($"/people/{a}/deactivate", $"/people/{a}", new Dictionary<string, string> { ["leavingDate"] = "2026-09-30" })).StatusCode);

        var retry = await admin.PostFormAsync($"/people/{b}/hire-source", $"/people/{b}", new Dictionary<string, string> { ["hireSource"] = "Owner" });
        Assert.Contains("Hire source set to Owner.", await admin.FollowAsync(retry));

        // Reactivating A would create a second active Owner: refused with a friendly message.
        var reactivate = await admin.PostFormAsync($"/people/{a}/reactivate", $"/people/{a}", new Dictionary<string, string> { ["rejoiningDate"] = "2026-10-05" });
        Assert.Contains("already has the Owner hire source", await admin.FollowAsync(reactivate));

        await using var check = TestDatabaseFixture.CreateDbContext();
        Assert.Equal(HireSource.Owner, (await check.People.SingleAsync(p => p.Id == b)).HireSource);
        Assert.False((await check.People.SingleAsync(p => p.Id == a)).IsActive);
        Assert.Equal(1, await check.People.CountAsync(p => p.IsActive && p.HireSource == HireSource.Owner));
    }

    [Fact]
    public async Task Admin_can_clear_a_hire_source_and_bad_values_are_refused()
    {
        var id = await App.CreatePersonAsync("Clearable", source: HireSource.CompanyRecommended);
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        var bad = await admin.PostFormAsync($"/people/{id}/hire-source", $"/people/{id}", new Dictionary<string, string> { ["hireSource"] = "3" });
        Assert.Contains("Choose a valid hire source.", await admin.FollowAsync(bad));

        var cleared = await admin.PostFormAsync($"/people/{id}/hire-source", $"/people/{id}", new Dictionary<string, string> { ["hireSource"] = "" });
        Assert.Contains("Hire source set to Not assigned.", await admin.FollowAsync(cleared));

        await using var db = TestDatabaseFixture.CreateDbContext();
        Assert.Null((await db.People.SingleAsync(p => p.Id == id)).HireSource);
    }

    // ---------- Deactivate / reactivate rules ----------

    [Fact]
    public async Task Deactivate_needs_a_leaving_date_on_or_after_joining_and_reactivate_a_date_after_leaving()
    {
        var id = await App.CreatePersonAsync("Dated", joined: new DateOnly(2026, 3, 2));
        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);

        var tooEarly = await client.PostFormAsync($"/people/{id}/deactivate", $"/people/{id}", new Dictionary<string, string> { ["leavingDate"] = "2026-03-01" });
        Assert.Contains("The leaving date can&#x27;t be before the joining date.", await client.FollowAsync(tooEarly));

        var missing = await client.PostFormAsync($"/people/{id}/deactivate", $"/people/{id}", new Dictionary<string, string>());
        Assert.Contains("Enter the leaving date.", await client.FollowAsync(missing));

        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            Assert.True((await db.People.SingleAsync(p => p.Id == id)).IsActive);
        }

        var ok = await client.PostFormAsync($"/people/{id}/deactivate", $"/people/{id}", new Dictionary<string, string> { ["leavingDate"] = "2026-03-02" });
        Assert.Contains("has been deactivated.", await client.FollowAsync(ok));

        var sameDay = await client.PostFormAsync($"/people/{id}/reactivate", $"/people/{id}", new Dictionary<string, string> { ["rejoiningDate"] = "2026-03-02" });
        Assert.Contains("The rejoining date must be after the previous leaving date.", await client.FollowAsync(sameDay));

        await using var check = TestDatabaseFixture.CreateDbContext();
        var person = await check.People.SingleAsync(p => p.Id == id);
        Assert.False(person.IsActive);
        Assert.Equal(new DateOnly(2026, 3, 2), person.LeavingDate);
    }

    [Fact]
    public async Task Mutating_actions_are_POST_only_and_need_antiforgery()
    {
        var id = await App.CreatePersonAsync("Guarded");
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        foreach (var url in new[] { $"/people/{id}/deactivate", $"/people/{id}/reactivate", $"/people/{id}/hire-source" })
        {
            var get = await admin.GetAsync(url);
            Assert.True(get.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"GET {url} returned {(int)get.StatusCode}");
        }

        var posts = new (string Url, Dictionary<string, string> Fields)[]
        {
            ("/people/create", PeopleHelpers.Form("No Token")),
            ($"/people/{id}/edit", PeopleHelpers.Form("No Token")),
            ($"/people/{id}/deactivate", new() { ["leavingDate"] = "2026-09-30" }),
            ($"/people/{id}/reactivate", new() { ["rejoiningDate"] = "2026-10-30" }),
            ($"/people/{id}/hire-source", new() { ["hireSource"] = "Owner" }),
        };
        foreach (var (url, fields) in posts)
        {
            var response = await admin.PostAsync(url, new FormUrlEncodedContent(fields));
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"POST {url} without a token returned {(int)response.StatusCode}");
        }

        await using var db = TestDatabaseFixture.CreateDbContext();
        var person = await db.People.SingleAsync(p => p.Id == id);
        Assert.True(person.IsActive);
        Assert.Null(person.HireSource);
        Assert.Equal("Guarded", person.FullName);
        Assert.Equal(1, await db.People.CountAsync());
    }

    // ---------- List ----------

    [Fact]
    public async Task Search_filters_sorting_and_paging_return_the_right_rows()
    {
        await App.CreatePersonAsync("Zara Khan", PersonType.Internee, email: "zara@example.test", phone: "0311-7654321", joined: new DateOnly(2026, 2, 2));
        await App.CreatePersonAsync("Adeel Shah", PersonType.Employee, joined: new DateOnly(2024, 1, 8));
        await App.CreatePersonAsync("Gone Person", PersonType.Employee, joined: new DateOnly(2024, 5, 6), left: new DateOnly(2025, 5, 30));
        for (var i = 1; i <= 22; i++)
        {
            await App.CreatePersonAsync($"Filler {i:00}", joined: new DateOnly(2025, 1, 6));
        }

        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);

        // Default: active only, by name, 20 per page.
        var page1 = await client.GetStringAsync("/people");
        Assert.Contains("24 people", page1);
        Assert.Equal(20, RowCount(page1));
        Assert.DoesNotContain("Gone Person", page1);
        Assert.Equal("Adeel Shah", Names(page1)[0]);
        Assert.Equal(4, RowCount(await client.GetStringAsync("/people?page=2")));

        Assert.Single(Names(await client.GetStringAsync("/people?q=zara%40example")));
        Assert.Equal(["Zara Khan"], Names(await client.GetStringAsync("/people?q=0311-7654321")));
        Assert.Equal(["Zara Khan"], Names(await client.GetStringAsync("/people?q=%2B92%20311%207654321")));
        Assert.Equal(["Zara Khan"], Names(await client.GetStringAsync("/people?q=Zara")));

        var zaraCode = await CodeOf("Zara Khan");
        Assert.Equal(["Zara Khan"], Names(await client.GetStringAsync($"/people?q={zaraCode}")));

        Assert.Equal(["Zara Khan"], Names(await client.GetStringAsync("/people?type=Internee")));
        Assert.Equal(["Gone Person"], Names(await client.GetStringAsync("/people?status=Inactive")));
        Assert.Contains("25 people", await client.GetStringAsync("/people?status=All"));

        Assert.Equal("Zara Khan", Names(await client.GetStringAsync("/people?sort=name_desc"))[0]);
        Assert.Equal("Zara Khan", Names(await client.GetStringAsync("/people?sort=joined_desc"))[0]);
        Assert.Equal("Adeel Shah", Names(await client.GetStringAsync("/people?sort=joined"))[0]);
        Assert.Equal("Zara Khan", Names(await client.GetStringAsync("/people?sort=code"))[0]);
        Assert.Equal("Filler 22", Names(await client.GetStringAsync("/people?sort=code_desc"))[0]);

        // Paging keeps the filters.
        Assert.Contains("href=\"/people?page=2&amp;type=Employee&amp;sort=code\"", await client.GetStringAsync("/people?type=Employee&sort=code"));

        Assert.Contains("No people match these filters.", await client.GetStringAsync("/people?q=nobody-matches"));
    }

    [Fact]
    public async Task Admin_can_filter_by_hire_source_including_not_assigned()
    {
        await App.CreatePersonAsync("Recommended One", source: HireSource.CompanyRecommended);
        await App.CreatePersonAsync("Budget One", source: HireSource.BudgetHire);
        await App.CreatePersonAsync("Unassigned One");
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        Assert.Equal(["Unassigned One"], Names(await admin.GetStringAsync("/people?hireSource=NotAssigned")));
        Assert.Equal(["Budget One"], Names(await admin.GetStringAsync("/people?hireSource=BudgetHire")));
        Assert.Equal(["Recommended One"], Names(await admin.GetStringAsync("/people?hireSource=CompanyRecommended")));
        var all = await admin.GetStringAsync("/people");
        Assert.Contains("pill pill-warning\">Not assigned<", all);
    }

    [Fact]
    public async Task Empty_list_shows_the_empty_state()
    {
        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);

        Assert.Contains("No people yet.", await client.GetStringAsync("/people"));
    }

    // ---------- Masking and logs ----------

    [Fact]
    public async Task Cnic_and_iban_are_masked_outside_the_details_page()
    {
        const string cnic = "35202-7654321-9";
        const string iban = "PK36SCBL0000001123456702";
        var id = await App.CreatePersonAsync("Masked Person", cnic: cnic, iban: iban);
        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);

        var list = await client.GetStringAsync("/people");
        var edit = await client.GetStringAsync($"/people/{id}/edit");
        var details = await client.GetStringAsync($"/people/{id}");

        foreach (var html in new[] { list, edit })
        {
            Assert.DoesNotContain(cnic, html);
            Assert.DoesNotContain("3520276543219", html);
            Assert.DoesNotContain(iban, html);
            Assert.DoesNotContain("PK36 SCBL 0000 0011 2345 6702", html);
        }

        Assert.Contains("data-testid=\"masked-cnic\">•••••-••••321-9<", edit);
        Assert.Contains("data-testid=\"masked-iban\">•••• •••• •••• •••• •••• 6702<", edit);
        Assert.Contains($"data-testid=\"cnic-full\">{cnic}<", details);
        Assert.Contains("data-testid=\"iban-full\">PK36 SCBL 0000 0011 2345 6702<", details);

        // Saving the edit form untouched keeps both values.
        var form = PeopleHelpers.Form("Masked Person", joined: "2025-01-06");
        form["RowVersion"] = PeopleHelpers.RowVersion(edit);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostFormAsync($"/people/{id}/edit", $"/people/{id}/edit", form)).StatusCode);
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            var stored = await db.People.SingleAsync(p => p.Id == id);
            Assert.Equal(cnic, stored.Cnic);
            Assert.Equal(iban, stored.Iban);
        }

        // The remove boxes clear them.
        var removeForm = PeopleHelpers.Form("Masked Person", joined: "2025-01-06");
        removeForm["RowVersion"] = PeopleHelpers.RowVersion(await client.GetStringAsync($"/people/{id}/edit"));
        removeForm["RemoveCnic"] = "true";
        removeForm["RemoveIban"] = "true";
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostFormAsync($"/people/{id}/edit", $"/people/{id}/edit", removeForm)).StatusCode);
        await using var check = TestDatabaseFixture.CreateDbContext();
        var cleared = await check.People.SingleAsync(p => p.Id == id);
        Assert.Null(cleared.Cnic);
        Assert.Null(cleared.Iban);
    }

    [Fact]
    public async Task Logs_never_contain_cnic_iban_or_phone()
    {
        const string cnic = "61101-1234987-5";
        const string iban = "PK36SCBL0000001123456702";
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        var created = await admin.PostFormAsync("/people/create", "/people/create",
            PeopleHelpers.Form("Logged Person", phone: "0333-9876543", cnic: cnic, iban: iban));
        var id = PeopleHelpers.IdFromLocation(created);
        var form = PeopleHelpers.Form("Logged Person", phone: "0333-9876543", cnic: "61101-1234987-6", joined: "2026-01-05");
        form["RowVersion"] = PeopleHelpers.RowVersion(await admin.GetStringAsync($"/people/{id}/edit"));
        await admin.PostFormAsync($"/people/{id}/edit", $"/people/{id}/edit", form);
        await admin.PostFormAsync($"/people/{id}/hire-source", $"/people/{id}", new Dictionary<string, string> { ["hireSource"] = "BudgetHire" });
        await admin.PostFormAsync($"/people/{id}/deactivate", $"/people/{id}", new Dictionary<string, string> { ["leavingDate"] = "2026-09-30" });
        await admin.PostFormAsync($"/people/{id}/reactivate", $"/people/{id}", new Dictionary<string, string> { ["rejoiningDate"] = "2026-10-05" });

        Assert.Contains(App.Logs.Entries, e => e.EventId.Id == 1100 && e.AllText.Contains($"PersonId={id}", StringComparison.Ordinal));
        Assert.Contains(App.Logs.Entries, e => e.EventId.Id == 1104 && e.AllText.Contains($"PersonId={id}", StringComparison.Ordinal));
        Assert.Contains(App.Logs.Entries, e => e.EventId.Id == 1103 && e.AllText.Contains($"PersonId={id}", StringComparison.Ordinal));

        var secrets = new[] { cnic, "6110112349875", "61101-1234987-6", iban, "+923339876543", "0333-9876543", "3339876543" };
        foreach (var entry in App.Logs.Entries)
        {
            foreach (var secret in secrets)
            {
                Assert.False(entry.AllText.Contains(secret, StringComparison.Ordinal), $"Log entry {entry.EventId} ({entry.Category}) contains a personal identifier.");
            }
        }
    }

    // ---------- Dashboard ----------

    [Fact]
    public async Task Dashboard_counts_active_people_and_shows_the_not_assigned_tile_only_to_Admins()
    {
        await App.CreatePersonAsync("E1", PersonType.Employee, source: HireSource.BudgetHire);
        await App.CreatePersonAsync("E2", PersonType.Employee);
        await App.CreatePersonAsync("I1", PersonType.Internee);
        await App.CreatePersonAsync("Gone", PersonType.Employee, left: new DateOnly(2026, 5, 29));

        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var adminHtml = await admin.GetStringAsync("/");
        Assert.Matches("data-testid=\"active-people-tile\"[\\s\\S]*?stat-value\">3<[\\s\\S]*?2 employees · 1 internee", adminHtml);
        Assert.Matches("data-testid=\"hire-source-tile\"[\\s\\S]*?stat-value\">2<", adminHtml);
        Assert.Contains("href=\"/people?hireSource=NotAssigned\"", adminHtml);

        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        var managerHtml = await manager.GetStringAsync("/");
        Assert.Matches("data-testid=\"active-people-tile\"[\\s\\S]*?stat-value\">3<", managerHtml);
        Assert.DoesNotContain("hire-source-tile", managerHtml);
        Assert.DoesNotContain("not assigned", managerHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sidebar_shows_Owner_Income_and_Invoices_only_to_Admins()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);

        var adminHtml = await admin.GetStringAsync("/");
        Assert.Contains("Owner Income (coming soon)", adminHtml);
        Assert.Contains("Invoices (coming soon)", adminHtml);
        Assert.Contains("href=\"/people\"", adminHtml);

        var managerHtml = await manager.GetStringAsync("/");
        Assert.DoesNotContain("Owner Income", managerHtml);
        Assert.DoesNotContain("Invoices", managerHtml);
        Assert.Contains("href=\"/people\"", managerHtml);
    }

    // ---------- Demo data ----------

    [Fact]
    public async Task Demo_seeder_is_idempotent_and_does_nothing_outside_Development()
    {
        await using (var scope = Fixture.Production.Services.CreateAsyncScope())
        {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync());
        }

        await using (var scope = App.Services.CreateAsyncScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DemoDataSeeder>();
            Assert.Equal(25, await seeder.SeedAsync());
            Assert.Equal(0, await seeder.SeedAsync());
        }

        await using var db = TestDatabaseFixture.CreateDbContext();
        var people = await db.People.ToListAsync();
        Assert.Equal(25, people.Count);
        Assert.Contains(people, p => p.Type == PersonType.Internee);
        Assert.Contains(people, p => !p.IsActive && p.LeavingDate is not null);
        Assert.All(people, p => Assert.StartsWith("00000-", p.Cnic));
        Assert.All(people, p => Assert.Equal("TEST", p.Iban![4..8]));
        Assert.All(people, p => Assert.EndsWith(DemoDataSeeder.EmailDomain, p.Email));
        Assert.Equal(1, people.Count(p => p.IsActive && p.HireSource == HireSource.Owner));
    }

    private static int RowCount(string html) => RowRegex().Matches(html).Count;

    private static string[] Names(string html) => NameRegex().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToArray();

    private static async Task<string> CodeOf(string name)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        return await db.People.Where(p => p.FullName == name).Select(p => p.Code).SingleAsync();
    }

    [GeneratedRegex("data-testid=\"person-row\"")]
    private static partial Regex RowRegex();

    [GeneratedRegex("data-testid=\"person-name\"[^>]*>([^<]+)<")]
    private static partial Regex NameRegex();
}
