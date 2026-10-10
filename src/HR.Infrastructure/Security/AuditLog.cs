using HR.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace HR.Infrastructure.Security;

/// <summary>
/// One row of the durable, append-only audit trail (M10). <see cref="Summary"/> is non-sensitive by construction: ids,
/// codes, periods, field names and old → new values that are not personal data. Never passwords, tokens, CNIC, IBAN,
/// phone numbers, notes or reasons (the reopen and void reasons stay in the payroll's own history, SPEC §6).
/// UPDATE and DELETE are refused by the TR_AuditLog_AppendOnly trigger.
/// </summary>
public sealed class AuditLogEntry
{
    public const int SummaryMaxLength = 1000;

    public long Id { get; set; }

    /// <summary>UTC.</summary>
    public DateTimeOffset At { get; set; }

    public string? ActorUserId { get; set; }

    public string? ActorName { get; set; }

    public string? ActorIp { get; set; }

    public int EventId { get; set; }

    public string EventName { get; set; } = string.Empty;

    public string? EntityType { get; set; }

    public string? EntityId { get; set; }

    public string Summary { get; set; } = string.Empty;
}

/// <summary>An audit event: the same id as its <see cref="SecurityLog"/> message.</summary>
public sealed record AuditEvent(int Id, string Name);

/// <summary>Every audited event (1000–17xx), mirrored from <see cref="SecurityLog"/>.</summary>
public static class AuditEvents
{
    // Authentication and accounts (10xx)
    public static readonly AuditEvent LoginSucceeded = new(1000, nameof(LoginSucceeded));
    public static readonly AuditEvent LoginFailed = new(1001, nameof(LoginFailed));
    public static readonly AuditEvent LockedOut = new(1002, nameof(LockedOut));
    public static readonly AuditEvent LoginRateLimited = new(1003, nameof(LoginRateLimited));
    public static readonly AuditEvent LoggedOut = new(1004, nameof(LoggedOut));
    public static readonly AuditEvent PasswordChanged = new(1005, nameof(PasswordChanged));
    public static readonly AuditEvent ChangePasswordRateLimited = new(1006, nameof(ChangePasswordRateLimited));
    public static readonly AuditEvent RequestRateLimited = new(1007, nameof(RequestRateLimited));
    public static readonly AuditEvent ManagerCreated = new(1010, nameof(ManagerCreated));
    public static readonly AuditEvent ManagerEdited = new(1011, nameof(ManagerEdited));
    public static readonly AuditEvent ManagerActivated = new(1012, nameof(ManagerActivated));
    public static readonly AuditEvent ManagerDeactivated = new(1013, nameof(ManagerDeactivated));
    public static readonly AuditEvent ManagerPasswordReset = new(1014, nameof(ManagerPasswordReset));
    public static readonly AuditEvent SelfDeactivationBlocked = new(1015, nameof(SelfDeactivationBlocked));
    public static readonly AuditEvent ManagerTwoFactorRequirementChanged = new(1016, nameof(ManagerTwoFactorRequirementChanged));
    public static readonly AuditEvent ManagerTwoFactorReset = new(1017, nameof(ManagerTwoFactorReset));
    public static readonly AuditEvent AdminSeeded = new(1020, nameof(AdminSeeded));
    public static readonly AuditEvent AdminSeedSkipped = new(1021, nameof(AdminSeedSkipped));
    public static readonly AuditEvent TwoFactorEnabled = new(1030, nameof(TwoFactorEnabled));
    public static readonly AuditEvent TwoFactorFailed = new(1031, nameof(TwoFactorFailed));
    public static readonly AuditEvent RecoveryCodeUsed = new(1032, nameof(RecoveryCodeUsed));
    public static readonly AuditEvent RecoveryCodesGenerated = new(1033, nameof(RecoveryCodesGenerated));
    public static readonly AuditEvent TwoFactorDisabled = new(1034, nameof(TwoFactorDisabled));
    public static readonly AuditEvent AdminResetCommand = new(1040, nameof(AdminResetCommand));
    public static readonly AuditEvent AuditPurged = new(1041, nameof(AuditPurged));

