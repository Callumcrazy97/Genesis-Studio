# Genesis Studio documentation

Genesis Studio makes 2D and 3D games for Windows. These guides are installed with it, in the
`Documentation` folder beside the application.

| Guide | What it covers |
|---|---|
| [Getting started](GettingStarted.md) | Your first game in four steps, the recipes, a first 3D scene and where things are. Studio shows it from *Help › Getting started* (F1). |
| [Game features](GameFeatures.md) | Weather, a terrain's water, what a script may ask of a model, animation events, controllers, positioned sound, HUD shapes, particle bursts, save slots, and room changes behind a loading screen. |
| [Large worlds](LargeWorlds.md) | Kilometre-scale terrain, scattered forests, automatic model detail, long views, loading, sharing a world between players, and what is not done yet. |
| [Terrain creation](TerrainCreation.md) | Making, sculpting and painting a terrain, and its materials. |
| [Post effects](PostEffects.md) | The project's own full-screen shaders over the finished frame, such as an ink outline. |
| [PGSL language notes](PgslLanguage.md) | Operators, loops, scope, the names the engine owns, limits and what happens on a mistake. |
| [Mesh shaders](MeshShaders.md) | A model's own pixel shader: per material, per instance from scripts, and the inputs it can rely on. |

*Help › Commands…* in Studio lists every game-code command with an example.

## Publishing your game

*File › Export Game* makes a folder or a ZIP that runs on any 64-bit Windows 10 or 11 computer
without Genesis Studio and without anything else to install. It contains your game, the Genesis
Player renamed to your game's title, and a `Licenses` folder.

- **Keep the `Licenses` folder with the game.** It holds the notices that the libraries inside
  the Player require every copy to carry.
- A game may be put anywhere, including a folder it cannot write to such as `Program Files`.
  It then keeps its log in the player's own folder (below) instead of beside itself.
- A game written only in PGSL is exported without compiling any C#: its PGSL is checked as
  strictly as Run checks it and shipped as it is, the project file keeps its chosen backend,
  and there is no `GameScripts.dll`. Only a game with C# scripts (`Assets\Scripts\*.cs`) has
  them compiled into `GameScripts.dll` beside its executable.
- Windows x64 is the only platform.

## Where Genesis keeps things

| What | Where |
|---|---|
| Studio's settings, window layout and log | `%LOCALAPPDATA%\Genesis\Genesis Application` |
| Your own theme images | `%LOCALAPPDATA%\Genesis\Genesis Application\Themes` |
| A project | wherever you created it; `Documents\Genesis Projects` by default |
| A project's backups, cache and compiled scripts | the `.genesis` folder inside the project |
| A game's save slots and saved numbers | `%LOCALAPPDATA%\Genesis\GameSaves\<game id>` |
| A game's log | `Debug\Logs\project_player.log` in the project or game folder; if that folder cannot be written to, `%LOCALAPPDATA%\Genesis\GameSaves\<game id>\Debug\Logs` |
| A game's crash report | `GenesisEngine.crash.log` beside the game; if that folder cannot be written to, `%LOCALAPPDATA%\Genesis\CrashReports` |

Removing Genesis Studio leaves all of these in place. Delete `%LOCALAPPDATA%\Genesis` yourself
if you also want the settings and saves gone.

## Licences

Genesis Studio is free to use. Its licence, `Licenses\Genesis-LICENSE.txt` beside the
application, says in plain English what you may do: make games with it, and give away or sell
them with the Genesis Player inside. What you make is yours, and Genesis takes no royalty. Every
exported game carries the same licence in its `Licenses` folder for its players.

The software Genesis Studio is built with is listed in `Licenses\ThirdPartyNotices.txt`.
*Help › About Genesis* shows the version you have.
