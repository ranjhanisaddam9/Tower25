# HR Payroll

Payroll and billing for a staffing arrangement: people are paid in PKR every 15 days and the client company is billed in USD.
Business rules live in [docs/SPEC.md](docs/SPEC.md); working conventions for contributors (and Claude Code) are in [CLAUDE.md](CLAUDE.md).
Milestone reports are in [docs/milestones](docs/milestones).

ASP.NET Core MVC on .NET 10, EF Core 10 with SQL Server, and the "Aurora Glass" UI on Bootstrap 5.3.

## Prerequisites

- **.NET SDK 10.0.401** or a later 10.0 feature band (pinned in `global.json`).
- **SQL Server Express** on the local instance `.\SQLEXPRESS`, with Windows authentication.
- **EF Core CLI tool**, version 10:
  ```bash
  dotnet tool install --global dotnet-ef --version "10.*"
  ```
- **LibMan CLI** (only needed if you change `libman.json`):
  ```bash
  dotnet tool install --global Microsoft.Web.LibraryManager.Cli
  ```

## Solution layout

```
HRPayroll.sln
src/HR.Domain          entities, enums, value objects, pure calculators (no EF / ASP.NET)
src/HR.Infrastructure  AppDbContext, entity configurations, migrations, services
src/HR.Web             controllers, view models, views, wwwroot (Aurora Glass)
tests/HR.Tests         xUnit: Unit/ (domain) and Integration/ (WebApplicationFactory + SQL Express)
```

Dependencies point inward only: Web → Infrastructure → Domain.

## Setup

1. Clone the repository.
2. Restore and build:
   ```bash
   dotnet build HRPayroll.sln
   ```
3. Create or update the dev database `HRPayroll`. The app also does this on startup in Development (`Database:MigrateOnStartup`).
   ```bash
   dotnet ef database update --project src/HR.Infrastructure --startup-project src/HR.Web
   ```

The dev connection string is in `src/HR.Web/appsettings.Development.json`:
`Server=.\SQLEXPRESS;Database=HRPayroll;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true`

### Machine-specific settings

If your SQL Server name (or anything else) differs on your machine, don't edit the committed file.
Create `src/HR.Web/appsettings.Development.local.json` instead. It is optional, git-ignored and never published, and it loads right after `appsettings.Development.json`, so its values win. User secrets and environment variables still override it.

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=MY-PC\\SQLEXPRESS;Database=HRPayroll;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
  }
}
```

Never put passwords in this file: the Admin seeder refuses a seed password found in any `appsettings*.json`.

### Demo people (Development only)

To fill an empty dev database with about 25 realistic but clearly fake people (CNICs start with `00000`, IBANs use the bank code `TEST`, emails end in `@demo.example`), set the flag for one run. Never commit it as `true`:

```bash
dotnet user-secrets set "DemoData:Seed" "true" --project src/HR.Web
```

Seeding runs only in the Development environment and is idempotent: it adds only the demo people that are missing. Turn it off again afterwards:

```bash
dotnet user-secrets remove "DemoData:Seed" --project src/HR.Web
```

### First Admin account

At startup the app creates the Admin and Manager roles. If no user has the Admin role yet, it also creates one from `Seed:Admin:Email`, `Seed:Admin:FullName` and `Seed:Admin:Password`, with a forced password change at first sign-in.
Seeding runs only once: once an Admin exists, nothing happens. If the password is missing, the app logs a warning and skips seeding.

On a dev machine, keep all three in user secrets (the password must never be in a committed file):

```bash
dotnet user-secrets set "Seed:Admin:Email" "you@example.com" --project src/HR.Web
```

```bash
dotnet user-secrets set "Seed:Admin:FullName" "Administrator" --project src/HR.Web
```

```bash
dotnet user-secrets set "Seed:Admin:Password" "<a temporary password: 10+ chars, upper, lower, digit>" --project src/HR.Web
```

After the first successful sign-in and password change, you can remove the seed password:

```bash
dotnet user-secrets remove "Seed:Admin:Password" --project src/HR.Web
```

### Two-factor sign-in

Admins must use two-factor sign-in (TOTP).
1. On the first sign-in, after the password change, the app shows only the setup page. Scan the QR code with an authenticator app (Microsoft Authenticator, Google Authenticator, …) or type the key.
2. Enter a code to confirm.
3. Store the 10 recovery codes somewhere safe. They are shown once; each works once instead of an authenticator code.

Managers can turn two-factor on from the user menu. The Admin can require it for a Manager, or reset it, on the Manager's edit page.

### Emergency Admin recovery

If an Admin loses both the authenticator and the recovery codes, or is locked out, run this **on the machine that hosts the app** (there is no web route for it). Keep the quotes around the email:

```bash
dotnet run --project src/HR.Web -- admin-reset --email "admin@example.com"
```

The command:
- prints a new temporary password once;
- clears two-factor and the lockout;
- forces a password change at the next sign-in, after which two-factor must be set up again;
- ends that user's sessions;
- writes an audit log entry.

Stop the running app first, because the build can't replace files it has locked. On an installed PC, use `C:\HRPayroll\tools\admin-reset.ps1 -Email "…"` instead (see [INSTALL.md](deploy/INSTALL.md)).

Fonts and icons (Plus Jakarta Sans, Bootstrap Icons) are self-hosted under `src/HR.Web/wwwroot/lib` and committed.
To re-download them after editing `libman.json`, run `libman restore` from `src/HR.Web`.

## Run

```bash
dotnet run --project src/HR.Web --launch-profile https
```

Then open https://localhost:7016 and sign in. Every page except sign-in, the error pages and static files requires a signed-in user.
Admins manage Manager accounts at `/admin/managers`. The dev-only style guide is at https://localhost:7016/dev/styleguide (it returns 404 in every other environment).

The site is HTTPS-only. If the browser warns about the certificate, trust the ASP.NET Core development certificate once:

```bash
dotnet dev-certs https --trust
```

## Test

```bash
dotnet test HRPayroll.sln
```

- **Unit tests** need nothing external.
- **Integration tests** use `HRPayroll_Test` on `.\SQLEXPRESS`. The shared fixture creates and migrates it once per run, clears its data before each test, and drops it at the end. They never touch `HRPayroll`.
- To point the tests at a different server, set `HRPAYROLL_TEST_CONNECTION` (the database name must stay `HRPayroll_Test`).
- **CI** (`.github/workflows/ci.yml`) runs a locked restore, a warnings-as-errors Release build, the unit tests and a vulnerable-package check. It doesn't run the integration tests (no SQL Server there), so run `dotnet test` locally before pushing.

## Security, audit and logs

- The security review is in [docs/SECURITY-REVIEW.md](docs/SECURITY-REVIEW.md).
- **Audit log:** every sign-in event, data change, payroll finalize or reopen, and export is written to the append-only `AuditLog` table. Admins can browse, filter and export it at `/admin/audit`.
- **File log:** set `Logging:File:Path` (for example `C:\HRPayroll\logs\hr-.json`) to get rolling daily JSON log files, 30 kept. It's empty by default, which means console logging only.
- **Health:** `/health` answers `Healthy` or `Unhealthy` (database connectivity) and needs no sign-in.
- **Data Protection keys** (they keep sign-ins valid across restarts) are stored in `DataProtection:KeysPath`, by default `%LOCALAPPDATA%\HRPayroll\DataProtection-Keys`, and are encrypted with DPAPI.

## Migrations

Migrations live in `src/HR.Infrastructure/Data/Migrations`. Give every migration a meaningful name, and never edit one that is already committed: add a new one.

```bash
# add a migration
dotnet ef migrations add <MeaningfulName> --project src/HR.Infrastructure --startup-project src/HR.Web --output-dir Data/Migrations

