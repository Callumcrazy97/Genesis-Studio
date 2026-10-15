"""Mushroom Meadow HERO content: Pip (the Explorer) and the shared gameplay rules.

Re-runnable and deterministic. Run from anywhere:
    python BuildTools/TemplateContent/MushroomMeadow/hero_gen.py
Writes into Source/Genesis.Application.Core/Projects/Templates/Assets/TwoDShowcase/Assets:

  Objects  Explorer (every event), Level Rules, Spore Shot, Shield Ring, Glowspore,
           power-up items Glide Seed / Spore Puff / Bark Shield / Starlit Firefly / Heart Berry,
           seed pod prize markers Prize Glide / Prize Spore / Prize Shield / Prize Star / Prize Heart,
           and the phase-1 enemies Acorn Walker and Buzz Bee adapted to the enemy convention.
  Shaders  Rainbow Glow (Sprite pipeline; Starlit Firefly invincibility).
  Particles Swim Bubbles, Water Splash, Starlit Sparkle.

The rules every other object relies on (globals, RoomValues, enemy convention) are documented in
the comments at the top of the Explorer and Level Rules Create events.
"""
import hashlib
import json
import os

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
ASSETS = os.path.join(ROOT, "Source", "Genesis.Application.Core", "Projects", "Templates",
                      "Assets", "TwoDShowcase", "Assets")
STAMP = "2026-10-11T00:00:00Z"


def guid(name):
    return hashlib.md5(("mushroom-meadow-hero:" + name).encode("utf-8")).hexdigest()


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
    """events: ordered list of (event name, PGSL source)."""
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
    if not events:
        return
    os.makedirs(folder, exist_ok=True)
    keep = set()
    for event, source in events:
        write_text("Objects/%s/%s.pgsl" % (name, event), source.strip("\n") + "\n")
        keep.add(event + ".pgsl")
    for stale in os.listdir(folder):
        if stale.endswith(".pgsl") and stale not in keep:
            os.remove(os.path.join(folder, stale))


ENEMY_NAMES = ["Acorn Walker", "Buzz Bee", "Flit Bat", "Glow Grub", "Bloatfin", "Driftjelly", "Dusk Moth",
               "Burr Hopper", "Grumblecrag", "Duskmantle", "Gloom Orb", "Pebble", "Stalactite"]
ENEMY_LIST = "[" + ", ".join('"%s"' % n for n in ENEMY_NAMES) + "]"
ITEM_NAMES = ["Glide Seed", "Spore Puff", "Bark Shield", "Starlit Firefly", "Heart Berry"]
ITEM_LIST = "[" + ", ".join('"%s"' % n for n in ITEM_NAMES) + "]"
PRIZES = [("Prize Glide", "Glide Seed"), ("Prize Spore", "Spore Puff"), ("Prize Shield", "Bark Shield"),
          ("Prize Star", "Starlit Firefly"), ("Prize Heart", "Heart Berry")]


def snd(name, volume, pitch="1"):
    return 'PlaySound("Assets/Audio/%s.audio.json", %s * sv, %s, false);' % (name, volume, pitch)


# ====================================================================== Explorer
EXPLORER_CREATE = r"""
// Pip, the hero of Mushroom Meadow. All tuning is editable here; the controller uses the sprite
// mask and room tile solidity. Jump physics: jumpSpeed 525 and gravity 1250 give about 106 px
// (3 tiles) of height and clear a gap of about 228 px at run speed (162 px walking), so 4-tile
// gaps are clearable at a run. BuildTools/TemplateContent/MushroomMeadow/hero_physics.py mirrors
// these numbers for the level reachability checks: change both together.
//
// RULES OTHER OBJECTS RELY ON (see Notes):
//  Globals (GlobalSet/GlobalGet): campaign (1 = started from the Main Menu), lives, coins, hearts,
//    maxHearts, power (0 none, 1 Glide Seed, 2 Spore Puff, 3 Bark Shield), clear1..5, medals1..5,
//    glowspores, mapNode, returnFromLevel, sfxVol, musicVol, crt, shake, muted.
//    Pip mirrors lives/coins/hearts/maxHearts/power into the globals every step. To change them
//    from another object (a shop), GlobalSet the new values then RoomValueSet("heroReload", 1),
//    or InstanceVariableSet on this instance (lives, coins, hearts, maxHearts, powerUp).
//  RoomValues read: frozen, levelId, hub, dark, underwater, timeLimit, windForce, dialogue,
//    musicChannel, ambienceChannel (Level Rules), heroReload.
//  RoomValues written: frozen, checkpoint, talk (1 for one frame when E/J/X is pressed in a hub),
//    talkX, talkY, starlit, paused, musicState (0 title, 1 play, 2 stopped).
//  Enemies: see the enemy convention in Notes; Pip adds 1 to an enemy's `hurt` per stomp.
spawnX = x; spawnY = y;
vx = 0; vy = 0; grounded = 1; wasGrounded = 1; coyote = 0; jumpBuffer = 0; fallPeak = 0; bounceLift = 0;
walkSpeed = 180; runSpeed = 260; jumpSpeed = 525; gravityAccel = 1250; bounceSpeed = 760;
score = 0; bigCoins = 0; checkpoint = 0; timeLimit = 300; timeLeft = 300; timeBonus = 0;
state = 0; invulnerable = 0; hurtTimer = 0; motion = -1; facing = 1; moveDirection = 0;
music = -1; ambience = -1; titleMusic = -1; hasRules = 0; rulesRead = 0;
// campaign: launched from the Main Menu (lives, hearts and power-ups carry between rooms).
// standalone (a room started directly, F5): phase-1 behaviour with a title card and restarts.
campaign = 0;
if (GlobalGet("campaign") == 1) { campaign = 1; }
lives = 5; coins = 0; maxHearts = 3; hearts = 3; powerUp = 0;
if (campaign == 1) {
    lives = GlobalGet("lives"); if (lives <= 0) { lives = 5; }
    coins = GlobalGet("coins");
    maxHearts = Clamp(GlobalGet("maxHearts"), 3, 6);
    hearts = GlobalGet("hearts"); if (hearts <= 0 || hearts > maxHearts) { hearts = maxHearts; }
    powerUp = Clamp(GlobalGet("power"), 0, 3);
}
entryCoins = coins; entryPower = powerUp; entryHearts = hearts;
// Publish at once: Step adopts any global that someone else changed since Pip last wrote it.
GlobalSet("lives", lives); GlobalSet("coins", coins); GlobalSet("hearts", hearts);
GlobalSet("maxHearts", maxHearts); GlobalSet("power", powerUp);
wroteLives = lives; wroteCoins = coins; wroteHearts = hearts; wroteMaxHearts = maxHearts; wrotePower = powerUp;
starTimer = 0; sparkleTimer = 0; shotCooldown = 0; throwTimer = 0; gliding = 0; swimming = 0;
swimSpeed = 120; swimBob = 0;
windVx = 0; riding = 0; contactGrace = 0; talking = 0;
levelId = 1; hub = 0; dark = 0; levelName = "MUSHROOM MEADOW";
menuItems = [0, 1, 2]; menuIndex = 0; menuNote = 0;
clearTimer = 0; clearKind = 0; clearJingle = 0; leaveReason = 0; overTimer = 0; bannerTimer = 0;
enemyNames = ENEMY_LIST;
itemNames = ITEM_LIST;
// Optional art: only switch to a sprite that exists, so a missing sheet never loses the mask.
hasSwim = ResourceExists("Explorer Swim"); hasGlide = ResourceExists("Explorer Glide");
hasThrow = ResourceExists("Explorer Throw"); hasVictory = ResourceExists("Explorer Victory");
hasTalk = ResourceExists("Explorer Talk"); hasEmptyHeart = ResourceExists("Icon Heart Empty");
hasRainbow = ResourceExists("Rainbow Glow");
// sv scales every effect; the sfx option volume itself is applied to the sfx bus (Apply Options).
sv = 1;
shakeOn = 1; if (GlobalExists("shake")) { shakeOn = GlobalGet("shake"); }
muted = GlobalGet("muted"); if (muted == 1) { SetMasterVolume(0); }
// Round 4 kits. Each is optional: when a Script is missing Pip falls back to the old behaviour.
hasInput = ResourceExists("Input Kit"); hasOptions = ResourceExists("Options Kit");
hasItems = ResourceExists("Item Kit"); hasRep = ResourceExists("Reputation Kit"); hasGore = ResourceExists("Gore Kit");
tipsOn = 1; if (GlobalExists("tips")) { tipsOn = GlobalGet("tips"); }
pauseMode = 0; hurtSource = 0; menuStick = 0; menuMouseX = -1; menuMouseY = -1;
repStageNow = 0; repStageSeen = -1; toastText = ""; toastTimer = 0; taintClock = 0; twitchTimer = 4; leakTimer = 0;
// Held item and arrows carry between rooms in the globals heldItem and arrows (Item Kit); a standalone
// room starts empty-handed. entryHeld/entryArrows are put back on a restart or Save and Quit.
if (campaign == 0) { GlobalSet("heldItem", 0); GlobalSet("arrows", 0); }
entryHeld = GlobalGet("heldItem"); entryArrows = GlobalGet("arrows");
if (hasInput == 1) { InputInit(); }
if (hasItems == 1) { ItemInit(); }
if (GlobalExists("crt")) {
    RoomValueSet("crt", GlobalGet("crt"));
    PostEffectSetParameter("SNES Look", "Scanlines", GlobalGet("crt"));
}
RoomValueSet("frozen", 1); RoomValueSet("checkpoint", 0); RoomValueSet("musicState", 0);
RoomValueSet("talk", 0); RoomValueSet("starlit", 0); RoomValueSet("paused", 0); RoomValueSet("heroReload", 0);
SpriteSet("Assets/Sprites/Explorer Idle.image.json"); AnimationPlay("Idle", true);
AnimationSetSpeed(0);
// Pixel Flash lights the hero up for a moment when hurt; Rainbow Glow replaces it while Starlit.
ShaderSet("Assets/Shaders/Pixel Flash.shader.json");
""".replace("ENEMY_LIST", ENEMY_LIST).replace("ITEM_LIST", ITEM_LIST)

