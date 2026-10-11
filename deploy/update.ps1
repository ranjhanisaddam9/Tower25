<#
.SYNOPSIS
    Updates an installed HR Payroll to the version in this package. Run from the unzipped NEW package, as Administrator.

.DESCRIPTION
    1. Backs up the database (backup.ps1). Nothing changes if the backup fails.
    2. Stops the app and keeps the current version as C:\HRPayroll\app.previous.
    3. Copies the new version, applies database migrations, starts the app and checks /health.
    4. If anything fails: puts the previous version back, starts it, and prints how to restore the database backup.
#>
[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'common.ps1')

$package = $PSScriptRoot
Assert-Windows
Assert-Administrator
$settings = Get-InstallSettings
$port = [int]$settings.Port
$connection = Get-ConnectionString $settings.SqlInstance $settings.DatabaseName
$newVersion = (Get-Content -Raw (Join-Path $package 'VERSION.txt') -ErrorAction SilentlyContinue)
if ($newVersion) { $newVersion = $newVersion.Trim() }
foreach ($required in @('app\HR.Web.exe', 'migrate.exe')) {
    if (-not (Test-Path (Join-Path $package $required))) { Stop-WithError "The package is incomplete: $required is missing." }
}

Write-Host ''
Write-Host "HR Payroll update: $($settings.Version) -> $newVersion" -ForegroundColor White
Write-Host ''

# ---------------------------------------------------------------- 1. backup
Write-Step 'Backing up the database first'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $ToolsDir 'backup.ps1')
if ($LASTEXITCODE -ne 0) { Stop-WithError 'The backup failed, so nothing was updated. Fix the backup first (see C:\HRPayroll\backups\backup.log).' }
$backupFile = (Get-Content -Raw $StatusFile | ConvertFrom-Json).lastFile
Write-Done "Backup: $backupFile"

# ---------------------------------------------------------------- 2. stop, keep the old version
Write-Step 'Stopping HR Payroll'
Stop-App
if (Test-Path $PreviousAppDir) { Remove-Item $PreviousAppDir -Recurse -Force }
Move-Item $AppDir $PreviousAppDir
Write-Done "The current version is kept as $PreviousAppDir."

function Restore-PreviousVersion([string]$Reason) {
    Write-Host ''
    Write-Warning2 "$Reason Rolling back to the previous version."
    Stop-App
    if (Test-Path $AppDir) { Remove-Item $AppDir -Recurse -Force }
    Move-Item $PreviousAppDir $AppDir
    $healthy = Start-App -Port $port
    Write-Host ''
    if ($healthy) { Write-Host 'The previous version is running again.' -ForegroundColor Yellow }
    else { Write-Host 'The previous version did not report Healthy either; check the newest log in C:\HRPayroll\logs.' -ForegroundColor Red }
    Write-Host ''
    Write-Host 'If the new version had already changed the database, restore the backup taken at the start:' -ForegroundColor Yellow
    Write-Host "  powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\restore.ps1 -BackupFile `"$backupFile`" -Replace"
    Write-Host '  (step-by-step instructions: C:\HRPayroll\tools\restore.md)'
    exit 1
}

# ---------------------------------------------------------------- 3. new version
try {
    Write-Step 'Copying the new version'
    New-Item -ItemType Directory -Path $AppDir | Out-Null
    Copy-Mirror (Join-Path $package 'app') $AppDir
    foreach ($file in Get-ChildItem -Path $package -File | Where-Object { $_.Extension -in '.ps1', '.md', '.exe', '.txt' }) {
        Copy-Item -Path $file.FullName -Destination $ToolsDir -Force
    }
    if (Test-Path (Join-Path $package 'licences')) { Copy-Mirror (Join-Path $package 'licences') (Join-Path $ToolsDir 'licences') }

    Write-Step 'Applying database migrations'
    & (Join-Path $package 'migrate.exe') --connection $connection
    if ($LASTEXITCODE -ne 0) { throw "migrate.exe failed (exit code $LASTEXITCODE)." }
}
catch {
    Restore-PreviousVersion "The update failed: $($_.Exception.Message)"
}

Write-Step 'Starting the new version'
if (-not (Start-App -Port $port)) {
    Restore-PreviousVersion 'The new version did not report Healthy within 90 seconds.'
}

$settings.Version = $newVersion
Save-InstallSettings $settings
Write-Done "https://localhost:$port/health answers Healthy."
Write-Host ''
Write-Host "HR Payroll is updated to $newVersion." -ForegroundColor Green
Write-Host "The previous version stays in $PreviousAppDir until the next update."
Write-Host ''
