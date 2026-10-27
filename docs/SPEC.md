# HR Payroll — Business Specification

This file is the source of truth for business rules. If code and this file disagree, the code is wrong.
Never change a rule here without the owner's explicit approval; record approved changes in the Change log at the bottom.

## 1. Context and roles

The **owner** runs a staffing arrangement with **one client company** ("the Company").
The owner places people (employees and internees) at the Company, pays them in PKR every 15 days, and bills the Company in USD.
The owner is also an employee placed at the Company.

| Role | Who | Can do |
|---|---|---|
| **Admin** | The owner | Everything. Manages Manager accounts. Only role that sees billing, commission, margin, invoices and owner income. |
| **Manager** | Staff the owner appoints | People (add, edit, list, activate/deactivate), absences, pay amounts and increments, exchange rates, payroll (draft, adjustments, finalize), payslips. **Never** sees billed amounts, commissions, margins, hire source economics, invoices or owner income. |

The visibility rule is enforced on the server (queries, view models, authorization policies), not just by hiding UI elements.

## 2. Core concepts

**Person**: an Employee or an Internee (`PersonType`). Has one or more employment periods (start date, optional end date; never overlapping; at most one open). Joining/leaving date shown in the UI are those of the latest period. Deactivate closes the open period (the leaving date may be in the future); reactivate opens a new one; cancelling a leaving date that hasn't passed reopens the same period. Also has a designation and contact details. A person is **active** until their leaving date has passed: no leaving date, or a leaving date on or after today (Asia/Karachi). Nothing about it is stored. Changes to employment that alter days a person was paid for in a finalized payroll are refused. Adding employment that covers a finalized payroll period the person is not in (a new person, rejoining, an earlier joining date) needs an explicit confirmation; any arrears are paid as a Bonus or extra days in the current payroll.

**Hire source** (`HireSource`, Admin-only field):

| Source | Meaning | Admin enters (monthly USD) | Derived | Owner earns |
|---|---|---|---|---|
| `CompanyRecommended` | Company found the person and set the salary | Billed + Commission (default commission $50 for the first record, else the previous one); the commission must be below the billed amount | Salary = Billed − Commission | The commission |
| `BudgetHire` | Company gave a budget; owner hired someone for less | Billed (the budget) + Salary | Commission = Billed − Salary (below 0 needs the "I understand this hire loses money" confirmation) | The commission (the margin) |
| `Owner` | The owner himself | Salary | Commission = 0, Billed = Salary | His whole billed amount |

At most one active person can have source `Owner`.

**Every salary is agreed in USD per month.** Everything is billed in USD and paid in PKR at the payroll's exchange rate. There is no pay currency.

**Rate record** (per person, history, never overwritten): `EffectiveFrom` (must be the 1st or 16th of a month) and three monthly USD figures: `SalaryMonthlyUsd`, `CommissionMonthlyUsd`, `BilledMonthlyUsd`.
- Invariant: `BilledMonthlyUsd = SalaryMonthlyUsd + CommissionMonthlyUsd` (a database check constraint).
- The server always derives the third figure from the two the source asks for; posted derived values are ignored.
- The form shows all three on a monthly basis, with the derived one read-only and updating live, plus a "per pay period" preview showing each figure halved, with PKR at today's rate labelled as an estimate.
- An increment = a new rate record. The person is paid the salary.
- **Manager increments**: the Manager enters the new monthly salary only.
  - `CompanyRecommended`: the commission is carried over, billed re-derived.
  - `BudgetHire`: billed (the budget) is carried over, the commission re-derived, and the record is flagged `NeedsBillingReview` for the Admin. A Manager never sees the loss warning; the Admin review covers it.
  - `Owner`: billed = salary.
  - Manager pages show the salary (USD) and its PKR estimate only, never commission, billed or budget.
- The rate record in effect on a period's start date applies to the whole period.

**Exchange rate**: history of `EffectiveFrom` date and `UsdToPkr` (decimal, 4 dp). The rate in effect on the period end date is proposed for a payroll. Admin or Manager may override it before finalizing. Finalizing locks it.

## 3. Pay periods and working days

- Two periods per month: the 1st–15th and the 16th–last day of the month.
- **Working days** = Monday–Friday dates in the period. Saturdays and Sundays are never working days.
- **Employed working days** = working days that fall inside any of the person's employment periods.
- Dates are calendar dates (`DateOnly`). "Today" means today in the Asia/Karachi time zone.

## 4. Absences and paid leave

