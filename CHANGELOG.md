# Changelog

All notable changes, one entry per milestone. Details are in [docs/milestones](docs/milestones).

## v1.1.0: monthly USD pay model (2026-10-17)
- **Pay records:** every salary is agreed in USD per month; everything is billed in USD and paid in PKR at the payroll's rate. A pay record holds three monthly USD figures, with Billed = Salary + Commission as a database check constraint. The pay currency is removed.
  - Company recommended: the Admin enters salary and commission (suggested $50); billed is derived.
  - Budget hire: the Admin enters the budget and the salary; commission is derived, and below 0 needs the "loses money" confirmation.
  - Owner: salary only.
- **Pay form:** the derived figure updates live and is read-only. A "per pay period" preview halves each figure and shows a PKR estimate at today's rate.
- **Manager increments:** the Manager enters the new monthly salary only. For company recommended, the commission is carried over. For budget hires, the budget is carried over and the record is flagged for the Admin's review. Manager pages show the USD salary and its PKR estimate, never commission, billed or budget.
- **Payroll:** SalaryUsd and CommissionUsd come from the monthly figures (half a month × f), and PKR pay = round0(SalaryUsd × rate). SPEC §9 golden table replaced.
- **Admin pay card:** salary, commission, billed and your earning, per month and per full period. The salaries overview, exports and the salary-changes report follow the new model.
- **Migration `MonthlyUsdPayModel`:** converts existing records, with PKR pay converted at the exchange rate in effect and flagged for review. Finalized payrolls and invoices are untouched; their old lines keep their legacy snapshot and still display correctly. Draft payrolls are recalculated with the new `recalculate-drafts` server command, which `migrate.ps1` runs.
- **Also in this release:**
  - the T25 export on the payroll page (name and billed USD, Admin only);
  - a security pin of System.Security.Cryptography.Pkcs to 10.0.13;
  - the IIS server deployment (see v1.0.0).

## v1.0.0: server deployment, backups and TOTP replay fix (2026-10-11)
- **Deployment:** Windows Server + IIS in-process hosting, internet-facing over HTTPS with Let's Encrypt (win-acme) and SQL Server on the same machine. The guide is `docs/DEPLOYMENT.md`.
- **Package:** `build-package.ps1` builds `HRPayroll-v1.0.0-win-x64.zip`. It contains:
  - the framework-dependent publish (profile `IIS-win-x64`);
  - `migrate.exe`;
  - the scripts `setup` (application pool, site, ACLs, least-privilege SQL login, firewall, first Admin), `migrate`, `update` (`app_offline.htm`, automatic rollback), `uninstall`, `admin-reset`, `restore` and `tls-hardening`.
- **Configuration:** `C:\HRPayroll\config\appsettings.Production.json`, outside the web root.
- **Backups:**
  - nightly `backup.ps1`: `CHECKSUM` plus `RESTORE VERIFYONLY`; 14 daily and 12 monthly kept;
  - an optional encrypted 7-Zip copy off the server;
  - the Admin dashboard warns when the last backup failed or is older than 48 hours.
- **Security:**
  - authenticator codes are single-use (migration `AddTotpReplayGuard`);
  - a new `seed-admin` server command.
- **Docs:** `SECURITY-REVIEW.md` gains a "Server deployment" section and a go-live checklist.

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
