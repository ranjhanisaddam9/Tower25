<#
.SYNOPSIS
    Registers the nightly HR Payroll backup (23:00; runs as soon as possible after a missed start). Run as Administrator.

.DESCRIPTION
    The task runs C:\HRPayroll\tools\backup.ps1 as SYSTEM, so it works when nobody is signed in and no Windows password
    is stored. SYSTEM gets just enough SQL Server rights for it: db_backupoperator on the HR Payroll database, plus
    CREATE ANY DATABASE, which SQL Server requires for RESTORE VERIFYONLY.

.PARAMETER Unregister
    Removes the task (the SQL rights stay until uninstall.ps1 -RemoveData).
#>
[CmdletBinding()]
param([switch]$Unregister)

. (Join-Path $PSScriptRoot 'common.ps1')

Assert-Administrator
$settings = Get-InstallSettings
Assert-DatabaseName $settings.DatabaseName

if ($Unregister) {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
    Write-Done "Task '$TaskName' removed."
    exit 0
}

Write-Step "Nightly backup task '$TaskName'"

# SQL Server rights for SYSTEM (the task's account).
$hadLogin = (Invoke-Sql -SqlInstance $settings.SqlInstance -Query "SELECT CAST(CASE WHEN SUSER_ID(N'NT AUTHORITY\SYSTEM') IS NULL THEN 0 ELSE 1 END AS int) AS HasLogin")[0].HasLogin -eq 1
Invoke-Sql -SqlInstance $settings.SqlInstance -Query @"
IF SUSER_ID(N'NT AUTHORITY\SYSTEM') IS NULL CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS;
GRANT CREATE ANY DATABASE TO [NT AUTHORITY\SYSTEM];
"@ | Out-Null
Invoke-Sql -SqlInstance $settings.SqlInstance -Database $settings.DatabaseName -Query @"
IF USER_ID(N'NT AUTHORITY\SYSTEM') IS NULL CREATE USER [NT AUTHORITY\SYSTEM] FOR LOGIN [NT AUTHORITY\SYSTEM];
ALTER ROLE db_backupoperator ADD MEMBER [NT AUTHORITY\SYSTEM];
"@ | Out-Null
if (-not $hadLogin) {
    if ($settings.PSObject.Properties.Name -contains 'CreatedSystemLogin') { $settings.CreatedSystemLogin = $true }
    else { $settings | Add-Member -NotePropertyName CreatedSystemLogin -NotePropertyValue $true }
    Save-InstallSettings $settings
}
Write-Done 'SQL Server: SYSTEM may back up this database and verify backups.'

$script = Join-Path $ToolsDir 'backup.ps1'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$script`""
$trigger = New-ScheduledTaskTrigger -Daily -At '23:00'
$principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
$taskSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew -RestartCount 2 -RestartInterval (New-TimeSpan -Minutes 15)
$task = New-ScheduledTask -Action $action -Trigger $trigger -Principal $principal -Settings $taskSettings `
    -Description 'Backs up the HR Payroll database (C:\HRPayroll\backups). Registered by register-backup-task.ps1.'
Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force | Out-Null
Write-Done 'Every day at 23:00 as SYSTEM; if the PC was off at 23:00 it runs as soon as possible afterwards.'
exit 0
