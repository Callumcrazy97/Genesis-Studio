#Requires -Version 5.1
<#
.SYNOPSIS
    Exports a Genesis game with the published engine, then runs the EXPORTED game on each renderer with
    scripted checks, and writes RESULTS.md, results.json and every log into a dated folder.

.DESCRIPTION
    1. Exports the project into <OutputRoot>\<yyyyMMdd-HHmm>\Game with the published Studio's own export code
       (BuildTools\GameBuildTest\GameBuildTestTool, built outside the repository on each run).
    2. For each renderer and each scenario of the game's configuration, starts the exported game with an
       unattended window (hidden from the desktop and never focused), below-normal priority and a fresh copy
       of the shader cache that shipped in the export, so every run is a first launch.
    3. Optionally writes a test plan the game reads at start (games that support one), then drives the
       game's window from outside: minimise/restore, a simulated alt-tab (sent behind other windows and
       back), resizes, and (only with -IncludeF11) F11.
    4. Collects stdout/stderr, the player log, the render log, crash reports, screenshots, slow frames,
       shader lines and a stall probe (how long the window's message loop stops answering), and judges the
       checks.

    Safety: holds the machine-wide mutex Global\GenesisAgentTestRun while a game process runs, waits while
    another test plan is present, the published Player is running or the same game is running from another
    folder (someone playing), never touches processes or windows it did not start, deletes the plan as soon
    as the game has read it and again in finally, and backs up and restores the game's save files it names.

.PARAMETER Config
    Game-specific settings (hashtable). Keys: Name, PlanPath, PlanConsumedPattern, HeartbeatPattern,
    ErrorPattern, SaveFolder, TestSaveFiles, GuardSaveFiles, NoScreenshotsOn, CheckOrder, Scenarios.
    Each scenario: Name, Primary, ReadyPattern, ReadyName, StartTimeoutSeconds, QuitAt, Plan (lines),
    Checks (Kind pattern|hitch|frames), Steps (At, Do = minimise|restore|away|back|resize|resizeback|f11,
    Width, Height, Check). See C:\Users\Cal\Desktop\Genesis Build Tests\Run-BuildTest.ps1 for an example.

.EXAMPLE
    & .\BuildTools\GameBuildTest.ps1 -Project 'C:\Games\My Game' -OutputRoot 'D:\Build Tests' -ConfigFile 'D:\Build Tests\MyGame.config.ps1'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [string[]]$Renderers = @('Direct3D11', 'Direct3D12', 'Vulkan', 'OpenGL'),
    [hashtable]$Config = $null,
    [string]$ConfigFile = '',
    [string]$Engine = '',
    [string]$ExportPath = '',
    [ValidateSet('Windowed', 'Fullscreen')][string]$WindowMode = 'Windowed',
    [string]$GameTitle = '',
    [int]$Keep = 3,
    [switch]$IncludeF11,
    [string[]]$Scenarios = @(),
    [int]$MaxWaitMinutes = 30,
    [string]$GoFile = '',          # when set, no game starts until this file exists (removed at the end)
    [string]$CompareTo = '',       # an earlier test's results.json: RESULTS.md then shows that test against this one
    [int]$HangSeconds = 120,
    [int]$ExitGraceSeconds = 45
)

