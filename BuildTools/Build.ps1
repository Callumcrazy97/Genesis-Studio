param([Parameter(ValueFromRemainingArguments = $true)][string[]]$BuildArguments)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$profile = 'Quick'; $config = 'Release'; $tests = 'Skipped'; $backends = @()
$focused = $null; $installer = $false; $launch = $false; $explicitQuick = $false; $explicitFull = $false
try {
    for ($i = 0; $i -lt $BuildArguments.Count; $i++) {
        switch ($BuildArguments[$i].ToLowerInvariant()) {
            { $_ -in @('--help','-h') } {
                Write-Host @'
Genesis Studio - Quick Build / Full Build
  Build.bat                Quick: incremental publish, package audit and startup smoke.
  --quick                  Same as default. Produces Studio and matching Player.
  --full                   Clean Release build, full regression, all five renderer smokes.
  --check                  Quick plus fast regression and DX11/DX12 smokes.
  --test TARGET            Add one editor/feature test, e.g. Room or Shader.
  --backend NAME           Add dx11, dx12, vulkan, opengl or software smoke.
  --full-tests             Add full regression to Quick.
  --quick-smoke            Add DX11/DX12 smokes.
  --full-smoke             Add all five renderer smokes.
  --skip-tests             Legacy Quick alias; startup smoke remains required.
  --debug                  Debug Quick Build. Full always uses Release.
  --installer              Compile installer from validated staging.
  --run                    Open Studio after successful promotion.
  --help                   Show help.
Output: Genesis Application. Logs: TestResults/Builds/<run>.
Builds never terminate Studio/Player. Failed builds preserve published output.
These profiles build Genesis Studio and Player. Game export is separate.
'@
                exit 0
            }
            { $_ -in @('--quick','--skip-tests') } { $explicitQuick = $true }
            '--full' { $explicitFull = $true; $profile = 'Full' }
            '--debug' { $config = 'Debug' }
            '--run' { $launch = $true }
            '--installer' { $installer = $true }
            '--check' { $tests = 'Fast'; $backends += @('dx11','dx12') }
            '--full-tests' { $tests = 'Full' }
            '--quick-smoke' { $backends += @('dx11','dx12') }
            '--full-smoke' { $backends += @('dx11','dx12','vulkan','opengl','software') }
            { $_ -in @('--test','-test','--backend','-backend') } {
                $flag = $_
                if (++$i -ge $BuildArguments.Count) { throw "$flag requires a value." }
                $value = $BuildArguments[$i]
                if ($flag -like '*backend') {
                    if ($value.ToLowerInvariant() -notin @('dx11','dx12','vulkan','opengl','software')) {
                        throw "Unsupported renderer '$value'. Use dx11, dx12, vulkan, opengl or software. WebGPU/wGPU and SDL3 GPU have been removed."
                    }
                    $backends += $value.ToLowerInvariant()
                } else { $focused = $value; $tests = 'Focused' }
            }
            default { throw "Unknown argument '$($BuildArguments[$i])'. Run Build.bat --help." }
        }
    }
    if ($explicitFull -and ($explicitQuick -or $config -eq 'Debug' -or $null -ne $focused)) {
        throw 'Full Build cannot be combined with Quick, --skip-tests, --debug or a focused test.'
    }
    if ($explicitFull) { $tests = 'Full'; $backends = @('dx11','dx12','vulkan','opengl','software') }
    $backends = @($backends | Select-Object -Unique)
} catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 2 }

