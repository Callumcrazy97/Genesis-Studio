[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Target,

    [Parameter(Mandatory = $true)]
    [string]$EventFile,

    [Parameter(Mandatory = $true)]
    [string]$SummaryFile,

    [string]$ArgumentsBase64 = ""
)

Set-StrictMode -Version 2.0
$wrapperErrorPreference = $ErrorActionPreference
$ErrorActionPreference = "Stop"

$launchGate = [string]$env:DEVPROFILER_LAUNCH_GATE
if (-not [string]::IsNullOrWhiteSpace($launchGate)) {
    $gateDeadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    while (-not [IO.File]::Exists($launchGate) -and [DateTimeOffset]::UtcNow -lt $gateDeadline) {
        Start-Sleep -Milliseconds 10
    }
}

$targetArguments = @()
if (-not [string]::IsNullOrWhiteSpace($ArgumentsBase64)) {
    try {
        $argumentJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($ArgumentsBase64))
        $decoded = ConvertFrom-Json -InputObject $argumentJson
        if ($null -ne $decoded) {
            $targetArguments = @($decoded | ForEach-Object { [string]$_ })
        }
    }
    catch {
        [Console]::Error.WriteLine("DevProfiler could not decode target arguments: $($_.Exception.Message)")
    }
}

$eventDirectory = Split-Path -Parent $EventFile
$summaryDirectory = Split-Path -Parent $SummaryFile
if ($eventDirectory) { [IO.Directory]::CreateDirectory($eventDirectory) | Out-Null }
if ($summaryDirectory) { [IO.Directory]::CreateDirectory($summaryDirectory) | Out-Null }

$utf8 = [Text.UTF8Encoding]::new($false)
$writer = [IO.StreamWriter]::new($EventFile, $false, $utf8)
$writer.AutoFlush = $true
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$startedAt = [DateTimeOffset]::Now
$success = $true
$exitCode = 0
$counts = [ordered]@{
    Output = 0
    Information = 0
    Verbose = 0
    Debug = 0
    Warning = 0
    Error = 0
}

function Write-ProfilerEvent {
    param(
        [string]$Stream,
        [string]$RecordType,
        [string]$Details,
        [string]$Script = "",
        [int]$Line = 0,
        [int]$Column = 0,
        [string]$FullyQualifiedErrorId = ""
    )

    $entry = [ordered]@{
        timestamp = [DateTimeOffset]::Now.ToString("o")
        elapsedMs = $stopwatch.Elapsed.TotalMilliseconds
        stream = $Stream
        recordType = $RecordType
        details = $Details
        script = $Script
        line = $Line
        column = $Column
        fullyQualifiedErrorId = $FullyQualifiedErrorId
    }
    $json = $entry | ConvertTo-Json -Compress -Depth 5
    $writer.WriteLine($json)
}

function Publish-ProfilerRecord {
    param([object]$Record)

    $stream = "Output"
    $recordType = if ($null -eq $Record) { "null" } else { $Record.GetType().FullName }
    $details = ""
    $script = ""
    $line = 0
    $column = 0
    $errorId = ""

    if ($Record -is [Management.Automation.ErrorRecord]) {
        $stream = "Error"
        $details = [string]$Record
        $errorId = [string]$Record.FullyQualifiedErrorId
        if ($null -ne $Record.InvocationInfo) {
            $script = [string]$Record.InvocationInfo.ScriptName
            $line = [int]$Record.InvocationInfo.ScriptLineNumber
            $column = [int]$Record.InvocationInfo.OffsetInLine
        }
        $counts.Error++
        [Console]::Error.WriteLine($details)
    }
    elseif ($Record -is [Management.Automation.WarningRecord]) {
        $stream = "Warning"
        $details = [string]$Record.Message
        $counts.Warning++
        [Console]::Out.WriteLine("WARNING: $details")
    }
    elseif ($Record -is [Management.Automation.VerboseRecord]) {
        $stream = "Verbose"
        $details = [string]$Record.Message
        $counts.Verbose++
        [Console]::Out.WriteLine("VERBOSE: $details")
    }
    elseif ($Record -is [Management.Automation.DebugRecord]) {
        $stream = "Debug"
        $details = [string]$Record.Message
        $counts.Debug++
        [Console]::Out.WriteLine("DEBUG: $details")
    }
    elseif ($Record -is [Management.Automation.InformationRecord]) {
        $stream = "Information"
        $details = [string]$Record.MessageData
        $counts.Information++
        [Console]::Out.WriteLine($details)
    }
    else {
        if ($null -ne $Record) {
            $details = ($Record | Out-String -Width 4096).TrimEnd()
        }
        $counts.Output++
        if (-not [string]::IsNullOrWhiteSpace($details)) {
            [Console]::Out.WriteLine($details)
        }
    }

    Write-ProfilerEvent -Stream $stream -RecordType $recordType -Details $details -Script $script -Line $line -Column $column -FullyQualifiedErrorId $errorId
}

try {
    $ErrorActionPreference = $wrapperErrorPreference
    & $Target @targetArguments *>&1 | ForEach-Object { Publish-ProfilerRecord -Record $_ }
    $pipelineSucceeded = $?

    $lastExit = Get-Variable -Name LASTEXITCODE -ValueOnly -ErrorAction SilentlyContinue
    if ($null -ne $lastExit) {
        $exitCode = [int]$lastExit
    }
    if (-not $pipelineSucceeded) {
        $success = $false
        if ($exitCode -eq 0) { $exitCode = 1 }
    }
}
catch {
    $success = $false
    $exitCode = 1
    Publish-ProfilerRecord -Record $_
}
finally {
    $stopwatch.Stop()
    $endedAt = [DateTimeOffset]::Now
    $runtimePath = ""
    try { $runtimePath = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName } catch { }

    $summary = [ordered]@{
        runtimePath = $runtimePath
        powerShellVersion = [string]$PSVersionTable.PSVersion
        edition = if ($PSVersionTable.ContainsKey("PSEdition")) { [string]$PSVersionTable.PSEdition } else { "Desktop" }
        target = $Target
        startedAt = $startedAt.ToString("o")
        endedAt = $endedAt.ToString("o")
        elapsedMs = $stopwatch.Elapsed.TotalMilliseconds
        success = $success
        exitCode = $exitCode
        outputCount = $counts.Output
        informationCount = $counts.Information
        verboseCount = $counts.Verbose
        debugCount = $counts.Debug
        warningCount = $counts.Warning
        errorCount = $counts.Error
    }

    try {
        $summaryJson = $summary | ConvertTo-Json -Depth 5
        [IO.File]::WriteAllText($SummaryFile, $summaryJson, $utf8)
    }
    catch {
        [Console]::Error.WriteLine("DevProfiler could not write the PowerShell summary: $($_.Exception.Message)")
    }

    $writer.Dispose()
}

exit $exitCode
