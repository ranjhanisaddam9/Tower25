# M3 report: people (employees and internees)

Date: 09 Oct 2026 · Status: **complete; build, tests, migration and checks green. The push is still blocked by GitHub credentials (see Known issues).**

## Part A: M2 follow-ups

| # | Item | Result |
|---|---|---|
| 1 | Change-password rate limit | `POST /account/change-password` allows 5 attempts per minute **per signed-in user** (partitioned by user id). The 6th gets the friendly 429 page with `Retry-After`, and security event 1006 is logged (user partition and IP, no passwords). `UseRateLimiter` moved after `UseAuthentication` so the limiter can see the user. |
| 2 | Git status | Working tree clean at the start. `origin/main` doesn't exist locally because nothing has ever been pushed. `git ls-remote origin` returns nothing (the remote repository is empty), and pushes still get **403 as `BloomHouseMarketing`**. As instructed, I didn't touch credentials. Commits waiting: M1, M2 and now M3. |

## Part B: Person data

**Domain** (`HR.Domain/People`, pure):
- `Person` entity with private setters. Its methods enforce the invariants:
  - `Create`
  - `UpdateDetails`: joining date can't move after the leaving date
  - `Deactivate`: leaving date ≥ joining date
  - `Reactivate`: rejoining date > previous leaving date; returns the previous dates for the audit log
  - `SetHireSource`
- `PersonInput.Normalize` validates every field and returns the stored form.
- Helpers: `PakistaniPhone` (normalises to `+923001234567`, mobiles and landlines), `Cnic`, `PakistaniIban` (ISO 13616 mod-97, PK only), `Masking`, `PersonCode` (`HR-0001`).
- Enums: `PersonType`, `HireSource`.

**Database** (migration `AddPeople`):

| Element | Detail |
|---|---|
| Table `People` | Columns as specified, plus `CodeNumber` (int, unique) for numeric sorting |
| Code | `PersonCodeSequence` (SQL Server sequence); unique index `UX_People_Code`; EF throws if code ever tries to change `Code` or `CodeNumber` |
| Email | Lower-cased; filtered unique index `UX_People_Email` (`WHERE Email IS NOT NULL`) |
| CNIC | Stored as `12345-1234567-1`; filtered unique index `UX_People_Cnic` |
| IBAN | Stored uppercase without spaces (24 chars); shown grouped in 4s |
| Owner rule | Filtered unique index `UX_People_ActiveOwner` (`WHERE HireSource = 'Owner' AND IsActive = 1`), plus a friendly message checked before saving |
| Check constraints | `CK_People_LeavingAfterJoining`, `CK_People_InactiveHasLeavingDate` |
| Concurrency | `RowVersion` (`rowversion`) |
| Audit fields | `CreatedAt`, `CreatedByUserId`, `UpdatedAt`, `UpdatedByUserId` |
| Enums | Stored as readable strings |

- **Codes are never reused.** The number is taken with `NEXT VALUE FOR` before the insert. Sequence values aren't rolled back, so a failed insert just leaves a gap.
- **Unique-index races** (email, CNIC, Owner) are caught and turned into the same friendly messages.

**Demo data** (`DemoDataSeeder`):
- Runs only when `DemoData:Seed` is true (never committed) **and** the environment is Development.
- 25 Pakistani people: 19 employees and 6 internees; all three hire sources (exactly one active Owner); 5 with no source; 4 inactive with leaving dates.
- Clearly fake values: CNICs `00000-…`, IBANs with bank code `TEST` (valid check digits), emails `@demo.example`, phones `+92 300 000 xxxx`.
- Idempotent: people are matched by demo email, so a re-run only adds what's missing.

## Part C: People pages (`/people`, `ManagerOrAdmin`)

