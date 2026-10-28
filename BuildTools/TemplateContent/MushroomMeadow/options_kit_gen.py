"""Mushroom Meadow Options Kit generator: the shared Options menu (Gameplay / Video / Audio tabs, a
Controls screen and a Remap screen) as ONE library Script, used by the in-game pause menu and the
Main Menu.

Re-runnable and deterministic. Run from anywhere:
    python BuildTools/TemplateContent/MushroomMeadow/options_kit_gen.py
Writes into Source/Genesis.Application.Core/Projects/Templates/Assets/TwoDShowcase/Assets:
  Scripts/Options Kit.pgsl (+ .meta)

The Kit has no resources besides itself. It reads/writes the option globals (tips, gore, shake, crt,
sprintModeKb, sprintModePad, windowMode, winSize, vsync, fpsCap, masterVol, musicVol, sfxVol, ambVol),
applies them through the "Apply Options" Script (frontend_gen.py) and calls the Input Kit
(input_kit_gen.py) for binding text, capturing keys/buttons and storing the bindings.
"""
import hashlib
import json
import os

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
ASSETS = os.path.join(ROOT, "Source", "Genesis.Application.Core", "Projects", "Templates",
                      "Assets", "TwoDShowcase", "Assets")
STAMP = "2026-10-15T00:00:00Z"


def guid(name):
    return hashlib.md5(("mushroom-meadow-options:" + name).encode("utf-8")).hexdigest()


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


