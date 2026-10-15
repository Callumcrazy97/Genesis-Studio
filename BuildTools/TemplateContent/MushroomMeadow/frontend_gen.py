"""Mushroom Meadow front end generator: Intro, Main Menu and Credits rooms, their objects,
the Main Menu UI resource, menu/cinematic particles, and the save/options Scripts.

Re-runnable and deterministic. Run from anywhere:
    python BuildTools/TemplateContent/MushroomMeadow/frontend_gen.py
Writes into Source/Genesis.Application.Core/Projects/Templates/Assets/TwoDShowcase/Assets.

What it writes
  Rooms/Intro.room.json            pre-title cinematic (Intro Director)
  Rooms/Main Menu.room.json        title + menu + save slots + options (Menu Controller object)
  Rooms/Credits.room.json          ending cinematic + scrolling credits (Credits Director)
  Objects/Intro Director, Menu Controller, Credits Director (+ their PGSL events)
  Scripts/Game Save, Game Load, New Game, Apply Options, Cinematic Kit (library functions)
  User Interfaces/Main Menu UI.ui.json
  Particles/Menu Fireflies, Menu Sparkles, Festival Lanterns
"""
import hashlib
import json
import os

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
ASSETS = os.path.join(ROOT, "Source", "Genesis.Application.Core", "Projects", "Templates",
                      "Assets", "TwoDShowcase", "Assets")
STAMP = "2026-10-11T00:00:00Z"


def guid(name):
    return hashlib.md5(("mushroom-meadow-frontend:" + name).encode("utf-8")).hexdigest()


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


def pgsl_list(items):
    return "[" + ", ".join(json.dumps(i) if isinstance(i, str) else repr(i) for i in items) + "]"


# ---------------------------------------------------------------- shared names
SPR = "Assets/Sprites/"
AUD = "Assets/Audio/"
INTRO_PANELS = [SPR + "Intro Panel %d.image.json" % i for i in range(1, 6)]
ENDING_PANELS = [SPR + "Ending Panel %d.image.json" % i for i in range(1, 4)]
TITLE_LOGO = SPR + "Title Logo.image.json"
MENU_UI = "Assets/User Interfaces/Main Menu UI.ui.json"
SND_MOVE = AUD + "Menu Move.audio.json"
SND_SELECT = AUD + "Menu Select.audio.json"
SND_BACK = AUD + "Menu Back.audio.json"
SND_DENY = AUD + "Shop Deny.audio.json"
SND_CHIME = AUD + "Save Chime.audio.json"

# Save slot text: "mm2,<field 1>,<field 2>,...". Game Save and Game Load share this order; the
# Main Menu reads a few fields (index = position + 2, because "mm2" is field 1).
SAVE_FIELDS = (["lives", "coins", "hearts", "maxHearts", "power"]
               + ["clear%d" % i for i in range(1, 6)] + ["medals%d" % i for i in range(1, 6)]
               + ["glowspores", "mapNode", "gameComplete", "introSeen", "mapRevealed"]
               + ["hubTalk%d" % i for i in range(1, 6)] + [
                  "musicVol", "sfxVol", "crt", "shake"]
               # Round 4 game state, appended so older saves (which end before these) still load.
               + ["heldItem", "arrows", "brutality", "killsStomp", "killsBlade", "killsArrow",
                  "killsGib", "killsSliced", "fearedBy", "taintStage"])
OPTION_FIELDS = ["musicVol", "sfxVol", "crt", "shake"]
STATE_FIELDS = ["heldItem", "arrows", "brutality", "killsStomp", "killsBlade", "killsArrow",
                "killsGib", "killsSliced", "fearedBy", "taintStage"]
# Options slot text: "mmopt2,<field>,<field>,..." in this order ("mmopt1" saves hold the first four).
OPTIONS_SLOT_FIELDS = ["musicVol", "sfxVol", "crt", "shake", "tips", "gore", "sprintModeKb", "sprintModePad",
                       "windowMode", "vsync", "fpsCap", "masterVol", "ambVol", "winSize"]


def field(name):
    return SAVE_FIELDS.index(name) + 2


MAP_NODES = ["Hearthollow", "Mushroom Meadow", "Rootdeep Caverns", "Tidepool Grotto",
             "Windswept Canopy", "Duskmantle Hollow"]


# ---------------------------------------------------------------- scripts
GAME_SAVE = """// Game Save: ScriptExecute("Game Save") writes every campaign global to the save slot
// "Slot <n>" for n = GlobalGet("slot") (1-3). ScriptExecute("Game Save", 1) also plays the
// Save Chime. Returns true when the slot was written; does nothing outside a campaign slot.
// Text format: "mm2,<value>,<value>,..." in the order of the list below (Game Load reads the
// same list; the Main Menu reads lives, coins, glowspores, mapNode and gameComplete).
var gsSlot = GlobalGet("slot");
if (gsSlot < 1 || gsSlot > 3) { return 0; }
var gsNames = %(fields)s;
var gsText = "mm2";
for (var gsI = 0; gsI < PgListSize(gsNames); gsI += 1) {
    gsText = gsText + "," + StringOf(GlobalGet(gsNames[gsI]));
}
var gsOk = SaveSlotWrite("Slot " + StringOf(gsSlot), gsText);
if (gsOk && argument0 == 1) { PlaySound("%(chime)s", 0.6, 1, false); }
return gsOk;
""" % {"fields": pgsl_list(SAVE_FIELDS), "chime": SND_CHIME}

GAME_LOAD = """// Game Load: ScriptExecute("Game Load", slot) restores the campaign globals saved in
// "Slot <slot>" (1-3) and starts campaign mode (campaign = 1). Options (music/sfx volume,
// CRT, shake) are player preferences kept in the "Options" slot, so a save does not change
// them. Returns 1 when the slot held a Mushroom Meadow save, 0 when it was empty.
var glSlot = argument0;
if (glSlot < 1 || glSlot > 3) { glSlot = GlobalGet("slot"); }
if (glSlot < 1 || glSlot > 3) { return 0; }
var glText = SaveSlotRead("Slot " + StringOf(glSlot));
if (StringSplitPart(glText, ",", 1) != "mm2") { return 0; }
var glNames = %(fields)s;
var glKeep = %(options)s;
// Round 4 state starts from defaults, so an older save (without these fields) or a save loaded
// after another game in the same run never inherits the other game's items or reputation.
var glState = %(state)s;
for (var glD = 0; glD < PgListSize(glState); glD = glD + 1) { GlobalSet(glState[glD], 0); }
for (var glI = 0; glI < PgListSize(glNames); glI += 1) {
    var glName = glNames[glI];
    var glPart = StringSplitPart(glText, ",", glI + 2);
    if (PgListFind(glKeep, glName) < 0 && glPart != "") { GlobalSet(glName, Real(glPart)); }
}
// A save from an older layout or a hand edit still loads into a playable state.
if (GlobalGet("maxHearts") < 3) { GlobalSet("maxHearts", 3); }
if (GlobalGet("maxHearts") > 6) { GlobalSet("maxHearts", 6); }
GlobalSet("hearts", GlobalGet("maxHearts"));
if (GlobalGet("lives") < 1) { GlobalSet("lives", 5); }
GlobalSet("mapNode", Clamp(GlobalGet("mapNode"), 0, 5));
GlobalSet("glowspores", Clamp(GlobalGet("glowspores"), 0, 5));
GlobalSet("heldItem", Clamp(Round(GlobalGet("heldItem")), 0, 4));
GlobalSet("arrows", Clamp(Round(GlobalGet("arrows")), 0, 99));
GlobalSet("brutality", Clamp(GlobalGet("brutality"), 0, 100));
GlobalSet("taintStage", Clamp(Round(GlobalGet("taintStage")), 0, 3));
GlobalSet("slot", glSlot);
GlobalSet("campaign", 1);
GlobalSet("returnFromLevel", 0);
return 1;
""" % {"fields": pgsl_list(SAVE_FIELDS), "options": pgsl_list(OPTION_FIELDS), "state": pgsl_list(STATE_FIELDS)}

NEW_GAME = """// New Game: ScriptExecute("New Game", slot) starts a fresh campaign in save slot 1-3:
// 5 lives, 3 hearts, no coins, no power-up, nothing cleared, Pip at Hearthollow (map node 0).
// It writes the slot at once (so Continue finds it) and sets campaign = 1.
var ngSlot = argument0;
if (ngSlot < 1 || ngSlot > 3) { ngSlot = 1; }
GlobalSet("slot", ngSlot);
GlobalSet("campaign", 1);
GlobalSet("lives", 5);
GlobalSet("coins", 0);
GlobalSet("hearts", 3);
GlobalSet("maxHearts", 3);
GlobalSet("power", 0);
for (var ngI = 1; ngI <= 5; ngI += 1) {
    GlobalSet("clear" + StringOf(ngI), 0);
    GlobalSet("medals" + StringOf(ngI), 0);
}
// Named once in quotes so the project check knows every global the campaign uses.
GlobalSet("clear1", 0); GlobalSet("medals1", 0);
GlobalSet("glowspores", 0);
GlobalSet("mapNode", 0);
GlobalSet("returnFromLevel", 0);
GlobalSet("gameComplete", 0);
// Hearthollow/Overworld bookkeeping (HUB): which map unlocks were already shown, and how far
// each villager's repeat-visit dialogue has got.
GlobalSet("mapRevealed", 0);
for (var ngT = 1; ngT <= 5; ngT += 1) { GlobalSet("hubTalk" + StringOf(ngT), 0); }
GlobalSet("hubTalk1", 0);
// Round 4 state (held item, arrows, reputation and kill counters) starts clean in a new game.
GlobalSet("heldItem", 0); GlobalSet("arrows", 0); GlobalSet("brutality", 0);
GlobalSet("killsStomp", 0); GlobalSet("killsBlade", 0); GlobalSet("killsArrow", 0);
GlobalSet("killsGib", 0); GlobalSet("killsSliced", 0); GlobalSet("fearedBy", 0);
GlobalSet("taintStage", 0);
GlobalSet("introSeen", 1);
ScriptExecute("Game Save");
return 1;
"""

