# Plan: online backup, local AI assistant, gameplay recording (16 Oct 2026)

Plan only. Nothing here is built yet. All three follow the engine rules: off by default,
built into Genesis's own source (no bundled mini-apps), and a game or project that never turns a
feature on pays nothing for it (no threads, no memory, no files).

---

## 1. Online backup (GitHub Manager merged into Genesis)

**Reuse.** GitHub Manager's `src/GitHubAutoSync/GitHub` and `Sync` folders (about 2,000 lines) do
the hard parts already. They talk to GitHub's REST API directly, with no git install needed:
- device-flow sign-in;
- blob hashing that skips files GitHub already has;
- metered uploads, ignore rules and per-device sync state.

They move into `Genesis.Application.Core/Backup/` (namespace `Genesis.Application.Core.Backup`).
Its own WinForms UI, CLI and settings stay behind.

**Sign-in.** Device flow ("enter this code on github.com"). The token is stored per Windows user with
DPAPI (as GitHub Manager does now), never in the project folder, so a backed-up or shared project
can never leak it.

**What gets backed up (Project Settings › Online Backup).**
- A folder tree with checkboxes, plus toggles by resource type. A preview line reads
  "1,240 files, 182 MB will be backed up; 37 skipped (2.1 GB)" and lists the skipped files with why.
- Always skipped: any file over 10 MB, and generated data:
  - `.genesis/Cache`
  - exports and builds
  - `TestResults`
  - recordings
- Skipped by default, and can be ticked back on:
  - models (`.fbx .obj .glb .gltf .blend` and model data folders);
  - raw audio stems over a size you set;
  - video.
- The rules are saved in the project as a plain `.genesisbackup` file (gitignore syntax), so they
  travel with the project and can be read.

**Upload in 10 MB rounds.**
1. Create the repository empty (private by default).
2. The first commit holds a small `README.md` plus `.genesisbackup`. GitHub's Git Data API refuses
   to make blobs in a repository with no commits, so this commit goes through the Contents API.
3. Then rounds: gather changed files up to **10 MB total**. Upload them as blobs, then one tree,
   one commit and a ref update. Then **wait**: 30 s by default, and longer when GitHub returns
   `Retry-After` or a secondary rate limit. Then the next round.
4. Each round is a finished commit, so a backup stopped halfway (app closed, PC off) resumes from
   the last finished round. The state file is `.genesis/Backup/state.json`: path → size, time and blob SHA.
5. Later backups send only changed files, in the same rounds.

**Studio.**
- Project Settings page, plus "Back up now" in the File menu.
- A status bar chip shows the round, the MB sent so far and the next wait.
- Optional: back up when the project closes.
- Restore: "Download a backup" → choose a repository → choose a folder.

**Tests (headless, no network).** A fake GitHub HTTP handler checks:
- the empty repository is created, then the first commit;
- every round stays at 10 MB or less, and the waits happen;
- a stopped backup resumes;
- files over 10 MB and models are skipped;
- `.genesisbackup` round-trips.

One manual run against a real private repository.

**Order:**
1. Port Core and its tests.
2. Settings page and preview.
3. Rounds and resume.
4. Restore.

**Licences.** GitHub Manager is Cal's own code. If it pulls in any NuGet package, that package
gets a line in `Documentation/ThirdPartyNotices.txt`.

---

## 2. Local AI assistant (GGUF; pieces of Nova ported)

**Target model: Gemma 2B (Q4, about 1.6 GB).** Cal's finding is that bigger models ignore
instructions more. Gemma 2B has an 8K context and no native tool calls. So:
- every answer is **grammar-constrained JSON** (llama.cpp GBNF), and the model can only produce
  shapes Genesis accepts;
- prompts stay small: a few retrieved reference entries, never whole documents.

**Runtime (ported from Nova, not linked to it).** Nova runs `llama-server` as a hidden child
process on an authenticated loopback port. Its health and readiness checks decide when the model
is up. Genesis ports that process manager into `Genesis.Application.Core/Assistant/`.
- `llama.cpp` (MIT) is an optional download from Settings, as is the GGUF file you choose.
  Neither goes in the installer. Both get ThirdPartyNotices entries.
- A crash or a hang in the model never takes Studio down.

**What it knows (retrieval, no embedding model).** A compact index is generated at build time:
- every PGSL command's signature and one-line purpose, from the VM's command table (1,275 commands);
- the resource schemas;
- Mushroom Meadow and Tumble Isles examples.

Plain keyword search (BM25) picks the top entries for each request.

**What it can do (tools, all checked by Genesis before anything is written).**
- Create a resource: sprite, Object, room, sound or note. The JSON must match the schema, and the
  resource validators must pass; then it is written through the normal resource service, so undo works.
- Write or fix PGSL. The PGSL checker runs on the result, and its errors go back to the model
  (at most 3 tries). Then the change is shown as a diff to accept.
