# Ink outline

> **Being retired.** An outline is a game's look, so it belongs in the game's project as a Shader
> resource, not in the engine. Use the project shader in `Documentation/Examples/InkOutline.hlsl`
> as a [post effect](PostEffects.md) instead; this built-in outline will be removed once no game
> uses it.

An optional outline for illustrated and cel-shaded projects. It is **off by default**: with it off
the composite's three ink constants are zero and the shader block is skipped, so existing projects
and golden images are unchanged.

Added 30 September 2026 from the Golden Stag game session, not by the engine audit. It has been
run on Vulkan in the Player only; DX11, DX12, OpenGL and the headless suites have not been run
against it.

## Use

PGSL (any Create event; the setting is process-wide, like `Engine.Rendering.BloomEnabled`):

```pgsl
Engine.Rendering.InkOutlineEnabled(true);
Engine.Rendering.InkOutlineWidth(2);            // pixels at 1080p, scales with viewport height
Engine.Rendering.InkOutlineOpacity(0.9);
Engine.Rendering.InkOutlineColor(0.11, 0.065, 0.04);
Engine.Rendering.InkOutlineCreaseAngle(32);     // creases sharper than this are inked
Engine.Rendering.InkOutlineDepthStep(0.012);    // relative depth jump that counts as a silhouette
Engine.Rendering.InkOutlineDistance(6, 40, 0.6); // full width to 6 m, one pixel and 60% by 40 m
```

C#: `InkOutlineSettings.Configure(...)` and `InkOutlineSettings.SetColor(r, g, b)`.

## How it works

There is no normal buffer, so the lines come from scene depth alone. Device depth is affine across a
plane in screen space, so its second difference is zero on every flat surface at any viewing angle
and non-zero only where the surface steps (a silhouette) or bends (a crease).

- A silhouette is inked on the nearer surface only, so a line is one width rather than two.
- A crease is measured as the angle the surface turns through, with the surface's own slope divided
  out, so a floor seen at a grazing angle does not over-respond.
- The line is applied after tonemapping, in display space, so it is the same colour at any exposure.
- Draws that never write depth (particles, alpha-blended materials) are not outlined.
- Orthographic projections are skipped: the test needs 1 / view depth.

Curved surfaces are faceted in depth even when their normals are smooth. Keep facets shallower than
the crease angle (20 segments or more around a barrel) or they draw as faint lines.

## Files

| File | Change |
| --- | --- |
| `Source/Runtime/Genesis.Shared/Interfaces/InkOutlineSettings.cs` | New: the settings |
| `Source/Runtime/Genesis.Rendering/Primitives/ForwardRenderer.InkOutline.cs` | New: packs the constants |
| `Source/Runtime/Genesis.Runtime/Scripting/PgslCommands.InkOutline.cs` | New: `Engine.Rendering.InkOutline*` |
| `Source/Runtime/Genesis.Rendering/Primitives/FogPostShaders.cs` | `InkParams`, `InkColor`, `InkFade` appended to `FogPostConstants`; `InkOutline()`; one block before the final `return` of `PS` |
| `Source/Runtime/Genesis.Rendering/Primitives/ForwardRenderer.cs` | The same three fields appended to `FogPostCB`; one `PackInkOutline` call in `CompositePost` |

There is no anti-aliasing in the engine yet, so lines are stair-stepped at 1080p.