APPLY_OPTIONS = """// Apply Options: ScriptExecute("Apply Options") applies the player's options; the first call
// of a run loads them from the "Options" save slot (defaults: music 0.8, sfx 0.9, CRT off,
// screen shake on). ScriptExecute("Apply Options", 1) also writes them back to that slot.
//   musicVol -> "music" bus, sfxVol -> "sfx" and "ambient" buses,
//   crt      -> the SNES Look post effect's Scanlines parameter (rooms that run SNES Look),
//   shake    -> read by gameplay objects: if (GlobalGet("shake") == 1) { ...shake the view... }
// Every room's controller can call it in Create; Intro and Main Menu do.
// Round 4 options (all global numbers; the Options Kit edits them):
//   tips (1) gore (1) shake (1) crt (0) sprintModeKb (0 hold / 1 toggle) sprintModePad (0)
//   masterVol (1) musicVol (0.8) sfxVol (0.9) ambVol (= sfxVol of an old save, else 0.9)
//   windowMode (-1 = leave as launched, else 0 windowed / 1 borderless / 2 fullscreen), winSize
//   (-1 or 0..3 = 1280x720, 1600x900, 1920x1080, 2560x1440; windowed only), vsync (-1 = room default,
//   0/1), fpsCap (-1 = the rooms' frame rate, 0 = unlimited, else a cap). A video value that is -1 is
//   never applied, so rooms keep their own settings until the player chooses; once chosen, the
//   game's choice outranks the rooms' (WindowSetVSync / GameSetMaxFps) for the rest of the run.
// The key bindings are kept by the Input Kit in its own save slot ("Input Bindings").
if (GlobalGet("optionsLoaded") == 0) {
    GlobalSet("optionsLoaded", 1);
    GlobalSet("musicVol", 0.8);
    GlobalSet("sfxVol", 0.9);
    GlobalSet("crt", 0);
    GlobalSet("shake", 1);
    GlobalSet("tips", 1);
    GlobalSet("gore", 1);
    GlobalSet("sprintModeKb", 0);
    GlobalSet("sprintModePad", 0);
    GlobalSet("windowMode", -1);
    GlobalSet("vsync", -1);
    GlobalSet("fpsCap", -1);
    GlobalSet("masterVol", 1);
    GlobalSet("ambVol", 0.9);
    GlobalSet("winSize", -1);
    GlobalSet("winApplied", -1);
    GlobalSet("sizeApplied", -1);
    GlobalSet("vsyncApplied", -1);
    GlobalSet("fpsApplied", -1);
    var aoText = SaveSlotRead("Options");
    var aoHead = StringSplitPart(aoText, ",", 1);
    if (aoHead == "mmopt1" || aoHead == "mmopt2") {
        var aoNames = @OPTION_NAMES@;
        for (var aoI = 0; aoI < PgListSize(aoNames); aoI = aoI + 1) {
            var aoPart = StringSplitPart(aoText, ",", aoI + 2);
            if (aoPart != "") { GlobalSet(aoNames[aoI], Real(aoPart)); }
        }
        // A saved layout from before the ambience slider keeps its old ambience volume (= sound effects).
        if (aoHead == "mmopt1") { GlobalSet("ambVol", GlobalGet("sfxVol")); }
    }
    GlobalSet("musicVol", Clamp(GlobalGet("musicVol"), 0, 1));
    GlobalSet("sfxVol", Clamp(GlobalGet("sfxVol"), 0, 1));
    GlobalSet("ambVol", Clamp(GlobalGet("ambVol"), 0, 1));
    GlobalSet("masterVol", Clamp(GlobalGet("masterVol"), 0, 1));
    GlobalSet("crt", Clamp(GlobalGet("crt"), 0, 1));
    GlobalSet("shake", Clamp(GlobalGet("shake"), 0, 1));
    GlobalSet("tips", Clamp(GlobalGet("tips"), 0, 1));
    GlobalSet("gore", Clamp(GlobalGet("gore"), 0, 1));
    GlobalSet("sprintModeKb", Clamp(GlobalGet("sprintModeKb"), 0, 1));
    GlobalSet("sprintModePad", Clamp(GlobalGet("sprintModePad"), 0, 1));
    GlobalSet("windowMode", Clamp(Round(GlobalGet("windowMode")), -1, 2));
    GlobalSet("winSize", Clamp(Round(GlobalGet("winSize")), -1, 3));
    GlobalSet("vsync", Clamp(Round(GlobalGet("vsync")), -1, 1));
    GlobalSet("fpsCap", Clamp(Round(GlobalGet("fpsCap")), -1, 1000));
    // The Input Kit reloads its bindings (guarded: a project without it still runs).
    if (ResourceExists("Input Kit")) { InputLoadBindings(); }
}
SetMasterVolume(GlobalGet("masterVol"));
SetBusVolume("music", GlobalGet("musicVol"));
SetBusVolume("sfx", GlobalGet("sfxVol"));
SetBusVolume("ambient", GlobalGet("ambVol"));
PostEffectSetParameter("SNES Look", "Scanlines", GlobalGet("crt"));

// Video: only values the player chose (>= 0) are applied, and only when they changed.
var aoModes = ["windowed", "borderless", "fullscreen"];
var aoMode = GlobalGet("windowMode");
if (aoMode >= 0 && aoMode != GlobalGet("winApplied")) {
    GlobalSet("winApplied", aoMode);
    GlobalSet("sizeApplied", -1);
    if (WindowGetMode() != aoModes[aoMode]) { WindowSetMode(aoModes[aoMode]); }
}
var aoSize = GlobalGet("winSize");
if (aoSize >= 0 && aoSize != GlobalGet("sizeApplied") && WindowGetMode() == "windowed") {
    GlobalSet("sizeApplied", aoSize);
    var aoWidths = [1280, 1600, 1920, 2560];
    var aoHeights = [720, 900, 1080, 1440];
    if (Abs(WindowGetWidth() - aoWidths[aoSize]) > 2 || Abs(WindowGetHeight() - aoHeights[aoSize]) > 2) {
        WindowSetSize(aoWidths[aoSize], aoHeights[aoSize]);
    }
}
var aoFps = GlobalGet("fpsCap");
if (aoFps != GlobalGet("fpsApplied")) {
    if (aoFps >= 0) { GameSetMaxFps(aoFps); }
    else if (GlobalGet("fpsApplied") >= 0) { GameUseRoomFps(); GlobalSet("vsyncApplied", -1); }
    GlobalSet("fpsApplied", aoFps);
}
var aoVsync = GlobalGet("vsync");
if (aoVsync >= 0 && aoVsync != GlobalGet("vsyncApplied")) {
    GlobalSet("vsyncApplied", aoVsync);
    WindowSetVSync(aoVsync == 1);
}

if (argument0 == 1) {
    var aoSave = "mmopt2";
    var aoKeys = @OPTION_NAMES@;
    for (var aoJ = 0; aoJ < PgListSize(aoKeys); aoJ = aoJ + 1) {
        aoSave = aoSave + "," + StringOf(GlobalGet(aoKeys[aoJ]));
    }
    SaveSlotWrite("Options", aoSave);
}
return 1;
""".replace("@OPTION_NAMES@", pgsl_list(OPTIONS_SLOT_FIELDS))