- Test: run the game headless with a short input script (`InputReplay` `.ginput` already exists).
  `ScriptScreenshots` captures frames, and the assistant reports log errors and what changed.

**"Seeing" for models without vision (port of Nova's Image to Text, still in progress there).** Nova
turns an image into text: OCR words with positions, plus basic pixel measurements. Genesis adds
three analysers that are more useful for games:
1. **Scene description from the engine itself.** The objects in view, with sprite, position,
   layer and state, plus the camera. This is far more reliable than reading pixels, and free:
   Genesis knows what it drew.
2. **Sprite analysis:**
   - size;
   - the palette and its colour count;
   - the alpha bounds;
   - whether it is a frame grid and how big each frame is;
   - empty or duplicate frames.
3. **Screenshot analysis:**
   - brightness and contrast;
   - letterbox bars;
   - a colour histogram;
   - HUD text through OCR (Nova's OCR; check its licence before shipping).

**Studio.** An Assistant dock (chat, with the diff or preview of each proposed change and an Apply
button). It is off until a model is chosen in Settings.

**Measuring it.** A Genesis eval set (like Nova's Judge) of 40 tasks:
- make a coin sprite;
- an Object that moves with the arrow keys;
- fix this PGSL error;
- a room with three platforms.

Each task is scored by validators passing and the headless run succeeding. Run it on Gemma 2B
first, then on any other model, to compare honestly.

**Order:**
1. Process manager and Settings.
2. PGSL index and constrained output.
3. Create and fix tools with the checker loop.
4. Seeing (scene description first, then sprites, then screenshots).
5. Eval set.
6. Test tool.

---

## 3. Gameplay recording, replay buffer and killcams

**Off by default.** Nothing is created until a game calls the API or you press Record in Debug Mode.

**Frame source.**
- Phase 1 uses the existing `IRenderController.TryReadFramePixels`, which works on every backend,
  at a capped rate (30 fps) and a capped size. The downscale and encoding happen on a worker thread.
- Phase 2 adds an asynchronous readback ring: 2–3 staging copies, read a few frames late, so the
  GPU never stalls. Order: DX11, then DX12 and OpenGL. Vulkan comes last (it is low priority).

**Audio.** Taken from Genesis's own mixer output, so only the game's sound is recorded (no Windows
loopback, no other apps).

**Two modes.**
1. **Record to file:**
   - MP4 (H.264 + AAC) through Windows Media Foundation, which is part of Windows and needs no
     licence or download;
   - or a PNG sequence;
   - on Windows "N" editions without the codec, PNG is the fallback, with a clear message.
2. **Replay buffer (killcams).**
   - The game sets how many seconds to keep (capped at 60), the fps and the scale.
   - Frames are kept in memory as JPEGs encoded on a worker thread: about 15 MB for 10 s at
     640×360, 30 fps. Audio is kept as a matching ring.
   - The game can save the buffer to MP4, or **play it back in the game**: PGSL draws the current
     replay frame anywhere (full screen or picture-in-picture), at any speed, with seek. That is a
     killcam, built by the game in PGSL.

**PGSL API.** The names are drafts and follow PGSL's naming when built.
- `record_start(file)`, `record_stop()`, `record_is_recording()`
- `replay_buffer_enable(seconds, fps, scale)`, `replay_buffer_disable()`
- `replay_buffer_save(file, seconds)`
- `replay_play(seconds_back, speed)`, `replay_stop()`, `replay_is_playing()`, `replay_seek(t)`
- `replay_draw(x, y, w, h)`
- Keys are the game's own choice through its input mapping ("Press F2 to record" is game code).
  Nothing is hardcoded.

**Debug Mode.** A Record button and a "Save the last 30 s" button in the debug overlay, both
reaching the same core.

**Files.**
- Running from Studio, recordings go to `<project>/Recordings`. This folder is skipped by the backup.
- An exported game writes to `%LocalAppData%/<Game>/Recordings`, because the install folder can be read-only.

**Project Settings › Recording:**
- allow recording in exported builds (off by default);
- the maximum replay seconds;
- quality.

**Tests:**
- record 2 s of a test room, then check the MP4's duration and frame count;
- the replay buffer saves the expected number of frames;
- `replay_draw` shows a frame from N seconds ago (pixel check);
- the PGSL API compiles;
- the DX11 smoke, then the other renderer smokes in a Full Build.

**Order:**
1. Core recorder with synchronous readback, to a PNG sequence.
2. MP4 through Media Foundation.
3. Replay buffer and the PGSL API.
4. Debug Mode buttons.
5. Asynchronous readback per backend.

**Reuse from Capturing.**
- Take its recorder's frame timing and audio sync logic, and its recording overlay.
- Don't take its screen capture: Genesis reads its own frames.
- Don't take its encoder: Capturing drives **ffmpeg**, which would mean shipping a large binary with
  LGPL/GPL terms. Genesis uses Windows Media Foundation instead.
