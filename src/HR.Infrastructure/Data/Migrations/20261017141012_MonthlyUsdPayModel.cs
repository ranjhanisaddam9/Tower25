using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HR.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Pay model v1.1.0 (owner-approved): every rate record becomes three monthly USD figures with
    /// Billed = Salary + Commission (SPEC §2). EF runs the whole migration in one transaction, so a stop rolls it all back.
    /// <list type="bullet">
    /// <item>CompanyRecommended: Salary = old pay (USD); Commission = old commission per period × 2; Billed = Salary + Commission.</item>
    /// <item>BudgetHire paid in USD: Billed = old budget; Salary = old pay; Commission = Billed − Salary.</item>
    /// <item>BudgetHire paid in PKR: Salary = round2(old pay ÷ the exchange rate in effect on EffectiveFrom, else the
    /// earliest rate); Billed = old budget; Commission = Billed − Salary; NeedsBillingReview; the note records the conversion.</item>
    /// <item>Owner: Salary = old pay; Commission 0; Billed = Salary.</item>
    /// </list>
    /// Finalized payroll lines and invoices are not touched: PayrollLines only gains two nullable snapshot columns (adding
    /// columns fires no trigger). Draft runs are recalculated afterwards by <c>HR.Web.exe recalculate-drafts</c> (migrate.ps1).
    /// No names or amounts are written to any log; stop messages name record ids only.
    /// </summary>
    public partial class MonthlyUsdPayModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Take nothing for granted: refuse data the old rules never allowed, and PKR pay without any exchange rate.
            migrationBuilder.Sql("""
                DECLARE @ids nvarchar(max);

                SELECT @ids = STRING_AGG(CONVERT(nvarchar(20), r.Id), ',')
                FROM dbo.RateRecords r JOIN dbo.People p ON p.Id = r.PersonId
                WHERE p.HireSource IS NULL OR p.HireSource NOT IN ('CompanyRecommended', 'BudgetHire', 'Owner');
                IF @ids IS NOT NULL
                BEGIN
                    DECLARE @m1 nvarchar(2048) = CONCAT(N'Pay-model migration stopped: rate records ', @ids, N' belong to people without a hire source. Set their hire source (or delete those records), then run the migration again.');
                    THROW 51040, @m1, 1;
                END;

                SELECT @ids = STRING_AGG(CONVERT(nvarchar(20), r.Id), ',')
                FROM dbo.RateRecords r JOIN dbo.People p ON p.Id = r.PersonId
                WHERE p.HireSource IN ('CompanyRecommended', 'Owner') AND r.PayCurrency <> 'USD';
                IF @ids IS NOT NULL
                BEGIN
                    DECLARE @m2 nvarchar(2048) = CONCAT(N'Pay-model migration stopped: rate records ', @ids, N' are CompanyRecommended or Owner records paid in PKR, which the old rules did not allow. Correct them to USD, then run the migration again.');
                    THROW 51041, @m2, 1;
                END;

                IF EXISTS (SELECT 1 FROM dbo.RateRecords WHERE PayCurrency = 'PKR') AND NOT EXISTS (SELECT 1 FROM dbo.ExchangeRates)
                    THROW 51042, N'Pay-model migration stopped: some pay records are in PKR but there is no exchange rate to convert them to USD. Add an exchange rate in the app, then run the migration again.', 1;
                """);

            // 2. New columns, filled from the old ones.
            migrationBuilder.AddColumn<decimal>(name: "SalaryMonthlyUsd", table: "RateRecords", type: "decimal(18,2)", precision: 18, scale: 2, nullable: true);
            migrationBuilder.AddColumn<decimal>(name: "CommissionMonthlyUsd", table: "RateRecords", type: "decimal(18,2)", precision: 18, scale: 2, nullable: true);

            migrationBuilder.Sql("""
                UPDATE r SET
                    SalaryMonthlyUsd = CASE
                        WHEN r.PayCurrency = 'PKR' THEN ROUND(r.PayMonthlyAmount / x.Rate, 2)
                        ELSE r.PayMonthlyAmount END,
                    NeedsBillingReview = CASE WHEN r.PayCurrency = 'PKR' THEN CAST(1 AS bit) ELSE r.NeedsBillingReview END,
                    Note = CASE
                        WHEN r.PayCurrency <> 'PKR' THEN r.Note
                        WHEN r.Note IS NULL THEN c.Text
                        ELSE LEFT(r.Note, 300 - LEN(c.Text) - 3) + N' | ' + c.Text END
                FROM dbo.RateRecords r
                OUTER APPLY (SELECT TOP (1) e.UsdToPkr FROM dbo.ExchangeRates e WHERE e.EffectiveFrom <= r.EffectiveFrom ORDER BY e.EffectiveFrom DESC) inEffect
                OUTER APPLY (SELECT TOP (1) e.UsdToPkr FROM dbo.ExchangeRates e ORDER BY e.EffectiveFrom) earliest
                CROSS APPLY (SELECT COALESCE(inEffect.UsdToPkr, earliest.UsdToPkr) AS Rate) x
                CROSS APPLY (SELECT CONCAT(N'Converted from Rs ', FORMAT(r.PayMonthlyAmount, 'N0', 'en-US'), N' at rate ', FORMAT(x.Rate, '0.####', 'en-US')) AS Text) c;

                UPDATE r SET
                    CommissionMonthlyUsd = CASE p.HireSource
                        WHEN 'CompanyRecommended' THEN r.CommissionPerPeriodUsd * 2
                        WHEN 'BudgetHire' THEN r.BilledMonthlyUsd - r.SalaryMonthlyUsd
                        ELSE 0 END
                FROM dbo.RateRecords r JOIN dbo.People p ON p.Id = r.PersonId;

                UPDATE r SET BilledMonthlyUsd = r.SalaryMonthlyUsd + r.CommissionMonthlyUsd
                FROM dbo.RateRecords r JOIN dbo.People p ON p.Id = r.PersonId
                WHERE p.HireSource IN ('CompanyRecommended', 'Owner');
                """);

            // 3. Old columns and constraints out, the new invariant in.
            migrationBuilder.DropCheckConstraint(name: "CK_RateRecords_Amounts", table: "RateRecords");
            migrationBuilder.DropCheckConstraint(name: "CK_RateRecords_PkrWholeRupees", table: "RateRecords");
            migrationBuilder.DropColumn(name: "PayCurrency", table: "RateRecords");
            migrationBuilder.DropColumn(name: "PayMonthlyAmount", table: "RateRecords");
            migrationBuilder.DropColumn(name: "CommissionPerPeriodUsd", table: "RateRecords");
            migrationBuilder.AlterColumn<decimal>(name: "SalaryMonthlyUsd", table: "RateRecords", type: "decimal(18,2)", precision: 18, scale: 2, nullable: false,
                oldClrType: typeof(decimal), oldType: "decimal(18,2)", oldPrecision: 18, oldScale: 2, oldNullable: true);
            migrationBuilder.AlterColumn<decimal>(name: "CommissionMonthlyUsd", table: "RateRecords", type: "decimal(18,2)", precision: 18, scale: 2, nullable: false,
                oldClrType: typeof(decimal), oldType: "decimal(18,2)", oldPrecision: 18, oldScale: 2, oldNullable: true);
            migrationBuilder.AddCheckConstraint(name: "CK_RateRecords_BilledIsSalaryPlusCommission", table: "RateRecords",
                sql: "[BilledMonthlyUsd] = [SalaryMonthlyUsd] + [CommissionMonthlyUsd]");
            migrationBuilder.AddCheckConstraint(name: "CK_RateRecords_SalaryPositive", table: "RateRecords",
                sql: "[SalaryMonthlyUsd] > 0 AND [BilledMonthlyUsd] > 0");

            // 4. New snapshot columns on payroll lines (nullable: lines frozen before v1.1.0 keep the legacy ones).
            migrationBuilder.AddColumn<decimal>(name: "SalaryMonthlyUsd", table: "PayrollLines", type: "decimal(18,2)", precision: 18, scale: 2, nullable: true);
            migrationBuilder.AddColumn<decimal>(name: "CommissionMonthlyUsd", table: "PayrollLines", type: "decimal(18,2)", precision: 18, scale: 2, nullable: true);
        }

        /// <summary>
        /// Back to the legacy columns. Lossy by nature: every pay becomes USD (PKR amounts converted in Up are not
        /// restored), and CompanyRecommended commission becomes half the monthly commission per period.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CommissionMonthlyUsd", table: "PayrollLines");
            migrationBuilder.DropColumn(name: "SalaryMonthlyUsd", table: "PayrollLines");
            migrationBuilder.DropCheckConstraint(name: "CK_RateRecords_BilledIsSalaryPlusCommission", table: "RateRecords");
            migrationBuilder.DropCheckConstraint(name: "CK_RateRecords_SalaryPositive", table: "RateRecords");

            migrationBuilder.AddColumn<string>(name: "PayCurrency", table: "RateRecords", type: "varchar(3)", unicode: false, maxLength: 3, nullable: false, defaultValue: "USD");
            migrationBuilder.AddColumn<decimal>(name: "PayMonthlyAmount", table: "RateRecords", type: "decimal(18,2)", precision: 18, scale: 2, nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<decimal>(name: "CommissionPerPeriodUsd", table: "RateRecords", type: "decimal(18,2)", precision: 18, scale: 2, nullable: false, defaultValue: 0m);
            migrationBuilder.Sql("""
                UPDATE r SET
                    PayMonthlyAmount = r.SalaryMonthlyUsd,
                    CommissionPerPeriodUsd = CASE WHEN p.HireSource = 'CompanyRecommended' THEN ROUND(r.CommissionMonthlyUsd / 2, 2) ELSE 0 END,
                    BilledMonthlyUsd = CASE WHEN p.HireSource = 'BudgetHire' THEN r.BilledMonthlyUsd ELSE r.SalaryMonthlyUsd END
                FROM dbo.RateRecords r JOIN dbo.People p ON p.Id = r.PersonId;
                """);
            migrationBuilder.DropColumn(name: "SalaryMonthlyUsd", table: "RateRecords");
            migrationBuilder.DropColumn(name: "CommissionMonthlyUsd", table: "RateRecords");
            migrationBuilder.AddCheckConstraint(name: "CK_RateRecords_Amounts", table: "RateRecords",
                sql: "[BilledMonthlyUsd] > 0 AND [PayMonthlyAmount] > 0 AND [CommissionPerPeriodUsd] >= 0");
            migrationBuilder.AddCheckConstraint(name: "CK_RateRecords_PkrWholeRupees", table: "RateRecords",
                sql: "[PayCurrency] <> 'PKR' OR [PayMonthlyAmount] = ROUND([PayMonthlyAmount], 0)");
        }
    }
}