CINEMATIC_KIT = """// Cinematic Kit: a library Script. Its functions are called directly by the Intro Director
// and the Credits Director (any object may use them). Everything here draws in Draw GUI, in a
// 1280x720 design space scaled to the window (sx = width / 1280, sy = height / 720).
//
//   CkMotesCreate(count)                       a pool of glowing motes (list of lists)
//   CkMoteSpawn(motes, kind, atX, atY)         one mote; atX < 0 picks the kind's own place
//   CkMotesStep(motes, dt, t, rate, kindA, kindB, altChance)   spawn rate per second + move
//   CkMotesDraw(motes, t, sx, sy, s, fade)     draw them as soft glows
//   CkDrawPanel(image, local, duration, fade, zA, zB, cxA, cyA, cxB, cyB, alpha, sw, sh)
//        a slow pan and zoom ("Ken Burns") across a 640x360 painting: zoom zA -> zB and
//        centre (cxA, cyA) -> (cxB, cyB) in fractions of the picture, eased over the panel
//   CkDrawCaption(lineA, lineB, local, start, alpha, sx, sy, s)   typed two-line caption
//   CkDrawVignette(sw, sh, strength), CkDrawLetterbox(sw, sh, sy, amount), CkDrawCrt(sw, sh, sy)
// Mote kinds: 0 firefly (rises), 1 dusk mote (violet, falls), 2 falling spore fleck,
// 3 Glowspore (big, flies out and arcs down), 4 festival ember (orange, rises).

function CkMotesCreate(count) {
    var motes = [[], [], [], [], [], [], [], [], [], [0]];
    for (var f = 0; f < 9; f += 1) {
        var column = motes[f];
        for (var i = 0; i < count; i += 1) { column[i] = 0; }
    }
    return motes;
}

function CkMoteSpawn(motes, kind, atX, atY) {
    var life = motes[4];
    var n = PgListSize(life);
    var slot = -1;
    for (var i = 0; i < n; i += 1) {
        if (life[i] <= 0) { slot = i; break; }
    }
    if (slot < 0) { return -1; }
    var mx = motes[0]; var my = motes[1]; var mvx = motes[2]; var mvy = motes[3];
    var full = motes[5]; var kinds = motes[6]; var size = motes[7]; var phase = motes[8];
    kinds[slot] = kind;
    phase[slot] = Random(360);
    if (kind == 0) {
        mx[slot] = Random(1280); my[slot] = RandomRange(380, 700);
        mvx[slot] = RandomRange(-12, 12); mvy[slot] = RandomRange(-30, -10);
        full[slot] = RandomRange(4, 7); size[slot] = RandomRange(2.6, 4.2);
    } else if (kind == 1) {
        mx[slot] = RandomRange(-80, 1200); my[slot] = -12;
        mvx[slot] = RandomRange(10, 34); mvy[slot] = RandomRange(18, 42);
        full[slot] = RandomRange(7, 10); size[slot] = RandomRange(1.8, 3.0);
    } else if (kind == 2) {
        mx[slot] = Random(1280); my[slot] = -12;
        mvx[slot] = RandomRange(-16, 16); mvy[slot] = RandomRange(30, 64);
        full[slot] = RandomRange(8, 12); size[slot] = RandomRange(2.4, 4.0);
    } else if (kind == 3) {
        mx[slot] = 640; my[slot] = 210;
        mvx[slot] = RandomRange(-260, 260); mvy[slot] = RandomRange(-170, -50);
        full[slot] = 5.5; size[slot] = RandomRange(7, 10);
    } else {
        mx[slot] = Random(1280); my[slot] = RandomRange(560, 740);
        mvx[slot] = RandomRange(-10, 10); mvy[slot] = RandomRange(-48, -22);
        full[slot] = RandomRange(5, 8); size[slot] = RandomRange(2.8, 4.6);
    }
    if (atX >= 0) { mx[slot] = atX; my[slot] = atY; }
    life[slot] = full[slot];
    return slot;
}

function CkMotesStep(motes, dt, t, rate, kindA, kindB, altChance) {
    var clock = motes[9];
    clock[0] = clock[0] + dt * rate;
    while (clock[0] >= 1) {
        clock[0] = clock[0] - 1;
        if (Random(1) < altChance) { CkMoteSpawn(motes, kindB, -1, 0); }
        else { CkMoteSpawn(motes, kindA, -1, 0); }
    }
    var mx = motes[0]; var my = motes[1]; var mvx = motes[2]; var mvy = motes[3];
    var life = motes[4]; var kinds = motes[6]; var phase = motes[8];
    var n = PgListSize(life);
    for (var i = 0; i < n; i += 1) {
        if (life[i] > 0) {
            life[i] = life[i] - dt;
            mx[i] = mx[i] + mvx[i] * dt + Sin(phase[i] + t * 120) * dt * 14;
            my[i] = my[i] + mvy[i] * dt;
            if (kinds[i] == 3) { mvy[i] = mvy[i] + 70 * dt; mvx[i] = mvx[i] * (1 - dt * 0.4); }
        }
    }
    return 0;
}

function CkMotesDraw(motes, t, sx, sy, s, fade) {
    var mx = motes[0]; var my = motes[1]; var life = motes[4]; var full = motes[5];
    var kinds = motes[6]; var size = motes[7]; var phase = motes[8];
    var n = PgListSize(life);
    for (var i = 0; i < n; i += 1) {
        if (life[i] > 0) {
            var a = Clamp(life[i] / full[i] * 3, 0, 1) * Clamp((full[i] - life[i]) * 3, 0, 1) * fade;
            var glow = 0.65 + 0.35 * Sin(phase[i] * 3 + t * 400);
            var k = kinds[i];
            if (k == 0) { DrawSetColorRgb(255, 214, 110); }
            else if (k == 1) { DrawSetColorRgb(196, 156, 255); }
            else if (k == 2) { DrawSetColorRgb(255, 240, 196); }
            else if (k == 3) { DrawSetColorRgb(255, 236, 160); }
            else { DrawSetColorRgb(255, 160, 80); }
            var cx = mx[i] * sx; var cy = my[i] * sy; var r = size[i] * s;
            if (k == 3) {
                DrawSetAlpha(a * 0.12); DrawCircleEx(cx, cy, r * 4.5, 0);
            }
            DrawSetAlpha(a * 0.22 * glow); DrawCircleEx(cx, cy, r * 2.6, 0);
            DrawSetAlpha(a * glow); DrawCircleEx(cx, cy, r, 0);
            if (k == 3) { DrawSetColorRgb(255, 255, 255); DrawSetAlpha(a); DrawCircleEx(cx, cy, r * 0.45, 0); }
        }
    }
    DrawSetAlpha(1);
    return 0;
}

function CkDrawPanel(image, local, duration, fade, zA, zB, cxA, cyA, cxB, cyB, alpha, sw, sh) {
    var p = Clamp((local + fade) / (duration + fade), 0, 1);
    var e = p * p * (3 - 2 * p);
    var w = 1 / Max(1, Lerp(zA, zB, e));
    var u0 = Clamp(Lerp(cxA, cxB, e) - w / 2, 0, 1 - w);
    var v0 = Clamp(Lerp(cyA, cyB, e) - w / 2, 0, 1 - w);
    DrawSpritePart(image, 0, u0, v0, u0 + w, v0 + w, 0, 0, sw, sh, Clamp(alpha, 0, 1));
    return 0;
}

function CkDrawCaption(lineA, lineB, local, start, alpha, sx, sy, s) {
    var ct = local - start;
    if (ct <= 0 || alpha <= 0) { return 0; }
    var cps = 34;
    var la = StringLength(lineA);
    var lb = StringLength(lineB);
    var na = Clamp(Floor(ct * cps), 0, la);
    var nb = Clamp(Floor((ct - la / cps - 0.45) * cps), 0, lb);
    var boxIn = Clamp(ct * 4, 0, 1) * alpha;
    DrawSetColorRgb(20, 14, 28); DrawSetAlpha(0.66 * boxIn);
    DrawRoundRectEx(150 * sx, 564 * sy, 1130 * sx, 662 * sy, 14 * s, 0);
    DrawSetColorRgb(255, 222, 150); DrawSetAlpha(0.5 * boxIn);
    DrawRoundRectEx(150 * sx, 564 * sy, 1130 * sx, 662 * sy, 14 * s, 2 * s);
    DrawSetFont("Georgia");
    DrawSetTextAlign("center");
    var textA = StringCopy(lineA, 1, na);
    var textB = StringCopy(lineB, 1, nb);
    DrawSetColorRgb(10, 6, 14); DrawSetAlpha(0.8 * alpha);
    DrawTextScaled(642 * sx, 578 * sy, textA, 24 * s);
    DrawTextScaled(642 * sx, 616 * sy, textB, 24 * s);
    DrawSetColorRgb(255, 241, 214); DrawSetAlpha(alpha);
    DrawTextScaled(640 * sx, 576 * sy, textA, 24 * s);
    DrawSetColorRgb(255, 214, 140);
    DrawTextScaled(640 * sx, 614 * sy, textB, 24 * s);
    DrawSetTextAlign("left");
    DrawSetAlpha(1);
    return 0;
}

function CkDrawVignette(sw, sh, strength) {
    DrawRectangleGradient(0, 0, sw, sh * 0.22, 0, 0, 0, 0.55 * strength, 0, 0, 0, 0, true);
    DrawRectangleGradient(0, sh * 0.72, sw, sh, 0, 0, 0, 0, 0, 0, 0, 0.6 * strength, true);
    DrawRectangleGradient(0, 0, sw * 0.14, sh, 0, 0, 0, 0.45 * strength, 0, 0, 0, 0, false);
    DrawRectangleGradient(sw * 0.86, 0, sw, sh, 0, 0, 0, 0, 0, 0, 0, 0.45 * strength, false);
    return 0;
}

function CkDrawLetterbox(sw, sh, sy, amount) {
    var bar = 46 * sy * Clamp(amount, 0, 1);
    if (bar <= 0) { return 0; }
    DrawSetColorRgb(6, 4, 10); DrawSetAlpha(1);
    DrawRectangle(0, 0, sw, bar);
    DrawRectangle(0, sh - bar, sw, sh);
    return 0;
}

function CkDrawCrt(sw, sh, sy) {
    // The SNES Look post effect only shades the world; GUI-drawn cinematics draw their own lines.
    if (GlobalGet("crt") != 1) { return 0; }
    DrawSetColorRgb(0, 0, 0); DrawSetAlpha(0.16);
    var row = 0;
    while (row < 720) {
        DrawRectangle(0, row * sy, sw, (row + 1) * sy);
        row = row + 3;
    }
    DrawSetAlpha(1);
    return 0;
}

function CkPadOrClickPressed() {
    return MousePressed(MbLeft) || GamepadPressed("A") || GamepadPressed("B") || GamepadPressed("X")
        || GamepadPressed("Y") || GamepadPressed("Start") || GamepadPressed("Back");
}
"""


# ---------------------------------------------------------------- Intro
INTRO_STORY = [
    ("Long ago, the village of Hearthollow made its home beneath the Great Lantern Tree.",
     "Five Glowspores shone in its branches and kept the long night away."),
    ("Then, one autumn dusk, a great shadow crossed the moon...",
     "Duskmantle, Queen of the Dusk Moths, had come for the light."),
    ("She tore the five Glowspores from the Tree and scattered them far and wide.",
     "Twilight crept over every hill, cave and shore."),
    ("Elder Morel pressed his old lantern into a pair of small, brave hands.",
     "'Bring back the Glowspores, Pip. Bring back the morning.'"),
    ("With a red cap, a full backpack and a borrowed light,",
     "Pip set out across the Meadow, toward the dawn."),
]
# zoom start/end (1 = whole painting), centre start/end in fractions of the painting.
INTRO_MOVES = [
    (1.38, 1.04, 0.50, 0.28, 0.50, 0.55),   # down from the Tree's glowing crown to the village
    (1.08, 1.36, 0.42, 0.52, 0.68, 0.36),   # drift across the valley to Duskmantle on the moon
    (1.42, 1.04, 0.62, 0.34, 0.50, 0.50),   # from Duskmantle's grasp, pull back on the dark Tree
    (1.06, 1.50, 0.50, 0.50, 0.46, 0.74),   # push in on the lantern changing hands
    (1.50, 1.02, 0.32, 0.78, 0.55, 0.50),   # from Pip on the path, open out on the valley
]
INTRO_MOTES = [  # spawn rate, kind A, kind B, chance of B
    (7, 0, 0, 0.0),
    (6, 1, 1, 0.0),
    (10, 2, 1, 0.3),
    (6, 0, 2, 0.25),
    (8, 0, 2, 0.35),
]


def intro_create():
    lines = [
        "// Intro Director: the pre-title cinematic. Five painted panels (Intro Panel 1-5) pan, zoom",
        "// and cross-fade while the story is typed into a caption box, Intro Theme plays and",
        "// glowing motes drift over the paintings (Cinematic Kit). Any key, controller button or",
        "// click skips. At the end it sets GlobalSet(\"introSeen\", 1) and goes to the Main Menu.",
        "// Timing: panelLength seconds per panel; the next panel fades in over fadeLength seconds.",
        "ScriptExecute(\"Apply Options\");",
        "t = 0; anyKey = 0; skipping = 0; skipTimer = 0; finalFade = 0; musicFading = 0; done = 0;",
        "panelLength = 11; fadeLength = 1.6; panelCount = 5; burstDone = 0;",
        "endTime = panelCount * panelLength;",
        "panels = %s;" % pgsl_list(INTRO_PANELS),
        "captionA = %s;" % pgsl_list([a for a, _ in INTRO_STORY]),
        "captionB = %s;" % pgsl_list([b for _, b in INTRO_STORY]),
    ]
    names = ["zoomA", "zoomB", "panXA", "panYA", "panXB", "panYB"]
    for j, name in enumerate(names):
        lines.append("%s = %s;" % (name, pgsl_list([m[j] for m in INTRO_MOVES])))
    names = ["moteRate", "moteKindA", "moteKindB", "moteAlt"]
    for j, name in enumerate(names):
        lines.append("%s = %s;" % (name, pgsl_list([m[j] for m in INTRO_MOTES])))
    lines += [
        "motes = CkMotesCreate(64);",
        "logo = \"%s\";" % TITLE_LOGO,
        "music = PlaySound(\"%sIntro Theme.audio.json\", 0.85, 1, false);" % AUD,
        "SoundSetBus(music, \"music\");",
        "WindowSetCursorVisible(false);",
    ]
    return "\n".join(lines) + "\n"


INTRO_KEY = """// Any keyboard key skips the cinematic (Step reads the flag).
anyKey = 1;
"""

