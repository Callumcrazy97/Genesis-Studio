<#
.SYNOPSIS
    Installs, starts, upgrades and removes Genesis Studio the way a user's machine would.

.DESCRIPTION
    Compiles a trial installer from a published package, under its own name and its own
    Add/Remove Programs identity, so it can never be mistaken for or upgrade a real installation.
    It is installed for the current user only (no administrator, no shortcuts) into a folder under
    TestResults, checked, started from a folder made read-only like Program Files, installed again
    over itself, and uninstalled. The script fails on the first thing that is not as it should be.

    Run from the repository root after a build:
        powershell -ExecutionPolicy Bypass -File BuildTools\InstallDrill.ps1
#>
[CmdletBinding()]
param(
    [string]$PublishDirectory,
    # Leave the trial installer in Dist afterwards.
    [switch]$KeepInstaller
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) { $PublishDirectory = Join-Path $repo 'Genesis Application' }
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$trial = 'Drill'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7C0D2B9E-51A4-4E0B-9A3F-2D6E8B1C4F70}_is1'
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$work = Join-Path $repo "TestResults\InstallDrill\$stamp"
$app = Join-Path $work 'App'
$userData = Join-Path $work 'UserData'
$setup = Join-Path $repo "Dist\GenesisStudio-Setup-$trial.exe"
$steps = [Collections.Generic.List[string]]::new()

