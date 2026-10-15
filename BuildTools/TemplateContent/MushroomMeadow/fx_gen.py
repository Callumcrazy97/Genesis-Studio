"""Mushroom Meadow FX content generator (particles, shaders, physics, paths, UI, FX objects).

Re-runnable and deterministic. Run from anywhere:
    python BuildTools/TemplateContent/MushroomMeadow/fx_gen.py
Writes into Source/Genesis.Application.Core/Projects/Templates/Assets/TwoDShowcase/Assets.
"""
import hashlib
import json
import os

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
ASSETS = os.path.join(ROOT, "Source", "Genesis.Application.Core", "Projects", "Templates",
                      "Assets", "TwoDShowcase", "Assets")
STAMP = "2026-10-11T00:00:00Z"


def guid(name):
    return hashlib.md5(("mushroom-meadow-fx:" + name).encode("utf-8")).hexdigest()


def write_text(rel, text):
    path = os.path.join(ASSETS, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def write_json(rel, data):
    write_text(rel, json.dumps(data, indent=2) + "\n")


def write_meta(rel, kind, display):
    path = os.path.join(ASSETS, rel + ".meta")
    existing = None
    if os.path.exists(path):
        try:
            with open(path, encoding="utf-8") as handle:
                existing = json.load(handle).get("guid")
        except (OSError, ValueError):
            existing = None
    write_json(rel + ".meta", {
        "guid": existing or guid(rel),
        "kind": kind,
        "displayName": display,
        "createdUtc": STAMP,
        "modifiedUtc": STAMP,
    })


def colour(r, g, b, a=1.0):
    return {"r": r, "g": g, "b": b, "a": a}


# ---------------------------------------------------------------- particles
# 2D rooms convert effect units to pixels at 12 px per unit (Particle2DLayout); gravity is
# positive upwards, so falling bits use negative gravity.
PARTICLE_BASE = {
    "preview2D": True,
    "maxParticles": 64,
    "emitRate": 0.0,
    "burstCount": 12,
    "loop": False,
    "duration": 1.0,
    "shape": "Sphere",
    "spreadDegrees": 30.0,
    "emitRadius": 0.1,
    "speed": 6.0,
    "speedVariance": 0.35,
    "gravity": 0.0,
    "drag": 1.0,
    "lifetime": 0.5,
    "lifetimeVariance": 0.25,
    "startSize": 0.3,
    "endSize": 0.08,
    "emissive": 1.0,
    "blendMode": "Alpha",
    "pixelSampling": True,
    "startColor": colour(1, 1, 1, 1),
    "endColor": colour(1, 1, 1, 0),
}


def particle(name, notes, **overrides):
    data = dict(PARTICLE_BASE)
    data.update(overrides)
    data["effectName"] = name
    data["notes"] = notes
    rel = "Particles/" + name + ".particle.json"
    write_json(rel, data)
    write_meta(rel, "Particle", name)


def build_particles():
    particle("Coin Sparkle", "Spawn Effect kind 0. Gold twinkles when a coin is collected.",
             burstCount=10, maxParticles=24, shape="Sphere", speed=5.5, drag=3.0, gravity=0.6,
             lifetime=0.45, startSize=0.34, endSize=0.06, blendMode="Additive", emissive=1.4,
             startColor=colour(1.0, 0.95, 0.55, 1), midColor=colour(1.0, 0.82, 0.25, 1),
             endColor=colour(0.91, 0.6, 0.11, 0))
    particle("Stomp Dust", "Spawn Effect kind 1. Puff of dust and leaf bits when an enemy is stomped.",
             burstCount=12, maxParticles=24, shape="Disc", emitRadius=0.8, spreadDegrees=70, speed=4.0,
             drag=2.5, gravity=0.8, lifetime=0.4, startSize=0.5, endSize=0.15,
             startColor=colour(0.96, 0.9, 0.76, 0.95), endColor=colour(0.6, 0.45, 0.3, 0))
    particle("Landing Dust", "Spawn Effect kind 2. Small side-puffs at the feet after a fall.",
             burstCount=8, maxParticles=16, shape="Disc", emitRadius=0.6, spreadDegrees=85, speed=3.0,
             drag=3.0, gravity=0.3, lifetime=0.32, startSize=0.36, endSize=0.1,
             startColor=colour(0.93, 0.87, 0.72, 0.85), endColor=colour(0.66, 0.5, 0.33, 0))
    particle("Bounce Puff", "Spawn Effect kind 3. Cream spores and rings off a Bounce Mushroom.",
             burstCount=14, maxParticles=28, shape="Cone", spreadDegrees=55, speed=7.0, drag=2.2,
             gravity=-0.6, lifetime=0.55, startSize=0.42, endSize=0.1,
             startColor=colour(1.0, 0.96, 0.86, 1), midColor=colour(1.0, 0.7, 0.66, 0.9),
             endColor=colour(0.85, 0.26, 0.23, 0))
    particle("Finish Fireworks", "Spawn Effect kind 4 (played three times around the shrine).",
             burstCount=48, maxParticles=96, shape="Sphere", speed=13.0, speedVariance=0.25, drag=1.4,
             gravity=-2.5, lifetime=1.25, startSize=0.4, endSize=0.1, blendMode="Additive", emissive=1.6,
             startColor=colour(1.0, 0.97, 0.7, 1), midColor=colour(1.0, 0.55, 0.3, 1),
             endColor=colour(0.85, 0.2, 0.45, 0), colorJitter=0.25)
    particle("Hurt Burst", "Spawn Effect kind 5. Red-white stars when the Explorer is hurt.",
             burstCount=14, maxParticles=28, shape="Sphere", speed=8.0, drag=2.6, gravity=-0.5,
             lifetime=0.5, startSize=0.42, endSize=0.08, blendMode="Additive",
             startColor=colour(1.0, 1.0, 1.0, 1), midColor=colour(1.0, 0.4, 0.35, 1),
             endColor=colour(0.6, 0.08, 0.1, 0))
    particle("Block Debris", "Spawn Effect kind 6. Brick chunks that tumble and fall.",
             burstCount=10, maxParticles=20, shape="Cone", spreadDegrees=60, speed=10.0, drag=0.3,
             gravity=-9.0, lifetime=0.9, startSize=0.5, endSize=0.42, rotationSpeed=540,
             rotationVariance=0.6,
             startColor=colour(0.78, 0.42, 0.24, 1), endColor=colour(0.5, 0.25, 0.14, 0.0))
    particle("Drifting Petals", "Ambient, looping. Spawn Effect kind 7 places it once per room "
             "(SpawnParticleEmitter at the top of the room); petals drift across the whole level.",
             loop=True, burstCount=0, emitRate=7.0, maxParticles=140, shape="Box", boxSizeX=440.0,
             boxSizeY=0.5, boxSizeZ=0.0, speed=0.4, speedVariance=0.6, drag=0.6, gravity=-0.4,
             windX=1.3, turbulenceStrength=0.9, lifetime=13.0, lifetimeVariance=0.2,
             startSize=0.28, endSize=0.22, rotationSpeed=120, rotationVariance=1.0,
             startColor=colour(1.0, 0.8, 0.86, 0.95), midColor=colour(1.0, 0.95, 0.85, 0.9),
             endColor=colour(0.62, 0.88, 0.36, 0), colorJitter=0.2)


# ---------------------------------------------------------------- shaders
SPRITE_FLASH = """// Pixel Flash: a Sprite-pipeline shader for 2D Objects (ShaderSet in PGSL).
// Flash 0..1 whitens the sprite (hit/bounce feedback); Shimmer adds a soft travelling
// highlight (Buzz Bee wings). Pixel art stays crisp: only colour changes, never UVs.
// Taint 0..1 (Pip only, hidden lore): the cap's reds sink through maroon and bruised violet to
// near black, the cream spots turn a sickly pale green that glows (TaintPulse 0..1 sets how
// strongly the glow breathes; Explorer drives it as a slow throb) and the rest of the sprite loses a little colour. Taint 0 = unchanged.
Texture2D SpriteTex : register(t0);
SamplerState SpriteSamp : register(s0);
cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
cbuffer GenesisParameters : register(b5) { float Flash; float Shimmer; float ShimmerSpeed; float Warmth; float Taint; float TaintPulse; };
struct VSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; float4 Color : COLOR; float FogDepth : TEXCOORD1; };
float4 MainPS(VSOut IN) : SV_Target
{
    float4 tex = SpriteTex.Sample(SpriteSamp, IN.UV) * IN.Color;
    float3 rgb = tex.rgb;
    float band = saturate(1.0 - abs(frac(IN.UV.x * 0.8 - IN.UV.y * 0.4 - Time * max(ShimmerSpeed, 0.01)) - 0.5) * 6.0);
    rgb += band * saturate(Shimmer) * 0.35;
    rgb = lerp(rgb, rgb * float3(1.06, 1.0, 0.9), saturate(Warmth));
    if (Taint > 0.001)
    {
        float tt = saturate(Taint);
        float luma = dot(rgb, float3(0.299, 0.587, 0.114));
        float redness = smoothstep(0.38, 0.5, 1.0 - max(rgb.g, rgb.b) / max(rgb.r, 0.001));
        float pale = smoothstep(0.70, 0.73, rgb.b / max(rgb.r, 0.001)) * smoothstep(0.78, 0.86, luma);
        float tone = saturate((luma - 0.1) / 0.5);
        float3 maroon = lerp(float3(0.17, 0.03, 0.07), float3(0.58, 0.12, 0.16), tone);
        float3 violet = lerp(float3(0.13, 0.05, 0.21), float3(0.47, 0.22, 0.58), tone);
        float3 ember = lerp(float3(0.025, 0.015, 0.045), float3(0.15, 0.1, 0.19), tone);
        float3 capTint = lerp(maroon, violet, saturate((tt - 0.4) / 0.35));
        capTint = lerp(capTint, ember, saturate((tt - 0.75) / 0.25));
        float3 aged = rgb;
        aged = lerp(aged, luma * float3(0.96, 1.0, 0.92), 0.3 * tt);
        aged = lerp(aged, capTint, saturate(tt * 2.5) * redness);
        float glow = 0.7 + 0.5 * saturate(TaintPulse);
        float3 sick = float3(0.6, 0.96, 0.5) * glow;
        float spotAmount = saturate(tt * 2.0) * pale;
        aged = lerp(aged, sick, spotAmount);
        aged += float3(0.1, 0.32, 0.08) * spotAmount * glow * (0.25 + 0.75 * saturate(TaintPulse)) * 0.6;
        rgb = aged;
    }
    rgb = lerp(rgb, float3(1.0, 0.98, 0.92), saturate(Flash));
    return float4(saturate(rgb), tex.a);
}
"""

SNES_LOOK = """// SNES Look: a Fullscreen post effect for Mushroom Meadow.
// Warm colour grade, gentle vignette and an optional CRT scanline mask (Scanlines 0 or 1,
// toggled in game with the C key through PostEffectSetParameter).
Texture2D SceneColor : register(t0);
SamplerState Linear : register(s0);
cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
cbuffer GenesisParameters : register(b5) { float Warmth; float Saturation; float Vignette; float Scanlines; };
struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };
float4 MainPS(PreviewVSOut IN) : SV_Target
{
    float4 c = SceneColor.Sample(Linear, IN.UV);
    float3 rgb = c.rgb;
    float luma = dot(rgb, float3(0.299, 0.587, 0.114));
    rgb = lerp(luma.xxx, rgb, max(Saturation, 0.0));
    rgb *= lerp(float3(1.0, 1.0, 1.0), float3(1.07, 1.02, 0.9), saturate(Warmth));
    rgb = lerp(rgb, rgb * rgb * (3.0 - 2.0 * rgb), 0.18);
    float2 p = IN.UV - 0.5;
    float v = saturate(1.0 - dot(p, p) * 1.6 * saturate(Vignette));
    rgb *= lerp(0.72, 1.0, v);
    float rows = max(Resolution.y, 1.0) / 3.0;
    float scan = 0.5 + 0.5 * cos(IN.UV.y * rows * 6.28318);
    rgb *= lerp(1.0, lerp(0.78, 1.04, scan), saturate(Scanlines));
    return float4(saturate(rgb), c.a);
}
"""


def param(name, value):
    return {"name": name, "type": "float", "value": [value]}


def build_shaders():
    rel = "Shaders/Pixel Flash.shader.json"
    write_json(rel, {
        "schemaVersion": 7,
        "pipeline": "Sprite",
        "authoringMode": "Code",
        "targetType": "Image",
        "preset": "",
        "targetComponent": "",
        "entry": "MainPS",
        "profile": "ps_5_0",
        "source": SPRITE_FLASH,
        "previewAsset": "Assets/Sprites/Buzz Bee Sprite.image.json",
        "parameters": [param("Flash", 0.0), param("Shimmer", 0.0), param("ShimmerSpeed", 0.8),
                       param("Warmth", 0.0), param("Taint", 0.0), param("TaintPulse", 0.0)],
    })
    write_meta(rel, "Shader", "Pixel Flash")
    rel = "Shaders/SNES Look.shader.json"
    write_json(rel, {
        "schemaVersion": 7,
        "pipeline": "Fullscreen",
        "authoringMode": "Code",
        "targetType": "Fullscreen",
        "preset": "",
        "targetComponent": "",
        "entry": "MainPS",
        "profile": "ps_5_0",
        "source": SNES_LOOK,
        "previewAsset": "",
        "parameters": [param("Warmth", 0.55), param("Saturation", 1.12), param("Vignette", 0.6),
                       param("Scanlines", 0.0)],
    })
    write_meta(rel, "Shader", "SNES Look")


# ---------------------------------------------------------------- physics
def build_physics():
    materials = {
        "Mushroom Cap": dict(friction=0.6, restitution=0.95, density=0.6, gravityScale=1.0,
                             notes="Springy cap of the Bounce Mushroom (nearly perfectly elastic). The "
                                   "tile-based Explorer does not run on rigid bodies, so its bounce "
                                   "(vy = -760) is scripted; this material documents the intent."),
        "Meadow Turf": dict(friction=0.8, restitution=0.0, density=1.2, gravityScale=1.0,
                            notes="Grass and dirt: grippy, no bounce."),
    }
    for name, values in materials.items():
        data = {"schemaVersion": 1, "Dimension": "TwoD"}
        data.update(values)
        rel = "Physics/" + name + ".physics.json"
        write_json(rel, data)
        write_meta(rel, "Physics", name)


# ---------------------------------------------------------------- paths
# Waypoints are room coordinates (PathFollow moves the agent to them). Buzz Bee picks the
# route whose first waypoint is nearest to where it was placed (see Buzz Bee/Create.pgsl).
def route(name, loop_mode, speed, waypoints, wait=0.0, curve=False):
    rel = "Paths/" + name + ".pathing"
    write_json(rel, {
        "schemaVersion": 1,
        "name": name,
        "dimension": "TwoD",
        "targetRoom": "Assets/Rooms/Mushroom Meadow.room.json",
        "targetObject": "Assets/Objects/Buzz Bee.object.json",
        "previewAgentCount": 1,
        "route": {
            "mode": "WaypointPatrol",
            "loopMode": loop_mode,
            "speed": speed,
            "stoppingDistance": 2.0,
            "defaultWaitSeconds": wait,
            "wanderRadius": 8.0,
            "followOffset": 2.0,
            "followTarget": "",
            "animationState": "",
            "speedCurve": [{"time": 0.0, "multiplier": 1.0}, {"time": 12.0, "multiplier": 1.0}],
            "waypoints": [
                {"name": wp[0], "x": float(wp[1]), "y": float(wp[2]), "z": 0.0,
                 "waitSeconds": float(wp[3]) if len(wp) > 3 else 0.0, "curve": curve}
                for wp in waypoints
            ],
        },
    })
    write_meta(rel, "Pathing", name)


BEE_ROUTES = {
    "Bee Patrol Meadow": (5700, 112),
    "Bee Patrol Hover": (3200, 60),
    "Bee Patrol Loop": (6080, 112),
}


def build_paths():
    route("Bee Patrol Meadow", "PingPong", 72.0,
          [("West", 5700, 112, 0.35), ("East", 5960, 112, 0.35)])
    route("Bee Patrol Hover", "PingPong", 54.0,
          [("High", 3200, 60, 0.5), ("Mid", 3232, 100), ("Low", 3200, 140, 0.5)], curve=True)
    # A figure-eight around (6160, 112).
    loop = [("A", 6080, 112), ("B", 6120, 82), ("C", 6160, 112), ("D", 6200, 142),
            ("E", 6240, 112), ("F", 6200, 82), ("G", 6160, 112), ("H", 6120, 142)]
    route("Bee Patrol Loop", "Loop", 80.0, loop, curve=True)


# ---------------------------------------------------------------- user interfaces
def element(eid, etype, x, y, w, h, order, **extra):
    data = {"id": eid, "parentId": "", "type": etype, "anchor": "TopLeft", "x": float(x), "y": float(y),
            "width": float(w), "height": float(h), "order": order, "visible": True, "text": "",
            "font": "Consolas", "fontSize": 18.0, "image": "", "background": "#00000000",
            "foreground": "#FFFFFFFF", "accent": "#FFFFD23F"}
    data.update(extra)
    return data


def build_ui():
    hud = [
        element("hudBar", "Panel", 20, 14, 1240, 80, 0, background="#F01D3043", fill="VerticalGradient",
                gradientEnd="#F0142232", cornerRadius=10.0, borderWidth=3.0, borderColor="#FFFFE7A0"),
        element("hudSheen", "Panel", 26, 18, 1228, 6, 1, background="#30FFFFFF", cornerRadius=3.0,
                borderWidth=0.0),
        element("coinIcon", "Image", 310, 58, 20, 22, 2, image="Assets/Sprites/Coin Icon.image.json",
                imageFit="Contain", borderWidth=0.0),
        element("heartIcon", "Image", 576, 59, 20, 20, 3, image="Assets/Sprites/Heart Icon.image.json",
                imageFit="Contain", borderWidth=0.0),
        element("bigCoinLabel", "Text", 900, 22, 140, 26, 4, text="SUN MEDALS", fontSize=15.0,
                foreground="#FFFFE7A0"),
    ]
    for i in range(3):
        hud.append(element("pip%d" % (i + 1), "Panel", 1040 + i * 30, 24, 22, 22, 5 + i,
                           background="#FF2A1D2E", cornerRadius=11.0, borderWidth=2.0,
                           borderColor="#FFE89A1B"))
        hud.append(element("pipFill%d" % (i + 1), "Panel", 1044 + i * 30, 28, 14, 14, 8 + i,
                           background="#FFFFD23F", fill="VerticalGradient", gradientEnd="#FFE89A1B",
                           cornerRadius=7.0, borderWidth=0.0))
    hud += [
        element("helpBar", "Panel", 20, 668, 1240, 38, 20, background="#D61D3043", cornerRadius=8.0,
                borderWidth=2.0, borderColor="#80FFE7A0"),
        element("helpText", "Text", 36, 672, 1210, 30, 21, fontSize=17.0, foreground="#FFF0F1DB",
                text="A/D MOVE   SPACE JUMP   SHIFT RUN   P/ESC PAUSE   R RESTART   M MUTE   C CRT"),
    ]
    write_json("User Interfaces/Meadow HUD.ui.json",
               {"schemaVersion": 1, "designWidth": 1280, "designHeight": 720, "elements": hud})
    write_meta("User Interfaces/Meadow HUD.ui.json", "UserInterface", "Meadow HUD")

    panels = [
        # The shade stops below the HUD bar so score, coins, lives and time stay readable on menus.
        element("shade", "Panel", 0, 100, 1280, 620, 0, background="#661A1020", borderWidth=0.0),
        element("frame", "Panel", 176, 196, 928, 384, 1, background="#F0203550", fill="VerticalGradient",
                gradientEnd="#F0122133", cornerRadius=14.0, borderWidth=4.0, borderColor="#FFFFD23F"),
        element("frameInner", "Panel", 188, 208, 904, 360, 2, background="#00000000", cornerRadius=10.0,
                borderWidth=2.0, borderColor="#80FFF1C8"),
        element("logo", "Image", 480, 92, 320, 96, 3, image="Assets/Sprites/Title Logo.image.json",
                imageFit="Contain", borderWidth=0.0),
        element("ribbon", "Panel", 440, 182, 400, 34, 4, background="#FFD8433B", fill="VerticalGradient",
                gradientEnd="#FFA52D2B", cornerRadius=6.0, borderWidth=2.0, borderColor="#FF2A1D2E"),
        element("ribbonText", "Text", 440, 182, 400, 34, 5, fontSize=20.0, foreground="#FFFFF4D6",
                textAlign="Center", textVerticalAlign="Middle", letterSpacing=2.0, text="STAGE 1"),
    ]
    write_json("User Interfaces/Meadow Panels.ui.json",
               {"schemaVersion": 1, "designWidth": 1280, "designHeight": 720, "elements": panels})
    write_meta("User Interfaces/Meadow Panels.ui.json", "UserInterface", "Meadow Panels")


# ---------------------------------------------------------------- objects
def object_json(sprite, script_class, events):
    return {
        "schemaVersion": 2,
        "dimension": "TwoD",
        "sprite": sprite,
        "model": "",
        "components": [
            {"type": "ScriptComponent", "enabled": True, "props": {"ScriptClass": script_class}}
        ],
        "events": events,
    }


def bee_create():
    lines = [
        "// Buzz Bee follows a Pathing resource (Assets/Paths/Bee Patrol *.pathing) with PathFollow.",
        "// It takes the route whose first waypoint is nearest to where the room placed it; a bee",
        "// placed far from every route hovers on the spot instead. Explorer stomps it (+300).",
        "AnimationPlay(\"Fly\", true); AnimationSetSpeed(0);",
        "homeX = x; homeY = y; lastX = x; hover = 0; following = 0;",
        "route = \"\"; var best = 400;",
    ]
    for name, (rx, ry) in BEE_ROUTES.items():
        lines.append("var d%s = Abs(x - %d) + Abs(y - %d);" % (name.split()[-1], rx, ry))
        lines.append("if (d%s < best) { best = d%s; route = \"Assets/Paths/%s.pathing\"; }"
                     % (name.split()[-1], name.split()[-1], name))
    lines += [
        "ShaderSet(\"Assets/Shaders/Pixel Flash.shader.json\");",
        "ShaderSetParameter(\"Shimmer\", 0.6); ShaderSetParameter(\"ShimmerSpeed\", 1.4);",
    ]
    return "\n".join(lines) + "\n"


BEE_STEP = """if (RoomValueGet("frozen", 1) == 1) {
    if (following == 1) { PathStop(); following = 0; }
    AnimationSetSpeed(0); return;
}
var dt = Clamp(DeltaTime, 0, 0.05);
AnimationSetSpeed(1);
if (route != "") {
    if (following == 0) { PathFollow(route, ""); following = 1; }
} else {
    hover = (hover + dt * 140) % 360;
    x = homeX + Sin(hover) * 24; y = homeY + Sin(hover * 2) * 8;
}
if (x < lastX - 0.05) { image_xscale = -1; }
if (x > lastX + 0.05) { image_xscale = 1; }
lastX = x;
"""

MUSHROOM_CREATE = """// Bounce Mushroom: Explorer bounces off the cap when it lands on it from above
// (Physics material "Mushroom Cap" documents the springy cap).
// Signal: Explorer sets RoomValueSet(StringJoin2("bounce_", StringOf(id)), 1) on the
// mushroom it bounced on; this object plays its squash and clears the value.
AnimationPlay("Idle", true); AnimationSetSpeed(0);
squash = 0; signal = StringJoin2("bounce_", StringOf(id));
RoomValueSet(signal, 0);
ShaderSet("Assets/Shaders/Pixel Flash.shader.json"); ShaderSetParameter("Flash", 0);
"""

MUSHROOM_STEP = """if (RoomValueGet(signal, 0) == 1) {
    RoomValueSet(signal, 0);
    squash = 0.22;
    AnimationPlay("Bounce", false); AnimationSetSpeed(0);
}
if (squash > 0) {
    squash = Max(0, squash - Clamp(DeltaTime, 0, 0.05));
    ShaderSetParameter("Flash", squash * 2.5);
    if (squash <= 0) { AnimationPlay("Idle", true); AnimationSetSpeed(0); ShaderSetParameter("Flash", 0); }
}
"""

SPAWN_EFFECT = """// Spawn Effect: ScriptExecute("Spawn Effect", kind, x, y)
// 0 coin sparkle, 1 stomp dust, 2 landing dust, 3 bounce puff, 4 finish fireworks,
// 5 hurt burst, 6 block debris, 7 ambient drifting petals (looping emitter; call once per room).
// Returns the emitter/burst id, or -1 for an unknown kind.
var kind = argument0; var px = argument1; var py = argument2;
if (kind == 0) { return ParticleBurstAt("Assets/Particles/Coin Sparkle.particle.json", px, py, 0, 1, 0); }
if (kind == 1) { return ParticleBurstAt("Assets/Particles/Stomp Dust.particle.json", px, py, 0, 1, 0); }
if (kind == 2) { return ParticleBurstAt("Assets/Particles/Landing Dust.particle.json", px, py, 0, 1, 0); }
if (kind == 3) { return ParticleBurstAt("Assets/Particles/Bounce Puff.particle.json", px, py, 0, 1, 0); }
if (kind == 4) {
    ParticleBurstAt("Assets/Particles/Finish Fireworks.particle.json", px - 70, py - 120, 0, 1, 0);
    ParticleBurstAt("Assets/Particles/Finish Fireworks.particle.json", px + 80, py - 150, 0, 0.8, 0);
    return ParticleBurstAt("Assets/Particles/Finish Fireworks.particle.json", px, py - 190, 0, 1.2, 0);
}
if (kind == 5) { return ParticleBurstAt("Assets/Particles/Hurt Burst.particle.json", px, py, 0, 1, 0); }
if (kind == 6) { return ParticleBurstAt("Assets/Particles/Block Debris.particle.json", px, py, 0, 1, 0); }
if (kind == 7) { return SpawnParticleEmitter("Assets/Particles/Drifting Petals.particle.json", px, py, 0, 1); }
return -1;
"""


def build_objects():
    # Buzz Bee's object and events are written by hero_gen.py since phase 2 (enemy convention);
    # bee_create/BEE_STEP above are kept only as the phase-1 reference.
    rel = "Objects/Bounce Mushroom.object.json"
    write_json(rel, object_json("Assets/Sprites/Bounce Mushroom Sprite.image.json", "Bounce Mushroom",
                                ["Create", "Step"]))
    write_meta(rel, "GameObject", "Bounce Mushroom")
    write_text("Objects/Bounce Mushroom/Create.pgsl", MUSHROOM_CREATE)
    write_text("Objects/Bounce Mushroom/Step.pgsl", MUSHROOM_STEP)

    write_text("Scripts/Spawn Effect.pgsl", SPAWN_EFFECT)
    write_meta("Scripts/Spawn Effect.pgsl", "Script", "Spawn Effect")


def main():
    build_particles()
    build_shaders()
    build_physics()
    build_paths()
    build_ui()
    build_objects()
    print("FX content written to", ASSETS)


if __name__ == "__main__":
    main()
