# Restoring an HR Payroll backup

Do this on the server, in **PowerShell opened as Administrator**. `restore.ps1` checks the backup (`RESTORE VERIFYONLY ... WITH CHECKSUM`) before it changes anything.

## 1. Pick the backup
- **Local backups:**
  - `C:\HRPayroll\backups\daily\HRPayroll_YYYYMMDD_HHMMSS.bak` (the last 14)
  - `C:\HRPayroll\backups\monthly\HRPayroll_YYYYMM.bak` (the last 12)
- **Encrypted copies:** `HRPayroll_YYYYMMDD_HHMMSS.bak.7z` in the second location. These need 7-Zip and the backup password. On the server that made them, the saved password is used automatically.
- Which backups succeeded, and when: `C:\HRPayroll\backups\backup.log`.

## 2. Drill, or check a backup: restore into a separate database
```powershell
powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\restore.ps1 -BackupFile "C:\HRPayroll\backups\daily\HRPayroll_20261011_230000.bak" -AsDatabase HRPayroll_RestoreTest
```
The live database and the site are untouched.

1. Compare the row counts of the main tables with the live database:
   - in SQL Server Management Studio, right-click the database → Reports → *Disk Usage by Table*;
   - or run a `COUNT(*)` per table.
2. Then drop the copy:
   ```powershell
   sqlcmd -S . -E -Q "DROP DATABASE [HRPayroll_RestoreTest]"
   ```

## 3. Restore over the live database
```powershell
powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\restore.ps1 -BackupFile "C:\HRPayroll\backups\daily\HRPayroll_20261011_230000.bak" -Replace
```
The script:
1. verifies the backup;
2. takes the site offline (`app_offline.htm`, so visitors see a maintenance page);
3. replaces the database;
4. brings the site back online and waits for `/health` to answer `Healthy`.

Everything entered after that backup was taken is lost. Write down anything you need to re-enter first.

## 4. After restoring
1. Sign in and check the latest payroll run and invoices.
2. If anything is missing, enter it again.
3. Run a fresh backup:
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\backup.ps1
   ```

## If the script can't be used
These are the same steps by hand, in SQL Server Management Studio:
1. Create `C:\HRPayroll\app\app_offline.htm`, containing any short text. This stops the site.
2. If the backup is a `.7z` copy, open it with 7-Zip to get the `.bak` (it asks for the password).
3. Run, as a SQL Server administrator:
   ```sql
   ALTER DATABASE [HRPayroll] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
   RESTORE DATABASE [HRPayroll] FROM DISK = N'C:\path\to\backup.bak' WITH CHECKSUM, REPLACE, RECOVERY;
   ALTER DATABASE [HRPayroll] SET MULTI_USER;
   ```
4. Delete `app_offline.htm`, then open `https://<your host>/health`. It should say `Healthy`.

The app's SQL login (`IIS APPPOOL\HRPayroll`) is stored inside the database, so a restore on the same server keeps it working. **On a new server:** run `setup.ps1` first, then restore with `restore.ps1 -Replace`, then run `setup.ps1` once more. The second run re-links the app's login and re-applies any newer migrations.
