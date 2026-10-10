# M7 report: payroll engine

Status: **done**. 442/442 tests green (0 warnings). The dev DB is migrated. The full HTTPS cycle passed on a disposable demo DB. Visual checks were done at 375px and 1440px in both themes, plus print emulation at A4.

## Golden values as stored by the tests (rate 280)

Every number below is asserted twice:
- by the calculator unit tests (`PayrollCalculatorTests`);
- on the **stored payroll lines** after generating real runs through the web app (`PayrollTests.Golden_*`).

| # | Line | Payable / working | BilledUsd | InvoiceUsd | PayUsd | PayPkr | NetPayUsd | NetPayPkr | OwnerUsd | OwnerPkr |
|---|---|---|---|---|---|---|---|---|---|---|
| G1 | CompanyRecommended, Oct 1–15 | 11 / 11 | 175.00 | 175.00 | 150.00 | 42,000 | 150.00 | 42,000 | 25.00 | 7,000 |
| G2 | joined Thu Oct 8, Oct 1–15 | 6 / 11 | 95.46 | 95.46 | 81.82 | 22,910 | 81.82 | 22,910 | 13.64 | 3,819 |
| G3 | Oct 5 half, Oct 7 full, Oct 1–15 | 10.5 / 11 | 167.04 | 167.04 | 143.18 | 40,090 | 143.18 | 40,090 | 23.86 | 6,681 |
| G4 | BudgetHire, Oct 1–15 | 11 / 11 | 500.00 | 500.00 | 350.00 | 98,000 | 350.00 | 98,000 | 150.00 | 42,000 |
| G5 | same person, Oct 16–31 | 9 / 11 | 409.09 | 409.09 | 286.36 | 80,182 | 286.36 | 80,182 | 122.73 | 34,363 |
| G6 | Owner, Oct 16–31 | 11 / 11 | 600.00 | 600.00 | 600.00 | 168,000 | 600.00 | 168,000 | 0.00 | 0 |
| G7 | leaving Wed Oct 21, Oct 16–31 | 4 / 11 | 63.64 | 63.64 | 54.55 | 15,274 | 54.55 | 15,274 | 9.09 | 2,545 |
| G8 | G5 + Rs 5,000 reimb., $20 bonus, Rs 1,000 deduction | 9 / 11 | 409.09 | **443.38** | 286.36 | 80,182 | **320.65** | **89,782** | **122.73** | **34,364** |
| G9 | G1 + 1 extra day | 12 / 11 | 190.91 | 190.91 | 163.64 | 45,819 | 163.64 | 45,819 | 27.27 | 7,636 |
| G10 | G5 person + 1.5 extra days | 10.5 / 11 | 477.27 | 477.27 | 334.09 | 93,545 | 334.09 | 93,545 | 143.18 | 40,091 |

Also asserted:
- **G8 adjustment conversions:** Rs 5,000 → $17.86; $20 → Rs 5,600; Rs 1,000 → $3.57.
- **G8 owner earning:** the USD figure equals the G5 base (pass-through); the PKR figure is Rs 1 higher.
- **Owner income Oct 16–31:** G6 + a full-period CompanyRecommended line + G5 = **$747.73**.
- **Snapshot:** the stored lines don't change after renaming the person, changing the exchange-rate history (280 → 300, plus a new 310 entry) or trying to edit the pay record (refused: locked).

## Part A: Follow-ups

### A1. Active status follows the leaving date (design)

**Design:**
- The stored `IsActive` column is **dropped**. Active ⇔ no leaving date, or a leaving date ≥ today (Asia/Karachi).
- The rule lives in one place, `HR.Domain.People.PersonStatus`:
  - `ActiveOn(today)` / `InactiveOn(today)` are expression trees, so EF translates them to SQL;
  - `IsActive(…)` and `IsLeaving(…)` are the in-memory versions.
- `Person.IsActiveOn(today)` uses the same rule. Every former use now goes through it:
  - the People list filter and counts
  - the dashboard tiles ("Active people", "No pay setup", "Hire source not assigned")
  - the Salaries overview and the billing-review count
  - the Absences status filter and person pickers
  - the demo seeder's Owner guard
  - the Owner rule.

