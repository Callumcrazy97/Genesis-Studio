"""Mushroom Meadow Input Kit generator: the library Script every room's input goes through.

Re-runnable and deterministic. Run from anywhere:
    python BuildTools/TemplateContent/MushroomMeadow/input_kit_gen.py
Writes into Source/Genesis.Application.Core/Projects/Templates/Assets/TwoDShowcase/Assets

What it writes
  Scripts/Input Kit.pgsl (+ .meta)   bindings, device detection, sprint hold/toggle, remap capture,
                                     keycap and controller glyph drawing (library functions only)

The kit reads the engine's own input commands (KeyCheck, MouseCheck, GamepadCheck, GamepadAxis...).
Facts it relies on (read from Source/Runtime/Genesis.Runtime, not guessed):
  * Key names the runtime accepts: A-Z, 0-9 (single characters), F1-F12, Left Right Up Down, Space,
    Enter, Escape, Tab, Backspace, Delete, Shift, Control, Alt (and Plus Minus Comma Period PageUp
    PageDown Home End exist in the enum but the window never reports them). Left and right Shift,
    Control and Alt are ONE key to a script, so the kit has a single "Shift" token.
  * Mouse buttons: 0 left, 1 right, 2 middle. Controller buttons: A B X Y LB RB LT RT L3 R3 Start Back
    DPadUp DPadDown DPadLeft DPadRight (LT/RT read as buttons past half pull). Sticks: GamepadAxis
    "LeftX" (right +) and "LeftY" (AWAY from the player, so up, +); the engine's stick dead zone is 0.18.
  * A controller also presses keys by default (A = Space, L3 = Control, R3 = Shift, triggers = mouse
    buttons). The kit switches that off (GamepadKeyboardEmulation(false)) so device detection and the
    bindings are not fooled by their own echo.
"""
import hashlib
import json
import os

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
ASSETS = os.path.join(ROOT, "Source", "Genesis.Application.Core", "Projects", "Templates",
                      "Assets", "TwoDShowcase", "Assets")
STAMP = "2026-10-15T00:00:00Z"


def guid(name):
    return hashlib.md5(("mushroom-meadow-input:" + name).encode("utf-8")).hexdigest()