INTRO_STEP = """var dt = Clamp(DeltaTime, 0, 0.05);
t = t + dt;
var wantSkip = anyKey;
anyKey = 0;
if (CkPadOrClickPressed()) { wantSkip = 1; }
if (wantSkip == 1 && skipping == 0 && done == 0 && t > 0.25) {
    skipping = 1; skipTimer = 0;
    StopSoundFaded(music, 0.6);
    PlaySound("%(select)s", 0.6, 1, false);
}
if (skipping == 1) {
    skipTimer = skipTimer + dt;
    finalFade = Clamp(skipTimer / 0.6, 0, 1);
} else if (t > endTime) {
    if (musicFading == 0) { musicFading = 1; StopSoundFaded(music, 1.8); }
    finalFade = Clamp((t - endTime) / 1.8, 0, 1);
}
if (done == 0 && (skipTimer > 0.7 || t > endTime + 2.0)) {
    done = 1;
    GlobalSet("introSeen", 1);
    RoomChangeFade(0.4);
    RoomGoto("Main Menu");
    return;
}
var k = Clamp(Floor(t / panelLength), 0, panelCount - 1);
// The third panel opens with the five Glowspores tumbling from Duskmantle's grasp.
if (k == 2 && burstDone == 0) {
    burstDone = 1;
    for (var b = 0; b < 5; b += 1) { CkMoteSpawn(motes, 3, 900 + b * 8, 262 - b * 6); }
}
CkMotesStep(motes, dt, t, moteRate[k], moteKindA[k], moteKindB[k], moteAlt[k]);
""" % {"select": SND_SELECT}

INTRO_DRAWGUI = """var sw = WindowGetWidth(); var sh = WindowGetHeight();
var sx = sw / 1280; var sy = sh / 720; var s = Min(sx, sy);
DrawSetAlpha(1); DrawSetColorRgb(0, 0, 0);
DrawRectangle(0, 0, sw, sh);
// Current panel, and the next one cross-fading in over its last fadeLength seconds.
var k = Clamp(Floor(t / panelLength), 0, panelCount - 1);
var local = t - k * panelLength;
CkDrawPanel(panels[k], local, panelLength, fadeLength, zoomA[k], zoomB[k], panXA[k], panYA[k], panXB[k], panYB[k], 1, sw, sh);
var nextLocal = t - (k + 1) * panelLength;
if (k < panelCount - 1 && nextLocal > -fadeLength) {
    var n = k + 1;
    CkDrawPanel(panels[n], nextLocal, panelLength, fadeLength, zoomA[n], zoomB[n], panXA[n], panYA[n], panXB[n], panYB[n],
        (nextLocal + fadeLength) / fadeLength, sw, sh);
}
CkDrawVignette(sw, sh, 1);
CkMotesDraw(motes, t, sx, sy, s, 1);
// The title rises out of the last panel.
var logoA = Clamp((t - (endTime - 4.6)) / 1.6, 0, 1);
if (logoA > 0) {
    DrawSetColorRgb(255, 226, 150); DrawSetAlpha(0.16 * logoA);
    DrawCircleEx(640 * sx, 150 * sy, 260 * s, 0);
    DrawSpriteScaled(logo, 320 * sx, (54 + (1 - logoA) * 18) * sy, 0, 2 * sx, 2 * sy, 0, logoA);
}
CkDrawLetterbox(sw, sh, sy, Clamp(t / 1.2, 0, 1));
// Captions: typed in 0.8 s after a panel starts, gone as the next panel fades in.
var capA = Clamp((panelLength - fadeLength * 0.6 - local) / 0.6, 0, 1);
// On the last panel the caption gives way to the rising title instead.
if (k == panelCount - 1) { capA = Clamp((panelLength - 4.8 - local) / 0.6, 0, 1); }
CkDrawCaption(captionA[k], captionB[k], local, 0.8, capA, sx, sy, s);
// Skip hint in the top bar.
var hintA = Clamp((t - 1.0) / 0.8, 0, 1) * (0.55 + 0.25 * Sin(t * 160)) * (1 - finalFade);
DrawSetFont("Georgia"); DrawSetTextAlign("right");
DrawSetColorRgb(255, 236, 200); DrawSetAlpha(hintA);
DrawTextScaled(1250 * sx, 12 * sy, "Press any key to skip", 17 * s);
DrawSetTextAlign("left");
CkDrawCrt(sw, sh, sy);
// Fade in from black at the start; fade out at the end or when skipped.
var fade = Max(1 - t / 1.4, finalFade);
if (fade > 0) {
    DrawSetColorRgb(0, 0, 0); DrawSetAlpha(Clamp(fade, 0, 1));
    DrawRectangle(0, 0, sw, sh);
}
DrawSetAlpha(1); DrawSetColorRgb(255, 255, 255);
"""

INTRO_ROOMEND = """StopSound(music);
WindowSetCursorVisible(true);
"""


# ---------------------------------------------------------------- Main Menu UI
def element(eid, etype, x, y, w, h, order, **extra):
    data = {"id": eid, "parentId": "", "type": etype, "anchor": "TopLeft", "x": float(x), "y": float(y),
            "width": float(w), "height": float(h), "order": order, "visible": True, "text": "",
            "font": "Georgia", "fontSize": 20.0, "image": "", "background": "#00000000",
            "foreground": "#FFFFFFFF", "accent": "#FFFFD23F"}
    data.update(extra)
    return data


GOLD = {"background": "#FFF2B33D", "gradientEnd": "#FFC9771E", "foreground": "#FF2A1D2E",
        "borderColor": "#FFFFF4D6"}


def button(eid, x, y, w, h, order, text, size=22.0, **extra):
    data = dict(background="#E62A1D2E", fill="VerticalGradient", gradientEnd="#E6402A44",
                foreground="#FFF6E7C1", accent="#FFFFD23F", cornerRadius=10.0, borderWidth=2.0,
                borderColor="#90FFE7A0", textAlign="Center", textVerticalAlign="Middle",
                letterSpacing=1.5, fontSize=size, text=text,
                hover=dict(GOLD), pressed={"background": "#FFFFE7A0", "gradientEnd": "#FFE89A1B",
                                           "foreground": "#FF2A1D2E", "borderColor": "#FFFFFFFF"},
                selected=dict(GOLD),
                disabled={"background": "#80201820", "gradientEnd": "#80201820",
                          "foreground": "#806E6272", "borderColor": "#40FFE7A0"})
    data.update(extra)
    return element(eid, "Button", x, y, w, h, order, **data)


def text(eid, x, y, w, h, order, value, size=20.0, colour="#FFF6E7C1", align="Center", **extra):
    data = dict(text=value, fontSize=size, foreground=colour, textAlign=align, textVerticalAlign="Middle")
    data.update(extra)
    return element(eid, "Text", x, y, w, h, order, **data)


def panel(eid, x, y, w, h, order, **extra):
    data = dict(background="#E01A1222", fill="VerticalGradient", gradientEnd="#E0261A30", cornerRadius=16.0,
                borderWidth=3.0, borderColor="#C0FFD23F")
    data.update(extra)
    return element(eid, "Panel", x, y, w, h, order, **data)


MAIN_IDS = ["btnNew", "btnContinue", "btnOptions", "btnCredits", "btnQuit"]
SLOT_IDS = ["slot1", "slot2", "slot3", "slotBack"]
OPTION_IDS = []
CONFIRM_IDS = ["confirmYes", "confirmNo"]
OPTION_ROWS_Y = [318, 374, 430, 486]


def build_menu_ui():
    groups = {"main": [], "slots": [], "options": [], "confirm": []}
    elements = []

    def add(group, el):
        elements.append(el)
        if group:
            groups[group].append(el["id"])

    # Main page: a column of five buttons under the logo.
    add("main", panel("mainPanel", 470, 250, 340, 330, 10))
    labels = ["NEW GAME", "CONTINUE", "OPTIONS", "CREDITS", "QUIT"]
    for i, (eid, label) in enumerate(zip(MAIN_IDS, labels)):
        add("main", button(eid, 500, 266 + i * 62, 280, 50, 11 + i, label))

    # Save slot page: three cards.
    add("slots", text("slotHeader", 0, 232, 1280, 40, 20, "START A NEW ADVENTURE", 28.0, "#FFFFE7A0",
                      letterSpacing=2.0))
    for k in range(1, 4):
        x = 130 + (k - 1) * 350
        y = 286
        base = 30 + k * 40
        add("slots", button("slot%d" % k, x, y, 320, 250, base, "", 20.0, textVerticalAlign="Top",
                            background="#E61E1528", gradientEnd="#E6302040", cornerRadius=16.0, borderWidth=3.0,
                            hover={"background": "#FF4A3020", "gradientEnd": "#FF2E1C14", "foreground": "#FFFFFFFF",
                                   "borderColor": "#FFFFD23F"},
                            selected={"background": "#FF4A3020", "gradientEnd": "#FF2E1C14",
                                      "foreground": "#FFFFFFFF", "borderColor": "#FFFFD23F"}))
        add("slots", text("slot%dTitle" % k, x, y + 12, 320, 34, base + 1, "SLOT %d" % k, 24.0, "#FFFFD23F",
                          letterSpacing=3.0))
        add("slots", text("slot%dLine1" % k, x, y + 58, 320, 30, base + 2, "- EMPTY -", 21.0))
        add("slots", text("slot%dLine2" % k, x, y + 94, 320, 30, base + 3, "", 18.0, "#FFDCCFB8"))
        add("slots", text("slot%dLine3" % k, x, y + 126, 320, 30, base + 4, "", 17.0, "#FFB8D8C0"))
        for j in range(1, 6):
            sx = x + 89 + (j - 1) * 30
            add("slots", element("slot%dSporeBase%d" % (k, j), "Panel", sx, y + 168, 22, 22, base + 5,
                                 background="#FF2A2036", cornerRadius=11.0, borderWidth=2.0,
                                 borderColor="#FF6A5A7A"))
            add("slots", element("slot%dSpore%d" % (k, j), "Panel", sx + 3, y + 171, 16, 16, base + 6,
                                 background="#FFFFF4C0", fill="VerticalGradient", gradientEnd="#FFF2A030",
                                 cornerRadius=8.0, borderWidth=0.0))
        add("slots", text("slot%dDone" % k, x, y + 204, 320, 30, base + 7, "THE TREE IS LIT", 16.0, "#FFFFD23F",
                          letterSpacing=2.0))
        add("slots", button("slot%dDel" % k, x + 95, y + 262, 130, 34, base + 8, "DELETE", 15.0,
                            background="#D63A1418", gradientEnd="#D6281014", borderColor="#90FF9A80",
                            foreground="#FFFFC8B8",
                            hover={"background": "#FFD8433B", "gradientEnd": "#FFA52D2B",
                                   "foreground": "#FFFFFFFF", "borderColor": "#FFFFE0D0"},
                            selected={"background": "#FFD8433B", "gradientEnd": "#FFA52D2B",
                                      "foreground": "#FFFFFFFF", "borderColor": "#FFFFE0D0"}))
    add("slots", button("slotBack", 540, 614, 200, 46, 200, "BACK"))

    # Options page: drawn by the Options Kit (Draw GUI), so the group stays empty.

    # Confirmation dialog over the slot page.
    add("confirm", element("confirmShade", "Panel", 0, 0, 1280, 684, 400, background="#A0080510"))
    add("confirm", panel("confirmPanel", 380, 246, 520, 236, 401))
    add("confirm", text("confirmText", 380, 268, 520, 44, 402, "OVERWRITE SLOT 1?", 27.0, "#FFFFE7A0",
                        letterSpacing=1.5))
    add("confirm", text("confirmSub", 380, 316, 520, 34, 403, "Its progress will be lost.", 19.0))
    add("confirm", button("confirmYes", 420, 392, 200, 52, 404, "YES"))
    add("confirm", button("confirmNo", 660, 392, 200, 52, 405, "NO"))

    # Always visible: footer with control hints.
    add(None, element("footer", "Panel", 0, 684, 1280, 36, 500, background="#D0140E18", fill="VerticalGradient",
                      gradientEnd="#F0080510", borderWidth=0.0))
    add(None, text("footerText", 24, 686, 900, 32, 501,
                   "ARROWS / D-PAD  choose      ENTER / A  select      ESC / B  back", 16.0, "#FFE8DCC4",
                   align="Left", letterSpacing=1.0))
    add(None, text("footerRight", 900, 686, 356, 32, 502, "made with Genesis Studio", 15.0, "#FFB8A890",
                   align="Right"))

    write_json("User Interfaces/Main Menu UI.ui.json",
               {"schemaVersion": 1, "designWidth": 1280, "designHeight": 720, "elements": elements})
    write_meta("User Interfaces/Main Menu UI.ui.json", "UserInterface", "Main Menu UI")
    return groups


