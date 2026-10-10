using HR.Domain.Absences;
using HR.Domain.Invoices;
using HR.Domain.Pay;
using HR.Domain.Payroll;
using HR.Domain.People;
using HR.Domain.Settings;

namespace HR.Tests.Unit;

public class InvoiceMathTests
{
    private static DateOnly Oct(int day) => new(2026, 10, day);

    private static readonly EmploymentSpan[] Always = [new(new DateOnly(2025, 1, 6), null)];
    private static readonly AbsenceDay[] BilalAbsences = [new(Oct(6), AbsencePortion.Full), new(Oct(20), AbsencePortion.Full), new(Oct(27), AbsencePortion.Full)];
    private static readonly PayTerms G1Terms = new(Oct(1), 300m, 25m, 300m, PayCurrency.USD);
    private static readonly PayTerms BudgetTerms = new(Oct(1), 1000m, 0m, 196_000m, PayCurrency.PKR);

    private static InvoiceLineAmounts Map(PayrollLineResult r) =>
        InvoiceMath.Line(r.BilledUsd!.Value, r.AdjustmentsUsd!.Value, r.InvoiceUsd!.Value, r.PayableDays, r.WorkingDays);

    private static PayrollLineResult Calc(PayPeriod period, PayTerms terms, HireSource source, AbsenceDay[]? absences = null, AdjustmentInput[]? adjustments = null) =>
        PayrollCalculator.Calculate(new PayrollInput(period, Always, absences ?? [], terms, source, 280m, 0m, adjustments ?? []));

    [Fact]
    public void G1_line_salary_only()
    {
        var line = Map(Calc(PayPeriod.For(Oct(1)), G1Terms, HireSource.CompanyRecommended));
        Assert.Equal(new InvoiceLineAmounts("11/11", 175.00m, 0m, 175.00m), line);
    }

    [Fact]
    public void G5_line_with_unpaid_days()
    {
        var line = Map(Calc(PayPeriod.For(Oct(16)), BudgetTerms, HireSource.BudgetHire, BilalAbsences));
        Assert.Equal(new InvoiceLineAmounts("9/11", 409.09m, 0m, 409.09m), line);
    }

    [Fact]
    public void G8_line_shows_the_adjustments_as_extras()
    {
        var line = Map(Calc(PayPeriod.For(Oct(16)), BudgetTerms, HireSource.BudgetHire, BilalAbsences,
        [
            new(AdjustmentType.Reimbursement, 5_000m, PayCurrency.PKR),
            new(AdjustmentType.Bonus, 20m, PayCurrency.USD),
            new(AdjustmentType.Deduction, 1_000m, PayCurrency.PKR),
        ]));

        Assert.Equal(new InvoiceLineAmounts("9/11", 409.09m, 34.29m, 443.38m), line);
    }

    [Fact]
    public void Days_text_keeps_half_and_extra_days()
    {
        Assert.Equal("10.5/11", InvoiceMath.Line(1m, 0m, 1m, 10.5m, 11).DaysText);
        Assert.Equal("12/11", InvoiceMath.Line(1m, 0m, 1m, 12m, 11).DaysText);
    }

    [Fact]
    public void Salary_plus_extras_must_equal_the_amount() =>
        Assert.Throws<InvalidOperationException>(() => InvoiceMath.Line(100m, 5m, 104m, 11m, 11));

    [Fact]
    public void Numbers_are_prefix_year_and_a_four_digit_sequence_that_restarts_each_year()
    {
        var y2026 = new InvoiceCounter(2026);
        var y2027 = new InvoiceCounter(2027);

        Assert.Equal("INV-2026-0001", InvoiceMath.Number("INV", 2026, y2026.Next()));
        Assert.Equal("INV-2026-0002", InvoiceMath.Number("INV", 2026, y2026.Next()));
        Assert.Equal("INV-2027-0001", InvoiceMath.Number("INV", 2027, y2027.Next()));
        Assert.Equal("ACME-2026-0003", InvoiceMath.Number("ACME", 2026, y2026.Next()));
        Assert.Equal("INV-2026-12345", InvoiceMath.Number("INV", 2026, 12345));
    }

