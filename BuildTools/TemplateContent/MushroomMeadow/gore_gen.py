"""Mushroom Meadow ENEMIES content (round 4): the Gore Kit and the Fear Kit.

Re-runnable and deterministic. Run from anywhere:
    python BuildTools/TemplateContent/MushroomMeadow/gore_gen.py
Writes into Source/Genesis.Application.Core/Projects/Templates/Assets/TwoDShowcase/Assets:

  Scripts    Gore Kit (library: GoreOn, GoreRgb, GoreFx, GoreEnemyInit, GoreEnemyHit, GoreEnemyDeath,
             GoreBlood, GoreSlice, GoreGib, GoreStain, GoreSpore, GoreCorpseHit, GoreFloorY ...),
             Fear Kit (library: FearCheck, FearDir, FearCower)
  Objects    Gore Half (the top or bottom half of a sliced enemy), Gore Chunk (bouncing bits),
             Blood Stain (flat floor decal; at most about 60 alive, fades over about 40 s)
  Particles  Blood Spray, Blood Mist, Gore Chunks, Blood Drip, Slice Spray in six blood colours
             (red, " Sap" green, " Yellow", " Ichor" violet, " Goo" teal, " Amber"), and Pip Spore Cloud.

The enemy scripts (hero_gen.py, fx_gen.py is untouched, levels_a_gen.py, levels_b_gen.py) call these kits.
Everything here draws in Draw GUI (room point -> GUI pixel as Hub Common's HubGuiX does) so it never
depends on world-space draw ordering. The `gore` global (0 = tidy poof only) is honoured by every blood
function; the Fear Kit does not depend on it.
"""
import hashlib
import json
import os

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
ASSETS = os.path.join(ROOT, "Source", "Genesis.Application.Core", "Projects", "Templates",
                      "Assets", "TwoDShowcase", "Assets")
STAMP = "2026-10-15T00:00:00Z"


def guid(name):
    return hashlib.md5(("mushroom-meadow-gore:" + name).encode("utf-8")).hexdigest()


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
    write_json(rel + ".meta", {"guid": existing or guid(rel), "kind": kind, "displayName": display,
                               "createdUtc": STAMP, "modifiedUtc": STAMP})


def write_script(name, code):
    rel = "Scripts/%s.pgsl" % name
    write_text(rel, code)
    write_meta(rel, "Script", name)


def write_object(name, events):
    rel = "Objects/%s.object.json" % name
    write_json(rel, {
        "schemaVersion": 2, "dimension": "TwoD", "sprite": "", "model": "",
        "components": [{"type": "ScriptComponent", "enabled": True, "props": {"ScriptClass": name}}],
        "events": [event for event, _ in events],
    })
    write_meta(rel, "GameObject", name)
    folder = os.path.join(ASSETS, "Objects", name)
    os.makedirs(folder, exist_ok=True)
    keep = set()
    for event, source in events:
        write_text("Objects/%s/%s.pgsl" % (name, event), source.strip("\n") + "\n")
        keep.add(event + ".pgsl")
    for stale in os.listdir(folder):
        if stale.endswith(".pgsl") and stale not in keep:
            os.remove(os.path.join(folder, stale))


# ====================================================================== particles
# Particle2DBurst: units are room pixels; gravity negative = falls (positive rises).
def colour(r, g, b, a=1.0):
    return {"r": r, "g": g, "b": b, "a": a}


BASE = {
    "preview2D": True, "maxParticles": 64, "emitRate": 0.0, "burstCount": 12, "loop": False,
    "duration": 1.0, "shape": "Sphere", "spreadDegrees": 40.0, "emitRadius": 2.0, "speed": 80.0,
    "speedVariance": 0.35, "gravity": 0.0, "drag": 1.0, "lifetime": 0.6, "lifetimeVariance": 0.3,
    "startSize": 4.0, "endSize": 1.0, "emissive": 1.0, "blendMode": "Alpha", "pixelSampling": True,
    "startColor": colour(1, 1, 1, 1), "endColor": colour(1, 1, 1, 0),
}

# kind: (suffix, bright RGB)  0 red blood, 1 green sap, 2 yellow, 3 violet ichor, 4 teal goo, 5 amber sap
KINDS = [("", (0.95, 0.08, 0.12)), (" Sap", (0.45, 0.85, 0.2)), (" Yellow", (0.98, 0.85, 0.2)),
         (" Ichor", (0.7, 0.3, 0.95)), (" Goo", (0.2, 0.85, 0.8)), (" Amber", (1.0, 0.6, 0.15))]


def particle(name, notes, **overrides):
    data = dict(BASE)
    data.update(overrides)
    data["effectName"] = name
    data["notes"] = notes + " Sizes and speeds are room pixels (played with Particle2DBurst)."
    rel = "Particles/%s.particle.json" % name
    write_json(rel, data)
    write_meta(rel, "Particle", name)


