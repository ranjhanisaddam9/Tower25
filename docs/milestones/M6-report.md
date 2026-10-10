# M6 report: absences and paid leave

Status: **done**. Build clean, 389/389 tests green, dev DB migrated, HTTPS smoke test and visual check passed.

## Part A: M5 follow-ups

1. **"$475.00" in the M5 report was a typo, not a bug.**
   - For $300 + $25 commission the Admin "Current" card renders:
     - $300.00 / month billed
     - $175.00 per full period (incl. $25.00 commission)
     - your earning $25.00
   - $475.00 is the figure for Fatima Zahra ($900 + $25), whose card was in the M5 screenshot.
   - The M5 report line is corrected and marks the correction.
   - New integration test `Admin_current_card_renders_the_golden_billing_values` asserts the exact rendered values:
     - G1: $300.00, "$175.00 per full period (incl. $25.00 commission)", earning $25.00
     - G4: $1,000.00, $500.00, earning $150.00 at rate 280
     - G6: $1,200.00, $600.00, earning $0.00
2. **Creating a rate record in a locked period is refused** for both roles.
   - Applies to both `CreateAdminAsync` and `CreateIncrementAsync`.
   - The message appears on the form's month picker so the user can choose another period.
   - Test: `A_locked_payroll_period_refuses_new_records_for_Admin_and_Manager` uses the lock double. It shows that Oct 16 is refused for both roles while Nov 1 / Nov 16 are accepted.

## Part B: Domain and data

**`Absence` entity** (`HR.Domain/Absences/Absence.cs`):
- Fields: Id, PersonId, Date, Portion, Note, audit fields, RowVersion.
  - Portion is `Full` (1.0) or `Half` (0.5), stored as its name.
  - Note is optional, max 300 characters, and trimmed.
- The entity refuses weekend dates and unknown portions itself.
- An edit changes the portion and note only. The date is fixed; to move an absence, delete it and add another.

**Database**:
- Unique index `UX_Absences_PersonId_Date`.
- Check constraint `CK_Absences_Weekday`: `(DATEDIFF(day, '19000101', [Date]) % 7) < 5`, which is independent of DATEFIRST. A test also inserts a Friday under `SET DATEFIRST 1`.
- FK to People with cascade.

**`AbsenceRules.DateError`** checks, in order:
1. weekend
2. more than one year ahead (today + 1 year)
3. outside every employment period (`EmploymentCalendar.IsEmployedOn`)
4. duplicate

The lock check (`IPayrollLock.IsLockedAsync(PayPeriod.For(date).Start)`) is done by the service for create, edit, delete, the daily sheet and the range.

**`PaidLeaveAllocator`** (pure; SPEC §4):
- 1.0 paid day per calendar month, applied by date across both periods.
- Unused leave expires at month end.
- Input order doesn't matter.
- Returns `{PaidDays, UnpaidDays}` per absence.
- Never stored: every page recomputes it from the person's absences in the month.

**`PayableDays.For(employment, allocated, period)`** (pure):
- Returns `WorkingDays`, `EmployedWorkingDays`, `UnpaidDays` and `PayableDays` = Employed − Unpaid.
- Only unpaid days on employed working days inside the period count, so an absence left outside employment (e.g. after a leaving date moved) can never reduce pay twice.
- M7 payroll will use exactly this.

## Part C: Pages (Manager and Admin; sidebar "Absences" is live)

### `/absences` list
- Period selector: current period by default, with prev/next arrows and a "Current period" link.
- Filters:
  - name/code search
  - portion
  - paid / unpaid / partly paid
  - person status (Active by default)
- Columns:
  - date with weekday
  - person
  - portion pill
  - paid-leave pill with text and icon, plus "0.5 paid · 0.5 unpaid"
  - note
  - added by
  - edit and delete (delete behind the confirm modal)