- **List**
  - Search by name, code, email or phone. Phone search matches any format: `0311-7654321`, `+92 311 7654321`, `3117654321`.
  - Filters: type, and status (Active by default, Inactive, All). Admins also get hire source, including "Not assigned".
  - Sort: name, code or joining date, either direction. 20 per page; paging links keep the filters.
  - Columns: code, name with designation, type pill, joined, status pill (with the leaving date when inactive).
  - Admins also get a hire-source pill, amber "Not assigned" when empty.
  - Stacked cards on mobile, with 40px tap targets on the row links. Empty states for "no people yet" and "no matches".
- **Details**
  - Glass profile card: avatar initials, code, type, active status, designation, joined/left, phone (`+92 300 1234567`), email.
  - Full CNIC and grouped IBAN, each with a copy button (IBAN copies without spaces), plus bank name, notes and added/updated times.
  - Disabled placeholder tabs: "Salary & increments (M5)", "Absences (M6)", "Payslips (M7)".
- **Create / Edit**
  - Floating-label form in sections, with client-side (unobtrusive) and server-side validation. The phone number is normalised on save.
  - The code is assigned on save.
  - Concurrency: a stale `RowVersion` gives **409** with a friendly message. The form reloads with the saved values, and a panel lists each differing field as "Saved: … · Yours: …" (CNIC and IBAN masked).
- **Deactivate:** modal with the leaving date (defaults to today, `min` = joining date). POST only, re-checked on the server.
- **Reactivate:** modal with the rejoining date (`min` = the day after leaving). POST only. Sets `JoiningDate` to the rejoining date, clears `LeavingDate`, and logs the previous joining and leaving dates in audit event 1103.
- **Masking:** the edit form never receives the stored CNIC or IBAN. It shows them masked (`•••••-••••321-9`, `•••• •••• •••• •••• •••• 6702`), with "leave empty to keep" and a "Remove the saved …" checkbox. The list never contains them. Only the details page shows them in full.
- **Logs:** audit events 1100–1110 include actor, person id and code only. CNIC, IBAN and phone numbers are never logged.

## Part D: Hire source (Admin only)

- `PersonFormViewModel` (create and edit, both roles) has **no HireSource property**, so a posted `HireSource` field binds to nothing.
- Manager queries project into `PersonRow` / `PersonDetails`, which have no hire source. The Admin list uses a separate `ListForAdminAsync` projection, and the details card uses `GetHireSourceAsync`. Both are called only for Admins.
- `POST /people/{id}/hire-source` is `[Authorize(Policy = AdminOnly)]` and accepts only enum names (not numbers, so `"3"` is rejected). Choosing Owner opens the confirm modal via a new conditional confirm (`data-confirm-if-name`/`-value`); other choices submit directly. "Not assigned" clears the source.
- Owner rule:
  - Setting Owner while another active person holds it gives a friendly error naming them.
  - Reactivating a former Owner while someone else is the active Owner is refused. Admins see why; Managers get a neutral message that doesn't mention hire sources.
- Manager responses contain no hire-source labels, values, filter options, enum names or the `pill-teal`/`pill-violet` classes. A Manager's `?hireSource=…` query parameter is ignored.
- **Sidebar:** "Invoices" and "Owner Income" (both Admin-only in SPEC §1) are now listed only for Admins. Otherwise Managers would have seen the word "Owner" on every page. "People" is now a live link for both roles.
- **Dashboard:**
  - "Active people" shows the real count with an employees/internees split and links to `/people`.
  - Admins also get an amber "Hire source not assigned: N" tile (active people with no source), linking to `/people?hireSource=NotAssigned`. For Managers the count isn't even queried.

## Migrations added

| Migration | Contents |
|---|---|
| `20261009134717_AddPeople` | `PersonCodeSequence`; table `People`; the two check constraints; unique and filtered unique indexes (`UX_People_Code`, `UX_People_Email`, `UX_People_Cnic`, `UX_People_ActiveOwner`); `CodeNumber` unique; `(IsActive, FullName)` and `JoiningDate` indexes. Applied to `HRPayroll` (0 people). |

## Tests: 214 passed, 0 failed, 0 skipped

121 unit + 93 integration. In M2 it was 134.