def shade(rgb, k, a=1.0):
    return colour(round(min(1, rgb[0] * k), 3), round(min(1, rgb[1] * k), 3), round(min(1, rgb[2] * k), 3), a)


def mix_white(rgb, t):
    return colour(*(round(c + (1 - c) * t, 3) for c in rgb), 1.0)


def build_particles():
    for suffix, rgb in KINDS:
        particle("Blood Spray" + suffix, "Gore Kit: droplets thrown out of a wound.",
                 spreadDegrees=38, speed=130, speedVariance=0.5, gravity=-380, drag=0.4, lifetime=0.55,
                 startSize=3, endSize=2, startColor=shade(rgb, 1.0), midColor=shade(rgb, 0.85),
                 endColor=shade(rgb, 0.5, 0))
        particle("Blood Mist" + suffix, "Gore Kit: a soft puff around a wound.",
                 spreadDegrees=180, speed=45, drag=3.0, gravity=-20, lifetime=0.45, startSize=4, endSize=9,
                 startColor=shade(rgb, 1.0, 0.7), endColor=shade(rgb, 0.7, 0))
        particle("Gore Chunks" + suffix, "Gore Kit: heavy bits that tumble.",
                 spreadDegrees=70, speed=150, speedVariance=0.5, gravity=-520, drag=0.2, lifetime=0.8,
                 startSize=4, endSize=3, rotationSpeed=360, rotationVariance=0.6,
                 startColor=shade(rgb, 1.0), midColor=shade(rgb, 0.8), endColor=shade(rgb, 0.45, 0))
        particle("Blood Drip" + suffix, "Gore Kit: single drops falling from a cut.",
                 spreadDegrees=12, speed=14, gravity=-480, drag=0.1, lifetime=0.4, startSize=2, endSize=2,
                 startColor=shade(rgb, 1.0), endColor=shade(rgb, 0.6, 0))
        particle("Slice Spray" + suffix, "Gore Kit: a fan of droplets along a clean cut.",
                 spreadDegrees=22, speed=190, speedVariance=0.4, gravity=-300, drag=0.5, lifetime=0.4,
                 startSize=3, endSize=1, startColor=mix_white(rgb, 0.6), midColor=shade(rgb, 1.0),
                 endColor=shade(rgb, 0.55, 0))
    particle("Pip Spore Cloud", "Gore Kit GoreSpore: a faint cloud of pale spores when Pip is hurt and tainted.",
             spreadDegrees=180, speed=25, speedVariance=0.5, gravity=15, drag=2.0, lifetime=1.2,
             lifetimeVariance=0.3, startSize=5, endSize=10, startColor=colour(0.82, 0.78, 0.96, 0.6),
             endColor=colour(0.5, 0.4, 0.7, 0))


