# Restoring an HR Payroll backup

Do this in **PowerShell opened as Administrator**. `restore.ps1` checks the backup before it changes anything.

## 1. Pick the backup
- **Local backups:**
  - `C:\HRPayroll\backups\daily\HRPayroll_YYYYMMDD_HHMMSS.bak` (the last 14)
  - `C:\HRPayroll\backups\monthly\HRPayroll_YYYYMM.bak` (the last 12)
- **Encrypted copies:** `HRPayroll_YYYYMMDD_HHMMSS.bak.7z` in your second location. These need 7-Zip and the backup password. On the PC that made them, the saved password is used automatically.
- To see which backups succeeded and when, check `C:\HRPayroll\backups\backup.log`.

## 2. (Recommended) Check the backup into a separate database first
```powershell
powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\restore.ps1 -BackupFile "C:\HRPayroll\backups\daily\HRPayroll_20261011_230000.bak" -AsDatabase HRPayroll_Check
```
This leaves the live database untouched.

To look at the copy, open it in SQL Server Management Studio. Afterwards, delete it there (right-click the database → Delete), or run:
```powershell
sqlcmd -S .\SQLEXPRESS -E -Q "DROP DATABASE [HRPayroll_Check]"
```

## 3. Restore over the live database
```powershell
powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\restore.ps1 -BackupFile "C:\HRPayroll\backups\daily\HRPayroll_20261011_230000.bak" -Replace
```
The script:
1. verifies the backup (`RESTORE VERIFYONLY ... WITH CHECKSUM`);
2. stops HR Payroll;
3. replaces the database;
4. starts HR Payroll again and checks that it's healthy.

Everything entered after that backup was taken is lost. Write down anything you need to re-enter first.

## 4. After restoring
1. Sign in and check the latest payroll run and invoices.
2. If anything is missing, enter it again.
3. Run a fresh backup:
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\backup.ps1
   ```

## If the script can't be used
These are the same steps by hand, for example in SQL Server Management Studio:
1. Stop the service:
   ```powershell
   Stop-Service HRPayroll
   ```
2. If the backup is a `.7z` copy, open it with 7-Zip to get the `.bak` (it asks for the password).
3. Run, as a SQL Server administrator:
   ```sql
   ALTER DATABASE [HRPayroll] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
   RESTORE DATABASE [HRPayroll] FROM DISK = N'C:\path\to\backup.bak' WITH CHECKSUM, REPLACE, RECOVERY;
   ALTER DATABASE [HRPayroll] SET MULTI_USER;
   ```
4. Start the service:
   ```powershell
   Start-Service HRPayroll
   ```
   Then open https://localhost:7443/health. It should say `Healthy`.
