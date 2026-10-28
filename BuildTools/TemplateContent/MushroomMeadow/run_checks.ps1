# Runs Mushroom Meadow's two engine checks, one lock hold each, and deletes the run folders it made.
#   .\BuildTools\TemplateContent\MushroomMeadow\run_checks.ps1 [-Targets 2d-showcase,mushroom-export]
param([string[]]$Targets = @("2d-showcase", "mushroom-export"), [string]$Who = "Mushroom Meadow")
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
Set-Location $repo
. (Join-Path $repo ".claude\agent-tools\lock-holder.ps1")
$env:GENESIS_SESSION_NAME = $Who
foreach ($t in $Targets) {
    $log = Join-Path $repo ("TestResults\mm-" + $t + ".log")
    $m = New-Object System.Threading.Mutex($false, "Global\GenesisAgentTestRun")
    try { [void]$m.WaitOne([TimeSpan]::FromMinutes(120)) } catch [System.Threading.AbandonedMutexException] { }
    try {
        Set-LockHolder $t
        $before = @(Get-ChildItem (Join-Path $repo "TestResults\Builds") -Directory -ErrorAction SilentlyContinue | ForEach-Object Name)
        & (Join-Path $repo "Build.bat") --quick --test $t *> $log
        "$t exit $LASTEXITCODE"
    } finally { Clear-LockHolder; $m.ReleaseMutex(); $m.Dispose() }
    Select-String -Path $log -Pattern '^(PASS|FAIL)|Quick Build:' | ForEach-Object { $_.Line.Substring(0, [Math]::Min(220, $_.Line.Length)) }
}
