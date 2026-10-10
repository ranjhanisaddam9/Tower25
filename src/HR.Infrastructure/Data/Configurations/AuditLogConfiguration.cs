using HR.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HR.Infrastructure.Data.Configurations;

/// <summary>The append-only audit trail (M10). The trigger that refuses UPDATE and DELETE is created by the AddAuditLog migration.</summary>
public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public const string Table = "AuditLog";
    public const string AppendOnlyTrigger = "TR_AuditLog_AppendOnly";

    /// <summary>The error number THROWn by <see cref="AppendOnlyTrigger"/>.</summary>
    public const int AppendOnlyErrorNumber = 51030;

    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable(Table, t => t.HasTrigger(AppendOnlyTrigger));
        builder.HasKey(a => a.Id);
        builder.Property(a => a.ActorUserId).HasMaxLength(450);
        builder.Property(a => a.ActorName).HasMaxLength(200);
        builder.Property(a => a.ActorIp).HasMaxLength(64);
        builder.Property(a => a.EventName).HasMaxLength(80).IsRequired();
        builder.Property(a => a.EntityType).HasMaxLength(40);
        builder.Property(a => a.EntityId).HasMaxLength(64);
        builder.Property(a => a.Summary).HasMaxLength(AuditLogEntry.SummaryMaxLength).IsRequired();
        builder.HasIndex(a => a.At);
        builder.HasIndex(a => new { a.EventId, a.At });
        builder.HasIndex(a => new { a.ActorUserId, a.At });
        builder.HasIndex(a => new { a.EntityType, a.EntityId });
    }
}