EXPLORER_STEP = r"""
var dt = Clamp(DeltaTime, 0, 0.05);
// Input Kit: once per frame, before anything reads an action (detects the last-used device, sprint toggle).
if (hasInput == 1) { InputUpdate(); }
if (dt <= 0) { return; }
// Options > Gameplay > Enable Tips (live: it can change from the pause menu).
tipsOn = 1; if (GlobalExists("tips")) { tipsOn = GlobalGet("tips"); }

// ---- room rules (Level Rules publishes them in its Create; every Create runs before any Step)
if (rulesRead == 0) {
    rulesRead = 1;
    if (InstanceNumber("Level Rules") > 0) {
        hasRules = 1;
        levelName = InstanceVariableGet(InstanceFind("Level Rules", 0), "levelName");
    }
    levelId = RoomValueGet("levelId", 1); hub = RoomValueGet("hub", 0); dark = RoomValueGet("dark", 0);
    timeLimit = RoomValueGet("timeLimit", 300); timeLeft = timeLimit;
    if (hub == 1 || campaign == 1 || tipsOn == 0) {
        // Campaign levels, the village and (tips off) a standalone room skip the title card: play straight away.
        // The "About This Level" surfaces (title card, STAGE banner) only appear with Enable Tips on.
        state = 1; RoomValueSet("frozen", 0); RoomValueSet("musicState", 1);
        if (hub == 0 && tipsOn == 1) { bannerTimer = 2.6; }
        if (hasRules == 0) {
            music = PlaySound("Assets/Audio/Meadow Theme.audio.json", 0.6, 1, true);
            ambience = PlaySound("Assets/Audio/Meadow Ambience.audio.json", 0.35, 1, true);
        }
    } else {
        titleMusic = PlaySound("Assets/Audio/Title Theme.audio.json", 0.6, 1, true);
        music = titleMusic;
    }
    if (hasRules == 0) { ScriptExecute("Spawn Effect", 7, RoomGetWidth() / 2, -8); }
}
if (hasRules == 1 && state != 0) {
    music = RoomValueGet("musicChannel", -1); ambience = RoomValueGet("ambienceChannel", -1);
}
// A shop or script changed a global since Pip last wrote it (or asked with heroReload): adopt it.
var reload = RoomValueGet("heroReload", 0) == 1;
if (reload) { RoomValueSet("heroReload", 0); }
if (reload || GlobalGet("lives") != wroteLives) { lives = GlobalGet("lives"); }
if (reload || GlobalGet("coins") != wroteCoins) { coins = GlobalGet("coins"); }
if (reload || GlobalGet("maxHearts") != wroteMaxHearts) { maxHearts = Clamp(GlobalGet("maxHearts"), 3, 6); }
if (reload || GlobalGet("hearts") != wroteHearts) { hearts = Clamp(GlobalGet("hearts"), 1, maxHearts); }
if (reload || GlobalGet("power") != wrotePower) { powerUp = Clamp(GlobalGet("power"), 0, 3); }
GlobalSet("lives", lives); GlobalSet("coins", coins); GlobalSet("hearts", hearts);
GlobalSet("maxHearts", maxHearts); GlobalSet("power", powerUp);
wroteLives = lives; wroteCoins = coins; wroteHearts = hearts; wroteMaxHearts = maxHearts; wrotePower = powerUp;

// ---- input: every action comes from the Input Kit (bindings, both devices, sprint hold/toggle).
// Without the kit the old direct key and gamepad reads below keep the game playable.
var inLeft = 0; var inRight = 0; var inUp = 0; var inDown = 0;
var jumpHeld = 0; var jumpPressed = 0; var strokePressed = 0; var runHeld = 0;
var usePressed = 0; var useHeld = 0; var throwPressed = 0; var interactPressed = 0;
if (hasInput == 1) {
    var axisX = InputAxisX(); var axisY = InputAxisY();
    inLeft = InputDown("left") || axisX < -0.35;
    inRight = InputDown("right") || axisX > 0.35;
    // InputAxisY is in screen direction: up is -1, down is +1.
    inUp = InputDown("up") || axisY < -0.5;
    inDown = InputDown("down") || axisY > 0.5;
    jumpHeld = InputDown("jump");
    jumpPressed = InputPressed("jump");
    // A swimming stroke is the jump button, except when it is the same press as "up" (W swims up, no burst).
    strokePressed = jumpPressed && !InputPressed("up");
    runHeld = InputSprintHeld();
    usePressed = InputPressed("use"); useHeld = InputDown("use");
    throwPressed = InputPressed("throw"); interactPressed = InputPressed("interact");
} else {
    var padX = GamepadAxis("LeftX"); var padY = GamepadAxis("LeftY");
    inLeft = KeyCheck("A") || KeyCheck("Left") || GamepadCheck("Left") || padX < -0.35;
    inRight = KeyCheck("D") || KeyCheck("Right") || GamepadCheck("Right") || padX > 0.35;
    inUp = KeyCheck("W") || KeyCheck("Up") || GamepadCheck("Up") || padY > 0.5;
    inDown = KeyCheck("S") || KeyCheck("Down") || GamepadCheck("Down") || padY < -0.5;
    jumpHeld = KeyCheck("Space") || KeyCheck("W") || KeyCheck("Up") || GamepadCheck("A");
    jumpPressed = KeyPressed("Space") || KeyPressed("W") || KeyPressed("Up") || GamepadPressed("A");
    strokePressed = KeyPressed("Space") || GamepadPressed("A");
    runHeld = KeyCheck("Shift") || GamepadCheck("B") || GamepadCheck("RB");
    usePressed = KeyPressed("J") || GamepadPressed("X"); useHeld = KeyCheck("J") || GamepadCheck("X");
    throwPressed = KeyPressed("K"); interactPressed = KeyPressed("X") || KeyPressed("E") || GamepadPressed("Y");
}
// actionPressed: the "talk / use" press (hub interact, results screens, the Spore Puff).
var actionPressed = usePressed || interactPressed;
// Pause and the menus keep fixed keys (Esc/P/Start, Enter/Space/A) so a bad remap can never trap the player.
var pausePressed = KeyPressed("P") || KeyPressed("Escape") || GamepadPressed("Start");
var confirmPressed = KeyPressed("Enter") || KeyPressed("Space") || GamepadPressed("A");
var heldNow = GlobalGet("heldItem");
// talk lasts exactly one frame for every NPC, whatever order the steps run in.
RoomValueSet("talk", 0);
if (dark == 1) {
    var lantern = 140; if (starTimer > 0) { lantern = 220; }
    Light2DPoint(1, x, y - 20, lantern, 1.15, 1, 0.84, 0.58);
}

// 0 title, 1 playing, 2 paused, 3 complete, 4 game over, 5 leaving for another room.
if (state == 5) { return; }
if (state == 0) {
    if (KeyPressed("Enter") || KeyPressed("Space") || GamepadPressed("A") || GamepadPressed("Start")) {
        state = 1; RoomValueSet("frozen", 0);
        StopSound(titleMusic); RoomValueSet("musicState", 1);
        if (hasRules == 0) {
            music = PlaySound("Assets/Audio/Meadow Theme.audio.json", 0.6, 1, true);
            ambience = PlaySound("Assets/Audio/Meadow Ambience.audio.json", 0.35, 1, true);
        }
    }
    return;
}
if (state == 3) {
    clearTimer = clearTimer + dt;
    if (campaign == 1 && hub == 0) {
        // One clear sound only: the Finish Fanfare (~4.9 s) already carries the Glowspore motif, so a
        // Finish clear no longer adds Glowspore Get on top of it. Auto-leave waits for the fanfare.
        if (clearJingle == 0) { clearJingle = 1; }
        if (clearTimer > 7 || (clearTimer > 1.6 && (confirmPressed || actionPressed))) {
            leaveReason = 1; EventUser(3);
        }
    } else {
        if (KeyPressed("Enter") || KeyPressed("R") || GamepadPressed("A") || GamepadPressed("Start")) { RoomRestart(); }
    }
    return;
}
if (state == 4) {
    overTimer = overTimer + dt;
    if (campaign == 1) {
        // Game Over is ~4.8 s long: let it finish before the map loads (a press still skips it).
        if (overTimer > 4.9 || (overTimer > 1.2 && confirmPressed)) { leaveReason = 3; EventUser(3); }
    } else {
        if (KeyPressed("Enter") || KeyPressed("R") || GamepadPressed("A") || GamepadPressed("Start")) { RoomRestart(); }
    }
    return;
}
talking = RoomValueGet("dialogue", 0) == 1;
// Options page (Options Kit) opened from the pause menu: the world stays frozen and the kit owns the input.
if (state == 2 && pauseMode == 1) {
    var optionsAlive = 0;
    if (hasOptions == 1) { OptionsStep(); optionsAlive = OptionsIsOpen(); }
    if (!optionsAlive) {
        pauseMode = 0;
        if (ResourceExists("Apply Options")) { ScriptExecute("Apply Options"); }
        shakeOn = 1; if (GlobalExists("shake")) { shakeOn = GlobalGet("shake"); }
        tipsOn = 1; if (GlobalExists("tips")) { tipsOn = GlobalGet("tips"); }
    }
    return;
}
if (pausePressed && !talking) {
    SFX_PAUSE
    if (state == 1) {
        state = 2; RoomValueSet("frozen", 1); RoomValueSet("paused", 1); AnimationSetSpeed(0);
        menuIndex = 0; menuNote = 0; pauseMode = 0; menuStick = 0;
        menuMouseX = MouseX; menuMouseY = MouseY;
        // 0 Resume, 1 Options, 2 Save and Quit (Options only when the Options Kit exists).
        if (hasOptions == 1) { menuItems = [0, 1, 2]; } else { menuItems = [0, 2]; }
    } else { state = 1; RoomValueSet("frozen", 0); RoomValueSet("paused", 0); }
    return;
}
if (state == 2) {
    // Pause menu: arrows / W,S / d-pad / stick choose, the mouse points and clicks, Enter / Space / pad A
    // confirms, pad B resumes (Esc, P and Start resume too, above).
    var menuCount = PgListSize(menuItems);
    var menuMove = 0;
    var stickMenu = GamepadAxis("LeftY"); var stickDir = 0;
    if (stickMenu > 0.6) { stickDir = -1; }
    if (stickMenu < -0.6) { stickDir = 1; }
    if (stickDir != 0 && stickDir != menuStick) { menuMove = stickDir; }
    menuStick = stickDir;
    if (KeyPressed("W") || KeyPressed("Up") || GamepadPressed("Up")) { menuMove = -1; }
    if (KeyPressed("S") || KeyPressed("Down") || GamepadPressed("Down")) { menuMove = 1; }
    if (menuMove != 0) {
        menuIndex = (menuIndex + menuCount + menuMove) % menuCount; menuNote = 0; SFX_MOVE
    }
    var menuScaleX = WindowGetWidth() / 1280; var menuScaleY = WindowGetHeight() / 720;
    var mouseNowX = MouseX; var mouseNowY = MouseY;
    var mouseOver = -1;
    for (var mk = 0; mk < menuCount; mk = mk + 1) {
        if (mouseNowX >= 250 * menuScaleX && mouseNowX <= 640 * menuScaleX
            && mouseNowY >= (290 + mk * 40) * menuScaleY && mouseNowY <= (330 + mk * 40) * menuScaleY) { mouseOver = mk; }
    }
    if (mouseOver >= 0 && mouseOver != menuIndex && (Abs(mouseNowX - menuMouseX) > 2 || Abs(mouseNowY - menuMouseY) > 2)) {
        menuIndex = mouseOver; SFX_MOVE
    }
    menuMouseX = mouseNowX; menuMouseY = mouseNowY;
    var menuClick = 0;
    if (mouseOver >= 0 && MousePressed(MbLeft)) { menuIndex = mouseOver; menuClick = 1; }
    if (GamepadPressed("B")) { state = 1; RoomValueSet("frozen", 0); RoomValueSet("paused", 0); SFX_BACK return; }
    if (confirmPressed || menuClick == 1) {
        var choice = menuItems[menuIndex];
        SFX_SELECT
        if (choice == 0) { state = 1; RoomValueSet("frozen", 0); RoomValueSet("paused", 0); }
        if (choice == 1 && hasOptions == 1) { OptionsOpen(1); pauseMode = 1; }
        if (choice == 2) { leaveReason = 4; EventUser(3); }
    }
    return;
}

// ---- playing
if (bannerTimer > 0) { bannerTimer = Max(0, bannerTimer - dt); }
if (hub == 0) { timeLeft = Max(0, timeLeft - dt); }
invulnerable = Max(0, invulnerable - dt);
hurtTimer = Max(0, hurtTimer - dt);
contactGrace = Max(0, contactGrace - dt);
shotCooldown = Max(0, shotCooldown - dt);
throwTimer = Max(0, throwTimer - dt);
if (starTimer > 0) {
    starTimer = Max(0, starTimer - dt);
    sparkleTimer = sparkleTimer - dt;
    if (sparkleTimer <= 0) {
        sparkleTimer = 0.12;
        ParticleBurstAt("Assets/Particles/Starlit Sparkle.particle.json", x, y - 18, 0, 1, 0);
    }
    if (starTimer <= 0) { ShaderSet("Assets/Shaders/Pixel Flash.shader.json"); RoomValueSet("starlit", 0); }
}
if (hub == 0 && timeLeft <= 0) { EventUser(1); return; }
// Village: Interact talks; Use talks too while Pip holds nothing (a held item keeps Use for itself).
if (hub == 1 && !talking && (interactPressed || (usePressed && heldNow == 0))) {
    RoomValueSet("talk", 1); RoomValueSet("talkX", x); RoomValueSet("talkY", y);
}

// ---- reputation (hidden lore: only ever hinted). Slow kindness recovery, a toast when the stage
// changes and a faint spore leak from the cap at stage 2 and up. The cap's colour is the Taint shader
// parameters, set with the other shader parameters at the end of Step.
repStageNow = 0;
if (hasRep == 1) {
    RepDecay(dt);
    repStageNow = RepStage();
    if (repStageSeen < 0) { repStageSeen = repStageNow; }
    if (repStageNow != repStageSeen) {
        if (repStageNow < repStageSeen) { toastText = "Your cap feels lighter."; }
        else if (repStageNow == 1) { toastText = "Your cap tingles."; }
        else if (repStageNow == 2) { toastText = "The spots on your cap seem to watch."; }
        else { toastText = "Your cap hums when nobody is near."; }
        toastTimer = 4.5; repStageSeen = repStageNow;
    }
    if (repStageNow >= 2) {
        leakTimer = leakTimer - dt;
        if (leakTimer <= 0) {
            leakTimer = 0.7 - (repStageNow - 2) * 0.25;
            ParticleBurstAt("Assets/Particles/Pip Spore Leak.particle.json", x, y - 38, 0, 1, 0);
        }
    }
}
toastTimer = Max(0, toastTimer - dt);

// ---- water: the whole room (RoomValue underwater) or a non-solid tile layer named "Water"
var inWater = RoomValueGet("underwater", 0) == 1 || TileIndexAt(x, y - 16, "Water") >= 0;
if (inWater && swimming == 0) {
    swimming = 1; gliding = 0; riding = 0;
    if (vy > 120) {
        SFX_SPLASH
        ParticleBurstAt("Assets/Particles/Water Splash.particle.json", x, y - 10, 0, 1, 0);
    }
    vy = vy * 0.35;
}
if (!inWater && swimming == 1) {
    swimming = 0;
    if (jumpHeld) { vy = Min(vy, -420); }
}

// ---- Moving Leaf platforms carry Pip (platformDx/platformDy/platformTop/platformW)
if (riding > 0) {
    if (!InstanceExists(riding)) { riding = 0; }
    else {
        x = TileMoveX(x, y, InstanceVariableGet(riding, "platformDx"));
        y = InstanceVariableGet(riding, "platformTop");
        // Standing on the leaf: no fall speed builds up (it used to sink Pip up to 11 px a frame).
        if (vy > 0) { vy = 0; }
        var leafX = InstanceGetX(riding);
        if (x < leafX - 6 || x > leafX + InstanceVariableGet(riding, "platformW") + 6) { riding = 0; }
    }
}
// Never stay embedded in solid tiles (a crumble block back under Pip, a leaf under a ledge, a
// respawn): step out the shortest way, trying up first.
if (TileMeeting(x, y)) {
    riding = 0;
    for (var uk = 1; uk <= 40; uk = uk + 1) {
        if (!TileMeeting(x, y - uk)) { y = y - uk; break; }
        if (!TileMeeting(x - uk, y)) { x = x - uk; break; }
        if (!TileMeeting(x + uk, y)) { x = x + uk; break; }
    }
}

// Buffered input and coyote time are in seconds, not a particular machine's frame rate.
wasGrounded = grounded;
grounded = 0;
if (swimming == 0 && (TileMeeting(x, y + 1) || riding > 0)) { grounded = 1; coyote = 0.10; }
else { coyote = Max(0, coyote - dt); }
if (swimming == 1) { coyote = 0; }
jumpBuffer = Max(0, jumpBuffer - dt);
if (jumpPressed && !talking && swimming == 0) { jumpBuffer = 0.12; }
if (swimming == 0 && jumpBuffer > 0 && coyote > 0) {
    vy = -jumpSpeed; grounded = 0; coyote = 0; jumpBuffer = 0; bounceLift = 0; riding = 0;
    SFX_JUMP
}
// Variable jump: releasing early cuts the rise. A mushroom bounce keeps its full lift.
if (vy >= 0) { bounceLift = 0; }
if (swimming == 0 && bounceLift == 0 && !jumpHeld && vy < -220) { vy = -220; }
moveDirection = 0;
if (!talking) {
    if (inLeft) { moveDirection = moveDirection - 1; }
    if (inRight) { moveDirection = moveDirection + 1; }
}
var swimDirY = 0;
if (swimming == 1 && !talking) {
    if (inUp) { swimDirY = swimDirY - 1; }
    if (inDown) { swimDirY = swimDirY + 1; }
}
if (swimming == 1) {
    // Swimming: free 8-direction movement at a steady speed, eased by the water's drag.
    // Sprint does nothing underwater. The jump button is a stroke burst the way Pip is swimming (up by default).
    var swimDiag = 1; if (moveDirection != 0 && swimDirY != 0) { swimDiag = 0.7071; }
    vx = vx + (moveDirection * swimSpeed * swimDiag - vx) * Min(1, dt * 4);
    if (!talking && strokePressed) {
        var strokeX = moveDirection * swimDiag; var strokeY = swimDirY * swimDiag;
        if (moveDirection == 0 && swimDirY == 0) { strokeY = -1; }
        vx = vx + strokeX * 200; vy = vy + strokeY * 200;
        SFX_STROKE
        ParticleBurstAt("Assets/Particles/Swim Bubbles.particle.json", x + facing * 6, y - 30, 0, 1, 0);
    }
} else {
    var targetSpeed = moveDirection * walkSpeed;
    if (runHeld) { targetSpeed = moveDirection * runSpeed; }
    vx = vx + (targetSpeed - vx) * Min(1, dt * 16);
}
// Wind (RoomValue windForce, px/s^2) pushes Pip, less while standing on the ground and hardly at all while he
// stands still there (a gust nudges him only a few px/s; walking keeps the old grip).
var windForce = RoomValueGet("windForce", 0);
if (windForce != 0 || windVx != 0) {
    var windGrip = 1;
    if (grounded == 1) {
        windGrip = 0.35;
        if (moveDirection == 0) { windGrip = 0.06; }
    }
    windVx = windVx + windForce * windGrip * dt;
    windVx = windVx - windVx * Min(1, dt * 2.2);
}
var requestedX = x + (vx + windVx) * dt;
var startX = x;
x = TileMoveX(x, y, (vx + windVx) * dt);
if (Abs(x - requestedX) > 0.001) {
    // Ledge step-up: a wall whose top is at most 6 px above Pip's feet (a jump that only just
    // clipped a ledge corner) is climbed instead of stopping him and dropping him down the side.
    var stepped = 0;
    if (swimming == 0 && vy >= 0) {
        for (var sk = 1; sk <= 6 && stepped == 0; sk = sk + 1) {
            if (!TileMeeting(startX, y - sk)) {
                var stepX = TileMoveX(startX, y - sk, requestedX - startX);
                if (Abs(stepX - requestedX) <= 0.001) { x = stepX; y = y - sk; stepped = 1; }
            }
        }
    }
    if (stepped == 0) { vx = 0; windVx = 0; }
}
x = Clamp(x, 8, RoomGetWidth() - 8);
var oldY = y;
if (swimming == 1) {
    var swimDiagY = 1; if (moveDirection != 0 && swimDirY != 0) { swimDiagY = 0.7071; }
    if (swimDirY != 0) {
        vy = vy + (swimDirY * swimSpeed * swimDiagY - vy) * Min(1, dt * 4);
    } else {
        // Near-neutral buoyancy: a gentle sink, water drag and a soft idle bob.
        swimBob = (swimBob + dt * 120) % 360;
        vy = vy + (40 + Cos(swimBob) * 36) * dt;
        vy = vy - vy * Min(1, dt * 1.4);
    }
    vy = Clamp(vy, -280, 220);
    vx = Clamp(vx, -280, 280);
} else {
    vy = Min(vy + gravityAccel * dt, 680);
    // Glide Seed: hold jump while falling to drift down under the dandelion parasol.
    if (powerUp == 1 && grounded == 0 && vy > 0 && jumpHeld) {
        if (gliding == 0) { SFX_GLIDE }
        gliding = 1; vy = Min(vy, 75);
    } else { gliding = 0; }
}
var descending = 0;
if (vy > 0) { descending = 1; }
if (grounded == 0) { fallPeak = Max(fallPeak, vy); }
var requestedY = y + vy * dt;
var preY = y;
y = TileMoveY(x, y, vy * dt);
if (Abs(y - requestedY) > 0.001 && vy < 0) {
    // Ceiling corner nudge: a head that clips a block's corner by 5 px or less slides past it
    // instead of killing the jump. A block hit squarely (seed pod, stone) is still bumped.
    for (var nk = 1; nk <= 5; nk = nk + 1) {
        if (!TileMeeting(x + nk, requestedY) && !TileMeeting(x + nk, preY)) { x = x + nk; y = requestedY; break; }
        if (!TileMeeting(x - nk, requestedY) && !TileMeeting(x - nk, preY)) { x = x - nk; y = requestedY; break; }
    }
}
if (Abs(y - requestedY) > 0.001) {
    if (vy < 0) {
        // Only the Blocks tile layer is destructible. Ground and pipes stay intact.
        var probeX = x;
        var block = TileIndexAt(probeX, y - 35, "Blocks");
        if (block < 0) { probeX = x - 6; block = TileIndexAt(probeX, y - 35, "Blocks"); }
        if (block < 0) { probeX = x + 6; block = TileIndexAt(probeX, y - 35, "Blocks"); }
        var blockX = Floor(probeX / 32) * 32 + 16;
        var blockY = Floor((y - 35) / 32) * 32 + 16;
        if (block == 3) {
            TileSetAt(probeX, y - 35, 4, 0, "Blocks");
            // A Prize marker at the block's centre makes it sprout a power-up instead of a coin.
            var prizeMarker = 0; var prizeItem = "";
PRIZE_LOOKUP
            if (prizeMarker > 0) {
                InstanceDestroy(prizeMarker);
                CreateInstance(prizeItem, blockX, blockY, 0);
                SFX_SPROUT
            } else {
                coins = coins + 1; score = score + 100;
                SFX_COIN
                ScriptExecute("Spawn Effect", 0, blockX, blockY - 24);
            }
        }
        if (block == 2) {
            TileRemoveAt(probeX, y - 35, "Blocks"); score = score + 50;
            SFX_BLOCK
            ScriptExecute("Spawn Effect", 6, probeX, y - 40);
        }
        if (block == 4) { SFX_BUMP }
    }
    vy = 0;
}
// Land on a Moving Leaf when falling through its walking surface.
if (riding == 0 && swimming == 0 && vy >= 0) {
    var leaves = InstanceNumber("Moving Leaf");
    for (var li = 0; li < leaves; li = li + 1) {
        var leaf = InstanceFind("Moving Leaf", li);
        var leafTop = InstanceVariableGet(leaf, "platformTop");
        var leafLeft = InstanceGetX(leaf);
        if (x >= leafLeft - 4 && x <= leafLeft + InstanceVariableGet(leaf, "platformW") + 4
            && oldY <= leafTop + 2 + Max(0, InstanceVariableGet(leaf, "platformDy")) && y >= leafTop - 1) {
            y = leafTop; vy = 0; riding = leaf;
            break;
        }
    }
}
if (swimming == 0 && (TileMeeting(x, y + 1) || riding > 0)) { grounded = 1; } else { grounded = 0; }
// A swimmer never stands on the seabed: touching it lifts him off again.
if (swimming == 1 && TileMeeting(x, y + 1)) { vy = Min(vy, -45); }
// Landing: a real fall (not a walk over a seam) plays the Land sound and kicks up dust.
if (grounded == 1 && wasGrounded == 0) {
    if (fallPeak > 260) {
        SFX_LAND
        ScriptExecute("Spawn Effect", 2, x, y);
    }
    fallPeak = 0;
}
// Falling out of the room costs a life at once.
if (y > RoomGetHeight() + 64) { EventUser(1); return; }

// ---- Item Kit (after movement): the held item acts on Use (sword swing, raised shield, bow draw,
// propeller flower), and Throw lets go of it. Base movement above never depends on any of this.
if (hasItems == 1 && !talking) {
    ItemStep(dt);
    if (throwPressed && GlobalGet("heldItem") > 0) { ItemThrow(); }
}

// Hazard tiles (thorns, spikes, urchins) live on the non-solid "Hazards" tile layer (index 32).
var thorn = 0;
if (TileIndexAt(x, y - 4, "Hazards") == 32 || TileIndexAt(x - 6, y - 4, "Hazards") == 32) { thorn = 1; }
if (TileIndexAt(x + 6, y - 4, "Hazards") == 32 || TileIndexAt(x, y - 20, "Hazards") == 32) { thorn = 1; }
if (thorn == 1 && invulnerable <= 0 && starTimer <= 0 && hub == 0) { hurtSource = 1; EventUser(0); return; }

// ---- pickups
var hit = CollisionRectangle(x - 7, y - 34, x + 7, y, "Sun Coin");
if (hit > 0) {
    ScriptExecute("Spawn Effect", 0, InstanceGetX(hit), InstanceGetY(hit));
    InstanceDestroy(hit); coins = coins + 1; score = score + 100;
    SFX_COIN2
}
hit = CollisionRectangle(x - 7, y - 34, x + 7, y, "Big Sun Coin");
if (hit > 0) {
    ScriptExecute("Spawn Effect", 0, InstanceGetX(hit), InstanceGetY(hit));
    InstanceDestroy(hit); coins = coins + 5; score = score + 1000; bigCoins = bigCoins + 1;
    SFX_BIGCOIN
}
// Power-ups are found by distance, so they work whatever their sprite's mask is.
for (var ii = 0; ii < 5; ii = ii + 1) {
    var item = InstanceNearest(x, y - 17, itemNames[ii]);
    if (item > 0 && PointDistance(InstanceGetX(item), InstanceGetY(item), x, y - 17) < 24) {
        ScriptExecute("Spawn Effect", 0, InstanceGetX(item), InstanceGetY(item));
        InstanceDestroy(item); score = score + 500;
        if (ii <= 2) {
            if (powerUp == ii + 1) { score = score + 500; }
            powerUp = ii + 1;
            SFX_POWERUP
        }
        if (ii == 3) {
            starTimer = 10; sparkleTimer = 0;
            if (hasRainbow) { ShaderSet("Assets/Shaders/Rainbow Glow.shader.json"); }
            RoomValueSet("starlit", 1);
            SFX_STARLIT
        }
        if (ii == 4) {
            if (hearts < maxHearts) { hearts = hearts + 1; } else { score = score + 500; }
            SFX_HEART
        }
    }
}
if (powerUp == 3 && InstanceNumber("Shield Ring") == 0) { CreateInstance("Shield Ring", x, y, 0); }

// ---- enemies (convention: enemy, hp, stompable, contactDamage, points, dead, hurt)
if (contactGrace <= 0) {
    for (var ei = 0; ei < PgListSize(enemyNames); ei = ei + 1) {
        var foeName = enemyNames[ei];
        var foe = CollisionRectangle(x - 7, y - 34, x + 7, y, foeName);
        if (foe > 0 && InstanceVariableGet(foe, "dead") == 0) {
            var isBoss = foeName == "Grumblecrag" || foeName == "Duskmantle";
            if (starTimer > 0 && !isBoss) {
                // Starlit: everything but a boss is knocked out on contact.
                // Damage convention: hitKind 6 (starlit touch), hitDir away from Pip, hitDmg what it takes.
                var starDmg = Max(1, InstanceVariableGet(foe, "hp"));
                InstanceVariableSet(foe, "hitKind", 6); InstanceVariableSet(foe, "hitDir", facing);
                InstanceVariableSet(foe, "hitDmg", starDmg);
                InstanceVariableSet(foe, "hitX", InstanceGetX(foe)); InstanceVariableSet(foe, "hitY", InstanceGetY(foe) - 12);
                InstanceVariableSet(foe, "hurt", InstanceVariableGet(foe, "hurt") + starDmg);
                score = score + InstanceVariableGet(foe, "points"); contactGrace = 0.05;
                SFX_STOMP
                ScriptExecute("Spawn Effect", 1, InstanceGetX(foe), InstanceGetY(foe));
            } else {
                // Find the foe's top edge under Pip with thin probes.
                var foeTop = y - 60;
                for (var py = y - 60; py <= y; py = py + 2) {
                    if (CollisionRectangle(x - 7, py, x + 7, py + 2, foeName) == foe) { foeTop = py; break; }
                }
                if (InstanceVariableGet(foe, "stompable") == 1 && descending == 1 && oldY <= foeTop + 8) {
                    y = foeTop - 2; vy = -340; grounded = 0; bounceLift = 0; riding = 0;
                    if (swimming == 1) { vy = -180; }
                    if (foeName == "Buzz Bee") { vy = -380; }
                    // Damage convention: hitKind 1 (stomp), hitDir the way Pip faces, 1 damage at the foe's top edge.
                    InstanceVariableSet(foe, "hitKind", 1); InstanceVariableSet(foe, "hitDir", facing);
                    InstanceVariableSet(foe, "hitDmg", 1);
                    InstanceVariableSet(foe, "hitX", InstanceGetX(foe)); InstanceVariableSet(foe, "hitY", foeTop);
                    InstanceVariableSet(foe, "hurt", InstanceVariableGet(foe, "hurt") + 1);
                    score = score + InstanceVariableGet(foe, "points"); contactGrace = 0.12;
                    SFX_STOMP
                    ScriptExecute("Spawn Effect", 1, InstanceGetX(foe), InstanceGetY(foe));
                } else if (InstanceVariableGet(foe, "contactDamage") > 0) {
                    // contactDamage 0 (e.g. a flipped boss shell) is harmless to touch.
                    if (invulnerable <= 0 && starTimer <= 0 && hub == 0) { EventUser(0); return; }
                }
            }
            break;
        }
    }
}
hit = CollisionRectangle(x - 7, y - 34, x + 7, y, "Bounce Mushroom");
if (hit > 0 && descending == 1 && oldY <= InstanceGetY(hit) - 14) {
    y = InstanceGetY(hit) - 20; vy = -bounceSpeed; grounded = 0; bounceLift = 1; fallPeak = 0; riding = 0;
    // The mushroom reads this room value and plays its squash ("Bounce") animation.
    RoomValueSet(StringJoin2("bounce_", StringOf(hit)), 1);
    SFX_BOUNCE
    ScriptExecute("Spawn Effect", 3, InstanceGetX(hit), InstanceGetY(hit) - 20);
}
if (checkpoint == 0 && PlaceMeeting(x, y, "Checkpoint")) {
    checkpoint = 1;
    var flag = CollisionRectangle(x - 7, y - 34, x + 7, y, "Checkpoint");
    spawnX = InstanceGetX(flag) + 24; spawnY = InstanceGetY(flag);
    RoomValueSet("checkpoint", 1); score = score + 250;
    SFX_CHECKPOINT
    ScriptExecute("Spawn Effect", 0, InstanceGetX(flag), InstanceGetY(flag) - 64);
}
// ---- goal: the summit Finish flag or a Glowspore clears the level
var goal = 0;
if (PlaceMeeting(x, y, "Finish")) { goal = 1; }
var orb = InstanceNearest(x, y - 17, "Glowspore");
if (orb > 0 && PointDistance(InstanceGetX(orb), InstanceGetY(orb), x, y - 17) < 30
    && InstanceVariableGet(orb, "ready") == 1) {
    goal = 2; InstanceDestroy(orb);
}
if (goal > 0 && hub == 0) { clearKind = goal; EventUser(2); return; }

// ---- Spore Puff: throw a bouncing spore (two on screen at most). Use does this only while Pip holds no item.
if (powerUp == 2 && usePressed && GlobalGet("heldItem") == 0 && hub == 0 && !talking
    && shotCooldown <= 0 && InstanceNumber("Spore Shot") < 2) {
    CreateInstance("Spore Shot", x + facing * 12, y - 22, 0);
    shotCooldown = 0.22; throwTimer = 0.22;
    SFX_THROW
}

// 0 idle, 1 run, 2 jump (rising), 3 fall, 4 hurt, 5 swim, 6 glide, 7 throw, 8 talk.
var nextMotion = 0;
if (Abs(vx) > 12) { nextMotion = 1; }
if (grounded == 0) {
    nextMotion = 2;
    if (vy > 60) { nextMotion = 3; }
}
if (gliding == 1 && hasGlide) { nextMotion = 6; }
if (swimming == 1 && hasSwim) { nextMotion = 5; }
if (throwTimer > 0 && hasThrow) { nextMotion = 7; }
if (talking && hasTalk) { nextMotion = 8; }
if (hurtTimer > 0) { nextMotion = 4; }
if (nextMotion != motion) {
    if (nextMotion == 0) { SpriteSet("Assets/Sprites/Explorer Idle.image.json"); AnimationPlay("Idle", true); }
    if (nextMotion == 1) { SpriteSet("Assets/Sprites/Explorer Run.image.json"); AnimationPlay("Run", true); }
    if (nextMotion == 2) { SpriteSet("Assets/Sprites/Explorer Jump.image.json"); AnimationPlay("Jump", true); }
    if (nextMotion == 3) { SpriteSet("Assets/Sprites/Explorer Fall.image.json"); AnimationPlay("Fall", true); }
    if (nextMotion == 4) { SpriteSet("Assets/Sprites/Explorer Hurt.image.json"); AnimationPlay("Hurt", true); }
    if (nextMotion == 5) { SpriteSet("Assets/Sprites/Explorer Swim.image.json"); AnimationPlay("Swim", true); }
    if (nextMotion == 6) { SpriteSet("Assets/Sprites/Explorer Glide.image.json"); AnimationPlay("Glide", true); }
    if (nextMotion == 7) { SpriteSet("Assets/Sprites/Explorer Throw.image.json"); AnimationPlay("Throw", false); }
    if (nextMotion == 8) { SpriteSet("Assets/Sprites/Explorer Talk.image.json"); AnimationPlay("Talk", true); }
    motion = nextMotion;
}
if (moveDirection < 0) { image_xscale = -1; facing = -1; }
if (moveDirection > 0) { image_xscale = 1; facing = 1; }
AnimationSetSpeed(1);
if (motion == 1) { AnimationSetSpeed(Max(0.5, Abs(vx) / walkSpeed)); }
if (motion == 5) { AnimationSetSpeed(Clamp(0.3 + PointDistance(0, 0, vx, vy) / 140, 0.3, 1.6)); }
image_alpha = 1;
if (starTimer > 0) {
    var glow = 1; if (starTimer < 2 && Floor(starTimer * 8) % 2 == 0) { glow = 0.35; }
    ShaderSetParameter("Strength", glow);
} else {
    ShaderSetParameter("Flash", hurtTimer > 0.25);
    if (invulnerable > 0 && Floor(invulnerable * 12) % 2 == 0) { image_alpha = 0.35; }
    // Pixel Flash Taint (0..1) darkens the cap with the reputation; TaintPulse (0..1) is a slow throb with
    // a brief twitch now and then at stage 2 and up. Both stay 0 (unchanged picture) without a reputation.
    var taintNow = 0; var taintPulseNow = 0;
    if (hasRep == 1) {
        taintNow = Clamp(RepTaintAmount(), 0, 1);
        taintClock = (taintClock + dt * 70) % 360;
        taintPulseNow = taintNow * (0.5 + 0.5 * Sin(taintClock));
        twitchTimer = twitchTimer - dt;
        if (repStageNow >= 2 && twitchTimer < 0.12) {
            taintPulseNow = 1;
            if (twitchTimer <= 0) { twitchTimer = 3 + Random(5); }
        }
    }
    ShaderSetParameter("Taint", taintNow);
    ShaderSetParameter("TaintPulse", taintPulseNow);
}
"""