# ---------------------------------------------------------------- Main Menu object
def menu_create(groups):
    clickables = (MAIN_IDS + SLOT_IDS + ["slot1Del", "slot2Del", "slot3Del"] + OPTION_IDS + CONFIRM_IDS)
    return """// Menu Controller: the Main Menu room controller. Pages: 0 main, 1 save slots, 2 options, 3 confirm.
// Keyboard (arrows/WASD, Enter/Space, Esc/Backspace, Delete), controller (D-pad or left
// stick, A select, B back, Y delete) and mouse (hover, click, drag sliders) all work: the
// mouse moves the same selection the keys do. The UI is the "Main Menu UI" User Interface
// resource; this object shows/hides its groups, sets texts and reads clicks.
// New Game / Continue call the New Game / Game Load Scripts (campaign = 1) and go to the
// Overworld. Options are applied live (Apply Options) and saved when leaving the page.
ScriptExecute("Apply Options");
GlobalSet("campaign", 0);
WindowSetCursorVisible(true);
ui = "%(ui)s";
logo = "%(logo)s";
page = 0; sel = 0; mode = 0; slotSel = 0; confirmKind = 0; confirmSlot = 1; confirmSel = 1;
stickCool = 0; t = 0; leaving = 0; leaveTimer = 0; leaveTo = ""; lastHover = ""; anySave = 0;
mainIds = %(main)s;
slotIds = %(slots)s;
confirmIds = %(confirm)s;
clickIds = %(clicks)s;
groupMain = %(gmain)s;
groupSlots = %(gslots)s;
groupOptions = %(goptions)s;
groupConfirm = %(gconfirm)s;
mapNames = %(nodes)s;
// Twinkles drawn over the logo (design pixels, phase in degrees).
twinkleX = [352, 470, 604, 742, 880, 930, 410, 812];
twinkleY = [70, 44, 120, 52, 96, 170, 188, 186];
twinklePhase = [0, 140, 75, 260, 200, 320, 40, 110];

function MmShowPage(p) {
    for (var a = 0; a < PgListSize(groupMain); a += 1) { UiSetVisible(ui, groupMain[a], p == 0); }
    for (var b = 0; b < PgListSize(groupSlots); b += 1) { UiSetVisible(ui, groupSlots[b], p == 1 || p == 3); }
    for (var c = 0; c < PgListSize(groupOptions); c += 1) { UiSetVisible(ui, groupOptions[c], p == 2); }
    for (var d = 0; d < PgListSize(groupConfirm); d += 1) { UiSetVisible(ui, groupConfirm[d], p == 3); }
    MmRefreshSlots(p == 1 || p == 3);
    var hints = "ARROWS / D-PAD  choose      ENTER / A  select      ESC / B  back";
    if (p == 1) { hints = "LEFT / RIGHT  choose a slot      ENTER / A  play      DEL / Y  delete      ESC / B  back"; }
    if (p == 2) { hints = "ARROWS / D-PAD  choose and change      ENTER / A  select      ESC / B  back (saves)"; }
    if (p == 3) { hints = "LEFT / RIGHT  choose      ENTER / A  confirm      ESC / B  cancel"; }
    UiSetText(ui, "footerText", hints);
    return 0;
}

function MmSlotUsed(k) {
    return StringSplitPart(SaveSlotRead("Slot " + StringOf(k)), ",", 1) == "mm2";
}

function MmRefreshSlots(shown) {
    var any = 0;
    for (var k = 1; k <= 3; k += 1) {
        var data = SaveSlotRead("Slot " + StringOf(k));
        var used = StringSplitPart(data, ",", 1) == "mm2";
        var sid = "slot" + StringOf(k);
        var spores = 0;
        var complete = 0;
        if (used) {
            any = 1;
            spores = Clamp(Real(StringSplitPart(data, ",", %(fglow)d)), 0, 5);
            complete = Real(StringSplitPart(data, ",", %(fdone)d));
            var node = Clamp(Real(StringSplitPart(data, ",", %(fnode)d)), 0, 5);
            UiSetText(ui, sid + "Line1", "Glowspores  " + StringOf(spores) + " / 5");
            UiSetText(ui, sid + "Line2", "Coins " + StringOf(Real(StringSplitPart(data, ",", %(fcoins)d)))
                + "      Lives " + StringOf(Real(StringSplitPart(data, ",", %(flives)d))));
            UiSetText(ui, sid + "Line3", "at " + mapNames[node]);
        } else {
            UiSetText(ui, sid + "Line1", "- EMPTY -");
            UiSetText(ui, sid + "Line2", "A new adventure awaits");
            UiSetText(ui, sid + "Line3", "");
        }
        for (var j = 1; j <= 5; j += 1) {
            UiSetVisible(ui, sid + "SporeBase" + StringOf(j), shown && used);
            UiSetVisible(ui, sid + "Spore" + StringOf(j), shown && used && spores >= j);
        }
        UiSetVisible(ui, sid + "Done", shown && used && complete == 1);
        UiSetVisible(ui, sid + "Del", shown && used);
    }
    UiSetEnabled(ui, "btnContinue", any == 1);
    self.anySave = any;
    return any;
}

function MmMostRecentSlot() {
    for (var i = 0; i < SaveSlotCount(); i += 1) {
        var name = SaveSlotName(i);
        if (name == "Slot 1") { return 0; }
        if (name == "Slot 2") { return 1; }
        if (name == "Slot 3") { return 2; }
    }
    return 0;
}

function MmConfirm(kind, k) {
    self.confirmKind = kind; self.confirmSlot = k; self.confirmSel = 1; self.page = 3;
    if (kind == 1) {
        UiSetText(ui, "confirmText", "OVERWRITE SLOT " + StringOf(k) + "?");
        UiSetText(ui, "confirmSub", "Its journey will be replaced by a new one.");
    } else {
        UiSetText(ui, "confirmText", "DELETE SLOT " + StringOf(k) + "?");
        UiSetText(ui, "confirmSub", "This cannot be undone.");
    }
    MmShowPage(3);
    PlaySound("%(move)s", 0.7, 1, false);
    return 0;
}

function MmLeave(target) {
    self.leaving = 1; self.leaveTimer = 0; self.leaveTo = target;
    StopSoundFaded(self.music, 0.5);
    PlaySound("%(select)s", 0.8, 1, false);
    return 0;
}

MmShowPage(0);
if (MmRefreshSlots(0) == 1) { sel = 1; }
music = PlaySound("%(theme)s", 0.75, 1, true);
SoundSetBus(music, "music");
// World-space particles over the Menu Backdrop (the GUI menu draws above them).
SpawnParticleEmitter("Assets/Particles/Menu Fireflies.particle.json", 320, 330, 0, 1);
SpawnParticleEmitter("Assets/Particles/Menu Sparkles.particle.json", 320, 64, 0, 1);
""" % {
        "ui": MENU_UI, "logo": TITLE_LOGO,
        "main": pgsl_list(MAIN_IDS), "slots": pgsl_list(SLOT_IDS), "options": pgsl_list(OPTION_IDS),
        "confirm": pgsl_list(CONFIRM_IDS), "clicks": pgsl_list(clickables),
        "gmain": pgsl_list(groups["main"]), "gslots": pgsl_list(groups["slots"]),
        "goptions": pgsl_list(groups["options"]), "gconfirm": pgsl_list(groups["confirm"]),
        "nodes": pgsl_list(MAP_NODES),
        "fglow": field("glowspores"), "fdone": field("gameComplete"), "fnode": field("mapNode"),
        "fcoins": field("coins"), "flives": field("lives"),
        "move": SND_MOVE, "select": SND_SELECT, "theme": AUD + "Menu Theme.audio.json",
    }


