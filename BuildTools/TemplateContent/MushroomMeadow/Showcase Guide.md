# Mushroom Meadow: game manual and authoring guide

Mushroom Meadow is a complete, cozy 2D adventure made entirely of ordinary Genesis Studio resources:
rooms, objects with PGSL events, images and tilesets, sounds, particles, shaders and post effects,
user interfaces, scripts, paths, physics materials and this note. Nothing is hidden in the engine;
every part of it can be opened, changed and run with F5.

Part 1 is the player's manual. Part 2 explains how it is built and how to extend it.

---------------------------------------------------------------------------------------------------

# Part 1: the manual

## The story

The village of Hearthollow lives beneath the Great Lantern Tree, whose five Glowspores keep the long
night away. One autumn dusk Duskmantle, the vast Queen of the Dusk Moths, tore the Glowspores from the
Tree and scattered them across the valley, and twilight crept over every hill, cave and shore.
Elder Morel gave his old lantern to Pip, a small wanderer in a red mushroom cap, and sent him to
bring the Glowspores home. Each level ends with one Glowspore recovered. When all five are back,
the Tree lights up again.

## Starting a game

1. **Intro**: the game opens with a short painted cinematic. Press any key, controller button or
   click to skip it.
2. **Main Menu**: choose with the arrows, W/S, the D-pad or the left stick, or point with the mouse.
   Enter, Space or A selects, and Esc, Backspace or B goes back.
   - **New Game**: pick one of three save slots (if the slot already holds a journey you are asked
     before it is overwritten), then choose a **difficulty**: Story, Normal or Challenge (see below).
   - **Continue**: pick a saved slot. The most recently played slot is selected. Each slot card
     shows its completion percentage, Glowspores (also as five lights), coins, lives, where Pip stands
     on the map, the difficulty, and "THE TREE IS LIT" once the story has been finished.
   - **How to Play**: controls (keyboard and mouse beside controller, drawn from your own bindings), every
     power-up and held item, and tips. The pause menu and the map menu have it too.
   - **Collection**: one saved adventure at a time (Enter or the NEXT button picks the slot): completion,
     the levels (cleared, Sun Medals, best time), totals (Glowspores, medals, coins found, creatures
     defeated, deaths), the wildlife met by kind, the items found, and the 18 achievements. The pause menu
     and the map menu show the live adventure.
   - **Delete** a slot with its Delete button, the Delete key or controller Y, then confirm.
   - **Options** (also in the pause menu): Gameplay (Enable Tips, Gore, Screen Shake, Difficulty, Controls with
     Sprint hold/toggle and full key and button remapping), Video (window mode, VSync, frame cap, CRT
     scanlines) and Audio (master, music, effects, ambience). They apply at once and are saved when you leave.
   - **Credits** rolls the credits. **Quit** closes the game. The version (1.0.0) is shown at the bottom
     right of the Main Menu and in the credits.
3. **The valley map** (Overworld): walk Pip's marker along the open paths between the six places and
   press Enter, Space or A to go in. A cleared place shows a lit Glowspore, and the next path opens
   when the previous place is cleared. The game saves every time you come back to the map. The banner
   at the top shows the place's best Sun Medals and best time, and the completion percentage sits under
   the purse. Any cleared place can be entered again: the best medal count and the best time are kept.
   Esc, P or Start opens the map menu (Resume, Collection, How to Play, Save and Quit).

## Controls in play

| Action | Keyboard / mouse (defaults) | Controller (defaults) |
|---|---|---|
| Move | A/D or Left/Right | D-pad or left stick |
| Jump (hold for height) | Space or W | A |
| Sprint | Shift (hold or toggle in Options) | Left stick click |
| Use the held item / throw a spore | J or left mouse button | X |
| Throw the held item | K or right mouse button | B |
| Talk / interact | E or X | Y |
| Pause menu (Resume, How to Play, Collection, Options, Save and Quit) | P or Esc | Start |

Every action except Pause can be remapped under Options > Gameplay > Controls (two bindings each). The hint bar along the
bottom shows the current device's buttons and appears only with Enable Tips on.

Underwater Pip truly swims: he hovers in the water, W/A/S/D or the arrows swim in any direction, and jump gives a stroke burst the way you are heading. There is no walking, running or jumping underwater. With the Glide Seed, hold jump while falling to glide.

## Difficulty

