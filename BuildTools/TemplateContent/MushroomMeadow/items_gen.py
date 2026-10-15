"""Mushroom Meadow round 4: the Item Kit (held items, pickups, thrown items and arrows).

Writes (into the 2DShowcase template Assets folder):
  Scripts   Item Kit (library: every function is prefixed Item, called directly by Explorer and others)
  Objects   Held Item (the overlay that draws the held item on Pip), Item Sword, Item Shield, Item Bow,
            Item Propeller Flower (pickups), Thrown Item, Arrow (the arrow in flight)

Held items: 1 Sword, 2 Shield, 3 Bow, 4 Propeller Flower. Globals `heldItem` and `arrows` carry them between
rooms; Explorer mirrors them in the instance variables itemHeld / itemArrows. Nothing here is required to finish
a level: every item is an optional pickup, and every sprite is guarded (a missing sprite is drawn as a simple
code-made shape).

The arrow object is `Arrow`; its image is `Arrow Sprite` (art contract amendment).

Run after hero_gen.py (README order). Deterministic.
"""
import hashlib
import json
import os
import re

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
ASSETS = os.path.join(ROOT, "Source", "Genesis.Application.Core", "Projects", "Templates",
                      "Assets", "TwoDShowcase", "Assets")
STAMP = "2026-10-15T00:00:00Z"

# Everything an item can hurt (keep in sync with hero_gen.ENEMY_NAMES; main() warns when they differ).
ENEMY_NAMES = ["Acorn Walker", "Buzz Bee", "Flit Bat", "Glow Grub", "Bloatfin", "Driftjelly", "Dusk Moth",
               "Burr Hopper", "Grumblecrag", "Duskmantle", "Gloom Orb", "Pebble", "Stalactite"]
FOE_LIST = ", ".join('"%s"' % n for n in ENEMY_NAMES)

# kind -> (pickup object, pickup image, held image, thrown image, label)
KINDS = {
    1: ("Item Sword", "Item Sword Sprite", "Held Sword", "Thrown Sword", "SWORD"),
    2: ("Item Shield", "Item Shield Sprite", "Held Shield", "Thrown Shield", "SHIELD"),
    3: ("Item Bow", "Item Bow Sprite", "Held Bow", "Thrown Bow", "BOW"),
    4: ("Item Propeller Flower", "Item Propeller Flower Sprite", "Held Flower", "Thrown Flower", "PROPELLER FLOWER"),
}


def guid(name):
    return hashlib.md5(("mushroom-meadow-items:" + name).encode("utf-8")).hexdigest()


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