| Class | Tests | Covers |
|---|---|---|
| `PakistaniPhoneTests` | 22 | 10 valid formats (mobile and landline) normalised; 10 invalid (short, long, foreign, letters, odd separators, trunk 0 after +92); display format |
| `CnicTests` | 9 | With and without dashes; 12/14 digits, misplaced dashes, spaces, letters |
| `PakistaniIbanTests` | 12 | Valid `PK36SCBL0000001123456702` (spaced, lower-case); wrong check digit; wrong account digit; 23 and 25 chars; a valid **non-PK** IBAN (GB); digit in the bank code; grouping; fake-IBAN builder |
| `MaskingTests` | 7 | Last 4 visible, separators kept, IBAN grouped, null and empty |
| `PersonInvariantTests` | 7 | Normalisation on create; **leaving before joining** rejected; deactivate always sets a leaving date (never inactive without one); reactivate rules; joining can't pass leaving; all field errors listed |
| `PeopleTests` | 22 | Anonymous → login; **Manager and Admin** list/create/edit/deactivate/reactivate (with phone normalisation and the audit dates); **codes sequential, unique, not reused after a rolled-back number and a failed create**; duplicate email and CNIC (create and edit); server validation; **concurrency 409 with differences**; **Manager pages free of hire-source information with all demo sources present** (13 URLs); Manager hire-source POST → 403; Manager posting `HireSource` → stored value unchanged; **Owner A → B refused → deactivate A → B allowed → reactivating A refused**; clear and invalid values; deactivate/reactivate date rules; GETs on mutating routes refused and POSTs without a token → 400 with no changes; search (name, code, email, three phone formats), type/status filters, 6 sorts, paging with kept filters; Admin hire-source filter; empty state; **masking in list and edit, full on details**, keep/remove on edit; **logs never contain the test CNIC, IBAN or phone** (7 forms); dashboard tiles (Admin count correct, absent for Managers); role-aware sidebar; demo seeder idempotent and a no-op outside Development |
| `AuthenticationTests` (+1) | 23 | **6th change-password POST → 429**, event 1006 logged, another user unaffected |
| Earlier classes | rest | Unchanged except: no "People (coming soon)" in the sidebar test; fixture resets People before Users (the code sequence is deliberately not reset) |

## Verification

- `dotnet build --no-incremental`: **0 warnings, 0 errors**.
- `dotnet test`: **214/214**.
- `dotnet ef database update` on `HRPayroll`: `AddPeople` applied. Sequence, 4 unique indexes and 2 check constraints confirmed with `sqlcmd`.

**Smoke test over HTTPS.** This ran against a disposable `HRPayroll_Visual` instance with demo data, a throwaway QA Admin and a QA Manager created through the Managers page. The database and credentials were deleted afterwards.

| Check | Result |
|---|---|
| Anonymous `/people` | 302 to login |
| Admin list | 200, "25 people"; not-assigned tile present |
| Manager `/people`, create, details, edit | 200 |
| Manager create with a `HireSource=Owner` field | 302; stored source NULL |
| Manager edit save, deactivate, reactivate | 302 |
| Manager POST to hire-source | 403 |
| Manager GET on deactivate | 404 |
| Manager POST without a token | 400 |
| Manager `/admin/managers` | 403 |
| Hire-source words on Manager pages | none |
| Admin sets BudgetHire | 302 |
| Admin sets a second active Owner | refused with the friendly message |
| Database check | phone stored `+923121112223`; dates and active flag correct; exactly 1 active Owner; codes HR-0001…HR-0026 |

**Visual check** (built-in browser, HTTPS, dark and light). The pane was only about 145px tall in this session, so screenshots were unreadable. I ran a scripted audit on every page and state instead, at 375px and 1440px in both themes:
- horizontal overflow
- elements past the viewport
- exactly one `<h1>`
- touch targets ≥ 40px on mobile
- text contrast against the composited glass background
- console errors

