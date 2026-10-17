<#
.SYNOPSIS
    Restricts this server's TLS (SCHANNEL) to TLS 1.2 and 1.3. Optional; run once on the server as Administrator, then reboot.

.DESCRIPTION
    Turns off SSL 2.0, SSL 3.0, TLS 1.0 and TLS 1.1 for both server and client use, turns on TLS 1.2 (and TLS 1.3 where
    Windows supports it: Server 2022 and later), and makes .NET Framework apps use strong cryptography.
    This affects EVERY program on the server. Check first that nothing else on it still needs TLS 1.0/1.1 (old SQL
    Server clients, old management tools). Use -WhatIf to see the changes without making them.
    Afterwards check the site with https://www.ssllabs.com/ssltest/ (aim for grade A).
#>
[CmdletBinding(SupportsShouldProcess)]
param()

. (Join-Path $PSScriptRoot 'common.ps1')
Assert-Administrator

$base = 'HKLM:\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols'
$build = [Environment]::OSVersion.Version.Build
$protocols = [ordered]@{
    'SSL 2.0' = $false
    'SSL 3.0' = $false
    'TLS 1.0' = $false
    'TLS 1.1' = $false
    'TLS 1.2' = $true
}
if ($build -ge 20348) { $protocols['TLS 1.3'] = $true } # Windows Server 2022 / Windows 11 and later

foreach ($protocol in $protocols.Keys) {
    foreach ($side in 'Server', 'Client') {
        $key = Join-Path (Join-Path $base $protocol) $side
        $enabled = $protocols[$protocol]
        if ($PSCmdlet.ShouldProcess($key, $(if ($enabled) { 'Enable' } else { 'Disable' }))) {
            if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
            New-ItemProperty -Path $key -Name 'Enabled' -Value ([int]$enabled) -PropertyType DWord -Force | Out-Null
            New-ItemProperty -Path $key -Name 'DisabledByDefault' -Value ([int](-not $enabled)) -PropertyType DWord -Force | Out-Null
        }
    }
}

foreach ($net in 'HKLM:\SOFTWARE\Microsoft\.NETFramework\v4.0.30319', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\.NETFramework\v4.0.30319') {
    if ((Test-Path $net) -and $PSCmdlet.ShouldProcess($net, 'Use strong cryptography and the OS default TLS versions')) {
        New-ItemProperty -Path $net -Name 'SchUseStrongCrypto' -Value 1 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $net -Name 'SystemDefaultTlsVersions' -Value 1 -PropertyType DWord -Force | Out-Null
    }
}

Write-Done "TLS restricted to $(($protocols.Keys | Where-Object { $protocols[$_] }) -join ' and '). Restart the server for it to take effect."
