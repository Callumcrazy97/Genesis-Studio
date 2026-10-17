# Game profiles, 17 October 2026

Cal asked for profiles of the Genesis games and for the engine gaps they show to be fixed. Six
projects were measured on the same Player build, before and after, with the engine's own
instruments; allocation stacks came from `dotnet-trace`.

## Method

Each project's main room ran for 40 seconds through the benchmark mode with the whole-run profile:

```
GenesisEngine.exe --room <room> --benchmark-output <dir> --benchmark-seconds 40 --benchmark-warmup-seconds 3
GENESIS_PROJECT_PATH=<project>  GENESIS_START_ROOM=<room>  GENESIS_PROFILE=1  GENESIS_UNATTENDED_WINDOW=1
```

`benchmark.json` gives the frame percentiles (its own picture captures excluded), the profile's
`report.md` and `frames.csv` give allocation, collections and script time. "Steady" allocation
is the median per frame after 20 seconds, once loading has finished. Allocation stacks:
`dotnet-trace collect -p <pid> --profile gc-verbose` for 12 seconds, grouped by type and the first
engine frames. Runs held `Global\GenesisAgentTestRun`; game saves were copied before and restored.

GenesisCraft was not run here (its own session was measuring it); its latest profile was read.

## What the profiles showed, and what changed

| Found | Where | Fix |
|---|---|---|
| A top-level `return` in an event threw `ReturnException` every call | Mushroom Meadow | Events run through `PgslVm.ExecuteEvent`, which ends without an exception |
| Every event call allocated a new `ExecutionContext` (the PGSL context was an `AsyncLocal`) | Kinfield: 212 MB in 45 s over 950,000 calls | The context is per thread |
| Script names with spaces made two regexes per name per code segment, re-parsed every time | Kinfield's script load: 2.2 GB allocated | Names absent from the code are skipped; patterns are cached |
| `with (id)` copied every live instance to find one | Aetherium: 319 MB of `PgslBehavior[]` | Found by id directly; other targets use a reused list |
| A sprite name that resolves to nothing was searched and thrown for every frame | Aetherium: `FileNotFoundException` each frame | Misses are remembered until the asset policy says to look again |
| Forward-renderer batches regrew their instance lists every frame | Aetherium: 70 MB of `InstanceGpu[]` in 12 s | A batch keeps its key across frames |
| Animated characters allocated a skin palette per draw; layer lookups allocated closures | Golden Stag, Mushroom Meadow | Reused palette buffer; plain loops |
| Physics copied its bindings, sorted contacts with LINQ, boxed an enumerator and regrew its contact table every step | Aetherium | Reused lists, index loops, key-by-key clearing |
| `ScreenshotSave` encoded the PNG on the game thread (about 110 ms a picture) | Cathedral Test | Encoded on a worker; flushed at exit |
| Models a Script function names for `DrawModel3D` were not in the first-room preload (a 175 ms first draw) | Cathedral Test | The preload follows calls into Script resources |
| Post effects had no world-to-clip matrix, and the docs' `float4x4` read the camera matrices transposed | Cathedral Test's floor reflections | `ViewProjection` added; docs say `row_major` |
| Every animated sprite built a list of its playable frames on every advance | Kinfield: a third of what remained | One shared array per run of frames |
| Attachment sockets built every bone's world transform to find one bone | Golden Stag: a quarter | The bone's own chain only |
| Each new clip frame made a GPU skin palette and its upload copy | Golden Stag | Released palettes are reused by size |
| A call by a name that is no Script searched the catalog and the disk every time | Kinfield | Misses remembered until the asset policy says to look again |
| Reading a list of numbers boxed each number | Kinfield | `PGList.TryGetNumber` |
| Each UI element's override keys were joined strings, made per lookup per frame | Mushroom Meadow's menus | Keys made once per asset, element and property |
| A variable read before anything set it threw and caught an exception on every read | GenesisCraft's title screen, every frame | `PgslEngineBridge.TryInvokeKnown`: no exception; the same diagnostics note |

