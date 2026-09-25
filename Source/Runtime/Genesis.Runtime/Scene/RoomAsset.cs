using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.ComponentModel;

namespace Genesis.Runtime.Scene;

public enum RoomDimension { TwoD, ThreeD }
public enum RoomNodeKind { GameObject, Background, TileLayer, Terrain }
public enum RoomBackgroundMode { TwoD, Sky, Billboard, WorldPlane }
public enum RoomBackgroundLayout { Single, Tile, StretchRoom, StretchView }

public sealed class RoomAsset
{
    public const string SchemaName = "genesis.room";
    public const int CurrentVersion = 1;

    [JsonProperty("$schema", Order = -100)] public string Schema { get; set; } = SchemaName;
    [JsonProperty("version", Order = -99)] public int Version { get; set; } = CurrentVersion;
    [JsonProperty("id")] public string Id { get; set; } = NewId();
    [JsonProperty("name")] public string Name { get; set; } = "Room";
    [JsonProperty("dimension")] public RoomDimension Dimension { get; set; }
    [JsonProperty("settings")] public RoomSettings Settings { get; set; } = new();
    [JsonProperty("environment")] public RoomEnvironment Environment { get; set; } = new();
    [JsonProperty("activeGameCameraId")] public string ActiveGameCameraId { get; set; } = "";
    [JsonProperty("viewports")] public List<RoomViewport> Viewports { get; set; } = new();
    [JsonProperty("layers")] public List<RoomLayer> Layers { get; set; } = new();
    [JsonProperty("nodes")] public List<RoomNode> Nodes { get; set; } = new();

    /// <summary>
    /// Fixed viewport count. A fixed set means slot 3 stays slot 3 for the life of the room, so
    /// scripts and split-screen layouts can address them by index without an id indirection.
    /// </summary>
    public const int MaxViewports = 8;

    /// <summary>
    /// True when the room draws through configured viewports rather than one full-window camera.
    /// A room with none enabled keeps the original single-camera path exactly.
    /// </summary>
    [JsonIgnore]
    public bool UsesViewports => Viewports.Exists(v => v.Enabled);

    public static RoomAsset Create(string name, RoomDimension dimension)
    {
        var room = new RoomAsset { Name = string.IsNullOrWhiteSpace(name) ? "Room" : name, Dimension = dimension };
        room.Layers.Add(new RoomLayer { Name = "Default", Order = 0 });
        room.Normalize();
        return room;
    }

    public RoomAsset DeepClone() => JsonConvert.DeserializeObject<RoomAsset>(
        JsonConvert.SerializeObject(this)) ?? Create(Name, Dimension);

    public void Normalize()
    {
        Schema = SchemaName;
        Version = CurrentVersion;
        if (string.IsNullOrWhiteSpace(Id)) Id = NewId();
        if (string.IsNullOrWhiteSpace(Name)) Name = "Room";
        Settings ??= new RoomSettings();
        Environment ??= new RoomEnvironment();
        Environment.SoundscapeLevels = (Environment.SoundscapeLevels ?? new()).Clone();
        Layers ??= new List<RoomLayer>();
        Nodes ??= new List<RoomNode>();
        Viewports ??= new List<RoomViewport>();
        Settings.Normalize();

        // Always exactly MaxViewports, so the editor can show slot N without inventing entries and
        // an older room gains its slots on load instead of failing to open.
        while (Viewports.Count < MaxViewports)
        {
            Viewports.Add(new RoomViewport
            {
                SourceWidth = Settings.Width,
                SourceHeight = Settings.Height,
                SourceDepth = Settings.Depth,
                PortWidth = Settings.Width,
                PortHeight = Settings.Height,
                FollowMarginX = Settings.Width * 0.25f,
                FollowMarginY = Settings.Height * 0.25f,
                FollowMarginZ = Settings.Depth * 0.25f,
            });
        }

        if (Viewports.Count > MaxViewports) Viewports.RemoveRange(MaxViewports, Viewports.Count - MaxViewports);
        foreach (RoomViewport viewport in Viewports) viewport.Normalize();
        if (Layers.Count == 0) Layers.Add(new RoomLayer { Name = "Default" });
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RoomLayer layer in Layers)
        {
            if (string.IsNullOrWhiteSpace(layer.Id) || !ids.Add(layer.Id)) layer.Id = NewId();
            layer.Name = string.IsNullOrWhiteSpace(layer.Name) ? "Layer" : layer.Name;
        }
        string defaultLayer = Layers[0].Id;
        ids.Clear();
        foreach (RoomNode node in Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || !ids.Add(node.Id)) node.Id = NewId();
            if (string.IsNullOrWhiteSpace(node.LayerId) || Layers.TrueForAll(l => l.Id != node.LayerId)) node.LayerId = defaultLayer;
            node.Transform ??= new RoomTransform();
            node.GameObject ??= node.Kind == RoomNodeKind.GameObject ? new RoomGameObjectData() : null;
            node.Background ??= node.Kind == RoomNodeKind.Background ? new RoomBackgroundData() : null;
            node.TileLayer ??= node.Kind == RoomNodeKind.TileLayer ? new RoomTileLayerData() : null;
            node.Terrain ??= node.Kind == RoomNodeKind.Terrain ? new RoomTerrainData() : null;
        }
        var valid = new HashSet<string>(Nodes.ConvertAll(n => n.Id), StringComparer.OrdinalIgnoreCase);
        foreach (RoomNode node in Nodes)
            if (node.ParentId == node.Id || (!string.IsNullOrEmpty(node.ParentId) && !valid.Contains(node.ParentId))) node.ParentId = "";
        if (!string.IsNullOrEmpty(ActiveGameCameraId) && !valid.Contains(ActiveGameCameraId)) ActiveGameCameraId = "";
    }

    public static string NewId() => Guid.NewGuid().ToString("N");
}

