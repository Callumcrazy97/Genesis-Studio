<#
.SYNOPSIS
    Installs a user-local Khronos Vulkan SDK copy so NEXT-122 can attach
    VK_LAYER_KHRONOS_validation without an Administrator prompt.

.DESCRIPTION
    winget's KhronosGroup.VulkanSDK package elevates and writes the registry. LunarG's installer
    also supports copy_only=1, which copies files only. This script downloads the pinned SDK
    installer and extracts it under %LOCALAPPDATA%\GenesisStudio\VulkanSDK so the Vulkan loader
    can find the validation layer via VK_ADD_LAYER_PATH.

    The layer is a developer/test tool. Do not copy it into Genesis Application\ or Dist\.
#>
[CmdletBinding()]
param(
    [string]$Version = '1.4.357.0',

    [string]$Url = 'https://sdk.lunarg.com/sdk/download/1.4.357.0/windows/vulkansdk-windows-X64-1.4.357.0.exe',

    [string]$ExpectedSha256 = '81F474711E9042F4CD22B31B2F7A8870DB2E428B21586FB43DD80150BE97310D'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-NormalizedSha256([string]$Value) {
    return ($Value -replace '\s', '').ToUpperInvariant()
}

function Test-KhronosLayer([string]$Root) {
    $dll = Join-Path $Root 'Bin\VkLayer_khronos_validation.dll'
    $json = Join-Path $Root 'Bin\VkLayer_khronos_validation.json'
    return (Test-Path -LiteralPath $dll -PathType Leaf) -and (Test-Path -LiteralPath $json -PathType Leaf)
}

$expected = Get-NormalizedSha256 $ExpectedSha256
$root = Join-Path $env:LOCALAPPDATA "GenesisStudio\VulkanSDK\$Version"
if (Test-KhronosLayer $root) {
    Write-Host "Khronos validation layer already present at $root"
} else {
    $cache = Join-Path $env:LOCALAPPDATA 'GenesisStudio\cache'
    [void][System.IO.Directory]::CreateDirectory($cache)
    $installer = Join-Path $cache "vulkansdk-windows-X64-$Version.exe"

    $needDownload = $true
    if (Test-Path -LiteralPath $installer -PathType Leaf) {
        $existing = Get-NormalizedSha256 ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash)
        if ($existing -eq $expected) {
            $needDownload = $false
            Write-Host "Using cached SDK installer ($existing)."
        } else {
            Write-Host "Cached installer hash $existing does not match pin; re-downloading."
            Remove-Item -LiteralPath $installer -Force
        }
    }

    if ($needDownload) {
        Write-Host "Downloading Vulkan SDK $Version (copy-only user install, no Administrator)..."
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $Url -OutFile $installer -UseBasicParsing
        $actual = Get-NormalizedSha256 ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash)
        if ($actual -ne $expected) {
            Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue
            throw "Vulkan SDK installer hash mismatch. expected $expected actual $actual"
        }
    }

    [void][System.IO.Directory]::CreateDirectory($root)
    Write-Host "Installing Vulkan SDK files to $root (copy_only=1)..."
    $process = Start-Process -FilePath $installer -ArgumentList @(
        '--root', $root,
        '--accept-licenses',
        '--default-answer',
        '--confirm-command',
        'install',
        'copy_only=1'
    ) -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0) {
        throw "Vulkan SDK installer failed with exit code $($process.ExitCode)."
    }

    if (-not (Test-KhronosLayer $root)) {
        throw "Vulkan SDK copy finished but VkLayer_khronos_validation.dll was not found under $root\Bin."
    }
}

$bin = Join-Path $root 'Bin'
[Environment]::SetEnvironmentVariable('VULKAN_SDK', $root, 'Process')
[Environment]::SetEnvironmentVariable('VULKAN_SDK', $root, 'User')

$add = [Environment]::GetEnvironmentVariable('VK_ADD_LAYER_PATH', 'User')
$parts = @()
if (-not [string]::IsNullOrWhiteSpace($add)) {
    $parts = @($add -split [IO.Path]::PathSeparator | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
if (-not ($parts | Where-Object { $_.TrimEnd('\') -ieq $bin.TrimEnd('\') })) {
    $parts = @($bin) + $parts
}
$joined = $parts -join [IO.Path]::PathSeparator
[Environment]::SetEnvironmentVariable('VK_ADD_LAYER_PATH', $joined, 'Process')
[Environment]::SetEnvironmentVariable('VK_ADD_LAYER_PATH', $joined, 'User')

Write-Host "VULKAN_SDK=$root"
Write-Host "VK_ADD_LAYER_PATH=$joined"
Write-Host "Khronos validation layer is available for NEXT-122."
