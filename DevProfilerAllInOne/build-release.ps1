[CmdletBinding()]
param(
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "DevProfiler\DevProfiler.csproj"
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts $RuntimeIdentifier
$zip = Join-Path $artifacts "DevProfiler-$RuntimeIdentifier.zip"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK was not found. Install the .NET 10 SDK and run this script again."
}

$version = (& dotnet --version).Trim()
if (-not $version.StartsWith("10.")) {
    Write-Warning "The active SDK is $version. This project targets .NET 10."
}

New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

& dotnet restore $project
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }

& dotnet publish $project `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $publish `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=embedded `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Published: $publish"
Write-Host "Packaged:  $zip"
