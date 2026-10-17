# Deploying HR Payroll (Windows Server + IIS)

This guide puts HR Payroll on your own Windows server:
- **IIS** with in-process hosting, reachable from the internet at your own domain over HTTPS;
- a free **Let's Encrypt** certificate from win-acme, renewed automatically;
- **SQL Server** on the same server. The default instance is written `.`; a named instance is written `.\NAME`.

The scripts referred to here are in the release package (`HRPayroll-v1.1.0-win-x64.zip`), which `build-package.ps1` builds. After setup they are also in `C:\HRPayroll\tools`. Run every script in **PowerShell opened as Administrator**.

> `dotnet run` (Development) is for development only. Never use it with real data.

## 1. Server prerequisites

| Need | How |
|---|---|
| **Windows Server 2019 or later** (or Windows 10/11 Pro), 64-bit | Server 2022 adds TLS 1.3. |
| **IIS** | Server Manager → Add Roles and Features → Web Server (IIS). Optional: *Application Initialization*, which lets the app start before the first visitor. Or run `Install-WindowsFeature Web-Server, Web-AppInit -IncludeManagementTools`. |
| **ASP.NET Core 10 Hosting Bundle** | From https://dotnet.microsoft.com/download/dotnet/10.0 (*Hosting Bundle*, under ASP.NET Core Runtime). Install it **after** IIS, then run `iisreset`. It installs the .NET runtime (64-bit and 32-bit) and the ASP.NET Core Module. A separately downloaded x86 runtime is **not** enough: the app is 64-bit and needs `C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App\10.x`. |
| **SQL Server** (Express or higher) on this server | Your Windows login must be a SQL Server **sysadmin** for setup, updates and migrations. The app itself gets far fewer rights (see step 5). |
| **BitLocker** on the disks with data and backups | Turns encryption on for the system disk and for the drive that holds `C:\HRPayroll\backups` and the SQL Server data files. Keep the recovery key off the server. |
| **7-Zip** (recommended) | https://www.7-zip.org. Used for the encrypted backup copy kept off the server. |

## 2. Network