EXPLORER_HURT = r"""
// User Event 0: Pip takes one hit (enemy contact, hazard tile, enemy projectile).
// Bark Shield breaks first; otherwise one heart and the current power-up are lost.
// Enemy projectiles need nothing special: Pip's enemy check calls this event when they touch him.
var fromHazard = hurtSource; hurtSource = 0;
if (state != 1 || hub == 1 || invulnerable > 0 || starTimer > 0) { return; }
// A raised Shield (Item Kit) absorbs enemy hits and projectiles; thorns and spikes still hurt.
if (hasItems == 1 && fromHazard == 0) {
    if (ItemOnHurt(1) == 1) { invulnerable = 0.5; contactGrace = 0.2; return; }
}
ScriptExecute("Spawn Effect", 5, x, y - 20);
if (shakeOn == 1) { ViewShake(0, 3, 0.2); }
GamepadVibrate(0.4, 0.2, 0.15);
// A tainted Pip leaks a puff of spores when he is hurt (Gore Kit).
if (hasGore == 1 && hasRep == 1 && RepStage() >= 1) { GoreSpore(x, y - 24); }
if (powerUp == 3) {
    powerUp = 0; invulnerable = 1.2; hurtTimer = 0.3; vy = -260;
    SFX_SHIELD
    return;
}
hearts = hearts - 1;
if (powerUp > 0) { powerUp = 0; SFX_POWERDOWN }
invulnerable = 1.5; hurtTimer = 0.45;
vx = -facing * 90; vy = -260; jumpBuffer = 0; gliding = 0; riding = 0;
SFX_HURT
if (hearts <= 0) { EventUser(1); }
"""

