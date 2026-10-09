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
        await db.People.ExecuteDeleteAsync();
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
