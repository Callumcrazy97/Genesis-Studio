# The Audio editor

The Audio editor edits a game's sounds. It has two modes, chosen by the resource it opens:

- **Sound** (`.audio.json`): one WAV or Ogg file and how the game plays it. Quick setup picks or
  imports the file, or **Make a sound…** builds one with Genesis's synthesiser (`SoundSynth`, the
  presets in `SoundSynth.Presets`, shared with automation's `audio.synth`); volume, repeat, trim,
  fades, bus, pitch and spatial falloff follow. **Use in game** makes a sound Object and shows the
  `PlaySound` call. Code: `Source/Genesis.Application.Editors.Suite/Assets/AudioEditorControl*.cs`.
- **Music** (`.music.json`): a piece of music made of notes, below.

## Music (MIDI)

A GarageBand-like studio for a game's music, added 29 October 2026 (plan item C4 in
[UnityParityPlan.md](UnityParityPlan.md)). New › Music makes a piece in `Assets/Music` from a
template (a piano arpeggio and a bar of drums, so Play sounds at once); opening it shows the Audio
editor's Music mode (`MusicEditorControl`, its piano roll `MusicPianoRoll`).

### The piece

`MusicDocument` (`Source/Genesis.Application.Core/Audio/Music/MusicDocument.cs`), saved as
`.music.json` (resource kind `Music`, type `Music` in the resource catalog, its own root folder):