Chosen when a New Game starts and changeable at any time under Options > Gameplay (the pause menu, or the Main
Menu for the next new game). Each saved adventure keeps its own. Movement, jumps and level layouts are the same
at every difficulty, so every level is completable at every difficulty; a hit always costs one heart.

| | Story | Normal | Challenge |
|---|---|---|---|
| Hearts at the start | 5 | 3 | 2 |
| Lives (and what Game Over refills) | 7 | 5 | 3 |
| Level timer | x 2 | x 1 | x 0.7 |
| The eight ordinary foes' speed | x 0.8 | x 1 | x 1.2 |
| Invulnerability after a hit | 2.2 s | 1.5 s | 1.0 s |
| A Heart Berry gives | 2 hearts | 1 | 1 |
| Checkpoint flags | heal every heart | - | - |

Heart containers from Chanterelle add to the base, up to 6 hearts in all. Switching difficulty moves the
heart total with the base (a purchased container stays) and keeps the timer's proportion.

## Health, lives and power-ups

- On Normal Pip starts with 3 hearts and 5 lives (see Difficulty). A hit costs a heart (a Bark Shield breaks
  first instead) and the current power-up, followed by a moment of invulnerability. With no hearts left Pip
  loses a life and starts again at the last checkpoint. Falling into a pit costs a life at once. With no
  lives left the game is over and Pip returns to the map with a fresh set of lives.
- Coins are the valley's currency: spend them at Chanterelle's stall in Hearthollow.
- Power-ups sprout from glowing **seed pods** (bump them from below) or lie in the levels:
  - **Glide Seed**: hold jump while falling to drift down slowly.
  - **Spore Puff**: the action key throws bouncing spores (two at a time).
  - **Bark Shield**: a bark ring that takes one hit for you.
  - **Starlit Firefly**: ten seconds of invincibility. Enemies you touch are defeated.
  - **Heart Berry**: one more heart, up to the most you can hold. Chanterelle sells extra heart
    containers (up to 6).
- **Held items** (all optional, found in the levels or sold by Chanterelle; Pip holds one at a time):
  - **Sword**: Use swings an arc in front of Pip (hold Up for overhead); **Shield**: hold Use to block
    from the front and bash by walking into a foe; **Bow**: hold Use to draw, aim with the move keys
    (or the mouse), release to fire arrows; **Propeller Flower**: Use in the air opens it as a
    parachute, hold Jump while it spins to rise for a moment. The Throw action throws the held item.
  - Stomping foes is the kind way to win; the bladed, gory ways have consequences the valley notices.
- **Sun Medals** (Big Sun Coins): three secrets in every level. Your best count per level is kept.

## The valley

| # | Place | What waits there |
|---|---|---|
| 0 | **Hearthollow** | The village hub: no timer and no danger. Talk to Elder Morel (the story so far), Chanterelle (shop: Heart Berry 30, Extra Life 50, Glide Seed 20, Spore Puff 25, Bark Shield 25 coins), Bram the snail postman (tips), Puffball (cute lines and a secret hint) and Old Wick the lamplighter. The Great Lantern Tree behind the houses gains a light for every Glowspore returned. The exit sign on the right leads back to the map. |
| 1 | **Mushroom Meadow** | Grassland: Acorn Walkers, Buzz Bees, bounce mushrooms, seed pods, a midpoint flag. The Glowspore waits at the Summit Shrine. |
| 2 | **Rootdeep Caverns** | Dark caves lit by Pip's lantern, glowing crystals and lanterns: Flit Bats, Glow Grubs, falling stalactites, crumbling blocks. Boss: **Grumblecrag**, a rock-shell beetle that charges, rolls and shakes pebbles from the ceiling. It can only be stomped while flipped after slamming into a wall (3 hits). |
| 3 | **Tidepool Grotto** | An undersea level you swim through: currents, Bloatfins (unstompable while puffed up), Driftjellies (never stompable: use spores), sunken ruins and treasure. |
| 4 | **Windswept Canopy** | Treetops at golden hour: wind gusts (watch the gauge), moving leaf platforms, Dusk Moths and Burr Hoppers. Long glides make the Glide Seed shine here. |
| 5 | **Duskmantle Hollow** | A twilight gauntlet and the final boss, **Duskmantle**: three phases of swoops, Gloom Orb volleys and summoned Dusk Moths. She can be stomped while dazed after a swoop, and spores hurt her at any time (6 hits; stomps alone are enough). If Pip loses a life the fight starts over. Defeating her returns the last Glowspore and plays the ending. |