Commits: 82dce27 (game profiles), 82fce20 (Cathedral Test profile), 9e40b21 (list indexes, tests), a62ca5c (third round).

## Before and after (same build method, quiet machine)

| Project, room | Steady allocation | Longest GC pause | Script time | Other |
|---|---|---|---|---|
| Kinfield 25D, Pennywick | 320 → 16 KB/frame | 50 → 11 ms | 1.43 → 0.68 ms/frame | Load allocation 2.2 GB → 0.4 GB; start to "boot ready" about 4.4 → 2.7 s; worst frame 106 → 19 ms |
| Aetherium, island | 706 → 64 KB/frame | 47 → 39 ms | unchanged | Sampled allocation in 12 s: 333 → 60 MB |
| Mushroom Meadow, meadow | 96 → 64 KB/frame | 40 → 7 ms | 1.10 → 0.97 ms/frame | |
| Golden Stag, tavern | 128 → 64 KB/frame | 10 → 4.4 ms | (C# scripts) | Allocation 23 → 17 MB/s |
| Multiplayer Test, arena | 21 KB/frame | none | 0.21 ms/frame | Nothing to fix |
| Cathedral Test (3-minute guided tour) | 87 KB/frame | 4 ms | 0.58 ms/frame | Ultra tier with volumetric haze and floor reflections: 59.8 fps, p99 17.4 ms, worst 31 ms, GPU 2.4 ms; script pictures no longer hitch; first draws preloaded |

All of them hold 60 fps at the 95th percentile; the differences are in hitches, collections and
memory churn, which is what long sessions and slower machines feel.

## The Player's GC mode
Aetherium (the largest heap measured here, 1.3 GB) was run twice. With the Player's
SustainedLowLatency the collector paused for 137 ms in all, 42 ms at most; with .NET's default
(`GENESIS_GC_LATENCY=interactive`) for 243 ms, 82 ms at most. The Player keeps SustainedLowLatency.

## Still open

- GenesisCraft: collections became rarer but longer (42-48 ms) after the Player's switch to
  `SustainedLowLatency` (6b2c1e8). `GENESIS_GC_LATENCY=interactive` runs .NET's default mode for a
  comparison; its 2.3 GB heap of boxed values (`Object[]`) is the deeper cause.
- Remaining steady allocation in Aetherium is mostly boxing of numbers passed to commands
  (`VmValue.ToObject`).
- Screen-space reflections are still not an engine feature; Cathedral Test does floor reflections
  as a project post effect.
- Cathedral Test's horror chase (evening round, `GENESIS_PROFILE=1` over the unattended story walk,
  87 s): 59.9 fps, CPU 1.34 ms, GPU 2.32 ms, 56.6 KB allocated a frame. Fixed: a capturing delegate
  per script model draw (`ModelRenderQueue.Calls`), and four file-time reads per `PlaySound` in
  development runs (now at most twice a second per sound). Open:
  - a 27 ms frame at the first translucent glowing draw (no allocation, so likely a shader variant
    or render state made on first use);
  - each `ScreenshotSave` allocates an 8 MB frame copy (`TryReadSubmittedFramePixels`) on the large
    object heap, which caused the run's two full collections.
  After the fix: 52.1 KB a frame (from 56.6), the delegate gone from the allocation samples; most of
  what is left is the check's own screenshots. An allocation trace of the visit then showed the
  engine's own per-frame garbage, now gone: the overlay flush's boxed enumerator, a texture-lookup
  delegate per sprite flush, a new attachment-action array per render pass, and the audio update's
  list of finished channels. Still per frame: a closure in `RuntimeScene.CollectMeshes` and in
  `ObjectCompositionSubsystem.Update` (ECS queries), bone arrays per animated model
  (`EvaluateClipLocals`), and numbers boxed for engine commands (`PgslEngineBridge.CallMethod`).