# ====================================================================== Gore Kit
GORE_KIT = """// Gore Kit: a library Script (ENEMIES, round 4). Cartoony pixel-art gore; every blood function does
// nothing when the `gore` global is 0 (the enemy then plays its old tidy poof).
//
// Enemy contract. Create calls  GoreEnemyInit(bloodKind, weak, originX, originY, foot)  once, after hp is set:
//   bloodKind  0 red blood, 1 green sap, 2 yellow, 3 violet ichor, 4 teal goo, 5 amber sap, -1 none
//   weak       1 for a small enemy that can feel fear (Fear Kit)
//   originX/Y  the sprite's origin in pixels;  foot = how far the collision mask bottom is below the origin
// Where the enemy spends `hurt` it calls  GoreEnemyHit()  first (before hp is lowered); on its death branch it
// calls  GoreEnemyDeath()  (before it swaps the sprite) which returns
//   0  splat: the enemy plays its own defeat (blood is already spilled)      1  sliced: Gore Half x2 made
//   2  gibbed: Gore Chunk bits made     for 1 and 2 the enemy must remove itself at once (InstanceDestroy(id)).
// Attackers set on the enemy: hitKind (1 stomp 2 sword 3 arrow 4 thrown 5 spore 6 starlit 7 bash 8 other),
// hitDir (-1/+1 direction of the blow), hitDmg, hitX, hitY; then add hitDmg to hurt.
// Bosses set isBoss = 1 after the init: no slicing or gibbing, only chips and ichor, RepKill is told 0.
//
//   GoreOn()                          1 unless the `gore` global is 0
//   GoreRgb(kind, channel)            0..255 colour of a blood kind (channel 0 red, 1 green, 2 blue)
//   GoreFx(base, kind)                path of the per-colour particle, e.g. GoreFx("Blood Spray", 1)
//   GoreBlood(x, y, amount, dirX, kind)  spray + mist at a point
//   GoreStain(x, y, kind)             floor decal with y = the floor surface; capped at 60 alive
//   GoreFloorY()                      the floor surface under the calling enemy (-1 if none within 200 px)
//   GoreSlice(), GoreGib()            called on the enemy by GoreEnemyDeath (halves / chunks)
//   GoreCorpseHit()                   a hit on a body already dying: gibs it, 1 if the enemy must vanish
//   GoreSpore(x, y)                   Pip's spore puff when hurt at taint stage >= 1

function GoreOn() {
    if (GlobalExists("gore")) {
        if (GlobalGet("gore") == 0) { return 0; }
    }
    return 1;
}

function GoreRgb(kind, channel) {
    var r = 242; var g = 20; var b = 30;
    if (kind == 1) { r = 115; g = 217; b = 51; }
    else if (kind == 2) { r = 250; g = 217; b = 51; }
    else if (kind == 3) { r = 179; g = 77; b = 242; }
    else if (kind == 4) { r = 51; g = 217; b = 204; }
    else if (kind == 5) { r = 255; g = 153; b = 38; }
    if (channel == 0) { return r; }
    if (channel == 1) { return g; }
    return b;
}

function GoreFx(base, kind) {
    var tail = "";
    if (kind == 1) { tail = " Sap"; }
    else if (kind == 2) { tail = " Yellow"; }
    else if (kind == 3) { tail = " Ichor"; }
    else if (kind == 4) { tail = " Goo"; }
    else if (kind == 5) { tail = " Amber"; }
    return StringJoin2(StringJoin2("Assets/Particles/", base), StringJoin2(tail, ".particle.json"));
}

// Room point -> GUI pixel, read from the view itself: Level Rules (RulesFitView) lays the view out in the window (the
// room has scaleViewportsWithWindow off), so the scale is the port's width over the world width it shows and the offset is
// the port's corner. A GUI-space drawing that belongs to the world uses the same numbers at any window shape.
function GoreGuiS() {
    var gbw = ViewGetBoundsW(0);
    if (gbw <= 0) { gbw = 640; }
    return ViewGetViewportW(0) / gbw;
}

function GoreGuiX(wx) {
    return ViewGetViewportX(0) + (wx - ViewGetPosX(0)) * GoreGuiS();
}

function GoreGuiY(wy) {
    return ViewGetViewportY(0) + (wy - ViewGetPosY(0)) * GoreGuiS();
}

// The first burst of an effect reads and prepares its file (tens of milliseconds): GoreEnemyInit calls this once per blood
// colour in a room, while the room loads, so the first hit or kill in play does not stall a frame.
function GoreWarm(kindW) {
    if (GoreOn() == 0 || kindW < 0) { return 0; }
    Particle2DBurst(GoreFx("Blood Spray", kindW), -9000, -9000, 1, 90);
    Particle2DBurst(GoreFx("Blood Mist", kindW), -9000, -9000, 1, 90);
    Particle2DBurst(GoreFx("Gore Chunks", kindW), -9000, -9000, 1, 90);
    Particle2DBurst(GoreFx("Blood Drip", kindW), -9000, -9000, 1, 90);
    Particle2DBurst(GoreFx("Slice Spray", kindW), -9000, -9000, 1, 90);
    return 1;
}

function GoreBlood(bx, by, amount, dirX, kind) {
    if (GoreOn() == 0) { return 0; }
    if (kind < 0) { return 0; }
    var deg = 90;
    if (dirX > 0) { deg = 35; }
    else if (dirX < 0) { deg = 145; }
    var n = Clamp(Floor(amount), 1, 40);
    Particle2DBurst(GoreFx("Blood Spray", kind), bx, by, n, deg);
    if (n >= 5) { Particle2DBurst(GoreFx("Blood Mist", kind), bx, by, Floor(n / 3) + 1, deg); }
    return 1;
}

function GoreStain(stx, sty, kind) {
    if (GoreOn() == 0) { return 0; }
    if (kind < 0) { return 0; }
    if (RoomValueGet("underwater", 0) == 1) { return 0; }
    var total = InstanceNumber("Blood Stain");
    if (total >= 60) {
        // Remove the stain with the least life left.
        var oldest = 0; var least = 100000;
        for (var i = 0; i < total; i = i + 1) {
            var cand = InstanceFind("Blood Stain", i);
            if (cand > 0) {
                var left = InstanceVariableGet(cand, "stainLife");
                if (left < least) { least = left; oldest = cand; }
            }
        }
        if (oldest > 0) { InstanceDestroy(oldest); }
    }
    var st = CreateInstance("Blood Stain", stx, sty, 0);
    if (st > 0) { InstanceVariableSet(st, "stainKind", kind); }
    return st;
}

function GoreSpore(sx, sy) {
    var stage = 0;
    if (GlobalExists("taintStage")) { stage = GlobalGet("taintStage"); }
    if (stage < 1) { return 0; }
    Particle2DBurst("Assets/Particles/Pip Spore Cloud.particle.json", sx, sy, 5 + stage * 4, 90);
    return 1;
}

// Call once in an enemy's Create, after hp is set.
function GoreEnemyInit(kindArg, weakArg, oxArg, oyArg, footArg) {
    var warmKey = StringJoin2("goreWarm", StringOf(kindArg));
    if (RoomValueGet(warmKey, 0) == 0) { RoomValueSet(warmKey, 1); GoreWarm(kindArg); }
    self.bloodKind = kindArg; self.weak = weakArg;
    self.goreOx = oxArg; self.goreOy = oyArg; self.goreFoot = footArg;
    if (!InstanceVariableExists(self.id, "maxHp")) { self.maxHp = self.hp; }
    self.isBoss = 0; self.goreCut = 0.55; self.goreCorpse = 0;
    self.feared = 0; self.fearTimer = 0; self.fearClock = 0; self.fearEnded = 0; self.fearCounted = 0;
    self.fearTag = ""; self.fearCornered = 0;
    self.hitKind = 0; self.hitDir = 0; self.hitDmg = 0; self.hitX = 0; self.hitY = 0;
    self.goreKind = 8; self.goreDmg = 1; self.goreDir = 1; self.goreHp = self.hp;
    self.goreName = ""; self.goreSw = 0; self.goreSh = 0; self.goreFrame = 0;
    // Collection page: which kind of creature this is (0..9), read from its own sprite (Achievements Kit).
    self.foeKind = AchKindOf(SpriteGet());
    return 1;
}

function GoreHeroDir() {
    var hero = RoomValueGet("heroId", 0);
    if (hero > 0) {
        if (InstanceGetX(hero) > self.x) { return -1; }
    }
    return 1;
}

// Reads the enemy's current sprite and frame into goreName / goreSw / goreSh / goreFrame.
function GoreMeasure() {
    self.goreName = SpriteGet();
    self.goreFrame = Floor(SpriteGetFrame());
    var w = 0; var h = 0;
    if (self.goreName != "") { w = SpriteWidth(self.goreName); h = SpriteHeight(self.goreName); }
    if (w <= 0 || h <= 0) { w = 16; h = 16; self.goreName = ""; }
    self.goreSw = w; self.goreSh = h;
    return 1;
}

// The floor surface under the calling enemy (its collision mask is walked down), or -1.
function GoreFloorY() {
    var px = self.x; var py = self.y;
    if (TileMeeting(px, py)) { return py + self.goreFoot; }
    var k = 0;
    while (k < 200) {
        k = k + 3;
        if (TileMeeting(px, py + k)) { return py + k - 2 + self.goreFoot; }
    }
    return -1;
}

// Spend-of-hurt hook: blood on every hit. Call BEFORE hp is lowered.
function GoreEnemyHit() {
    var kindNow = self.hitKind; var dmgNow = self.hitDmg;
    if (kindNow == 0) { kindNow = 8; }
    if (dmgNow <= 0) { dmgNow = Max(1, self.hurt); }
    var dirNow = self.hitDir;
    if (dirNow == 0) { dirNow = GoreHeroDir(); }
    var hxp = self.hitX; var hyp = self.hitY;
    if (hxp == 0 && hyp == 0) { hxp = self.x; hyp = self.y - self.goreOy * 0.5; }
    self.goreKind = kindNow; self.goreDmg = dmgNow; self.goreDir = dirNow; self.goreHp = self.hp;
    self.hitKind = 0; self.hitDmg = 0; self.hitX = 0; self.hitY = 0;
    if (self.bloodKind < 0) { return 0; }
    if (GoreOn() == 0) { return 0; }
    if (self.isBoss == 1) {
        GoreBlood(hxp, hyp, 16, dirNow, self.bloodKind);
        Particle2DBurst(GoreFx("Gore Chunks", self.bloodKind), hxp, hyp, 6, 90);
        return 1;
    }
    var amount = 4 + dmgNow * 3;
    if (kindNow == 1) { amount = 3; }
    GoreBlood(hxp, hyp, amount, dirNow, self.bloodKind);
    return 1;
}

// Death hook. Returns 0 splat (enemy continues its own defeat), 1 sliced, 2 gibbed (enemy must vanish now).
function GoreEnemyDeath() {
    var kindNow = self.goreKind;
    var boss = self.isBoss;
    var overkill = 0; var sliced = 0; var result = 0;
    GoreMeasure();
    var bodyX = self.x; var bodyY = self.y - self.goreOy * 0.5;
    if (boss == 0) {
        if (self.goreDmg >= self.goreHp + 2) { overkill = 1; }
        if (GoreOn() == 1 && self.bloodKind >= 0) {
            if (overkill == 1) { result = 2; }
            else if (kindNow == 2 || kindNow == 4) { result = 1; sliced = 1; }
        }
    }
    if (result == 1) { GoreSlice(); }
    else if (result == 2) { GoreGib(); }
    else if (GoreOn() == 1 && self.bloodKind >= 0) {
        if (boss == 1) {
            GoreBlood(bodyX, bodyY, 34, 0, self.bloodKind);
            Particle2DBurst(GoreFx("Gore Chunks", self.bloodKind), bodyX, bodyY, 16, 90);
        } else {
            var amount = 8;
            if (kindNow == 1) { amount = 5; }
            GoreBlood(bodyX, bodyY, amount, self.goreDir, self.bloodKind);
            var ground = GoreFloorY();
            if (ground >= 0) { GoreStain(self.x + RandomRange(-4, 4), ground, self.bloodKind); }
        }
    }
    // Collection counters and the first-stomp / first-slice / boss achievements (Achievements Kit).
    AchOnKill(kindNow, sliced, overkill, self.foeKind);
    if (boss == 1) { RepKill(0, 0, 0, 2); }
    else { RepKill(kindNow, overkill, sliced, self.weak); }
    return result;
}

function GoreHalfMake(part, ground, dirNow, flip, ox, oy, cutRow, rest, vxArg, vyArg) {
    var piece = CreateInstance("Gore Half", self.x, self.y, 0);
    if (piece <= 0) { return 0; }
    InstanceVariableSet(piece, "halfSpr", self.goreName);
    InstanceVariableSet(piece, "halfFrame", self.goreFrame);
    InstanceVariableSet(piece, "halfW", self.goreSw);
    InstanceVariableSet(piece, "halfH", self.goreSh);
    InstanceVariableSet(piece, "halfOx", ox);
    InstanceVariableSet(piece, "halfOy", oy);
    InstanceVariableSet(piece, "halfFlip", flip);
    InstanceVariableSet(piece, "halfCutRow", cutRow);
    InstanceVariableSet(piece, "halfPart", part);
    InstanceVariableSet(piece, "halfKind", self.bloodKind);
    InstanceVariableSet(piece, "halfGround", ground);
    InstanceVariableSet(piece, "halfRest", rest);
    InstanceVariableSet(piece, "halfUnder", RoomValueGet("underwater", 0));
    InstanceVariableSet(piece, "vx", vxArg);
    InstanceVariableSet(piece, "vy", vyArg);
    return piece;
}

// Slice the calling enemy in half across its middle: two Gore Half instances using its current sprite frame.
function GoreSlice() {
    GoreMeasure();
    var sw = self.goreSw; var sh = self.goreSh;
    var flip = 0;
    if (self.image_xscale < 0) { flip = 1; }
    var ox = self.goreOx;
    if (flip == 1) { ox = sw - self.goreOx; }
    var oy = self.goreOy;
    var cutRow = Clamp(Floor(sh * self.goreCut), 2, Max(2, sh - 2));
    var dirNow = self.goreDir;
    if (dirNow == 0) { dirNow = 1; }
    var ground = GoreFloorY();
    var restTop = 100000; var restBottom = 100000;
    if (ground >= 0) { restTop = ground + oy - cutRow; restBottom = ground - self.goreFoot; }
    GoreHalfMake(0, ground, dirNow, flip, ox, oy, cutRow, restTop, dirNow * RandomRange(70, 120), -RandomRange(170, 240));
    GoreHalfMake(1, ground, dirNow, flip, ox, oy, cutRow, restBottom, -dirNow * RandomRange(8, 28), -RandomRange(20, 60));
    var cutX = self.x - ox + sw * 0.5; var cutY = self.y - oy + cutRow;
    var deg = 90 - dirNow * 55;
    Particle2DBurst(GoreFx("Slice Spray", self.bloodKind), cutX, cutY, 14, deg);
    Particle2DBurst(GoreFx("Blood Mist", self.bloodKind), cutX, cutY, 5, 90);
    return 1;
}

// Burst the calling enemy into bits: chunks of its own sprite and of its blood, flying and bouncing.
function GoreGib() {
    GoreMeasure();
    var sw = self.goreSw; var sh = self.goreSh;
    var ground = GoreFloorY();
    var cx = self.x; var cy = self.y - self.goreOy * 0.5;
    var kind = self.bloodKind;
    Particle2DBurst(GoreFx("Blood Spray", kind), cx, cy, 14, 55);
    Particle2DBurst(GoreFx("Blood Spray", kind), cx, cy, 14, 125);
    Particle2DBurst(GoreFx("Blood Mist", kind), cx, cy, 10, 90);
    Particle2DBurst(GoreFx("Gore Chunks", kind), cx, cy, 8, 90);
    var i = 0;
    while (i < 6) {
        var bit = CreateInstance("Gore Chunk", cx + RandomRange(-6, 6), cy + RandomRange(-8, 8), 0);
        if (bit > 0) {
            InstanceVariableSet(bit, "vx", RandomRange(-140, 140));
            InstanceVariableSet(bit, "vy", RandomRange(-280, -90));
            InstanceVariableSet(bit, "chunkKind", kind);
            InstanceVariableSet(bit, "chunkGround", ground);
            InstanceVariableSet(bit, "chunkUnder", RoomValueGet("underwater", 0));
            if (i % 2 == 0 && self.goreName != "") {
                InstanceVariableSet(bit, "chunkSpr", self.goreName);
                InstanceVariableSet(bit, "chunkFrame", self.goreFrame);
                InstanceVariableSet(bit, "chunkU", RandomRange(0.15, 0.6));
                InstanceVariableSet(bit, "chunkV", RandomRange(0.15, 0.6));
                InstanceVariableSet(bit, "chunkDu", 6 / sw);
                InstanceVariableSet(bit, "chunkDv", 6 / sh);
            }
        }
        i = i + 1;
    }
    if (ground >= 0) {
        GoreStain(self.x - 5, ground, kind);
        GoreStain(self.x + 6, ground, kind);
    }
    return 1;
}

// A hit on a body that is already dying (tumbling bat, bee, moth): it bursts. 1 = the enemy must vanish now.
function GoreCorpseHit() {
    if (self.hurt <= 0) { return 0; }
    self.hurt = 0;
    if (GoreOn() == 0) { return 0; }
    if (self.bloodKind < 0) { return 0; }
    if (self.goreCorpse == 1) { return 0; }
    self.goreCorpse = 1;
    var kindNow = self.hitKind;
    if (kindNow == 0) { kindNow = 8; }
    self.hitKind = 0; self.hitDmg = 0;
    GoreGib();
    RepKill(kindNow, 1, 0, self.weak);
    return 1;
}
"""