- An absence is recorded per date (`Full` = 1.0 day, `Half` = 0.5 day) with an optional note. A date range is a convenience that creates one record per working day.
- Absences may not be recorded on a Saturday or Sunday, on a date outside every employment period, or twice on the same date.
- **Paid leave: 1.0 day per calendar month per person (employees and internees).** It is applied automatically and chronologically to the first absences in that month, across both periods. A half day uses 0.5; a following full-day absence is then 0.5 paid and 0.5 unpaid. Unused paid leave expires at month end and never carries over.
- Paid/unpaid status is computed, never entered by hand.
- Absences inside a **finalized** payroll period are locked (cannot be added, edited or deleted). This keeps the chronological paid-leave allocation stable.
- A paid-leave day is billed to the Company in full.

## 5. Line calculation (per person, per period)

```
f            = (employed working days − unpaid absence days + extra days) / working days in period
SalaryUsd     = round2(SalaryMonthlyUsd / 2 × f)
CommissionUsd = round2(CommissionMonthlyUsd / 2 × f)
BilledUsd     = SalaryUsd + CommissionUsd                   ← one amount per person on the invoice
PayUsd        = SalaryUsd
PayPkr        = round0(PayUsd × rate)

OwnerEarningUsd = InvoiceUsd − NetPayUsd          (Owner lines: InvoiceUsd)
OwnerEarningPkr = round0(InvoiceUsd × rate) − NetPayPkr   (Owner lines: round0(InvoiceUsd × rate))
```

