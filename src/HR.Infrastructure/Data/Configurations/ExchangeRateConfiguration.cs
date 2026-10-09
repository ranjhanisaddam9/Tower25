using HR.Domain.Rates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HR.Infrastructure.Data.Configurations;

internal sealed class ExchangeRateConfiguration : IEntityTypeConfiguration<ExchangeRate>
{
    public const string EffectiveFromIndex = "UX_ExchangeRates_EffectiveFrom";

    public void Configure(EntityTypeBuilder<ExchangeRate> builder)
    {
        builder.ToTable("ExchangeRates", table =>
            table.HasCheckConstraint("CK_ExchangeRates_UsdToPkrRange", "[UsdToPkr] >= 100.0000 AND [UsdToPkr] <= 1000.0000"));

        builder.HasKey(r => r.Id);
        builder.HasIndex(r => r.EffectiveFrom).IsUnique().HasDatabaseName(EffectiveFromIndex);
        builder.Property(r => r.UsdToPkr).HasPrecision(18, 4);
        builder.Property(r => r.Note).HasMaxLength(ExchangeRateRules.NoteMaxLength);
        builder.Property(r => r.CreatedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(r => r.UpdatedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(r => r.RowVersion).IsRowVersion();
    }
}
