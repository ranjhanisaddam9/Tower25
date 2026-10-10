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

    // ---- Rate records (audit; server-side logs only) ----

    [LoggerMessage(EventId = 1300, Level = LogLevel.Information, Message = "Audit: {ActorId} added rate record {RecordId} for person {PersonId} ({ChangeType}): {EffectiveFrom} billed {BilledMonthlyUsd} USD, commission {CommissionPerPeriodUsd} USD, pay {PayMonthlyAmount} {PayCurrency}")]
    public static partial void RateRecordCreated(ILogger logger, string actorId, int recordId, int personId, string changeType, DateOnly effectiveFrom, decimal billedMonthlyUsd, decimal commissionPerPeriodUsd, decimal payMonthlyAmount, string payCurrency);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Information, Message = "Audit: {ActorId} edited rate record {RecordId} for person {PersonId}: {OldValues} -> {NewValues} ({ChangeType})")]
    public static partial void RateRecordEdited(ILogger logger, string actorId, int recordId, int personId, string oldValues, string newValues, string changeType);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Warning, Message = "Audit: {ActorId} deleted rate record {RecordId} for person {PersonId}: {OldValues}")]
    public static partial void RateRecordDeleted(ILogger logger, string actorId, int recordId, int personId, string oldValues);

    [LoggerMessage(EventId = 1303, Level = LogLevel.Information, Message = "Audit: {ActorId} marked the billing of rate record {RecordId} for person {PersonId} as reviewed")]
    public static partial void RateRecordReviewed(ILogger logger, string actorId, int recordId, int personId);

    // ---- Absences (audit; never the note) ----

    [LoggerMessage(EventId = 1400, Level = LogLevel.Information, Message = "Audit: {ActorId} added absence {AbsenceId} for person {PersonId} on {Date}: {Portion}")]
    public static partial void AbsenceCreated(ILogger logger, string actorId, int absenceId, int personId, DateOnly date, string portion);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Information, Message = "Audit: {ActorId} edited absence {AbsenceId} for person {PersonId} on {Date}: {OldPortion} -> {NewPortion}")]
    public static partial void AbsenceEdited(ILogger logger, string actorId, int absenceId, int personId, DateOnly date, string oldPortion, string newPortion);

    [LoggerMessage(EventId = 1402, Level = LogLevel.Warning, Message = "Audit: {ActorId} deleted absence {AbsenceId} for person {PersonId} on {Date}: {OldPortion} -> none")]
    public static partial void AbsenceDeleted(ILogger logger, string actorId, int absenceId, int personId, DateOnly date, string oldPortion);

    [LoggerMessage(EventId = 1410, Level = LogLevel.Information, Message = "Demo data: seeded {Count} absences")]
    public static partial void DemoAbsencesSeeded(ILogger logger, int count);

    [LoggerMessage(EventId = 1105, Level = LogLevel.Information, Message = "Audit: {ActorId} cancelled the leaving date {LeavingDate} of person {PersonId} ({PersonCode})")]
    public static partial void PersonLeavingCancelled(ILogger logger, string actorId, int personId, string personCode, DateOnly leavingDate);

    // ---- Payroll (audit; never notes or the reopen reason) ----

    [LoggerMessage(EventId = 1500, Level = LogLevel.Information, Message = "Audit: {ActorId} generated payroll {RunId} for {PeriodStart} with {LineCount} lines, rate {Rate}")]
    public static partial void PayrollGenerated(ILogger logger, string actorId, int runId, DateOnly periodStart, int lineCount, string rate);

    [LoggerMessage(EventId = 1501, Level = LogLevel.Information, Message = "Audit: {ActorId} regenerated payroll {RunId}: {LineCount} lines, {ChangedCount} changed, {OrphanCount} orphaned")]
    public static partial void PayrollRegenerated(ILogger logger, string actorId, int runId, int lineCount, int changedCount, int orphanCount);

    [LoggerMessage(EventId = 1502, Level = LogLevel.Information, Message = "Audit: {ActorId} changed the rate of payroll {RunId}: {OldRate} -> {NewRate} (overridden {Overridden})")]
    public static partial void PayrollRateChanged(ILogger logger, string actorId, int runId, string oldRate, string newRate, bool overridden);

    [LoggerMessage(EventId = 1503, Level = LogLevel.Information, Message = "Audit: {ActorId} set extra days on payroll {RunId} line {LineId} (person {PersonId}): {OldDays} -> {NewDays}")]
    public static partial void PayrollExtraDaysChanged(ILogger logger, string actorId, int runId, int lineId, int personId, decimal oldDays, decimal newDays);

    [LoggerMessage(EventId = 1504, Level = LogLevel.Information, Message = "Audit: {ActorId} added adjustment {AdjustmentId} to payroll {RunId} line {LineId}: {NewValues}")]
    public static partial void PayrollAdjustmentAdded(ILogger logger, string actorId, int runId, int lineId, int adjustmentId, string newValues);

    [LoggerMessage(EventId = 1505, Level = LogLevel.Information, Message = "Audit: {ActorId} edited adjustment {AdjustmentId} on payroll {RunId} line {LineId}: {OldValues} -> {NewValues}")]
    public static partial void PayrollAdjustmentEdited(ILogger logger, string actorId, int runId, int lineId, int adjustmentId, string oldValues, string newValues);

    [LoggerMessage(EventId = 1506, Level = LogLevel.Warning, Message = "Audit: {ActorId} deleted adjustment {AdjustmentId} from payroll {RunId} line {LineId}: {OldValues}")]
    public static partial void PayrollAdjustmentDeleted(ILogger logger, string actorId, int runId, int lineId, int adjustmentId, string oldValues);

    [LoggerMessage(EventId = 1507, Level = LogLevel.Warning, Message = "Security: {ActorId} finalized payroll {RunId} for {PeriodStart}: {LineCount} lines, net pay {NetPayPkr} PKR, rate {Rate}")]
    public static partial void PayrollFinalized(ILogger logger, string actorId, int runId, DateOnly periodStart, int lineCount, decimal netPayPkr, decimal rate);

    [LoggerMessage(EventId = 1508, Level = LogLevel.Warning, Message = "Audit: {ActorId} could not finalize payroll {RunId}: {Reason}")]
    public static partial void PayrollFinalizeRefused(ILogger logger, string actorId, int runId, string reason);

    [LoggerMessage(EventId = 1509, Level = LogLevel.Warning, Message = "Security: {ActorId} reopened payroll {RunId} for {PeriodStart}")]
    public static partial void PayrollReopened(ILogger logger, string actorId, int runId, DateOnly periodStart);

    [LoggerMessage(EventId = 1510, Level = LogLevel.Warning, Message = "Audit: {ActorId} deleted draft payroll {RunId} for {PeriodStart}")]
    public static partial void PayrollDeleted(ILogger logger, string actorId, int runId, DateOnly periodStart);

    [LoggerMessage(EventId = 1110, Level = LogLevel.Information, Message = "Demo data: seeded {Count} people")]
    public static partial void DemoDataSeeded(ILogger logger, int count);
}
