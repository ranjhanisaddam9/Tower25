<#
.SYNOPSIS
    Emergency Admin recovery: a new temporary password, two-factor and lockout cleared. Run on the server as Administrator.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File C:\HRPayroll\tools\admin-reset.ps1 -Email "admin@example.com"

.DESCRIPTION
    Runs "HR.Web.exe admin-reset" with the server's Production configuration. There is no web route for this.
    The new password is printed once on this screen and stored nowhere. All of that Admin's sessions end; at the next
    sign-in they must change the password and set up two-factor sign-in again. The reset is written to the audit log.
    The site keeps running.
#>
[CmdletBinding()]
param([Parameter(Mandatory)] [string]$Email)

. (Join-Path $PSScriptRoot 'common.ps1')

Assert-Administrator
$null = Get-InstallSettings

Write-Step "Resetting the Admin account $Email"
try {
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    Push-Location $AppDir # the app finds appsettings.json and its config folder from here
    & $Exe admin-reset --email $Email
    $exit = $LASTEXITCODE
}
finally {
    Pop-Location
    Remove-Item Env:\ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
}

if ($exit -ne 0) { Stop-WithError 'The reset was not done (see the message above).' }
Write-Host ''
Write-Host 'Sign in with the temporary password above, choose a new password, then set up two-factor sign-in again.' -ForegroundColor Green
