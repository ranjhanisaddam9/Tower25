<#
.SYNOPSIS
    Emergency Admin recovery: a new temporary password, two-factor and lockout cleared. Run as Administrator.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\admin-reset.ps1 -Email "admin@example.com"

.DESCRIPTION
    Stops HR Payroll, runs "HR.Web.exe admin-reset" with the installed (Production) configuration, and starts it again.
    The new password is printed once on this screen and stored nowhere. At the next sign-in the Admin must change it and
    set up two-factor sign-in again. All of that Admin's sessions end. The reset is written to the audit log.
#>
[CmdletBinding()]
param([Parameter(Mandatory)] [string]$Email)

. (Join-Path $PSScriptRoot 'common.ps1')

Assert-Administrator
$settings = Get-InstallSettings

Write-Step 'Stopping HR Payroll'
Stop-App

try {
    Write-Step "Resetting the Admin account $Email"
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    Push-Location $AppDir
    & $Exe admin-reset --email $Email
    $exit = $LASTEXITCODE
}
finally {
    Pop-Location
    Remove-Item Env:\ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
    Write-Step 'Starting HR Payroll'
    if (Start-App -Port ([int]$settings.Port)) { Write-Done "https://localhost:$($settings.Port) is running." }
    else { Write-Warning2 'HR Payroll did not report Healthy; check the newest log in C:\HRPayroll\logs.' }
}

if ($exit -ne 0) { Stop-WithError 'The reset was not done (see the message above).' }
Write-Host ''
Write-Host 'Sign in with the temporary password above, choose a new password, then set up two-factor sign-in again.' -ForegroundColor Green
