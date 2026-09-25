<#
.SYNOPSIS
    Downloads the pinned Visual C++ x64 redistributable used by the Genesis Studio installer.

.DESCRIPTION
    End users never run this script. Build.bat already stages vcruntime140.dll next to the
    published Studio and Player executables. Build.bat --installer still calls this so Setup.exe
    can additionally install the system-wide runtime without requiring winget on the user's machine.

    The URL is Microsoft's content-addressed Visual Studio CDN path for VC_redist.x64.exe
    14.51.36247.0. The SHA-256 is checked after every download. If Microsoft publishes a newer
    package, update both -Url and -ExpectedSha256 together after verifying the new file.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$DestinationDirectory,

    [string]$Url = 'https://download.visualstudio.microsoft.com/download/pr/ebdab8e5-1d7b-4d9f-a11b-cbb1720c3b12/843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C/VC_redist.x64.exe',

    [string]$ExpectedSha256 = '843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-NormalizedSha256([string]$Value) {
    return ($Value -replace '\s', '').ToUpperInvariant()
}

$expected = Get-NormalizedSha256 $ExpectedSha256
if ($expected.Length -ne 64) {
    throw "ExpectedSha256 must be a 64-character hex SHA-256, not '$ExpectedSha256'."
}

[void][System.IO.Directory]::CreateDirectory($DestinationDirectory)
$destination = Join-Path $DestinationDirectory 'VC_redist.x64.exe'

if (Test-Path -LiteralPath $destination -PathType Leaf) {
    $existing = Get-NormalizedSha256 ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash)
    if ($existing -eq $expected) {
        Write-Host "VC_redist.x64.exe already present and hash-matched."
        return
    }

    Write-Host "Cached VC_redist.x64.exe hash $existing does not match pin $expected; re-downloading."
    Remove-Item -LiteralPath $destination -Force
}

Write-Host "Downloading pinned VC_redist.x64.exe..."
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $Url -OutFile $destination -UseBasicParsing

if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) {
    throw "Download finished but '$destination' was not created."
}

$actual = Get-NormalizedSha256 ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash)
if ($actual -ne $expected) {
    Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
    throw @"
VC_redist.x64.exe hash mismatch.
  expected: $expected
  actual:   $actual
The pin in Installer/EnsureVcRedist.ps1 must be updated together with the Microsoft download URL after verifying the new official package.
"@
}

Write-Host "VC_redist.x64.exe verified ($actual)."
