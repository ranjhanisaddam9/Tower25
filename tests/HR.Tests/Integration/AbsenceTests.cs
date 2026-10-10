using System.Net;
using System.Text.RegularExpressions;
using HR.Domain.Absences;
using HR.Domain.Pay;
using HR.Domain.People;
using HR.Domain.Time;
using HR.Infrastructure.Absences;
using HR.Infrastructure.Identity;
using HR.Infrastructure.People;
using HR.Infrastructure.Security;
using HR.Tests.Integration.Infrastructure;
using HR.Web.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HR.Tests.Integration;

public partial class AbsenceTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    /// <summary>Friday 09 Oct 2026, in the Oct 1–15 period.</summary>
    private static readonly DateOnly Today = new(2026, 10, 9);

    private HrWebApplicationFactory App => Fixture.Development;

    private static DateOnly Oct(int day) => new(2026, 10, day);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Encoded(string text) => WebUtility.HtmlEncode(text).Replace("&#39;", "&#x27;", StringComparison.Ordinal);

    // ---------- A test app with a fixed "today" and, optionally, a payroll lock ----------

    private sealed class FixedClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new DateTimeOffset(today.ToDateTime(new TimeOnly(5, 0)), TimeSpan.Zero); // 10:00 in Karachi

        public DateOnly Today { get; } = today;
    }

    private sealed class FakePayrollLock(DateOnly lockedBefore) : IPayrollLock
    {
        public Task<bool> IsLockedAsync(DateOnly periodStart, CancellationToken cancellationToken = default) => Task.FromResult(periodStart < lockedBefore);

        public Task<DateOnly?> LatestLockedPeriodStartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DateOnly?>(HR.Domain.Payroll.PayPeriod.For(lockedBefore.AddDays(-1)).Start);
    }

    private sealed class TestApp(WebApplicationFactory<Program> factory) : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory { get; } = factory;

        public async Task<HttpClient> SignInAsync(HrWebApplicationFactory shared, string role)
        {
            var user = await shared.CreateUserAsync(role);
            var client = Factory.CreateHttpsClient();
            Assert.Equal(HttpStatusCode.Redirect, (await client.PostLoginAsync(user.Email, user.Password)).StatusCode);
            return client;
        }

        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }

    private TestApp AppAt(DateOnly today, DateOnly? lockedBefore = null) =>
        new(App.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(new FixedClock(today));
            if (lockedBefore is { } locked)
            {
                services.RemoveAll<IPayrollLock>();
                services.AddSingleton<IPayrollLock>(new FakePayrollLock(locked));
            }
        })));

    private static async Task<int> AddAbsenceAsync(int personId, DateOnly date, AbsencePortion portion = AbsencePortion.Full, string? note = null)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        var absence = Absence.Create(personId, date, portion, note, "test-setup", DateTimeOffset.UtcNow);
        db.Absences.Add(absence);
        await db.SaveChangesAsync();
        return absence.Id;
    }

    private static async Task<List<Absence>> AbsencesAsync(int personId)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        return await db.Absences.AsNoTracking().Where(a => a.PersonId == personId).OrderBy(a => a.Date).ToListAsync();
    }

    private static Dictionary<string, string> NewForm(int personId, DateOnly date, string portion = "Full", string? note = null)
    {
        var form = new Dictionary<string, string> { ["PersonId"] = personId.ToString(CultureInfo.InvariantCulture), ["Date"] = Iso(date), ["Portion"] = portion };
        if (note is not null) form["Note"] = note;
        return form;
    }

    private static string? Toast(string html) => ToastRegex().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;

    // ---------- CRUD and roles ----------

    [Theory]
    [InlineData(AppRoles.Admin)]
    [InlineData(AppRoles.Manager)]
    public async Task Both_roles_can_add_edit_and_delete(string role)
    {
        var id = await App.CreatePersonAsync("Crud " + role, joined: new DateOnly(2026, 9, 1));
        await using var app = AppAt(Today);
        var client = await app.SignInAsync(App, role);

        var add = await client.PostFormAsync("/absences/new", "/absences/new", NewForm(id, Oct(7), note: "Dentist"));
        Assert.Equal(HttpStatusCode.Redirect, add.StatusCode);
        Assert.Equal("/absences?period=2026-10-01", add.Headers.Location?.PathAndQueryOrOriginal());
        var absence = Assert.Single(await AbsencesAsync(id));
        Assert.Equal((Oct(7), AbsencePortion.Full, "Dentist"), (absence.Date, absence.Portion, absence.Note));

        var editUrl = $"/absences/{absence.Id}/edit";
        var form = NewForm(id + 999, Oct(8), "Half", "Half day"); // tampered person and date are ignored on edit
        form["RowVersion"] = PeopleHelpers.RowVersion(await client.GetStringAsync(editUrl));
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostFormAsync(editUrl, editUrl, form)).StatusCode);
        absence = Assert.Single(await AbsencesAsync(id));
        Assert.Equal((Oct(7), AbsencePortion.Half, "Half day"), (absence.Date, absence.Portion, absence.Note));

        Assert.Equal(HttpStatusCode.Redirect, (await client.PostFormAsync($"/absences/{absence.Id}/delete", editUrl)).StatusCode);
        Assert.Empty(await AbsencesAsync(id));
    }

    [Fact]
    public async Task Anonymous_users_are_sent_to_login()
    {
        using var anonymous = App.CreateHttpsClient();
        foreach (var url in new[] { "/absences", "/absences/day", "/absences/range", "/absences/new", "/absences/1/edit" })
        {
            var response = await anonymous.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/account/login", response.Headers.Location?.PathAndQueryOrOriginal(), StringComparison.Ordinal);
        }
    }

    // ---------- Validation ----------

    [Fact]
    public async Task Validation_refuses_weekends_dates_outside_employment_duplicates_locked_periods_and_far_future()
    {
        var id = await App.CreatePersonAsync("Rules Person", joined: new DateOnly(2026, 9, 1));
        var lockedId = await AddAbsenceAsync(id, new DateOnly(2026, 9, 15));
        await AddAbsenceAsync(id, Oct(6));
        await using var app = AppAt(Today, lockedBefore: Oct(1));
        var admin = await app.SignInAsync(App, AppRoles.Admin);

        async Task AssertRefused(DateOnly date, string message)
        {
            var response = await admin.PostFormAsync("/absences/new", "/absences/new", NewForm(id, date));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(Encoded(message), await response.Content.ReadAsStringAsync());
        }

        await AssertRefused(Oct(10), AbsenceRules.WeekendMessage); // Saturday
        await AssertRefused(new DateOnly(2026, 8, 31), AbsenceRules.LockedMessage); // a locked period wins over employment
        await AssertRefused(Oct(6), AbsenceRules.DuplicateMessage);
        await AssertRefused(new DateOnly(2027, 10, 11), AbsenceRules.TooFarAheadMessage);
        await AssertRefused(new DateOnly(2026, 9, 16), AbsenceRules.LockedMessage);

        // Outside employment, in an unlocked period.
        var leaver = await App.CreatePersonAsync("Early Leaver", joined: new DateOnly(2026, 9, 1), left: Oct(2));
        var outside = await admin.PostFormAsync("/absences/new", "/absences/new", NewForm(leaver, Oct(5)));
        Assert.Contains(Encoded(AbsenceRules.NotEmployedMessage), await outside.Content.ReadAsStringAsync());

        // Bad portion values.
        var badPortion = await admin.PostFormAsync("/absences/new", "/absences/new", NewForm(id, Oct(8), "1"));
        Assert.Contains("Choose full or half day.", await badPortion.Content.ReadAsStringAsync());

        // Edit and delete in a locked period are refused too.
        var editUrl = $"/absences/{lockedId}/edit";
        var editPage = await admin.GetStringAsync(editUrl);
        Assert.Contains("data-testid=\"absence-locked\"", editPage);
        var form = NewForm(id, new DateOnly(2026, 9, 15), "Half");
        form["RowVersion"] = await RowVersionAsync(lockedId);
        Assert.Contains(Encoded(AbsenceRules.LockedMessage), await admin.FollowAsync(await admin.PostFormAsync(editUrl, "/absences", form)));
        Assert.Contains(Encoded(AbsenceRules.LockedMessage), await admin.FollowAsync(await admin.PostFormAsync($"/absences/{lockedId}/delete", "/absences")));

        var stored = await AbsencesAsync(id);
        Assert.Equal([new DateOnly(2026, 9, 15), Oct(6)], stored.Select(a => a.Date).ToArray());
        Assert.Equal(AbsencePortion.Full, stored[0].Portion);
        Assert.Empty(await AbsencesAsync(leaver));
    }

    private static async Task<string> RowVersionAsync(int absenceId)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        return Convert.ToBase64String((await db.Absences.AsNoTracking().SingleAsync(a => a.Id == absenceId)).RowVersion);
    }

    [Fact]
    public async Task The_database_refuses_a_Saturday_absence()
    {
        var id = await App.CreatePersonAsync("Saturday Person", joined: new DateOnly(2026, 9, 1));
        await using var db = TestDatabaseFixture.CreateDbContext();
        var absence = Absence.Create(id, Oct(9), AbsencePortion.Full, null, "test", DateTimeOffset.UtcNow);
        db.Absences.Add(absence);
        db.Entry(absence).Property(a => a.Date).CurrentValue = Oct(10); // bypass the domain check

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("CK_Absences_Weekday", error.InnerException?.Message, StringComparison.Ordinal);

        // A Friday is accepted whatever DATEFIRST is.
        db.ChangeTracker.Clear();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET DATEFIRST 1");
        db.Absences.Add(Absence.Create(id, Oct(9), AbsencePortion.Full, null, "test", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        await db.Database.CloseConnectionAsync();
        Assert.Single(await AbsencesAsync(id));
    }

    // ---------- Daily attendance ----------

    [Fact]
    public async Task Daily_attendance_lists_only_employed_people_and_saves_changes_in_one_go()
    {
        var alpha = await App.CreatePersonAsync("Alpha Present", joined: new DateOnly(2026, 9, 1));
        var bravo = await App.CreatePersonAsync("Bravo Half", joined: new DateOnly(2026, 9, 1));
        var charlie = await App.CreatePersonAsync("Charlie Full", joined: new DateOnly(2026, 9, 1));
        var future = await App.CreatePersonAsync("Delta Future", joined: new DateOnly(2026, 11, 2));
        var gone = await App.CreatePersonAsync("Echo Gone", joined: new DateOnly(2026, 9, 1), left: Oct(1));
        await AddAbsenceAsync(bravo, Oct(7), AbsencePortion.Half);
        await AddAbsenceAsync(charlie, Oct(7), AbsencePortion.Full, "Flu");
        await using var app = AppAt(Today);
        var manager = await app.SignInAsync(App, AppRoles.Manager);

        var page = await manager.GetStringAsync("/absences/day?date=2026-10-07");
        Assert.Equal(3, AttendanceRowRegex().Matches(page).Count);
        foreach (var person in new[] { alpha, bravo, charlie })
        {
            Assert.Contains($"data-person-id=\"{person}\"", page);
        }

        Assert.DoesNotContain($"data-person-id=\"{future}\"", page);
        Assert.DoesNotContain($"data-person-id=\"{gone}\"", page);
        Assert.Matches("value=\"Half\" checked=\"checked\"|checked=\"checked\"[^>]*value=\"Half\"", page);

        // Alpha → half (add), Bravo → full (change), Charlie → present (remove).
        var form = new Dictionary<string, string>
        {
            ["Date"] = "2026-10-07",
            ["Rows[0].PersonId"] = alpha.ToString(CultureInfo.InvariantCulture), ["Rows[0].Status"] = "Half", ["Rows[0].Note"] = "Left early",
            ["Rows[1].PersonId"] = bravo.ToString(CultureInfo.InvariantCulture), ["Rows[1].Status"] = "Full",
            ["Rows[2].PersonId"] = charlie.ToString(CultureInfo.InvariantCulture), ["Rows[2].Status"] = "Present", ["Rows[2].Note"] = "ignored",
        };
        var save = await manager.PostFormAsync("/absences/day", "/absences/day?date=2026-10-07", form);
        Assert.Equal("Attendance for 07 Oct 2026 saved: 1 added, 1 changed, 1 removed.", Toast(await manager.FollowAsync(save)));

        Assert.Equal(AbsencePortion.Half, Assert.Single(await AbsencesAsync(alpha)).Portion);
        Assert.Equal("Left early", (await AbsencesAsync(alpha))[0].Note);
        Assert.Equal(AbsencePortion.Full, Assert.Single(await AbsencesAsync(bravo)).Portion);
        Assert.Empty(await AbsencesAsync(charlie));

        // Saving the same sheet again changes nothing.
        form["Rows[2].Note"] = string.Empty;
        var again = await manager.PostFormAsync("/absences/day", "/absences/day?date=2026-10-07", form);
        Assert.Equal("No changes for 07 Oct 2026.", Toast(await manager.FollowAsync(again)));

        // A person who wasn't employed that day rejects the whole save.
        form["Rows[0].Status"] = "Full";
        form["Rows[3].PersonId"] = future.ToString(CultureInfo.InvariantCulture);
        form["Rows[3].Status"] = "Full";
        var tampered = await manager.PostFormAsync("/absences/day", "/absences/day?date=2026-10-07", form);
        Assert.Equal(HttpStatusCode.OK, tampered.StatusCode);
        Assert.Contains("wasn&#x27;t employed on that date", await tampered.Content.ReadAsStringAsync());
        Assert.Equal(AbsencePortion.Half, Assert.Single(await AbsencesAsync(alpha)).Portion);
        Assert.Empty(await AbsencesAsync(future));
    }

    [Fact]
    public async Task Daily_attendance_refuses_weekends_and_is_read_only_on_locked_dates()
    {
        var id = await App.CreatePersonAsync("Weekend Person", joined: new DateOnly(2026, 9, 1));
        await AddAbsenceAsync(id, new DateOnly(2026, 9, 30));
        await using var app = AppAt(Today, lockedBefore: Oct(1));
        var admin = await app.SignInAsync(App, AppRoles.Admin);

        var weekend = await admin.GetStringAsync("/absences/day?date=2026-10-10");
        Assert.Contains("data-testid=\"day-weekend\"", weekend);
        Assert.DoesNotContain("data-testid=\"attendance-form\"", weekend);
        var weekendPost = await admin.PostFormAsync("/absences/day", "/absences/day", new Dictionary<string, string>
        {
            ["Date"] = "2026-10-10", ["Rows[0].PersonId"] = id.ToString(CultureInfo.InvariantCulture), ["Rows[0].Status"] = "Full",
        });
        Assert.Contains(Encoded(AbsenceRules.WeekendMessage), await weekendPost.Content.ReadAsStringAsync());

        var locked = await admin.GetStringAsync("/absences/day?date=2026-09-30");
        Assert.Contains("data-testid=\"day-locked\"", locked);
        Assert.Contains("<fieldset class=\"segmented\"", locked);
        Assert.Contains("disabled=\"disabled\"", locked);
        Assert.DoesNotContain("data-testid=\"day-save\"", locked);

        var lockedPost = await admin.PostFormAsync("/absences/day", "/absences/day?date=2026-09-30", new Dictionary<string, string>
        {
            ["Date"] = "2026-09-30", ["Rows[0].PersonId"] = id.ToString(CultureInfo.InvariantCulture), ["Rows[0].Status"] = "Present",
        });
        Assert.Contains(Encoded(AbsenceRules.LockedMessage), await admin.FollowAsync(lockedPost));
        Assert.Single(await AbsencesAsync(id));
    }

    // ---------- Range entry ----------

    [Fact]
    public async Task Range_preview_shows_every_outcome_and_confirm_adds_only_the_will_add_dates()
    {
        var id = await App.CreatePersonAsync("Range Person", joined: new DateOnly(2026, 9, 1), left: Oct(14));
        await AddAbsenceAsync(id, Oct(7));
        await using var app = AppAt(Today, lockedBefore: Oct(1));
        var manager = await app.SignInAsync(App, AppRoles.Manager);

        var previewUrl = $"/absences/range?PersonId={id}&From=2026-09-28&To=2026-10-16&Note=Travel";
        var preview = await manager.GetStringAsync(previewUrl);
        string Outcome(int month, int day) => Regex.Match(preview, $"data-date=\"2026-{month:00}-{day:00}\" data-outcome=\"(\\w+)\"").Groups[1].Value;
        Assert.Equal("Locked", Outcome(9, 28));
        Assert.Equal("Locked", Outcome(9, 30));
        Assert.Equal("WillAdd", Outcome(10, 1));
        Assert.Equal("Weekend", Outcome(10, 3));
        Assert.Equal("AlreadyRecorded", Outcome(10, 7));
        Assert.Equal("WillAdd", Outcome(10, 14));
        Assert.Equal("NotEmployed", Outcome(10, 15));
        Assert.Equal("NotEmployed", Outcome(10, 16));
        Assert.Contains("data-testid=\"range-will-add\">9 days<", preview); // Oct 1, 2, 5, 6, 8, 9, 12, 13, 14
        // Allocation after adding: Oct 1 takes October's paid day, everything after it is unpaid.
        Assert.Matches("data-date=\"2026-10-01\"(?:(?!data-date)[\\s\\S])*?</i> Paid\\s*</span>", preview);
        Assert.Matches("data-date=\"2026-10-02\"(?:(?!data-date)[\\s\\S])*?</i> Unpaid\\s*</span>", preview);

        var far = await manager.GetStringAsync($"/absences/range?PersonId={await App.CreatePersonAsync("Far Future", joined: new DateOnly(2026, 9, 1))}&From=2027-10-04&To=2027-10-15");
        Assert.Contains("data-outcome=\"WillAdd\"", far);
        Assert.Contains("data-outcome=\"TooFarAhead\"", far);

        // Confirm with exactly the preview's dates.
        var willAdd = Regex.Matches(preview, "name=\"Dates\" value=\"([0-9-]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(9, willAdd.Count);
        var confirm = await manager.PostAsync("/absences/range", new FormUrlEncodedContent(
            RangeConfirm(id, "2026-09-28", "2026-10-16", "Travel", willAdd, await manager.GetAntiforgeryTokenAsync(previewUrl))));
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        Assert.Equal("9 absences added.", Toast(await manager.FollowAsync(confirm)));

        var stored = await AbsencesAsync(id);
        Assert.Equal(10, stored.Count);
        Assert.All(stored.Where(a => a.Date != Oct(7)), a => Assert.Equal(("Travel", AbsencePortion.Full), (a.Note, a.Portion)));
    }

    [Theory]
    [InlineData("2026-10-10")] // weekend
    [InlineData("2026-09-29")] // locked
    [InlineData("2026-10-07")] // already recorded
    [InlineData("2026-10-20")] // outside the range
    public async Task A_tampered_range_confirm_is_rejected_as_a_whole(string tamperedDate)
    {
        var id = await App.CreatePersonAsync("Tamper Person", joined: new DateOnly(2026, 9, 1));
        await AddAbsenceAsync(id, Oct(7));
        await using var app = AppAt(Today, lockedBefore: Oct(1));
        var admin = await app.SignInAsync(App, AppRoles.Admin);

        var token = await admin.GetAntiforgeryTokenAsync("/absences/range");
        var response = await admin.PostAsync("/absences/range", new FormUrlEncodedContent(
            RangeConfirm(id, "2026-09-28", "2026-10-12", null, ["2026-10-08", tamperedDate], token)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(AbsenceService.RangeChangedMessage, await response.Content.ReadAsStringAsync());
        Assert.Equal([Oct(7)], (await AbsencesAsync(id)).Select(a => a.Date).ToArray());
    }

    private static List<KeyValuePair<string, string>> RangeConfirm(int personId, string from, string to, string? note, IEnumerable<string> dates, string token)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("PersonId", personId.ToString(CultureInfo.InvariantCulture)),
            new("From", from),
            new("To", to),
        };
        if (note is not null) fields.Add(new("Note", note));
        fields.AddRange(dates.Select(d => new KeyValuePair<string, string>("Dates", d)));
        return fields;
    }

    [Fact]
    public async Task Range_longer_than_31_days_is_refused()
    {
        var id = await App.CreatePersonAsync("Long Range", joined: new DateOnly(2026, 9, 1));
        await using var app = AppAt(Today);
        var admin = await app.SignInAsync(App, AppRoles.Admin);

        var html = await admin.GetStringAsync($"/absences/range?PersonId={id}&From=2026-10-01&To=2026-11-01");
        Assert.Contains(AbsenceService.RangeTooLongMessage, html);
        Assert.DoesNotContain("data-testid=\"range-preview-panel\"", html);
    }

    // ---------- List, summary, demo data ----------

    [Fact]
    public async Task List_filters_and_summary_match_the_demo_G3_G4_G5_people()
    {
        await using (var scope = App.Services.CreateAsyncScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DemoDataSeeder>();
            await seeder.SeedAsync();
            var count = await CountAbsencesAsync();
            await seeder.SeedAsync(); // idempotent
            Assert.Equal(count, await CountAbsencesAsync());
        }

        await using var app = AppAt(Today);
        var manager = await app.SignInAsync(App, AppRoles.Manager);

        // G3 (Kamran Yousaf): Oct 5 half, Oct 7 full → 1.5 absent, 1 paid, 0.5 unpaid, 10.5 of 11.
        var g3 = await manager.GetStringAsync("/absences?period=2026-10-01&q=Kamran");
        AssertSummary(g3, "1.5 days", "1 day", "0.5 days", "10.5 of 11");
        Assert.Equal(2, AbsenceRowRegex().Matches(g3).Count);
        var partly = await manager.GetStringAsync("/absences?period=2026-10-01&q=Kamran&paid=PartlyPaid");
        Assert.Single(AbsenceRowRegex().Matches(partly));
        Assert.Contains("07 Oct 2026", partly);
        Assert.Contains("0.5 paid · 0.5 unpaid", partly);
        var half = await manager.GetStringAsync("/absences?period=2026-10-01&q=Kamran&portion=Half");
        Assert.Single(AbsenceRowRegex().Matches(half));
        Assert.Contains("05 Oct 2026", half);

        // G4 (Bilal Ahmed, Oct 1–15): Oct 6 uses the paid day → 11 of 11.
        AssertSummary(await manager.GetStringAsync("/absences?period=2026-10-01&q=Bilal"), "1 day", "1 day", "0 days", "11 of 11");

        // G5 (Bilal Ahmed, Oct 16–31): Oct 20 and Oct 27 unpaid → 9 of 11.
        var g5 = await manager.GetStringAsync("/absences?period=2026-10-16&q=Bilal");
        AssertSummary(g5, "2 days", "0 days", "2 days", "9 of 11");
        Assert.Equal(2, AbsenceRowRegex().Matches(await manager.GetStringAsync("/absences?period=2026-10-16&q=Bilal&paid=Unpaid")).Count);
        Assert.Empty(AbsenceRowRegex().Matches(await manager.GetStringAsync("/absences?period=2026-10-16&q=Bilal&paid=Paid")));

        // G1, G2, G6, G7 have no absences; G7 (left Oct 21) is inactive, so only the "All" status shows inactive people.
        foreach (var name in new[] { "Ayesha", "Nadia", "Imran", "Rizwan" })
        {
            Assert.DoesNotContain("data-testid=\"summary-row\"", await manager.GetStringAsync($"/absences?period=2026-10-16&q={name}&status=All"));
        }

        // Period navigation and the default (current) period.
        var current = await manager.GetStringAsync("/absences");
        Assert.Contains("data-testid=\"period-label\">01–15 Oct 2026<", WebUtility.HtmlDecode(current));
        Assert.Contains("href=\"/absences?period=2026-09-16\"", current);
        Assert.Contains("href=\"/absences?period=2026-10-16\"", current);
    }

    private static async Task<int> CountAbsencesAsync()
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        return await db.Absences.CountAsync();
    }

    private static void AssertSummary(string html, string absent, string paid, string unpaid, string payable)
    {
        var row = SummaryRowRegex().Match(html);
        Assert.True(row.Success, "No summary row.");
        Assert.Contains($"data-testid=\"summary-absent\">{absent}<", row.Value);
        Assert.Contains($"data-testid=\"summary-paid\">{paid}<", row.Value);
        Assert.Contains($"data-testid=\"summary-unpaid\">{unpaid}<", row.Value);
        Assert.Contains($"data-testid=\"summary-payable\">{payable}<", row.Value);
    }

    // ---------- Person tab ----------

    [Fact]
    public async Task Person_calendar_marks_absences_and_shows_paid_leave_left()
    {
        var id = await App.CreatePersonAsync("Calendar Person", joined: Oct(2));
        await AddAbsenceAsync(id, Oct(5), AbsencePortion.Half);
        await using var app = AppAt(Today);
        var manager = await app.SignInAsync(App, AppRoles.Manager);

        var html = await manager.GetStringAsync($"/people/{id}?tab=absences");
        Assert.Contains("data-testid=\"calendar-month\">October 2026<", html);
        Assert.Contains("Paid leave this month", html);
        Assert.Contains("data-testid=\"paid-leave-left\">0.5 of 1 day left<", html);
        Assert.Contains("data-testid=\"current-payable\">10 of 11<", html); // joined Oct 2: 10 employed days, the half day is paid
        Assert.Contains("data-date=\"2026-10-05\" data-state=\"Half-Paid\"", html);
        Assert.Contains("data-date=\"2026-10-03\" data-state=\"weekend\"", html);
        Assert.Contains("data-date=\"2026-10-01\" data-state=\"not-employed\"", html);
        Assert.Contains("data-date=\"2026-10-06\" data-state=\"working\"", html);
        Assert.Contains("aria-current=\"date\"", html);
        Assert.DoesNotContain("data-testid=\"salary-tab\"", html);

        await AddAbsenceAsync(id, Oct(7));
        html = await manager.GetStringAsync($"/people/{id}?tab=absences");
        Assert.Contains("data-date=\"2026-10-07\" data-state=\"Full-Partly paid\"", html);
        Assert.Contains("data-testid=\"paid-leave-left\">0 of 1 day left<", html);
        Assert.Contains("data-testid=\"current-payable\">9.5 of 11<", html);
        Assert.Contains("class=\"cal-portion\">Full<", html);

        // Another month: November has a fresh day; the label names the month.
        var november = await manager.GetStringAsync($"/people/{id}?tab=absences&month=2026-11");
        Assert.Contains("Paid leave in November 2026", november);
        Assert.Contains("data-testid=\"paid-leave-left\">1 of 1 day left<", november);

        // Year filter lists the years with absences.
        Assert.Contains("data-testid=\"person-absence-row\"", html);
        Assert.DoesNotContain("data-testid=\"person-absence-row\"", await manager.GetStringAsync($"/people/{id}?tab=absences&year=2025"));
    }

    // ---------- Later-allocation notes ----------

    [Fact]
    public async Task Editing_or_deleting_an_earlier_absence_notes_the_change_to_later_ones()
    {
        var id = await App.CreatePersonAsync("Shifting Leave", joined: new DateOnly(2026, 9, 1));
        var first = await AddAbsenceAsync(id, Oct(6));
        var later = await AddAbsenceAsync(id, Oct(20));
        await using var app = AppAt(Today);
        var admin = await app.SignInAsync(App, AppRoles.Admin);

        var editPage = await admin.GetStringAsync($"/absences/{first}/edit");
        Assert.Contains("Changing it to half day will change the paid/unpaid status of 1 later absence in October 2026.", editPage);
        Assert.Contains("Deleting it will change the paid/unpaid status of 1 later absence in October 2026.", editPage);
        Assert.DoesNotContain("data-testid=\"later-change-note\"", await admin.GetStringAsync($"/absences/{later}/edit"));

        var form = NewForm(id, Oct(6), "Half");
        form["RowVersion"] = PeopleHelpers.RowVersion(editPage);
        var edit = await admin.PostFormAsync($"/absences/{first}/edit", $"/absences/{first}/edit", form);
        Assert.Equal("Absence on 06 Oct 2026 updated. This changed the paid/unpaid status of 1 later absence in October 2026.", Toast(await admin.FollowAsync(edit)));

        var list = await admin.GetStringAsync("/absences?period=2026-10-16&q=Shifting");
        Assert.Contains("0.5 paid · 0.5 unpaid", list);

        var delete = await admin.PostFormAsync($"/absences/{first}/delete?returnTo=person", $"/absences/{later}/edit");
        Assert.Equal($"/people/{id}?tab=absences&month=2026-10#absences", delete.Headers.Location?.OriginalString);
        Assert.Equal("Absence on 06 Oct 2026 deleted. This changed the paid/unpaid status of 1 later absence in October 2026.", Toast(await admin.FollowAsync(delete)));

        // Nothing later to change: no note.
        var plain = await admin.PostFormAsync($"/absences/{later}/delete", "/absences");
        Assert.Equal("Absence on 20 Oct 2026 deleted.", Toast(await admin.FollowAsync(plain)));
    }

    // ---------- Dashboard ----------

    [Fact]
    public async Task Dashboard_tiles_count_this_periods_days_unpaid_days_and_people_absent_today()
    {
        var one = await App.CreatePersonAsync("Tile One", joined: new DateOnly(2026, 9, 1));
        var two = await App.CreatePersonAsync("Tile Two", joined: new DateOnly(2026, 9, 1));
        await AddAbsenceAsync(one, Oct(6)); // paid
        await AddAbsenceAsync(one, Oct(7), AbsencePortion.Half); // unpaid
        await AddAbsenceAsync(two, Today, AbsencePortion.Half); // today, paid
        await AddAbsenceAsync(two, Oct(20)); // next period
        await AddAbsenceAsync(two, new DateOnly(2026, 9, 30)); // previous period
        await using var app = AppAt(Today);
        var manager = await app.SignInAsync(App, AppRoles.Manager);

        var html = await manager.GetStringAsync("/");
        Assert.Matches("data-testid=\"absences-tile\"[\\s\\S]*?stat-value\">2 days<[\\s\\S]*?0.5 unpaid", html);
        Assert.Matches("data-testid=\"absent-today-tile\"[\\s\\S]*?stat-value\">1<", html);
        Assert.Contains("href=\"/absences\"", html);
        Assert.Contains("href=\"/absences/day\"", html);
        Assert.DoesNotContain("Pending absences", html);
    }

    // ---------- Security ----------

    [Fact]
    public async Task Mutations_are_POST_only_need_antiforgery_and_are_audited_without_notes()
    {
        var id = await App.CreatePersonAsync("Audit Person", joined: new DateOnly(2026, 9, 1));
        await using var app = AppAt(Today);
        var adminUser = await App.CreateUserAsync(AppRoles.Admin);
        using var admin = app.Factory.CreateHttpsClient();
        await admin.PostLoginAsync(adminUser.Email, adminUser.Password);

        const string secretNote = "Private medical detail 7Q";
        await admin.PostFormAsync("/absences/new", "/absences/new", NewForm(id, Oct(7), note: secretNote));
        var absence = Assert.Single(await AbsencesAsync(id));
        var form = NewForm(id, Oct(7), "Half", secretNote + " updated");
        form["RowVersion"] = await RowVersionAsync(absence.Id);
        await admin.PostFormAsync($"/absences/{absence.Id}/edit", "/absences", form);
        await admin.PostFormAsync("/absences/day", "/absences/day?date=2026-10-08", new Dictionary<string, string>
        {
            ["Date"] = "2026-10-08", ["Rows[0].PersonId"] = id.ToString(CultureInfo.InvariantCulture), ["Rows[0].Status"] = "Full", ["Rows[0].Note"] = secretNote,
        });
        await admin.PostFormAsync($"/absences/{absence.Id}/delete", "/absences");

        var entries = App.Logs.Entries.Where(e => e.Values.Contains($"PersonId={id}")).ToList();
        Assert.Contains(entries, e => e.EventId.Id == 1400 && e.Values.Contains($"ActorId={adminUser.Id}") && e.Values.Contains($"Date={Oct(7)}") && e.Values.Contains("Portion=Full"));
        Assert.Contains(entries, e => e.EventId.Id == 1401 && e.Values.Contains("OldPortion=Full") && e.Values.Contains("NewPortion=Half"));
        Assert.Contains(entries, e => e.EventId.Id == 1402 && e.Values.Contains($"AbsenceId={absence.Id}") && e.Values.Contains("OldPortion=Half"));
        Assert.Contains(entries, e => e.EventId.Id == 1400 && e.Values.Contains($"Date={Oct(8)}"));
        Assert.DoesNotContain(App.Logs.Entries, e => e.AllText.Contains("Private medical", StringComparison.Ordinal));
        Assert.All(entries, e => Assert.Equal(SecurityLog.Category, e.Category));

        // GET can't delete; POSTs without a token are rejected.
        var remaining = (await AbsencesAsync(id)).Single();
        var get = await admin.GetAsync($"/absences/{remaining.Id}/delete");
        Assert.True(get.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"GET delete → {(int)get.StatusCode}");
        foreach (var url in new[] { "/absences/new", $"/absences/{remaining.Id}/edit", $"/absences/{remaining.Id}/delete", "/absences/day", "/absences/range" })
        {
            var post = await admin.PostAsync(url, new FormUrlEncodedContent(NewForm(id, Oct(9))));
            Assert.True(post.StatusCode == HttpStatusCode.BadRequest, $"POST {url} without a token → {(int)post.StatusCode}");
        }

        Assert.Single(await AbsencesAsync(id));
    }

    [Fact]
    public async Task New_pages_have_no_inline_scripts_styles_or_handlers()
    {
        var id = await App.CreatePersonAsync("Markup Person", joined: new DateOnly(2026, 9, 1));
        var absence = await AddAbsenceAsync(id, Oct(7), AbsencePortion.Half, "Note");
        await AddAbsenceAsync(id, Oct(8));
        await using var app = AppAt(Today, lockedBefore: Oct(1));
        var manager = await app.SignInAsync(App, AppRoles.Manager);

        foreach (var url in new[]
                 {
                     "/absences", "/absences?period=2026-09-16", "/absences/day", "/absences/day?date=2026-10-10", "/absences/day?date=2026-09-30",
                     "/absences/new", $"/absences/{absence}/edit", "/absences/range", $"/absences/range?PersonId={id}&From=2026-09-28&To=2026-10-12",
                     $"/people/{id}?tab=absences", "/",
                 })
        {
            var response = await manager.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {(int)response.StatusCode}");
            var html = await response.Content.ReadAsStringAsync();
            Assert.Empty(InlineScriptRegex().Matches(html));
            Assert.DoesNotContain(" style=\"", html);
            Assert.Empty(EventHandlerRegex().Matches(html));
            Assert.Single(Regex.Matches(html, "<h1[\\s>]"));
            Assert.DoesNotContain("Billed", html, StringComparison.OrdinalIgnoreCase);
        }
    }

    [GeneratedRegex("data-testid=\"absence-row\"")]
    private static partial Regex AbsenceRowRegex();

    [GeneratedRegex("data-testid=\"attendance-row\"")]
    private static partial Regex AttendanceRowRegex();

    [GeneratedRegex("data-testid=\"summary-row\"[\\s\\S]*?</tr>")]
    private static partial Regex SummaryRowRegex();

    [GeneratedRegex("class=\"toast-message\"[^>]*>([^<]*)<")]
    private static partial Regex ToastRegex();

    [GeneratedRegex("<script\\b(?![^>]*\\bsrc\\s*=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScriptRegex();

    [GeneratedRegex("\\son[a-z]+\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerRegex();
}
