<#
.SYNOPSIS
    Installs (or repairs) HR Payroll on this PC. Run once, from the unzipped package, in PowerShell opened as Administrator.

.DESCRIPTION
    Safe to run again: it repairs what is missing and never duplicates anything. Your data, keys and backups are kept.

.PARAMETER DatabaseName
    SQL Server database to use (default HRPayroll). Kept from the previous install when not given.
.PARAMETER Port
    HTTPS port on this PC only (default 7443). Kept from the previous install when not given.
.PARAMETER NoService
    Don't install a Windows Service. A "Start HR Payroll" shortcut starts the app (minimized) instead.
.PARAMETER SqlInstance
    SQL Server instance (default .\SQLEXPRESS).
.PARAMETER BackupTask
    Yes = register the nightly backup without asking; No = skip it. Default: ask.
#>
[CmdletBinding()]
param(
    [string]$DatabaseName,
    [int]$Port,
    [switch]$NoService,
    [string]$SqlInstance,
    [ValidateSet('Ask', 'Yes', 'No')] [string]$BackupTask = 'Ask'
)

. (Join-Path $PSScriptRoot 'common.ps1')

$package = $PSScriptRoot
Write-Host ''
Write-Host 'HR Payroll setup' -ForegroundColor White
Write-Host ''

# ---------------------------------------------------------------- checks
Write-Step 'Checking this PC'
Assert-Windows
Assert-Administrator
foreach ($required in @('app\HR.Web.exe', 'migrate.exe', 'backup.ps1', 'register-backup-task.ps1')) {
    if (-not (Test-Path (Join-Path $package $required))) { Stop-WithError "The package is incomplete: $required is missing. Unzip the whole package and run setup.ps1 from it." }
}

$previous = $null
if (Test-Path $InstallFile) { $previous = Get-Content -Raw $InstallFile | ConvertFrom-Json }
if (-not $PSBoundParameters.ContainsKey('DatabaseName')) { $DatabaseName = Get-SettingOrDefault $previous 'DatabaseName' 'HRPayroll' }
if (-not $PSBoundParameters.ContainsKey('Port'))         { $Port = [int](Get-SettingOrDefault $previous 'Port' 7443) }
if (-not $PSBoundParameters.ContainsKey('SqlInstance'))  { $SqlInstance = Get-SettingOrDefault $previous 'SqlInstance' '.\SQLEXPRESS' }
Assert-DatabaseName $DatabaseName
if ($Port -lt 1024 -or $Port -gt 65535) { Stop-WithError "Port $Port is not allowed; use 1024-65535." }
$mode = if ($NoService) { 'Console' } else { 'Service' }

try {
    $isSysadmin = (Invoke-Sql -SqlInstance $SqlInstance -Query "SELECT CAST(IS_SRVROLEMEMBER('sysadmin') AS int) AS IsSysadmin")[0].IsSysadmin
}
catch {
    Stop-WithError "Can't connect to SQL Server '$SqlInstance' with your Windows login. Is SQL Server Express installed and running (services.msc > 'SQL Server (SQLEXPRESS)')? Details: $($_.Exception.Message)"
}
if ($isSysadmin -ne 1) {
    Stop-WithError "Your Windows login can connect to '$SqlInstance' but is not a SQL Server administrator (sysadmin), which setup needs once. Sign in as the Windows user who installed SQL Server Express, or ask whoever did to add you."
}

$listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    Where-Object { (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).Name -ne 'HR.Web' }
if ($listener) { Stop-WithError "Port $Port is already used by another program (process id $($listener[0].OwningProcess)). Choose another with -Port." }
Write-Done "Windows $([Environment]::OSVersion.Version), PowerShell $($PSVersionTable.PSVersion), SQL Server '$SqlInstance' reachable as sysadmin, port $Port free."

