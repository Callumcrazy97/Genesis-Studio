<#
.SYNOPSIS
    Room-change soak (Thursday list item B3): builds a two-room project with Studio's own
    automation commands, changes room thousands of times and reports whether managed memory stays
    level, with the allocation a frame and the collector's pauses.

.DESCRIPTION
    "Soak Field" holds four streamed tiles of one painted terrain (grass, scattered rocks, a terrain
    distance of 250 m), 60 crates and 20 animated figures; "Soak Hall" the same objects. Each room
    goes to the other after 20 frames. The run is game.benchmark (uncapped) on the field; the
    player log's "Room change memory" lines give managed memory at every change, and the lowest
    value in each run of 500 changes is its level (values between collections are noise). Exit code
    1 when the level at the end is more than -MaxRiseMegabytes above the level after warming up.
    The project is made in -Folder (generated output) when missing or with -Rebuild. Close Studio
    and any game first: the run opens a game window.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File BuildTools\Benchmarks\RoomSoak.ps1 -Seconds 300
#>
param(
    [string]$Studio = (Join-Path $PSScriptRoot "..\..\Genesis Application\Genesis Application.exe"),
    [string]$Folder = (Join-Path $PSScriptRoot "..\..\.build\soak-rooms"),
    [double]$Seconds = 300,
    [double]$MaxRiseMegabytes = 100,
    [switch]$Rebuild
)
$ErrorActionPreference = "Stop"
$Studio = [IO.Path]::GetFullPath($Studio)
$Folder = [IO.Path]::GetFullPath($Folder)
if (-not (Test-Path $Studio)) { throw "Studio is not published at $Studio (Build.bat --quick)." }
$project = Join-Path $Folder "Room Soak"
$scratch = Join-Path $env:TEMP ("genesis-soak-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $scratch | Out-Null

function Call([string]$command, $arguments) {
    # Input goes through a file: Windows PowerShell strips the quotes from inline JSON.
    $file = Join-Path $scratch ([Guid]::NewGuid().ToString("N") + ".json")
    [IO.File]::WriteAllText($file, ($arguments | ConvertTo-Json -Depth 12 -Compress))
    $text = & $Studio --automation $project call $command "@$file" 2>&1 | Out-String
    $result = $text | ConvertFrom-Json
    if (-not $result.ok -and $command -ne "game.benchmark") { throw "$command failed: $($result.message)" }
    $errors = @($result.issues | Where-Object { $_.severity -eq "error" })
    if ($errors.Count -gt 0) { throw "$command left errors: $(($errors | ForEach-Object { "$($_.path):$($_.line) $($_.message)" }) -join '; ')" }
    return $result
}

function Build-Project {
    if (Test-Path $project) { Remove-Item -Recurse -Force $project }
    New-Item -ItemType Directory -Force $Folder | Out-Null
    $made = & $Studio --automation new $Folder "Room Soak" 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Could not make the project: $made" }
    function Room([string]$name) {
        $room = (Call "resource.create" @{ kind = "Room"; folder = "Assets/Rooms"; name = $name }).data.path
        Call "resource.set" @{ path = $room; values = @{ dimension = 1 } } | Out-Null
        return $room
    }
    $crate = (Call "model.create" @{ name = "Crate Model"; parts = @(@{ primitive = "Cube"; color = "#A0703C" }) }).data.path
    Call "object.create" @{ name = "Crate"; model = $crate } | Out-Null
    $figure = (Call "model.create" @{ name = "Walker Model"; rig = "Humanoid"; parts = @(
        @{ primitive = "Cube"; position = @(0, 1.25, 0); scale = @(0.5, 0.7, 0.3); color = "#4A6FA5" },
        @{ primitive = "Sphere"; position = @(0, 1.8, 0); scale = @(0.3, 0.3, 0.3); color = "#E0B090" },
        @{ primitive = "Cylinder"; position = @(-0.15, 0.45, 0); scale = @(0.16, 0.9, 0.16); color = "#333344" },
        @{ primitive = "Cylinder"; position = @(0.15, 0.45, 0); scale = @(0.16, 0.9, 0.16); color = "#333344" }) }).data.path
    $walker = (Call "object.create" @{ name = "Walker"; model = $figure; events = @{
        Create = "var tree = AnimationBlendTreeCreate1D(`"Speed`");`nAnimationBlendTreeAddClip(tree, `"Idle`", 0);`nAnimationBlendTreeAddClip(tree, `"Walk`", 1);`nAnimationStateCreate(`"Move`", tree);`nAnimationSetParameter(`"Speed`", Random(1));`n" } }).data.path
    Call "object.setComponent" @{ object = $walker; type = "ModelAnimatorComponent"; values = @{ Playing = $true; Loop = $true } } | Out-Null

    $field = Room "Soak Field"
    $hall = Room "Soak Hall"
    $terrain = Call "terrain.create" @{ name = "Soak Hills"; preset = "Hills"; resolutionX = 129; resolutionZ = 129; cellSize = 2 }
    $rock = (Call "model.create" @{ name = "Rock Model"; parts = @(@{ primitive = "Sphere"; scale = @(1.2, 0.7, 1.0); color = "#7A7A72" }) }).data.path
    Call "terrain.setGrass" @{ terrain = $terrain.data.path; enabled = $true } | Out-Null
    Call "terrain.addScatterLayer" @{ terrain = $terrain.data.path; name = "Rocks"; model = $rock; densityPerHectare = 120 } | Out-Null
    # Streamed like the old test island: four tiles of the same terrain, loaded by distance on each visit.
    Call "room.setEnvironment" @{ room = $field; values = @{ terrainDistance = 250 } } | Out-Null
    $tiles = @(@(0, 0), @(400, 0), @(0, 400), @(400, 400))
    for ($k = 0; $k -lt 4; $k++) {
        Call "room.addTerrain" @{ room = $field; terrain = $terrain.data.path; name = "Tile $k" } | Out-Null
        Call "room.moveInstance" @{ room = $field; instance = "Tile $k"; x = $tiles[$k][0]; y = 0; z = $tiles[$k][1] } | Out-Null
    }

    function Switcher([string]$name, [string]$room, [string]$next, [string]$camera) {
        $object = (Call "object.create" @{ name = $name; events = @{
            Create = "ScriptInstructionLimit(5000000);`nt = 0;`n$camera" +
                "for (var i = 0; i < 60; i++) { CreateInstance(`"Crate`", RandomRange(-20, 20), 0.5, RandomRange(-20, 20)); }`n" +
                "for (var j = 0; j < 20; j++) { CreateInstance(`"Walker`", RandomRange(-10, 10), 0, RandomRange(-10, 10)); }`n";
            Step = "t = t + 1;`nif (t == 20) { RoomGoto(`"$next`"); }`n" } }).data.path
        Call "room.addInstance" @{ room = $room; object = $object } | Out-Null
    }
    $cx = ($terrain.data.extent.minX + $terrain.data.extent.maxX) / 2 + 200; $cz = ($terrain.data.extent.minZ + $terrain.data.extent.maxZ) / 2 + 200
    $top = $terrain.data.heights[1]
    Switcher "Field Switcher" $field "Soak Hall" "SetCameraPosition($cx, $($top + 30), $($cz - 120));`nSetCameraTarget($cx, $($top * 0.5), $cz);`n"
    Switcher "Hall Switcher" $hall "Soak Field" "SetCameraPosition(0, 20, -40);`nSetCameraTarget(0, 0, 0);`n"
    Call "room.makeStart" @{ room = $field } | Out-Null
}

if ($Rebuild -or -not (Test-Path (Join-Path $project "Assets"))) { Build-Project }
$log = Join-Path $project "Debug\Logs\project_player.log"
if (Test-Path $log) { Remove-Item -Force $log }
$result = Call "game.benchmark" @{ room = "Soak Field"; seconds = $Seconds; warmupSeconds = 5 }
Remove-Item -Recurse -Force $scratch
$changes = @(Select-String -Path $log -Pattern "Room change memory: .* holds (\d+) MB managed" | ForEach-Object { [int]$_.Matches[0].Groups[1].Value })
$levels = @(for ($s = 0; $s + 500 -le $changes.Count; $s += 500) { ($changes[$s..($s + 499)] | Measure-Object -Minimum).Minimum })
Write-Output $result.message
Write-Output ("Peak working set {0} MB, collector pauses {1} ms, {2} room changes." -f $result.data.peakWorkingSetMegabytes, $result.data.collectorPauseMilliseconds, $changes.Count)
Write-Output ("Managed level per 500 changes (MB): " + ($levels -join ", "))
if ($levels.Count -lt 3) { Write-Output "Too few changes to judge; run longer."; exit 1 }
$rise = $levels[-1] - $levels[1]
if ($rise -gt $MaxRiseMegabytes) { Write-Output "Managed memory rose $rise MB after warming up (limit $MaxRiseMegabytes)."; exit 1 }
Write-Output "Level: $rise MB from the second window to the last, of which the benchmark's own list of frames is about $([Math]::Round($result.data.frames * 150 / 1MB)) MB."
exit 0