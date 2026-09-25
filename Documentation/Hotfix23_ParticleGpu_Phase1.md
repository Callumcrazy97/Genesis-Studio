# Hotfix 23 — Particle GPU Completion, Phase 1

## Scope

Phase 1 deliberately establishes the particle execution contract before moving simulation state.
It does **not** claim that H22's CPU simulator has already been replaced by compute simulation.

The rule is now explicit and shared by Studio and Player:

- Direct3D 11 / Direct3D 12 / Vulkan / OpenGL with compute + indirect-draw capability => **GPU target**.
- Software renderer => **CPU target**.
- A hardware backend without the required GPU capability => **unsupported**. Genesis must not silently
  run CPU particles behind the user's back.

The active Particle Editor status bar reports the renderer name and this target. Runtime object
composition exposes the same decision for diagnostics and Phase 2 integration.

## Why this is a separate phase

The H22 particle implementation still owns particle state on the CPU. Switching that state to GPU
affects lifetime/spawn state, collision/event handling, render submission, editor seeking, diagnostics,
and resource lifetime. Landing the routing contract first gives a small native test boundary and makes
an accidental CPU fallback visible before the simulation switch.

## Acceptance test

1. Build and confirm Studio source revision H23.
2. Open a Particle resource and let the preview render at least one frame.
3. On Direct3D 11, Direct3D 12, Vulkan and OpenGL, the Particle Editor status must show:
   `<backend>: GPU target`.
4. Explicitly select Software renderer. The status must show:
   `Software: CPU target (Software backend)`.
5. No hardware backend should ever show `CPU target`.
6. Run:
   `.\Build.bat --test particle-workbench`
   and verify the two H23 execution-policy cases pass.

Phase 2 is the actual GPU simulation/render-state migration. Phase 3 is the subsequent Physics Editor
refactor after the completed Particle Editor is accepted.
