# M2 report: authentication, roles and Manager management

Date: 09 Oct 2026 · Status: **complete; build, tests, migration and checks green. The push is blocked by GitHub credentials (see Known issues).**

## Part A: M1 follow-ups

| # | Item | Result |
|---|---|---|
| 1 | Push M1 to origin | **Still blocked.** Git on this PC authenticates as the GitHub user `BloomHouseMarketing`, which gets a 403 on `ranjhanisaddam9/Tower25`. Both commits are local and get pushed the moment the credentials are fixed. |
| 2 | Button contrast | The slate-overlay workaround is gone. New tokens: primary `#4F46E5`→`#7C3AED` (hover `#4338CA`→`#6D28D9`), danger `#E11D48` (hover `#BE123C`). Added `--on-accent`/`--on-danger` (white) and `--on-success`/`--on-warning` (`#0F172A`), plus new `.btn-success-solid` and `.pill-solid-*` variants, which use dark text on success and warning. CLAUDE.md is updated. |
| 3 | Connection string | `appsettings.Development.json` is back to `Server=.\SQLEXPRESS`. A new optional, git-ignored `appsettings.Development.local.json` loads directly after it (`LocalSettings.AddLocalSettingsFile`), so user secrets and environment variables still win, and it's excluded from build and publish output. This PC's `SADDAMKHAN-PC\SQLEXPRESS` now lives there. Documented in README and CLAUDE.md. |
| 4 | Dark aurora veil | Recorded in CLAUDE.md as the approved token `--aurora-veil: rgba(11,16,32,.50)` (dark only). |

**Contrast re-check (WCAG relative luminance):**

| Pair | Ratio |
|---|---|
| White on `#4F46E5` | 6.3:1 |
| White on `#7C3AED` | 5.7:1 |
| White on `#E11D48` | 4.7:1 |
| White on hover `#4338CA` | 7.9:1 |
| White on hover `#6D28D9` | 7.0:1 |
| White on hover `#BE123C` | 5.7:1 |
| `#0F172A` on `#10B981` | 7.1:1 |
| `#0F172A` on `#F59E0B` | 8.4:1 |
| `#0F172A` on `#059669` | 4.8:1 |
| Danger and warning alert text on their tints (light) | about 5.0:1 and 6.2:1 |

All pass 4.5:1.

## Part B: Authentication

- **Identity**
  - ASP.NET Core Identity with the `Admin` and `Manager` roles (`AppRoles`), wired in `AddInfrastructure`.
  - Unique email, and the username is the email.
  - Passwords: at least 10 characters, with upper, lower and digit.
  - Lockout: 5 failures → 15 minutes, also for new users.
  - No email confirmation. No default Identity UI was scaffolded.
- **Auth cookie** (`hr.auth`)
  - HttpOnly, Secure, SameSite=Lax.
  - 8-hour sliding expiry, session cookie only (no "remember me").
  - `LoginPath /account/login`, `AccessDeniedPath /error/403`.
- **Fallback policy:** authenticated users only. `[AllowAnonymous]` appears only on login (GET and POST), `ErrorController`, and `MapStaticAssets().AllowAnonymous()`.
- **Login** (`AccountController`, centred glass card on the new `_AuthLayout`)
  - One generic message for unknown email, wrong password, inactive account and lockout (owner decision). Unknown emails also run a dummy password-hash check, so response timing doesn't reveal whether an email exists.
  - Inactive users are refused by `AppSignInManager.CanSignInAsync`.
  - `returnUrl` is honoured only if `Url.IsLocalUrl` passes; otherwise the user goes to `/`.
  - `LastLoginAt` is recorded on success.
  - Rate limit: the built-in rate limiter allows 10 POSTs per minute per client IP. The 11th gets a 429 with `Retry-After`, and the friendly `/error/429` page renders in place.
- **Logout:** POST only, with an antiforgery token, from the user menu. A GET returns 405 with `Allow: POST`.
- **Change password:** available to every signed-in user. The current password is required and the new one must differ. On success the cookie is reissued with fresh claims.
- **Forced password change:** `MustChangePassword` is carried as a cookie claim. `ForcePasswordChangeMiddleware` redirects every MVC action to `/account/change-password` except change password, logout and the error pages; static files pass through.
- **Kicking out deactivated users**
  - Deactivating a user, resetting their password or changing their email rotates the security stamp.
  - `ActiveUserSecurityStampValidator` also requires `IsActive`.
  - The validation interval is 1 minute.
- **Admin seeding** (`AdminSeeder`, at every startup)
  - Ensures both roles exist.
  - Creates the Admin only when no user has the Admin role, from `Seed:Admin:*`, with `MustChangePassword = true`.
  - Logs a warning and skips if the password is missing, or if it's found in any `appsettings*.json`.
  - Idempotent.
  - Your Admin email (`ranjhani.saddam@gmail.com`) and name (`Administrator`) are in **user secrets**, not a committed file. **Seeding happens once you set the password** (see Manual checks).
