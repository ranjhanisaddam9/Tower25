# M4 report: employment history and exchange rates

Date: 09 Oct 2026 · Status: **complete; build, tests, migrations and checks green.** Commit and push: see the end.

## Part A: Employment history (owner-approved SPEC change)

**Domain**
- `EmploymentPeriod`: Id, PersonId, StartDate, EndDate?, audit fields. It is owned by `Person`, which is the only code that creates or changes periods.
- `Person` keeps the cache in step inside the same `SaveChanges`, so one transaction covers both:
  - **Create** opens the first period.
  - **Deactivate** closes the open period.
  - **Reactivate** opens a new period, which must start after the previous end.
  - **The edit form's joining date** moves the latest period's start. It must stay after the previous period's end and on or before its own end.
- `JoiningDate` and `LeavingDate` remain cached copies of the latest period, so the M3 UI and filters work unchanged.
- `EmploymentHistory.EnsureValid` enforces: end ≥ start, no overlaps, at most one open period, only the latest period may be open.
- `EmploymentCalendar.EmployedWorkingDays(periods, rangeStart, rangeEnd)` and `IsEmployedOn(periods, date)` are pure helpers, ready for M6 and M7.

**Database** (migration `AddEmploymentPeriods`):

| Rule | How the database enforces it |
|---|---|
| End ≥ start | Check constraint `CK_EmploymentPeriods_EndAfterStart` |
| At most one open period per person | Filtered unique index `UX_EmploymentPeriods_OnePerPersonOpen` (`WHERE EndDate IS NULL`) |
| One period per start date | Unique `(PersonId, StartDate)` |
| No overlaps | Trigger `TR_EmploymentPeriods_NoOverlap` (`THROW 51001`), declared to EF with `HasTrigger` |
| Delete | Cascades from `People` |

- **Backfill:** one `INSERT … SELECT` per existing person, copying JoiningDate, LeavingDate and the audit fields. Exactly one period each.
- **Friendly errors:** "The joining date must be after the previous employment period, which ended on 31 Mar 2026." The trigger's error (51001) also maps to a friendly message as a last line of defence.

**UI:** the details page has an "Employment history" section, visible to both roles. It lists every period newest first: start → end or a **Current** pill, plus working days in total ("so far" for the open period, counted to today in Karachi). The M3 placeholder-tabs card is renamed "Pay records" so it isn't confused with employment history.

**SPEC.md**, updated with the owner-approved text:
- **§2 Person:** your sentence verbatim. Because it replaced the clause that listed designation, contact details and the active flag, I added "Also has a designation, contact details and an active flag." so nothing is lost.
- **§2 Owner rule:** "At most one active person".
- **§3:** "Employed working days = working days that fall inside any of the person's employment periods."
- **§4:** "…on a date outside every employment period…" replaces "before joining, after leaving".
- **Change log:** "2026-10-09: Employment periods replace single joining/leaving dates; Owner rule relaxed to at most one active Owner."

## Part B: Exchange rates (`/exchange-rates`, `ManagerOrAdmin`)

- **Entity `ExchangeRate`** (migration `AddExchangeRates`):
  - Columns: Id, EffectiveFrom (unique index `UX_ExchangeRates_EffectiveFrom`), UsdToPkr `decimal(18,4)`, Note (max 200), audit fields, RowVersion.
  - The 100–1000 range is a database check constraint (`CK_ExchangeRates_UsdToPkrRange`) as well as a domain rule.
  - More than 4 decimals is rejected rather than rounded.
- **Lookup:**
  - `RateTimeline.RateOn/InEffectOn` is a single `IQueryable` rule ("latest EffectiveFrom ≤ date"). EF Core runs it in SQL; the unit tests run it in memory.
  - `IExchangeRateService.GetRateForAsync(date)` and `GetCurrentAsync()` (today in Karachi) return a `RateSnapshot` (Id, EffectiveFrom, UsdToPkr, Note).
