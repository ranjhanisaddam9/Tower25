using System.Data;
using System.Globalization;
using HR.Domain.Pay;
using HR.Domain.People;
using HR.Domain.Rates;
using HR.Infrastructure.Data.Configurations;
using HR.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HR.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Person> People => Set<Person>();

    public DbSet<EmploymentPeriod> EmploymentPeriods => Set<EmploymentPeriod>();

    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();

    public DbSet<RateRecord> RateRecords => Set<RateRecord>();

    /// <summary>Takes the next person-code number. Sequence values are never rolled back, so codes are never reused.</summary>
    public async Task<int> NextPersonCodeNumberAsync(CancellationToken cancellationToken = default)
    {
        // A plain scalar command: EF's SqlQuery wraps SQL in a subquery, where NEXT VALUE FOR is not allowed.
        var connection = Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT NEXT VALUE FOR [dbo].[PersonCodeSequence]";
            command.Transaction = Database.CurrentTransaction?.GetDbTransaction();
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasSequence<int>(PersonConfiguration.CodeSequence, "dbo").StartsAt(1).IncrementsBy(1);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
