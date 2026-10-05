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
shader changed and about 25 seconds when they all did. Exit code 0 means every program compiled;
the log is `TestResults\Builds\<run>\precompile-shaders.log`.

At run time the folder is read before the user's shader cache, with the same keys, so the first
game or 3D view after an engine update draws without compiling. A changed shader or compiler
misses it and compiles; a file that does not match `manifest.txt` is compiled instead of used.
Project shaders are compiled and cached at run time as before. `GENESIS_PRECOMPILED_SHADERS=<folder>`
uses another folder, `GENESIS_PRECOMPILED_SHADERS=0` turns it off. Checks: `--test shader-precompiled`.
