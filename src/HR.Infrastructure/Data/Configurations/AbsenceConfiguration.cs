using HR.Domain.Absences;
using HR.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HR.Infrastructure.Data.Configurations;

internal sealed class AbsenceConfiguration : IEntityTypeConfiguration<Absence>
{
    public const string DateIndex = "UX_Absences_PersonId_Date";
    public const string WeekdayConstraint = "CK_Absences_Weekday";

    public void Configure(EntityTypeBuilder<Absence> builder)
    {
        builder.ToTable("Absences", table =>
        {
            // Monday–Friday whatever DATEFIRST is: 1900-01-01 was a Monday, so days since then mod 7 is 0 (Mon) to 6 (Sun).
            table.HasCheckConstraint(WeekdayConstraint, "(DATEDIFF(day, '19000101', [Date]) % 7) < 5");
        });

        builder.HasKey(a => a.Id);
        builder.HasOne<Person>().WithMany().HasForeignKey(a => a.PersonId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => new { a.PersonId, a.Date }).IsUnique().HasDatabaseName(DateIndex);
        builder.HasIndex(a => a.Date);

        builder.Property(a => a.Portion).HasConversion<string>().HasMaxLength(8).IsUnicode(false);
        builder.Property(a => a.Note).HasMaxLength(Absence.NoteMaxLength);
        builder.Property(a => a.CreatedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(a => a.UpdatedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(a => a.RowVersion).IsRowVersion();
    }
}
