# Security review (M10)

Scope: HR Payroll v1, ASP.NET Core MVC on .NET 10, EF Core 10, SQL Server Express. The review follows the **OWASP Top 10 (2025)**. Target posture: an internet-facing Windows server over HTTPS. The owner has deferred deployment, so the app currently runs locally only.

For each category: what was checked, what was found, what was fixed, and the risk that remains. Every check below is backed by an automated test unless it says otherwise.

Test suite: 571 tests (353 unit, 218 integration), all passing. The M10-specific tests are in `tests/HR.Tests/Integration/SecurityHardeningTests.cs` and `tests/HR.Tests/Unit/SecurityUnitTests.cs`.

---

## A01 Broken access control

**Checked**
- Every one of the 116 routed actions, as anonymous, Manager and Admin. The test is generated from the app's own endpoint data source and compared with a reviewed matrix. Adding an endpoint without a matrix entry fails the tests.
- Each endpoint's authorization metadata against the matrix.
- Object-level access:
  - payroll lines, adjustments and rate records addressed under the wrong parent;
  - Managers acting on each other's increments;
  - invoices, owner income, audit log and settings (Admin only).
- Over-posting: every POST action binds a dedicated form, view-model or input type (28 bound models). No action binds an EF entity. Admin-only fields are absent from Manager view models.
- "No GET changes state": an XSS crawl of 34 pages and 7 exports compares the database rowversion counter (`@@DBTS`) and every business table's row count before and after. They are unchanged; only AuditLog rows for exports are added.

**Found**
- No authorization gaps.
- A Manager trying to edit another Manager's increment gets 404, by design: the response doesn't reveal that the record exists. Deleting it gets 403.

**Fixed**
- Nothing was broken. The matrix test now guards against regressions.

**Residual risk**
- Low. Authorization depends on the claims in the cookie, which are refreshed every minute by security-stamp validation. A role change can therefore take up to a minute to apply to an open session. Deactivation and resets rotate the security stamp, which ends sessions at the next validation.

## A02 Security misconfiguration

**Checked**
- Cookies, security headers, error pages, request limits and Production-only behaviour.

**Fixed (M10)**
- **Cookies** are renamed with the `__Host-` prefix (Secure, Path=/, no Domain): auth, antiforgery, TempData and the two-factor step cookie. All are HttpOnly and SameSite=Lax.
- **New headers:** `Cross-Origin-Opener-Policy: same-origin` and `Cross-Origin-Resource-Policy: same-origin`. These join the existing CSP, nosniff, `X-Frame-Options: DENY`, Referrer-Policy and Permissions-Policy. `Server` and `X-Powered-By` are removed.
- **Header coverage is tested** on pages, static files, error pages, downloads and `/health`.
- **Request limits:**
  - 1 MB request body (Kestrel and IIS)
  - form limits: 2,048 values; keys up to 512 characters; values up to 64 KB
  - model binding: up to 1,024 items per collection; at most 100 validation errors
  - headers: 32 KB in total; request line: 8 KB.
- **Production behaviour:**
  - no developer exception page;
  - `/dev/styleguide` returns 404;
  - demo data runs only in Development;
  - `Database:MigrateOnStartup` is ignored outside Development, with a warning logged. Production schema changes go through a migration bundle.
- **`web.config`** (for IIS, when deployment is resumed): in-process hosting, `removeServerHeader`, X-Powered-By removed, verbs limited to GET/HEAD/POST, 1 MB limit, appsettings files hidden.

**Residual risk**
- TLS settings, the certificate and the firewall are server configuration. They belong to the deferred deployment guide.

## A03 Software supply chain failures

**Checked**
- `dotnet list package --vulnerable --include-transitive`: **no vulnerable packages**.
- `--outdated`: ClosedXML 0.105.0 → 0.105.1 and QuestPDF 2026.8.0 → 2026.9.1 were updated; a renamed QuestPDF API was adapted.
- Test-only tools (xunit runner 4, Test SDK 18, coverlet 10) have major updates available. These are left as they are (no security issue) and Dependabot will raise them.

**Fixed**
- **NuGet lock files** (`packages.lock.json`) for every project; restoring in locked mode is verified.
- **NuGet audit** is on for all packages, including transitive ones.
- **`.github/dependabot.yml`**: weekly NuGet and GitHub Actions updates.
- **`.github/workflows/ci.yml`**:
  - locked restore
  - Release build with warnings as errors
  - unit tests
  - a vulnerable-package gate.

  These steps were run locally and pass. Integration tests need SQL Server and run locally (see README).

**Client-side assets** (self-hosted, no CDN):

| Asset | Version |
|---|---|
| Bootstrap | 5.3.3 |
| Bootstrap Icons | 1.13.1 (LibMan) |
| Plus Jakarta Sans | @fontsource 5.2.8 (LibMan) |
| jQuery | 3.7.1 |
| jQuery Validation | 1.21.0 |
| jQuery Validation Unobtrusive | 4.0.0 |

