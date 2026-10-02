using System.Numerics;
using Genesis.Runtime;
using Genesis.Runtime.Climate;
using Genesis.Runtime.Core;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.Audio;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless;

/// <summary>
/// Engine features added for games that need them rather than for one game: weather the engine
/// draws itself, and what follows it in this file.
/// </summary>
internal static class EngineAdditionsSuite
{
    private sealed class ThunderRecorder : IAudioSystem
    {
        public readonly List<(string Sound, float Volume)> Played = new();
        private readonly Dictionary<int, string> _sounds = new();
        public float MasterVolume { get; set; } = 1f;
        public int LoadSound(string path) { int id = _sounds.Count + 1; _sounds[id] = path; return id; }
        public AudioChannel Play(int soundId, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            Played.Add((_sounds[soundId], volume));
            return new AudioChannel(Played.Count);
        }

        public void Stop(AudioChannel channel) { }
        public void StopAll() { }
        public bool IsPlaying(AudioChannel channel) => false;
        public void SetChannelVolume(AudioChannel channel, float volume) { }
        public void SetChannelPosition(AudioChannel channel, Vector3 position) { }
        public void SetListener(Vector3 position, Vector3 forward) { }
        public void Update() { }
    }

    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.RunCase(context.Report, "Engine.World.Weather.TheEngineDrawsRainSnowAndLightningWhenARoomAsks", () =>
        {
            RoomAsset room = RoomAsset.Create("Moor", RoomDimension.ThreeD);
            HeadlessHarness.Assert(!room.Environment.WeatherEffects && !RoomWeatherEffectsSubsystem.ShouldRegister(room),
                "A new room must not be given weather effects: a game that draws its own would get two.");
            room.Environment.DynamicSky = true;
            room.Environment.AutomaticWeather = false;
            room.Environment.Weather = "Thunderstorm";
            room.Environment.WeatherEffects = true;
            room.Environment.ThunderAudio = "Thunder Clap";
            room.Environment.SoundscapeLevels.Thunder = 0.5f;
            HeadlessHarness.Assert(RoomWeatherEffectsSubsystem.ShouldRegister(room), "A 3D room with a dynamic sky that asks for weather effects was refused them.");

            using var scene = new RuntimeScene("Weather");
            RoomSceneBuilder.ApplySceneSettings(scene, room);
            var audio = new ThunderRecorder();
            using var weather = new RoomWeatherEffectsSubsystem(room.Environment, audio);
            var time = new GameTime();
            float brightest = 0f;
            int framesLit = 0, frames = 0;
            void Run(float seconds)
            {
                for (float elapsed = 0f; elapsed < seconds; elapsed += 1f / 30f)
                {
                    time.Advance(1f / 30f);
                    scene.UpdateVariable(1f / 30f);
                    weather.Update(scene, time);
                    scene.World.FlushDeferred();
                    brightest = MathF.Max(brightest, weather.Flash);
                    frames++;
                    if (weather.Flash > 0.01f) framesLit++;
                    HeadlessHarness.Assert(scene.Environment.LightningFlash == weather.Flash, "The scene was not told about the flash.");
                }
            }

            // A storm: heavy rain falling, and lightning every few seconds with thunder after it.
            Run(60f);
            HeadlessHarness.Assert(weather.Precipitation == "builtin://Rain" && weather.PrecipitationRate > 1f,
                $"A thunderstorm should bring a downpour; the effect is '{weather.Precipitation}' at rate {weather.PrecipitationRate:F2}.");
            int emitters = 0;
            float emitterRate = 0f;
            scene.World.Query<TransformComponent, ParticleComponent>((Entity _, ref TransformComponent _, ref ParticleComponent particles) =>
            {
                emitters++;
                emitterRate = particles.RateScale;
                HeadlessHarness.Assert(particles.Asset == "builtin://Rain" && particles.Emitting, "The rain emitter is not emitting rain.");
            });
            HeadlessHarness.Assert(emitters == 1 && MathF.Abs(emitterRate - weather.PrecipitationRate) < 1e-4f,
                $"There should be one rain emitter at the weather's rate; there are {emitters} at {emitterRate:F2}.");
            HeadlessHarness.Assert(weather.Strikes is >= 3 and <= 14, $"A minute of thunderstorm struck {weather.Strikes} times; expected roughly one every five to thirteen seconds.");
            HeadlessHarness.Assert(brightest > 0.3f && framesLit > 0 && framesLit < frames / 4,
                $"Lightning should light a few frames brightly; it lit {framesLit} of {frames}, the brightest at {brightest:F2}.");

            // The flash lights the frame: more light from above, and a paler sky.
            var dark = new Mesh3DState { AmbientColor = new Vector3(0.05f), SkyHorizonColor = new Vector3(0.1f), SkyZenithColor = new Vector3(0.05f), BackgroundColor = new Vector3(0.1f) };
            Mesh3DState lit = dark;
            EnvironmentMapper.ApplyLightningFlash(ref lit, 1f);
            Mesh3DState untouched = dark;
            EnvironmentMapper.ApplyLightningFlash(ref untouched, 0f);
            HeadlessHarness.Assert(lit.AmbientColor.Y > dark.AmbientColor.Y + 1f && lit.SkyHorizonColor.Y > 0.5f && untouched.AmbientColor == dark.AmbientColor,
                "A lightning flash did not light the frame, or lit a frame with no flash.");

            // Thunder follows each strike, never before it, at the level the room set.
            Run(13f);
            HeadlessHarness.Assert(weather.Thunderclaps >= weather.Strikes - 2 && weather.Thunderclaps <= weather.Strikes
                && audio.Played.Count == weather.Thunderclaps,
                $"{weather.Strikes} strikes were followed by {weather.Thunderclaps} thunderclaps ({audio.Played.Count} played).");
            HeadlessHarness.Assert(audio.Played.All(clap => clap.Sound == "Thunder Clap" && clap.Volume > 0f && clap.Volume <= 0.5f),
                "Thunder was played with the wrong sound, or louder than the room's thunder level.");

            // The weather turns to snow, then clears: the effect follows, and the lightning stops.
            scene.Climate.SetWeather(WeatherKind.Snow, 0f);
            int strikes = weather.Strikes;
            Run(20f);
            HeadlessHarness.Assert(weather.Precipitation == "builtin://Snow" && weather.Strikes == strikes,
                $"Snow should replace the rain and end the lightning; the effect is '{weather.Precipitation}' after {weather.Strikes - strikes} more strikes.");
            scene.Climate.SetWeather(WeatherKind.Clear, 0f);
            Run(2f);
            emitters = 0;
            scene.World.Query<ParticleComponent>((Entity _, ref ParticleComponent _) => emitters++);
            HeadlessHarness.Assert(weather.Precipitation.Length == 0 && emitters == 0, "Clear weather left a precipitation emitter behind.");

            // A script can call lightning down from a clear sky.
            weather.Strike(120f);
            Run(1f / 30f);
            HeadlessHarness.Assert(weather.Strikes == strikes + 1 && weather.Flash > 0.5f, "A strike asked for by a script did not flash.");
            Run(1.5f);
            HeadlessHarness.Assert(weather.Flash == 0f && audio.Played.Count == weather.Thunderclaps && audio.Played[^1].Volume > 0.4f,
                "A strike 120 m away should be over within a second, with loud thunder just after it.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.World.Water.ATerrainCanGiveItsWaterItsOwnLook", () =>
        {
            // By default a lake and a sea keep the look their kind is given.
            var lake = new Genesis.World.Terrain.TerrainWaterDefinition { Name = "Tarn", Kind = Genesis.World.Terrain.TerrainWaterKind.Water };
            var sea = new Genesis.World.Terrain.TerrainWaterDefinition { Name = "Sea", Kind = Genesis.World.Terrain.TerrainWaterKind.Ocean };
            Genesis.World.Water.WaterMaterialSettings standard = Genesis.World.Water.WaterMaterialSettings.Default;
            Genesis.World.Water.WaterMaterialSettings lakeLook = lake.ToWaterBody().Material, seaLook = sea.ToWaterBody().Material;
            HeadlessHarness.Assert(lakeLook.ShallowColor == standard.ShallowColor && lakeLook.Opacity == standard.Opacity && seaLook.Opacity > 0.9f,
                "Water with no look of its own must keep the look of its kind.");

            // A peat river: brown, nearly opaque within a metre, a thin line of foam.
            var peat = new Genesis.World.Terrain.TerrainWaterDefinition
            {
                Name = "Peat Burn", Kind = Genesis.World.Terrain.TerrainWaterKind.River, CustomAppearance = true,
                ShallowColor = new Vector3(0.42f, 0.30f, 0.14f), DeepColor = new Vector3(0.12f, 0.07f, 0.03f),
                Opacity = 0.85f, ClarityDepth = 1.2f, FoamWidth = 0.3f,
            };
            Genesis.World.Water.WaterMaterialSettings peatLook = peat.ToWaterBody().Material;
            HeadlessHarness.Assert(peatLook.ShallowColor == peat.ShallowColor && peatLook.DeepColor == peat.DeepColor
                && peatLook.Opacity == 0.85f && peatLook.DepthFade == 1.2f && peatLook.FoamWidth == 0.3f,
                "The water's own colours, clarity and foam did not reach what is drawn.");

            // The look travels with a copy, survives a save, and nonsense is brought into range.
            Genesis.World.Terrain.TerrainWaterDefinition copy = peat.Clone();
            HeadlessHarness.Assert(copy.CustomAppearance && copy.DeepColor == peat.DeepColor && copy.ClarityDepth == 1.2f, "Copying water lost its look.");
            string folder = Path.Combine(context.Workspace, "WaterLook");
            Directory.CreateDirectory(folder);
            string resource = Path.Combine(folder, "Vale.terrain.json");
            var nature = new Genesis.World.Terrain.TerrainNatureDocument();
            nature.WaterBodies.Add(peat);
            Genesis.World.Terrain.TerrainNatureSerializer.Save(resource, nature);
            Genesis.World.Terrain.TerrainWaterDefinition loaded = Genesis.World.Terrain.TerrainNatureSerializer.LoadOrDefault(resource).WaterBodies.Single();
            HeadlessHarness.Assert(loaded.CustomAppearance && Vector3.Distance(loaded.ShallowColor, peat.ShallowColor) < 1e-5f
                && loaded.Opacity == 0.85f && loaded.FoamWidth == 0.3f,
                $"The water's look was not saved with the terrain (custom {loaded.CustomAppearance}, opacity {loaded.Opacity}).");
            var broken = new Genesis.World.Terrain.TerrainWaterDefinition
            {
                CustomAppearance = true, ShallowColor = new Vector3(float.NaN, 2f, -1f), Opacity = 7f, ClarityDepth = -3f, FoamWidth = float.PositiveInfinity,
            };
            Genesis.World.Water.WaterMaterialSettings mended = broken.ToWaterBody().Material;
            HeadlessHarness.Assert(mended.Opacity == 1f && mended.DepthFade == 0.1f && float.IsFinite(mended.FoamWidth)
                && float.IsFinite(mended.ShallowColor.X) && mended.ShallowColor.Y <= 1f && mended.ShallowColor.Z >= 0f,
                "Out-of-range water settings reached the renderer.");

            // The Water Bodies dialog shows and edits the look.
            using var dialog = new Genesis.Application.Editors.Suite.Terrain.TerrainWaterDialog(new[] { peat }, Vector3.Zero);
            var custom = (System.Windows.Forms.CheckBox)dialog.Controls.Find("TerrainWaterCustomLook", true)[0];
            System.Windows.Forms.Control shallow = dialog.Controls.Find("TerrainWaterShallowColour", true)[0];
            HeadlessHarness.Assert(custom.Checked && shallow.Enabled && shallow.BackColor.R == 107 && shallow.BackColor.B == 36,
                $"The dialog did not open on the water's own look (custom {custom.Checked}, swatch {shallow.BackColor}).");
            Genesis.World.Terrain.TerrainWaterDefinition edited = dialog.WaterBodies[0];
            HeadlessHarness.Assert(edited.CustomAppearance && edited.Opacity == 0.85f && Vector3.Distance(edited.ShallowColor, peat.ShallowColor) < 0.01f,
                "Opening the dialog changed the water's look.");
            custom.Checked = false;
            HeadlessHarness.Assert(!dialog.WaterBodies[0].CustomAppearance && !shallow.Enabled, "Turning the look off in the dialog did not take.");
        });

        HeadlessHarness.RunCase(context.Report, "Engine.Particles.Weather.FallsFromJustAboveTheCameraAtAnyAltitude", () =>
        {
            // On a mountain 600 m up, rain must fall past the camera, not start at sea level.
            ParticleConfig rain = ParticlePresets.Rain();
            HeadlessHarness.Assert(rain.FollowCameraXZ && rain.FollowCameraHeight >= 5.0, "The rain preset no longer follows the camera from above.");
            var simulation = new ParticleSimulation();
            simulation.LoadConfig(rain);
            simulation.Reset(7);
            var camera = new Vector3(1200f, 600f, -340f);
            simulation.UpdateCameraPosition(camera);
            for (int step = 0; step < 30; step++) simulation.Step(1f / 60f);
            simulation.GetEmitterBounds(out Vector3 centre, out float radius);
            HeadlessHarness.Assert(simulation.ActiveCount > 50, $"Half a second of rain produced {simulation.ActiveCount} drops.");
            HeadlessHarness.Assert(MathF.Abs(centre.X - camera.X) < radius && MathF.Abs(centre.Z - camera.Z) < radius
                && centre.Y > camera.Y - radius && centre.Y < camera.Y + radius && radius < 80f,
                $"Rain around a camera at height 600 is centred at {centre} with radius {radius:F0}; it should surround the camera.");
        });
    }
}
