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

| Source | Meaning | Billed to Company per month | Commission per period | Paid to person per month | Owner earns |
|---|---|---|---|---|---|
| `CompanyRecommended` | Company found the person and set the salary | The salary (USD) | A per-person USD amount (e.g. $25), editable | The same salary (USD, disbursed as PKR) | The commission |
| `BudgetHire` | Company gave a budget; owner hired someone for less | The budget (USD) | 0 | The pay the owner agreed with the person (usually PKR) | Budget − pay (the margin) |
| `Owner` | The owner himself | His salary (USD) | 0 | The same salary | His whole billed amount |

At most one active person can have source `Owner`.

**Rate record** (per person, history, never overwritten): `EffectiveFrom` (must be the 1st or 16th of a month), `BilledMonthlyUsd`, `CommissionPerPeriodUsd`, `PayMonthlyAmount`, `PayCurrency` (USD or PKR).
- `CompanyRecommended`: `PayMonthlyAmount` = `BilledMonthlyUsd`, `PayCurrency` = USD (the UI fills it in automatically and keeps it in sync).
- `BudgetHire`: `CommissionPerPeriodUsd` = 0.
- `Owner`: `PayMonthlyAmount` = `BilledMonthlyUsd`, USD, commission 0.
- An increment = a new rate record. Managers only see and edit the pay fields; the billed and commission fields are Admin-only.
- When a Manager adds an increment, the Admin-only fields are carried over from the previous record (for `CompanyRecommended` and `Owner`, billed follows pay because they are equal by definition). The Admin is shown a "review billing" flag on rate records created by a Manager for `BudgetHire` people, because the budget may also have changed.
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
SalaryPart   = round2(BilledMonthlyUsd / 2 × f)
Commission   = round2(CommissionPerPeriodUsd × f)
BilledUsd    = SalaryPart + Commission                      ← one amount per person on the invoice

if PayCurrency = USD:  PayUsd = round2(PayMonthlyAmount / 2 × f);  PayPkr = round0(PayUsd × rate)
if PayCurrency = PKR:  PayPkr = round0(PayMonthlyAmount / 2 × f);  PayUsd = round2(PayPkr / rate)

OwnerEarningUsd = BilledUsd − PayUsd
OwnerEarningPkr = round0(BilledUsd × rate) − PayPkr
```

- Rounding is always `MidpointRounding.AwayFromZero`. USD to 2 decimals, PKR to whole rupees.
- Round each component first, then add (this is why `BilledUsd` is `SalaryPart + Commission`).
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
3. **Finalize**: snapshot everything onto the lines (rates, days, amounts, exchange rate, who/when). A finalized payroll is read-only forever; later changes to people, rates or exchange rates never alter it. Only Admin can reopen a finalized payroll, with a mandatory reason recorded in the audit log.

Outputs: payroll register (PKR, Manager and Admin), payslips per person (PKR with USD reference), company invoice (Admin), owner income (Admin).

## 7. Company invoice (Admin only)

One invoice per finalized payroll. One row per person: name, designation, **amount in USD** (= `InvoiceUsd`).

The invoice is issued when the payroll is finalized (if the business and client names are set in Settings), numbered `<prefix>-YYYY-NNNN` per year, dated on the finalize date and due after the payment terms. Reopening the payroll voids its invoice (refused while it is paid); finalizing again issues a replacement that refers to the voided one. Rows show the salary part (`BilledUsd`), extras (Σ s·AmountUsd) and the amount (`InvoiceUsd`). **Commission is never shown as a separate line**; it is already inside the amount. The owner's own line appears like any other. Invoice total = sum of rows.

## 8. Owner income (Admin only)

```
OwnerIncome(period) = Σ final OwnerEarning over all lines; broken down as own salary (Owner lines), commission (CompanyRecommended lines) and margin (BudgetHire lines).
```
Shown by period, month and year, broken into: own salary, commission, margin, total, in USD and PKR.

## 9. Golden test cases

These must exist as unit tests of the calculator, with exactly these numbers. All use **rate 280**. In October 2026 both periods have 11 working days (Oct 1 is a Thursday; Oct 31 is a Saturday).

| # | Person | Period | Inputs | f | BilledUsd | PayUsd | PayPkr | OwnerUsd | OwnerPkr |
|---|---|---|---|---|---|---|---|---|---|
| G1 | CompanyRecommended | Oct 1–15 | $300/mo, commission $25 | 11/11 | 175.00 | 150.00 | 42,000 | 25.00 | 7,000 |
| G2 | CompanyRecommended | Oct 1–15 | as G1, joined Thu Oct 8 | 6/11 | 95.46 | 81.82 | 22,910 | 13.64 | 3,819 |
| G3 | CompanyRecommended | Oct 1–15 | as G1; absent Mon Oct 5 (half), Wed Oct 7 (full) | 10.5/11 | 167.04 | 143.18 | 40,090 | 23.86 | 6,681 |
| G4 | BudgetHire | Oct 1–15 | budget $1,000/mo, pay PKR 196,000/mo; absent Oct 6, Oct 20, Oct 27 (all full) | 11/11 | 500.00 | 350.00 | 98,000 | 150.00 | 42,000 |
| G5 | BudgetHire | Oct 16–31 | same person as G4 | 9/11 | 409.09 | 286.36 | 80,182 | 122.73 | 34,363 |
| G6 | Owner | Oct 16–31 | $1,200/mo | 11/11 | 600.00 | 600.00 | 168,000 | 600.00 | 168,000 |
| G7 | CompanyRecommended | Oct 16–31 | as G1, leaving date Wed Oct 21 | 4/11 | 63.64 | 54.55 | 15,274 | 9.09 | 2,545 |

With adjustments and extra days (also rate 280):

| # | Person | Period | Inputs | f | BilledUsd | InvoiceUsd | PayUsd | PayPkr | NetPayUsd | NetPayPkr | OwnerUsd | OwnerPkr |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| G8 | BudgetHire | Oct 16–31 | the G5 line + Reimbursement Rs 5,000, Bonus $20, Deduction Rs 1,000 | 9/11 | 409.09 | 443.38 | | | 320.65 | 89,782 | 122.73 | 34,364 |
| G9 | CompanyRecommended | Oct 1–15 | as G1 + 1 extra day | 12/11 | 190.91 | | 163.64 | 45,819 | | | 27.27 | 7,636 |
| G10 | BudgetHire | Oct 16–31 | the G5 person + 1.5 extra days | 10.5/11 | 477.27 | | 334.09 | 93,545 | | | 143.18 | 40,091 |

- G3: Oct 5 uses 0.5 paid leave; Oct 7 is 0.5 paid + 0.5 unpaid.
- G4/G5: Oct 6 uses October's paid leave, so Oct 20 and Oct 27 are unpaid.
- **Owner income Oct 16–31** with G6 + one full-period CompanyRecommended person (as G1) + G5 = 600.00 + 25.00 + 122.73 = **$747.73**.

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