EXPLORER_LOSE_LIFE = r"""
// User Event 1: Pip loses a life (hearts ran out, a pit, or the timer). Respawn at the start or
// the lit checkpoint with full hearts; no lives left is game over.
if (state != 1) { return; }
ScriptExecute("Spawn Effect", 5, x, y - 20);
if (shakeOn == 1) { ViewShake(0, 5, 0.3); }
lives = lives - 1; hearts = maxHearts; powerUp = 0; invulnerable = 2; hurtTimer = 0.45;
vx = 0; vy = 0; windVx = 0; jumpBuffer = 0; coyote = 0; fallPeak = 0; bounceLift = 0; riding = 0; gliding = 0;
if (hub == 0) { timeLeft = timeLimit; }
if (starTimer > 0) { starTimer = 0; ShaderSet("Assets/Shaders/Pixel Flash.shader.json"); RoomValueSet("starlit", 0); }
SFX_HURT
GlobalSet("lives", lives); wroteLives = lives;
if (lives <= 0) {
    state = 4; overTimer = 0; RoomValueSet("frozen", 1); RoomValueSet("musicState", 2); AnimationSetSpeed(0);
    StopSound(music); StopSound(ambience);
    SFX_GAMEOVER
} else {
    // Respawn at the start, or at the midpoint flag once it is lit.
    x = spawnX; y = spawnY; grounded = 1; wasGrounded = 1;
    ViewSetPosX(0, Max(0, x - 200)); ViewSetPosY(0, Clamp(y - 250, 0, Max(0, RoomGetHeight() - 360)));
}
"""

EXPLORER_CLEAR = r"""
// User Event 2: level clear (clearKind 1 = Finish flag, 2 = Glowspore).
if (state != 1) { return; }
state = 3; clearTimer = 0; clearJingle = 0;
timeBonus = ScriptExecute("Time Bonus", timeLeft);
score = score + timeBonus;
RoomValueSet("frozen", 1); RoomValueSet("musicState", 2);
StopSound(music); StopSound(ambience);
if (starTimer > 0) { starTimer = 0; ShaderSet("Assets/Shaders/Pixel Flash.shader.json"); RoomValueSet("starlit", 0); }
if (hasVictory) {
    SpriteSet("Assets/Sprites/Explorer Victory.image.json"); AnimationPlay("Victory", true); AnimationSetSpeed(1);
    motion = 9;
} else { AnimationSetSpeed(0); }
if (clearKind == 2) { SFX_GLOWSPORE } else { SFX_FINISH }
ScriptExecute("Spawn Effect", 4, x, y - 96);
"""

EXPLORER_LEAVE = r"""
// User Event 3 (campaign): leave the level for the Overworld map.
// leaveReason 1 = cleared (records clear<id>, medals<id>, glowspores, returnFromLevel),
// 2 = Return to map, 3 = game over (lives refilled to 5), 4 = Save and Quit from the pause menu
// (saves the game, then the Main Menu; a level in progress keeps what Pip entered it with).
if (leaveReason == 4) {
    if (campaign == 1) {
        if (hub == 0) {
            coins = entryCoins; powerUp = entryPower; hearts = entryHearts;
            GlobalSet("heldItem", entryHeld); GlobalSet("arrows", entryArrows);
        }
        GlobalSet("lives", lives); GlobalSet("coins", coins); GlobalSet("hearts", hearts);
        GlobalSet("maxHearts", maxHearts); GlobalSet("power", powerUp);
        if (levelId >= 1 && levelId <= 5) { GlobalSet("mapNode", levelId); }
        if (ResourceExists("Game Save")) { ScriptExecute("Game Save"); }
    }
    state = 5; RoomValueSet("frozen", 1); RoomValueSet("paused", 0);
    RoomChangeFade(0.4);
    if (ResourceExists("Main Menu")) { RoomGoto("Main Menu"); } else { RoomRestart(); }
    return;
}
if (leaveReason == 1 && levelId >= 1 && levelId <= 5) {
    GlobalSet(StringJoin2("clear", StringOf(levelId)), 1);
    var medalKey = StringJoin2("medals", StringOf(levelId));
    GlobalSet(medalKey, Max(GlobalGet(medalKey), bigCoins));
    var found = 0;
    for (var level = 1; level <= 5; level = level + 1) {
        if (GlobalGet(StringJoin2("clear", StringOf(level))) == 1) { found = found + 1; }
    }
    GlobalSet("glowspores", found);
    GlobalSet("returnFromLevel", levelId);
    if (levelId == 5) { GlobalSet("gameComplete", 1); }
}
if (leaveReason == 2 && hub == 0) {
    coins = entryCoins; powerUp = entryPower; hearts = entryHearts;
    GlobalSet("heldItem", entryHeld); GlobalSet("arrows", entryArrows);
}
if (leaveReason == 3) { lives = 5; hearts = maxHearts; powerUp = 0; }
if (levelId >= 1 && levelId <= 5) { GlobalSet("mapNode", levelId); }
GlobalSet("lives", lives); GlobalSet("coins", coins); GlobalSet("hearts", hearts);
GlobalSet("maxHearts", maxHearts); GlobalSet("power", powerUp);
if (ResourceExists("Game Save")) { ScriptExecute("Game Save"); }
var nextRoom = "Overworld";
if (leaveReason == 1 && levelId == 5 && ResourceExists("Credits")) { nextRoom = "Credits"; }
state = 5; RoomValueSet("frozen", 1);
RoomChangeFade(0.4);
if (ResourceExists(nextRoom)) { RoomGoto(nextRoom); } else { RoomRestart(); }
"""

