# M8 report: settings, company invoice and owner income

Status: **done**. 486/486 tests green (306 unit, 180 integration, 0 skipped), 0 warnings on a clean build. The dev DB is migrated. The full HTTPS cycle passed on a disposable demo DB, which has since been dropped. Visual checks were done at 375px and 1440px in both themes, plus A4 print emulation of the invoice.

## Part A: Follow-ups

### A1. The Owner line earns its whole billed amount

- **Calculator** (`PayrollCalculator`): for an Owner line, `OwnerEarningUsd = InvoiceUsd` and `OwnerEarningPkr = round0(InvoiceUsd × rate)`. Adjustments on the Owner's own line are therefore included.
- **Full-period figures** (`PayMath.FullPeriod`, which takes the hire source): the Owner earns the full billed amount. This is used by:
  - the M5 "current pay" card ("Your whole billed amount")
  - the Salaries overview (Owner per full period = billed).
- **Totals** follow automatically, because every total sums the stored `OwnerEarning*` columns:
  - run page totals
  - the Owner line's billing panel ("your whole invoiced amount")
  - payroll list totals
  - the dashboard.
- **SPEC:**
  - §2 Owner row: "His whole billed amount".
  - §5: the Owner sentence.
  - §8: the new formula.
  - §9 G6: OwnerUsd **600.00**, OwnerPkr **168,000**.
  - A change-log entry for 2026-10-10 (M8).
- **Tests:**
  - G6 (unit and stored line)
  - owner income Oct 16–31 = **$747.73** = Σ OwnerEarningUsd ($600.00 + $25.00 + $122.73)
  - `The_Owner_line_earns_its_whole_invoice_including_adjustments`
  - `Run_totals_include_the_Owner_line_whole_invoice`
  - Salaries overview total $750.00.
- **Finalized M7 runs are not rewritten.** Finalized lines keep their frozen M7 snapshot (Owner earning 0). The dev DB had **0 finalized runs** when it was migrated, so nothing there carries the old rule. In any DB that does have them, they keep the old figures until reopened and re-finalized.

### A2. Deductions exceeding pay block finalize

- The new `LineIssue.NegativeNetPay` is set when a line has no other issue and `NetPayPkr < 0`. The run page shows "Deductions exceed pay", and finalize is refused like any other issue.
- Tests:
  - `Deductions_exceeding_pay_are_an_issue` (unit)
  - `Deductions_that_exceed_pay_block_finalize` (integration).

### A3. Late-added people

When a change adds employment days to a **finalized** period in which the person has **no payroll line**, the form requires the checkbox:

> "This person is not included in the finalized payroll(s) for 01–15 Oct 2026. Pay any arrears as a Bonus or extra days in the current payroll."

This applies to creating a person, reactivating, or moving a joining date earlier.

- The checkbox appears only when it applies:
  - on Create and Edit after a refused POST;
  - always present in the Reactivate modal.
- The server enforces it: without the checkbox the change is refused with that message.
- When confirmed, the change is audited as event **1106**, with person ID and periods only.

**How it interacts with the M7 lock.** `IPayrollLock` gained a person-aware check, `IsLockedForPersonAsync(personId, periodStart)`. It is true only when the finalized run has a line for that person. `PersonService.EmploymentImpactAsync` sorts each finalized period a change touches:

| Situation | Result |
|---|---|
| The person **has a line** in the finalized period (any change to their days there) | **Refused**, as in M7: reopen the payroll first |
| Change **adds** days, person has **no line** there | **Needs confirmation**, then audited (1106) |
| Change **removes** days, person has no line there (e.g. deactivate back-dated) | Allowed silently (there is nothing frozen to contradict) |
| **Cancel leaving** that would add uncovered finalized days | Refused (no confirmation route) |

The interface's default implementation of `IsLockedForPersonAsync` falls back to the M7 `IsLockedAsync`, so test doubles behave as in M7. Absences and pay records keep the M7 lock unchanged.

## Part B: Settings (`/admin/settings`, AdminOnly)

