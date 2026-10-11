<#
.SYNOPSIS
    Backs up the HR Payroll database. The nightly task runs this; you can run it any time (as Administrator).

.DESCRIPTION
    - BACKUP DATABASE ... WITH CHECKSUM, then RESTORE VERIFYONLY.
    - Keeps 14 daily and 12 monthly backups in C:\HRPayroll\backups.
    - With 7-Zip installed and a second location set: also an encrypted copy (AES-256, encrypted file names) there.
    - Logs every run to C:\HRPayroll\backups\backup.log and writes status.json, which the Admin dashboard reads.

.PARAMETER SetPassword
    Sets the password for the encrypted copy. It is stored DPAPI-encrypted for this PC, readable only by
    Administrators and the backup task. Keep it somewhere safe too: without it the encrypted copies can't be opened.
.PARAMETER SecondCopyPath
    Sets (and remembers) the folder for the encrypted copy, e.g. D:\Backups\HRPayroll or a OneDrive folder.
    Use "" to stop making the second copy.
#>
[CmdletBinding()]
param(
    [switch]$SetPassword,
    [string]$SecondCopyPath
)

. (Join-Path $PSScriptRoot 'common.ps1')

$settings = Get-InstallSettings
$database = $settings.DatabaseName
Assert-DatabaseName $database
$logFile = Join-Path $BackupsDir 'backup.log'

if ($SetPassword -or $PSBoundParameters.ContainsKey('SecondCopyPath')) {
    Assert-Administrator
    if ($PSBoundParameters.ContainsKey('SecondCopyPath')) {
        if ($SecondCopyPath -and -not (Test-Path $SecondCopyPath)) { New-Item -ItemType Directory -Path $SecondCopyPath | Out-Null }
        if ($settings.PSObject.Properties.Name -contains 'BackupSecondCopy') { $settings.BackupSecondCopy = $SecondCopyPath }
        else { $settings | Add-Member -NotePropertyName BackupSecondCopy -NotePropertyValue $SecondCopyPath }
        Save-InstallSettings $settings
        Write-Done $(if ($SecondCopyPath) { "Encrypted copies will go to $SecondCopyPath." } else { 'No second copy will be made.' })
    }
    if ($SetPassword) {
        while ($true) {
            $first = ConvertFrom-SecureStringToPlain (Read-Host 'Password for the encrypted backup copies (12+ characters)' -AsSecureString)
            $second = ConvertFrom-SecureStringToPlain (Read-Host 'Again' -AsSecureString)
            if ($first -ceq $second -and $first.Length -ge 12) { break }
            Write-Warning2 'The passwords differ or are shorter than 12 characters. Try again.'
        }
        Add-Type -AssemblyName System.Security
        $bytes = [Text.Encoding]::UTF8.GetBytes($first)
        $protected = [Security.Cryptography.ProtectedData]::Protect($bytes, $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)
        [Array]::Clear($bytes, 0, $bytes.Length)
        $first = $null; $second = $null
        [IO.File]::WriteAllBytes($PasswordFile, $protected)
        & icacls.exe $PasswordFile /inheritance:r /grant:r "*${SidAdministrators}:F" "*${SidSystem}:F" /Q | Out-Null
        Write-Done "Password saved (DPAPI, this PC only): $PasswordFile"
        Write-Note 'Write the password down and keep it away from this PC: restoring an encrypted copy elsewhere needs it.'
    }
    exit 0
}

function Write-Log([string]$Message) {
    $line = "{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $Message
    Add-Content -Path $logFile -Value $line -Encoding UTF8
    Write-Host $line
}

