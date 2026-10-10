using HR.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HR.Infrastructure.Data.Configurations;

internal sealed class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public const string CodeSequence = "PersonCodeSequence";
    public const string ActiveOwnerTrigger = "TR_People_SingleActiveOwner";
    public const string EmailIndex = "UX_People_Email";
    public const string CnicIndex = "UX_People_Cnic";
    public const string CodeIndex = "UX_People_Code";

    public void Configure(EntityTypeBuilder<Person> builder)
    {
        builder.ToTable("People", table =>
        {
            table.HasCheckConstraint("CK_People_LeavingAfterJoining", "[LeavingDate] IS NULL OR [LeavingDate] >= [JoiningDate]");

            // SPEC §2: at most one active Owner. "Active" depends on today, so a filtered index can't express it; the
            // service checks it and this trigger is the safety net (THROW 51002).
            table.HasTrigger(ActiveOwnerTrigger);
        });

        builder.HasKey(p => p.Id);

        // Immutable once saved: EF throws if code ever tries to change them.
        builder.Property(p => p.CodeNumber).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(p => p.Code).HasMaxLength(PersonCode.MaxLength).IsUnicode(false).IsRequired()
            .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasIndex(p => p.Code).IsUnique().HasDatabaseName(CodeIndex);
        builder.HasIndex(p => p.CodeNumber).IsUnique();

        builder.Property(p => p.FullName).HasMaxLength(PersonInput.FullNameMaxLength).IsRequired();
        builder.Property(p => p.Type).HasConversion<string>().HasMaxLength(16).IsUnicode(false).IsRequired();
        builder.Property(p => p.Designation).HasMaxLength(PersonInput.DesignationMaxLength).IsRequired();

        builder.Property(p => p.Email).HasMaxLength(PersonInput.EmailMaxLength);
        builder.HasIndex(p => p.Email).IsUnique().HasFilter("[Email] IS NOT NULL").HasDatabaseName(EmailIndex);

        builder.Property(p => p.Phone).HasMaxLength(PakistaniPhone.MaxLength).IsUnicode(false).IsRequired();

        builder.Property(p => p.Cnic).HasMaxLength(Cnic.Length).IsUnicode(false);
        builder.HasIndex(p => p.Cnic).IsUnique().HasFilter("[Cnic] IS NOT NULL").HasDatabaseName(CnicIndex);

        builder.Property(p => p.BankName).HasMaxLength(PersonInput.BankNameMaxLength);
        builder.Property(p => p.Iban).HasMaxLength(PakistaniIban.Length).IsUnicode(false);

        builder.Property(p => p.HireSource).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        builder.HasIndex(p => p.HireSource);

        builder.Property(p => p.Notes).HasMaxLength(PersonInput.NotesMaxLength);

        builder.Property(p => p.CreatedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(p => p.UpdatedByUserId).HasMaxLength(450).IsRequired();

        builder.Property(p => p.RowVersion).IsRowVersion();

        // Employment periods are owned by the person; JoiningDate/LeavingDate cache the latest period.
        builder.HasMany(p => p.EmploymentPeriods)
            .WithOne()
            .HasForeignKey(e => e.PersonId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.EmploymentPeriods).HasField("_periods").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(p => p.FullName);
        builder.HasIndex(p => p.LeavingDate);
        builder.HasIndex(p => p.JoiningDate);
    }
}
