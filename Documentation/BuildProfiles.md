# Build profiles

The build, validation, game export and recovery guide is maintained in the [master document](README.md#build-validation-and-recovery).

## Precompiled engine shaders

Every build (Quick and Full) has a step "Precompile engine shaders for Studio and Player". It runs
the staged Player as a tool:

```bat
Player\GenesisEngine.exe --precompile-shaders <folder> [--formats dxbc,dxil,spirv,glsl] [--parallel n] [--reuse <older folder>]
```

It compiles the engine's built-in shaders (`EngineShaderCatalog.PrecompiledJobs`) for DX11 (DXBC),
DX12 (DXIL), Vulkan (SPIR-V) and OpenGL (GLSL) into `Player\PrecompiledShaders`, which the build
copies beside Studio as well. Programs whose key and SHA-256 match the last promoted package's
folder are kept instead of compiled (`--reuse`), so the step takes under a second when no engine
shader changed and about 10 seconds when they all did. Exit code 0 means every program compiled;
the log is `TestResults\Builds\<run>\precompile-shaders.log`.

At run time the folder is read before the user's shader cache, with the same keys, so the first
game or 3D view after an engine update draws without compiling. A changed shader or compiler
misses it and compiles; a file that does not match `manifest.txt` is compiled instead of used.
`GENESIS_PRECOMPILED_SHADERS=<folder>` uses another folder, `GENESIS_PRECOMPILED_SHADERS=0` turns
it off. Checks: `--test shader-precompiled`.

## Project shaders

A project's own Shader resources (mesh, sprite and Fullscreen post effects) are not in that folder.
They use the runtime cache's keys, and `ProjectShaderPrograms` lists the programs exactly as the
draws and post effects compile them: every enabled pass's vertex, skinned vertex and pixel
entries, and a Fullscreen effect's own entry.

**Export.** *Export Game* compiles that list, every variant included, for DX11, DX12, Vulkan and
OpenGL into the game's `.genesis-shaders` folder, which the exported Player uses as its shader
cache, so the game's first start on any PC compiles none of them. A program one compiler rejects
is left out and named in the export dialog; the game then compiles it when it first draws with
it, as Run does. An include no longer puts its folder into the key, so a shader that includes a
file is found again where the game is installed. Checks: `--test export-pgsl` (`Export.Shaders.*`)
and `--test shader-precompiled` (`Render.Shaders.Project.*`).

**First draw.** Anything not made ahead (a variant only an instance names, a shader edited while
the game runs) is compiled by the draw that needs it. The slow-frame report names it
("loading in that frame: 1 shader compiled 412 ms").

Measured on 9 Oct 2026 (i7-14700F, P-cores, idle PC, GenesisCraft's ten shaders: four mesh, six
Fullscreen; three cold runs each): one compile costs 27-50 ms in DXC (a process per compile) and
10-85 ms in fxc, except the block shader's 2048-entry `static const` table, which takes fxc
1.14-1.17 s (DXC 72-79 ms). All ten, one after another: DX11 1.36-1.40 s, DX12 0.40-0.54 s,
Vulkan 0.55-0.61 s, OpenGL 0.56-0.59 s; on four workers 1.09-1.33 s, 0.14-0.16 s, 0.18-0.19 s and
0.19-0.21 s. The 45 s first-run frame GenesisCraft reported was one 13 KB DX12 compile (50 ms when
idle) made inside the frame while the PC was saturated (that run's Player took 63 s to write its
first log line, normally 1-2 s).
