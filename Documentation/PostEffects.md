# Post effects

A post effect is one of the project's own **Fullscreen Shader resources**, run over the finished
frame: an ink outline, a colour grade, a vignette, a screen tint for being hurt. The look lives in
the project; the engine only runs the shaders and hands them their inputs. Effects run in order,
each reading what the one before drew; none run unless a room or a script asks for them.

Added 4 October 2026. Before it, a Fullscreen shader could only be previewed in the Shader editor,
which is why an ink outline had been built into the engine's composite for one game. That built-in
outline (`InkOutlineSettings`, `Engine.Rendering.InkOutline*`) has been removed; the same outline is
now a project shader (below).

## Turning effects on

In a room's environment (its `.room.json`):

```json
"environment": { "postEffects": ["Ink Outline", "Warm Grade"] }
```

From PGSL, while the room runs:

| Command | Meaning |
|---|---|
| `PostEffectAdd(shader)` | Run a Fullscreen Shader resource after the effects already running. |
| `PostEffectRemove(shader)` | Stop one. |
| `PostEffectClear()` | Stop all of them. |
| `PostEffectSetParameter(shader, parameter, value)` | Set one of its parameters by the name it has in `GenesisParameters`. |

From C# (namespace `Genesis.Runtime.Rendering`): `ProjectPostEffects.Add`, `Remove`, `Clear`,
`SetRoomEffects(names)` and `SetParameter(shader, parameter, params float[] values)`, which also
sets vector parameters (`SetParameter("Ink Outline", "InkColor", 0.11f, 0.065f, 0.04f)`).

A room's list replaces the running effects when the room starts. A shader that does not compile is
left out (`IRenderController.LastPostEffectError` says why) and the game keeps running.

Effects run after the engine's own anti-aliasing and camera motion blur (`RenderSetAntiAliasing`,
`RenderSetMotionBlur`; see [Game features](GameFeatures.md#anti-aliasing-quality-tiers-and-motion-blur))
and before the GUI: `SceneColor` is the smoothed picture, and an effect's own lines, grain or
sharpening are left as it draws them.

## Writing one

Make a Shader resource, choose the **Full screen** target and write code. The entry point is
`MainPS`; the vertex stage is the engine's full-screen triangle, so declare its output:

```hlsl
struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };
```

| Input | Register | Contents |
|---|---|---|
| `SceneColor` | `t0` | The frame so far, in display space (after tonemapping). |
| `SceneDepth` | `t1` | Scene depth, `Texture2D<float>`. |
| `SceneFlags` | `t2` | Per pixel, `r`: 1 or 0.875 fogged in the forward pass, 0.5 or 0.375 engine-lit, 0 for authored shaders and the sky. **0.875 and 0.375 mark foliage** (grass, flowers, anything drawn as foliage). `gba`: the pixel's ambient light before fog. |
| `ObjectMarks` | `t3` | `Texture2D<float4>`. Where an instance marked with `InstanceSetOutline` covers the pixel (the nearest, where several do): `rgb` its outline colour, `a` = (width + 1) / 63, so any mark has `a` above 0. 0 elsewhere, and everywhere in a frame without marks. |
| `ObjectIds` | `t4` | `Texture2D<float>`. The marked instance's id there (its instance id); **negative where that instance is hidden behind something** (marks shown through walls only), 0 where nothing is marked. |
| sampler | `s0` | Linear, clamped. |
| `GenesisFrame` | `b4` | `float Time; float Frame; float2 Resolution;` as in the Shader editor. |
| `GenesisParameters` | `b5` | Your parameters (up to 16 floats), set in the Shader editor and by scripts. |
| `GenesisCamera` | `b6` | `float4 Clip` (near, far, 1 when depth is reversed, tan of half the vertical field of view); `float4 View` (aspect, 1 for a perspective view, radians per pixel, unused); `float4x4 InvViewProjection`; `float4 CameraPosition`. |

Return the new colour of the pixel. View depth from `SceneDepth` (`d`):

```hlsl
float viewZ = Clip.z > 0.5
    ? near * far / (near + d * (far - near))   // reversed depth
    : near * far / (far - d * (far - near));   // standard depth
```

## Example: teammates through walls

The engine draws an outline itself (`InstanceSetOutline(id, r, g, b, width, throughWalls)`, see
[Game features](GameFeatures.md#outlines-and-the-object-images)). A post effect can add to it from
the same marks, for example a faint fill over the parts of marked instances that are hidden:

```hlsl
Texture2D SceneColor : register(t0);
Texture2D<float4> ObjectMarks : register(t3);
Texture2D<float> ObjectIds : register(t4);
cbuffer GenesisParameters : register(b5) { float FillOpacity; };
struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };

float4 MainPS(PreviewVSOut IN) : SV_Target
{
    int3 p = int3(int2(IN.SvPos.xy), 0);
    float4 colour = SceneColor.Load(p);
    float4 mark = ObjectMarks.Load(p);
    if (ObjectIds.Load(p) < 0.0)                     // a marked instance, behind something
        colour.rgb = lerp(colour.rgb, mark.rgb, FillOpacity);
    return colour;
}
```

Use width 0 in `InstanceSetOutline` to mark instances for an effect like this without the engine's
line.

## Example: the ink outline

`Documentation/Examples/InkOutline.hlsl` is a complete ink outline for illustrated and cel-shaded
games: lines on silhouettes and creases from depth alone, thinner and fainter with distance, gone
past a fade-out distance, and gone from foliage sooner, so a distant meadow is not inked into a
dark speckle. Its parameters (`Opacity`, `WidthPixels`, `DepthStep`, `CreaseDegrees`, `InkColor`,
`FullWidthDistance`, `FarDistance`, `FarOpacity`, `FadeOutDistance`, `FoliageDistance`) are the
ones the removed built-in outline had, plus the last two. Copy it into a project as a Shader
resource (Full screen target, code) and list it in a room's `postEffects`.

`Build.bat --test post-effects` runs a project shader that inverts the frame (and takes it off
again through its parameter), and this ink outline over a 257 m meadow: near grass outlined, under
15% of the distant meadow's pixels inked (46% with no level of detail).