# The PGSL below is a plain (not %-formatted) string: write PGSL operators freely.
OPTIONS_KIT = r"""// Options Kit: a library Script. ONE options menu used by the in-game pause menu and the Main Menu.
// Everything draws in Draw GUI in a 1280x720 design space scaled to the window (sx = width / 1280,
// sy = height / 720) and works with keyboard (arrows/WASD, Enter/Space, Esc/Backspace, Q/E pages,
// Delete clears a binding), controller (D-pad/left stick, A, B, LB/RB pages, Y clears) and mouse
// (hover, click, drag sliders, wheel, right-click clears a binding).
//
// API (call from any object; all state lives on the CALLER'S instance, variables prefixed opt):
//   OptionsOpen(context)   context 0 = Main Menu, 1 = in-game pause menu; starts at Gameplay
//   OptionsStep()          once per frame in Step while OptionsIsOpen() == 1; returns 1 while open
//   OptionsDraw()          in Draw GUI every frame while open (draws a dim layer + the panel)
//   OptionsIsOpen()        1 while the menu is open
//   OptionsClose()         saves ("Apply Options", 1) and closes (Esc/B on the first screen does this)
// Pages: Gameplay (Enable Tips, Gore, Screen Shake, Difficulty, Controls >), Video (Window Mode, Window Size,
// VSync, Frame Rate Limit, CRT Scanlines), Audio (Master, Music, Sound Effects, Ambience).
// Controls screen: Control Scheme (keyboard+mouse / controller), Sprint hold/toggle for that scheme,
// Remap Controls >. Remap screen: nine actions x two bindings; Enter/click = press the new key or
// button (Esc / Start cancels), Delete / Y / right-click clears, a key already in use is swapped.
//
// Caller instance variables (all set by OptionsOpen): optOpen, optCtx, optPage, optView (0 tabs,
// 1 controls, 2 remap), optSel (-1 = tab bar), optScroll, optHover, optHoverTab, optT, optCap,
// optCapAct, optCapSlot, optCapGrace, optSlot, optHeld, optHoldT, optDrag, optMsg, optMsgT,
// optConfirm, optConfirmT, optDirty, optOpenedAt, optRawX, optRawY, optPad, optScheme, optHasInput,
// optHasSnd, optActions, optActionNames, optKeys, and the layout values optSx, optSy, optS, optPx,
// optPy, optPw, optPh, optRowX, optRowW, optRowY0, optRowH, optPitch, optVis, optTabX0, optTabW.
//
// Calls into the Input Kit (all in the OptIn* wrappers below, guarded by optHasInput):
//   InputInit() (main menu only), InputUpdate() (main menu only), InputDevice(), InputBindingText,
//   InputCapture, InputSetBinding, InputResetBindings, InputDrawGlyph. Device numbers: 0 keyboard +
//   mouse, 1 controller; slots 1 and 2. Binding tokens are the strings kept in the globals
//   bind_<action>_<kb|pad>_<1|2>; the Input Kit keeps them in its own save slot "Input Bindings" and
//   swaps a clashing binding itself (InputSetBinding returns the swapped action). InputCaptureArm() is
//   called when the rebind prompt opens, InputBindingGet() tells whether a slot is bound.

function OptionsOpen(context) {
    self.optOpen = 1;
    self.optCtx = context;
    self.optPage = 0;
    self.optView = 0;
    self.optSel = 0;
    self.optScroll = 0;
    self.optHover = -1;
    self.optHoverTab = -1;
    self.optT = 0;
    self.optCap = 0;
    self.optCapAct = 0;
    self.optCapSlot = 1;
    self.optCapGrace = 0;
    self.optSlot = 1;
    self.optHeld = 9;
    self.optHoldT = 0;
    self.optDrag = -1;
    self.optMsg = "";
    self.optMsgT = 0;
    self.optConfirm = 0;
    self.optConfirmT = 0;
    self.optDirty = 0;
    self.optOpenedAt = TimeMs();
    self.optRawX = UiMouseX();
    self.optRawY = UiMouseY();
    self.optPad = 0;
    self.optScheme = 0;
    self.optHasInput = 0;
    if (ResourceExists("Input Kit")) { self.optHasInput = 1; }
    self.optHasSnd = 0;
    if (ResourceExists("Menu Move")) { self.optHasSnd = 1; }
    self.optActions = ["left", "right", "up", "down", "jump", "sprint", "use", "throw", "interact"];
    self.optActionNames = ["MOVE LEFT", "MOVE RIGHT", "UP", "DOWN", "JUMP", "SPRINT", "USE ITEM", "THROW ITEM", "INTERACT"];
    self.optSx = 1; self.optSy = 1; self.optS = 1;
    self.optPx = 190; self.optPy = 92; self.optPw = 900; self.optPh = 540;
    self.optRowX = 226; self.optRowW = 828; self.optRowY0 = 198; self.optRowH = 40; self.optPitch = 44; self.optVis = 8;
    self.optTabX0 = 341; self.optTabW = 190;
    self.optKeys = [];
    ScriptExecute("Apply Options");
    if (self.optHasInput == 1) {
        if (context == 0) { InputInit(); }
        if (InputDevice() == 1) { self.optScheme = 1; self.optPad = 1; }
    }
    WindowSetCursorVisible(true);
    OptLayout();
    OptRebuild();
    return 1;
}

function OptionsIsOpen() {
    return self.optOpen;
}

function OptionsClose() {
    if (self.optOpen != 1) { return 0; }
    self.optOpen = 0;
    self.optCap = 0;
    self.optDrag = -1;
    ScriptExecute("Apply Options", 1);
    OptSound(2);
    return 1;
}

// ------------------------------------------------------------------ small helpers
function OptSay(msg) {
    self.optMsg = msg;
    self.optMsgT = 2.6;
    return 0;
}

function OptSound(soundKind) {
    if (self.optHasSnd != 1) { return 0; }
    if (soundKind == 0) { PlaySound("Assets/Audio/Menu Move.audio.json", 0.5, 1, false); }
    else if (soundKind == 1) { PlaySound("Assets/Audio/Menu Select.audio.json", 0.7, 1, false); }
    else if (soundKind == 2) { PlaySound("Assets/Audio/Menu Back.audio.json", 0.7, 1, false); }
    else { PlaySound("Assets/Audio/Shop Deny.audio.json", 0.7, 1, false); }
    return 0;
}

// 1 slider, 2 choice (left/right changes it), 3 opens a screen, 4 button, 5 remap row
function OptKind(rowKey) {
    if (rowKey == "master" || rowKey == "music" || rowKey == "sfx" || rowKey == "amb") { return 1; }
    if (rowKey == "controls" || rowKey == "remap") { return 3; }
    if (rowKey == "back" || rowKey == "reset") { return 4; }
    if (StringCopy(rowKey, 1, 3) == "act") { return 5; }
    return 2;
}

function OptVolKey(rowKey) {
    if (rowKey == "master") { return "masterVol"; }
    if (rowKey == "music") { return "musicVol"; }
    if (rowKey == "sfx") { return "sfxVol"; }
    return "ambVol";
}

function OptLabel(rowKey) {
    if (rowKey == "tips") { return "ENABLE TIPS"; }
    if (rowKey == "gore") { return "GORE"; }
    if (rowKey == "shake") { return "SCREEN SHAKE"; }
    if (rowKey == "controls") { return "CONTROLS"; }
    if (rowKey == "difficulty") { return "DIFFICULTY"; }
    if (rowKey == "winmode") { return "WINDOW MODE"; }
    if (rowKey == "winsize") { return "WINDOW SIZE"; }
    if (rowKey == "vsync") { return "VSYNC"; }
    if (rowKey == "fpscap") { return "FRAME RATE LIMIT"; }
    if (rowKey == "crt") { return "CRT SCANLINES"; }
    if (rowKey == "master") { return "MASTER VOLUME"; }
    if (rowKey == "music") { return "MUSIC"; }
    if (rowKey == "sfx") { return "SOUND EFFECTS"; }
    if (rowKey == "amb") { return "AMBIENCE"; }
    if (rowKey == "scheme") { return "CONTROL SCHEME"; }
    if (rowKey == "sprint") { return "SPRINT"; }
    if (rowKey == "remap") { return "REMAP CONTROLS"; }
    if (rowKey == "reset") {
        if (self.optConfirm == 1) { return "PRESS AGAIN TO CONFIRM"; }
        return "RESET TO DEFAULTS";
    }
    if (rowKey == "back") { return "BACK"; }
    if (OptKind(rowKey) == 5) { return self.optActionNames[Real(StringCopy(rowKey, 4, 1))]; }
    return rowKey;
}

function OptHelp(rowKey) {
    if (rowKey == "tips") { return "Shows level cards and the control hints along the bottom."; }
    if (rowKey == "gore") { return "Blood and gruesome bits when foes fall. Off keeps the tidy poof."; }
    if (rowKey == "shake") { return "Shake the view on heavy hits and landings."; }
    if (rowKey == "controls") { return "Choose keyboard or controller, sprint style, and remap every button."; }
    if (rowKey == "difficulty") {
        if (self.optCtx == 0) { return "The difficulty offered to a new adventure. Each saved adventure keeps its own."; }
        return DiffName(DiffRaw()) + ": " + DiffBlurb(DiffRaw(), 0);
    }
    if (rowKey == "winmode") { return "Windowed, borderless fullscreen, or exclusive fullscreen."; }
    if (rowKey == "winsize") { return "The size of the game window while windowed."; }
    if (rowKey == "vsync") { return "Waits for your display to refresh: no tearing. Off can run faster."; }
    if (rowKey == "fpscap") {
        return "Room default follows each room. Now running at " + StringOf(Round(GameGetFps())) + " FPS.";
    }
    if (rowKey == "crt") { return "Old television scanlines over the game."; }
    if (rowKey == "master") { return "The overall volume of everything."; }
    if (rowKey == "music") { return "Music volume."; }
    if (rowKey == "sfx") { return "Footsteps, jumps, hits and menu sounds."; }
    if (rowKey == "amb") { return "Wind, water and the sounds of each place."; }
    if (rowKey == "scheme") { return "Pick which device you are editing. Both stay active in play."; }
    if (rowKey == "sprint") { return "Hold the sprint button to run, or press once to switch running on and off."; }
    if (rowKey == "remap") { return "Two keys or buttons per action. A key already in use is swapped over."; }
    if (rowKey == "reset") { return "Restores the default keys or buttons for this device."; }
    if (OptKind(rowKey) == 5) {
        if (self.optScheme == 1) { return "A or click to rebind. Y clears. Left / Right picks the slot."; }
        return "Enter or click to rebind. Delete clears. Left / Right picks the slot.";
    }
    return "";
}

function OptOnOff(flag) {
    if (flag >= 1) { return "ON"; }
    return "OFF";
}

function OptWinMode() {
    var wm = GlobalGet("windowMode");
    if (wm >= 0) { return wm; }
    var cur = WindowGetMode();
    if (cur == "borderless") { return 1; }
    if (cur == "fullscreen") { return 2; }
    return 0;
}

function OptVsync() {
    var vs = GlobalGet("vsync");
    if (vs >= 0) { return vs; }
    if (WindowGetVSync()) { return 1; }
    return 0;
}

function OptWinSizeIndex() {
    var ws = GlobalGet("winSize");
    if (ws >= 0) { return ws; }
    var widths = [1280, 1600, 1920, 2560];
    var best = 0;
    var bestGap = 99999;
    var curW = WindowGetWidth();
    for (var wi = 0; wi < 4; wi = wi + 1) {
        var gap = Abs(curW - widths[wi]);
        if (gap < bestGap) { bestGap = gap; best = wi; }
    }
    return best;
}

function OptValueText(rowKey) {
    if (rowKey == "tips") { return OptOnOff(GlobalGet("tips")); }
    if (rowKey == "gore") { return OptOnOff(GlobalGet("gore")); }
    if (rowKey == "shake") { return OptOnOff(GlobalGet("shake")); }
    if (rowKey == "crt") { return OptOnOff(GlobalGet("crt")); }
    if (rowKey == "difficulty") { return DiffName(DiffRaw()); }
    if (rowKey == "vsync") { return OptOnOff(OptVsync()); }
    if (rowKey == "scheme") {
        if (self.optScheme == 1) { return "CONTROLLER"; }
        return "KEYBOARD / MOUSE";
    }
    if (rowKey == "sprint") {
        var sm = GlobalGet("sprintModeKb");
        if (self.optScheme == 1) { sm = GlobalGet("sprintModePad"); }
        if (sm >= 1) { return "TOGGLE"; }
        return "HOLD";
    }
    if (rowKey == "winmode") {
        var wmIdx = OptWinMode();
        if (wmIdx == 1) { return "BORDERLESS"; }
        if (wmIdx == 2) { return "FULLSCREEN"; }
        return "WINDOWED";
    }
    if (rowKey == "winsize") {
        var sizeW = [1280, 1600, 1920, 2560];
        var sizeH = [720, 900, 1080, 1440];
        var sizeIdx = OptWinSizeIndex();
        return StringOf(sizeW[sizeIdx]) + " x " + StringOf(sizeH[sizeIdx]);
    }
    if (rowKey == "fpscap") {
        var fc = GlobalGet("fpsCap");
        if (fc < 0) { return "ROOM DEFAULT"; }
        if (fc == 0) { return "UNLIMITED"; }
        return StringOf(fc) + " FPS";
    }
    return "";
}

// ------------------------------------------------------------------ Input Kit wrappers (the only call sites)
function OptInText(act, dev, slot) {
    if (self.optHasInput != 1) { return "?"; }
    return InputBindingText(act, dev, slot);
}

function OptInCapture(dev) {
    if (self.optHasInput != 1) { return ""; }
    return InputCapture(dev);
}

function OptInSet(act, dev, slot, tok) {
    if (self.optHasInput != 1) { return 0; }
    InputSetBinding(act, dev, slot, tok);
    return 1;
}

function OptInReset(dev) {
    if (self.optHasInput != 1) { return 0; }
    InputResetBindings(dev);
    return 1;
}

function OptInGlyph(act, gx, gy, gsize) {
    if (self.optHasInput != 1) { return 0; }
    var gw = InputDrawGlyph(act, gx * self.optSx, gy * self.optSy, gsize * self.optS);
    if (gw <= 0) { return gsize; }
    return gw / self.optSx;
}

function OptIsBound(act, dev, slot) {
    if (self.optHasInput != 1) { return 0; }
    if (InputBindingGet(act, dev, slot) != "") { return 1; }
    return 0;
}

// The four direction actions of a controller are the stick and D-pad: shown, not remappable.
function OptFixed(actIdx) {
    if (self.optScheme == 1 && actIdx < 4) { return 1; }
    return 0;
}

// ------------------------------------------------------------------ layout and rows
function OptLayout() {
    var sw = WindowGetWidth();
    var sh = WindowGetHeight();
    self.optSx = Max(0.1, sw / 1280);
    self.optSy = Max(0.1, sh / 720);
    self.optS = Min(self.optSx, self.optSy);
    self.optPx = 190;
    self.optPw = 900;
    if (self.optCtx == 0) { self.optPy = 228; self.optPh = 452; }
    else { self.optPy = 92; self.optPh = 540; }
    self.optRowX = self.optPx + 36;
    self.optRowW = self.optPw - 72;
    self.optRowY0 = self.optPy + 106;
    self.optRowH = 40;
    self.optPitch = 44;
    self.optVis = Max(3, Floor((self.optPh - 106 - 66) / 44));
    self.optTabW = 190;
    self.optTabX0 = self.optPx + (self.optPw - 598) / 2;
    return 0;
}

function OptRebuild() {
    var keys = [];
    if (self.optView == 0) {
        if (self.optPage == 0) { keys = ["tips", "gore", "shake", "difficulty", "controls", "back"]; }
        else if (self.optPage == 1) { keys = ["winmode", "winsize", "vsync", "fpscap", "crt", "back"]; }
        else { keys = ["master", "music", "sfx", "amb", "back"]; }
    } else if (self.optView == 1) {
        keys = ["scheme", "sprint", "remap", "back"];
    } else {
        keys = ["act0", "act1", "act2", "act3", "act4", "act5", "act6", "act7", "act8", "reset", "back"];
    }
    self.optKeys = keys;
    self.optDrag = -1;
    self.optConfirm = 0;
    var n = PgListSize(keys);
    if (self.optSel >= n) { self.optSel = n - 1; }
    if (self.optView > 0 && self.optSel < 0) { self.optSel = 0; }
    OptEnsureVisible();
    return 0;
}

function OptEnsureVisible() {
    var n = PgListSize(self.optKeys);
    if (self.optSel < 0) { self.optScroll = 0; return 0; }
    if (self.optSel < self.optScroll) { self.optScroll = self.optSel; }
    if (self.optSel >= self.optScroll + self.optVis) { self.optScroll = self.optSel - self.optVis + 1; }
    self.optScroll = Clamp(self.optScroll, 0, Max(0, n - self.optVis));
    return 0;
}

function OptMoveSel(stepDir) {
    var n = PgListSize(self.optKeys);
    var cur = self.optSel + stepDir;
    if (self.optView == 0) {
        if (cur >= n) { cur = -1; }
        if (cur < -1) { cur = n - 1; }
    } else {
        if (cur >= n) { cur = 0; }
        if (cur < 0) { cur = n - 1; }
    }
    self.optSel = cur;
    self.optConfirm = 0;
    OptEnsureVisible();
    OptSound(0);
    return 0;
}

function OptSwitchPage(newPage) {
    self.optPage = (newPage + 3) % 3;
    self.optScroll = 0;
    OptRebuild();
    OptSound(0);
    return 0;
}

// ------------------------------------------------------------------ changing values
function OptSetVol(rowKey, amount) {
    var volName = OptVolKey(rowKey);
    var volNew = Clamp(amount, 0, 1);
    if (Abs(GlobalGet(volName) - volNew) < 0.001) { return 0; }
    GlobalSet(volName, volNew);
    ScriptExecute("Apply Options");
    self.optDirty = 1;
    return 1;
}

function OptSliderTo(rowKey, mouseDesignX) {
    var frac = Clamp((mouseDesignX - (self.optRowX + 330)) / 400, 0, 1);
    OptSetVol(rowKey, Round(frac * 20) / 20);
    return 0;
}

function OptChange(rowKey, dr) {
    var changed = 0;
    var apply = 1;
    if (rowKey == "tips") { GlobalSet("tips", 1 - GlobalGet("tips")); changed = 1; }
    else if (rowKey == "gore") { GlobalSet("gore", 1 - GlobalGet("gore")); changed = 1; }
    else if (rowKey == "shake") { GlobalSet("shake", 1 - GlobalGet("shake")); changed = 1; }
    else if (rowKey == "crt") { GlobalSet("crt", 1 - GlobalGet("crt")); changed = 1; }
    else if (rowKey == "difficulty") {
        var diffOld = DiffRaw();
        var diffNew = (diffOld + dr + 3) % 3;
        DiffChange(diffOld, diffNew);
        changed = 1;
    }
    else if (rowKey == "scheme") { self.optScheme = 1 - self.optScheme; apply = 0; changed = 1; }
    else if (rowKey == "sprint") {
        if (self.optScheme == 0) { GlobalSet("sprintModeKb", 1 - GlobalGet("sprintModeKb")); }
        else { GlobalSet("sprintModePad", 1 - GlobalGet("sprintModePad")); }
        changed = 1;
    }
    else if (rowKey == "winmode") {
        var wmNew = (OptWinMode() + dr + 3) % 3;
        GlobalSet("windowMode", wmNew);
        changed = 1;
    }
    else if (rowKey == "winsize") {
        if (OptWinMode() != 0) {
            OptSay("WINDOW SIZE ONLY APPLIES IN WINDOWED MODE");
            OptSound(3);
            return 0;
        }
        GlobalSet("winSize", (OptWinSizeIndex() + dr + 4) % 4);
        changed = 1;
    }
    else if (rowKey == "vsync") { GlobalSet("vsync", 1 - OptVsync()); changed = 1; }
    else if (rowKey == "fpscap") {
        var caps = [-1, 30, 60, 120, 144, 240, 0];
        var capIdx = PgListFind(caps, GlobalGet("fpsCap"));
        if (capIdx < 0) { capIdx = 0; }
        capIdx = (capIdx + dr + 7) % 7;
        GlobalSet("fpsCap", caps[capIdx]);
        changed = 1;
    }
    else if (OptKind(rowKey) == 1) {
        var volName = OptVolKey(rowKey);
        var volNew = Clamp(Round(GlobalGet(volName) * 20 + dr) / 20, 0, 1);
        if (Abs(volNew - GlobalGet(volName)) > 0.001) {
            GlobalSet(volName, volNew);
            changed = 1;
        }
    }
    if (changed == 1) {
        if (apply == 1) {
            ScriptExecute("Apply Options");
            self.optDirty = 1;
        }
        OptSound(0);
    }
    return changed;
}

// ------------------------------------------------------------------ remapping
function OptBeginCapture(actIdx, slot) {
    if (self.optHasInput != 1) { OptSay("THE INPUT KIT IS MISSING"); OptSound(3); return 0; }
    if (OptFixed(actIdx) == 1) { OptSay("THE STICK AND D-PAD ALWAYS MOVE PIP"); OptSound(3); return 0; }
    self.optCap = 1;
    self.optCapAct = actIdx;
    self.optCapSlot = slot;
    self.optCapGrace = 0.25;
    if (self.optHasInput == 1) { InputCaptureArm(); }
    OptSound(1);
    return 1;
}

function OptAssign(actIdx, slot, tok) {
    var dev = self.optScheme;
    var act = self.optActions[actIdx];
    if (dev == 0 && tok == "P") {
        OptSay("P ALWAYS PAUSES THE GAME");
        OptSound(3);
        return 0;
    }
    // The Input Kit swaps on a clash and saves its own bindings; it returns the other action's name.
    var swapped = "";
    if (self.optHasInput == 1) { swapped = InputSetBinding(act, dev, slot, tok); }
    var swapIdx = PgListFind(self.optActions, swapped);
    if (swapped != "" && swapIdx >= 0) { OptSay("SWAPPED WITH " + self.optActionNames[swapIdx]); }
    else { OptSay("BOUND"); }
    OptSound(1);
    return 1;
}

function OptClearSlot(actIdx, slot) {
    if (OptFixed(actIdx) == 1) { OptSay("THE STICK AND D-PAD ALWAYS MOVE PIP"); OptSound(3); return 0; }
    var act = self.optActions[actIdx];
    if (OptIsBound(act, self.optScheme, slot) != 1) { return 0; }
    if (OptIsBound(act, self.optScheme, 3 - slot) != 1) {
        OptSay("EVERY ACTION KEEPS AT LEAST ONE BINDING");
        OptSound(3);
        return 0;
    }
    OptInSet(act, self.optScheme, slot, "none");
    OptSay("CLEARED");
    OptSound(2);
    return 1;
}

function OptCaptureStep() {
    if (KeyPressed("Escape") || GamepadPressed("Start")) {
        self.optCap = 0;
        OptSay("CANCELLED");
        OptSound(2);
        return 0;
    }
    if (self.optCapGrace > 0) { return 0; }
    var tok = OptInCapture(self.optScheme);
    if (tok == "") { return 0; }
    self.optCap = 0;
    OptAssign(self.optCapAct, self.optCapSlot, tok);
    return 1;
}

// ------------------------------------------------------------------ activating rows
function OptBack() {
    if (self.optView == 2) {
        self.optView = 1; self.optSel = 2; self.optScroll = 0;
        OptRebuild();
        OptSound(2);
        return 1;
    }
    if (self.optView == 1) {
        self.optView = 0; self.optSel = 4; self.optScroll = 0;
        OptRebuild();
        OptSound(2);
        return 1;
    }
    OptionsClose();
    return 1;
}

function OptActivate(rowIdx, dr) {
    var rk = self.optKeys[rowIdx];
    var rowKind = OptKind(rk);
    self.optSel = rowIdx;
    if (rowKind == 2) { OptChange(rk, dr); return 1; }
    if (rowKind == 1) { return 0; }
    if (rk == "controls") {
        self.optView = 1; self.optSel = 0; self.optScroll = 0;
        OptRebuild();
        OptSound(1);
        return 1;
    }
    if (rk == "remap") {
        self.optView = 2; self.optSel = 0; self.optScroll = 0; self.optSlot = 1;
        OptRebuild();
        OptSound(1);
        return 1;
    }
    if (rk == "back") { OptBack(); return 1; }
    if (rk == "reset") {
        if (self.optConfirm == 0) {
            self.optConfirm = 1;
            self.optConfirmT = 3;
            OptSay("PRESS AGAIN TO RESTORE THE DEFAULTS");
            OptSound(0);
            return 1;
        }
        OptInReset(self.optScheme);
        if (self.optScheme == 0) { GlobalSet("sprintModeKb", 0); }
        else { GlobalSet("sprintModePad", 0); }
        self.optConfirm = 0;
        self.optConfirmT = 0;
        OptSay("DEFAULTS RESTORED");
        ScriptExecute("Apply Options", 1);
        OptSound(1);
        return 1;
    }
    if (rowKind == 5) { OptBeginCapture(Real(StringCopy(rk, 4, 1)), self.optSlot); return 1; }
    return 0;
}

function OptClickRow(rowIdx, mouseDesignX) {
    var rk = self.optKeys[rowIdx];
    var rowKind = OptKind(rk);
    self.optSel = rowIdx;
    if (rowKind == 1) {
        if (mouseDesignX >= self.optRowX + 316 && mouseDesignX <= self.optRowX + 746) {
            self.optDrag = rowIdx;
            OptSliderTo(rk, mouseDesignX);
        }
        return 0;
    }
    if (rowKind == 2) {
        var dr = 1;
        var valueX = self.optRowX + 330;
        if (mouseDesignX >= valueX && mouseDesignX < valueX + 240) { dr = -1; }
        OptChange(rk, dr);
        return 1;
    }
    if (rowKind == 5) {
        if (mouseDesignX >= self.optRowX + 546) { self.optSlot = 2; } else { self.optSlot = 1; }
        OptBeginCapture(Real(StringCopy(rk, 4, 1)), self.optSlot);
        return 1;
    }
    OptActivate(rowIdx, 1);
    return 1;
}

// ------------------------------------------------------------------ the per-frame step
function OptionsStep() {
    if (self.optOpen != 1) { return 0; }
    var dt = Clamp(DeltaTime, 0, 0.05);
    self.optT = self.optT + dt;
    if (self.optMsgT > 0) { self.optMsgT = self.optMsgT - dt; }
    if (self.optCapGrace > 0) { self.optCapGrace = self.optCapGrace - dt; }
    if (self.optConfirmT > 0) {
        self.optConfirmT = self.optConfirmT - dt;
        if (self.optConfirmT <= 0) { self.optConfirm = 0; }
    }
    OptLayout();
    if (self.optHasInput == 1 && self.optCtx == 0) { InputUpdate(); }
    if (self.optCap == 1) {
        OptCaptureStep();
        return 1;
    }

    var settled = 0;
    if (TimeMs() - self.optOpenedAt > 100) { settled = 1; }
    var stickX = GamepadAxis("LeftX");
    var stickY = GamepadAxis("LeftY");

    var accept = 0;
    if (KeyPressed("Enter") || KeyPressed("Space") || GamepadPressed("A")) { accept = 1; }
    var back = 0;
    if (KeyPressed("Escape") || KeyPressed("Backspace") || GamepadPressed("B")) { back = 1; }
    var wipe = 0;
    if (KeyPressed("Delete") || GamepadPressed("Y")) { wipe = 1; }
    var pageStep = 0;
    if (KeyPressed("Q") || GamepadPressed("LB")) { pageStep = -1; }
    if (KeyPressed("E") || GamepadPressed("RB")) { pageStep = 1; }

    // Which device was touched last decides which hints are shown.
    var padTouched = 0;
    if (GamepadPressed("A") || GamepadPressed("B") || GamepadPressed("Y") || GamepadPressed("LB") || GamepadPressed("RB")
        || GamepadPressed("DPadUp") || GamepadPressed("DPadDown") || GamepadPressed("DPadLeft") || GamepadPressed("DPadRight")
        || Abs(stickX) > 0.6 || Abs(stickY) > 0.6) { padTouched = 1; }
    var keyTouched = 0;
    if (KeyPressed("Enter") || KeyPressed("Space") || KeyPressed("Escape") || KeyPressed("Up") || KeyPressed("Down")
        || KeyPressed("Left") || KeyPressed("Right") || KeyPressed("W") || KeyPressed("S") || KeyPressed("A") || KeyPressed("D")
        || KeyPressed("Delete") || KeyPressed("Q") || KeyPressed("E")) { keyTouched = 1; }
    if (padTouched == 1) { self.optPad = 1; }
    else if (keyTouched == 1) { self.optPad = 0; }

    // One direction at a time with auto-repeat (vertical and sliders repeat; switches fire once).
    var held = 0;
    if (KeyCheck("Up") || KeyCheck("W") || GamepadCheck("DPadUp") || stickY > 0.6) { held = 1; }
    else if (KeyCheck("Down") || KeyCheck("S") || GamepadCheck("DPadDown") || stickY < -0.6) { held = 2; }
    else if (KeyCheck("Left") || KeyCheck("A") || GamepadCheck("DPadLeft") || stickX < -0.6) { held = 3; }
    else if (KeyCheck("Right") || KeyCheck("D") || GamepadCheck("DPadRight") || stickX > 0.6) { held = 4; }
    var currentKey = "";
    if (self.optSel >= 0 && self.optSel < PgListSize(self.optKeys)) { currentKey = self.optKeys[self.optSel]; }
    var repeatOk = 0;
    if (held <= 2 || OptKind(currentKey) == 1) { repeatOk = 1; }
    var fire = 0;
    if (held == 0) { self.optHeld = 0; }
    else if (self.optHeld != 9) {
        if (held != self.optHeld) {
            fire = held;
            self.optHeld = held;
            self.optHoldT = 0.38;
        } else {
            self.optHoldT = self.optHoldT - dt;
            if (self.optHoldT <= 0) {
                self.optHoldT = 0.09;
                if (repeatOk == 1) { fire = held; }
            }
        }
    }
    var navV = 0;
    var navH = 0;
    if (fire == 1) { navV = -1; }
    if (fire == 2) { navV = 1; }
    if (fire == 3) { navH = -1; }
    if (fire == 4) { navH = 1; }

    // Mouse: hover moves the selection, click acts, wheel scrolls or nudges a slider.
    var rawX = UiMouseX();
    var rawY = UiMouseY();
    var mouseMoved = 0;
    if (Abs(rawX - self.optRawX) + Abs(rawY - self.optRawY) > 2) { mouseMoved = 1; }
    self.optRawX = rawX;
    self.optRawY = rawY;
    var mx = rawX / self.optSx;
    var my = rawY / self.optSy;
    var click = 0;
    if (MousePressed(MbLeft)) { click = 1; }
    var rclick = 0;
    if (MousePressed(MbRight)) { rclick = 1; }
    var wheel = MouseWheel();
    if (mouseMoved == 1 || click == 1) { self.optPad = 0; }

    var n = PgListSize(self.optKeys);
    self.optHover = -1;
    self.optHoverTab = -1;
    if (self.optView == 0) {
        for (var ti = 0; ti < 3; ti = ti + 1) {
            var tabX = self.optTabX0 + ti * (self.optTabW + 14);
            if (mx >= tabX && mx < tabX + self.optTabW && my >= self.optPy + 56 && my < self.optPy + 90) { self.optHoverTab = ti; }
        }
    }
    for (var hv = 0; hv < self.optVis; hv = hv + 1) {
        var hi = self.optScroll + hv;
        var hy = self.optRowY0 + hv * self.optPitch;
        if (hi < n && mx >= self.optRowX && mx < self.optRowX + self.optRowW && my >= hy && my < hy + self.optRowH) {
            self.optHover = hi;
        }
    }
    if (mouseMoved == 1 && self.optHover >= 0 && self.optHover != self.optSel) {
        self.optSel = self.optHover;
        self.optConfirm = 0;
    }

    if (self.optDrag >= 0) {
        if (MouseCheck(MbLeft)) { OptSliderTo(self.optKeys[self.optDrag], mx); }
        else { self.optDrag = -1; }
    }

    if (settled == 1) {
        if (click == 1) {
            if (self.optHoverTab >= 0) {
                if (self.optHoverTab != self.optPage) { OptSwitchPage(self.optHoverTab); }
            } else if (self.optHover >= 0) {
                OptClickRow(self.optHover, mx);
            }
        }
        if (rclick == 1 && self.optView == 2 && self.optHover >= 0) {
            var rrk = self.optKeys[self.optHover];
            if (OptKind(rrk) == 5) {
                if (mx >= self.optRowX + 546) { self.optSlot = 2; } else { self.optSlot = 1; }
                OptClearSlot(Real(StringCopy(rrk, 4, 1)), self.optSlot);
            }
        }
    }

    if (wheel != 0) {
        var wheelDone = 0;
        if (self.optHover >= 0) {
            var wrk = self.optKeys[self.optHover];
            if (OptKind(wrk) == 1) {
                if (wheel > 0) { OptChange(wrk, 1); } else { OptChange(wrk, -1); }
                wheelDone = 1;
            }
        }
        if (wheelDone == 0) {
            if (wheel > 0) { navV = -1; } else { navV = 1; }
        }
    }

    if (pageStep != 0 && self.optView == 0) { OptSwitchPage(self.optPage + pageStep); }
    if (navV != 0) { OptMoveSel(navV); }
    if (navH != 0) {
        if (self.optSel == -1) { OptSwitchPage(self.optPage + navH); }
        else if (self.optSel >= 0 && self.optSel < n) {
            var nrk = self.optKeys[self.optSel];
            var nKind = OptKind(nrk);
            if (nKind == 1 || nKind == 2) { OptChange(nrk, navH); }
            else if (nKind == 5) {
                if (navH < 0) { self.optSlot = 1; } else { self.optSlot = 2; }
                OptSound(0);
            }
        }
    }
    if (settled == 1) {
        if (accept == 1) {
            if (self.optSel == -1) { OptMoveSel(1); }
            else if (self.optSel >= 0 && self.optSel < n) { OptActivate(self.optSel, 1); }
        }
        if (wipe == 1 && self.optView == 2 && self.optSel >= 0 && self.optSel < n) {
            var wiRk = self.optKeys[self.optSel];
            if (OptKind(wiRk) == 5) { OptClearSlot(Real(StringCopy(wiRk, 4, 1)), self.optSlot); }
        }
        if (back == 1) { OptBack(); }
    }
    return self.optOpen;
}

// ------------------------------------------------------------------ drawing helpers
function OptText(tx, ty, msg, size, align, cr, cg, cb, ca, shadow) {
    var sx = self.optSx;
    var sy = self.optSy;
    var sc = self.optS;
    DrawSetTextAlign(align);
    if (shadow == 1) {
        DrawSetColorRgb(8, 6, 20);
        DrawSetAlpha(ca * 0.7);
        DrawTextScaled((tx + 1.5) * sx, (ty + 1.5) * sy, msg, size * sc);
    }
    DrawSetColorRgb(cr, cg, cb);
    DrawSetAlpha(ca);
    DrawTextScaled(tx * sx, ty * sy, msg, size * sc);
    return 0;
}

function OptRound(x1, y1, x2, y2, radius, border) {
    DrawRoundRectEx(x1 * self.optSx, y1 * self.optSy, x2 * self.optSx, y2 * self.optSy, radius * self.optS, border * self.optS);
    return 0;
}

// A key cap / pad glyph box. state: 0 normal, 1 selected slot, 2 waiting for input, 3 fixed, 4 unbound.
function OptCap(cx, cy, cw, ch, msg, dev, state, fade) {
    var br = 238; var bg = 226; var bb = 190;
    var tr = 42; var tg = 29; var tb = 46;
    if (dev == 1) { br = 150; bg = 214; bb = 214; tr = 20; tg = 40; tb = 52; }
    if (state == 3) { br = 96; bg = 100; bb = 140; tr = 214; tg = 214; tb = 232; }
    if (state == 4) { br = 54; bg = 62; bb = 104; tr = 170; tg = 170; tb = 196; }
    if (state == 2) {
        var pulse = 0.5 + 0.5 * Sin(self.optT * 360);
        br = 255; bg = 196 + 40 * pulse; bb = 90 + 60 * pulse;
    }
    DrawSetColorRgb(10, 8, 24); DrawSetAlpha(0.55 * fade);
    OptRound(cx, cy + 3, cx + cw, cy + ch + 3, 8, 0);
    DrawSetColorRgb(br, bg, bb); DrawSetAlpha(fade);
    OptRound(cx, cy, cx + cw, cy + ch, 8, 0);
    if (state == 1) { DrawSetColorRgb(255, 120, 60); DrawSetAlpha(fade); OptRound(cx, cy, cx + cw, cy + ch, 8, 3); }
    else { DrawSetColorRgb(60, 44, 70); DrawSetAlpha(0.7 * fade); OptRound(cx, cy, cx + cw, cy + ch, 8, 1.5); }
    OptText(cx + cw / 2, cy + 7, msg, 18, "center", tr, tg, tb, fade, 0);
    return 0;
}

function OptDrawRow(rowIdx, slotIdx, fade) {
    var rk = self.optKeys[rowIdx];
    var rowKind = OptKind(rk);
    var rx = self.optRowX;
    var rw = self.optRowW;
    var ry = self.optRowY0 + slotIdx * self.optPitch;
    var rh = self.optRowH;
    var isSel = 0;
    if (rowIdx == self.optSel) { isSel = 1; }
    var isHot = 0;
    if (rowIdx == self.optHover) { isHot = 1; }
    var lr = 246; var lg = 231; var lb = 193;
    if (isSel == 1) {
        DrawSetColorRgb(244, 182, 62); DrawSetAlpha(fade);
        OptRound(rx, ry, rx + rw, ry + rh, 10, 0);
        DrawSetColorRgb(255, 244, 214); DrawSetAlpha(0.9 * fade);
        OptRound(rx, ry, rx + rw, ry + rh, 10, 2);
        lr = 42; lg = 29; lb = 46;
    } else {
        DrawSetColorRgb(60, 74, 128); DrawSetAlpha((0.34 + 0.16 * isHot) * fade);
        OptRound(rx, ry, rx + rw, ry + rh, 10, 0);
        if (rk == "winsize" && OptWinMode() != 0) { lr = 150; lg = 148; lb = 172; }
    }
    var noShadow = 1 - isSel;
    OptText(rx + 22, ry + 8, OptLabel(rk), 21, "left", lr, lg, lb, fade, noShadow);

    if (rowKind == 2) {
        var valX = rx + 330;
        var vw = 480;
        var arrowA = 0.45;
        if (isSel == 1 || isHot == 1) { arrowA = 1; }
        OptText(valX + 16, ry + 8, "<", 21, "left", lr, lg, lb, arrowA * fade, noShadow);
        OptText(valX + vw - 16, ry + 8, ">", 21, "right", lr, lg, lb, arrowA * fade, noShadow);
        OptText(valX + vw / 2, ry + 8, OptValueText(rk), 21, "center", lr, lg, lb, fade, noShadow);
    } else if (rowKind == 1) {
        var trackX = rx + 330;
        var trackW = 400;
        var mid = ry + rh / 2;
        var frac = Clamp(GlobalGet(OptVolKey(rk)), 0, 1);
        if (isSel == 1) { DrawSetColorRgb(196, 128, 36); } else { DrawSetColorRgb(22, 26, 52); }
        DrawSetAlpha(0.95 * fade);
        OptRound(trackX, mid - 6, trackX + trackW, mid + 6, 6, 0);
        if (isSel == 1) { DrawSetColorRgb(42, 29, 46); } else { DrawSetColorRgb(244, 182, 62); }
        DrawSetAlpha(fade);
        OptRound(trackX, mid - 6, trackX + 8 + (trackW - 8) * frac, mid + 6, 6, 0);
        if (isSel == 1) { DrawSetColorRgb(42, 29, 46); } else { DrawSetColorRgb(255, 244, 214); }
        DrawCircleEx((trackX + 4 + (trackW - 8) * frac) * self.optSx, mid * self.optSy, 9 * self.optS, 0);
        OptText(rx + rw - 22, ry + 8, StringOf(Round(frac * 100)) + "%", 21, "right", lr, lg, lb, fade, noShadow);
    } else if (rowKind == 3) {
        OptText(rx + rw - 24, ry + 8, ">", 22, "right", lr, lg, lb, fade, noShadow);
    } else if (rowKind == 5) {
        var actIdx = Real(StringCopy(rk, 4, 1));
        var act = self.optActions[actIdx];
        var dev = self.optScheme;
        for (var sl = 1; sl <= 2; sl = sl + 1) {
            var capX = rx + 316;
            if (sl == 2) { capX = rx + 556; }
            var capText = "";
            var capState = 0;
            if (OptFixed(actIdx) == 1) {
                capState = 3;
                if (sl == 1) { capText = "LEFT STICK"; } else { capText = "D-PAD"; }
            } else {
                capText = OptInText(act, dev, sl);
                if (OptIsBound(act, dev, sl) != 1) { capText = "-"; capState = 4; }
                if (isSel == 1 && self.optSlot == sl) { capState = 1; }
            }
            if (self.optCap == 1 && self.optCapAct == actIdx && self.optCapSlot == sl) {
                capState = 2;
                if (dev == 1) { capText = "PRESS A BUTTON"; } else { capText = "PRESS A KEY"; }
            }
            OptCap(capX, ry + 4, 220, 32, capText, dev, capState, fade);
        }
    }
    return 0;
}

function OptDrawHints(fade) {
    var px = self.optPx;
    var py = self.optPy;
    var pw = self.optPw;
    var ph = self.optPh;
    var rk = "";
    if (self.optSel >= 0 && self.optSel < PgListSize(self.optKeys)) { rk = self.optKeys[self.optSel]; }
    // Line one: a message (warm) while one is showing, the help for the selected row otherwise.
    if (self.optCap == 1) {
        if (self.optScheme == 1) { OptText(px + pw / 2, py + ph - 64, "PRESS THE NEW BUTTON.  START CANCELS.", 19, "center", 255, 214, 130, fade, 1); }
        else { OptText(px + pw / 2, py + ph - 64, "PRESS THE NEW KEY OR MOUSE BUTTON.  ESC CANCELS.", 19, "center", 255, 214, 130, fade, 1); }
    } else if (self.optMsgT > 0) {
        OptText(px + pw / 2, py + ph - 64, self.optMsg, 19, "center", 255, 200, 120, fade, 1);
    } else {
        OptText(px + pw / 2, py + ph - 64, OptHelp(rk), 18, "center", 206, 200, 224, fade, 1);
    }
    var hint = "UP / DOWN  choose     LEFT / RIGHT  change     ENTER  select     ESC  back";
    if (self.optPad == 1) { hint = "D-PAD  choose / change      A  select      B  back"; }
    if (self.optView == 0) {
        if (self.optPad == 1) { hint = "D-PAD  choose / change     A  select     B  back     LB / RB  page"; }
        else { hint = "UP / DOWN  choose    LEFT / RIGHT  change    ENTER  select    Q / E  page    ESC  back"; }
    }
    if (self.optView == 2) {
        if (self.optPad == 1) { hint = "A  rebind     Y  clear     LEFT / RIGHT  slot     B  back"; }
        else { hint = "ENTER  rebind     DEL  clear     LEFT / RIGHT  slot     ESC  back"; }
    }
    OptText(px + pw / 2, py + ph - 36, hint, 18, "center", 170, 176, 210, fade, 1);
    return 0;
}

// ------------------------------------------------------------------ the per-frame draw
function OptionsDraw() {
    if (self.optOpen != 1) { return 0; }
    OptLayout();
    var sx = self.optSx;
    var sy = self.optSy;
    var sw = WindowGetWidth();
    var sh = WindowGetHeight();
    var px = self.optPx;
    var py = self.optPy;
    var pw = self.optPw;
    var ph = self.optPh;
    var fade = Clamp(self.optT / 0.18, 0, 1);
    DrawSetFont("Georgia");
    DrawSetTextTracking(0);
    DrawSetColorRgb(6, 6, 18);
    if (self.optCtx == 1) { DrawSetAlpha(0.62 * fade); } else { DrawSetAlpha(0.3 * fade); }
    DrawRectangle(0, 0, sw, sh);

    // Panel: soft shadow, navy body, parchment inset, gold border.
    DrawSetColorRgb(10, 8, 24); DrawSetAlpha(0.4 * fade);
    OptRound(px + 6, py + 9, px + pw + 6, py + ph + 9, 22, 0);
    DrawSetColorRgb(24, 32, 66); DrawSetAlpha(0.97 * fade);
    OptRound(px, py, px + pw, py + ph, 20, 0);
    DrawSetColorRgb(232, 214, 168); DrawSetAlpha(0.07 * fade);
    OptRound(px + 16, py + 98, px + pw - 16, py + ph - 76, 14, 0);
    DrawSetColorRgb(255, 210, 90); DrawSetAlpha(0.95 * fade);
    OptRound(px, py, px + pw, py + ph, 20, 3);
    DrawSetColorRgb(255, 235, 170); DrawSetAlpha(0.22 * fade);
    OptRound(px + 8, py + 8, px + pw - 8, py + ph - 8, 14, 1);

    var title = "OPTIONS";
    if (self.optView == 1) { title = "CONTROLS"; }
    if (self.optView == 2) {
        if (self.optScheme == 1) { title = "REMAP: CONTROLLER"; } else { title = "REMAP: KEYBOARD AND MOUSE"; }
    }
    DrawSetTextTracking(3);
    OptText(px + pw / 2, py + 12, title, 30, "center", 255, 223, 145, fade, 1);
    DrawSetTextTracking(0);

    if (self.optView == 0) {
        var tabNames = ["GAMEPLAY", "VIDEO", "AUDIO"];
        for (var ti = 0; ti < 3; ti = ti + 1) {
            var tabX = self.optTabX0 + ti * (self.optTabW + 14);
            var tabY = py + 56;
            var tabOn = 0;
            if (ti == self.optPage) { tabOn = 1; }
            if (tabOn == 1) { DrawSetColorRgb(244, 182, 62); DrawSetAlpha(fade); }
            else if (ti == self.optHoverTab) { DrawSetColorRgb(86, 104, 168); DrawSetAlpha(0.9 * fade); }
            else { DrawSetColorRgb(52, 64, 112); DrawSetAlpha(0.7 * fade); }
            OptRound(tabX, tabY, tabX + self.optTabW, tabY + 34, 10, 0);
            if (self.optSel == -1 && tabOn == 1) {
                DrawSetColorRgb(255, 244, 214); DrawSetAlpha(fade);
                OptRound(tabX - 3, tabY - 3, tabX + self.optTabW + 3, tabY + 37, 12, 3);
            }
            if (tabOn == 1) { OptText(tabX + self.optTabW / 2, tabY + 5, tabNames[ti], 21, "center", 42, 29, 46, fade, 0); }
            else { OptText(tabX + self.optTabW / 2, tabY + 5, tabNames[ti], 21, "center", 246, 231, 193, fade, 1); }
        }
    } else if (self.optView == 1) {
        OptText(px + pw / 2, py + 62, "CHOOSE A DEVICE, THEN HOW SPRINT BEHAVES", 18, "center", 190, 196, 226, fade, 1);
    } else {
        OptText(px + pw / 2, py + 62, "TWO BINDINGS PER ACTION", 18, "center", 190, 196, 226, fade, 1);
    }

    var n = PgListSize(self.optKeys);
    for (var dv = 0; dv < self.optVis; dv = dv + 1) {
        var di = self.optScroll + dv;
        if (di < n) { OptDrawRow(di, dv, fade); }
    }
    if (self.optScroll > 0) {
        OptText(px + pw - 40, self.optRowY0 - 22, "^", 22, "center", 255, 223, 145, fade, 1);
    }
    if (self.optScroll + self.optVis < n) {
        OptText(px + pw - 40, self.optRowY0 + self.optVis * self.optPitch - 8, "v", 20, "center", 255, 223, 145, fade, 1);
    }

    // The Controls screen previews the control-hint bar the game shows when tips are on.
    if (self.optView == 1 && self.optHasInput == 1) {
        var prevY = self.optRowY0 + 4 * self.optPitch + 16;
        OptText(self.optRowX + 22, prevY, "HINT BAR PREVIEW", 18, "left", 200, 190, 160, fade, 1);
        var gx = self.optRowX + 230;
        var glyphActs = ["jump", "sprint", "use", "throw"];
        var glyphNames = ["JUMP", "SPRINT", "USE", "THROW"];
        for (var gi = 0; gi < 4; gi = gi + 1) {
            var gw = OptInGlyph(glyphActs[gi], gx, prevY - 6, 28);
            OptText(gx + gw + 6, prevY, glyphNames[gi], 18, "left", 236, 226, 196, fade, 1);
            gx = gx + gw + 6 + DrawTextWidth(glyphNames[gi], 18 * self.optS) / sx + 26;
        }
    }

    OptDrawHints(fade);
    DrawSetAlpha(1);
    DrawSetColorRgb(255, 255, 255);
    DrawSetTextAlign("left");
    DrawSetTextTracking(0);
    if (self.optCtx == 1) { DrawSetFont("Consolas"); }
    return 0;
}
"""


def main():
    write_text("Scripts/Options Kit.pgsl", OPTIONS_KIT)
    write_meta("Scripts/Options Kit.pgsl", "Script", "Options Kit")
    print("Options Kit written to", ASSETS)


if __name__ == "__main__":
    main()
