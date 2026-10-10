using HR.Domain.Invoices;
using HR.Domain.Payroll;
using HR.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HR.Infrastructure.Data.Configurations;

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public const string NumberIndex = "UX_Invoices_Number";
    public const string ActivePerRunIndex = "UX_Invoices_RunId_NotVoid";

    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("Invoices", table =>
        {
            table.HasCheckConstraint("CK_Invoices_Due", "[DueDate] >= [IssueDate]");
            table.HasCheckConstraint("CK_Invoices_Paid", "[Status] <> 'Paid' OR ([PaidDate] IS NOT NULL AND [AmountReceivedUsd] > 0)");
            table.HasCheckConstraint("CK_Invoices_Void", "[Status] <> 'Void' OR ([VoidedAt] IS NOT NULL AND [VoidReason] IS NOT NULL)");
        });

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Number).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.HasIndex(i => i.Number).IsUnique().HasDatabaseName(NumberIndex);
        // At most one invoice that isn't void per payroll run.
        builder.HasIndex(i => i.RunId).IsUnique().HasFilter("[Status] <> 'Void'").HasDatabaseName(ActivePerRunIndex);
        builder.HasOne<PayrollRun>().WithMany().HasForeignKey(i => i.RunId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Invoice>().WithMany().HasForeignKey(i => i.ReplacesInvoiceId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(8).IsUnicode(false);
        builder.Property(i => i.TotalUsd).HasPrecision(18, 2);
        builder.Property(i => i.AmountReceivedUsd).HasPrecision(18, 2);
        builder.Property(i => i.PaymentNote).HasMaxLength(Invoice.NoteMaxLength);
        builder.Property(i => i.VoidReason).HasMaxLength(500);
        builder.Property(i => i.VoidedByUserId).HasMaxLength(450);
        builder.Property(i => i.IssuedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(i => i.UpdatedByUserId).HasMaxLength(450).IsRequired();

        builder.Property(i => i.BusinessName).HasMaxLength(200).IsRequired();
        builder.Property(i => i.BusinessAddress).HasMaxLength(500);
        builder.Property(i => i.BusinessEmail).HasMaxLength(254);
        builder.Property(i => i.BusinessPhone).HasMaxLength(40);
        builder.Property(i => i.BankName).HasMaxLength(100);
        builder.Property(i => i.BankAccountTitle).HasMaxLength(150);
        builder.Property(i => i.BankAccountNumber).HasMaxLength(40);
        builder.Property(i => i.BankSwift).HasMaxLength(11).IsUnicode(false);
        builder.Property(i => i.ClientName).HasMaxLength(200).IsRequired();
        builder.Property(i => i.ClientAddress).HasMaxLength(500);
        builder.Property(i => i.ClientContactPerson).HasMaxLength(150);
        builder.Property(i => i.ClientEmail).HasMaxLength(254);
        builder.Property(i => i.Footer).HasMaxLength(500);
        builder.Property(i => i.RowVersion).IsRowVersion();

        builder.HasMany(i => i.Lines).WithOne().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(i => i.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(i => new { i.Status, i.DueDate });
    }
}

internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public const string FrozenTrigger = "TR_InvoiceLines_Frozen";

    public void Configure(EntityTypeBuilder<InvoiceLine> builder)
    {
        builder.ToTable("InvoiceLines", table => table.HasTrigger(FrozenTrigger));
        builder.HasKey(l => l.Id);
        builder.HasIndex(l => new { l.InvoiceId, l.Position }).IsUnique();
        builder.Property(l => l.Name).HasMaxLength(PersonInput.FullNameMaxLength).IsRequired();
        builder.Property(l => l.Designation).HasMaxLength(PersonInput.DesignationMaxLength).IsRequired();
        builder.Property(l => l.DaysText).HasMaxLength(16).IsUnicode(false).IsRequired();
        builder.Property(l => l.SalaryUsd).HasPrecision(18, 2);
        builder.Property(l => l.ExtrasUsd).HasPrecision(18, 2);
        builder.Property(l => l.AmountUsd).HasPrecision(18, 2);
    }
}

internal sealed class InvoiceCounterConfiguration : IEntityTypeConfiguration<InvoiceCounter>
{
    public void Configure(EntityTypeBuilder<InvoiceCounter> builder)
    {
        builder.ToTable("InvoiceCounters", table => table.HasCheckConstraint("CK_InvoiceCounters_Last", "[LastNumber] >= 0"));
        builder.HasKey(c => c.Year);
        builder.Property(c => c.Year).ValueGeneratedNever();
    }
}
