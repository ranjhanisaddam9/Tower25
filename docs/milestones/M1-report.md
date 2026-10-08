# M1 report: foundation, data layer, Aurora Glass design system, security baseline

Date: 08 Oct 2026 · Status: **complete, all checks green**

## Environment (Step 0)

| Item | Finding |
|---|---|
| .NET SDK | Only 9.0.307 (STS) was installed. With owner approval, the **.NET 10.0.401 SDK** was installed via winget and pinned in `global.json` (`rollForward: latestFeature`). Runtime 10.0.12. |
| SQL Server | `.\SQLEXPRESS` = `SADDAMKHAN-PC\SQLEXPRESS`, Express 2019 (15.0.2190.7), Windows auth OK. |
| dotnet-ef | Updated 8.0.4 → **10.0.12** (matches EF Core 10.0.12). |
| LibMan CLI | 2.1.175 already present. |
| Disk | C: ran out of space mid-setup; the owner freed space (20.5 GB free afterwards). |

## What was built

**Solution** (`HRPayroll.sln`): `src/HR.Domain`, `src/HR.Infrastructure`, `src/HR.Web` (MVC) and `tests/HR.Tests` (xUnit). References point inward only (Web → Infrastructure → Domain).
- `Directory.Build.props`: net10.0, nullable, implicit usings, `TreatWarningsAsErrors`.
- `Directory.Packages.props`: central package management; EF Core and ASP.NET Core packages at 10.0.12.

**Domain** (`HR.Domain`, pure, no EF or ASP.NET references)
- `Time/IClock`: the single source of `UtcNow` and `Today`.
- `Time/PakistanTime`: converts an instant to the Asia/Karachi date. It falls back to the Windows zone id, then to a fixed UTC+5 zone.
- `Payroll/PayPeriod.For(date)`: returns the 1st–15th or the 16th–month-end period. Also has `Next()`, `Previous()`, `Contains()` and `WorkingDayCount`.
- `Payroll/WorkingDays.Count(start, end)`: counts Monday–Friday, both ends inclusive; an empty range gives 0. `IsWorkingDay(date)`.
- `Payroll/Money.RoundUsd` / `RoundPkr`: `MidpointRounding.AwayFromZero`, to 2 decimals and to whole rupees.