| Part | What it holds |
|---|---|
| Timing | `ticksPerBeat` (480 by default; an imported file keeps its own), `tempo` (quarter notes a minute, 20 to 400), `tempoChanges` (an imported file's later tempos), `beatsPerBar` and `beatUnit` (the time signature) |
| Tracks | `name`, `instrument` (a synthesiser preset by name), `volume` (0 to 1), `mute`, `solo`, and `attack`, `release`, `filter` (absent = the instrument's own); `program` and `channel` remember an imported MIDI file's General MIDI program and channel for export |
| Notes | `pitch` (MIDI key, 60 = C4), `start` and `length` (ticks), `velocity` (1 to 127) |
| Loop | `loopStart`, `loopEnd` (ticks; an end of 0 is no range) |
| Output | `audio`: the name of the Audio resource Render to Audio writes |

### Editing

- **Tracks** (left): each row has the name, instrument, **M** (mute), **S** (solo) and a volume
  slider; click a row to edit its notes. Below: name, instrument, attack, release and filter for the
  selected track, "Instrument's own sound" to clear them, "Hear the instrument".
- **Piano roll**: a keyboard down the left (click a key to hear it), a ruler of bars along the top,
  the selected track's notes on the key grid (the other tracks' faint behind), a velocity lane under
  it. Click an empty place for a note (drag to set its length; it starts as long as the last note
  placed); drag a note to move it, its right edge to resize it, **Alt**+drag to change its velocity;
  **Ctrl**+click adds to the selection, **Ctrl**/**Shift**+drag on empty space selects with a box;
  right-click deletes. Drag in the velocity lane to set velocities.
- **Snap** (Bar, Beat, 1/8, 1/16, Off) applies to placing, moving and resizing. **Zoom** with − and +
  or Ctrl+wheel; the wheel scrolls keys, Shift+wheel scrolls time.
- **Keys** while the roll has the focus: Delete, Ctrl+C, Ctrl+X, Ctrl+V (at the play-head), Ctrl+A,
  arrows (move a key or a snap step; Shift+Up/Down an octave), Space (play and stop), Escape.
- **Undo and redo**: every change (a drag is one step) goes through the editor's undo, Ctrl+Z and
  Ctrl+Y, up to 100 steps.

### Instruments

A track's instrument is a synthesiser preset by name, so any preset plays notes and a project can add
its own later. The built-in musical presets (`MusicInstruments.BuiltIn`, category Instruments in
Make a sound) are Piano, Pluck, Bass, Lead, Pad, Organ and Drum Kit. A note plays the preset moved
from its first layer's key to the note's key (its filter follows half way), held for the note's
length and released (`MusicInstrumentKind.Sustained`). Other presets play through once at the note's
key (`OneShot`), except repeating chords such as Drone or Wind, which are held. The Drum Kit maps
General MIDI drum keys to the Drums presets (Kick Drum 35-36, Snare Drum 37/38/40, Clap 39, Hi-Hat
42/44, Open Hi-Hat 46 and the rides, Crash Cymbal 49/52/55/57, Tom 41-50 tuned by key). A track's
attack, release and filter replace the preset's on every layer.

### Play and render

`MusicRenderer` renders a note at a time through `SoundSynth.Render` (each note normalised, then
scaled by velocity, track volume and a fixed headroom), side by side in batches of 64 and summed in
order, so the same piece always gives the same samples. A mix louder than 0.95 is turned down as a
whole. Solo wins over mute: when any track is soloed only soloed tracks are heard. Rendering runs
on the editor's thread when Play or Render is pressed; the tests hold a minute of four busy tracks
(960 notes) to under 20 seconds.

- **Play** renders from the play-head to the end (or the loop range when **Loop** is on, exactly
  that long, repeating) and plays it on the game's own mixer (`XAudioSystem`, music bus) while a
  clock moves the play-head. Edits are heard the next time Play is pressed. Without an audio device
  the render still runs and the status says it cannot be heard.
- **Render to Audio** renders the whole piece (to the end of the last release) and writes it as the
  WAV of the piece's Audio resource: the first time it makes `<piece> Audio` in `Assets/Audio`
  (music bus, repeating, volume 0.8) and remembers its name in the piece; later renders replace that
  WAV and keep the resource's own settings. The piece is saved. A game plays it with
  `PlaySound("<piece> Audio", volume, pitch, loop)` like any sound. Up to 10 minutes.

### MIDI files

`MidiFile` (`Source/Genesis.Application.Core/Audio/Music/MidiFile.cs`) is Genesis's own reader and
writer for Standard MIDI Files, no library:

- **Import MIDI…** reads formats 0 and 1 (format 2 is refused with a reason): notes with velocities
  (note-ons of velocity 0 are note-offs; running status; a note-off closes the earliest open note of
  its key), tempo and tempo changes, the first time signature, track and instrument names, the first
  program change (as the nearest built-in instrument) and channel volume (CC 7) of each channel.
  Each file track is split by channel, so a format 0 file gives a track per channel; channel 10 is
  the Drum Kit. SMPTE-timed files and files under 96 ticks a beat are brought to 480 ticks. Import
  replaces the piece in one undo step.
- **Export MIDI…** writes format 1: a tempo track (time signature, tempo and its changes), then a
  track per music track with its name, volume, program (the imported one, or the instrument's
  General MIDI program) and notes, on its imported channel or the next free one (channel 10 for
  drums). At one tick a note-off is written before a note-on, so a key played again is heard.

### Not in this first version

- A SoundFont player. MeltySynth (C#, MIT: <https://github.com/sinshu/meltysynth/blob/main/LICENSE.txt>)
  and the MuseScore General SoundFont (MIT, S. Christian Collins:
  <https://people.debian.org/~tg/sf/MuseScore_General_Full.copyright>) may both be shipped with their
  notices, but the SoundFont is 208 MB as SF2 (MeltySynth's documentation names SF2 only, not the
  smaller compressed SF3), so neither was added; the studio works with the built-in instruments alone.
- Pan, effects per track, pitch bend and controllers other than volume; tempo changes made in the
  editor (imported ones are kept and played); time-signature changes; recording from a MIDI keyboard.
- Edits heard live while playing (Play renders what the piece is when pressed).
- An automation command for music (`audio.music`); the editor and `MusicRenderer.RenderToAudio` are
  the ways in.

### Tests

`Tests/Genesis.Application.Headless/Suites/EditorGate.MusicStudio.cs`, run with the Audio editor's
cases (`Build.bat --test Audio`) or alone (`--test music`): the MIDI round trip and a format 0 file
as another program writes it (and a `.mid` from `C:\Windows\Media` when there is one, read only),
the instruments' pitch and loudness, solo and the drum kit, render speed and repeatability, a new
piece with two tracks through Play, Loop, Render to Audio, save and reopen and a game's `PlaySound`,
and undo and redo of every note edit and of Import MIDI.