$ErrorActionPreference = 'Stop'
$scriptStart = Get-Date
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Engine) { $Engine = Join-Path $repoRoot 'Genesis Application' }
$Engine = [IO.Path]::GetFullPath($Engine).TrimEnd('\')
$playerDir = Join-Path $Engine 'Player'
$Project = [IO.Path]::GetFullPath($Project).TrimEnd('\')
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if ($ConfigFile) { $Config = & $ConfigFile }
if (-not $Config) { $Config = @{} }
# "powershell -File" passes "a,b" as one string
$Renderers = @($Renderers | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$Scenarios = @($Scenarios | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

function Get-Cfg($table, [string]$key, $default) {
    if ($table -and $table.ContainsKey($key) -and $null -ne $table[$key]) { return $table[$key] }
    return $default
}

$planPath = Get-Cfg $Config 'PlanPath' ''
$planConsumedPattern = Get-Cfg $Config 'PlanConsumedPattern' ''
$heartbeatPattern = Get-Cfg $Config 'HeartbeatPattern' ''
$errorPattern = Get-Cfg $Config 'ErrorPattern' 'SCRIPT ERROR'
$saveFolder = Get-Cfg $Config 'SaveFolder' ''
$testSaves = @(Get-Cfg $Config 'TestSaveFiles' @())
$guardSaves = @(Get-Cfg $Config 'GuardSaveFiles' @())
$plantSaves = Get-Cfg $Config 'PlantSaveFiles' @{}     # name -> file copied into SaveFolder for each run (the run's own settings)
$noShotsOn = @(Get-Cfg $Config 'NoScreenshotsOn' @())
$scenarioList = @(Get-Cfg $Config 'Scenarios' @(@{ Name = 'start'; Primary = $true; ReadyPattern = ''; QuitAt = 20 }))
if ($Scenarios.Count -gt 0) { $scenarioList = @($scenarioList | Where-Object { $Scenarios -contains $_.Name }) }
$planMarker = '# genesis-build-test'
$markerFile = '.genesis-build-test'
$shortNames = @{ Direct3D11 = 'DX11'; Direct3D12 = 'DX12'; Vulkan = 'Vulkan'; OpenGL = 'OpenGL' }
function Get-Short([string]$renderer) { if ($shortNames.ContainsKey($renderer)) { return $shortNames[$renderer] } return $renderer }

# ---------------------------------------------------------------------------------------------------------
# Win32 and process helpers (C# 5: compiled by Windows PowerShell 5.1)
# ---------------------------------------------------------------------------------------------------------
if (-not ('GbtGame' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public sealed class GbtLine
{
    public DateTime Time;
    public string Text;
    public bool IsError;
}

public sealed class GbtGame
{
    public Process Process;
    private readonly ConcurrentQueue<GbtLine> _lines = new ConcurrentQueue<GbtLine>();
    private Thread _out;
    private Thread _err;

    // Started without STARTF_USESHOWWINDOW, so the engine's own unattended placement decides how the
    // window is shown (hidden, then shown without activation and cloaked).
    public static GbtGame Start(string exe, string workDir, string[] setVariables, string stdoutPath, string stderrPath)
    {
        ProcessStartInfo info = new ProcessStartInfo(exe);
        info.UseShellExecute = false;
        info.WorkingDirectory = workDir;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.CreateNoWindow = true;
        info.StandardOutputEncoding = new UTF8Encoding(false);
        info.StandardErrorEncoding = new UTF8Encoding(false);
        List<string> inherited = new List<string>();
        foreach (string key in info.EnvironmentVariables.Keys) inherited.Add(key);
        foreach (string key in inherited)
        {
            if (key.StartsWith("genesis_", StringComparison.OrdinalIgnoreCase)) info.EnvironmentVariables.Remove(key);
        }
        foreach (string pair in setVariables)
        {
            int split = pair.IndexOf('=');
            info.EnvironmentVariables[pair.Substring(0, split)] = pair.Substring(split + 1);
        }
        GbtGame game = new GbtGame();
        game.Process = Process.Start(info);
        game._out = game.Pump(game.Process.StandardOutput, stdoutPath, false);
        game._err = game.Pump(game.Process.StandardError, stderrPath, true);
        return game;
    }

    private Thread Pump(StreamReader reader, string path, bool isError)
    {
        Thread thread = new Thread(delegate ()
        {
            using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                writer.AutoFlush = true;
                string text;
                while ((text = reader.ReadLine()) != null)
                {
                    writer.WriteLine(text);
                    GbtLine line = new GbtLine();
                    line.Time = DateTime.Now;
                    line.Text = text;
                    line.IsError = isError;
                    _lines.Enqueue(line);
                }
            }
        });
        thread.IsBackground = true;
        thread.Start();
        return thread;
    }

    public GbtLine[] Drain()
    {
        List<GbtLine> list = new List<GbtLine>();
        GbtLine line;
        while (_lines.TryDequeue(out line)) list.Add(line);
        return list.ToArray();
    }

    public void JoinPumps(int milliseconds)
    {
        _out.Join(milliseconds);
        _err.Join(milliseconds);
    }
}

public static class GbtWin
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    // The game's GLFW window (class GLFW30), whether or not it is visible yet.
    public static IntPtr FindGameWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr hwnd, IntPtr unused)
        {
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);
            if (owner != (uint)processId) return true;
            StringBuilder name = new StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() == "GLFW30") { found = hwnd; return false; }      // not the hidden "GLFW3 Helper"
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // WM_NULL through the window's message loop: false while the game thread is stuck in a frame.
    public static bool Responds(IntPtr hwnd, int timeoutMs)
    {
        IntPtr result;
        return SendMessageTimeout(hwnd, 0, IntPtr.Zero, IntPtr.Zero, 2, (uint)timeoutMs, out result) != IntPtr.Zero;
    }

    public static int[] GetRect(IntPtr hwnd)
    {
        RECT r;
        if (!GetWindowRect(hwnd, out r)) return new int[] { 0, 0, 0, 0 };
        return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }

    public static int[] GetClientSize(IntPtr hwnd)
    {
        RECT r;
        if (!GetClientRect(hwnd, out r)) return new int[] { 0, 0 };
        return new int[] { r.Right - r.Left, r.Bottom - r.Top };
    }

    public static int Cloaked(IntPtr hwnd)
    {
        int value;
        return DwmGetWindowAttribute(hwnd, 14, out value, 4) == 0 ? value : -1;
    }

    // A key press posted to the window (no focus change, nothing sent to other programs).
    public static void PressKey(IntPtr hwnd, int virtualKey, int scanCode)
    {
        PostMessage(hwnd, 0x0100, new IntPtr(virtualKey), new IntPtr(1 | (scanCode << 16)));
        Thread.Sleep(80);
        PostMessage(hwnd, 0x0101, new IntPtr(virtualKey), new IntPtr(unchecked((int)(0xC0000001u | ((uint)scanCode << 16)))));
    }
}
'@
}

# ---------------------------------------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------------------------------------
$utf8 = New-Object Text.UTF8Encoding($false)
$script:testLog = $null
$script:pending = New-Object System.Collections.Generic.List[string]
function Write-Log([string]$message) {
    $line = (Get-Date -Format 'HH:mm:ss') + ' ' + $message
    Write-Host $line
    if ($script:testLog) { [IO.File]::AppendAllText($script:testLog, $line + "`r`n", $utf8) } else { $script:pending.Add($line) }
}
function Quote([string]$text) { if ($text -match '[\s"]') { return '"' + ($text -replace '"', '\"') + '"' } return $text }
function Format-Bytes([double]$bytes) { if ($bytes -ge 1GB) { return ('{0:N1} GB' -f ($bytes / 1GB)) } return ('{0:N0} MB' -f ($bytes / 1MB)) }
function Get-FolderBytes([string]$path) {
    $total = [double]0
    if (Test-Path -LiteralPath $path) { foreach ($f in [IO.Directory]::EnumerateFiles($path, '*', 'AllDirectories')) { $total += (New-Object IO.FileInfo $f).Length } }
    return $total
}
function Get-FreeBytes([string]$path) { return (New-Object IO.DriveInfo ([IO.Path]::GetPathRoot($path))).AvailableFreeSpace }

# Runs a console program at below-normal priority, its output into a log file; returns the exit code.
function Invoke-Native([string]$exe, [string[]]$arguments, [string]$log) {
    $err = $log + '.err'
    $p = Start-Process -FilePath $exe -ArgumentList (($arguments | ForEach-Object { Quote $_ }) -join ' ') -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $err
    $null = $p.Handle
    try { $p.PriorityClass = 'BelowNormal' } catch { }
    $p.WaitForExit()
    if ((Test-Path -LiteralPath $err) -and (Get-Item -LiteralPath $err).Length -gt 0) { Add-Content -LiteralPath $log -Value (Get-Content -LiteralPath $err) }
    Remove-Item -LiteralPath $err -ErrorAction SilentlyContinue
    return $p.ExitCode
}

function Stop-Tree([System.Diagnostics.Process]$process) {
    if ($process -and -not $process.HasExited) {
        & taskkill.exe /PID $process.Id /T /F 2>&1 | Out-Null
        $process.WaitForExit(10000) | Out-Null
    }
}

function Test-OurPlan {
    if (-not $planPath -or -not (Test-Path -LiteralPath $planPath)) { return $false }
    $first = Get-Content -LiteralPath $planPath -TotalCount 1 -ErrorAction SilentlyContinue
    return ($first -and $first.StartsWith($planMarker))
}
function Remove-OurPlan([string]$why) {
    if (Test-OurPlan) { Remove-Item -LiteralPath $planPath -Force; Write-Log "plan removed ($why)" }
}

# ---------------------------------------------------------------------------------------------------------
# One test at a time across the machine, and never while someone else's run or game could read our plan
# ---------------------------------------------------------------------------------------------------------
$script:mutex = New-Object System.Threading.Mutex($false, 'Global\GenesisAgentTestRun')
$script:mutexHeld = $false
$script:waited = [TimeSpan]::Zero
$script:exeBase = ''
function Get-BusyReason {
    if ($GoFile -and -not (Test-Path -LiteralPath $GoFile)) { return "no game starts until $GoFile exists (an explicit go)" }
    if ($planPath -and (Test-Path -LiteralPath $planPath)) { return "a test plan from another run is at $planPath" }
    foreach ($p in @(Get-Process -Name GenesisEngine -ErrorAction SilentlyContinue)) {
        $path = $null; try { $path = $p.Path } catch { }
        if ($path -and $path.StartsWith($playerDir, [StringComparison]::OrdinalIgnoreCase)) { return "the published Player is running (pid $($p.Id))" }
    }
    if ($script:exeBase) {
        foreach ($p in @(Get-Process -Name $script:exeBase -ErrorAction SilentlyContinue)) {
            $path = $null; try { $path = $p.Path } catch { }
            if (-not ($path -and $path.StartsWith($OutputRoot, [StringComparison]::OrdinalIgnoreCase))) { return "'$($script:exeBase)' is running from another folder (pid $($p.Id)): someone is playing it" }
        }
    }
    return $null
}
function Enter-Gate {
    $started = Get-Date
    try {
        while ($true) {
            $left = [TimeSpan]::FromMinutes($MaxWaitMinutes) - $script:waited - ((Get-Date) - $started)
            if ($left.TotalSeconds -le 0) { return "not run: waited $MaxWaitMinutes min in total for other test runs or games" }
            $got = $false
            try { $got = $script:mutex.WaitOne($left) }
            catch {
                # another test process ended without releasing it: the mutex is ours now
                if ($_.Exception -is [System.Threading.AbandonedMutexException] -or $_.Exception.InnerException -is [System.Threading.AbandonedMutexException]) { $got = $true } else { throw }
            }
            if (-not $got) { continue }
            $script:mutexHeld = $true
            $busy = Get-BusyReason
            if (-not $busy) { $script:lastBusy = $null; return $true }
            if ($busy -ne $script:lastBusy) { Write-Log "waiting: $busy" }
            $script:lastBusy = $busy
            Exit-Gate
            Start-Sleep -Seconds 15
        }
    }
    finally { $script:waited += ((Get-Date) - $started) }
}
function Exit-Gate { if ($script:mutexHeld) { $script:mutex.ReleaseMutex(); $script:mutexHeld = $false } }

# ---------------------------------------------------------------------------------------------------------
# Save files the game writes during a run (backed up first, put back after)
# ---------------------------------------------------------------------------------------------------------
# Every file in the save folder is backed up before a run and any that changed is put back after it. Only
# files matching TestSaveFiles that the run created are deleted; anything else new is reported and kept.
# Files in PlantSaveFiles are copied in for the run (the run's own settings) and put back silently.
# Between runs nothing may change: GuardSaveFiles changed while no run was active stops the test.
$script:savesBetween = $null
function Get-SaveHashes {
    $hashes = @{}
    if ($saveFolder -and (Test-Path -LiteralPath $saveFolder)) {
        foreach ($f in @(Get-ChildItem -LiteralPath $saveFolder -File)) { $hashes[$f.Name] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash }
    }
    return $hashes
}
function Get-ChangedSinceLastRun {
    $changed = @()
    if ($null -eq $script:savesBetween) { return $changed }
    $now = Get-SaveHashes
    foreach ($name in @($script:savesBetween.Keys + $now.Keys | Select-Object -Unique)) {
        if ($script:savesBetween[$name] -ne $now[$name]) { $changed += $name }
    }
    return $changed
}
function Backup-Saves([string]$dir) {
    $state = @{}
    if (-not $saveFolder -or -not (Test-Path -LiteralPath $saveFolder)) { return $state }
    $backup = Join-Path $dir 'saves-before'
    New-Item -ItemType Directory -Force $backup | Out-Null
    foreach ($f in @(Get-ChildItem -LiteralPath $saveFolder -File)) {
        Copy-Item -LiteralPath $f.FullName (Join-Path $backup $f.Name) -Force
        $state[$f.Name] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
    }
    $log = Join-Path $dir 'saves-restore.log'
    foreach ($name in @($state.Keys)) { [IO.File]::AppendAllText($log, ('{0:HH:mm:ss.fff} snapshot {1} {2}' -f (Get-Date), $name, $state[$name]) + "`r`n", $utf8) }
    foreach ($name in @($plantSaves.Keys)) {
        $target = Join-Path $saveFolder $name
        Copy-Item -LiteralPath $plantSaves[$name] $target -Force
        (Get-Item -LiteralPath $target).LastWriteTime = Get-Date      # a planted copy must not look like an old write
        [IO.File]::AppendAllText($log, ('{0:HH:mm:ss.fff} planted {1} {2} (from {3})' -f (Get-Date), $name, (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash, $plantSaves[$name]) + "`r`n", $utf8)
    }
    return $state
}
# Puts back, from the run's own snapshot, every file whose hash differs (the planted copy included), then
# reads each hash back. Returns the names that still differ (should be none).
function Restore-Saves([string]$dir, $state, $notes) {
    if (-not $saveFolder -or -not (Test-Path -LiteralPath $saveFolder)) { return @() }
    $backup = Join-Path $dir 'saves-before'
    $log = Join-Path $dir 'saves-restore.log'
    $bad = @()
    foreach ($name in @($state.Keys)) {
        $file = Join-Path $saveFolder $name
        $found = $(if (Test-Path -LiteralPath $file) { (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash } else { 'missing' })
        $action = 'unchanged'
        if ($found -ne $state[$name]) {
            Copy-Item -LiteralPath (Join-Path $backup $name) $file -Force
            $action = 'put back'
            if (-not $plantSaves.ContainsKey($name)) { $notes.Add("$name was changed during the run and has been put back") }
        }
        $after = $(if (Test-Path -LiteralPath $file) { (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash } else { 'missing' })
        $ok = ($after -eq $state[$name])
        if (-not $ok) { $bad += $name; $notes.Add("RESTORE FAILED: $name is $after, the snapshot was $($state[$name])") }
        [IO.File]::AppendAllText($log, ('{0:HH:mm:ss.fff} {1} {2}: found {3}, {4}, now {5} ({6})' -f (Get-Date), $(if ($ok) { 'ok' } else { 'FAILED' }), $name, $found, $action, $after, $(if ($ok) { 'matches the snapshot' } else { 'DIFFERS FROM THE SNAPSHOT' })) + "`r`n", $utf8)
    }
    foreach ($f in @(Get-ChildItem -LiteralPath $saveFolder -File)) {
        if ($state.ContainsKey($f.Name)) { continue }
        $isTest = $plantSaves.ContainsKey($f.Name) -or @($testSaves | Where-Object { $f.Name -like $_ }).Count -gt 0
        if ($isTest) { Remove-Item -LiteralPath $f.FullName -Force; [IO.File]::AppendAllText($log, ('{0:HH:mm:ss.fff} removed {1} (made by the run)' -f (Get-Date), $f.Name) + "`r`n", $utf8) }
        else { $notes.Add("$($f.Name) appeared during the run (left in place)"); [IO.File]::AppendAllText($log, ('{0:HH:mm:ss.fff} kept {1} (appeared during the run, not a test file)' -f (Get-Date), $f.Name) + "`r`n", $utf8) }
    }
    $script:savesBetween = Get-SaveHashes
    return $bad
}

# ---------------------------------------------------------------------------------------------------------
# Logs
# ---------------------------------------------------------------------------------------------------------
function ConvertTo-LogTime([string]$hms, [datetime]$day) {
    $t = [datetime]::ParseExact($hms, 'HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)
    $value = $day.Date.Add($t.TimeOfDay)
    if ($value -lt $day.AddMinutes(-5)) { $value = $value.AddDays(1) }
    return $value
}
function Read-PlayerLog([string]$path, [datetime]$day) {
    $entries = New-Object System.Collections.Generic.List[object]
    if (-not (Test-Path -LiteralPath $path)) { return $entries }
    foreach ($line in [IO.File]::ReadAllLines($path, $utf8)) {
        if ($line -match '^(\d\d:\d\d:\d\d\.\d{3})\s+(.*)$') { $entries.Add(@{ Time = (ConvertTo-LogTime $Matches[1] $day); Text = $Matches[2] }) }
    }
    return $entries
}
function Get-SlowFrames($entries) {
    $frames = New-Object System.Collections.Generic.List[object]
    foreach ($e in $entries) {
        if ($e.Text -match '^Slow frame: (\d+) ms in (.+?) \(frame (\d+), ([\d.]+) s after the room began\): (.*)$') {
            $ms = [int]$Matches[1]; $rest = $Matches[5]
            $loading = ''; if ($rest -match 'loading in that frame: (.*?); garbage') { $loading = $Matches[1] }
            $frames.Add(@{ Time = $e.Time; Start = $e.Time.AddMilliseconds(-$ms); Ms = $ms; Loading = $loading; Text = $e.Text })
        }
    }
    return $frames
}
function Get-Worst($frames, $stalls, [datetime]$from, [datetime]$to) {
    $worst = $null
    foreach ($f in $frames) { if ($f.Start -ge $from -and $f.Start -le $to -and (-not $worst -or $f.Ms -gt $worst.Ms)) { $worst = @{ Ms = $f.Ms; Start = $f.Start; Loading = $f.Loading; Source = 'frame' } } }
    foreach ($s in $stalls) { if ($s.Start -ge $from -and $s.Start -le $to -and (-not $worst -or $s.Ms -gt $worst.Ms)) { $worst = @{ Ms = [int]$s.Ms; Start = $s.Start; Loading = ''; Source = 'stall' } } }
    return $worst
}
function First-Line($lines, [string]$pattern, [datetime]$after) {
    foreach ($l in $lines) { if (-not $l.IsError -and $l.Time -ge $after -and $l.Text -match $pattern) { return $l } }
    return $null
}
function Short([string]$text, [int]$max) { if (-not $text) { return '' } $t = $text.Trim(); if ($t.Length -le $max) { return $t } return $t.Substring(0, $max - 3) + '...' }
function Set-Check($run, [string]$name, [string]$status, [string]$short, [string]$detail) {
    $run.Checks[$name] = [ordered]@{ Status = $status; Short = $short; Detail = $detail }
}

# ---------------------------------------------------------------------------------------------------------
# One run of the exported game
# ---------------------------------------------------------------------------------------------------------
function Invoke-GameRun([string]$renderer, $scenario, $ctx) {
    $label = (Get-Short $renderer) + '-' + $scenario.Name
    $dir = Join-Path $ctx.RunsDir $label
    New-Item -ItemType Directory -Force $dir | Out-Null
    $runLog = Join-Path $dir 'steps.log'
    $run = [ordered]@{
        Renderer = $renderer; Scenario = $scenario.Name; Label = $label; Dir = $dir; NotRun = $null; Pid = 0
        Started = $null; Ready = $null; ExitCode = $null; Exited = $null; Killed = $null; Window = ''
        Notes = (New-Object System.Collections.Generic.List[string]); Checks = [ordered]@{}
    }
    function Log-Run([string]$m) { $l = (Get-Date -Format 'HH:mm:ss.fff') + ' ' + $m; [IO.File]::AppendAllText($runLog, $l + "`r`n", $utf8); Write-Log "  [$label] $m" }

    # the shader cache exactly as the export shipped it
    $cache = Join-Path $dir 'ShaderCache'
    if (Test-Path -LiteralPath $cache) { Remove-Item -LiteralPath $cache -Recurse -Force }
    if (Test-Path -LiteralPath $ctx.PackagedCache) { Copy-Item -LiteralPath $ctx.PackagedCache $cache -Recurse } else { New-Item -ItemType Directory $cache | Out-Null }
    $cacheBefore = @{}
    foreach ($f in [IO.Directory]::EnumerateFiles($cache, '*', 'AllDirectories')) { $cacheBefore[$f] = $true }

    $planLines = @()
    if ($planPath -and $scenario.Plan) {
        $planLines = @("$planMarker $label $(Get-Date -Format s)")
        foreach ($l in $scenario.Plan) { if (($noShotsOn -contains $renderer) -and $l -match '^\s*at\s+\S+\s+shot\b') { continue }; $planLines += $l }
        [IO.File]::WriteAllText((Join-Path $dir 'plan.txt'), ($planLines -join "`r`n"), $utf8)
    }

    $gate = Enter-Gate
    if ($gate -ne $true) { $run.NotRun = $gate; Write-Log "  [$label] $gate"; return $run }
    $changed = @(Get-ChangedSinceLastRun | Where-Object { $guardSaves -contains $_ })
    if ($changed.Count -gt 0) {
        Exit-Gate
        throw "save files changed while no run of this test was active: $($changed -join ', ') in $saveFolder. Stopped; nothing was restored or overwritten."
    }

    $game = $null; $saveState = @{}
    $lines = New-Object System.Collections.Generic.List[object]
    $beats = New-Object System.Collections.Generic.List[datetime]
    $stalls = New-Object System.Collections.Generic.List[object]
    $done = New-Object System.Collections.Generic.List[object]
    $playerLog = Join-Path $ctx.GameDir 'Debug\Logs\project_player.log'
    $crashBeside = Join-Path $ctx.GameDir 'GenesisEngine.crash.log'
    $crashShared = Join-Path $env:LOCALAPPDATA 'Genesis\CrashReports\GenesisEngine.crash.log'
    $crashOffsets = @{}
    foreach ($c in @($crashBeside, $crashShared)) { $crashOffsets[$c] = $(if (Test-Path -LiteralPath $c) { (Get-Item -LiteralPath $c).Length } else { 0 }) }
    $hwnd = [IntPtr]::Zero
    $ready = $null
    $planGone = ($planLines.Count -eq 0)
    $spanStart = $null
    $startTimeout = [int](Get-Cfg $scenario 'StartTimeoutSeconds' 240)
    $steps = @(@(Get-Cfg $scenario 'Steps' @()) | Sort-Object { [double]$_.At })
    $nextStep = 0
    $win = @{}
    $readyPattern = Get-Cfg $scenario 'ReadyPattern' ''
    try {
        $saveState = Backup-Saves $dir
        if ($planLines.Count -gt 0) {
            [IO.File]::WriteAllText($planPath, ($planLines -join "`n"), [Text.Encoding]::ASCII)
            Log-Run "plan written to $planPath ($($planLines.Count - 1) lines)"
        }
        if (Test-Path -LiteralPath $playerLog) { Remove-Item -LiteralPath $playerLog -Force }

        $gameVariables = @(
            "GENESIS_RENDER_BACKEND=$renderer", 'GENESIS_UNATTENDED_WINDOW=1', 'GENESIS_UNATTENDED_AUDIO=0', 'GENESIS_ALLOW_ESCAPE=0',
            "GENESIS_SHADER_CACHE=$cache", "GENESIS_PERF_LOG_DIR=$dir", "GENESIS_PERF_LABEL=$label")
        $run.Started = Get-Date
        $game = [GbtGame]::Start($ctx.GameExe, $ctx.GameDir, [string[]]$gameVariables, (Join-Path $dir 'stdout.log'), (Join-Path $dir 'stderr.log'))
        $proc = $game.Process
        try { $proc.PriorityClass = 'BelowNormal' } catch { }
        $run.Pid = $proc.Id
        Log-Run "started pid $($proc.Id) on $renderer"

        $deadline = $run.Started.AddSeconds($startTimeout)
        if (-not $readyPattern) { $ready = $run.Started; $deadline = $ready.AddSeconds([double](Get-Cfg $scenario 'QuitAt' 20) + $ExitGraceSeconds) }

        while ($true) {
            foreach ($l in $game.Drain()) {
                $lines.Add($l)
                if ($l.IsError) { continue }
                if (-not $planGone -and $planConsumedPattern -and $l.Text -match $planConsumedPattern) { Remove-OurPlan 'the game has read it'; $planGone = $true }
                if (-not $ready -and $readyPattern -and $l.Text -match $readyPattern) {
                    $ready = $l.Time
                    $deadline = $ready.AddSeconds([double](Get-Cfg $scenario 'QuitAt' 20) + $ExitGraceSeconds)
                    Log-Run ("ready after {0:N1} s: {1}" -f ($ready - $run.Started).TotalSeconds, (Short $l.Text 100))
                }
                if ($heartbeatPattern -and $l.Text -match $heartbeatPattern) { $beats.Add($l.Time) }
            }
            if ($proc.HasExited) { break }
            $now = Get-Date

            if ($hwnd -eq [IntPtr]::Zero) {
                $hwnd = [GbtWin]::FindGameWindow($proc.Id)
                if ($hwnd -ne [IntPtr]::Zero) {
                    $r = [GbtWin]::GetRect($hwnd)
                    $run.Window = ('{0}x{1} at {2},{3}, cloaked={4}, visible={5}' -f $r[2], $r[3], $r[0], $r[1], [GbtWin]::Cloaked($hwnd), [GbtWin]::IsWindowVisible($hwnd))
                    Log-Run "window found: $($run.Window)"
                }
            }
            if ($hwnd -ne [IntPtr]::Zero) {
                if (-not [GbtWin]::IsWindow($hwnd)) { $hwnd = [IntPtr]::Zero }
                else {
                    $t0 = Get-Date
                    $ok = [GbtWin]::Responds($hwnd, 2000)
                    $t1 = Get-Date
                    if ($ok) {
                        if ($spanStart) {
                            $ms = ($t1 - $spanStart).TotalMilliseconds
                            $stalls.Add(@{ Start = $spanStart; Ms = $ms }); Log-Run ("window did not answer for {0:N0} ms" -f $ms); $spanStart = $null
                        } elseif (($t1 - $t0).TotalMilliseconds -ge 200) { $stalls.Add(@{ Start = $t0; Ms = ($t1 - $t0).TotalMilliseconds }) }
                    } elseif (-not $spanStart) { $spanStart = $t0 }
                }
            }

            if ($ready) {
                while ($nextStep -lt $steps.Count -and $now -ge $ready.AddSeconds([double]$steps[$nextStep].At)) {
                    $s = $steps[$nextStep]; $nextStep++
                    $entry = @{ Step = $s; Done = (Get-Date); StateOk = $null; StateDetail = ''; BeatOk = $null; BeatDetail = ''; Skipped = $false }
                    if ($s.Do -eq 'f11' -and -not $IncludeF11) { $entry.Skipped = $true; $done.Add($entry); continue }
                    if ($hwnd -eq [IntPtr]::Zero) { $entry.StateOk = $false; $entry.StateDetail = 'no game window'; $entry.BeatOk = $false; $done.Add($entry); continue }
                    $before = [GbtWin]::GetRect($hwnd)
                    switch ($s.Do) {
                        'minimise'   { $win.BeforeMinimise = $before; [void][GbtWin]::ShowWindowAsync($hwnd, 7) }       # SW_SHOWMINNOACTIVE
                        'restore'    { [void][GbtWin]::ShowWindowAsync($hwnd, 4) }                                     # SW_SHOWNOACTIVATE
                        'away'       { [void][GbtWin]::SetWindowPos($hwnd, [IntPtr]1, 0, 0, 0, 0, 0x4013) }            # HWND_BOTTOM, no move/size/activate, async
                        'back'       { [void][GbtWin]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, 0, 0, 0x4013) }       # HWND_TOP, no activation
                        'resize'     { if (-not $win.BeforeResize) { $win.BeforeResize = $before }; [void][GbtWin]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, [int]$s.Width, [int]$s.Height, 0x4016) }
                        'resizeback' { $b = $win.BeforeResize; if ($b) { [void][GbtWin]::SetWindowPos($hwnd, [IntPtr]::Zero, $b[0], $b[1], $b[2], $b[3], 0x4014) } }
                        'f11'        { $win.BeforeF11 = $before; [GbtWin]::PressKey($hwnd, 0x7A, 0x57) }
                    }
                    $entry.Before = $before
                    $entry.Done = Get-Date
                    $entry.StateAt = $entry.Done.AddSeconds(2)
                    $entry.BeatBy = $entry.Done.AddSeconds(12)
                    Log-Run ("step {0} at +{1} s (window was {2}x{3} at {4},{5})" -f $s.Do, $s.At, $before[2], $before[3], $before[0], $before[1])
                    $done.Add($entry)
                }
            }
            foreach ($e in $done) {
                if ($e.Skipped) { continue }
                if ($null -eq $e.StateOk -and $now -ge $e.StateAt -and $hwnd -ne [IntPtr]::Zero) {
                    $answers = [GbtWin]::Responds($hwnd, 3000)
                    $r = [GbtWin]::GetRect($hwnd); $iconic = [GbtWin]::IsIconic($hwnd); $client = [GbtWin]::GetClientSize($hwnd)
                    $same = { param($a, $b) $a -and $b -and $a[0] -eq $b[0] -and $a[1] -eq $b[1] -and $a[2] -eq $b[2] -and $a[3] -eq $b[3] }
                    switch ($e.Step.Do) {
                        'minimise'   { $ok = $iconic }
                        'restore'    { $ok = (-not $iconic) -and (& $same $r $win.BeforeMinimise) }
                        'resize'     { $ok = ($r[2] -eq [int]$e.Step.Width -and $r[3] -eq [int]$e.Step.Height) }
                        'resizeback' { $ok = (& $same $r $win.BeforeResize) }
                        'f11'        { $ok = -not (& $same $r $e.Before) }
                        default      { $ok = $true }
                    }
                    $e.StateOk = ($ok -and $answers)
                    $e.StateDetail = ('window {0}x{1} at {2},{3}, client {4}x{5}, minimised={6}, answering={7}' -f $r[2], $r[3], $r[0], $r[1], $client[0], $client[1], $iconic, $answers)
                    Log-Run ("check after {0}: {1} ({2})" -f $e.Step.Do, $(if ($e.StateOk) { 'ok' } else { 'NOT OK' }), $e.StateDetail)
                }
                if ($null -eq $e.BeatOk) {
                    $expect = $true; if ($e.Step.ContainsKey('ExpectBeat')) { $expect = [bool]$e.Step.ExpectBeat } elseif ($e.Step.Do -eq 'minimise') { $expect = $false }
                    if (-not $expect -or -not $heartbeatPattern) { $e.BeatOk = $true }
                    else {
                        $after = $null; foreach ($b in $beats) { if ($b -gt $e.Done) { $after = $b; break } }
                        if ($after) { $e.BeatOk = $true; $e.BeatDetail = ('frames running again {0:N1} s later' -f ($after - $e.Done).TotalSeconds) }
                        elseif ($now -ge $e.BeatBy) { $e.BeatOk = $false; $e.BeatDetail = 'no frames for 12 s afterwards'; Log-Run "no heartbeat 12 s after $($e.Step.Do)" }
                    }
                }
            }

            if ($now -gt $deadline) {
                if (-not $ready) { $run.Killed = "no '$readyPattern' within $startTimeout s" } else { $run.Killed = "still running $ExitGraceSeconds s after the plan's end" }
                Log-Run "stopping: $($run.Killed)"; Stop-Tree $proc; break
            }
            if ($spanStart -and ((Get-Date) - $spanStart).TotalSeconds -gt $HangSeconds) {
                $stalls.Add(@{ Start = $spanStart; Ms = ((Get-Date) - $spanStart).TotalMilliseconds })
                $run.Killed = "window stopped answering for over $HangSeconds s (hung)"
                Log-Run "stopping: $($run.Killed)"; Stop-Tree $proc; $spanStart = $null; break
            }
            Start-Sleep -Milliseconds 100
        }
        if ($spanStart) { $stalls.Add(@{ Start = $spanStart; Ms = ((Get-Date) - $spanStart).TotalMilliseconds; Open = $true }) }
        $proc.WaitForExit(15000) | Out-Null
        $game.JoinPumps(5000)
        foreach ($l in $game.Drain()) { $lines.Add($l); if (-not $l.IsError -and $heartbeatPattern -and $l.Text -match $heartbeatPattern) { $beats.Add($l.Time) } }
        if ($proc.HasExited) { $run.ExitCode = $proc.ExitCode; $run.Exited = $proc.ExitTime }
        Log-Run ("ended: exit code {0}{1}" -f $run.ExitCode, $(if ($run.Killed) { ", stopped by the test: $($run.Killed)" } else { '' }))
    }
    catch {
        $run.Killed = "the test itself failed: $($_.Exception.Message)"
        Log-Run $run.Killed
    }
    finally {
        if ($game -and -not $game.Process.HasExited) { Stop-Tree $game.Process }
        Remove-OurPlan 'run finished'
        $run.RestoreFailed = @(Restore-Saves $dir $saveState $run.Notes)
        Exit-Gate
    }
    $run.Ready = $ready
    if (-not $run.Started) { $run.Started = Get-Date }

    # ---- collect ----
    if (Test-Path -LiteralPath $playerLog) { Copy-Item -LiteralPath $playerLog (Join-Path $dir 'project_player.log') -Force }
    $renderLog = Join-Path $env:LOCALAPPDATA ("GenesisRuntime\Logs\render-{0}.log" -f $run.Pid)
    if ($run.Pid -and (Test-Path -LiteralPath $renderLog)) { Copy-Item -LiteralPath $renderLog (Join-Path $dir 'render.log') -Force }
    $crashText = ''
    foreach ($c in @($crashBeside, $crashShared)) {
        if ((Test-Path -LiteralPath $c) -and (Get-Item -LiteralPath $c).Length -gt $crashOffsets[$c]) {
            $fs = [IO.File]::Open($c, 'Open', 'Read', 'ReadWrite'); try { [void]$fs.Seek($crashOffsets[$c], 'Begin'); $crashText += (New-Object IO.StreamReader($fs, $utf8)).ReadToEnd() } finally { $fs.Dispose() }
        }
    }
    if ($crashText) { [IO.File]::WriteAllText((Join-Path $dir 'crash.log'), $crashText, $utf8) }
    $images = Join-Path $ctx.GameDir 'Debug\Images'
    if (Test-Path -LiteralPath $images) {
        Get-ChildItem -LiteralPath $images -Filter *.png | Where-Object { $_.LastWriteTime -ge $run.Started } | ForEach-Object { Move-Item -LiteralPath $_.FullName (Join-Path $dir $_.Name) -Force }
    }
    $newCache = @(); foreach ($f in [IO.Directory]::EnumerateFiles($cache, '*', 'AllDirectories')) { if (-not $cacheBefore.ContainsKey($f)) { $newCache += $f.Substring($cache.Length + 1) } }
    [IO.File]::WriteAllLines((Join-Path $dir 'shader-cache-new.txt'), [string[]]$newCache, $utf8)
    Remove-Item -LiteralPath $cache -Recurse -Force -ErrorAction SilentlyContinue

    $entries = Read-PlayerLog (Join-Path $dir 'project_player.log') $run.Started
    $allFrames = @(Get-SlowFrames $entries)
    # the engine idles while minimised, so the frame that spans a minimise is long by design: not a hitch
    $minimised = @()
    foreach ($e in $done) {
        if ($e.Skipped -or $e.Step.Do -ne 'minimise') { continue }
        $back = $done | Where-Object { -not $_.Skipped -and $_.Step.Do -eq 'restore' -and $_.Done -gt $e.Done } | Select-Object -First 1
        $minimised += , @($e.Done, $(if ($back) { $back.Done.AddSeconds(1) } else { [datetime]::MaxValue }))
    }
    $frames = @($allFrames | Where-Object { $f = $_; -not ($minimised | Where-Object { $f.Start -lt $_[1] -and $f.Time -gt $_[0] }) })
    $slowOut = @($allFrames | ForEach-Object { '{0:HH:mm:ss.fff} {1}{2}' -f $_.Time, $_.Text, $(if ($frames -contains $_) { '' } else { '  [spans the minimised window: not counted]' }) })
    $slowOut += @($lines | Where-Object { -not $_.IsError -and $_.Text -match 'SLOWDRAW (\d+)$' -and [int]$Matches[1] -ge 50 } | ForEach-Object { '{0:HH:mm:ss.fff} stdout {1}' -f $_.Time, $_.Text })
    [IO.File]::WriteAllLines((Join-Path $dir 'slow-frames.txt'), [string[]]$slowOut, $utf8)
    [IO.File]::WriteAllLines((Join-Path $dir 'stalls.txt'), [string[]]@($stalls | ForEach-Object { '{0:HH:mm:ss.fff} window did not answer for {1:N0} ms' -f $_.Start, $_.Ms }), $utf8)
    $shaderOut = @($entries | Where-Object { $_.Text -match 'shader' } | ForEach-Object { '{0:HH:mm:ss.fff} {1}' -f $_.Time, $_.Text })
    if (Test-Path -LiteralPath (Join-Path $dir 'render.log')) { $shaderOut += @(Get-Content -LiteralPath (Join-Path $dir 'render.log') | Where-Object { $_ -match 'shader|compil|pipeline' } | ForEach-Object { "render.log: $_" }) }
    [IO.File]::WriteAllLines((Join-Path $dir 'shaders.txt'), [string[]]$shaderOut, $utf8)

    # ---- judge ----
    $exitText = ''
    if ($null -ne $run.ExitCode) { $exitText = $(if ($run.ExitCode -lt 0 -or $run.ExitCode -gt 255) { '0x{0:X8}' -f $run.ExitCode } else { "$($run.ExitCode)" }) }
    $problemLines = @()
    if ($crashText) { $problemLines += @(($crashText -split "`r?`n") | Where-Object { $_ -match '\S' -and $_ -notmatch 'GenesisEngine crashed' } | Select-Object -First 2) }
    $problemLines += @($lines | Where-Object { $_.IsError -and $_.Text -match '\S' } | Select-Object -First 2 | ForEach-Object { $_.Text })
    $problemLines += @($entries | Where-Object { $_.Text -match '(?i)\b(error|exception|failed|fatal)\b' -and $_.Text -notmatch 'SCRIPT ERROR' } | Select-Object -First 2 | ForEach-Object { $_.Text })
    if (Test-Path -LiteralPath (Join-Path $dir 'render.log')) { $problemLines += @(Get-Content -LiteralPath (Join-Path $dir 'render.log') | Where-Object { $_ -match '(?i)\b(error|exception|failed|fatal|removed)\b' } | Select-Object -First 2) }
    $why = Short ((@($problemLines | Where-Object { $_ -and $_ -notmatch '^\s*-+\s*$' } | Select-Object -First 2) | ForEach-Object { $_.Trim() }) -join ' / ') 300
    $ended = $(if ($run.Killed) { "stopped by the test: $($run.Killed)" } elseif ($null -ne $run.ExitCode) { "the game exited with code $exitText" } else { 'the game ended' })
    $readyAt = $ready

    if (Get-Cfg $scenario 'Primary' $false) {
        if ($readyAt) { Set-Check $run 'Starts' 'PASS' ('{0:N0} s' -f ($readyAt - $run.Started).TotalSeconds) ('{0} after {1:N1} s' -f (Get-Cfg $scenario 'ReadyName' 'ready'), ($readyAt - $run.Started).TotalSeconds) }
        else { Set-Check $run 'Starts' 'FAIL' $(if ($run.Killed) { 'no start' } else { "exit $exitText" }) "$ended before '$readyPattern'. $why" }
    }

    foreach ($chk in @(Get-Cfg $scenario 'Checks' @())) {
        $kind = Get-Cfg $chk 'Kind' 'pattern'
        $name = $chk.Name
        if (-not $readyAt) {
            if ($kind -eq 'pattern') { Set-Check $run $name 'FAIL' 'no start' "the game never reached '$readyPattern': $ended. $why" }
            else { Set-Check $run $name 'NOT RUN' '' "the game never reached '$readyPattern' ($ended)" }
            continue
        }
        switch ($kind) {
            'pattern' {
                $hit = First-Line $lines $chk.Pattern $run.Started
                if ($hit) {
                    $offset = ($hit.Time - $(if (Get-Cfg $chk 'FromStart' $false) { $run.Started } else { $readyAt })).TotalSeconds
                    $d = ('{0:N1} s' -f $offset)
                    if ($chk.Detail -and $hit.Text -match $chk.Pattern) { $d = $chk.Detail -f @($Matches[0], $Matches[1], $Matches[2], $Matches[3]) }
                    Set-Check $run $name 'PASS' $d ("'{0}' at {1:N1} s" -f (Short $hit.Text 120), $offset)
                } else { Set-Check $run $name 'FAIL' 'not reached' "never printed '$($chk.Pattern)'; $ended. $why" }
            }
            'hitch' {
                $mark = First-Line $lines $chk.Marker $readyAt
                if (-not $mark) { Set-Check $run $name 'FAIL' 'not reached' "never printed '$($chk.Marker)'; $ended. $why"; continue }
                $seconds = [double](Get-Cfg $chk 'Seconds' 5)
                if ($chk.Confirm -and -not (First-Line $lines $chk.Confirm $mark.Time)) { Set-Check $run $name 'FAIL' 'not applied' "'$($chk.Confirm)' never printed after '$($chk.Marker)'; $ended"; continue }
                $w = Get-Worst $frames $stalls $mark.Time.AddMilliseconds(-200) $mark.Time.AddSeconds($seconds)
                $full = $entries | Where-Object { $_.Text -match 'no more are written for this room' -and $_.Time -lt $mark.Time } | Select-Object -First 1
                $note = $(if ($full) { ' (the slow-frame log was already full; stall probe only)' } else { '' })
                if (-not $w) { Set-Check $run $name 'PASS' 'no slow frame' ("no frame over 100 ms and no stall within {0} s{1}" -f $seconds, $note) }
                else {
                    $st = $(if ($w.Ms -ge 1000) { 'FAIL' } elseif ($w.Ms -ge 250) { 'WARN' } else { 'PASS' })
                    Set-Check $run $name $st ('{0} ms' -f $w.Ms) ('worst {0} {1} ms at +{2:N1} s{3}{4}' -f $w.Source, $w.Ms, ($w.Start - $mark.Time).TotalSeconds, $(if ($w.Loading) { " (loading: $($w.Loading))" } else { '' }), $note)
                }
            }
            'frames' {
                $from = $readyAt.AddSeconds([double](Get-Cfg $chk 'From' -1)); $to = $readyAt.AddSeconds([double](Get-Cfg $chk 'Seconds' 12))
                $list = @($frames | Where-Object { $_.Start -ge $from -and $_.Start -le $to } | Sort-Object { $_.Ms } -Descending)
                $w = Get-Worst $frames $stalls $from $to
                $mobs = ''
                if ($chk.CountPattern) {
                    foreach ($l in $lines) {
                        if (-not $l.IsError -and $l.Time -ge $readyAt -and $l.Text -match $chk.CountPattern) {
                            $n = 0; foreach ($m in [regex]::Matches($l.Text, '=(\d+)')) { $n += [int]$m.Groups[1].Value }
                            $mobs += ('{0}@+{1:N1}s ' -f $n, ($l.Time - $readyAt).TotalSeconds)
                        }
                    }
                }
                $top = ($list | Select-Object -First 3 | ForEach-Object { '{0} ms at +{1:N1} s ({2})' -f $_.Ms, ($_.Start - $readyAt).TotalSeconds, $(if ($_.Loading) { $_.Loading } else { 'no loading' }) }) -join '; '
                if (-not $w) { Set-Check $run $name 'PASS' 'none >100 ms' "no frame over 100 ms in the first $(Get-Cfg $chk 'Seconds' 12) s. creatures: $mobs" }
                else {
                    $st = $(if ($w.Ms -ge 1000) { 'FAIL' } elseif ($w.Ms -ge 100) { 'WARN' } else { 'PASS' })
                    Set-Check $run $name $st ('{0} ms +{1:N1}s' -f $w.Ms, ($w.Start - $readyAt).TotalSeconds) ("slow frames: $top. creatures (count@time): $mobs")
                }
            }
        }
    }

    # window steps, grouped by the check they belong to
    $groups = [ordered]@{}
    foreach ($s in @(Get-Cfg $scenario 'Steps' @())) { if ($s.Check -and -not $groups.Contains($s.Check)) { $groups[$s.Check] = $true } }
    # a crash or kill soon after the last window step is that step's failure, even if the game drew a frame first
    $executed = @($done | Where-Object { -not $_.Skipped })
    $lastStep = $(if ($executed.Count -gt 0) { $executed[$executed.Count - 1] } else { $null })
    $abnormal = ($run.Killed -or ($null -ne $run.ExitCode -and $run.ExitCode -ne 0))
    foreach ($g in $groups.Keys) {
        $planned = @(@(Get-Cfg $scenario 'Steps' @()) | Where-Object { $_.Check -eq $g })
        $ran = @($done | Where-Object { $_.Step.Check -eq $g })
        if (-not $IncludeF11 -and @($planned | Where-Object { $_.Do -ne 'f11' }).Count -eq 0) { Set-Check $run $g 'SKIP' 'switch off' 'not run: needs -IncludeF11 (a fullscreen game covers the monitor)'; continue }
        if (-not $readyAt) { Set-Check $run $g 'NOT RUN' '' "the game never reached '$readyPattern'"; continue }
        if ($ran.Count -eq 0) { Set-Check $run $g 'NOT RUN' '' "$ended before this step"; continue }
        $bad = @($ran | Where-Object { $_.StateOk -eq $false -or $_.BeatOk -eq $false })
        $open = @($ran | Where-Object { $null -eq $_.StateOk -or $null -eq $_.BeatOk })
        $last = $ran[$ran.Count - 1]
        if ($abnormal -and $run.Exited -and $lastStep -and $last.Done -eq $lastStep.Done -and $last.Step.Do -eq $lastStep.Step.Do -and ($run.Exited - $last.Done).TotalSeconds -lt 15) {
            Set-Check $run $g 'FAIL' $(if ($run.Killed) { 'hung' } else { "crash $exitText" }) ("{0} {1:N1} s after '{2}'. {3}" -f $ended, ($run.Exited - $last.Done).TotalSeconds, $last.Step.Do, $why)
        } elseif ($bad.Count -gt 0) {
            $b = $bad[0]
            Set-Check $run $g 'FAIL' $(if ($b.StateOk -eq $false) { "after $($b.Step.Do)" } else { 'stopped drawing' }) ("after '{0}': {1} {2}" -f $b.Step.Do, $b.StateDetail, $b.BeatDetail)
        } elseif ($open.Count -gt 0 -or $ran.Count -lt $planned.Count) {
            $since = $(if ($run.Exited) { ' {0:N1} s after ''{1}''' -f ($run.Exited - $last.Done).TotalSeconds, $last.Step.Do } else { '' })
            Set-Check $run $g 'FAIL' $(if ($null -ne $run.ExitCode -and $run.ExitCode -ne 0) { "crash $exitText" } else { 'game ended' }) ("$ended$since. $why")
        } else {
            Set-Check $run $g 'PASS' '' (($ran | ForEach-Object { "$($_.Step.Do): $($_.StateDetail) $($_.BeatDetail)" }) -join ' | ')
        }
    }

    if (-not $readyAt) { Set-Check $run 'Clean exit' 'NOT RUN' '' $ended }
    elseif ($run.Killed) { Set-Check $run 'Clean exit' 'FAIL' 'killed' $run.Killed }
    elseif ($run.ExitCode -eq 0) { Set-Check $run 'Clean exit' 'PASS' '' 'exit code 0' }
    else { Set-Check $run 'Clean exit' 'FAIL' "exit $exitText" "$ended. $why" }

    # (a) a first launch from the shipped cache: what was compiled on this machine, and what it cost
    if ($readyAt) {
        $proj = $entries | Where-Object { $_.Text -match '^project shaders: ' } | Select-Object -First 1
        $eng = $entries | Where-Object { $_.Text -match '^engine shaders so far: ' } | Select-Object -First 1
        $pRead = -1; $pCompiled = -1; $pTotal = -1; $eCompiled = -1; $ePre = -1
        if ($proj -and $proj.Text -match 'project shaders: (\d+) programs? for \w+ made on workers in \d+ ms \((\d+) read from the shader cache, (\d+) compiled') { $pTotal = [int]$Matches[1]; $pRead = [int]$Matches[2]; $pCompiled = [int]$Matches[3] }
        if ($eng -and $eng.Text -match 'engine shaders so far: (\d+) precompiled, (\d+) compiled here') { $ePre = [int]$Matches[1]; $eCompiled = [int]$Matches[2] }
        # only up to the first window step: what a step does (a resize, a crash) is judged by that step
        $until = [datetime]::MaxValue
        if ($executed.Count -gt 0) { $until = $executed[0].Done }
        $early = @($frames | Where-Object { $_.Start -lt $until })
        $shaderFrames = @($early | Where-Object { $_.Loading -match 'shader' } | Sort-Object { $_.Ms } -Descending)
        $worstShader = $(if ($shaderFrames.Count -gt 0) { $shaderFrames[0].Ms } else { 0 })
        $worstAny = 0; foreach ($f in $early) { if ($f.Ms -gt $worstAny) { $worstAny = $f.Ms } }
        $worstStall = 0; foreach ($s in $stalls) { if ($s.Start -ge $readyAt -and $s.Start -lt $until -and $s.Ms -gt $worstStall) { $worstStall = [int]$s.Ms } }
        $span = $(if ($until -lt [datetime]::MaxValue) { 'before the window steps' } else { 'in the run' })
        $detail = ('project shaders {0}/{1} from the shipped cache, {2} compiled; engine {3} precompiled, {4} compiled here; {5} new cache files; {9}: longest frame loading a shader {6} ms, longest frame {7} ms, longest stall after load {8} ms' -f $pRead, $pTotal, $pCompiled, $ePre, $eCompiled, $newCache.Count, $worstShader, $worstAny, $worstStall, $span)
        $st = 'PASS'
        if ($worstAny -ge 5000 -or $worstStall -ge 5000 -or $worstShader -ge 1000) { $st = 'FAIL' }
        elseif ($pCompiled -gt 0 -or $eCompiled -gt 0 -or $worstShader -ge 250 -or $newCache.Count -gt 0) { $st = 'WARN' }
        $sh = $(if ($pCompiled -ge 0) { "$pCompiled+$eCompiled compiled" } else { 'no summary' })
        if ($worstShader -gt 0) { $sh += ", $worstShader ms" }
        Set-Check $run 'First launch shaders (a)' $st $sh $detail
    } else { Set-Check $run 'First launch shaders (a)' 'NOT RUN' '' $ended }

    $scriptErrors = @($lines | Where-Object { $_.Text -match $errorPattern }) + @($entries | Where-Object { $_.Text -match $errorPattern })
    if ($scriptErrors.Count -eq 0) { Set-Check $run 'Script errors' 'PASS' 'none' 'none' }
    else { Set-Check $run 'Script errors' 'WARN' "$($scriptErrors.Count)" (Short ($scriptErrors[0].Text) 300) }

    # numbers for the timings table
    $fps = @($lines | Where-Object { -not $_.IsError -and $readyAt -and $_.Time -ge $readyAt -and $_.Text -match 'fps=(\d+)' } | ForEach-Object { [void]($_.Text -match 'fps=(\d+)'); [int]$Matches[1] } | Sort-Object)
    $run.FpsMedian = $(if ($fps.Count) { $fps[[int][math]::Floor($fps.Count / 2)] } else { $null })
    $run.FpsMin = $(if ($fps.Count) { $fps[0] } else { $null })
    $loaded = First-Line $lines 'LOADED in (\d+) ms' $run.Started
    $run.GameLoadMs = $(if ($loaded -and $loaded.Text -match 'LOADED in (\d+) ms') { [int]$Matches[1] } else { $null })
    $run.ReadySeconds = $(if ($readyAt) { [math]::Round(($readyAt - $run.Started).TotalSeconds, 1) } else { $null })
    $run.LongestStallMs = 0; foreach ($s in $stalls) { if ($readyAt -and $s.Start -ge $readyAt -and $s.Ms -gt $run.LongestStallMs) { $run.LongestStallMs = [int]$s.Ms } }
    $run.LongestLoadStallMs = 0; foreach ($s in $stalls) { if ((-not $readyAt -or $s.Start -lt $readyAt) -and $s.Ms -gt $run.LongestLoadStallMs) { $run.LongestLoadStallMs = [int]$s.Ms } }
    $run.Problem = $why
    $run.Images = @(Get-ChildItem -LiteralPath $dir -Filter *.png | ForEach-Object { $_.Name })
    foreach ($k in @($run.Checks.Keys)) { Log-Run ("{0,-26} {1,-7} {2}" -f $k, $run.Checks[$k].Status, $run.Checks[$k].Detail) }
    return $run
}

# ---------------------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath (Join-Path $playerDir 'GenesisEngine.exe'))) { throw "The published Player was not found: $playerDir" }
if (-not (Get-ChildItem -LiteralPath $Project -Filter *.genesisproj -ErrorAction SilentlyContinue)) { throw "No .genesisproj in $Project" }
New-Item -ItemType Directory -Force $OutputRoot | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$testDir = Join-Path $OutputRoot $stamp
$n = 2; while (Test-Path -LiteralPath $testDir) { $testDir = Join-Path $OutputRoot "$stamp-$n"; $n++ }
New-Item -ItemType Directory $testDir | Out-Null
[IO.File]::WriteAllText((Join-Path $testDir $markerFile), "Made by BuildTools\GameBuildTest.ps1; deleted when more than $Keep test folders exist.", $utf8)
$script:testLog = Join-Path $testDir 'test.log'
foreach ($l in $script:pending) { [IO.File]::AppendAllText($script:testLog, $l + "`r`n", $utf8) }
$runsDir = Join-Path $testDir 'Runs'
New-Item -ItemType Directory $runsDir | Out-Null

$engineVersion = (Get-Item -LiteralPath (Join-Path $playerDir 'GenesisEngine.exe')).VersionInfo.ProductVersion
$engineCommit = $(if ($engineVersion -match '\+([0-9a-f]{7})') { $Matches[1] } else { $engineVersion })
$engineBuilt = (Get-Item -LiteralPath (Join-Path $Engine 'Genesis.Application.Core.dll')).LastWriteTime
Write-Log "Genesis game build test: $Project"
Write-Log "engine: $Engine (commit $engineCommit, built $($engineBuilt.ToString('yyyy-MM-dd HH:mm')))"
Write-Log "output: $testDir"
Write-Log "renderers: $($Renderers -join ', '); scenarios: $(($scenarioList | ForEach-Object { $_.Name }) -join ', '); F11: $(if ($IncludeF11) { 'on' } else { 'off' })"

if (Test-OurPlan) { Remove-OurPlan 'left behind by an earlier, interrupted build test' }
elseif ($planPath -and (Test-Path -LiteralPath $planPath)) { Write-Log "note: a test plan from another run is at $planPath; runs wait until it is gone" }

$results = [ordered]@{
    When = $scriptStart.ToString('yyyy-MM-dd HH:mm'); Project = $Project; Engine = $Engine; EngineCommit = $engineCommit
    EngineBuilt = $engineBuilt.ToString('yyyy-MM-dd HH:mm'); TestDir = $testDir; IncludeF11 = [bool]$IncludeF11; Export = $null; Runs = @(); Notes = @()
}
$exitCode = 0
try {
    # ---- the helper tool, built outside the repository ----
    $toolRoot = Join-Path $OutputRoot '.tool'
    $toolSrc = Join-Path $toolRoot 'src'
    $toolBin = Join-Path $toolRoot 'bin'
    $toolExe = Join-Path $toolBin 'GameBuildTestTool.exe'
    New-Item -ItemType Directory -Force $toolSrc | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'GameBuildTest\*') $toolSrc -Force
    $code = Invoke-Native 'dotnet' @('build', (Join-Path $toolSrc 'GameBuildTestTool.csproj'), '-c', 'Release', '-o', $toolBin, '-nologo', '-v:q') (Join-Path $testDir 'tool-build.log')
    if ($code -ne 0 -or -not (Test-Path -LiteralPath $toolExe)) { throw "Building the helper tool failed; see $(Join-Path $testDir 'tool-build.log')" }

    # ---- keep at most $Keep test folders ----
    $old = @(Get-ChildItem -LiteralPath $OutputRoot -Directory | Where-Object { $_.Name -match '^\d{8}-\d{4}(-\d+)?$' -and (Test-Path -LiteralPath (Join-Path $_.FullName $markerFile)) -and $_.FullName -ne $testDir } | Sort-Object Name -Descending)
    foreach ($o in @($old | Select-Object -Skip ([math]::Max(0, $Keep - 1)))) {
        Write-Log "removing old test folder $($o.Name)"
        [void](Invoke-Native $toolExe @('delete', $o.FullName) (Join-Path $testDir 'cleanup.log'))
    }

    # ---- export ----
    if ($ExportPath) {
        $gameDir = [IO.Path]::GetFullPath($ExportPath).TrimEnd('\')
        $exe = Get-ChildItem -LiteralPath $gameDir -Filter *.exe | Where-Object { $_.Name -ne 'createdump.exe' } | Select-Object -First 1
        if (-not $exe) { throw "No game executable in $gameDir" }
        $results.Export = [ordered]@{ Reused = $true; Path = $gameDir; Executable = $exe.Name; Bytes = (Get-FolderBytes $gameDir) }
        Write-Log "using the existing export $gameDir ($(Format-Bytes $results.Export.Bytes))"
    } else {
        $projectBytes = Get-FolderBytes $Project
        $need = $projectBytes + 2GB
        $free = Get-FreeBytes $testDir
        Write-Log ("project {0}, free on {1} {2}" -f (Format-Bytes $projectBytes), [IO.Path]::GetPathRoot($testDir), (Format-Bytes $free))
        if ($free -lt $need) { throw ("Not enough free space: the export needs about {0}, {1} free" -f (Format-Bytes $need), (Format-Bytes $free)) }
        $gameDir = Join-Path $testDir 'Game'
        $report = Join-Path $testDir 'export.json'
        $arguments = @('export', $Engine, $Project, $gameDir, '--report', $report, '--window-mode', $WindowMode)
        if ($GameTitle) { $arguments += @('--title', $GameTitle) }
        Write-Log 'exporting with the published Studio''s export (this copies the whole project)'
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $code = Invoke-Native $toolExe $arguments (Join-Path $testDir 'export.log')
        $sw.Stop()
        if (-not (Test-Path -LiteralPath $report)) { throw "The export did not finish (exit $code); see $(Join-Path $testDir 'export.log')" }
        $ex = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        if (-not $ex.success) { throw "The export failed: $($ex.errorMessage)" }
        $exportBytes = Get-FolderBytes $gameDir
        $results.Export = [ordered]@{
            Reused = $false; Path = $gameDir; Executable = $ex.executableName; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 0); Bytes = $exportBytes
            ModelsCooked = $ex.modelsCooked; ShadersCooked = $ex.shadersCooked; ShaderFailures = @($ex.shaderFailures)
        }
        Write-Log ("exported {0} in {1:N0} s: {2}, {3} shader binaries, {4} shader failures, {5} models cooked" -f $ex.executableName, $sw.Elapsed.TotalSeconds, (Format-Bytes $exportBytes), $ex.shadersCooked, @($ex.shaderFailures).Count, $ex.modelsCooked)
        $exe = Get-Item -LiteralPath (Join-Path $gameDir $ex.executableName)
    }
    $staleCopy = Join-Path $gameDir 'Debug\TestPlan.txt'
    if (Test-Path -LiteralPath $staleCopy) { Remove-Item -LiteralPath $staleCopy -Force }
    $script:exeBase = [IO.Path]::GetFileNameWithoutExtension($exe.Name)
    $gameVersion = (Get-Item -LiteralPath $exe.FullName).VersionInfo.ProductVersion
    $results.GameCommit = $(if ($gameVersion -match '\+([0-9a-f]{7})') { $Matches[1] } else { "$gameVersion" })
    Write-Log "the exported game runs engine commit $($results.GameCommit)"
    $ctx = @{ GameDir = $gameDir; GameExe = $exe.FullName; RunsDir = $runsDir; PackagedCache = (Join-Path $gameDir '.genesis-shaders') }

    # ---- runs ----
    $script:savesBetween = Get-SaveHashes
    $runs = New-Object System.Collections.Generic.List[object]
    foreach ($renderer in $Renderers) {
        foreach ($scenario in $scenarioList) {
            Write-Log "run $(Get-Short $renderer) $($scenario.Name)"
            $runs.Add((Invoke-GameRun $renderer $scenario $ctx))
        }
    }
    $results.Runs = $runs.ToArray()
}
catch {
    $exitCode = 1
    Write-Log "BUILD TEST STOPPED: $($_.Exception.Message)"
    $results.Notes += "Stopped: $($_.Exception.Message)"
}
finally {
    Exit-Gate
    if ($GoFile -and (Test-Path -LiteralPath $GoFile)) { Remove-Item -LiteralPath $GoFile -Force; Write-Log "removed $GoFile (the next test needs a new go)" }
    if (Test-OurPlan) { Remove-OurPlan 'end of the build test' }
    if ($planPath -and (Test-Path -LiteralPath $planPath)) { Write-Log "note: a test plan (not this test's) is at $planPath" }
}

# ---------------------------------------------------------------------------------------------------------
# RESULTS.md and results.json
# ---------------------------------------------------------------------------------------------------------
$rank = @{ 'FAIL' = 5; 'WARN' = 4; 'NOT RUN' = 3; 'PASS' = 2; 'SKIP' = 1 }
$order = @(Get-Cfg $Config 'CheckOrder' @())
$names = New-Object System.Collections.Generic.List[string]
foreach ($o in $order) { $names.Add($o) }
foreach ($r in $results.Runs) { foreach ($k in $r.Checks.Keys) { if (-not $names.Contains($k)) { $names.Add($k) } } }
$table = @{}
foreach ($renderer in $Renderers) {
    $cells = @{}
    foreach ($r in @($results.Runs | Where-Object { $_.Renderer -eq $renderer })) {
        foreach ($k in $r.Checks.Keys) {
            $c = $r.Checks[$k]
            if (-not $cells.ContainsKey($k) -or $rank[$c.Status] -gt $rank[$cells[$k].Status]) { $cells[$k] = @{ Status = $c.Status; Short = $c.Short; Detail = $c.Detail; Run = $r.Label } }
        }
        if ($r.NotRun) { foreach ($k in $names) { if (-not $cells.ContainsKey($k)) { $cells[$k] = @{ Status = 'NOT RUN'; Short = ''; Detail = $r.NotRun; Run = $r.Label } } } }
    }
    $table[$renderer] = $cells
}
$md = New-Object System.Text.StringBuilder
$title = $(if ($results.Export -and $results.Export.Executable) { [IO.Path]::GetFileNameWithoutExtension($results.Export.Executable) } else { Split-Path $Project -Leaf })
[void]$md.AppendLine("# Build test: $title")
[void]$md.AppendLine()
[void]$md.AppendLine(("- When: {0}, took {1:N0} min" -f $results.When, ((Get-Date) - $scriptStart).TotalMinutes))
[void]$md.AppendLine("- Engine: published package, commit $engineCommit (built $($results.EngineBuilt)); the exported game runs commit $($results.GameCommit)")
if ($results.Export) {
    if ($results.Export.Reused) { [void]$md.AppendLine("- Export: reused $($results.Export.Path)") }
    else { [void]$md.AppendLine(("- Export: ``{0}`` in {1} s, {2}, {3} shader binaries cooked, {4} shader failures" -f $results.Export.Path, $results.Export.Seconds, (Format-Bytes $results.Export.Bytes), $results.Export.ShadersCooked, @($results.Export.ShaderFailures).Count)) }
}
[void]$md.AppendLine("- Window: unattended (hidden from the desktop, never focused); alt-tab is simulated (sent behind other windows and back, no focus change); F11 $(if ($IncludeF11) { 'on' } else { 'off (run with -IncludeF11)' })")
[void]$md.AppendLine("- Logs: ``Runs\<renderer>-<scenario>\`` (steps.log, stdout.log, project_player.log, render.log, slow-frames.txt, stalls.txt, shaders.txt, screenshots)")
[void]$md.AppendLine()
[void]$md.AppendLine('| Check | ' + (($Renderers | ForEach-Object { Get-Short $_ }) -join ' | ') + ' |')
[void]$md.AppendLine('|---|' + (($Renderers | ForEach-Object { '---' }) -join '|') + '|')
foreach ($k in $names) {
    $row = "| $k |"
    foreach ($renderer in $Renderers) {
        $c = $table[$renderer][$k]
        if ($c) { $row += ' ' + ($c.Status + ' ' + $c.Short).Trim() + ' |' } else { $row += ' - |' }
    }
    [void]$md.AppendLine($row)
}
[void]$md.AppendLine()
[void]$md.AppendLine('## Timings')
[void]$md.AppendLine()
[void]$md.AppendLine('| | ' + (($Renderers | ForEach-Object { Get-Short $_ }) -join ' | ') + ' |')
[void]$md.AppendLine('|---|' + (($Renderers | ForEach-Object { '---' }) -join '|') + '|')
$primary = @($scenarioList | Where-Object { $_.Primary } | Select-Object -First 1)
$timingRows = [ordered]@{
    'Start to ready (s)' = { param($r) $r.ReadySeconds }
    'Game world load (ms)' = { param($r) $r.GameLoadMs }
    'Longest stall while loading (ms)' = { param($r) $r.LongestLoadStallMs }
    'Longest stall after load (ms)' = { param($r) $r.LongestStallMs }
    'FPS median / min' = { param($r) if ($null -ne $r.FpsMedian) { "$($r.FpsMedian) / $($r.FpsMin)" } }
}
foreach ($tk in $timingRows.Keys) {
    $row = "| $tk |"
    foreach ($renderer in $Renderers) {
        $r = $results.Runs | Where-Object { $_.Renderer -eq $renderer -and $primary.Count -and $_.Scenario -eq $primary[0].Name } | Select-Object -First 1
        $v = $null; if ($r -and -not $r.NotRun) { $v = & $timingRows[$tk] $r }
        $row += $(if ($null -ne $v -and "$v" -ne '') { " $v |" } else { ' - |' })
    }
    [void]$md.AppendLine($row)
}

# ---- an earlier test (for example the baseline before a fix) against this one ----
if ($CompareTo -and (Test-Path -LiteralPath $CompareTo)) { try {
    $base = Get-Content -LiteralPath $CompareTo -Raw | ConvertFrom-Json
    $baseCells = @{}
    foreach ($r in @($base.Runs)) {
        if (-not $baseCells.ContainsKey($r.Renderer)) { $baseCells[$r.Renderer] = @{} }
        if ($r.Checks) {
            foreach ($p in $r.Checks.PSObject.Properties) {
                $cur = $baseCells[$r.Renderer][$p.Name]
                if (-not $cur -or $rank[$p.Value.Status] -gt $rank[$cur.Status]) { $baseCells[$r.Renderer][$p.Name] = @{ Status = $p.Value.Status; Short = $p.Value.Short } }
            }
        }
    }
    $baseCommit = $(if ($base.PSObject.Properties['GameCommit'] -and $base.GameCommit) { $base.GameCommit } else { $base.EngineCommit })
    $newCommit = $(if ($results.GameCommit) { $results.GameCommit } else { $engineCommit })
    [void]$md.AppendLine()
    [void]$md.AppendLine("## Before and after: $baseCommit ($($base.When)) -> $newCommit (this test)")
    [void]$md.AppendLine()
    [void]$md.AppendLine('| Check | ' + (($Renderers | ForEach-Object { Get-Short $_ }) -join ' | ') + ' |')
    [void]$md.AppendLine('|---|' + (($Renderers | ForEach-Object { '---' }) -join '|') + '|')
    foreach ($k in $names) {
        $row = "| $k |"
        foreach ($renderer in $Renderers) {
            $b = $null; if ($baseCells.ContainsKey($renderer)) { $b = $baseCells[$renderer][$k] }
            $n = $table[$renderer][$k]
            $bs = $(if ($b) { $b.Status } else { '-' }); $ns = $(if ($n) { $n.Status } else { '-' })
            $row += $(if ($bs -eq $ns) { " $ns |" } else { " $bs -> **$ns** |" })
        }
        [void]$md.AppendLine($row)
    }
    [void]$md.AppendLine()
    [void]$md.AppendLine('| Number (world run) | ' + (($Renderers | ForEach-Object { Get-Short $_ }) -join ' | ') + ' |')
    [void]$md.AppendLine('|---|' + (($Renderers | ForEach-Object { '---' }) -join '|') + '|')
    $primaryName = $(if ($primary.Count) { $primary[0].Name } else { '' })
    $numberRows = [ordered]@{
        'Launch to world (s)' = { param($r, $isBase) $r.ReadySeconds }
        'Shaders compiled on first launch' = { param($r, $isBase) Get-CheckShort $r 'First launch shaders (a)' }
        'Post effect on, worst frame' = { param($r, $isBase) Get-CheckShort $r 'Post effect on (b)' }
        'First spawns, worst frame' = { param($r, $isBase) Get-CheckShort $r 'Creature spawns (c)' }
        'Longest stall after load (ms)' = { param($r, $isBase) $r.LongestStallMs }
        'FPS median / min' = { param($r, $isBase) if ($null -ne $r.FpsMedian) { "$($r.FpsMedian) / $($r.FpsMin)" } }
    }
    function Get-CheckShort($r, [string]$name) {
        if (-not $r.Checks) { return $null }
        if ($r.Checks -is [System.Collections.IDictionary]) { $c = $r.Checks[$name] } else { $c = $r.Checks.PSObject.Properties[$name]; if ($c) { $c = $c.Value } }
        if (-not $c) { return $null }
        return ($c.Status + ' ' + $c.Short).Trim()
    }
    foreach ($nk in $numberRows.Keys) {
        $row = "| $nk |"
        foreach ($renderer in $Renderers) {
            $br = @($base.Runs) | Where-Object { $_.Renderer -eq $renderer -and $_.Scenario -eq $primaryName } | Select-Object -First 1
            $nr = $results.Runs | Where-Object { $_.Renderer -eq $renderer -and $_.Scenario -eq $primaryName } | Select-Object -First 1
            $bv = $null; if ($br -and -not $br.NotRun) { $bv = & $numberRows[$nk] $br $true }
            $nv = $null; if ($nr -and -not $nr.NotRun) { $nv = & $numberRows[$nk] $nr $false }
            $row += (' {0} -> {1} |' -f $(if ($null -ne $bv -and "$bv" -ne '') { $bv } else { '-' }), $(if ($null -ne $nv -and "$nv" -ne '') { $nv } else { '-' }))
        }
        [void]$md.AppendLine($row)
    }
    $baseBytes = $null
    if ($base.Export -and $base.Export.PSObject.Properties['Bytes'] -and $base.Export.Bytes) { $baseBytes = [double]$base.Export.Bytes }
    elseif ($base.Export -and $base.Export.Path -and (Test-Path -LiteralPath $base.Export.Path)) { $baseBytes = Get-FolderBytes $base.Export.Path }
    $newBytes = $(if ($results.Export -and $results.Export.Bytes) { [double]$results.Export.Bytes } else { $null })
    if ($null -ne $baseBytes -or $null -ne $newBytes) {
        [void]$md.AppendLine()
        [void]$md.AppendLine(('Export size: {0} -> {1}' -f $(if ($null -ne $baseBytes) { Format-Bytes $baseBytes } else { '-' }), $(if ($null -ne $newBytes) { Format-Bytes $newBytes } else { '-' })))
    }
} catch { [void]$md.AppendLine(); [void]$md.AppendLine("(The comparison with $CompareTo failed: $($_.Exception.Message))") } }

[void]$md.AppendLine()
[void]$md.AppendLine('## Details (everything not PASS, and the (a)/(b)/(c) measurements)')
[void]$md.AppendLine()
foreach ($renderer in $Renderers) {
    foreach ($k in $names) {
        $c = $table[$renderer][$k]
        if (-not $c) { continue }
        if ($c.Status -ne 'PASS' -or $k -match '\((a|b|c)\)') { [void]$md.AppendLine(("- **{0} {1}: {2}** - {3} (``Runs\{4}``)" -f (Get-Short $renderer), $k, $c.Status, (Short $c.Detail 400), $c.Run)) }
    }
}
foreach ($r in $results.Runs) { foreach ($n in $r.Notes) { $results.Notes += "$($r.Label): $n" } }
if ($results.Notes.Count -gt 0) {
    [void]$md.AppendLine()
    [void]$md.AppendLine('## Notes')
    foreach ($n in $results.Notes) { [void]$md.AppendLine("- $n") }
}
$resultsFile = Join-Path $testDir 'RESULTS.md'
[IO.File]::WriteAllText($resultsFile, $md.ToString(), $utf8)
Copy-Item -LiteralPath $resultsFile (Join-Path $OutputRoot 'RESULTS.md') -Force

$json = [ordered]@{ When = $results.When; Project = $Project; EngineCommit = $engineCommit; GameCommit = $results.GameCommit; EngineBuilt = $results.EngineBuilt; Export = $results.Export; Notes = $results.Notes; Runs = @() }
foreach ($r in $results.Runs) {
    $o = [ordered]@{}
    foreach ($k in $r.Keys) { $v = $r[$k]; if ($v -is [datetime]) { $v = $v.ToString('yyyy-MM-dd HH:mm:ss.fff') }; $o[$k] = $v }
    $json.Runs += $o
}
[IO.File]::WriteAllText((Join-Path $testDir 'results.json'), ($json | ConvertTo-Json -Depth 6), $utf8)
Write-Log "results: $resultsFile"
Write-Host ''
Write-Host $md.ToString()
exit $exitCode
