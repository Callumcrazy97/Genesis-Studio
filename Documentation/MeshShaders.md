# Mesh shaders

A **Mesh** Shader resource replaces the pixel shader the engine draws a model with. The engine
still runs its own vertex shader, so skinning, instancing and wind keep working; your shader
decides each pixel's colour. This page covers where a mesh shader can be used and the inputs it
can rely on.

## Where a mesh shader is used

| Where | How |
|---|---|
| A whole Object | The Object's **Shader** (and its parameter and texture values) in the Object editor. |
| One material of a model | `materialShaders` in the `.model.json`, material name to Shader resource, for example `"materialShaders": { "Glass": "Window Glass" }`. A building's glass and walls can be one model. The setting is kept when the model is imported again. |
| One instance, while the game runs | The script commands below. |

A material's own shader wins over the Object's shader for that material; the Object's shader
covers the rest of the model.

See-through draws keep their shader. A material or instance that is partly transparent (alpha
below 1, or a fading Object) is drawn in the transparent pass with its own shader, parameters
and textures; only the blending is the pass's. Glass, ghost walls and ability shells can
therefore use custom shaders.

## Per-instance values from scripts

Every instance can carry its own values for one shader: every weapon its own camo, every window
its own glow. Values are set by the name the parameter has in the shader's `GenesisParameters`
and replace the shader's and the Object's own values for that instance only.

| Command | What it does |
|---|---|
| `ShaderSet(shader)` | Draws this instance with a Shader resource (empty for none). |
| `ShaderSetParameter(name, value)` | Sets one of this instance's `float` parameters. |
| `ShaderSetVector(name, x, y, z, w)` | Sets a `float2`, `float3` or `float4` parameter (extra components are ignored). |
| `InstanceSetShader(id, shader)` | As `ShaderSet`, for another instance. |
| `InstanceSetShaderParameter(id, name, value)` | As `ShaderSetParameter`, for another instance. |
| `InstanceSetShaderVector(id, name, x, y, z, w)` | As `ShaderSetVector`, for another instance. |
| `InstanceGetShaderParameter(id, name)` | A value set on an instance (its first component); 0 when none of that name is set. |

In a weapon's Create event, for example:

```pgsl
ShaderSet("Weapon Camo")
ShaderSetVector("CamoTint", 0.32, 0.41, 0.22, 1)
ShaderSetParameter("Wear", random(1))
```

## Inputs a mesh shader can rely on

These registers and fields are stable. Declare only the ones you use.

### Your own values

| Register | Contents |
|---|---|
| `b4` `GenesisFrame` | `float Time; float Frame; float2 Resolution;` |
| `b5` `GenesisParameters` | Your parameters, up to 16 floats (four `float4` rows). Their names are what the Object editor shows and what the commands above set. |
| `t17`–`t20` | Free for your own textures (set in the Shader editor's Resources; an Object can override them). `t21`–`t23` are free except on terrain. |

### What the engine passes in

The pixel shader receives the engine's vertex output. Declare it with these semantics (later
fields can be left off):

```hlsl
struct VSOut
{
    float4 SvPos       : SV_Position;
    float3 WorldPos    : TEXCOORD1;
    float3 Normal      : TEXCOORD2;   // world space, not normalised
    float4 Color       : TEXCOORD3;   // instance and material tint
    float2 UV          : TEXCOORD4;
    float4 ShadowPos   : TEXCOORD5;
    float4 ShadowPosNr : TEXCOORD6;
    float  AtlasLayer  : TEXCOORD7;
    float4 ShadowPosMd : TEXCOORD8;
};
```

| Register | Contents |
|---|---|
| `t1` `AlbedoTex`, `s0` `AlbedoSamp` | The material's base colour texture and its sampler. |
| `t3` `NormalMap`, `t7` `OrmMap`, `t9` `EmissionMap` | The material's other maps, where it has them. |
| `b0` `PerFrameConstants` | `row_major float4x4 ViewProjection;` then the light matrices; `float4 CameraPosTime` (camera position, time in seconds). |
| `b1` `EngineConstants` | Begins `float4 LightDirEnabled; float4 FogParams; float4 FogColor; float4 AmbientColor; float4 AmbientGroundColor; float4 SunColorIntensity;` |
| `b2` `DrawConstants` | Begins `row_major float4x4 WorldMatrix; float4 MaterialColor; float4 MaterialParams;` (x emissive, y unlit). |

`LightDirEnabled.xyz` is the direction the sunlight travels; `SunColorIntensity` is its colour
(rgb) and strength (w). A constant buffer may be declared as a prefix: list the fields up to the
last one you need, in the order above.

The full shader the engine compiles is in `ForwardShaders.cs`; the Shader editor's
reference panel lists the same inputs.