- **Table:** a single-row `AppSettings` (Id = 1, enforced by `CK_AppSettings_Singleton`), with a RowVersion.
- **Fields:**
  - business: name, address, email, phone
  - bank: name, account title, account number/IBAN, SWIFT
  - client: company, address, contact person, email
  - invoice: prefix (default INV), payment terms (default 7, 0–120), footer note
  - payslip issuer name.
- **Validation** (`SettingsRules`, pure domain):
  - length limits; email format;
  - a Pakistani IBAN is checksum-validated and normalized when the account number looks like one (`PK…`);
  - SWIFT pattern;
  - prefix `A–Z0–9`, at most 10 characters;
  - issuer name required.
- **Saving:**
  - a stale RowVersion returns **409** with "Someone else changed the settings…";
  - the audit (event 1600) lists **changed field names only**, never values. The toast says how many fields changed.
- **Payslip issuer:**
  - The `Payslip:IssuerName` config key is **removed** from `appsettings.json`.
  - The migration seeds `PayslipIssuerName = 'HR Payroll'`, the old config value.
  - Payslips read it from Settings.
- **Missing details:** without a business name or client company name, invoices can't be issued. Banners appear:
  - on the Settings page;
  - on the Invoices list, linking to Settings (added after the visual pass; the test covers it);
  - on the run page, where the pending panel links to Settings.

## Part C: Company invoice

### Model (migration `AddInvoices`)

**`Invoices`:**
- Number `<prefix>-YYYY-NNNN` (unique), from `InvoiceCounters` (per year, incremented inside the finalize transaction).
- RunId; Status Issued / Paid / Void.
- IssueDate (Karachi date of `FinalizedAt`) and DueDate (issue + terms), with check DueDate ≥ IssueDate.
- Snapshot of business, bank and client details.
- TotalUsd.
- PaidDate / PaidAmountUsd, with a check that both are set exactly when Paid.
- VoidedAt / VoidReason, with a check that they are set exactly when Void.
- ReplacesInvoiceId; RowVersion.
- **At most one non-void invoice per run:** filtered unique index `RunId WHERE Status <> 'Void'`.
- FKs use Restrict, so a run with invoices can't be deleted (the service also refuses this with a message).

**`InvoiceLines`:**
- Ordered by person name; columns: days text ("10.5/11"), designation, SalaryUsd = BilledUsd, ExtrasUsd = Σ s·AmountUsd, AmountUsd = InvoiceUsd.
- `InvoiceMath.Line` throws unless salary + extras = amount.
- The Owner line is listed like any other.
- **Immutability:** trigger `TR_InvoiceLines_Frozen` (THROW 51020) refuses insert, update or delete of lines whose invoice is Paid or Void.

### Lifecycle

| Action | Behaviour |
|---|---|
| Finalize | Issues the invoice in the **same transaction** (event 1601). If Settings are incomplete, the payroll still finalizes and the run shows "Invoice pending: complete Settings…" with an **Issue invoice** button. |
| Issue invoice (pending) | Serializable transaction; refused while Settings are incomplete. Issue date = finalize date. |
| Mark paid / unpaid | POST with a confirm modal. Paid date between the issue date and today; amount > 0 with at most 2 decimals (events 1602 / 1603). |
| Reopen | Refused while the invoice is Paid: "Mark the invoice unpaid first." Otherwise the invoice is **voided** with the reopen reason (event 1604). |
| Re-finalize | Issues a new number with `ReplacesInvoiceId` = the latest void invoice. The void invoice shows a banner linking to its replacement. |

### Pages

- **`/invoices`:**
  - status filter (All / Unpaid / Overdue / Paid / Void) and year filter
  - tiles for invoiced, received and outstanding
  - table; empty state linking to Payroll.
- **`/invoices/{id}`:**
  - a printable A4 document: business and client blocks, bank details, lines, total, footer note;
  - Print, Mark paid / Mark unpaid, and a back link to the payroll;
  - no "commission", "margin", "budget" or "earning" anywhere (an integration test checks this).
