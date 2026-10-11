<#
.SYNOPSIS
    Restores an HR Payroll backup (.bak, or an encrypted .bak.7z copy). Run as Administrator. See restore.md.

.PARAMETER BackupFile
    The backup to restore, e.g. C:\HRPayroll\backups\daily\HRPayroll_20261011_230000.bak or a .bak.7z copy.
.PARAMETER AsDatabase
    Restore into this database name instead of the live one (for checking a backup). The live database is untouched.
.PARAMETER Replace
    Overwrite the live database. HR Payroll is stopped during the restore and started again afterwards.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$BackupFile,
    [string]$AsDatabase,
    [switch]$Replace
)

. (Join-Path $PSScriptRoot 'common.ps1')

Assert-Administrator
$settings = Get-InstallSettings
$live = $settings.DatabaseName
$target = if ($AsDatabase) { $AsDatabase } else { $live }
Assert-DatabaseName $target
if (-not (Test-Path $BackupFile)) { Stop-WithError "Backup file not found: $BackupFile" }
if ($target -eq $live -and -not $Replace) { Stop-WithError "Restoring over the live database [$live] needs -Replace (or use -AsDatabase <name> to restore a copy)." }

$work = $null
$bak = (Resolve-Path $BackupFile).Path
if ($bak.EndsWith('.7z', [StringComparison]::OrdinalIgnoreCase)) {
    $sevenZip = Find-SevenZip
    if (-not $sevenZip) { Stop-WithError 'Opening a .7z copy needs 7-Zip (https://www.7-zip.org).' }
    $password = Read-BackupPassword
    if (-not $password) { $password = ConvertFrom-SecureStringToPlain (Read-Host 'Password of the encrypted copy' -AsSecureString) }
    $work = Join-Path $BackupsDir ("restore-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    & $sevenZip x "-p$password" "-o$work" -y $bak | Out-Null
    $password = $null
    if ($LASTEXITCODE -ne 0) { Remove-Item $work -Recurse -Force; Stop-WithError 'Could not decrypt the copy (wrong password or damaged file).' }
    $bak = (Get-ChildItem $work -Filter *.bak | Select-Object -First 1).FullName
}

try {
    Write-Step "Checking $bak"
    Invoke-Sql -SqlInstance $settings.SqlInstance -Query 'RESTORE VERIFYONLY FROM DISK = @file WITH CHECKSUM' -Parameters @{ file = $bak } | Out-Null
    $files = Invoke-Sql -SqlInstance $settings.SqlInstance -Query 'RESTORE FILELISTONLY FROM DISK = @file' -Parameters @{ file = $bak }
    $paths = (Invoke-Sql -SqlInstance $settings.SqlInstance -Query "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(400)) AS DataPath, CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(400)) AS LogPath")[0]

    $moves = @(); $parameters = @{ file = $bak }; $i = 0
    foreach ($f in $files) {
        $folder = if ($f.Type -eq 'L') { $paths.LogPath } else { $paths.DataPath }
        $suffix = if ($f.Type -eq 'L') { '_log.ldf' } elseif ($i -eq 0) { '.mdf' } else { "_$i.ndf" }
        $parameters["logical$i"] = $f.LogicalName
        $parameters["physical$i"] = Join-Path $folder "$target$suffix"
        $moves += "MOVE @logical$i TO @physical$i"
        $i++
    }

    if ($target -eq $live) {
        Write-Step 'Stopping HR Payroll'
        Stop-App
    }
    Write-Step "Restoring into [$target]"
    $sql = "RESTORE DATABASE [$target] FROM DISK = @file WITH CHECKSUM, RECOVERY, REPLACE, " + ($moves -join ', ')
    if ($target -eq $live) { $sql = "IF DB_ID(N'$target') IS NOT NULL ALTER DATABASE [$target] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; $sql; ALTER DATABASE [$target] SET MULTI_USER;" }
    Invoke-Sql -SqlInstance $settings.SqlInstance -Query $sql -Parameters $parameters -TimeoutSeconds 3600 | Out-Null
    Write-Done "Restored $([IO.Path]::GetFileName($BackupFile)) into [$target]."

    if ($target -eq $live) {
        Write-Step 'Starting HR Payroll'
        if (Start-App -Port ([int]$settings.Port)) { Write-Done 'Running and healthy.' }
        else { Write-Warning2 'HR Payroll did not report Healthy; check the newest log in C:\HRPayroll\logs.' }
    }
}
finally {
    if ($work -and (Test-Path $work)) { Remove-Item $work -Recurse -Force }
}