    // People (11xx)
    public static readonly AuditEvent PersonCreated = new(1100, nameof(PersonCreated));
    public static readonly AuditEvent PersonEdited = new(1101, nameof(PersonEdited));
    public static readonly AuditEvent PersonDeactivated = new(1102, nameof(PersonDeactivated));
    public static readonly AuditEvent PersonReactivated = new(1103, nameof(PersonReactivated));
    public static readonly AuditEvent PersonHireSourceChanged = new(1104, nameof(PersonHireSourceChanged));
    public static readonly AuditEvent PersonLeavingCancelled = new(1105, nameof(PersonLeavingCancelled));
    public static readonly AuditEvent PersonLateAdditionConfirmed = new(1106, nameof(PersonLateAdditionConfirmed));

    // Exchange rates (12xx), rate records (13xx), absences (14xx)
    public static readonly AuditEvent ExchangeRateCreated = new(1200, nameof(ExchangeRateCreated));
    public static readonly AuditEvent ExchangeRateEdited = new(1201, nameof(ExchangeRateEdited));
    public static readonly AuditEvent ExchangeRateDeleted = new(1202, nameof(ExchangeRateDeleted));
    public static readonly AuditEvent RateRecordCreated = new(1300, nameof(RateRecordCreated));
    public static readonly AuditEvent RateRecordEdited = new(1301, nameof(RateRecordEdited));
    public static readonly AuditEvent RateRecordDeleted = new(1302, nameof(RateRecordDeleted));
    public static readonly AuditEvent RateRecordReviewed = new(1303, nameof(RateRecordReviewed));
    public static readonly AuditEvent AbsenceCreated = new(1400, nameof(AbsenceCreated));
    public static readonly AuditEvent AbsenceEdited = new(1401, nameof(AbsenceEdited));
    public static readonly AuditEvent AbsenceDeleted = new(1402, nameof(AbsenceDeleted));

    // Payroll (15xx)
    public static readonly AuditEvent PayrollGenerated = new(1500, nameof(PayrollGenerated));
    public static readonly AuditEvent PayrollRegenerated = new(1501, nameof(PayrollRegenerated));
    public static readonly AuditEvent PayrollRateChanged = new(1502, nameof(PayrollRateChanged));
    public static readonly AuditEvent PayrollExtraDaysChanged = new(1503, nameof(PayrollExtraDaysChanged));
    public static readonly AuditEvent PayrollAdjustmentAdded = new(1504, nameof(PayrollAdjustmentAdded));
    public static readonly AuditEvent PayrollAdjustmentEdited = new(1505, nameof(PayrollAdjustmentEdited));
    public static readonly AuditEvent PayrollAdjustmentDeleted = new(1506, nameof(PayrollAdjustmentDeleted));
    public static readonly AuditEvent PayrollFinalized = new(1507, nameof(PayrollFinalized));
    public static readonly AuditEvent PayrollFinalizeRefused = new(1508, nameof(PayrollFinalizeRefused));
    public static readonly AuditEvent PayrollReopened = new(1509, nameof(PayrollReopened));
    public static readonly AuditEvent PayrollDeleted = new(1510, nameof(PayrollDeleted));

    // Settings and invoices (16xx), exports (17xx)
    public static readonly AuditEvent SettingsChanged = new(1600, nameof(SettingsChanged));
    public static readonly AuditEvent InvoiceIssued = new(1601, nameof(InvoiceIssued));
    public static readonly AuditEvent InvoicePaid = new(1602, nameof(InvoicePaid));
    public static readonly AuditEvent InvoiceUnpaid = new(1603, nameof(InvoiceUnpaid));
    public static readonly AuditEvent InvoiceVoided = new(1604, nameof(InvoiceVoided));
    public static readonly AuditEvent Exported = new(1700, nameof(Exported));

    public static IReadOnlyList<AuditEvent> All { get; } = typeof(AuditEvents)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.FieldType == typeof(AuditEvent))
        .Select(f => (AuditEvent)f.GetValue(null)!)
        .OrderBy(e => e.Id)
        .ToList();
}

/// <summary>Who is acting right now (the HTTP request's client IP). The web app supplies it; elsewhere it is null.</summary>
public interface IAuditContext
{
    string? ClientIp { get; }
}

/// <summary>
/// Writes audit rows for events that change no data (sign-in failures, rate limits, exports, refusals). Uses its own
/// context, so it never saves unrelated tracked changes of the caller.
/// </summary>
public sealed class AuditWriter(IServiceScopeFactory scopes)
{
    public async Task WriteAsync(AuditEvent auditEvent, string? actorUserId, string? entityType, object? entityId, string summary, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Audit(auditEvent, actorUserId, entityType, entityId, summary);
        await db.SaveChangesAsync(cancellationToken);
    }
}