# ====================================================================== Fear Kit
FEAR_KIT = """// Fear Kit: a library Script (ENEMIES, round 4). Small weak enemies get frightened of a brutal Pip.
//
//   FearCheck(radius)   call early in a weak enemy's Step (after its frozen / dead handling). Returns 1 while
//                       the enemy is afraid: brutality >= 35 and Pip within radius (the radius grows to 2.2x as
//                       brutality goes 35 -> 100); it stays afraid 1.4 s after Pip leaves the radius and calms
//                       when brutality drops below 28. On the first fright it switches to the "Scared"
//                       animation tag (a sprite without it just plays its whole animation) and counts
//                       fearedBy once. On the frame it calms down it restores its animation tag and sets
//                       fearEnded = 1 for one frame (the enemy may reset its own state then).
//   FearDir()           -1 / +1: the direction away from Pip.
//   FearCower(on)       a small shiver (image_angle wobble) for a cornered enemy; on = 0 stops it.
// Fleeing never matters to the level: nothing needs a weak enemy touched, and a frightened enemy that has
// nowhere to run simply stays and cowers (it can still be stomped).

function FearCalm() {
    self.feared = 0; self.fearTimer = 0; self.fearEnded = 1; self.fearCornered = 0;
    self.image_angle = 0;
    if (self.fearTag != "") { AnimationPlay(self.fearTag, true); }
    return 0;
}

function FearCheck(radius) {
    self.fearEnded = 0;
    if (self.dead == 1) { return 0; }
    var was = self.feared;
    var b = 0;
    if (GlobalExists("brutality")) { b = GlobalGet("brutality"); }
    var floorB = 35;
    if (was == 1) { floorB = 28; }
    if (b < floorB) {
        if (was == 1) { return FearCalm(); }
        return 0;
    }
    var dtf = Clamp(DeltaTime, 0, 0.05);
    self.fearClock = self.fearClock + dtf;
    var hero = RoomValueGet("heroId", 0);
    var seen = 0;
    if (hero > 0) {
        var reach = radius * (1 + Clamp((b - 35) / 65, 0, 1) * 1.2);
        if (was == 1) { reach = reach * 1.25; }
        if (PointDistance(self.x, self.y - 12, InstanceGetX(hero), InstanceGetY(hero) - 16) < reach) { seen = 1; }
    }
    if (seen == 1) { self.fearTimer = 1.4; }
    else { self.fearTimer = Max(0, self.fearTimer - dtf); }
    if (self.fearTimer > 0) {
        if (was == 0) {
            self.feared = 1; self.fearTag = AnimationGetTag();
            AnimationPlay("Scared", true);
            if (self.fearCounted == 0) {
                self.fearCounted = 1;
                var already = 0;
                if (GlobalExists("fearedBy")) { already = GlobalGet("fearedBy"); }
                GlobalSet("fearedBy", already + 1);
            }
        }
        return 1;
    }
    if (was == 1) { return FearCalm(); }
    return 0;
}

function FearDir() {
    var hero = RoomValueGet("heroId", 0);
    if (hero > 0) {
        if (InstanceGetX(hero) > self.x) { return -1; }
    }
    return 1;
}

function FearCower(on) {
    self.fearCornered = on;
    if (on == 1) { self.image_angle = Sin(self.fearClock * 2400) * 3; }
    else { self.image_angle = 0; }
    return on;
}
"""