def write_object(name, sprite, events):
    """events: ordered list of (event name, PGSL source). sprite: image name without path ('' for none)."""
    rel = "Objects/%s.object.json" % name
    write_json(rel, {
        "schemaVersion": 2,
        "dimension": "TwoD",
        "sprite": ("Assets/Sprites/%s.image.json" % sprite) if sprite else "",
        "model": "",
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


def write_script(name, code):
    rel = "Scripts/%s.pgsl" % name
    write_text(rel, code)
    write_meta(rel, "Script", name)


def sprite_or_carrier(name):
    """The pickup's own image carries its mask when it exists; otherwise the Spore Shot's small mask does
    (the Draw event draws everything either way)."""
    if os.path.exists(os.path.join(ASSETS, "Sprites", name + ".image.json")):
        return name
    return "Spore Shot Sprite"


# ====================================================================== Item Kit (library)
ITEM_KIT = r"""// Item Kit: a library Script (round 4). Every function is called directly; names start with Item.
// Held items: 1 Sword, 2 Shield, 3 Bow, 4 Propeller Flower (global heldItem; arrows in global arrows).
// Functions that act on Pip use `self` (Explorer calls them, so self is Pip); the ones that take a target
// or foe work from any object.
//
// Called by Explorer (HERO):
//   ItemInit()                 Create: reads heldItem/arrows, makes the Held Item overlay.
//   ItemStep(dt)               once per playing frame after movement: Use/aim/draw/swing/shield/flower.
//   ItemOnHurt(damage)         returns 1 when a raised shield absorbed an enemy hit (call before taking damage).
//   ItemThrow()                the held item flies away (Down + Throw on the ground drops it instead).
//   ItemDrop()                 lays the held item at Pip's feet.
//   ItemPickup(kind)           gives Pip an item (1..4); the old one is tossed out. Pickups call ItemGive themselves.
//   ItemDrawHud()              DrawGui: held item slot, arrow count, flower fuel meter (1280x720 design space).
// Optional hooks Explorer may call (ItemStep already does the equivalent as a fallback):
//   ItemAdjustVelocity(dt)     returns Pip's new vertical speed; call as vy = ItemAdjustVelocity(dt) right after
//                              gravity: flower parachute caps the fall at 90 px/s, the rise pulls toward -150 px/s.
//   ItemMovementScale()        1 normally, 0.5 with the shield raised, 0.65 while drawing the bow; multiply the
//                              target walking speed by it.
// For shops and scripts (any object):
//   ItemGrantGlobal(kind)      puts an item in Pip's hands through the globals (Pip adopts it the next frame);
//                              the item held before is tossed out near Pip.
//   ItemGrantArrows(n)         adds n arrows (max 99) through the global.
//   ItemObjectName(kind)       the pickup object ("Item Sword" ...), ItemLabel(kind) a caption ("SWORD" ...).
// Damage to enemies follows the round-4 convention (hitKind 2 sword, 3 arrow, 4 thrown, 7 shield bash).

function ItemSnd(nm, vol, pit) {
    if (ResourceExists(nm)) {
        PlaySound(StringJoin2(StringJoin2("Assets/Audio/", nm), ".audio.json"), vol, pit, false);
    }
    return 0;
}

function ItemObjectName(kind) {
    if (kind == 1) { return "Item Sword"; }
    if (kind == 2) { return "Item Shield"; }
    if (kind == 3) { return "Item Bow"; }
    if (kind == 4) { return "Item Propeller Flower"; }
    return "";
}

function ItemLabel(kind) {
    if (kind == 1) { return "SWORD"; }
    if (kind == 2) { return "SHIELD"; }
    if (kind == 3) { return "BOW"; }
    if (kind == 4) { return "PROPELLER FLOWER"; }
    return "";
}

function ItemSpriteName(kind) {
    if (kind == 1) { return "Item Sword Sprite"; }
    if (kind == 2) { return "Item Shield Sprite"; }
    if (kind == 3) { return "Item Bow Sprite"; }
    if (kind == 4) { return "Item Propeller Flower Sprite"; }
    return "";
}

function ItemFoeList() {
    return [FOE_LIST];
}

// ------------------------------------------------------------------ input (Input Kit, with the old keys as a fallback)
function ItemDown(action) {
    if (self.itemHasInput == 1) { return InputDown(action); }
    if (action == "use") { return KeyCheck("J") || GamepadCheck("X") || MouseCheck(MbLeft); }
    if (action == "up") { return KeyCheck("W") || KeyCheck("Up") || GamepadCheck("Up"); }
    if (action == "down") { return KeyCheck("S") || KeyCheck("Down") || GamepadCheck("Down"); }
    if (action == "jump") { return KeyCheck("Space") || KeyCheck("W") || KeyCheck("Up") || GamepadCheck("A"); }
    return 0;
}

function ItemPressed(action) {
    if (self.itemHasInput == 1) { return InputPressed(action); }
    if (action == "use") { return KeyPressed("J") || GamepadPressed("X") || MousePressed(MbLeft); }
    return 0;
}

// ------------------------------------------------------------------ hitting enemies (any caller)
// Sets the damage-convention fields on the foe and adds to its hurt. Score is awarded when the hit would kill.
function ItemHitFoe(foe, kind, dmg, dirX, hx, hy, pip) {
    var dr = dirX;
    if (dr == 0) { dr = 1; }
    InstanceVariableSet(foe, "hitKind", kind);
    InstanceVariableSet(foe, "hitDir", dr);
    InstanceVariableSet(foe, "hitDmg", dmg);
    InstanceVariableSet(foe, "hitX", hx);
    InstanceVariableSet(foe, "hitY", hy);
    var hpNow = InstanceVariableGet(foe, "hp");
    InstanceVariableSet(foe, "hurt", InstanceVariableGet(foe, "hurt") + dmg);
    if (pip > 0 && hpNow <= dmg) {
        InstanceVariableSet(pip, "score", InstanceVariableGet(pip, "score") + InstanceVariableGet(foe, "points"));
    }
    if (kind == 2) { ItemSnd("Stomp", 0.9, 1.35); }
    else if (kind == 3) { ItemSnd("Spore Hit", 0.9, 0.9); }
    else if (kind == 7) { ItemSnd("Bump", 0.9, 0.9); }
    else { ItemSnd("Stomp", 0.9, 0.9); }
    return 1;
}

// Hits every live foe whose body overlaps the rectangle and is not in hitList yet (the ids hit are added).
// Returns how many were hit.
function ItemScan(names, x1, y1, x2, y2, kind, dmg, dirX, hitList, pip) {
    var count = 0;
    for (var ni = 0; ni < PgListSize(names); ni = ni + 1) {
        var nm = names[ni];
        var total = InstanceNumber(nm);
        if (total > 0) {
            var firstHit = CollisionRectangle(x1, y1, x2, y2, nm);
            for (var fi = 0; fi < total; fi = fi + 1) {
                var foe = InstanceFind(nm, fi);
                if (foe > 0 && InstanceVariableGet(foe, "dead") == 0 && PgListFind(hitList, foe) < 0) {
                    var fx = InstanceGetX(foe); var fy = InstanceGetY(foe);
                    var inside = 0;
                    if (foe == firstHit) { inside = 1; }
                    if (fx > x1 - 10 && fx < x2 + 10 && fy > y1 - 6 && fy < y2 + 30) { inside = 1; }
                    if (inside == 1) {
                        PgListAdd(hitList, foe);
                        ItemHitFoe(foe, kind, dmg, dirX, fx, Clamp(fy - 10, y1, y2), pip);
                        count = count + 1;
                    }
                }
            }
        }
    }
    return count;
}

// ------------------------------------------------------------------ drawing helpers (world or GUI space)
// A simple code-made picture of an item for when its sprite is missing. ang in degrees (clockwise on screen,
// 0 = pointing right); for the flower ang is its spin.
function ItemDrawFallback(kind, cx, cy, ang, sc, al) {
    DrawSetAlpha(al);
    var ca = Cos(ang); var sa = Sin(ang);
    if (kind == 1) {
        DrawSetColorRgb(222, 232, 240);
        for (var bi = 3; bi <= 15; bi = bi + 1) { DrawCircle(cx + ca * bi * sc, cy + sa * bi * sc, 1.3 * sc, false); }
        DrawSetColorRgb(196, 144, 62);
        for (var gi = -4; gi <= 4; gi = gi + 1) { DrawCircle(cx + ca * 2 * sc - sa * gi * sc, cy + sa * 2 * sc + ca * gi * sc, 1.1 * sc, false); }
        DrawSetColorRgb(120, 78, 40);
        DrawCircle(cx, cy, 1.5 * sc, false);
    } else if (kind == 2) {
        DrawSetColorRgb(126, 88, 48);
        DrawRoundRect(cx - 6 * sc, cy - 8 * sc, cx + 6 * sc, cy + 8 * sc, 3 * sc, false);
        DrawSetColorRgb(236, 196, 104);
        DrawRoundRect(cx - 6 * sc, cy - 8 * sc, cx + 6 * sc, cy + 8 * sc, 3 * sc, true);
        DrawSetColorRgb(236, 196, 104);
        DrawRectangle(cx - 1 * sc, cy - 6 * sc, cx + 1 * sc, cy + 6 * sc);
    } else if (kind == 3) {
        DrawSetColorRgb(150, 98, 52);
        var lastX = cx; var lastY = cy;
        for (var ki = -5; ki <= 5; ki = ki + 1) {
            var tt = ki / 5;
            var fwd = (1 - tt * tt) * 5 * sc;
            var side = tt * 10 * sc;
            var bx = cx + ca * fwd - sa * side;
            var by = cy + sa * fwd + ca * side;
            DrawCircle(bx, by, 1.3 * sc, false);
            if (ki == -5) { lastX = bx; lastY = by; }
            if (ki == 5) {
                DrawSetColorRgb(236, 230, 210);
                DrawLine(lastX, lastY, bx, by);
            }
        }
    } else if (kind == 4) {
        DrawSetColorRgb(86, 150, 70);
        DrawRectangle(cx - 0.8 * sc, cy - 2 * sc, cx + 0.8 * sc, cy + 8 * sc);
        DrawSetColorRgb(244, 126, 160);
        var rw = 9 * sc * Abs(Cos(ang));
        DrawEllipse(cx - rw - 1 * sc, cy - 5 * sc, cx - 1 * sc, cy - 2 * sc, false);
        DrawEllipse(cx + 1 * sc, cy - 5 * sc, cx + rw + 1 * sc, cy - 2 * sc, false);
        DrawSetColorRgb(255, 226, 120);
        DrawCircle(cx, cy - 3.5 * sc, 1.6 * sc, false);
    }
    DrawSetAlpha(1);
    return 0;
}

// An arrow centred on (cx, cy) pointing along ang.
function ItemDrawArrow(cx, cy, ang, al) {
    var img = "";
    if (ResourceExists("Arrow Sprite")) { img = "Assets/Sprites/Arrow Sprite.image.json"; }
    if (img != "") {
        var fy = 1;
        if (Cos(ang) < 0) { fy = -1; }
        DrawSpriteScaled(img, cx, cy, 0, 1, fy, ang, al);
    } else {
        var ca = Cos(ang); var sa = Sin(ang);
        DrawSetAlpha(al);
        DrawSetColorRgb(232, 218, 176);
        DrawLine(cx - ca * 7, cy - sa * 7, cx + ca * 6, cy + sa * 6);
        DrawLine(cx - ca * 7, cy - sa * 7 + 1, cx + ca * 6, cy + sa * 6 + 1);
        DrawSetColorRgb(214, 222, 232);
        DrawCircle(cx + ca * 7, cy + sa * 7, 1.6, false);
        DrawSetColorRgb(206, 70, 64);
        DrawCircle(cx - ca * 7, cy - sa * 7, 1.5, false);
        DrawSetAlpha(1);
    }
    return 0;
}

// ------------------------------------------------------------------ pickups and holding
function ItemSpawnPickup(kind, px, py, pvx, pvy, dly, fresh) {
    var obj = ItemObjectName(kind);
    if (obj == "") { return 0; }
    var pk = CreateInstance(obj, px, py, 0);
    if (pk > 0) {
        InstanceVariableSet(pk, "mode", 1);
        InstanceVariableSet(pk, "vx", pvx);
        InstanceVariableSet(pk, "vy", pvy);
        InstanceVariableSet(pk, "delay", dly);
        InstanceVariableSet(pk, "fresh", fresh);
    }
    return pk;
}

// Clears everything an item was doing on the target (Pip).
function ItemResetState(target) {
    InstanceVariableSet(target, "itemSwingT", 0);
    InstanceVariableSet(target, "itemShield", 0);
    InstanceVariableSet(target, "itemDrawing", 0);
    InstanceVariableSet(target, "itemDrawT", 0);
    InstanceVariableSet(target, "itemFlowerOpen", 0);
    InstanceVariableSet(target, "itemRising", 0);
    InstanceVariableSet(target, "itemRiseT", 0);
    InstanceVariableSet(target, "itemRiseBlocked", 0);
    return 0;
}

// Gives the target (Pip) an item. fresh = 1 for a pickup from the level or the shop (a Bow then comes with arrows).
// Returns 1 when the pickup was used up, 0 when nothing changed (already holding that kind).
function ItemGive(target, kind, fresh) {
    var old = InstanceVariableGet(target, "itemHeld");
    var tx = InstanceGetX(target); var ty = InstanceGetY(target);
    var tf = InstanceVariableGet(target, "facing");
    if (tf == 0) { tf = 1; }
    if (old == kind) {
        if (kind == 3 && fresh == 1 && InstanceVariableGet(target, "itemArrows") < 12) {
            InstanceVariableSet(target, "itemArrows", 12);
            InstanceVariableSet(target, "itemWroteArrows", 12);
            GlobalSet("arrows", 12);
            ItemSnd("Power Up", 0.8, 1.3);
            return 1;
        }
        return 0;
    }
    if (old != 0) { ItemSpawnPickup(old, tx - tf * 4, ty - 18, -tf * 70, -210, 0.9, 0); }
    ItemResetState(target);
    InstanceVariableSet(target, "itemHeld", kind);
    InstanceVariableSet(target, "itemWroteHeld", kind);
    GlobalSet("heldItem", kind);
    if (kind == 3 && fresh == 1 && InstanceVariableGet(target, "itemArrows") < 12) {
        InstanceVariableSet(target, "itemArrows", 12);
        InstanceVariableSet(target, "itemWroteArrows", 12);
        GlobalSet("arrows", 12);
    }
    ItemSnd("Power Up", 0.85, 1.25);
    ScriptExecute("Spawn Effect", 0, tx, ty - 22);
    return 1;
}

function ItemPickup(kind) {
    return ItemGive(self.id, kind, 1);
}

function ItemGrantGlobal(kind) {
    var pip = InstanceFind("Explorer", 0);
    var old = Clamp(GlobalGet("heldItem"), 0, 4);
    if (old != 0 && old != kind && pip > 0) {
        var pf = InstanceVariableGet(pip, "facing");
        if (pf == 0) { pf = 1; }
        ItemSpawnPickup(old, InstanceGetX(pip) - pf * 4, InstanceGetY(pip) - 18, -pf * 70, -210, 1.2, 0);
    }
    GlobalSet("heldItem", kind);
    if (kind == 3 && GlobalGet("arrows") < 12) { GlobalSet("arrows", 12); }
    return 1;
}

function ItemGrantArrows(n) {
    GlobalSet("arrows", Clamp(GlobalGet("arrows") + n, 0, 99));
    return GlobalGet("arrows");
}

function ItemDrop() {
    var held = self.itemHeld;
    if (held == 0) { return 0; }
    ItemSpawnPickup(held, self.x + self.facing * 10, self.y - 12, self.facing * 40, -90, 0.6, 0);
    ItemResetState(self.id);
    self.itemHeld = 0; self.itemWroteHeld = 0;
    GlobalSet("heldItem", 0);
    ItemSnd("Bump", 0.5, 1.2);
    return 1;
}

function ItemThrow() {
    var held = self.itemHeld;
    if (held == 0) { return 0; }
    if (self.grounded == 1 && ItemDown("down")) { return ItemDrop(); }
    var th = CreateInstance("Thrown Item", self.x + self.facing * 10, self.y - 22, 0);
    if (th > 0) {
        var tvx = self.facing * 330 + self.vx * 0.25;
        var tvy = -150;
        if (ItemDown("up")) { tvx = self.facing * 110; tvy = -430; }
        InstanceVariableSet(th, "kindItem", held);
        InstanceVariableSet(th, "vx", tvx);
        InstanceVariableSet(th, "vy", tvy);
        InstanceVariableSet(th, "spinDir", self.facing);
        InstanceVariableSet(th, "pip", self.id);
    }
    ItemResetState(self.id);
    self.itemHeld = 0; self.itemWroteHeld = 0;
    GlobalSet("heldItem", 0);
    self.throwTimer = 0.22;
    ItemSnd("Spore Throw", 0.8, 0.9);
    return 1;
}

// ------------------------------------------------------------------ the shield
// Returns 1 when a raised shield absorbed the hit. The source is the nearest live foe: a foe in front (or on top of
// Pip) is blocked, one behind is not. With no foe near (a projectile of some other kind) the shield blocks.
function ItemOnHurt(damage) {
    if (self.itemReady != 1) { return 0; }
    if (self.itemHeld == 2 && self.itemShield == 1) {
        var foes = self.itemFoes;
        var cx = self.x; var cy = self.y - 17;
        var bestD = 70; var bestX = cx; var found = 0;
        for (var ni = 0; ni < PgListSize(foes); ni = ni + 1) {
            var nm = foes[ni];
            var total = InstanceNumber(nm);
            for (var fi = 0; fi < total; fi = fi + 1) {
                var foe = InstanceFind(nm, fi);
                if (foe > 0 && InstanceVariableGet(foe, "dead") == 0) {
                    var fx = InstanceGetX(foe);
                    var dd = PointDistance(fx, InstanceGetY(foe) - 12, cx, cy);
                    if (dd < bestD) { bestD = dd; bestX = fx; found = 1; }
                }
            }
        }
        var front = 1;
        if (found == 1 && (bestX - cx) * self.facing < -6) { front = 0; }
        if (front == 1) {
            self.itemBlockT = 0.25;
            self.vx = -self.facing * 140;
            ScriptExecute("Spawn Effect", 0, cx + self.facing * 12, cy);
            ItemSnd("Shield Break", 0.55, 1.6);
            return 1;
        }
    }
    if (self.itemFlowerOpen == 1) { self.itemFlowerOpen = 0; self.itemRising = 0; }
    self.itemDrawing = 0; self.itemDrawT = 0; self.itemSwingT = 0;
    return 0;
}

function ItemMovementScale() {
    if (self.itemShield == 1) { return 0.5; }
    if (self.itemDrawing == 1) { return 0.65; }
    return 1;
}

// ------------------------------------------------------------------ the propeller flower
function ItemAdjustVelocity(dt) {
    var nv = self.vy;
    if (self.itemFlowerOpen == 1) {
        if (self.itemRising == 1) {
            if (nv > -150) { nv = nv + (-150 - nv) * Min(1, dt * 10); }
        } else if (nv > 90) {
            nv = 90;
        }
    }
    return nv;
}

// ------------------------------------------------------------------ the sword
function ItemSwordHit() {
    var f = self.facing;
    var x1 = self.x + 2; var x2 = self.x + 46; var y1 = self.y - 44; var y2 = self.y - 8;
    if (f < 0) { x1 = self.x - 46; x2 = self.x - 2; }
    if (self.itemSwingUp == 1) { x1 = self.x - 24; x2 = self.x + 24; y1 = self.y - 80; y2 = self.y - 30; }
    var n = ItemScan(self.itemFoes, x1, y1, x2, y2, 2, 2, f, self.itemSwingList, self.id);
    if (n > 0) {
        var shk = 1;
        if (GlobalExists("shake")) { shk = GlobalGet("shake"); }
        if (shk == 1) { ViewShake(0, 2, 0.12); }
    }
    return n;
}

// ------------------------------------------------------------------ the bow
// Sets itemAimX / itemAimY (unit vector): the mouse when it moved recently, otherwise 8-way from the held directions.
function ItemComputeAim() {
    var useMouse = 0;
    var ix = 0; var iy = 0;
    if (self.itemHasInput == 1) {
        if (InputMouseRecent() == 1) { useMouse = 1; }
        ix = InputAxisX(); iy = InputAxisY();
    } else {
        if (self.itemMouseT > 0) { useMouse = 1; }
        ix = self.moveDirection;
        if (ItemDown("up")) { iy = -1; }
        if (ItemDown("down")) { iy = 1; }
    }
    var ax = 0; var ay = 0;
    if (ix > 0.4) { ax = 1; }
    if (ix < -0.4) { ax = -1; }
    if (iy > 0.4) { ay = 1; }
    if (iy < -0.4) { ay = -1; }
    if (useMouse == 1 && ax == 0 && ay == 0) {
        var mdx = PointerWorldX() - self.x;
        var mdy = PointerWorldY() - (self.y - 22);
        var ml = PointDistance(0, 0, mdx, mdy);
        if (ml > 6) {
            self.itemAimX = mdx / ml; self.itemAimY = mdy / ml;
            return 1;
        }
    }
    if (ay == 1 && self.grounded == 1) { ay = 0; }
    if (ax == 0 && ay == 0) { ax = self.facing; }
    var nl = 1;
    if (ax != 0 && ay != 0) { nl = 0.7071; }
    self.itemAimX = ax * nl; self.itemAimY = ay * nl;
    return 0;
}

function ItemFireArrow() {
    var pw = self.itemDrawT / 0.45;
    var spd = 300 + 280 * pw;
    var ax = self.itemAimX; var ay = self.itemAimY;
    var ar = CreateInstance("Arrow", self.x + ax * 14, self.y - 22 + ay * 14, 0);
    if (ar > 0) {
        InstanceVariableSet(ar, "vx", ax * spd);
        InstanceVariableSet(ar, "vy", ay * spd);
        InstanceVariableSet(ar, "pip", self.id);
    }
    self.itemArrows = self.itemArrows - 1;
    self.throwTimer = 0.2;
    ItemSnd("Spore Throw", 0.8, 1.25 + pw * 0.3);
    return 1;
}

// ------------------------------------------------------------------ set up
function ItemInit() {
    self.itemReady = 1;
    self.itemHasInput = ResourceExists("Input Kit");
    self.itemHeld = Clamp(GlobalGet("heldItem"), 0, 4);
    self.itemArrows = Clamp(GlobalGet("arrows"), 0, 99);
    self.itemWroteHeld = self.itemHeld; self.itemWroteArrows = self.itemArrows;
    self.itemSwingT = 0; self.itemSwingUp = 0; self.itemCool = 0; self.itemBashCool = 0; self.itemBlockT = 0;
    self.itemShield = 0; self.itemDrawing = 0; self.itemDrawT = 0; self.itemDryT = 0;
    self.itemAimX = 1; self.itemAimY = 0; self.itemMouseT = 0; self.itemPmx = 0; self.itemPmy = 0;
    self.itemFlowerOpen = 0; self.itemRising = 0; self.itemRiseT = 0; self.itemRiseBlocked = 0;
    self.flowerFuel = 1.6;
    self.itemAnimT = 0;
    self.itemFoes = ItemFoeList();
    self.itemSwingList = [];
    self.itemObj = 0;
    var ov = CreateInstance("Held Item", self.x, self.y, 0);
    if (ov > 0) { InstanceVariableSet(ov, "owner", self.id); self.itemObj = ov; }
    return 1;
}

// ------------------------------------------------------------------ every playing frame
function ItemStep(dt) {
    if (self.itemReady != 1) { ItemInit(); }
    if (self.state != 1) { return 0; }
    // The shop or a script changed the globals: adopt them.
    var gHeld = Clamp(GlobalGet("heldItem"), 0, 4);
    if (gHeld != self.itemWroteHeld) {
        ItemResetState(self.id);
        self.itemHeld = gHeld; self.itemWroteHeld = gHeld;
    }
    var gAmmo = Clamp(GlobalGet("arrows"), 0, 99);
    if (gAmmo != self.itemWroteArrows) { self.itemArrows = gAmmo; }

    self.itemCool = Max(0, self.itemCool - dt);
    self.itemBashCool = Max(0, self.itemBashCool - dt);
    self.itemBlockT = Max(0, self.itemBlockT - dt);
    self.itemDryT = Max(0, self.itemDryT - dt);
    self.itemAnimT = self.itemAnimT + dt;
    // Without the Input Kit the mouse counts as "recent" for 1.5 s after it moves.
    var pmx = MouseX; var pmy = MouseY;
    if (Abs(pmx - self.itemPmx) + Abs(pmy - self.itemPmy) > 2) { self.itemMouseT = 1.5; }
    else { self.itemMouseT = Max(0, self.itemMouseT - dt); }
    self.itemPmx = pmx; self.itemPmy = pmy;

    var held = self.itemHeld;
    var act = 1;
    if (self.hub == 1 || RoomValueGet("dialogue", 0) == 1) { act = 0; }
    var swim = self.swimming;
    var useDown = 0; var usePress = 0;
    if (act == 1) { useDown = ItemDown("use"); usePress = ItemPressed("use"); }

    // ---- sword: a 0.28 s arc; the middle of the swing hurts everything in front (or overhead with Up held)
    if (held == 1 && act == 1 && usePress && self.itemSwingT <= 0 && self.itemCool <= 0) {
        self.itemSwingT = 0.28; self.itemSwingUp = 0;
        if (ItemDown("up")) { self.itemSwingUp = 1; }
        self.itemSwingList = [];
        self.throwTimer = 0.2;
        ItemSnd("Spore Throw", 0.7, 1.7);
    }
    if (self.itemSwingT > 0) {
        self.itemSwingT = self.itemSwingT - dt;
        var swe = 0.28 - self.itemSwingT;
        if (swe >= 0.04 && swe <= 0.2) { ItemSwordHit(); }
        if (self.itemSwingT <= 0) { self.itemSwingT = 0; self.itemCool = 0.06; }
    }

    // ---- shield: held up while Use is down; Pip walks slowly, and walking into a foe bashes it
    if (held == 2 && act == 1 && swim == 0) {
        if (useDown) {
            if (self.itemShield == 0) { ItemSnd("Bump", 0.5, 1.4); }
            self.itemShield = 1;
        } else { self.itemShield = 0; }
    } else { self.itemShield = 0; }
    if (self.itemShield == 1) {
        if (self.grounded == 1) { self.vx = Clamp(self.vx, -90, 90); }
        if (self.itemBashCool <= 0 && self.moveDirection != 0 && self.moveDirection == self.facing) {
            var bx1 = self.x + 2; var bx2 = self.x + 26;
            if (self.facing < 0) { bx1 = self.x - 26; bx2 = self.x - 2; }
            var bashList = [];
            var bn = ItemScan(self.itemFoes, bx1, self.y - 36, bx2, self.y - 2, 7, 1, self.facing, bashList, self.id);
            if (bn > 0) { self.itemBashCool = 0.5; self.vx = -self.facing * 70; }
        }
    }

    // ---- bow: hold Use to draw (0.45 s to full), aim 8-way or with the mouse, release to shoot
    if (held == 3 && act == 1) {
        if (self.itemDrawing == 0 && usePress && self.itemCool <= 0) {
            if (self.itemArrows <= 0) {
                self.itemDryT = 0.25; self.itemCool = 0.25;
                ItemSnd("Menu Move", 0.5, 0.5);
            } else {
                self.itemDrawing = 1; self.itemDrawT = 0;
                ItemSnd("Glide Open", 0.4, 1.8);
            }
        }
        if (self.itemDrawing == 1) {
            self.itemDrawT = Min(0.45, self.itemDrawT + dt);
            ItemComputeAim();
            if (Abs(self.itemAimX) > 0.3) {
                self.facing = Sign(self.itemAimX);
                self.image_xscale = self.facing;
            }
            if (self.grounded == 1) { self.vx = Clamp(self.vx, -110, 110); }
            if (!useDown) {
                ItemFireArrow();
                self.itemDrawing = 0; self.itemDrawT = 0; self.itemCool = 0.12;
            }
        }
    } else { self.itemDrawing = 0; self.itemDrawT = 0; }

    // ---- propeller flower: Use in the air opens it (auto parachute); Jump held while open rises while fuel lasts
    var airborne = 0;
    if (self.grounded == 0 && swim == 0) { airborne = 1; }
    if (self.grounded == 1) { self.flowerFuel = Min(1.6, self.flowerFuel + 1.2 * dt); }
    if (held == 4 && act == 1) {
        if (self.itemFlowerOpen == 0 && airborne == 1 && usePress) {
            self.itemFlowerOpen = 1; self.itemRiseT = 0; self.itemRiseBlocked = 0;
            ItemSnd("Glide Open", 0.7, 1.4);
        }
        if (self.itemFlowerOpen == 1 && (!useDown || airborne == 0)) {
            self.itemFlowerOpen = 0; self.itemRising = 0;
        }
    } else if (self.itemFlowerOpen == 1) {
        self.itemFlowerOpen = 0; self.itemRising = 0;
    }
    if (self.itemFlowerOpen == 1) {
        var jumpDown = ItemDown("jump");
        if (!jumpDown) { self.itemRiseBlocked = 0; }
        if (jumpDown && self.flowerFuel > 0 && self.itemRiseBlocked == 0) {
            self.itemRising = 1;
            self.flowerFuel = Max(0, self.flowerFuel - dt);
            self.itemRiseT = self.itemRiseT + dt;
            // A ceiling bump (Explorer zeroes vy) cancels the rise until Jump is released.
            if (self.itemRiseT > 0.3 && self.vy > -40) {
                self.itemRiseBlocked = 1; self.itemRising = 0; self.itemRiseT = 0;
            }
        } else { self.itemRising = 0; self.itemRiseT = 0; }
        self.vy = ItemAdjustVelocity(dt);
    } else { self.itemRising = 0; self.itemRiseT = 0; self.itemRiseBlocked = 0; }

    GlobalSet("heldItem", self.itemHeld); self.itemWroteHeld = self.itemHeld;
    GlobalSet("arrows", self.itemArrows); self.itemWroteArrows = self.itemArrows;
    return 0;
}

// ------------------------------------------------------------------ the HUD (Explorer DrawGui, 1280x720 design space)
function ItemDrawHud() {
    var held = self.itemHeld;
    if (held == 0) { return 0; }
    var sw = WindowGetWidth(); var sh = WindowGetHeight();
    var sx = sw / 1280; var sy = sh / 720; var s = Min(sx, sy);
    DrawSetFont("Consolas");
    DrawSetAlpha(0.85); DrawSetColorRgb(29, 48, 67);
    DrawRoundRect(724 * sx, 17 * sy, 760 * sx, 53 * sy, 6 * s, false);
    DrawSetAlpha(1); DrawSetColorRgb(255, 231, 160);
    DrawRoundRect(724 * sx, 17 * sy, 760 * sx, 53 * sy, 6 * s, true);
    var spr = ItemSpriteName(held);
    if (ResourceExists(spr)) {
        DrawSpritePart(StringJoin2(StringJoin2("Assets/Sprites/", spr), ".image.json"), 0, 0, 0, 1, 1,
            727 * sx, 20 * sy, 30 * s, 30 * s, 1);
    } else {
        ItemDrawFallback(held, 742 * sx, 35 * sy, 0, 1.6 * s, 1);
    }
    if (held == 3) {
        DrawSetColorRgb(248, 252, 244);
        if (self.itemArrows <= 0) { DrawSetColorRgb(255, 140, 120); }
        DrawTextScaled(768 * sx, 24 * sy, StringJoin2("x", StringOf(self.itemArrows)), 19 * s);
    }
    if (held == 4) {
        var fuel = Clamp(self.flowerFuel / 1.6, 0, 1);
        DrawSetAlpha(0.85); DrawSetColorRgb(29, 48, 67);
        DrawRectangle(768 * sx, 28 * sy, 848 * sx, 42 * sy);
        DrawSetAlpha(1); DrawSetColorRgb(244, 126, 160);
        if (fuel < 0.25) { DrawSetColorRgb(255, 140, 120); }
        DrawRectangle(770 * sx, 30 * sy, (770 + 76 * fuel) * sx, 40 * sy);
        DrawSetColorRgb(255, 231, 160);
        DrawRectangle(768 * sx, 28 * sy, 848 * sx, 29 * sy);
        DrawRectangle(768 * sx, 41 * sy, 848 * sx, 42 * sy);
    }
    DrawSetAlpha(1);
    return 0;
}
""".replace("FOE_LIST", FOE_LIST)


# ====================================================================== Held Item (overlay on Pip)
HELD_CREATE = r"""
// Held Item: the picture of whatever Pip holds (Item Kit). ItemInit sets `owner` right after creating it. Everything is
// drawn in the Draw event from Pip's item variables, so it also shows while the game is paused.
owner = 0;
"""

HELD_STEP = r"""
if (owner > 0 && !InstanceExists(owner)) { InstanceDestroy(id); }
"""

HELD_DRAW = r"""
var pip = owner;
if (pip <= 0) { return; }
if (!InstanceExists(pip)) { return; }
var held = InstanceVariableGet(pip, "itemHeld");
if (held == 0) { return; }
var px = Round(InstanceGetX(pip)); var py = Round(InstanceGetY(pip));
var f = InstanceVariableGet(pip, "facing");
if (f == 0) { f = 1; }
var al = 1;
var inv = InstanceVariableGet(pip, "invulnerable");
if (inv > 0 && Floor(inv * 12) % 2 == 0) { al = 0.35; }
var clock = InstanceVariableGet(pip, "itemAnimT");
if (held == 1) {
    // Sword: rests on the shoulder; a swing sweeps -95 to +75 degrees (or -175 to -15 overhead) in front.
    var swingT = InstanceVariableGet(pip, "itemSwingT");
    var hx = px + f * 6; var hy = py - 18;
    var th = -62; var fr = 0;
    if (swingT > 0) {
        var p = Clamp((0.28 - swingT) / 0.28, 0, 1);
        th = Lerp(-95, 75, p);
        if (InstanceVariableGet(pip, "itemSwingUp") == 1) { th = Lerp(-175, -15, p); }
        fr = Min(3, Floor(p * 4));
    }
    var swordAng = th; var swordFlip = 1;
    if (f < 0) { swordAng = 180 - th; swordFlip = -1; }
    if (ResourceExists("Held Sword")) {
        DrawSpriteScaled("Assets/Sprites/Held Sword.image.json", hx, hy, fr, 1, swordFlip, swordAng, al);
    } else {
        ItemDrawFallback(1, hx, hy, swordAng, 1, al);
    }
    if (swingT > 0) {
        for (var k = 1; k <= 4; k = k + 1) {
            var tha = th - k * 13;
            if (f < 0) { tha = 180 - (th - k * 13); }
            DrawSetAlpha(0.5 - k * 0.1); DrawSetColorRgb(255, 255, 255);
            DrawCircle(hx + Cos(tha) * 30, hy + Sin(tha) * 30, 2.6 - k * 0.4, false);
        }
        DrawSetAlpha(1);
    }
}
if (held == 2) {
    // Shield: on Pip's back, or raised in front while Use is held; a block flashes.
    var up = InstanceVariableGet(pip, "itemShield");
    var shx = px - f * 6; var shf = 0;
    if (up == 1) { shx = px + f * 11; shf = 1; }
    if (ResourceExists("Held Shield")) {
        DrawSpriteScaled("Assets/Sprites/Held Shield.image.json", shx, py - 17, shf, f, 1, 0, al);
    } else {
        ItemDrawFallback(2, shx, py - 17, 0, 1.3, al);
    }
    var blockT = InstanceVariableGet(pip, "itemBlockT");
    if (blockT > 0) {
        DrawSetAlpha(Clamp(blockT * 3, 0, 0.8)); DrawSetColorRgb(255, 255, 255);
        DrawCircle(shx + f * 5, py - 17, 12 - blockT * 14, true);
        DrawSetAlpha(1);
    }
}
if (held == 3) {
    // Bow: carried forward; while drawing it turns to the aim, shows the nocked arrow and a dotted aim line.
    var drawing = InstanceVariableGet(pip, "itemDrawing");
    var drawT = InstanceVariableGet(pip, "itemDrawT");
    var bowX = px + f * 5; var bowY = py - 18;
    var bowAng = 0; var bowFlip = 1; var bowFr = 0;
    if (f < 0) { bowAng = 180; bowFlip = -1; }
    if (drawing == 1) {
        var aimX = InstanceVariableGet(pip, "itemAimX"); var aimY = InstanceVariableGet(pip, "itemAimY");
        bowAng = ArcTan2(aimY, aimX);
        bowFlip = 1; if (aimX < 0) { bowFlip = -1; }
        bowFr = 1 + Min(2, Floor(drawT / 0.45 * 2.999));
        bowX = px + aimX * 5; bowY = py - 20 + aimY * 5;
    }
    if (ResourceExists("Held Bow")) {
        DrawSpriteScaled("Assets/Sprites/Held Bow.image.json", bowX, bowY, bowFr, 1, bowFlip, bowAng, al);
    } else {
        ItemDrawFallback(3, bowX, bowY, bowAng, 1.2, al);
    }
    if (drawing == 1) {
        var pull = drawT / 0.45;
        ItemDrawArrow(bowX + aimX * (9 - pull * 5), bowY + aimY * (9 - pull * 5), bowAng, al);
        if (drawT >= 0.1) {
            DrawSetColorRgb(255, 245, 200);
            for (var dk = 1; dk <= 4; dk = dk + 1) {
                DrawSetAlpha((0.7 - dk * 0.13) * Min(1, pull + 0.3));
                DrawCircle(bowX + aimX * (14 + dk * 13), bowY + aimY * (14 + dk * 13), 1.5, false);
            }
            DrawSetAlpha(1);
        }
    }
    if (InstanceVariableGet(pip, "itemDryT") > 0) {
        DrawSetAlpha(0.8); DrawSetColorRgb(255, 140, 120);
        DrawCircle(px + f * 16, py - 38, 2 + Sin(clock * 900), false);
        DrawSetAlpha(1);
    }
}
if (held == 4) {
    // Propeller Flower: carried closed; open it spins above Pip's head.
    var open = InstanceVariableGet(pip, "itemFlowerOpen");
    if (open == 1) {
        var flX = px; var flY = py - 33 + Sin(clock * 500);
        if (ResourceExists("Held Flower")) {
            DrawSpriteScaled("Assets/Sprites/Held Flower.image.json", flX, flY, 1 + Floor(clock * 20) % 4, 1, 1, 0, al);
        } else {
            ItemDrawFallback(4, flX, flY + 4, clock * 1100, 1.2, al);
        }
    } else {
        var cx = px + f * 8; var cy = py - 12;
        if (ResourceExists("Held Flower")) {
            DrawSpriteScaled("Assets/Sprites/Held Flower.image.json", cx, cy, 0, 1, 1, 0, al);
        } else {
            ItemDrawFallback(4, cx, cy, 0, 1, al);
        }
    }
}
DrawSetAlpha(1);
"""

# ====================================================================== pickups
PICKUP_CREATE = r"""
// KINDNAME pickup (Item Kit). Placed in a level it bobs where it stands; Pip takes it by touch (swapping with the item
// he holds). A tossed, dropped or thrown one (mode 1) falls to the floor first and cannot be taken for `delay` seconds.
// `fresh` is 1 for a level or shop pickup (a Bow then brings arrows), 0 for one Pip let go of.
kindItem = KINDNUM; mode = 0; vx = 0; vy = 0; delay = 0; fresh = 1;
homeY = y; bob = (x * 7) % 360; animT = (x * 3) % 10;
"""

PICKUP_STEP = r"""
if (RoomValueGet("frozen", 0) == 1) { return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
delay = Max(0, delay - dt);
animT = animT + dt;
bob = (bob + dt * 160) % 360;
if (mode == 1) {
    vy = Min(vy + 900 * dt, 520);
    var wantX = x + vx * dt;
    x = TileMoveX(x, y, vx * dt);
    if (Abs(x - wantX) > 0.01) { vx = 0; }
    vx = vx * (1 - Min(1, dt * 3));
    var wantY = y + vy * dt;
    y = TileMoveY(x, y, vy * dt);
    if (Abs(y - wantY) > 0.01) {
        if (vy > 0) { mode = 0; vx = 0; vy = 0; homeY = y - 3; }
        else { vy = 0; }
    }
    if (y > RoomGetHeight() + 40) { InstanceDestroy(id); return; }
} else {
    y = homeY + Sin(bob) * 3;
}
if (delay <= 0) {
    var pip = InstanceFind("Explorer", 0);
    if (pip > 0) {
        if (InstanceVariableGet(pip, "state") == 1 && InstanceVariableGet(pip, "hub") == 0
            && PointDistance(x, y, InstanceGetX(pip), InstanceGetY(pip) - 17) < 22) {
            if (ItemGive(pip, kindItem, fresh) == 1) { InstanceDestroy(id); return; }
        }
    }
}
"""

PICKUP_DRAW = r"""
var fr = Floor(animT * 2.5) % 2;
var al = 1;
if (delay > 0 && Floor(delay * 12) % 2 == 0) { al = 0.45; }
DrawSetAlpha((0.16 + 0.07 * Sin(bob)) * al); DrawSetColorRgb(255, 240, 180);
DrawCircle(x, y, 11, false);
DrawSetAlpha(1);
if (ResourceExists("SPRITENAME")) {
    DrawSpriteScaled("Assets/Sprites/SPRITENAME.image.json", x, y, fr, 1, 1, 0, al);
} else {
    ItemDrawFallback(kindItem, x, y, animT * 300, 1, al);
}
DrawSetAlpha(1);
"""

# ====================================================================== thrown item
THROWN_CREATE = r"""
// Thrown Item: the held item in flight (Item Kit ItemThrow sets kindItem, vx, vy, spinDir, pip after creating it). It hurts
// the first foe it touches (2 damage, hit kind 4: a sword or a shield can slice), then lands and lies as a pickup again
// after a 0.6 s grace so Pip does not catch it at once.
kindItem = 1; vx = 0; vy = 0; spinDir = 1; pip = 0; life = 6; hitDone = 0; spinT = 0;
foes = ItemFoeList(); hitList = [];
"""

THROWN_STEP = r"""
if (RoomValueGet("frozen", 0) == 1) { return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
life = life - dt; spinT = spinT + dt;
var grav = 760;
if (kindItem == 2) { grav = 480; }
if (kindItem == 4) { grav = 300; }
if (RoomValueGet("underwater", 0) == 1) { grav = grav * 0.4; vx = vx * (1 - Min(1, dt * 2)); }
vy = Min(vy + grav * dt, 520);
if (kindItem == 4) { vy = Min(vy, 150); }
var wantX = x + vx * dt;
x = TileMoveX(x, y, vx * dt);
if (Abs(x - wantX) > 0.01) { vx = -vx * 0.25; }
var wantY = y + vy * dt;
y = TileMoveY(x, y, vy * dt);
if (Abs(y - wantY) > 0.01) {
    if (vy > 0) {
        ItemSpawnPickup(kindItem, x, y - 4, 0, 0, 0.6, 0);
        InstanceDestroy(id); return;
    }
    vy = 0;
}
if (hitDone == 0) {
    var dirX = Sign(vx);
    if (dirX == 0) { dirX = spinDir; }
    if (ItemScan(foes, x - 8, y - 8, x + 8, y + 8, 4, 2, dirX, hitList, pip) > 0) {
        hitDone = 1; vx = -dirX * 70; vy = -170;
        var shk = 1;
        if (GlobalExists("shake")) { shk = GlobalGet("shake"); }
        if (shk == 1) { ViewShake(0, 2, 0.1); }
    }
}
if (life <= 0 || y > RoomGetHeight() + 40 || x < -40 || x > RoomGetWidth() + 40) { InstanceDestroy(id); }
"""

THROWN_DRAW = r"""
var names = ["", "Thrown Sword", "Thrown Shield", "Thrown Bow", "Thrown Flower"];
var nm = names[kindItem];
if (ResourceExists(nm)) {
    DrawSpriteScaled(StringJoin2(StringJoin2("Assets/Sprites/", nm), ".image.json"), x, y, Floor(spinT * 18) % 4, spinDir, 1, 0, 1);
} else {
    ItemDrawFallback(kindItem, x, y, spinT * 720 * spinDir, 1.1, 1);
}
DrawSetAlpha(1);
"""

# ====================================================================== arrow
ARROW_CREATE = r"""
// Arrow: an arrow in flight (Item Kit ItemFireArrow sets vx, vy, pip after creating it). It arcs under gravity, hurts the
// first foe it touches (2 damage, hit kind 3), or sticks into a wall or the floor and fades.
vx = 0; vy = 0; pip = 0; life = 4; stuck = 0; stuckT = 0; dmg = 2;
foes = ItemFoeList(); hitList = [];
"""

ARROW_STEP = r"""
if (RoomValueGet("frozen", 0) == 1) { return; }
var dt = Clamp(DeltaTime, 0, 0.05);
if (dt <= 0) { return; }
if (stuck == 1) {
    stuckT = stuckT + dt;
    if (stuckT > 1.4) { InstanceDestroy(id); }
    return;
}
life = life - dt;
var grav = 420;
if (RoomValueGet("underwater", 0) == 1) { grav = 110; vx = vx * (1 - Min(1, dt * 1.4)); vy = vy * (1 - Min(1, dt * 1.4)); }
vy = Min(vy + grav * dt, 640);
var wantX = x + vx * dt;
x = TileMoveX(x, y, vx * dt);
var blocked = 0;
if (Abs(x - wantX) > 0.01) { blocked = 1; }
var wantY = y + vy * dt;
y = TileMoveY(x, y, vy * dt);
if (Abs(y - wantY) > 0.01) { blocked = 1; }
if (blocked == 1) {
    // Stick in: nudge a little way along the flight and stop.
    var tilt = ArcTan2(vy, vx);
    x = x + Cos(tilt) * 3; y = y + Sin(tilt) * 3;
    stuck = 1; stuckT = 0; image_angle = tilt;
    ItemSnd("Bump", 0.35, 1.9);
    return;
}
if (ItemScan(foes, x - 5, y - 5, x + 5, y + 5, 3, dmg, Sign(vx), hitList, pip) > 0) { InstanceDestroy(id); return; }
if (life <= 0 || y > RoomGetHeight() + 40 || x < -40 || x > RoomGetWidth() + 40) { InstanceDestroy(id); }
"""

ARROW_DRAW = r"""
var al = 1;
if (stuck == 1) { al = Clamp(1 - Max(0, stuckT - 0.6) / 0.8, 0, 1); }
ItemDrawArrow(x, y, ArcTan2(vy, vx), al);
if (stuck == 1) { ItemDrawArrow(x, y, image_angle, al); }
DrawSetAlpha(1);
"""


def build_objects():
    write_script("Item Kit", ITEM_KIT)
    write_object("Held Item", "", [("Create", HELD_CREATE), ("Step", HELD_STEP), ("Draw", HELD_DRAW)])
    for kind, (obj, pickup_sprite, _held, _thrown, label) in KINDS.items():
        create = PICKUP_CREATE.replace("KINDNAME", obj).replace("KINDNUM", str(kind))
        draw = PICKUP_DRAW.replace("SPRITENAME", pickup_sprite)
        write_object(obj, sprite_or_carrier(pickup_sprite),
                     [("Create", create), ("Step", PICKUP_STEP), ("Draw", draw)])
    write_object("Thrown Item", "Spore Shot Sprite",
                 [("Create", THROWN_CREATE), ("Step", THROWN_STEP), ("Draw", THROWN_DRAW)])
    write_object("Arrow", "Spore Shot Sprite",
                 [("Create", ARROW_CREATE), ("Step", ARROW_STEP), ("Draw", ARROW_DRAW)])


def check_pgsl():
    """Cheap static lint of the PGSL written here (no engine run): banned syntax and balanced brackets."""
    sources = {"Item Kit": ITEM_KIT, "Held Item Create": HELD_CREATE, "Held Item Step": HELD_STEP,
               "Held Item Draw": HELD_DRAW, "Pickup Create": PICKUP_CREATE, "Pickup Step": PICKUP_STEP,
               "Pickup Draw": PICKUP_DRAW, "Thrown Create": THROWN_CREATE, "Thrown Step": THROWN_STEP,
               "Thrown Draw": THROWN_DRAW, "Arrow Create": ARROW_CREATE, "Arrow Step": ARROW_STEP,
               "Arrow Draw": ARROW_DRAW}
    for label, text in sources.items():
        code = re.sub(r"//[^\n]*", "", text)
        code = re.sub(r'"[^"\n]*"', '""', code)
        for bad in ("++", "--", "+=", "-=", "*=", "/="):
            assert bad not in code, "%s: %s is not used in this codebase's PGSL" % (label, bad)
        assert not re.search(r"\bvar\s+(x|y|id|speed|direction|depth|alpha|visible|solid|image_\w+)\b", code), label
        for open_c, close_c in ("()", "[]", "{}"):
            assert code.count(open_c) == code.count(close_c), "%s: unbalanced %s%s" % (label, open_c, close_c)
    names = re.findall(r"^function (\w+)\(", ITEM_KIT, re.M)
    assert len(names) == len(set(names)), "duplicate library function names"
    assert all(n.startswith("Item") for n in names), "library functions must start with Item"


def warn_enemy_list():
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "hero_gen.py")
    try:
        with open(path, encoding="utf-8") as handle:
            text = handle.read()
        found = re.search(r"ENEMY_NAMES = \[(.*?)\]", text, re.S)
        theirs = re.findall(r'"([^"]+)"', found.group(1)) if found else []
        if theirs and theirs != ENEMY_NAMES:
            print("WARNING: items_gen.ENEMY_NAMES differs from hero_gen.ENEMY_NAMES:", theirs)
    except OSError:
        pass


def main():
    check_pgsl()
    warn_enemy_list()
    build_objects()
    print("Item content written to", ASSETS)


if __name__ == "__main__":
    main()