$runId = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$generated = Join-Path $repo '.build'
$staging = Join-Path $generated "staging/$runId"
$output = Join-Path $repo 'Genesis Application'
$previous = Join-Path $generated "previous/$runId"
$reportDir = Join-Path $repo "TestResults/Builds/$runId"
$testBin = Join-Path $generated "tests/$runId"
$studioProject = Join-Path $repo 'Source/Genesis.Application.Studio/Genesis.Application.Studio.csproj'
$playerProject = Join-Path $repo 'Source/Genesis.Player/Genesis.Player.csproj'
[xml]$studioProjectXml = Get-Content -LiteralPath $studioProject -Raw
$studioRevision = $studioProjectXml.SelectSingleNode('/Project/PropertyGroup/GenesisStudioRevision').InnerText
if ([string]::IsNullOrWhiteSpace($studioRevision)) { throw 'Studio source revision is missing.' }
$testProject = Join-Path $repo 'Tests/Genesis.Application.Headless/Genesis.Application.Headless.csproj'
$stages = [Collections.Generic.List[object]]::new()
$clock = [Diagnostics.Stopwatch]::StartNew(); $stepNumber = 0; $promoted = $false; $failure = $null
$oldEnvironment = @{}
foreach ($name in @('GENESIS_APPLICATION_HOME','GENESIS_DXC_LOCAL_ONLY','GENESIS_DXC_PATH')) {
    $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Assert-WorkspacePath([string]$path) {
    $resolved = [IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith($repo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Generated path escaped workspace: $resolved" }
    return $resolved
}
function Invoke-BuildStep([string]$name, [scriptblock]$action) {
    $script:stepNumber++
    Write-Host "`n[$script:stepNumber] $name" -ForegroundColor Cyan
    $stepClock = [Diagnostics.Stopwatch]::StartNew(); $result = 'Passed'
    try { & $action } catch { $result = 'Failed'; throw } finally {
        $stages.Add([ordered]@{ Name = $name; Result = $result; Seconds = [Math]::Round($stepClock.Elapsed.TotalSeconds,2) })
    }
}
function Invoke-Logged([string]$file, [string[]]$arguments, [string]$logName) {
    $log = Join-Path $reportDir $logName
    $savedPreference = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $file @arguments 2>&1 | Tee-Object -FilePath $log | Out-Host; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $savedPreference }
    if ($code -ne 0) { throw "$file failed (exit $code). See $log" }
}
function Copy-Tree([string]$from, [string]$to) {
    [void](Assert-WorkspacePath $to)
    & robocopy $from $to /E /NFL /NDL /NJH /NJS /NC /NS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy failed: $from -> $to (robocopy $LASTEXITCODE)" }
}
function Assert-Package {
    foreach ($prefix in @('', 'Player/')) {
        $entry = if ($prefix) { 'GenesisEngine' } else { 'Genesis Application' }
        foreach ($relative in @("$entry.exe", "$entry.dll", "$entry.deps.json", "$entry.runtimeconfig.json", 'vcruntime140.dll', 'Tools/DXC/dxc.exe', 'Tools/DXC/dxcompiler.dll', 'Tools/DXC/dxil.dll')) {
            $path = Join-Path $staging ($prefix + $relative)
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Incomplete package: $path" }
        }
    }
    $retired = @(Get-ChildItem -LiteralPath $staging -Recurse -File | Where-Object { $_.Name -match '(?i)^(SDL3|wgpu|Silk\.NET\.WebGPU)' -or $_.Name -match '(?i)shadercross' })
    if ($retired.Count) { throw "Retired backend binaries found: $($retired.FullName -join ', ')" }
    foreach ($relative in @('Genesis Application.deps.json', 'Player/GenesisEngine.deps.json')) {
        if ([IO.File]::ReadAllText((Join-Path $staging $relative)) -match '(?i)Silk.NET.WebGPU|SDL3-CS') { throw "Retired dependency in $relative" }
    }
    foreach ($assembly in @('Genesis.Runtime.dll','Genesis.Rendering.dll','Genesis.Rendering.Abstractions.dll','Genesis.Shared.dll')) {
        $studioHash = Get-PackageHash (Join-Path $staging $assembly)
        $playerHash = Get-PackageHash (Join-Path $staging "Player/$assembly")
        if ($studioHash -ne $playerHash) { throw "Studio and Player contain different versions of $assembly" }
    }
}
function Get-PackageHash([string]$path) {
    $stream = [IO.File]::OpenRead($path); $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
    finally { $stream.Dispose(); $sha.Dispose() }
}

try {
    [void][IO.Directory]::CreateDirectory((Assert-WorkspacePath $staging))
    [void][IO.Directory]::CreateDirectory((Assert-WorkspacePath $reportDir))
    Push-Location $repo
    Write-Host "Genesis Studio - $profile Build ($config, win-x64)" -ForegroundColor Green
    Write-Host "Staging: $staging"
    if ($profile -eq 'Full') {
        Invoke-BuildStep 'Restore dependencies and clean Release compilation outputs' {
            foreach ($project in @($studioProject,$playerProject,$testProject)) {
                Invoke-Logged 'dotnet' @('restore',$project,'-r','win-x64','--force','--nologo') ('restore-' + [IO.Path]::GetFileNameWithoutExtension($project) + '.log')
                Invoke-Logged 'dotnet' @('clean',$project,'-c','Release','-r','win-x64','--nologo') ('clean-' + [IO.Path]::GetFileNameWithoutExtension($project) + '.log')
            }
        }
    }
    Write-Host "Studio source: H$studioRevision / Build $runId" -ForegroundColor Cyan
    Invoke-BuildStep 'Publish Studio and matching Player' {
        foreach ($item in @(@($studioProject,$staging,'studio'), @($playerProject,(Join-Path $staging 'Player'),'player'))) {
            $publishArgs = @('publish',$item[0],'-c',$config,'-r','win-x64','--self-contained','true','--nologo','-p:TreatWarningsAsErrors=true','-p:PublishSingleFile=false',"-p:GenesisBuildId=$runId",'-o',$item[1])
            if ($profile -eq 'Full') { $publishArgs += '--no-restore' }
            Invoke-Logged 'dotnet' $publishArgs ("publish-$($item[2]).log")
        }
    }
    Invoke-BuildStep 'Stage shader toolchain, runtime and documentation' {
        & (Join-Path $PSScriptRoot 'StageDxc.ps1') -OutputDirectory (Join-Path $staging 'Tools/DXC') -PackageVersion '1.9.2607.13'
        Copy-Tree (Join-Path $staging 'Tools/DXC') (Join-Path $staging 'Player/Tools/DXC')
        & (Join-Path $PSScriptRoot 'StageVcRuntime.ps1') -OutputDirectory $staging
        & (Join-Path $PSScriptRoot 'StageVcRuntime.ps1') -OutputDirectory (Join-Path $staging 'Player')
        # Review portfolios contain disposable project fixtures and very long sidecar paths.
        # Ship the product guide; keep development evidence in the repository/report directory.
        [void][IO.Directory]::CreateDirectory((Join-Path $staging 'Documentation'))
        foreach ($guide in @('README.md','BuildProfiles.md')) {
            $sourceGuide = Join-Path $repo "Documentation/$guide"
            if (Test-Path -LiteralPath $sourceGuide) { Copy-Item -LiteralPath $sourceGuide -Destination (Join-Path $staging "Documentation/$guide") }
        }
    }
    Invoke-BuildStep 'Audit package and engine assembly consistency' { Assert-Package }
    if ($tests -ne 'Skipped' -or $backends.Count) {
        Invoke-BuildStep 'Build test harness against staged Player' {
            Invoke-Logged 'dotnet' @('build',$testProject,'-c',$config,'--nologo','-p:TreatWarningsAsErrors=true',"-p:GenesisBuildId=$runId",'-o',$testBin) 'test-build.log'
            Copy-Tree (Join-Path $staging 'Player') (Join-Path $testBin 'Player')
        }
        $testExe = Join-Path $testBin 'Genesis.Application.Headless.exe'
        if ($tests -ne 'Skipped') {
            Invoke-BuildStep "$tests regression tests" {
                $testArgs = @('--output',(Join-Path $reportDir 'Tests'))
                if ($tests -eq 'Fast') { $testArgs += '--fast-tests' }
                if ($tests -eq 'Focused') { $testArgs += @('--test',$focused) }
                Invoke-Logged $testExe $testArgs 'regression.log'
            }
        }
        foreach ($backend in $backends) {
            Invoke-BuildStep "Explicit $backend renderer smoke" {
                Invoke-Logged $testExe @('--backend',$backend,'--output',(Join-Path $reportDir "Backends/$backend")) ("backend-$backend.log")
            }
        }
    }
    Invoke-BuildStep 'Published Studio startup and bundled shader compiler smoke' {
        $env:GENESIS_APPLICATION_HOME = Join-Path $reportDir 'SmokeUserData'
        $env:GENESIS_DXC_LOCAL_ONLY = '1'
        $env:GENESIS_DXC_PATH = Join-Path $staging 'Tools/DXC/dxc.exe'
        $smoke = Start-Process -FilePath (Join-Path $staging 'Genesis Application.exe') -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru
        if (-not $smoke.WaitForExit(60000)) {
            # Only terminate this build's isolated smoke process; it has no user documents.
            Stop-Process -Id $smoke.Id -ErrorAction SilentlyContinue
            throw 'Published startup smoke timed out after 60 seconds.'
        }
        $smoke.Refresh()
        if ($smoke.ExitCode -ne 0) { throw "Published startup smoke failed with exit $($smoke.ExitCode)." }
        $smokeLog = Join-Path $env:GENESIS_APPLICATION_HOME 'Logs/studio.log'
        $smokeText = [IO.File]::ReadAllText($smokeLog)
        foreach ($success in @("Build identity: H$studioRevision / $runId", 'Bundled shader toolchain passed:', 'Published application smoke test passed.')) {
            if (-not $smokeText.Contains($success)) { throw "Missing smoke evidence: $success ($smokeLog)" }
        }
    }
    if ($installer) {
        Invoke-BuildStep 'Compile installer from validated staging' {
            & (Join-Path $repo 'Installer/EnsureVcRedist.ps1') -DestinationDirectory (Join-Path $repo 'Installer/redist')
            & (Join-Path $repo 'Installer/PrepareWizardArt.ps1') -AssetsDirectory (Join-Path $repo 'Source/Genesis.Application.Studio/Assets') -OutputDirectory (Join-Path $repo 'Installer/art')
            & (Join-Path $repo 'Installer/CompileInstaller.ps1') -RepositoryRoot $repo -PublishDirectory $staging
        }
    }
    Invoke-BuildStep 'Write package manifest' {
        $manifest = @(Get-ChildItem -LiteralPath $staging -Recurse -File | ForEach-Object {
            [ordered]@{ Path = $_.FullName.Substring($staging.Length + 1); Bytes = $_.Length; Sha256 = (Get-PackageHash $_.FullName) }
        })
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $staging 'PackageManifest.json') -Encoding UTF8
    }
    Invoke-BuildStep 'Promote successful build' {
        [void](Assert-WorkspacePath $output); [void](Assert-WorkspacePath $previous)
        $running = @(Get-Process | Where-Object { try { $_.Path -and $_.Path.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase) } catch { $false } })
        if ($running.Count) { throw "Published Studio/Player is running. Close it and rerun. Validated staging remains at $staging; current output is untouched." }
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($previous))
        $hadOutput = Test-Path -LiteralPath $output
        if ($hadOutput) { Move-Item -LiteralPath $output -Destination $previous }
        try { Move-Item -LiteralPath $staging -Destination $output; $script:promoted = $true }
        catch {
            if ($hadOutput -and -not (Test-Path -LiteralPath $output)) { Move-Item -LiteralPath $previous -Destination $output }
            throw
        }
    }
} catch { $failure = $_.Exception.Message; Write-Host "`nBUILD FAILED: $failure" -ForegroundColor Red }
finally {
    $clock.Stop()
    foreach ($name in $oldEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], 'Process') }
    $passedBackends = @($backends | Where-Object { $name = "Explicit $_ renderer smoke"; @($stages | Where-Object { $_.Name -eq $name -and $_.Result -eq 'Passed' }).Count -gt 0 })
    $regressionStage = @($stages | Where-Object { $_.Name -eq "$tests regression tests" })
    $summary = [ordered]@{
        Profile = $profile; Configuration = $config; Runtime = 'win-x64'; Run = $runId; StudioRevision = $studioRevision
        Result = $(if ($promoted) { 'Passed' } else { 'Failed' }); Seconds = [Math]::Round($clock.Elapsed.TotalSeconds,2)
        Output = $output; Staging = $staging; Previous = $previous; Failure = $failure
        RequestedRegression = $tests; Regression = $(if ($regressionStage.Count) { $regressionStage[0].Result } else { 'Skipped' })
        RequestedBackends = $backends; BackendCoverage = $passedBackends
        SkippedBackends = @(@('dx11','dx12','vulkan','opengl','software') | Where-Object { $_ -notin $passedBackends })
        Stages = @($stages.ToArray())
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reportDir 'BuildSummary.json') -Encoding UTF8
    if ($promoted) { Copy-Item -LiteralPath (Join-Path $reportDir 'BuildSummary.json') -Destination (Join-Path $output 'BuildSummary.json') }
    Write-Host "Studio identity: H$studioRevision / $runId"
    Write-Host "`n$profile Build: $($summary.Result) in $($summary.Seconds)s. Regression: $($summary.Regression)."
    Write-Host "Renderer smokes passed: $($passedBackends -join ', '). Skipped: $($summary.SkippedBackends -join ', ')."
    Write-Host "Report: $reportDir"
    Pop-Location
}
if (-not $promoted) { exit 1 }
if ($launch) { Start-Process -FilePath (Join-Path $output 'Genesis Application.exe') }
exit 0