# ====================================================================== objects
GORE_HALF_CREATE = """// Gore Half: the top (halfPart 0) or bottom (1) of a sliced enemy, made by GoreSlice (Gore Kit). It draws
// that part of the enemy's sprite frame in Draw GUI, falls, leaves a Blood Stain when it lands and fades.
halfSpr = ""; halfFrame = 0; halfW = 0; halfH = 0; halfOx = 0; halfOy = 0; halfFlip = 0; halfCutRow = 8;
halfPart = 0; halfKind = 0; halfGround = -1; halfRest = 100000; halfUnder = 0;
vx = 0; vy = 0; age = 0; halfLife = 3.5; landed = 0; bounces = 0; dripT = 0;
"""

GORE_HALF_STEP = """if (RoomValueGet("frozen", 0) == 1) { return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
age = age + dt;
if (age >= halfLife || y > RoomGetHeight() + 80) { InstanceDestroy(id); return; }
if (landed == 1) { return; }
if (halfUnder == 1) {
    vy = Min(vy + 160 * dt, 60);
    vx = vx * Max(0, 1 - dt * 2.5);
} else {
    vy = Min(vy + 900 * dt, 620);
}
x = x + vx * dt; y = y + vy * dt;
if (halfUnder == 0 && y >= halfRest && vy > 0) {
    y = halfRest;
    if (halfPart == 0 && bounces == 0 && vy > 150) {
        vy = -vy * 0.28; vx = vx * 0.6; bounces = 1;
    } else {
        vy = 0; vx = 0; landed = 1;
        if (halfGround >= 0) { GoreStain(x - halfOx + halfW * 0.5, halfGround, halfKind); }
    }
}
dripT = dripT - dt;
if (dripT <= 0 && halfUnder == 0) {
    dripT = 0.07;
    var cutX = x - halfOx + halfW * 0.5; var cutY = y - halfOy + halfCutRow;
    Particle2DBurst(GoreFx("Blood Drip", halfKind), cutX + RandomRange(-4, 4), cutY, 1, 270);
}
"""