# ---------------------------------------------------------------- folders and files
Write-Step "Copying the app to $Root"
Stop-App
foreach ($dir in @($Root, $AppDir, $ToolsDir, $ConfigDir, $KeysDir, $LogsDir, $BackupsDir, (Join-Path $BackupsDir 'daily'), (Join-Path $BackupsDir 'monthly'))) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
}
Copy-Mirror (Join-Path $package 'app') $AppDir
foreach ($file in Get-ChildItem -Path $package -File | Where-Object { $_.Extension -in '.ps1', '.md', '.exe', '.txt' }) {
    Copy-Item -Path $file.FullName -Destination $ToolsDir -Force
}
if (Test-Path (Join-Path $package 'licences')) { Copy-Mirror (Join-Path $package 'licences') (Join-Path $ToolsDir 'licences') }
Write-Done 'App and tools copied.'

# ---------------------------------------------------------------- certificate
Write-Step 'HTTPS certificate for localhost'
$cert = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.FriendlyName -eq $CertFriendlyName -and $_.NotAfter -gt (Get-Date).AddDays(30) -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -DnsName 'localhost' -CertStoreLocation 'Cert:\LocalMachine\My' -FriendlyName $CertFriendlyName `
        -NotAfter (Get-Date).AddYears(5) -KeyAlgorithm RSA -KeyLength 2048 -KeyExportPolicy NonExportable `
        -Provider 'Microsoft Software Key Storage Provider' -HashAlgorithm SHA256 `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1')
    Write-Done "Created a self-signed certificate (valid until $($cert.NotAfter.ToString('dd MMM yyyy')))."
}
else {
    Write-Done "Using the existing certificate (valid until $($cert.NotAfter.ToString('dd MMM yyyy')))."
}
if (-not (Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -eq $cert.Thumbprint)) {
    $cer = Join-Path $env:TEMP "hrpayroll-$($cert.Thumbprint).cer"
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    Remove-Item $cer -Force
    Write-Done 'Trusted on this PC (LocalMachine\Root), so browsers show no warning.'
}

# ---------------------------------------------------------------- configuration
Write-Step 'Writing the configuration'
$config = [ordered]@{
    ConnectionStrings = [ordered]@{ DefaultConnection = (Get-ConnectionString $SqlInstance $DatabaseName) }
    AllowedHosts      = 'localhost'
    Kestrel           = [ordered]@{ Endpoints = [ordered]@{ Https = [ordered]@{ Url = "https://localhost:$Port" } } }
    HttpsCertificate  = [ordered]@{ Thumbprint = $cert.Thumbprint }
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

$settings = [ordered]@{
    Version         = (Get-Content -Raw (Join-Path $package 'VERSION.txt') -ErrorAction SilentlyContinue)
    DatabaseName    = $DatabaseName
    Port            = $Port
    SqlInstance     = $SqlInstance
    Mode            = $mode
    CertThumbprint  = $cert.Thumbprint
    InstalledBy     = "$env:USERDOMAIN\$env:USERNAME"
    BackupSecondCopy = (Get-SettingOrDefault $previous 'BackupSecondCopy' '')
    CreatedSystemLogin = [bool](Get-SettingOrDefault $previous 'CreatedSystemLogin' $false)
}
if ($settings.Version) { $settings.Version = $settings.Version.Trim() }
Save-InstallSettings $settings
Write-Done $AppConfigFile

# ---------------------------------------------------------------- database
Write-Step "Database '$DatabaseName': applying migrations"
& (Join-Path $package 'migrate.exe') --connection (Get-ConnectionString $SqlInstance $DatabaseName)
if ($LASTEXITCODE -ne 0) { Stop-WithError "migrate.exe failed (exit code $LASTEXITCODE). Nothing else was changed in the database." }
Write-Done 'Database is up to date.'

# ---------------------------------------------------------------- service or shortcut
$desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
$startShortcut = Join-Path $desktop 'Start HR Payroll.lnk'
if ($mode -eq 'Service') {
    Write-Step "Windows Service '$ServiceName'"
    if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
        New-Service -Name $ServiceName -BinaryPathName "`"$Exe`"" -DisplayName 'HR Payroll' -StartupType Automatic `
            -Description 'HR Payroll web app (https://localhost only).' | Out-Null
    }
    & sc.exe config $ServiceName binPath= "`"$Exe`"" obj= $ServiceAccount start= auto | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
    & sc.exe failureflag $ServiceName 1 | Out-Null
    if (Test-Path $startShortcut) { Remove-Item $startShortcut -Force }
    Write-Done "Runs as $ServiceAccount, starts automatically, restarts after a failure."
}
else {
    Write-Step 'No service: creating the "Start HR Payroll" shortcut'
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        & sc.exe delete $ServiceName | Out-Null
        Write-Note 'The Windows Service from an earlier install was removed.'
    }
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($startShortcut)
    $link.TargetPath = $Exe
    $link.WorkingDirectory = $AppDir
    $link.WindowStyle = 7 # minimized
    $link.Description = 'Starts HR Payroll (close its window to stop it)'
    $link.Save()
    Write-Done $startShortcut
}

# ---------------------------------------------------------------- permissions
Write-Step 'Folder permissions'
$installer = "$env:USERDOMAIN\$env:USERNAME"
$runAs = if ($mode -eq 'Service') { $ServiceAccount } else { $installer }
$admins = "*$SidAdministrators"
$system = "*$SidSystem"
& icacls.exe $Root /inheritance:r /grant:r "${admins}:(OI)(CI)F" "${system}:(OI)(CI)F" "${runAs}:(OI)(CI)RX" "${installer}:(OI)(CI)RX" /Q | Out-Null
& icacls.exe $KeysDir /grant:r "${runAs}:(OI)(CI)M" /Q | Out-Null
& icacls.exe $LogsDir /grant:r "${runAs}:(OI)(CI)M" /Q | Out-Null
$sqlServiceAccount = (Invoke-Sql -SqlInstance $SqlInstance -Query "SELECT TOP (1) service_account FROM sys.dm_server_services WHERE servicename LIKE N'SQL Server (%'")[0].service_account
& icacls.exe $BackupsDir /grant:r "${sqlServiceAccount}:(OI)(CI)M" /Q | Out-Null
if (Test-Path $PasswordFile) { & icacls.exe $PasswordFile /inheritance:r /grant:r "${admins}:F" "${system}:F" /Q | Out-Null }
if ($LASTEXITCODE -ne 0) { Stop-WithError 'Setting folder permissions failed.' }
Write-Done "$runAs may read the app and config, and write keys and logs; SQL Server ($sqlServiceAccount) may write backups."

# The app reads the certificate's private key.
$rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
$keyName = if ($rsa -is [Security.Cryptography.RSACng]) { $rsa.Key.UniqueName } else { $rsa.CspKeyContainerInfo.UniqueKeyContainerName }
$keyFile = @((Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$keyName"), (Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$keyName")) |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $keyFile) { Stop-WithError "Can't find the certificate's private key file ($keyName)." }
& icacls.exe $keyFile /grant "${runAs}:R" /Q | Out-Null

# ---------------------------------------------------------------- SQL login for the app
if ($mode -eq 'Service') {
    Write-Step "SQL login for $ServiceAccount (read, write and execute only)"
    $db = "[$DatabaseName]"
    Invoke-Sql -SqlInstance $SqlInstance -Query @"
IF SUSER_ID(N'NT SERVICE\HRPayroll') IS NULL CREATE LOGIN [NT SERVICE\HRPayroll] FROM WINDOWS WITH DEFAULT_DATABASE = $db;
"@ | Out-Null
    Invoke-Sql -SqlInstance $SqlInstance -Database $DatabaseName -Query @"
IF USER_ID(N'NT SERVICE\HRPayroll') IS NULL CREATE USER [NT SERVICE\HRPayroll] FOR LOGIN [NT SERVICE\HRPayroll];
ALTER ROLE db_datareader ADD MEMBER [NT SERVICE\HRPayroll];
ALTER ROLE db_datawriter ADD MEMBER [NT SERVICE\HRPayroll];
GRANT EXECUTE TO [NT SERVICE\HRPayroll];
GRANT UPDATE ON OBJECT::dbo.PersonCodeSequence TO [NT SERVICE\HRPayroll];
"@ | Out-Null
    Write-Done 'db_datareader, db_datawriter, EXECUTE (and the person-code sequence). No schema changes, no backups, no admin rights.'
}

# ---------------------------------------------------------------- first Admin
$adminCount = (Invoke-Sql -SqlInstance $SqlInstance -Database $DatabaseName -Query @"
SELECT COUNT(*) AS Admins FROM dbo.AspNetUserRoles ur JOIN dbo.AspNetRoles r ON r.Id = ur.RoleId WHERE r.Name = N'Admin'
"@)[0].Admins
if ($adminCount -eq 0) {
    Write-Step 'Create the first Admin account'
    Write-Note 'The password must have at least 10 characters with upper case, lower case and a digit.'
    Write-Note 'It is used once to create the account and is never written to disk.'
    do { $email = (Read-Host '    Admin email').Trim() } until ($email -match '^[^@\s]+@[^@\s]+\.[^@\s]+$')
    do { $fullName = (Read-Host '    Full name').Trim() } until ($fullName.Length -gt 0)
    while ($true) {
        $first = Read-Host '    Password' -AsSecureString
        $second = Read-Host '    Password again' -AsSecureString
        $plain = ConvertFrom-SecureStringToPlain $first
        $again = ConvertFrom-SecureStringToPlain $second
        $ok = ($plain -ceq $again) -and $plain.Length -ge 10 -and $plain -cmatch '[A-Z]' -and $plain -cmatch '[a-z]' -and $plain -match '[0-9]'
        $again = $null
        if ($ok) { break }
        $plain = $null
        Write-Warning2 'The passwords differ or are too weak. Try again.'
    }

    # Passed once through this process's environment to the child process; removed straight after.
    try {
        $env:Seed__Admin__Email = $email
        $env:Seed__Admin__FullName = $fullName
        $env:Seed__Admin__Password = $plain
        $env:ASPNETCORE_ENVIRONMENT = 'Production'
        Push-Location $AppDir # the app finds appsettings.json, views and wwwroot in its working folder
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
else {
    Write-Step 'Admin account'
    Write-Done 'An Admin already exists; nothing to do.'
}

# ---------------------------------------------------------------- shortcuts
$urlShortcut = Join-Path $desktop 'HR Payroll.url'
[IO.File]::WriteAllText($urlShortcut, "[InternetShortcut]`r`nURL=https://localhost:$Port/`r`n", [Text.Encoding]::ASCII)
Write-Step 'Desktop shortcut'
Write-Done "$urlShortcut -> https://localhost:$Port"

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
    Write-Warning2 'No nightly backup registered. Run C:\HRPayroll\tools\register-backup-task.ps1 later; until then the dashboard warns that no backup has run.'
}

# ---------------------------------------------------------------- start
Write-Step 'Starting HR Payroll'
if (-not (Start-App -Port $Port)) {
    Stop-WithError "HR Payroll didn't report Healthy within 90 seconds. Check the newest file in $LogsDir and the Windows Event Log (Application)."
}
Write-Done "https://localhost:$Port/health answers Healthy."

Write-Host ''
Write-Host 'HR Payroll is installed.' -ForegroundColor Green
Write-Host "  Open:   https://localhost:$Port  (desktop shortcut 'HR Payroll')"
Write-Host '  First sign-in: change the password, then set up two-factor sign-in with an authenticator app.'
Write-Host '                 Keep the 10 recovery codes somewhere safe.'
Write-Host '  Encrypted off-PC backup copy: install 7-Zip, then run C:\HRPayroll\tools\backup.ps1 -SetPassword -SecondCopyPath <folder>.'
Write-Host ''