EXPLORER_RESTART = r"""
// User Event 4: restart the level. A campaign restart puts back the coins, hearts and power-up
// Pip entered with (lives already lost stay lost); standalone restarts are a fresh run.
if (campaign == 1) {
    GlobalSet("coins", entryCoins); GlobalSet("power", entryPower); GlobalSet("hearts", entryHearts);
    GlobalSet("lives", lives);
    GlobalSet("heldItem", entryHeld); GlobalSet("arrows", entryArrows);
}
RoomRestart();
"""

EXPLORER_DRAWGUI = r"""
// GUI is laid out in a 1280x720 design space, independent of the room size and OS resize.
// Frames, icons and pips come from two User Interface resources (DrawUi stretches them to the
// window: x by sw/1280, y by sh/720); the live numbers are drawn on top with the same mapping.
var sw = WindowGetWidth(); var sh = WindowGetHeight();
var sx = sw / 1280; var sy = sh / 720;
var s = Min(sx, sy);
var hud = "Assets/User Interfaces/Meadow HUD.ui.json";
var panels = "Assets/User Interfaces/Meadow Panels.ui.json";

DrawSetTextAlign("left");

// Control-hint bar (bottom): only with Options > Gameplay > Enable Tips. Keycaps or pad glyphs for the
// device used last (Input Kit), by context. hintA are action names, hintL their captions ("" = glyph only).
var showHints = state == 1 && tipsOn == 1;
var padNow = GlobalGet("lastInput") == 1;
var heldHint = GlobalGet("heldItem");
var hintA = ["left", "right", "jump", "sprint"]; var hintL = ["", "MOVE", "JUMP", "RUN"];
if (powerUp == 1) { hintA = ["left", "right", "jump", "sprint"]; hintL = ["", "MOVE", "HOLD JUMP TO GLIDE", "RUN"]; }
if (powerUp == 2 && heldHint == 0) { hintA = ["left", "right", "jump", "sprint", "use"]; hintL = ["", "MOVE", "JUMP", "RUN", "SPORES"]; }
if (heldHint == 1) { hintA = ["left", "right", "jump", "sprint", "use", "throw"]; hintL = ["", "MOVE", "JUMP", "RUN", "SWING SWORD", "THROW"]; }
if (heldHint == 2) { hintA = ["left", "right", "jump", "sprint", "use", "throw"]; hintL = ["", "MOVE", "JUMP", "RUN", "HOLD TO BLOCK", "THROW"]; }
if (heldHint == 3) { hintA = ["left", "right", "jump", "use", "throw"]; hintL = ["", "MOVE", "JUMP", "HOLD AND RELEASE TO SHOOT", "THROW"]; }
if (heldHint == 4) { hintA = ["left", "right", "jump", "use", "throw"]; hintL = ["", "MOVE", "JUMP", "IN AIR: PARACHUTE", "THROW"]; }
if (swimming == 1) { hintA = ["left", "right", "up", "down", "jump"]; hintL = ["", "", "", "SWIM", "STROKE"]; }
if (hub == 1) { hintA = ["left", "right", "jump", "interact"]; hintL = ["", "WALK", "JUMP", "TALK"]; }
var pauseHint = "ESC: PAUSE"; if (padNow) { pauseHint = "START: PAUSE"; }
if (hasInput == 1) {
    // The hint text lives in the bar drawn below (glyphs need code), so the bar's own text stays hidden.
    UiSetVisible(hud, "helpText", false);
} else {
    var help = "A/D MOVE   SPACE JUMP   SHIFT RUN   P/ESC PAUSE";
    if (swimming == 1) { help = "W/A/S/D: SWIM ANY WAY   SPACE: STROKE   P/ESC PAUSE"; }
    if (hub == 1) { help = "A/D WALK   SPACE JUMP   E / J: TALK   P/ESC PAUSE"; }
    UiSetText(hud, "helpText", help);
    UiSetVisible(hud, "helpText", showHints);
}
UiSetVisible(hud, "pipFill1", bigCoins >= 1);
UiSetVisible(hud, "pipFill2", bigCoins >= 2);
UiSetVisible(hud, "pipFill3", bigCoins >= 3);
UiSetVisible(hud, "helpBar", showHints);
DrawUi(hud);
if (showHints && hasInput == 1) {
    var hintX = 40 * sx; var hintY = 673 * sy; var hintSize = 26 * s;
    for (var hk = 0; hk < PgListSize(hintA); hk = hk + 1) {
        var hintAction = hintA[hk]; var hintLabel = hintL[hk];
        // A pad's stick is one glyph for both directions (and up/down): draw it once, on "left".
        var glyphWanted = 1;
        if (padNow && (hintAction == "right" || hintAction == "up" || hintAction == "down")) { glyphWanted = 0; }
        if (glyphWanted == 1) {
            var glyphWidth = InputDrawGlyph(hintAction, hintX, hintY, hintSize);
            hintX = hintX + glyphWidth + 6 * sx;
        }
        DrawSetFont("Consolas"); DrawSetAlpha(1); DrawSetColorRgb(240, 241, 219);
        if (hintLabel != "") {
            DrawTextScaled(hintX, 677 * sy, hintLabel, 17 * s);
            hintX = hintX + DrawTextWidth(hintLabel, 17 * s) + 26 * sx;
        }
    }
    DrawSetFont("Consolas"); DrawSetAlpha(1); DrawSetColorRgb(240, 241, 219);
    DrawTextScaled(hintX, 677 * sy, pauseHint, 17 * s);
}

DrawSetFont("Consolas"); DrawSetAlpha(1);
DrawSetColorRgb(42, 29, 46);
DrawTextScaled(42*sx, 24*sy, levelName, 22*s);
DrawSetColorRgb(255, 235, 185);
DrawTextScaled(40*sx, 22*sy, levelName, 22*s);
// Hearts (maxHearts slots), the power-up slot, Glowspores returned and the Starlit timer.
var heartImage = "Assets/Sprites/Heart Icon.image.json";
for (var hi = 0; hi < maxHearts; hi = hi + 1) {
    var hx = (296 + hi * 24) * sx; var hy = 24 * sy;
    if (hi < hearts) {
        var beat = 1;
        if (hearts == 1 && state == 1) { beat = 0.55 + 0.45 * Abs(Sin(timeLeft * 360)); }
        DrawSpriteScaled(heartImage, hx, hy, 0, 1.25*s, 1.25*s, 0, beat);
    } else if (hasEmptyHeart) {
        DrawSpriteScaled("Assets/Sprites/Icon Heart Empty.image.json", hx, hy, 0, 1.25*s, 1.25*s, 0, 1);
    } else {
        DrawSpriteScaled(heartImage, hx, hy, 0, 1.25*s, 1.25*s, 0, 0.25);
    }
}
DrawSetAlpha(0.85); DrawSetColorRgb(29, 48, 67);
DrawRoundRect(450*sx, 17*sy, 486*sx, 53*sy, 6*s, false);
DrawSetAlpha(1); DrawSetColorRgb(255, 231, 160);
DrawRoundRect(450*sx, 17*sy, 486*sx, 53*sy, 6*s, true);
var slotIcon = "";
if (powerUp == 1) { slotIcon = "Assets/Sprites/Icon Glide.image.json"; }
if (powerUp == 2) { slotIcon = "Assets/Sprites/Icon Spore.image.json"; }
if (powerUp == 3) { slotIcon = "Assets/Sprites/Icon Shield.image.json"; }
if (starTimer > 0) { slotIcon = "Assets/Sprites/Icon Star.image.json"; }
if (slotIcon != "") { DrawSpriteScaled(slotIcon, 452*sx, 19*sy, 0, 2*s, 2*s, 0, 1); }
DrawSpriteScaled("Assets/Sprites/Icon Glowspore.image.json", 502*sx, 25*sy, 0, 1.25*s, 1.25*s, 0, 1);
DrawSetColorRgb(255, 235, 185);
DrawTextScaled(526*sx, 24*sy, StringJoin2("x", StringOf(GlobalGet("glowspores"))), 19*s);
if (starTimer > 0) {
    DrawSetColorRgb(255, 214, 120);
    DrawTextScaled(600*sx, 24*sy, StringJoin2("STARLIT ", StringOf(Ceil(starTimer))), 19*s);
}
DrawSetColorRgb(248, 252, 244);
DrawTextScaled(40*sx, 57*sy, StringJoin2("SCORE ", StringOf(score)), 19*s);
DrawTextScaled(338*sx, 57*sy, StringJoin2("COINS ", StringOf(coins)), 19*s);
DrawTextScaled(604*sx, 57*sy, StringJoin2("LIVES ", StringOf(lives)), 19*s);
if (hub == 0) {
    if (timeLeft < 30 && state == 1 && Floor(timeLeft * 2) % 2 == 0) { DrawSetColorRgb(255, 140, 120); }
    DrawTextScaled(860*sx, 57*sy, StringJoin2("TIME ", StringOf(Ceil(timeLeft))), 19*s);
    DrawSetColorRgb(248, 252, 244);
    DrawTextScaled(1090*sx, 57*sy, StringJoin2("STAGE ", StringOf(levelId)), 19*s);
} else {
    DrawTextScaled(1090*sx, 57*sy, "VILLAGE", 19*s);
}
// Held item, arrows and flower fuel (Item Kit, drawn in this same 1280x720 GUI space).
if (hasItems == 1 && (state == 1 || state == 2)) {
    ItemDrawHud();
    DrawSetTextAlign("left"); DrawSetFont("Consolas"); DrawSetAlpha(1); DrawSetColorRgb(248, 252, 244);
}
// A one-line hint when the reputation stage changes (never explained).
if (toastTimer > 0 && state == 1 && toastText != "") {
    var toastFade = Min(1, toastTimer / 0.8);
    var toastSize = 19 * s; var toastW = DrawTextWidth(toastText, toastSize);
    var toastX = 640 * sx - toastW / 2;
    DrawSetAlpha(0.5 * toastFade); DrawSetColorRgb(20, 14, 30);
    DrawRoundRect(toastX - 14 * sx, 612 * sy, toastX + toastW + 14 * sx, 644 * sy, 8 * s, false);
    DrawSetAlpha(toastFade); DrawSetColorRgb(214, 200, 232);
    DrawTextScaled(toastX, 619 * sy, toastText, toastSize);
    DrawSetAlpha(1);
}
if (checkpoint == 1 && state == 1) {
    DrawSetAlpha(0.8); DrawSetColorRgb(29, 48, 67);
    DrawRectangle(28*sx, 100*sy, 230*sx, 128*sy);
    DrawSetAlpha(1); DrawSetColorRgb(255, 231, 150);
    DrawTextScaled(40*sx, 104*sy, "CHECKPOINT SAVED", 17*s);
}
// Campaign level banner.
if (bannerTimer > 0 && state == 1) {
    var fade = Min(1, bannerTimer / 0.6);
    DrawSetAlpha(0.7 * fade); DrawSetColorRgb(26, 18, 36);
    DrawRectangle(0, 290*sy, sw, 400*sy);
    DrawSetAlpha(fade); DrawSetColorRgb(255, 223, 145);
    var bannerText = StringJoin2("STAGE ", StringOf(levelId));
    DrawTextScaled((640 - StringLength(bannerText) * 6.6)*sx, 300*sy, bannerText, 24*s);
    DrawSetColorRgb(248, 252, 244);
    DrawTextScaled((640 - StringLength(levelName) * 12.1)*sx, 334*sy, levelName, 44*s);
    DrawSetAlpha(1);
}

if (state == 2 && pauseMode == 1) {
    // The Options Kit page draws itself over the frozen world.
    if (hasOptions == 1) { OptionsDraw(); }
} else if (state != 1) {
    UiSetVisible(panels, "logo", state == 0);
    UiSetVisible(panels, "ribbon", state == 0);
    UiSetVisible(panels, "ribbonText", state == 0);
    UiSetText(panels, "ribbonText", StringJoin2("STAGE ", StringOf(levelId)));
    DrawUi(panels);
    DrawSetAlpha(1); DrawSetColorRgb(255, 223, 145);
    if (state == 0) {
        DrawTextScaled(238*sx, 236*sy, levelName, 44*s);
        DrawSetColorRgb(220, 239, 224);
        DrawTextScaled(238*sx, 296*sy, "PIP'S QUEST FOR THE FIVE GLOWSPORES", 22*s);
        DrawTextScaled(238*sx, 340*sy, "Hearts keep you going; power-ups sprout from seed pods.", 21*s);
        // Each world gets its own controls line and goal.
        var cardControls = "A/D or ARROWS: move   SPACE: jump   SHIFT: run";
        var cardGoal = "Reach the Summit Shrine to recover the Glowspore.";
        if (levelId == 2) { cardGoal = "Light the dark with your lantern. Defeat Grumblecrag."; }
        if (levelId == 3) {
            cardControls = "ARROWS / WASD: swim   SPACE: stroke   S: dive";
            cardGoal = "Ride the currents to the giant clam's Glowspore.";
        }
        if (levelId == 4) { cardGoal = "Brace for gusts; glide to the Giant Blossom."; }
        if (levelId == 5) { cardGoal = "Climb to the nest and face Duskmantle herself."; }
        var cardUse = "J / X: throw spores   P / ESC: pause   (gamepad works too)";
        if (hasInput == 1) {
            // Built from the player's own bindings for the device used last.
            var moveWord = "MOVE: "; var jumpWord = "   JUMP: ";
            if (levelId == 3) { moveWord = "SWIM: "; jumpWord = "   STROKE: "; }
            cardControls = StringJoin2(moveWord, InputGlyph("left"));
            cardControls = StringJoin2(cardControls, StringJoin2("/", InputGlyph("right")));
            cardControls = StringJoin2(cardControls, StringJoin2(jumpWord, InputGlyph("jump")));
            cardControls = StringJoin2(cardControls, StringJoin2("   SPRINT: ", InputGlyph("sprint")));
            cardUse = StringJoin2("USE: ", InputGlyph("use"));
            cardUse = StringJoin2(cardUse, StringJoin2("   THROW: ", InputGlyph("throw")));
            cardUse = StringJoin2(cardUse, StringJoin2("   INTERACT: ", InputGlyph("interact")));
            cardUse = StringJoin2(cardUse, "   PAUSE: ESC / START");
        }
        DrawTextScaled(238*sx, 376*sy, cardControls, 21*s);
        DrawTextScaled(238*sx, 412*sy, cardUse, 21*s);
        DrawTextScaled(238*sx, 448*sy, cardGoal, 21*s);
        DrawSetColorRgb(255, 223, 145);
        DrawTextScaled(238*sx, 506*sy, "PRESS ENTER TO BEGIN", 28*s);
    }
    if (state == 2) {
        DrawTextScaled(270*sx, 226*sy, "PAUSED", 44*s);
        for (var mi = 0; mi < PgListSize(menuItems); mi = mi + 1) {
            var code = menuItems[mi];
            var label = "RESUME";
            if (code == 1) { label = "OPTIONS"; }
            if (code == 2) { label = "SAVE AND QUIT"; }
            if (mi == menuIndex) {
                DrawSetColorRgb(255, 223, 145);
                DrawTextScaled(270*sx, (296 + mi * 40)*sy, StringJoin2("> ", label), 26*s);
            } else {
                DrawSetColorRgb(220, 239, 224);
                DrawTextScaled(300*sx, (296 + mi * 40)*sy, label, 26*s);
            }
        }
        DrawSetColorRgb(220, 239, 224);
        DrawTextScaled(270*sx, 440*sy, StringJoin2("COINS ", StringOf(coins)), 22*s);
        DrawTextScaled(470*sx, 440*sy, StringJoin2("LIVES ", StringOf(lives)), 22*s);
        DrawTextScaled(670*sx, 440*sy, StringJoin2("GLOWSPORES ", StringOf(GlobalGet("glowspores"))), 22*s);
        if (hasRep == 1) {
            DrawSetColorRgb(206, 196, 224);
            DrawTextScaled(270*sx, 480*sy, StringJoin2("REPUTATION  ", RepLabel()), 22*s);
        }
        DrawSetColorRgb(220, 239, 224);
        var pauseHelp = "UP/DOWN choose   ENTER select   ESC resume";
        if (GlobalGet("lastInput") == 1) { pauseHelp = "D-PAD choose   A select   START or B resume"; }
        DrawTextScaled(270*sx, 530*sy, pauseHelp, 20*s);
    }
    if (state == 3) {
        if (campaign == 1 || clearKind == 2) { DrawTextScaled(270*sx, 226*sy, "GLOWSPORE RECOVERED!", 40*s); }
        else { DrawTextScaled(270*sx, 226*sy, "LEVEL COMPLETE!", 44*s); }
        DrawSetColorRgb(220, 239, 224);
        DrawTextScaled(270*sx, 296*sy, StringJoin2("FINAL SCORE  ", StringOf(score)), 28*s);
        DrawTextScaled(270*sx, 344*sy, StringJoin2("COINS ", StringOf(coins)), 24*s);
        DrawTextScaled(520*sx, 344*sy, StringJoin2("LIVES ", StringOf(lives)), 24*s);
        DrawTextScaled(760*sx, 344*sy, StringJoin2("TIME ", StringOf(Ceil(timeLeft))), 24*s);
        DrawTextScaled(270*sx, 384*sy, StringJoin3("SUN MEDALS ", StringOf(bigCoins), " / 3"), 24*s);
        DrawTextScaled(560*sx, 384*sy, StringJoin2("TIME BONUS ", StringOf(timeBonus)), 24*s);
        if (campaign == 1) {
            DrawSetColorRgb(255, 240, 190);
            DrawTextScaled(270*sx, 428*sy, "A Glowspore returns to the Great Lantern Tree!", 21*s);
            DrawSetColorRgb(255, 223, 145);
            if (clearTimer > 1.6) { DrawTextScaled(270*sx, 500*sy, "ENTER: return to the map", 26*s); }
        } else {
            DrawTextScaled(270*sx, 428*sy, "The meadow is safe. Thanks for playing!", 21*s);
            DrawSetColorRgb(255, 223, 145);
            DrawTextScaled(270*sx, 500*sy, "ENTER or R: play again", 26*s);
        }
    }
    if (state == 4) {
        if (campaign == 1) {
            DrawTextScaled(270*sx, 250*sy, "GAME OVER", 44*s);
            DrawSetColorRgb(220, 239, 224);
            DrawTextScaled(270*sx, 336*sy, "Pip heads back to the map to catch his breath.", 23*s);
            DrawTextScaled(270*sx, 380*sy, "Lives are refilled to 5. Glowspores you found stay found.", 21*s);
            DrawTextScaled(270*sx, 440*sy, "ENTER: continue", 26*s);
        } else {
            DrawTextScaled(270*sx, 250*sy, "TRY AGAIN", 44*s);
            DrawSetColorRgb(220, 239, 224);
            DrawTextScaled(270*sx, 336*sy, "Hold jump longer. Use SHIFT for wider gaps.", 23*s);
            DrawTextScaled(270*sx, 380*sy, StringJoin2("FINAL SCORE  ", StringOf(score)), 23*s);
            DrawTextScaled(270*sx, 440*sy, "ENTER or R: start a fresh run", 26*s);
        }
    }
}
DrawSetAlpha(1); DrawSetColorRgb(255, 255, 255);
"""

