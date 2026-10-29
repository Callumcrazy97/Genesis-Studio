using System.Globalization;
using System.Numerics;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Physics;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Assets;
using Genesis.Shared.Audio;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>What one script run left behind: its variables and what it reported.</summary>
internal sealed record EngineApiRun(IReadOnlyDictionary<string, object> Variables, string Errors)
{
    public bool Failed => Errors.Length > 0;

    /// <summary>A variable as a number (true is 1); NaN when it is not set or is text.</summary>
    public double Number(string name) => Variables.TryGetValue(name, out object? value) ? EngineApiGame.AsNumber(value) : double.NaN;

    /// <summary>A variable as text; empty when it is not set.</summary>
    public string Text(string name) => Variables.TryGetValue(name, out object? value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
}

/// <summary>
/// A running 3D room without a window or renderer, with what the engine's values need to live in: a
/// scene with its physics, climate and input; a sound system that remembers its bus volumes; a terrain
/// with a lake, grass and scattered stones; and one probe instance with a physics body, a model, a
/// sprite, a particle effect and a drawing surface. Scripts run as the Create event of the probe and
/// report what they set. What needs a window or a real renderer or network (the window's place, the
/// monitor, a sound device, replication) has nothing here to hold it and reads its default. The
/// process-wide state a script touches is put back on dispose.
/// </summary>
internal sealed class EngineApiGame : IDisposable
{
    private readonly RuntimeScene _scene;
    private readonly ProjectGameContext _game;
    private readonly ScriptHostSystem _host;
    private readonly PgslContext? _previousContext;
    private readonly IGameContext? _previousGame;
    private readonly string _previousPath;
    private readonly Entity _probe;
    private readonly Genesis.Runtime.Rendering.ObjectCompositionSubsystem _effects;

    public EngineApiGame(ProjectSession project)
    {
        string root = project.RootPath;
        RoomAsset room = RoomAsset.Create("EngineApiRoom", RoomDimension.ThreeD);
        AddTerrain(project, room);

        _scene = new RuntimeScene("Engine API");
        _scene.Physics = PhysicsWorld.Create(new PhysicsWorldAsset());
        _scene.Physics.EnableThreadDispatcher = false;
        _scene.Input = new Genesis.Runtime.Input.InputState();
        _scene.ConfigureClimate(new Genesis.Runtime.Climate.EnvironmentOptions { AutomaticWeather = false }, Genesis.Runtime.Climate.WeatherKind.Rain);
        _scene.ConfigureAtmosphere(new Genesis.Runtime.Climate.AtmosphereOptions());
        _game = new ProjectGameContext(root, _scene, null, null, room, null, new FakeAudio());
        _scene.AddSubsystem(new RoomTerrainSubsystem(root, room, _game));
        _effects = new Genesis.Runtime.Rendering.ObjectCompositionSubsystem(root);

        _previousContext = PgslCommands.BindContext(new PgslContext());
        _previousGame = PgslCommands.ActiveGameContext;
        _previousPath = PgslCommands.ProjectPath;
        PgslCommands.ActiveGameContext = _game;
        PgslCommands.ProjectPath = root;
        VMEngine.Initialize();
        _host = new ScriptHostSystem();
        _host.SetContext(_game);

        var world = _scene.World;
        _probe = world.CreateEntity();
        world.Set(_probe, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
        world.Set(_probe, new Transform3DComponent { Position = new Vector3(0, 5, 0), Rotation = Quaternion.Identity, Scale = Vector3.One });
        world.Set(_probe, RigidBodyComponent.DynamicBox(new Vector3(0.5f), 2f));
        world.Set(_probe, new ModelRendererComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1, CastShadows = true, ReceiveShadows = true });
        world.Set(_probe, new ModelAnimatorComponent { PlaybackSpeed = 1f, Loop = true });
        world.Set(_probe, new Draw2DComponent { Visible = true });
        world.Set(_probe, new ParticleComponent { Asset = "builtin://Explosion", ParticleTypeId = -1, RateScale = 1f, FollowEntity = true, Emitting = true });
        Inspector = new PgslContext { InstanceId = _probe.Id, DrawSurface = new PgslRecordingDrawSurface() };

        // The effect starts running, and the climate takes its first step, as a room's first frame does.
        var time = new Genesis.Runtime.Core.GameTime();
        time.Advance(1f / 30f);
        _effects.Update(_scene, time);
        _scene.Climate.Update(0.1f, Vector3.Zero);
    }

