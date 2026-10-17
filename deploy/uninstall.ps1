<#
.SYNOPSIS
    Removes HR Payroll from this server. Run as Administrator.

.DESCRIPTION
    By default removes the IIS site and application pool, the HTTPS binding, the firewall rules, the backup task and the
    app, and KEEPS the database, backups, keys and configuration, so a later setup.ps1 picks up where you left off.

.PARAMETER RemoveData
    Also deletes the database, all backups in C:\HRPayroll\backups, the keys, the configuration, the SQL logins created
    by setup and the temporary certificate. You must type the database name to confirm. This can't be undone; the
    encrypted copies in the second location and a win-acme renewal (see DEPLOYMENT.md) are left alone.
#>
[CmdletBinding()]
param([switch]$RemoveData)

. (Join-Path $PSScriptRoot 'common.ps1')

Assert-Administrator
$settings = $null
if (Test-Path $InstallFile) { $settings = Get-Content -Raw $InstallFile | ConvertFrom-Json }
$database = Get-SettingOrDefault $settings 'DatabaseName' 'HRPayroll'
$sqlInstance = Get-SettingOrDefault $settings 'SqlInstance' '.'
$hostName = Get-SettingOrDefault $settings 'HostName' ''
$httpsPort = [int](Get-SettingOrDefault $settings 'HttpsPort' 443)
Assert-DatabaseName $database

if ($RemoveData) {
    Write-Host ''
    Write-Host 'This permanently deletes:' -ForegroundColor Red
    Write-Host "  - the SQL Server database [$database] and everything in it"
    Write-Host "  - every backup in $BackupsDir, the keys and the configuration"
    Write-Host '  - the SQL logins and the temporary certificate created by setup'
    Write-Host ''
    $typed = Read-Host "Type the database name ($database) to confirm"
    if ($typed -cne $database) { Stop-WithError 'Not confirmed; nothing was removed.' }
}

Write-Step 'IIS'
if ((& $AppCmd list site /name:$SiteName) -match $SiteName) { Invoke-AppCmd delete site $SiteName | Out-Null; Write-Done "Site '$SiteName' removed." }
if ((& $AppCmd list apppool /name:$AppPoolName) -match $AppPoolName) { Invoke-AppCmd delete apppool $AppPoolName | Out-Null; Write-Done "Application pool '$AppPoolName' removed." }
if ($hostName) {
    $sslKey = "${hostName}:$httpsPort"
    if (((& netsh.exe http show sslcert hostnameport=$sslKey) -join "`n") -match 'Certificate Hash') {
        & netsh.exe http delete sslcert hostnameport=$sslKey | Out-Null
        Write-Done "HTTPS binding $sslKey removed."
    }
}
$global:LASTEXITCODE = 0

$rules = @(Get-NetFirewallRule -Group $FirewallGroup -ErrorAction SilentlyContinue)
if ($rules.Count -gt 0) { $rules | Remove-NetFirewallRule; Write-Done 'Firewall rules removed.' }

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Done "Task '$TaskName' removed."
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
Invoke-Sql -SqlInstance $sqlInstance -Query "IF SUSER_ID(N'IIS APPPOOL\HRPayroll') IS NOT NULL DROP LOGIN [IIS APPPOOL\HRPayroll];" | Out-Null
if ([bool](Get-SettingOrDefault $settings 'CreatedSystemLogin' $false)) {
    Invoke-Sql -SqlInstance $sqlInstance -Query "IF SUSER_ID(N'NT AUTHORITY\SYSTEM') IS NOT NULL DROP LOGIN [NT AUTHORITY\SYSTEM];" | Out-Null
}
else {
    Invoke-Sql -SqlInstance $sqlInstance -Query "IF SUSER_ID(N'NT AUTHORITY\SYSTEM') IS NOT NULL REVOKE CREATE ANY DATABASE FROM [NT AUTHORITY\SYSTEM];" | Out-Null
}
Write-Done 'SQL logins created by setup removed.'

$certs = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object FriendlyName -eq $TempCertName)
$trusted = Get-SettingOrDefault $settings 'TrustedTemporaryCertificate' $null
if ($trusted) { $certs += @(Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -eq $trusted) }
foreach ($cert in $certs) { Remove-Item -Path $cert.PSPath -DeleteKey -ErrorAction SilentlyContinue; if (Test-Path $cert.PSPath) { Remove-Item -Path $cert.PSPath } }
if ($certs.Count -gt 0) { Write-Done 'Temporary certificate removed.' }

if (Test-Path $Root) { Remove-Item $Root -Recurse -Force; Write-Done "Removed $Root." }
Write-Host ''
Write-Host 'HR Payroll and all its data are removed from this server.' -ForegroundColor Green
Write-Host 'If win-acme renews a certificate for this site, remove that renewal too: wacs.exe --cancel --friendlyname <name>.'