# apply to the dev database
dotnet ef database update --project src/HR.Infrastructure --startup-project src/HR.Web
```

A model test fails the build if the model has changes that no migration captures.

## Production configuration

Never commit secrets. In production, supply settings through environment variables (or user secrets on a dev box):

| Setting | Environment variable |
|---|---|
| Connection string | `ConnectionStrings__DefaultConnection` |
| Auto-migrate on startup (default `false`) | `Database__MigrateOnStartup` |
| Allowed host names | `AllowedHosts` |
| First Admin (only used while no Admin exists) | `Seed__Admin__Email`, `Seed__Admin__FullName`, `Seed__Admin__Password` |
| Data Protection key folder | `DataProtection__KeysPath` |
| JSON log file path | `Logging__File__Path` |
| Audit retention in days (0 = keep forever) | `Audit__RetentionDays` |

Behind a reverse proxy, the rate limits (10 logins per minute per IP; 120 POSTs and 30 exports per minute per user) need forwarded headers configured so they see the real client IP. On an installed PC, `setup.ps1` writes the machine settings to `C:\HRPayroll\config\appsettings.Production.json` (see "Release package" below).

## Release package (install on a PC)

HR Payroll v1 is installed on one Windows PC from a portable zip. It is reachable only on that PC, at `https://localhost:7443`.

Build the package (this needs the pinned .NET SDK and `dotnet-ef`):

```bash
powershell -ExecutionPolicy Bypass -File build-package.ps1
```

This creates `dist\HRPayroll-v1.0.0-win-x64.zip` (git-ignored). It contains:
- the self-contained app;
- `migrate.exe`, the EF migration bundle;
- the scripts `setup`, `update`, `uninstall`, `admin-reset`, `backup`, `register-backup-task` and `restore` (all `.ps1`);
- `INSTALL.md` and `restore.md`;
- the licences.

The target PC needs Windows 10 or 11 and SQL Server Express, but no .NET. How to install, update, back up, restore and uninstall is in [deploy/INSTALL.md](deploy/INSTALL.md). See also [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

> `dotnet run` (Development) is for development only. Never use it with real data.

## Exports and third-party licences

- **Excel** files are written with [ClosedXML](https://github.com/ClosedXML/ClosedXML) (MIT).
- **PDF** files are written with [QuestPDF](https://www.questpdf.com) under its **Community licence**, set in code (`QuestPDF.Settings.License = LicenseType.Community`). The Community licence is free for organisations with **less than USD 1M annual gross revenue**. Above that, a paid QuestPDF licence is required; check the current terms on questpdf.com before relying on this.
- The PDF font is **Plus Jakarta Sans** (static TTFs from the official repository, github.com/tokotype/PlusJakartaSans), embedded from `src/HR.Infrastructure/Fonts` under the SIL Open Font License 1.1 (`Fonts/OFL.txt`).
- **Two-factor QR codes** are drawn with [QRCoder](https://github.com/codebude/QRCoder) (MIT).
- **Logging** uses [Serilog](https://serilog.net) (Apache 2.0).
- **Web assets:**
  - Bootstrap 5.3.3 (MIT)
  - Bootstrap Icons 1.13.1 (MIT)
  - Plus Jakarta Sans via @fontsource 5.2.8 (OFL 1.1)
  - jQuery 3.7.1 and jQuery Validation (MIT).