**Schema (migration `AddPayroll`):**
- The M3 check constraint `CK_People_InactiveHasLeavingDate` and the filtered unique index `UX_People_ActiveOwner` are dropped. "Active" now depends on today, which no index can express.
- **At most one active Owner** is checked in `PersonService` (set hire source, reactivate). A new trigger, `TR_People_SingleActiveOwner` (THROW 51002), recounts active Owners on every insert or update using today in UTC+05:00, as a safety net.
- New indexes on `LeavingDate`, `FullName` and `HireSource`.
- The migration's Down rebuilds `IsActive` from `LeavingDate`.

**UI:**
- While the leaving date hasn't passed, a person shows **Active** plus a **"Leaving 21 Oct 2026"** pill ("Leaving today" on the day). They count as active everywhere.
- The details page fact reads "Leaving" instead of "Left".

**Actions:**
- **Deactivate** sets a leaving date, which may be in the future. It is offered only when there is none.
- **Cancel leaving** (POST, ManagerOrAdmin, confirm modal) is offered only while the leaving date is today or later. It **reopens the same employment period** (clears its end date; no new period) and is audited as event 1105.
- **Reactivate** (rejoin) is offered once the leaving date has passed.

### A2. SPEC updated as instructed

- §5:
  - the new f line;
  - a new "Extra days" paragraph;
  - the "Adjustments" section replaced, with formulas in a code block.
- §7: invoice amount = `InvoiceUsd`.
- §8: the new formula; the "adjustments" breakdown item is gone.
- §9: G8, G9 and G10. **Note:** §9 had no G8 to replace, and these rows need columns the G1–G7 table doesn't have (InvoiceUsd, NetPay). They are in a second table under the first, with the same rate.
- §11: Q2 and Q3 resolved; Q1 left open.
- Change log entry added.
- §2's Person paragraph now describes the derived active status and "cancel leaving". The change log names this change; no other rule moved.

## Part B: Domain (`HR.Domain/Payroll`)

**`PayrollCalculator.Calculate(PayrollInput)`** implements SPEC §5 exactly.

Calculation:
- The days come from M6's `PaidLeaveAllocator` / `PayableDays` (with the whole month's absences), plus extra days.
- Every amount is `amount × payable / working`; multiplying first keeps the only inexact step at the final division.
- Each component is rounded (AwayFromZero) before it is summed.
- Adjustments are converted per SPEC, with s = −1 for deductions.

The result carries every intermediate value:
- WorkingDays, EmployedWorkingDays, UnpaidDays, ExtraDays, PayableDays
- SalaryPartUsd, CommissionUsd, BilledUsd
- PayUsd, PayPkr
- adjustment totals in PKR and USD, plus each converted adjustment
- NetPayPkr, NetPayUsd, InvoiceUsd
- OwnerEarningUsd, OwnerEarningPkr
- the period's absences with their paid/unpaid split.

Without a rate:
- Only the unconverted amounts are known (for example USD billing and PKR pay); everything that needs the rate is null.
- The run is flagged "Set exchange rate" and can't be finalized.

**Line issues** (block finalize):
- `NoHireSource` and `NoRateRecordAtPeriodStart`.
- A record that starts Oct 16 does not cover an Oct 1–15 line.
- Managers see both as **"Pay setup incomplete"**.

**Rules:**
- `ExtraDaysRules`: 0.5–10 in steps of 0.5; 0 means none and is used only to clear.
- `AdjustmentRules`: USD 0.01–100,000 with cents; PKR 1–50,000,000 in whole rupees.
- `OwnerIncome.Usd/Pkr` implements SPEC §8.

## Part C: Data (migration `20261010040220_AddPayroll`)

### Tables

**`PayrollRuns`:**
- Columns:
  - `PeriodStart` (unique) and `PeriodEnd`
  - `Status` (Draft/Finalized)
  - `ExchangeRate decimal(18,4)` (null until set)
  - `ExchangeRateEntryId`, `RateOverridden`, `RateNote`
  - generated and finalized at/by
  - `RowVersion`
- Checks: period start on the 1st/16th; rate > 0; finalized ⇒ rate and time set.

