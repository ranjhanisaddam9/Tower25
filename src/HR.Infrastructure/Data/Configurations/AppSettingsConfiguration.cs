using HR.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HR.Infrastructure.Data.Configurations;

internal sealed class AppSettingsConfiguration : IEntityTypeConfiguration<AppSettings>
{
    public void Configure(EntityTypeBuilder<AppSettings> builder)
    {
        builder.ToTable("AppSettings", table =>
        {
            // A single row.
            table.HasCheckConstraint("CK_AppSettings_Singleton", "[Id] = 1");
            table.HasCheckConstraint("CK_AppSettings_Terms", "[PaymentTermsDays] BETWEEN 0 AND 120");
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Ignore(s => s.CanIssueInvoices);

        foreach (var (field, max) in SettingsRules.MaxLengths)
        {
            builder.Property(field).HasMaxLength(max);
        }

        builder.Property(s => s.InvoicePrefix).IsUnicode(false).IsRequired();
        builder.Property(s => s.BankSwift).IsUnicode(false);
        builder.Property(s => s.PayslipIssuerName).IsRequired();
        builder.Property(s => s.UpdatedByUserId).HasMaxLength(450);
        builder.Property(s => s.RowVersion).IsRowVersion();
    }
}