**Infrastructure**
- `ApplicationUser : IdentityUser`, adding `FullName` (required, max 200), `IsActive` and `CreatedAt` (`DateTimeOffset`, UTC). `AppRoles` holds the role-name constants, ready for M2.
- `AppDbContext : IdentityDbContext<ApplicationUser>`, with entity configurations applied from the assembly.
- `AddInfrastructure()`: registers SQL Server using the `DefaultConnection` connection string (resolved lazily, with a clear error if it's missing), `TimeProvider`, and `IClock` → `SystemClock`.
- `DatabaseMigrator`: migrates at startup only when `Database:MigrateOnStartup` is true. That's set only in `appsettings.Development.json`; the default is false.

**Security baseline**
- HTTPS redirection everywhere. HSTS outside Development: 365 days, with subdomains.
- A global `AutoValidateAntiforgeryTokenAttribute`. The only state-changing action (the style guide's toast demo) is POST.
- `SecurityHeadersMiddleware` sets these on every response, including static files and error pages:
  - the exact CSP from CLAUDE.md
  - `X-Content-Type-Options: nosniff`
  - `X-Frame-Options: DENY`
  - `Referrer-Policy: strict-origin-when-cross-origin`
  - `Permissions-Policy: camera=(), microphone=(), geolocation=()`
- The Kestrel `Server` header is removed.
- Cookies: the cookie policy sets HttpOnly, Secure and SameSite=Lax on every cookie. The antiforgery cookie (`hr.af`) and TempData cookie (`hr.tempdata`) are configured the same way explicitly.
- Error pages: `UseStatusCodePagesWithReExecute("/error/{0}")` in every environment, plus `UseExceptionHandler("/error/500")` outside Development. `ErrorController` (`[AllowAnonymous]`) renders a friendly page for 400, 403, 404, 405 and 5xx, with no exception text, stack trace or request id.
- Named policies `AdminOnly` and `ManagerOrAdmin` are registered.

**Aurora Glass design system**
- LibMan self-hosts Plus Jakarta Sans 5.2.8 (weights 400–700, with its OFL licence) and Bootstrap Icons 1.13.1 under `wwwroot/lib`. Bootstrap 5.3.3 comes from the template. There are no CDN links.
- `tokens.css` (variables only, both themes), `glass.css` (components and layout), `site.css` (page bits).
- `theme-init.js` runs in `<head>` and applies the theme and rail state before paint. `site.js` handles the theme toggle, the rail collapse (stored in `localStorage`), toasts and the confirm modal.
- `_Layout`:
  - fixed aurora background: three drifting blobs, motion off under `prefers-reduced-motion`
  - skip link
  - glass sidebar: 260px, collapsible to a 76px rail that's remembered; Bootstrap `offcanvas-lg` below `lg`
  - sticky glass top bar: breadcrumb, the single `<h1>` page title, theme toggle, placeholder user chip
  - toast partial (TempData success/error, auto-dismiss) and a shared confirm modal
- Sidebar: Dashboard is active. People, Absences, Salaries, Exchange Rates, Payroll, Invoices, Owner Income, Reports and Managers are disabled, each with a "Soon" pill.
- Fallbacks: `@supports not (backdrop-filter)` and `prefers-reduced-transparency` both switch `--glass-bg` to `--glass-bg-strong`.

**Dashboard** (`Home/Index`), with four stat tiles:
- Active people: "—".
- USD/PKR rate: "—".
- Pay period: the current period from `PayPeriod.For(IClock.Today)` with its working-day count, plus the next period and its count. On 08 Oct 2026 this shows *01–15 Oct 2026, 11 working days; Next: 16–31 Oct 2026, 11 working days*.
- Pending absences: "—".

**Style guide** (`/dev/styleguide`, Development only, 404 elsewhere) shows:
- colours and typography
- buttons: primary gradient, ghost, danger, disabled, icon buttons with `aria-label`
- status pills and stat tiles (with deltas)
- floating-label form controls with valid and invalid states, a select, a textarea and a checkbox
- a money table with a stacked mobile mode driven by `data-label`
- the empty state
- toasts (a real POST-redirect-GET through antiforgery) and the confirm modal

**Display formats** (`HR.Web/Formatting/DisplayFormat`): `$1,234.56`, `Rs 1,234,567` (en-PK uses 3-digit grouping on .NET 10/ICU, as CLAUDE.md shows), dates as `08 Oct 2026`.

**Housekeeping**
- `.gitignore`: the Visual Studio template, plus `*.user`, `appsettings.*.local.json` and `.claude/`.
- `README.md`: prerequisites, setup, run, test, migrations and production config.
- `docs/milestones/`, git initialised, origin added.

## Migrations added

| Migration | Contents |
|---|---|
| `20261008180726_InitialCreate` | ASP.NET Identity schema, with `AspNetUsers.FullName nvarchar(200) NOT NULL`, `IsActive bit NOT NULL` and `CreatedAt datetimeoffset NOT NULL`. Applied to the `HRPayroll` dev database. |

## Tests: 86 passed, 0 failed, 0 skipped

| Area | Class | Tests |
|---|---|---|
| Unit | `PayPeriodTests`: all five SPEC §9 period cases, Feb 16–29 2028 = 10 working days, boundaries, Next/Previous across month and year | 15 |
| Unit | `WorkingDaysTests`: Oct 1–15 2026 = 11, Oct 16–31 2026 = 11, Feb 16–29 2028 = 10, weekends, empty range | 14 |
| Unit | `MoneyTests`: .5 midpoint away from zero for USD and PKR, including negatives | 14 |
| Unit | `DisplayFormatTests` | 11 |
| Unit | `ClockTests`: Karachi date around midnight and New Year | 5 |
| Integration | `SecurityBaselineTests`: the five headers on pages, static files and 404s in both environments; exact CSP; no inline script, `style=""`, `on*=` or CDN links; cookie flags | 13 |
| Integration | `DashboardTests`: `GET /` returns 200 with the sidebar and exactly one `<h1>`; disabled sections; real period dates with a fixed clock (Oct 8, Dec 31 → Jan) | 4 |
| Integration | `StyleguideTests`: 200 in Development, 404 in Production; POST without a token → 400; with a token → 302 and a one-time toast | 4 |
| Integration | `ErrorPageTests`: custom 404 in both environments; generic Production 500 that leaks no exception text; HTTP→HTTPS redirect and HSTS | 4 |
| Integration | `ModelTests`: `HasPendingModelChanges()` is false; user column configuration | 2 |

The integration fixture migrates `HRPayroll_Test` once per run, clears its data before each test, and drops it at the end. A guard refuses to touch any other database. After the run, only `HRPayroll` remains on the instance.

## Verification

- `dotnet build`: **0 warnings, 0 errors** (`TreatWarningsAsErrors` on).
- `dotnet test`: **86/86 passed**.
- `dotnet ef database update` against `HRPayroll`: up to date.

**Smoke test** (`dotnet run`, https profile, `curl`)

| Request | Result |
|---|---|
| `GET /` | 200 |
| `GET /dev/styleguide` | 200 |
| `GET /nope` | 404, custom page |
| `GET /error/404` | 404 |
| CSS, JS, woff2 font and icon files | 200 |
| `http://localhost:5073/` | 307 → `https://localhost:7016/` |
| All five security headers | present |
| `POST /dev/styleguide/toast` without a token | 400 |
| `GET` on that POST-only action | 404 |
| POST with a token (round trip) | 302 → success toast rendered |

**Visual check** (built-in browser, 1440px and 375px, dark and light)
- No horizontal overflow at either width.
- Fonts and icons load from `wwwroot`. Console has no CSP violations.
- At 375px:
  - The sidebar becomes an offcanvas. It opens with focus inside and closes on Escape, returning focus to the menu button.
  - The money table stacks into labelled cards, and money stays right-aligned.
  - Gutters are 16px, and every touch target is at least 40px. The checkbox input is 19px, but its row is 40px and its label is clickable.
- At 1440px, the rail collapses to 76px, keeps that across a reload, and updates `aria-expanded` and its label.
- Keyboard focus ring: 2px accent outline at 2px offset.
- The theme toggle updates `data-bs-theme`, `localStorage` and its `aria-label`.
- The confirm modal blocks the submit, focuses the confirm button, and submits only after confirmation.
- Fixes made during the check: the pay-period value no longer wraps (1.25rem, no wrap), and the breadcrumb is hidden below `sm` so the top bar stays one line.

**Self-review**
- No `Html.Raw`, raw SQL, `DateTime.Now`, `double` or `float`, inline `<script>`, `style=""` or `on*=` in our code.
- `[AllowAnonymous]` is used only on `ErrorController`.
- View models only; no entities in views.
- Headings are in order, labels are tied to inputs, decorative icons have `aria-hidden`, and icon buttons have `aria-label`.

## Known issues and deviations (owner decisions)

1. **Dark aurora veil.** Owner-approved before building. With the exact tokens, `--text-muted` on `.06` glass over a `.45` blob measured about 3.0–3.9:1. A 50% `#0B1020` veil over the aurora in dark mode (`--aurora-veil`) brings the worst case to about 4.7:1 or better. The light theme is unchanged.
2. **Label shade on solid buttons.** White text on the specified gradient (`#6366F1`→`#8B5CF6`) is 4.2–4.5:1, and on danger `#F43F5E` it's 3.7:1. Both are below 4.5:1 for 15px text. The accent colours are unchanged; a slate shade is layered on top (12% for primary, 18% for danger), which gives about 5.0:1. Please confirm or choose another approach.
3. **Connection string.** At the owner's request, `appsettings.Development.json` uses `Server=SADDAMKHAN-PC\SQLEXPRESS` rather than the `.\SQLEXPRESS` written in CLAUDE.md. It's the same instance, but it only works on this PC. Consider updating CLAUDE.md, or keeping machine-specific values in a git-ignored `appsettings.Development.local.json`.
4. **The authenticated-by-default fallback policy is not on yet.** There's no login or authentication scheme until M2, so turning it on now would lock every page. It's planned for M2, and the named policies are already registered.
5. **Security-event logging.** There are no security events to log until M2 (login, lockout, roles).
6. **The dev HTTPS certificate isn't trusted on this machine**, so the browser pane couldn't open `https://localhost:7016`. I didn't change your certificate store. For the visual check, I ran an HTTP-only Development instance and viewed pages rendered by the HTTPS instance as temporary static files, which I deleted afterwards.
   - Over plain HTTP, the style guide's forms correctly refuse to render (500 in Development only), because the antiforgery cookie is `Secure`-only. Use HTTPS for local work.
7. Non-ASCII text (–, —, ·) is emitted unencoded (`WebEncoderOptions` set to `UnicodeRanges.All`). HTML-significant characters are still encoded.

## Manual checks for the owner

1. Trust the dev certificate once:
   ```bash
   dotnet dev-certs https --trust
   ```
   Then run:
   ```bash
   dotnet run --project src/HR.Web --launch-profile https
   ```
   and open https://localhost:7016.
2. On the dashboard, confirm today's period: on 08 Oct 2026 it should read *01–15 Oct 2026 · 11 working days*, with *Next: 16–31 Oct 2026 · 11 working days*.
3. Toggle the theme (sun/moon button), then reload. The theme should stick, with no flash of the other theme.
4. Collapse the sidebar (bottom button), then reload. The rail should persist. Narrow the window below ~992px and check the ☰ menu opens the drawer.
5. On `/dev/styleguide`:
   - click "Show success toast" and "Show error toast";
   - click "Deactivate (confirm modal)", cancel, then confirm;
   - narrow to phone width and check the money table turns into cards.
6. Tab through the dashboard using only the keyboard. Every control should show the focus ring, and the "Skip to main content" link should appear first.
7. Look at the dark theme on your monitor. Muted text should be readable over all three blobs.
8. Decide on deviations 2 and 3 above.
