<#
.SYNOPSIS
    Installs and verifies the Windows developer prerequisites for Genesis Studio.

.DESCRIPTION
    Genesis Studio targets Windows x64 and .NET 10. This script installs the required .NET SDK,
    Git, the x64 Visual C++ runtime, Inno Setup 6 (to compile Build.bat --installer), and the
    Vulkan runtime through winget when missing, and the Vulkan SDK either as a user-local copy
    (`BuildTools/EnsureVulkanSdk.ps1`, no Administrator) or via winget. It then restores the canonical
    solution so NuGet supplies DXC, SDL3, WebGPU and the other pinned native/runtime dependencies
    used by Build.bat.

    End users never run this script. They run Dist\GenesisStudio-Setup.exe, which copies the
    published app and installs only the Visual C++ runtime.

    GPU drivers cannot be installed safely without knowing the device vendor. The script detects
    the Vulkan loader and installed display adapters and prints an actionable warning when the
    machine still needs a current Intel, AMD or NVIDIA driver.

.PARAMETER CheckOnly
    Detect and report requirements without installing packages or restoring the solution.

.PARAMETER SkipVulkanSdk
    Do not require/install the Vulkan Runtime and SDK. DX11/DX12 development still works, but the
    Vulkan validation workflow will not be available.

.PARAMETER SkipRestore
    Do not run dotnet restore after prerequisite verification.

.PARAMETER RunBuildCheck
    After restore, run Build.bat --check (13 workflows, DX11/DX12 smokes and published-app smoke).

.EXAMPLE
    .\DeveloperRequirementsInstaller.ps1

.EXAMPLE
    .\DeveloperRequirementsInstaller.ps1 -RunBuildCheck

.EXAMPLE
    .\DeveloperRequirementsInstaller.ps1 -CheckOnly
#>
[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [switch]$SkipVulkanSdk,
    [switch]$SkipRestore,
    [switch]$RunBuildCheck
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:Warnings = [System.Collections.Generic.List[string]]::new()

function Write-Status([string]$Kind, [string]$Message, [ConsoleColor]$Colour) {
    Write-Host ("[{0}] {1}" -f $Kind, $Message) -ForegroundColor $Colour
}

function Refresh-ProcessPath {
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    # Preserve task-local PATH entries (portable tools/CI shims) while adding anything an installer
    # just registered. Case-insensitive de-duplication keeps repeated runs tidy.
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $entries = [System.Collections.Generic.List[string]]::new()
    foreach ($pathSet in @($env:Path, $machinePath, $userPath)) {
        foreach ($entry in @($pathSet -split ';')) {
            $trimmed = $entry.Trim()
            if (-not [string]::IsNullOrWhiteSpace($trimmed) -and $seen.Add($trimmed)) {
                $entries.Add($trimmed)
            }
        }
    }
    $env:Path = $entries -join ';'

    foreach ($name in @('VULKAN_SDK', 'VK_SDK_PATH', 'VK_ADD_LAYER_PATH')) {
        $value = [Environment]::GetEnvironmentVariable($name, 'Machine')
        if ([string]::IsNullOrWhiteSpace($value)) {
            $value = [Environment]::GetEnvironmentVariable($name, 'User')
        }
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            [Environment]::SetEnvironmentVariable($name, $value, 'Process')
        }
    }
}

function Test-DotNet10Sdk {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    $sdks = @(& dotnet --list-sdks 2>$null)
    return $LASTEXITCODE -eq 0 -and @($sdks | Where-Object { $_ -match '^10\.' }).Count -gt 0
}

function Test-Git { return $null -ne (Get-Command git -ErrorAction SilentlyContinue) }

function Test-InnoSetup {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LocalAppData 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $true
        }
    }
    return $null -ne (Get-Command iscc -ErrorAction SilentlyContinue)
}

function Test-VcRuntimeX64 {
    $keys = @(
        'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64'
    )
    foreach ($key in $keys) {
        if (Test-Path -LiteralPath $key) {
            $installed = (Get-ItemProperty -LiteralPath $key -Name Installed -ErrorAction SilentlyContinue).Installed
            if ($installed -eq 1) { return $true }
        }
    }
    return $false
}

function Test-VulkanRuntime {
    return Test-Path -LiteralPath (Join-Path $env:WINDIR 'System32\vulkan-1.dll') -PathType Leaf
}