MENU_STEP = """var dt = Clamp(DeltaTime, 0, 0.05);
t = t + dt;
stickCool = Max(0, stickCool - dt);
UiUpdate(ui);
// Read (and so clear) every click; only the current page's ids act below.
var clicked = "";
for (var c = 0; c < PgListSize(clickIds); c += 1) {
    if (UiClicked(ui, clickIds[c]) && clicked == "") { clicked = clickIds[c]; }
}
if (leaving == 1) {
    leaveTimer = leaveTimer + dt;
    if (leaveTimer > 0.55) {
        if (leaveTo == "Overworld") { GlobalSet("campaign", 1); }
        leaving = 2;
        RoomChangeFade(0.4);
        RoomGoto(leaveTo);
    }
    return;
}
if (leaving == 2) { return; }

// One set of directions for keys, D-pad and the left stick (with repeat while held).
var navUp = KeyPressed("Up") || KeyPressed("W") || GamepadPressed("DPadUp");
var navDown = KeyPressed("Down") || KeyPressed("S") || GamepadPressed("DPadDown");
var navLeft = KeyPressed("Left") || KeyPressed("A") || GamepadPressed("DPadLeft");
var navRight = KeyPressed("Right") || KeyPressed("D") || GamepadPressed("DPadRight");
var stickX = GamepadAxis("LeftX"); var stickY = GamepadAxis("LeftY");
if (Abs(stickX) < 0.3 && Abs(stickY) < 0.3) { stickCool = 0; }
if (stickCool <= 0) {
    if (stickY > 0.55) { navUp = 1; stickCool = 0.24; }
    else if (stickY < -0.55) { navDown = 1; stickCool = 0.24; }
    else if (stickX < -0.55) { navLeft = 1; stickCool = 0.24; }
    else if (stickX > 0.55) { navRight = 1; stickCool = 0.24; }
}
var accept = KeyPressed("Enter") || KeyPressed("Space") || GamepadPressed("A") || GamepadPressed("Start");
var back = KeyPressed("Escape") || KeyPressed("Backspace") || GamepadPressed("B");
var erase = KeyPressed("Delete") || GamepadPressed("Y");

// The pointer moves the selection of the page it is over.
var hovered = UiGetHovered(ui);
var hoverMoved = 0;
if (hovered != lastHover) {
    lastHover = hovered;
    if (hovered != "") { hoverMoved = 1; }
}

if (page == 0) {
    var hi = PgListFind(mainIds, hovered);
    if (hoverMoved == 1 && hi >= 0 && hi != sel) { sel = hi; PlaySound("%(move)s", 0.5, 1, false); }
    // anySave is kept up to date by MmRefreshSlots (Create and every page change).
    if (navUp || navDown) {
        var step = navDown ? 1 : -1;
        sel = (sel + step + 5) %% 5;
        if (sel == 1 && anySave == 0) { sel = (sel + step + 5) %% 5; }
        PlaySound("%(move)s", 0.6, 1, false);
    }
    var pick = -1;
    if (accept) { pick = sel; }
    if (clicked != "" && PgListFind(mainIds, clicked) >= 0) { pick = PgListFind(mainIds, clicked); sel = pick; }
    if (back) { sel = 4; PlaySound("%(move)s", 0.6, 1, false); }
    if (pick == 0 || (pick == 1 && anySave == 1)) {
        mode = pick; page = 1;
        slotSel = 0;
        if (mode == 0) {
            if (MmSlotUsed(1)) { slotSel = 1; if (MmSlotUsed(2)) { slotSel = 2; if (MmSlotUsed(3)) { slotSel = 0; } } }
            UiSetText(ui, "slotHeader", "START A NEW ADVENTURE");
        } else {
            slotSel = MmMostRecentSlot();
            UiSetText(ui, "slotHeader", "CONTINUE YOUR JOURNEY");
        }
        MmShowPage(1);
        PlaySound("%(select)s", 0.7, 1, false);
    } else if (pick == 1) {
        PlaySound("%(deny)s", 0.7, 1, false);
    } else if (pick == 2) {
        page = 2; OptionsOpen(0); MmShowPage(2);
        PlaySound("%(select)s", 0.7, 1, false);
    } else if (pick == 3) {
        MmLeave("Credits");
    } else if (pick == 4) {
        PlaySound("%(back)s", 0.8, 1, false);
        GameQuit();
    }
} else if (page == 1) {
    var hs = PgListFind(slotIds, hovered);
    if (hoverMoved == 1 && hs >= 0 && hs != slotSel) { slotSel = hs; PlaySound("%(move)s", 0.5, 1, false); }
    if (navLeft) { slotSel = (slotSel + 3) %% 4; if (slotSel == 3) { slotSel = 2; } PlaySound("%(move)s", 0.6, 1, false); }
    if (navRight) { slotSel = Min(slotSel + 1, 2); if (slotSel > 2) { slotSel = 0; } PlaySound("%(move)s", 0.6, 1, false); }
    if (navDown) { slotSel = 3; PlaySound("%(move)s", 0.6, 1, false); }
    if (navUp && slotSel == 3) { slotSel = 1; PlaySound("%(move)s", 0.6, 1, false); }
    var chosen = -1;
    if (accept) { chosen = slotSel; }
    if (clicked != "" && PgListFind(slotIds, clicked) >= 0) { chosen = PgListFind(slotIds, clicked); slotSel = chosen; }
    var del = -1;
    if (erase && slotSel < 3) { del = slotSel; }
    if (clicked == "slot1Del") { del = 0; }
    if (clicked == "slot2Del") { del = 1; }
    if (clicked == "slot3Del") { del = 2; }
    if (back || chosen == 3) {
        page = 0; sel = mode; MmShowPage(0);
        PlaySound("%(back)s", 0.7, 1, false);
    } else if (del >= 0) {
        if (MmSlotUsed(del + 1)) { MmConfirm(2, del + 1); }
        else { PlaySound("%(deny)s", 0.7, 1, false); }
    } else if (chosen >= 0) {
        var used = MmSlotUsed(chosen + 1);
        if (mode == 0) {
            if (used) { MmConfirm(1, chosen + 1); }
            else { ScriptExecute("New Game", chosen + 1); MmLeave("Overworld"); }
        } else {
            if (used && ScriptExecute("Game Load", chosen + 1) == 1) { MmLeave("Overworld"); }
            else { PlaySound("%(deny)s", 0.7, 1, false); }
        }
    }
} else if (page == 2) {
    // The Options Kit owns this page (Gameplay / Video / Audio, Controls, Remap) and saves on close.
    OptionsStep();
    if (OptionsIsOpen() == 0) {
        page = 0; sel = 2; MmShowPage(0);
    }
} else if (page == 3) {
    var hc = PgListFind(confirmIds, hovered);
    if (hoverMoved == 1 && hc >= 0 && hc != confirmSel) { confirmSel = hc; PlaySound("%(move)s", 0.5, 1, false); }
    if (navLeft || navRight) { confirmSel = 1 - confirmSel; PlaySound("%(move)s", 0.6, 1, false); }
    var answer = -1;
    if (accept) { answer = confirmSel; }
    if (clicked == "confirmYes") { answer = 0; }
    if (clicked == "confirmNo") { answer = 1; }
    if (back) { answer = 1; }
    if (answer == 0) {
        if (confirmKind == 1) {
            ScriptExecute("New Game", confirmSlot);
            MmLeave("Overworld");
        } else {
            SaveSlotDelete("Slot " + StringOf(confirmSlot));
            PlaySound("%(chime)s", 0.6, 1, false);
            page = 1; MmShowPage(1);
            if (MmRefreshSlots(1) == 0 && mode == 1) { page = 0; sel = 0; MmShowPage(0); }
        }
    } else if (answer == 1) {
        page = 1; MmShowPage(1);
        PlaySound("%(back)s", 0.7, 1, false);
    }
}

// Selected looks follow the keyboard/controller selection.
for (var m = 0; m < 5; m += 1) {
    UiSetSelected(ui, mainIds[m], page == 0 && m == sel);
    if (m < 4) {
        UiSetSelected(ui, slotIds[m], (page == 1 || page == 3) && m == slotSel);
    }
    if (m < 2) { UiSetSelected(ui, confirmIds[m], page == 3 && m == confirmSel); }
}
""" % {"move": SND_MOVE, "select": SND_SELECT, "back": SND_BACK, "deny": SND_DENY, "chime": SND_CHIME}

MENU_DRAWGUI = """var sw = WindowGetWidth(); var sh = WindowGetHeight();
var sx = sw / 1280; var sy = sh / 720; var s = Min(sx, sy);
// Title Logo (320x96 pixel art at 2x) bobbing gently, with a warm glow behind it.
var logoIn = Clamp(t / 0.9, 0, 1);
var bob = Sin(t * 70) * 5;
DrawSetColorRgb(255, 214, 130); DrawSetAlpha(0.05 * logoIn);
DrawCircleEx(640 * sx, (124 + bob) * sy, 250 * s, 0);
DrawSetAlpha(0.06 * logoIn);
DrawCircleEx(640 * sx, (124 + bob) * sy, 160 * s, 0);
DrawSpriteScaled(logo, 320 * sx, (26 + bob + (1 - logoIn) * 20) * sy, 0, 2 * sx, 2 * sy, 0, logoIn);
// Four-point sparkles twinkling across the logo.
for (var i = 0; i < PgListSize(twinkleX); i += 1) {
    var tw = Sin(twinklePhase[i] + t * 150);
    if (tw > 0.2) {
        var a = (tw - 0.2) / 0.8 * logoIn;
        var cx = twinkleX[i] * sx; var cy = (twinkleY[i] + bob) * sy; var r = (6 + 8 * a) * s;
        DrawSetColorRgb(255, 248, 220); DrawSetAlpha(a);
        DrawRectangle(cx - r, cy - 1.2 * s, cx + r, cy + 1.2 * s);
        DrawRectangle(cx - 1.2 * s, cy - r, cx + 1.2 * s, cy + r);
        DrawSetAlpha(a * 0.35); DrawCircleEx(cx, cy, r * 0.7, 0);
    }
}
DrawSetAlpha(1);
DrawUi(ui);
if (page == 2) { OptionsDraw(); }
// Fade in from the intro; fade out when leaving.
var fade = Max(1 - t / 0.6, Clamp(leaveTimer / 0.5, 0, 1) * (leaving > 0));
if (fade > 0) {
    DrawSetColorRgb(0, 0, 0); DrawSetAlpha(Clamp(fade, 0, 1));
    DrawRectangle(0, 0, sw, sh);
}
DrawSetAlpha(1); DrawSetColorRgb(255, 255, 255);
"""

MENU_ROOMEND = """StopSound(music);
"""


# ---------------------------------------------------------------- Credits
ENDING_STORY = [
    ("Deep in Duskmantle Hollow, Pip lifted the last Glowspore high.",
     "Its warm light sang out, and the long twilight began to fade."),
    ("One by one, the five Glowspores came home to the Great Lantern Tree,",
     "and it blazed brighter than it had in a hundred years."),
    ("That night Hearthollow held the grandest Lantern Festival anyone remembered.",
     "Morel, Chanterelle, Bram, Puffball and Old Wick danced until dawn."),
]
ENDING_MOVES = [
    (1.45, 1.05, 0.46, 0.66, 0.50, 0.50),   # Pip lifts the last Glowspore
    (1.04, 1.30, 0.50, 0.62, 0.50, 0.34),   # up to the blazing crown
    (1.40, 1.03, 0.50, 0.72, 0.50, 0.50),   # the festival opens out
]
ENDING_MOTES = [  # spawn rate, kind A, kind B, chance of B
    (7, 0, 2, 0.4),
    (9, 2, 0, 0.4),
    (11, 4, 0, 0.35),
]