- 20 per page; mobile cards; empty state.
- **Summary panel** per person with an absence in the period: absent days, paid leave used, unpaid days, and payable days ("10.5 of 11").
- Locked periods show a banner and a "Locked" pill instead of actions.

### `/absences/day` (daily attendance)
- Date picker (today by default), previous/next working day, "Today".
- Weekends show a message with links to the nearest working days.
- Lists everyone employed on that date (from employment periods), pre-filled. Each row has a **Present / Half / Full** segmented control (real radio buttons, so arrow keys work) and an optional note.
- Save writes only the changed rows, in one transaction, and toasts "Attendance for 07 Oct 2026 saved: 1 added, 1 changed, 1 removed."
- Posting a person who wasn't employed that day rejects the whole save.
- Locked dates: banner, disabled controls, no Save button. A POST is refused too.

### `/absences/range`
- Step 1 (GET, changes nothing): person, from/to (max 31 days), note. Previews every date as one of:
  - Will add, with Paid/Unpaid after the month's allocation
  - Skipped: weekend
  - Skipped: already recorded
  - Skipped: not employed
  - Skipped: locked
  - Skipped: more than a year ahead
- Step 2 (POST): saves the "will add" dates as full days in one transaction.
  - The server re-runs the preview.
  - Any posted date that isn't "will add" (or is outside the range) rejects the whole confirm.

### Single add / edit / delete
- `/absences/new` (person, date, Full/Half, note) and `/absences/{id}/edit`, reachable from the list and from person details.
- Delete is behind the confirm modal; there are delete buttons on the list, on the person tab and on the edit page.
- Before the change:
  - The edit page warns "Changing it to half day will change the paid/unpaid status of 1 later absence in October 2026."
  - The edit page and the delete confirm text both carry the same kind of note for deleting.
- After the change, the toast says "… This changed the paid/unpaid status of 1 later absence in October 2026."

### Person details: Absences tab (live)
- The tabs are now server-rendered links (`?tab=absences`); only the chosen tab's data is loaded.
- Month calendar (CSS grid, no JS), with month navigation and "This month":
  - Weekends are dimmed.
  - Days outside employment are hatched and labelled "Not employed".
  - Each absence shows Full/Half and Paid / Partly paid / Unpaid as text and an icon, as well as colour.
  - Today has a ring.
  - There is a legend.
- "Paid leave this month: 0.5 of 1 day left" (another month reads "Paid leave in November 2026").
- The current period's payable days ("10.5 of 11 · employed 11 · unpaid 0.5").
- The person's absences with a year filter, and edit/delete per row.

### Dashboard
- The "Pending absences" placeholder is replaced by **"Absences this period: N days · U unpaid"**, which links to `/absences`.
- New tile **"Absent today: N"** links to `/absences/day`. On weekends its caption reads "Not a working day".

### Audit (category `HR.Security`)
- Events:
  - 1400 added
  - 1401 edited (old → new portion)
  - 1402 deleted (old portion → none)
  - 1410 demo absences seeded
- Each carries the actor, absence id, person id and date. **Notes are never logged.**

## Part D: Demo data (Development + `DemoData:Seed` only; idempotent)

- Three new demo people, all CompanyRecommended at $300 + $25 from 2026-10-01:
  - **Kamran Yousaf**: G3 clone, absent Mon Oct 5 (half) and Wed Oct 7 (full)
  - **Nadia Haider**: G2-style, joins Thu 2026-10-08
  - **Rizwan Ali**: G7-style, leaves Wed 2026-10-21
- **Bilal Ahmed** (G4/G5): Oct 6, Oct 20 and Oct 27, all full days.
- Ayesha (G1), Imran (G6), Nadia and Rizwan: no absences.
- Everyone else gets 0–3 pseudo-random absences across Sep–Oct 2026, only on days they were employed.
  - Each person uses a fixed seed (their place in the demo list).
  - People who already have absences are skipped, so re-running adds nothing (tested).

## Migrations added