- **Migration** `AddUserSecurityFields` adds `MustChangePassword bit NOT NULL DEFAULT 0` and `LastLoginAt datetimeoffset NULL`.

## Part C: Manager management (`/admin/managers`, `AdminOnly`)

- **List**
  - Search by name or email; filter All/Active/Inactive; 20 per page with previous/next.
  - Shows last login (Asia/Karachi time) and status pills (Active/Inactive, plus "Temp password" while a change is pending).
  - Empty states for "no managers yet" and "no matches".
  - Only Manager-role users are listed; Admins never appear here.
- **Create:** full name and email. The temporary password is 14 characters from a cryptographic RNG, always has upper, lower and digit, and skips look-alike characters (0/O, 1/l/I). It is rendered **once**, straight from the POST response, with `Cache-Control: no-store` and a copy button. It is never put in TempData or logs. `MustChangePassword = true`.
- **Edit:** full name and email. The email stays unique, and changing it also changes the username and ends that Manager's sessions.
- **Activate / Deactivate:** POST only, behind the confirm modal (primary or danger variant). An Admin can't deactivate their own account.
- **Reset password:** generates a new temporary password, shows it once, sets `MustChangePassword`, clears any lockout, and rotates the stamp so existing sessions end.
- **Layout**
  - The sidebar is role-aware: Managers is a live link for Admins and isn't listed at all for Managers. Other sections keep their "Soon" pills.
  - The user chip shows initials, the real name and the role, with a menu: change password, sign out.
- **Security log** (`HR.Security` category, structured `LoggerMessage`, event ids 1000–1021)
  - Events: login success, failure (with reason), lockout and rate-limit hits; logout; password change; Manager created, edited, activated, deactivated and password reset (with actor and target ids); blocked self-deactivation; seeding outcome.
  - Never passwords or tokens.

## Migrations added

| Migration | Contents |
|---|---|
| `20261008191235_AddUserSecurityFields` | `AspNetUsers.MustChangePassword` (bit, not null, default false) and `AspNetUsers.LastLoginAt` (datetimeoffset, null). Applied to `HRPayroll`. |

## Tests: 134 passed, 0 failed, 0 skipped

74 integration + 60 unit. In M1 it was 86.

| Class | Tests | Covers |
|---|---|---|
| `AuthenticationTests` | 22 | Anonymous → 302 to login with `ReturnUrl`; generic message for wrong password and unknown email (identical); 5 failures → locked; inactive refused; external `returnUrl` (`https://evil.example`, `//evil`, `/\evil`, `javascript:`) → `/`; local `returnUrl` followed; 11th POST from one IP → 429 (another IP unaffected); logout GET → 405, POST without a token → 400, POST with a token signs out; weak passwords (short, no upper, no lower, no digit) rejected; wrong current password and mismatched confirmation; successful change; login/logout events logged without passwords |
| `ManagerManagementTests` | 10 | Manager → 403 on every GET and every POST; anonymous → login; list, search, filter, paging and empty states; **create → Manager logs in → forced change → dashboard**; duplicate email and invalid input; edit with unique email; 404 for unknown and Admin ids; **deactivate → login refused → open session rejected after the validation interval** (adjustable test clock) → reactivate; Admin can't deactivate self; **reset ends the session and forces a change**; temporary password never in logs |
| `AdminSeederTests` | 5 | Creates the Admin once (idempotent); no-op when an Admin exists; warning and skip when the password is missing; refuses a password from an `appsettings` file; both roles created |
| `TemporaryPasswordGeneratorTests` (unit) | 5 | Length 14; policy in 1,000 samples; no look-alike characters; **no repeats in 1,000 samples**; guaranteed characters are shuffled |
| M1 classes (updated for login) | 92 | Dashboard (now signed in, role-aware sidebar, user chip), style guide (sign-in required, 404 in Production), error pages (anonymous unknown URL → login), security headers, no inline script or style on login, admin, change-password and other pages, cookie flags for `hr.af` and `hr.auth`, model has no pending changes |

**Test seams**
- Each test client gets its own fake client IP (a test-only startup filter), so the real 10-per-minute limit applies without tests throttling each other.
- An adjustable `TimeProvider` drives security-stamp validation, so the real 1-minute interval is tested.
- A capturing log provider backs the "never logged" assertions.

## Verification

- `dotnet build`: **0 warnings, 0 errors**.
- `dotnet test`: **134/134 passed**.
- `dotnet ef database update` against `HRPayroll`: both migrations applied. It has 0 users until the Admin is seeded.

**Smoke test over HTTPS.** I didn't use your real Admin or dev DB for this. A disposable instance ran against a temporary `HRPayroll_Visual` database, with a throwaway QA Admin seeded from environment variables. The database and the throwaway credentials were deleted afterwards.

