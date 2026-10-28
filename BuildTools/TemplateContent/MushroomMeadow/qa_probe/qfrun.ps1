param([string]$Room, [string]$StepsFile, [string]$OutFile, [string]$What = "QA feature probe", [string]$Mode = "main")
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path
Set-Location $repo
. .\.claude\agent-tools\lock-holder.ps1
$env:GENESIS_SESSION_NAME = "Mushroom Meadow QA-FEATURES"
$m = New-Object System.Threading.Mutex($false, "Global\GenesisAgentTestRun")
try { [void]$m.WaitOne([TimeSpan]::FromMinutes(120)) } catch [System.Threading.AbandonedMutexException] {}
try {
    Set-LockHolder $What
    & python "$PSScriptRoot\qf_setup.py" | Out-Null
    $modeText = "function QfMode() { return `"$Mode`"; }"
    [System.IO.File]::WriteAllText((Join-Path $repo "TestResults\qf\Assets\Scripts\QF Mode.pgsl"), $modeText, (New-Object System.Text.UTF8Encoding($false)))
    $deadline = (Get-Date).AddMinutes(10)
    while ((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory -lt 6000000 -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 15 }
    $free = [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB, 1)
    "free RAM GB at start: $free" | Out-File $OutFile -Encoding utf8
    $steps = Get-Content $StepsFile -Raw
    $json = '{"room":"' + $Room + '","steps":' + $steps + ',"timeoutSeconds":240}'
    $inFile = Join-Path (Split-Path $OutFile) "qf-input.json"
    [System.IO.File]::WriteAllText($inFile, $json, (New-Object System.Text.UTF8Encoding($false)))
    & ".\Genesis Application\Genesis Application.exe" --automation "TestResults\qf" call game.run "@$inFile" 2>&1 | Out-File $OutFile -Append -Encoding utf8
} finally {
    Clear-LockHolder
    $m.ReleaseMutex()
    $m.Dispose()
}
