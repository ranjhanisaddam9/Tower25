# Shared helpers for the HR Payroll server scripts (Windows PowerShell 5.1, IIS). Dot-sourced; not run on its own.
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# HRPAYROLL_ROOT lets a test install live elsewhere; servers use C:\HRPayroll.
$script:Root            = if ($env:HRPAYROLL_ROOT) { $env:HRPAYROLL_ROOT } else { 'C:\HRPayroll' }
$script:AppDir          = Join-Path $Root 'app'
$script:PreviousAppDir  = Join-Path $Root 'app.previous'
$script:ToolsDir        = Join-Path $Root 'tools'
$script:ConfigDir       = Join-Path $Root 'config'
$script:KeysDir         = Join-Path $Root 'keys'
$script:LogsDir         = Join-Path $Root 'logs'
$script:BackupsDir      = Join-Path $Root 'backups'
$script:InstallFile     = Join-Path $ConfigDir 'install.json'
$script:AppConfigFile   = Join-Path $ConfigDir 'appsettings.Production.json'
$script:PasswordFile    = Join-Path $ConfigDir 'backup-password.bin'
$script:StatusFile      = Join-Path $BackupsDir 'status.json'
$script:SiteName        = 'HRPayroll'
$script:AppPoolName     = 'HRPayroll'
$script:AppPoolIdentity = 'IIS APPPOOL\HRPayroll'
$script:TaskName        = 'HRPayroll Nightly Backup'
$script:FirewallGroup   = 'HR Payroll'
$script:TempCertName    = 'HR Payroll temporary certificate'
$script:Exe             = Join-Path $AppDir 'HR.Web.exe'
$script:AppCmd          = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'

# Well-known SIDs, so the scripts work on any Windows display language.
$script:SidAdministrators = 'S-1-5-32-544'
$script:SidSystem         = 'S-1-5-18'

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Done([string]$Message) { Write-Host "    $Message" -ForegroundColor Green }
function Write-Note([string]$Message) { Write-Host "    $Message" }
function Write-Warning2([string]$Message) { Write-Host "WARNING: $Message" -ForegroundColor Yellow }

function Stop-WithError([string]$Message) {
    Write-Host ''
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 1
}

function Assert-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Stop-WithError 'Run this script from PowerShell opened with "Run as administrator".'
    }
}

function Assert-Windows {
    if ([Environment]::OSVersion.Platform -ne 'Win32NT' -or [Environment]::OSVersion.Version.Major -lt 10) {
        Stop-WithError 'HR Payroll needs Windows Server 2016 or later (or Windows 10/11).'
    }
    if (-not [Environment]::Is64BitOperatingSystem) { Stop-WithError 'HR Payroll needs 64-bit Windows.' }
    if ($PSVersionTable.PSVersion -lt [Version]'5.1') {
        Stop-WithError "Windows PowerShell 5.1 or later is needed (this is $($PSVersionTable.PSVersion))."
    }
}

function Assert-DatabaseName([string]$Name) {
    if ($Name -notmatch '^[A-Za-z][A-Za-z0-9_]{0,63}$') {
        Stop-WithError "Database name '$Name' is not allowed: use letters, digits and underscores, starting with a letter."
    }
}

function Assert-HostName([string]$Name) {
    if ($Name -notmatch '^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$') {
        Stop-WithError "'$Name' is not a valid host name (for example hr.example.com)."
    }
}

function Get-ConnectionString([string]$SqlInstance, [string]$Database) {
    "Server=$SqlInstance;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
}

# Runs one T-SQL batch with Windows authentication; returns the rows. Values go in -Parameters, never in the text.
function Invoke-Sql {
    param(
        [Parameter(Mandatory)] [string]$SqlInstance,
        [string]$Database = 'master',
        [Parameter(Mandatory)] [string]$Query,
        [hashtable]$Parameters = @{},
        [int]$TimeoutSeconds = 600
    )
    $connection = New-Object System.Data.SqlClient.SqlConnection("Server=$SqlInstance;Database=$Database;Integrated Security=True;Connect Timeout=15")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $Query
        $command.CommandTimeout = $TimeoutSeconds
        foreach ($key in $Parameters.Keys) { [void]$command.Parameters.AddWithValue("@$key", $Parameters[$key]) }
        $table = New-Object System.Data.DataTable
        $reader = $command.ExecuteReader()
        $table.Load($reader)
        $reader.Close()
        return , $table.Rows
    }
    finally {
        $connection.Dispose()
    }
}

