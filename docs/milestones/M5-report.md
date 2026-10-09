# M5 report: pay records, billing and increments

Date: 09 Oct 2026 · Status: **complete; build, tests, migration and checks green.**

## Part A: Follow-ups

| # | Item | Result |
|---|---|---|
| 1 | Git | `git log` showed M1–M4 (`fca7ae2` latest), the working tree was clean, and `git push -u origin main` reported **"Everything up-to-date"**: all four milestones are on GitHub. |
| 2 | CLAUDE.md | The history-tables line now reads: "Rate records and exchange rates may be corrected; every change is audited. Finalized payroll data is immutable and keeps its own snapshot of every amount and rate." |

## Part B: Domain and data

**`RateRecord`** (`HR.Domain/Pay`):
- Fields: Id, PersonId, EffectiveFrom (1st or 16th, unique per person), BilledMonthlyUsd, CommissionPerPeriodUsd, PayMonthlyAmount (all `decimal(18,2)`), PayCurrency (USD/PKR, stored as a string), ChangeType, Note (≤ 300), NeedsBillingReview, audit fields, RowVersion.
- The entity refuses invalid terms even if validation is bypassed.

**Derivation** (`PayRules`). The server derives billed, commission and currency from the hire source and never trusts posted values:

| Source | Admin enters | Stored |
|---|---|---|
| CompanyRecommended | Monthly salary, commission | billed = pay = salary, USD, commission as entered (default: previous record's, or $25) |
| BudgetHire | Budget, pay, currency | billed = budget, commission 0, currency USD or PKR (default PKR) |
| Owner | Monthly salary | billed = pay = salary, USD, commission 0 |
| none | — | Refused. Managers see "Pay setup for this person hasn't been completed yet." |

**Rules:**
- **ChangeType:** derived on every save.
  - The first record is Initial.
  - Otherwise compare pay with the previous record: up is Increment, down is Decrement, same pay with billing or commission changed is BillingChange.
  - An Admin can tick "Mark as correction".
  - After any edit or delete, types are re-derived for the person's later records; Admin-marked corrections stay.
- **EffectiveFrom:** must be the 1st or 16th, and on or after the start of the pay period containing the person's first employment period.
- **Limits:**
  - USD monthly: 1–100,000, at most 2 decimals.
  - PKR monthly: 1,000–50,000,000, whole rupees.
  - Commission: 0–10,000 per period.

**Database** (migration `AddRateRecords`):
- Check constraints:
  - `DAY(EffectiveFrom) IN (1, 16)`
  - amounts > 0, commission ≥ 0
  - PKR has no fraction (`ROUND(..., 0)`)
- Unique `(PersonId, EffectiveFrom)`; filtered index on `NeedsBillingReview`; cascade from People.

**Other pieces:**
- `RateRecords.InEffectOn(records, date)`: the latest EffectiveFrom ≤ date. Payroll (M7) will call it with `period.Start`.
- `PayMath.FullPeriod`: SPEC §5 rounding with f = 1, used for every "per full period" figure.
- `IPayrollLock`: a no-op implementation for now (M7 replaces it). Edit or delete is refused when the record's original or new EffectiveFrom is in a locked period.
- **Hire-source lock:** while a person has any rate record, the hire source can't be changed ("Delete this person's pay records before changing the hire source."). The Admin card shows a lock note and disables the control.

## Part C: Admin pay setup

- **Tab:** person details → Pay records → **Salary & increments** (live).
- **Form:**
  - The form adapts to the hire source; the server ignores fields that don't apply.
  - EffectiveFrom picker: a month input plus 1st–15th / 16th–end. It defaults to the current period, or for a first record the period containing the joining date.
  - BudgetHire: a live margin preview in `site.js` (data attributes, no inline script), using the same rounding as the server. PKR pay is converted at today's rate and labelled an estimate; without a rate the preview says so.
  - Pay at or above the budget shows a warning and needs "I understand this hire loses money" (server-enforced).
- **Records list** (newest first): effective from (Scheduled pill for future dates), change type pill, pay in its currency, billed/month, commission/period, pay change %, note, added by, plus a **Review billing** pill with a **Mark reviewed** POST and edit/delete (confirm modal).
- **Current card:** pay per month and per full period; billed per month and per full period (with commission); **your earning per full period** (PKR pay converted at today's rate, labelled an estimate).
- **Audit:** events 1300 created, 1301 edited (old → new), 1302 deleted, 1303 reviewed, with actor, record id and person id. Server-side logs only.

## Part D: Manager increments

- **Manager tab:** built from `ManagerPayTab`, which is projected from pay columns only, so billed, commission, review flag and hire source are never loaded.
  - Columns: effective from, change (BillingChange shown as **Update**), pay, pay change %, note, added by.
  - Current card: pay per month and per full period, plus the other-currency equivalent labelled "approx.".
- **Record increment:** EffectiveFrom picker, new monthly pay, note; currency shown read-only. It needs an existing record, otherwise the neutral message.
  - The server copies billed and commission from the record it follows. For CompanyRecommended and Owner, billed follows pay.
  - BudgetHire increments set NeedsBillingReview.
- **Managers may edit or delete only records they created**, and only the pay, date and note, under the same rules. Anyone else's record gives 404 on the edit form and 403 on delete. The Admin form, edit and mark-reviewed routes are 403.

## Part E: Salaries overview (`/salaries`, sidebar link live)

- **All roles:** active people with code, name and designation, pay/month, since, last change (date and %), and "No pay setup" (or "Starts …") pills.
  - Filters: All / No pay setup / Changed in the last 90 days.
  - Search, 5 sorts, 20 per page, mobile cards.
- **Admin also gets:** billed/month, commission/period, est. earning per full period, the Review billing pill, a "Needs billing review" filter, and footer totals (billed/month, est. earning) over the whole filtered set. A note appears when PKR pay is estimated, or skipped for lack of a rate.
- **Dashboard:** "No pay setup: N" for both roles (links to the filtered list); the Admin-only "Billing reviews pending: N" tile.

## Part F: Demo data

Development and flag only, idempotent (people who already have records are skipped):

| Person | Mirrors | Records |
|---|---|---|
| Ayesha | G1 | CompanyRecommended $300 + $25 from 2026-10-01 |
| Bilal | G4/G5 | BudgetHire budget $1,000, pay Rs 196,000 from 2026-10-01 |
| Imran | G6 | Owner $1,200 from 2026-10-01 |
| Fatima | — | CompanyRecommended, Increment by a "Manager" |
| Hamza, Mariam | — | BudgetHire, Increments by a "Manager" with NeedsBillingReview |
| Omar | — | A commission-only BillingChange |
| Ali | — | An Increment |

## Migrations added

| Migration | Contents |
|---|---|
| `20261009155652_AddRateRecords` | Table `RateRecords` with the three check constraints, the unique `(PersonId, EffectiveFrom)` index, the review index, FK cascade. Applied to `HRPayroll`. |

## Tests: 334 passed, 0 failed, 0 skipped

206 unit + 128 integration. In M4 it was 270; M5 added 64.

| Class | Tests | Covers |
|---|---|---|
| `RateRecordLookupTests` | 2 | **Oct 16 record doesn't apply to Oct 1–15, does to Oct 16–31; Initial from Oct 1 for a person joining Oct 8** |
| `PayDerivationTests` | 8 | **Each source with tampered inputs ignored**; BudgetHire currency default; increments per source (billed follows pay; commission carried; budget unchanged); no source → refused |
| `ChangeTypeTests` | 4 | Initial, Increment, Decrement, **BillingChange** (billing or commission only); currency switch compared in USD, or Correction without a rate |
| `PayValidationTests` | 22 | **1st/16th and ≥ first-period start**; required date; **USD and PKR limits; PKR fraction rejected**; commission limits; the entity refuses invalid terms |
| `PayMathTests` | 9 | **Budget 1000, pay Rs 196,000 at 280 → pay $700, margin $150.00**; G1 and G6 full-period figures; no rate → no estimate; loss check (equality counts as a loss) |
| `PayTests` | 19 | **Admin Initial per source with tampered fields**; effective-date rules; **duplicate date**; PKR fraction; **pay ≥ budget needs the checkbox**; Admin tab margin $150.00; **Manager increment per source** (CompanyRecommended: billed follows, commission kept, no flag; BudgetHire: budget kept, flag set; Owner: billed follows) with tampered fields ignored; **Manager edits/deletes only their own** (404/403) and Admin routes 403; **Manager pages free of Billed/Budget/Commission/Margin/earning/Review/BillingChange/1,234.56/43.21** (dashboard, 4 salaries views, 2 details, 2 increment forms) with "Update" shown; **no hire source blocks both roles**; **hire source locked until records are deleted**; **mark reviewed clears the flag, Manager 403**, dashboard review tile 1 → 0; **`IPayrollLock` double refuses edit, delete and moving into a locked period**, while an unlocked edit works; change types re-derived on edit and Correction; **audit 1300/1301/1302 with old → new**; **POST-only and antiforgery**; **salaries filters, Admin totals ($2,500.00 billed, $150.00 earning) and dashboard counts**, review tile absent for Managers |
| Earlier classes | rest | Sidebar test updated (Salaries is live) |

## Verification

- `dotnet build --no-incremental`: **0 warnings, 0 errors**.
- `dotnet test`: **334/334**.
- `dotnet ef database update` on `HRPayroll`: `AddRateRecords` applied, check constraints confirmed.

**Smoke test over HTTPS.** This ran against a disposable `HRPayroll_Visual` instance with demo data, rate 280, a QA Admin and a QA Manager; all were deleted afterwards.

| Check | Result |
|---|---|
| Anonymous `/salaries` | 302 to login |
| Earning per full period | **Ayesha (G1) $25.00, Bilal (G4) $150.00, Imran (G6) $0.00** |
| Admin salaries totals | **billed $7,880.00, est. earning $606.43** (checked by hand from the 8 demo people) |
| Review filter | 2 rows |
| Admin "set up pay" without a hire source | Redirected with a message |
| Manager salaries, details, increment form | 200 |
| Manager increment with a tampered `BilledMonthlyUsd` | 302; stored as derived |
| Manager on the Admin form | 403 |
| Manager on a person with no source | Neutral message |
| Hire-source words on Manager pages (8 URLs) | none |
| Mark reviewed | Admin 302, Manager 403 |
| Hire-source change while records exist | Refused with the friendly message |
| Dashboard tiles | Correct for both roles |

**Visual check** (built-in browser, HTTPS, real screenshots):
- Admin tab for BudgetHire (Bilal), CompanyRecommended (Fatima) and Owner.
- Admin forms per source. The margin preview reacts live: $150.00 at Rs 196,000; −$35.71 with the loss warning at Rs 300,000.
- Manager tab and increment form.
- Salaries as an Admin desktop table with totals, and as mobile cards.
- Dashboard tiles.
- Desktop (961px native) and 375px, dark and light.

Fixed during the pass:
1. The Note column was squeezed until "Demo data" broke letter by letter. Wrapping cells now have a 14ch minimum and wide tables scroll inside their wrapper.
2. In the salaries table, codes and dates wrapped across lines; numbers, codes and dates now stay on one line.

**Self-review**
- Money is `decimal` throughout. The only `double` is SVG chart pixel coordinates.
- SPEC §5 rounding is applied via `Money` / `PayMath`.
- No `Html.Raw`, raw SQL, inline script or style.
- Every mutation is POST with antiforgery.
- Admin-only routes carry `AdminOnly`; Manager data comes from pay-only projections.
- Labels and fieldsets/legends on the period picker; icon buttons labelled.

## Known issues and deviations

1. **`IPayrollLock.IsLockedAsync(periodStart)` is async**, because M7 will need the database. Your prompt said `IsLocked`.
2. **Edge cases not specified, handled as follows:**
   - A BudgetHire currency switch derives its change type by comparing pay in USD at today's rate; without a rate it's labelled Correction.
   - An edit that changes nothing vs the previous record is labelled Correction.
   - The "loses money" check is skipped when pay is PKR and there's no exchange rate; the form says the margin can't be shown.
   - Managers never get the loss warning (it would reveal the budget); their BudgetHire increments are flagged for your review instead.
   - Creating a record inside a locked period isn't blocked; only edit and delete are, as specified.
3. **Salaries overview** loads active people and their records into memory to compute rows and totals. That's fine for dozens to low hundreds of people; it can move to SQL later if needed.
4. **Demo "Manager" increments** use a placeholder creator (`demo-manager`), so "Added by" shows "—".
5. **Scrolling in the in-app browser** still paints blank areas when emulating sizes. For the 375px shots I hid the sections above the pay tab rather than scrolling. Real browsers are unaffected.

## Manual checks for the owner

1. On a CompanyRecommended person: Salary & increments → **Set up pay** with $300 and commission 25. The tab shows billed $475.00 per full period and your earning $25.00.
2. On a BudgetHire person: try budget $1,000 and pay Rs 196,000, and watch the margin preview ($150.00 at rate 280). Then type pay Rs 300,000: the loss warning appears and saving needs the checkbox.
3. As a Manager, **Record increment** on that BudgetHire person. As Admin, you see the **Review billing** pill and the dashboard "Billing reviews pending" tile; click **Mark reviewed**.
4. As a Manager, open the same person and `/salaries`: no billed, budget, commission or review anywhere.
5. Try changing that person's hire source: it's locked until their pay records are deleted.
6. `/salaries` as Admin: check the totals row, and the "Needs billing review" filter.