## Progress, collection and achievements

Every adventure keeps statistics, shown on the Collection page: levels cleared, Sun Medals (15), Glowspores
(5), the best time of each level, coins found, creatures defeated by kind, items found (five power-ups and four
held items), deaths, time played and the valley's opinion of you. The **completion percentage** is 30% levels
cleared, 40% Sun Medals, 15% items found and 15% kinds of creature defeated. It is shown on each slot card and
under the purse on the map.

Eighteen **achievements** (a gold banner slides in when one is earned) belong to the player, not to a slot, so
they carry across all three adventures: First Stomp, First Slice, Sharp Shot, Coin Collector (100 coins),
First Light, Golden Hands (all three medals in a level), Sun Seeker (all fifteen), Untouched (a level without a
hit), Quick Feet (a level with half the time left), Beetle Buster, Five Lights, Dusk Breaker, Beloved and Dreaded
(finish the story with that reputation), Well Equipped (every item found), Friendly Face (talk to everyone),
Regular (buy something) and Challenge Accepted (finish on Challenge).

---------------------------------------------------------------------------------------------------

# Part 2: how it is made

## The flow of rooms

```
Intro -> Main Menu -> Overworld <-> Hearthollow
                          |  <->  Mushroom Meadow, Rootdeep Caverns, Tidepool Grotto, Windswept Canopy
                          '-----> Duskmantle Hollow -> Credits -> Main Menu
```

The project's start room is **Intro**. Rooms change with `RoomGoto("<room name>")`, after
`RoomChangeFade(seconds)` sets how gently the cover lifts. A room is found by its resource name, so
**every resource name must be unique across all kinds**: an Image, a Sound or an Object may not
share a name with each other or with a room. The Player refuses to start while two resources share
a name, and lists them. This is why sprite images whose object has the same name are called
"<Name> Sprite" (Glowspore Sprite, Flit Bat Sprite...), the checkpoint sound is "Checkpoint Chime",
the finish sound is "Finish Fanfare", and the Main Menu room's controller is the object "Menu Controller".

**Campaign and standalone.** The Main Menu sets the global `campaign` to 1 (through the New Game
and Game Load scripts). Any level started directly with F5 runs standalone (campaign 0) with the
phase-1 behaviour: a title card, and a restart on winning or game over. So every level can still be
tested on its own.

## Global game state

Globals (`GlobalSet` / `GlobalGet`) carry the campaign between rooms:

| Global | Meaning |
|---|---|
| `campaign` | 1 when started from the Main Menu |
| `slot` | the save slot in use (1-3) |
| `lives`, `coins`, `hearts`, `maxHearts`, `power` | Pip's state (power: 0 none, 1 Glide Seed, 2 Spore Puff, 3 Bark Shield) |
| `clear1`..`clear5`, `medals1`..`medals5` | levels cleared and the best Sun Medal count per level |
| `glowspores`, `mapNode`, `returnFromLevel` | Glowspores returned, where the map marker stands, and the level just cleared (the map plays its unlock reveal) |
| `mapRevealed`, `hubTalk1`..`hubTalk5` | which map unlock has been shown, and each villager's repeat-visit dialogue |
| `introSeen`, `gameComplete` | the intro has played, and the story is finished |
| `musicVol`, `sfxVol`, `crt`, `shake` | the player's options |
| `difficulty` | 0 Story, 1 Normal, 2 Challenge (saved with the slot; the Options slot remembers the one New Game offers) |
| `playTime`, `deaths`, `statCoins`, `itemsSeen`, `shopBuys` | the statistics of the adventure (seconds, lives lost, coins found, a bit mask of the nine items found, purchases) |
| `bestTime1`..`bestTime5`, `foe0`..`foe9` | each level's best time in seconds, and the creatures defeated by kind (see `product_consts.py`) |
| `achMask`, `achQueue`, `achToastT` | the achievements unlocked (a profile in the save slot "Profile", not in a slot) and the banner queue |

## Saving and options (Scripts)

