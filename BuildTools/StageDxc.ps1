param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$PackageVersion = '1.9.2607.13'
)

$ErrorActionPreference = 'Stop'
$packageId = 'microsoft.direct3d.dxc'

function Resolve-NuGetRoot {
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        return [System.IO.Path]::GetFullPath($env:NUGET_PACKAGES)
    }

    if ([string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        throw 'USERPROFILE is not defined and NUGET_PACKAGES was not supplied.'
    }

    return Join-Path $env:USERPROFILE '.nuget\packages'
}

function Find-X64File([string]$Root, [string]$Name) {
    $all = @(Get-ChildItem -Path $Root -Filter $Name -File -Recurse)
    if ($all.Count -eq 0) {
        throw "Microsoft.Direct3D.DXC $PackageVersion does not contain $Name."
    }

    $x64 = @($all | Where-Object {
        $_.FullName -match '[\\/](x64|win-x64)[\\/]' -and $_.FullName -notmatch '[\\/]arm64[\\/]'
    })

    $pool = if ($x64.Count -gt 0) { $x64 } else { $all }
    return $pool | Sort-Object @{ Expression = { $_.FullName.Length } }, FullName | Select-Object -First 1
}

function Get-Sha256([string]$Path) {
    # Get-FileHash is absent on a few legacy/restricted Windows PowerShell hosts used to launch
    # Build.bat. The framework implementation is available everywhere Genesis can build.
    $command = Get-Command Get-FileHash -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    }

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $bytes = $algorithm.ComputeHash($stream)
        return ([System.BitConverter]::ToString($bytes)).Replace('-', '')
    }
    finally {
        $stream.Dispose()
        $algorithm.Dispose()
    }
}

$nugetRoot = Resolve-NuGetRoot
$packageRoot = Join-Path (Join-Path $nugetRoot $packageId) $PackageVersion
if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
    throw "Pinned DXC package not restored: $packageRoot"
}

$dxc = Find-X64File $packageRoot 'dxc.exe'
$compiler = Find-X64File $packageRoot 'dxcompiler.dll'
$dxil = Find-X64File $packageRoot 'dxil.dll'

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $OutputDirectory -Force -ErrorAction SilentlyContinue | Remove-Item -Force -Recurse

$files = @($dxc, $compiler, $dxil)
foreach ($file in $files) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $OutputDirectory $file.Name) -Force
}

foreach ($licenseName in @('LICENSE-LLVM.txt', 'LICENSE-MIT.txt')) {
    $license = Get-ChildItem -Path $packageRoot -Filter $licenseName -File -Recurse | Select-Object -First 1
    if ($null -ne $license) {
        Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $OutputDirectory $licenseName) -Force
    }
}

$manifestPath = Join-Path $OutputDirectory 'toolchain.manifest'
$dxcVersion = (Get-Item -LiteralPath (Join-Path $OutputDirectory 'dxc.exe')).VersionInfo.FileVersion
$lines = @(
    'Package=Microsoft.Direct3D.DXC',
    "Version=$PackageVersion",
    'Architecture=win-x64',
    "DxcFileVersion=$dxcVersion"
)

foreach ($name in @('dxc.exe', 'dxcompiler.dll', 'dxil.dll')) {
    $hash = Get-Sha256 (Join-Path $OutputDirectory $name)
    $lines += "$name.sha256=$hash"
}

[System.IO.File]::WriteAllLines($manifestPath, $lines, [System.Text.UTF8Encoding]::new($false))

Write-Host "Bundled DXC $PackageVersion from $($dxc.Directory.FullName)"
Write-Host "Staged to $OutputDirectory"
exit 0
