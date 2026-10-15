# Side bands / offset art on the Main Menu when the window is not 16:9

Status: fixed in the template (frontend_gen.py `room()` no longer gives Intro, Main Menu or Credits a viewport). No engine patch is
required for Mushroom Meadow, so there is no .diff. This note is for the engine team: the behaviour below is by design but easy to trip over.

## Cause
- `RoomDisplayLayout.Port` (Source\Runtime\Genesis.Runtime\Scene\RoomDisplayLayout.cs:27-41) with `scaleViewportsWithWindow` fits the authored
  window (1280x720) into the real buffer at ONE uniform scale and CENTRES it. Any other aspect leaves bands (pillarbox/letterbox) holding only the clear colour
  (Main Menu: [0.08, 0.06, 0.14] = the flat dark purple).
- `RoomRenderSubsystem.RenderViewports2D` (RoomRenderSubsystem.cs:84-122) draws backgrounds/tiles/objects only inside that port (ViewportSpriteSink clips to it).
- DrawGui/DrawUi are NOT in the port: `WindowGetWidth/Height` = swap-chain size (PgslCommands.Tilemap.cs:13-16) and `DrawUi` scales the 1280x720 design
  non-uniformly to the whole buffer (PgslCommands.Ui.cs:41-59, UiTargetSize 102-108). The template's DrawGui code also uses sx = sw/1280, sy = sh/720.
- So in a 1920x~1000 maximised window: art is 1.39x and centred with ~70 px side bands, the menu is stretched 1.5x x 1.39x over the whole window => offset gap on the
  left, art shifted/cropped relative to the menu, help bar (bottom, UI-drawn) not aligned with the art.
- It is not a first-frame race: Port and WindowGetWidth both read renderer.PixelWidth on the same frame. It looks "first start only" because the Windowed/Borderless
  mode from launch settings applies in SilkGameWindow.Load (ApplyMode, SilkGameWindow.cs:563-614, called before OnLoad creates the swap chain), and a window the
  player later resizes back to 16:9 has no bands. The window title says "Intro" because the title is the first room's name (ProjectPlayerApp.cs:311-313).

## Reproduce deterministically (for a regression check)
Render the Main Menu with the template before the fix at buffer sizes: 1280x720 (no bands), 1920x1000 (side bands about 71 px), 1920x1200 (top/bottom bands 60 px),
2560x1080 (large side bands), 1000x720. Assert that the pixel at (2, h/2) equals the backdrop colour (not the clear colour [0.08,0.06,0.14])
and that the UI element rect from `UiHitTest` lies over the same part of the backdrop at every size. Sequence for a live test: start windowed 1280x720, then
`WindowSetSize(1920,1000)` (or maximise), then `WindowSetMode("borderless")` on a 16:10 monitor, then back to windowed, screenshotting after each.

## Optional engine improvement (not applied, not needed now)
Give DrawGui/DrawUi the same letterbox as the world when `scaleViewportsWithWindow` is on (a GUI origin offset plus a uniform scale taken from
`RoomDisplayLayout.Port` of the authored window), or document that GUI is always full-window. A room that mixes a viewport with DrawGui code
will otherwise misalign on any non-16:9 window. Gameplay rooms (levels) still use a letterboxed viewport with a full-window HUD; check them the same way
on a 16:10 or ultrawide window (HUD edges sit on the bands; the world does not fill them).
