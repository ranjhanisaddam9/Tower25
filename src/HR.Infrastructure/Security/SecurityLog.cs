using Microsoft.Extensions.Logging;

namespace HR.Infrastructure.Security;

/// <summary>
/// Structured security events (CLAUDE.md: log logins, lockouts and account changes; never passwords or tokens).
/// Every event carries who acted and the target user id where one exists.
/// </summary>
public static partial class SecurityLog
{
    public const string Category = "HR.Security";

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "Security: login succeeded for user {UserId} from {ClientIp}")]
    public static partial void LoginSucceeded(ILogger logger, string userId, string clientIp);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Security: login failed ({Reason}) for user {UserId} from {ClientIp}")]
    public static partial void LoginFailed(ILogger logger, string reason, string userId, string clientIp);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Security: user {UserId} is locked out (login from {ClientIp})")]
    public static partial void LockedOut(ILogger logger, string userId, string clientIp);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Security: login rate limit exceeded from {ClientIp}")]
    public static partial void LoginRateLimited(ILogger logger, string clientIp);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Warning, Message = "Security: change-password rate limit exceeded for {Partition} from {ClientIp}")]
    public static partial void ChangePasswordRateLimited(ILogger logger, string partition, string clientIp);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information, Message = "Security: user {UserId} signed out")]
    public static partial void LoggedOut(ILogger logger, string userId);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information, Message = "Security: user {UserId} changed their password")]
    public static partial void PasswordChanged(ILogger logger, string userId);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Information, Message = "Security: {ActorId} created Manager {TargetUserId}")]
    public static partial void ManagerCreated(ILogger logger, string actorId, string targetUserId);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Information, Message = "Security: {ActorId} edited Manager {TargetUserId} (email changed: {EmailChanged})")]
    public static partial void ManagerEdited(ILogger logger, string actorId, string targetUserId, bool emailChanged);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Information, Message = "Security: {ActorId} activated Manager {TargetUserId}")]
    public static partial void ManagerActivated(ILogger logger, string actorId, string targetUserId);

    [LoggerMessage(EventId = 1013, Level = LogLevel.Warning, Message = "Security: {ActorId} deactivated Manager {TargetUserId}")]
    public static partial void ManagerDeactivated(ILogger logger, string actorId, string targetUserId);

    [LoggerMessage(EventId = 1014, Level = LogLevel.Warning, Message = "Security: {ActorId} reset the password of Manager {TargetUserId}")]
    public static partial void ManagerPasswordReset(ILogger logger, string actorId, string targetUserId);

    [LoggerMessage(EventId = 1015, Level = LogLevel.Warning, Message = "Security: {ActorId} tried to deactivate their own account")]
    public static partial void SelfDeactivationBlocked(ILogger logger, string actorId);

    [LoggerMessage(EventId = 1020, Level = LogLevel.Information, Message = "Security: seeded Admin user {UserId}")]
    public static partial void AdminSeeded(ILogger logger, string userId);

    [LoggerMessage(EventId = 1021, Level = LogLevel.Warning, Message = "Security: Admin seeding skipped: {Reason}")]
    public static partial void AdminSeedSkipped(ILogger logger, string reason);

    // ---- People (audit). Never CNIC, IBAN or phone numbers. ----

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information, Message = "Audit: {ActorId} created person {PersonId} ({PersonCode})")]
    public static partial void PersonCreated(ILogger logger, string actorId, int personId, string personCode);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Information, Message = "Audit: {ActorId} edited person {PersonId} ({PersonCode})")]
    public static partial void PersonEdited(ILogger logger, string actorId, int personId, string personCode);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Information, Message = "Audit: {ActorId} deactivated person {PersonId} ({PersonCode}) with leaving date {LeavingDate}")]
    public static partial void PersonDeactivated(ILogger logger, string actorId, int personId, string personCode, DateOnly leavingDate);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Information, Message = "Audit: {ActorId} reactivated person {PersonId} ({PersonCode}) from {RejoiningDate}; previous joining {PreviousJoiningDate}, previous leaving {PreviousLeavingDate}")]
    public static partial void PersonReactivated(ILogger logger, string actorId, int personId, string personCode, DateOnly rejoiningDate, DateOnly previousJoiningDate, DateOnly? previousLeavingDate);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Information, Message = "Audit: {ActorId} changed the hire source of person {PersonId} ({PersonCode}) from {PreviousSource} to {NewSource}")]
    public static partial void PersonHireSourceChanged(ILogger logger, string actorId, int personId, string personCode, string previousSource, string newSource);

    // ---- Exchange rates (audit) ----

    [LoggerMessage(EventId = 1200, Level = LogLevel.Information, Message = "Audit: {ActorId} added exchange rate {RateId}: {NewEffectiveFrom} = {NewUsdToPkr}")]
    public static partial void ExchangeRateCreated(ILogger logger, string actorId, int rateId, DateOnly newEffectiveFrom, decimal newUsdToPkr);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information, Message = "Audit: {ActorId} edited exchange rate {RateId}: {OldEffectiveFrom} = {OldUsdToPkr} -> {NewEffectiveFrom} = {NewUsdToPkr}")]
    public static partial void ExchangeRateEdited(ILogger logger, string actorId, int rateId, DateOnly oldEffectiveFrom, decimal oldUsdToPkr, DateOnly newEffectiveFrom, decimal newUsdToPkr);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Warning, Message = "Audit: {ActorId} deleted exchange rate {RateId}: {OldEffectiveFrom} = {OldUsdToPkr}")]
    public static partial void ExchangeRateDeleted(ILogger logger, string actorId, int rateId, DateOnly oldEffectiveFrom, decimal oldUsdToPkr);

    [LoggerMessage(EventId = 1110, Level = LogLevel.Information, Message = "Demo data: seeded {Count} people")]
    public static partial void DemoDataSeeded(ILogger logger, int count);
}