public sealed class RoomSettings
{
    [JsonProperty("width")] public int Width { get; set; } = 1280;
    [JsonProperty("height")] public int Height { get; set; } = 720;
    [JsonProperty("depth")] public int Depth { get; set; } = 720;
    /// <summary>Zero derives display size from enabled viewport ports, or the legacy room size.</summary>
    [JsonProperty("windowWidth")] public int WindowWidth { get; set; }
    [JsonProperty("windowHeight")] public int WindowHeight { get; set; }
    /// <summary>Scale 2D viewport destinations together and letterbox when the game window resizes.</summary>
    [JsonProperty("scaleViewportsWithWindow")] public bool ScaleViewportsWithWindow { get; set; }
    /// <summary>Point sampling for crisp pixel-art sprites and tiles; false preserves legacy filtering.</summary>
    [JsonProperty("pixelArtSampling")] public bool PixelArtSampling { get; set; }
    [JsonProperty("targetFps")] public int TargetFps { get; set; } = 60;
    [JsonProperty("fixedFps")] public int FixedFps { get; set; } = 60;
    [JsonProperty("persistent")] public bool Persistent { get; set; }
    [JsonProperty("vsync")] public bool VSync { get; set; } = true;
    /// <summary>When set, overrides default capture-on-3D behaviour. Menu rooms should set false.</summary>
    [JsonProperty("captureMouse")] public bool? CaptureMouse { get; set; }
    /// <summary>Apply voxel-world sky/sun defaults (gameplay rooms only).</summary>
    [JsonProperty("voxelWorld")] public bool VoxelWorld { get; set; }
    [JsonProperty("gridSize")] public float GridSize { get; set; } = 32f;
    [JsonProperty("snapEnabled")] public bool SnapEnabled { get; set; } = true;
    /// <summary>Metric 3D increment. Null preserves the legacy gridSize / 32 conversion.</summary>
    [JsonProperty("metricGridSize", NullValueHandling = NullValueHandling.Ignore)] public float? MetricGridSize { get; set; }
    [JsonProperty("snapToTerrain", DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(true)]
    public bool SnapToTerrain { get; set; } = true;
    [JsonProperty("alignToTerrainNormal")] public bool AlignToTerrainNormal { get; set; }
    [JsonProperty("randomYaw")] public bool RandomYaw { get; set; }
    [JsonProperty("randomYawDegrees")] public float RandomYawDegrees { get; set; } = 180f;
    [JsonProperty("placementRandomSeed")] public int PlacementRandomSeed { get; set; } = 1;
    /// <summary>Advances only when a randomized object placement is committed, with that placement's undo.</summary>
    [JsonProperty("placementRandomSequence")] public int PlacementRandomSequence { get; set; }
    public void Normalize()
    {
        Width = Math.Clamp(Width, 1, 100000); Height = Math.Clamp(Height, 1, 100000); Depth = Math.Clamp(Depth, 1, 100000);
        WindowWidth = Math.Clamp(WindowWidth, 0, 16384); WindowHeight = Math.Clamp(WindowHeight, 0, 16384);
        TargetFps = Math.Clamp(TargetFps, 0, 1000); FixedFps = Math.Clamp(FixedFps, 1, 1000); GridSize = Math.Clamp(GridSize, .01f, 4096f);
        if (MetricGridSize is float metric) MetricGridSize = float.IsFinite(metric) ? Math.Clamp(metric, .01f, 4096f) : null;
        RandomYawDegrees = float.IsFinite(RandomYawDegrees) ? Math.Clamp(RandomYawDegrees, 0, 180) : 180;
        PlacementRandomSequence = Math.Max(0, PlacementRandomSequence);
    }
}

/// <summary>
/// One camera region of the room, drawn into a rectangle of the game window.
/// </summary>
/// <remarks>
/// <para>Named <b>viewport</b> rather than "view" because the Room Editor already has a view of its
/// own, and every current engine uses this word for the same idea. A room carries a fixed set of
/// <see cref="RoomAsset.MaxViewports"/> so a designer configures slot 3 and it stays slot 3 —
/// scripts and split-screen layouts address them by index.</para>
///
/// <para><b>Source is position + size, not start/end.</b> Start/end pairs let a designer author a
/// negative width by editing one field, which is a state no renderer can draw and every consumer
/// then has to guard against. Position and size cannot express it.</para>
///
/// <para><b>Destination carries X/Y as well as size</b>, without which two viewports cannot be
/// placed side by side — which is the split-screen case the whole feature exists for.</para>
/// </remarks>
public sealed class RoomViewport
{
    /// <summary>Off by default: a room with every viewport live would letterbox itself eight ways.</summary>
    [JsonProperty("enabled")] public bool Enabled { get; set; }

