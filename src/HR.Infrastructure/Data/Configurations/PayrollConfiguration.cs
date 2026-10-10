using HR.Domain.Payroll;
using HR.Domain.People;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HR.Infrastructure.Data.Configurations;

internal sealed class PayrollRunConfiguration : IEntityTypeConfiguration<PayrollRun>
{
    public const string PeriodIndex = "UX_PayrollRuns_PeriodStart";
    public const string NoDeleteFinalizedTrigger = "TR_PayrollRuns_NoDeleteFinalized";

    public void Configure(EntityTypeBuilder<PayrollRun> builder)
    {
        builder.ToTable("PayrollRuns", table =>
        {
            table.HasCheckConstraint("CK_PayrollRuns_Period", "[PeriodEnd] >= [PeriodStart] AND DAY([PeriodStart]) IN (1, 16)");
            table.HasCheckConstraint("CK_PayrollRuns_Rate", "[ExchangeRate] IS NULL OR [ExchangeRate] > 0");
            table.HasCheckConstraint("CK_PayrollRuns_FinalizedHasRate",
                "[Status] <> 'Finalized' OR ([ExchangeRate] IS NOT NULL AND [FinalizedAt] IS NOT NULL)");
            table.HasTrigger(NoDeleteFinalizedTrigger);
        });

        builder.HasKey(r => r.Id);
        builder.Ignore(r => r.Period);
        builder.Ignore(r => r.IsDraft);
        builder.HasIndex(r => r.PeriodStart).IsUnique().HasDatabaseName(PeriodIndex);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        builder.Property(r => r.ExchangeRate).HasPrecision(18, 4);
        builder.Property(r => r.RateNote).HasMaxLength(PayrollRun.RateNoteMaxLength);
        builder.Property(r => r.GeneratedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(r => r.FinalizedByUserId).HasMaxLength(450);
        builder.Property(r => r.RowVersion).IsRowVersion();

        builder.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.RunId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(r => r.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(r => r.Events).WithOne().HasForeignKey(e => e.RunId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(r => r.Events).HasField("_events").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class PayrollRunEventConfiguration : IEntityTypeConfiguration<PayrollRunEvent>
{
    public void Configure(EntityTypeBuilder<PayrollRunEvent> builder)
    {
        builder.ToTable("PayrollRunEvents");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        builder.Property(e => e.Detail).HasMaxLength(PayrollRunEvent.DetailMaxLength);
        builder.Property(e => e.ActorId).HasMaxLength(450).IsRequired();
        builder.HasIndex(e => new { e.RunId, e.At });
    }
}

internal sealed class PayrollLineConfiguration : IEntityTypeConfiguration<PayrollLine>
{
    public const string PersonIndex = "UX_PayrollLines_RunId_PersonId";
    public const string FrozenTrigger = "TR_PayrollLines_Frozen";

    public void Configure(EntityTypeBuilder<PayrollLine> builder)
    {
        builder.ToTable("PayrollLines", table =>
        {
            table.HasCheckConstraint("CK_PayrollLines_ExtraDays",
                "[ExtraDays] = 0 OR ([ExtraDays] BETWEEN 0.5 AND 10 AND [ExtraDays] * 2 = FLOOR([ExtraDays] * 2))");
            table.HasTrigger(FrozenTrigger);
        });

        builder.HasKey(l => l.Id);
        builder.Ignore(l => l.HasEntries);
        builder.HasIndex(l => new { l.RunId, l.PersonId }).IsUnique().HasDatabaseName(PersonIndex);
        builder.HasOne<Person>().WithMany().HasForeignKey(l => l.PersonId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(l => l.PersonCode).HasMaxLength(PersonCode.MaxLength).IsUnicode(false).IsRequired();
        builder.Property(l => l.PersonName).HasMaxLength(PersonInput.FullNameMaxLength).IsRequired();
        builder.Property(l => l.Designation).HasMaxLength(PersonInput.DesignationMaxLength).IsRequired();
        builder.Property(l => l.PersonType).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        builder.Property(l => l.HireSource).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        builder.Property(l => l.BankName).HasMaxLength(PersonInput.BankNameMaxLength);
        builder.Property(l => l.Iban).HasMaxLength(PakistaniIban.Length).IsUnicode(false);
        builder.Property(l => l.PayCurrency).HasConversion<string>().HasMaxLength(3).IsUnicode(false);
        builder.Property(l => l.Issue).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        builder.Property(l => l.ExtraDaysNote).HasMaxLength(PayrollLine.ExtraDaysNoteMaxLength);

        foreach (var day in new[] { nameof(PayrollLine.ExtraDays), nameof(PayrollLine.UnpaidDays), nameof(PayrollLine.PayableDays) })
        {
            builder.Property(day).HasPrecision(5, 2);
        }

        foreach (var money in new[]
                 {
                     nameof(PayrollLine.BilledMonthlyUsd), nameof(PayrollLine.CommissionPerPeriodUsd), nameof(PayrollLine.PayMonthlyAmount),
                     nameof(PayrollLine.SalaryPartUsd), nameof(PayrollLine.CommissionUsd), nameof(PayrollLine.BilledUsd),
                     nameof(PayrollLine.PayUsd), nameof(PayrollLine.PayPkr), nameof(PayrollLine.AdjustmentsPkr), nameof(PayrollLine.AdjustmentsUsd),
                     nameof(PayrollLine.NetPayPkr), nameof(PayrollLine.NetPayUsd), nameof(PayrollLine.InvoiceUsd),
                     nameof(PayrollLine.OwnerEarningUsd), nameof(PayrollLine.OwnerEarningPkr),
                 })
        {
            builder.Property(money).HasPrecision(18, 2);
        }

        builder.Property(l => l.RowVersion).IsRowVersion();

        builder.HasMany(l => l.Adjustments).WithOne().HasForeignKey(a => a.LineId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(l => l.Adjustments).HasField("_adjustments").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(l => l.Absences).WithOne().HasForeignKey(a => a.LineId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(l => l.Absences).HasField("_absences").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class PayrollLineAbsenceConfiguration : IEntityTypeConfiguration<PayrollLineAbsence>
{
    public const string FrozenTrigger = "TR_PayrollLineAbsences_Frozen";

    public void Configure(EntityTypeBuilder<PayrollLineAbsence> builder)
    {
        builder.ToTable("PayrollLineAbsences", table => table.HasTrigger(FrozenTrigger));
        builder.HasKey(a => a.Id);
        builder.HasIndex(a => new { a.LineId, a.Date }).IsUnique();
        builder.Property(a => a.Portion).HasConversion<string>().HasMaxLength(8).IsUnicode(false);
        builder.Property(a => a.PaidDays).HasPrecision(5, 2);
        builder.Property(a => a.UnpaidDays).HasPrecision(5, 2);
    }
}

internal sealed class PayrollAdjustmentConfiguration : IEntityTypeConfiguration<PayrollAdjustment>
{
    public const string FrozenTrigger = "TR_PayrollAdjustments_Frozen";

    public void Configure(EntityTypeBuilder<PayrollAdjustment> builder)
    {
        builder.ToTable("PayrollAdjustments", table =>
        {
            table.HasCheckConstraint("CK_PayrollAdjustments_Amount",
                "([Currency] = 'USD' AND [Amount] BETWEEN 0.01 AND 100000) OR ([Currency] = 'PKR' AND [Amount] BETWEEN 1 AND 50000000 AND [Amount] = ROUND([Amount], 0))");
            table.HasTrigger(FrozenTrigger);
        });

        builder.HasKey(a => a.Id);
        builder.Ignore(a => a.Sign);
        builder.Property(a => a.Type).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        builder.Property(a => a.Currency).HasConversion<string>().HasMaxLength(3).IsUnicode(false);
        builder.Property(a => a.Amount).HasPrecision(18, 2);
        builder.Property(a => a.AmountPkr).HasPrecision(18, 2);
        builder.Property(a => a.AmountUsd).HasPrecision(18, 2);
        builder.Property(a => a.Note).HasMaxLength(AdjustmentRules.NoteMaxLength);
        builder.Property(a => a.CreatedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(a => a.UpdatedByUserId).HasMaxLength(450).IsRequired();
        builder.Property(a => a.RowVersion).IsRowVersion();
    }
}
