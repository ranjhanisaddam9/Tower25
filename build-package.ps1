<#
.SYNOPSIS
    Builds the portable release package: dist\HRPayroll-v<Version>-win-x64.zip.

.DESCRIPTION
    Self-contained win-x64 Release publish of HR.Web, a self-contained EF Core migration bundle (migrate.exe), the
    deploy\ scripts and documents, and the licences. The target PC needs no .NET SDK or runtime.
    Needs the .NET SDK pinned in global.json and the dotnet-ef tool (see README).
#>
[CmdletBinding()]
param([string]$Version = '1.0.0')

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$repo = $PSScriptRoot
$name = "HRPayroll-v$Version-win-x64"
$dist = Join-Path $repo 'dist'
$stage = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

function Invoke-Checked([string]$What, [scriptblock]$Command) {
    Write-Host "==> $What" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

Invoke-Checked 'Publishing HR.Web (self-contained, win-x64, Release)' {
    dotnet publish (Join-Path $repo 'src\HR.Web\HR.Web.csproj') -c Release -r win-x64 --self-contained true `
        -p:DebugType=none -p:EnvironmentName=Production -o (Join-Path $stage 'app') -warnaserror
}

Invoke-Checked 'Building the migration bundle (migrate.exe)' {
    dotnet ef migrations bundle --self-contained -r win-x64 --configuration Release `
        --project (Join-Path $repo 'src\HR.Infrastructure') --startup-project (Join-Path $repo 'src\HR.Web') `
        -o (Join-Path $stage 'migrate.exe') --force
}

Write-Host '==> Adding scripts, documents and licences' -ForegroundColor Cyan
# Development settings never ship.
Get-ChildItem (Join-Path $stage 'app') -Filter 'appsettings.Development*.json' | Remove-Item -Force
Copy-Item (Join-Path $repo 'deploy\*.ps1') $stage
Copy-Item (Join-Path $repo 'deploy\INSTALL.md'), (Join-Path $repo 'deploy\restore.md') $stage
$licences = New-Item -ItemType Directory -Path (Join-Path $stage 'licences')
Copy-Item (Join-Path $repo 'deploy\THIRD-PARTY-NOTICES.md') $licences
Copy-Item (Join-Path $repo 'src\HR.Infrastructure\Fonts\OFL.txt') $licences
$commit = (& git -C $repo rev-parse --short HEAD).Trim()
$dirty = if (& git -C $repo status --porcelain) { '-dirty' } else { '' }
[IO.File]::WriteAllText((Join-Path $stage 'VERSION.txt'), "$Version ($commit$dirty)", (New-Object Text.UTF8Encoding($false)))

Write-Host "==> Zipping $zip" -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
[IO.File]::WriteAllText("$zip.sha256", "$hash  $name.zip`n", (New-Object Text.UTF8Encoding($false)))

$sizeMb = [Math]::Round((Get-Item $zip).Length / 1MB, 1)
$unzippedMb = [Math]::Round(((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Host ''
Write-Host "Package: $zip" -ForegroundColor Green
Write-Host "  $sizeMb MB zipped, $unzippedMb MB unzipped; SHA-256 $hash"