| Script | Use |
|---|---|
| **Game Save** | `ScriptExecute("Game Save")` writes every campaign global to save slot "Slot <slot>". `ScriptExecute("Game Save", 1)` also plays the Save Chime. The Overworld saves on every return. |
| **Game Load** | `ScriptExecute("Game Load", slot)` restores a slot and starts campaign mode. It returns 0 for an empty slot. |
| **New Game** | `ScriptExecute("New Game", slot)` sets the starting values (5 lives, 3 hearts, nothing cleared, Pip at Hearthollow) and writes the slot at once. |
| **Apply Options** | `ScriptExecute("Apply Options")` loads the options once per run from the "Options" save slot (defaults: music 0.8, effects 0.9, CRT off, shake on) and applies them: music volume to the "music" bus, effects volume to the "sfx" and "ambient" buses, CRT to the SNES Look post effect's Scanlines. `ScriptExecute("Apply Options", 1)` also saves them. Every room's controller calls it in Create. Gameplay reads `GlobalGet("shake")` before shaking the view. |

A slot is one line of text, `mm2,<value>,<value>,...`, in the order listed at the top of Game Save
(Game Load reads the same list). Save slots live under the player's profile, per project. To add a
global to the save, add its name to the list in both scripts (in
`BuildTools/TemplateContent/MushroomMeadow/frontend_gen.py`, `SAVE_FIELDS`) and give it a default
in New Game.

## Rooms

All rooms are 2D. Their viewport shows 640x360 world pixels in a 1280x720 window, and GUI drawing
uses a 1280x720 design space scaled to the window.

Window shapes: the level rooms and Hearthollow have `scaleViewportsWithWindow` off and their Level Rules object lays the
view out in the window itself (`RulesFitView`, every frame the window changes). A 16:9 or wider window shows the world to its
full height at one uniform scale and a wider slice of it (840 px at 21:9, never more than 960 px); a narrower window keeps the
640x360 view with bars above and below. The HUD is drawn over the whole window, so at every shape it sits on the same frame
as the world. Anything drawn in GUI space that belongs to the world (blood stains, the held item, shop prompts) converts room
points with `GoreGuiX/Y`, `ItemWX/Y` or `HubGuiX/Y`, which read the view's own port and width. The Overworld map is exactly one
screen, so it keeps the room's own letterbox; its GUI marks use `WindowGetGuiX/Y/Width`.