    // A small hill with a lake, grass and a layer of stones: what Engine.Terrain, Engine.Water and the
    // grass and scatter values act on.
    private static void AddTerrain(ProjectSession project, RoomAsset room)
    {
        var resources = new ResourceService(project);
        string terrainPath = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Terrain), ResourceKind.Terrain, "Api Vale");
        var asset = new Genesis.World.Terrain.TerrainAsset(33, 33, 2f, -32f, -32f, 0f, 20f);
        for (int z = 0; z < 33; z++)
            for (int x = 0; x < 33; x++) asset.SetHeight(x, z, x * 0.5f);
        asset.Save(terrainPath + ".gterrain");
        var nature = Genesis.World.Terrain.TerrainNatureSerializer.LoadOrDefault(terrainPath);
        nature.GrassRule.Enabled = true;
        nature.ScatterLayers.Add(new Genesis.World.Terrain.TerrainScatterLayer { Name = "Stones", DensityPerHectare = 50 });
        nature.WaterBodies.Add(new Genesis.World.Terrain.TerrainWaterDefinition { Name = "Tarn", Kind = Genesis.World.Terrain.TerrainWaterKind.Water });
        Genesis.World.Terrain.TerrainNatureSerializer.Save(terrainPath, nature);
        ResourceCatalog.Invalidate(project.RootPath);
        room.Nodes.Add(new RoomNode
        {
            Name = "Vale", Kind = RoomNodeKind.Terrain, LayerId = room.Layers[0].Id,
            Terrain = new RoomTerrainData { Asset = Path.GetRelativePath(project.RootPath, terrainPath).Replace('\\', '/') },
        });
    }

    public RuntimeScene Scene => _scene;

    public SceneEnvironment Environment => _scene.Environment;

    /// <summary>The probe instance as the Inspector and the debug screen address it.</summary>
    public PgslContext Inspector { get; }

    public Entity Probe => _probe;

    public ScriptHostSystem Host => _host;

    /// <summary>Runs a script as the probe's Create event; its variables and the errors it reported.</summary>
    public EngineApiRun Run(string script)
    {
        _host.ClearDiagnostics();
        // Create waits until the drawing surface is on the instance, so Engine.Draw has one to set.
        _host.DeferCreateEvents = true;
        try
        {
            using (_host.UseEventSources(new Dictionary<string, string> { ["Create"] = script }))
                _host.Attach(_scene.World, _probe, "EngineApiProbe");
            PgslBehavior created = _host.Instances.OfType<PgslBehavior>().Single(candidate => candidate.Entity == _probe);
            created.Context.DrawSurface = new PgslRecordingDrawSurface();
        }
        finally
        {
            _host.DeferCreateEvents = false;
        }
        _host.FlushDeferredCreates();
        PgslBehavior behavior = _host.Instances.OfType<PgslBehavior>().Single(candidate => candidate.Entity == _probe);
        var variables = new Dictionary<string, object>(behavior.GetVariablesSnapshot(), StringComparer.Ordinal);
        string errors = string.Join("; ", _host.RecentDiagnostics.Select(diagnostic => diagnostic.Message));
        _host.Detach(_probe);
        return new EngineApiRun(variables, errors);
    }

    public static double AsNumber(object? value) => value switch
    {
        null => double.NaN,
        bool flag => flag ? 1 : 0,
        string => double.NaN,
        IConvertible convertible => convertible.ToDouble(CultureInfo.InvariantCulture),
        _ => double.NaN,
    };

    /// <summary>A value as PGSL source: a number, true or false, or quoted text.</summary>
    public static string Literal(object? value) => value switch
    {
        bool flag => flag ? "true" : "false",
        string text => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
        null => "0",
        IConvertible convertible => convertible.ToDouble(CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
        _ => "0",
    };

    public void Dispose()
    {
        _host.Detach(_probe);
        // Values some scripts leave in process-wide statics when nothing puts them back.
        PgslCommands.AudioApiAcoustics = "outdoors";
        Genesis.Runtime.Modeling.ModelLodView.Quality = 1f;
        Genesis.Runtime.Project.RoomTerrainSubsystem.GameTerrainDetail = 1f;
        Genesis.Runtime.ECS.Components.ParticleQuality.Reset();
        Genesis.Runtime.Input.InputSettings.Reset();
        PgslCommands.ActiveGameContext = _previousGame;
        PgslCommands.ProjectPath = _previousPath;
        PgslCommands.BindContext(_previousContext);
        _effects.Dispose();
        _scene.Dispose();
    }

    /// <summary>A sound system that plays nothing and remembers what the mixer would be told.</summary>
    private sealed class FakeAudio : IAudioSystem
    {
        private readonly Dictionary<string, float> _buses = new(StringComparer.OrdinalIgnoreCase);
        public float MasterVolume { get; set; } = 1f;
        public int LoadSound(string projectRelativePath) => 1;
        public AudioChannel Play(int soundId, float volume = 1f, float pitch = 1f, bool loop = false) => new(1);
        public void Stop(AudioChannel channel) { }
        public void StopAll() { }
        public bool IsPlaying(AudioChannel channel) => false;
        public void SetChannelVolume(AudioChannel channel, float volume) { }
        public void SetChannelPosition(AudioChannel channel, Vector3 position) { }
        public void SetListener(Vector3 position, Vector3 forward) { }
        public void SetBusVolume(string bus, float volume) => _buses[bus] = volume;
        public float GetBusVolume(string bus) => _buses.TryGetValue(bus, out float volume) ? volume : 1f;
        public string Acoustics { get; set; } = "outdoors";
        public float AcousticsAmount { get; set; } = .5f;
        public float MusicMuffle { get; set; }
        public void Update() { }
    }
}
