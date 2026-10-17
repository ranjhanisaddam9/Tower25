<#
.SYNOPSIS
    Installs (or repairs) HR Payroll on a Windows Server with IIS. Run once from the unzipped package, as Administrator.

.DESCRIPTION
    Safe to run again: it repairs what is missing and never duplicates anything. Data, keys and backups are kept.
    See DEPLOYMENT.md for the whole procedure (prerequisites, DNS, the Let's Encrypt certificate, backups).

.PARAMETER HostName
    The public host name, e.g. hr.example.com (its DNS A record must point at this server for Let's Encrypt).
.PARAMETER DatabaseName
    SQL Server database (default HRPayroll).
.PARAMETER SqlInstance
    SQL Server on this machine (default "." = the default instance; e.g. .\SQLEXPRESS for a named one).
.PARAMETER HttpsPort
    Default 443.
.PARAMETER HttpPort
    Default 80 (redirects to HTTPS and serves Let's Encrypt validation). 0 = no HTTP binding.
.PARAMETER NoFirewall
    Don't create Windows Firewall rules (e.g. a test install that must not be reachable from the network).
.PARAMETER TrustTemporaryCertificate
    Test installs only: also trust the temporary self-signed certificate on this machine.
.PARAMETER BackupTask
    Yes = register the nightly backup without asking; No = skip it. Default: ask.
#>
[CmdletBinding()]
param(
    [string]$HostName,
    [string]$DatabaseName,
    [string]$SqlInstance,
    [int]$HttpsPort,
    [int]$HttpPort,
    [switch]$NoFirewall,
    [switch]$TrustTemporaryCertificate,
    [ValidateSet('Ask', 'Yes', 'No')] [string]$BackupTask = 'Ask'
)

. (Join-Path $PSScriptRoot 'common.ps1')

$package = $PSScriptRoot
Write-Host ''
Write-Host 'HR Payroll setup (IIS)' -ForegroundColor White
Write-Host ''

# ---------------------------------------------------------------- settings
Assert-Windows
Assert-Administrator
$previous = $null
if (Test-Path $InstallFile) { $previous = Get-Content -Raw $InstallFile | ConvertFrom-Json }
if (-not $PSBoundParameters.ContainsKey('HostName'))     { $HostName = Get-SettingOrDefault $previous 'HostName' '' }
if (-not $PSBoundParameters.ContainsKey('DatabaseName')) { $DatabaseName = Get-SettingOrDefault $previous 'DatabaseName' 'HRPayroll' }
if (-not $PSBoundParameters.ContainsKey('SqlInstance'))  { $SqlInstance = Get-SettingOrDefault $previous 'SqlInstance' '.' }
if (-not $PSBoundParameters.ContainsKey('HttpsPort'))    { $HttpsPort = [int](Get-SettingOrDefault $previous 'HttpsPort' 443) }
if (-not $PSBoundParameters.ContainsKey('HttpPort'))     { $HttpPort = [int](Get-SettingOrDefault $previous 'HttpPort' 80) }
if (-not $HostName) { $HostName = (Read-Host 'Public host name of HR Payroll (e.g. hr.example.com)').Trim() }
Assert-HostName $HostName
Assert-DatabaseName $DatabaseName
foreach ($port in @($HttpsPort) + @($HttpPort | Where-Object { $_ -ne 0 })) {
    if ($port -lt 1 -or $port -gt 65535) { Stop-WithError "Port $port is not valid." }
}

# ---------------------------------------------------------------- checks
Write-Step 'Checking this server'
foreach ($required in @('app\HR.Web.dll', 'app\web.config', 'migrate.exe', 'migrate.ps1', 'backup.ps1', 'register-backup-task.ps1')) {
    if (-not (Test-Path (Join-Path $package $required))) { Stop-WithError "The package is incomplete: $required is missing. Unzip the whole package and run setup.ps1 from it." }
}
if (-not (Test-Path $AppCmd) -or -not (Get-Service W3SVC -ErrorAction SilentlyContinue)) {
    Stop-WithError 'IIS is not installed. Install the Web Server (IIS) role first (DEPLOYMENT.md, step 1).'
}
if (-not (Test-Path 'HKLM:\SOFTWARE\Microsoft\IIS Extensions\IIS AspNetCore Module V2')) {
    Stop-WithError 'The ASP.NET Core Module is missing. Install the ASP.NET Core 10 Hosting Bundle, then run "iisreset" (DEPLOYMENT.md, step 1).'
}
# The app is 64-bit: it needs the x64 runtimes in "Program Files\dotnet" (an x86-only install in "Program Files (x86)" won't do).
$x64Shared = Join-Path $env:ProgramFiles 'dotnet\shared'
foreach ($framework in 'Microsoft.NETCore.App', 'Microsoft.AspNetCore.App') {
    if (-not (Get-ChildItem (Join-Path $x64Shared $framework) -Directory -Filter '10.*' -ErrorAction SilentlyContinue)) {
        Stop-WithError "The 64-bit (x64) $framework 10 runtime is missing in $x64Shared. Install the ASP.NET Core 10 Hosting Bundle (it includes x64 and x86), then run iisreset (DEPLOYMENT.md, step 1). An x86-only .NET install is not enough."
    }
}
if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) { Stop-WithError 'curl.exe is missing (it ships with Windows Server 2019 / Windows 10 1803 and later).' }
try {
    $isSysadmin = (Invoke-Sql -SqlInstance $SqlInstance -Query "SELECT CAST(IS_SRVROLEMEMBER('sysadmin') AS int) AS IsSysadmin")[0].IsSysadmin
}
catch {
    Stop-WithError "Can't connect to SQL Server '$SqlInstance' with your Windows login. Is it running? For a named instance use -SqlInstance .\NAME. Details: $($_.Exception.Message)"
}
if ($isSysadmin -ne 1) { Stop-WithError "Your Windows login is not a sysadmin on '$SqlInstance'; setup needs that once." }
Write-Done "IIS, ASP.NET Core Module, .NET 10 runtime, SQL Server '$SqlInstance' (sysadmin) - OK."

# SQL Server should not listen on the network (the app connects locally).
$sqlPids = @(Get-Process -Name sqlservr -ErrorAction SilentlyContinue | ForEach-Object Id)
$exposed = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
    Where-Object { $sqlPids -contains $_.OwningProcess -and $_.LocalAddress -notin '127.0.0.1', '::1' })
if ($exposed.Count -gt 0) {
    $ports = ($exposed | ForEach-Object LocalPort | Sort-Object -Unique) -join ', '
    Write-Warning2 "SQL Server listens on the network (TCP $ports). Make sure Windows Firewall blocks these ports from outside (DEPLOYMENT.md, step 2). Setup does not change it, because other applications may use it."
}

# ---------------------------------------------------------------- folders and files
Write-Step "Copying the app to $Root"
foreach ($dir in @($Root, $AppDir, $ToolsDir, $ConfigDir, $KeysDir, $LogsDir, $BackupsDir, (Join-Path $BackupsDir 'daily'), (Join-Path $BackupsDir 'monthly'))) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
}
$hadApp = Test-Path $Exe
if ($hadApp) { Enter-Maintenance }
Copy-Mirror (Join-Path $package 'app') $AppDir
Install-Tools $package
Write-Done 'App and tools copied.'