**`PayrollRunEvents`:**
- Generated, Regenerated, RateChanged ("old → new"), Finalized, Reopened (with the reason).
- Each with actor and time.

**`PayrollLines`:** a **full snapshot**, unique (RunId, PersonId):
- person: code, name, designation, type, hire source, bank name, IBAN;
- rate record: id and its four values;
- extra days (`decimal(5,2)`, checked 0 or 0.5–10 in steps of 0.5) and their note;
- every calculator output;
- the issue, an orphan flag and a RowVersion.

**`PayrollLineAbsences`:**
- The period's absences with their paid/unpaid split, frozen with the line.
- The payslip reads them from here, never from live data.

**`PayrollAdjustments`:**
- Type, Amount, Currency, Note.
- Stored `AmountPkr` / `AmountUsd`: recomputed while Draft, frozen at finalize.
- Audit fields, RowVersion. No billable flag.
- A check constraint enforces the amount limits.

### Database-level protection
- `TR_PayrollRuns_NoDeleteFinalized` (THROW 51010): a finalized run can't be deleted, even by raw SQL.
- `TR_PayrollLines_Frozen`, `TR_PayrollAdjustments_Frozen` and `TR_PayrollLineAbsences_Frozen` (THROW 51011): no insert, update or delete on a finalized run's rows.
- Tests prove both with raw SQL.

### `IPayrollLock`
- The stub is replaced by `PayrollLock`: a period is locked ⇔ its run is Finalized.
- The interface gained `LatestLockedPeriodStartAsync`, plus a `FirstLockedAsync(from, to)` helper used for employment changes.

**Employment changes are locked** when they would add or remove employed days in a finalized period:
- a leaving date (removes days after it);
- cancel leaving (adds days after it);
- reactivate (adds days from the rejoining date);
- moving the joining date (the days between the old and new dates).

Each is refused with: "This change would alter the finalized payroll for 01 Oct–15 Oct 2026. An administrator has to reopen that payroll first."

## Part D: Workflow

**Generate** (ManagerOrAdmin):
- Default period: the one after the latest finalized run, else the current period.
- A period earlier than the latest finalized run is refused; an existing period opens the existing run.
- Lines are made for everyone with ≥ 1 employed working day, from employment periods, whatever their active status.
- Proposed rate: the history rate in effect on `PeriodEnd`; with none, the draft is flagged "Set exchange rate".

**Recalculate** (Draft):
- Rebuilds every line from current data. Extra days and adjustments stay with the person.
- Someone no longer employed but with extra days or adjustments keeps an **orphaned** line: listed in a warning, blocking finalize, removed when its last entry is deleted.
- The page shows what changed, line by line. Billing differences are shown only to Admins.

**Change rate** (Draft):
- Choose the history rate, or override with a rate and a note.
- A note is required whenever a rate was already set.
- PKR values are recalculated.

**Extra days and adjustments** (Draft):
- Extra days need a note listing the dates.
- Adjustments can be added, edited (RowVersion) and deleted.

**Finalize** (confirm modal) is refused unless all of these hold:
- no line issues and no orphans
- a rate is set
- no earlier period has a Draft
- the period is not before the latest finalized one
- **a fresh recalculation inside a Serializable transaction matches the stored draft exactly.**

If the recalculation differs, finalize is refused with "Data changed since this draft was calculated". The draft is recalculated and saved, and the per-line changes are shown (for example "Unpaid days 0.00 → 1.00").

**Reopen:**
- AdminOnly, with a reason (10–500 characters, kept in the run history, never logged).
- Only the latest finalized run.

**Delete draft:**
- Draft only (confirm modal).
- Finalized runs are refused by the service and by the trigger.

**Audit events (category `HR.Security`):**

| Event | Action | Details |
|---|---|---|
| 1500 | generate | |
| 1501 | regenerate | lines / changed / orphaned |
| 1502 | rate | old → new |
| 1503 | extra days | old → new |
| 1504 / 1505 / 1506 | adjustments | old → new |
| 1507 | finalize | |
| 1508 | finalize refused | |
| 1509 | reopen | |
| 1510 | delete | |
| 1105 | cancel leaving | |

