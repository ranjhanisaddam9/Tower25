# Pay model report: monthly USD salary, commission and billed (v1.1.0)

**Status: complete, committed and tagged v1.1.0.**
- Build: 0 warnings, in Debug and in Release with warnings as errors.
- Tests: **599 passed**, 0 failed, 0 skipped.
- The dev database is migrated.
- No vulnerable packages.

## What was built

### Rules (SPEC §2, §5, §9 and change log updated as specified)
- **Currency:** every salary is agreed in USD per month. Everything is billed in USD and paid in PKR at the payroll's exchange rate. The pay currency is removed from pay records. Payroll **adjustments** keep their USD/PKR currency, as SPEC still says.
- **Rate record:** `SalaryMonthlyUsd`, `CommissionMonthlyUsd`, `BilledMonthlyUsd`.
  - Billed = Salary + Commission is enforced by the domain and by the database check constraint `CK_RateRecords_BilledIsSalaryPlusCommission`.
  - Salary and billed must be greater than zero.
- **Admin inputs by hire source.** The server always derives the third figure; posted derived values are ignored.

  | Source | Admin enters | Derived |
  |---|---|---|
  | Company recommended | Salary + Commission. The suggestion is $50 for the first record, then the previous record's commission. | Billed |
  | Budget hire | Billed (the budget) + Salary | Commission = Billed − Salary |
  | Owner | Salary | Commission 0, Billed = Salary |

  A budget-hire commission below 0 needs the existing "I understand this hire loses money" confirmation. Zero commission does not.
- **Payroll line:**
  - SalaryUsd = round2(Salary/2 × f)
  - CommissionUsd = round2(Commission/2 × f)
  - BilledUsd = SalaryUsd + CommissionUsd
  - PayUsd = SalaryUsd
  - PayPkr = round0(PayUsd × rate)

  Adjustments, the invoice, net pay and the Owner-line rule are unchanged.
- **Manager increments:** the new monthly salary only.
  - Company recommended: the commission is carried over.
  - Budget hire: the budget is carried over, the commission re-derived, and the record flagged `NeedsBillingReview`. There is no loss warning for Managers.
  - Owner: billed = salary.

### Screens and outputs
- **Admin pay form (`site.js`, no inline script):**
  - the three monthly figures, the derived one read-only and live;
  - a "per pay period" preview (salary, commission, billed, and the salary in PKR labelled as an estimate);
  - the loss warning for budget hires.
- **Admin "current" card:** a per-month / per-full-period table of salary (with a PKR estimate), commission, billed and your earning.
- **Manager pay tab and increment form:** the USD salary and its PKR estimate only.
- **Salaries overview:**
  - Managers see salary per month.
  - Admins also see commission per month, billed per month and earning per period.
  - Totals cover salary, commission, billed and earning.
- **Exports:**
  - salaries (Manager and Admin);
  - salary changes (salary; Admin adds commission and billed).

  The payroll export, register, payslips and PDFs show the monthly salary for new lines and the legacy pay and currency for old lines.
- **Line detail billing breakdown:** new lines show "Salary … ÷ 2 × f" and "Commission … ÷ 2 × f". Lines frozen before v1.1.0 keep the old "Salary part / commission per period" wording.
- **Invoices:** mapping unchanged. They are built from `BilledUsd`, which means the same in both models.
- **Demo seeder:** updated to the new golden people (Bilal $700 of $1,000; company-recommended staff $300 + $50).
- **New server command `recalculate-drafts`:** run by `migrate.ps1` after the migration bundle, and run once on the dev database.

### Frozen data
- Finalized payroll lines keep their legacy snapshot columns (`CommissionPerPeriodUsd`, `PayMonthlyAmount`, `PayCurrency`).
- New lines fill the new `SalaryMonthlyUsd` / `CommissionMonthlyUsd` columns. Old finalized lines leave them null.
- The migration only adds nullable columns to `PayrollLines`. It runs no `UPDATE`, so the frozen-data triggers stay on and never fire.

## Migration: `20261017141012_MonthlyUsdPayModel`
- **One transaction:** EF's migration transaction, so a stop rolls everything back.
- **Validation first, stopping with a clear message (record ids only, no names or amounts):**
  - a pay record for a person without a hire source;
  - a company-recommended or Owner record paid in PKR;
  - PKR pay with no exchange rate at all.
- **Conversion:** exactly as specified. PKR budget hires use the rate in effect on `EffectiveFrom`, or the earliest rate. They are flagged for billing review and the note gets "Converted from Rs X at rate Y" appended, truncated to fit the 300-character limit if needed.
- **`Down`** restores the legacy columns but is lossy: PKR amounts aren't restored. This is documented in the code.