function Step([string]$name, [scriptblock]$action) {
    Write-Host "`n- $name" -ForegroundColor Cyan
    & $action
    $steps.Add($name)
}
function Require([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}
function Invoke-Waiting([string]$file, [string[]]$arguments, [int]$seconds) {
    $process = Start-Process -FilePath $file -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit($seconds * 1000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "$([IO.Path]::GetFileName($file)) did not finish within $seconds seconds."
    }
    $process.Refresh()
    return $process.ExitCode
}
function Install([string]$log) {
    $code = Invoke-Waiting $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/NOICONS', '/TASKS=""', "/DIR=`"$app`"", "/LOG=`"$log`"") 900
    Require ($code -eq 0) "The installer failed with exit code $code. See $log"
}
function Test-CanWrite([string]$directory) {
    $probe = Join-Path $directory 'write-probe.tmp'
    try { [IO.File]::WriteAllText($probe, 'x'); Remove-Item -LiteralPath $probe -Force; return $true }
    catch { return $false }
}

Require (Test-Path -LiteralPath (Join-Path $PublishDirectory 'Genesis Application.exe')) "No published Studio at $PublishDirectory. Run Build.bat first."
Require (-not (Test-Path -LiteralPath $uninstallKey)) "A trial installation from an earlier drill is still registered ($uninstallKey). Remove it from Add/Remove Programs first."
New-Item -ItemType Directory -Path $work, $userData -Force | Out-Null
$failure = $null
try {
    Step 'Compile the trial installer' {
        & (Join-Path $repo 'Installer\EnsureVcRedist.ps1') -DestinationDirectory (Join-Path $repo 'Installer\redist')
        & (Join-Path $repo 'Installer\PrepareWizardArt.ps1') -AssetsDirectory (Join-Path $repo 'Source\Genesis.Application.Studio\Assets') -OutputDirectory (Join-Path $repo 'Installer\art')
        & (Join-Path $repo 'Installer\CompileInstaller.ps1') -RepositoryRoot $repo -PublishDirectory $PublishDirectory -TrialName $trial
        Require (Test-Path -LiteralPath $setup) "The trial installer was not written: $setup"
        Write-Host ("  {0:N0} MB" -f ((Get-Item -LiteralPath $setup).Length / 1MB))
    }

    Step 'Install for the current user, silently, with no administrator' {
        Install (Join-Path $work 'install.log')
        Require (Test-Path -LiteralPath (Join-Path $app 'Genesis Application.exe')) 'The installed folder has no Genesis Application.exe.'
    }

    Step 'Check what was installed' {
        foreach ($relative in @(
                'Player\GenesisEngine.exe', 'Tools\DXC\dxc.exe', 'Player\Tools\DXC\dxc.exe',
                'Licenses\Genesis-LICENSE.txt', 'Licenses\ThirdPartyNotices.txt', 'Licenses\DotNet-LICENSE.txt', 'Licenses\DotNet-THIRD-PARTY-NOTICES.txt',
                'Licenses\SkiaSharp-THIRD-PARTY-NOTICES.txt', 'Licenses\Assimp-LICENSE.txt',
                'Player\Licenses\Genesis-LICENSE.txt', 'Player\Licenses\ThirdPartyNotices.txt', 'Player\Licenses\DotNet-LICENSE.txt',
                'Tools\DXC\LICENSE-MS.txt', 'Tools\DXC\LICENSE-LLVM.txt',
                'Documentation\README.md', 'Documentation\GettingStarted.md', 'Documentation\GameFeatures.md', 'Documentation\LargeWorlds.md',
                'unins000.exe')) {
            Require (Test-Path -LiteralPath (Join-Path $app $relative) -PathType Leaf) "Not installed: $relative"
        }
        $unwanted = @(Get-ChildItem -LiteralPath $app -Recurse -File | Where-Object { $_.Extension -in @('.pdb', '.log') -or $_.Name -in @('BuildSummary.json', 'PackageManifest.json') })
        Require ($unwanted.Count -eq 0) "Development files were installed: $(($unwanted | Select-Object -First 5 | ForEach-Object { $_.FullName.Substring($app.Length + 1) }) -join ', ')"
        $readme = Get-Content -LiteralPath (Join-Path $app 'Documentation\README.md') -TotalCount 1
        Require ($readme -eq '# Genesis Studio documentation') "The installed README is not the product guide: '$readme'"
        Require (Test-Path -LiteralPath $uninstallKey) 'The installation is not registered for removal.'
        $registered = Get-ItemProperty -LiteralPath $uninstallKey
        $version = (Get-Content -LiteralPath (Join-Path $repo 'Installer\AppVersion.txt') -TotalCount 1).Trim()
        Require ($registered.DisplayVersion -eq $version) "Add/Remove Programs shows version '$($registered.DisplayVersion)', not $version."
        $files = @(Get-ChildItem -LiteralPath $app -Recurse -File)
        Write-Host ("  {0} files, {1:N0} MB, version {2}" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), $registered.DisplayVersion)
    }

    Step 'Start Studio from a folder it may only read, as under Program Files' {
        & icacls $app /inheritance:r /grant:r '*S-1-5-32-545:(OI)(CI)RX' '*S-1-5-18:(OI)(CI)F' /Q | Out-Null
        try {
            Require (-not (Test-CanWrite $app)) 'The installed folder could not be made read-only for the drill.'
            $saved = @{}
            foreach ($name in @('GENESIS_APPLICATION_HOME', 'GENESIS_DXC_LOCAL_ONLY', 'GENESIS_DXC_PATH')) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
            try {
                $env:GENESIS_APPLICATION_HOME = $userData
                $env:GENESIS_DXC_LOCAL_ONLY = '1'
                $env:GENESIS_DXC_PATH = $null
                $code = Invoke-Waiting (Join-Path $app 'Genesis Application.exe') @('--smoke-test') 180
            }
            finally { foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') } }
            Require ($code -eq 0) "The installed Studio's start-up check failed with exit code $code. See $userData\Logs\studio.log"
            $log = [IO.File]::ReadAllText((Join-Path $userData 'Logs\studio.log'))
            foreach ($evidence in @('Bundled shader toolchain passed:', 'Published application smoke test passed.')) {
                Require ($log.Contains($evidence)) "The installed Studio's log lacks: $evidence"
            }
        }
        finally { & icacls $app /reset /T /C /Q | Out-Null }
    }

    Step 'Install again over the top, as an upgrade does' {
        Install (Join-Path $work 'upgrade.log')
        Require (Test-Path -LiteralPath (Join-Path $app 'Genesis Application.exe')) 'The upgrade left no Genesis Application.exe.'
        Require (@(Get-ChildItem -LiteralPath $app -Filter 'unins*.exe').Count -eq 1) 'The upgrade registered a second uninstaller instead of reusing the first.'
    }

    Step 'Uninstall' {
        $code = Invoke-Waiting (Join-Path $app 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') 600
        Require ($code -eq 0) "The uninstaller failed with exit code $code."
        # The uninstaller finishes from a copy of itself; give that copy a moment to delete the rest.
        for ($i = 0; $i -lt 60 -and (Test-Path -LiteralPath $app); $i++) { Start-Sleep -Milliseconds 500 }
        $left = @()
        if (Test-Path -LiteralPath $app) { $left = @(Get-ChildItem -LiteralPath $app -Recurse -File) }
        Require ($left.Count -eq 0) "Uninstalling left $($left.Count) files behind, such as $(($left | Select-Object -First 5 | ForEach-Object { $_.FullName.Substring($app.Length + 1) }) -join ', ')"
        Require (-not (Test-Path -LiteralPath $uninstallKey)) 'Uninstalling left the Add/Remove Programs entry behind.'
        Require (Test-Path -LiteralPath (Join-Path $userData 'Logs\studio.log')) "Uninstalling removed the user's own data."
    }
}
catch { $failure = $_.Exception.Message }
finally {
    if (Test-Path -LiteralPath $app) { & icacls $app /reset /T /C /Q 2>$null | Out-Null }
    if (-not $KeepInstaller -and (Test-Path -LiteralPath $setup)) { Remove-Item -LiteralPath $setup -Force }
}

$result = [ordered]@{
    Result = $(if ($failure) { 'Failed' } else { 'Passed' }); Failure = $failure
    PublishDirectory = $PublishDirectory; Folder = $work; StepsPassed = @($steps.ToArray())
}
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $work 'InstallDrill.json') -Encoding UTF8
if ($failure) {
    Write-Host "`nINSTALL DRILL FAILED: $failure" -ForegroundColor Red
    if (Test-Path -LiteralPath $uninstallKey) { Write-Host "The trial installation is still present at $app; remove 'Genesis Studio $trial' from Add/Remove Programs." -ForegroundColor Yellow }
    exit 1
}
Write-Host "`nInstall drill passed: $($steps.Count) steps. Report: $work\InstallDrill.json" -ForegroundColor Green
exit 0