Notes, the rate note and the reopen reason are never logged (tested).

## Part E: Pages (sidebar "Payroll" is live)

**`/payroll`:**
- period, status pill, people, total net pay;
- Admin also sees total invoice and their earning;
- "New payroll" opens a month + half picker pre-set to the default;
- empty state.

**`/payroll/{id}` (run page):**
- Header: working days, people, the exchange rate with its source ("from the rate history (since …)" or "Overridden" plus the note), generated and finalized by/at.
- Actions: Register, Print all payslips, Recalculate, Delete draft, Finalize, and Reopen (Admin, latest finalized only).
- "Set exchange rate" banner and an editable rate panel (Draft).
- Panels: issues (each with a link to the person), orphans, and the recalculation changes.
- Lines table:
  - Managers: person (name, code · type), payable days ("9 of 11", "10.5 of 11 incl. 1.5 extra"), monthly pay, period pay (PKR, plus USD for USD pay), adjustments, net pay; totals footer; mobile cards.
  - Admins also get: hire source pill, billed, invoice, and their earning in USD and PKR, with totals.
  - **The Manager query never selects billing columns:** `LinesAsync` projects pay fields only, and `BillingAsync` is called for Admins only.
- History of events.

**`/payroll/{id}/lines/{lineId}` (line page):**
- Plain-language days (working, employed, − unpaid, + extra = payable).
- The period's absences with their paid/unpaid split.
- The pay calculation written out, for example "Rs 196,000 ÷ 2 × 10.5/11 = Rs 93,545", with the reference conversion.
- Base pay, each adjustment and net pay.
- Extra-days form (with Clear), and the adjustments table and form.
- Admin only: a "Billing and your earning" breakdown (salary part, commission, billed, pass-through, invoice, earning in USD and PKR).

**Payslip (`…/payslip`) and "Print all payslips" (`/payroll/{id}/payslips`):**
- issuer from `Payslip:IssuerName` (default "HR Payroll");
- code, name, designation, type, period;
- working, employed, paid-leave and unpaid days, extra days, payable days;
- monthly and base pay, each adjustment, net pay in PKR;
- the rate used and the USD reference;
- "Draft: not final" on drafts;
- **no billing for any role** (tested for Admin too);
- a Print button using `data-print` (no inline handler).

**Register (`/payroll/{id}/register`):**
- code, name, bank, full IBAN (grouped), net pay, total.
- Manager and Admin.

**`print.css`** (linked with `media="print"` on every page):
- A4 with 14/12 mm margins;
- hides the aurora, sidebar, top bar, toasts, modals and buttons;
- black on white, no glass, blur or shadows;
- tables stay ruled tables (not mobile cards);
- each payslip starts on a new page.

**Dashboard:**
- "Payroll 01–15 Oct 2026: Not started / Draft / Finalized", with a link.
- Admin also sees "Latest finalized payroll: $X invoiced · $Y your earning".

## Migrations added

- `20261010040220_AddPayroll`:
  - payroll tables, indexes, checks and triggers;
  - People: `IsActive` dropped (with its check constraint and filtered Owner index), the Owner trigger and new indexes added.
- **No separate Part A migration:** both changes went into the one model diff, under the name your prompt specified.

## Tests: 442 passed, 0 failed, 0 skipped

273 unit + 169 integration. M6 ended at 389; M7 added 53.