function Test-VulkanSdk {
    # System32\vulkaninfo.exe ships with the NVIDIA/AMD runtime and is not the SDK.
    # The Khronos validation layer is what NEXT-122 / Phase 6.2 actually need.
    foreach ($sdk in @(Get-VulkanSdkRoots)) {
        $layer = Join-Path $sdk 'Bin\VkLayer_khronos_validation.dll'
        $spirvVal = Join-Path $sdk 'Bin\spirv-val.exe'
        if ((Test-Path -LiteralPath $layer -PathType Leaf) -or (Test-Path -LiteralPath $spirvVal -PathType Leaf)) {
            return $true
        }
    }

    return $false
}

function Get-VulkanSdkRoots {
    $roots = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($value in @(
        $env:VULKAN_SDK,
        [Environment]::GetEnvironmentVariable('VULKAN_SDK', 'Machine'),
        [Environment]::GetEnvironmentVariable('VULKAN_SDK', 'User')
    )) {
        if (-not [string]::IsNullOrWhiteSpace($value) -and $seen.Add($value.TrimEnd('\'))) {
            $roots.Add($value.TrimEnd('\'))
        }
    }

    $local = Join-Path $env:LOCALAPPDATA 'GenesisStudio\VulkanSDK'
    if (Test-Path -LiteralPath $local -PathType Container) {
        foreach ($dir in Get-ChildItem -LiteralPath $local -Directory) {
            if ($seen.Add($dir.FullName)) {
                $roots.Add($dir.FullName)
            }
        }
    }

    if (Test-Path -LiteralPath 'C:\VulkanSDK' -PathType Container) {
        foreach ($dir in Get-ChildItem -LiteralPath 'C:\VulkanSDK' -Directory) {
            if ($seen.Add($dir.FullName)) {
                $roots.Add($dir.FullName)
            }
        }
    }

    return $roots
}

function Install-WingetPackage([string]$Id, [string]$DisplayName) {
    if ($CheckOnly) {
        $script:Failures.Add("$DisplayName is missing.")
        Write-Status 'MISSING' "$DisplayName (check-only mode)" Red
        return
    }

    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "$DisplayName is missing and winget is unavailable. Install Microsoft App Installer, then rerun this script."
    }

    Write-Status 'INSTALL' "$DisplayName ($Id)" Yellow
    & winget install --id $Id --exact --source winget --silent --disable-interactivity `
        --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "winget failed to install $DisplayName (exit code $LASTEXITCODE)."
    }
    Refresh-ProcessPath
}

function Ensure-Requirement(
    [string]$Name,
    [scriptblock]$Test,
    [string]$WingetId
) {
    if (& $Test) {
        Write-Status 'OK' $Name Green
        return
    }

    Install-WingetPackage $WingetId $Name
    if (-not $CheckOnly -and -not (& $Test)) {
        $script:Failures.Add("$Name was installed but is not visible in this process. Restart Windows or open a new terminal and rerun the script.")
        Write-Status 'RECHECK' "$Name is not visible yet; a sign-out/restart may be required." Yellow
    }
}

function Ensure-VulkanSdk {
    $name = 'Vulkan SDK and validation tools'
    if (Test-VulkanSdk) {
        Write-Status 'OK' $name Green
        return
    }

    $localScript = Join-Path $PSScriptRoot 'BuildTools\EnsureVulkanSdk.ps1'
    if (-not $CheckOnly -and (Test-Path -LiteralPath $localScript -PathType Leaf)) {
        Write-Status 'INSTALL' "$name (user-local copy, no Administrator)" Yellow
        try {
            & $localScript
            Refresh-ProcessPath
        } catch {
            $script:Warnings.Add("User-local Vulkan SDK copy failed: $($_.Exception.Message)")
            Write-Status 'RECHECK' 'User-local Vulkan SDK copy failed; trying winget.' Yellow
        }
    }

    if (Test-VulkanSdk) {
        Write-Status 'OK' $name Green
        return
    }

    Ensure-Requirement $name ${function:Test-VulkanSdk} 'KhronosGroup.VulkanSDK'
}

Write-Host '=== Genesis Studio developer requirements ===' -ForegroundColor Cyan
Write-Host "Repository: $PSScriptRoot"

if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') {
    throw 'Genesis Studio development is supported on Windows only.'
}
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'Genesis Studio requires 64-bit Windows.'
}

Refresh-ProcessPath
Ensure-Requirement '.NET 10 SDK (x64)' ${function:Test-DotNet10Sdk} 'Microsoft.DotNet.SDK.10'
Ensure-Requirement 'Git for Windows' ${function:Test-Git} 'Git.Git'
Ensure-Requirement 'Microsoft Visual C++ 2015-2022 Redistributable (x64)' ${function:Test-VcRuntimeX64} 'Microsoft.VCRedist.2015+.x64'
Ensure-Requirement 'Inno Setup 6' ${function:Test-InnoSetup} 'JRSoftware.InnoSetup'

if (-not $SkipVulkanSdk) {
    Ensure-Requirement 'Vulkan Runtime' ${function:Test-VulkanRuntime} 'KhronosGroup.VulkanRT'
    Ensure-VulkanSdk
} else {
    Write-Status 'SKIP' 'Vulkan Runtime/SDK were explicitly skipped.' Yellow
}

$adapters = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) } |
    Select-Object -ExpandProperty Name -Unique)
if ($adapters.Count -gt 0) {
    Write-Status 'GPU' ($adapters -join ', ') Cyan
} else {
    $script:Warnings.Add('No display adapter could be queried. Install the current vendor GPU driver before running hardware-backend tests.')
}

if (-not (Test-VulkanRuntime) -and -not $SkipVulkanSdk) {
    $script:Warnings.Add('The Vulkan loader is still unavailable. Install the current Intel, AMD or NVIDIA graphics driver; the SDK alone is not a GPU driver.')
}

if (Test-DotNet10Sdk) {
    $sdk = @(& dotnet --list-sdks | Where-Object { $_ -match '^10\.' } | Select-Object -Last 1)
    Write-Status 'SDK' ($sdk -join '') Green
}

Write-Status 'INFO' 'DXC 1.9.2607.13, SDL3, WebGPU, Silk.NET, Bepu, SkiaSharp and other libraries are pinned NuGet dependencies restored from the solution.' DarkGray
Write-Status 'INFO' 'Visual Studio is optional; the .NET SDK and Build.bat are sufficient for command-line development.' DarkGray
Write-Status 'INFO' 'Inno Setup is required only to compile Dist\GenesisStudio-Setup.exe via Build.bat --installer. End users run that Setup, not this script.' DarkGray
Write-Status 'INFO' 'Direct3D 11/12 and OpenGL come from Windows plus the installed GPU driver.' DarkGray

if (-not $CheckOnly -and -not $SkipRestore -and $script:Failures.Count -eq 0) {
    $solution = Join-Path $PSScriptRoot 'Genesis.Application.slnx'
    if (-not (Test-Path -LiteralPath $solution -PathType Leaf)) {
        throw "Canonical solution not found: $solution"
    }

    Write-Status 'RESTORE' 'Restoring the canonical Genesis Studio solution and pinned toolchain packages...' Cyan
    & dotnet restore $solution --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
    Write-Status 'OK' 'NuGet restore completed.' Green
}

if ($RunBuildCheck) {
    if ($CheckOnly) { throw '-RunBuildCheck cannot be combined with -CheckOnly.' }
    if ($script:Failures.Count -gt 0) { throw 'Build verification cannot run while requirements are missing.' }
    $build = Join-Path $PSScriptRoot 'Build.bat'
    Write-Status 'VERIFY' 'Running Build.bat --check...' Cyan
    & $build --check
    if ($LASTEXITCODE -ne 0) { throw "Build.bat --check failed with exit code $LASTEXITCODE." }
}

foreach ($warning in $script:Warnings) { Write-Status 'WARNING' $warning Yellow }

if ($script:Failures.Count -gt 0) {
    Write-Host ''
    Write-Status 'FAILED' ("{0} requirement(s) need attention:" -f $script:Failures.Count) Red
    foreach ($failure in $script:Failures) { Write-Host "  - $failure" }
    exit 1
}

Write-Host ''
Write-Status 'READY' 'Genesis Studio developer requirements are installed and verified.' Green
if (-not $RunBuildCheck) {
    Write-Host 'Next: .\Build.bat --check' -ForegroundColor Cyan
}
exit 0