EXPLORER_ROOMEND = r"""
StopAllSounds();
Light2DRemove(1);
RoomValueSet("starlit", 0);
"""

SOUNDS = {
    "SFX_GLOWSPORE": snd("Glowspore Get", "1"),
    "SFX_FINISH": snd("Finish Fanfare", "1"),
    "SFX_PAUSE": snd("Pause", "0.8"),
    "SFX_MOVE": snd("Menu Move", "0.7"),
    "SFX_BACK": snd("Menu Back", "0.7"),
    "SFX_SELECT": snd("Menu Select", "0.8"),
    "SFX_SPLASH": snd("Splash", "0.8"),
    "SFX_JUMP": snd("Jump", "0.70"),
    "SFX_STROKE": snd("Swim Stroke", "0.7"),
    "SFX_GLIDE": snd("Glide Open", "0.7"),
    "SFX_SPROUT": snd("Power Up", "0.55", "0.8"),
    "SFX_COIN2": snd("Coin", "0.85"),
    "SFX_COIN": snd("Coin", "0.9"),
    "SFX_BLOCK": snd("Block", "0.8"),
    "SFX_BUMP": snd("Bump", "0.7"),
    "SFX_LAND": snd("Land", "0.5"),
    "SFX_BIGCOIN": snd("Big Coin", "0.95"),
    "SFX_ONEUP": snd("Extra Life", "1"),
    "SFX_POWERUP": snd("Power Up", "0.9"),
    "SFX_STARLIT": snd("Starlit Get", "0.9"),
    "SFX_HEART": snd("Heart Get", "0.9"),
    "SFX_STOMP": snd("Stomp", "0.9"),
    "SFX_BOUNCE": snd("Bounce", "0.9"),
    "SFX_CHECKPOINT": snd("Checkpoint Chime", "0.9"),
    "SFX_THROW": snd("Spore Throw", "0.8"),
    "SFX_SHIELD": snd("Shield Break", "0.9"),
    "SFX_POWERDOWN": snd("Power Down", "0.85"),
    "SFX_HURT": snd("Hurt", "0.85"),
    "SFX_GAMEOVER": snd("Game Over", "1"),
}


def fill(text):
    lookup = []
    for marker, item in PRIZES:
        # Markers have no sprite (invisible everywhere), so they are found by Object name, not by mask.
        lookup.append('            for (var pi%s = 0; prizeMarker == 0 && pi%s < InstanceNumber("%s"); pi%s = pi%s + 1) {'
                      % ((marker.split()[1],) * 2 + (marker,) + (marker.split()[1],) * 2))
        lookup.append('                var m%s = InstanceFind("%s", pi%s);' % (marker.split()[1], marker, marker.split()[1]))
        lookup.append('                if (PointDistance(InstanceGetX(m%s), InstanceGetY(m%s), blockX, blockY) < 12) '
                      '{ prizeMarker = m%s; prizeItem = "%s"; }' % ((marker.split()[1],) * 3 + (item,)))
        lookup.append('            }')
    text = text.replace("PRIZE_LOOKUP", "\n".join(lookup))
    # longest keys first so SFX_COIN does not eat SFX_COIN2
    for key in sorted(SOUNDS, key=len, reverse=True):
        text = text.replace(key, SOUNDS[key])
    assert "SFX_" not in text, text[text.index("SFX_"):text.index("SFX_") + 30]
    return text


# ====================================================================== Level Rules
LEVEL_RULES_CREATE = r"""
// Level Rules: place ONE in every playable room (levels and the village hub). It reads the room's
// name and publishes that room's rules as RoomValues, plays its music and ambience, and darkens
// dark rooms. Explorer falls back to the 1-1 defaults when a room has none.
//   RoomValues published: levelId, dark, underwater, wind, timeLimit, hub, levelRules (= 1)
//   Music: RoomValue musicState (Explorer: 0 title, 1 play, 2 stop), starlit (1 = Starlit Theme),
//   bossFight (bosses: 1 = boss theme, 2 = defeated: music stops and Boss Defeated plays), paused
//   (music ducks). Channels are published as RoomValues musicChannel and ambienceChannel.
// Room        | levelId dark underwater wind timeLimit hub | music (boss) / ambience
// Mushroom Meadow   1  0 0 0 300 0 | Meadow Theme / Meadow Ambience
// Rootdeep Caverns  2  1 0 0 360 0 | Cavern Theme (Boss Theme) / Cavern Ambience
// Tidepool Grotto   3  0 1 0 360 0 | Grotto Theme (Boss Theme) / Grotto Ambience
// Windswept Canopy  4  0 0 1 360 0 | Canopy Theme (Boss Theme) / Canopy Wind
// Duskmantle Hollow  5  1 0 0 420 0 | Hollow Theme (Duskmantle Theme) / Hollow Ambience
// Hearthollow       0  0 0 0   0 1 | Village Theme / Meadow Ambience
var room = RoomGetName();
levelId = 1; dark = 0; underwater = 0; windy = 0; timeLimit = 300; hub = 0; petals = 1;
levelName = "MUSHROOM MEADOW"; musicName = "Meadow Theme"; bossName = "Boss Theme"; ambienceName = "Meadow Ambience";
darkness = 0.2; shadeR = 0.02; shadeG = 0.03; shadeB = 0.07;
if (StringPos("Rootdeep", room) > 0) {
    levelId = 2; dark = 1; timeLimit = 360; petals = 0; levelName = "ROOTDEEP CAVERNS";
    musicName = "Cavern Theme"; ambienceName = "Cavern Ambience"; darkness = 0.16;
}
if (StringPos("Tidepool", room) > 0) {
    levelId = 3; underwater = 1; timeLimit = 360; petals = 0; levelName = "TIDEPOOL GROTTO";
    musicName = "Grotto Theme"; ambienceName = "Grotto Ambience";
}
if (StringPos("Canopy", room) > 0) {
    levelId = 4; windy = 1; timeLimit = 360; petals = 0; levelName = "WINDSWEPT CANOPY";
    musicName = "Canopy Theme"; ambienceName = "Canopy Wind";
}
if (StringPos("Duskmantle", room) > 0) {
    levelId = 5; dark = 1; timeLimit = 420; petals = 0; levelName = "DUSKMANTLE HOLLOW";
    musicName = "Hollow Theme"; bossName = "Duskmantle Theme"; ambienceName = "Hollow Ambience";
    darkness = 0.26; shadeR = 0.09; shadeG = 0.03; shadeB = 0.14;
}
if (StringPos("Hearthollow", room) > 0) {
    levelId = 0; hub = 1; timeLimit = 0; levelName = "HEARTHOLLOW";
    musicName = "Village Theme"; ambienceName = "Meadow Ambience";
}
RoomValueSet("levelRules", 1);
RoomValueSet("levelId", levelId); RoomValueSet("dark", dark); RoomValueSet("underwater", underwater);
RoomValueSet("wind", windy); RoomValueSet("timeLimit", timeLimit); RoomValueSet("hub", hub);
// Music/sfx volume, CRT scanlines and screen shake from the options (globals musicVol, sfxVol, crt, shake).
if (ResourceExists("Apply Options")) { ScriptExecute("Apply Options"); }
// Any 2D light or particle burst turns the room's light mask on (default ambient 0.32), so lit
// rooms state full daylight here, before anything lights up. Light ids: Explorer 1, Glowspore 2.
if (dark == 1) { Light2DAmbient(darkness, shadeR, shadeG, shadeB); }
else { Light2DAmbient(1, 0, 0, 0); }
if (petals == 1) { ScriptExecute("Spawn Effect", 7, RoomGetWidth() / 2, -8); }
// The option volumes are applied to the music/sfx/ambient buses by Apply Options.
musicVolume = 0.6; ambienceVolume = 0.35;
musicChannel = -1; ambienceChannel = -1; playing = ""; ambienceOn = 0; bossDone = 0; ducked = 0;
RoomValueSet("musicChannel", -1); RoomValueSet("ambienceChannel", -1);
"""