# ---------------------------------------------------------------- configuration
Write-Step 'Writing the configuration'
$config = [ordered]@{
    ConnectionStrings = [ordered]@{ DefaultConnection = (Get-ConnectionString $SqlInstance $DatabaseName) }
    AllowedHosts      = $HostName
    https_port        = $HttpsPort
    DataProtection    = [ordered]@{ KeysPath = $KeysDir }
    Logging           = [ordered]@{ File = [ordered]@{ Path = (Join-Path $LogsDir 'hr-.json') } }
    Backup            = [ordered]@{ StatusFile = $StatusFile }
}
$json = $config | ConvertTo-Json -Depth 6
if ((Test-Path $AppConfigFile) -and ((Get-Content -Raw $AppConfigFile) -ne $json)) {
    Copy-Item $AppConfigFile "$AppConfigFile.bak" -Force
    Write-Note "The previous configuration was saved as $AppConfigFile.bak."
}
[IO.File]::WriteAllText($AppConfigFile, $json, (New-Object Text.UTF8Encoding($false)))
$settings = [pscustomobject][ordered]@{
    Version            = ((Get-Content -Raw (Join-Path $package 'VERSION.txt') -ErrorAction SilentlyContinue) -as [string])
    HostName           = $HostName
    DatabaseName       = $DatabaseName
    SqlInstance        = $SqlInstance
    HttpsPort          = $HttpsPort
    HttpPort           = $HttpPort
    InstalledBy        = "$env:USERDOMAIN\$env:USERNAME"
    BackupSecondCopy   = (Get-SettingOrDefault $previous 'BackupSecondCopy' '')
    CreatedSystemLogin = [bool](Get-SettingOrDefault $previous 'CreatedSystemLogin' $false)
    FirewallRules      = (-not $NoFirewall)
}
if ($settings.Version) { $settings.Version = $settings.Version.Trim() }
Save-InstallSettings $settings
Write-Done $AppConfigFile

