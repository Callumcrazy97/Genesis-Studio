<#
.SYNOPSIS
    Compiles Installer/GenesisStudio.iss into Dist/GenesisStudio-Setup.exe.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RepositoryRoot,
    [string]$PublishDirectory,
    # An install drill compiles a trial installer under its own identity and file name, so it
    # can be installed and removed without touching a real installation.
    [string]$TrialName
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot.TrimEnd('\'))

function Find-Iscc {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LocalAppData 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $candidate
        }
    }

    $command = Get-Command iscc -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    return $null
}

if ([string]::IsNullOrWhiteSpace($PublishDirectory)) { $PublishDirectory = Join-Path $RepositoryRoot 'Genesis Application' }
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$publishExe = Join-Path $PublishDirectory 'Genesis Application.exe'
if (-not (Test-Path -LiteralPath $publishExe -PathType Leaf)) {
    throw "Published Studio is missing: $publishExe. Build.bat must publish before compiling the installer."
}

$iss = Join-Path $RepositoryRoot 'Installer\GenesisStudio.iss'
if (-not (Test-Path -LiteralPath $iss -PathType Leaf)) {
    throw "Installer script missing: $iss"
}

$wizardSide = Join-Path $RepositoryRoot 'Installer\art\wizard-side.bmp'
$wizardSmall = Join-Path $RepositoryRoot 'Installer\art\wizard-small.bmp'
$redist = Join-Path $RepositoryRoot 'Installer\redist\VC_redist.x64.exe'
foreach ($required in @($wizardSide, $wizardSmall, $redist)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Installer input missing: $required"
    }
}

$versionPath = Join-Path $RepositoryRoot 'Installer\AppVersion.txt'
$version = (Get-Content -LiteralPath $versionPath -TotalCount 1).Trim()
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Installer/AppVersion.txt is empty."
}

$iscc = Find-Iscc
if ([string]::IsNullOrWhiteSpace($iscc)) {
    throw "Inno Setup 6 (ISCC.exe) was not found. Run DeveloperRequirementsInstaller.ps1, then open a new terminal and rerun Build.bat --installer."
}

$dist = Join-Path $RepositoryRoot 'Dist'
[void][System.IO.Directory]::CreateDirectory($dist)

$defines = @("/DMyAppVersion=$version", "/DPublishDir=$PublishDirectory")
$baseName = 'GenesisStudio-Setup'
if (-not [string]::IsNullOrWhiteSpace($TrialName)) {
    $baseName = "GenesisStudio-Setup-$TrialName"
    $defines += @('/DMyAppId={{7C0D2B9E-51A4-4E0B-9A3F-2D6E8B1C4F70}', "/DMyAppName=Genesis Studio $TrialName", "/DMyOutputBaseFilename=$baseName")
}

# Signing is set up by GENESIS_SIGN_COMMAND (see BuildTools/SignFiles.ps1): the same command,
# with $f where the file goes, signs the setup program and its uninstaller.
if (-not [string]::IsNullOrWhiteSpace($env:GENESIS_SIGN_COMMAND)) {
    $defines += @('/DSignInstaller', "/Sgenesis=$($env:GENESIS_SIGN_COMMAND)")
    Write-Host 'The installer will be signed.'
}

Write-Host "Compiling Genesis Studio Setup $version with $iscc"
& $iscc @defines $iss
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe failed with exit code $LASTEXITCODE."
}

$setup = Join-Path $dist "$baseName.exe"
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
    throw "Inno Setup reported success but '$setup' was not created."
}

Write-Host "Installer written to $setup"
