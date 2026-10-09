using HR.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HR.Infrastructure.Data.Configurations;

internal sealed class EmploymentPeriodConfiguration : IEntityTypeConfiguration<EmploymentPeriod>
{
    public const string OpenPeriodIndex = "UX_EmploymentPeriods_OnePerPersonOpen";
    public const string NoOverlapTrigger = "TR_EmploymentPeriods_NoOverlap";

    public void Configure(EntityTypeBuilder<EmploymentPeriod> builder)
    {
        builder.ToTable("EmploymentPeriods", table =>
        {
            table.HasCheckConstraint("CK_EmploymentPeriods_EndAfterStart", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
            // Overlaps can't be expressed as a constraint; the migration adds this trigger. Declaring it tells EF
            // not to use OUTPUT clauses, which SQL Server rejects on tables with triggers.
            table.HasTrigger(NoOverlapTrigger);
        });

        builder.HasKey(p => p.Id);
        builder.Ignore(p => p.Span);
        builder.Ignore(p => p.IsOpen);

        // At most one open period per person.
        builder.HasIndex(p => p.PersonId).IsUnique().HasFilter("[EndDate] IS NULL").HasDatabaseName(OpenPeriodIndex);
        builder.HasIndex(p => new { p.PersonId, p.StartDate }).IsUnique();

        builder.Property(p => p.CreatedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(p => p.UpdatedByUserId).HasMaxLength(450).IsRequired();
    }
}