### Converted records (dev database `HRPayroll`, 17 Oct 2026)
The full old → new table with amounts is in **`docs/private/PayModel-conversions.md`** on this PC only. That folder is git-ignored, because the GitHub repo is public. Summary, without amounts:

| Source (old pay currency) | Records | People |
|---|---|---|
| Company recommended (USD) | 20 | HR-0003, HR-0006, HR-0007, HR-0008, HR-0010, HR-0012, HR-0014 to HR-0017, HR-0019 to HR-0027 |
| Budget hire (PKR → USD at Rs 270, the only rate, as the earliest) | 15, all flagged "Review billing" | HR-0002, HR-0004, HR-0005, HR-0009, HR-0011, HR-0013, HR-0018, HR-0028, HR-0029, HR-0030 |
| Owner (USD) | 1 | HR-0001 |

- After the conversion: 0 records violate Billed = Salary + Commission, and 0 have a negative commission.
- The single draft run was recalculated: its 28 lines are now on the new model.
- A safety backup was taken first: `HRPayroll_before_paymodel_20261017.bak` in SQL Server's default backup folder.

## Tests: 599 passed, 0 failed, 0 skipped
- **Golden cases:** G1–G10 and the $747.73 owner income, exactly as in the new §9 table, plus your two examples:
  - $750 + $50 → 375.00 / 25.00 / 400.00 / Rs 105,000;
  - budget $1,000, salary $800 → 400.00 / 100.00 / 500.00 / Rs 112,000.
- **Derivation per source**, with tampered posts ignored (unit tests and over HTTP), plus the $50 / previous-commission suggestion.
- **Database constraint:** Billed = Salary + Commission refused by the database on insert and on update.
- **Manager increments per source** over HTTP, including a budget hire raised above its budget: saved, flagged, and no loss warning shown.
- **Migration tests** on throwaway databases (`HRPayroll_Test_PayModel…`, created and dropped by the tests):
  - all five conversion paths;
  - a finalized run, its line and its issued invoice byte-for-byte unchanged (`FOR JSON` with rowversions);
  - the stop on a PKR company-recommended record (rolled back, no amounts in the message);
  - the stop when there's no exchange rate.
- **Legacy line display:** a line frozen before v1.1.0 renders on the run page, the line page, the payslip, the payslip/register PDFs, the Excel exports and the T25 export, for Admin and Manager.
- **Manager visibility:** the existing tests still pass. Manager pages and exports contain no commission, billed or budget.

## Smoke test
- **Running-app smoke test and visual check:** skipped at your request. You chose the manual checklist below rather than a sign-in.
- **Covered over HTTP by the integration tests instead:** the Admin form for all three sources (create, edit, loss confirmation), Manager increments and edits, the salaries overview, exports, and payroll pages.
- **Server command:** `recalculate-drafts` was run on the dev database: "Draft payrolls recalculated: 1 of 1".

## Known issues and deviations
- **No deviations from the new SPEC.**
- **Real-world effect for budget hires:** paid in PKR before, now converted at Rs 270. Their PKR pay will follow each payroll's rate from now on. Check each in Salaries → Needs review.
- **Migration test databases:** these tests use their own throwaway databases next to `HRPayroll_Test`, because the migration has to run from the old schema. They are named `HRPayroll_Test_…` and always dropped; the dev database is never touched.

## Release
- `dist\HRPayroll-v1.1.0-win-x64.zip`, about 91 MB, rebuilt from the tagged commit.
- **On your server:** unzip it and run `update.ps1` as Administrator.
  - It backs up first, runs the conversion and recalculates drafts.
  - Make sure at least one exchange rate exists before you run it.
  - Afterwards, review the flagged budget hires (DEPLOYMENT.md, "Upgrading from 1.0 to 1.1.0").

## Manual checks for you
At about 375px (phone) and on desktop, in light and dark themes:
1. **People → a company-recommended person → Add pay record:**
   - Salary and Commission are editable (commission prefilled with 50 or the previous value).
   - Billed updates as you type and can't be edited.
   - The per-period preview shows half of each figure and "≈ Rs …".
2. **A budget hire → Add pay record:**
   - Budget and Salary are editable; Commission is derived.
   - Typing a salary above the budget shows the "loses money" warning with its checkbox.
3. **The Owner → Add pay record:** salary only; Commission 0 and Billed = Salary.
4. **A person's Pay tab (Admin):** the current table (per month / per full period) is readable and doesn't overflow on a phone.
5. **As a Manager:**
   - Record increment asks for the new monthly salary in USD only.
   - The pay tab shows the salary and its PKR estimate, and nothing about commission or billing.
6. **Salaries:** the Admin totals row lines up under Salary, Commission, Billed and Earning.
7. **An existing finalized payroll** (if any): its lines, payslips and invoice show the same amounts as before.
