# HR Payroll — instructions for Claude Code

Read `docs/SPEC.md` before any work. It holds the business rules, the payroll math, the golden test cases and the roadmap. Business rules come only from SPEC.md; if something is unclear or missing there, ask the owner instead of inventing a rule.

Work one milestone at a time. Never start the next milestone until the owner sends its prompt.

## Environment
- Windows, the owner's machine. SQL Server **Express**, instance `.\SQLEXPRESS`, Windows authentication.
- Dev connection string (in `appsettings.Development.json`):
  `Server=.\SQLEXPRESS;Database=HRPayroll;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true`
  Machine-specific overrides go in the optional, git-ignored `appsettings.Development.local.json`, which loads right after `appsettings.Development.json`.
- Integration tests use a separate database, `HRPayroll_Test`, on the same instance. Tests create and drop it; never touch `HRPayroll` from tests.
- .NET: the newest LTS SDK installed (.NET 10 preferred, else .NET 8), pinned in `global.json`. EF Core and ASP.NET Core packages match that major version.
- Never put production secrets in the repo. Production config comes from environment variables or user secrets.

## Solution layout
```
HRPayroll.sln
global.json, Directory.Build.props, Directory.Packages.props   (central package management)
src/HR.Domain          entities, enums, value objects, pure calculators (no EF, no ASP.NET references)
src/HR.Infrastructure  AppDbContext, entity configurations, migrations, services, seeding
src/HR.Web             controllers, view models, views, tag helpers, wwwroot
tests/HR.Tests         xUnit: Unit/ (domain), Integration/ (WebApplicationFactory + SQL Express test DB)
docs/SPEC.md, docs/milestones/Mx-report.md
```
Dependencies point inward only: Web → Infrastructure → Domain.

## Code conventions
- Nullable enabled, `TreatWarningsAsErrors` true, implicit usings, file-scoped namespaces, async all the way for I/O.
- Money and rates are `decimal`. Columns: money `decimal(18,2)`; exchange rates `decimal(18,4)`; day counts `decimal(5,2)`. Rounding per SPEC §5 via one shared helper (`Money.RoundUsd`, `Money.RoundPkr`). Never `double`/`float` for money.
- Dates are `DateOnly`; timestamps are `DateTimeOffset` stored in UTC. "Today" comes from an injectable `IClock` using Asia/Karachi. Never call `DateTime.Now` in domain or services.
- Business math lives in `HR.Domain` as pure, deterministic classes (e.g. `PayPeriod`, `WorkingDays`, `PaidLeaveAllocator`, `PayrollCalculator`). Controllers stay thin: validate, call a service, map to a view model.
- Never bind entities directly to forms or views; use view models (prevents over-posting). Separate view models for Admin and Manager wherever Admin-only data exists, so Admin-only fields are never even loaded for a Manager.
- Authorization through named policies (`AdminOnly`, `ManagerOrAdmin`) on controllers or actions. Default policy: authenticated. `[AllowAnonymous]` only on login, error and static pages.
- History tables (rates, exchange rates) are append-only; finalized payroll data is immutable.
- Display formats: USD `$1,234.56`; PKR `Rs 1,234,567` (whole rupees, en-PK grouping); dates `08 Oct 2026`. Money columns right-aligned with tabular figures.
- Every migration gets a meaningful name. Never edit a migration that's already committed; add a new one.

## Security baseline (must hold at every milestone)
- HTTPS redirection and HSTS (non-development).
- Global `AutoValidateAntiforgeryTokenAttribute`; every state-changing action is POST (never GET).
- Security headers on every response: `Content-Security-Policy` (`default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`), `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy` (camera, microphone, geolocation off), `X-Frame-Options: DENY`.
- Therefore: **no inline `<script>`, no inline `style=""` attributes, no `on*=` handlers, no CDN links**. All CSS/JS/fonts/icons are self-hosted under `wwwroot`.
- Razor encoding only; never `Html.Raw` on user data. EF parameterized queries only; no string-built SQL.
- Auth cookies HttpOnly, Secure, SameSite=Lax. Production error pages reveal nothing (no stack traces, no exception text).
- Log security events (login success/failure, lockout, role changes, payroll finalize/reopen) without logging passwords or tokens.

## UI design system: "Aurora Glass"
Modern, responsive glassmorphism on Bootstrap 5.3 (bundled with the MVC template) plus our own CSS. Light and dark themes via `data-bs-theme` on `<html>`.

**Files**: `wwwroot/css/tokens.css` (variables only), `wwwroot/css/glass.css` (components), `wwwroot/css/site.css` (page bits), `wwwroot/js/theme-init.js` (loaded in `<head>`, sets theme before paint, no inline script), `wwwroot/js/site.js`. Fonts and icons via LibMan into `wwwroot/lib`: Plus Jakarta Sans (`@fontsource/plus-jakarta-sans`) and Bootstrap Icons.

