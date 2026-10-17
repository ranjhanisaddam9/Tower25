<#
.SYNOPSIS
    Applies HR Payroll database migrations with the migration bundle (migrate.exe next to this script).

.DESCRIPTION
    Migrations are never applied by the web app itself: an administrator runs this (setup.ps1 and update.ps1 call it).
    It connects with the current Windows login, which needs to be able to change the schema (sysadmin or db_owner).
    Already-applied migrations are skipped, so running it twice is harmless.

.PARAMETER SqlInstance
    Default: the installed value, else "." (the default instance).
.PARAMETER DatabaseName
    Default: the installed value, else HRPayroll. The database is created if it doesn't exist.
#>
[CmdletBinding()]
param(
    [string]$SqlInstance,
    [string]$DatabaseName
)

. (Join-Path $PSScriptRoot 'common.ps1')

$settings = $null
if (Test-Path $InstallFile) { $settings = Get-Content -Raw $InstallFile | ConvertFrom-Json }
if (-not $SqlInstance) { $SqlInstance = Get-SettingOrDefault $settings 'SqlInstance' '.' }
if (-not $DatabaseName) { $DatabaseName = Get-SettingOrDefault $settings 'DatabaseName' 'HRPayroll' }
Assert-DatabaseName $DatabaseName

$bundle = Join-Path $PSScriptRoot 'migrate.exe'
if (-not (Test-Path $bundle)) { Stop-WithError "migrate.exe not found next to this script ($bundle)." }

Write-Step "Applying migrations to [$DatabaseName] on '$SqlInstance'"
& $bundle --connection (Get-ConnectionString $SqlInstance $DatabaseName)
if ($LASTEXITCODE -ne 0) { Stop-WithError "migrate.exe failed (exit code $LASTEXITCODE). The database was left as it was before the failing migration." }
Write-Done 'The database schema is up to date.'

# Draft payrolls are recalculated with the current rules (needed after a pay-model change; harmless otherwise).
# Finalized payrolls are never touched.
if (Test-Path $Exe) {
    Write-Step 'Recalculating draft payrolls'
    try {
        $env:ASPNETCORE_ENVIRONMENT = 'Production'
        Push-Location $AppDir # the app finds appsettings.json and its config folder from here
        & $Exe recalculate-drafts
        $recalculated = $LASTEXITCODE
    }
    finally {
        Pop-Location
        Remove-Item Env:\ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
    }
    if ($recalculated -ne 0) { Stop-WithError 'Recalculating the draft payrolls failed; open each draft payroll and press Recalculate.' }
}
exit 0