GORE_HALF_DRAWGUI = """if (halfW <= 0 || halfH <= 0 || halfSpr == "") { return; }
var s = GoreGuiS();
var vx0 = ViewGetPosX(0); var vy0 = ViewGetPosY(0);
if (x < vx0 - 80 || x > vx0 + ViewGetBoundsW(0) + 80 || y < vy0 - 80 || y > vy0 + 440) { return; }
var fade = Clamp((halfLife - age) / 0.8, 0, 1);
if (fade <= 0) { return; }
var gl = Floor(x - halfOx);
var gt = Floor(y - halfOy);
var hh = halfCutRow; var v0 = 0; var v1 = halfCutRow / halfH;
if (halfPart == 1) {
    gt = gt + halfCutRow; hh = halfH - halfCutRow; v0 = halfCutRow / halfH; v1 = 1;
}
var gx = GoreGuiX(gl); var gy = GoreGuiY(gt);
if (halfFlip == 0) {
    DrawSpritePart(halfSpr, halfFrame, 0, v0, 1, v1, gx, gy, halfW * s, hh * s, fade);
} else {
    // The sprite is mirrored: draw it in 2 px columns from the far side (DrawSpritePart cannot flip).
    var col = 0;
    while (col < halfW) {
        var cw = Min(2, halfW - col);
        DrawSpritePart(halfSpr, halfFrame, (halfW - col - cw) / halfW, v0, (halfW - col) / halfW, v1,
            gx + col * s, gy, cw * s, hh * s, fade);
        col = col + 2;
    }
}
// The raw cut edge: a short blood-coloured bar across the middle of the cut.
var edge = gt;
if (halfPart == 0) { edge = gt + hh - 2; }
DrawSetAlpha(fade);
DrawSetColorRgb(GoreRgb(halfKind, 0), GoreRgb(halfKind, 1), GoreRgb(halfKind, 2));
DrawRectangle(gx + halfW * 0.3 * s, GoreGuiY(edge), gx + halfW * 0.7 * s, GoreGuiY(edge + 2));
DrawSetAlpha(1);
"""

