using System.Data;
using System.Globalization;
using HR.Domain.Absences;
using HR.Domain.Pay;
using HR.Domain.Payroll;
using HR.Domain.People;
using HR.Domain.Rates;
using HR.Infrastructure.Data.Configurations;
using HR.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace HR.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Person> People => Set<Person>();

    public DbSet<EmploymentPeriod> EmploymentPeriods => Set<EmploymentPeriod>();

    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();

    public DbSet<RateRecord> RateRecords => Set<RateRecord>();

    public DbSet<Absence> Absences => Set<Absence>();

    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();

    public DbSet<PayrollLine> PayrollLines => Set<PayrollLine>();

    public DbSet<PayrollAdjustment> PayrollAdjustments => Set<PayrollAdjustment>();

    public DbSet<HR.Domain.Settings.AppSettings> Settings => Set<HR.Domain.Settings.AppSettings>();

    public DbSet<HR.Domain.Invoices.Invoice> Invoices => Set<HR.Domain.Invoices.Invoice>();

    public DbSet<Security.AuditLogEntry> AuditLog => Set<Security.AuditLogEntry>();

    private readonly List<(Security.AuditEvent Event, string? Actor, string? EntityType, Func<object?> EntityId, string Summary, DateTimeOffset At)> _pendingAudit = [];

    /// <summary>
    /// Queues an audit row for the next SaveChanges: it is written in the same transaction as the change it describes
    /// (the context opens one if the caller has none). <paramref name="entityId"/> is read after the change is saved, so
    /// a lambda over a new entity's generated key works.
    /// </summary>
    public void Audit(Security.AuditEvent auditEvent, string? actorUserId, string? entityType, Func<object?> entityId, string summary) =>
        _pendingAudit.Add((auditEvent, actorUserId, entityType, entityId, summary, DateTimeOffset.UtcNow));

    public void Audit(Security.AuditEvent auditEvent, string? actorUserId, string? entityType, object? entityId, string summary) =>
        Audit(auditEvent, actorUserId, entityType, () => entityId, summary);

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (_pendingAudit.Count == 0)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        var pending = _pendingAudit.ToList();
        _pendingAudit.Clear(); // a failed save never leaves rows behind for a later, unrelated save
        var own = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            var actors = pending.Select(p => p.Actor).OfType<string>().Distinct().ToList();
            var names = actors.Count == 0
                ? []
                : await Users.AsNoTracking().Where(u => actors.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
            AuditLog.AddRange(pending.Select(p => ToEntry(p, names)));
            await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (own is not null)
            {
                await own.CommitAsync(cancellationToken);
            }

            return result;
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync();
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        if (_pendingAudit.Count == 0)
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        var pending = _pendingAudit.ToList();
        _pendingAudit.Clear();
        using var own = Database.CurrentTransaction is null ? Database.BeginTransaction() : null;
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        var actors = pending.Select(p => p.Actor).OfType<string>().Distinct().ToList();
        var names = actors.Count == 0 ? [] : Users.AsNoTracking().Where(u => actors.Contains(u.Id)).ToDictionary(u => u.Id, u => u.FullName);
        AuditLog.AddRange(pending.Select(p => ToEntry(p, names)));
        base.SaveChanges(acceptAllChangesOnSuccess);
        own?.Commit();
        return result;
    }

    private Security.AuditLogEntry ToEntry(
        (Security.AuditEvent Event, string? Actor, string? EntityType, Func<object?> EntityId, string Summary, DateTimeOffset At) p,
        Dictionary<string, string> names)
    {
        var summary = p.Summary.Length > Security.AuditLogEntry.SummaryMaxLength ? p.Summary[..Security.AuditLogEntry.SummaryMaxLength] : p.Summary;
        return new Security.AuditLogEntry
        {
            At = p.At,
            ActorUserId = p.Actor,
            ActorName = p.Actor is not null && names.TryGetValue(p.Actor, out var name) ? name : null,
            ActorIp = AuditContext?.ClientIp,
            EventId = p.Event.Id,
            EventName = p.Event.Name,
            EntityType = p.EntityType,
            EntityId = Convert.ToString(p.EntityId(), CultureInfo.InvariantCulture),
            Summary = summary,
        };
    }

    /// <summary>The request's client IP, from the application's services (null in tools and tests that build the context by hand).</summary>
    private Security.IAuditContext? AuditContext =>
        this.GetService<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptions>()
            .FindExtension<Microsoft.EntityFrameworkCore.Infrastructure.CoreOptionsExtension>()?
            .ApplicationServiceProvider?.GetService(typeof(Security.IAuditContext)) as Security.IAuditContext;

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
