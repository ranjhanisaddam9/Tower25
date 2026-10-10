using HR.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HR.Tests.Integration.Infrastructure;

/// <summary>
/// One per test run: migrates HRPayroll_Test at the start, resets data between tests, and drops it at the end.
/// Also owns the shared Development and Production app factories. Never touches the HRPayroll dev database.
/// </summary>
public sealed class TestDatabaseFixture : IAsyncLifetime
{
    public const string DatabaseName = "HRPayroll_Test";

    public static string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("HRPAYROLL_TEST_CONNECTION")
        ?? $"Server=.\\SQLEXPRESS;Database={DatabaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true";

    public HrWebApplicationFactory Development { get; } = new("Development");

    public HrWebApplicationFactory Production { get; } = new("Production");

    public async Task InitializeAsync()
    {
        EnsureTestDatabase();
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// Deletes all users so every test starts from an empty, migrated database. Roles are reference data
    /// (created by the startup seeder) and stay.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var db = CreateDbContext();
        // Invoices (lines cascade) and their per-year counters; Settings back to the migration's defaults.
        await db.Invoices.ExecuteDeleteAsync();
        await db.Set<HR.Domain.Invoices.InvoiceCounter>().ExecuteDeleteAsync();
        await db.Settings.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.BusinessName, (string?)null).SetProperty(x => x.BusinessAddress, (string?)null)
            .SetProperty(x => x.BusinessEmail, (string?)null).SetProperty(x => x.BusinessPhone, (string?)null)
            .SetProperty(x => x.BankName, (string?)null).SetProperty(x => x.BankAccountTitle, (string?)null)
            .SetProperty(x => x.BankAccountNumber, (string?)null).SetProperty(x => x.BankSwift, (string?)null)
            .SetProperty(x => x.ClientName, (string?)null).SetProperty(x => x.ClientAddress, (string?)null)
            .SetProperty(x => x.ClientContactPerson, (string?)null).SetProperty(x => x.ClientEmail, (string?)null)
            .SetProperty(x => x.InvoicePrefix, "INV").SetProperty(x => x.PaymentTermsDays, 7)
            .SetProperty(x => x.InvoiceFooter, (string?)null).SetProperty(x => x.PayslipIssuerName, "HR Payroll"));

        // Finalized runs can never be deleted (trigger), so tests first turn them back into drafts; lines, adjustments
        // and line absences cascade from runs.
        await db.PayrollRuns.ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, HR.Domain.Payroll.PayrollStatus.Draft).SetProperty(r => r.FinalizedAt, (DateTimeOffset?)null));
        await db.PayrollRuns.ExecuteDeleteAsync();
        await db.ExchangeRates.ExecuteDeleteAsync();
        await db.People.ExecuteDeleteAsync(); // employment periods, rate records and absences cascade
        // Identity's user-role, claim, login and token rows cascade from users.
        await db.Users.ExecuteDeleteAsync();
        // The person-code sequence is deliberately NOT reset: codes are never reused, even across tests.
    }

    public async Task DisposeAsync()
    {
        await Development.DisposeAsync();
        await Production.DisposeAsync();

        EnsureTestDatabase();
        await using var db = CreateDbContext();
        await db.Database.EnsureDeletedAsync();
    }

    public static AppDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    private static void EnsureTestDatabase()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;
        if (!string.Equals(database, DatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Integration tests may only use '{DatabaseName}', not '{database}'.");
        }
    }
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<TestDatabaseFixture>
{
    public const string Name = "Integration";
}
