<#
.SYNOPSIS
    Standard benchmark scenes (Thursday list item 14): builds a test project with Studio's own
    automation commands, measures each room with game.benchmark (vsync and caps off) and checks
    the numbers against budgets.

.DESCRIPTION
    Scenes: 2D sprites (3000 bouncing), 3D objects (4096 crates), terrain with grass and scattered
    rocks, a physics pile (500 crates falling on a floor), a skinned crowd (200 rigged figures, each
    with its own animation graph), a heavy GUI (400 labelled panels) and a synthetic complex scene
    (64 chunk meshes built in worker jobs and placed once on the static layer, 1000 instances moved
    by four jobs a frame through queued scene changes, 200 script mesh draws a frame, a hot script
    function and a GUI).
    The project is made in -Folder (generated output) only when missing, made by an older version
    of this script, or with -Rebuild. The report goes to TestResults\Benchmarks\<time>.md and .json.
    -Track also adds each scene's numbers to TestResults\Benchmarks\history.jsonl and compares them
    with the scene's earlier runs (Build.ps1 does this for the complex scene on every publish that
    changes the engine). Exit code 1 when a scene is over budget or failed. Close Studio and any
    game first: the runs open a game window.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File BuildTools\Benchmarks\BenchmarkScenes.ps1 -Rebuild
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File BuildTools\Benchmarks\BenchmarkScenes.ps1 -Scenes "Complex Scene" -Track
#>
param(
    [string]$Studio = (Join-Path $PSScriptRoot "..\..\Genesis Application\Genesis Application.exe"),
    [string]$Folder = (Join-Path $PSScriptRoot "..\..\.build\benchmark-scenes"),
    [double]$Seconds = 10,
    [double]$Warmup = 4,
    [string]$Budgets = (Join-Path $PSScriptRoot "budgets.json"),
    [string[]]$Scenes = @("Sprites 2D", "Objects 3D", "Terrain", "Physics Pile", "Skinned Crowd", "GUI", "Complex Scene"),
    [switch]$Track,
    [string]$EngineHash = "",
    [switch]$Rebuild
)
# Raised whenever Build-Project makes something new, so an older project is made again.
$projectVersion = "3"
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

    # Skinned crowd: 200 rigged figures, each with its own animation graph (idle-walk blend).
    $crowd = Room "Skinned Crowd" $true
    $figure = (Call "model.create" @{ name = "Walker Model"; rig = "Humanoid"; parts = @(
        @{ primitive = "Cube"; position = @(0, 1.25, 0); scale = @(0.5, 0.7, 0.3); color = "#4A6FA5" },
        @{ primitive = "Sphere"; position = @(0, 1.8, 0); scale = @(0.3, 0.3, 0.3); color = "#E0B090" },
        @{ primitive = "Cylinder"; position = @(-0.15, 0.45, 0); scale = @(0.16, 0.9, 0.16); color = "#333344" },
        @{ primitive = "Cylinder"; position = @(0.15, 0.45, 0); scale = @(0.16, 0.9, 0.16); color = "#333344" },
        @{ primitive = "Cylinder"; position = @(-0.35, 1.25, 0); scale = @(0.12, 0.7, 0.12); color = "#4A6FA5" },
        @{ primitive = "Cylinder"; position = @(0.35, 1.25, 0); scale = @(0.12, 0.7, 0.12); color = "#4A6FA5" }) }).data.path
    $walker = (Call "object.create" @{ name = "Walker"; model = $figure; events = @{
        Create = "var tree = AnimationBlendTreeCreate1D(`"Speed`");`nAnimationBlendTreeAddClip(tree, `"Idle`", 0);`nAnimationBlendTreeAddClip(tree, `"Walk`", 1);`nAnimationStateCreate(`"Move`", tree);`nAnimationSetParameter(`"Speed`", Random(1));`n" } }).data.path
    Call "object.setComponent" @{ object = $walker; type = "ModelAnimatorComponent"; values = @{ Playing = $true; Loop = $true } } | Out-Null
    Spawner "Crowd Spawner" $crowd ("ScriptInstructionLimit(5000000);`nSetCameraPosition(0, 9, -26);`nSetCameraTarget(0, 1, 0);`n" +
        "for (var cx = 0; cx < 20; cx++) { for (var cz = 0; cz < 10; cz++) { CreateInstance(`"Walker`", cx * 1.6 - 15, 0, cz * 1.6 - 6); } }`n") ""

    # Heavy GUI: 400 labelled panels drawn every frame.
    $gui = Room "GUI" $false
    Spawner "Panels" $gui "t = 0;`n" ("ScriptInstructionLimit(1000000);`nt = t + 1;`nfor (var i = 0; i < 400; i++) {`n    var px = (i % 20) * 64;`n    var py = Floor(i / 20) * 36;`n" +
        "    DrawSetColorRgb((i * 37) % 255, (i * 91 + t) % 255, 180);`n    DrawRectangle(px, py, px + 60, py + 32);`n" +
        "    DrawSetColorRgb(255, 255, 255);`n    DrawText(px + 4, py + 8, `"Item `" + i);`n}`n")

    # Complex scene: the engine's newer paths together, as a large game uses them.
    $complex = Room "Complex Scene" $true
    Call "script.write" @{ path = "Assets/Scripts/ComplexScene.pgsl"; code = @'
// Ground height at a column: numbers only, so the compiled tier runs it once it is hot.
function CxHeight(gx, gz) {
    return Floor(5 + 3 * Sin(gx * 9) * Sin(gz * 7) + 2 * Sin((gx + gz) * 4));
}
// A 16 x 16 chunk of columns, three blocks deep, built straight into a mesh lent to the job.
function CxChunk(m, cx, cz) {
    for (var lz = 0; lz < 16; lz = lz + 1) {
        for (var lx = 0; lx < 16; lx = lx + 1) {
            var h = CxHeight(cx * 16 + lx, cz * 16 + lz);
            for (var level = h - 2; level <= h; level = level + 1) {
                MeshAddCube(m, lx + 0.5, level + 0.5, lz + 0.5, 1, 55, 70 + h * 14, 120 + h * 10, 60, 0, 0, 1, 1);
            }
        }
    }
    return MeshVertexCount(m);
}
// A streamed strip of the world: its 16 x 256 cells of the world grid worked out again for time t
// (only that rectangle comes back) and their top faces meshed into the mesh lent to the job.
function CxStream(m, world, slot, t) {
    for (var gz = 0; gz < 256; gz = gz + 1) {
        for (var lx = 0; lx < 16; lx = lx + 1) {
            var h = CxHeight(slot * 16 + lx + t, gz);
            DsGridSet(world, slot * 16 + lx, gz, h);
            MeshAddCube(m, lx + 0.5, h + 0.5, gz + 0.5, 1, 4, 80 + h * 12, 140, 70 + h * 8, 0, 0, 1, 1);
        }
    }
    return MeshVertexCount(m);
}
// A share of the movers circling their places, queued for the game to make when it takes the job.
function CxMove(ids, homes, startAt, many, t) {
    for (var i = startAt; i < startAt + many; i = i + 1) {
        var a = t * 40 + i * 7;
        SceneQueuePosition(NumListGet(ids, i), NumListGet(homes, i * 3) + Sin(a) * 1.5, 14 + Sin(a * 2) * 0.5, NumListGet(homes, i * 3 + 2) + Cos(a) * 1.5);
    }
    return many;
}
'@ } | Out-Null
    Call "object.create" @{ name = "Complex Mover"; model = $crate } | Out-Null
    # 500 entities that move themselves in their own Step events.
    Call "object.create" @{ name = "Complex Walker"; model = $crate; events = @{
        Create = "dx = RandomRange(-0.2, 0.2);`ndz = RandomRange(-0.2, 0.2);`n"
        Step = "x = x + dx;`nz = z + dz;`nif (x < 0 || x > 128) { dx = -dx; }`nif (z < 0 || z > 128) { dz = -dz; }`n" } } | Out-Null
    Call "particle.create" @{ name = "Complex Sparks"; preset = "Sparks" } | Out-Null
    $director = (Call "object.create" @{ name = "Complex Director"; events = @{
        Create = @'
ScriptInstructionLimit(20000000);
SetCameraPosition(64, 46, -40);
SetCameraTarget(64, 4, 60);
// 8 x 8 chunks, each built in a worker job and placed once its job has finished.
chunkJobs = DsListCreate();
chunkMeshes = DsListCreate();
for (var cz = 0; cz < 8; cz++) {
    for (var cx = 0; cx < 8; cx++) {
        var m = MeshCreate();
        var j = JobScriptCreate("CxChunk");
        JobScriptMesh(j, m);
        JobScriptStart(j, m, cx, cz);
        DsListAdd(chunkJobs, j);
        DsListAdd(chunkMeshes, m);
    }
}
waitingChunks = 64;
ids = NumListCreate();
for (var i = 0; i < 1000; i++) { NumListAdd(ids, CreateInstance("Complex Mover", (i % 40) * 3.2, 14, Floor(i / 40) * 5)); }
homes = NumListCreate();
InstanceGetPositionsToNumList(ids, homes);
moveJobs = DsListCreate();
for (var k = 0; k < 4; k++) { DsListAdd(moveJobs, 0); }
prop = MeshCreate();
MeshAddCube(prop, 0, 0, 0, 1, 63, 230, 200, 90, 0, 0, 1, 1);
t = 0;
noise = 0;
// Post effects of the high tier.
RenderSetQuality("high");
// A world grid the size of a voxel game's row of chunks; eight strips of it streamed by jobs.
world = DsGridCreate(4096, 256, "u16");
streamMeshes = DsListCreate();
for (var sm = 0; sm < 8; sm++) { var strip = MeshCreate(); DsListAdd(streamMeshes, strip); MeshStaticAdd(strip, "", sm * 16, -6, 140, 1, 1, 1, 0, ""); }
streamJobs = DsListCreate();
DsListAdd(streamJobs, 0);
DsListAdd(streamJobs, 0);
nextStream = 0;
for (var w = 0; w < 500; w++) { CreateInstance("Complex Walker", Random(128), 1, Random(128)); }
'@
        Step = @'
ScriptInstructionLimit(20000000);
t = t + 0.016;
// Chunks go on the static layer as their jobs finish; the engine draws them from then on.
if (waitingChunks > 0) {
    for (var k = 0; k < 64; k++) {
        var j = DsListGet(chunkJobs, k);
        if (j > 0 && JobStatus(j) == "succeeded") {
            JobTake(j);
            JobRelease(j);
            DsListSet(chunkJobs, k, 0);
            MeshStaticAdd(DsListGet(chunkMeshes, k), "", (k % 8) * 16, 0, Floor(k / 8) * 16, 1, 1, 1, 0, "");
            waitingChunks = waitingChunks - 1;
        }
    }
}
// Four jobs move 250 movers each; their moves land as each is taken, then the next four start.
var idle = 0;
for (var k2 = 0; k2 < 4; k2++) {
    var mj = DsListGet(moveJobs, k2);
    if (mj > 0) {
        var js = JobStatus(mj);
        if (js == "succeeded") { JobTake(mj); }
        if (js == "succeeded" || js == "failed" || js == "cancelled") { JobRelease(mj); DsListSet(moveJobs, k2, 0); mj = 0; }
    }
    if (mj == 0) { idle = idle + 1; }
}
if (idle == 4) {
    for (var k3 = 0; k3 < 4; k3++) {
        var nj = JobScriptCreate("CxMove");
        JobScriptNumList(nj, ids, false);
        JobScriptNumList(nj, homes, false);
        JobScriptStart(nj, ids, homes, k3 * 250, 250, t);
        DsListSet(moveJobs, k3, nj);
    }
}
// Script work every frame: a hot numbers-only function.
var s = 0;
for (var i = 0; i < 400; i++) { s = s + CxHeight(i, t * 10); }
noise = s;
// Two strips rebuilt at a time by jobs, as a world streams: each writes its part of the world grid and meshes it.
for (var k4 = 0; k4 < 2; k4++) {
    var sj = DsListGet(streamJobs, k4);
    if (sj > 0) {
        var ss = JobStatus(sj);
        if (ss == "succeeded") { JobTake(sj); }
        if (ss == "succeeded" || ss == "failed" || ss == "cancelled") { JobRelease(sj); DsListSet(streamJobs, k4, 0); sj = 0; }
    }
    if (sj == 0) {
        var slot = nextStream % 8;
        nextStream = nextStream + 1;
        var strip = DsListGet(streamMeshes, slot);
        var nj2 = JobScriptCreate("CxStream");
        JobScriptGridRegion(nj2, world, slot * 16, 0, slot * 16 + 15, 255, true);
        JobScriptMesh(nj2, strip);
        JobScriptStart(nj2, strip, world, slot, Floor(t * 10));
        DsListSet(streamJobs, k4, nj2);
    }
}
// Sparks now and then.
if (nextStream % 3 == 0) { ParticleBurstBatched("Complex Sparks", Random(128), 6, Random(128), 1); }
'@
        Draw = @'
for (var i = 0; i < 200; i++) {
    DrawMesh3DTransform(prop, (i % 20) * 6.4 + 2, 22 + Sin(t * 90 + i * 11), Floor(i / 20) * 12.8 + 4, 0.8, 0.8, 0.8, t * 60 + i * 9, "");
}
'@
        DrawGui = @'
DrawSetColorRgb(255, 255, 255);
DrawText(12, 12, "Complex scene: 64 job-built chunks, 1000 job-moved instances, 200 mesh draws a frame");
for (var i = 0; i < 40; i++) { DrawText(12 + (i % 8) * 150, 40 + Floor(i / 8) * 18, "Item " + i + " " + Floor(noise)); }
'@ } }).data.path
    Call "room.addInstance" @{ room = $complex; object = $director } | Out-Null

    Call "room.makeStart" @{ room = $sprites } | Out-Null
    Set-Content -LiteralPath (Join-Path $Folder "project-version.txt") -Value $projectVersion
}

$versionFile = Join-Path $Folder "project-version.txt"
$madeVersion = if (Test-Path -LiteralPath $versionFile) { (Get-Content -LiteralPath $versionFile -TotalCount 1).Trim() } else { "" }
if ($Rebuild -or -not (Test-Path (Join-Path $project "Assets")) -or $madeVersion -ne $projectVersion) { Build-Project }

$scenes = $Scenes
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
    # The three parts of the frame that took longest on average (FrameBudget).
    $parts = @($d.frameParts | Select-Object -First 3 | ForEach-Object { "$($_.Part) $([Math]::Round($_.AverageMilliseconds, 2))" }) -join ", "
    $rows += [pscustomobject]@{ Scene = $scene; Median = $d.medianMilliseconds; Fps = $d.medianFps; P95 = $d.p95Milliseconds; P99 = $d.p99Milliseconds
        Worst = $d.worstMilliseconds; KbPerFrame = $d.allocationKilobytesPerFrame; Draws = $d.averageDrawCalls; Triangles = $d.averageTriangles
        Backend = $d.backend; Parts = $parts; Over = ($over -join "; ") }
}

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$out = Join-Path $repo "TestResults\Benchmarks"
New-Item -ItemType Directory -Force $out | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$rows | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $out "$stamp.json")
$md = @("# Benchmark scenes $stamp", "", "Uncapped (vsync and caps off), $Seconds s after $Warmup s warm-up.", "",
    "| Scene | Median ms | fps | p95 | p99 | Worst | KB/frame | Draws | Triangles | Longest parts (ms) | Over budget |", "|---|---|---|---|---|---|---|---|---|---|---|")
$md += $rows | ForEach-Object { "| $($_.Scene) | $($_.Median) | $($_.Fps) | $($_.P95) | $($_.P99) | $($_.Worst) | $($_.KbPerFrame) | $($_.Draws) | $($_.Triangles) | $($_.Parts) | $($_.Over) |" }

# Tracking: each run's numbers go on the scene's history, compared with its earlier runs. A p95 more
# than 25% over the best of the last ten runs is called out (not failed: a busy machine is slower too).
if ($Track) {
    $history = Join-Path $out "history.jsonl"
    $earlier = @(if (Test-Path -LiteralPath $history) { Get-Content -LiteralPath $history | Where-Object { $_.Trim() } | ForEach-Object { try { $_ | ConvertFrom-Json } catch { } } })
    $md += @("", "## Against earlier runs (history.jsonl)", "")
    foreach ($row in $rows) {
        # Only runs of the same scenes compare: a new project version changes what is measured.
        $previousRuns = @($earlier | Where-Object { $_.scene -eq $row.Scene -and $_.p95 -gt 0 -and "$($_.version)" -eq $projectVersion } | Select-Object -Last 10)
        $line = "$($row.Scene): median $($row.Median) ms, p95 $($row.P95) ms"
        if ($previousRuns.Count -gt 0) {
            $last = $previousRuns[-1]
            $best = ($previousRuns | Measure-Object -Property p95 -Minimum).Minimum
            $change = [Math]::Round(($row.P95 - $last.p95) * 100 / [Math]::Max(0.01, $last.p95))
            $line += "; previous p95 $($last.p95) ms ($(if ($change -ge 0) { '+' })$change%), best of last $($previousRuns.Count) $best ms"
            if ($row.P95 -gt $best * 1.25) { $line += " - SLOWER than the best by more than 25%" }
        } else { $line += " (first tracked run of these scenes, version $projectVersion)" }
        $md += "- $line"
        [ordered]@{ time = (Get-Date).ToString("s"); scene = $row.Scene; version = $projectVersion; engine = $EngineHash; median = $row.Median; p95 = $row.P95; p99 = $row.P99
            worst = $row.Worst; kbPerFrame = $row.KbPerFrame; draws = $row.Draws; triangles = $row.Triangles; backend = $row.Backend; parts = $row.Parts; over = $row.Over } |
            ConvertTo-Json -Compress | Add-Content -LiteralPath $history -Encoding utf8
    }
}
$md | Set-Content -Encoding utf8 (Join-Path $out "$stamp.md")
# Each run leaves its frames and captures in the project; only the newest few are kept.
Get-ChildItem -LiteralPath $project -Recurse -Directory -Filter "Benchmarks" -ErrorAction SilentlyContinue |
    Where-Object { $_.Parent.Name -eq "Automation" } | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Directory | Sort-Object Name -Descending | Select-Object -Skip 14 |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    }
$md | ForEach-Object { Write-Output $_ }
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
if ($failed -gt 0) { Write-Output "$failed scene(s) over budget or failed."; exit 1 }
exit 0