- **Run page:** links to its invoice, or shows the pending panel.
- **Dashboard (Admin):** tile "Outstanding invoices: $X (N overdue)".
- **Sidebar:** Invoices is live for Admins.

## Part D: Owner income (`/owner-income`, AdminOnly)

- **Data:** only frozen values from finalized runs (`OwnerEarningUsd/Pkr` per line plus hire source), aggregated by the pure `OwnerIncomeAggregator`. A **"Include current draft"** toggle adds the draft run's lines, which are shown separately (hatched bars, "draft" label) and never mixed into the finalized KPI tiles.
- **Views:** Period / Month / Year, with previous / next.
- **KPI tiles:** this month, year to date, last 12 months, average per finalized period. These use calendar ranges, so both halves of the current month count.
- **Breakdown table:** own salary, commission and margin, with totals, in USD and PKR.
- **Chart:** an inline SVG stacked bar chart (periods, or months for the year view):
  - `<title>` / `<desc>` plus the same numbers in a data table under the chart;
  - no style attributes and no JS; colours come from CSS tokens per theme;
  - legend.
  - Series contrast against the card is **≥ 4.8:1 in light and ≥ 8.2:1 in dark**.
- **Contributors table:** person, source, periods, USD, PKR, share %, largest first.

## Migrations added

1. `20261010074712_AddSettings`:
   - creates the `AppSettings` table (with singleton and terms check constraints);
   - seeds row 1 (prefix INV, terms 7, issuer "HR Payroll").
2. `20261010075435_AddInvoices`:
   - creates the `Invoices`, `InvoiceLines` and `InvoiceCounters` tables, with the indexes and check constraints above;
   - adds trigger `TR_InvoiceLines_Frozen`; Down drops it.

Both were applied to the dev DB with `dotnet ef database update`. `HasPendingModelChanges()` is false (model test).

## Tests

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| Unit | 306 | 0 | 0 |
| Integration | 180 | 0 | 0 |
| **Total** | **486** | **0** | **0** |

New in M8:
- **Unit:**
  - `InvoiceMathTests`: number format, due date, overdue, line consistency
  - `OwnerIncomeAggregatorTests`: ranges, steps, breakdown, by-month, contributors, KPIs including early in the month
  - `SettingsRulesTests`: lengths, email, IBAN, SWIFT, prefix, terms, changed field names
  - calculator tests for G6, the Owner line with adjustments, and negative net pay.
- **Integration** (`InvoiceAndIncomeTests`):
  - run totals with the Owner line
  - negative net pay blocks finalize
  - late addition confirmation and audit
  - settings validation, audit and 409
  - invoice on finalize
  - pending invoice then issue
  - reopen void, replacement and paid blocking reopen
  - overdue, outstanding and dashboard with a fake clock
  - owner income by period, month and year plus draft
  - Manager 403 on every new route
  - POST-only, antiforgery, and no inline script or style.
- **Updated:**
  - golden G6 and $747.73 in `PayrollTests`
  - Owner earning $600.00 and salaries total $750.00 in `PayTests`
  - sidebar tests in `PeopleTests` and `DashboardTests`
  - `TestDatabaseFixture` resets invoices, counters and settings.

## Smoke test (HTTPS, disposable demo DB `HRPayroll_Visual`, real date 10 Oct 2026)

**Access:**
- anonymous `/invoices` → 302 to login;
- Manager → **403** on `/invoices`, `/owner-income` and `/admin/settings`.

**Settings:**
1. The empty settings showed the "can't issue invoices" banner.
2. Saving with prefix **T25**, terms 7 and full details reported "15 fields changed".

**Payrolls:**
1. 01–15 Oct 2026: issues fixed, then finalized → invoice **T25-2026-0001**, $9,187.50, 24 lines.
2. 16–31 Oct 2026: finalized → **T25-2026-0002**, $9,060.23, 24 lines.
3. Both invoices list the Owner line (Imran Qureshi). Neither contains a forbidden word.

