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

## Shader packs

A game that offers shader packs keeps each as a Fullscreen Shader resource in a folder, lists them
with `ResourceList("Shaders/Packs", "Fullscreen shader")` and turns one on with `PostEffectAdd`. For
packs that are not resources (a player drops a `.hlsl` file into the game's folder), and for packs
that need pictures of their own (10 October 2026):

| Command | Meaning |
|---|---|
| `PostEffectAddFile(path)` | Run a Full screen shader written as a plain `.hlsl` (or `.fx`) file of the game's folder, named from the project folder or from its Assets folder (`"ShaderPacks/Sepia.hlsl"`). The effect's name is the path given, for the other commands. It is compiled on a worker like any effect, and again when the file changes (it is looked at twice a second). A file outside the game's folder is not run. False when there is no such file; it is still added, and runs once the file is there. |
| `PostEffectSetTexture(shader, slot, image)` | Give a running effect a picture: `image` an Image resource's name or a texture made with `TextureCreate` (`""` takes it away), `slot` the texture's register, 3 to 15, or its name in the shader. Replaces what the Shader editor bound there. False when the effect is not running or the slot is not one it may be given. |
| `PostEffectIsRunning(shader)` | True once the effect is compiled and drawn; false while it is being compiled, after it failed, or when it was never added. |
| `PostEffectError(shader)` | Why that effect does not run: the compiler's message, or the file or resource that was not found. Empty when it runs or is being compiled. |
| `PostEffectLastError()` | The newest such reason of any effect still asked for, as `"effect: reason"`; empty once every one runs. |

The textures a Fullscreen Shader resource declares at `t3` and above and binds to Images in the
Shader editor are given to it when it runs as a post effect, as they are to Mesh and Sprite shaders
(before, a post effect only had `t0` to `t2`). A texture register the shader declares and nothing
binds reads white. Samplers beyond `s0`, for an effect that declares them: `s1` point and clamped,
`s2` point and repeating, `s3` smooth and repeating. An effect has up to four pictures of its own.

```hlsl
Texture2D SceneColor : register(t0);
Texture2D Paper : register(t3);          // PostEffectSetTexture(pack, "Paper", "Paper Grain")
SamplerState Linear : register(s0);
SamplerState Repeat : register(s3);
struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };
float4 MainPS(PreviewVSOut IN) : SV_Target
{
    float4 c = SceneColor.Sample(Linear, IN.UV);
    return float4(c.rgb * Paper.Sample(Repeat, IN.UV * 4).rgb, c.a);
}
```

A pack menu that says when a pack is on, or why it is not (the compile takes a moment, so it
looks each frame):

```
// When the pack is chosen
pack = "ShaderPacks/" + name + ".hlsl";
if (!PostEffectAddFile(pack)) { message = PostEffectLastError(); }

// In Step
if (PostEffectIsRunning(pack)) { message = name + " is on"; }
else if (PostEffectError(pack) != "") { message = PostEffectError(pack); }
```

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
