using HR.Domain.Pay;
using HR.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HR.Infrastructure.Data.Configurations;

internal sealed class RateRecordConfiguration : IEntityTypeConfiguration<RateRecord>
{
    public const string EffectiveFromIndex = "UX_RateRecords_PersonId_EffectiveFrom";

    public void Configure(EntityTypeBuilder<RateRecord> builder)
    {
        builder.ToTable("RateRecords", table =>
        {
            table.HasCheckConstraint("CK_RateRecords_EffectiveFromPeriodStart", "DAY([EffectiveFrom]) IN (1, 16)");
            table.HasCheckConstraint("CK_RateRecords_Amounts",
                "[BilledMonthlyUsd] > 0 AND [PayMonthlyAmount] > 0 AND [CommissionPerPeriodUsd] >= 0");
            table.HasCheckConstraint("CK_RateRecords_PkrWholeRupees",
                "[PayCurrency] <> 'PKR' OR [PayMonthlyAmount] = ROUND([PayMonthlyAmount], 0)");
        });

        builder.HasKey(r => r.Id);
        builder.Ignore(r => r.Terms);

        builder.HasOne<Person>().WithMany().HasForeignKey(r => r.PersonId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(r => new { r.PersonId, r.EffectiveFrom }).IsUnique().HasDatabaseName(EffectiveFromIndex);
        builder.HasIndex(r => r.NeedsBillingReview).HasFilter("[NeedsBillingReview] = 1");

        builder.Property(r => r.BilledMonthlyUsd).HasPrecision(18, 2);
        builder.Property(r => r.CommissionPerPeriodUsd).HasPrecision(18, 2);
        builder.Property(r => r.PayMonthlyAmount).HasPrecision(18, 2);
        builder.Property(r => r.PayCurrency).HasConversion<string>().HasMaxLength(3).IsUnicode(false);
        builder.Property(r => r.ChangeType).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        builder.Property(r => r.Note).HasMaxLength(RateRecord.NoteMaxLength);
        builder.Property(r => r.CreatedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(r => r.UpdatedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(r => r.RowVersion).IsRowVersion();
    }
}