    [Theory]
    [InlineData("2026-10-15", 7, "2026-10-22")]
    [InlineData("2026-10-31", 7, "2026-11-07")]
    [InlineData("2026-12-30", 7, "2027-01-06")]
    [InlineData("2028-02-25", 7, "2028-03-03")] // leap year
    [InlineData("2026-10-15", 0, "2026-10-15")]
    [InlineData("2026-10-15", 120, "2027-02-12")]
    public void Due_date_is_issue_date_plus_terms(string issue, int terms, string due) =>
        Assert.Equal(DateOnly.Parse(due, CultureInfo.InvariantCulture), InvoiceMath.DueDate(DateOnly.Parse(issue, CultureInfo.InvariantCulture), terms));

    [Fact]
    public void Overdue_only_when_unpaid_and_after_the_due_date()
    {
        Assert.False(InvoiceMath.IsOverdue(InvoiceStatus.Issued, Oct(22), Oct(22)));
        Assert.True(InvoiceMath.IsOverdue(InvoiceStatus.Issued, Oct(22), Oct(23)));
        Assert.False(InvoiceMath.IsOverdue(InvoiceStatus.Paid, Oct(22), Oct(30)));
        Assert.False(InvoiceMath.IsOverdue(InvoiceStatus.Void, Oct(22), Oct(30)));
    }

    [Fact]
    public void An_invoice_total_is_the_sum_of_its_lines_and_it_moves_issued_paid_unpaid_void()
    {
        var parties = new InvoiceParties("Business", null, null, null, null, null, null, null, "Client", null, null, null, null);
        var invoice = Invoice.Issue("INV-2026-0001", 1, Oct(1), Oct(15), Oct(15), 7, parties,
            [new InvoiceLine(1, 1, "A", "Dev", new InvoiceLineAmounts("11/11", 175m, 0m, 175m)), new InvoiceLine(2, 2, "B", "QA", new InvoiceLineAmounts("9/11", 409.09m, 34.29m, 443.38m))],
            null, "t", DateTimeOffset.UnixEpoch);

        Assert.Equal((618.38m, Oct(22), InvoiceStatus.Issued), (invoice.TotalUsd, invoice.DueDate, invoice.Status));
        invoice.MarkPaid(Oct(20), 618.38m, "Wire", "t", DateTimeOffset.UnixEpoch);
        Assert.Throws<InvalidOperationException>(() => invoice.Void("reason", "t", DateTimeOffset.UnixEpoch)); // paid: unpay first
        invoice.MarkUnpaid("t", DateTimeOffset.UnixEpoch);
        Assert.Equal((InvoiceStatus.Issued, (DateOnly?)null), (invoice.Status, invoice.PaidDate));
        invoice.Void("Wrong bonus", "t", DateTimeOffset.UnixEpoch);
        Assert.Equal(InvoiceStatus.Void, invoice.Status);
        Assert.Throws<InvalidOperationException>(() => invoice.MarkPaid(Oct(20), 1m, null, "t", DateTimeOffset.UnixEpoch));
    }
}

public class OwnerIncomeAggregatorTests
{
    private static DateOnly D(int month, int day, int year = 2026) => new(year, month, day);

