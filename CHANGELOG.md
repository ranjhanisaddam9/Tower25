# Changelog

All notable changes, one entry per milestone. Details are in [docs/milestones](docs/milestones).

## v1.0.0: release package, backups and TOTP replay fix (2026-10-11)
- **Release package:** a portable `HRPayroll-v1.0.0-win-x64.zip` built by `build-package.ps1`. It contains:
  - the self-contained app, running as the Windows Service `HRPayroll` or as a console app, on `https://localhost:7443` only;
  - `migrate.exe`;
  - the scripts `setup`, `update` (with automatic rollback), `uninstall`, `admin-reset` and `restore`;
  - `INSTALL.md` and `restore.md`.
- **Backups:**
  - nightly `backup.ps1`: `CHECKSUM` plus `RESTORE VERIFYONLY`; 14 daily and 12 monthly kept;
  - an optional encrypted 7-Zip copy to a second location;
  - the Admin dashboard warns when the last backup failed or is older than 48 hours.
- **Security:**
  - authenticator codes are single-use (migration `AddTotpReplayGuard`);
  - a new `seed-admin` server command;
  - the app's SQL login is least-privilege.
- **Docs:** `docs/DEPLOYMENT.md`, plus a "before any network or internet exposure" checklist in `SECURITY-REVIEW.md`.

## M10: security audit and hardening (2026-10-10)
- **Two-factor sign-in (TOTP):** QR code and recovery codes; required for Admins; Admins can require or reset it for Managers.
- **Sessions:** logout ends all of a user's sessions.
- **Audit log:** the append-only `AuditLog` table records 59 security and business events in the same transaction as each change. Admin page `/admin/audit` with filters and an Excel export.
- **Cookies and headers:** cookies use the `__Host-` prefix; COOP and CORP headers added; Server and X-Powered-By headers removed.
- **Limits:** request size limits; per-user rate limits on POSTs and exports.
- **Keys:** Data Protection keys persist to disk, DPAPI-encrypted.
- **Monitoring:** Serilog JSON file log; security alerts on the Admin dashboard; anonymous `/health`.
- **Errors:** error pages carry a correlation id; a database outage shows a friendly 503.
- **Fixed:**
  - two simultaneous payroll finalizes (or invoice issues) could deadlock into a 500;
  - PDF hyphens copied as U+2212 minus signs.
- **Server commands:** `admin-reset` and `audit-purge`.
- **Supply chain:** NuGet lock files, Dependabot and a CI workflow.
- **Tests:** an authorization-matrix test covering all 116 routes, plus an XSS crawl.
- **Docs:** `docs/SECURITY-REVIEW.md` (OWASP Top 10, 2025).
- **Deferred:** deployment (IIS, backups, the deployment guide) and the ZAP scan.

## M9: reports and exports (2026-10-10)
- Reports, with Excel exports (ClosedXML) and PDF payslips and invoices (QuestPDF), protected against spreadsheet formula injection.

## M8: settings, company invoice and owner income (2026-10-10)
- Company settings, the client invoice issued from a finalized payroll, and owner income tracking.

## M7: payroll engine (2026-10-10)
- 15-day pay periods, payroll calculation with the SPEC §9 golden cases, adjustments, finalize and reopen, immutable snapshots.

## M6: absences and paid leave (2026-10-10)
- Absence records and paid-leave allocation.

## M5: pay records, billing and increments (2026-10-09)
- Pay and billing rate records with audited corrections, and increments.

## M4: employment history and exchange rates (2026-10-09)
- Employment history and USD/PKR exchange rates with audited corrections.

## M3: people (2026-10-09)
- Employees and internees, with Admin-only personal data kept away from Managers.

## M2: authentication, roles and Manager management (2026-10-09)
- ASP.NET Identity sign-in, Admin and Manager roles, lockout, login rate limit, Manager accounts.

## M1: foundation (2026-10-08)
- Solution layout, data layer, the "Aurora Glass" design system and the security baseline.
