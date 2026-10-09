using HR.Domain.Time;
using HR.Infrastructure.Data;
using HR.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HR.Infrastructure.Identity;

public enum ManagerStatusFilter
{
    All,
    Active,
    Inactive,
}

public sealed record ManagerListQuery(string? Search, ManagerStatusFilter Status, int Page, int PageSize = ManagerService.DefaultPageSize);

public sealed record ManagerSummary(
    string Id,
    string FullName,
    string Email,
    bool IsActive,
    bool MustChangePassword,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

public enum ManagerResultStatus
{
    Success,
    NotFound,
    DuplicateEmail,
    CannotChangeSelf,
    Invalid,
}

/// <summary>
/// Outcome of a Manager command. <see cref="TemporaryPassword"/> is set only by create and reset; callers show it
/// once and must never log it or put it in TempData.
/// </summary>
public sealed record ManagerResult(ManagerResultStatus Status, string? UserId = null, string? TemporaryPassword = null, IReadOnlyList<string>? Errors = null)
{
    public bool Succeeded => Status == ManagerResultStatus.Success;
}

/// <summary>Admin-only management of Manager-role accounts. Users in other roles are invisible here.</summary>
public sealed class ManagerService(
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    ITemporaryPasswordGenerator passwordGenerator,
    IClock clock,
    ILoggerFactory loggerFactory)
{
    public const int DefaultPageSize = 20;

    private readonly ILogger _log = loggerFactory.CreateLogger(SecurityLog.Category);

    public async Task<PagedResult<ManagerSummary>> ListAsync(ManagerListQuery query, CancellationToken cancellationToken = default)
    {
        var managers = ManagersQuery();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            managers = managers.Where(u => u.FullName.Contains(term) || u.Email!.Contains(term));
        }

        managers = query.Status switch
        {
            ManagerStatusFilter.Active => managers.Where(u => u.IsActive),
            ManagerStatusFilter.Inactive => managers.Where(u => !u.IsActive),
            _ => managers,
        };

        var total = await managers.CountAsync(cancellationToken);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        var page = Math.Clamp(query.Page, 1, lastPage);

        var items = await managers
            .OrderBy(u => u.FullName).ThenBy(u => u.Email)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new ManagerSummary(u.Id, u.FullName, u.Email!, u.IsActive, u.MustChangePassword, u.CreatedAt, u.LastLoginAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<ManagerSummary>(items, page, pageSize, total);
    }

    public Task<ManagerSummary?> FindAsync(string id, CancellationToken cancellationToken = default) =>
        ManagersQuery()
            .Where(u => u.Id == id)
            .Select(u => new ManagerSummary(u.Id, u.FullName, u.Email!, u.IsActive, u.MustChangePassword, u.CreatedAt, u.LastLoginAt))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<ManagerResult> CreateAsync(string fullName, string email, string actorId, CancellationToken cancellationToken = default)
    {
        email = email.Trim();
        if (await EmailTakenAsync(email, exceptUserId: null, cancellationToken))
        {
            return new ManagerResult(ManagerResultStatus.DuplicateEmail);
        }

        var temporaryPassword = passwordGenerator.Generate();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = fullName.Trim(),
            IsActive = true,
            CreatedAt = clock.UtcNow,
            MustChangePassword = true,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var created = await userManager.CreateAsync(user, temporaryPassword);
        if (!created.Succeeded)
        {
            return Failure(created);
        }

        var roleAdded = await userManager.AddToRoleAsync(user, AppRoles.Manager);
        if (!roleAdded.Succeeded)
        {
            return Failure(roleAdded);
        }

        await transaction.CommitAsync(cancellationToken);
        SecurityLog.ManagerCreated(_log, actorId, user.Id);
        return new ManagerResult(ManagerResultStatus.Success, user.Id, temporaryPassword);
    }

    public async Task<ManagerResult> UpdateAsync(string id, string fullName, string email, string actorId, CancellationToken cancellationToken = default)
    {
        var user = await FindManagerEntityAsync(id, cancellationToken);
        if (user is null)
        {
            return new ManagerResult(ManagerResultStatus.NotFound);
        }

        email = email.Trim();
        var emailChanged = !string.Equals(userManager.NormalizeEmail(email), user.NormalizedEmail, StringComparison.Ordinal);
        if (emailChanged && await EmailTakenAsync(email, exceptUserId: user.Id, cancellationToken))
        {
            return new ManagerResult(ManagerResultStatus.DuplicateEmail);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        user.FullName = fullName.Trim();
        if (emailChanged)
        {
            // The email is the username. Both setters update the security stamp, which ends existing sessions.
            var setEmail = await userManager.SetEmailAsync(user, email);
            if (!setEmail.Succeeded)
            {
                return Failure(setEmail);
            }

            user.EmailConfirmed = true;
            var setUserName = await userManager.SetUserNameAsync(user, email);
            if (!setUserName.Succeeded)
            {
                return Failure(setUserName);
            }
        }

        var updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            return Failure(updated);
        }

        await transaction.CommitAsync(cancellationToken);
        SecurityLog.ManagerEdited(_log, actorId, user.Id, emailChanged);
        return new ManagerResult(ManagerResultStatus.Success, user.Id);
    }

    public async Task<ManagerResult> SetActiveAsync(string id, bool active, string actorId, CancellationToken cancellationToken = default)
    {
        if (!active && string.Equals(id, actorId, StringComparison.Ordinal))
        {
            SecurityLog.SelfDeactivationBlocked(_log, actorId);
            return new ManagerResult(ManagerResultStatus.CannotChangeSelf);
        }

        var user = await FindManagerEntityAsync(id, cancellationToken);
        if (user is null)
        {
            return new ManagerResult(ManagerResultStatus.NotFound);
        }

        if (user.IsActive == active)
        {
            return new ManagerResult(ManagerResultStatus.Success, user.Id);
        }

        user.IsActive = active;
        var updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            return Failure(updated);
        }

        if (!active)
        {
            // A new stamp invalidates every open session at the next cookie validation.
            await userManager.UpdateSecurityStampAsync(user);
            SecurityLog.ManagerDeactivated(_log, actorId, user.Id);
        }
        else
        {
            SecurityLog.ManagerActivated(_log, actorId, user.Id);
        }

        return new ManagerResult(ManagerResultStatus.Success, user.Id);
    }

    public async Task<ManagerResult> ResetPasswordAsync(string id, string actorId, CancellationToken cancellationToken = default)
    {
        var user = await FindManagerEntityAsync(id, cancellationToken);
        if (user is null)
        {
            return new ManagerResult(ManagerResultStatus.NotFound);
        }

        var temporaryPassword = passwordGenerator.Generate();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var removed = await userManager.RemovePasswordAsync(user);
        if (!removed.Succeeded)
        {
            return Failure(removed);
        }

        var added = await userManager.AddPasswordAsync(user, temporaryPassword);
        if (!added.Succeeded)
        {
            return Failure(added);
        }

        user.MustChangePassword = true;
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        var updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            return Failure(updated);
        }

        // Ends existing sessions (AddPasswordAsync already rotates the stamp; this makes the intent explicit).
        await userManager.UpdateSecurityStampAsync(user);

        await transaction.CommitAsync(cancellationToken);
        SecurityLog.ManagerPasswordReset(_log, actorId, user.Id);
        return new ManagerResult(ManagerResultStatus.Success, user.Id, temporaryPassword);
    }

    private IQueryable<ApplicationUser> ManagersQuery()
    {
        var managerRoleIds = db.Roles.Where(r => r.Name == AppRoles.Manager).Select(r => r.Id);
        return db.Users.Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id && managerRoleIds.Contains(ur.RoleId)));
    }

    private Task<ApplicationUser?> FindManagerEntityAsync(string id, CancellationToken cancellationToken) =>
        ManagersQuery().SingleOrDefaultAsync(u => u.Id == id, cancellationToken);

    private Task<bool> EmailTakenAsync(string email, string? exceptUserId, CancellationToken cancellationToken)
    {
        var normalized = userManager.NormalizeEmail(email);
        return db.Users.AnyAsync(
            u => (u.NormalizedEmail == normalized || u.NormalizedUserName == normalized) && u.Id != exceptUserId,
            cancellationToken);
    }

    private static ManagerResult Failure(IdentityResult result)
    {
        var duplicate = result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName));
        return duplicate
            ? new ManagerResult(ManagerResultStatus.DuplicateEmail)
            : new ManagerResult(ManagerResultStatus.Invalid, Errors: result.Errors.Select(e => e.Description).ToList());
    }
}