# (style, text): 0 heading, 1 name, 2 note, 3 big title, 4 spacer, 5 Title Logo
CREDITS = [
    (5, ""), (2, "The Five Glowspores of Hearthollow"), (4, ""), (4, ""),
    (0, "GAME BY"), (1, "Cal"), (4, ""),
    (0, "BUILT WITH"), (1, "Genesis Studio"), (2, "the Ember engine and PGSL scripting"), (4, ""),
    (0, "STARRING"), (1, "Pip"), (2, "a small wanderer in a red mushroom cap"), (4, ""),
    (1, "Elder Morel"), (1, "Chanterelle"), (1, "Bram the snail postman"), (1, "Puffball"), (1, "Old Wick"),
    (2, "and"), (1, "Duskmantle"), (2, "Queen of the Dusk Moths"), (4, ""),
    (0, "THE VALLEY"), (1, "Hearthollow"), (1, "Mushroom Meadow"), (1, "Rootdeep Caverns"),
    (1, "Tidepool Grotto"), (1, "Windswept Canopy"), (1, "Duskmantle Hollow"), (4, ""),
    (0, "BOSSES"), (1, "Grumblecrag"), (2, "the rock-shell beetle"), (1, "Duskmantle"), (4, ""),
    (0, "ART"), (1, "All pixel art is original"), (2, "generated for this template"), (4, ""),
    (0, "MUSIC AND SOUND"), (1, "All music and sound is original"), (2, "generated for this template"), (4, ""),
    (0, "MADE OF"), (2, "rooms, objects, images, tilesets, sounds, particles,"),
    (2, "shaders, post effects, paths, physics, user interfaces,"), (2, "scripts and notes"),
    (2, "every one of them editable in Genesis Studio"), (4, ""),
    (0, "SPECIAL THANKS"), (1, "Everyone who plays, edits"), (1, "and extends this meadow"),
    (4, ""), (4, ""), (4, ""),
    (3, "Thank you for playing!"), (2, ""),
]
CREDIT_HEIGHTS = {0: 40, 1: 40, 2: 30, 3: 80, 4: 34, 5: 220}
THUMBS = ENDING_PANELS + [INTRO_PANELS[0], INTRO_PANELS[3], INTRO_PANELS[4]]


def credits_create():
    texts = [t for _, t in CREDITS]
    styles = [s for s, _ in CREDITS]
    total = 0
    tops = []
    for s in styles:
        tops.append(total)
        total += CREDIT_HEIGHTS[s]
    final_top = tops[-1]
    thumb_y = [360 + i * 430 for i in range(len(THUMBS))]
    lines = [
        "// Credits Director: the ending and the credits. Reached from the final boss (campaign = 1)",
        "// it first plays the ending cinematic (Ending Panel 1-3, typed captions, festival embers),",
        "// marks the save as complete (gameComplete = 1, Game Save), then rolls the credits over the",
        "// Credits Backdrop with rising Festival Lanterns. From the Main Menu (campaign = 0) only the",
        "// credits roll. Any key skips the cinematic; hold Enter/Space/A to speed the roll;",
        "// Esc/Backspace/B returns to the Main Menu.",
        "ScriptExecute(\"Apply Options\");",
        "fromEnding = GlobalGet(\"campaign\") == 1;",
        "if (fromEnding) {",
        "    GlobalSet(\"gameComplete\", 1);",
        "    GlobalSet(\"glowspores\", 5);",
        "    ScriptExecute(\"Game Save\");",
        "}",
        "phase = fromEnding ? 0 : 1;",
        "t = 0; phaseT = 0; anyKey = 0; scrollT = 0; holdT = 0; outFade = 0; done = 0; leaving = 0;",
        "panelLength = 10; fadeLength = 1.6; panelCount = 3;",
        "panels = %s;" % pgsl_list(ENDING_PANELS),
        "captionA = %s;" % pgsl_list([a for a, _ in ENDING_STORY]),
        "captionB = %s;" % pgsl_list([b for _, b in ENDING_STORY]),
    ]
    names = ["zoomA", "zoomB", "panXA", "panYA", "panXB", "panYB"]
    for j, name in enumerate(names):
        lines.append("%s = %s;" % (name, pgsl_list([m[j] for m in ENDING_MOVES])))
    names = ["moteRate", "moteKindA", "moteKindB", "moteAlt"]
    for j, name in enumerate(names):
        lines.append("%s = %s;" % (name, pgsl_list([m[j] for m in ENDING_MOTES])))
    lines += [
        "burstDone = 0;",
        "creditText = %s;" % pgsl_list(texts),
        # The last two entries (big title, small note) change with Pip's reputation (Reputation Kit).
        "repEnd = 0; repKind = 0;",
        "if (ResourceExists(\"Reputation Kit\")) { repEnd = RepStage(); repKind = RepIsKind(); }",
        "endBig = [\"Thank you for playing!\", \"Thank you for playing.\", \"Thank you for playing?\", \"Rest now, little wanderer.\"];",
        "endSmall = [\"The valley sleeps easy tonight.\", \"The valley remembers everything.\", "
        "\"Somewhere under the cap, something hums.\", \"The spots are glowing. They will wait.\"];",
        "if (repKind == 1) { PgListSet(endSmall, 0, \"The valley will remember your kindness.\"); }",
        "PgListSet(creditText, PgListSize(creditText) - 2, endBig[repEnd]);",
        "PgListSet(creditText, PgListSize(creditText) - 1, endSmall[repEnd]);",
        "creditStyle = %s;" % pgsl_list(styles),
        "creditTop = %s;" % pgsl_list(tops),
        "finalTop = %d;" % final_top,
        "thumbs = %s;" % pgsl_list(THUMBS),
        "thumbY = %s;" % pgsl_list(thumb_y),
        "scrollSpeed = 40;",
        "logo = \"%s\";" % TITLE_LOGO,
        "motes = CkMotesCreate(64);",
        "music = PlaySound(\"%sCredits Theme.audio.json\", 0.85, 1, true);" % AUD,
        "SoundSetBus(music, \"music\");",
        "SpawnParticleEmitter(\"Assets/Particles/Festival Lanterns.particle.json\", 320, 372, 0, 1);",
        "SpawnParticleEmitter(\"Assets/Particles/Menu Fireflies.particle.json\", 320, 330, 0, 1);",
        "WindowSetCursorVisible(false);",
    ]
    return "\n".join(lines) + "\n"


CREDITS_KEY = """anyKey = 1;
"""

CREDITS_STEP = """var dt = Clamp(DeltaTime, 0, 0.05);
t = t + dt;
phaseT = phaseT + dt;
var pressed = anyKey || CkPadOrClickPressed();
anyKey = 0;
if (phase == 0) {
    // Ending cinematic, then a fade to the credits roll.
    var endTime = panelCount * panelLength;
    var k = Clamp(Floor(phaseT / panelLength), 0, panelCount - 1);
    // 1: fireflies and spore flecks; 2: light bursts from the Tree; 3: festival embers.
    if (k == 1 && burstDone == 0) {
        burstDone = 1;
        for (var b = 0; b < 5; b += 1) { CkMoteSpawn(motes, 3, -1, 0); }
    }
    CkMotesStep(motes, dt, t, moteRate[k], moteKindA[k], moteKindB[k], moteAlt[k]);
    if ((pressed && phaseT > 0.4) || phaseT > endTime + 3.2) { phase = 2; phaseT = 0; }
} else if (phase == 2) {
    // Fade out the paintings, then reveal the credits.
    if (phaseT > 1.0) { phase = 1; phaseT = 0; }
} else if (phase == 1) {
    var rollSpeed = scrollSpeed;
    if (KeyCheck("Enter") || KeyCheck("Space") || GamepadCheck("A")) { rollSpeed = scrollSpeed * 4; }
    var stopT = (740 + finalTop - 330) / scrollSpeed;
    if (scrollT < stopT) { scrollT = Min(stopT, scrollT + dt * rollSpeed / scrollSpeed); }
    else { holdT = holdT + dt; }
    if (leaving == 0 && (holdT > 6 || KeyPressed("Escape") || KeyPressed("Backspace") || GamepadPressed("B"))) {
        leaving = 1; StopSoundFaded(music, 1.4);
    }
}
if (leaving == 1) {
    outFade = Clamp(outFade + dt / 1.5, 0, 1);
    if (outFade >= 1 && done == 0) {
        done = 1;
        GlobalSet("campaign", 0);
        RoomChangeFade(0.5);
        RoomGoto("Main Menu");
    }
}
"""

CREDITS_DRAWGUI = """var sw = WindowGetWidth(); var sh = WindowGetHeight();
var sx = sw / 1280; var sy = sh / 720; var s = Min(sx, sy);
if (phase == 0 || phase == 2) {
    DrawSetAlpha(1); DrawSetColorRgb(0, 0, 0);
    DrawRectangle(0, 0, sw, sh);
    var pt = phaseT;
    if (phase == 2) { pt = panelCount * panelLength; }
    var k = Clamp(Floor(pt / panelLength), 0, panelCount - 1);
    var local = pt - k * panelLength;
    CkDrawPanel(panels[k], local, panelLength, fadeLength, zoomA[k], zoomB[k], panXA[k], panYA[k], panXB[k], panYB[k], 1, sw, sh);
    var nextLocal = pt - (k + 1) * panelLength;
    if (k < panelCount - 1 && nextLocal > -fadeLength) {
        var n = k + 1;
        CkDrawPanel(panels[n], nextLocal, panelLength, fadeLength, zoomA[n], zoomB[n], panXA[n], panYA[n], panXB[n], panYB[n],
            (nextLocal + fadeLength) / fadeLength, sw, sh);
    }
    CkDrawVignette(sw, sh, 1);
    CkMotesDraw(motes, t, sx, sy, s, 1);
    CkDrawLetterbox(sw, sh, sy, Clamp(t / 1.2, 0, 1));
    var capA = Clamp((panelLength - fadeLength * 0.6 - local) / 0.6, 0, 1);
    if (k == panelCount - 1) { capA = 1; }
    if (phase == 0) { CkDrawCaption(captionA[k], captionB[k], local, 0.8, capA, sx, sy, s); }
    // "The End" over the festival.
    var endA = Clamp((pt - (panelCount * panelLength - 0.5)) / 1.2, 0, 1);
    if (endA > 0) {
        DrawSetFont("Georgia"); DrawSetTextAlign("center");
        DrawSetColorRgb(20, 10, 4); DrawSetAlpha(0.7 * endA);
        DrawTextScaled(643 * sx, 233 * sy, "The End", 64 * s);
        DrawSetColorRgb(255, 226, 150); DrawSetAlpha(endA);
        DrawTextScaled(640 * sx, 230 * sy, "The End", 64 * s);
        DrawSetTextAlign("left");
    }
    CkDrawCrt(sw, sh, sy);
    var fade = Max(1 - t / 1.4, phase == 2 ? Clamp(phaseT / 1.0, 0, 1) : 0);
    if (fade > 0) { DrawSetColorRgb(0, 0, 0); DrawSetAlpha(Clamp(fade, 0, 1)); DrawRectangle(0, 0, sw, sh); }
} else {
    // The roll: text column in the middle, framed paintings drifting up at the sides.
    var offset = 740 - scrollT * scrollSpeed;
    DrawSetColorRgb(10, 6, 18); DrawSetAlpha(0.42);
    DrawRoundRectEx(360 * sx, -20 * sy, 920 * sx, 740 * sy, 24 * s, 0);
    for (var p = 0; p < PgListSize(thumbs); p += 1) {
        var ty = offset + thumbY[p];
        if (ty > -220 && ty < 740) {
            var tx = 40;
            if (p % 2 == 1) { tx = 920; }
            DrawSetColorRgb(255, 214, 130); DrawSetAlpha(0.9);
            DrawRoundRectEx((tx - 6) * sx, (ty - 6) * sy, (tx + 326) * sx, (ty + 186) * sy, 8 * s, 0);
            DrawSpritePart(thumbs[p], 0, 0, 0, 1, 1, tx * sx, ty * sy, 320 * sx, 180 * sy, 1);
        }
    }
    DrawSetFont("Georgia"); DrawSetTextAlign("center");
    for (var i = 0; i < PgListSize(creditText); i += 1) {
        var ly = offset + creditTop[i];
        var st = creditStyle[i];
        if (ly > -240 && ly < 740) {
            var edge = Clamp(Min(ly + 40, 720 - ly) / 90, 0, 1);
            if (st == 5) {
                DrawSpriteScaled(logo, 400 * sx, (ly + 6) * sy, 0, 1.5 * sx, 1.5 * sy, 0, edge);
            } else if (st != 4) {
                var size = 26;
                if (st == 0) { size = 19; DrawSetColorRgb(255, 200, 96); DrawSetTextTracking(4 * s); }
                if (st == 1) { size = 28; DrawSetColorRgb(255, 244, 222); DrawSetTextTracking(0); }
                if (st == 2) { size = 19; DrawSetColorRgb(206, 196, 220); DrawSetTextTracking(0); }
                if (st == 3) { size = 44; DrawSetColorRgb(255, 226, 150); DrawSetTextTracking(1 * s); }
                DrawSetAlpha(edge);
                DrawTextScaled(640 * sx, ly * sy, creditText[i], size * s);
            }
        }
    }
    DrawSetTextTracking(0);
    DrawSetTextAlign("left");
    var hintA = Clamp(scrollT / 2, 0, 1) * 0.6;
    DrawSetColorRgb(230, 220, 200); DrawSetAlpha(hintA * (1 - outFade));
    DrawTextScaled(24 * sx, 690 * sy, "hold ENTER / A to speed up      ESC / B  menu", 15 * s);
    CkDrawCrt(sw, sh, sy);
    var fadeIn = Clamp(1 - phaseT / 1.5, 0, 1);
    var fade = Max(fadeIn, outFade);
    if (fade > 0) { DrawSetColorRgb(0, 0, 0); DrawSetAlpha(fade); DrawRectangle(0, 0, sw, sh); }
}
DrawSetAlpha(1); DrawSetColorRgb(255, 255, 255);
"""