| Room | Made by | Contents |
|---|---|---|
| Intro | `frontend_gen.py` | Intro Director only. The cinematic is drawn in Draw GUI. |
| Main Menu | `frontend_gen.py` | Menu Backdrop (background), Menu Controller. Post effect SNES Look. |
| Credits | `frontend_gen.py` | Credits Backdrop (background), Credits Director. Post effect SNES Look. |
| Overworld | `hub_gen.py` | Overworld Map, Map Clouds, six Map Nodes, the Map Marker (also the map's controller). |
| Hearthollow | `hub_gen.py` | Village Tiles and backdrops, Lantern Tree Glow, Chimney Smoke, Village Host, the five NPCs, Shop Stall, Exit Sign, Explorer, Level Rules. |
| Mushroom Meadow | `level_gen.py` | Level 1 (phase 1), now with Level Rules and the Glowspore. |
| Rootdeep Caverns, Windswept Canopy | `levels_a_gen.py` | Levels 2 and 4 (Cavern Damp and Golden Hour post effects). |
| Tidepool Grotto, Duskmantle Hollow | `levels_b_gen.py` | Levels 3 and 5 (Underwater and Gloom post effects). |

Level rooms are built from readable ASCII maps in their generator: edit a section, run the generator
and the room is rewritten deterministically. Painting a room by hand in the Room Editor works just as
well; the generators are only a convenient source. Tile layers in a level: **World** (solid
terrain), **Blocks** (breakable stone 2, seed pods 3, opened pods 4, crumble blocks; changed live with
`TileSetAt` / `TileRemoveAt`), **Decor** (no collision), **Hazards** (thorns 32, read with
`TileIndexAt`) and, in water levels, **Water**. Every world tileset (Meadow, Cavern, Grotto, Canopy,
Hollow and Village Tiles) uses the same 40-tile layout and solid list, so a map can be re-skinned by
swapping the tileset.

## Objects

**Front end** (`frontend_gen.py`)
- **Intro Director**: five Intro Panels pan, zoom and cross-fade while the story is typed into a
  caption box. Glowing motes drift over them, and the five Glowspores tumble out of Duskmantle's grasp
  on the third panel. Intro Theme plays and the Title Logo rises at the end. Any key (its Key
  Pressed event), button or click skips. It sets `introSeen` and goes to the Main Menu. The timing,
  captions, camera moves and motes are tables at the top of its Create event.
- **Menu Controller**: the Main Menu. It drives the **Main Menu UI** resource (show and hide page
  groups, `UiSetText`, `UiSetSelected`, `UiClicked`, `UiGetHovered`, slider and toggle values) with
  one selection shared by keys, controller and mouse. It also draws the bobbing logo and its
  sparkles, and spawns the Menu Fireflies and Menu Sparkles particles.
- **Credits Director**: when reached from the final boss it plays the ending (Ending Panels 1-3)
  and sets `gameComplete`. It then rolls the credits over the Credits Backdrop, with framed paintings
  and rising Festival Lanterns. Hold Enter or A to speed the roll up; Esc or B returns to the menu.
  From the menu only the roll plays.

**Pip and the rules** (`hero_gen.py`)
- **Explorer** (Pip): movement, swimming, gliding, spores, damage, checkpoints, goals and the HUD.
  The top of its Create event lists every global and RoomValue other objects rely on.
- **Level Rules**: one per playable room. It reads the room's name and publishes `levelId`, `dark`,
  `underwater`, `wind`, `timeLimit` and `hub` as RoomValues. It plays the room's music and ambience,
  darkens dark rooms and swaps to the boss theme when RoomValue `bossFight` becomes 1.
- Power-ups **Glide Seed, Spore Puff, Bark Shield, Starlit Firefly, Heart Berry**. **Spore Shot**,
  **Shield Ring**, **Glowspore** (touch = level clear; bosses drop one), and the seed pod prize
  markers **Prize Glide / Spore / Shield / Star / Heart**: place one at the centre of a seed pod
  tile and that power-up sprouts from it.

**Enemies**. All of them follow one convention, so any new enemy works with Pip unchanged. In
Create set `enemy = 1; hp = n; stompable = 1 or 0; contactDamage = 1; points = n; dead = 0;
hurt = 0;`. Pip adds 1 to `hurt` for a stomp (only when stompable is 1), a spore or the Starlit
Firefly. The enemy's own Step spends `hurt` on `hp`, plays its defeat, sets `dead` and destroys itself.
- Meadow: Acorn Walker, Buzz Bee (follows the Bee Patrol paths).
- Caverns and Canopy (`levels_a_gen.py`): Flit Bat, Glow Grub, Stalactite, Pebble, Grumblecrag,
  Dusk Moth, Burr Hopper. Props: Crumble Block, Moving Leaf (a one-way platform publishing
  `platformW`, `platformTop`, `platformDx`, `platformDy`), Cavern Glow (lights), Wind Controller
  (publishes RoomValue `windForce`).
- Grotto and Hollow (`levels_b_gen.py`): Bloatfin, Driftjelly, Duskmantle, Gloom Orb. Props: Current
  (water current zones), Grotto Controller, Hollow Controller.

**Map and village** (`hub_gen.py`): Map Marker, Map Node, Map Clouds; Village Host (dialogue box,
Chanterelle's shop, leaving the village), NPC Elder Morel / Chanterelle / Bram / Puffball / Old Wick (all
their lines are in the **Village Lines** script), Lantern Tree Glow, Chimney Smoke (live puffs over the
Village Houses chimneys, following that backdrop's 0.6 parallax), Shop Stall, Exit Sign.

**Phase-1 helpers**: Sun Coin, Big Sun Coin, Checkpoint (with its sprite rig), Finish, Bounce Mushroom.

## Every resource kind, and where it is used

- **Images**: Pip's poses, creatures, NPCs and portraits, items and HUD icons (`char_art.py`,
  `creature_art.py`). Six world tilesets and the level backgrounds with parallax (`world_art.py`,
  `worlds_art.py`). The Intro and Ending panels, Menu and Credits backdrops, the Overworld Map and
  Clouds, and the Lantern Tree Lights overlays (`scenes_art.py`).
- **Audio**: one theme per place plus Intro, Menu, Boss, Duskmantle, Starlit and Credits themes
  (`music_gen.py`, "music" bus), ambiences ("ambient" bus), and the sound effects (`sfx_gen.py`,
  `audio_gen.py`, "sfx" bus). All of it is original and generated for this template.
- **Shaders**: SNES Look (warm grade, vignette, optional CRT scanlines) on most rooms. Cavern Damp,
  Underwater, Golden Hour and Gloom as per-level post effects. Pixel Flash (hit flash) and Rainbow
  Glow (Starlit Firefly) are sprite shaders.
- **Particles**: dust, sparkles, bubbles, leaves, boss bursts, map unlock bursts and festival
  lanterns. World particles are placed with `ParticleBurstAt` / `SpawnParticleEmitter`. In 2D
  rooms an effect unit is 12 pixels and gravity is positive upwards.
- **User Interfaces**: Meadow HUD and Meadow Panels (Pip's HUD), Overworld HUD, Dialogue Box,
  Shop Menu, Main Menu UI.
- **Scripts**: Game Save, Game Load, New Game, Apply Options, Cinematic Kit (library functions for
  the cinematics: `CkDrawPanel`, `CkDrawCaption`, `CkMotes*`...), Hub Common, Village Lines,
  Levels B Lib, Spawn Effect, Time Bonus. The product libraries (`product_gen.py`): **Difficulty Kit**
  (`DiffLevel`, `DiffBaseHearts`, `DiffTime`, `DiffFoe`, `DiffGrace`, `DiffBerry`, `DiffChange`...; the
  ordinary foes' Step events scale their delta time by `DiffFoe()`), **Achievements Kit** (`AchGrant`,
  `AchOnKill`, `AchOnClear`, `AchToastStep` / `AchToastDraw`...; the Gore Kit's death hook, Explorer, Village
  Host and the Map Marker feed it) and **Journal Kit** (`JrnOpen`, `JrnStep`, `JrnDraw`: the How to Play and
  Collection pages, opened like the Options Kit from the Main Menu, the pause menu and the map menu).
  A new achievement is one more row in `ACHIEVEMENTS` (`product_gen.py`; at most 20) and one `AchGrant(n)`
  call where it is earned.
- **Paths**: the Bee Patrol routes. **Physics**: Meadow Turf and Mushroom Cap. **Note**: this guide.

## Extending the game

- **A new level**: copy a level room (or write a new section in a generator) and give it a unique
  name. Place the Explorer, one Level Rules and a Glowspore (or a boss that drops one). Add the room
  to the table in Level Rules' Create (level id, dark/underwater/wind, time limit, music). On the map,
  add a node in `hub_gen.py`. Add `clear6` / `medals6` to `SAVE_FIELDS` in `frontend_gen.py`.
- **A new enemy**: an Object following the enemy convention above. If Pip should collide with it, add
  its name to Pip's enemy list (Explorer).
- **A new power-up**: an item object, a `power` number in Explorer, and a Prize marker if it should
  come from seed pods.
- **New dialogue**: edit LINES in `hub_gen.py` (or the Village Lines script directly).
- **Another menu option**: add a row to the options page in `build_menu_ui()` (`frontend_gen.py`), a
  global with a default in Apply Options, its field in the "Options" save line, and its handling in
  the Menu Controller's options page.
- **A different story**: the intro and ending captions, camera moves and credits are plain tables at
  the top of `frontend_gen.py` (and of the directors' Create events in Studio).

## Releasing the game

The version lives in the project file (`"version": "1.0.0"`, Preferences > Project > Release > Game version) and is
read by the game as `Engine.Game.Version` (the Main Menu footer and the credits show it; `JrnVersion()` falls
back to "1.0.0" when a project keeps the default "1.0"). An exported game's window title and executable are the
project's name, "Mushroom Meadow". For the Windows icon pick `Mushroom Meadow.ico` (next to the template's
project file and in `BuildTools/TemplateContent/MushroomMeadow/`, made by `product_gen.py` from the cap of the
Pip portrait) in the Export Game dialog. `product_gen.py` also sets the project icon (`projectIcon`) to the Pip
portrait for the project hub.

## Acceptance checks

- Launch: the intro plays with music and captions, any key skips it, and the Main Menu fades in
  with its theme.
- The menu works by keyboard, controller and mouse. New Game on an empty slot goes to the map;
  New Game on a used slot asks first; Continue loads; Delete asks and clears the slot. Options
  change volumes and scanlines at once and are remembered after a restart.
- On the map, clear the Meadow: the Glowspore count rises, the path to Rootdeep Caverns opens and
  the game saves.
- In Hearthollow, the Tree shows one light per Glowspore and every villager talks.
- Defeating Duskmantle plays the ending and the credits, then returns to the menu, where the slot
  shows "THE TREE IS LIT".