    /// <summary>Two finalized October periods with the G cases, plus September and a 2025 line.</summary>
    private static readonly IncomeLine[] Lines =
    [
        new(D(10, 1), 1, "Ayesha", HireSource.CompanyRecommended, 25.00m, 7_000m),
        new(D(10, 1), 2, "Bilal", HireSource.BudgetHire, 150.00m, 42_000m),
        new(D(10, 1), 3, "Imran", HireSource.Owner, 600.00m, 168_000m),
        new(D(10, 16), 1, "Ayesha", HireSource.CompanyRecommended, 25.00m, 7_000m),
        new(D(10, 16), 2, "Bilal", HireSource.BudgetHire, 122.73m, 34_364m),
        new(D(10, 16), 3, "Imran", HireSource.Owner, 600.00m, 168_000m),
        new(D(9, 16), 3, "Imran", HireSource.Owner, 600.00m, 168_000m),
        new(D(12, 16, 2025), 1, "Ayesha", HireSource.CompanyRecommended, 30.00m, 8_400m),
    ];

    [Fact]
    public void A_period_sums_its_lines_by_source()
    {
        var b = OwnerIncomeAggregator.For(Lines, IncomeView.Period, D(10, 20));

        Assert.Equal((600.00m, 25.00m, 122.73m, 747.73m), (b.SalaryUsd, b.CommissionUsd, b.MarginUsd, b.TotalUsd));
        Assert.Equal(168_000m + 7_000m + 34_364m, b.TotalPkr);
    }

    [Fact]
    public void A_month_includes_both_periods()
    {
        var b = OwnerIncomeAggregator.For(Lines, IncomeView.Month, D(10, 5));
        Assert.Equal((1_200.00m, 50.00m, 272.73m, 1_522.73m), (b.SalaryUsd, b.CommissionUsd, b.MarginUsd, b.TotalUsd));
    }

    [Fact]
    public void A_year_includes_every_month_of_it_only()
    {
        Assert.Equal(2_122.73m, OwnerIncomeAggregator.For(Lines, IncomeView.Year, D(1, 1)).TotalUsd);
        Assert.Equal(30.00m, OwnerIncomeAggregator.For(Lines, IncomeView.Year, D(6, 1, 2025)).TotalUsd);
    }

    [Fact]
    public void By_month_has_twelve_entries()
    {
        var months = OwnerIncomeAggregator.ByMonth(Lines, 2026);
        Assert.Equal(12, months.Count);
        Assert.Equal(600.00m, months[8].Income.TotalUsd); // September
        Assert.Equal(1_522.73m, months[9].Income.TotalUsd); // October
        Assert.Equal(0m, months[0].Income.TotalUsd);
    }

    [Fact]
    public void Contributors_are_sorted_by_earning_with_shares()
    {
        var c = OwnerIncomeAggregator.Contributors(Lines, IncomeView.Month, D(10, 1));

        Assert.Equal(["Imran", "Bilal", "Ayesha"], c.Select(x => x.PersonName).ToArray());
        Assert.Equal((2, 1_200.00m, 78.8m), (c[0].Periods, c[0].EarningUsd, c[0].SharePercent));
        Assert.Equal(100.0m, c.Sum(x => x.SharePercent), 1);
    }

    [Fact]
    public void Kpis_this_month_year_to_date_last_12_months_and_average()
    {
        var k = OwnerIncomeAggregator.Kpis(Lines, D(10, 20));

        Assert.Equal(1_522.73m, k.ThisMonth.TotalUsd);
        Assert.Equal(2_122.73m, k.YearToDate.TotalUsd);
        Assert.Equal(2_152.73m, k.Last12Months.TotalUsd); // includes Dec 2025
        Assert.Equal(4, k.PeriodsInLast12Months);
        Assert.Equal(538.18m, k.AveragePerPeriodUsd); // 2,152.73 / 4

        // Early in the month the KPIs still count a period of this month that was already finalized (the calendar month).
        Assert.Equal(1_522.73m, OwnerIncomeAggregator.Kpis(Lines, D(10, 3)).ThisMonth.TotalUsd);
        Assert.Equal(2_122.73m, OwnerIncomeAggregator.Kpis(Lines, D(10, 3)).YearToDate.TotalUsd);
    }

