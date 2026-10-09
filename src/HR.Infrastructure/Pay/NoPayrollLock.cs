using HR.Domain.Pay;

namespace HR.Infrastructure.Pay;

/// <summary>Until payroll exists (M7), no period is locked.</summary>
public sealed class NoPayrollLock : IPayrollLock
{
    public Task<bool> IsLockedAsync(DateOnly periodStart, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