# ---------------------------------------------------------------- database
Write-Step "Database '$DatabaseName': applying migrations"
& (Join-Path $package 'migrate.ps1') -SqlInstance $SqlInstance -DatabaseName $DatabaseName
if ($LASTEXITCODE -ne 0) { Stop-WithError 'Applying migrations failed; see above.' }

# ---------------------------------------------------------------- IIS app pool and site
Write-Step "IIS application pool and site '$SiteName'"
if (-not ((& $AppCmd list apppool /name:$AppPoolName) -match $AppPoolName)) {
    Invoke-AppCmd add apppool "/name:$AppPoolName" | Out-Null
}
# No Managed Code; its own ApplicationPoolIdentity; always running and never idled out, so the first request is fast.
Invoke-AppCmd set apppool $AppPoolName '/managedRuntimeVersion:' '/managedPipelineMode:Integrated' '/startMode:AlwaysRunning' `
    '/autoStart:true' '/processModel.identityType:ApplicationPoolIdentity' '/processModel.idleTimeout:00:00:00' `
    '/processModel.loadUserProfile:true' '/recycling.periodicRestart.time:00:00:00' | Out-Null

$bindings = @("https/*:${HttpsPort}:$HostName")
if ($HttpPort -ne 0) { $bindings += "http/*:${HttpPort}:$HostName" }
if (-not ((& $AppCmd list site /name:$SiteName) -match $SiteName)) {
    Invoke-AppCmd add site "/name:$SiteName" "/physicalPath:$AppDir" "/bindings:$($bindings -join ',')" | Out-Null
}
Invoke-AppCmd set site "/site.name:$SiteName" "/bindings:$($bindings -join ',')" | Out-Null
Invoke-AppCmd set vdir "$SiteName/" "/physicalPath:$AppDir" | Out-Null
Invoke-AppCmd set site "/site.name:$SiteName" "/bindings.[protocol='https',bindingInformation='*:${HttpsPort}:$HostName'].sslFlags:1" | Out-Null
Invoke-AppCmd set app "$SiteName/" "/applicationPool:$AppPoolName" '/preloadEnabled:true' | Out-Null
Invoke-AppCmd set site "/site.name:$SiteName" '/serverAutoStart:true' | Out-Null
Write-Done "Pool '$AppPoolName' (No Managed Code, $AppPoolIdentity, always running); bindings $($bindings -join ', ')."

# ---------------------------------------------------------------- certificate
Write-Step "HTTPS certificate for $HostName"
$sslKey = "${HostName}:$HttpsPort"
$existing = (& netsh.exe http show sslcert hostnameport=$sslKey) -join "`n"
if ($existing -match 'Certificate Hash\s*:\s*([0-9a-fA-F]+)') {
    Write-Done "Already bound (certificate $($Matches[1])). Let's Encrypt renewals via win-acme keep this binding up to date."
}
else {
    $cert = Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.FriendlyName -eq $TempCertName -and $_.Subject -eq "CN=$HostName" -and $_.NotAfter -gt (Get-Date).AddDays(7) } |
        Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -DnsName $HostName -CertStoreLocation 'Cert:\LocalMachine\My' -FriendlyName $TempCertName `
            -NotAfter (Get-Date).AddDays(90) -KeyAlgorithm RSA -KeyLength 2048 -KeyExportPolicy NonExportable `
            -HashAlgorithm SHA256 -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1')
    }
    & netsh.exe http add sslcert hostnameport=$sslKey certhash=$($cert.Thumbprint) certstorename=MY appid='{4dc3e181-e14b-4a21-b022-59fc669b0914}' | Out-Null
    if ($LASTEXITCODE -ne 0) { Stop-WithError "Binding the certificate to $sslKey failed." }
    Write-Warning2 "A TEMPORARY self-signed certificate is bound (browsers will warn). Replace it with Let's Encrypt via win-acme (DEPLOYMENT.md, step 4)."
    if ($TrustTemporaryCertificate -and -not (Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -eq $cert.Thumbprint)) {
        $cer = Join-Path $env:TEMP "hrpayroll-$($cert.Thumbprint).cer"
        Export-Certificate -Cert $cert -FilePath $cer | Out-Null
        Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
        Remove-Item $cer -Force
        Set-Setting $settings 'TrustedTemporaryCertificate' $cert.Thumbprint
        Save-InstallSettings $settings
        Write-Note 'Test install: the temporary certificate is trusted on this machine.'
    }
}