    // ── Source: the region of the room this viewport looks at ──────────────────
    [JsonProperty("sourceX")] public float SourceX { get; set; }
    [JsonProperty("sourceY")] public float SourceY { get; set; }
    [JsonProperty("sourceZ")] public float SourceZ { get; set; }
    [JsonProperty("sourceWidth")] public float SourceWidth { get; set; } = 1280f;
    [JsonProperty("sourceHeight")] public float SourceHeight { get; set; } = 720f;
    [JsonProperty("sourceDepth")] public float SourceDepth { get; set; } = 720f;

    // ── Destination: where it lands on the game window, in pixels ──────────────
    [JsonProperty("portX")] public int PortX { get; set; }
    [JsonProperty("portY")] public int PortY { get; set; }
    [JsonProperty("portWidth")] public int PortWidth { get; set; } = 1280;
    [JsonProperty("portHeight")] public int PortHeight { get; set; } = 720;

    // ── 3D Camera Configuration ───────────────────────────────────────────────
    [JsonProperty("frustumNear")] public float FrustumNear { get; set; } = 0.1f;
    [JsonProperty("frustumFar")] public float FrustumFar { get; set; } = 2000f;
    [JsonProperty("fieldOfView")] public float FieldOfView { get; set; } = 60f;

    // ── Following ──────────────────────────────────────────────────────────────
    /// <summary>Object name this viewport tracks. Empty means the viewport does not move.</summary>
    [JsonProperty("followTarget")] public string FollowTarget { get; set; } = "";

    /// <summary>
    /// Dead zone, in pixels, the target may move inside before the viewport scrolls. Half the
    /// source size or more pins the target to the centre.
    /// </summary>
    [JsonProperty("followMarginX")] public float FollowMarginX { get; set; } = 320f;
    [JsonProperty("followMarginY")] public float FollowMarginY { get; set; } = 180f;
    [JsonProperty("followMarginZ")] public float FollowMarginZ { get; set; } = 180f;

    /// <summary>Pixels per frame the viewport may travel. -1 snaps instantly.</summary>
    [JsonProperty("followSpeedX")] public float FollowSpeedX { get; set; } = -1f;
    [JsonProperty("followSpeedY")] public float FollowSpeedY { get; set; } = -1f;
    [JsonProperty("followSpeedZ")] public float FollowSpeedZ { get; set; } = -1f;

    // ── Editor presentation (never affects the game) ───────────────────────────
    [JsonProperty("editorVisible")] public bool EditorVisible { get; set; } = true;
    [JsonProperty("editorColor")] public float[] EditorColor { get; set; } = { 0.35f, 0.78f, 1f, 1f };
    [JsonProperty("editorFillStyle")] public RoomViewportFillStyle EditorFillStyle { get; set; }
        = RoomViewportFillStyle.Outline;