- **List page:**
  - Hero card: "1 USD = Rs 280.50", "Effective since …", and the change vs the previous entry, absolute and %. The direction is given in words ("Up"/"Down"/"No change") and an arrow icon; colour only reinforces it.
  - **Inline SVG chart** of the last 12 entries, rendered on the server: CSS classes and SVG presentation attributes only (no `style`, no script, no library). It has `role="img"`, `<title>` and `<desc>`, a visible text summary, and a `<title>` tooltip per point. Scheduled points are drawn hollow and dashed.
  - "Rate on a date" lookup (GET, no state change): the rate and its entry, or "No rate on or before …".
  - History newest first: effective from (with a **Scheduled** pill for future dates), rate, change vs previous, note, added by (the user's name), and edit/delete actions. 20 per page. Empty state.
- **Add and edit:**
  - EffectiveFrom defaults to today (Karachi).
  - A duplicate date gives a friendly error, from the pre-check or from a unique-index race.
  - **>5% rule:** compared with the entry this one would follow (excluding itself on edit). A change of more than 5% gets a warning ("a rise of 10.52% from Rs 280.50 …") and an "I've double-checked this rate" checkbox. It is enforced on the server; exactly 5% doesn't trigger it.
  - Concurrency is handled like People: 409, the latest saved values, and a "Saved / Yours" differences panel.
  - Delete is POST behind the confirm modal, from the list or the edit page.
- **Audit log:** events 1200 (added: new date and rate), 1201 (edited: old date and rate → new date and rate) and 1202 (deleted: old values), each with the actor and entry id.
- **Display:** `ExchangeRateRules.Format` shows 2–4 decimals with trailing zeros trimmed (280.50, 280.1234, 280.123).
- **Dashboard tile** (same for both roles):
  - With a rate: "Rs 280.50", "since 06 Oct 2026", "Up 0.25 (+0.09%) vs previous", linking to `/exchange-rates`.
  - Without one: "Not set" and "Add rate", linking to the create page.
  - Future-dated entries are ignored until their date.
- **Sidebar:** Exchange Rates is a live link for both roles.

## Migrations added

| Migration | Contents |
|---|---|
| `20261009145339_AddEmploymentPeriods` | Table `EmploymentPeriods` (FK cascade), check constraint, filtered unique index, `(PersonId, StartDate)` unique index, **overlap trigger**, **backfill** |
| `20261009145429_AddExchangeRates` | Table `ExchangeRates`, unique `EffectiveFrom`, range check constraint, rowversion |

Both are applied to `HRPayroll` (0 people and 0 rates there, so the backfill inserted nothing). The trigger and constraints were confirmed with `sqlcmd`. They were generated as two separate migrations so each has a meaningful name.

## Tests: 270 passed, 0 failed, 0 skipped

161 unit + 109 integration. In M3 it was 214.

| Class | Tests | Covers |
|---|---|---|
| `EmploymentCalendarTests` | 9 | **Oct 1–9 and Oct 19–31 2026 → Oct 1–15 = 7, Oct 16–31 = 10**; open period; no periods; `IsEmployedOn` in, between and outside periods |
| `EmploymentPeriodInvariantTests` | 7 | **Overlap rejected; second open period rejected; rejoin must be after the previous end**; end before start; back-to-back is valid; create opens, deactivate closes, cache follows; moving the joining date |
| `RateTimelineTests` | 5 | **Before the first entry → null; exactly on EffectiveFrom; between entries; after the last; a future-dated entry ignored until its date** |
| `ExchangeRateRulesTests` | 19 | Formatting (280.50, 280.1234, 280.12, 280.123, 1,000.00); **>5% both directions, exactly ±5% not large**; range and 4-decimal checks |
| `EmploymentPeriodTests` | 4 | **Backfill:** the test migrates the test database back to `AddPeople`, inserts three M3-style people (active, left, active), migrates forward, and asserts exactly one matching period each. Also: deactivate → reactivate → **2 periods on details** and the cache equals the latest period (and again after a second deactivate); **joining date before the previous end → friendly error**, a valid move updates period and cache; the database rejects an overlap (51001), a second open period, and end < start |
| `ExchangeRateTests` | 12 | Anonymous → login; **Manager and Admin** list/add/edit/delete; duplicate date; out of range (99.99, 1000.0001, 5000) and 5 decimals; **>5% without the box → not saved, exactly 5% saved, −>5% with the box saved**; **concurrency 409**; **rate-on-date lookup** (before, between, on); **dashboard tile: current rate, future entry ignored, "Not set" when empty**, same for Admin; scheduled pill; **POST only and antiforgery**; **audit 1200/1201/1202 with old → new values and actor**; **chart SVG has no style attributes, accessible title, 12 points; no inline script**; paging at 20, newest first, change vs previous, "First entry" |
| Earlier classes | rest | Unchanged except the sidebar test (Exchange Rates is no longer "coming soon") and the fixture reset (also clears exchange rates) |

## Verification

- `dotnet build --no-incremental`: **0 warnings, 0 errors**.
- `dotnet test`: **270/270**.
- `dotnet ef database update` on `HRPayroll`: both migrations applied.

**Smoke test over HTTPS.** This ran against a disposable `HRPayroll_Visual` instance with demo data, a QA Admin and a QA Manager created through the UI. The database and credentials were deleted afterwards.

| Check | Result |
|---|---|
| Anonymous `/exchange-rates` | 302 to login |
| Manager list and create pages | 200 |
| Six monthly rates added | Each 302 |
| +10.52% without the box | Warning shown, not saved |
| Same change with the box | 302, saved |
| Duplicate date | Friendly error |
| Rate 1500 | Range error |
| Hero | "1 USD = Rs 280.50" |
| Scheduled entries | 1 Scheduled pill; chart drew 7 points |
| Lookup a month back | Correct entry and note |
| Lookup in 2020 | "No rate on or before 01 Jan 2020" |
| Edit with a token | 302 |
| GET on delete | 404 |
| POST delete without a token | 400 |
| Delete with a token | 302 |
| Dashboard tile (Manager) | "Rs 280.50 · since 06 Oct 2026 · Up 0.25 (+0.09%) vs previous"; Admin sees the same |
| Demo person deactivated and reactivated | Details shows 2 periods |

**Visual check** (built-in browser, HTTPS; screenshots after you enlarged the pane):
- **Pages covered:** exchange-rates page (hero, chart, lookup result, table with the Scheduled pill, the >5% warning on add, the delete confirm modal), dashboard tile, and person details with employment history.
- **Sizes and themes:** 1440px and the pane's native 961px desktop layout, and 375px; dark and light.

Fixed during the pass:
1. The chart line and points in **dark theme** were too dark (indigo on navy, below 3:1 for graphics). They now use the theme's focus colour: `#A5B4FC` dark, `#4F46E5` light.
2. Chart labels were about 6px on phones because the SVG scales with its viewBox. On narrow screens labels, points and the line are drawn larger (labels about 11px), and the left padding was widened.
3. In the desktop table, cell groups were right-aligned (meant only for the mobile cards). Row actions and the change text also wrapped, making rows tall. Both fixed.
4. The confirm modal's close button sat next to the title instead of at the right edge. This dates from M1 and is now fixed.

**Self-review**
- No `Html.Raw`, string-built SQL, inline script or style, or `on*=`. The only raw SQL is the constant trigger and backfill in the migration.
- `[AllowAnonymous]` is unchanged.
- Every mutation is POST with antiforgery.
- View models only.
- Accessibility: chart `role="img"` with title/desc and a text summary; direction in words, not colour; labels tied to inputs; icon buttons labelled.

## Known issues and deviations

1. **CLAUDE.md says exchange-rate history is append-only.** Your prompt explicitly allows corrections (edit and delete, audited), and I followed the prompt. Payroll will snapshot its own rate in M7. **Suggest updating that CLAUDE.md line** to "exchange rates may be corrected; every change is audited; finalized payrolls keep their own copy".
2. **Glass cards painting blank after scrolling in the in-app browser.** On the long person-details page, after scrolling, some glass cards occasionally painted without their content. The DOM was correct, and with `backdrop-filter` switched off they painted normally, so this looks like a GPU/compositing limit of the embedded browser. Please glance at a person's details page in your regular browser (manual check 4). If you see it there too, I'll reduce the glass blur on long pages.
3. **Working days for the current period** count up to today. A period that starts in the future shows 0 "so far".
4. **I stopped your running `dotnet run`** (with your approval) to release the build lock. Restart it with `dotnet run --project src/HR.Web --launch-profile https`.
5. **Git:** at your request I didn't run the git checks earlier. Commit and push results are reported in the chat.

## Manual checks for the owner

1. Run the app. Exchange Rates → **Add rate**: add today's rate, then one dated next month with a big jump (e.g. +10%). You should get the warning and checkbox; tick it to save. The list shows a Scheduled pill and the chart's last point is hollow.
2. Use **Rate on a date** for a past date, and for a date before your first entry.
3. The dashboard tile shows the current rate, ignoring the scheduled one.
4. **People** → deactivate someone, then reactivate them later. Their details page shows two periods in **Employment history**. In your regular browser, scroll that page and check every card shows its text (see Known issue 2).
5. Edit that person's joining date to a date before the first period ended: you get the friendly error.
6. Decide on Known issue 1 (the CLAUDE.md "append-only" wording).