# ---------------------------------------------------------------- permissions
Write-Step 'Folder permissions'
$admins = "*$SidAdministrators"
$system = "*$SidSystem"
& icacls.exe $Root /inheritance:r /grant:r "${admins}:(OI)(CI)F" "${system}:(OI)(CI)F" "${AppPoolIdentity}:(OI)(CI)RX" /Q | Out-Null
if ($LASTEXITCODE -ne 0) { Stop-WithError "Setting permissions on $Root failed." }
foreach ($writable in @($KeysDir, $LogsDir)) {
    & icacls.exe $writable /grant:r "${AppPoolIdentity}:(OI)(CI)M" /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { Stop-WithError "Setting permissions on $writable failed." }
}
$sqlServiceAccount = (Invoke-Sql -SqlInstance $SqlInstance -Query "SELECT TOP (1) service_account FROM sys.dm_server_services WHERE servicename LIKE N'SQL Server (%'")[0].service_account
& icacls.exe $BackupsDir /grant:r "${sqlServiceAccount}:(OI)(CI)M" /Q | Out-Null
if ($LASTEXITCODE -ne 0) { Stop-WithError "Setting permissions on $BackupsDir failed." }
if (Test-Path $PasswordFile) { & icacls.exe $PasswordFile /inheritance:r /grant:r "${admins}:F" "${system}:F" /Q | Out-Null }
Write-Done "$AppPoolIdentity reads the app and config and writes only keys and logs; SQL Server ($sqlServiceAccount) writes backups."

# ---------------------------------------------------------------- SQL login for the app
Write-Step "SQL login for $AppPoolIdentity (read, write and execute only)"
Invoke-Sql -SqlInstance $SqlInstance -Query "IF SUSER_ID(N'IIS APPPOOL\HRPayroll') IS NULL CREATE LOGIN [IIS APPPOOL\HRPayroll] FROM WINDOWS WITH DEFAULT_DATABASE = [$DatabaseName];" | Out-Null
Invoke-Sql -SqlInstance $SqlInstance -Database $DatabaseName -Query @"
IF USER_ID(N'IIS APPPOOL\HRPayroll') IS NULL CREATE USER [IIS APPPOOL\HRPayroll] FOR LOGIN [IIS APPPOOL\HRPayroll];
ALTER ROLE db_datareader ADD MEMBER [IIS APPPOOL\HRPayroll];
ALTER ROLE db_datawriter ADD MEMBER [IIS APPPOOL\HRPayroll];
GRANT EXECUTE TO [IIS APPPOOL\HRPayroll];
GRANT UPDATE ON OBJECT::dbo.PersonCodeSequence TO [IIS APPPOOL\HRPayroll];
"@ | Out-Null
Write-Done 'db_datareader, db_datawriter, EXECUTE (and the person-code sequence). No DDL, no backups, no server rights.'