    public void Normalize()
    {
        // A viewport with no area is indistinguishable from a disabled one at draw time but not at
        // edit time, so clamp rather than let it become an invisible special case.
        SourceWidth = Math.Clamp(SourceWidth, 1f, 100000f);
        SourceHeight = Math.Clamp(SourceHeight, 1f, 100000f);
        SourceDepth = Math.Clamp(SourceDepth, 1f, 100000f);
        PortWidth = Math.Clamp(PortWidth, 1, 100000);
        PortHeight = Math.Clamp(PortHeight, 1, 100000);
        FollowMarginX = Math.Clamp(FollowMarginX, 0f, 100000f);
        FollowMarginY = Math.Clamp(FollowMarginY, 0f, 100000f);
        FollowMarginZ = Math.Clamp(FollowMarginZ, 0f, 100000f);
        FollowTarget ??= "";
        if (EditorColor is not { Length: 4 }) EditorColor = new[] { 0.35f, 0.78f, 1f, 1f };
    }

    public RoomViewport Clone() => (RoomViewport)MemberwiseClone();
}

public enum RoomViewportFillStyle
{
    Outline = 0,
    Filled = 1,
    Both = 2,
}

public sealed class RoomEnvironment
{
    [JsonProperty("backgroundColor")] public float[] BackgroundColor { get; set; } = { .07f, .09f, .14f, 1f };
    [JsonProperty("gravity")] public float[] Gravity { get; set; } = { 0f, -9.81f, 0f };
    [JsonProperty("ambientIntensity")] public float AmbientIntensity { get; set; } = 1f;
    [JsonProperty("fogDensity")] public float FogDensity { get; set; }
    [JsonProperty("dynamicSky")] public bool DynamicSky { get; set; }
    [JsonProperty("weather")] public string Weather { get; set; } = "Clear";
    [JsonProperty("timeOfDayHours")] public float TimeOfDayHours { get; set; } = 7.5f;
    [JsonProperty("timeScale")] public float TimeScale { get; set; } = 24f;
    [JsonProperty("automaticWeather")] public bool AutomaticWeather { get; set; } = true;
    [JsonProperty("climateSeed")] public int ClimateSeed { get; set; } = 1337;
    [JsonProperty("dayOfYear")] public int DayOfYear { get; set; } = 215;
    [JsonProperty("latitudeDegrees")] public float LatitudeDegrees { get; set; } = 53.9f;
    [JsonProperty("atmospherePreset")] public string AtmospherePreset { get; set; } = "Natural";
    [JsonProperty("volumetricClouds")] public bool VolumetricClouds { get; set; } = true;
    [JsonProperty("cloudQuality")] public int CloudQuality { get; set; } = 2;
    [JsonProperty("cloudBaseHeight")] public float CloudBaseHeight { get; set; } = 180f;
    [JsonProperty("cloudThickness")] public float CloudThickness { get; set; } = 85f;
    [JsonProperty("cloudCoverageScale")] public float CloudCoverageScale { get; set; } = 1f;
    [JsonProperty("atmosphericHaze")] public float AtmosphericHaze { get; set; } = 0.15f;
    [JsonProperty("windAudio")] public string WindAudio { get; set; } = "";
    [JsonProperty("rainAudio")] public string RainAudio { get; set; } = "";
    [JsonProperty("waterAudio")] public string WaterAudio { get; set; } = "";
    [JsonProperty("fireAudio")] public string FireAudio { get; set; } = "";
    [JsonProperty("wildlifeAudio")] public string WildlifeAudio { get; set; } = "";
    [JsonProperty("nightAudio")] public string NightAudio { get; set; } = "";
    [JsonProperty("soundscapeLevels")] public RoomSoundscapeLevels SoundscapeLevels { get; set; } = new();
}

/// <summary>Authored linear gain; missing fields in older rooms retain unity gain.</summary>
public sealed class RoomSoundscapeLevels
{
    [JsonProperty("master")] public float Master { get; set; } = 1f;
    [JsonProperty("wind")] public float Wind { get; set; } = 1f;
    [JsonProperty("rain")] public float Rain { get; set; } = 1f;
    [JsonProperty("water")] public float Water { get; set; } = 1f;
    [JsonProperty("fire")] public float Fire { get; set; } = 1f;
    [JsonProperty("wildlife")] public float Wildlife { get; set; } = 1f;
    [JsonProperty("night")] public float Night { get; set; } = 1f;
    public static float Sanitize(float gain) => float.IsFinite(gain) ? Math.Clamp(gain, 0f, 1f) : 1f;
    public RoomSoundscapeLevels Clone() => new()
    {
        Master = Sanitize(Master), Wind = Sanitize(Wind), Rain = Sanitize(Rain), Water = Sanitize(Water),
        Fire = Sanitize(Fire), Wildlife = Sanitize(Wildlife), Night = Sanitize(Night),
    };
}

public sealed class RoomLayer
{
    [JsonProperty("id")] public string Id { get; set; } = RoomAsset.NewId();
    [JsonProperty("name")] public string Name { get; set; } = "Layer";
    /// <summary>
    /// Additive draw/spawn order for everything on the layer. Lower values are nearer in 2D,
    /// matching the sprite renderer's depth convention.
    /// </summary>
    [JsonProperty("order")] public int Order { get; set; }
    [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
    /// <summary>Editor-only guard against accidental viewport, inspector, paint, or delete edits.</summary>
    [JsonProperty("locked", DefaultValueHandling = DefaultValueHandling.Ignore), DefaultValue(false)]
    public bool Locked { get; set; }
}

public sealed class RoomNode
{
    [JsonProperty("id")] public string Id { get; set; } = RoomAsset.NewId();
    [JsonProperty("name")] public string Name { get; set; } = "Node";
    [JsonProperty("kind")] public RoomNodeKind Kind { get; set; }
    [JsonProperty("parentId")] public string ParentId { get; set; } = "";
    [JsonProperty("layerId")] public string LayerId { get; set; } = "";
    [JsonProperty("order")] public int Order { get; set; }
    [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
    [JsonProperty("locked", DefaultValueHandling = DefaultValueHandling.Ignore), DefaultValue(false)]
    public bool Locked { get; set; }
    [JsonProperty("enabledIn2D", DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(true)] public bool EnabledIn2D { get; set; } = true;
    [JsonProperty("enabledIn3D", DefaultValueHandling = DefaultValueHandling.Populate), DefaultValue(true)] public bool EnabledIn3D { get; set; } = true;
    [JsonProperty("transform")] public RoomTransform Transform { get; set; } = new();
    [JsonProperty("gameObject", NullValueHandling = NullValueHandling.Ignore)] public RoomGameObjectData GameObject { get; set; }
    [JsonProperty("background", NullValueHandling = NullValueHandling.Ignore)] public RoomBackgroundData Background { get; set; }
    [JsonProperty("tileLayer", NullValueHandling = NullValueHandling.Ignore)] public RoomTileLayerData TileLayer { get; set; }
    [JsonProperty("terrain", NullValueHandling = NullValueHandling.Ignore)] public RoomTerrainData Terrain { get; set; }
    public bool Supports(RoomDimension dimension) => dimension == RoomDimension.TwoD ? EnabledIn2D : EnabledIn3D;
}

public sealed class RoomTransform
{
    [JsonProperty("position")] public float[] Position { get; set; } = { 0f, 0f, 0f };
    [JsonProperty("rotation")] public float[] Rotation { get; set; } = { 0f, 0f, 0f };
    [JsonProperty("scale")] public float[] Scale { get; set; } = { 1f, 1f, 1f };
    public float X { get => Get(Position, 0); set => Position = Set(Position, 0, value, 0f); }
    public float Y { get => Get(Position, 1); set => Position = Set(Position, 1, value, 0f); }
    public float Z { get => Get(Position, 2); set => Position = Set(Position, 2, value, 0f); }
    public float RotationX { get => Get(Rotation, 0); set => Rotation = Set(Rotation, 0, value, 0f); }
    public float RotationY { get => Get(Rotation, 1); set => Rotation = Set(Rotation, 1, value, 0f); }
    public float RotationZ { get => Get(Rotation, 2); set => Rotation = Set(Rotation, 2, value, 0f); }
    public float ScaleX { get => Get(Scale, 0, 1f); set => Scale = Set(Scale, 0, value, 1f); }
    public float ScaleY { get => Get(Scale, 1, 1f); set => Scale = Set(Scale, 1, value, 1f); }
    public float ScaleZ { get => Get(Scale, 2, 1f); set => Scale = Set(Scale, 2, value, 1f); }
    private static float Get(float[] a, int i, float fallback = 0f) => a != null && a.Length > i ? a[i] : fallback;
    private static float[] Set(float[] a, int i, float v, float fallback) { if (a == null || a.Length < 3) a = new[] { fallback, fallback, fallback }; a[i] = v; return a; }
}

public sealed class RoomGameObjectData
{
    [JsonProperty("prefab")] public string Prefab { get; set; } = "";
    [JsonProperty("componentOverrides")] public List<RoomComponentOverride> ComponentOverrides { get; set; } = new();
}

public sealed class RoomComponentOverride
{
    [JsonProperty("componentId")] public string ComponentId { get; set; } = "";
    [JsonProperty("enabled", NullValueHandling = NullValueHandling.Ignore)] public bool? Enabled { get; set; }
    // PGSL user identifiers are case-sensitive; instance field paths must preserve that identity.
    [JsonProperty("properties")] public Dictionary<string, JToken> Properties { get; set; } = new(StringComparer.Ordinal);
}

public sealed class RoomBackgroundData
{
    [JsonProperty("asset")] public string Asset { get; set; } = "";
    [JsonProperty("mode")] public RoomBackgroundMode Mode { get; set; } = RoomBackgroundMode.TwoD;
    [JsonProperty("layout")] public RoomBackgroundLayout Layout { get; set; } = RoomBackgroundLayout.Single;
    [JsonProperty("depth")] public int Depth { get; set; }
    [JsonProperty("scroll")] public float[] Scroll { get; set; } = { 0f, 0f };
    [JsonProperty("repeatX")] public bool RepeatX { get; set; }
    [JsonProperty("repeatY")] public bool RepeatY { get; set; }
    [JsonProperty("depthTest")] public bool DepthTest { get; set; } = true;
    [JsonProperty("tint")] public int TintArgb { get; set; } = unchecked((int)0xffffffff);
    [JsonProperty("opacity")] public float Opacity { get; set; } = 1f;
}

public sealed class RoomTileLayerData
{
    [JsonProperty("tileset")] public string Tileset { get; set; } = "";
    [JsonProperty("cellWidth")] public int CellWidth { get; set; } = 32;
    [JsonProperty("cellHeight")] public int CellHeight { get; set; } = 32;
    [JsonProperty("margin")] public int Margin { get; set; }
    [JsonProperty("separation")] public int Separation { get; set; }
    [JsonProperty("depth")] public int Depth { get; set; }
    [JsonProperty("collisionEnabled")] public bool CollisionEnabled { get; set; }
    [JsonProperty("cells")] public List<RoomTileCell> Cells { get; set; } = new();
}

public sealed class RoomTileCell
{
    [JsonProperty("x")] public int X { get; set; }
    [JsonProperty("y")] public int Y { get; set; }
    [JsonProperty("tileX")] public int TileX { get; set; }
    [JsonProperty("tileY")] public int TileY { get; set; }
    [JsonProperty("flipX")] public bool FlipX { get; set; }
    [JsonProperty("flipY")] public bool FlipY { get; set; }

    // Optional per-cell transform. Grid-authored rooms omit these values, so existing room files
    // remain byte-small and behave exactly as before. The Room Editor uses them when a designer
    // treats an individual painted tile like a classic room tile: free-move, resize and rotate.
    [JsonProperty("offsetX", DefaultValueHandling = DefaultValueHandling.Ignore), DefaultValue(0f)]
    public float OffsetX { get; set; }
    [JsonProperty("offsetY", DefaultValueHandling = DefaultValueHandling.Ignore), DefaultValue(0f)]
    public float OffsetY { get; set; }
    [JsonProperty("scaleX", DefaultValueHandling = DefaultValueHandling.Ignore), DefaultValue(1f)]
    public float ScaleX { get; set; } = 1f;
    [JsonProperty("scaleY", DefaultValueHandling = DefaultValueHandling.Ignore), DefaultValue(1f)]
    public float ScaleY { get; set; } = 1f;
    [JsonProperty("rotation", DefaultValueHandling = DefaultValueHandling.Ignore), DefaultValue(0f)]
    public float Rotation { get; set; }
}

public sealed class RoomTerrainData
{
    [JsonProperty("asset")] public string Asset { get; set; } = "";
    [JsonProperty("material")] public string Material { get; set; } = "";
    [JsonProperty("albedo")] public string Albedo { get; set; } = "";
    [JsonProperty("uvScale")] public float UvScale { get; set; } = 6f;
}