CREDITS_ROOMEND = """StopSound(music);
WindowSetCursorVisible(true);
"""


# ---------------------------------------------------------------- particles
def colour(r, g, b, a=1.0):
    return {"r": r, "g": g, "b": b, "a": a}


PARTICLE_BASE = {
    "preview2D": True, "maxParticles": 64, "emitRate": 0.0, "burstCount": 0, "loop": True,
    "duration": 1.0, "shape": "Box", "spreadDegrees": 30.0, "emitRadius": 0.1, "speed": 1.0,
    "speedVariance": 0.5, "gravity": 0.0, "drag": 1.0, "lifetime": 3.0, "lifetimeVariance": 0.3,
    "startSize": 0.3, "endSize": 0.1, "emissive": 1.2, "blendMode": "Additive", "pixelSampling": True,
    "startColor": colour(1, 1, 1, 1), "endColor": colour(1, 1, 1, 0),
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
    # 2D rooms use 12 px per effect unit; gravity is positive upwards.
    particle("Menu Fireflies", "Main Menu and Credits: warm fireflies rising out of the meadow grass. "
             "Looping; spawned once with SpawnParticleEmitter at the bottom-centre of a 640x360 room.",
             emitRate=5.0, maxParticles=60, boxSizeX=54.0, boxSizeY=2.0, boxSizeZ=0.0, speed=0.6,
             speedVariance=0.7, gravity=0.18, drag=0.4, turbulenceStrength=1.4, windX=0.15,
             lifetime=6.0, lifetimeVariance=0.35, startSize=0.32, endSize=0.12, emissive=1.6,
             startColor=colour(1.0, 0.92, 0.55, 0.0), midColor=colour(1.0, 0.84, 0.42, 1.0),
             endColor=colour(0.95, 0.6, 0.2, 0.0), colorJitter=0.15)
    particle("Menu Sparkles", "Main Menu: tiny white-gold twinkles around the Title Logo. Looping; "
             "spawned at the logo's centre (320, 64) in the 640x360 menu room.",
             emitRate=12.0, maxParticles=40, boxSizeX=27.0, boxSizeY=6.0, boxSizeZ=0.0, speed=0.15,
             speedVariance=1.0, gravity=0.05, drag=1.0, lifetime=0.7, lifetimeVariance=0.4,
             startSize=0.36, endSize=0.0, emissive=2.0, rotationSpeed=180, rotationVariance=1.0,
             startColor=colour(1.0, 1.0, 0.92, 1.0), midColor=colour(1.0, 0.9, 0.55, 1.0),
             endColor=colour(1.0, 0.75, 0.3, 0.0))
    particle("Festival Lanterns", "Credits: paper lanterns of the Lantern Festival drifting up past the "
             "credits. Looping; spawned just below the bottom of the 640x360 credits room.",
             emitRate=2.2, maxParticles=40, boxSizeX=52.0, boxSizeY=1.0, boxSizeZ=0.0, speed=0.9,
             speedVariance=0.4, gravity=0.35, drag=0.5, turbulenceStrength=0.7, windX=0.25,
             lifetime=10.0, lifetimeVariance=0.25, startSize=0.55, endSize=0.32, emissive=1.4,
             blendMode="Alpha",
             startColor=colour(1.0, 0.62, 0.28, 0.0), midColor=colour(1.0, 0.72, 0.36, 0.95),
             endColor=colour(1.0, 0.86, 0.5, 0.0), colorJitter=0.2)


# ---------------------------------------------------------------- objects and rooms
def object_json(script_class, events):
    return {
        "schemaVersion": 2,
        "dimension": "TwoD",
        "sprite": "",
        "model": "",
        "components": [
            {"type": "ScriptComponent", "enabled": True, "props": {"ScriptClass": script_class}}
        ],
        "events": events,
    }


def write_object(name, events):
    rel = "Objects/%s.object.json" % name
    write_json(rel, object_json(name, list(events.keys())))
    write_meta(rel, "GameObject", name)
    for event, code in events.items():
        write_text("Objects/%s/%s.pgsl" % (name, event), code)


def write_script(name, code):
    rel = "Scripts/%s.pgsl" % name
    write_text(rel, code)
    write_meta(rel, "Script", name)


def room(name, background_colour, nodes, post_effects):
    rel = "Rooms/%s.room.json" % name
    write_json(rel, {
        "$schema": "genesis.room", "version": 1, "id": guid("room:" + name), "name": name, "dimension": "TwoD",
        "settings": {"width": 640, "height": 360, "depth": 720, "windowWidth": 1280, "windowHeight": 720,
                     "scaleViewportsWithWindow": True, "targetFps": 60, "fixedFps": 60, "vsync": True,
                     "captureMouse": False, "gridSize": 32, "snapEnabled": True, "pixelArtSampling": True},
        "environment": {"backgroundColor": background_colour, "fogEnabled": False, "postEffects": post_effects},
        # NO viewport on purpose. A viewport with scaleViewportsWithWindow letterboxes the room at a
        # uniform scale, centred (RoomDisplayLayout.Port), leaving bands of the clear colour whenever the
        # window is not 16:9 (maximised, snapped, borderless on 16:10 or ultrawide). Everything these
        # rooms draw in DrawGui (and DrawUi) is scaled to the WHOLE window, so the backdrop and the
        # menu drifted apart and bands showed on the sides. Without a viewport the room takes the
        # single-camera path, whose StretchView backdrop fills the whole window exactly as the GUI does.
        "viewports": [],
        "layers": [{"id": "scenery", "name": "Backdrop", "order": 0, "enabled": True},
                   {"id": "actors", "name": "Controllers", "order": 2, "enabled": True}],
        "nodes": nodes,
    })
    write_meta(rel, "Room", name)


def background_node(room_name, label, asset):
    return {"id": guid(room_name + ":bg:" + label), "kind": "Background", "name": label, "enabled": True,
            "layerId": "scenery",
            "transform": {"position": [0, 0, 0], "rotation": [0, 0, 0], "scale": [1, 1, 1]},
            "background": {"asset": asset, "mode": "TwoD", "layout": "StretchView", "depth": 10000,
                           "repeatX": False, "opacity": 1}}


def instance_node(room_name, prefab):
    return {"id": guid(room_name + ":obj:" + prefab), "kind": "GameObject", "name": prefab, "enabled": True,
            "layerId": "actors", "order": 0,
            "transform": {"position": [0, 0, 0], "rotation": [0, 0, 0], "scale": [1, 1, 1]},
            "gameObject": {"prefab": "Assets/Objects/%s.object.json" % prefab}}


def main():
    write_script("Game Save", GAME_SAVE)
    write_script("Game Load", GAME_LOAD)
    write_script("New Game", NEW_GAME)
    write_script("Apply Options", APPLY_OPTIONS)
    write_script("Cinematic Kit", CINEMATIC_KIT)
    # The Options Kit script itself comes from options_kit_gen.py (README order 13a).
    groups = build_menu_ui()
    build_particles()
    write_object("Intro Director", {"Create": intro_create(), "Step": INTRO_STEP, "KeyPressed": INTRO_KEY,
                                    "DrawGui": INTRO_DRAWGUI, "RoomEnd": INTRO_ROOMEND})
    write_object("Menu Controller", {"Create": menu_create(groups), "Step": MENU_STEP, "DrawGui": MENU_DRAWGUI,
                               "RoomEnd": MENU_ROOMEND})
    write_object("Credits Director", {"Create": credits_create(), "Step": CREDITS_STEP, "KeyPressed": CREDITS_KEY,
                                      "DrawGui": CREDITS_DRAWGUI, "RoomEnd": CREDITS_ROOMEND})
    room("Intro", [0, 0, 0, 1], [instance_node("Intro", "Intro Director")], [])
    room("Main Menu", [0.08, 0.06, 0.14, 1],
         [background_node("Main Menu", "Menu backdrop", SPR + "Menu Backdrop.image.json"),
          instance_node("Main Menu", "Menu Controller")], ["SNES Look"])
    room("Credits", [0.05, 0.04, 0.1, 1],
         [background_node("Credits", "Credits backdrop", SPR + "Credits Backdrop.image.json"),
          instance_node("Credits", "Credits Director")], ["SNES Look"])
    print("Front end written to", ASSETS)
    print("  save fields:", len(SAVE_FIELDS), "credits lines:", len(CREDITS))


if __name__ == "__main__":
    main()