- `20261010024617_AddAbsences`: the `Absences` table, `UX_Absences_PersonId_Date`, `IX_Absences_Date`, `CK_Absences_Weekday` and the FK to People (cascade).

## Tests: 389 passed, 0 failed, 0 skipped

238 unit + 151 integration. M5 ended at 334; M6 added 55.

| Class | Tests | Covers |
|---|---|---|
| `PaidLeaveAllocatorTests` | 9 | **G3** (Oct 5 half → 0.5 paid; Oct 7 → 0.5 + 0.5); **G4/G5** (Oct 6 paid, Oct 20 and Oct 27 unpaid); **month boundary** (Fri Oct 30 and Mon Nov 2 both paid); **two halves paid, third half unpaid**; **order independence** (20 shuffles); **deleting the first makes the next paid**; **rehire in the same month shares the allowance**; leave left per month; empty input |
| `PayableDaysTests` | 9 | **G1 11/11, G2 6/11, G3 10.5/11, G4 11/11, G5 9/11, G6 11/11, G7 4/11**; **two employment periods 7/11 and 10/11**; absences outside employment or the period are ignored |
| `AbsenceRulesTests` | 14 | **Weekend, outside employment (before, after, between periods), duplicate, more than a year ahead**; valid date; the entity refuses weekends, long notes and unknown portions; portion parsing accepts names only |
| `AbsenceTests` (integration) | 19 | **Both roles add, edit (tampered person/date ignored) and delete**; **anonymous → login** on 5 routes; **validation over HTTP**: weekend, locked, duplicate, > 1 year, outside employment, bad portion; locked edit and delete refused; **DB check constraint rejects a Saturday through the DbContext**, and a Friday passes under `SET DATEFIRST 1`; **daily sheet**: only employed people listed, "1 added, 1 changed, 1 removed", a second save says "No changes", a non-employed person rejects the save; **weekend refused; locked date read-only** (lock double); **range preview shows every outcome kind** with paid/unpaid after allocation; **confirm saves exactly the 9 "will add" rows**; **tampered confirm** (weekend / locked / duplicate / outside range) rejected as a whole; > 31 days refused; **demo G3/G4/G5 summary numbers and filters** (10.5 of 11, 11 of 11, 9 of 11; partly paid / half / unpaid / paid filters), seeder idempotent; **calendar markers and "Paid leave this month"** (0.5 → 0 left, other month 1 left); **later-allocation notes** on the edit page, the edit toast and the delete toast; **dashboard tiles** (2 days · 0.5 unpaid, absent today 1); **POST-only, antiforgery, audit 1400/1401/1402 with old → new, notes never logged**; **no inline script/style/handlers, one `<h1>`, no "Billed"** on all 11 new page variants |
| `PayTests` (Part A) | +4 | Golden Current-card values (3) and the locked create for both roles |
| Updated | — | Sidebar test (Absences is live); demo counts 25 → 28 people (23 active); the Manager hire-source leak check now also covers the Absences tab and pages |

## Verification

- `dotnet build --no-incremental`: **0 warnings, 0 errors**.
- `dotnet test`: **389/389**.
- `dotnet ef database update` on `HRPayroll`: `AddAbsences` applied.

**HTTPS smoke test** on a disposable `HRPayroll_Visual` DB with demo data. It was dropped afterwards, and the QA launch config and credentials were removed.
- Anonymous `/absences` → 302 to login.
- All new routes return 200 for both Admin and Manager: list, day, day with date, range, new, person tab.
- Today (Sat 10 Oct) on `/absences/day` shows the weekend message.
- Summary panel:
  - G3 Kamran, Oct 1–15: **10.5 of 11**
  - G4 Bilal, Oct 1–15: **11 of 11**
  - G5 Bilal, Oct 16–31: **9 of 11**
- Manager round trip:
  - add → 302
  - edit to Half → 302, stored "Half day"
  - Admin delete → 302, row gone