The PDF font is the Plus Jakarta Sans TTF from the official repository (SIL OFL), embedded in the app.

**Residual risk**
- LibMan assets are not covered by Dependabot. Review them by hand when updating.

## A04 Cryptographic failures

**Checked**
- Secrets in the repository and the full git history, password storage, the Data Protection key ring.

**Found**
- **Git history:** a scripted search of all commits found no secrets. It looked for:
  - password assignments
  - connection strings carrying credentials
  - private keys
  - GitHub, AWS and Azure keys
  - JWTs
  - generic API tokens
  - the QA throwaway passwords.

  The only literal passwords are two test-only values for the disposable test database (`Correct-Horse-42`, `Seeded-Admin-Pass-1`). Committed connection strings use Windows authentication. Nothing needed rotating.
- **gitleaks** was not available, so the search was scripted. To run gitleaks later: `gitleaks detect --source . --log-opts="--all"`.

**Fixed**
- **Data Protection keys** persist to `DataProtection:KeysPath` (default `%LOCALAPPDATA%\HRPayroll\DataProtection-Keys`) and are **DPAPI-encrypted at machine scope**. Tested: a restarted app accepts an existing session, and the key files hold only `encryptedSecret`.

**Unchanged and good**
- Passwords are hashed by ASP.NET Identity (PBKDF2).
- The seeded Admin password is accepted only from user secrets or an environment variable.
- Temporary passwords are shown once and never logged.

## A05 Injection

**Checked**
- **Raw SQL:** apart from migrations, raw SQL appears only in:
  - `NEXT VALUE FOR [PersonCodeSequence]`
  - `DISABLE/ENABLE TRIGGER` in the server-only `audit-purge` command.

  All are constant strings with no user input. Everything else is EF LINQ or `ExecuteSqlInterpolated` (parameterised).
- **Stored XSS crawl:** these 6 payloads were stored:
  - `<script>alert(1)</script>`
  - `"><img src=x onerror=alert(1)>`
  - `javascript:alert(1)`
  - `{{7*7}}`
  - `</text><svg onload=alert(1)>`
  - `' onmouseover='alert(1)`

  They were placed in names, designations, notes, bank name, every Settings text field, adjustment notes, extra-days notes, rate and exchange-rate notes, and a Manager's name. The crawl then visited 34 pages as Admin and Manager, including SVG charts and tables. It confirmed:
  - no live `<script>` tags, no event-handler attributes, no `javascript:` URLs;
  - the encoded form is present, so the payloads really were rendered;
  - exports hold text cells only, never formulas;
  - PDFs render the payloads as plain text.
- **Spreadsheet injection:** covered since M9 (quote prefix, text cells).
- **`site.js`:** only `textContent` and `setAttribute`; no `innerHTML`, `insertAdjacentHTML`, `eval` or `new Function`.
- **Log injection:** user text reaches logs only as structured parameters. The JSON file log escapes CR/LF, so a value can't forge a log line (unit test).

**Residual risk**
- Low. All views rely on Razor's encoding; `Html.Raw` is never used on data.

## A06 Insecure design (business logic)

**Checked**
- **Money and value limits:** every input has range checks from earlier milestones (amounts, rates of 1–10,000 with 4 decimals, days in 0.5 steps, received amounts up to 10,000,000 with 2 decimals). All arithmetic is `decimal`.
- **Concurrency:** RowVersion protects people, rate records, absences, exchange rates, payroll runs, adjustments, invoices and settings; users have Identity's concurrency stamp.
- **Double submits** of finalize, issue invoice and mark paid.

**Found**
- **Bug:** two simultaneous "Finalize" clicks deadlocked inside the serializable transaction. SQL Server rolled one back as the deadlock victim (error 1205) and the user saw a 500.

**Fixed**
- The losing request is now refused politely ("someone changed this at the same time").
- "Issue invoice" is handled the same way; a unique index also enforces one invoice per run.
- Mark paid / unpaid were already safe through RowVersion.
- Tests fire each action twice in parallel: one finalize, one invoice, one payment.
- **Rate limits:** 120 POSTs per minute per user (or IP) and 30 exports or PDFs per minute per user (configurable). Tested. Rejections are audited at most once per minute per user, so a flood can't flood the database.

## A07 Authentication failures

