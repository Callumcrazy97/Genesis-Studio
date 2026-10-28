# The Nature Walk benchmark: builds, with Studio's own automation commands only, the same walk as
# reference-threejs.html (a quick three.js game), then plays it and measures it. It answers "how long
# does Genesis take to make that, and how does it look and run?" (Documentation/Design/FinishedProduct.md).
#
#   powershell -ExecutionPolicy Bypass -File BuildTools\Benchmarks\NatureWalk\NatureWalk.ps1 [-Project <folder>] [-Fresh] [-Play] [-Benchmark]
#
# -Fresh rebuilds the project from nothing (its folder is replaced). -Play takes pictures from a short
# walk (Debug/Images in the project); -Benchmark measures frame times. Both open a game window and take
# the machine-wide test lock (Global\GenesisAgentTestRun) for their run.
param(
    [string]$Project = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Genesis Projects\Nature Walk'),
    [string]$Studio = (Join-Path $PSScriptRoot '..\..\..\Genesis Application\Genesis Application.exe'),
    [switch]$Fresh,
    [switch]$Play,
    [switch]$Benchmark,
    # Writes the batch to this file and stops, touching no project: Documentation/NatureWalk.batch.json,
    # the Cookbook's worked example, is made this way.
    [string]$ExportBatch)
$ErrorActionPreference = 'Stop'
$Studio = (Resolve-Path $Studio).Path
$work = Join-Path ([IO.Path]::GetTempPath()) 'genesis-nature-walk'
New-Item -ItemType Directory -Force $work | Out-Null

function Invoke-Automation([string]$Command, $InputObject) {
    $file = Join-Path $work 'call.json'
    [IO.File]::WriteAllText($file, ($InputObject | ConvertTo-Json -Depth 12 -Compress), (New-Object Text.UTF8Encoding($false)))
    $text = & $Studio --automation $Project call $Command "@$file" 2>&1 | Out-String
    $result = $null
    try { $result = $text | ConvertFrom-Json } catch { }
    if ($LASTEXITCODE -ne 0 -or -not $result -or -not $result.ok) { throw "$Command failed: $text" }
    return $result
}

# The path round the lake, as the three.js version draws it: radius 64 + 9 sin 3a + 6 cos 5a about (35, 25).
$lakeX = 35; $lakeZ = 25
function Ring([double]$cx, [double]$cz, [double]$r, [int]$n, [double]$wobble = 0) {
    $points = @()
    for ($i = 0; $i -le $n; $i++) {
        $a = $i / $n * [Math]::PI * 2
        $rr = $r + $wobble * ([Math]::Sin(3 * $a) * 0.6 + [Math]::Cos(5 * $a) * 0.4)
        $points += , @([Math]::Round($cx + [Math]::Cos($a) * $rr, 2), [Math]::Round($cz + [Math]::Sin($a) * $rr, 2))
    }
    return , $points
}
function PathPoint([double]$degrees) {
    $a = $degrees * [Math]::PI / 180; $r = 64 + 9 * [Math]::Sin(3 * $a) + 6 * [Math]::Cos(5 * $a)
    return @(($lakeX + [Math]::Cos($a) * $r), ($lakeZ + [Math]::Sin($a) * $r))
}

$clock = [Diagnostics.Stopwatch]::StartNew()
if ($Fresh -and -not $ExportBatch -and (Test-Path $Project)) { Remove-Item -LiteralPath $Project -Recurse -Force }
if (-not $ExportBatch -and -not (Test-Path (Join-Path $Project '*.genesisproj'))) {
    $made = & $Studio --automation new (Split-Path $Project) (Split-Path $Project -Leaf) Blank 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Could not make the project: $made" }
}

$T = 'Assets/Terrain/Valley.terrain.json'
$R = 'Assets/Rooms/Meadow.room.json'
function Part($primitive, $position, $scale, $color, $rotation = @(0, 0, 0)) { @{ primitive = $primitive; position = $position; scale = $scale; color = $color; rotation = $rotation } }
function Call($command, $inputObject) { @{ command = $command; input = $inputObject } }
$start = PathPoint 21.6; $ahead = PathPoint 26
$startYaw = [Math]::Atan2($ahead[0] - $start[0], $ahead[1] - $start[1]) * 180 / [Math]::PI