| Check | Result |
|---|---|
| Anonymous requests to `/`, `/admin/managers`, `/account/change-password`, `/dev/styleguide`, `/nope` | 302 to login with `ReturnUrl` |
| CSS | 200 |
| Login POST without a token | 400 |
| Seeding | Created the QA Admin once with `MustChangePassword`; log line has only the user id |
| QA Admin login | Forced change; a weak password was rejected; after the change, the dashboard with a toast |
| User menu | Sign out works, as a POST |
| Create Manager | Validation errors; on success, the shown-once screen; copy button works |
| List and edit pages | Render correctly |
| Reset password | Through the confirm modal; new shown-once screen; server log has the event with actor and target ids and **no password** (searched) |
| Six wrong logins | Generic message; `LockoutEnd` 15 minutes ahead |
| Further POSTs | Friendly 429 page |

**Visual check** (built-in browser, HTTPS; 375px and 1440px, dark and light): login, lockout message, 429 page, change password (forced and with errors), Managers list (desktop table and stacked mobile cards), create (with validation), edit (with the Access card), confirm modal, temporary-password screen (create and reset), and the user menu.

| Check | Result |
|---|---|
| Horizontal overflow | None on any page at either width or theme |
| `<h1>` per page | Exactly one |
| Touch targets | No visible control under 40px |
| Login card | Centred; 420px on desktop, full width minus 16px gutters on a phone |
| Focus ring | Visible |
| Copy button | Announces "Copied" via a live region |

Fixed during the visual pass:
- Server-side invalid inputs now get a red border (`.input-validation-error`), and field errors are linked with `aria-describedby`.
- Status pills and row actions are grouped so they don't spread apart in stacked mobile cards.
- The duplicate heading on signed-out error pages is gone.

**Self-review**
- Security baseline holds: no `Html.Raw`, no raw SQL, no inline script or style, no `on*=`; every state change is POST with antiforgery; cookies are HttpOnly, Secure and Lax; error pages leak nothing.
- Forms use view models only (`ManagerFormViewModel` binds just FullName and Email), so there's no over-posting.
- Accessibility: labels tied to inputs; errors in `role="alert"` regions or linked by `aria-describedby`; icon buttons have `aria-label`; the dropdown is keyboard-operable (Bootstrap).

## Known issues and deviations

1. **Push blocked (M1 and M2 commits).** Git on this PC authenticates to GitHub as `BloomHouseMarketing`, which has no write access to `ranjhanisaddam9/Tower25`. Fix the credentials, or grant that account access, then run `git push -u origin main`.
2. **403 rendered in place.** `AccessDeniedPath` is `/error/403`, but a forbidden request gets a **403 status on the requested URL** with the access-denied page, not a 302 redirect. Stated in the plan; this is what makes "Manager → 403" hold.
3. **`GET /account/logout` returns 405.** It's an explicit action that returns 405 with `Allow: POST`, because ASP.NET's default for a method mismatch here was 404.
4. **Anonymous visitors to unknown URLs are sent to sign-in** rather than shown a 404, because the fallback policy applies before routing decides. Signed-in users get the custom 404.
5. **Sign-out of an open session takes up to 1 minute after deactivation or reset.** That's the requested validation interval. New sign-ins are refused immediately.
6. **Rate-limit IP behind a proxy.** The limiter uses the connection's remote IP. Behind a reverse proxy this needs forwarded headers, which are planned for the M10 deployment guide.
7. **Dashboard still shows "—"** for people, rate and absences; those arrive in M3, M4 and M6.

## Manual checks for the owner

1. **Set your Admin password.** Type it yourself, and don't paste it into chat:
   ```bash
   dotnet user-secrets set "Seed:Admin:Password" "<your temporary password>" --project src/HR.Web
   ```
   It needs at least 10 characters, with an uppercase letter, a lowercase letter and a digit. Your email and name are already in user secrets.
2. Run the app and sign in at https://localhost:7016 as `ranjhani.saddam@gmail.com`:
   ```bash
   dotnet run --project src/HR.Web --launch-profile https
   ```
   You'll be sent to **Change password** first. After you change it, remove the seed password:
   ```bash
   dotnet user-secrets remove "Seed:Admin:Password" --project src/HR.Web
   ```
3. Managers → Add Manager. Copy the temporary password from the shown-once screen. In a private window, sign in as that Manager and confirm the forced change, then check the sidebar has no "Managers" entry and that `/admin/managers` shows "Access denied".
4. As Admin, deactivate that Manager. Within about a minute the private window is sent back to sign-in, and signing in again fails with the generic message.
5. As Admin, reset the Manager's password. The old session ends and the new temporary password works once.
6. Enter a wrong password 5 times. The account locks for 15 minutes, and the message stays the same generic text.
7. Check both themes and a phone-width window on the Managers pages.