LEVEL_RULES_STEP = r"""
// Rooms with no Explorer play straight away (musicState defaults to 1).
var wanted = RoomValueGet("musicState", 1);
var boss = RoomValueGet("bossFight", 0);
var track = "";
if (wanted == 1) {
    track = musicName;
    if (RoomValueGet("starlit", 0) == 1) { track = "Starlit Theme"; }
    if (boss == 1) { track = bossName; }
    if (boss == 2) { track = ""; }
}
if (track != playing) {
    StopSound(musicChannel);
    playing = track; musicChannel = -1; ducked = 0;
    if (track != "") {
        if (ResourceExists(track)) {
            musicChannel = PlaySound(StringJoin3("Assets/Audio/", track, ".audio.json"), musicVolume, 1, true);
        } else if (track == "Starlit Theme") {
            // No Starlit Theme yet: the level theme, sped up.
            musicChannel = PlaySound(StringJoin3("Assets/Audio/", musicName, ".audio.json"), musicVolume, 1.25, true);
        }
    }
    RoomValueSet("musicChannel", musicChannel);
}
if (wanted == 1 && ambienceOn == 0) {
    ambienceOn = 1;
    ambienceChannel = PlaySound(StringJoin3("Assets/Audio/", ambienceName, ".audio.json"), ambienceVolume, 1, true);
    RoomValueSet("ambienceChannel", ambienceChannel);
}
if (wanted == 2 && ambienceOn == 1) { ambienceOn = 0; StopSound(ambienceChannel); }
if (boss == 2 && bossDone == 0) {
    bossDone = 1;
    PlaySound("Assets/Audio/Boss Defeated.audio.json", 1, 1, false);
}
// The pause menu ducks the music.
var pausedNow = RoomValueGet("paused", 0);
if (pausedNow != ducked && musicChannel >= 0) {
    ducked = pausedNow;
    if (ducked == 1) { SoundSetVolume(musicChannel, musicVolume * 0.35); }
    else { SoundSetVolume(musicChannel, musicVolume); }
}
"""

LEVEL_RULES_ROOMEND = r"""
StopSound(musicChannel); StopSound(ambienceChannel);
"""

# ====================================================================== projectiles and props
SPORE_CREATE = r"""
// Spore Shot: thrown by Pip with the Spore Puff power-up. It bounces along the ground and gives
// the first live enemy it touches one point of `hurt` (enemy convention).
var pip = InstanceFind("Explorer", 0);
dir = 1;
if (pip > 0 && InstanceVariableGet(pip, "facing") < 0) { dir = -1; }
vx = dir * 330; vy = -60; life = 2.2; bounces = 0;
enemyNames = ENEMY_LIST;
AnimationPlay("Spin", true);
""".replace("ENEMY_LIST", ENEMY_LIST)

SPORE_STEP = r"""
if (RoomValueGet("frozen", 0) == 1) { AnimationSetSpeed(0); return; }
AnimationSetSpeed(1);
var dt = Clamp(DeltaTime, 0, 0.05);
life = life - dt;
if (life <= 0 || y > RoomGetHeight() + 32) { InstanceDestroy(id); return; }
vy = Min(vy + 900 * dt, 520);
var wantX = x + vx * dt;
x = TileMoveX(x, y, vx * dt);
if (Abs(x - wantX) > 0.01) {
    ScriptExecute("Spawn Effect", 1, x, y);
    InstanceDestroy(id); return;
}
var wantY = y + vy * dt;
y = TileMoveY(x, y, vy * dt);
if (Abs(y - wantY) > 0.01) {
    if (vy > 0) { vy = -280; bounces = bounces + 1; } else { vy = 0; }
}
if (bounces > 4) { InstanceDestroy(id); return; }
for (var ei = 0; ei < PgListSize(enemyNames); ei = ei + 1) {
    var foe = CollisionRectangle(x - 6, y - 6, x + 6, y + 6, enemyNames[ei]);
    if (foe > 0 && InstanceVariableGet(foe, "dead") == 0) {
        // Damage convention: hitKind 5 (spore shot), travelling the way the spore flew.
        InstanceVariableSet(foe, "hitKind", 5); InstanceVariableSet(foe, "hitDir", Sign(vx));
        InstanceVariableSet(foe, "hitDmg", 1);
        InstanceVariableSet(foe, "hitX", x); InstanceVariableSet(foe, "hitY", y);
        InstanceVariableSet(foe, "hurt", InstanceVariableGet(foe, "hurt") + 1);
        var pip = InstanceFind("Explorer", 0);
        if (pip > 0) { InstanceVariableSet(pip, "score", InstanceVariableGet(pip, "score") + InstanceVariableGet(foe, "points")); }
        PlaySound("Assets/Audio/Spore Hit.audio.json", 0.85, 1, false);
        ScriptExecute("Spawn Effect", 1, x, y);
        InstanceDestroy(id); return;
    }
}
"""

SHIELD_CREATE = r"""
// Shield Ring: the bark-ring overlay Pip wears while he has the Bark Shield (power 3).
AnimationPlay("Spin", true);
image_alpha = 0.9;
"""

SHIELD_STEP = r"""
var pip = InstanceFind("Explorer", 0);
if (pip <= 0 || InstanceVariableGet(pip, "powerUp") != 3) { InstanceDestroy(id); return; }
x = InstanceGetX(pip); y = InstanceGetY(pip) + 2;
if (RoomValueGet("frozen", 0) == 1) { AnimationSetSpeed(0); } else { AnimationSetSpeed(1); }
"""

GLOWSPORE_CREATE = r"""
// Glowspore: one of the five lights of the Great Lantern Tree. Touching it clears the level
// (Explorer User Event 2). Bosses drop one with CreateInstance("Glowspore", x, y, 0); it grows
// in over half a second and can be collected once `ready` is 1.
ready = 0; grow = 0; homeY = y; bob = 0;
image_xscale = 0.2; image_yscale = 0.2;
AnimationPlay("Glow", true);
ShaderSet("Assets/Shaders/Pixel Flash.shader.json");
ShaderSetParameter("Shimmer", 0.8); ShaderSetParameter("ShimmerSpeed", 0.6); ShaderSetParameter("Warmth", 1);
"""

GLOWSPORE_STEP = r"""
var dt = Clamp(DeltaTime, 0, 0.05);
// Hold still while the world is frozen (title, pause, results).
if (RoomValueGet("frozen", 0) == 1) { AnimationSetSpeed(0); return; }
AnimationSetSpeed(1);
if (ready == 0) {
    grow = Min(1, grow + dt * 2);
    image_xscale = 0.2 + 0.8 * grow; image_yscale = image_xscale;
    if (grow >= 1) { ready = 1; }
}
bob = (bob + dt * 90) % 360;
y = homeY + Sin(bob) * 4;
var pulse = 1 + 0.06 * Sin(bob * 3);
if (ready == 1) { image_xscale = pulse; image_yscale = pulse; }
if (RoomValueGet("dark", 0) == 1) { Light2DPoint(2, x, y, 120, 1.0, 1, 0.92, 0.6); }
"""

GLOWSPORE_DESTROY = r"""
if (RoomValueGet("dark", 0) == 1) { Light2DRemove(2); }
"""

ITEM_CREATE = r"""
// Power-up item (KIND). Pip collects it on touch (Explorer Step). Sprouting out of a seed pod
// (created inside a Blocks tile) it rises 30 px first; otherwise it bobs where it was placed.
homeX = x; homeY = y; startY = y; bob = (x * 7) % 360; rising = 0;
if (TileIndexAt(x, y, "Blocks") >= 0) { rising = 1; }
AnimationPlay("Idle", true);
"""

ITEM_STEP = r"""
var dt = Clamp(DeltaTime, 0, 0.05);
if (rising == 1) {
    y = y - 64 * dt;
    if (y <= startY - 30) { y = startY - 30; rising = 0; homeY = y; }
    return;
}
if (RoomValueGet("frozen", 0) == 1) { AnimationSetSpeed(0); return; }
AnimationSetSpeed(1);
bob = (bob + dt * 160) % 360;
y = homeY + Sin(bob) * 3;
"""

FIREFLY_STEP = r"""
var dt = Clamp(DeltaTime, 0, 0.05);
if (rising == 1) {
    y = y - 64 * dt;
    if (y <= startY - 30) { y = startY - 30; rising = 0; homeY = y; }
    return;
}
if (RoomValueGet("frozen", 0) == 1) { AnimationSetSpeed(0); return; }
AnimationSetSpeed(1);
// The Starlit Firefly loops lazily around where it appeared.
bob = (bob + dt * 120) % 360;
x = homeX + Sin(bob) * 18;
y = homeY + Sin(bob * 2) * 6;
if (Cos(bob) > 0) { image_xscale = 1; } else { image_xscale = -1; }
"""

PRIZE_CREATE = r"""
// Seed pod prize marker: place it at the CENTRE of a seed pod tile (Blocks layer, index 3).
// When Pip bumps that block, a ITEM sprouts instead of a coin (Explorer Step).
visible = false;
"""

ACORN_CREATE = r"""
// Enemy convention (every enemy): Pip's stomps and Spore Shots add to `hurt`; this object spends
// it on `hp`, plays its own defeat and removes itself. Pip ignores it while `dead` is 1.
enemy = 1; hp = 1; stompable = 1; contactDamage = 1; points = 200; dead = 0; hurt = 0;
moveDirection = -1; fallSpeed = 0; deadTime = 0;
GoreEnemyInit(5, 1, 16, 28, 0);
AnimationPlay("Walk", true); AnimationSetSpeed(0);
"""

ACORN_STEP = r"""
var dt = Clamp(DeltaTime, 0, 0.05);
if (hurt > 0 && dead == 0) {
    GoreEnemyHit();
    hp = hp - hurt; hurt = 0;
    if (hp <= 0) {
        dead = 1;
        var goreResult = GoreEnemyDeath();
        if (goreResult > 0) { InstanceDestroy(id); return; }
        SpriteSet("Assets/Sprites/Acorn Squashed.image.json"); AnimationPlay("Squash", false);
    }
}
if (RoomValueGet("frozen", 1) == 1) { AnimationSetSpeed(0); return; }
// Defeated: show the flattened acorn for 0.4 s, then remove it. It no longer hurts.
if (dead == 1) {
    deadTime = deadTime + dt;
    if (deadTime >= 0.4) { InstanceDestroy(id); }
    return;
}
AnimationSetSpeed(1);
var scared = FearCheck(130);
if (scared == 1) {
    // Afraid of a brutal Pip: run away at 1.5x speed; at a ledge or wall it stops and cowers.
    moveDirection = FearDir();
    var fleeStep = moveDirection * 69 * dt;
    var fleeEdge = 0;
    if (TileMeeting(x, y + 1) && !TileMeeting(x + moveDirection * 20, y + 2)) { fleeEdge = 1; }
    var fleeTo = x;
    if (fleeEdge == 0) { fleeTo = TileMoveX(x, y, fleeStep); }
    if (fleeEdge == 1 || Abs(x + fleeStep - fleeTo) > 0.001) { FearCower(1); } else { FearCower(0); }
    x = fleeTo;
} else {
    if (TileMeeting(x, y + 1)) {
        if (!TileMeeting(x + moveDirection * 20, y + 2)) { moveDirection = -moveDirection; }
    }
    var requested = x + moveDirection * 46 * dt;
    var permitted = TileMoveX(x, y, moveDirection * 46 * dt);
    if (Abs(requested - permitted) > 0.001) { moveDirection = -moveDirection; }
    x = permitted;
}
fallSpeed = Min(fallSpeed + 1250 * dt, 600);
var wanted = y + fallSpeed * dt;
y = TileMoveY(x, y, fallSpeed * dt);
if (Abs(y - wanted) > 0.001) { fallSpeed = 0; }
image_xscale = moveDirection;
if (y > RoomGetHeight() + 80) { InstanceDestroy(id); }
"""

BEE_STEP = r"""
var dt = Clamp(DeltaTime, 0, 0.05);
if (hurt > 0 && dead == 0) {
    GoreEnemyHit();
    hp = hp - hurt; hurt = 0;
    if (hp <= 0) {
        dead = 1; fallSpeed = -140;
        if (following == 1) { PathStop(); following = 0; }
        ShaderSetParameter("Shimmer", 0);
        var goreResult = GoreEnemyDeath();
        if (goreResult > 0) { InstanceDestroy(id); return; }
    }
}
if (dead == 1) {
    // Knocked out: tumble down and fade.
    if (hurt > 0 && GoreCorpseHit() == 1) { InstanceDestroy(id); return; }
    deadTime = deadTime + dt;
    fallSpeed = fallSpeed + 900 * dt; y = y + fallSpeed * dt;
    image_angle = image_angle + 540 * dt; image_alpha = Max(0, 1 - deadTime / 0.7);
    if (deadTime >= 0.7) { InstanceDestroy(id); }
    return;
}
if (RoomValueGet("frozen", 1) == 1) {
    if (following == 1) { PathStop(); following = 0; }
    AnimationSetSpeed(0); return;
}
AnimationSetSpeed(1);
var scared = FearCheck(150);
if (scared == 0 && fearEnded == 1) { fleeBack = 1; }
if (scared == 1 || fleeBack == 1) {
    // Afraid of a brutal Pip: dart away (within 260 px of home, a little upward); once calm glide home
    // and take the route again from where it started.
    if (following == 1) { PathStop(); following = 0; }
    if (scared == 1) {
        var away = FearDir();
        var fx = x + away * 130 * dt;
        if (Abs(fx - homeX) > 260 || TileMeeting(fx, y)) { FearCower(1); }
        else { x = fx; FearCower(0); }
        y = y + ((homeY - 24) - y) * Min(1, dt * 2);
    } else {
        var backLen = Max(1, PointDistance(x, y, homeX, homeY));
        if (backLen < 4) { x = homeX; y = homeY; fleeBack = 0; }
        else { x = x + (homeX - x) / backLen * 110 * dt; y = y + (homeY - y) / backLen * 110 * dt; }
    }
} else if (route != "") {
    if (following == 0) { PathFollow(route, ""); following = 1; }
} else {
    hover = (hover + dt * 140) % 360;
    x = homeX + Sin(hover) * 24; y = homeY + Sin(hover * 2) * 8;
}
if (x < lastX - 0.05) { image_xscale = -1; }
if (x > lastX + 0.05) { image_xscale = 1; }
lastX = x;
"""