def write_text(rel, text):
    path = os.path.join(ASSETS, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def write_meta(rel, kind, display):
    path = os.path.join(ASSETS, rel + ".meta")
    existing = None
    if os.path.exists(path):
        try:
            with open(path, encoding="utf-8") as handle:
                existing = json.load(handle).get("guid")
        except (OSError, ValueError):
            existing = None
    write_text(rel + ".meta", json.dumps({
        "guid": existing or guid(rel),
        "kind": kind,
        "displayName": display,
        "createdUtc": STAMP,
        "modifiedUtc": STAMP,
    }, indent=2) + "\n")


def pgsl_list(items):
    return "[" + ", ".join(json.dumps(i) for i in items) + "]"


LETTERS = [chr(c) for c in range(ord("A"), ord("Z") + 1)]
DIGITS = [str(d) for d in range(10)]
FKEYS = ["F%d" % n for n in range(1, 13)]
NAMED = ["Left", "Right", "Up", "Down", "Space", "Enter", "Tab", "Backspace", "Delete",
         "Shift", "Control", "Alt"]
# Keys a player may bind (Escape and P stay with the pause menu).
CAPTURE_KEYS = [k for k in LETTERS if k != "P"] + DIGITS + FKEYS + NAMED
# Keys that count as "the keyboard was used" when working out the last-used device.
SCAN_KEYS = LETTERS + DIGITS + FKEYS + NAMED + ["Escape"]
PAD_CAPTURE = ["A", "B", "X", "Y", "LB", "RB", "LT", "RT", "L3", "R3", "Back"]
PAD_SCAN = PAD_CAPTURE + ["Start", "DPadUp", "DPadDown", "DPadLeft", "DPadRight"]

INPUT_KIT = r"""// Input Kit: a library Script. Every object calls these functions directly (a function defined in a
// project Script is callable from any object and uses the caller's instance through self.).
// All game input of Mushroom Meadow goes through here: movement, actions, remappable bindings, the
// last-used device, sprint hold/toggle, rebind capture and the glyphs of the control-hint bar.
//
// DEVICES: 0 = keyboard and mouse, 1 = controller (gamepad 0). Used by every function with a device.
// ACTIONS (names are strings): left right up down jump sprint use throw interact.
//   Pause is NOT an action: it is fixed to Esc, P and controller Start (read them yourself).
// SLOTS: every action has two binding slots per device, 1 and 2.
//
// TOKENS (what a binding is: a string)
//   keyboard: "A".."Z", "0".."9", "F1".."F12", "Left" "Right" "Up" "Down", "Space" "Enter" "Tab"
//             "Backspace" "Delete" "Shift" "Control" "Alt" (left and right are one key to scripts);
//             mouse: "MouseLeft" "MouseRight" "MouseMiddle". Aliases are accepted and turned into these
//             ("LeftShift", "RightShift" -> "Shift", "ctrl" -> "Control", "Return" -> "Enter").
//   controller: "A" "B" "X" "Y" "LB" "RB" "LT" "RT" "L3" (left stick click) "R3" "Back" and the fixed
//             "DPadUp" "DPadDown" "DPadLeft" "DPadRight"; "Start" and "Escape" are never bindable.
//   "none" or "" = empty slot.
// On the controller left/right/up/down are FIXED (left stick + D-pad) and cannot be rebound.
// Keyboard defaults (slot 1, slot 2): left A,Left | right D,Right | up W,Up | down S,Down |
//   jump Space,W | sprint Shift,- | use J,MouseLeft | throw K,MouseRight | interact E,X
// Controller defaults: jump A | sprint L3 | use X | throw B | interact Y (slot 2 empty: LB RB LT RT are free).
//
// STORAGE: Global strings "bind_<action>_<kb|pad>_<slot>" (a missing global means the default; "none" means
//   empty). Persisted by InputSaveBindings in the save slot "Input Bindings". Global "inp_rev" is bumped on every
//   change: every instance rebuilds its cache when its copy differs (so setting bind_* globals by hand needs
//   GlobalSet("inp_rev", GlobalGet("inp_rev") + 1)). Global "lastInput" (0/1) mirrors the last-used device.
//   Reads of the options "sprintModeKb" and "sprintModePad" (0 hold, 1 toggle) happen here.
//
// CALLER INSTANCE VARIABLES (set by these functions on the instance that calls them; do not reuse the names):
//   inpReady inpRev inpDev inpStamp inpMx inpMy inpMouseIdle inpKind0 inpArg0 inpKind1 inpArg1
//   inpKeyScan inpKeyCap inpPadScan inpPadCap inpStickNow inpStickPrev inpGlyphOk inpCapWait
//   inpModeKb inpModePad inpSprintKb inpSprintPad inpMoveT inpMoveSeen
//   Sprint toggle state is inpSprintKb / inpSprintPad (1 = running); read them if you must, or use
//   InputSprintHeld().
//
// ---------------------------------------------------------------------------------------------
// FUNCTIONS
//   InputInit()                    Create event. Loads bindings (once per game session), builds the cache,
//                                  switches off the controller's key emulation. Every other function calls
//                                  it by itself if you forgot, so nothing breaks without it.
//   InputUpdate()                  once per frame at the top of Step: rebuilds the cache after a rebind,
//                                  detects the last-used device, tracks stick edges, runs sprint toggle.
//                                  Returns the device. (Controller stick "pressed"/"released" edges and the
//                                  sprint toggle need it; plain InputDown works without.)
//   InputDown(action)              1 while any binding of the action is held (both devices, both slots)
//   InputPressed(action)           1 in the frame it goes down        InputReleased(action)  1 in the frame it comes up
//   InputAxisX()                   -1..1: right positive. Keyboard digital, controller stick (dead zone 0.25,
//                                  rescaled) or D-pad. Keyboard wins when both are used.
//   InputAxisY()                   -1..1 in SCREEN direction: up is -1, down is +1.
//   InputSprintHeld()              1 while sprinting: hold mode = binding held; toggle mode = toggled on
//                                  (turns itself off after 0.35 s without horizontal input once you have
//                                  moved). The mode is per device: sprintModeKb / sprintModePad globals.
//   InputSprintCancel()            clears both toggles (call it when hurt, swimming, entering a room...).
//   InputDevice()                  0 keyboard/mouse, 1 controller. Stays current by itself even if the room
//                                  never calls InputUpdate (it runs the detection when called).
//   InputMouseRecent()             1 when the keyboard/mouse is the device and the mouse moved in the last 2.5 s
//                                  (bow aiming with the mouse).
//   InputReload()                  rebuild the cache from the globals now.
//   InputBindingGet(action, device, slot)   the canonical token, "" when the slot is empty.
//   InputBindingText(action, device, slot)  a short label: "SPACE", "SHIFT", "J", "L CLICK", "A", "LS CLICK",
//                                  "NONE" for an empty slot. Controller left/right/up/down: "L STICK" / "D-PAD".
//   InputGlyph(action)             that label for the CURRENT device (slot 1, else slot 2).
//   InputDrawGlyph(action, px, py, sz)   draws the control for the current device with its TOP-LEFT at
//                                  (px, py), sz pixels tall, in whatever space the caller draws in (Draw GUI:
//                                  pre-scale px, py, sz). Keyboard: a code-drawn keycap (rounded key, shadow,
//                                  label; arrow keys get a drawn arrowhead; mouse buttons use the "Pad Glyphs"
//                                  frames when that image exists). Controller: the "Pad Glyphs" frame, or a
//                                  round code-drawn label when that image is missing. Returns the width drawn,
//                                  so lay out a bar with px = px + width + gap. Uses the current draw alpha,
//                                  keeps the draw colour, leaves DrawSetTextAlign("left"). Text uses the
//                                  current font (DrawSetFont).
//   InputDrawToken(token, device, px, py, sz)  the same for one given token (the Remap list).
//   InputDrawMove(px, py, sz)      the movement control: left stick glyph, or the left and right keycaps.
//   InputCaptureArm()              call when you start waiting for a key: the next 2 captures return "" so the
//                                  press that opened the prompt (Enter, a click, A) is not taken as the binding.
//   InputCapture(device)           the token pressed this frame on that device, or "". Esc, P and Start are
//                                  never returned (use them to cancel). Mouse: MouseLeft/Right/Middle.
//   InputSetBinding(action, device, slot, token)   sets a slot (invalid tokens, controller directions, "P",
//                                  "Escape" and "Start" are ignored). If another slot of that device already
//                                  held the token, it takes this slot's old token (swap; extra holders are
//                                  cleared). Returns the name of the action that was swapped, "" if none.
//                                  Rebuilds the cache and saves.
//   InputClearBinding(action, device, slot)   empties a slot (saves).
//   InputResetBindings(device)     defaults for that device (0 or 1), saves.
//   InputSaveBindings() / InputLoadBindings()   the "Input Bindings" save slot; Load fills the globals.
//   InputActionCount() 9, InputActionName(i) "left".."interact" (i 0..8), InputActionLabel(action) "MOVE LEFT"...
// ---------------------------------------------------------------------------------------------

function InputActionCount() { return 9; }

function InputActionName(ai) {
    var names = ["left", "right", "up", "down", "jump", "sprint", "use", "throw", "interact"];
    if (ai < 0 || ai > 8) { return ""; }
    return names[ai];
}

function InputActionIndex(action) {
    if (action == "left") { return 0; }
    if (action == "right") { return 1; }
    if (action == "jump") { return 4; }
    if (action == "up") { return 2; }
    if (action == "down") { return 3; }
    if (action == "use") { return 6; }
    if (action == "sprint") { return 5; }
    if (action == "throw") { return 7; }
    if (action == "interact") { return 8; }
    return -1;
}

function InputActionLabel(action) {
    var labels = ["MOVE LEFT", "MOVE RIGHT", "UP / AIM UP", "DOWN / AIM DOWN", "JUMP", "SPRINT", "USE ITEM", "THROW ITEM", "INTERACT"];
    var ai = InputActionIndex(action);
    if (ai < 0) { return ""; }
    return labels[ai];
}

// ------------------------------------------------------------------ tokens
function InputDefault(ai, dv, sl) {
    var kb = ["A", "Left", "D", "Right", "W", "Up", "S", "Down", "Space", "W", "Shift", "none", "J", "MouseLeft", "K", "MouseRight", "E", "X"];
    var pd = ["none", "none", "none", "none", "none", "none", "none", "none", "A", "none", "L3", "none", "X", "none", "B", "none", "Y", "none"];
    var ix = ai * 2 + sl - 1;
    if (ix < 0 || ix > 17) { return "none"; }
    if (dv == 0) { return kb[ix]; }
    return pd[ix];
}

function InputBindingName(ai, dv, sl) {
    var gname = StringJoin2("bind_", InputActionName(ai));
    if (dv == 0) { gname = StringJoin2(gname, "_kb_"); } else { gname = StringJoin2(gname, "_pad_"); }
    return StringJoin2(gname, StringOf(sl));
}

// A token as the kit stores it, or "" when it is empty or not valid for that device.
function InputCanon(tok, dv) {
    var t = StringTrim(tok);
    if (t == "" || t == "none") { return ""; }
    var lo = StringLower(t);
    if (lo == "none") { return ""; }
    var up = StringUpper(t);
    if (dv == 0) {
        if (StringLength(t) == 1) {
            if (StringPos(up, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789") > 0) { return up; }
            return "";
        }
        if (lo == "space") { return "Space"; }
        if (lo == "enter" || lo == "return") { return "Enter"; }
        if (lo == "tab") { return "Tab"; }
        if (lo == "backspace") { return "Backspace"; }
        if (lo == "delete" || lo == "del") { return "Delete"; }
        if (lo == "left") { return "Left"; }
        if (lo == "right") { return "Right"; }
        if (lo == "up") { return "Up"; }
        if (lo == "down") { return "Down"; }
        if (lo == "shift" || lo == "leftshift" || lo == "rightshift" || lo == "lshift" || lo == "rshift") { return "Shift"; }
        if (lo == "control" || lo == "ctrl" || lo == "leftcontrol" || lo == "rightcontrol" || lo == "lctrl" || lo == "rctrl") { return "Control"; }
        if (lo == "alt" || lo == "leftalt" || lo == "rightalt") { return "Alt"; }
        if (lo == "mouseleft" || lo == "mouse1") { return "MouseLeft"; }
        if (lo == "mouseright" || lo == "mouse2") { return "MouseRight"; }
        if (lo == "mousemiddle" || lo == "mouse3") { return "MouseMiddle"; }
        if (StringLength(lo) >= 2 && StringLength(lo) <= 3 && StringStartsWith(lo, "f")) {
            var fnum = RealOf(StringCopy(lo, 2, 2));
            if (fnum >= 1 && fnum <= 12) { return StringJoin2("F", StringOf(Floor(fnum))); }
        }
        return "";
    }
    if (lo == "a" || lo == "b" || lo == "x" || lo == "y") { return up; }
    if (lo == "lb" || lo == "l1") { return "LB"; }
    if (lo == "rb" || lo == "r1") { return "RB"; }
    if (lo == "lt" || lo == "l2") { return "LT"; }
    if (lo == "rt" || lo == "r2") { return "RT"; }
    if (lo == "l3" || lo == "ls") { return "L3"; }
    if (lo == "r3" || lo == "rs") { return "R3"; }
    if (lo == "back" || lo == "select" || lo == "view") { return "Back"; }
    if (lo == "start" || lo == "menu") { return "Start"; }
    if (lo == "dpadup") { return "DPadUp"; }
    if (lo == "dpaddown") { return "DPadDown"; }
    if (lo == "dpadleft") { return "DPadLeft"; }
    if (lo == "dpadright") { return "DPadRight"; }
    return "";
}

// The effective token of a slot: the global when set, else the default. Always canonical or "".
function InputTokenAt(ai, dv, sl) {
    var gname = InputBindingName(ai, dv, sl);
    if (GlobalExists(gname)) { return InputCanon(GlobalGetString(gname), dv); }
    return InputCanon(InputDefault(ai, dv, sl), dv);
}

function InputBindingGet(action, dv, sl) {
    var ai = InputActionIndex(action);
    if (ai < 0) { return ""; }
    if (dv == 1 && ai < 4) { return ""; }
    return InputTokenAt(ai, dv, sl);
}

// 0 empty, 1 key, 2 mouse button, 3 controller button.
function InputTokKind(tok, dv) {
    if (tok == "") { return 0; }
    if (dv == 1) { return 3; }
    if (StringStartsWith(tok, "Mouse")) { return 2; }
    return 1;
}

function InputTokArg(tok, dv) {
    if (tok == "") { return ""; }
    if (dv == 0) {
        if (tok == "MouseLeft") { return 0; }
        if (tok == "MouseRight") { return 1; }
        if (tok == "MouseMiddle") { return 2; }
    }
    return tok;
}

function InputTokLabel(tok, dv) {
    if (tok == "") { return "NONE"; }
    if (dv == 0) {
        if (tok == "MouseLeft") { return "L CLICK"; }
        if (tok == "MouseRight") { return "R CLICK"; }
        if (tok == "MouseMiddle") { return "M CLICK"; }
        if (tok == "Backspace") { return "BKSP"; }
        if (tok == "Delete") { return "DEL"; }
        if (tok == "Control") { return "CTRL"; }
        return StringUpper(tok);
    }
    if (tok == "L3") { return "LS CLICK"; }
    if (tok == "R3") { return "RS CLICK"; }
    if (tok == "DPadUp") { return "D-PAD UP"; }
    if (tok == "DPadDown") { return "D-PAD DOWN"; }
    if (tok == "DPadLeft") { return "D-PAD LEFT"; }
    if (tok == "DPadRight") { return "D-PAD RIGHT"; }
    return StringUpper(tok);
}

// Frame of the "Pad Glyphs" image for a token, -1 when it has none.
// Order: A B X Y LB RB LT RT L3 R3 Start Back DpadUp DpadDown DpadLeft DpadRight DpadAll StickMove
//        MouseBody MouseLeft MouseRight MouseMiddle
function InputGlyphFrame(tok, dv) {
    if (dv == 0) {
        if (tok == "MouseLeft") { return 19; }
        if (tok == "MouseRight") { return 20; }
        if (tok == "MouseMiddle") { return 21; }
        return -1;
    }
    if (tok == "A") { return 0; }
    if (tok == "B") { return 1; }
    if (tok == "X") { return 2; }
    if (tok == "Y") { return 3; }
    if (tok == "LB") { return 4; }
    if (tok == "RB") { return 5; }
    if (tok == "LT") { return 6; }
    if (tok == "RT") { return 7; }
    if (tok == "L3") { return 8; }
    if (tok == "R3") { return 9; }
    if (tok == "Start") { return 10; }
    if (tok == "Back") { return 11; }
    if (tok == "DPadUp") { return 12; }
    if (tok == "DPadDown") { return 13; }
    if (tok == "DPadLeft") { return 14; }
    if (tok == "DPadRight") { return 15; }
    return -1;
}

// ------------------------------------------------------------------ setup
function InputInit() {
    if (!GlobalExists("inp_ready")) {
        GlobalSet("inp_ready", 1);
        InputLoadBindings();
    }
    self.inpKeyScan = @SCAN_KEYS@;
    self.inpKeyCap = @CAPTURE_KEYS@;
    self.inpPadScan = @PAD_SCAN@;
    self.inpPadCap = @PAD_CAPTURE@;
    self.inpStickNow = [0, 0, 0, 0];
    self.inpStickPrev = [0, 0, 0, 0];
    self.inpGlyphOk = -1;
    self.inpCapWait = 0;
    self.inpSprintKb = 0;
    self.inpSprintPad = 0;
    self.inpMoveT = 0;
    self.inpMoveSeen = 0;
    self.inpModeKb = GlobalGet("sprintModeKb");
    self.inpModePad = GlobalGet("sprintModePad");
    self.inpMx = MouseX;
    self.inpMy = MouseY;
    self.inpMouseIdle = 99;
    self.inpStamp = TimeMs();
    self.inpDev = 0;
    if (GlobalGet("lastInput") == 1 && GamepadConnected()) { self.inpDev = 1; }
    GamepadKeyboardEmulation(false);
    self.inpReady = 1;
    InputReload();
    GlobalSet("lastInput", self.inpDev);
    return 1;
}

// Rebuilds this instance's cache: per action and slot, a kind (0 none, 1 key, 2 mouse, 3 pad) and an
// argument (key name, mouse button number or pad button name). Index = action * 2 + slot - 1.
function InputReload() {
    var k0 = []; var a0 = []; var k1 = []; var a1 = [];
    for (var ai = 0; ai < 9; ai = ai + 1) {
        for (var sl = 1; sl <= 2; sl = sl + 1) {
            var ix = ai * 2 + sl - 1;
            var t0 = InputTokenAt(ai, 0, sl);
            k0[ix] = InputTokKind(t0, 0);
            a0[ix] = InputTokArg(t0, 0);
            var t1 = "";
            if (ai >= 4) { t1 = InputTokenAt(ai, 1, sl); }
            k1[ix] = InputTokKind(t1, 1);
            a1[ix] = InputTokArg(t1, 1);
        }
    }
    self.inpKind0 = k0; self.inpArg0 = a0;
    self.inpKind1 = k1; self.inpArg1 = a1;
    self.inpRev = GlobalGet("inp_rev");
    return 1;
}

// ------------------------------------------------------------------ queries
// mode 0 held, 1 pressed, 2 released. devs 1 keyboard/mouse only, 2 controller only, 3 both.
function InputQuery(action, mode, devs) {
    if (self.inpReady != 1) { InputInit(); }
    var ai = InputActionIndex(action);
    if (ai < 0) { return 0; }
    var bi = ai * 2;
    if (devs == 1 || devs == 3) {
        var kk = self.inpKind0; var ka = self.inpArg0;
        for (var sa = 0; sa < 2; sa = sa + 1) {
            var kd = kk[bi + sa];
            if (kd == 1) {
                if (mode == 0) { if (KeyCheck(ka[bi + sa])) { return 1; } }
                else if (mode == 1) { if (KeyPressed(ka[bi + sa])) { return 1; } }
                else { if (KeyReleased(ka[bi + sa])) { return 1; } }
            } else if (kd == 2) {
                if (mode == 0) { if (MouseCheck(ka[bi + sa])) { return 1; } }
                else if (mode == 1) { if (MousePressed(ka[bi + sa])) { return 1; } }
                else { if (MouseReleased(ka[bi + sa])) { return 1; } }
            }
        }
    }
    if (devs >= 2) {
        if (ai < 4) {
            var dpn = "DPadDown";
            if (ai == 0) { dpn = "DPadLeft"; } else if (ai == 1) { dpn = "DPadRight"; } else if (ai == 2) { dpn = "DPadUp"; }
            var sn = self.inpStickNow; var sprv = self.inpStickPrev;
            if (mode == 0) {
                if (GamepadCheck(dpn)) { return 1; }
                if (InputStickDir(ai) == 1) { return 1; }
            } else if (mode == 1) {
                if (GamepadPressed(dpn)) { return 1; }
                if (sn[ai] == 1 && sprv[ai] == 0) { return 1; }
            } else {
                if (GamepadReleased(dpn)) { return 1; }
                if (sn[ai] == 0 && sprv[ai] == 1) { return 1; }
            }
        } else {
            var pk = self.inpKind1; var pa = self.inpArg1;
            for (var sb = 0; sb < 2; sb = sb + 1) {
                if (pk[bi + sb] == 3) {
                    if (mode == 0) { if (GamepadCheck(pa[bi + sb])) { return 1; } }
                    else if (mode == 1) { if (GamepadPressed(pa[bi + sb])) { return 1; } }
                    else { if (GamepadReleased(pa[bi + sb])) { return 1; } }
                }
            }
        }
    }
    return 0;
}

// 1 while the left stick points that way (0 left, 1 right, 2 up, 3 down) past the same thresholds the old
// controls used: 0.35 sideways, 0.5 up and down.
function InputStickDir(ai) {
    if (ai == 0) { if (GamepadAxis("LeftX") < -0.35) { return 1; } return 0; }
    if (ai == 1) { if (GamepadAxis("LeftX") > 0.35) { return 1; } return 0; }
    if (ai == 2) { if (GamepadAxis("LeftY") > 0.5) { return 1; } return 0; }
    if (ai == 3) { if (GamepadAxis("LeftY") < -0.5) { return 1; } return 0; }
    return 0;
}

function InputDown(action) { return InputQuery(action, 0, 3); }
function InputPressed(action) { return InputQuery(action, 1, 3); }
function InputReleased(action) { return InputQuery(action, 2, 3); }

// One stick or D-pad axis, rescaled past a 0.25 dead zone. positive = the "right" or "away" direction.
function InputPadAxis(axisName, dpadPos, dpadNeg) {
    var pv = 0;
    if (GamepadCheck(dpadPos)) { pv = pv + 1; }
    if (GamepadCheck(dpadNeg)) { pv = pv - 1; }
    if (pv != 0) { return pv; }
    var sv = GamepadAxis(axisName);
    if (sv > 0.25) { return Min(1, (sv - 0.25) / 0.65); }
    if (sv < -0.25) { return Max(-1, (sv + 0.25) / 0.65); }
    return 0;
}

function InputAxisX() {
    var kv = 0;
    if (InputQuery("right", 0, 1)) { kv = kv + 1; }
    if (InputQuery("left", 0, 1)) { kv = kv - 1; }
    if (kv != 0) { return kv; }
    if (!GamepadConnected()) { return 0; }
    return InputPadAxis("LeftX", "DPadRight", "DPadLeft");
}

// Screen direction: up is -1, down is +1 (the controller stick reads away-from-you positive, flipped here).
function InputAxisY() {
    var kv = 0;
    if (InputQuery("down", 0, 1)) { kv = kv + 1; }
    if (InputQuery("up", 0, 1)) { kv = kv - 1; }
    if (kv != 0) { return kv; }
    if (!GamepadConnected()) { return 0; }
    return 0 - InputPadAxis("LeftY", "DPadUp", "DPadDown");
}

// ------------------------------------------------------------------ device and frame update
function InputDetect() {
    if (self.inpReady != 1) { InputInit(); }
    var nowMs = TimeMs();
    var gap = Clamp((nowMs - self.inpStamp) / 1000, 0, 0.25);
    self.inpStamp = nowMs;
    var mx = MouseX; var my = MouseY;
    var moved = 0;
    if (Abs(mx - self.inpMx) + Abs(my - self.inpMy) > 3) { moved = 1; }
    self.inpMx = mx; self.inpMy = my;
    if (moved == 1) { self.inpMouseIdle = 0; } else { self.inpMouseIdle = self.inpMouseIdle + gap; }
    var dv = self.inpDev;
    if (GamepadConnected()) {
        var act = 0;
        if (dv == 0) {
            var pn = self.inpPadScan;
            for (var pj = 0; pj < 16; pj = pj + 1) {
                if (GamepadPressed(pn[pj])) { act = 1; break; }
            }
            if (act == 0) {
                if (Abs(GamepadAxis("LeftX")) > 0.5 || Abs(GamepadAxis("LeftY")) > 0.5 || Abs(GamepadAxis("RightX")) > 0.5 || Abs(GamepadAxis("RightY")) > 0.5) { act = 1; }
            }
            if (act == 1) { dv = 1; }
        } else {
            var ks = self.inpKeyScan;
            var kn = PgListSize(ks);
            for (var ki = 0; ki < kn; ki = ki + 1) {
                if (KeyPressed(ks[ki])) { act = 1; break; }
            }
            if (act == 0) {
                if (MousePressed(0) || MousePressed(1) || MousePressed(2) || moved == 1) { act = 1; }
            }
            if (act == 1) { dv = 0; }
        }
    } else {
        dv = 0;
    }
    if (dv != self.inpDev) {
        self.inpDev = dv;
        GlobalSet("lastInput", dv);
    }
    return dv;
}

function InputUpdate() {
    if (self.inpReady != 1) { InputInit(); }
    if (self.inpRev != GlobalGet("inp_rev")) { InputReload(); }
    InputDetect();
    var ft = Clamp(DeltaTime, 0, 0.1);
    // virtual left-stick directions and their previous frame (edges for InputPressed/InputReleased)
    var sn = self.inpStickNow; var sprv = self.inpStickPrev;
    for (var si = 0; si < 4; si = si + 1) { sprv[si] = sn[si]; }
    if (GamepadConnected()) {
        for (var sj = 0; sj < 4; sj = sj + 1) { sn[sj] = InputStickDir(sj); }
    } else {
        for (var sk = 0; sk < 4; sk = sk + 1) { sn[sk] = 0; }
    }
    // sprint: hold or toggle per device
    self.inpModeKb = GlobalGet("sprintModeKb");
    self.inpModePad = GlobalGet("sprintModePad");
    if (self.inpModeKb == 1) {
        if (InputQuery("sprint", 1, 1)) { self.inpSprintKb = 1 - self.inpSprintKb; self.inpMoveSeen = 0; }
    } else {
        self.inpSprintKb = 0;
    }
    if (self.inpModePad == 1) {
        if (InputQuery("sprint", 1, 2)) { self.inpSprintPad = 1 - self.inpSprintPad; self.inpMoveSeen = 0; }
    } else {
        self.inpSprintPad = 0;
    }
    if (self.inpSprintKb == 1 || self.inpSprintPad == 1) {
        if (InputAxisX() != 0) {
            self.inpMoveT = 0; self.inpMoveSeen = 1;
        } else {
            self.inpMoveT = self.inpMoveT + ft;
            if (self.inpMoveSeen == 1 && self.inpMoveT > 0.35) {
                self.inpSprintKb = 0; self.inpSprintPad = 0; self.inpMoveSeen = 0;
            }
        }
    } else {
        self.inpMoveT = 0; self.inpMoveSeen = 0;
    }
    return self.inpDev;
}

function InputSprintHeld() {
    if (self.inpReady != 1) { InputInit(); }
    if (self.inpModeKb == 1) {
        if (self.inpSprintKb == 1) { return 1; }
    } else {
        if (InputQuery("sprint", 0, 1)) { return 1; }
    }
    if (self.inpModePad == 1) {
        if (self.inpSprintPad == 1) { return 1; }
    } else {
        if (InputQuery("sprint", 0, 2)) { return 1; }
    }
    return 0;
}

function InputSprintCancel() {
    self.inpSprintKb = 0; self.inpSprintPad = 0; self.inpMoveSeen = 0; self.inpMoveT = 0;
    return 0;
}

function InputDevice() {
    if (self.inpReady != 1) { InputInit(); }
    if (TimeMs() - self.inpStamp > 3) { InputDetect(); }
    return self.inpDev;
}

function InputMouseRecent() {
    if (self.inpReady != 1) { InputInit(); }
    if (self.inpDev == 0 && self.inpMouseIdle < 2.5) { return 1; }
    return 0;
}

// ------------------------------------------------------------------ labels and glyphs
function InputBindingText(action, dv, sl) {
    var ai = InputActionIndex(action);
    if (ai < 0) { return ""; }
    if (dv == 1 && ai < 4) {
        if (sl == 1) { return "L STICK"; }
        return "D-PAD";
    }
    return InputTokLabel(InputTokenAt(ai, dv, sl), dv);
}

function InputGlyph(action) {
    var dv = InputDevice();
    var ai = InputActionIndex(action);
    if (ai < 0) { return ""; }
    if (dv == 1 && ai < 4) { return "L STICK"; }
    var tk = InputTokenAt(ai, dv, 1);
    if (tk == "") { tk = InputTokenAt(ai, dv, 2); }
    return InputTokLabel(tk, dv);
}

function InputPadSprite() {
    if (self.inpGlyphOk < 0) {
        self.inpGlyphOk = 0;
        if (ResourceExists("Pad Glyphs")) { self.inpGlyphOk = 1; }
    }
    return self.inpGlyphOk;
}

// A code-drawn key: shadow, face, outline and label. arrow 0 none, 1 left, 2 right, 3 up, 4 down.
// rnd 1 makes it a pill (the controller fallback). Returns the width.
function InputDrawCap(label, arrow, px, py, sz, rnd) {
    var alp = DrawGetAlpha();
    var cr = DrawGetColorR(); var cg = DrawGetColorG(); var cb = DrawGetColorB();
    var ts = sz * 0.5;
    DrawSetTextAlign("left");
    var tw = DrawTextWidth(label, ts);
    var cw = sz;
    if (arrow == 0) { cw = Max(sz, tw + sz * 0.5); }
    var dsh = Max(2, Floor(sz * 0.1));
    var fh = sz - dsh;
    var rad = Min(sz * 0.22, fh * 0.5);
    if (rnd == 1) { rad = Min(fh * 0.5, cw * 0.5); }
    DrawSetAlpha(alp);
    DrawSetColorRgb(36, 28, 40);
    DrawRoundRect(px, py + dsh, px + cw, py + sz, rad, false);
    if (rnd == 1) { DrawSetColorRgb(226, 214, 238); } else { DrawSetColorRgb(244, 236, 214); }
    DrawRoundRect(px, py, px + cw, py + fh, rad, false);
    DrawSetColorRgb(118, 98, 108);
    DrawRoundRect(px, py, px + cw, py + fh, rad, true);
    DrawSetColorRgb(52, 38, 44);
    if (arrow == 0) {
        DrawTextScaled(px + (cw - tw) * 0.5, py + (fh - DrawTextHeight(ts)) * 0.5, label, ts);
    } else {
        var mcx = px + cw * 0.5; var mcy = py + fh * 0.5; var rr = sz * 0.22;
        if (arrow == 1) { DrawTriangle(mcx - rr, mcy, mcx + rr * 0.8, mcy - rr, mcx + rr * 0.8, mcy + rr, false); }
        else if (arrow == 2) { DrawTriangle(mcx + rr, mcy, mcx - rr * 0.8, mcy - rr, mcx - rr * 0.8, mcy + rr, false); }
        else if (arrow == 3) { DrawTriangle(mcx, mcy - rr, mcx - rr, mcy + rr * 0.8, mcx + rr, mcy + rr * 0.8, false); }
        else { DrawTriangle(mcx, mcy + rr, mcx - rr, mcy - rr * 0.8, mcx + rr, mcy - rr * 0.8, false); }
    }
    DrawSetColorRgb(cr, cg, cb);
    DrawSetAlpha(alp);
    return cw;
}

// One "Pad Glyphs" frame into a square of sz pixels (the image is drawn into a rectangle, so its origin
// does not matter). Returns sz.
function InputDrawFrame(fr, px, py, sz) {
    DrawSpritePart("Assets/Sprites/Pad Glyphs.image.json", fr, 0, 0, 1, 1, px, py, sz, sz, DrawGetAlpha());
    return sz;
}

function InputDrawToken(tok, dv, px, py, sz) {
    if (self.inpReady != 1) { InputInit(); }
    var fr = InputGlyphFrame(tok, dv);
    if (fr >= 0 && InputPadSprite() == 1) { return InputDrawFrame(fr, px, py, sz); }
    var arrow = 0;
    if (dv == 0) {
        if (tok == "Left") { arrow = 1; } else if (tok == "Right") { arrow = 2; } else if (tok == "Up") { arrow = 3; } else if (tok == "Down") { arrow = 4; }
    }
    var rnd = 0;
    if (dv == 1) { rnd = 1; }
    return InputDrawCap(InputTokLabel(tok, dv), arrow, px, py, sz, rnd);
}

function InputDrawGlyph(action, px, py, sz) {
    var dv = InputDevice();
    var ai = InputActionIndex(action);
    if (ai < 0) { return 0; }
    if (dv == 1 && ai < 4) {
        if (InputPadSprite() == 1) { return InputDrawFrame(17, px, py, sz); }
        return InputDrawCap("STICK", 0, px, py, sz, 1);
    }
    var tk = InputTokenAt(ai, dv, 1);
    if (tk == "") { tk = InputTokenAt(ai, dv, 2); }
    return InputDrawToken(tk, dv, px, py, sz);
}

function InputDrawMove(px, py, sz) {
    var dv = InputDevice();
    var w1 = InputDrawGlyph("left", px, py, sz);
    if (dv == 1) { return w1; }
    var gap = Max(2, sz * 0.12);
    var w2 = InputDrawGlyph("right", px + w1 + gap, py, sz);
    return w1 + gap + w2;
}

// ------------------------------------------------------------------ rebinding
function InputCaptureArm() {
    self.inpCapWait = 2;
    return 0;
}

function InputCapture(dv) {
    if (self.inpReady != 1) { InputInit(); }
    if (self.inpCapWait > 0) { self.inpCapWait = self.inpCapWait - 1; return ""; }
    if (dv == 0) {
        var ks = self.inpKeyCap;
        var kn = PgListSize(ks);
        for (var ki = 0; ki < kn; ki = ki + 1) {
            if (KeyPressed(ks[ki])) { return ks[ki]; }
        }
        if (MousePressed(0)) { return "MouseLeft"; }
        if (MousePressed(1)) { return "MouseRight"; }
        if (MousePressed(2)) { return "MouseMiddle"; }
        return "";
    }
    var pc = self.inpPadCap;
    var pn = PgListSize(pc);
    for (var pj = 0; pj < pn; pj = pj + 1) {
        if (GamepadPressed(pc[pj])) { return pc[pj]; }
    }
    return "";
}

function InputSetBinding(action, dv, sl, token) {
    var ai = InputActionIndex(action);
    if (ai < 0) { return ""; }
    if (sl < 1 || sl > 2) { return ""; }
    if (dv == 1 && ai < 4) { return ""; }
    var canon = InputCanon(token, dv);
    var wantEmpty = 0;
    if (StringTrim(token) == "" || StringLower(StringTrim(token)) == "none") { wantEmpty = 1; }
    if (canon == "" && wantEmpty == 0) { return ""; }
    if (canon == "P" || canon == "Start") { return ""; }
    var old = InputTokenAt(ai, dv, sl);
    var swapped = "";
    if (canon != "") {
        for (var aj = 0; aj < 9; aj = aj + 1) {
            if (dv == 1 && aj < 4) { continue; }
            for (var sj = 1; sj <= 2; sj = sj + 1) {
                if (aj == ai && sj == sl) { continue; }
                if (InputTokenAt(aj, dv, sj) == canon) {
                    if (swapped == "") {
                        swapped = InputActionName(aj);
                        if (old == "") { GlobalSetString(InputBindingName(aj, dv, sj), "none"); }
                        else { GlobalSetString(InputBindingName(aj, dv, sj), old); }
                    } else {
                        GlobalSetString(InputBindingName(aj, dv, sj), "none");
                    }
                }
            }
        }
    }
    if (canon == "") { GlobalSetString(InputBindingName(ai, dv, sl), "none"); }
    else { GlobalSetString(InputBindingName(ai, dv, sl), canon); }
    GlobalSet("inp_rev", GlobalGet("inp_rev") + 1);
    InputReload();
    InputSaveBindings();
    return swapped;
}

function InputClearBinding(action, dv, sl) {
    return InputSetBinding(action, dv, sl, "none");
}

function InputResetBindings(dv) {
    for (var ai = 0; ai < 9; ai = ai + 1) {
        for (var sl = 1; sl <= 2; sl = sl + 1) {
            GlobalDelete(InputBindingName(ai, dv, sl));
        }
    }
    GlobalSet("inp_rev", GlobalGet("inp_rev") + 1);
    InputReload();
    InputSaveBindings();
    return 1;
}

// Save slot "Input Bindings": "mmin1" then 36 tokens: keyboard (action, slot) then controller, "none" = empty.
function InputSaveBindings() {
    var blob = "mmin1";
    for (var dv = 0; dv < 2; dv = dv + 1) {
        for (var ai = 0; ai < 9; ai = ai + 1) {
            for (var sl = 1; sl <= 2; sl = sl + 1) {
                var tk = InputTokenAt(ai, dv, sl);
                if (tk == "") { tk = "none"; }
                blob = StringJoin3(blob, ",", tk);
            }
        }
    }
    SaveSlotWrite("Input Bindings", blob);
    return 1;
}

function InputLoadBindings() {
    var blob = SaveSlotRead("Input Bindings");
    if (blob == "") { return 0; }
    if (StringSplitPart(blob, ",", 1) != "mmin1") { return 0; }
    if (StringSplitCount(blob, ",") < 37) { return 0; }
    var pp = 2;
    for (var dv = 0; dv < 2; dv = dv + 1) {
        for (var ai = 0; ai < 9; ai = ai + 1) {
            for (var sl = 1; sl <= 2; sl = sl + 1) {
                GlobalSetString(InputBindingName(ai, dv, sl), StringSplitPart(blob, ",", pp));
                pp = pp + 1;
            }
        }
    }
    GlobalSet("inp_rev", GlobalGet("inp_rev") + 1);
    return 1;
}
"""


def build():
    code = (INPUT_KIT
            .replace("@SCAN_KEYS@", pgsl_list(SCAN_KEYS))
            .replace("@CAPTURE_KEYS@", pgsl_list(CAPTURE_KEYS))
            .replace("@PAD_SCAN@", pgsl_list(PAD_SCAN))
            .replace("@PAD_CAPTURE@", pgsl_list(PAD_CAPTURE)))
    assert "@SCAN" not in code and "@CAPTURE" not in code and "@PAD_" not in code
    return code


def main():
    rel = "Scripts/Input Kit.pgsl"
    write_text(rel, build())
    write_meta(rel, "Script", "Input Kit")
    print("Wrote " + rel)


if __name__ == "__main__":
    main()
