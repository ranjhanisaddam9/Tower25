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

Fonts and icons (Plus Jakarta Sans, Bootstrap Icons) are self-hosted under `src/HR.Web/wwwroot/lib` and committed.
To re-download them after editing `libman.json`, run `libman restore` from `src/HR.Web`.

## Run

```bash
dotnet run --project src/HR.Web --launch-profile https
```

Then open https://localhost:7016. The dev-only style guide is at https://localhost:7016/dev/styleguide (it returns 404 in every other environment).

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
