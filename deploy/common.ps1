# Shared helpers for the HR Payroll release scripts (Windows PowerShell 5.1). Dot-sourced; not run on its own.
Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$script:Root            = 'C:\HRPayroll'
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
$script:ServiceName     = 'HRPayroll'
$script:ServiceAccount  = 'NT SERVICE\HRPayroll'
$script:TaskName        = 'HRPayroll Nightly Backup'
$script:CertFriendlyName = 'HR Payroll (localhost)'
$script:Exe             = Join-Path $AppDir 'HR.Web.exe'

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
        Stop-WithError 'HR Payroll needs Windows 10 or Windows 11.'
    }
    if (-not [Environment]::Is64BitOperatingSystem) {
        Stop-WithError 'HR Payroll needs 64-bit Windows.'
    }
    if ($PSVersionTable.PSVersion -lt [Version]'5.1') {
        Stop-WithError "Windows PowerShell 5.1 or later is needed (this is $($PSVersionTable.PSVersion))."
    }
}

function Assert-DatabaseName([string]$Name) {
    if ($Name -notmatch '^[A-Za-z][A-Za-z0-9_]{0,63}$') {
        Stop-WithError "Database name '$Name' is not allowed: use letters, digits and underscores, starting with a letter."
    }
}

function Get-ConnectionString([string]$SqlInstance, [string]$Database) {
    "Server=$SqlInstance;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
}

# Runs one T-SQL batch with Windows authentication; returns the rows (DataRow objects). Values go in -Parameters, never in the text.
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
        Stop-WithError "HR Payroll is not installed on this PC ($InstallFile not found). Run setup.ps1 first."
    }
    Get-Content -Raw -Path $InstallFile | ConvertFrom-Json
}

function Save-InstallSettings($Settings) {
    $json = $Settings | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($InstallFile, $json, (New-Object Text.UTF8Encoding($false)))
}

function Get-SettingOrDefault($Settings, [string]$Name, $Default) {
    if ($null -ne $Settings -and $Settings.PSObject.Properties.Name -contains $Name -and $null -ne $Settings.$Name) { return $Settings.$Name }
    return $Default
}

# Waits until https://localhost:<port>/health answers "Healthy". Returns $true or $false.
function Wait-Healthy([int]$Port, [int]$TimeoutSeconds = 90) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri "https://localhost:$Port/health" -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -eq 200 -and $response.Content.Trim() -eq 'Healthy') { return $true }
        }
        catch { }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Get-InstallMode {
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { 'Service' } else { 'Console' }
}

# Stops the app however it runs: the service, or console copies started from the "Start HR Payroll" shortcut.
function Stop-App {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    foreach ($folder in @($AppDir, $PreviousAppDir)) {
        Get-Process -Name 'HR.Web' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($folder + '\', [StringComparison]::OrdinalIgnoreCase) } |
            Stop-Process -Force
    }
    Start-Sleep -Seconds 2
}

function Start-App([int]$Port) {
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Start-Service -Name $ServiceName
    }
    else {
        $shortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Start HR Payroll.lnk'
        # Through Explorer, so the app runs as the signed-in user without Administrator rights.
        Start-Process -FilePath explorer.exe -ArgumentList "`"$shortcut`""
    }
    return (Wait-Healthy -Port $Port)
}

# Copies a folder's contents, removing files that are no longer in the source. robocopy exit codes below 8 mean success.
function Copy-Mirror([string]$Source, [string]$Destination) {
    & robocopy.exe $Source $Destination /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { Stop-WithError "Copying $Source to $Destination failed (robocopy exit code $LASTEXITCODE)." }
    $global:LASTEXITCODE = 0
}

function Find-SevenZip {
    $candidates = @(
        (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
        (Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe')
    )
    foreach ($candidate in $candidates) { if ($candidate -and (Test-Path $candidate)) { return $candidate } }
    $command = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    return $null
}

function Read-BackupPassword {
    if (-not (Test-Path $PasswordFile)) { return $null }
    Add-Type -AssemblyName System.Security
    $protected = [IO.File]::ReadAllBytes($PasswordFile)
    $bytes = [Security.Cryptography.ProtectedData]::Unprotect($protected, $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    try { return [Text.Encoding]::UTF8.GetString($bytes) } finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}

function ConvertFrom-SecureStringToPlain([Security.SecureString]$Secure) {
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}
