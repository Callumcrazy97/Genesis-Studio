<#
.SYNOPSIS
    Standard benchmark scenes (Thursday list item 14): builds a test project with Studio's own
    automation commands, measures each room with game.benchmark (vsync and caps off) and checks
    the numbers against budgets.

.DESCRIPTION
    Scenes: 2D sprites (3000 bouncing), 3D objects (4096 crates), terrain with grass and scattered
    rocks, a physics pile (500 crates falling on a floor) and a heavy GUI (400 labelled panels).
    The project is made in -Folder (generated output) only when missing or with -Rebuild. The report
    goes to TestResults\Benchmarks\<time>.md and .json. Exit code 1 when a scene is over budget or
    failed. Close Studio and any game first: the runs open a game window.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File BuildTools\Benchmarks\BenchmarkScenes.ps1 -Rebuild
#>
param(
    [string]$Studio = (Join-Path $PSScriptRoot "..\..\Genesis Application\Genesis Application.exe"),
    [string]$Folder = (Join-Path $PSScriptRoot "..\..\.build\benchmark-scenes"),
    [double]$Seconds = 10,
    [double]$Warmup = 4,
    [string]$Budgets = (Join-Path $PSScriptRoot "budgets.json"),
    [switch]$Rebuild
)
$ErrorActionPreference = "Stop"
$Studio = [IO.Path]::GetFullPath($Studio)
$Folder = [IO.Path]::GetFullPath($Folder)
if (-not (Test-Path $Studio)) { throw "Studio is not published at $Studio (Build.bat --quick)." }
$project = Join-Path $Folder "Benchmark Scenes"
$scratch = Join-Path $env:TEMP ("genesis-bench-" + [Guid]::NewGuid().ToString("N"))
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
    $made = & $Studio --automation new $Folder "Benchmark Scenes" 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Could not make the project: $made" }

    function Room([string]$name, [bool]$threeD) {
        $room = (Call "resource.create" @{ kind = "Room"; folder = "Assets/Rooms"; name = $name }).data.path
        if ($threeD) { Call "resource.set" @{ path = $room; values = @{ dimension = 1 } } | Out-Null }
        return $room
    }
    function Spawner([string]$name, [string]$room, [string]$create, [string]$gui) {
        $events = @{ Create = $create }
        if ($gui) { $events.DrawGui = $gui }
        $object = (Call "object.create" @{ name = $name; events = $events }).data.path
        Call "room.addInstance" @{ room = $room; object = $object } | Out-Null
    }

    # 2D sprites: 3000 bouncing 16 px balls.
    $sprites = Room "Sprites 2D" $false
    $ball = (Call "image.create" @{ name = "Ball Image"; width = 16; height = 16 }).data.path
    Call "image.draw" @{ image = $ball; operations = @(@{ shape = "ellipse"; x = 1; y = 1; width = 14; height = 14; color = "#FFB040" }) } | Out-Null
    Call "object.create" @{ name = "Mover"; sprite = $ball; events = @{
        Create = "dx = RandomRange(-3, 3);`ndy = RandomRange(-3, 3);`n";
        Step = "x = x + dx;`ny = y + dy;`nif (x < 0 || x > 1280) { dx = -dx; }`nif (y < 0 || y > 720) { dy = -dy; }`n" } } | Out-Null
    Spawner "Sprite Spawner" $sprites "ScriptInstructionLimit(5000000);`nfor (var i = 0; i < 3000; i++) { CreateInstance(`"Mover`", Random(1280), Random(720), 0); }`n" ""

    # 3D objects: 4096 crates in a grid.
    $objects = Room "Objects 3D" $true
    $crate = (Call "model.create" @{ name = "Crate Model"; parts = @(@{ primitive = "Cube"; color = "#A0703C" }) }).data.path
    Call "object.create" @{ name = "Crate"; model = $crate } | Out-Null
    Spawner "Crate Spawner" $objects ("ScriptInstructionLimit(5000000);`nSetCameraPosition(0, 45, -110);`nSetCameraTarget(0, 0, 0);`n" +
        "for (var gx = 0; gx < 64; gx++) { for (var gz = 0; gz < 64; gz++) { CreateInstance(`"Crate`", gx * 2 - 64, 0.5, gz * 2 - 64); } }`n") ""

    # Terrain: rolling hills with the grass rule and scattered rocks.
    $land = Room "Terrain" $true
    $terrain = Call "terrain.create" @{ name = "Hills"; preset = "Hills"; resolutionX = 257; resolutionZ = 257; cellSize = 2 }
    $rock = (Call "model.create" @{ name = "Rock Model"; parts = @(@{ primitive = "Sphere"; scale = @(1.2, 0.7, 1.0); color = "#7A7A72" }) }).data.path
    Call "terrain.setGrass" @{ terrain = $terrain.data.path; enabled = $true } | Out-Null
    Call "terrain.addScatterLayer" @{ terrain = $terrain.data.path; name = "Rocks"; model = $rock; densityPerHectare = 120 } | Out-Null
    Call "room.addTerrain" @{ room = $land; terrain = $terrain.data.path } | Out-Null
    $cx = ($terrain.data.extent.minX + $terrain.data.extent.maxX) / 2; $cz = ($terrain.data.extent.minZ + $terrain.data.extent.maxZ) / 2
    $top = $terrain.data.heights[1]
    Spawner "Terrain Camera" $land ("SetCameraPosition($cx, $($top + 30), $($cz - 180));`nSetCameraTarget($cx, $($top * 0.5), $cz);`n") ""

    # Physics pile: 500 crates dropped on a floor.
    $pile = Room "Physics Pile" $true
    # A unit cube at scale 60 with a static box of the same size (a part-built model has no collider of its own); its top is at y = 0.
    $floorModel = (Call "model.create" @{ name = "Floor Model"; parts = @(@{ primitive = "Cube"; color = "#55606A" }) }).data.path
    $floor = (Call "object.create" @{ name = "Floor"; model = $floorModel; solid = $true }).data.path
    Call "resource.set" @{ path = $floor; values = @{ physics = "StaticSolid" } } | Out-Null
    Call "room.addInstance" @{ room = $pile; object = $floor; y = -30; scale = 60 } | Out-Null
    $box = (Call "object.create" @{ name = "Falling Crate"; model = $crate; solid = $true }).data.path
    Call "resource.set" @{ path = $box; values = @{ physics = "Dynamic" } } | Out-Null
    Spawner "Pile Spawner" $pile ("ScriptInstructionLimit(5000000);`nSetCameraPosition(0, 22, -40);`nSetCameraTarget(0, 4, 0);`n" +
        "for (var i = 0; i < 500; i++) { CreateInstance(`"Falling Crate`", RandomRange(-6, 6), 3 + i * 0.12, RandomRange(-6, 6)); }`n") ""

    # Heavy GUI: 400 labelled panels drawn every frame.
    $gui = Room "GUI" $false
    Spawner "Panels" $gui "t = 0;`n" ("ScriptInstructionLimit(1000000);`nt = t + 1;`nfor (var i = 0; i < 400; i++) {`n    var px = (i % 20) * 64;`n    var py = Floor(i / 20) * 36;`n" +
        "    DrawSetColorRgb((i * 37) % 255, (i * 91 + t) % 255, 180);`n    DrawRectangle(px, py, px + 60, py + 32);`n" +
        "    DrawSetColorRgb(255, 255, 255);`n    DrawText(px + 4, py + 8, `"Item `" + i);`n}`n")

    Call "room.makeStart" @{ room = $sprites } | Out-Null
}

if ($Rebuild -or -not (Test-Path (Join-Path $project "Assets"))) { Build-Project }

$scenes = @("Sprites 2D", "Objects 3D", "Terrain", "Physics Pile", "GUI")
$budget = if (Test-Path $Budgets) { Get-Content $Budgets -Raw | ConvertFrom-Json } else { $null }
$rows = @(); $failed = 0
foreach ($scene in $scenes) {
    $result = Call "game.benchmark" @{ room = $scene; seconds = $Seconds; warmupSeconds = $Warmup }
    $d = $result.data
    $limit = if ($budget) { $budget.$scene } else { $null }
    $over = @()
    if (-not $result.ok -or -not $d.completed) { $over += "did not complete: $($result.message)" }
    if ($limit -and $d.p95Milliseconds -gt $limit.p95) { $over += "p95 $($d.p95Milliseconds) > $($limit.p95)" }
    if ($limit -and $d.p99Milliseconds -gt $limit.p99) { $over += "p99 $($d.p99Milliseconds) > $($limit.p99)" }
    if ($limit -and $d.allocationKilobytesPerFrame -gt $limit.kbPerFrame) { $over += "alloc $($d.allocationKilobytesPerFrame) KB > $($limit.kbPerFrame)" }
    if ($over.Count -gt 0) { $failed++ }
    $rows += [pscustomobject]@{ Scene = $scene; Median = $d.medianMilliseconds; Fps = $d.medianFps; P95 = $d.p95Milliseconds; P99 = $d.p99Milliseconds
        Worst = $d.worstMilliseconds; KbPerFrame = $d.allocationKilobytesPerFrame; Draws = $d.averageDrawCalls; Triangles = $d.averageTriangles
        Backend = $d.backend; Over = ($over -join "; ") }
}

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$out = Join-Path $repo "TestResults\Benchmarks"
New-Item -ItemType Directory -Force $out | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$rows | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $out "$stamp.json")
$md = @("# Benchmark scenes $stamp", "", "Uncapped (vsync and caps off), $Seconds s after $Warmup s warm-up.", "",
    "| Scene | Median ms | fps | p95 | p99 | Worst | KB/frame | Draws | Triangles | Over budget |", "|---|---|---|---|---|---|---|---|---|---|")
$md += $rows | ForEach-Object { "| $($_.Scene) | $($_.Median) | $($_.Fps) | $($_.P95) | $($_.P99) | $($_.Worst) | $($_.KbPerFrame) | $($_.Draws) | $($_.Triangles) | $($_.Over) |" }
$md | Set-Content -Encoding utf8 (Join-Path $out "$stamp.md")
$md | ForEach-Object { Write-Output $_ }
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
if ($failed -gt 0) { Write-Output "$failed scene(s) over budget or failed."; exit 1 }
exit 0
