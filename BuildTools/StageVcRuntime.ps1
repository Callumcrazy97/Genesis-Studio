param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

# App-local Visual C++ x64 CRT. The retained GLFW and shader compiler binaries import
# VCRUNTIME140.dll; Windows looks next to the loading module before System32.
# Staging these files into the published Studio and Player folders keeps a
# self-contained tree from prompting for the VC++ redistributable.

$required = @('vcruntime140.dll')
$optional = @(
    'vcruntime140_1.dll',
    'msvcp140.dll',
    'msvcp140_1.dll',
    'msvcp140_2.dll',
    'msvcp140_atomic_wait.dll',
    'msvcp140_codecvt_ids.dll',
    'concrt140.dll'
)

function Get-Sha256([string]$Path) {
    # Build.bat may run under a legacy or restricted Windows PowerShell host where
    # Microsoft.PowerShell.Utility/Get-FileHash is unavailable. The framework hash
    # implementation is present on every machine capable of building Genesis.
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

function Find-CrtDirectory {
    $hits = [System.Collections.Generic.List[string]]::new()

    if (-not [string]::IsNullOrWhiteSpace($env:VCToolsRedistDir)) {
        foreach ($name in @('Microsoft.VC143.CRT', 'Microsoft.VC142.CRT')) {
            $candidate = Join-Path $env:VCToolsRedistDir "x64\$name"
            if (Test-Path -LiteralPath (Join-Path $candidate 'vcruntime140.dll')) {
                $hits.Add((Get-Item -LiteralPath $candidate).FullName)
            }
        }
    }

    $globs = @(
        'C:\Program Files\Microsoft Visual Studio\*\*\VC\Redist\MSVC\*\x64\Microsoft.VC*.CRT\vcruntime140.dll',
        'C:\Program Files (x86)\Microsoft Visual Studio\*\*\VC\Redist\MSVC\*\x64\Microsoft.VC*.CRT\vcruntime140.dll'
    )
    foreach ($glob in $globs) {
        foreach ($file in @(Get-Item -Path $glob -ErrorAction SilentlyContinue)) {
            $hits.Add($file.Directory.FullName)
        }
    }

    $system32 = Join-Path $env:SystemRoot 'System32'
    if (Test-Path -LiteralPath (Join-Path $system32 'vcruntime140.dll')) {
        $hits.Add((Get-Item -LiteralPath $system32).FullName)
    }

    if ($hits.Count -eq 0) {
        throw 'Visual C++ x64 CRT (vcruntime140.dll) was not found in a Visual Studio redistributable folder or System32.'
    }

    # Prefer a VC Redist folder over System32 so the staged files match the
    # official Microsoft.VC*.CRT set rather than a WinSxS-redirected copy.
    $preferred = @($hits | Where-Object { $_ -match 'Microsoft\.VC\d+\.CRT$' } | Sort-Object -Descending)
    if ($preferred.Count -gt 0) {
        return $preferred[0]
    }

    return $hits[0]
}

$sourceDirectory = Find-CrtDirectory
if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
}

$staged = [System.Collections.Generic.List[string]]::new()
foreach ($name in ($required + $optional)) {
    $source = Join-Path $sourceDirectory $name
    if (-not (Test-Path -LiteralPath $source)) {
        if ($required -contains $name) {
            throw "Required CRT file missing in ${sourceDirectory}: $name"
        }
        continue
    }

    $destination = Join-Path $OutputDirectory $name
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $staged.Add($name)
}

foreach ($name in $required) {
    if ($staged -notcontains $name) {
        throw "Failed to stage required CRT file: $name"
    }
}

$manifestPath = Join-Path $OutputDirectory 'vcruntime.manifest'
$sourceItem = Get-Item -LiteralPath (Join-Path $sourceDirectory 'vcruntime140.dll')
$lines = @(
    'Package=Microsoft.VC.CRT',
    'Architecture=win-x64',
    "Source=$sourceDirectory",
    "VcruntimeFileVersion=$($sourceItem.VersionInfo.FileVersion)"
)
foreach ($name in $staged) {
    $hash = Get-Sha256 (Join-Path $OutputDirectory $name)
    $lines += "$name.sha256=$hash"
}

[System.IO.File]::WriteAllLines($manifestPath, $lines, [System.Text.UTF8Encoding]::new($false))

Write-Host "Bundled VC++ CRT from $sourceDirectory"
Write-Host "Staged to $OutputDirectory : $($staged -join ', ')"
exit 0