    [Fact]
    public void Stepping_moves_by_the_selected_unit()
    {
        Assert.Equal(D(9, 16), OwnerIncomeAggregator.Step(IncomeView.Period, D(10, 3), -1));
        Assert.Equal(D(11, 1), OwnerIncomeAggregator.Step(IncomeView.Month, D(10, 3), 1));
        Assert.Equal(new DateOnly(2027, 1, 1), OwnerIncomeAggregator.Step(IncomeView.Year, D(10, 3), 1));
    }
}

public class SettingsRulesTests
{
    private static SettingsInput Valid() => new("Acme Staffing", "1 Main St\nKarachi", "Billing@Acme.example", "+92 21 111 222", "Meezan Bank", "Acme",
        "PK36 SCBL 0000 0011 2345 6702", "meezpkka", "Client Co", null, "Sara", "ap@client.example", "inv", 7, "Thank you.", "Acme Staffing");

    [Fact]
    public void Normalizes_case_trims_and_groups_a_PK_IBAN()
    {
        var n = SettingsRules.Normalize(Valid(), out var errors);
        Assert.Empty(errors);
        Assert.Equal(("billing@acme.example", "MEEZPKKA", "INV", "PK36SCBL0000001123456702"), (n!.BusinessEmail, n.BankSwift, n.InvoicePrefix, n.BankAccountNumber));
    }

    [Theory]
    [InlineData("BusinessEmail", "not-an-email")]
    [InlineData("ClientEmail", "a@b")]
    [InlineData("BankAccountNumber", "PK00 BAD")]
    [InlineData("BankSwift", "ABC")]
    [InlineData("InvoicePrefix", "IN V")]
    [InlineData("PayslipIssuerName", "")]
    public void Rejects_invalid_values(string field, string value)
    {
        var input = field switch
        {
            "BusinessEmail" => Valid() with { BusinessEmail = value },
            "ClientEmail" => Valid() with { ClientEmail = value },
            "BankAccountNumber" => Valid() with { BankAccountNumber = value },
            "BankSwift" => Valid() with { BankSwift = value },
            "InvoicePrefix" => Valid() with { InvoicePrefix = value },
            _ => Valid() with { PayslipIssuerName = value },
        };

        Assert.Null(SettingsRules.Normalize(input, out var errors));
        Assert.Contains(errors, e => e.Field == field);
    }

    [Fact]
    public void Rejects_lengths_and_terms()
    {
        Assert.Null(SettingsRules.Normalize(Valid() with { BusinessName = new string('a', 201) }, out var e1));
        Assert.Contains(e1, e => e.Field == "BusinessName");
        Assert.Null(SettingsRules.Normalize(Valid() with { PaymentTermsDays = 121 }, out var e2));
        Assert.Contains(e2, e => e.Field == "PaymentTermsDays");
    }

    [Fact]
    public void A_non_PK_account_number_is_kept_as_typed_and_defaults_fill_in()
    {
        var n = SettingsRules.Normalize(Valid() with { BankAccountNumber = "0123-4567-89", InvoicePrefix = null, PaymentTermsDays = null }, out _);
        Assert.Equal(("0123-4567-89", "INV", 7), (n!.BankAccountNumber, n.InvoicePrefix, n.PaymentTermsDays));
    }

    [Fact]
    public void Invoices_need_both_names_and_changes_list_field_names_only()
    {
        var settings = AppSettings.CreateDefault();
        Assert.False(settings.CanIssueInvoices);

        var changed = settings.Apply(SettingsRules.Normalize(Valid(), out _)!, "t", DateTimeOffset.UnixEpoch);
        Assert.True(settings.CanIssueInvoices);
        Assert.Contains("BusinessName", changed);
        Assert.DoesNotContain("PaymentTermsDays", changed); // 7 was already the default

        Assert.Empty(settings.Apply(SettingsRules.Normalize(Valid(), out _)!, "t", DateTimeOffset.UnixEpoch));
    }
}
