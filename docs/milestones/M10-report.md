# M10 report: security audit and hardening

**Status: complete for local use; committed.**
- The build has 0 warnings (Debug and Release).
- **571/571 tests pass** (353 unit, 218 integration, 0 skipped).
- The dev database is migrated.
- Owner decisions during the milestone:
  - Part C (deployment, IIS, backup scripts and restore drill) is deferred.
  - Docker / OWASP ZAP is not used.
  - The app runs locally only.
- The **v1.0.0 tag is held** until deployment is done.

## Decisions agreed before building
- **Reopen reason:** the reason stays in the payroll's own history. The AuditLog row only records who reopened which run and when. SPEC §6 and its change log are updated.
- **Anonymous `/health`:** approved as an exception to the anonymous-access rule. It's recorded in CLAUDE.md, along with the `__Host-` cookies, the COOP/CORP headers, the Admin 2FA rule and the AuditLog rule.

## What was built

### Part A: PDF hyphens
- **Cause:** Plus Jakarta Sans' contextual alternates (`calt`) swap a hyphen between digits for a dash glyph whose text mapping is U+2212. That's why "T25-2026-0001" copied as "T25−2026−0001".
- **Fix:** `calt` is turned off in every PDF.
- **Tests:** they now assert the exact extracted invoice number, the grouped IBAN and the dates, with no U+2212 anywhere.

### Part B: security hardening
The full detail is in [docs/SECURITY-REVIEW.md](../SECURITY-REVIEW.md), organised by OWASP Top 10 (2025) category.

1. **Access control**
   - A generated authorization matrix covers all **116 routed actions**: a new endpoint without a reviewed entry fails the tests.
   - Live checks for anonymous, Manager and Admin users.
   - Every POST without an antiforgery token gets 400.
   - Object-level checks: a record addressed under the wrong parent → 404; another Manager's increment: edit → 404, delete → 403.
2. **Misconfiguration**
   - `__Host-` cookies.
   - COOP and CORP headers; no Server or X-Powered-By header.
   - Request size, form and header limits.
   - `MigrateOnStartup`, demo data and `/dev` are Development-only.
   - `web.config` is written for later IIS use; it has no effect locally.
3. **Supply chain**
   - No vulnerable packages; ClosedXML and QuestPDF patched.
   - NuGet lock files with locked restore.
   - Dependabot.
   - CI workflow: locked restore, `-warnaserror` build, unit tests, vulnerable-package gate.
4. **Secrets and keys**
   - Data Protection keys persist to disk, DPAPI-encrypted (survives restarts; tested).
   - The git history scan is clean; nothing needed rotating.
5. **Injection**
   - XSS crawl: 6 payloads, 34 pages, as Admin and Manager. Everything is encoded; exports hold text cells only.
   - No GET changes state (checked with the rowversion counter and row counts).
   - Log CR/LF escaping.
6. **Business logic**
   - **Bug fixed:** simultaneous finalizes, and simultaneous invoice issues, deadlocked into a 500. The losing request now gets a polite refusal.
   - Rate limits: 120 POSTs and 30 exports per minute per user.
7. **Authentication**
   - TOTP two-factor:
     - QR code and manual key; 10 single-use recovery codes;
     - **required for Admins**; optional for Managers, but the Admin can require or reset it;
     - wrong codes count towards lockout.
   - Logout ends all of a user's sessions; session fixation tested.
   - `admin-reset` console command.
8. **Audit trail**
   - `AuditLog` table: 59 events, written in the same transaction as each change. No personal data or secrets in summaries (tested).
   - An append-only trigger.
   - `/admin/audit` page with filters and an Excel export.
   - Optional `audit-purge` command.
9. **Logging and health**
   - Serilog JSON rolling file (path only, never the query string).
   - Dashboard security alert.
   - Anonymous, rate-limited `/health`.
10. **Failures**
    - Correlation-id error page.
    - Friendly 503 when the database is down; startup survives a database outage.
    - A failure in the middle of finalize leaves nothing behind.

### Documentation
- `docs/SECURITY-REVIEW.md`: A01–A10 with checked / found / fixed / residual risk. Also the raw-SQL inventory, the over-posting review, the secret scan, the client library versions, and how to run ZAP later.
- `README.md`:
  - two-factor first sign-in;
  - emergency `admin-reset`;
  - audit log, file log, `/health`, Data Protection keys;
  - new environment variables;
  - CI scope;
  - licences.
- `CHANGELOG.md`: M1–M10.

## Migrations added
- `20261010135734_AddAuditLog` (with the append-only trigger)
- `20261010135752_AddTwoFactorRequirement`

## Tests: 571 passed, 0 failed, 0 skipped
- New: `SecurityHardeningTests` (23 integration tests) and `SecurityUnitTests` (10 unit tests).
- Existing tests pass unchanged: Admin test users are enrolled in 2FA, and the login helper answers the code step with a real TOTP code.

## Smoke tests
- Automated in the integration suite:
  - all 116 routes for each role;
  - the 2FA enrolment and sign-in round trip, recovery codes, lockout;
  - the audit page and its export;
  - `/health`;
  - 404, 500 and 503 pages;
  - double submits.
- Earlier this session the app ran on the dev database, and `/health` answered `Healthy`.

## Known issues and deviations
- No deviations from SPEC. §6 was clarified with your approval.
- **Residual risks** (see SECURITY-REVIEW.md):
  - a TOTP code can be reused within its 30-second window (lockout and rate limits bound this);
  - no password-breach check;
  - security alerts appear on the dashboard only (no email);
  - LibMan assets are not watched by Dependabot.
- **Deferred by you:**
  - Part C: deployment guide, scripts, migration bundle, backups and restore drill, IIS publish;
  - the ZAP scan;
  - the v1.0.0 tag.
- Docker Desktop may still be running from an earlier attempt; you can close it.

## Manual checks for you
**I didn't do the visual checks myself.** The browser check needed a throwaway database with a generated test Admin password, and the permission system blocked that step. Please check these at a phone width (about 375px) and on desktop, in both light and dark themes:
1. **Two-factor setup** (forced on your first Admin sign-in):
   - the QR code scans;
   - the manual key fits without horizontal scrolling;
   - Tab focus is visible.
2. **Recovery codes:** 10 codes in a readable grid, with nothing spilling off a phone screen. Save them somewhere safe.
3. **Security page** (user menu → Two-factor sign-in): the status is shown, "New recovery codes" works, and "Turn off" asks for confirmation.
4. **Managers → Edit:** the two-factor card ("Require" and "Reset") shows the current state.
5. **Audit log** (`/admin/audit`):
   - the filters stack on a phone;
   - the table turns into cards on narrow screens;
   - the Excel export downloads.
6. **Dashboard alert:** lock a test Manager with 5 wrong passwords. The Admin dashboard then shows a security alert that links to the audit log.
7. **Error page:** open `/does-not-exist`. The 404 card should be readable in both themes.

## Important for your next local sign-in
Your Admin account must now **set up two-factor sign-in** on first login. Have an authenticator app ready, and keep the 10 recovery codes.

If you're ever locked out:
1. Stop the app.
2. Run this on the machine (keep the quotes):
   ```bash
   dotnet run --project src/HR.Web -- admin-reset --email "ranjhani.saddam@gmail.com"
   ```
3. Sign in with the printed password, change it, and set up two-factor again.