**Fixed (M10)**
- **TOTP two-factor sign-in** (ASP.NET Identity authenticator):
  - **Enrolment:** a server-rendered QR code as a PNG data URI (allowed by the CSP), plus the manual key. 10 recovery codes are shown once, in a `no-store` response that is never stored or sent through TempData.
  - **Required for Admins:** an Admin without two-factor reaches only the setup page until they enrol.
  - **Managers:** optional, but the Admin can require it per Manager (same gate) and can reset a Manager's two-factor. A reset also ends their sessions.
  - **Lockout:** wrong authenticator codes and wrong recovery codes count towards the account lockout (Identity doesn't count recovery codes by default; this app does).
  - **No "remember this device".** The partial-login cookie lasts 5 minutes.
- **Logout** rotates the security stamp, so every other session of that user ends at its next validation (tested).
- **Session fixation:** a new auth cookie is issued at login; a planted cookie value is never adopted (tested).
- **Emergency Admin recovery:** `HR.Web.exe admin-reset --email <email>`, run on the server console only:
  - prints a random password once, clears two-factor and lockout, sets MustChangePassword, ends sessions;
  - audited;
  - no HTTP route (tested).
- **Unchanged and good:**
  - one generic login error;
  - lockout after 5 failures for 15 minutes;
  - login limited to 10 per minute per IP;
  - passwords of at least 10 characters with upper case, lower case and digits;
  - timing-equalised unknown-email path.

**Residual risk**
- TOTP codes aren't single-use within their 30-second window: Identity doesn't track used time steps. Lockout and rate limits bound guessing. Possible future improvement: store the last accepted time step.
- No password-breach (HIBP) check; it would need an outbound call.

## A08 Software or data integrity failures

**Fixed (M10)**
- **`AuditLog` table** with columns At (UTC), ActorUserId, ActorName, ActorIp, EventId, EventName, EntityType, EntityId and Summary:
  - All 59 security and business events (1000–17xx) write a row, in the **same transaction** as the change when there is one. The context queues the row and saves it with the change.
  - Events that change no data (sign-in failures, exports, refusals) use their own short transaction.
- **Summaries are non-sensitive:** field names, ids, periods and non-personal old → new values. Never passwords, tokens, CNIC, IBAN, phone numbers, notes or reasons. Tested by searching every row for seeded test values.
- **Append-only:** the `TR_AuditLog_AppendOnly` trigger refuses UPDATE and DELETE (error 51030; tested).
- **Admin page `/admin/audit`:** filters by date range, user, event and entity; paging; Excel export. Managers get 403.
- **Retention:** keep forever by default (`Audit:RetentionDays` = 0). An administrator can run `HR.Web.exe audit-purge` with a SQL login that may alter the table. The app's own login can't, by design.
- **Finalized payroll data stays immutable** (database triggers since M7). The append-only rule now covers the audit trail too.

## A09 Logging and alerting failures

**Fixed (M10)**
- **Serilog file log:** rolling daily JSON files, 30 kept, at the path in `Logging:File:Path` (empty means no file).
  - JSON escapes CR/LF.
  - Request logs carry the path only, never the query string.
  - No personal data: events log ids, never CNIC, IBAN, phone numbers or notes (tested since M3).
- **Admin dashboard alert** for the last 24 hours, linking to the audit log. It fires on any of:
  - ≥ 10 failed sign-ins
  - any lockout
  - ≥ 5 rejected two-factor codes
  - any payroll reopen.
- **`/health`:**
  - anonymous, rate-limited, `no-store`;
  - the body is only `Healthy` or `Unhealthy` (database connectivity), with 503 when unhealthy;
  - no details.

**Residual risk**
- The alert appears on the dashboard only; there is no email or SMS push. Monitoring `/health` from outside is part of deployment.

## A10 Mishandling of exceptional conditions

**Fixed (M10)**
- **Global error page:** generic text plus a **correlation id** that is also logged; no exception text or stack trace (tested).
- **Database unavailable:** a friendly **503** page and `/health` reports Unhealthy (tested with an unreachable server). Startup tolerates a database outage instead of crashing.
- **Transactions:** a failure forced in the middle of finalize leaves nothing behind (tested). The run stays a draft, with no invoice, no counter step and no audit rows.
- **Fail closed:**
  - the fallback authorization policy requires a signed-in user;
  - the enrolment gate treats a missing claim as "not enrolled";
  - cookies that fail validation are rejected.

## Dynamic scan (OWASP ZAP)

Not run: the owner chose not to use Docker for now. To run it against a local Production-mode instance:

```bash
docker run --rm -t ghcr.io/zaproxy/zaproxy:stable zap-baseline.py -t https://host.docker.internal:7016 -r zap-baseline.html
```

For an authenticated scan:
1. Sign in as a Manager in a browser and copy the `__Host-hr.auth` cookie value.
2. Add it with `-z "-config replacer.full_list(0).matchtype=REQ_HEADER -config replacer.full_list(0).matchstr=Cookie -config replacer.full_list(0).replacement=__Host-hr.auth=<value>"`.
3. Repeat with an Admin cookie.
4. Record and justify every alert in this file.
