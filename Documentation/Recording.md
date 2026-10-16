# Gameplay recording and replays

Genesis can record a game to a video, keep the last few seconds in a replay buffer, save those as a
video, or play them back inside the game (a killcam). Nothing runs until a game, or Debug mode,
asks for it: no thread, no memory and no files are used otherwise.

Videos are H.264 MP4, made with Windows Media Foundation (part of Windows, so there is nothing to
install). Windows "N" editions without the media pack have no H.264 encoder; there the recorded
frames are kept as pictures in `Recordings/<name> (frames)` instead.

## Where the files go

`<project>/Recordings/`. An exported game installed where it cannot write uses
`%LocalAppData%\Genesis Games\<game folder>\Recordings\` instead. Online backup skips `Recordings/`.

## In Debug mode

Press **F9** to start recording and **F9** again to stop. The video is `Recordings/debug_<date>_<time>.mp4`.

## From PGSL

| Command | What it does |
|---|---|
| `RecordStart(name, format)` | Start recording. `format` is `"mp4"` (the default) or `"png"` (every frame as a picture in `Recordings/name`). Up to 30 frames a second. |
| `RecordStop()` | Stop, and return where the video goes (it is finished in the background a moment later). |
| `RecordIsRecording()` | True while recording. |
| `ReplayBufferEnable(seconds, fps, scale)` | Keep the last `seconds` (1–60) in memory at `fps` (5–60) and `scale` of the screen (0.1–1). |
| `ReplayBufferDisable()` | Stop keeping a buffer and free it. |
| `ReplayBufferSeconds()` | How much the buffer holds now. |
| `ReplayBufferSave(name, seconds)` | Save the last `seconds` as `Recordings/name.mp4` (in the background). |
| `ReplayPlay(secondsBack, speed)` | Play back the last `secondsBack` at `speed` (0.5 is slow motion). The buffer pauses while it plays. |
| `ReplayDraw(x, y, width, height)` | Draw the replay's current frame, in Draw GUI: full screen, or small for picture-in-picture. |
| `ReplayIsPlaying()`, `ReplayProgress()`, `ReplayStop()` | Follow or end a replay. |

Keys are the game's own choice. For example, "press R to record" is game code:

```
// Step
if (KeyPressed("R")) {
    if (RecordIsRecording()) { RecordStop(); } else { RecordStart("my clip", "mp4"); }
}
```

A killcam:

```
// Create
ReplayBufferEnable(8, 30, 0.5);

// when the player is defeated
ReplayPlay(3, 0.5);

// Draw GUI
if (ReplayIsPlaying()) { ReplayDraw(0, 0, RoomGetWidth(), RoomGetHeight()); }
```

## Cost

Each captured frame is read back from the graphics card (at most at the rate asked for), then
scaled and compressed on a worker thread. If the worker falls behind, frames are dropped rather
than slowing the game. Sound is not recorded yet. A faster readback that does not wait for the
graphics card is the next step, one renderer at a time.
