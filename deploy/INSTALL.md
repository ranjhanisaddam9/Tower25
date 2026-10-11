# HR Payroll: install and run on one Windows PC

HR Payroll runs on this PC only, at **https://localhost:7443**. Other computers on the network can't reach it.

> `dotnet run` (Development) is for development only. Never use it with real data.

## What you need
- **Windows 10 or 11**, 64-bit.
- **SQL Server Express** installed. The default instance name is `SQLEXPRESS`.
- **Administrator rights, once.** Your Windows user must also be a SQL Server administrator; whoever installed SQL Server Express is one.
- **Optional: [7-Zip](https://www.7-zip.org).** It's needed for the encrypted backup copy kept off this PC.

You don't need .NET: the package includes it.

## Install
1. Unzip `HRPayroll-v1.0.0-win-x64.zip`, for example to `C:\Temp\HRPayroll-v1.0.0`.
2. Open **PowerShell as Administrator** (Start → type *PowerShell* → *Run as administrator*).
3. Run:
   ```powershell
   cd C:\Temp\HRPayroll-v1.0.0-win-x64
   powershell -ExecutionPolicy Bypass -File .\setup.ps1
   ```
   Setup checks the PC, copies the app to `C:\HRPayroll`, creates the database and a certificate, and installs the **HRPayroll** Windows Service.
4. Setup asks for the **first Admin**: an email, a full name and a password (10+ characters, with upper case, lower case and a digit). The password is used once and never saved.
5. Setup asks whether to register the **nightly backup**. Answer **Y**.

Setup is safe to run again: it repairs the install and never duplicates anything.

**Options:**
- `-NoService`: no Windows Service. A desktop shortcut, **Start HR Payroll**, starts the app minimized instead.
- `-Port 7500`: use another port.

## First sign-in
1. Open the **HR Payroll** shortcut on the desktop.
2. Sign in with the Admin email and password from setup, then choose a new password.
3. Set up **two-factor sign-in**:
   - scan the QR code with an authenticator app (Microsoft Authenticator or Google Authenticator), or type the key shown;
   - enter the 6-digit code;
   - store the **10 recovery codes** somewhere safe. Each one works once if you lose your phone.

## Backups
- **When:** every night at 23:00. If the PC was off at that time, the backup runs as soon as it's back on.
- **Where:** `C:\HRPayroll\backups`. The last 14 daily and 12 monthly backups are kept, and each backup is verified.
- **Warnings:** the dashboard warns you if the last backup failed or is more than 48 hours old.
- **Back up now** (as Administrator):
  ```powershell
  powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\backup.ps1
  ```
- **Encrypted copy off this PC (recommended):**
  1. Install 7-Zip.
  2. Run:
     ```powershell
     powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\backup.ps1 -SecondCopyPath "D:\HRPayroll-Backups" -SetPassword
     ```
     The folder can be another drive, or a OneDrive or Google Drive folder.
  3. Write the password down and keep it away from this PC. Without it, the encrypted copies can't be opened.
- **Restore:** follow `C:\HRPayroll\tools\restore.md`.

## Update to a new version
1. Unzip the new package.
2. In PowerShell as Administrator:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\update.ps1
   ```

The update backs up first, keeps the old version, migrates the database and checks the app. If anything fails, it puts the old version back and tells you how to restore the backup.

## Forgotten Admin password or lost phone
In PowerShell as Administrator:
```powershell
powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\admin-reset.ps1 -Email "admin@example.com"
```
A temporary password is printed once. Sign in with it, choose a new password and set up two-factor sign-in again.

## Moving to another PC
1. On the old PC: run `backup.ps1` and copy the newest `.bak` from `C:\HRPayroll\backups\daily` to the new PC, for example on a USB stick.
2. On the new PC: install with `setup.ps1`. Setup asks for an Admin; that account is replaced in the next step.
3. Restore the backup over the new install:
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\restore.ps1 -BackupFile "E:\HRPayroll_20261011_230000.bak" -Replace
   ```
4. Everyone signs in again with their existing passwords and authenticator apps. Only the browser sessions are new.
5. Uninstall from the old PC: `uninstall.ps1 -RemoveData`.

## Uninstall
- `C:\HRPayroll\tools\uninstall.ps1` removes the app, the service, the backup task and the shortcuts. It **keeps** the database, backups and settings.
- `uninstall.ps1 -RemoveData` deletes **everything**, including the database and backups. You must type the database name to confirm.

## Where things are
| Folder | Contents |
|---|---|
| `C:\HRPayroll\app` | the program (replaced by updates; the old one stays in `app.previous`) |
| `C:\HRPayroll\config` | settings (`appsettings.Production.json`, `install.json`) |
| `C:\HRPayroll\keys` | sign-in encryption keys, protected for this PC |
| `C:\HRPayroll\logs` | daily log files (30 kept) |
| `C:\HRPayroll\backups` | backups, `backup.log`, `status.json` |
| `C:\HRPayroll\tools` | these scripts and documents |