function Get-InstallSettings {
    if (-not (Test-Path $InstallFile)) {
        Stop-WithError "HR Payroll is not installed on this server ($InstallFile not found). Run setup.ps1 first."
    }
    Get-Content -Raw -Path $InstallFile | ConvertFrom-Json
}

function Save-InstallSettings($Settings) {
    [IO.File]::WriteAllText($InstallFile, ($Settings | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
}

function Get-SettingOrDefault($Settings, [string]$Name, $Default) {
    if ($null -ne $Settings -and $Settings.PSObject.Properties.Name -contains $Name -and $null -ne $Settings.$Name) { return $Settings.$Name }
    return $Default
}

function Set-Setting($Settings, [string]$Name, $Value) {
    if ($Settings.PSObject.Properties.Name -contains $Name) { $Settings.$Name = $Value }
    else { $Settings | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
}

# GET https://<host>:<port><path> on this server (resolved to 127.0.0.1, so it works before public DNS points here and
# without hair-pinning through the router). The certificate isn't checked here: this tests the app, not TLS.
function Invoke-LocalHttps([string]$HostName, [int]$Port, [string]$Path) {
    $output = & curl.exe -sk --max-time 10 --resolve "${HostName}:${Port}:127.0.0.1" -w "`n%{http_code}" "https://${HostName}:${Port}$Path"
    if ($LASTEXITCODE -ne 0 -or -not $output) { return [pscustomobject]@{ Status = 0; Body = '' } }
    $lines = @($output)
    return [pscustomobject]@{ Status = [int]$lines[-1]; Body = (($lines | Select-Object -SkipLast 1) -join "`n").Trim() }
}

# Waits until /health answers "Healthy" (the first request also starts the app). Returns $true or $false.
function Wait-Healthy([string]$HostName, [int]$Port, [int]$TimeoutSeconds = 90) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $response = Invoke-LocalHttps $HostName $Port '/health'
        if ($response.Status -eq 200 -and $response.Body -eq 'Healthy') { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

# app_offline.htm makes the ASP.NET Core Module stop the app and answer every request with this page (503).
function Enter-Maintenance {
    $html = '<!doctype html><html lang="en"><head><meta charset="utf-8"><title>HR Payroll: maintenance</title></head>' +
        '<body><h1>HR Payroll is being updated</h1><p>Please try again in a few minutes.</p></body></html>'
    [IO.File]::WriteAllText((Join-Path $AppDir 'app_offline.htm'), $html, (New-Object Text.UTF8Encoding($false)))
    Start-Sleep -Seconds 5 # the module needs a moment to shut the app down and release its files
}

function Exit-Maintenance {
    $offline = Join-Path $AppDir 'app_offline.htm'
    if (Test-Path $offline) { Remove-Item $offline -Force }
}

# Copies a folder's contents, removing files that are no longer in the source (except app_offline.htm).
function Copy-Mirror([string]$Source, [string]$Destination) {
    & robocopy.exe $Source $Destination /MIR /XF app_offline.htm /R:5 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying $Source to $Destination failed (robocopy exit code $LASTEXITCODE)." }
    $global:LASTEXITCODE = 0
}

function Invoke-AppCmd {
    $output = & $AppCmd @args
    if ($LASTEXITCODE -ne 0) { throw "appcmd $($args -join ' ') failed: $output" }
    return $output
}

function Find-SevenZip {
    foreach ($candidate in @((Join-Path $env:ProgramFiles '7-Zip\7z.exe'), (Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe'))) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    $command = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    return $null
}

function Read-BackupPassword {
    if (-not (Test-Path $PasswordFile)) { return $null }
    Add-Type -AssemblyName System.Security
    $bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($PasswordFile), $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    try { return [Text.Encoding]::UTF8.GetString($bytes) } finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}

function ConvertFrom-SecureStringToPlain([Security.SecureString]$Secure) {
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

# Copies the scripts and documents that ship in the package to C:\HRPayroll\tools and removes the "downloaded from the
# internet" mark, so the nightly task can run them under the RemoteSigned execution policy.
function Install-Tools([string]$Package) {
    foreach ($file in Get-ChildItem -Path $Package -File | Where-Object { $_.Extension -in '.ps1', '.md', '.exe', '.txt' }) {
        Copy-Item -Path $file.FullName -Destination $ToolsDir -Force
    }
    if (Test-Path (Join-Path $Package 'licences')) { Copy-Mirror (Join-Path $Package 'licences') (Join-Path $ToolsDir 'licences') }
    Get-ChildItem $ToolsDir -File -Recurse | Unblock-File
}
