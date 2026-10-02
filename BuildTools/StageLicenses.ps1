param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$RuntimeConfig
)

$ErrorActionPreference = 'Stop'

# A self-contained package carries the .NET runtime, so it must carry the runtime's licence and
# the notices for the software inside it. They come from the same runtime pack the publish used,
# so the text always matches the files that ship. The project build has already put Genesis's
# own ThirdPartyNotices.txt and the SkiaSharp notices in the Licenses folder.

function Resolve-NuGetRoot {
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        return [System.IO.Path]::GetFullPath($env:NUGET_PACKAGES)
    }

    if ([string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        throw 'USERPROFILE is not defined and NUGET_PACKAGES was not supplied.'
    }

    return Join-Path $env:USERPROFILE '.nuget\packages'
}

if (-not (Test-Path -LiteralPath $RuntimeConfig -PathType Leaf)) {
    throw "Runtime configuration not found: $RuntimeConfig"
}

$config = Get-Content -LiteralPath $RuntimeConfig -Raw | ConvertFrom-Json
$framework = @($config.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }) | Select-Object -First 1
if ($null -eq $framework) {
    throw "$RuntimeConfig does not describe a self-contained .NET runtime."
}

$pack = Join-Path (Join-Path (Resolve-NuGetRoot) 'microsoft.netcore.app.runtime.win-x64') $framework.version
$licenses = Join-Path $OutputDirectory 'Licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null

foreach ($pair in @(@('LICENSE.TXT', 'DotNet-LICENSE.txt'), @('THIRD-PARTY-NOTICES.TXT', 'DotNet-THIRD-PARTY-NOTICES.txt'))) {
    $source = Join-Path $pack $pair[0]
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "The .NET $($framework.version) runtime pack has no $($pair[0]): $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $licenses $pair[1]) -Force
}

foreach ($required in @('ThirdPartyNotices.txt', 'SkiaSharp-THIRD-PARTY-NOTICES.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $licenses $required) -PathType Leaf)) {
        throw "The published package has no Licenses\$required. It comes from Genesis.Shared's project file."
    }
}

Write-Host "Licences for .NET $($framework.version) staged to $licenses"
exit 0