BEE_ROUTES = {
    "Bee Patrol Meadow": (5700, 112),
    "Bee Patrol Hover": (3200, 60),
    "Bee Patrol Loop": (6080, 112),
}


def bee_create():
    lines = [
        "// Buzz Bee follows a Pathing resource (Assets/Paths/Bee Patrol *.pathing) with PathFollow.",
        "// It takes the route whose first waypoint is nearest to where the room placed it; a bee",
        "// placed far from every route hovers on the spot instead. Enemy convention: see Acorn Walker.",
        "enemy = 1; hp = 1; stompable = 1; contactDamage = 1; points = 300; dead = 0; hurt = 0;",
        "fallSpeed = 0; deadTime = 0; fleeBack = 0;",
        "GoreEnemyInit(2, 1, 16, 12, 8);",
        "AnimationPlay(\"Fly\", true); AnimationSetSpeed(0);",
        "homeX = x; homeY = y; lastX = x; hover = 0; following = 0;",
        "route = \"\"; var best = 400;",
    ]
    for name, (rx, ry) in BEE_ROUTES.items():
        short = name.split()[-1]
        lines.append("var d%s = Abs(x - %d) + Abs(y - %d);" % (short, rx, ry))
        lines.append("if (d%s < best) { best = d%s; route = \"Assets/Paths/%s.pathing\"; }" % (short, short, name))
    lines += [
        "ShaderSet(\"Assets/Shaders/Pixel Flash.shader.json\");",
        "ShaderSetParameter(\"Shimmer\", 0.6); ShaderSetParameter(\"ShimmerSpeed\", 1.4);",
    ]
    return "\n".join(lines) + "\n"


# ====================================================================== shader + particles
RAINBOW_GLOW = """// Rainbow Glow: a Sprite-pipeline shader for Pip while the Starlit Firefly's invincibility lasts.
// Strength 0..1 blends a travelling rainbow over the sprite (the Explorer blinks it in the last two
// seconds); Speed is hue cycles per second; Bands is how many rainbows fit across the sprite;
// Sparkle adds twinkling pixels. Pixel art stays crisp: only colour changes, never UVs.
Texture2D SpriteTex : register(t0);
SamplerState SpriteSamp : register(s0);
cbuffer GenesisFrame : register(b4) { float Time; float Frame; float2 Resolution; };
cbuffer GenesisParameters : register(b5) { float Strength; float Speed; float Bands; float Sparkle; };
struct VSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; float4 Color : COLOR; float FogDepth : TEXCOORD1; };
float3 HueToRgb(float h)
{
    float3 k = abs(frac(h + float3(0.0, 0.6666667, 0.3333333)) * 6.0 - 3.0) - 1.0;
    return saturate(k);
}
float4 MainPS(VSOut IN) : SV_Target
{
    float4 tex = SpriteTex.Sample(SpriteSamp, IN.UV) * IN.Color;
    float strength = saturate(Strength);
    float h = frac(Time * max(Speed, 0.01) + (IN.UV.y * 0.75 + IN.UV.x * 0.25) * max(Bands, 0.1));
    float luma = dot(tex.rgb, float3(0.299, 0.587, 0.114));
    float3 rainbow = HueToRgb(h) * (0.55 + luma * 0.9);
    float3 rgb = lerp(tex.rgb, rainbow, 0.6 * strength);
    float2 cell = floor(IN.UV * 24.0) + floor(Time * 10.0);
    float twinkle = step(0.985, frac(sin(dot(cell, float2(12.9898, 78.233))) * 43758.5453));
    rgb += twinkle * saturate(Sparkle) * strength * 0.8;
    rgb += 0.12 * strength * (0.5 + 0.5 * sin(Time * 18.0));
    return float4(saturate(rgb), tex.a);
}
"""


def param(name, value):
    return {"name": name, "type": "float", "value": [value]}


def colour(r, g, b, a=1.0):
    return {"r": r, "g": g, "b": b, "a": a}


PARTICLE_BASE = {
    "preview2D": True, "maxParticles": 32, "emitRate": 0.0, "burstCount": 8, "loop": False,
    "duration": 1.0, "shape": "Sphere", "spreadDegrees": 30.0, "emitRadius": 0.1, "speed": 4.0,
    "speedVariance": 0.35, "gravity": 0.0, "drag": 1.0, "lifetime": 0.5, "lifetimeVariance": 0.25,
    "startSize": 0.3, "endSize": 0.08, "emissive": 1.0, "blendMode": "Alpha", "pixelSampling": True,
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


def build_assets():
    rel = "Shaders/Rainbow Glow.shader.json"
    write_json(rel, {
        "schemaVersion": 7, "pipeline": "Sprite", "authoringMode": "Code", "targetType": "Image",
        "preset": "", "targetComponent": "", "entry": "MainPS", "profile": "ps_5_0",
        "source": RAINBOW_GLOW, "previewAsset": "Assets/Sprites/Explorer Idle.image.json",
        "parameters": [param("Strength", 1.0), param("Speed", 1.6), param("Bands", 1.5), param("Sparkle", 1.0)],
    })
    write_meta(rel, "Shader", "Rainbow Glow")
    particle("Swim Bubbles", "Explorer: a few bubbles rise from Pip on every swim stroke.",
             burstCount=6, maxParticles=16, shape="Disc", emitRadius=0.4, spreadDegrees=25, speed=2.5,
             drag=1.2, gravity=3.0, lifetime=0.9, startSize=0.22, endSize=0.14,
             startColor=colour(0.85, 0.96, 1.0, 0.95), endColor=colour(0.6, 0.85, 1.0, 0))
    particle("Water Splash", "Explorer: splash when Pip drops into a Water layer.",
             burstCount=14, maxParticles=24, shape="Cone", spreadDegrees=50, speed=8.0, drag=0.8,
             gravity=-9.0, lifetime=0.6, startSize=0.3, endSize=0.16,
             startColor=colour(0.9, 0.97, 1.0, 1), endColor=colour(0.45, 0.75, 0.95, 0))
    particle("Pip Spore Leak", "Explorer: a faint pale puff of spores drifting off Pip's cap at reputation stage 2 and up.",
             burstCount=2, maxParticles=10, shape="Sphere", emitRadius=0.5, speed=0.8, drag=1.5,
             gravity=0.6, lifetime=1.4, lifetimeVariance=0.3, startSize=0.16, endSize=0.05,
             startColor=colour(0.82, 0.78, 0.9, 0.55), endColor=colour(0.7, 0.62, 0.82, 0), colorJitter=0.15)
    particle("Starlit Sparkle", "Explorer: rainbow twinkles trail Pip while the Starlit Firefly lasts.",
             burstCount=3, maxParticles=8, shape="Sphere", emitRadius=0.9, speed=1.5, drag=2.0,
             gravity=1.0, lifetime=0.5, startSize=0.3, endSize=0.05, blendMode="Additive", emissive=1.6,
             startColor=colour(1.0, 1.0, 0.75, 1), midColor=colour(0.6, 0.9, 1.0, 1),
             endColor=colour(1.0, 0.45, 0.85, 0), colorJitter=0.5)


COIN_CREATE = """homeY = y; bob = x % 360;
AnimationPlay("Spin", true); AnimationSetSpeed(0);
"""
COIN_STEP = """if (RoomValueGet("frozen", 1) == 1) { AnimationSetSpeed(0); return; }
AnimationSetSpeed(1); bob = (bob + DeltaTime * 150) % 360;
y = homeY + Sin(bob) * 2;
"""
BIG_COIN_CREATE = """// A secret Big Sun Coin (Sun Medal): worth 5 coins and 1000 points (collected in Explorer Step).
homeY = y; bob = x % 360;
AnimationPlay("Spin", true); AnimationSetSpeed(0);
"""
BIG_COIN_STEP = """if (RoomValueGet("frozen", 1) == 1) { AnimationSetSpeed(0); return; }
AnimationSetSpeed(0.8); bob = (bob + DeltaTime * 120) % 360;
y = homeY + Sin(bob) * 3;
"""
CHECKPOINT_CREATE = """lit = 0;
// This saved Image rig is sampled at runtime; no baked replacement frames are needed.
SpriteRigBind("Assets/Sprites/Checkpoint Sprite.image.json", "Checkpoint pennant");
SpriteRigPlay("Flutter"); SpriteRigStop();
"""
CHECKPOINT_STEP = """if (lit == 0 && RoomValueGet("checkpoint", 0) == 1) {
    lit = 1;
}
// Rebinding the same saved file is cheap and picks up live edits without losing playback.
if (lit == 1) { SpriteRigBind("Assets/Sprites/Checkpoint Lit.image.json", "Checkpoint pennant"); }
else { SpriteRigBind("Assets/Sprites/Checkpoint Sprite.image.json", "Checkpoint pennant"); }
if (RoomValueGet("frozen", 1) == 1) { SpriteRigStop(); return; }
SpriteRigPlay("Flutter");
"""


def build_phase1_objects():
    # Phase-1 pickups/props, rewritten for amendment 1 (images named "<Object> Sprite").
    write_object("Sun Coin", "Sun Coin Sprite", [("Create", COIN_CREATE), ("Step", COIN_STEP)])
    write_object("Big Sun Coin", "Big Sun Coin Sprite", [("Create", BIG_COIN_CREATE), ("Step", BIG_COIN_STEP)])
    write_object("Checkpoint", "Checkpoint Sprite", [("Create", CHECKPOINT_CREATE), ("Step", CHECKPOINT_STEP)])
    write_object("Finish", "Summit Shrine", [])


def build_objects():
    build_phase1_objects()
    write_object("Explorer", "Explorer Idle", [
        ("Create", fill(EXPLORER_CREATE)),
        ("Step", fill(EXPLORER_STEP)),
        ("UserEvent0", fill(EXPLORER_HURT)),
        ("UserEvent1", fill(EXPLORER_LOSE_LIFE)),
        ("UserEvent2", fill(EXPLORER_CLEAR)),
        ("UserEvent3", fill(EXPLORER_LEAVE)),
        ("UserEvent4", fill(EXPLORER_RESTART)),
        ("DrawGui", fill(EXPLORER_DRAWGUI)),
        ("RoomEnd", fill(EXPLORER_ROOMEND)),
    ])
    write_object("Level Rules", None, [
        ("Create", LEVEL_RULES_CREATE), ("Step", LEVEL_RULES_STEP), ("RoomEnd", LEVEL_RULES_ROOMEND)])
    write_object("Spore Shot", "Spore Shot Sprite", [("Create", SPORE_CREATE), ("Step", SPORE_STEP)])
    write_object("Shield Ring", "Bark Shield Ring", [("Create", SHIELD_CREATE), ("Step", SHIELD_STEP)])
    write_object("Glowspore", "Glowspore Sprite", [
        ("Create", GLOWSPORE_CREATE), ("Step", GLOWSPORE_STEP), ("Destroy", GLOWSPORE_DESTROY)])
    for name in ITEM_NAMES:
        step = FIREFLY_STEP if name == "Starlit Firefly" else ITEM_STEP
        write_object(name, name + " Sprite", [("Create", ITEM_CREATE.replace("KIND", name)), ("Step", step)])
    for marker, item in PRIZES:
        write_object(marker, None, [("Create", PRIZE_CREATE.replace("ITEM", item))])
    write_object("Acorn Walker", "Acorn Walker Sprite", [("Create", ACORN_CREATE), ("Step", ACORN_STEP)])
    write_object("Buzz Bee", "Buzz Bee Sprite", [("Create", bee_create()), ("Step", BEE_STEP)])


def check_physics_module():
    """hero_physics.py (used by the level reachability checks) must match Explorer's numbers."""
    import re
    import sys
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import hero_physics as hp
    expected = {"walkSpeed": hp.WALK_SPEED, "runSpeed": hp.RUN_SPEED, "jumpSpeed": hp.JUMP_SPEED,
                "gravityAccel": hp.GRAVITY, "bounceSpeed": hp.BOUNCE_SPEED, "swimSpeed": hp.SWIM_SPEED}
    for name, value in expected.items():
        found = re.search(r"\b%s = ([0-9.]+);" % name, EXPLORER_CREATE)
        assert found and float(found.group(1)) == value, "hero_physics.%s is out of sync with Explorer" % name
    for snippet in ("coyote = 0.10", "jumpBuffer = 0.12", "vy < -%d) { vy = -%d; }" % (hp.JUMP_CUT, hp.JUMP_CUT),
                    "vy = Min(vy + gravityAccel * dt, %d)" % hp.MAX_FALL, "Min(1, dt * %d)" % hp.ACCEL_RATE,
                    "nk <= %d" % hp.CORNER_NUDGE, "sk <= %d" % hp.STEP_UP, "vy = -%d;" % hp.STOMP_BOUNCE,
                    "vy = Min(vy, -%d)" % hp.SWIM_EXIT_HOP, "x - 7, y - 34, x + 7, y"):
        assert snippet in EXPLORER_STEP, "hero_physics is out of sync with Explorer Step: " + snippet


def main():
    check_physics_module()
    build_assets()
    build_objects()
    print("Hero content written to", ASSETS)


if __name__ == "__main__":
    main()
