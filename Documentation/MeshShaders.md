# Mesh shaders

A **Mesh** Shader resource changes how a model's pixels look, and a surface shader can also move
its vertices ([Vertex stage](#vertex-stage)). The engine still runs its own vertex shader, so
skinning, instancing and wind keep working. There are two kinds:

- A **surface shader** (`#include "GenesisSurface.hlsl"`) works out the surface (its colour,
  normal, roughness, metalness, light given off, see-through amount) and the engine lights it, with
  everything it lights its own materials with: the sun and its shadows, ambient light, point and
  spot lights and their shadows, environment reflection, wet weather and fog. Use this for camo, lit
  windows, glass, dissolves, decals and most looks. Added 9 October 2026.
- A **full pixel shader** decides each pixel's final colour itself. It lights nothing unless it does
  so itself, and has to declare the engine's constants to read the sun or the camera. See
  [Full pixel shaders](#full-pixel-shaders).

This page covers both, where a mesh shader can be used and the values a script can give each
instance. A surface shader can also be built from nodes: **Nodes** in the Shader editor writes
this same code from a graph ([the node view](README.md#node-view-29-october-2026)).

## Where a mesh shader is used

| Where | How |
|---|---|
| A whole Object | The Object's **Shader** (and its parameter and texture values) in the Object editor. |
| One material of a model | The Model editor's **Texture** page: select a mesh, then **Shader** under **Material & PBR** (`…` to choose, `×` for none). It is kept in the `.model.json` as `materialShaders`, material name to Shader resource, for example `"materialShaders": { "Glass": "Window Glass" }`, so a re-import keeps it. A building's glass and walls can be one model. |
| One instance, while the game runs | The script commands below. |
| A script's draw | `DrawModelShader3D`, `DrawMeshShader3D`. |

A material's own shader wins over the Object's shader for that material; the Object's shader
covers the rest of the model. (Until 9 October a room's Objects gave every material the Object's
shader; the Object editor's preview already did as described.)

See-through draws keep their shader. A material or instance that is partly transparent (alpha
below 1, or a fading Object) is drawn in the transparent pass with its own shader, parameters
and textures; only the blending is the pass's. This includes draws with no shadow and no depth
write, which are otherwise batched like particles. Glass, ghost walls and ability shells can
therefore use custom shaders.

The Software renderer draws a model's own material in place of any mesh shader.

## Surface shaders

```hlsl
#include "GenesisSurface.hlsl"

cbuffer GenesisParameters : register(b5) { float4 CamoTint; float CamoScale; };
Texture2D CamoPattern : register(t17);

void Surface(GenesisSurfaceInput i, inout GenesisSurface s)
{
    float3 pattern = GenesisColor(CamoPattern.Sample(GenesisLinearWrap, i.UV * CamoScale).rgb);
    s.Albedo = GenesisColor(CamoTint.rgb) * pattern;
    s.Roughness = 0.65;
}
```

`s` arrives holding the material's own values (its texture, colour and tint, normal map, ORM map
and factors, emission map), so a surface changes only what it means to: the empty
`void Surface(GenesisSurfaceInput i, inout GenesisSurface s) { }` looks exactly like the material
without a shader. The engine then lights the result.

The include is built into the engine (there is no file to copy); the line must be on a line of its
own. The resource's entry point is ignored: the engine's own pixel shader is the entry. Declare
`GenesisParameters` (b5) and textures at t17-t23 as for any mesh shader; the Shader editor and the
Object editor show and set them the same way.

### What the surface returns

`GenesisSurface`:

| Field | Meaning | Starts as |
|---|---|---|
| `float3 Albedo` | Base colour, in the space the engine lights in (see below). | The material's texture × colour × the draw's tint. |
| `float Alpha` | Coverage. Below the material's alpha cut-off (0.35 unless the material sets one) the pixel is not drawn; a see-through draw is blended by it. The draw's own fade still multiplies it. | The texture's alpha. |
| `float3 Normal` | World space; it is normalised. | The mesh's normal bent by the material's normal map. |
| `float Roughness` | 0 a mirror, 1 matte. | The ORM map's green, or the material's factor, or 0.72. |
| `float Metalness` | 0 to 1. | The ORM map's blue, or the material's factor, or 0. |
| `float Occlusion` | Ambient occlusion, 1 none. | The ORM map's red, or 1. |
| `float3 Emission` | Light the surface gives off, added after lighting, in the lighting space; may exceed 1. | The material's emission map × its strength. |

The material's glow (`ModelSetGlow`, the emissive factor, hit flashes) is still added on top, from
the surface's albedo.

**Colours.** `Albedo`, `Emission`, `BaseColorTexture`, `SunColor` and the ambient colours are in
the space the engine lights in: linear when the project's colour pipeline is linear, as picked
otherwise. `GenesisColor(c)` turns a colour picked in a colour box, or
read from an ordinary image, into that space; values the surface receives are already in it.

### What the surface can read

`GenesisSurfaceInput` (contract version 1, `GENESIS_SURFACE_VERSION`; fields are only ever added):

| Field | Contents |
|---|---|
| `float3 WorldPosition` | The pixel's position in the world. |
| `float3 Normal` | The mesh's own normal (before any normal map), world space, unit length, towards the side drawn. |
| `float3 ViewDirection` | Unit vector from the surface to the camera. |
| `float3 CameraPosition` | The camera in the world. |
| `float2 UV` | The mesh's texture coordinates. |
| `float2 MaterialUV` | The coordinates the material's maps are read with (its UV scale, flow and parallax). |
| `float4 Color` | The draw's colour: vertex colour × material colour × instance tint, as picked, and its alpha. |
| `float4 BaseColorTexture` | The material's base colour image at `MaterialUV` (rgb in the lighting space, a as stored). |
| `float2 ScreenUV`, `float2 ScreenPosition` | The pixel's place on screen, 0 to 1 and in pixels. |
| `float Time`, `float Frame`, `float2 Resolution` | As `GenesisFrame`: seconds since the game started, the frame number, the image's size. |
| `float3 SunDirection`, `float3 SunColor` | Unit vector towards the sun; its colour times its strength. |
| `float3 AmbientSky`, `float3 AmbientGround` | The ambient light from above and below. |
| `bool FrontFace` | False on the back of a two-sided surface. |

Helpers: `GenesisColor(c)`, `GenesisLuminance(c)`, `GenesisFresnel(i, normal, power)` (0 facing the
camera, 1 at a grazing angle), `GenesisPerturbNormal(i, normal, tangentNormal)` (bends a normal by a
tangent-space normal in -1..1, for a normal map of the surface's own) and the sampler
`GenesisLinearWrap` (s3, smooth and repeating; `AlbedoSamp`, s0, is the material's own). The
engine's declarations come before the surface (its constant buffers, `AlbedoTex` and the other
material maps, `SrgbToLinear3`, `Hash21`, `InterleavedGradientNoise`), so their names are taken:
give your own functions names of their own.

Registers a surface shader leaves alone: b0-b4 and b6 are the engine's (its `GenesisFrame` is read
through the input, not declared); t0-t16 and s0-s2 likewise.

### Examples

Lit windows at dusk, one shader for every window of a building's glass material; a script raises
`Glow` as the sun goes down (`ShaderSetParameter("Glow", ...)` on each building, or the Object's
parameter):

```hlsl
#include "GenesisSurface.hlsl"
cbuffer GenesisParameters : register(b5) { float Glow; float4 WindowColor; };

void Surface(GenesisSurfaceInput i, inout GenesisSurface s)
{
    // Some windows stay dark: one random number per window pane of 1.2 m.
    float pane = Hash21(floor(i.WorldPosition.xz / 1.2) + floor(i.WorldPosition.y / 1.2) * 17.0);
    float lit = step(0.35, pane) * Glow;
    s.Emission += GenesisColor(WindowColor.rgb) * lit * 3.0;
    s.Roughness = 0.1;
}
```

Glass with a Fresnel edge, drawn see-through (the material's alpha mode Blend, or an Object faded
below 1):

```hlsl
#include "GenesisSurface.hlsl"
void Surface(GenesisSurfaceInput i, inout GenesisSurface s)
{
    float edge = GenesisFresnel(i, s.Normal, 3.0);
    s.Albedo = GenesisColor(float3(0.75, 0.85, 0.95));
    s.Alpha = lerp(0.12, 1.0, edge);
    s.Roughness = 0.05;
    s.Emission += i.AmbientSky * edge * 0.3;
}
```

## Vertex stage

Added 29 October 2026. A surface shader may also define a **Vertex** function, which moves the
mesh's vertices before they are drawn: grass and trees swaying in the wind, waves on water, a flag,
a breathing creature, a jelly's wobble.

```hlsl
#include "GenesisSurface.hlsl"
cbuffer GenesisParameters : register(b5) { float Amplitude; float Wavelength; float Speed; };

void Vertex(GenesisVertexInput i, inout GenesisVertex v)
{
    float k = 6.28318 / max(Wavelength, 0.05);
    v.Position.y += sin(v.Position.x * k + i.Time * Speed) * Amplitude;
}

void Surface(GenesisSurfaceInput i, inout GenesisSurface s) { }
```

`void Vertex(GenesisVertexInput i, inout GenesisVertex v)` runs once for each vertex inside the
engine's own vertex shaders, **after skinning and the material's height displacement (and the
foliage wind the engine gives foliage), before the world transform**. So `v` is in the model's own
space: a skinned character is already posed, and a model turned or scaled in the room turns and
scales the movement with it. Instancing, skinning, the material's maps and everything the surface
does keep working. A surface shader without a Vertex function draws with the engine's vertex
shaders exactly as before; a full pixel shader has no vertex stage (it can give a vertex entry of
its own instead, in the pass settings).

`GenesisVertex` (change any of it):

| Field | Meaning |
|---|---|
| `float3 Position` | The vertex in the model's own space, after skinning. |
| `float3 Normal` | Its normal in the model's own space; the engine normalises it after. Tilt it when the surface's slope changes, so the light follows. |
| `float2 UV` | Its texture coordinates (the surface's `i.UV` and the material's maps follow a change). |
| `float4 Color` | Its vertex colour as stored, and alpha. |

`GenesisVertexInput` (contract version 1, read only):

| Field | Contents |
|---|---|
| `float Time` | Seconds since the game started; the same clock as the surface's `i.Time`. |
| `float3 ObjectPosition` | Where the drawn object (this copy, when many are drawn together) stands in the world. Add it to a wave's phase so neighbours do not move as one. |
| `float3 WorldPosition` | This vertex in the world, before Vertex moves it: waves that run across many objects as one sea. |
| `float3 CameraPosition` | The camera in the world. |
| `float2 Wind` | The weather's wind over the ground, world x and z, in metres a second; 0 when the room has no weather. |
| `float2 ObjectWind` | The same wind along the model's own x and z (turned and scaled with it): add it to `v.Position.xz` to bend with the wind. |

The shader's parameters (`GenesisParameters`, b5) can be read in Vertex as in Surface, and scripts
set them per instance the same way. Textures cannot be read in Vertex (they are bound for the
surface).

How it works: the line `#include "GenesisSurface.hlsl"` already makes the engine compile its own
pixel shader around the project's code. When the code also has `void Vertex(`, it defines
`GENESIS_SURFACE_VERTEX`, places the vertex contract before the engine's vertex shaders, and
compiles their entries `VS` and `VS_Skinned` from the same text; the engine's vertex shaders call
the function where the hooks are (`ForwardShaders.cs`, `#if defined(GENESIS_SURFACE_VERTEX)`).
Every renderer gets it through the same compile path (DXBC for DX11, DXIL, SPIR-V for Vulkan,
GLSL from SPIR-V for OpenGL).

**Shadows.** The sun's and lamps' shadow casters are still drawn with the engine's own shadow
vertex shaders, so a shadow falls from the shape before Vertex moved it. A sway or a ripple of a
few centimetres hardly shows; a big displacement casts its shadow from the undisplaced shape. The
shadow vertex shaders already call Vertex when compiled from a vertex-stage shader (their hooks are
in place); the shadow pass choosing them per shader is still to come. The outline marks
(`InstanceSetOutline`) use the undisplaced shape too.

**In the Shader editor.** The **Wind Sway** and **Wave** presets have a vertex stage. On the Code
page, **Vertex** goes to the shader's Vertex function, or adds an empty one to a surface shader that
has none (Undo takes it away). The editor compiles the vertex stage (DXBC and SPIR-V) with the
pixel shader, so a mistake in Vertex shows in Diagnostics, and the preview moves with it (Play runs
its time). In the node view, the Output's **Vertex offset** (Mesh targets) becomes the Vertex
function: what feeds it is worked out per vertex (UV, Normal and Object position are the vertex's
own, in the model's space; it cannot read textures). The vertex stage is made ahead like the rest of
a shader: the export cooks it and a room's loading warms it (`ProjectShaderPrograms` lists `VS` and
`VS_Skinned` for a shader with a Vertex function).

## Full pixel shaders

A Shader resource without the include is a full pixel shader: its entry point (normally `MainPS`)
replaces the engine's pixel shader and its output is the pixel's colour. The engine passes in:

### Your own values

| Register | Contents |
|---|---|
| `b4` `GenesisFrame` | `float Time; float Frame; float2 Resolution;`: seconds since the game started, the frame number and the target size, in the game as in the Shader editor (placed Objects, model materials, `DrawModelShader3D`, `DrawMeshShader3D`). |
| `b5` `GenesisParameters` | Your parameters, up to 16 floats (four `float4` rows). Their names are what the Object editor shows and what the commands below set. |
| `t17`–`t20` | Free for your own textures (set in the Shader editor's Resources; an Object can override them). `t21`–`t23` are free except on terrain. They are bound the same way for a placed Object, a model material's shader and a script's `DrawModelShader3D`. |

A full pixel shader built on the engine's own forward shader, one that declares its
`cbuffer LocalShadowConstants : register(b4)` (as painted terrain's layer shaders do), is given the
engine's constants as its own draws have them instead: point and spot lamp shadows at b4, and
`GenesisFrame` at b3 as for a surface shader. Until 11 October 2026 painted terrain got `GenesisFrame`
at b4, so lamps lit it but never cast shadows on it.

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
reference panel lists the same inputs. A shader that needs the engine's lighting is simpler and
safer as a surface shader: the engine's constant buffers are its own and change with it.

## Per-instance values from scripts

Every instance can carry its own values for its shaders: every weapon its own camo, every window
its own glow. Values are set by the name a parameter (or texture) has in the shader and replace the
shader's and the Object's own for that instance only. They reach the Object's shader and the
shaders of its model's materials alike, so a weapon whose painted parts have a camo shader of their
own takes its paint from these commands.

| Command | What it does |
|---|---|
| `ShaderSet(shader)` | Draws this instance with a Shader resource (empty for none). |
| `ShaderSetParameter(name, value)` | Sets one of this instance's `float` parameters. |
| `ShaderSetVector(name, x, y, z, w)` | Sets a `float2`, `float3` or `float4` parameter (extra components are ignored). |
| `ShaderSetTexture(name, image)` | Gives one of this instance's shader textures another Image (empty gives it back its own). |
| `ModelSetMaterialTexture(material, slot, image) -> bool` | Gives one material of this instance's model another Image. `slot` is `albedo`, `normal`, `orm` or `emission` for the material's own maps, or the name of a texture that material's shader declares. Empty gives the slot back its own. False when the model has no such material. |
| `InstanceSetShader(id, shader)` | As `ShaderSet`, for another instance. |
| `InstanceSetShaderParameter(id, name, value)` | As `ShaderSetParameter`, for another instance. |
| `InstanceSetShaderVector(id, name, x, y, z, w)` | As `ShaderSetVector`, for another instance. |
| `InstanceSetShaderTexture(id, name, image)` | As `ShaderSetTexture`, for another instance. |
| `InstanceSetMaterialTexture(id, material, slot, image) -> bool` | As `ModelSetMaterialTexture`, for another instance. |
| `InstanceGetShaderParameter(id, name)` | A value set on an instance (its first component); 0 when none of that name is set. |

In a weapon's Create event, for example:

```pgsl
ShaderSetVector("CamoTint", 0.32, 0.41, 0.22, 1)
ShaderSetParameter("CamoScale", 4)
ModelSetMaterialTexture("Paint", "CamoPattern", "camo_tiger")
```

Drawing: draws of the same mesh with the same shader, values and images are still drawn together
(instanced); an instance with values or images of its own is drawn in a batch of its own, and only
it. Instances without any are batched exactly as before.

C#: the same commands are static methods on `PgslCommands`; `ModelInstance.SetMaterialTexture(world,
entity, material, slot, image)` (namespace `Genesis.Runtime.Modeling`) is the model one.

## Checks

`Build.bat --test mesh-surface` composes and compiles a surface shader for DX11, DX12, Vulkan and
OpenGL and checks on each that: an empty surface looks like the material without a shader; a
surface painting its parameters' colour is lit, shadowed and lit by a lamp exactly as the engine's
material of that colour (tile differences of 2.5 or less); turning the sun away darkens it and
its emission lights it in shade; a glass box shows what is behind it. It then places three
instances of a two-material model in a room: each wears its own paint through the material's own
shader, one has an Object shader on its other material, one swaps its pattern and its metal's
albedo, and another instance keeps its own; twelve boxes sharing values make one batch and an
instance with its own one more. The Model editor's descriptor keeps each material's shader.

`Build.bat --test shader-nodes` (and `shader-workspace`) checks the vertex stage
(`Editor.Shader.VertexStage.*`): a Vertex function reading every input and a parameter, the Wind Sway
and Wave presets and the Code page's template compile `VS`, `VS_Skinned`, `VS_Shadow`,
`VS_ShadowSkinned` and the pixel shader for DXBC and SPIR-V; the engine's vertex shaders with the
hooks taken out compile to the same bytes as with them; the node view's Vertex offset writes the
function and refuses a texture; and the Shader editor's preview of the Wave preset on a ball differs
from the same shader without its Vertex function (DX11, captures `shader-vertex-wave.png` and
`shader-vertex-none.png`). `shader-workspace` previews both presets on DX11, DX12, Vulkan, OpenGL and
Software with the other presets. Not yet checked in a running game, with instancing or on a skinned
model.