| Class | Tests | Covers |
|---|---|---|
| `PayrollCalculatorTests` | 35 | **G1–G10 exactly**, including every G8 output and conversion; **owner income $747.73**; **G8 USD earning = G5, PKR +1**; issues (no source; no record; an Oct 16 record doesn't cover Oct 1–15); **extra-day limits (0, 0.25, 10.5, −1 rejected; 0.5, 2.5, 10 accepted)** and the calculator refusing 0.25; adjustment limits (10 cases); **a rate change recomputes PKR** (USD and PKR pay); no-rate behaviour |
| `PayrollTests` | 14 | **End-to-end G1–G8 on stored lines, finalize both, snapshot unchanged** after editing person / exchange rate (pay-record edit refused); **G9/G10 through the extra-days form**, extra-day limits over HTTP, Clear; **generation defaults** and refusal of a period before the latest finalized one, existing period → existing run; line eligibility (left before, weekend joiner, joins later) and the missing-rate flag; **finalize refused**: no rate, issues, earlier draft (out of order), **data changed with the per-line diff shown**, then success; **regenerate keeps extra days and adjustments, orphan warning blocks finalize until removed**; **rate override needs a note and recomputes PKR**; **reopen**: Manager 403, latest only, reason ≥ 10, reason kept but not logged, finalize again; **delete draft OK; finalized refused by the service and by raw SQL (51010), frozen lines (51011)**; **locked period refuses absence create/edit/delete, pay record create/edit/delete, leaving date inside/before, rejoin into, joining date across; exchange-rate edits allowed and the run keeps 280**; **Manager responses on list, run, line, payslip, payslips, register and dashboard contain none of the billing words, hire-source names or distinctive billed values; Admin payslips have none either**; register total Rs 413,000; **POST-only, antiforgery, audit 1500–1507 with old → new, notes never logged**, anonymous → login; **no inline script/style/handlers, one `<h1>`, print.css served (A4, page breaks)**; **a 25-person recalculation diff doesn't break the session** (regression) |
| `PersonStatusTests` | 4 | **Future leaving date → active, "Leaving 21 Oct 2026" / "Leaving today" pill, counted on the dashboard; the day after → inactive** (fixed clock); **cancel leaving reopens the same period (same id, end date cleared)**, POST-only, audit 1105; cancel refused after the date and when a locked period is affected (lock double); **the Owner rule follows the new definition** in the service and in the trigger (51002) |
| Updated | — | Fixture resets payroll runs first; `IsActive` assertions now use `IsActiveOn(today)`; the 23 → 24 active demo count (Rizwan leaves 21 Oct, so he is still active); sidebar test (Payroll live); test lock doubles implement `LatestLockedPeriodStartAsync` |

## Verification

- `dotnet build --no-incremental`: **0 warnings**.
- `dotnet test`: **442/442**.
- `dotnet ef database update` on `HRPayroll`: `AddPayroll` applied.

**HTTPS smoke test, full cycle** on a disposable `HRPayroll_Visual` DB with demo data. The DB, the QA launch config and the credentials were removed afterwards.
- Anonymous `/payroll` → login.
- Dashboard tile "Payroll 01–15 Oct 2026 · Not started". Rizwan shows "Leaving 21 Oct 2026".
- The Manager generated Oct 1–15: **24 lines, 13 issues**. Finalize was refused ("Some lines have problems").
- The Admin set hire sources and pay. The Manager recalculated ("117 values changed"): **0 issues**.
- Extra day for Ayesha: "12 of 11 incl. 1 extra". Bilal got Rs 5,000 reimbursement, $20 bonus and Rs 1,000 deduction:
  - net **Rs 107,600**;
  - Admin sees invoice **$534.29**, earning **$150.00 / Rs 42,001**.
- Rate override 281 (net Rs 107,620), then back to the history rate.
- **Finalized**: total net Rs 2,017,619.
- Locked:
  - absence rows show "Locked";
  - editing Kamran's Oct 5 absence → "That date is part of a finalized payroll…";
  - adding an Oct 6 absence → the same refusal;
  - extra days → "This payroll is finalized…";
  - a leaving date of Oct 9 → "This change would alter the finalized payroll for 01 Oct–15 Oct 2026…".
- Manager reopen → **403**. Admin reopen with a reason → **Draft**. Finalize again → **Finalized**.
- All payroll pages return 200 for both roles. `/css/print.css` returns 200.
- Delete finalized → "A finalized payroll can never be deleted."
- **Manager leaks: none.** Register total Rs 2,017,619.
- Admin dashboard: "Finalized", plus "Latest finalized payroll $9,237.70 invoiced · $2,031.90 your earning".

**Visual checks** (in-app browser, 375px mobile emulation and 1440px, light and dark):
- Pages: payroll list; run page as Admin (with the issues panel) and as Manager; line breakdown with extra days and adjustments; payslip on screen; register.
- Scripted audit at 375px in both themes on run, line, payslip and register: **no horizontal overflow, no contrast failures, no small touch targets, one `<h1>`**.
- Manager page, live in the browser: no billing words.
- **Fixed during the check:** at 1440px the Admin lines table needed 1,307px in a 1,100px column.
  - Code and type moved under the name.
  - The hire source became a pill in the Person cell.
  - Compact cell padding on that table at ≥ 768px.
  - It now fits exactly (1,100px) with no scroll. Managers get 7 columns, Admins 10.

**Print preview:** the pane can't open Chrome's print dialog. I applied `print.css` to the screen at the A4 viewport (794 × 1123) and measured. Results:
- **Payslip:** white page, black text. Aurora, sidebar, top bar and buttons hidden (computed `display: none`). Ruled tables. The whole payslip (with three adjustments) is **639px tall, so it fits one A4 page**.
- **Register:** 24 rows plus the total on **one A4 page**, as a ruled table (not mobile cards). IBANs grouped on one line, money right-aligned.
- "Print all" puts each payslip on its own page (`break-before: page`, asserted by a test). Your Chrome "Save as PDF" is in the manual checks below.

**Security and accessibility self-review:**
- Every new mutation is POST with the global antiforgery filter.
- Reopen is `AdminOnly`; everything else is `ManagerOrAdmin`.
- Manager queries never select billing.
- Adjustment edits use RowVersion.
- EF only (the triggers are DDL in the migration).
- No `Html.Raw`, no inline script, style or handlers. Printing uses `data-print` in `site.js`.
- Notes and reasons are never logged.
- A recalculation diff now lives in server memory (10 minutes, per user) with only a random key in TempData. This fixed a real bug the smoke test found: a 13-person diff overflowed the TempData cookie and broke the Manager's session.
- Labels are tied to inputs; the period picker and rate source are radio groups with legends. Status is shown as text, not colour alone.

## Known issues and deviations

**No SPEC deviations.** Design choices within SPEC:
1. **Active status:** "Cancel leaving" and the "Leaving" pill apply while the leaving date is today or later, because the person is still active on their last day. Reactivate (rejoin) appears only after the date has passed.
2. **Orphaned lines** (extra days or adjustments for someone no longer employed in the period) **block finalize** until they are removed. Paying them silently didn't seem safe. Tell me if you'd rather allow it.
3. **Negative net pay** (a deduction larger than pay) isn't blocked: SPEC has no rule for it. Say if you want one.
4. **People created after a finalized period** who joined before it are not added to that run. A later leaving date for them that would cut into the finalized period is refused like any other employment change.
5. **Changing the rate needs a note** once a rate exists, including switching back to the history rate, so the run history always explains it.

Tooling: the browser pane can't drive the native print dialog, so print was emulated (see above).

## Manual checks for the owner

1. **Payroll → New payroll:** the picker suggests the current period. Generate it. People without pay setup appear in the issues panel with links. Fix one, click **Recalculate**: the "What changed" panel lists it.
2. Open a line:
   - Add **1.5 extra days** (note "Sat 17 Oct, Sun 18 Oct half"): the days read "… incl. 1.5 extra".
   - Add a Reimbursement in PKR and a Bonus in USD: net pay changes by exactly those amounts.
   - As Admin, the billing card shows the invoice moving by the same amount and your earning in USD unchanged.
3. Exchange rate panel: override with a note. PKR amounts change; the header shows "Overridden". Switch back to the history rate.
4. **Finalize.** Then try to edit an absence in that period, add a pay record dated in it, or set a leaving date inside it: all are refused with a clear message.
5. Sign in as a **Manager:** the run, line, payslip and register show no billed, invoice or earning values and no hire sources. Reopen isn't offered.
6. As Admin, **Reopen** with a reason: the reason appears in the run's History. Finalize again.
7. **Print in Chrome** (Ctrl+P → Save as PDF, A4):
   - a payslip and "Print all payslips": white, one payslip per page, no sidebar or buttons;
   - the register: one table with grouped IBANs and the total.
8. **People:** give someone a leaving date next week. They stay in the active list with a "Leaving …" pill and still count on the dashboard. **Cancel leaving** brings them back to the same employment period (Employment history still shows one period).