# ---------------------------------------------------------------- firewall
if (-not $NoFirewall) {
    Write-Step 'Windows Firewall'
    $ports = @($HttpsPort) + @($HttpPort | Where-Object { $_ -ne 0 })
    foreach ($port in $ports) {
        $name = "HR Payroll TCP $port"
        if (-not (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName $name -Group $FirewallGroup -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow -Profile Any | Out-Null
        }
    }
    Write-Done "Inbound TCP $($ports -join ', ') allowed (rule group '$FirewallGroup')."
}

# ---------------------------------------------------------------- first Admin
$adminCount = (Invoke-Sql -SqlInstance $SqlInstance -Database $DatabaseName -Query @"
SELECT COUNT(*) AS Admins FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE r.Name = N'Admin'
"@)[0].Admins
if ($adminCount -eq 0) {
    Write-Step 'Create the first Admin account'
    Write-Note 'The password must have at least 10 characters with upper case, lower case and a digit.'
    Write-Note 'It is passed once to the app through this process''s environment and never written to disk.'
    do { $email = (Read-Host '    Admin email').Trim() } until ($email -match '^[^@\s]+@[^@\s]+\.[^@\s]+$')
    do { $fullName = (Read-Host '    Full name').Trim() } until ($fullName.Length -gt 0)
    while ($true) {
        $plain = ConvertFrom-SecureStringToPlain (Read-Host '    Password' -AsSecureString)
        $again = ConvertFrom-SecureStringToPlain (Read-Host '    Password again' -AsSecureString)
        $ok = ($plain -ceq $again) -and $plain.Length -ge 10 -and $plain -cmatch '[A-Z]' -and $plain -cmatch '[a-z]' -and $plain -match '[0-9]'
        $again = $null
        if ($ok) { break }
        $plain = $null
        Write-Warning2 'The passwords differ or are too weak. Try again.'
    }
    try {
        $env:Seed__Admin__Email = $email
        $env:Seed__Admin__FullName = $fullName
        $env:Seed__Admin__Password = $plain
        $env:ASPNETCORE_ENVIRONMENT = 'Production'
        Push-Location $AppDir # the app finds appsettings.json and its config folder from here
        & $Exe seed-admin
        $seedExit = $LASTEXITCODE
    }
    finally {
        Pop-Location
        Remove-Item Env:\Seed__Admin__Password, Env:\Seed__Admin__Email, Env:\Seed__Admin__FullName, Env:\ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
        $plain = $null
        [GC]::Collect()
    }
    if ($seedExit -ne 0) { Stop-WithError 'The Admin account was not created (see the message above). Run setup.ps1 again to retry.' }
    Write-Done "Admin $email created."
}

# ---------------------------------------------------------------- backups
$registerTask = $BackupTask
if ($registerTask -eq 'Ask') {
    $answer = Read-Host '==> Register the nightly backup (23:00)? [Y/n]'
    $registerTask = if ($answer -match '^(n|no)$') { 'No' } else { 'Yes' }
}
if ($registerTask -eq 'Yes') {
    & (Join-Path $ToolsDir 'register-backup-task.ps1')
    if ($LASTEXITCODE -ne 0) { Stop-WithError 'Registering the backup task failed.' }
}
else {
    Write-Warning2 'No nightly backup registered; the dashboard will warn until one runs. Run C:\HRPayroll\tools\register-backup-task.ps1 later.'
}

# ---------------------------------------------------------------- start
Write-Step 'Starting HR Payroll'
Exit-Maintenance
& $AppCmd start apppool "/apppool.name:$AppPoolName" | Out-Null # "already started" is fine
& $AppCmd start site "/site.name:$SiteName" | Out-Null
$global:LASTEXITCODE = 0
if (-not (Wait-Healthy $HostName $HttpsPort)) {
    Stop-WithError "HR Payroll didn't report Healthy within 90 seconds. Check the newest file in $LogsDir and the Application event log (source 'IIS AspNetCore Module V2')."
}
Write-Done "https://${HostName}:$HttpsPort/health answers Healthy."

Write-Host ''
Write-Host 'HR Payroll is installed.' -ForegroundColor Green
Write-Host "  URL:   https://$HostName$(if ($HttpsPort -ne 443) { ":$HttpsPort" })"
Write-Host '  Next:  1. Let''s Encrypt certificate with win-acme (DEPLOYMENT.md, step 4).'
Write-Host '         2. First sign-in: change the password, set up two-factor sign-in, keep the 10 recovery codes.'
Write-Host '         3. Encrypted off-server backup copy: install 7-Zip, run backup.ps1 -SecondCopyPath <folder> -SetPassword.'
Write-Host ''