**Background**: a fixed "aurora" layer behind everything. Base `#0B1020` (dark) or `#EEF2FF` (light), with three large blurred radial blobs: indigo `#6366F1`, violet `#A855F7`, teal `#14B8A6`. Blob opacity 0.45 (dark) or 0.30 (light). In dark mode a veil `--aurora-veil: rgba(11,16,32,.50)` sits over the blobs (owner-approved in M1) so muted text on glass stays ≥ 4.5:1; light mode has no veil. They drift slowly (60s+ loop); the animation is off under `prefers-reduced-motion`.

**Glass surface tokens**

| Token | Dark | Light |
|---|---|---|
| `--glass-bg` | `rgba(255,255,255,.06)` | `rgba(255,255,255,.62)` |
| `--glass-bg-strong` (sidebar, modals, tables) | `rgba(17,24,39,.72)` | `rgba(255,255,255,.80)` |
| `--glass-border` | `rgba(255,255,255,.12)` | `rgba(255,255,255,.70)` |
| `--text` / `--text-muted` | `#E6E8F2` / `#A3A9C2` | `#0F172A` / `#475569` |

Blur: `backdrop-filter: blur(18px) saturate(160%)`. Highlight: `inset 0 1px 0 rgba(255,255,255,.08)`. Shadow: `0 8px 32px rgba(2,6,23,.25)`. Radius: 20px for cards, 12px for inputs and buttons, 999px for pills.
- Fallbacks: `@supports not (backdrop-filter: blur(1px))` and `prefers-reduced-transparency` → use the opaque `--glass-bg-strong`.
- Accents (darkened in M2 so white labels pass 4.5:1): primary is an indigo→violet gradient (`#4F46E5`→`#7C3AED`, hover `#4338CA`→`#6D28D9`). Danger `#E11D48` (hover `#BE123C`). Success `#10B981`, warning `#F59E0B`, info `#0EA5E9`.
- Text on solid surfaces: white on primary and danger; dark `#0F172A` on any solid success or warning surface (never white).
- Type: Plus Jakarta Sans. Page title 1.5rem/700, card title 1rem/600, body 0.9375rem. `font-variant-numeric: tabular-nums` on numbers.

**Layout**
- A glass sidebar 260px wide (icon + label), collapsible to a 76px icon rail (state remembered in `localStorage`).
- Below `lg`, the sidebar becomes a Bootstrap offcanvas.
- A sticky glass top bar holds the page title and breadcrumb, a theme toggle and the user menu.
- Content max-width 1400px, gutters 24px (16px on mobile).

**Components** (all shown on the dev-only `/dev/styleguide` page):
- Containers and display: `.glass-card`, `.stat-tile` (icon chip, label, value, optional delta), `.glass-table`, status pills, empty state (icon, sentence, primary action).
- `.glass-table`: sticky header, subtle row hover, money right-aligned. Below `md` it switches to stacked cards using `data-label` on cells.
- Actions and forms: primary gradient button, ghost button, icon button (needs `aria-label`), form controls with floating labels and visible validation states.
- Feedback: toast (TempData success/error, auto-dismiss), confirm modal for destructive or irreversible actions (deactivate, finalize).

**Accessibility (required)**
- Body text contrast ≥ 4.5:1 on glass in both themes; check it, don't assume.
- Visible focus ring: 2px accent outline, 2px offset.
- Everything works by keyboard. One `<h1>` per page. Form labels tied to inputs.
- Touch targets ≥ 40px on mobile.

## Testing strategy
- **Unit** (`tests/HR.Tests/Unit`): all domain math. The golden cases in SPEC §9 are mandatory once their milestone arrives (M7), with exactly those numbers.
- **Integration** (`tests/HR.Tests/Integration`): `WebApplicationFactory` against `HRPayroll_Test`. Cover status codes, authorization (anonymous → login redirect; Manager → 403 on Admin routes; Manager responses never contain Admin-only data), antiforgery rejection, security headers, and the happy path of each feature.
- One shared fixture creates or migrates the test DB once per run, resets data between tests, and drops the DB at the end.
- A model test asserts `HasPendingModelChanges()` is false.

## Milestone protocol
For every milestone:
1. **Plan**: list the files and migrations you'll add or change. Flag anything in the prompt that conflicts with SPEC.md and ask before building.
2. **Implement** only that milestone's scope.
3. **Verify**, fixing and re-running until everything passes:
   - `dotnet build` (zero warnings).
   - `dotnet test` (all green). Add tests for everything new: domain rules, authorization, validation.
   - `dotnet ef database update` against the dev DB.
   - Run the app and smoke-test every new route: expected status codes, role restrictions, a create/edit round trip.
   - Visual check: if you have a browser or preview tool, check new pages at 375px and 1440px in both themes (layout, overflow, contrast, focus). If you don't, write a short manual checklist for the owner instead.
   - Self-review the diff against the Security baseline and the Accessibility list.
4. **Report**: write `docs/milestones/Mx-report.md` covering:
   - what was built
   - migrations added
   - test counts (passed/failed/skipped)
   - smoke-test results
   - known issues and deviations from SPEC (should be none)
   - manual checks for the owner.
5. **Commit** only when step 3 is fully green, with message `Mx: <summary>`. Push to `origin` if it exists. Never commit secrets, `bin/`, `obj/` or `*.user`.
6. **Stop.** Show the owner the report and wait for the next prompt.
