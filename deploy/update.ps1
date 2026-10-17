<#
.SYNOPSIS
    Updates HR Payroll on this server to the version in this package. Run from the unzipped NEW package, as Administrator.

.DESCRIPTION
    1. Backs up the database (backup.ps1). Nothing changes if the backup fails.
    2. Takes the site offline (app_offline.htm) and keeps the current build in C:\HRPayroll\app.previous.
    3. Copies the new build, applies migrations (migrate.ps1), brings the site back and checks /health.
    4. If anything fails: restores the previous build and prints how to restore the database backup.
#>
[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'common.ps1')

$package = $PSScriptRoot
Assert-Windows
Assert-Administrator
$settings = Get-InstallSettings
$hostName = $settings.HostName
$port = [int]$settings.HttpsPort
$newVersion = (Get-Content -Raw (Join-Path $package 'VERSION.txt') -ErrorAction SilentlyContinue)
if ($newVersion) { $newVersion = $newVersion.Trim() }
foreach ($required in @('app\HR.Web.dll', 'migrate.exe', 'migrate.ps1')) {
    if (-not (Test-Path (Join-Path $package $required))) { Stop-WithError "The package is incomplete: $required is missing." }
}

Write-Host ''
Write-Host "HR Payroll update: $($settings.Version) -> $newVersion" -ForegroundColor White
Write-Host ''

# ---------------------------------------------------------------- 1. backup
Write-Step 'Backing up the database first'
& (Join-Path $ToolsDir 'backup.ps1')
if ($LASTEXITCODE -ne 0) { Stop-WithError 'The backup failed, so nothing was updated. Fix the backup first (C:\HRPayroll\backups\backup.log).' }
$backupFile = (Get-Content -Raw $StatusFile | ConvertFrom-Json).lastFile
Write-Done "Restore point: $backupFile"

# ---------------------------------------------------------------- 2. offline, keep the current build
Write-Step 'Taking the site offline (app_offline.htm)'
Enter-Maintenance
Copy-Mirror $AppDir $PreviousAppDir
Write-Done "The current build is kept in $PreviousAppDir."

function Restore-PreviousVersion([string]$Reason) {
    Write-Host ''
    Write-Warning2 "$Reason Rolling back to the previous build."
    Enter-Maintenance
    Copy-Mirror $PreviousAppDir $AppDir
    Exit-Maintenance
    $healthy = Wait-Healthy $hostName $port
    Write-Host ''
    if ($healthy) { Write-Host 'The previous build is running again.' -ForegroundColor Yellow }
    else { Write-Host 'The previous build did not report Healthy either; check the newest log in C:\HRPayroll\logs.' -ForegroundColor Red }
    Write-Host ''
    Write-Host 'If migrations had already changed the database, restore the backup taken at the start:' -ForegroundColor Yellow
    Write-Host "  powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\restore.ps1 -BackupFile `"$backupFile`" -Replace"
    Write-Host '  (step by step: C:\HRPayroll\tools\restore.md)'
    exit 1
}

# ---------------------------------------------------------------- 3. new build, migrations, online
try {
    Write-Step 'Copying the new build'
    Copy-Mirror (Join-Path $package 'app') $AppDir
    Install-Tools $package

    & (Join-Path $package 'migrate.ps1') -SqlInstance $settings.SqlInstance -DatabaseName $settings.DatabaseName
    if ($LASTEXITCODE -ne 0) { throw 'Applying migrations failed.' }
}
catch {
    Restore-PreviousVersion "The update failed: $($_.Exception.Message)"
}

Write-Step 'Bringing the site back online'
Exit-Maintenance
if (-not (Wait-Healthy $hostName $port)) {
    Restore-PreviousVersion 'The new build did not report Healthy within 90 seconds.'
}

$settings.Version = $newVersion
Save-InstallSettings $settings
Write-Done "https://${hostName}:$port/health answers Healthy."
Write-Host ''
Write-Host "HR Payroll is updated to $newVersion." -ForegroundColor Green
Write-Host "The previous build stays in $PreviousAppDir until the next update."
Write-Host ''