GORE_CHUNK_CREATE = """// Gore Chunk: a small bouncing bit of a gibbed enemy (Gore Kit GoreGib). Half of them draw a 6x6 piece of
// the enemy's own sprite, the rest are blood-coloured pixels.
vx = 0; vy = 0; chunkKind = 0; chunkGround = -1; chunkUnder = 0; chunkSpr = ""; chunkFrame = 0;
chunkU = 0; chunkV = 0; chunkDu = 0.2; chunkDv = 0.2; age = 0; chunkLife = 2.6; bounces = 0; resting = 0;
"""

GORE_CHUNK_STEP = """if (RoomValueGet("frozen", 0) == 1) { return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
age = age + dt;
if (age >= chunkLife || y > RoomGetHeight() + 80) { InstanceDestroy(id); return; }
if (resting == 1) { return; }
if (chunkUnder == 1) {
    vy = Min(vy + 120 * dt, 50);
    vx = vx * Max(0, 1 - dt * 3);
} else {
    vy = Min(vy + 700 * dt, 560);
}
x = x + vx * dt; y = y + vy * dt;
if (chunkUnder == 0 && chunkGround >= 0 && y >= chunkGround - 2 && vy > 0) {
    y = chunkGround - 2;
    bounces = bounces + 1;
    if (bounces >= 3 || vy < 90) { resting = 1; vx = 0; vy = 0; }
    else { vy = -vy * 0.45; vx = vx * 0.6; }
}
"""