Without adjustments, InvoiceUsd = BilledUsd and NetPay = Pay, so the earning is the commission (and the Owner's whole billed amount on his own line).

- Rounding is always `MidpointRounding.AwayFromZero`. USD to 2 decimals, PKR to whole rupees.
- Round each component first, then add (this is why `BilledUsd` is `SalaryUsd + CommissionUsd`).
- Lines finalized before v1.1.0 keep their own snapshot (billed salary part, commission per period, pay amount and currency) and are never recalculated; draft runs are recalculated when the pay model changes.
- Use `decimal` everywhere. Never `double` or `float` for money or rates.
- A person with 0 employed working days in a period gets no line.

### Extra days
Extra days worked (e.g. a weekend or holiday) are entered on a payroll line in steps of 0.5, from 0.5 to 10. They add to payable days at the normal daily rate, so billing, commission/margin and pay all scale with them. f may exceed 1.

### Adjustments
Types: Bonus, Reimbursement, Deduction. Amount, currency (USD or PKR), note. Not prorated. Every adjustment passes through to the Company at cost: the invoice changes by exactly the amount the person's pay changes, and the owner takes no commission on it. With the payroll's rate and sign s = −1 for Deduction, +1 otherwise:

```
AmountPkr = PKR ? Amount : round0(Amount × rate);  AmountUsd = USD ? Amount : round2(Amount / rate).
NetPayPkr = PayPkr + Σ s·AmountPkr;  NetPayUsd = PayUsd + Σ s·AmountUsd.
InvoiceUsd = BilledUsd + Σ s·AmountUsd.
Final OwnerEarningUsd = InvoiceUsd − NetPayUsd (always equals BilledUsd − PayUsd);  Final OwnerEarningPkr = round0(InvoiceUsd × rate) − NetPayPkr (may differ from the base by a rupee or two of rounding).
```

For Owner lines, Final OwnerEarningUsd = InvoiceUsd and Final OwnerEarningPkr = round0(InvoiceUsd × rate).

## 6. Payroll lifecycle

1. **Draft**: generated for one period (unique per period). Includes every person with at least one employed working day. Can be regenerated; regenerating keeps adjustments.
2. **Review**: add or edit adjustments, adjust the proposed exchange rate.
3. **Finalize**: snapshot everything onto the lines (rates, days, amounts, exchange rate, who/when). A finalized payroll is read-only forever; later changes to people, rates or exchange rates never alter it. Only Admin can reopen a finalized payroll, with a mandatory reason recorded in the payroll's own history (shown on the run page); the audit log records who reopened which run and when, but never the reason text.

Outputs: payroll register (PKR, Manager and Admin), payslips per person (PKR with USD reference), company invoice (Admin), owner income (Admin).

## 7. Company invoice (Admin only)

One invoice per finalized payroll. One row per person: name, designation, **amount in USD** (= `InvoiceUsd`).

The invoice is issued when the payroll is finalized (if the business and client names are set in Settings), numbered `<prefix>-YYYY-NNNN` per year, dated on the finalize date and due after the payment terms. Reopening the payroll voids its invoice (refused while it is paid); finalizing again issues a replacement that refers to the voided one. Rows show the salary part (`BilledUsd`), extras (Σ s·AmountUsd) and the amount (`InvoiceUsd`). **Commission is never shown as a separate line**; it is already inside the amount. The owner's own line appears like any other. Invoice total = sum of rows.

When an invoice is marked paid with an amount received that differs from its total, it shows "Short by $X" or "Over by $X". Outstanding = Σ totals of unpaid (issued) invoices + Σ shortfalls on invoices paid short; amounts received over the total are shown separately as "Received in excess".

## 8. Owner income (Admin only)

```
OwnerIncome(period) = Σ final OwnerEarning over all lines; broken down as own salary (Owner lines), commission (CompanyRecommended lines) and margin (BudgetHire lines).
```
Shown by period, month and year, broken into: own salary, commission, margin, total, in USD and PKR.

## 9. Golden test cases

These must exist as unit tests of the calculator, with exactly these numbers. All use **rate 280**. In October 2026 both periods have 11 working days (Oct 1 is a Thursday; Oct 31 is a Saturday).

Monthly USD figures (pay model v1.1.0): CompanyRecommended = Salary $300 + Commission $50 (Billed $350); Bilal (BudgetHire) = Salary $700, Billed $1,000, Commission $300; Owner = Salary $1,200. Bilal is absent Tue Oct 6, Tue Oct 20 and Tue Oct 27 (all full days).

| # | Person | Period | Inputs | f | SalaryUsd | CommissionUsd | BilledUsd | InvoiceUsd | PayPkr | NetPayPkr | NetPayUsd | OwnerUsd | OwnerPkr |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| G1 | CompanyRecommended | Oct 1–15 | full period | 11/11 | 150.00 | 25.00 | 175.00 | 175.00 | 42,000 | 42,000 | 150.00 | 25.00 | 7,000 |
| G2 | CompanyRecommended | Oct 1–15 | as G1, joined Thu Oct 8 | 6/11 | 81.82 | 13.64 | 95.46 | 95.46 | 22,910 | 22,910 | 81.82 | 13.64 | 3,819 |
| G3 | CompanyRecommended | Oct 1–15 | as G1; absent Mon Oct 5 (half), Wed Oct 7 (full) | 10.5/11 | 143.18 | 23.86 | 167.04 | 167.04 | 40,090 | 40,090 | 143.18 | 23.86 | 6,681 |
| G4 | BudgetHire (Bilal) | Oct 1–15 | the Oct 6 absence is paid leave | 11/11 | 350.00 | 150.00 | 500.00 | 500.00 | 98,000 | 98,000 | 350.00 | 150.00 | 42,000 |
| G5 | BudgetHire (Bilal) | Oct 16–31 | Oct 20 and Oct 27 unpaid | 9/11 | 286.36 | 122.73 | 409.09 | 409.09 | 80,181 | 80,181 | 286.36 | 122.73 | 34,364 |
| G6 | Owner | Oct 16–31 | full period | 11/11 | 600.00 | 0.00 | 600.00 | 600.00 | 168,000 | 168,000 | 600.00 | 600.00 | 168,000 |
| G7 | CompanyRecommended | Oct 16–31 | as G1, leaving date Wed Oct 21 | 4/11 | 54.55 | 9.09 | 63.64 | 63.64 | 15,274 | 15,274 | 54.55 | 9.09 | 2,545 |
| G8 | BudgetHire (Bilal) | Oct 16–31 | G5 + Reimbursement Rs 5,000, Bonus $20, Deduction Rs 1,000 | 9/11 | 286.36 | 122.73 | 409.09 | 443.38 | 80,181 | 89,781 | 320.65 | 122.73 | 34,365 |
| G9 | CompanyRecommended | Oct 1–15 | as G1 + 1 extra day | 12/11 | 163.64 | 27.27 | 190.91 | 190.91 | 45,819 | 45,819 | 163.64 | 27.27 | 7,636 |
| G10 | BudgetHire (Bilal) | Oct 16–31 | G5 + 1.5 extra days | 10.5/11 | 334.09 | 143.18 | 477.27 | 477.27 | 93,545 | 93,545 | 334.09 | 143.18 | 40,091 |

- G3: Oct 5 uses 0.5 paid leave; Oct 7 is 0.5 paid + 0.5 unpaid.
- G4/G5: Oct 6 uses October's paid leave, so Oct 20 and Oct 27 are unpaid.
- **G8 explained:** adjustments convert at 280: the Rs 5,000 reimbursement is $17.86, the $20 bonus is Rs 5,600, and the Rs 1,000 deduction is $3.57. So Σ s·AmountPkr = 9,600, Σ s·AmountUsd = 34.29, InvoiceUsd = 409.09 + 34.29 = 443.38, NetPayUsd = 286.36 + 34.29 = 320.65 and NetPayPkr = 80,181 + 9,600 = 89,781. OwnerUsd = 443.38 − 320.65 = 122.73, the same as G5. OwnerPkr = round0(443.38 × 280) − 89,781 = 124,146 − 89,781 = 34,365, which is Re 1 more than G5's 34,364 because of rounding.
- **G9, G10:** extra days raise f above 1 (12/11 and 10.5/11) and scale salary, commission and pay alike.
- **Owner income Oct 16–31** with G6 + one full-period CompanyRecommended person (as G1) + G5 = 600.00 + 25.00 + 122.73 = **$747.73**.
- **The owner's examples (full period, rate 280):**
  - CompanyRecommended, $750 + $50 → SalaryUsd 375.00, CommissionUsd 25.00, BilledUsd 400.00, PayPkr Rs 105,000.
  - BudgetHire, budget $1,000, salary $800 → SalaryUsd 400.00, CommissionUsd 100.00, BilledUsd 500.00, PayPkr Rs 112,000.

Period helper cases: Oct 8 2026 → Oct 1–15; Oct 16 2026 → Oct 16–31; Feb 20 2028 → Feb 16–29 (10 working days); Feb 16 2027 → Feb 16–28; Dec 31 2026 → Dec 16–31.

## 10. Roadmap

Each milestone follows the Milestone protocol in `CLAUDE.md` and ends with a commit.

| # | Milestone | Done when |
|---|---|---|
| M1 | Foundation, data layer, glass design system, security baseline | App runs on SQL Express, layout and style guide work in both themes, tests green |
| M2 | Authentication, roles, Manager management (Admin) | Login/logout, seeded Admin, Admin CRUD + activate/deactivate Managers, role-locked routes, lockout |
| M3 | People (employees/internees) | Add/edit/list/search/filter/page, activate/deactivate with leaving date, hire source Admin-only |
| M4 | Exchange rates | Rate history, current rate on dashboard, rate-for-date lookup |
| M5 | Rate records (billing, commission, pay) and increments | History per person, Manager sees pay side only, effective-date rules |
| M6 | Absences and paid leave | Record/edit/delete, ranges, automatic paid-leave allocation, locking |
| M7 | Payroll engine | Draft → adjustments → finalize, register, payslips, golden tests pass |
| M8 | Company invoice and owner income | Invoice per payroll, owner income dashboard |
| M9 | Reports and exports | Excel and PDF exports, history reports, dashboard summary |
| M10 | Security audit and hardening | OWASP Top 10 review fixed and tested, audit log, deployment guide |

## 11. Open questions (settle before the milestone that needs them)

1. (M6) Public holidays: no holiday calendar in v1. If the Company gives paid holidays, the Manager simply doesn't record absences on them. Confirm.
2. ~~(M7) Adjustment "billable" defaults (Reimbursement billable, Bonus not).~~ **Resolved 2026-10-10:** all adjustments pass through.
3. ~~(M7) PKR rounded to whole rupees.~~ **Resolved 2026-10-10:** PKR whole rupees.

## Change log
- 2026-10-08: Initial spec agreed with the owner.
- 2026-10-09: Employment periods replace single joining/leaving dates; Owner rule relaxed to at most one active Owner.
- 2026-10-10: extra days; adjustments pass through at cost; deductions credit the Company; active status follows leaving date.
- 2026-10-10 (M8): the Owner line's earning is its whole invoiced amount; owner income = Σ final OwnerEarning over all lines; deductions may not exceed pay (NegativeNetPay blocks finalize); people added after a finalized period need a confirmation (arrears paid in the current payroll); invoice and settings rules (§7).
- 2026-10-10 (M10): the reopen reason lives in the payroll history, not the audit log (§6); two-factor sign-in required for Admins; durable append-only audit log.
- 2026-10-10 (M9): short and over payments on paid invoices; outstanding includes shortfalls, overpayments shown as "received in excess" (§7).
- 2026-10-17 (v1.1.0, owner-approved pay model): every salary is agreed in USD per month; everything is billed in USD and paid in PKR at the payroll's rate, and `PayCurrency` is removed. A rate record holds `SalaryMonthlyUsd`, `CommissionMonthlyUsd` and `BilledMonthlyUsd` with Billed = Salary + Commission; the Admin enters two figures per hire source and the third is derived (§2). Line formulas SalaryUsd / CommissionUsd / BilledUsd / PayPkr (§5). Golden table replaced (§9): Bilal is now Salary $700, Billed $1,000, Commission $300; CompanyRecommended is Salary $300 + Commission $50/month. Existing records converted by migration `MonthlyUsdPayModel` (PKR pay at the exchange rate in effect, flagged for billing review); finalized payrolls and invoices are unchanged.
- 2026-10-18 (owner request): for `CompanyRecommended` the Admin enters the amount billed to the Company and the commission; the salary paid to the person is derived as Billed − Commission (§2). Manager increments are unchanged (new salary; commission carried over, billed re-derived).
