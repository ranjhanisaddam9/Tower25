using HR.Domain.Pay;
using HR.Domain.Payroll;
using HR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HR.Infrastructure.Payroll;

/// <summary>A pay period is locked ⇔ its payroll run is Finalized (SPEC §6).</summary>
public sealed class PayrollLock(AppDbContext db) : IPayrollLock
{
    public Task<bool> IsLockedAsync(DateOnly periodStart, CancellationToken cancellationToken = default) =>
        db.PayrollRuns.AsNoTracking().AnyAsync(r => r.PeriodStart == periodStart && r.Status == PayrollStatus.Finalized, cancellationToken);

    public Task<bool> IsLockedForPersonAsync(int personId, DateOnly periodStart, CancellationToken cancellationToken = default) =>
        db.PayrollLines.AsNoTracking().AnyAsync(
            l => l.PersonId == personId && db.PayrollRuns.Any(r => r.Id == l.RunId && r.PeriodStart == periodStart && r.Status == PayrollStatus.Finalized),
            cancellationToken);

    public Task<DateOnly?> LatestLockedPeriodStartAsync(CancellationToken cancellationToken = default) =>
        db.PayrollRuns.AsNoTracking()
            .Where(r => r.Status == PayrollStatus.Finalized)
            .OrderByDescending(r => r.PeriodStart)
            .Select(r => (DateOnly?)r.PeriodStart)
            .FirstOrDefaultAsync(cancellationToken);
}