GORE_CHUNK_DRAWGUI = """var s = GoreGuiS();
var vx0 = ViewGetPosX(0); var vy0 = ViewGetPosY(0);
if (x < vx0 - 40 || x > vx0 + ViewGetBoundsW(0) + 40 || y < vy0 - 40 || y > vy0 + 400) { return; }
var fade = Clamp((chunkLife - age) / 0.6, 0, 1);
if (fade <= 0) { return; }
var gx = GoreGuiX(Floor(x) - 3); var gy = GoreGuiY(Floor(y) - 3);
if (chunkSpr != "") {
    DrawSpritePart(chunkSpr, chunkFrame, chunkU, chunkV, chunkU + chunkDu, chunkV + chunkDv, gx, gy, 6 * s, 6 * s, fade);
} else {
    DrawSetAlpha(fade);
    DrawSetColorRgb(GoreRgb(chunkKind, 0), GoreRgb(chunkKind, 1), GoreRgb(chunkKind, 2));
    DrawRectangle(gx + s, gy + s, gx + 5 * s, gy + 5 * s);
    DrawSetColorRgb(255, 255, 255);
    DrawSetAlpha(fade * 0.6);
    DrawRectangle(gx + 2 * s, gy + 2 * s, gx + 3 * s, gy + 3 * s);
    DrawSetAlpha(1);
}
"""

BLOOD_STAIN_CREATE = """// Blood Stain: a flat decal on a floor (Gore Kit GoreStain; y is the floor surface). Cheap: a few rectangles in
// Draw GUI, about 40 s of life, fading over the last 8 s. GoreStain keeps at most 60 alive.
stainKind = 0; stainLife = 40; stainW = 8 + Floor(Random(11)); stainShape = Floor(Random(3));
"""

BLOOD_STAIN_STEP = """if (RoomValueGet("frozen", 0) == 1) { return; }
stainLife = stainLife - Clamp(DeltaTime, 0, 0.05);
if (stainLife <= 0) { InstanceDestroy(id); }
"""

BLOOD_STAIN_DRAWGUI = """var vx0 = ViewGetPosX(0);
if (x < vx0 - 30 || x > vx0 + ViewGetBoundsW(0) + 30) { return; }
var vy0 = ViewGetPosY(0);
if (y < vy0 - 10 || y > vy0 + 380) { return; }
var s = GoreGuiS();
var fade = Clamp(stainLife / 8, 0, 1);
var gx = GoreGuiX(Floor(x)); var gy = GoreGuiY(Floor(y));
var half = stainW * 0.5 * s;
// The puddle lies in the floor's top row (below Pip's feet): a dark body with a bright highlight above it.
DrawSetAlpha(0.85 * fade);
DrawSetColorRgb(GoreRgb(stainKind, 0) * 0.62, GoreRgb(stainKind, 1) * 0.62, GoreRgb(stainKind, 2) * 0.62);
DrawRectangle(gx - half, gy, gx + half, gy + 2 * s);
DrawSetAlpha(0.95 * fade);
DrawSetColorRgb(GoreRgb(stainKind, 0), GoreRgb(stainKind, 1), GoreRgb(stainKind, 2));
if (stainShape == 0) { DrawRectangle(gx - half * 0.55, gy - s, gx + half * 0.3, gy + s); }
else if (stainShape == 1) { DrawRectangle(gx - half * 0.2, gy - s, gx + half * 0.7, gy + s); }
else { DrawRectangle(gx - half * 0.7, gy, gx - half * 0.2, gy + s); DrawRectangle(gx + half * 0.1, gy - s, gx + half * 0.5, gy + s); }
DrawSetAlpha(1);
"""


def build():
    build_particles()
    write_script("Gore Kit", GORE_KIT)
    write_script("Fear Kit", FEAR_KIT)
    write_object("Gore Half", [("Create", GORE_HALF_CREATE), ("Step", GORE_HALF_STEP), ("DrawGui", GORE_HALF_DRAWGUI)])
    write_object("Gore Chunk", [("Create", GORE_CHUNK_CREATE), ("Step", GORE_CHUNK_STEP), ("DrawGui", GORE_CHUNK_DRAWGUI)])
    write_object("Blood Stain", [("Create", BLOOD_STAIN_CREATE), ("Step", BLOOD_STAIN_STEP), ("DrawGui", BLOOD_STAIN_DRAWGUI)])


def main():
    build()
    print("Gore and Fear kits written to", ASSETS)


if __name__ == "__main__":
    main()