$calls = @(
    (Call 'room.create' @{ name = 'Meadow'; dimension = '3D'; scene = 'SunnyDay'; width = 400; height = 80; depth = 400; windowWidth = 1600; windowHeight = 900; start = $true }),
    # Ground: rolling hills, a ring of higher hills, a lake basin and a dirt path, with the Terrain editor's brushes.
    (Call 'terrain.create' @{ name = 'Valley'; preset = 'RollingHills'; seed = 7; resolutionX = 401; resolutionZ = 401; cellSize = 1; minHeight = -8; maxHeight = 28; erosionIterations = 20 }),
    (Call 'terrain.sculpt' @{ terrain = $T; brush = 'Raise'; points = (Ring 0 0 165 48 20); radius = 34; strength = 1 }),
    (Call 'terrain.sculpt' @{ terrain = $T; brush = 'Raise'; points = (Ring 0 0 180 48 20); radius = 34; strength = 1 }),
    (Call 'terrain.sculpt' @{ terrain = $T; brush = 'Flatten'; points = (Ring $lakeX $lakeZ 4 24); radius = 12; strength = 1; flattenHeight = 0 }),
    (Call 'terrain.sculpt' @{ terrain = $T; brush = 'Flatten'; points = (Ring $lakeX $lakeZ 12 24); radius = 12; strength = 1; flattenHeight = 0 }),
    (Call 'terrain.sculpt' @{ terrain = $T; brush = 'Flatten'; points = (Ring $lakeX $lakeZ 20 24); radius = 12; strength = 1; flattenHeight = 0 }),
    (Call 'terrain.sculpt' @{ terrain = $T; brush = 'Smooth'; points = (Ring $lakeX $lakeZ 32 32); radius = 10; strength = 1 }),
    (Call 'terrain.sculpt' @{ terrain = $T; brush = 'Smooth'; points = (Ring $lakeX $lakeZ 64 96 15); radius = 4; strength = 1 }),
    # A faint worn verge keeps trees off the path (they need nearly pure grass), then the path itself.
    (Call 'terrain.sculpt' @{ terrain = $T; brush = 'Paint'; points = (Ring $lakeX $lakeZ 64 96 15); radius = 6; strength = 0.05; layer = 'Dirt' }),
    (Call 'terrain.sculpt' @{ terrain = $T; brush = 'Paint'; points = (Ring $lakeX $lakeZ 64 96 15); radius = 2.2; strength = 1; layer = 'Dirt' }),
    # Trees and rocks from the Model editor's primitives, scattered by the Terrain editor's rules.
    (Call 'model.create' @{ name = 'Pine'; parts = @(
        (Part Cylinder @(0, 1, 0) @(0.35, 2, 0.35) '#5a3d26'), (Part Cone @(0, 2.6, 0) @(3.2, 2.6, 3.2) '#22482a'),
        (Part Cone @(0, 3.9, 0) @(2.5, 2.2, 2.5) '#2a5531'), (Part Cone @(0, 5.0, 0) @(1.7, 1.8, 1.7) '#336238')) }),
    (Call 'model.create' @{ name = 'Oak'; parts = @(
        (Part Cylinder @(0, 1.3, 0) @(0.45, 2.6, 0.45) '#6b4a2e'), (Part Sphere @(0, 3.5, 0) @(3.2, 2.8, 3.2) '#4f7d2a'),
        (Part Sphere @(0.95, 3.0, 0.4) @(2.2, 2.0, 2.2) '#5d8c30'), (Part Sphere @(-0.85, 3.1, -0.35) @(2.4, 2.1, 2.4) '#47722a')) }),
    (Call 'model.create' @{ name = 'Boulder'; parts = @(
        (Part Sphere @(0, 0.3, 0) @(1.6, 1.0, 1.3) '#8a857c' @(0, 20, 8)), (Part Sphere @(0.4, 0.5, 0.2) @(0.9, 0.8, 0.9) '#7d786f' @(10, 0, 0))) }),
    (Call 'terrain.addScatterLayer' @{ terrain = $T; name = 'Pines'; model = 'Assets/Models/Pine.model.json'; densityPerHectare = 60; minimumSpacing = 4; minimumHeight = 7; maximumSlopeDegrees = 32; clumpSize = 40; clumpStrength = 0.7; minimumScale = 0.8; maximumScale = 1.5; castShadows = $true; collisionRadius = 0.25; collisionHeight = 2; seed = 3; paintLayerMask = 1; minimumPaintWeight = 0.92 }),
    (Call 'terrain.addScatterLayer' @{ terrain = $T; name = 'Oaks'; model = 'Assets/Models/Oak.model.json'; densityPerHectare = 25; minimumSpacing = 6; minimumHeight = 4; maximumSlopeDegrees = 28; clumpSize = 30; clumpStrength = 0.5; minimumScale = 0.75; maximumScale = 1.35; castShadows = $true; collisionRadius = 0.3; collisionHeight = 2.5; seed = 5; paintLayerMask = 1; minimumPaintWeight = 0.92 }),
    (Call 'terrain.addScatterLayer' @{ terrain = $T; name = 'Boulders'; model = 'Assets/Models/Boulder.model.json'; densityPerHectare = 12; minimumSpacing = 5; minimumScale = 0.4; maximumScale = 2.2; sink = 0.3; alignToGround = $true; castShadows = $true; seed = 9 }),
    (Call 'model.create' @{ name = 'Daisy'; parts = @((Part Cylinder @(0, 0.14, 0) @(0.02, 0.28, 0.02) '#3f6b22'), (Part Sphere @(0, 0.3, 0) @(0.12, 0.05, 0.12) '#f4f0e6'), (Part Sphere @(0, 0.31, 0) @(0.05, 0.04, 0.05) '#f2c94c')) }),
    (Call 'model.create' @{ name = 'Campion'; parts = @((Part Cylinder @(0, 0.16, 0) @(0.02, 0.32, 0.02) '#3f6b22'), (Part Sphere @(0, 0.34, 0) @(0.11, 0.06, 0.11) '#d9649a')) }),
    (Call 'terrain.addScatterLayer' @{ terrain = $T; name = 'Daisies'; model = 'Assets/Models/Daisy.model.json'; densityPerHectare = 2500; minimumSpacing = 0.4; maximumSlopeDegrees = 25; clumpSize = 12; clumpStrength = 0.6; minimumScale = 0.8; maximumScale = 1.3; drawDistance = 45; castShadows = $false; seed = 11; paintLayerMask = 1 }),
    (Call 'terrain.addScatterLayer' @{ terrain = $T; name = 'Campions'; model = 'Assets/Models/Campion.model.json'; densityPerHectare = 1500; minimumSpacing = 0.5; maximumSlopeDegrees = 25; clumpSize = 10; clumpStrength = 0.7; minimumScale = 0.8; maximumScale = 1.3; drawDistance = 45; castShadows = $false; seed = 13; paintLayerMask = 1 }),
    (Call 'terrain.setGrass' @{ terrain = $T; enabled = $true; species = 'MeadowGrass'; radius = 90; spacing = 0.3; minimumScale = 0.5; maximumScale = 0.85 }),
    (Call 'terrain.addWater' @{ terrain = $T; name = 'Lake'; kind = 'Lake'; center = @($lakeX, 3.2, $lakeZ); sizeX = 62; sizeZ = 56; surfaceHeight = 3.2 }),
    # Sound from Genesis's synthesiser: footsteps on grass and on the path, wind, birdsong.
    (Call 'audio.synth' @{ name = 'Step Grass'; preset = 'Footstep' }),
    (Call 'audio.synth' @{ name = 'Step Path'; layers = @(
        @{ wave = 'Noise'; frequency = 700; duration = 0.14; attack = 0.003; decay = 0.05; sustain = 0.3; release = 0.06; lowPass = 1400; volume = 0.8 },
        @{ wave = 'Sine'; frequency = 90; frequencyEnd = 60; duration = 0.08; attack = 0.002; release = 0.05; volume = 0.4 }) }),
    (Call 'audio.synth' @{ name = 'Wind'; preset = 'Wind' }),
    (Call 'audio.synth' @{ name = 'Birds'; layers = @(
        foreach ($i in 0..3) { $f = 2780 + $i * 180; @{ wave = 'Sine'; frequency = $f; frequencyEnd = $f * 1.45; start = 0.4 + $i * 0.13; duration = 0.08; attack = 0.008; decay = 0.03; sustain = 0.4; release = 0.04; volume = 0.35 } }
        foreach ($i in 0..2) { $f = 3200 - $i * 200; @{ wave = 'Sine'; frequency = $f; frequencyEnd = $f * 0.8; start = 2.52 + $i * 0.16; duration = 0.11; attack = 0.01; release = 0.05; volume = 0.25 } }) }),
    (Call 'particle.create' @{ name = 'Motes'; preset = 'DustMotes' }),
    # The walker: the Object editor's "Walk in first person" recipe, with footsteps that follow the ground.
    (Call 'object.create' @{ name = 'Walker'; recipes = @('FirstPerson'); events = @{
        Create = "stepSound = `"Step Grass`";`n// The look: shade a little lighter, as the sky lights it in the three.js version.`nEngine.Rendering.Shadows.Strength = 0.7;`n// Start with the feet on the ground, wherever the path is.`ny = Engine.Terrain.GetHeight(x, z);`nCharacterSetPosition(x, y, z);`n// A second in, the game checks itself (game.run reports these).`nSetAlarm(1, 60);"
        Step = "// The path is the terrain's second paint layer (Dirt).`nif (Engine.Terrain.MaterialAt(x, z) == 2) { stepSound = `"Step Path`"; } else { stepSound = `"Step Grass`"; }"
        Alarm1 = "TestCheck(`"the walker stands on the ground`", Abs(y - Engine.Terrain.GetHeight(x, z)) < 0.3, `"feet `" + String(y) + `", ground `" + String(Engine.Terrain.GetHeight(x, z)));`nTestCheck(`"the walk starts on the path`", Engine.Terrain.MaterialAt(x, z) == 2, `"layer `" + String(Engine.Terrain.MaterialAt(x, z)));" } }),
    (Call 'object.setComponent' @{ object = 'Assets/Objects/Walker.object.json'; type = 'ParticleComponent'; values = @{ Asset = 'Assets/Particles/Motes.particle.json' } }),
    (Call 'room.addTerrain' @{ room = $R; terrain = $T }),
    (Call 'room.addInstance' @{ room = $R; object = 'Assets/Objects/Walker.object.json'; x = [Math]::Round($start[0], 2); y = 12; z = [Math]::Round($start[1], 2); yaw = [Math]::Round($startYaw, 1) }),
    (Call 'room.setEnvironment' @{ room = $R; values = @{ timeOfDayHours = 15.5; shadowDistance = 150; visibilityKilometres = 2.5; windAudio = 'Wind'; wildlifeAudio = 'Birds' } })
)
if ($ExportBatch) {
    # One command a line, so it reads (and copies) as a list.
    $lines = @($calls | ForEach-Object { '    ' + ($_ | ConvertTo-Json -Depth 12 -Compress) })
    [IO.File]::WriteAllText($ExportBatch, "{`n  `"calls`": [`n" + ($lines -join ",`n") + "`n  ]`n}`n", (New-Object Text.UTF8Encoding($false)))
    "Wrote the batch of $($calls.Count) commands to $ExportBatch"
    return
}
$batch = Invoke-Automation 'batch' @{ calls = $calls }
$built = $clock.Elapsed.TotalSeconds
"Built in {0:0.0} s with {1} commands: {2}" -f $built, $calls.Count, $batch.message
foreach ($issue in @($batch.issues)) { if ($issue) { "  {0}: {1} ({2} line {3})" -f $issue.severity, $issue.message, $issue.path, $issue.line } }

function Invoke-Locked([string]$Command, $InputObject) {
    $lock = New-Object System.Threading.Mutex($false, 'Global\GenesisAgentTestRun')
    try { [void]$lock.WaitOne([TimeSpan]::FromMinutes(90)) } catch [System.Threading.AbandonedMutexException] { }
    try { return Invoke-Automation $Command $InputObject } finally { $lock.ReleaseMutex() }
}
if ($Play) {
    $run = Invoke-Locked 'game.run' @{ steps = @(@{ seconds = 4 }, @{ seconds = 3; hold = @('W') }, @{ seconds = 1 }); shots = @(3.5, 7.5) }
    "Played: $($run.message) The game took $($run.data.startSeconds) s to start; the run $($run.data.seconds) s."
    foreach ($check in @($run.data.checks)) { if ($check) { "  {0} {1}: {2}" -f $(if ($check.passed) { 'PASS' } else { 'FAIL' }), $check.name, $check.detail } }
    foreach ($shot in @($run.data.pictures)) { "  $shot" }
}
if ($Benchmark) {
    $bench = Invoke-Locked 'game.benchmark' @{ seconds = 10; steps = @(@{ seconds = 12; hold = @('W') }) }
    "Benchmark: $($bench.message)"
    $d = $bench.data
    "  median {0} ms, p95 {1} ms, p99 {2} ms, worst {3} ms; {4} KB allocated a frame; {5} draw calls" -f $d.medianMilliseconds, $d.p95Milliseconds, $d.p99Milliseconds, $d.worstMilliseconds, $d.allocationKilobytesPerFrame, $d.averageDrawCalls
}