**Invoice lifecycle:**
1. **T25-2026-0001** marked paid.
2. 16–31 Oct reopened → **T25-2026-0002 void**, with the reason and void banner shown.
3. A $50 bonus was added for Ayesha, then the run was re-finalized → **T25-2026-0003**, **$9,110.23**, "Replaces T25-2026-0002 (void)". The void invoice links to it.
4. Reopening 01–15 Oct was refused: "Only the latest finalized payroll can be reopened."
5. T25-2026-0003 was marked paid; reopening 16–31 Oct was then refused with "Mark the invoice unpaid first." The invoice was then marked unpaid again.

**Invoice totals:** invoiced **$18,297.73**, received **$9,187.50**, outstanding **$9,110.23**.

**Dashboard tile:** Outstanding invoices **$9,110.23** (1 unpaid · 0 overdue).

**Owner income, month view, October 2026:**

| | USD | PKR |
|---|---|---|
| Own salary | $1,200.00 | Rs 336,000 |
| Commission | $740.45 | Rs 207,326 |
| Margin | $3,286.99 | Rs 920,363 |
| **Total** | **$5,227.44** | **Rs 1,463,689** |

- **Per period:** 01–15 Oct $2,629.63; 16–31 Oct $2,597.81.
- **KPIs:** this month $5,227.44; year to date $5,227.44; last 12 months $5,227.44; average per period $2,613.72 (2 periods).
- **Contributors:** 24, the top one Imran Qureshi (Owner, 2 periods, $1,200.00).

## Visual checks

| Page | 375px dark/light | 1440px dark/light | Notes |
|---|---|---|---|
| Settings form | ✓ | ✓ | Every input has a label. Grouped glass cards; the banner links when incomplete |
| Invoices list | ✓ | ✓ | Table stacks into cards on mobile; tiles wrap |
| Invoice | ✓ | ✓ | **A4 print emulation (794px):** sidebar, top bar and actions hidden; 24 rows; 2 pages with rows kept whole and the header row repeated |
| Owner income | ✓ | ✓ | Tiles, chart, legend and contributors; no overflow; series contrast as above |

All pages, in both themes:
- no horizontal overflow
- exactly one `<h1>`
- no text below 4.5:1 (computed against the composited glass background)
- no touch target below 40px on mobile.

## Self-review

**Security:**
- The new controllers are `AdminOnly`. Every mutation is a POST with antiforgery.
- No inline `<script>`, `style=""` or `on*=` attributes, and no `Html.Raw`.
- EF-parameterized queries; view models only.
- The audit logs field names and IDs, never bank details, IBAN, emails or phone numbers.
- Invoice and owner-income data never reach a Manager (403 tested).

**Accessibility:**
- labels tied to inputs; confirm modals for paid/unpaid;
- the chart has a title, description and data table;
- focus rings come from the shared styles;
- keyboard-operable filters and prev/next links.

## Known issues and deviations

- **No SPEC deviations.** Two design notes:
  - A pending invoice issued later still carries the **finalize date** as its issue date (SPEC §7 "IssueDate = finalize date"). Its due date counts from that date.
  - A draft run that previously had an invoice (now void) can't be deleted, because the void invoice keeps its history.
- Finalized runs from before M8 (none in the dev DB) keep their frozen Owner earning of 0 until reopened and re-finalized.

## Manual checks for the owner

1. Open **Settings**. Enter your business, bank and client details and set the invoice prefix. The "can't issue invoices" banner should disappear.
2. Finalize a payroll. The run page should link to the new invoice.
3. Open the invoice and print it (Ctrl+P, A4). Check it reads as a client document: no internal terms, bank details present, total correct.
4. Mark it paid, then try to reopen the payroll. It should be refused until you mark it unpaid.
5. Reopen an unpaid payroll and re-finalize it. The old invoice should show as void and link to the replacement.
6. Open **Owner income**. Check the month view against your own expectation for the Owner, commission and margin figures, in both themes.
7. Add a new person whose joining date falls inside a finalized period. The confirmation checkbox should be required.
