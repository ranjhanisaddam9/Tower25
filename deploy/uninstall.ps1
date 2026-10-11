<#
.SYNOPSIS
    Removes HR Payroll from this PC. Run as Administrator.

.DESCRIPTION
    By default removes the service, the backup task, the shortcuts and the app, and KEEPS the database, backups, keys and
    configuration, so a later setup.ps1 picks up where you left off.

.PARAMETER RemoveData
    Also deletes the database, all backups in C:\HRPayroll\backups, the keys, the configuration, the SQL logins and the
    certificate. You must type the database name to confirm. This can't be undone; encrypted copies in the second
    location are left alone.
#>
[CmdletBinding()]
param([switch]$RemoveData)

. (Join-Path $PSScriptRoot 'common.ps1')

Assert-Administrator
$settings = $null
if (Test-Path $InstallFile) { $settings = Get-Content -Raw $InstallFile | ConvertFrom-Json }
$database = Get-SettingOrDefault $settings 'DatabaseName' 'HRPayroll'
$sqlInstance = Get-SettingOrDefault $settings 'SqlInstance' '.\SQLEXPRESS'
Assert-DatabaseName $database

if ($RemoveData) {
    Write-Host ''
    Write-Host 'This permanently deletes:' -ForegroundColor Red
    Write-Host "  - the SQL Server database [$database] and everything in it"
    Write-Host "  - every backup in $BackupsDir, the keys and the configuration"
    Write-Host '  - the HTTPS certificate and the SQL logins created by setup'
    Write-Host ''
    $typed = Read-Host "Type the database name ($database) to confirm"
    if ($typed -cne $database) { Stop-WithError 'Not confirmed; nothing was removed.' }
}

Write-Step 'Stopping HR Payroll'
Stop-App
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    & sc.exe delete $ServiceName | Out-Null
    Write-Done "Service '$ServiceName' removed."
}

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Done "Task '$TaskName' removed."
}

$desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
foreach ($shortcut in @('HR Payroll.url', 'Start HR Payroll.lnk')) {
    $path = Join-Path $desktop $shortcut
    if (Test-Path $path) { Remove-Item $path -Force; Write-Done "Removed shortcut $shortcut." }
}

# This script may run from C:\HRPayroll\tools; it is already loaded, so the folder can go (but not while it's the current one).
Set-Location $env:TEMP
foreach ($dir in @($AppDir, $PreviousAppDir, $ToolsDir)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force; Write-Done "Removed $dir." }
}

if (-not $RemoveData) {
    Write-Host ''
    Write-Host 'HR Payroll is removed. Kept: the database, backups, keys and configuration in C:\HRPayroll.' -ForegroundColor Green
    Write-Host 'To install again, run setup.ps1 from the package. To delete everything, run uninstall.ps1 -RemoveData.'
    exit 0
}

Write-Step "Deleting database [$database]"
$exists = (Invoke-Sql -SqlInstance $sqlInstance -Query 'SELECT CAST(CASE WHEN DB_ID(@name) IS NULL THEN 0 ELSE 1 END AS int) AS DbExists' -Parameters @{ name = $database })[0].DbExists
if ($exists -eq 1) {
    Invoke-Sql -SqlInstance $sqlInstance -Query "ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database];" | Out-Null
    Write-Done 'Database deleted.'
}
Invoke-Sql -SqlInstance $sqlInstance -Query "IF SUSER_ID(N'NT SERVICE\HRPayroll') IS NOT NULL DROP LOGIN [NT SERVICE\HRPayroll];" | Out-Null
if ([bool](Get-SettingOrDefault $settings 'CreatedSystemLogin' $false)) {
    Invoke-Sql -SqlInstance $sqlInstance -Query "IF SUSER_ID(N'NT AUTHORITY\SYSTEM') IS NOT NULL DROP LOGIN [NT AUTHORITY\SYSTEM];" | Out-Null
}
else {
    Invoke-Sql -SqlInstance $sqlInstance -Query "IF SUSER_ID(N'NT AUTHORITY\SYSTEM') IS NOT NULL REVOKE CREATE ANY DATABASE FROM [NT AUTHORITY\SYSTEM];" | Out-Null
}
Write-Done 'SQL logins created by setup removed.'

$thumbprint = Get-SettingOrDefault $settings 'CertThumbprint' $null
$certs = @(Get-ChildItem Cert:\LocalMachine\My, Cert:\LocalMachine\Root |
    Where-Object { $_.FriendlyName -eq $CertFriendlyName -or ($thumbprint -and $_.Thumbprint -eq $thumbprint) })
foreach ($cert in $certs) { Remove-Item -Path $cert.PSPath -DeleteKey -ErrorAction SilentlyContinue; if (Test-Path $cert.PSPath) { Remove-Item -Path $cert.PSPath } }
if ($certs.Count -gt 0) { Write-Done 'HTTPS certificate removed (personal and trusted stores).' }

if (Test-Path $Root) { Remove-Item $Root -Recurse -Force; Write-Done "Removed $Root." }
Write-Host ''
Write-Host 'HR Payroll and all its data are removed from this PC.' -ForegroundColor Green
