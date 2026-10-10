using System.Net;
using System.Text.RegularExpressions;
using HR.Domain.Pay;
using HR.Domain.People;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Pay;
using HR.Infrastructure.Rates;
using HR.Tests.Integration.Infrastructure;
using HR.Web.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HR.Tests.Integration;

public partial class PayTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    private HrWebApplicationFactory App => Fixture.Development;

    // ---------- helpers ----------

    /// <summary>Every field the Admin form knows, plus fields no form has: the server must derive or ignore them.</summary>
    private static Dictionary<string, string> AdminForm(
        string month = "2026-10",
        string half = "1",
        string? salary = null,
        string? commission = null,
        string? budget = null,
        string? pay = null,
        string? currency = null,
        bool confirmLoss = false)
    {
        var form = new Dictionary<string, string>
        {
            ["EffectiveMonth"] = month,
            ["EffectiveHalf"] = half,
            // Tampering: these are not form fields and must have no effect.
            ["BilledMonthlyUsd"] = "1",
            ["CommissionPerPeriodUsd"] = "999",
            ["PayCurrency"] = "USD",
            ["PayMonthlyAmount"] = "5",
            ["ChangeType"] = "Correction",
            ["NeedsBillingReview"] = "true",
            ["CreatedByUserId"] = "someone-else",
        };
        if (salary is not null) form["Salary"] = salary;
        if (commission is not null) form["Commission"] = commission;
        if (budget is not null) form["Budget"] = budget;
        if (pay is not null) form["Pay"] = pay;
        if (currency is not null) form["Currency"] = currency;
        if (confirmLoss) form["ConfirmLoss"] = "true";
        return form;
    }

    private static Dictionary<string, string> IncrementForm(string month, string half, string pay) => new()
    {
        ["EffectiveMonth"] = month,
        ["EffectiveHalf"] = half,
        ["Pay"] = pay,
        // Tampering from a Manager.
        ["BilledMonthlyUsd"] = "99999",
        ["CommissionPerPeriodUsd"] = "999",
        ["PayCurrency"] = "USD",
        ["Currency"] = "USD",
        ["Budget"] = "1",
        ["Commission"] = "999",
        ["Salary"] = "1",
        ["NeedsBillingReview"] = "false",
    };

    private async Task<int> SeedRecordAsync(int personId, AdminPayInput input, string actorId = "test-admin")
    {
        await using var scope = App.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<PayRecordService>().CreateAdminAsync(personId, input, "seed", confirmLoss: true, actorId);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors?.Select(e => e.Message) ?? [result.Status.ToString()]));
        return result.Id!.Value;
    }

    private async Task AddExchangeRateAsync(DateOnly from, decimal rate)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ExchangeRateService>().CreateAsync(new RateInput(from, rate, null), true, "test");
        Assert.True(result.Succeeded);
    }

    private static async Task<List<RateRecord>> RecordsAsync(int personId)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        return await db.RateRecords.AsNoTracking().Where(r => r.PersonId == personId).OrderBy(r => r.EffectiveFrom).ToListAsync();
    }

    private static AdminPayInput Cr(string date, decimal salary, decimal commission = 25m) =>
        new(DateOnly.Parse(date, CultureInfo.InvariantCulture), salary, commission, null, null, null);

    private static AdminPayInput Budget(string date, decimal budget, decimal pay, PayCurrency currency = PayCurrency.PKR) =>
        new(DateOnly.Parse(date, CultureInfo.InvariantCulture), null, null, budget, pay, currency);

    private static AdminPayInput OwnerPay(string date, decimal salary) =>
        new(DateOnly.Parse(date, CultureInfo.InvariantCulture), salary, null, null, null, null);

    // ---------- Admin setup ----------

    [Theory]
    [InlineData(HireSource.CompanyRecommended)]
    [InlineData(HireSource.BudgetHire)]
    [InlineData(HireSource.Owner)]
    public async Task Admin_creates_the_Initial_record_and_the_invariants_hold_despite_tampered_fields(HireSource source)
    {
        var id = await App.CreatePersonAsync("Setup " + source, joined: new DateOnly(2026, 10, 8), source: source);
        var (admin, adminUser) = await App.SignInAsAsync(AppRoles.Admin);

        var form = source switch
        {
            HireSource.CompanyRecommended => AdminForm(salary: "300", commission: "25", budget: "9999", pay: "4444", currency: "PKR"),
            HireSource.BudgetHire => AdminForm(budget: "1000", pay: "196000", currency: "PKR", salary: "1", commission: "77"),
            _ => AdminForm(salary: "1200", commission: "50", budget: "9999", pay: "4444", currency: "PKR"),
        };
        var response = await admin.PostFormAsync($"/people/{id}/pay/new", $"/people/{id}/pay/new", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var record = Assert.Single(await RecordsAsync(id));
        Assert.Equal(new DateOnly(2026, 10, 1), record.EffectiveFrom); // joined Oct 8 → the Oct 1–15 period is allowed
        Assert.Equal(RateChangeType.Initial, record.ChangeType);
        Assert.False(record.NeedsBillingReview);
        Assert.Equal(adminUser.Id, record.CreatedByUserId);
        switch (source)
        {
            case HireSource.CompanyRecommended:
                Assert.Equal((300m, 25m, 300m, PayCurrency.USD), (record.BilledMonthlyUsd, record.CommissionPerPeriodUsd, record.PayMonthlyAmount, record.PayCurrency));
                break;
            case HireSource.BudgetHire:
                Assert.Equal((1000m, 0m, 196_000m, PayCurrency.PKR), (record.BilledMonthlyUsd, record.CommissionPerPeriodUsd, record.PayMonthlyAmount, record.PayCurrency));
                break;
            default:
                Assert.Equal((1200m, 0m, 1200m, PayCurrency.USD), (record.BilledMonthlyUsd, record.CommissionPerPeriodUsd, record.PayMonthlyAmount, record.PayCurrency));
                break;
        }
    }

    [Fact]
    public async Task EffectiveFrom_must_be_a_period_start_not_before_the_first_employment_period()
    {
        var id = await App.CreatePersonAsync("Early Start", joined: new DateOnly(2026, 10, 8), source: HireSource.Owner);
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        var tooEarly = await admin.PostFormAsync($"/people/{id}/pay/new", $"/people/{id}/pay/new", AdminForm(month: "2026-09", half: "16", salary: "1200"));
        Assert.Equal(HttpStatusCode.OK, tooEarly.StatusCode);
        Assert.Contains("Pay can&#x27;t start before 01 Oct 2026", await tooEarly.Content.ReadAsStringAsync());

        var badHalf = await admin.PostFormAsync($"/people/{id}/pay/new", $"/people/{id}/pay/new", AdminForm(month: "2026-10", half: "8", salary: "1200"));
        Assert.Equal(HttpStatusCode.Redirect, badHalf.StatusCode); // anything but 16 means the 1st
        Assert.Equal(new DateOnly(2026, 10, 1), Assert.Single(await RecordsAsync(id)).EffectiveFrom);
    }

    [Fact]
    public async Task Duplicate_EffectiveFrom_and_PKR_fractions_get_friendly_errors()
    {
        var id = await App.CreatePersonAsync("Duplicate Date", source: HireSource.BudgetHire);
        await SeedRecordAsync(id, Budget("2026-10-01", 1000m, 196_000m));
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        var duplicate = await admin.PostFormAsync($"/people/{id}/pay/new", $"/people/{id}/pay/new", AdminForm(budget: "1000", pay: "200000", currency: "PKR"));
        Assert.Contains(WebUtility.HtmlEncode(PayRecordService.DuplicateMessage), await duplicate.Content.ReadAsStringAsync());

        var fraction = await admin.PostFormAsync($"/people/{id}/pay/new", $"/people/{id}/pay/new", AdminForm(month: "2026-11", budget: "1000", pay: "200000.50", currency: "PKR"));
        Assert.Contains("PKR pay must be in whole rupees.", await fraction.Content.ReadAsStringAsync());

        Assert.Single(await RecordsAsync(id));
    }

    [Fact]
    public async Task Pay_at_or_above_the_budget_needs_the_loses_money_checkbox()
    {
        await AddExchangeRateAsync(new DateOnly(2026, 1, 1), 280m);
        var id = await App.CreatePersonAsync("Losing Hire", source: HireSource.BudgetHire);
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        // PKR 280,000 at 280 = $1,000, equal to the budget.
        var rejected = await admin.PostFormAsync($"/people/{id}/pay/new", $"/people/{id}/pay/new", AdminForm(budget: "1000", pay: "280000", currency: "PKR"));
        var html = await rejected.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Contains("This hire loses money", html);
        Assert.Contains(WebUtility.HtmlEncode(PayController.LossConfirmMessage), html);
        Assert.Empty(await RecordsAsync(id));

        var saved = await admin.PostFormAsync($"/people/{id}/pay/new", $"/people/{id}/pay/new", AdminForm(budget: "1000", pay: "280000", currency: "PKR", confirmLoss: true));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Single(await RecordsAsync(id));
    }

    [Fact]
    public async Task Admin_tab_shows_billing_and_earning_with_the_margin_math()
    {
        await AddExchangeRateAsync(new DateOnly(2026, 1, 1), 280m);
        var id = await App.CreatePersonAsync("Margin Person", source: HireSource.BudgetHire, joined: new DateOnly(2025, 1, 6));
        await SeedRecordAsync(id, Budget("2025-01-01", 1000m, 196_000m));
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        var html = await admin.GetStringAsync($"/people/{id}");
        Assert.Contains("data-testid=\"pay-earning\">$150.00<", html);
        Assert.Contains("Billed to the Company", html);
        Assert.Contains("$500.00 per full period", html);
        Assert.Contains("Rs 196,000", html);
        Assert.Contains("Rs 98,000 per full period", html);
    }

    /// <summary>
    /// The Admin "Current" card for the SPEC §9 setups (G1 CompanyRecommended, G4 BudgetHire, G6 Owner): billed per month,
    /// billed per full period and the Company's earning per full period, exactly as rendered.
    /// </summary>
    [Theory]
    [InlineData(HireSource.CompanyRecommended, "$300.00", "$175.00 per full period (incl. $25.00 commission)", "$25.00")]
    [InlineData(HireSource.BudgetHire, "$1,000.00", "$500.00 per full period", "$150.00")]
    [InlineData(HireSource.Owner, "$1,200.00", "$600.00 per full period", "$0.00")]
    public async Task Admin_current_card_renders_the_golden_billing_values(HireSource source, string billedMonthly, string billedPerPeriod, string earning)
    {
        await AddExchangeRateAsync(new DateOnly(2026, 1, 1), 280m);
        var id = await App.CreatePersonAsync("Golden " + source switch { HireSource.CompanyRecommended => "Alpha", HireSource.BudgetHire => "Delta", _ => "Foxtrot" },
            joined: new DateOnly(2026, 9, 1), source: source);
        await SeedRecordAsync(id, source switch
        {
            HireSource.CompanyRecommended => Cr("2026-10-01", 300m, 25m),
            HireSource.BudgetHire => Budget("2026-10-01", 1000m, 196_000m),
            _ => OwnerPay("2026-10-01", 1200m),
        });
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        var html = await admin.GetStringAsync($"/people/{id}");
        var start = html.IndexOf("data-testid=\"pay-current\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "The current card is missing.");
        var card = WebUtility.HtmlDecode(html[start..html.IndexOf("Your earning per full period", start, StringComparison.Ordinal)]);
        Assert.Contains($"Billed to the Company</span>", card);
        Assert.Contains($">{billedMonthly} <span class=\"pay-summary-unit\">/ month</span>", card);
        Assert.Contains($">{billedPerPeriod}</span>", card);
        Assert.Contains($"data-testid=\"pay-earning\">{earning}<", html);
    }

    // ---------- Manager increments ----------

    [Theory]
    [InlineData(HireSource.CompanyRecommended)]
    [InlineData(HireSource.BudgetHire)]
    [InlineData(HireSource.Owner)]
    public async Task Manager_increment_follows_the_source_rules_and_ignores_tampered_fields(HireSource source)
    {
        var id = await App.CreatePersonAsync("Increment " + source, joined: new DateOnly(2025, 1, 6), source: source);
        await SeedRecordAsync(id, source switch
        {
            HireSource.CompanyRecommended => Cr("2025-01-01", 300m, 25m),
            HireSource.BudgetHire => Budget("2025-01-01", 1000m, 196_000m),
            _ => OwnerPay("2025-01-01", 1200m),
        });
        var (manager, managerUser) = await App.SignInAsAsync(AppRoles.Manager);

        var newPay = source switch { HireSource.BudgetHire => "210000", HireSource.Owner => "1300", _ => "330" };
        var response = await manager.PostFormAsync($"/people/{id}/pay/increment", $"/people/{id}/pay/increment", IncrementForm("2026-04", "16", newPay));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var records = await RecordsAsync(id);
        Assert.Equal(2, records.Count);
        var increment = records[1];
        Assert.Equal(new DateOnly(2026, 4, 16), increment.EffectiveFrom);
        Assert.Equal(RateChangeType.Increment, increment.ChangeType);
        Assert.Equal(managerUser.Id, increment.CreatedByUserId);
        switch (source)
        {
            case HireSource.CompanyRecommended:
                Assert.Equal((330m, 25m, 330m, PayCurrency.USD), (increment.BilledMonthlyUsd, increment.CommissionPerPeriodUsd, increment.PayMonthlyAmount, increment.PayCurrency));
                Assert.False(increment.NeedsBillingReview);
                break;
            case HireSource.BudgetHire:
                Assert.Equal((1000m, 0m, 210_000m, PayCurrency.PKR), (increment.BilledMonthlyUsd, increment.CommissionPerPeriodUsd, increment.PayMonthlyAmount, increment.PayCurrency));
                Assert.True(increment.NeedsBillingReview);
                break;
            default:
                Assert.Equal((1300m, 0m, 1300m, PayCurrency.USD), (increment.BilledMonthlyUsd, increment.CommissionPerPeriodUsd, increment.PayMonthlyAmount, increment.PayCurrency));
                Assert.False(increment.NeedsBillingReview);
                break;
        }
    }

    [Fact]
    public async Task Manager_can_edit_and_delete_only_their_own_records()
    {
        var id = await App.CreatePersonAsync("Ownership", joined: new DateOnly(2025, 1, 6), source: HireSource.CompanyRecommended);
        var adminRecord = await SeedRecordAsync(id, Cr("2025-01-01", 300m));
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        await manager.PostFormAsync($"/people/{id}/pay/increment", $"/people/{id}/pay/increment", IncrementForm("2026-01", "1", "320"));
        var ownRecord = (await RecordsAsync(id)).Single(r => r.Id != adminRecord).Id;

        // Someone else's record: no edit form, no edit, no delete.
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/people/{id}/pay/{adminRecord}/increment-edit")).StatusCode);
        var tokenPage = $"/people/{id}/pay/{ownRecord}/increment-edit";
        var editOther = await manager.PostFormAsync($"/people/{id}/pay/{adminRecord}/increment-edit", tokenPage, IncrementForm("2025-01", "1", "999"));
        Assert.True(editOther.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
        var deleteOther = await manager.PostFormAsync($"/people/{id}/pay/{adminRecord}/delete", tokenPage);
        Assert.Equal(HttpStatusCode.Forbidden, deleteOther.StatusCode);
        Assert.Equal(300m, (await RecordsAsync(id)).Single(r => r.Id == adminRecord).PayMonthlyAmount);

        // Admin-only routes.
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/people/{id}/pay/{adminRecord}/edit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/people/{id}/pay/new")).StatusCode);

        // Their own: edit pay (tampering ignored), then delete.
        var editPage = await manager.GetStringAsync(tokenPage);
        var form = IncrementForm("2026-01", "1", "325");
        form["RowVersion"] = PeopleHelpers.RowVersion(editPage);
        Assert.Equal(HttpStatusCode.Redirect, (await manager.PostFormAsync(tokenPage, tokenPage, form)).StatusCode);
        var edited = (await RecordsAsync(id)).Single(r => r.Id == ownRecord);
        Assert.Equal((325m, 325m, 25m, PayCurrency.USD), (edited.PayMonthlyAmount, edited.BilledMonthlyUsd, edited.CommissionPerPeriodUsd, edited.PayCurrency));

        Assert.Equal(HttpStatusCode.Redirect, (await manager.PostFormAsync($"/people/{id}/pay/{ownRecord}/delete", $"/people/{id}")).StatusCode);
        Assert.Single(await RecordsAsync(id));
    }

    // ---------- Visibility ----------

    private static readonly string[] AdminOnlyWords = ["Billed", "Budget", "Commission", "Margin", "earning", "Review", "BillingChange", "1,234.56", "43.21"];

    [Fact]
    public async Task Manager_responses_never_contain_billing_information()
    {
        // Distinctive amounts: billed $1,234.56 vs pay $777.00; commission $43.21.
        var budgetPerson = await App.CreatePersonAsync("Person Bravo", joined: new DateOnly(2025, 1, 6), source: HireSource.BudgetHire);
        await SeedRecordAsync(budgetPerson, Budget("2025-01-01", 1234.56m, 777m, PayCurrency.USD));
        var crPerson = await App.CreatePersonAsync("Person Charlie", joined: new DateOnly(2025, 1, 6), source: HireSource.CompanyRecommended);
        await SeedRecordAsync(crPerson, Cr("2025-01-01", 500m, 43.21m));
        await SeedRecordAsync(crPerson, Cr("2025-06-01", 500m, 50m)); // BillingChange
        await App.CreatePersonAsync("Nobody Set Up", source: HireSource.CompanyRecommended);
        await AddExchangeRateAsync(new DateOnly(2025, 1, 1), 280m);

        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        // A Manager increment on the budget person creates the (Admin-only) review flag.
        await manager.PostFormAsync($"/people/{budgetPerson}/pay/increment", $"/people/{budgetPerson}/pay/increment", IncrementForm("2026-01", "1", "800"));

        var urls = new[]
        {
            "/", "/salaries", "/salaries?filter=NeedsReview", "/salaries?filter=ChangedRecently", "/salaries?filter=MissingSetup",
            $"/people/{budgetPerson}", $"/people/{crPerson}", $"/people/{budgetPerson}/pay/increment", $"/people/{crPerson}/pay/increment",
        };
        foreach (var url in urls)
        {
            var response = await manager.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} returned {(int)response.StatusCode}");
            var html = await response.Content.ReadAsStringAsync();
            foreach (var word in AdminOnlyWords)
            {
                Assert.False(html.Contains(word, StringComparison.OrdinalIgnoreCase), $"{url} contains \"{word}\"");
            }
        }

        // Managers see the BillingChange as an "Update", and the pay amounts.
        var crHtml = await manager.GetStringAsync($"/people/{crPerson}");
        Assert.Contains(">Update<", crHtml);
        Assert.Contains("$500.00", crHtml);
        Assert.Contains("$777.00", await manager.GetStringAsync($"/people/{budgetPerson}"));

        // The Admin sees all of it.
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var adminHtml = await admin.GetStringAsync($"/people/{budgetPerson}");
        Assert.Contains("$1,234.56", adminHtml);
        Assert.Contains("Review billing", adminHtml);
    }

    [Fact]
    public async Task No_hire_source_blocks_pay_setup_for_both_roles()
    {
        var id = await App.CreatePersonAsync("No Source");
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);

        Assert.Contains("data-testid=\"pay-no-source\"", await admin.GetStringAsync($"/people/{id}"));
        var adminNew = await admin.GetAsync($"/people/{id}/pay/new");
        Assert.Equal(HttpStatusCode.Redirect, adminNew.StatusCode);
        var adminPost = await admin.PostFormAsync($"/people/{id}/pay/new", $"/people/{id}", AdminForm(salary: "300", commission: "25"));
        Assert.Contains("Set this person&#x27;s hire source before setting up pay.", await admin.FollowAsync(adminPost));

        var managerDetails = await manager.GetStringAsync($"/people/{id}");
        Assert.Contains(PayRules.NoHireSourceMessage.Replace("'", "&#x27;", StringComparison.Ordinal), managerDetails);
        var managerIncrement = await manager.PostFormAsync($"/people/{id}/pay/increment", $"/people/{id}", IncrementForm("2026-10", "1", "300"));
        Assert.Contains(PayRules.NoHireSourceMessage.Replace("'", "&#x27;", StringComparison.Ordinal), await manager.FollowAsync(managerIncrement));

        Assert.Empty(await RecordsAsync(id));
    }

    // ---------- Hire source lock, review, payroll lock ----------

    [Fact]
    public async Task Hire_source_is_locked_while_pay_records_exist()
    {
        var id = await App.CreatePersonAsync("Locked Source", source: HireSource.BudgetHire);
        var recordId = await SeedRecordAsync(id, Budget("2026-10-01", 1000m, 196_000m));
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        Assert.Contains("data-testid=\"hire-source-locked\"", await admin.GetStringAsync($"/people/{id}"));
        var refused = await admin.PostFormAsync($"/people/{id}/hire-source", $"/people/{id}", new Dictionary<string, string> { ["hireSource"] = "CompanyRecommended" });
        Assert.Contains("Delete this person&#x27;s pay records before changing the hire source.", await admin.FollowAsync(refused));

        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            Assert.Equal(HireSource.BudgetHire, (await db.People.SingleAsync(p => p.Id == id)).HireSource);
        }

        await admin.PostFormAsync($"/people/{id}/pay/{recordId}/delete", $"/people/{id}");
        var allowed = await admin.PostFormAsync($"/people/{id}/hire-source", $"/people/{id}", new Dictionary<string, string> { ["hireSource"] = "CompanyRecommended" });
        Assert.Contains("Hire source set to Company recommended.", await admin.FollowAsync(allowed));
    }

    [Fact]
    public async Task Mark_reviewed_clears_the_flag_and_is_Admin_only()
    {
        var id = await App.CreatePersonAsync("Flag Me", joined: new DateOnly(2025, 1, 6), source: HireSource.BudgetHire);
        await SeedRecordAsync(id, Budget("2025-01-01", 1000m, 196_000m));
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        await manager.PostFormAsync($"/people/{id}/pay/increment", $"/people/{id}/pay/increment", IncrementForm("2026-01", "1", "200000"));
        var flagged = (await RecordsAsync(id)).Single(r => r.NeedsBillingReview);

        var managerAttempt = await manager.PostFormAsync($"/people/{id}/pay/{flagged.Id}/reviewed", $"/people/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, managerAttempt.StatusCode);
        Assert.True((await RecordsAsync(id)).Single(r => r.Id == flagged.Id).NeedsBillingReview);

        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        Assert.Matches("data-testid=\"review-tile\"[\\s\\S]*?stat-value\">1<", await admin.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostFormAsync($"/people/{id}/pay/{flagged.Id}/reviewed", $"/people/{id}")).StatusCode);
        Assert.False((await RecordsAsync(id)).Single(r => r.Id == flagged.Id).NeedsBillingReview);
        Assert.Matches("data-testid=\"review-tile\"[\\s\\S]*?stat-value\">0<", await admin.GetStringAsync("/"));
    }

    [Fact]
    public async Task A_locked_payroll_period_refuses_edits_and_deletes()
    {
        var id = await App.CreatePersonAsync("Locked Period", joined: new DateOnly(2026, 9, 1), source: HireSource.Owner);
        var oct1 = await SeedRecordAsync(id, OwnerPay("2026-10-01", 1200m));
        var nov1 = await SeedRecordAsync(id, OwnerPay("2026-11-01", 1300m));

        await using var factory = App.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPayrollLock>();
            services.AddSingleton<IPayrollLock>(new FakePayrollLock(new DateOnly(2026, 11, 1)));
        }));
        var adminUser = await App.CreateUserAsync(AppRoles.Admin);
        using var admin = factory.CreateHttpsClient();
        await admin.PostLoginAsync(adminUser.Email, adminUser.Password);

        var editPage = await admin.GetStringAsync($"/people/{id}/pay/{oct1}/edit");
        var form = AdminForm(month: "2026-10", half: "1", salary: "1250");
        form["RowVersion"] = PeopleHelpers.RowVersion(editPage);
        var edit = await admin.PostFormAsync($"/people/{id}/pay/{oct1}/edit", $"/people/{id}/pay/{oct1}/edit", form);
        Assert.Contains(PayRecordService.LockedMessage.Replace("'", "&#x27;", StringComparison.Ordinal), await admin.FollowAsync(edit));

        var delete = await admin.PostFormAsync($"/people/{id}/pay/{oct1}/delete", $"/people/{id}");
        Assert.Contains(PayRecordService.LockedMessage.Replace("'", "&#x27;", StringComparison.Ordinal), await admin.FollowAsync(delete));

        // Moving an unlocked record INTO the locked period is refused too.
        var move = AdminForm(month: "2026-10", half: "16", salary: "1300");
        move["RowVersion"] = PeopleHelpers.RowVersion(await admin.GetStringAsync($"/people/{id}/pay/{nov1}/edit"));
        await admin.PostFormAsync($"/people/{id}/pay/{nov1}/edit", $"/people/{id}/pay/{nov1}/edit", move);

        var records = await RecordsAsync(id);
        Assert.Equal(2, records.Count);
        Assert.Equal(1200m, records[0].PayMonthlyAmount);
        Assert.Equal(new DateOnly(2026, 11, 1), records[1].EffectiveFrom);

        // An unlocked record can still be edited.
        var ok = AdminForm(month: "2026-11", half: "1", salary: "1350");
        ok["RowVersion"] = PeopleHelpers.RowVersion(await admin.GetStringAsync($"/people/{id}/pay/{nov1}/edit"));
        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostFormAsync($"/people/{id}/pay/{nov1}/edit", $"/people/{id}/pay/{nov1}/edit", ok)).StatusCode);
        Assert.Equal(1350m, (await RecordsAsync(id))[1].PayMonthlyAmount);
    }

    [Fact]
    public async Task A_locked_payroll_period_refuses_new_records_for_Admin_and_Manager()
    {
        var id = await App.CreatePersonAsync("Locked Create", joined: new DateOnly(2026, 9, 1), source: HireSource.CompanyRecommended);
        await SeedRecordAsync(id, Cr("2026-09-01", 300m));

        await using var factory = App.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPayrollLock>();
            services.AddSingleton<IPayrollLock>(new FakePayrollLock(new DateOnly(2026, 11, 1)));
        }));
        var adminUser = await App.CreateUserAsync(AppRoles.Admin);
        using var admin = factory.CreateHttpsClient();
        await admin.PostLoginAsync(adminUser.Email, adminUser.Password);
        var managerUser = await App.CreateUserAsync(AppRoles.Manager);
        using var manager = factory.CreateHttpsClient();
        await manager.PostLoginAsync(managerUser.Email, managerUser.Password);
        var locked = PayRecordService.LockedMessage.Replace("'", "&#x27;", StringComparison.Ordinal);

        var adminNew = await admin.PostFormAsync($"/people/{id}/pay/new", $"/people/{id}/pay/new", AdminForm(month: "2026-10", half: "16", salary: "320", commission: "25"));
        Assert.Equal(HttpStatusCode.OK, adminNew.StatusCode);
        Assert.Contains(locked, await adminNew.Content.ReadAsStringAsync());

        var managerNew = await manager.PostFormAsync($"/people/{id}/pay/increment", $"/people/{id}/pay/increment", IncrementForm("2026-10", "1", "330"));
        Assert.Equal(HttpStatusCode.OK, managerNew.StatusCode);
        Assert.Contains(locked, await managerNew.Content.ReadAsStringAsync());

        Assert.Single(await RecordsAsync(id));

        // The first unlocked period is fine for both.
        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostFormAsync($"/people/{id}/pay/new", $"/people/{id}/pay/new", AdminForm(month: "2026-11", half: "1", salary: "320", commission: "25"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await manager.PostFormAsync($"/people/{id}/pay/increment", $"/people/{id}/pay/increment", IncrementForm("2026-11", "16", "330"))).StatusCode);
        Assert.Equal(3, (await RecordsAsync(id)).Count);
    }

    private sealed class FakePayrollLock(DateOnly lockedBefore) : IPayrollLock
    {
        // Locks every period that starts before the given date (both October periods in this test).
        public Task<bool> IsLockedAsync(DateOnly periodStart, CancellationToken cancellationToken = default) => Task.FromResult(periodStart < lockedBefore);

        public Task<DateOnly?> LatestLockedPeriodStartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DateOnly?>(HR.Domain.Payroll.PayPeriod.For(lockedBefore.AddDays(-1)).Start);
    }

    // ---------- Change types, audit, POST-only ----------

    [Fact]
    public async Task Admin_edits_rederive_change_types_and_are_audited_with_old_and_new_values()
    {
        var id = await App.CreatePersonAsync("Audited", joined: new DateOnly(2025, 1, 6), source: HireSource.CompanyRecommended);
        var (admin, adminUser) = await App.SignInAsAsync(AppRoles.Admin);

        await admin.PostFormAsync($"/people/{id}/pay/new", $"/people/{id}/pay/new", AdminForm(month: "2025-01", salary: "300", commission: "25"));
        await admin.PostFormAsync($"/people/{id}/pay/new", $"/people/{id}/pay/new", AdminForm(month: "2025-07", salary: "300", commission: "30"));
        var records = await RecordsAsync(id);
        Assert.Equal([RateChangeType.Initial, RateChangeType.BillingChange], records.Select(r => r.ChangeType).ToArray());

        var second = records[1].Id;
        var form = AdminForm(month: "2025-07", salary: "280", commission: "30");
        form["RowVersion"] = PeopleHelpers.RowVersion(await admin.GetStringAsync($"/people/{id}/pay/{second}/edit"));
        await admin.PostFormAsync($"/people/{id}/pay/{second}/edit", $"/people/{id}/pay/{second}/edit", form);
        Assert.Equal(RateChangeType.Decrement, (await RecordsAsync(id))[1].ChangeType);

        // Marked as a correction.
        form["RowVersion"] = PeopleHelpers.RowVersion(await admin.GetStringAsync($"/people/{id}/pay/{second}/edit"));
        form["MarkCorrection"] = "true";
        await admin.PostFormAsync($"/people/{id}/pay/{second}/edit", $"/people/{id}/pay/{second}/edit", form);
        Assert.Equal(RateChangeType.Correction, (await RecordsAsync(id))[1].ChangeType);

        await admin.PostFormAsync($"/people/{id}/pay/{second}/delete", $"/people/{id}");

        Assert.Contains(App.Logs.Entries, e => e.EventId.Id == 1300 && e.Values.Contains($"PersonId={id}") && e.Values.Contains($"ActorId={adminUser.Id}"));
        var edited = App.Logs.Entries.First(e => e.EventId.Id == 1301 && e.Values.Contains($"RecordId={second}"));
        Assert.Contains("billed 300.00 USD, commission 30.00 USD, pay 300.00 USD", edited.AllText);  // old
        Assert.Contains("billed 280.00 USD, commission 30.00 USD, pay 280.00 USD", edited.AllText);  // new
        Assert.Contains("->", edited.Message);
        Assert.Contains(App.Logs.Entries, e => e.EventId.Id == 1302 && e.Values.Contains($"RecordId={second}"));
    }

    [Fact]
    public async Task Pay_mutations_are_POST_only_and_need_antiforgery()
    {
        var id = await App.CreatePersonAsync("Guarded Pay", source: HireSource.Owner);
        var recordId = await SeedRecordAsync(id, OwnerPay("2026-10-01", 1200m));
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        foreach (var url in new[] { $"/people/{id}/pay/{recordId}/delete", $"/people/{id}/pay/{recordId}/reviewed" })
        {
            var get = await admin.GetAsync(url);
            Assert.True(get.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"GET {url} → {(int)get.StatusCode}");
        }

        foreach (var url in new[] { $"/people/{id}/pay/new", $"/people/{id}/pay/{recordId}/edit", $"/people/{id}/pay/{recordId}/delete",
                     $"/people/{id}/pay/{recordId}/reviewed", $"/people/{id}/pay/increment" })
        {
            var post = await admin.PostAsync(url, new FormUrlEncodedContent(AdminForm(salary: "1300")));
            Assert.True(post.StatusCode == HttpStatusCode.BadRequest, $"POST {url} without a token → {(int)post.StatusCode}");
        }

        Assert.Equal(1200m, Assert.Single(await RecordsAsync(id)).PayMonthlyAmount);
    }

    // ---------- Salaries overview and dashboard ----------

    [Fact]
    public async Task Salaries_filters_totals_and_dashboard_tiles()
    {
        await AddExchangeRateAsync(new DateOnly(2025, 1, 1), 280m);
        var cr = await App.CreatePersonAsync("Alpha CR", joined: new DateOnly(2025, 1, 6), source: HireSource.CompanyRecommended);
        await SeedRecordAsync(cr, Cr("2025-01-01", 300m, 25m));
        var budget = await App.CreatePersonAsync("Bravo Budget", joined: new DateOnly(2025, 1, 6), source: HireSource.BudgetHire);
        await SeedRecordAsync(budget, Budget("2025-01-01", 1000m, 196_000m));
        var owner = await App.CreatePersonAsync("Charlie Owner", joined: new DateOnly(2025, 1, 6), source: HireSource.Owner);
        await SeedRecordAsync(owner, OwnerPay("2025-01-01", 1200m));
        await App.CreatePersonAsync("Delta Missing", source: HireSource.CompanyRecommended);
        await App.CreatePersonAsync("Echo Missing");
        await App.CreatePersonAsync("Foxtrot Gone", source: HireSource.CompanyRecommended, left: new DateOnly(2026, 1, 30)); // inactive: excluded

        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        // A recent increment by the Manager on the budget person.
        var today = App.Services.GetRequiredService<HR.Domain.Time.IClock>().Today;
        var recent = HR.Domain.Payroll.PayPeriod.For(today).Start;
        await manager.PostFormAsync($"/people/{budget}/pay/increment", $"/people/{budget}/pay/increment",
            IncrementForm(recent.ToString("yyyy-MM", CultureInfo.InvariantCulture), recent.Day == 16 ? "16" : "1", "210000"));

        var all = await manager.GetStringAsync("/salaries");
        Assert.Contains("5 people", all);
        Assert.Equal(2, Regex.Matches(all, "data-testid=\"no-pay-setup\"").Count);
        Assert.Equal(["Delta Missing", "Echo Missing"], Names(await manager.GetStringAsync("/salaries?filter=MissingSetup")));
        Assert.Equal(["Bravo Budget"], Names(await manager.GetStringAsync("/salaries?filter=ChangedRecently")));
        Assert.Contains("+7.14%", WebUtility.HtmlDecode(await manager.GetStringAsync("/salaries?filter=ChangedRecently")));
        Assert.Matches("data-testid=\"no-pay-tile\"[\\s\\S]*?stat-value\">2<", await manager.GetStringAsync("/"));
        Assert.DoesNotContain("review-tile", await manager.GetStringAsync("/"));

        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var adminAll = await admin.GetStringAsync("/salaries");
        // Billed: 300 + 1000 + 1200 = $2,500.00. Earning per full period: CR 25.00 + Budget (500 − 375.00) 125.00 + Owner 0 = $150.00.
        Assert.Contains("data-testid=\"total-billed\">$2,500.00<", adminAll);
        Assert.Contains("data-testid=\"total-earning\">$150.00<", adminAll);
        Assert.Equal(["Bravo Budget"], Names(await admin.GetStringAsync("/salaries?filter=NeedsReview")));
        Assert.Matches("data-testid=\"review-tile\"[\\s\\S]*?stat-value\">1<", await admin.GetStringAsync("/"));
        Assert.Matches("data-testid=\"no-pay-tile\"[\\s\\S]*?stat-value\">2<", await admin.GetStringAsync("/"));
    }

    private static string[] Names(string html) =>
        Regex.Matches(html, "data-testid=\"salary-name\"[^>]*>([^<]+)<").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToArray();
}