function Write-Status([bool]$Ok, [string]$Message, [string]$File) {
    $previous = $null
    if (Test-Path $StatusFile) { try { $previous = Get-Content -Raw $StatusFile | ConvertFrom-Json } catch { } }
    $now = [DateTimeOffset]::UtcNow.ToString('o')
    $lastGood = if ($Ok) { $now } elseif ($previous -and $previous.lastGoodUtc) { $previous.lastGoodUtc } else { $null }
    $lastFile = if ($Ok) { $File } elseif ($previous) { $previous.lastFile } else { $null }
    $status = [ordered]@{ lastRunUtc = $now; lastRunOk = $Ok; lastGoodUtc = $lastGood; message = $Message; lastFile = $lastFile }
    $temp = "$StatusFile.tmp"
    [IO.File]::WriteAllText($temp, ($status | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    Move-Item $temp $StatusFile -Force
}

function Remove-OldFiles([string]$Folder, [string]$Filter, [int]$Keep) {
    Get-ChildItem -Path $Folder -Filter $Filter -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -Skip $Keep |
        ForEach-Object { Remove-Item $_.FullName -Force; Write-Log "Removed old backup $($_.Name)" }
}

$warnings = New-Object System.Collections.Generic.List[string]
try {
    $daily = Join-Path $BackupsDir 'daily'
    $monthly = Join-Path $BackupsDir 'monthly'
    foreach ($dir in @($daily, $monthly)) { if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null } }

    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $file = Join-Path $daily "$($database)_$stamp.bak"
    Write-Log "Backup of [$database] started -> $file"

    Invoke-Sql -SqlInstance $settings.SqlInstance -Query "BACKUP DATABASE [$database] TO DISK = @file WITH CHECKSUM, INIT, NAME = @name" `
        -Parameters @{ file = $file; name = "HR Payroll $stamp" } -TimeoutSeconds 3600 | Out-Null
    Invoke-Sql -SqlInstance $settings.SqlInstance -Query 'RESTORE VERIFYONLY FROM DISK = @file WITH CHECKSUM' `
        -Parameters @{ file = $file } -TimeoutSeconds 3600 | Out-Null
    $sizeMb = [Math]::Round((Get-Item $file).Length / 1MB, 1)
    Write-Log "Backup written and verified (CHECKSUM, RESTORE VERIFYONLY): $sizeMb MB"

    # The first backup of each month is also kept as that month's backup.
    $monthFile = Join-Path $monthly ("$($database)_{0:yyyyMM}.bak" -f (Get-Date))
    if (-not (Test-Path $monthFile)) {
        Copy-Item $file $monthFile
        Write-Log "Kept as this month's backup: $monthFile"
    }

    Remove-OldFiles $daily "$($database)_*.bak" 14
    Remove-OldFiles $monthly "$($database)_*.bak" 12

    # Encrypted second copy.
    $second = Get-SettingOrDefault $settings 'BackupSecondCopy' ''
    if ($second) {
        $sevenZip = Find-SevenZip
        $password = Read-BackupPassword
        if (-not $sevenZip) {
            $warnings.Add("7-Zip is not installed: local backup only, no encrypted copy in $second. Install 7-Zip from https://www.7-zip.org.")
        }
        elseif (-not $password) {
            $warnings.Add("No password for the encrypted copy: local backup only. Run backup.ps1 -SetPassword as Administrator.")
        }
        else {
            if (-not (Test-Path $second)) { New-Item -ItemType Directory -Path $second | Out-Null }
            $archive = Join-Path $second ((Split-Path $file -Leaf) + '.7z')
            # -mhe=on encrypts the file names too; AES-256 is 7-Zip's 7z encryption.
            & $sevenZip a -t7z -mx=5 -mhe=on "-p$password" -y $archive $file | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "7-Zip could not create $archive (exit code $LASTEXITCODE)." }
            & $sevenZip t "-p$password" $archive | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "7-Zip could not verify $archive (exit code $LASTEXITCODE)." }
            $password = $null
            Write-Log "Encrypted copy written and tested: $archive"
            Remove-OldFiles $second "$($database)_*.bak.7z" 14
        }
    }
    else {
        $warnings.Add('No second location is set: the only copies are on this PC. Run backup.ps1 -SecondCopyPath <folder> -SetPassword.')
    }

    foreach ($warning in $warnings) { Write-Log "WARNING: $warning" }
    $message = if ($warnings.Count -gt 0) { 'OK with warnings: ' + ($warnings -join ' ') } else { 'OK' }
    Write-Status $true $message $file
    Write-Log 'Backup finished.'
    exit 0
}
catch {
    $reason = $_.Exception.Message
    Write-Log "FAILED: $reason"
    Write-Status $false "Backup failed: $reason" $null
    exit 1
}
