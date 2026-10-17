using HR.Infrastructure.Data;
using HR.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HR.Tests.Integration;

/// <summary>
/// Pay model v1.1.0: the MonthlyUsdPayModel migration against old-style data. Each test gets its own throwaway database
/// (HRPayroll_Test_PayModel…, created and dropped here), migrated to the migration before, seeded with raw SQL in the old
/// schema, then migrated.
/// </summary>
public sealed class PayModelMigrationTests
{
    private const string Before = "20261010210705_AddTotpReplayGuard";

    private static string ConnectionString(string database)
    {
        Assert.StartsWith("HRPayroll_Test", database, StringComparison.Ordinal); // never the dev database
        return new SqlConnectionStringBuilder(TestDatabaseFixture.ConnectionString) { InitialCatalog = database }.ConnectionString;
    }

    private static AppDbContext Context(string database) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString(database)).Options);

    private static async Task WithDatabaseAsync(string database, Func<AppDbContext, Task> test)
    {
        await using var db = Context(database);
        await db.Database.EnsureDeletedAsync();
        try
        {
            await db.Database.GetService<IMigrator>().MigrateAsync(Before);
            await test(db);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static Task SqlAsync(AppDbContext db, string sql) => db.Database.ExecuteSqlRawAsync(sql);

    private static async Task<string> ScalarAsync(AppDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private const string Audit = "'2026-01-01T00:00:00+00:00', 'seed', '2026-01-01T00:00:00+00:00', 'seed'";

    private static string Person(int n, string source, string type = "Employee") =>
        $"INSERT INTO dbo.People (CodeNumber, Code, FullName, Type, Designation, Phone, JoiningDate, HireSource, CreatedAt, CreatedByUserId, UpdatedAt, UpdatedByUserId) " +
        $"VALUES ({n}, 'HR-{n:0000}', 'Person {n}', '{type}', 'Staff', '+923000000000', '2025-01-06', '{source}', {Audit});" +
        $"INSERT INTO dbo.EmploymentPeriods (PersonId, StartDate, CreatedAt, CreatedByUserId, UpdatedAt, UpdatedByUserId) VALUES (SCOPE_IDENTITY(), '2025-01-06', {Audit});";

    private static string OldRecord(int person, string from, decimal billed, decimal commissionPerPeriod, decimal pay, string currency, string? note = null) =>
        string.Create(CultureInfo.InvariantCulture,
            $"INSERT INTO dbo.RateRecords (PersonId, EffectiveFrom, BilledMonthlyUsd, CommissionPerPeriodUsd, PayMonthlyAmount, PayCurrency, ChangeType, Note, NeedsBillingReview, CreatedAt, CreatedByUserId, UpdatedAt, UpdatedByUserId) " +
            $"VALUES ((SELECT Id FROM dbo.People WHERE CodeNumber = {person}), '{from}', {billed}, {commissionPerPeriod}, {pay}, '{currency}', 'Initial', {(note is null ? "NULL" : $"N'{note}'")}, 0, {Audit});");

    private static async Task<(decimal Salary, decimal Commission, decimal Billed, bool Review, string? Note)> RecordAsync(AppDbContext db, int person, string from)
    {
        var text = await ScalarAsync(db,
            $"SELECT CONCAT_WS(CHAR(30), SalaryMonthlyUsd, CommissionMonthlyUsd, BilledMonthlyUsd, CAST(NeedsBillingReview AS int), ISNULL(Note, '')) FROM dbo.RateRecords " +
            $"WHERE PersonId = (SELECT Id FROM dbo.People WHERE CodeNumber = {person}) AND EffectiveFrom = '{from}'");
        var parts = text.Split((char)30);
        return (decimal.Parse(parts[0], CultureInfo.InvariantCulture), decimal.Parse(parts[1], CultureInfo.InvariantCulture),
            decimal.Parse(parts[2], CultureInfo.InvariantCulture), parts[3] == "1", parts[4].Length == 0 ? null : parts[4]);
    }

    /// <summary>Every frozen row, with its rowversion: identical JSON before and after means not a byte was written.</summary>
    private static Task<string> FrozenAsync(AppDbContext db) => ScalarAsync(db,
        "SELECT (SELECT * FROM dbo.PayrollRuns ORDER BY Id FOR JSON PATH) AS Runs, " +
        "(SELECT * FROM dbo.PayrollLines ORDER BY Id FOR JSON PATH) AS Lines, " +
        "(SELECT * FROM dbo.Invoices ORDER BY Id FOR JSON PATH) AS Invoices, " +
        "(SELECT * FROM dbo.InvoiceLines ORDER BY Id FOR JSON PATH) AS InvoiceLines FOR JSON PATH, WITHOUT_ARRAY_WRAPPER");

    [Fact]
    public Task Old_records_convert_by_hire_source_and_frozen_payrolls_and_invoices_are_untouched() =>
        WithDatabaseAsync("HRPayroll_Test_PayModel", async db =>
        {
            await SqlAsync(db, Person(1, "CompanyRecommended") + Person(2, "BudgetHire") + Person(3, "BudgetHire") + Person(4, "Owner"));
            await SqlAsync(db, $"INSERT INTO dbo.ExchangeRates (EffectiveFrom, UsdToPkr, CreatedAt, CreatedByUserId, UpdatedAt, UpdatedByUserId) VALUES ('2026-01-01', 250, {Audit}), ('2026-06-01', 280, {Audit});");
            await SqlAsync(db,
                OldRecord(1, "2025-01-01", 300m, 25m, 300m, "USD") +              // CompanyRecommended
                OldRecord(2, "2025-01-01", 1000m, 0m, 700m, "USD") +              // BudgetHire paid in USD
                OldRecord(3, "2025-10-01", 1000m, 0m, 150000m, "PKR", "Old note") + // PKR before any rate → earliest (250)
                OldRecord(3, "2026-07-01", 1000m, 0m, 196000m, "PKR") +           // PKR with the rate in effect (280)
                OldRecord(4, "2025-01-01", 1200m, 0m, 1200m, "USD"));             // Owner

            // A finalized run with a legacy line, and its issued invoice (written while the run was a draft, as the app does).
            await SqlAsync(db, """
                INSERT INTO dbo.PayrollRuns (PeriodStart, PeriodEnd, Status, ExchangeRate, RateOverridden, GeneratedAt, GeneratedByUserId)
                VALUES ('2026-10-01', '2026-10-15', 'Draft', 280, 0, '2026-10-16T00:00:00+00:00', 'seed');
                DECLARE @run int = SCOPE_IDENTITY();
                INSERT INTO dbo.PayrollLines (RunId, PersonId, PersonCode, PersonName, Designation, PersonType, HireSource, BilledMonthlyUsd,
                    CommissionPerPeriodUsd, PayMonthlyAmount, PayCurrency, ExtraDays, WorkingDays, EmployedWorkingDays, UnpaidDays, PayableDays,
                    SalaryPartUsd, CommissionUsd, BilledUsd, PayUsd, PayPkr, AdjustmentsPkr, AdjustmentsUsd, NetPayPkr, NetPayUsd, InvoiceUsd,
                    OwnerEarningUsd, OwnerEarningPkr, IsOrphaned)
                VALUES (@run, (SELECT Id FROM dbo.People WHERE CodeNumber = 3), 'HR-0003', 'Person 3', 'Staff', 'Employee', 'BudgetHire', 1000,
                    0, 196000, 'PKR', 0, 11, 11, 0, 11, 500, 0, 500, 350, 98000, 0, 0, 98000, 350, 500, 150, 42000, 0);
                INSERT INTO dbo.Invoices (Number, RunId, PeriodStart, PeriodEnd, Status, IssueDate, DueDate, BusinessName, ClientName, TotalUsd,
                    IssuedAt, IssuedByUserId, UpdatedAt, UpdatedByUserId)
                VALUES ('T25-2026-0001', @run, '2026-10-01', '2026-10-15', 'Issued', '2026-10-16', '2026-10-31', 'Owner Co', 'Client Co', 500,
                    '2026-10-16T00:00:00+00:00', 'seed', '2026-10-16T00:00:00+00:00', 'seed');
                INSERT INTO dbo.InvoiceLines (InvoiceId, Position, PersonId, Name, Designation, DaysText, SalaryUsd, ExtrasUsd, AmountUsd)
                VALUES (SCOPE_IDENTITY(), 1, (SELECT Id FROM dbo.People WHERE CodeNumber = 3), 'Person 3', 'Staff', '11/11', 500, 0, 500);
                UPDATE dbo.PayrollRuns SET Status = 'Finalized', FinalizedAt = '2026-10-16T00:00:00+00:00', FinalizedByUserId = 'seed' WHERE Id = @run;
                """);
            var frozenBefore = await FrozenAsync(db);

            await db.Database.GetService<IMigrator>().MigrateAsync();

            Assert.Equal((300m, 50m, 350m, false, (string?)null), await RecordAsync(db, 1, "2025-01-01"));      // commission per period × 2
            Assert.Equal((700m, 300m, 1000m, false, (string?)null), await RecordAsync(db, 2, "2025-01-01"));    // Billed − Salary
            Assert.Equal((600m, 400m, 1000m, true, "Old note | Converted from Rs 150,000 at rate 250"), await RecordAsync(db, 3, "2025-10-01"));
            Assert.Equal((700m, 300m, 1000m, true, "Converted from Rs 196,000 at rate 280"), await RecordAsync(db, 3, "2026-07-01"));
            Assert.Equal((1200m, 0m, 1200m, false, (string?)null), await RecordAsync(db, 4, "2025-01-01"));

            Assert.Equal(frozenBefore, await FrozenAsync(db)); // byte for byte, rowversions included
            Assert.Equal("0", await ScalarAsync(db, "SELECT COUNT(*) FROM dbo.RateRecords WHERE BilledMonthlyUsd <> SalaryMonthlyUsd + CommissionMonthlyUsd"));
            Assert.False(db.Database.HasPendingModelChanges());
        });

    [Fact]
    public Task Billed_must_equal_salary_plus_commission_in_the_database() =>
        WithDatabaseAsync("HRPayroll_Test_PayModelCheck", async db =>
        {
            await db.Database.GetService<IMigrator>().MigrateAsync();
            await SqlAsync(db, Person(1, "CompanyRecommended"));
            var insert = $"INSERT INTO dbo.RateRecords (PersonId, EffectiveFrom, SalaryMonthlyUsd, CommissionMonthlyUsd, BilledMonthlyUsd, ChangeType, NeedsBillingReview, CreatedAt, CreatedByUserId, UpdatedAt, UpdatedByUserId) " +
                $"VALUES ((SELECT Id FROM dbo.People WHERE CodeNumber = 1), '2026-10-01', 300, 50, {{0}}, 'Initial', 0, {Audit});";

            var refused = await Assert.ThrowsAsync<SqlException>(() => SqlAsync(db, string.Format(CultureInfo.InvariantCulture, insert, 400)));
            Assert.Contains("CK_RateRecords_BilledIsSalaryPlusCommission", refused.Message, StringComparison.Ordinal);

            await SqlAsync(db, string.Format(CultureInfo.InvariantCulture, insert, 350));
            var update = await Assert.ThrowsAsync<SqlException>(() => SqlAsync(db, "UPDATE dbo.RateRecords SET CommissionMonthlyUsd = 60"));
            Assert.Contains("CK_RateRecords_BilledIsSalaryPlusCommission", update.Message, StringComparison.Ordinal);
        });

    [Fact]
    public Task The_migration_stops_on_a_PKR_company_recommended_record_and_changes_nothing() =>
        WithDatabaseAsync("HRPayroll_Test_PayModelStop", async db =>
        {
            await SqlAsync(db, Person(1, "CompanyRecommended"));
            await SqlAsync(db, OldRecord(1, "2025-01-01", 300m, 25m, 84000m, "PKR"));

            var stopped = await Assert.ThrowsAsync<SqlException>(() => db.Database.GetService<IMigrator>().MigrateAsync());
            Assert.Equal(51041, stopped.Number);
            Assert.DoesNotContain("84", stopped.Message, StringComparison.Ordinal); // ids only, no amounts

            // Rolled back: still the old schema and the old values.
            Assert.Equal("84000.00|PKR", await ScalarAsync(db, "SELECT CONCAT(PayMonthlyAmount, '|', PayCurrency) FROM dbo.RateRecords"));
            Assert.Equal(Before, await ScalarAsync(db, "SELECT TOP (1) MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC"));
        });

    [Fact]
    public Task The_migration_stops_when_PKR_pay_has_no_exchange_rate() =>
        WithDatabaseAsync("HRPayroll_Test_PayModelNoRate", async db =>
        {
            await SqlAsync(db, Person(1, "BudgetHire"));
            await SqlAsync(db, OldRecord(1, "2025-01-01", 1000m, 0m, 196000m, "PKR"));

            var stopped = await Assert.ThrowsAsync<SqlException>(() => db.Database.GetService<IMigrator>().MigrateAsync());
            Assert.Equal(51042, stopped.Number);
            Assert.Contains("no exchange rate", stopped.Message, StringComparison.Ordinal);
        });
}