Transitions were switched off during the audit; the first pass showed they freeze mid-way in a tiny pane and give false contrast readings.

Pages and states covered:
- Admin: dashboard, people list (table and stacked cards), details (Owner, inactive), deactivate modal, reactivate modal, edit, the concurrency panel (a real conflict staged in the browser), create with client-side validation errors, and the hire-source card with the Owner confirm
- Manager: list, details and edit

Results:
- No overflow anywhere.
- One `<h1>` on every page.
- No contrast failures in either theme, modals included.
- One defect found and fixed: row links in the mobile cards were 23px tall and are now 40px tap targets.
- Client-side validation shows the field messages and red 2px borders before any request is sent.
- The date input defaults to today (Karachi).
- The reactivate modal's `min` is the day after leaving.
- No CSP violations. The only console error was the intentional 409.

**Self-review**
- No `Html.Raw`, raw SQL built from strings, inline script/style or `on*=`. The only SQL text is the constant `SELECT NEXT VALUE FOR [dbo].[PersonCodeSequence]`.
- `[AllowAnonymous]` is unchanged (login, errors, static files).
- Every mutation is POST with antiforgery.
- View models only, with no hire-source property in Manager-reachable models.
- Labels are tied to inputs, errors linked via `aria-describedby`, icon buttons have `aria-label`, modals are labelled, disabled tabs have `aria-disabled`.

## Known issues and deviations

1. **Push blocked.** The remote is empty and pushes get 403 as `BloomHouseMarketing`. M1, M2 and M3 are committed locally. Fix the credentials, then run `git push -u origin main`.
2. **"Exactly one active Owner" (SPEC §2) is enforced as *at most one*.** It can't be "exactly" while sources are unassigned or the Owner is deactivated. The dashboard's not-assigned tile helps you finish the job.
3. **Reactivation overwrites `JoiningDate`**, as specified. The earlier stint's dates survive only in the audit log. M6 and M7 rely on "employed working days" and "no absences before joining", so absences or rate records from the earlier stint would fall before the new joining date. **Suggest deciding before M6** whether to keep an employment-history table instead.
4. **Masking on the edit form.** The edit form never shows the stored CNIC or IBAN (masked, with "leave empty to keep" and a remove checkbox). If a create or edit fails validation, the form does echo back **the value the user just typed**, which is their own input, not stored data.
5. **The demo seeder needs the flag *and* Development**, so turning it on in production config does nothing.
6. **I stopped the `hr-web` preview server you started at 18:34 PKT** to release the build lock. Restart it with the command below if you want it back.
7. Not done (planned or out of scope): rate records, absences and payslips tabs (M5–M7); the employment-history question in item 3.

## Manual checks for the owner

1. Run:
   ```bash
   dotnet run --project src/HR.Web --launch-profile https
   ```
   then sign in as Admin and open **People**. To try with demo data first:
   ```bash
   dotnet user-secrets set "DemoData:Seed" "true" --project src/HR.Web
   ```
   Restart the app, then remove the flag:
   ```bash
   dotnet user-secrets remove "DemoData:Seed" --project src/HR.Web
   ```
2. Add a person with phone `0300 1234567`. The details page should show `+92 300 1234567` and a code like `HR-0026`.
3. On a details page, copy the CNIC and IBAN. The IBAN should paste without spaces.
4. Open Edit. The CNIC and IBAN are masked; save without touching them and they stay. Then open the same edit page in two tabs, save one, then the other: the second shows the "someone else saved changes" panel.
5. Deactivate someone (try a date before joining first), then reactivate (try the leaving date itself first).
6. As Admin, set a hire source; choosing **Owner** asks for confirmation. Try making a second active person Owner.
7. Sign in as a Manager in a private window. The People pages and dashboard have no hire-source anything, and the sidebar has no Invoices or Owner Income.
8. With the pane or window at full size, glance at the People list in both themes and at phone width. My visual audit was scripted, because the pane was too small for screenshots.
9. Decide on item 3 (employment history) before M6.