1. **DNS:** create an A record, for example `hr.example.com`, pointing at the server's public IP. Let's Encrypt needs it.
2. **Firewall:**
   - inbound TCP **443** (HTTPS) and **80** (HTTP: it only redirects to HTTPS, and Let's Encrypt uses it to validate). Setup creates these rules (group *HR Payroll*).
   - Nothing else for HR Payroll.
   - Keep RDP and other management ports closed to the internet, or limited to your office IP.
3. **SQL Server must not be reachable from the internet.** HR Payroll connects on the same machine.
   - If nothing else uses SQL Server over the network: SQL Server Configuration Manager → Protocols → disable **TCP/IP**, then restart SQL Server.
   - If other apps do use it: allow its port (1433, or the instance's dynamic port) **only** from those machines' IPs, and block it everywhere else.
   - Setup warns if SQL Server is listening on the network.
4. **TLS 1.2+ only:** run `tls-hardening.ps1` once and reboot. It turns off SSL 3.0 and TLS 1.0/1.1 server-wide, so first check that nothing else on the server needs them. Afterwards test the site at https://www.ssllabs.com/ssltest/ and aim for grade A.

## 3. Install

1. Copy `HRPayroll-v1.1.0-win-x64.zip` to the server and unzip it, for example to `C:\Temp\HRPayroll-v1.1.0-win-x64`.
2. Run:
   ```powershell
   cd C:\Temp\HRPayroll-v1.1.0-win-x64
   powershell -ExecutionPolicy Bypass -File .\setup.ps1 -HostName hr.example.com
   ```
   Options:
   - `-SqlInstance .\SQLEXPRESS` for a named instance (the default is `.`);
   - `-DatabaseName`;
   - `-HttpsPort` and `-HttpPort` (default 443 and 80);
   - `-NoFirewall`.

What setup does (it's safe to run again: it repairs and never duplicates):

| Step | Result |
|---|---|
| Checks | Windows, IIS, ASP.NET Core Module, .NET 10 runtime, SQL Server sysadmin; warns if SQL Server listens on the network. |
| Files | `C:\HRPayroll\app` (the published site, also the IIS physical path), `tools`, `config`, `keys`, `logs`, `backups`. |
| Configuration | `C:\HRPayroll\config\appsettings.Production.json`, **outside the web root**: connection string (Windows authentication, no password), `AllowedHosts`, Data Protection key folder, log path, backup status file. |
| Database | `migrate.ps1` applies the migrations with `migrate.exe`. The app never migrates on startup. |
| IIS | Application pool **HRPayroll**: *No Managed Code*, `ApplicationPoolIdentity`, always running, never idled out or recycled on a timer. Site **HRPayroll** with an `https` binding (SNI) for the host name, plus `http` for redirects and validation. |
| Certificate | A **temporary** self-signed certificate is bound until step 4. |
| Permissions | `C:\HRPayroll` drops inherited permissions. `IIS APPPOOL\HRPayroll` can read the app and config, and write only to `keys` and `logs`. The SQL Server service can write `backups`. |
| SQL login | `IIS APPPOOL\HRPayroll`: `db_datareader`, `db_datawriter`, `EXECUTE` and the person-code sequence. No DDL, no backups, no server rights. |
| Firewall | Inbound 443 and 80. |
| First Admin | Asks for an email, full name and password. The password is passed once through the process environment to `HR.Web.exe seed-admin`; it is never written to disk and never stored as a machine variable. |
| Backups | Offers to register the nightly backup task (step 7). |
| Start | Waits until `/health` answers `Healthy`. |

## 4. Let's Encrypt certificate (win-acme)

1. Download win-acme (`win-acme.v2.x.x.x64.pluggable.zip`) from https://www.win-acme.com and unzip it to `C:\Tools\win-acme`.
2. As Administrator, run:
   ```powershell
   C:\Tools\win-acme\wacs.exe --source iis --host hr.example.com --siteid (Get-IISSite HRPayroll).Id --installation iis --emailaddress you@example.com --accepttos
   ```
   - win-acme proves you own the domain over port 80 (HTTP-01), installs the certificate on the site's https binding, and creates a scheduled task that renews it automatically, about 30 days before it expires.
   - If `Get-IISSite` isn't available, use the site ID shown by `%windir%\System32\inetsrv\appcmd list site`.
3. Check: https://hr.example.com should show a valid padlock. Then remove the temporary certificate from **Certificates (Local Computer) → Personal**; its friendly name is *HR Payroll temporary certificate*.

Renewal problems are logged by win-acme in `%ProgramData%\win-acme`. Test a renewal with `wacs.exe --renew --force`.

## 5. Database security
- The app connects as `IIS APPPOOL\HRPayroll` with Windows authentication: there is no SQL password anywhere.
- Its login can read and write data and run procedures. It can't change the schema, back up, disable the audit-log trigger, or touch other databases.
- **Migrations run separately**, with an administrator's login:
  ```powershell
  powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\migrate.ps1
  ```
  setup.ps1 and update.ps1 call it for you.
- The audit log (`AuditLog`) is append-only: a database trigger refuses updates and deletes. `HR.Web.exe audit-purge` applies a retention period (`Audit:RetentionDays`; the default 0 keeps everything). It must run with an administrator's login.

## 6. First sign-in
1. Open `https://hr.example.com`.
2. Sign in with the Admin from setup, then choose a new password.
3. Set up **two-factor sign-in** (required for Admins):
   - scan the QR code with an authenticator app;
   - enter the 6-digit code;
   - keep the **10 recovery codes** somewhere safe.
4. Complete **Settings** (company, bank, invoice details), then add Managers under **Managers**. You can require two-factor sign-in for each Manager.

## 7. Backups (no SQL Server Agent needed)

| | |
|---|---|
| When | Every night at 23:00, as SYSTEM, by the scheduled task *HRPayroll Nightly Backup* (`register-backup-task.ps1`). If the server was off at 23:00, the backup runs as soon as it's back on. |
| What | `BACKUP DATABASE … WITH CHECKSUM`, then `RESTORE VERIFYONLY`. |
| Kept | 14 daily and 12 monthly copies in `C:\HRPayroll\backups`. |
| Off-server copy | An encrypted 7-Zip copy (AES-256, encrypted file names) to a second location: another disk, a network share, or a OneDrive or Google Drive folder. Set it up once: `backup.ps1 -SecondCopyPath "D:\Offsite\HRPayroll" -SetPassword`. The password is stored DPAPI-encrypted for this server (Administrators and SYSTEM only), never in a script. **Write it down off the server**: without it the copies can't be opened. |
| Monitoring | `C:\HRPayroll\backups\backup.log`, plus `status.json`. The Admin dashboard warns when the last backup failed or the last good one is older than 48 hours. |
| Rights | SYSTEM gets `db_backupoperator` on the database, plus `CREATE ANY DATABASE`, which `RESTORE VERIFYONLY` needs. |

**Restore:** follow `restore.md`, which uses `restore.ps1`. Do a **restore drill** every few months, restoring into a copy so the live database is untouched:
```powershell
powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\restore.ps1 -BackupFile <newest .bak> -AsDatabase HRPayroll_RestoreTest
```
Then compare row counts with the live database, and drop `HRPayroll_RestoreTest`. The M10 drill result is in `docs/milestones/M10-report.md`.

## 8. Updates and rollback

Unzip the new package on the server and run:
```powershell
powershell -ExecutionPolicy Bypass -File .\update.ps1
```

| Step | What happens |
|---|---|
| 1 | `backup.ps1` takes the restore point. If it fails, nothing is changed. |
| 2 | `app_offline.htm` takes the site offline; visitors see a maintenance page. The current build is copied to `C:\HRPayroll\app.previous`. |
| 3 | The new build is copied; `migrate.ps1` applies new migrations. |
| 4 | The site comes back online and `/health` must answer `Healthy` within 90 seconds. |
| Failure | The previous build is put back automatically and the script prints the database restore command for the backup from step 1. A schema change can only be undone by restoring that backup (`restore.ps1 -Replace`). |

### Upgrading from 1.0 to 1.1.0 (the pay model change)

Version 1.1.0 changes every pay record to three monthly USD figures: salary, commission and billed. The update converts the existing records automatically, inside one transaction:

| Hire source | Conversion |
|---|---|
| Company recommended | Salary = the old pay; Commission = 2 × the old commission per period. |
| Budget hire paid in USD | Billed = the old budget, Salary = the old pay, Commission = the difference. |
| Budget hire paid in PKR | Salary = the old PKR pay ÷ the exchange rate in effect on the record's start date (or, before the first rate, the earliest rate). Billed = the old budget. The record is flagged **Review billing**, and its note says "Converted from Rs X at rate Y". |
| Owner | Salary = Billed = the old pay; Commission 0. |

- **Before updating:** make sure at least one exchange rate exists if anyone was paid in PKR. Otherwise the conversion stops with a message and nothing changes; the update then rolls back.
- **After updating:** open **Salaries → Needs review** and check each converted budget hire's salary.
- **What doesn't change:** finalized payrolls and invoices keep their amounts exactly. Draft payrolls are recalculated.

## 9. Operations

| Task | How |
|---|---|
| **Logs** | App logs: `C:\HRPayroll\logs\hr-YYYYMMDD.json`, JSON, 30 days kept, no personal data. Startup failures: the Application event log, source *IIS AspNetCore Module V2*. IIS request logs: `%SystemDrive%\inetpub\logs\LogFiles`. Every security event is also in **Audit log** (`/admin/audit`). |
| **Health** | `https://hr.example.com/health` → `Healthy` or `Unhealthy` (database), with no details. Point an uptime monitor at it. |
| **Data Protection keys** | `C:\HRPayroll\keys`, DPAPI-encrypted for this machine. New keys are made every 90 days automatically. To force a new key (for example after a suspected leak), stop the site, delete the files, and start it again: everyone signs in again. Back up this folder with the server; it can't be used on another machine. |
| **Certificate renewal** | Automatic (win-acme scheduled task). Check `wacs.exe --list` now and then. |
| **Managers** | Admin → **Managers**: add, edit, deactivate (which ends their sessions at once), reset password, require or reset two-factor sign-in. |
| **Lost Admin access** | On the server: `powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\admin-reset.ps1 -Email "admin@example.com"`. It prints a temporary password once, clears two-factor and lockout, ends sessions, and is audited. There is no web route for it. |
| **Uninstall** | `uninstall.ps1` removes the site, pool, binding, firewall rules, task and app, and **keeps** the data. `uninstall.ps1 -RemoveData` also deletes the database, backups, keys and config; you must type the database name to confirm. |

## Appendix A: LAN-only variant (no internet)
- Run setup with an internal name: `-HostName hr.office.local`, or the server's name.
- Create a matching DNS record on your internal DNS (or in the hosts files of the PCs).
- **Certificate:** Let's Encrypt can't validate internal names. Use your internal CA (Active Directory Certificate Services), or keep the self-signed certificate and trust it on each staff PC: export it from the server (public key only) and import it into *Trusted Root Certification Authorities*, by Group Policy or by hand.
- **Firewall:** allow 443 only from the office subnet (`New-NetFirewallRule … -RemoteAddress 192.168.1.0/24`), and skip port 80 (`-HttpPort 0`).

## Appendix B: Azure (brief)
- **App Service** (Windows, .NET 10): deploy the `app` folder (for example with zip deploy).
  - Settings go in *Configuration → Application settings*: `ConnectionStrings__DefaultConnection`, `DataProtection__KeysPath`, `AllowedHosts`. No `appsettings.Production.json` file.
  - Use HTTPS Only and TLS 1.2 minimum, with a managed certificate on a custom domain.
- **Azure SQL Database:** connect with the App Service's **managed identity** (`Authentication=Active Directory Managed Identity`), not a password. Create a contained user with `db_datareader`, `db_datawriter` and `EXECUTE`.
- **Data Protection:** DPAPI isn't available across instances. Store keys in Azure Blob Storage and protect them with Key Vault. This needs a small code change, so it is out of scope for v1.
- **Backups:** Azure SQL's automatic point-in-time restore replaces `backup.ps1`. Turn on long-term retention for monthly copies.
- **Migrations:** run `migrate.exe --connection "<Azure SQL admin connection>"` from a trusted machine or a release pipeline.