- Daily save: "Attendance for 09 Oct 2026 saved: 1 added, 0 changed, 0 removed."
- Range Oct 12–18: preview 5 will add + 2 weekend; confirm "5 absences added."; a tampered Saturday is rejected.
- `GET …/delete` → 404; a POST without a token → 400.
- Dashboard: "Absences this period 10 days · 4 unpaid".
- Kamran's calendar: "0 of 1 day left", Oct 5 Half-Paid, Oct 7 Full-Partly paid.
- CSP header present.

**Visual check** in the in-app browser: 375px (mobile emulation) and 1440px, light and dark.
- List with summary panel, daily attendance, range preview and confirm, add form, person calendar, dashboard.
- Scripted audit, for each page and theme: horizontal overflow, contrast of every text node against its composited background, touch targets, `<h1>` count.
  - Final state: **no overflow, no contrast failures, one `<h1>`**.
- Segmented control at 375px: **98 × 44 px per option**, full-width.
  - The arrow keys move the selection.
  - The focus ring is the 2px accent outline.
- Locked banner on the day page: a lock can't be created until M7, so it was viewed by adding the same markup in the browser. It uses the existing warning alert; tests cover the server-rendered version.

**Fixed during the check**:
- Toolbar buttons on the Absences tab stretched to the facts' height; they now align to the top.
- Day-sheet note inputs were 37px tall; they are now ≥ 40px.
- The small "0.5 day" hint no longer uses opacity.
- "Absent today" said "Everyone is in" on a Saturday; it now says "Not a working day".

**Security and accessibility self-review**:
- All mutations are POST with the global antiforgery filter; the range preview is a GET that changes nothing.
- The controller is under `ManagerOrAdmin`.
- View models only: posted person, date or ids are re-validated or ignored server-side.
- EF parameterized queries only.
- No `Html.Raw`, no inline script, style or handlers.
- Notes are never logged.
- Labels are tied to inputs: each segmented group is a fieldset with a hidden legend naming the person.
- Paid/unpaid is always text and an icon, not colour alone.

## Known issues and deviations

- **No SPEC deviations.**
- Design choices within SPEC, worth knowing:
  - Editing an absence changes its portion and note; the date is fixed (delete and add to move it). This keeps the "later absences" note exact.
  - The list's status filter uses the person's active flag. Someone with a future leaving date is already inactive, e.g. Rizwan before Oct 21, so their absences appear under "All" or "Inactive". The daily sheet and range use employment periods, so they still include that person until the leaving date.
  - The daily sheet saves only the rows it posts. Rows not posted are left untouched.
  - The dashboard's period tile counts everyone's absences in the current period, active or not.
- Tooling: the in-app browser pane was 800×455 and wheel-scrolling was stuck, as before. Sections were hidden with JS to view lower content, and 1440px screenshots are scaled down.

## Manual checks for the owner

1. **Absences** in the sidebar → the current period, with the per-person summary. Use ‹ › to move periods. Filter by "Partly paid" for Kamran Yousaf: one row (07 Oct, 0.5 paid · 0.5 unpaid).
2. **Daily attendance** on a weekday: tap Half for one person and Full for another, then Save. Check the toast "… 2 added, 0 changed, 0 removed". Open a Saturday: it is refused, with links to Friday and Monday.
3. **Add a range** for someone over two weeks across a weekend: check the preview (weekends skipped; the first day Paid, later days Unpaid if leave is used), then Confirm.
4. Open **Bilal Ahmed → Absences tab**: Oct 6 Paid, Oct 20 and Oct 27 Unpaid. Edit Oct 6: the page warns that changing it to half day affects 1 later absence. Delete it: the toast says Oct 20 changed and now shows as Paid.
5. **Nadia Haider → Absences**: Oct 1–7 hatched "Not employed", payable days 6 of 11.
6. Phone or narrow window: the segmented buttons are easy to tap; the calendar fits.
7. Dashboard: "Absences this period" and "Absent today" open the list and the daily sheet.
